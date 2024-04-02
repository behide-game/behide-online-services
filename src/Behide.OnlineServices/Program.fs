module Behide.OnlineServices.Program

open Behide.OnlineServices.Hubs

open Falco
open Falco.Routing
open Falco.HostBuilder

open System.Text.Json.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection


let configureServices (services: IServiceCollection) =
    services
        .AddSignalR()
        .AddJsonProtocol(fun options ->
            JsonFSharpOptions
                .Default()
                .AddToJsonSerializerOptions(options.PayloadSerializerOptions)
        )
    |> ignore
    services.AddSingleton<Signaling.IOfferStore, Signaling.OfferStore>() |> ignore
    services.AddSingleton<Signaling.IRoomStore, Signaling.RoomStore>() |> ignore
    services.AddSingleton<Signaling.IPlayerConnsStore, Signaling.PlayerConnsStore>() |> ignore

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
        endpoints [
            get "/" (Response.ofPlainText "Hello world")
        ]
    }
    0