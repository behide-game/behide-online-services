module Behide.OnlineServices.Auth.Endpoints

open Behide.OnlineServices
open Behide.OnlineServices.Api.Common
open Behide.OnlineServices.Api.Types

open System.Net
open System.Net.Http
open System.Threading
open System.IdentityModel.Tokens.Jwt

open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http

open Falco
open Falco.Routing
open FsToolkit.ErrorHandling
open Thoth.Json.Net

let challengeProvider provider redirectUri : HttpHandler =
    match provider with
    | None ->
        Response.withStatusCode (HttpStatusCode.BadRequest |> int)
        >> Response.ofPlainText "Invalid provider"
    | Some provider ->
        Response.challengeWithRedirect
            (provider |> Provider.toString)
            redirectUri

let getProviderToken (ctx: HttpContext) =
    let getToken tokenName =
        tokenName
        |> ctx.GetTokenAsync
        |> Task.map Option.ofNull

    getToken "id_token"
    |> TaskOption.map Choice1Of2
    |> TaskOption.orElseWith (fun _ ->
        getToken "access_token"
        |> TaskOption.map Choice2Of2
    )
    |> TaskResult.requireSome (HttpStatusCode.Unauthorized, "No token found")

let getDataFromJwtToken rawJwtToken = taskResult {
    let! jwtToken =
        try
            rawJwtToken
            |> JwtSecurityTokenHandler().ReadJwtToken
            |> Ok
        with _ ->
            (HttpStatusCode.Unauthorized, "Token invalid")
            |> Error

    let! issuer =
        jwtToken.Issuer
        |> Provider.fromJwtIssuer
        |> Result.requireSome (HttpStatusCode.InternalServerError, "Invalid issuer")

    let! providerUserId = // This is the user id from the provider
        jwtToken.Claims
        |> Seq.tryFind (fun c -> c.Type = "sub")
        |> Option.bindNull (fun c -> c.Value)
        |> Result.requireSome (HttpStatusCode.Unauthorized, "No user id found")

    return issuer, providerUserId
}

let getDataFromDiscordAccessToken (cts: CancellationToken) discordAccessToken = taskResult {
    let httpClient = new HttpClient()

    let request = new HttpRequestMessage(
        HttpMethod.Get,
        "https://discord.com/api/users/@me"
    )
    request.Headers.Authorization <- new Headers.AuthenticationHeaderValue("Bearer", discordAccessToken)

    let! response = httpClient.SendAsync(request, cts)
    let! rawJson = response.Content.ReadAsStringAsync()
    let! json =
        rawJson
        |> Decode.Auto.fromString<{| id: string |}>
        |> Result.setError (HttpStatusCode.Unauthorized, "Invalid token")

    return Provider.Discord, json.id
}


module SignUp =
    let signUpHandler provider = challengeProvider provider "/auth/sign-up-complete"

    let completeSignUpHandler (ctx: HttpContext) =
        taskResult {
            let! providerToken = ctx |> getProviderToken

            let! (issuer, providerUserId) =
                match providerToken with
                | Choice1Of2 rawJwtToken -> getDataFromJwtToken rawJwtToken
                | Choice2Of2 discordAccessToken -> getDataFromDiscordAccessToken ctx.RequestAborted discordAccessToken

            // Check if user exists
            do! providerUserId
                |> Repository.Database.Users.findUserByNameIdentifier
                |> TaskResult.requireNone (HttpStatusCode.Conflict, "User already exists")

            // Create user
            let userId = UserId.create()
            let tokens = Jwt.generateTokens userId
            let authConnection =
                ProviderConnection.fromProviderAndId
                    issuer
                    providerUserId
            let user =
                { Id = userId
                  Name = (sprintf "Test - %s" (issuer |> Provider.toString))
                  AuthConnection = authConnection
                  RefreshTokenHashes = tokens.RefreshTokenHash |> Array.singleton }

            do! user |> Repository.Database.Users.insert

            return
                {| Token = tokens.AccessToken
                   RefreshToken = tokens.RefreshToken.Token |}
        }
        |> Task.map (Result.eitherMap
            (fun response -> ctx |> Response.ofJson response)
            (fun (statusCode, message) ->
                ctx
                |> Response.withStatusCode (statusCode |> int)
                |> Response.ofPlainText message
            )
        )

