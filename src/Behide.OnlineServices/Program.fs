module Behide.OnlineServices.Program

open Behide.OnlineServices.Hubs

open Falco
open Falco.Routing
open Falco.HostBuilder

open System.Threading.Tasks
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.IdentityModel.Tokens
open NamelessInteractive.FSharp.MongoDB


let configureServices (services: IServiceCollection) =
    services
        .AddSignalR()
        .AddJsonProtocol(fun options ->
            JsonFSharpOptions
                .Default()
                .AddToJsonSerializerOptions(options.PayloadSerializerOptions)
        )
    |> ignore
    services.AddSingleton<Signaling.IConnAttemptStore, Signaling.ConnAttemptStore>() |> ignore
    services.AddSingleton<Signaling.IRoomStore, Signaling.RoomStore>() |> ignore
    services.AddSingleton<Signaling.IPlayerConnsStore, Signaling.PlayerConnsStore>() |> ignore

    services
        .AddAuthentication(fun options ->
            options.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
        )
        .AddCookie()
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, fun options ->
            options.Authority <- Config.Auth.JWT.issuer
            options.Audience <- Config.Auth.JWT.audience
            options.RequireHttpsMetadata <- false // TODO: set to true in production
            options.TokenValidationParameters <- Config.Auth.JWT.validationParameters
        )
        .AddDiscord("discord", fun options ->
            options.ClientId <- ""
            options.ClientSecret <- ""
            options.CallbackPath <- "/auth/provider-call-back/discord"
            options.SaveTokens <- true
        )
        .AddOpenIdConnect("google", fun options ->
            options.ClientId <- ""
            options.ClientSecret <- ""
            options.Authority <- "https://accounts.google.com"
            options.CallbackPath <- "/auth/provider-call-back/google"
            options.SaveTokens <- true

            options.ResponseType <- "code"
            // options.Prompt <- "consent"
            options.SaveTokens <- true
            options.Scope.Add("openid")
            options.Scope.Add("profile")
            options.Scope.Add("email")

            options.TokenValidationParameters <- new TokenValidationParameters(
                ValidIssuer = "accounts.google.com",
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true
            )

            options.Events.OnRedirectToIdentityProvider <- fun context ->
                context.ProtocolMessage.SetParameter("access_type", "offline")
                Task.CompletedTask
        )
        .AddOpenIdConnect("microsoft", fun options ->
            options.ClientId <- ""
            options.ClientSecret <- ""
            options.Authority <- ""

            options.CallbackPath <- "/auth/provider-call-back/microsoft"
            options.SaveTokens <- true

            options.ResponseType <- "id_token "
            options.ResponseMode <- "form_post"
            options.Scope.Add("openid")

            options.TokenValidationParameters <- new TokenValidationParameters(
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true
            )
        )
    |> ignore

let appBuilder (app: IApplicationBuilder) =
    app.UseRouting() |> ignore
    app.UseEndpoints(fun endpoints ->
        endpoints.MapHub<Signaling.SignalingHub>("/webrtc-signaling") |> ignore
    )

[<EntryPoint>]
let main args =
    // Register MongoDB serializers
    SerializationProviderModule.Register()
    Conventions.ConventionsModule.Register()
    Serialization.SerializationProviderModule.Register()

    webHost args {
        host (fun builder ->
            builder.ConfigureServices(fun _builder services ->
                configureServices services
            )
        )
        use_middleware appBuilder

        // use_authentication
        // use_authorization
        endpoints [
            get "/" (Response.ofPlainText "Hello world")
            yield! Auth.Endpoints.endpoints
        ]
    }
    0