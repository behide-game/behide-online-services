module Behide.OnlineServices.Api.Auth.Jwt

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

let private createJwtUserClaims userId userName =
    let userId = userId |> UserId.rawString

    [ Claim(ClaimTypes.NameIdentifier, userId)
      Claim(ClaimTypes.Name, userName) ]

let private passwordHasher = Microsoft.AspNetCore.Identity.PasswordHasher<UserId>()

let private hashToken userId token = passwordHasher.HashPassword(userId, token)

// Public
let generateTokens userId userName =
    let accessToken =
        (userId, userName)
        ||> createJwtUserClaims
        |> createJwtToken

    let refreshToken = RefreshToken.create()

    {| AccessToken = accessToken
       RefreshToken = refreshToken
       RefreshTokenHash =
        { Hash = refreshToken |> RefreshToken.raw |> hashToken userId
          Expiration = refreshToken.Expiration } |}

let getUserIdFromToken (token: string) =
    let handler = new JwtSecurityTokenHandler()
    let mutable validatedToken: SecurityToken = new JwtSecurityToken()

    try
        let claims = handler.ValidateToken(token, Config.validationParameters, &validatedToken)
        claims.Claims
        |> Seq.tryFind (fun claim -> claim.Type = ClaimTypes.NameIdentifier)
        |> Option.bind (fun claim -> claim.Value |> UserId.tryParse)
        |> Result.ofOption "Name identifier claim not found"
    with ex -> Error $"Exception: {ex.Message}"

/// Returns true if the refresh token is valid
let verifyRefreshTokenHash userId (refreshTokenHash: RefreshTokenHash) refreshToken =
    try
        passwordHasher.VerifyHashedPassword(userId, refreshTokenHash.Hash, refreshToken)
        |> function
            | Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed -> false
            | _ -> true
    with
    | _ -> false
