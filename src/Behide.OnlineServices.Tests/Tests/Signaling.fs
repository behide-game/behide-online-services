module Behide.OnlineServices.Tests.SignalingHub

open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.AspNetCore.Http.Connections.Client
open Microsoft.Extensions.DependencyInjection
open System.Threading
open System.Threading.Tasks

open Expecto
open FsToolkit.ErrorHandling

open Behide.OnlineServices.Signaling
open System.Text.Json.Serialization



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
                conn1.On<int, ConnAttemptId>("CreateOffer", fun _playerId ->
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
                let! playerId =
                    signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                // Retrieve the updated room
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"


                // Check if the room connection info is correct
                Expect.equal playerId 2 "Peer ID should be 2 in that case"

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

        testList "ConnectToRoomPlayers" [
            testTask "Connect to room players" {
                // Create room
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub

                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Failed to create a room")

                // Join room
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                let! secondPlayerId =
                    signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Failed to join room")

                Expect.equal secondPlayerId 2 "The second player should have a playerId/peerId to 2"

                // Register "CreateOffer" handler on first player
                let mutable connAttemptId = None

                conn1.On<int, ConnAttemptId>("CreateOffer", fun _playerId ->
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok c -> connAttemptId <- Some c; c
                        | Error e -> failwithf "Failed to create connection attempt: %A" e)
                )
                |> ignore

                // Connect players
                let! (res: RoomConnectionInfo) =
                    signalingHub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantOk "Failed to create connection information for players")

                Expect.isEmpty res.FailedCreations "No connection attempt creation should be failed"

                let connAttemptId = Expect.wantSome connAttemptId "An connection attempt id should have been generated"
                Expect.sequenceEqual
                    (res.PlayersConnInfo |> List.ofArray)
                    [ { PeerId = 1; ConnAttemptId = connAttemptId } ]
                    "Players connection info should contain the first player connection info and only that"
            }

            testTheoryTask
                "Connect to room players with multiple players"
                [ 3, 0
                  10, 4
                  100, 99 ]
                <| fun (nbOfPeers: int, peerThatConnects: int) -> task {
                    let! players =
                        List.init nbOfPeers (fun _ -> testServer |> connectHub)
                        |> Task.WhenAll
                        |> Task.map List.ofArray

                    // Register handlers
                    let cts = new CancellationTokenSource()
                    let connAttemptIdsTask =
                        players
                        |> List.indexed
                        |> List.choose (fun (idx, (conn, hub)) ->
                            let connAttemptIdTcs = new TaskCompletionSource<Result<ConnAttemptId, _>>()
                            cts.Token.Register(fun _ -> connAttemptIdTcs.TrySetCanceled() |> ignore) |> ignore

                            conn.On<int, ConnAttemptId>("CreateOffer", fun _ ->
                                Common.fakeSdpDescription
                                |> hub.StartConnectionAttempt
                                |> Task.map (fun res ->
                                    res
                                    |> connAttemptIdTcs.SetResult
                                    |> ignore

                                    match res with
                                    | Ok c -> c
                                    | Error _ -> failwith "Failed to create connection attempt"
                                )
                            )
                            |> ignore

                            match idx = peerThatConnects with // Don't await the connection attempt creation for the peer that will connect
                            | true -> None
                            | false -> Some connAttemptIdTcs.Task
                        )
                        |> List.sequenceTaskResultA
                        |> Task.map (Flip.Expect.wantOk "Failed to create some connection attempt")

                    // Create room
                    let! roomId =
                        (players |> List.head |> snd).CreateRoom()
                        |> Task.map (Flip.Expect.wantOk "Failed to create room")

                    // Join room
                    do! players
                        |> List.tail
                        |> List.map (fun (_, hub) -> hub.JoinRoom roomId)
                        |> List.sequenceTaskResultA
                        |> Task.map (Flip.Expect.isOk "All players should be able to join the room")

                    // Connect players
                    let hub = players |> List.item peerThatConnects |> snd
                    let! res =
                        hub.ConnectToRoomPlayers()
                        |> Task.map (Flip.Expect.wantOk "Failed to create connection information for players")

                    cts.CancelAfter 1000
                    let! connAttemptIds = connAttemptIdsTask

                    Expect.isEmpty res.FailedCreations "All connection attempt creations should be successful"
                    Expect.hasLength res.PlayersConnInfo (players.Length - 1) "Their should be one player connection info per player to connect to"
                    Expect.hasLength connAttemptIds (players.Length - 1) "Their should be one connection attempt id per players to connect to"
                    Expect.hasLength connAttemptIds res.PlayersConnInfo.Length "Their should be one connection attempt id per player connection info"

                    Expect.containsAll
                        connAttemptIds
                        (res.PlayersConnInfo |> Array.map _.ConnAttemptId)
                        "Created connection attempt ids should be the same that the received connection attempt ids"
                }

            testTask "Connect to players without a CreateOffer handler" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                do! signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                let! (res: RoomConnectionInfo) =
                    signalingHub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantOk "Connecting to room players should return an \"Ok\" result")

                Expect.sequenceEqual
                    res.FailedCreations
                    [ 1 ] // 1 is the peerId of the first player, the one who created the room
                    "Joining room without add handler for CreateOffer should fail"

                Expect.isEmpty res.PlayersConnInfo "No connection info should be returned"
            }

            testTask "Connect to players while not in a room" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let! (error: Errors.ConnectToRoomPlayersError) =
                    signalingHub.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantError "Connecting to room players while not in a room should return an error")

                Expect.equal
                    error
                    Errors.ConnectToRoomPlayersError.NotInARoom
                    "Connecting to room players while not in a room should fail"
            }
        ]

        // testList "LeaveRoom" [
        //     testTask "Leave room" {
        //         let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub
        //         let! (conn2: HubConnection, signalingHub2: ISignalingHub) = testServer |> connectHub

        //         let mutable offerId = None

        //         // Add handler for creating offer
        //         conn1.On<int, ConnAttemptId>("CreateOffer", fun _playerId ->
        //             Common.fakeSdpDescription
        //             |> signalingHub1.StartConnectionAttempt
        //             |> Task.map (function
        //                 | Ok o -> offerId <- Some o; o
        //                 | Error e -> failwithf "Failed to create offer: %A" e)
        //         )
        //         |> ignore

        //         // Create a room and check if it was created
        //         let! roomId =
        //             signalingHub1.CreateRoom()
        //             |> Task.map (Flip.Expect.wantOk "Room creation should success")

        //         roomStore.Get roomId
        //         |> Flip.Expect.isSome "Room should be created"

        //         // Join the room
        //         do! signalingHub2.JoinRoom roomId
        //             |> Task.map (Flip.Expect.wantOk "Room joining should success")
        //             |> Task.map ignore // ignore the playerId returned by the hub

        //         // Leave the room
        //         do! signalingHub2.LeaveRoom()
        //             |> Task.map (Flip.Expect.isOk "Leaving room should success")

        //         // Check if the room was removed
        //         roomStore.Get roomId
        //         |> Flip.Expect.isNone "Room should be removed"

        //         // Check if the player is not in the room anymore
        //         let playerConn =
        //             playerConnStore.Get (conn2.ConnectionId |> ConnId.parse)
        //             |> Flip.Expect.wantSome "Player should still exist"

        //         Expect.isNone playerConn.RoomId "Player should not be in a room anymore"
        //     }
        // ]

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

                // Check if the offer has answerer (it should not)
                offerStore.Get offerId
                |> Flip.Expect.wantSome "Offer should exist"
                |> _.Answerer
                |> Flip.Expect.isNone "Offer should be marked as not answered"

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

                Expect.isSome offer.Answerer "Offer should has an answerer"

                Expect.equal
                    offer.InitiatorConnectionId
                    (conn1.ConnectionId |> ConnId.parse)
                    "Offer initiator should be the first player connection id"
            }

            testTask "Join nonexisting connection attempt" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let fakeOfferId = ConnAttemptId.create()

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
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

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

            testTask "Join connection attempt as the initiator" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                let! (error: Errors.JoinConnectionAttemptError) =
                    offerId
                    |> signalingHub.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Joining connection attempt as the initiator should return an error")

                Expect.equal
                    error
                    Errors.JoinConnectionAttemptError.InitiatorCannotJoin
                    "Joining connection attempt as the initiator should fail"
            }
        ]

        testList "SendAnswer" [
            testTask "Send answer" {
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                // Subscribe to SdpAnswerReceived event
                let sdpAnswerReceivedTcs = Common.TimedTaskCompletionSource<ConnAttemptId * SdpDescription>(1000)

                conn1.On("SdpAnswerReceived", fun (offerId: ConnAttemptId) (sdpDescription: SdpDescription) ->
                    sdpAnswerReceivedTcs.SetResult(offerId, sdpDescription)
                )
                |> ignore

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Send answer
                let answerSdpDesc = Common.fakeSdpDescription
                do! answerSdpDesc
                    |> signalingHub2.SendAnswer offerId
                    |> Task.map (Flip.Expect.isOk "Answer sending should success")

                // Test received answer
                let! (receivedOfferId: ConnAttemptId, receivedSdpDesc: SdpDescription) = sdpAnswerReceivedTcs.Task

                Expect.equal
                    receivedOfferId
                    offerId
                    "Received offer ID should be the same"

                Expect.equal
                    receivedSdpDesc
                    answerSdpDesc
                    "Received SDP description should be the same"
            }

            testTask "Send answer to nonexisting connection attempt" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let fakeOfferId = ConnAttemptId.create()

                let! (error: Errors.SendAnswerError) =
                    signalingHub.SendAnswer
                        fakeOfferId
                        Common.fakeSdpDescription
                    |> Task.map (Flip.Expect.wantError "Sending answer to nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendAnswerError.OfferNotFound
                    "Sending answer to nonexisting connection attempt should fail"
            }

            testTask "Send answer to not joined connection attempt" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Sending answer
                let! (error: Errors.SendAnswerError) =
                    signalingHub2.SendAnswer
                        offerId
                        Common.fakeSdpDescription
                    |> Task.map (Flip.Expect.wantError "Sending answer to not joined connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendAnswerError.NotAnswerer
                    "Sending answer to not joined connection attempt should fail"
            }

            testTask "Send answer to joined connection attempt by another player" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub3: ISignalingHub) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Sending answer
                let! (error: Errors.SendAnswerError) =
                    signalingHub3.SendAnswer
                        offerId
                        Common.fakeSdpDescription
                    |> Task.map (Flip.Expect.wantError "Sending answer should return an error")

                Expect.equal
                    error
                    Errors.SendAnswerError.NotAnswerer
                    "Sending answer connection attempt should fail"
            }
        ]

        testList "SendIceCandidate" [
            testTask "Send ice candidate" {
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                // Subscribe to IceCandidateReceived event
                let iceCandidateReceivedTcs = Common.TimedTaskCompletionSource<ConnAttemptId * IceCandidate>(1000)

                conn1.On("IceCandidateReceived", fun (offerId: ConnAttemptId) (iceCandidate: IceCandidate) ->
                    iceCandidateReceivedTcs.SetResult(offerId, iceCandidate)
                )
                |> ignore

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Send ice candidate
                let iceCandidate = Common.fakeIceCandidate
                do! iceCandidate
                    |> signalingHub2.SendIceCandidate offerId
                    |> Task.map (Flip.Expect.isOk "Ice candidate sending should success")

                // Test received ice candidate
                let! (receivedOfferId: ConnAttemptId, receivedIceCandidate: IceCandidate) = iceCandidateReceivedTcs.Task

                Expect.equal
                    receivedOfferId
                    offerId
                    "Received offer ID should be the same"

                Expect.equal
                    receivedIceCandidate
                    iceCandidate
                    "Received ice candidate should be the same"
            }

            testTask "Send ice candidate to nonexisting connection attempt" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let fakeOfferId = ConnAttemptId.create()

                let! (error: Errors.SendIceCandidateError) =
                    signalingHub.SendIceCandidate
                        fakeOfferId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate to nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.OfferNotFound
                    "Sending ice candidate to nonexisting connection attempt should fail"
            }

            testTask "Send ice candidate to not joined connection attempt" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Sending ice candidate
                let! (error: Errors.SendIceCandidateError) =
                    signalingHub2.SendIceCandidate
                        offerId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate to not joined connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.NotAnswerer
                    "Sending ice candidate to not joined connection attempt should fail"
            }

            testTask "Send ice candidate to joined connection attempt by another player" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub3: ISignalingHub) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Sending ice candidate
                let! (error: Errors.SendIceCandidateError) =
                    signalingHub3.SendIceCandidate
                        offerId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.NotParticipant
                    "Sending ice candidate connection attempt should fail"
            }

            testTask "Send ice candidate to connection attempt without answerer" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Sending ice candidate
                let! (error: Errors.SendIceCandidateError) =
                    signalingHub2.SendIceCandidate
                        offerId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.NotAnswerer
                    "Sending ice candidate connection attempt should fail"
            }
        ]
    ]
