module Behide.OnlineServices.Tests.Signaling.Common

open Behide.OnlineServices.Signaling

open System.Text.Json.Serialization
open System.Threading.Tasks

open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Http.Connections.Client
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.AspNetCore.TestHost

type SignalingHub(connection: HubConnection) =
    interface ISignalingHub with
        member _.StartConnectionAttempt sdpDescription       = connection.InvokeAsync<_>("StartConnectionAttempt", sdpDescription)
        member _.JoinConnectionAttempt  offerId              = connection.InvokeAsync<_>("JoinConnectionAttempt",  offerId)
        member _.SendAnswer             offerId iceCandidate = connection.InvokeAsync<_>("SendAnswer",             offerId, iceCandidate)
        member _.SendIceCandidate       offerId iceCandidate = connection.InvokeAsync<_>("SendIceCandidate",       offerId, iceCandidate)
        member _.EndConnectionAttempt   offerId              = connection.InvokeAsync<_>("EndConnectionAttempt",   offerId)

        member _.CreateRoom   () = connection.InvokeAsync<_>("CreateRoom")
        member _.JoinRoom roomId = connection.InvokeAsync<_>("JoinRoom", roomId)
        member _.LeaveRoom    () = connection.InvokeAsync<_>("LeaveRoom")
        member _.ConnectToRoomPlayers() = connection.InvokeAsync<_>("ConnectToRoomPlayers")


let connectHub (testServer: TestServer) : Task<HubConnection * ISignalingHub> =
    let httpConnectionOptions (options: HttpConnectionOptions) =
        options.HttpMessageHandlerFactory <- fun _ -> testServer.CreateHandler()

    let setupJsonProtocol (options: JsonHubProtocolOptions) =
        JsonFSharpOptions
            .Default()
            .AddToJsonSerializerOptions(options.PayloadSerializerOptions)

    let url = testServer.BaseAddress.ToString() + "webrtc-signaling"

    let connection =
        HubConnectionBuilder()
            .WithUrl(url, httpConnectionOptions)
            .AddJsonProtocol(setupJsonProtocol)
            .Build()

    task {
        do! connection.StartAsync()
        return connection, SignalingHub(connection) :> ISignalingHub
    }
