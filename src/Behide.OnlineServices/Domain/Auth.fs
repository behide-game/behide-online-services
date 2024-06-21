namespace Behide.OnlineServices.Auth

open System
open Behide.OnlineServices
open Behide.OnlineServices.Common

[<RequireQualifiedAccess>]
type Provider =
    | Discord
    | Google
    | Microsoft

    static member parse (provider: string) =
        match provider with
        | "discord" -> Some Discord
        | "google" -> Some Google
        | "microsoft" -> Some Microsoft
        | _ -> None

    static member toString (provider: Provider) =
        match provider with
        | Discord -> "discord"
        | Google -> "google"
        | Microsoft -> "microsoft"

    static member fromJwtIssuer issuer =
        let microsoftIssuer = sprintf "https://login.microsoftonline.com/%s/v2.0" Config.Auth.Microsoft.tenantId

        // Discord is not a JWT provider
        match issuer with
        | "https://accounts.google.com" -> Some Google
        | Equals microsoftIssuer -> Some Microsoft
        | _ -> None

type ProviderConnection =
    | Discord of string
    | Google of string
    | Microsoft of string

    static member fromProviderAndId (provider: Provider) (id: string) =
        match provider with
        | Provider.Discord -> Discord id
        | Provider.Google -> Google id
        | Provider.Microsoft -> Microsoft id

type RefreshToken =
    { Token: string
      Expiration: DateTimeOffset }

    static member create () =
        { Token = Guid.NewGuid().ToString()
          Expiration = DateTimeOffset.UtcNow.AddDays 90 }

    static member raw (token: RefreshToken) = token.Token

type TokenHashes =
    { AccessTokenHash: string
      RefreshTokenHash:
        {| RefreshTokenHash: string
           Expiration: DateTimeOffset |} }