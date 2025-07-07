module Behide.OnlineServices.Tests.Signaling.All

open Expecto
open Microsoft.AspNetCore.SignalR.Client

open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Tests
open Behide.OnlineServices.Tests.Signaling
open Behide.OnlineServices.Tests.Signaling.Common

[<Tests>]
let signalingTests =
    let testServer, connectionAttemptStore, roomStore, playerConnStore = Common.createTestServer()

    testList "Signaling" [
        testTask "Signaling hub connection should success" {
            let! (connection: HubConnection, _) = testServer |> connectHub

            Expect.equal connection.State HubConnectionState.Connected "Should be connected to the hub"

            let connId = connection.ConnectionId |> ConnId.parse
            let playerConn =
                playerConnStore.Get connId
                |> Flip.Expect.wantSome "Client should be registered in the player connections store"

            Expect.equal playerConn.ConnectionId connId "Connection ID should be the same"
        }

        RoomManagement.tests testServer roomStore
        WebRTCSignaling.tests testServer connectionAttemptStore
    ]
