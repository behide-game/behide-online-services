module Behide.OnlineServices.Auth.Jwt

open Behide.OnlineServices
open Behide.OnlineServices.Common
module Config = Config.Auth.JWT

open System
open System.IdentityModel.Tokens.Jwt
open System.Security.Claims
open Microsoft.IdentityModel.Tokens
open FsToolkit.ErrorHandling

let private credentials = SigningCredentials(
    Config.securityKey,
    SecurityAlgorithms.HmacSha256
)

let private createJwtToken claims =
    JwtSecurityToken(
        issuer = Config.issuer,
        audience = Config.audience,
        claims = claims,
        notBefore = DateTime.Now,
        expires = DateTime.Now + Config.tokenDuration,
        signingCredentials = credentials
    )
    |> JwtSecurityTokenHandler().WriteToken

let private createJwtUserClaims userId =
    let userId = userId |> UserId.rawString

    Claim(ClaimTypes.NameIdentifier, userId)
    |> Seq.singleton

let private hashToken user token = Microsoft.AspNetCore.Identity.PasswordHasher().HashPassword(user, token)

// Public
let generateTokens userId =
    let accessToken =
        userId
        |> createJwtUserClaims
        |> createJwtToken

    let refreshToken = RefreshToken.create()

    {| AccessToken = accessToken
       RefreshToken = refreshToken
       AccessTokenHash = accessToken |> hashToken userId
       RefreshTokenHash = refreshToken |> RefreshToken.raw |> hashToken userId |}

// let verifyUserTokens
//     user
//     accessToken
//     refreshToken
//     accessTokenHash
//     refreshTokenHash
//     = result {
//     let passwordHasher = Microsoft.AspNetCore.Identity.PasswordHasher()

//     try
//         do! passwordHasher.VerifyHashedPassword(user, accessTokenHash, accessToken)
//             |> function
//                 | Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed -> false
//                 | _ -> true
//             |> Result.requireTrue "Invalid access token"

//         do! passwordHasher.VerifyHashedPassword(user, refreshTokenHash, refreshToken)
//             |> function
//                 | Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed -> false
//                 | _ -> true
//             |> Result.requireTrue "Invalid refresh token"

//     with e -> return! Error e.Message
// }

// let validateToken (token: string) = // to test
//     let handler = new JwtSecurityTokenHandler()
//     let mutable validatedToken: SecurityToken = upcast new JwtSecurityToken()

//     try
//         handler.ValidateToken(token, Config.validationParameters, &validatedToken)
//         |> Ok
//     with ex -> Error $"Exception: {ex.Message}"

// let getUserIdFromClaims (claims: #seq<Claim>) = // to test
//     claims
//     |> Seq.tryFind (fun claim -> claim.Type = ClaimTypes.NameIdentifier)
//     |> Result.ofOption "name identifier claim not found"
//     |> Result.bind (fun claim ->
//         claim.Value
//         |> UserId.tryParse
//         |> Result.ofOption "failed to parse name identifier claim"
//     )