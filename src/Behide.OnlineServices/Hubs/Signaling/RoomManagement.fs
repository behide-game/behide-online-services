module Behide.OnlineServices.Hubs.Signaling.RoomManagement

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Signaling.Errors
type Hub = Microsoft.AspNetCore.SignalR.Hub<ISignalingClient>

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

open FsToolkit.ErrorHandling

let createRoom (hub: Hub) (playerStore: IPlayerStore) (_connectionAttemptStore: IConnectionAttemptStore) (roomStore: IRoomStore) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        let! player =
            playerId
            |> playerStore.Get
            |> Result.ofOption CreateRoomError.PlayerNotFound

        // Check if player is already in a room
        do! player.Room |> Result.requireNone CreateRoomError.PlayerAlreadyInARoom

        // Create room
        let room =
            { Id = RoomId.create ()
              Initiator = playerId
              Players = [ KeyValuePair(playerId, 1) ] |> Dictionary // Host peer id should always be 1
              Connections = HashSet()
              ConnectionsInProgress = HashSet()
              Semaphore = new SemaphoreSlim(1, 1) }

        do! roomStore.Add
                room.Id
                room
            |> Result.requireTrue CreateRoomError.FailedToRegisterRoom

        // Update player
        let newPlayer = { player with Room = Some {| Id = room.Id; PeerId = 1 |} }

        do! playerStore.Update playerId player newPlayer
            |> Result.requireTrue CreateRoomError.FailedToUpdatePlayer

        return room.Id
    }

let joinRoom (hub: Hub) (playerStore: IPlayerStore) (_connectionAttemptStore: IConnectionAttemptStore) (roomStore: IRoomStore) (roomId: RoomId) = // TODO: Add concurrency test
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        let! player =
            playerId
            |> playerStore.Get
            |> Result.ofOption JoinRoomError.PlayerNotFound

        // Check if player is already in a room
        do! player.Room |> Result.requireNone JoinRoomError.PlayerAlreadyInARoom

        // Update room
        let! room =
            roomId
            |> roomStore.Get
            |> Result.ofOption JoinRoomError.RoomNotFound
        do! room.Semaphore.WaitAsync() // Lock to prevent several players having the same peerId
        let newPeerId =
            room.Players
            |> Seq.maxBy _.Value // Max by peerId
            |> _.Value
            |> (+) 1

        room.Players.Add(playerId, newPeerId)
        room.Semaphore.Release() |> ignore

        // Update player connection
        let newPlayerConn = { player with Room = Some {| Id = roomId; PeerId = newPeerId |} }

        do! playerStore.Update
                playerId
                player
                newPlayerConn
            |> Result.requireTrue JoinRoomError.FailedToUpdatePlayer

        return newPeerId
    }


let private findPlayersToConnectTo (requestingPlayer: Player) (room: Room) =
    room.Players
    |> Array.ofSeq
    |> Array.filter (fun kv ->
        let playerId = kv.Key

        let connectionToCheck = Pair.create requestingPlayer.Id playerId
        let alreadyConnected =
            room.Connections.Contains(connectionToCheck)
            || room.ConnectionsInProgress.Contains(connectionToCheck)

        playerId <> requestingPlayer.Id && not alreadyConnected
    )

let setInProgressConnections player (playersToConnectTo: KeyValuePair<PlayerId,int> array) room =
    playersToConnectTo |> Array.map (fun kv ->
        let connection = Pair.create player.Id kv.Key
        room.ConnectionsInProgress.Add connection |> ignore
        connection
    )

let requestConnectionForPlayer (hub: Hub) player requestingPeerId targetPeerId targetPlayerId =
    taskResult {
        let! r =
            targetPlayerId
            |> PlayerId.raw
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
            return { PeerId = targetPeerId; ConnAttemptId = connAttemptId },
                   Pair.create player.Id targetPlayerId
    }

let connectToRoomPlayers (hub: Hub) (playerStore: IPlayerStore) (_connectionAttemptStore: IConnectionAttemptStore) (roomStore: IRoomStore) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        let! player =
            playerId
            |> playerStore.Get
            |> Result.ofOption ConnectToRoomPlayersError.PlayerNotFound

        let! room, requestingPeerId =
            player.Room
            |> Option.bind (fun roomInfo ->
                roomInfo.Id
                |> roomStore.Get
                |> Option.map (fun roomId -> roomId, roomInfo.PeerId)
            )
            |> Result.ofOption ConnectToRoomPlayersError.NotInARoom

        // Ensure player is in room players
        do! room.Players.ContainsKey player.Id
            |> Result.requireTrue ConnectToRoomPlayersError.PlayerNotInRoomPlayers

        do! room.Semaphore.WaitAsync() // Lock room
        let playersToConnectTo = findPlayersToConnectTo player room
        let inProgressConnections = setInProgressConnections player playersToConnectTo room
        room.Semaphore.Release() |> ignore // Unlock room

        // Create connection attempts
        let requestConnectionForPlayer = requestConnectionForPlayer hub player requestingPeerId
        let! playersConnectionInfo =
            playersToConnectTo
            |> Array.map (fun kv -> requestConnectionForPlayer kv.Value kv.Key)
            |> Task.WhenAll

        // Build return value and update room connections
        do! room.Semaphore.WaitAsync()
        let playersConnectionInfo, failed =
            playersConnectionInfo |> Array.fold
                (fun (playersConnInfo, failed) playerConnectionInfo ->
                    match playerConnectionInfo with
                    | Ok (connInfo, connection) ->

                        // Convert in progress connections in connections
                        room.ConnectionsInProgress.Remove connection |> ignore
                        room.Connections.Add connection |> ignore

                        connInfo :: playersConnInfo, failed
                    | Error peerId -> playersConnInfo, peerId :: failed)
                (List.empty, List.empty)

        // Remove inProgressConnections (in case a connection attempt failed)
        inProgressConnections |> Array.iter (fun connection ->
            room.ConnectionsInProgress.Remove connection |> ignore
        )

        room.Semaphore.Release() |> ignore

        return { PlayersConnectionInfo = playersConnectionInfo |> List.toArray
                 FailedCreations = failed |> List.toArray }
    }

let leaveRoom (hub: Hub) (playerStore: IPlayerStore) (_connectionAttemptStore: IConnectionAttemptStore) (roomStore: IRoomStore) = // TODO: Add tests (ex: Leave while another is connecting)
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        // Check if a player connection exists
        let! player =
            playerId
            |> playerStore.Get
            |> Result.ofOption LeaveRoomError.PlayerNotFound

        let! room =
            player.Room
            |> Option.bind (_.Id >> roomStore.Get)
            |> Result.ofOption LeaveRoomError.NotInARoom
        do! room.Semaphore.WaitAsync()
        match room.Players.Count with
        | 1 -> // If the player is the last one in the room, remove the room
            do! roomStore.Remove room.Id
                |> Result.requireTrue LeaveRoomError.FailedToRemoveRoom

        | _ ->
            // Remove player connections and player from room
            room.Connections.RemoveWhere(fun connection ->
                playerId |> Pair.isInPair connection
            ) |> ignore

            do! room.Players.Remove(playerId)
                |> Result.requireTrue LeaveRoomError.FailedToUpdateRoom
        room.Semaphore.Release() |> ignore

        // Update player connection
        do! playerStore.Update
                playerId
                player
                { player with Room = None }
                |> Result.requireTrue LeaveRoomError.FailedToUpdatePlayer
    }
