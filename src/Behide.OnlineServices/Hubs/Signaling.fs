module Behide.OnlineServices.Hubs.Signaling

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Signaling.Errors

/// Store of player states in the signaling process
type IPlayerConnectionStore = Store.IStore<ConnId, PlayerConnection>
type PlayerConnectionStore = Store.Store<ConnId, PlayerConnection>

/// WebRTC connection attempts store
type IConnAttemptStore = Store.IStore<ConnAttemptId, ConnAttempt>
type ConnAttemptStore = Store.Store<ConnAttemptId, ConnAttempt>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>

type SignalingHub(connAttemptStore: IConnAttemptStore, roomStore: IRoomStore, playerConnectionStore: IPlayerConnectionStore) =
    inherit Hub<ISignalingClient>()
    // Should interface ISignalingHub, but it makes the methods not callable from the client

    // --- Player Connection Management ---
    override hub.OnConnectedAsync() =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let playerConn =
                { ConnectionId = playerConnId
                  ConnAttemptIds = []
                  Room = None }

            do! playerConnectionStore.Add
                    playerConnId
                    playerConn
                |> Result.requireTrue "Failed to add player connection"
        }
        |> TaskResult.mapError (printfn "Error occurred while registering player: %s")
        |> Task.map ignore
        :> Task

    override hub.OnDisconnectedAsync _exn =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnId
                |> playerConnectionStore.Get
                |> Result.ofOption "Player connection not found"

            // Remove player from room
            let! leaveRoomError =
                match playerConn.Room with
                | None -> None |> Task.singleton
                | Some _ ->
                    hub.LeaveRoom()
                    |> Task.map (function
                        | Ok _ -> None
                        | Error _ -> Some "Failed to remove player from it's room")

            // Remove connection attempts
            let removeConnAttemptsError =
                playerConn.ConnAttemptIds
                |> List.choose (fun connAttemptId ->
                    match connAttemptId |> connAttemptStore.Get with
                    | None -> None
                    | Some _ ->
                        match connAttemptId |> connAttemptStore.Remove with
                        | true -> None
                        | false -> Some connAttemptId
                )
                |> function
                    | [] -> None
                    | failedConnAttempts ->
                        failedConnAttempts
                        |> sprintf "Failed to remove connection attempts: %A"
                        |> Some

            // Remove player connection
            let removePlayerConnectionError =
                playerConnectionStore.Remove playerConnId
                |> Result.requireTrue "Failed to remove player connection"
                |> function
                    | Ok _ -> None
                    | Error error -> Some error

            return!
                match leaveRoomError, removeConnAttemptsError, removePlayerConnectionError with
                | None, None, None -> Ok ()
                | _ ->
                    sprintf
                        "\nLeave room error: %s\nRemove connection attempt error: %s\nRemove player connection error: %s"
                        (leaveRoomError |> Option.defaultValue "None")
                        (removeConnAttemptsError |> Option.defaultValue "None")
                        (removePlayerConnectionError |> Option.defaultValue "None")
                    |> Error
        }
        |> TaskResult.mapError (printfn "Error occurred while deregistering player: %s")
        |> Task.map ignore
        :> Task

    // --- WebRTC Signaling ---
    member hub.StartConnectionAttempt (sdpDescription: SdpDescription) =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnId
                |> playerConnectionStore.Get
                |> Result.ofOption StartConnectionAttemptError.PlayerConnectionNotFound

            // Create connection attempt
            let connAttempt =
                { Id = ConnAttemptId.create ()
                  InitiatorConnectionId = playerConnId
                  SdpDescription = sdpDescription
                  Answerer = None }

            do! connAttemptStore.Add
                    connAttempt.Id
                    connAttempt
                |> Result.requireTrue StartConnectionAttemptError.FailedToCreateConnAttempt

            // Update player connection
            let newPlayerConn =
                { playerConn with ConnAttemptIds = connAttempt.Id :: playerConn.ConnAttemptIds }

            do! playerConnectionStore.Update
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue StartConnectionAttemptError.FailedToUpdatePlayerConnection

            return connAttempt.Id
        }

    /// Returns the offer sdp desc and allow to send the answer
    member hub.JoinConnectionAttempt (connAttemptId: ConnAttemptId) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            do! connId
                |> playerConnectionStore.Get
                |> Result.ofOption JoinConnectionAttemptError.PlayerConnectionNotFound
                |> Result.ignore

            // Retrieve connection attempt
            let! connAttempt =
                connAttemptId
                |> connAttemptStore.Get
                |> Result.ofOption JoinConnectionAttemptError.OfferNotFound

            // Check if connection attempt has not been answered
            do! connAttempt.Answerer
                |> Result.requireNone JoinConnectionAttemptError.OfferAlreadyAnswered

            // Check if the answerer is not the initiator
            do! connAttempt.InitiatorConnectionId <> connId
                |> Result.requireTrue JoinConnectionAttemptError.InitiatorCannotJoin

            // Update connection attempt
            do! connAttemptStore.Update
                    connAttemptId
                    connAttempt
                    { connAttempt with Answerer = Some connId }
                |> Result.requireTrue JoinConnectionAttemptError.FailedToUpdateOffer

            return connAttempt.SdpDescription
        }

    member hub.SendAnswer (connAttemptId: ConnAttemptId) (sdpDescription: SdpDescription) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            do! connId
                |> playerConnectionStore.Get
                |> Result.ofOption SendAnswerError.PlayerConnectionNotFound
                |> Result.ignore

            // Retrieve connection attempt
            let! connAttempt =
                connAttemptId
                |> connAttemptStore.Get
                |> Result.ofOption SendAnswerError.OfferNotFound

            // Get answerer
            let! answerer = connAttempt.Answerer |> Result.ofOption SendAnswerError.NotAnswerer
            // Check if the client is the answerer
            do! connId = answerer |> Result.requireTrue SendAnswerError.NotAnswerer

            // Send answer to initiator
            try
                do! hub.Clients.Client(connAttempt.InitiatorConnectionId |> ConnId.raw).SdpAnswerReceived connAttemptId sdpDescription
            with _ ->
                return! Error SendAnswerError.FailedToTransmitAnswer
        }

    member hub.SendIceCandidate (connAttemptId: ConnAttemptId) (iceCandidate: IceCandidate) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            do! connId
                |> playerConnectionStore.Get
                |> Result.ofOption SendIceCandidateError.PlayerConnectionNotFound
                |> Result.ignore

            let! connAttempt =
                connAttemptId
                |> connAttemptStore.Get
                |> Result.ofOption SendIceCandidateError.OfferNotFound

            let! answerer =
                connAttempt.Answerer
                |> Result.ofOption SendIceCandidateError.NotAnswerer

            // Check if the client is in the connection attempt
            do! (connId = connAttempt.InitiatorConnectionId || connId = answerer)
                |> Result.requireTrue SendIceCandidateError.NotParticipant

            // Determine the target connection id
            let targetConnId =
                match connId = answerer with
                | true -> connAttempt.InitiatorConnectionId
                | false -> answerer

            // Send ice candidate to other peer
            try
                do! hub.Clients.Client(targetConnId |> ConnId.raw).IceCandidateReceived connAttemptId iceCandidate
            with _ ->
                return! Error SendIceCandidateError.FailedToTransmitCandidate
        }

    member hub.EndConnectionAttempt (connAttemptId: ConnAttemptId) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            do! connId
                |> playerConnectionStore.Get
                |> Result.ofOption EndConnectionAttemptError.PlayerConnectionNotFound
                |> Result.ignore

            let! connAttempt =
                connAttemptId
                |> connAttemptStore.Get
                |> Result.ofOption EndConnectionAttemptError.OfferNotFound

            // Check if the client is in the connection attempt
            match connId = connAttempt.InitiatorConnectionId with
            | true -> ()
            | false ->
                do! connAttempt.Answerer
                    |> Result.ofOption EndConnectionAttemptError.NotParticipant
                    |> Result.bind ((=) connId >> Result.requireTrue EndConnectionAttemptError.NotParticipant)

            // Remove connection attempt
            do! connAttemptStore.Remove connAttemptId
                |> Result.requireTrue EndConnectionAttemptError.FailedToRemoveOffer
        }

    // --- Rooms ---
    member hub.CreateRoom() =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnId
                |> playerConnectionStore.Get
                |> Result.ofOption CreateRoomError.PlayerConnectionNotFound

            // Check if player is already in a room
            do! playerConn.Room |> Result.requireNone CreateRoomError.PlayerAlreadyInARoom

            // Create room
            let room =
                { Id = RoomId.create ()
                  Initiator = playerConnId
                  Players = [ KeyValuePair(playerConnId, 1) ] |> Dictionary // Host peer id should always be 1
                  Connections = HashSet()
                  ConnectionsInProgress = HashSet()
                  Semaphore = new SemaphoreSlim(1, 1) }

            do! roomStore.Add
                    room.Id
                    room
                |> Result.requireTrue CreateRoomError.FailedToRegisterRoom

            // Update player connection
            let newPlayerConn = { playerConn with Room = Some room.Id }

            do! playerConnectionStore.Update
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue CreateRoomError.FailedToUpdatePlayerConnection

            return room.Id
        }

    member hub.JoinRoom (roomId: RoomId) =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnId
                |> playerConnectionStore.Get
                |> Result.ofOption JoinRoomError.PlayerConnectionNotFound

            // Check if player is already in a room
            do! playerConn.Room
                |> Result.requireNone JoinRoomError.PlayerAlreadyInARoom

            // Update room
            let! newPeerId = lock roomStore (fun () -> taskResult {
                let! room =
                    roomId
                    |> roomStore.Get
                    |> Result.ofOption JoinRoomError.RoomNotFound

                // Update room
                let newPeerId =
                    room.Players
                    |> Seq.maxBy _.Value // Max by peerId
                    |> _.Value
                    |> (+) 1

                room.Players.Add(playerConnId, newPeerId)

                // do! roomStore.Update
                //         roomId
                //         room
                //         newRoom
                //     |> Result.requireTrue JoinRoomError.FailedToUpdateRoom // TODO

                return newPeerId
            })

            // Update player connection
            let newPlayerConn = { playerConn with Room = Some roomId }

            do! playerConnectionStore.Update
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue JoinRoomError.FailedToUpdatePlayerConnection

            return newPeerId
        }

    member hub.ConnectToRoomPlayers() =
        taskResult {
            let playerConnectionId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnectionId
                |> playerConnectionStore.Get
                |> Result.ofOption ConnectToRoomPlayersError.PlayerConnectionNotFound

            // Find players to connect to
            let! room =
                playerConn.Room
                |> Option.bind roomStore.Get
                |> Result.ofOption ConnectToRoomPlayersError.NotInARoom

            do! room.Semaphore.WaitAsync() // Lock room

            let! requestingPeerId =
                match room.Players.TryGetValue playerConnectionId with
                | false, _ -> Error ConnectToRoomPlayersError.PlayerNotInRoomPlayers
                | true, peerId -> Ok peerId

            let playersToConnectTo =
                room.Players
                |> Seq.filter (fun kv ->
                    let playerConnectionId' = kv.Key

                    let connectionToCheck = min playerConnectionId playerConnectionId', max playerConnectionId playerConnectionId'
                    let alreadyConnected =
                        room.Connections.Contains(connectionToCheck)
                        || room.ConnectionsInProgress.Contains(connectionToCheck)

                    playerConnectionId' <> playerConnectionId && not alreadyConnected
                )
                |> Seq.toArray

            // Indicates connections are in progress
            let attemptingConnections =
                playersToConnectTo |> Seq.map (fun kv ->
                    let playerConnectionId' = kv.Key
                    min playerConnectionId playerConnectionId',
                    max playerConnectionId playerConnectionId'
                )
            attemptingConnections |> Seq.iter (room.ConnectionsInProgress.Add >> ignore)

            room.Semaphore.Release() |> ignore // Unlock room

            // Create connection attempts
            let createConnectionAttemptForPlayer (targetPeerId, targetConnId) =
                taskResult {
                    let! r =
                        targetConnId
                        |> ConnId.raw
                        |> hub.Clients.Client
                        |> _.ConnectionRequested(requestingPeerId)
                        |> _.WaitAsync(TimeSpan.FromSeconds 10.)
                        |> Task.catch
                        |> Task.map (function // Handle the case where the client didn't register a handler
                            | Choice1Of2 r -> Ok r
                            | Choice2Of2 _ -> Error targetPeerId
                        )

                    match r with
                    | null -> return! Error targetPeerId
                    | connAttemptId ->
                        let connInfo = { PeerId = targetPeerId; ConnAttemptId = connAttemptId }
                        let connection =
                            min playerConnectionId targetConnId,
                            max playerConnectionId targetConnId

                        return connInfo, connection
                }

            let! playersConnectionInfoResults =
                playersToConnectTo
                |> Seq.map (fun kv -> createConnectionAttemptForPlayer (kv.Value, kv.Key))
                |> Task.WhenAll

            let mutable playersConnInfo = []
            let mutable failed = []
            let mutable newEstablishedConnections = []

            playersConnectionInfoResults |> Array.iter (
                function
                | Ok (connInfo, connection) ->
                    playersConnInfo <- connInfo :: playersConnInfo
                    newEstablishedConnections <- connection :: newEstablishedConnections
                | Error peerId ->
                    failed <- peerId :: failed
            )

            // Update room
            do! room.Semaphore.WaitAsync()

            newEstablishedConnections |> Seq.iter (fun connection ->
                room.ConnectionsInProgress.Remove connection |> ignore
                room.Connections.Add connection |> ignore
            )

            attemptingConnections |> Seq.iter (fun connection ->
                room.ConnectionsInProgress.Remove connection |> ignore
            )

            // ConnectToRoomPlayersError.FailedToUpdateRoom // TODO: ?
            room.Semaphore.Release() |> ignore

            return { PlayersConnInfo = playersConnInfo |> List.toArray
                     FailedCreations = failed |> List.toArray }
        }

    member hub.LeaveRoom() =
        taskResult {
            let playerConnectionId = hub.Context.ConnectionId |> ConnId.parse

            // Check if a player connection exists
            let! playerConnection =
                playerConnectionId
                |> playerConnectionStore.Get
                |> Result.ofOption LeaveRoomError.PlayerConnectionNotFound

            do! lock roomStore (fun _ -> taskResult {
                // Get player's room
                let! room =
                    playerConnection.Room
                    |> Option.bind roomStore.Get
                    |> Result.ofOption LeaveRoomError.NotInARoom

                match room.Players |> Seq.length with
                | 1 -> // If the player is the last one in the room, remove the room
                    do! roomStore.Remove room.Id
                        |> Result.requireTrue LeaveRoomError.FailedToRemoveRoom

                | _ ->
                    // Remove player connections and player from room
                    room.Connections.RemoveWhere(fun (p1, p2) ->
                        match playerConnectionId with
                        | Equals p1
                        | Equals p2 -> true
                        | _ -> false
                    ) |> ignore

                    do! room.Players.Remove(playerConnectionId)
                        |> Result.requireTrue LeaveRoomError.FailedToUpdateRoom
            })

            // Update player connection
            do! playerConnectionStore.Update
                    playerConnectionId
                    playerConnection
                    { playerConnection with Room = None }
                    |> Result.requireTrue LeaveRoomError.FailedToUpdatePlayerConnection
        }
