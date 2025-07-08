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

let createRoom (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (_connAttemptStore: IConnAttemptStore) (roomStore: IRoomStore) =
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

let joinRoom (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (_connAttemptStore: IConnAttemptStore) (roomStore: IRoomStore) (roomId: RoomId) =
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


let private findPlayersToConnectTo (playerConnection: PlayerConnection) (room: Room) =
    room.Players
    |> Array.ofSeq
    |> Array.filter (fun kv ->
        let playerConnectionId = kv.Key

        let connectionToCheck = Connection.create playerConnection.Id playerConnectionId
        let alreadyConnected =
            room.Connections.Contains(connectionToCheck)
            || room.ConnectionsInProgress.Contains(connectionToCheck)

        playerConnectionId <> playerConnection.Id && not alreadyConnected
    )

let setInProgressConnections playerConnection (playersToConnectTo: KeyValuePair<ConnId,int> array) room =
    playersToConnectTo |> Array.map (fun kv ->
        let connection = Connection.create playerConnection.Id kv.Key
        room.ConnectionsInProgress.Add connection |> ignore
        connection
    )

let requestConnectionForPlayer (hub: Hub) playerConnection requestingPeerId (targetPeerId, targetConnId) =
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
            return { PeerId = targetPeerId; ConnAttemptId = connAttemptId },
                   Connection.create playerConnection.Id targetConnId
    }

let connectToRoomPlayers (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (_connAttemptStore: IConnAttemptStore) (roomStore: IRoomStore) =
    taskResult {
        let playerConnectionId = hub.Context.ConnectionId |> ConnId.parse

        let! playerConnection =
            playerConnectionId
            |> playerConnectionStore.Get
            |> Result.ofOption ConnectToRoomPlayersError.PlayerConnectionNotFound

        let! room =
            playerConnection.Room
            |> Option.bind roomStore.Get
            |> Result.ofOption ConnectToRoomPlayersError.NotInARoom

        do! room.Semaphore.WaitAsync() // Lock room
        let! requestingPeerId =
            match room.Players.TryGetValue playerConnection.Id with
            | false, _ -> Error ConnectToRoomPlayersError.PlayerNotInRoomPlayers
            | true, peerId -> Ok peerId

        let playersToConnectTo = findPlayersToConnectTo playerConnection room
        let inProgressConnections = setInProgressConnections playerConnection playersToConnectTo room
        room.Semaphore.Release() |> ignore // Unlock room

        // Create connection attempts
        let requestConnectionForPlayer = requestConnectionForPlayer hub playerConnection requestingPeerId
        let! playersConnectionInfo =
            playersToConnectTo
            |> Array.map (fun kv -> requestConnectionForPlayer (kv.Value, kv.Key))
            |> Task.WhenAll

        // Build return value and update room connections
        do! room.Semaphore.WaitAsync()
        let playersConnInfo, failed =
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

        return { PlayersConnInfo = playersConnInfo |> List.toArray
                 FailedCreations = failed |> List.toArray }
    }

let leaveRoom (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (_connAttemptStore: IConnAttemptStore) (roomStore: IRoomStore) =
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
                room.Connections.RemoveWhere(fun connection ->
                    playerConnectionId |> Connection.playerIsPartOf connection
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
