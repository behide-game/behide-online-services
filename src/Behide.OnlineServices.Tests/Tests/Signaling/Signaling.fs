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

            let playerId = connection.ConnectionId |> PlayerId.fromHubConnectionId
            let player =
                playerConnStore.Get playerId
                |> Flip.Expect.wantSome "Client should be registered in the player connections store"

            Expect.equal player.Id playerId "Player ids should be the same"
        }

        WebRTCSignaling.tests testServer connectionAttemptStore
        RoomManagement.tests testServer roomStore
    ]
