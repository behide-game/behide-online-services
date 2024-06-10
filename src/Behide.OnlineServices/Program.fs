module Behide.OnlineServices.Program

open Behide.OnlineServices.Hubs

open Falco
open Falco.Routing
open Falco.HostBuilder

open System.Text.Json.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Authentication.Cookies


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

    // services
    //     .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    //     .AddCookie(fun options ->
    //         options.LoginPath <- "/auth/sign-in"
    //         options.LogoutPath <- "/auth/sign-out"
    //     )
    //     .AddGoogle("google", fun options ->
    //         options.ClientId <- ""
    //         options.ClientSecret <- ""
    //         options.CallbackPath <- "/auth/signin-google"
    //     )
    // |> ignore

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