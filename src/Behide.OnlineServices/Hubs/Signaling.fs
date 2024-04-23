namespace Behide.OnlineServices.Hubs.Signaling

open System.Threading.Tasks
open Behide.OnlineServices
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling

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
            match playerConn.Room with
            | None -> ()
            | Some roomId ->
                let! room =
                    roomId
                    |> roomStore.Get
                    |> Result.ofOption "Room not found"

                let newPlayersConnId =
                    room.Players
                    |> List.filter (fun (_playerId, connId) -> connId <> playerConnId)

                let newRoom = { room with Players = newPlayersConnId }

                do! roomStore.Update' roomId room newRoom
                    |> Result.requireTrue "Failed to update room"

            // Remove connection attempts
            let removeConnAttemptErrors =
                playerConn.ConnAttemptIds
                |> List.choose (fun connAttemptId ->
                    connAttemptId
                    |> connAttemptStore.Remove
                    |> function
                        | true -> None
                        | false -> Some $"Failed to remove connection attempt: {connAttemptId}"
                )

            match removeConnAttemptErrors with
            | [] -> ()
            | errors -> return! errors |> String.concat "\n" |> Error

            // Remove player connection
            do! playerConnsStore.Remove playerConnId
                |> Result.requireTrue "Failed to remove player connection"
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
                |> Result.requireTrue StartConnectionAttemptError.FailedToCreateOffer

            // Update player connection
            let newPlayerConn =
                { playerConn with ConnAttemptIds = connAttempt.Id :: playerConn.ConnAttemptIds }

            do! playerConnsStore.Update'
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
            do! { connAttempt with Answerer = Some connId }
                |> connAttemptStore.Update connAttemptId
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
                  Players = [ 1, playerConnId ] } // Host peer id should always be 1

            do! roomStore.Add
                    room.Id
                    room
                |> Result.requireTrue CreateRoomError.FailedToRegisterRoom

            // Update player connection
            let newPlayerConn =
                { playerConn with Room = Some room.Id }

            do! playerConnsStore.Update'
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue CreateRoomError.FailedToUpdatePlayerConnection

            // Add player to group
            do! hub.Groups.AddToGroupAsync(
                hub.Context.ConnectionId,
                room.Id |> RoomId.raw
            )

            return room.Id
        }

    /// Contains a bug: Can add player to the room and fail to create connection attempts (should create connection attempts in another method)
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

            let! room =
                roomId
                |> roomStore.Get
                |> Result.ofOption JoinRoomError.RoomNotFound

            // Update room
            let newPlayerId = (room.Players |> List.length) + 1
            let newRoom =
                { room with Players = (newPlayerId, playerConnId) :: room.Players }

            do! roomStore.Update'
                    roomId
                    room
                    newRoom
                |> Result.requireTrue JoinRoomError.FailedToUpdateRoom

            // Update player connection
            let newPlayerConn =
                { playerConn with Room = Some roomId }

            do! playerConnsStore.Update'
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue JoinRoomError.FailedToUpdatePlayerConnection

            // Add player to group
            do! hub.Groups.AddToGroupAsync(
                hub.Context.ConnectionId,
                roomId |> RoomId.raw
            )

            // --- Connect new player to all others
            let getConnAttemptIdForPlayer (playerId, connId) =
                fun _ ->
                    hub.Clients.Client(connId |> ConnId.raw).CreateOffer(newPlayerId)
                    |> Task.catch
                    |> Task.map (function
                        | Choice1Of2 (Some connAttemptId) -> Ok { PeerId = playerId; ConnAttemptId = connAttemptId }
                        | Choice1Of2 None -> Error JoinRoomError.FailedToCreateOffer
                        | Choice2Of2 _ -> Error JoinRoomError.FailedToCreateOffer)

            let! playersConnectionInfo =
                room.Players
                |> List.map getConnAttemptIdForPlayer
                |> List.traverseTaskResultM (fun func -> func ())
                |> TaskResult.map List.toArray

            return { PeerId = newPlayerId; PlayersConnectionInfo = playersConnectionInfo }
        }