module SignIn =
    let signInHandler provider = challengeProvider provider "/auth/sign-in-complete"

    let completeSignInHandler (ctx: HttpContext) =
        taskResult {
            let! providerToken = ctx |> getProviderToken

            let! (_, providerUserId) =
                match providerToken with
                | Choice1Of2 rawJwtToken -> getDataFromJwtToken rawJwtToken
                | Choice2Of2 discordAccessToken -> getDataFromDiscordAccessToken ctx.RequestAborted discordAccessToken

            let! user =
                providerUserId
                |> Repository.Database.Users.findUserByNameIdentifier
                |> TaskResult.requireSome (HttpStatusCode.Unauthorized, "User not found")

            let tokens = Jwt.generateTokens user.Id

            do! Repository.Database.Users.addRefreshTokenHashToUser
                    user.Id
                    tokens.RefreshTokenHash
                |> TaskResult.setError (HttpStatusCode.InternalServerError, "Failed to update user tokens")

            return
                { accessToken = tokens.AccessToken
                  refreshToken = tokens.RefreshToken.Token
                  refreshTokenExpiration = tokens.RefreshToken.Expiration }
        }
        |> Task.map (Result.eitherMap
            (fun response -> ctx |> Response.ofJson response)
            (fun (statusCode, message) ->
                ctx
                |> Response.withStatusCode (statusCode |> int)
                |> Response.ofPlainText message
            )
        )

module Refresh =
    let refreshHandler (ctx: HttpContext) =
        taskResult {
            // Retrieve info from body
            let! (body: {| accessToken: string; refreshToken: string |}) =
                ctx
                |> Request.getBodyString
                |> Task.map Decode.Auto.fromString
                |> TaskResult.setError (HttpStatusCode.BadRequest, "Invalid body")

            let! userId =
                body.accessToken
                |> Jwt.getUserIdFromToken
                |> Result.mapError (fun error ->
                    printfn "%A" error
                    HttpStatusCode.Unauthorized, "Invalid access token"
                )

            // Find user
            let! user =
                userId
                |> Repository.Database.Users.findUserById
                |> TaskResult.requireSome (HttpStatusCode.Unauthorized, "Invalid refresh token")

            // Check if refresh token is valid
            let! oldRefreshTokenHash =
                user.RefreshTokenHashes
                |> Array.tryFind (fun refreshTokenHash ->
                    Jwt.validRefreshToken
                        user.Id
                        refreshTokenHash
                        body.refreshToken
                )
                |> Result.requireSome (HttpStatusCode.Unauthorized, "Invalid refresh token")

            // Generate new tokens
            let newTokens = Jwt.generateTokens user.Id

            do! Repository.Database.Users.replaceRefreshTokenHashToUser
                    user.Id
                    oldRefreshTokenHash
                    newTokens.RefreshTokenHash
                |> TaskResult.setError (HttpStatusCode.InternalServerError, "Failed to update user tokens")

            return
                { accessToken = newTokens.AccessToken
                  refreshToken = newTokens.RefreshToken.Token
                  refreshTokenExpiration = newTokens.RefreshToken.Expiration }
        }
        |> Task.map (Result.eitherMap
            (fun response -> ctx |> Response.ofJson response)
            (fun (statusCode, message) ->
                ctx
                |> Response.withStatusCode (statusCode |> int)
                |> Response.ofPlainText message
            )
        )

module Request =
    /// Retrieve the provider from the route (the route must have ``{provider}`` in it)
    let mapRouteProvider : (Provider option -> HttpHandler) -> HttpHandler =
        Request.mapRoute (fun r ->
            r.TryGetString "provider"
            |> Option.bind Provider.parse
        )

let endpoints = [
    get "/auth/sign-up/{provider:alpha}" (Request.mapRouteProvider SignUp.signUpHandler)
    get "/auth/sign-up-complete" (SignUp.completeSignUpHandler |> taskResultHandler)

    get "/auth/sign-in/{provider:alpha}" (Request.mapRouteProvider SignIn.signInHandler)
    get "/auth/sign-in-complete" (SignIn.completeSignInHandler |> taskResultHandler)

    post "/auth/refresh-token" (Refresh.refreshHandler |> taskResultHandler)
]