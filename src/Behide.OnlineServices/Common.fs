namespace Behide.OnlineServices.Common

module private DotEnv =
    open System
    open System.IO

    let private parseLine(line : string) =
        match line.Split('=', StringSplitOptions.RemoveEmptyEntries) with
        | args when args.Length = 2 -> Environment.SetEnvironmentVariable(args.[0], args.[1])
        | _ -> ()

    let private load() = lazy (
        let dir = Directory.GetCurrentDirectory()
        let filePath = Path.Combine(dir, ".env")

        filePath
        |> File.Exists
        |> function
            | false -> Console.WriteLine "No .env file found."
            | true  ->
                filePath
                |> File.ReadAllLines
                |> Seq.iter parseLine
    )

    let init = load().Value


module Config =
    open System

    let private get key =
        DotEnv.init
        Environment.GetEnvironmentVariable key

    module Auth =
        open System.Text
        open Microsoft.IdentityModel.Tokens

        module Discord =
            let clientId = get "AUTH_DISCORD_CLIENT_ID"
            let clientSecret = get "AUTH_DISCORD_CLIENT_SECRET"

        module Google =
            let clientId = get "AUTH_GOOGLE_CLIENT_ID"
            let clientSecret = get "AUTH_GOOGLE_CLIENT_SECRET"

        module Microsoft =
            let tenantId = get "AUTH_MICROSOFT_TENANT_ID"
            let clientId = get "AUTH_MICROSOFT_CLIENT_ID"
            let clientSecret = get "AUTH_MICROSOFT_CLIENT_SECRET"

        module JWT =
            let tokenDuration = TimeSpan.FromDays 1

            let signingKey = get "JWT_SIGNING_KEY"
            let securityKey =
                signingKey
                |> Encoding.UTF8.GetBytes
                |> SymmetricSecurityKey

            let issuer = get "JWT_ISSUER"
            let audience = get "JWT_AUDIENCE"

            let validationParameters =
                new TokenValidationParameters(
                    ClockSkew = TimeSpan.Zero,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = securityKey
                )

    module Database =
        let connectionString = get "MONGODB_CONNECTION_STRING"