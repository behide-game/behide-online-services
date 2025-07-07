module Behide.OnlineServices.Tests.Signaling.RoomManagement

open Expecto
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.SignalR.Client

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Tests
open Behide.OnlineServices.Tests.Signaling.Common

let tests testServer (roomStore: Hubs.Signaling.IRoomStore) =
    testList "RoomManagement" [
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
                conn1.On<int, ConnAttemptId>("ConnectionRequested", fun _playerId ->
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
                    [ KeyValuePair(conn1.ConnectionId |> ConnId.parse, 1)
                      KeyValuePair(conn2.ConnectionId |> ConnId.parse, 2) ]
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

            testTask "Joining room should give a unique playerId" {
                let! (_, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub3: ISignalingHub) = testServer |> connectHub

                // Initialization
                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                let playerId1 = 1 // The first player should always have the playerId 1

                // Join room
                let! playerId2 =
                    signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                Expect.equal playerId2 2 "The second player should have the playerId to 2"
                Expect.notEqual playerId1 playerId2 "The two players should have different playerId"

                // Leave and rejoin room
                do! signalingHub2.LeaveRoom()
                    |> Task.map (Flip.Expect.wantOk "Leaving room should success")

                let! playerId3 =
                    signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                Expect.equal playerId3 2 "The second player should have the playerId to 2"
                Expect.notEqual playerId1 playerId3 "The two players should have different playerId"

                // Join room with a third player
                let! playerId4 =
                    signalingHub3.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                Expect.equal playerId4 3 "The third player should have the playerId to 3"
                Expect.notEqual playerId1 playerId4 "The two players should have different playerId"
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
                let! (conn2: HubConnection, signalingHub2: ISignalingHub) = testServer |> connectHub

                let! secondPlayerId =
                    signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Failed to join room")

                Expect.equal secondPlayerId 2 "The second player should have a playerId/peerId to 2"

                // Register "ConnectionRequested" handler on first player
                let mutable connAttemptId = None

                conn1.On<int, ConnAttemptId>("ConnectionRequested", fun _playerId ->
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok c -> connAttemptId <- Some c; c
                        | Error e -> failtestf "Failed to create connection attempt: %A" e)
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

                // Check connections
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"

                let connId1 = conn1.ConnectionId |> ConnId.parse
                let connId2 = conn2.ConnectionId |> ConnId.parse
                let connection =
                    min connId1 connId2,
                    max connId1 connId2

                Expect.containsAll
                    room.Connections
                    [ connection ]
                    "Room should contain the connection between the two players"
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
                            let connAttemptIdTcs = TaskCompletionSource<Result<ConnAttemptId, _>>()
                            cts.Token.Register(fun _ -> connAttemptIdTcs.TrySetCanceled() |> ignore) |> ignore

                            conn.On<int, ConnAttemptId>("ConnectionRequested", fun _ ->
                                Common.fakeSdpDescription
                                |> hub.StartConnectionAttempt
                                |> Task.map (fun res ->
                                    connAttemptIdTcs.SetResult(res)

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

                    // Check connections
                    let room =
                        roomStore.Get roomId
                        |> Flip.Expect.wantSome "Room should still exist"

                    let connIdThatConnects =
                        players
                        |> List.item peerThatConnects
                        |> fst
                        |> _.ConnectionId
                        |> ConnId.parse

                    let expectedConnections =
                        players
                        |> List.choose (fun (conn, _) ->
                            let connId = conn.ConnectionId |> ConnId.parse

                            match connId <> connIdThatConnects with
                            | false -> None // The player that connects should not be connected to himself
                            | true ->
                                Some (min connIdThatConnects connId,
                                      max connIdThatConnects connId)
                        )

                    Expect.containsAll
                        room.Connections
                        expectedConnections
                        "Room should contain the all the new connections"
                }

            testTheoryTask
                "Simultaneously connect players"
                [ 3, [1; 2]
                  10, [5; 6; 7; 8; 9]
                  100, (List.init 50 ((+) 50))
                  100, (List.init 99 ((+) 1)) ]
                <| fun (nbOfPlayers: int, playersThatConnect: int list) -> task {
                    let! players =
                        List.init nbOfPlayers (fun _ -> testServer |> connectHub)
                        |> Task.WhenAll
                        |> Task.map List.ofArray

                    // Register handlers
                    let connectionAttemptIds = ConcurrentBag()
                    players |> List.iter (fun (hubConnection, hub) ->
                        hubConnection.On<int, ConnAttemptId>("ConnectionRequested", fun requestingPeerId ->
                            Task.Run<ConnAttemptId>(fun () ->
                                Common.fakeSdpDescription
                                |> hub.StartConnectionAttempt
                                |> TaskResult.tee connectionAttemptIds.Add
                                |> Task.map (Flip.Expect.wantOk "Connection attempt creation should succeed")
                            )
                        )
                        |> ignore
                    )

                    // Create room
                    let! roomId =
                        players
                        |> List.head
                        |> snd
                        |> _.CreateRoom()
                        |> Task.map (Flip.Expect.wantOk "Failed to create room")

                    // Join room
                    let! playerIndexByPlayerPeerId =
                        players
                        |> List.tail
                        |> List.mapi (fun idx (_, hub) ->
                            let playerIdx = idx + 1
                            hub.JoinRoom roomId
                            |> TaskResult.map (fun peerId -> peerId, playerIdx)
                        )
                        |> List.sequenceTaskResultA
                        |> Task.map (
                            Flip.Expect.wantOk "All players should be able to join the room"
                            >> List.append [ 1, 0 ] // Room creator
                            >> dict
                        )

                    // Connect players
                    let cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
                    let connectionsMade = Array.init nbOfPlayers (fun _ -> Array.create nbOfPlayers false)

                    do! Parallel.ForEachAsync(
                        playersThatConnect,
                        ParallelOptions(
                            MaxDegreeOfParallelism = playersThatConnect.Length,
                            CancellationToken = cts.Token
                        ),
                        Func<int, _, _>(fun playerIdx _ ->
                            task {
                                let hub = players[playerIdx] |> snd

                                let! res = hub.ConnectToRoomPlayers()
                                let connectionInfo = Expect.wantOk res "ConnectToRoomPlayers method should succeed"
                                Expect.isEmpty connectionInfo.FailedCreations $"All connection attempt creations should be successful: PlayerIdx {playerIdx}"

                                for connectionInfo in connectionInfo.PlayersConnInfo do
                                    let targetPlayerIdx = playerIndexByPlayerPeerId[connectionInfo.PeerId]
                                    connectionsMade[playerIdx][targetPlayerIdx] <- true
                                    connectionsMade[targetPlayerIdx][playerIdx] <- true
                            }
                            |> ValueTask
                        )
                    )

                    // Check that players that should connect are connected to every other players
                    playersThatConnect |> List.iter (fun connectingPlayerIdx ->
                        let playerConnections = connectionsMade[connectingPlayerIdx]

                        playerConnections |> Array.iteri (fun targetPlayerIdx isConnected ->
                            match connectingPlayerIdx = targetPlayerIdx with
                            | true ->
                                Expect.isFalse
                                    isConnected
                                    $"Connection {connectingPlayerIdx} <-> {targetPlayerIdx} should not be established. We cannot connect to ourself"
                            | false ->
                                Expect.isTrue
                                    isConnected
                                    $"Connection {connectingPlayerIdx} <-> {targetPlayerIdx} should be established"
                        )
                    )

                    // Check connections are well represented on the room store
                    let room = roomStore.Get roomId |> Flip.Expect.wantSome "Room should still exist"

                    Expect.isEmpty room.ConnectionsInProgress "No connection should still be in progress"

                    let expectedConnections =
                        playersThatConnect |> List.collect (fun playerIdx ->
                            let requestingConnectionId =
                                players[playerIdx]
                                |> fst
                                |> _.ConnectionId
                                |> ConnId.parse

                            List.init nbOfPlayers (fun otherPlayerIdx ->
                                players[otherPlayerIdx]
                                |> fst
                                |> _.ConnectionId
                                |> ConnId.parse
                            )
                            |> List.filter ((<>) requestingConnectionId)
                            |> List.map (fun otherConnectionId ->
                                min requestingConnectionId otherConnectionId,
                                max requestingConnectionId otherConnectionId
                            )
                        )

                    Expect.containsAll room.Connections expectedConnections "All connections should be present"
                }

            testTask "Connect to players without a ConnectionRequested handler" {
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
                    "Joining room without add handler for ConnectionRequested should fail"

                Expect.isEmpty res.PlayersConnInfo "No connection info should be returned"
            }

            testTask "Connect to players with a blocking ConnectionRequested handler" {
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                // Create room
                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Join room
                do! signalingHub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                // Register blocking "ConnectionRequested" handler on first player
                conn1.On<int, ConnAttemptId>("ConnectionRequested", fun _playerId ->
                    while true do () // Never returns
                    ConnAttemptId.create()
                )
                |> ignore

                let! (res: RoomConnectionInfo) =
                    signalingHub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantOk "Connecting to room players should return an \"Ok\" result")

                Expect.sequenceEqual
                    res.FailedCreations
                    [ 1 ] // 1 is the peerId of the first player, the one who created the room
                    "Joining room with a blocking ConnectionRequested handler should fail"

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

        testList "LeaveRoom" [
            testTask "Leave room" {
                // Create room
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub

                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Join room
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                do! roomId
                    |> signalingHub2.JoinRoom
                    |> Task.map (Flip.Expect.isOk "Room joining should success")

                // Leave room
                do! signalingHub2.LeaveRoom()
                    |> Task.map (Flip.Expect.wantOk "Leaving room should success")

                // Check if the player is not in the room anymore
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"

                Expect.sequenceEqual
                    room.Players
                    [ KeyValuePair(conn1.ConnectionId |> ConnId.parse, 1) ]
                    "Room should not contain the second player"

                Expect.isEmpty room.Connections "Room should not contain any connection"
            }

            testTask "Leave room where we are connected to players" {
                // Create room
                let! (conn1: HubConnection, signalingHub1: ISignalingHub) = testServer |> connectHub

                let! roomId =
                    signalingHub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Join room
                let! (_, signalingHub2: ISignalingHub) = testServer |> connectHub

                do! roomId
                    |> signalingHub2.JoinRoom
                    |> Task.map (Flip.Expect.isOk "Room joining should success")

                // Connect to room players
                conn1.On<int, ConnAttemptId>("ConnectionRequested", fun _playerId ->
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok c -> c
                        | Error e -> failwithf "Failed to create connection attempt: %A" e)
                )
                |> ignore

                do! signalingHub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.isOk "Connecting to room players should success")

                // Leave room
                do! signalingHub2.LeaveRoom()
                    |> Task.map (Flip.Expect.isOk "Leaving room should success")

                // Check if the player is not in the room anymore
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"

                Expect.sequenceEqual
                    room.Players
                    [ KeyValuePair(conn1.ConnectionId |> ConnId.parse, 1) ]
                    "Room should not contain the second player"

                Expect.isEmpty room.Connections "Room should not contain any connection"
            }

            testTask "Leave room while not in a room" {
                let! (_, signalingHub: ISignalingHub) = testServer |> connectHub

                let! (error: Errors.LeaveRoomError) =
                    signalingHub.LeaveRoom()
                    |> Task.map (Flip.Expect.wantError "Leaving room while not in a room should return an error")

                Expect.equal
                    error
                    Errors.LeaveRoomError.NotInARoom
                    "Leaving room while not in a room should fail"
            }

            testTask "Leave room while being the last player delete the room" {
                let! (_, hub1: ISignalingHub) = testServer |> connectHub

                // Create room
                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Leave room
                do! hub1.LeaveRoom()
                    |> Task.map (Flip.Expect.wantOk "Leaving room should success")

                // Check if the room is removed
                roomStore.Get roomId
                |> Flip.Expect.isNone "Room should be removed"
            }
        ]
    ]
