module Behide.OnlineServices.Tests.SignalingHub

open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.AspNetCore.Http.Connections.Client
open Microsoft.Extensions.DependencyInjection
open System.Threading.Tasks

open Expecto
open FsToolkit.ErrorHandling

open Behide.OnlineServices
open Behide.OnlineServices.Hubs.Signaling
open System.Text.Json.Serialization



type SignalingHub(connection: HubConnection) =
    interface ISignalingHub with
        member _.StartConnectionAttempt sdpDescription       = connection.InvokeAsync<_>("StartConnectionAttempt", sdpDescription)
        member _.JoinConnectionAttempt  offerId              = connection.InvokeAsync<_>("JoinConnectionAttempt",  offerId)
        member _.SendAnswer             offerId iceCandidate = connection.InvokeAsync<_>("SendAnswer",             offerId, iceCandidate)
        member _.SendIceCandidate       offerId iceCandidate = connection.InvokeAsync<_>("SendIceCandidate",       offerId, iceCandidate)

        member _.CreateRoom   () = connection.InvokeAsync<_>("CreateRoom")
        member _.JoinRoom roomId = connection.InvokeAsync<_>("JoinRoom", roomId)


let connectHub (testServer: TestServer) : Task<HubConnection * ISignalingHub> =
    let httpConnectionOptions (options: HttpConnectionOptions) =
        options.HttpMessageHandlerFactory <- fun _ -> testServer.CreateHandler()

    let setupJsonProtocol (options: JsonHubProtocolOptions) =
        JsonFSharpOptions
            .Default()
            .AddToJsonSerializerOptions(options.PayloadSerializerOptions);

    let url = testServer.BaseAddress.ToString() + "webrtc-signaling"

    let connection =
        (new HubConnectionBuilder())
            .WithUrl(url, httpConnectionOptions)
            .AddJsonProtocol(setupJsonProtocol)
            .Build()

    task {
        do! connection.StartAsync()
        return connection, SignalingHub(connection) :> ISignalingHub
    }


