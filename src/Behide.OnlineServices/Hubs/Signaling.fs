namespace Behide.OnlineServices.Hubs.Signaling

open System
open System.Collections.Generic
open System.Threading.Tasks

open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Signaling.Errors


/// Store of player states in the signaling process
type PlayerInfoStore = Store.Store<ConnId, PlayerInfo>
type IPlayerInfoStore = Store.IStore<ConnId, PlayerInfo>

type IConnAttemptStore = Store.IStore<ConnAttemptId, ConnAttempt>
type ConnAttemptStore = Store.Store<ConnAttemptId, ConnAttempt>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>


type SignalingHub(connAttemptStore: IConnAttemptStore, roomStore: IRoomStore, playerInfoStore: IPlayerInfoStore) =
    inherit Hub<ISignalingClient>()
    // Should interface ISignalingHub, but it makes the methods not callable from the client

    // --- Player Connection Management ---
    override hub.OnConnectedAsync() =
        taskResult {
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            let playerInfo =
                { ConnectionId = playerInfoId
                  ConnAttemptIds = []
                  Room = None }

            do! playerInfoStore.Add
                    playerInfoId
                    playerInfo
                |> Result.requireTrue "Failed to add player connection"
        }
        |> TaskResult.mapError (printfn "Error occurred while registering player: %s")
        |> Task.map ignore
        :> Task

    override hub.OnDisconnectedAsync _exn =
        taskResult {
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            let! playerInfo =
                playerInfoId
                |> playerInfoStore.Get
                |> Result.ofOption "Player connection not found"

            // Remove player from room
            let! leaveRoomError =
                match playerInfo.Room with
                | None -> None |> Task.singleton
                | Some _ ->
                    hub.LeaveRoom()
                    |> Task.map (function
                        | Ok _ -> None
                        | Error _ -> Some "Failed to remove player from it's room")

            // Remove connection attempts
            let removeConnAttemptsError =
                playerInfo.ConnAttemptIds
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
            let removePlayerInfoError =
                playerInfoStore.Remove playerInfoId
                |> Result.requireTrue "Failed to remove player connection"
                |> function
                    | Ok _ -> None
                    | Error error -> Some error

            return!
                match leaveRoomError, removeConnAttemptsError, removePlayerInfoError with
                | None, None, None -> Ok ()
                | _ ->
                    sprintf
                        "\nLeave room error: %s\nRemove connection attempt error: %s\nRemove player connection error: %s"
                        (leaveRoomError |> Option.defaultValue "None")
                        (removeConnAttemptsError |> Option.defaultValue "None")
                        (removePlayerInfoError |> Option.defaultValue "None")
                    |> Error
        }
        |> TaskResult.mapError (printfn "Error occurred while deregistering player: %s")
        |> Task.map ignore
        :> Task

    // --- WebRTC Signaling ---
    member hub.StartConnectionAttempt (sdpDescription: SdpDescription) =
        taskResult {
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            let! playerInfo =
                playerInfoId
                |> playerInfoStore.Get
                |> Result.ofOption StartConnectionAttemptError.PlayerInfoNotFound

            // Create connection attempt
            let connAttempt =
                { Id = ConnAttemptId.create ()
                  InitiatorConnectionId = playerInfoId
                  SdpDescription = sdpDescription
                  Answerer = None }

            do! connAttemptStore.Add
                    connAttempt.Id
                    connAttempt
                |> Result.requireTrue StartConnectionAttemptError.FailedToCreateConnAttempt

            // Update player connection
            let newPlayerInfo =
                { playerInfo with ConnAttemptIds = connAttempt.Id :: playerInfo.ConnAttemptIds }

            do! playerInfoStore.Update
                    playerInfoId
                    playerInfo
                    newPlayerInfo
                |> Result.requireTrue StartConnectionAttemptError.FailedToUpdatePlayerInfo

            return connAttempt.Id
        }

    /// Returns the offer sdp desc and allow to send the answer
    member hub.JoinConnectionAttempt (connAttemptId: ConnAttemptId) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            do! connId
                |> playerInfoStore.Get
                |> Result.ofOption JoinConnectionAttemptError.PlayerInfoNotFound
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
                |> playerInfoStore.Get
                |> Result.ofOption SendAnswerError.PlayerInfoNotFound
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
            let! _playerInfo =
                connId
                |> playerInfoStore.Get
                |> Result.ofOption SendIceCandidateError.PlayerInfoNotFound

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
            let! _playerInfo =
                connId
                |> playerInfoStore.Get
                |> Result.ofOption EndConnectionAttemptError.PlayerInfoNotFound

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
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            let! playerInfo =
                playerInfoId
                |> playerInfoStore.Get
                |> Result.ofOption CreateRoomError.PlayerInfoNotFound

            // Check if player is already in a room
            do! playerInfo.Room
                |> Result.requireNone CreateRoomError.PlayerAlreadyInARoom


            // Create room
            let room =
                { Id = RoomId.create ()
                  Initiator = playerInfoId
                  Players = [ 1, playerInfoId ] // Host peer id should always be 1
                  Connections = [] }

            do! roomStore.Add
                    room.Id
                    room
                |> Result.requireTrue CreateRoomError.FailedToRegisterRoom

            // Update player connection
            let newPlayerInfo =
                { playerInfo with Room = Some room.Id }

            do! playerInfoStore.Update
                    playerInfoId
                    playerInfo
                    newPlayerInfo
                |> Result.requireTrue CreateRoomError.FailedToUpdatePlayerInfo

            return room.Id
        }

    member hub.JoinRoom (roomId: RoomId) =
        taskResult {
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            let! playerInfo =
                playerInfoId
                |> playerInfoStore.Get
                |> Result.ofOption JoinRoomError.PlayerInfoNotFound

            // Check if player is already in a room
            do! playerInfo.Room
                |> Result.requireNone JoinRoomError.PlayerAlreadyInARoom

            // Update room
            let! newPeerId, playerCount = lock roomStore (fun () -> taskResult {
                let! room =
                    roomId
                    |> roomStore.Get
                    |> Result.ofOption JoinRoomError.RoomNotFound

                // Update room
                let newPeerId =
                    room.Players
                    |> List.maxBy fst
                    |> fst
                    |> (+) 1

                let newRoom =
                    { room with Players = (newPeerId, playerInfoId) :: room.Players }

                do! roomStore.Update
                        roomId
                        room
                        newRoom
                    |> Result.requireTrue JoinRoomError.FailedToUpdateRoom

                return newPeerId, newRoom.Players.Length
            })

            // Update player connection
            let newPlayerInfo = { playerInfo with Room = Some roomId }

            do! playerInfoStore.Update
                    playerInfoId
                    playerInfo
                    newPlayerInfo
                |> Result.requireTrue JoinRoomError.FailedToUpdatePlayerInfo

            return newPeerId, playerCount // TODO: Add testes for player count
        }

    member hub.ConnectToRoomPlayers() =
        taskResult {
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            let! playerInfo =
                playerInfoId
                |> playerInfoStore.Get
                |> Result.ofOption ConnectToRoomPlayersError.PlayerInfoNotFound

            return! lock roomStore (fun _ -> taskResult {
                let! room =
                    playerInfo.Room
                    |> Option.bind roomStore.Get
                    |> Result.ofOption ConnectToRoomPlayersError.NotInARoom

                let! requestingPeerId =
                    room.Players
                    |> List.tryFind (snd >> (=) playerInfo.ConnectionId)
                    |> Result.ofOption ConnectToRoomPlayersError.PlayerNotInRoomPlayers
                    |> Result.map fst

                // Find players to connect to
                let alreadyConnectedPlayers =
                    room.Connections
                    |> List.choose (fun (p1, p2) ->
                        match playerInfoId with
                        | Equals p1 -> Some p2
                        | Equals p2 -> Some p1
                        | _ -> None
                    )
                    |> HashSet

                let playersToConnectTo =
                    room.Players |> List.filter (fun (_, connId) ->
                        let isRequestingPlayer = connId = playerInfoId
                        let alreadyConnected = alreadyConnectedPlayers.Contains(connId)

                        // Don't connect the player to himself and don't reconnect player
                        not (isRequestingPlayer || alreadyConnected)
                    )

                // Create connection attempts
                let mutable playersConnInfo = List.empty
                let mutable failed = List.empty
                let mutable newConnections = List.empty // TODO: Make another step to indicate the connection is established

                /// <summary>Create connection attempt on the requested client</summary>
                let createConnAttemptForPlayer (targetPeerId, targetConnId) =
                    task {
                        let! r =
                            targetConnId
                            |> ConnId.raw
                            |> hub.Clients.Client
                            |> _.ConnectionRequested(requestingPeerId)
                            |> Task.catch // Handle the case where the client didn't register a handler
                            |> Task.map (function
                                | Choice1Of2 e -> e
                                | Choice2Of2 _ -> null
                            )

                        match r with
                        | null -> failed <- targetPeerId :: failed
                        | connAttemptId ->
                            playersConnInfo <- { PeerId = targetPeerId; ConnAttemptId = connAttemptId } :: playersConnInfo
                            newConnections <- (playerInfoId, targetConnId) :: newConnections
                    }

                do! Parallel.ForEachAsync(
                    playersToConnectTo,
                    Func<_, _, _>(fun e _ -> createConnAttemptForPlayer e |> ValueTask)
                )

                // Update room
                do! roomStore.Update
                        room.Id
                        room
                        { room with Connections = newConnections @ room.Connections }
                    |> Result.requireTrue ConnectToRoomPlayersError.FailedToUpdateRoom

                return { PlayersConnInfo = playersConnInfo |> List.toArray
                         FailedCreations = failed |> List.toArray }
            })
        }

    member hub.LeaveRoom() =
        taskResult {
            let playerInfoId = hub.Context.ConnectionId |> ConnId.parse

            // Check if a player connection exists
            let! playerInfo =
                playerInfoId
                |> playerInfoStore.Get
                |> Result.ofOption LeaveRoomError.PlayerInfoNotFound

            do! lock roomStore (fun _ -> taskResult {
                // Get player's room
                let! room =
                    playerInfo.Room
                    |> Option.bind roomStore.Get
                    |> Result.ofOption LeaveRoomError.NotInARoom

                match room.Players |> List.length with
                | 1 -> // If the player is the last one in the room, remove the room
                    do! roomStore.Remove room.Id
                        |> Result.requireTrue LeaveRoomError.FailedToRemoveRoom

                | _ ->
                    // Remove player connections and player from room
                    let newConnections =
                        room.Connections
                        |> List.filter (fun (p1, p2) ->
                            match playerInfoId with
                            | Equals p1 -> false
                            | Equals p2 -> false
                            | _ -> true
                        )

                    let newRoom =
                        { room with
                            Players = room.Players |> List.filter (snd >> (<>) playerInfoId)
                            Connections = newConnections}

                    do! roomStore.Update room.Id room newRoom
                        |> Result.requireTrue LeaveRoomError.FailedToUpdateRoom
            })

            // Update player connection
            do! playerInfoStore.Update
                    playerInfoId
                    playerInfo
                    { playerInfo with Room = None }
                    |> Result.requireTrue LeaveRoomError.FailedToUpdatePlayerInfo
        }
