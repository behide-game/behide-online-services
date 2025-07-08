module Behide.OnlineServices.Tests.Signaling.RoomManagement

open Expecto
open FsToolkit.ErrorHandling

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
                let! (hub: TestHubClient) = testServer |> connectHub

                let! roomId =
                    hub.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should be created"

                Expect.equal room.Id roomId "Room ID should be the same"
            }

            testTask "Create room while already in a room" {
                let! (hub: TestHubClient) = testServer |> connectHub

                do! hub.CreateRoom()
                    |> Task.map (Flip.Expect.isOk "Room creation should success")

                let! (error: Errors.CreateRoomError) =
                    hub.CreateRoom()
                    |> Task.map (Flip.Expect.wantError "Second room creation should return an error")

                Expect.equal
                    error
                    Errors.CreateRoomError.PlayerAlreadyInARoom
                    "Room creation should fail"
            }
        ]

        testList "JoinRoom" [
            testTask "Join room should success" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                let mutable offerId = None

                // Add handler for creating offer
                hub1.SetHandlerFor.ConnectionRequested(fun _playerId ->
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok o -> offerId <- Some o; o
                        | Error e -> failwithf "Failed to create offer: %A" e)
                )
                |> ignore


                // Create a room and check if it was created
                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                roomStore.Get roomId
                |> Flip.Expect.isSome "Room should be created"


                // Join the room
                let! peerId =
                    hub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                // Retrieve the updated room
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"


                // Check if the room connection info is correct
                Expect.equal peerId 2 "Peer ID should be 2 in that case"

                // Check if the room contains both players
                Expect.containsAll
                    room.Players
                    [ KeyValuePair(hub1.PlayerId, 1)
                      KeyValuePair(hub2.PlayerId, 2) ]
                    "Room should contain both player connection ids"
            }

            testTask "Join room while already in a room" {
                let! (hub: TestHubClient) = testServer |> connectHub

                let! roomId =
                    hub.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                let! (error: Errors.JoinRoomError) =
                    roomId
                    |> hub.JoinRoom
                    |> Task.map (Flip.Expect.wantError "Joining room should return an error")

                Expect.equal
                    error
                    Errors.JoinRoomError.PlayerAlreadyInARoom
                    "Room joining should fail"
            }

            testTask "Join nonexisting room" {
                let! (hub: TestHubClient) = testServer |> connectHub

                let fakeRoomId = RoomId.create()

                let! (error: Errors.JoinRoomError) =
                    fakeRoomId
                    |> hub.JoinRoom
                    |> Task.map (Flip.Expect.wantError "Joining nonexisting room should return an error")

                Expect.equal
                    error
                    Errors.JoinRoomError.RoomNotFound
                    "Nonexisting room joining should fail"
            }

            testTask "Joining room should give a unique peerId" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub
                let! (hub3: TestHubClient) = testServer |> connectHub

                // Initialization
                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                let peerId1 = 1 // The first player should always have the peerId 1

                // Join room
                let! peerId2 =
                    hub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                Expect.equal peerId2 2 "The second player should have the peerId to 2"
                Expect.notEqual peerId1 peerId2 "The two players should have different peerId"

                // Leave and rejoin room
                do! hub2.LeaveRoom()
                    |> Task.map (Flip.Expect.wantOk "Leaving room should success")

                let! peerId3 =
                    hub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                Expect.equal peerId3 2 "The second player should have the peerId to 2"
                Expect.notEqual peerId1 peerId3 "The two players should have different peerId"

                // Join room with a third player
                let! peerId4 =
                    hub3.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                Expect.equal peerId4 3 "The third player should have the peerId to 3"
                Expect.notEqual peerId1 peerId4 "The two players should have different peerId"
            }

            testTheoryTask
                "Simultaneously joining room should give unique peerIds"
                [ 3; 5; 10; 100 ]
                (fun nbOfPlayers -> task {
                    let! roomCreator = testServer |> connectHub
                    let! joiningPlayers =
                        List.init nbOfPlayers (fun _ -> testServer |> connectHub)
                        |> Task.WhenAll

                    // Create room
                    let! roomId = roomCreator.CreateRoom() |> Task.map (Flip.Expect.wantOk "Room creation should success")

                    // Join room
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
                    let peerIds = ConcurrentBag([ roomCreator.PlayerId, 1 ])

                    do! Parallel.ForEachAsync(
                        joiningPlayers,
                        ParallelOptions(
                            MaxDegreeOfParallelism = nbOfPlayers,
                            CancellationToken = cts.Token
                        ),
                        Func<TestHubClient, _, _>(fun player ct ->
                            player.JoinRoom(roomId)
                            |> Task.map (Flip.Expect.wantOk "Should be able to join the room")
                            |> Task.map (fun peerId -> peerIds.Add (player.PlayerId, peerId))
                            |> ValueTask
                        )
                    )

                    // Check
                    Expect.hasLength peerIds (nbOfPlayers + 1) "All players should have a peer id"

                    let distinctPeerIds = peerIds |> Array.ofSeq |> Array.distinct
                    Expect.hasLength distinctPeerIds peerIds.Count "Peer ids should all be unique"

                    let room = roomStore.Get roomId |> Flip.Expect.wantSome "Room should still exist"
                    let roomPeerIds =
                        room.Players
                        |> Array.ofSeq
                        |> Array.map _.Deconstruct()

                    Expect.containsAll roomPeerIds distinctPeerIds "Room should have assigned the peer ids to the correct players"
                })
        ]

        testList "ConnectToRoomPlayers" [
            testTask "Connect to room players" {
                // Create room
                let! (hub1: TestHubClient) = testServer |> connectHub

                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Failed to create a room")

                // Join room
                let! (hub2: TestHubClient) = testServer |> connectHub

                let! secondPeerId =
                    hub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Failed to join room")

                Expect.equal secondPeerId 2 "The second player should have a peerId to 2"

                // Register "ConnectionRequested" handler on first player
                let mutable connAttemptId = None

                hub1.SetHandlerFor.ConnectionRequested(fun _peerId ->
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok c -> connAttemptId <- Some c; c
                        | Error e -> failtestf "Failed to create connection attempt: %A" e)
                )
                |> ignore

                // Connect players
                let! (res: RoomConnectionInfo) =
                    hub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantOk "Failed to create connection information for players")

                Expect.isEmpty res.FailedCreations "No connection attempt creation should be failed"

                let connAttemptId = Expect.wantSome connAttemptId "An connection attempt id should have been generated"
                Expect.sequenceEqual
                    (res.PlayersConnectionInfo |> List.ofArray)
                    [ { PeerId = 1; ConnAttemptId = connAttemptId } ]
                    "Players connection info should contain the first player connection info and only that"

                // Check connections
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"

                Expect.containsAll
                    room.Connections
                    [ Pair.create hub1.PlayerId hub2.PlayerId ]
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
                    use cts = new CancellationTokenSource()
                    let connAttemptIdsTask =
                        players
                        |> List.indexed
                        |> List.choose (fun (idx, hub) ->
                            let connAttemptIdTcs = TaskCompletionSource<Result<ConnectionAttemptId, _>>()
                            cts.Token.Register(fun _ -> connAttemptIdTcs.TrySetCanceled() |> ignore) |> ignore

                            hub.SetHandlerFor.ConnectionRequested(fun _ ->
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
                        (players |> List.head).CreateRoom()
                        |> Task.map (Flip.Expect.wantOk "Failed to create room")

                    // Join room
                    do! players
                        |> List.tail
                        |> List.map (fun hub -> hub.JoinRoom roomId)
                        |> List.sequenceTaskResultA
                        |> Task.map (Flip.Expect.isOk "All players should be able to join the room")

                    // Connect players
                    let! res =
                        players[peerThatConnects].ConnectToRoomPlayers()
                        |> Task.map (Flip.Expect.wantOk "Failed to create connection information for players")

                    cts.CancelAfter 1000
                    let! connAttemptIds = connAttemptIdsTask

                    Expect.isEmpty res.FailedCreations "All connection attempt creations should be successful"
                    Expect.hasLength res.PlayersConnectionInfo (players.Length - 1) "Their should be one player connection info per player to connect to"
                    Expect.hasLength connAttemptIds (players.Length - 1) "Their should be one connection attempt id per players to connect to"
                    Expect.hasLength connAttemptIds res.PlayersConnectionInfo.Length "Their should be one connection attempt id per player connection info"

                    Expect.containsAll
                        connAttemptIds
                        (res.PlayersConnectionInfo |> Array.map _.ConnAttemptId)
                        "Created connection attempt ids should be the same that the received connection attempt ids"

                    // Check connections
                    let room =
                        roomStore.Get roomId
                        |> Flip.Expect.wantSome "Room should still exist"

                    let playerIdThatConnects =
                        players
                        |> List.item peerThatConnects
                        |> _.PlayerId

                    let expectedConnections =
                        players
                        |> List.choose (fun hub ->
                            match hub.PlayerId <> playerIdThatConnects with
                            | false -> None // The player that connects should not be connected to himself
                            | true -> Some <| Pair.create playerIdThatConnects hub.PlayerId
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
                    players |> List.iter (fun hub ->
                        hub.SetHandlerFor.ConnectionRequested(fun _ ->
                            Task.Run<ConnectionAttemptId>(fun () ->
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
                        |> _.CreateRoom()
                        |> Task.map (Flip.Expect.wantOk "Failed to create room")

                    // Join room
                    let! playerIndexByPlayerPeerId =
                        players
                        |> List.tail
                        |> List.mapi (fun idx hub ->
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
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
                    let connectionsMade = Array.init nbOfPlayers (fun _ -> Array.create nbOfPlayers false)

                    do! Parallel.ForEachAsync(
                        playersThatConnect,
                        ParallelOptions(
                            MaxDegreeOfParallelism = playersThatConnect.Length,
                            CancellationToken = cts.Token
                        ),
                        Func<int, _, _>(fun playerIdx _ ->
                            task {
                                let hub = players[playerIdx]

                                let! res = hub.ConnectToRoomPlayers()
                                let connectionInfo = Expect.wantOk res "ConnectToRoomPlayers method should succeed"
                                Expect.isEmpty connectionInfo.FailedCreations $"All connection attempt creations should be successful: PlayerIdx {playerIdx}"

                                for connectionInfo in connectionInfo.PlayersConnectionInfo do
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
                            let requestingPlayerId = players[playerIdx].PlayerId

                            List.init nbOfPlayers (fun otherPlayerIdx -> players[otherPlayerIdx].PlayerId)
                            |> List.filter ((<>) requestingPlayerId)
                            |> List.map (Pair.create requestingPlayerId)
                        )

                    Expect.containsAll room.Connections expectedConnections "All connections should be present"
                }

            testTask "Connect to players without a ConnectionRequested handler" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                do! hub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                let! (res: RoomConnectionInfo) =
                    hub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantOk "Connecting to room players should return an \"Ok\" result")

                Expect.sequenceEqual
                    res.FailedCreations
                    [ 1 ] // 1 is the peerId of the first player, the one who created the room
                    "Joining room without add handler for ConnectionRequested should fail"

                Expect.isEmpty res.PlayersConnectionInfo "No connection info should be returned"
            }

            testTask "Connect to players with a blocking ConnectionRequested handler" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                // Create room
                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Join room
                do! hub2.JoinRoom roomId
                    |> Task.map (Flip.Expect.wantOk "Room joining should success")

                // Register blocking "ConnectionRequested" handler on first player
                hub1.SetHandlerFor.ConnectionRequested(fun _playerId ->
                    while true do () // Never returns
                    ConnectionAttemptId.create()
                )
                |> ignore

                let! (res: RoomConnectionInfo) =
                    hub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.wantOk "Connecting to room players should return an \"Ok\" result")

                Expect.sequenceEqual
                    res.FailedCreations
                    [ 1 ] // 1 is the peerId of the first player, the one who created the room
                    "Joining room with a blocking ConnectionRequested handler should fail"

                Expect.isEmpty res.PlayersConnectionInfo "No connection info should be returned"
            }

            testTask "Connect to players while not in a room" {
                let! (hub: TestHubClient) = testServer |> connectHub

                let! (error: Errors.ConnectToRoomPlayersError) =
                    hub.ConnectToRoomPlayers()
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
                let! (hub1: TestHubClient) = testServer |> connectHub

                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Join room
                let! (hub2: TestHubClient) = testServer |> connectHub

                do! roomId
                    |> hub2.JoinRoom
                    |> Task.map (Flip.Expect.isOk "Room joining should success")

                // Leave room
                do! hub2.LeaveRoom()
                    |> Task.map (Flip.Expect.wantOk "Leaving room should success")

                // Check if the player is not in the room anymore
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"

                Expect.sequenceEqual
                    room.Players
                    [ KeyValuePair(hub1.PlayerId, 1) ]
                    "Room should not contain the second player"

                Expect.isEmpty room.Connections "Room should not contain any connection"
            }

            testTask "Leave room where we are connected to players" {
                // Create room
                let! (hub1: TestHubClient) = testServer |> connectHub

                let! roomId =
                    hub1.CreateRoom()
                    |> Task.map (Flip.Expect.wantOk "Room creation should success")

                // Join room
                let! (hub2: TestHubClient) = testServer |> connectHub

                do! roomId
                    |> hub2.JoinRoom
                    |> Task.map (Flip.Expect.isOk "Room joining should success")

                // Connect to room players
                hub1.SetHandlerFor.ConnectionRequested(fun _playerId ->
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (function
                        | Ok c -> c
                        | Error e -> failwithf "Failed to create connection attempt: %A" e)
                )
                |> ignore

                do! hub2.ConnectToRoomPlayers()
                    |> Task.map (Flip.Expect.isOk "Connecting to room players should success")

                // Leave room
                do! hub2.LeaveRoom()
                    |> Task.map (Flip.Expect.isOk "Leaving room should success")

                // Check if the player is not in the room anymore
                let room =
                    roomStore.Get roomId
                    |> Flip.Expect.wantSome "Room should still exist"

                Expect.sequenceEqual
                    room.Players
                    [ KeyValuePair(hub1.PlayerId, 1) ]
                    "Room should not contain the second player"

                Expect.isEmpty room.Connections "Room should not contain any connection"
            }

            testTask "Leave room while not in a room" {
                let! (hub: TestHubClient) = testServer |> connectHub

                let! (error: Errors.LeaveRoomError) =
                    hub.LeaveRoom()
                    |> Task.map (Flip.Expect.wantError "Leaving room while not in a room should return an error")

                Expect.equal
                    error
                    Errors.LeaveRoomError.NotInARoom
                    "Leaving room while not in a room should fail"
            }

            testTask "Leave room while being the last player delete the room" {
                let! (hub1: TestHubClient) = testServer |> connectHub

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

            testTheoryTask
                "Leave room while other players are joining"
                [ (3, 1, 1)
                  (3, 3, 2)
                  (3, 2, 3)
                  (100, 50, 50)
                  (100, 100, 42) ]
                (fun (initialPlayerCount, leavingPlayerCount, joiningPlayerCount) -> task {
                    let! inRoomPlayers = List.init initialPlayerCount (fun _ -> testServer |> connectHub) |> Task.WhenAll
                    let! joiningPlayers = List.init joiningPlayerCount (fun _ -> testServer |> connectHub) |> Task.WhenAll
                    let leavingPlayers = ArraySegment(inRoomPlayers, initialPlayerCount - leavingPlayerCount, leavingPlayerCount)

                    let! roomId = inRoomPlayers[0].CreateRoom() |> Task.map (Flip.Expect.wantOk "Room creation should success")
                    do! inRoomPlayers
                        |> Array.tail
                        |> Array.map _.JoinRoom(roomId)
                        |> Task.WhenAll
                        |> Task.map (Array.iter (Flip.Expect.isOk "Should be able to join room"))

                    use cts = new CancellationTokenSource()
                    let joinTcs = TaskCompletionSource()
                    let leaveTcs = TaskCompletionSource()

                    let join () =
                        Parallel.ForEachAsync(
                            joiningPlayers,
                            ParallelOptions(
                                MaxDegreeOfParallelism = joiningPlayers.Length,
                                CancellationToken = cts.Token
                            ),
                            Func<TestHubClient, _, _>(fun player ct ->
                                player.JoinRoom(roomId)
                                |> Task.map (Flip.Expect.isOk "Should be able to join the room")
                                |> ValueTask
                            )
                        ).ContinueWith(fun _ -> joinTcs.SetResult())
                        |> ignore
                    let leave () =
                        Parallel.ForEachAsync(
                            leavingPlayers,
                            ParallelOptions(
                                MaxDegreeOfParallelism = joiningPlayers.Length,
                                CancellationToken = cts.Token
                            ),
                            Func<TestHubClient, _, _>(fun player ct ->
                                player.LeaveRoom()
                                |> Task.map (Flip.Expect.isOk "Should be able to leave the room")
                                |> ValueTask
                            )
                        ).ContinueWith(fun _ -> leaveTcs.SetResult())
                        |> ignore

                    cts.CancelAfter(TimeSpan.FromSeconds 20.)
                    Parallel.Invoke(join, leave)
                    do! Task.WhenAll(joinTcs.Task, leaveTcs.Task)

                    let room = roomStore.Get roomId |> Flip.Expect.wantSome "Room should still exist"

                    // Check if players correctly leaved
                    Expect.all
                        leavingPlayers
                        (fun player -> room.Players.ContainsKey player.PlayerId |> not)
                        "All leaving players id should be absent from the room dict"

                    // Check if players correctly joined
                    Expect.all
                        joiningPlayers
                        (fun player -> room.Players.ContainsKey player.PlayerId)
                        "All joining players id should be present in the room dict"

                    Expect.equal
                        room.Players.Count
                        (initialPlayerCount - leavingPlayerCount + joiningPlayerCount)
                        "The room should have the correct number of players"
                })
        ]
    ]
