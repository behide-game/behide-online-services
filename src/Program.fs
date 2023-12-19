module Behide.OnlineServices.Program

open System.Text.Json.Serialization

open Falco
open Falco.Routing
open Falco.HostBuilder

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection


[<EntryPoint>]
let main args =
    webHost args {
        endpoints [
            get "/" (Response.ofPlainText "Hello world")
        ]

        host (fun builder -> builder.ConfigureServices(fun _builder services ->
            services
                .AddSignalR()
                .AddJsonProtocol(fun options ->
                    JsonFSharpOptions
                        .Default()
                        .AddToJsonSerializerOptions(options.PayloadSerializerOptions)
                )
            |> ignore
            services.AddSingleton<Store.IStore<OfferId, SdpDescription>, Store.Store<OfferId, SdpDescription>>() |> ignore
            services.AddSingleton<Store.IStore<RoomId, Room>, Store.Store<RoomId, Room>>() |> ignore
        ))

        use_middleware (fun app ->
            app.UseRouting() |> ignore
            app.UseEndpoints(fun endpoints ->
                endpoints.MapHub<Hubs.SignalingHub>("/webrtc-signaling") |> ignore
            )
        )
    }
    0