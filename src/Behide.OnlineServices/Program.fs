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
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.Authentication


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
            options.DefaultChallengeScheme <- OpenIdConnectDefaults.AuthenticationScheme
        )
        .AddCookie()
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
            options.ResponseType <- "code"
            // options.Prompt <- "consent"
            options.SaveTokens <- true
            options.Scope.Add("openid")
            options.Scope.Add("profile")
            options.Scope.Add("email")

            options.Events.OnRedirectToIdentityProvider <- fun context ->
                context.ProtocolMessage.SetParameter("access_type", "offline")
                Task.CompletedTask
        )
        .AddOpenIdConnect("microsoft", fun options ->
            options.ClientId <- ""
            options.ClientSecret <- ""
            options.Authority <- ""

            options.CallbackPath <- "/auth/provider-call-back/microsoft"
            options.ResponseType <- "id_token "
            options.ResponseMode <- "form_post"
            // options.Prompt <- "consent"
            options.SaveTokens <- true
            options.Scope.Add("openid")

        )
    |> ignore

let appBuilder (app: IApplicationBuilder) =
    app.UseRouting() |> ignore
    app.UseEndpoints(fun endpoints ->
        endpoints.MapHub<Signaling.SignalingHub>("/webrtc-signaling") |> ignore
    )

[<EntryPoint>]
let main args =
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
            yield! Auth.endpoints
        ]
    }
    0