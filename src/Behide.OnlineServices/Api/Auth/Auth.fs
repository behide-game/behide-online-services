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
    open System.Security.Claims

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
                |> TaskOption.orElseWith (fun _ -> getToken "access_token")
                |> TaskResult.requireSome (HttpStatusCode.Unauthorized, "No token found")

            let parsedToken = providerToken |> JwtSecurityTokenHandler().ReadJwtToken

            // Check if user exists
            let! providerUserId = // This is the user id from the provider
                parsedToken.Claims
                |> Seq.tryFind (fun c -> c.Type = "sub")
                |> Option.bindNull (fun c -> c.Value)
                |> Result.requireSome (HttpStatusCode.Unauthorized, "No user id found")

            do! providerUserId
                |> Repository.Database.Users.findByUserNameIdentifier
                |> TaskResult.requireNone (HttpStatusCode.Conflict, "User already exists")

            // Create user
            let! authConnection =
                ProviderConnection.fromIssuer
                    parsedToken.Issuer
                    providerUserId
                |> Result.requireSome (HttpStatusCode.InternalServerError, "Invalid issuer")
                // If the token is valid, we should be able to create a user auth connection

            let userId = UserId.create()

            let tokens = Jwt.generateTokens userId

            let user =
                { Id = userId
                  Name = sprintf "Test - %s" parsedToken.Issuer
                  AuthConnection = authConnection
                  TokenHashes =
                    { AccessTokenHash = tokens.AccessTokenHash
                      RefreshTokenHash =
                        {| RefreshTokenHash = tokens.RefreshTokenHash
                           Expiration = tokens.RefreshToken.Expiration |} } }

            do! user |> Repository.Database.Users.insert

            return {|
                Token = tokens.AccessToken
                RefreshToken = tokens.RefreshToken.Token
            |}
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