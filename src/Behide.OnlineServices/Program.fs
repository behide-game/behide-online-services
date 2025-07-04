module Behide.OnlineServices.Program

open Behide.OnlineServices.Hubs

open Falco
open Falco.Routing

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
    services.AddSingleton<Signaling.IConnAttemptStore, Signaling.ConnAttemptStore>() |> ignore
    services.AddSingleton<Signaling.IRoomStore, Signaling.RoomStore>() |> ignore
    services.AddSingleton<Signaling.IPlayerConnStore, Signaling.PlayerConnStore>() |> ignore
    services

let appEndpoints =
    [ get "/" (Response.ofPlainText "Hello world") ]

[<EntryPoint>]
let main args =
    let wapp =
        WebApplication.CreateBuilder(args)
            .AddServices(fun _ -> configureServices)
            .Build()

    wapp.UseRouting()
        .UseEndpoints(fun endpoints ->
            endpoints.MapHub<Signaling.SignalingHub>("/webrtc-signaling") |> ignore
        )
        .UseFalco(appEndpoints) |> ignore

    wapp.Run(Response.ofPlainText "Not found")

    0