[<Tests>]
let signalingTests =
    let testServer, offerStore, roomStore, playerConnStore = Common.createTestServer()

    testList "Signaling tests" [
        testTask "Signaling hub connection should success" {
            let! (connection: HubConnection, _) = testServer |> connectHub

            Expect.equal connection.State HubConnectionState.Connected "Should be connected to the hub"

            let connId = connection.ConnectionId |> ConnId.parse
            let playerConn =
                playerConnStore.Get connId
                |> Flip.Expect.wantSome "Client should be registered in the player connections store"

            Expect.equal playerConn.ConnectionId connId "Connection ID should be the same"
        }

        testList "CreateRoom" [
            testTask "Create room should success" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let! roomId =
                    signalingHub.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should be created"

                Expect.equal room.Id roomId "Room ID should be the same"
            }

            testTask "Create room while already in a room" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                do! signalingHub.CreateRoom()
                    |> Task.map (Flip.Expect.isOk "Room creation should success")

                let! (error: Errors.CreateRoomError) =
                    signalingHub.CreateRoom()
                    |> Task.map (Flip.Expect.wantError "Second room creation should return an error")

                Expect.equal
                    error
                    Errors.CreateRoomError.PlayerAlreadyInARoom
                    "Room creation should fail"
            }
        ]

        testList "JoinRoom" [
            testTask "Join room should success" {
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (conn2: HubConnection, signalingHub2: ISignalingHub) = testServer |> connectHub

                let mutable offerId = None

                // Add handler for creating offer
                conn1.On<int, OfferId>("CreateOffer", fun _playerId ->
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok o -> offerId <- Some o; o
                        | Error e -> failwithf "Failed to create offer: %A" e)
                )
                |> ignore


                // Create a room and check if it was created
                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                roomStore.Get roomId
                |> Flip.Expect.isSome "Room should be created"


                // Join the room
                let! (roomConnectionInfo: RoomConnectionInfo) =
                    signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                // Retrieve the updated room
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"


                // Check if the room connection info is correct
                Expect.equal roomConnectionInfo.PeerId 2 "Peer ID should be 2 in that case"

                Expect.sequenceEqual
                    roomConnectionInfo.PlayersConnectionInfo
                    [ { PeerId = 1
                        OfferId = offerId |> Flip.Expect.wantSome "An offer id should have been generated" } ]
                    "Room connection info should contain the first player connection info and only that"

                // Check if the room contains both players
                Expect.equal
                    conn1.ConnectionId
                    (room.Initiator |> ConnId.raw)
                    "Room initiator should be the first player connection id"

                Expect.containsAll
                    room.Players
                    [ 1, conn1.ConnectionId |> ConnId.parse
                      2, conn2.ConnectionId |> ConnId.parse ]
                    "Room should contain both player connection ids"
            }

            testTask "Join room while already in a room" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let! roomId =
                    signalingHub.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                let! (error: Errors.JoinRoomError) =
                    roomId
                    |> signalingHub.JoinRoom
                    |> Task.map (Flip.Expect.wantError "Joining room should return an error")

                Expect.equal
                    error
                    Errors.JoinRoomError.PlayerAlreadyInARoom
                    "Room joining should fail"
            }

            testTask "Join nonexisting room" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let fakeRoomId = RoomId.create()

                let! (error: Errors.JoinRoomError) =
                    fakeRoomId
                    |> signalingHub.JoinRoom
                    |> Task.map (Flip.Expect.wantError "Joining nonexisting room should return an error")

                Expect.equal
                    error
                    Errors.JoinRoomError.RoomNotFound
                    "Nonexisting room joining should fail"
            }
        ]

        testList "StartConnectionAttempt" [
            testTask "Create connection attempt" {
                let! (conn: HubConnection, signalingHub: ISignalingHub) = testServer |> connectHub

                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Check if the offer was created
                let offer =
                    offerStore.Get offerId
                    |> Flip.Expect.wantSome "Offer should be created"

                Expect.equal
                    offer.InitiatorConnectionId
                    (conn.ConnectionId |> ConnId.parse)
                    "Offer initiator should be the player connection id"

                Expect.equal
                    offer.SdpDescription
                    Common.fakeSdpDescription
                    "Offer SDP description should be the same"
            }
        ]

        testList "JoinConnectionAttempt" [
            testTask "Join connection attempt" {
                // Create a connection attempt
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub

                let originalSdpDesc = Common.fakeSdpDescription

                let! offerId =
                    originalSdpDesc
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Check if the offer is marked as not answered
                offerStore.Get offerId
                |> Flip.Expect.wantSome "Offer should exist"
                |> _.Answered
                |> Flip.Expect.isFalse "Offer should be marked as not answered"

                // Join connection attempt
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                let! sdpDescription =
                    offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt joining should success")

                Expect.equal
                    sdpDescription
                    originalSdpDesc
                    "SDP description should be the same"

                // Check if the offer is marked as answered
                let offer =
                    offerStore.Get offerId
                    |> Flip.Expect.wantSome "Offer should exist"

                Expect.isTrue offer.Answered "Offer should be marked as answered"

                Expect.equal
                    offer.InitiatorConnectionId
                    (conn1.ConnectionId |> ConnId.parse)
                    "Offer initiator should be the first player connection id"
            }

            testTask "Join nonexisting connection attempt" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let fakeOfferId = OfferId.create()

                let! (error: Errors.JoinConnectionAttemptError) =
                    fakeOfferId
                    |> signalingHub.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Joining nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.JoinConnectionAttemptError.OfferNotFound
                    "Nonexisting connection attempt joining should fail"
            }

            testTask "Join already answered connection attempt" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub3: ISignalingHub) = testServer |> connectHub

                // Create a connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                let! _sdpDescription =
                    offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt joining should success")

                // Try to join the same connection attempt again
                let! (error: Errors.JoinConnectionAttemptError) =
                    offerId
                    |> signalingHub3.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Joining already answered connection attempt should return an error")

                Expect.equal
                    error
                    Errors.JoinConnectionAttemptError.OfferAlreadyAnswered
                    "Already answered connection attempt joining should fail"
            }
        ]
    ]
