namespace Behide.OnlineServices.Hubs.Signaling

open System.Threading
open System.Threading.Tasks

open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Signaling.Errors



type IPlayerConnsStore = Store.IStore<ConnId, PlayerConnection>
type PlayerConnsStore = Store.Store<ConnId, PlayerConnection>

type IConnAttemptStore = Store.IStore<ConnAttemptId, ConnAttempt>
type ConnAttemptStore = Store.Store<ConnAttemptId, ConnAttempt>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>


type SignalingHub(connAttemptStore: IConnAttemptStore, roomStore: IRoomStore, playerConnsStore: IPlayerConnsStore) =
    inherit Hub<ISignalingClient>()
    // Should interface ISignalingHub, but it make the methods not callable from the client

    // --- Player Connection Management ---
    override hub.OnConnectedAsync() =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let playerConn =
                { ConnectionId = playerConnId
                  ConnAttemptIds = []
                  Room = None }

            do! playerConnsStore.Add
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
                |> playerConnsStore.Get
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
                playerConnsStore.Remove playerConnId
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
                |> playerConnsStore.Get
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

            do! playerConnsStore.Update
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue StartConnectionAttemptError.FailedToUpdatePlayerConnection

            return connAttempt.Id
        }

    member hub.JoinConnectionAttempt (connAttemptId: ConnAttemptId) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            let! _playerConn =
                connId
                |> playerConnsStore.Get
                |> Result.ofOption JoinConnectionAttemptError.PlayerConnectionNotFound

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

            let! _playerConn = // Check if client has a player connection
                connId
                |> playerConnsStore.Get
                |> Result.ofOption SendAnswerError.PlayerConnectionNotFound

            // Retrieve connection attempt
            let! connAttempt =
                connAttemptId
                |> connAttemptStore.Get
                |> Result.ofOption SendAnswerError.OfferNotFound

            // Get answerer
            let! answerer =
                connAttempt.Answerer
                |> Result.ofOption SendAnswerError.NotAnswerer

            // Check if the client is the answerer
            do! connId = answerer
                |> Result.requireTrue SendAnswerError.NotAnswerer

            // Send answer to initiator
            do! hub.Clients.Client(connAttempt.InitiatorConnectionId |> ConnId.raw).SdpAnswerReceived connAttemptId sdpDescription
        }

    member hub.SendIceCandidate (connAttemptId: ConnAttemptId) (iceCandidate: IceCandidate) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            let! _playerConn =
                connId
                |> playerConnsStore.Get
                |> Result.ofOption SendIceCandidateError.PlayerConnectionNotFound

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
            do! hub.Clients.Client(targetConnId |> ConnId.raw).IceCandidateReceived connAttemptId iceCandidate
        }

    member hub.EndConnectionAttempt (connAttemptId: ConnAttemptId) =
        taskResult {
            let connId = hub.Context.ConnectionId |> ConnId.parse

            // Check if client has a player connection
            let! _playerConn =
                connId
                |> playerConnsStore.Get
                |> Result.ofOption EndConnectionAttemptError.PlayerConnectionNotFound

            let! connAttempt =
                connAttemptId
                |> connAttemptStore.Get
                |> Result.ofOption EndConnectionAttemptError.OfferNotFound

            let! answerer =
                connAttempt.Answerer
                |> Result.ofOption EndConnectionAttemptError.AnswererNotFound

            // Check if the client is in the connection attempt
            do! (connId = connAttempt.InitiatorConnectionId || connId = answerer)
                |> Result.requireTrue EndConnectionAttemptError.NotParticipant

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
                |> playerConnsStore.Get
                |> Result.ofOption CreateRoomError.PlayerConnectionNotFound

            // Check if player is already in a room
            do! playerConn.Room
                |> Result.requireNone CreateRoomError.PlayerAlreadyInARoom


            // Create room
            let room =
                { Id = RoomId.create ()
                  Initiator = playerConnId
                  Players = [ 1, playerConnId ] // Host peer id should always be 1
                  Connections = [] }

            do! roomStore.Add
                    room.Id
                    room
                |> Result.requireTrue CreateRoomError.FailedToRegisterRoom

            // Update player connection
            let newPlayerConn =
                { playerConn with Room = Some room.Id }

            do! playerConnsStore.Update
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
                |> playerConnsStore.Get
                |> Result.ofOption JoinRoomError.PlayerConnectionNotFound

            // Check if player is already in a room
            do! playerConn.Room
                |> Result.requireNone JoinRoomError.PlayerAlreadyInARoom

            let! newPlayerId = lock roomStore (fun _ -> taskResult {
                let! room =
                    roomId
                    |> roomStore.Get
                    |> Result.ofOption JoinRoomError.RoomNotFound

                // Update room
                let newPlayerId = (room.Players |> List.length) + 1
                let newRoom =
                    { room with Players = (newPlayerId, playerConnId) :: room.Players }

                do! roomStore.Update
                        roomId
                        room
                        newRoom
                    |> Result.requireTrue JoinRoomError.FailedToUpdateRoom

                return newPlayerId
            })

            // Update player connection
            let newPlayerConn =
                { playerConn with Room = Some roomId }

            do! playerConnsStore.Update
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue JoinRoomError.FailedToUpdatePlayerConnection

            return newPlayerId
        }

    member hub.ConnectToRoomPlayers() =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnId
                |> playerConnsStore.Get
                |> Result.ofOption ConnectToRoomPlayersError.PlayerConnectionNotFound

            return! lock roomStore (fun _ -> taskResult {
                let! room =
                    playerConn.Room
                    |> Option.bind roomStore.Get
                    |> Result.ofOption ConnectToRoomPlayersError.NotInARoom

                let! requestingPlayerId =
                    room.Players
                    |> List.tryFind (snd >> (=) playerConn.ConnectionId)
                    |> Result.ofOption ConnectToRoomPlayersError.PlayerNotInRoomPlayers
                    |> Result.map fst


                /// Create connection attempt on the requested client and return the ConnAttemptId with a "connection" to add in the room
                let createConnAttemptForPlayer (peerId, connId) =
                    task {
                        let cts = new CancellationTokenSource(5000)
                        try
                            cts.Token.ThrowIfCancellationRequested()
                            let! r = hub.Clients.Client(connId |> ConnId.raw).CreateConnAttempt(requestingPlayerId)

                            match r with
                            | None -> return Error peerId
                            | Some connAttemptId ->
                                let connInfo = { PeerId = peerId; ConnAttemptId = connAttemptId }
                                let connection = playerConnId, connId

                                return Ok (connInfo, connection)

                        with // Handle the case where the client didn't registered an handler
                        | _ -> return Error peerId
                    }

                let alreadyConnectedPlayers =
                    room.Connections
                    |> List.choose (fun (p1, p2) ->
                        match playerConnId with
                        | Equals p1 -> Some p2
                        | Equals p2 -> Some p1
                        | _ -> None
                    )

                let! playersConnectionInfoResults =
                    room.Players
                    |> List.choose (fun (connInfo, connId) ->
                        let isRequestingPlayer = connId <> playerConnId
                        let alreadyConnected = alreadyConnectedPlayers |> List.contains connId |> not

                        // Don't connect the player to himself
                        // And don't reconnect player
                        match isRequestingPlayer && alreadyConnected with
                        | false -> None
                        | true -> (connInfo, connId) |> createConnAttemptForPlayer |> Some
                    )
                    |> Task.WhenAll

                let mutable playersConnInfo = []
                let mutable newConnections = []
                let mutable failed = []

                playersConnectionInfoResults |> Array.iter (
                    function
                    | Ok (connInfo, connection) ->
                        playersConnInfo <- connInfo :: playersConnInfo
                        newConnections <- connection :: newConnections
                    | Error peerId ->
                        failed <- peerId :: failed
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
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            // Check if a player connection exists
            let! playerConn =
                playerConnId
                |> playerConnsStore.Get
                |> Result.ofOption LeaveRoomError.PlayerConnectionNotFound

            do! lock roomStore (fun _ -> taskResult {
                // Get player's room
                let! room =
                    playerConn.Room
                    |> Option.bind roomStore.Get
                    |> Result.ofOption LeaveRoomError.NotInARoom

                // Remove player connections and player from room
                let newConnections =
                    room.Connections
                    |> List.filter (fun (p1, p2) ->
                        match playerConnId with
                        | Equals p1 -> false
                        | Equals p2 -> false
                        | _ -> true
                    )

                let newRoom =
                    { room with
                        Players = room.Players |> List.filter (snd >> (<>) playerConnId)
                        Connections = newConnections}

                do! roomStore.Update room.Id room newRoom
                    |> Result.requireTrue LeaveRoomError.FailedToUpdateRoom
            })

            // Update player connection
            do! playerConnsStore.Update
                    playerConnId
                    playerConn
                    { playerConn with Room = None }
                    |> Result.requireTrue LeaveRoomError.FailedToUpdatePlayerConnection
        }
