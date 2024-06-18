module Behide.OnlineServices.Auth.Endpoints

open Behide.OnlineServices

open System.Net
open System.Threading.Tasks

open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http

open Falco
open Falco.Routing
open FsToolkit.ErrorHandling

module SignUp =
    open System.IdentityModel.Tokens.Jwt
    open Microsoft.IdentityModel.Tokens
    open System.Net.Http
    open Thoth.Json.Net

    let signUpHandler provider =
        match provider with
        | None ->
            Response.withStatusCode (HttpStatusCode.BadRequest |> int)
            >> Response.ofPlainText "Invalid provider"
        | Some provider ->
            Response.challengeWithRedirect
                (provider |> Provider.toString)
                "/auth/sign-up-complete"

    let completeSignUpHandler (ctx: HttpContext) : Task =
        taskResult {
            // Retrieve provider token
            let getToken tokenName =
                tokenName
                |> ctx.GetTokenAsync
                |> Task.map Option.ofNull

            let! providerToken =
                getToken "id_token"
                |> TaskOption.map Choice1Of2
                |> TaskOption.orElseWith (fun _ ->
                    getToken "access_token"
                    |> TaskOption.map Choice2Of2
                )
                |> TaskResult.requireSome (HttpStatusCode.Unauthorized, "No token found")

            let! (issuer, providerUserId) = taskResult {
                match providerToken with
                | Choice1Of2 rawJwtToken ->
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
                | Choice2Of2 discordAccessToken ->
                    let! providerUserId = taskResult {
                        let httpClient = new HttpClient()

                        let request =
                            new HttpRequestMessage(
                                HttpMethod.Get,
                                "https://discord.com/api/users/@me"
                            )
                        request.Headers.Authorization <- new Headers.AuthenticationHeaderValue("Bearer", discordAccessToken)

                        let! response = httpClient.SendAsync(request, ctx.RequestAborted)
                        let! rawJson = response.Content.ReadAsStringAsync()
                        let! json =
                            rawJson
                            |> Decode.Auto.fromString<{| id: string |}>
                            |> Result.setError (HttpStatusCode.Unauthorized, "Invalid token")

                        return json.id
                    }

                    return Provider.Discord, providerUserId
            }

            // Check if user exists
            do! providerUserId
                |> Repository.Database.Users.findByUserNameIdentifier
                |> TaskResult.requireNone (HttpStatusCode.Conflict, "User already exists")

            // Create user
            let userId = UserId.create()
            let tokens = Jwt.generateTokens userId
            let user =
                { Id = userId
                  Name = sprintf "Test - %s" (issuer |> Provider.toString)
                  AuthConnection =
                    ProviderConnection.fromProviderAndId
                        issuer
                        providerUserId
                  TokenHashes =
                    { AccessTokenHash = tokens.AccessTokenHash
                      RefreshTokenHash =
                        {| RefreshTokenHash = tokens.RefreshTokenHash
                           Expiration = tokens.RefreshToken.Expiration |} } }

            do! user |> Repository.Database.Users.insert

            return
                {| Token = tokens.AccessToken
                   RefreshToken = tokens.RefreshToken.Token |}
        }
        |> fun t -> task {
            let! r = t

            match r with
            | Ok response -> return! ctx |> Response.ofJson response
            | Error (statusCode, message) ->
                return!
                    ctx
                    |> Response.withStatusCode (statusCode |> int)
                    |> Response.ofPlainText message
        }
        :> Task

module Request =
    let mapProvider : (Provider option -> HttpHandler) -> HttpHandler =
        Request.mapRoute (fun r ->
            r.TryGetString "provider"
            |> Option.bind Provider.parse
        )

let endpoints = [
    get "/auth/sign-up/{provider:alpha}" (Request.mapProvider SignUp.signUpHandler)
    get "/auth/sign-up-complete" SignUp.completeSignUpHandler
]