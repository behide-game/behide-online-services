module Behide.OnlineServices.Tests.Api.Auth

open System
open System.Net
open System.Net.Http
open System.IdentityModel.Tokens.Jwt
open System.Security.Claims
open FsToolkit.ErrorHandling

open Expecto
open Expecto.Flip

open Behide.OnlineServices
open Behide.OnlineServices.Tests.Common
open System.Net.Http.Json
open Microsoft.IdentityModel.Tokens


let createRefreshTokensReq (client: HttpClient) (accessToken: string) (refreshToken: string) =
    new HttpRequestMessage(
        RequestUri = Uri(client.BaseAddress, "/auth/refresh-token"),
        Method = HttpMethod.Post,
        Content =
            JsonContent.Create
                {| accessToken = accessToken
                   refreshToken = refreshToken |}
    )

let verifyAccessToken (user: User) (token: string) =
    let validationParameters = Common.Config.Auth.JWT.validationParameters
    let handler = new JwtSecurityTokenHandler()
    let mutable validatedToken: SecurityToken = new JwtSecurityToken()

    let claimPrincipal =
        try
            handler.ValidateToken(token, validationParameters, &validatedToken)
        with error ->
            failtestf "Access token validation failed: %s" error.Message

    let getClaim claimType  =
        claimPrincipal.FindFirstValue claimType
        |> Option.ofNull
        |> function
            | Some value -> value
            | None -> failtestf "JWT claims should contain %s" claimType

    // Test claims
    let audience = getClaim "aud"
    let issuer = getClaim "iss"
    let nameIdentifier = getClaim  ClaimTypes.NameIdentifier
    let userNameClaim = getClaim ClaimTypes.Name

    Expect.equal "JWT issuer should not be that" Common.Config.Auth.JWT.issuer issuer
    Expect.equal "JWT audience should not be that" Common.Config.Auth.JWT.audience audience
    Expect.equal "JWT name identifier should not be that" (user.Id |> UserId.rawString) nameIdentifier
    Expect.equal "JWT name should not be that" user.Name userNameClaim


let verifyRefreshToken
    (user: User)
    (refreshToken: Api.Auth.RefreshToken)
    (refreshTokenHash: Api.Auth.RefreshTokenHash)
    =
    // Test refresh token hash
    Api.Auth.Jwt.verifyRefreshTokenHash
        user.Id
        refreshTokenHash
        refreshToken.Token
    |> Expect.isTrue "Refresh token should be valid"

[<Tests>]
let tests = testList "Auth" [
    let testServer, _ = createTestServer()
    let client = testServer.CreateClient()

    testList "JWT" [
        testTask "Generate tokens" {
            let user = User.createUser()
            let tokens = Api.Auth.Jwt.generateTokens user.Id user.Name

            verifyAccessToken user tokens.AccessToken
            verifyRefreshToken
                user
                tokens.RefreshToken
                tokens.RefreshTokenHash
        }
    ]

    testList "Refresh tokens" [
        testTask "Authorized user should be able to refresh his tokens" {
            let user = User.createUser()
            do! user |> User.putInDatabase

            let tokens = Api.Auth.Jwt.generateTokens user.Id user.Name
            do! tokens.RefreshTokenHash |> User.putRefreshTokenHashInDb user

            // Refresh
            let! (response: Api.Types.TokenPairDTO) =
                createRefreshTokensReq
                    client
                    tokens.AccessToken
                    tokens.RefreshToken.Token
                |> client.SendAsync
                |> Task.bind Serialization.decodeHttpResponse

            // Check if new tokens are valid
            verifyAccessToken user response.accessToken

            // Check if new refresh token is valid
            Expect.isTrue
                "Refresh token should not be expired"
                (DateTimeOffset.UtcNow < response.refreshTokenExpiration)
        }

        testTask "Unauthorized user should not be able to refresh tokens" {
            let! (response: HttpResponseMessage) =
                createRefreshTokensReq
                    client
                    "fake access token"
                    "fake refresh token"
                |> client.SendAsync

            Expect.equal "Status code should be Unauthorized" HttpStatusCode.Unauthorized response.StatusCode
        }

        testTask "Invalid body should return BadRequest" {
            let! (response: HttpResponseMessage) =
                new HttpRequestMessage(
                    RequestUri = Uri(client.BaseAddress, "/auth/refresh-token"),
                    Method = HttpMethod.Post,
                    Content = JsonContent.Create {| aaa = "aaa" |}
                )
                |> client.SendAsync

            Expect.equal "Status code should be BadRequest" HttpStatusCode.BadRequest response.StatusCode
        }

        testTask "Expired refresh token should return Unauthorized" {
            let user = User.createUser()
            do! user |> User.putInDatabase

            let tokens = Api.Auth.Jwt.generateTokens user.Id user.Name
            do! { tokens.RefreshTokenHash with
                    Expiration = DateTimeOffset.MinValue }
                |> User.putRefreshTokenHashInDb user

            // Refresh
            let! (response: HttpResponseMessage) =
                createRefreshTokensReq
                    client
                    tokens.AccessToken
                    tokens.RefreshToken.Token
                |> client.SendAsync

            Expect.equal "Status code should be Unauthorized" HttpStatusCode.Unauthorized response.StatusCode
        }

        testTask "Refresh with wrong refresh token should return Unauthorized" {
            let user = User.createUser()
            do! user |> User.putInDatabase

            let tokens = Api.Auth.Jwt.generateTokens user.Id user.Name
            do! tokens.RefreshTokenHash |> User.putRefreshTokenHashInDb user

            // Refresh
            let! (response: HttpResponseMessage) =
                createRefreshTokensReq
                    client
                    tokens.AccessToken
                    "fake refresh token"
                |> client.SendAsync

            Expect.equal "Status code should be Unauthorized" HttpStatusCode.Unauthorized response.StatusCode
        }

        testTask "Refresh with wrong access token should return Unauthorized" {
            let user = User.createUser()
            do! user |> User.putInDatabase

            let tokens = Api.Auth.Jwt.generateTokens user.Id user.Name
            do! tokens.RefreshTokenHash |> User.putRefreshTokenHashInDb user

            // Refresh
            let! (response: HttpResponseMessage) =
                createRefreshTokensReq
                    client
                    "fake access token"
                    tokens.RefreshToken.Token
                |> client.SendAsync

            Expect.equal "Status code should be Unauthorized" HttpStatusCode.Unauthorized response.StatusCode
        }

        testTask "Re-refresh with old refresh token should return Unauthorized" {
            let user = User.createUser()
            do! user |> User.putInDatabase

            let tokens = Api.Auth.Jwt.generateTokens user.Id user.Name
            do! tokens.RefreshTokenHash |> User.putRefreshTokenHashInDb user

            // Refresh
            let! (response: Api.Types.TokenPairDTO) =
                createRefreshTokensReq
                    client
                    tokens.AccessToken
                    tokens.RefreshToken.Token
                |> client.SendAsync
                |> Task.bind Serialization.decodeHttpResponse

            // Refresh again
            let! (response: HttpResponseMessage) =
                createRefreshTokensReq
                    client
                    response.accessToken
                    tokens.RefreshToken.Token
                |> client.SendAsync

            Expect.equal "Status code should be Unauthorized" HttpStatusCode.Unauthorized response.StatusCode
        }
    ]
]
