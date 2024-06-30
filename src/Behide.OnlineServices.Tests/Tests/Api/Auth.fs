module Behide.OnlineServices.Tests.Api.Auth

open System
open System.Net
open System.Net.Http
open System.IdentityModel.Tokens.Jwt
open System.Security.Claims
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.AspNetCore.Identity
open FsToolkit.ErrorHandling

open Expecto
open Expecto.Flip

open Behide.OnlineServices
open Behide.OnlineServices.Tests.Common
open System.Net.Http.Json
open Microsoft.IdentityModel.JsonWebTokens
open Microsoft.IdentityModel.Tokens
open System.Security.Claims


let refreshTokensReq (accessToken: string) (refreshToken: string) =
    new HttpRequestMessage(
        RequestUri = Uri "http://localhost:5000/auth/refresh-token",
        Method = HttpMethod.Post,
        Content =
            JsonContent.Create
                {| accessToken = accessToken
                   refreshToken = refreshToken |}
    )

let refreshTokens client accessToken refreshToken =
    refreshTokensReq
        accessToken
        refreshToken
    |> Http.send HttpStatusCode.OK client
    |> Http.parseResponse<Api.Types.TokenPairDTO>
    |> Task.map (Expect.wantOk "Response should be parsable")

// let getClaim claimType (claims: #seq<string * string>) =
//     claims
//     |> Seq.tryPick (fun (type', value) ->
//         match type' = claimType with
//         | true -> Some value
//         | false -> None
//     )
//     |> function
//         | Some value -> value
//         | None -> failtestf "JWT claims should contain %s" claimType



[<Tests>]
let tests = testList "Auth" [
    testList "JWT" [
        testTask "Generate tokens" {
            let userId = UserId.create()
            let tokens = Api.Auth.Jwt.generateTokens userId

            let validationParameters = Common.Config.Auth.JWT.validationParameters
            let handler = new JwtSecurityTokenHandler()
            let mutable validatedToken: SecurityToken = new JwtSecurityToken()

            let claimPrincipal =
                try
                    handler.ValidateToken(tokens.AccessToken, validationParameters, &validatedToken)
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

            Expect.equal "JWT issuer should not be that" Common.Config.Auth.JWT.issuer issuer
            Expect.equal "JWT audience should not be that" Common.Config.Auth.JWT.audience audience
            Expect.equal "JWT name identifier should not be that" (userId |> UserId.rawString) nameIdentifier

            // Test refresh token hash
            let hasher = PasswordHasher()
            Expect.notEqual
                "Refresh token should be valid"
                PasswordVerificationResult.Failed
                (hasher.VerifyHashedPassword("", tokens.RefreshTokenHash.Hash, tokens.RefreshToken.Token))
        }

        // testTask "Verify tokens" {
        //     let! user, (accessToken: string), (refreshToken: string) =
        //         Helpers.User.createUser()
        //         |> Helpers.Database.addUser

        //     BehideApi.JWT.verifyUserTokens user accessToken refreshToken
        //     |> Expect.wantOk "Tokens should be approved"
        // }
    ]

    // testTask "Authorized user should be able to refresh his tokens" {
    //     let client = Helpers.getClient()
    //     let! _user, (accessToken: string), (refreshToken: string) =
    //         Helpers.User.createUser()
    //         |> Helpers.Database.addUser

    //     // Refresh
    //     let! (response: DTO.Auth.RefreshToken.Response) = (accessToken, refreshToken) |> refreshTokens client

    //     // Refresh with wrong tokens
    //     do! (accessToken, refreshToken)
    //         |> refreshTokensReq
    //         |> Helpers.Http.send HttpStatusCode.Unauthorized client
    //         |> Task.map ignore

    //     // Re-refresh with correct tokens
    //     do! (response.accessToken, response.refreshToken)
    //         |> refreshTokens client
    //         |> Task.map ignore
    // }
]