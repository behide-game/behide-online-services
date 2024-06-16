namespace Behide.OnlineServices.Auth

open System

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

type ProviderConnection =
    | Discord of string
    | Google of string
    | Microsoft of string

    static member fromIssuer issuer nameIdentifier =
        match issuer with
        | "https://discord.com" -> nameIdentifier |> Discord |> Some
        | "https://accounts.google.com" -> nameIdentifier |> Google |> Some
        | "https://login.microsoftonline.com//v2.0" -> nameIdentifier |> Microsoft |> Some
        | _ -> None

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