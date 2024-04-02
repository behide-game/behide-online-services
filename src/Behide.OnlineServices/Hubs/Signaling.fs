namespace Behide.OnlineServices.Hubs.Signaling

open System.Threading.Tasks
open Behide.OnlineServices
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling


type ConnId =
    private | ConnId of string
    static member parse connId = ConnId connId
    static member raw (ConnId connId) = connId

/// Player state in the signaling process
type PlayerConnection =
    { ConnectionId: ConnId
      OfferIds: OfferId list
      Room: RoomId option }

/// A P2P offer
type P2POffer =
    { Id: OfferId
      InitiatorConnectionId: ConnId
      SdpDescription: SdpDescription }

/// A room
/// A group of players that are connected to each other
type Room =
    { Id: RoomId
      Initiator: ConnId
      Players: (int * ConnId) list }


type IPlayerConnsStore = Store.IStore<ConnId, PlayerConnection>
type PlayerConnsStore = Store.Store<ConnId, PlayerConnection>

type IOfferStore = Store.IStore<OfferId, P2POffer>
type OfferStore = Store.Store<OfferId, P2POffer>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>


type ISignalingClient =
    abstract member CreateOffer : int -> Task<OfferId option>
    abstract member SdpAnswerReceived: OfferId -> SdpDescription -> Task
    abstract member IceCandidateReceived: OfferId -> IceCandidate -> Task

module Errors =
    [<RequireQualifiedAccess>]
    type StartConnectionAttemptError =
        | PlayerConnectionNotFound = 0
        | FailedToCreateOffer = 1
        | FailedToUpdatePlayerConnection = 2

    [<RequireQualifiedAccess>]
    type JoinConnectionAttemptError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1

    [<RequireQualifiedAccess>]
    type SendAnswerError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1
        /// Client is not the answerer. You're trying to answer to your own offer
        | NotAnswerer = 2

    [<RequireQualifiedAccess>]
    type SendIceCandidateError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1

    [<RequireQualifiedAccess>]
    type CreateRoomError =
        | PlayerConnectionNotFound = 0
        | PlayerAlreadyInARoom = 1
        | FailedToRegisterRoom = 2
        | FailedToUpdatePlayerConnection = 3

    [<RequireQualifiedAccess>]
    type JoinRoomError =
        | PlayerConnectionNotFound = 0
        | PlayerAlreadyInARoom = 1
        | RoomNotFound = 2
        | FailedToUpdateRoom = 3
        | FailedToUpdatePlayerConnection = 4
        | FailedToCreateOffer = 5

open Errors

type ISignalingHub =
    abstract member StartConnectionAttempt : SdpDescription -> TaskResult<OfferId, StartConnectionAttemptError>
    abstract member JoinConnectionAttempt : OfferId -> TaskResult<SdpDescription, JoinConnectionAttemptError>
    abstract member SendAnswer : OfferId -> SdpDescription -> TaskResult<unit, SendAnswerError>
    abstract member SendIceCandidate : OfferId -> IceCandidate -> TaskResult<unit, SendIceCandidateError>

    abstract member CreateRoom : unit -> TaskResult<RoomId, CreateRoomError>
    abstract member JoinRoom : RoomId -> TaskResult<RoomConnectionInfo, JoinRoomError>

type SignalingHub(offerStore: IOfferStore, roomStore: IRoomStore, playerConnsStore: IPlayerConnsStore) =
    inherit Hub<ISignalingClient>()
    // Should interface ISignalingHub, but it make the methods not callable from the client

    // --- Player Connection Management ---
    override hub.OnConnectedAsync() =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let playerConn =
                { ConnectionId = playerConnId
                  OfferIds = []
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

            // Remove offers
            let removeOfferErrors =
                playerConn.OfferIds
                |> List.choose (fun offerId ->
                    offerId
                    |> offerStore.Remove
                    |> function
                        | true -> None
                        | false -> Some $"Failed to remove offer: {offerId}"
                )

            match removeOfferErrors with
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

            // Create offer
            let offer =
                { Id = OfferId.create ()
                  InitiatorConnectionId = playerConnId
                  SdpDescription = sdpDescription }

            do! offerStore.Add
                    offer.Id
                    offer
                |> Result.requireTrue StartConnectionAttemptError.FailedToCreateOffer

            // Update player connection
            let newPlayerConn =
                { playerConn with OfferIds = offer.Id :: playerConn.OfferIds }

            do! playerConnsStore.Update'
                    playerConnId
                    playerConn
                    newPlayerConn
                |> Result.requireTrue StartConnectionAttemptError.FailedToUpdatePlayerConnection

            // Add player to group
            do! hub.Groups.AddToGroupAsync(
                hub.Context.ConnectionId,
                offer.Id |> OfferId.raw
            )

            return offer.Id
        }

    member hub.JoinConnectionAttempt (offerId: OfferId) =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! _playerConn = // Check if client has a player connection
                playerConnId
                |> playerConnsStore.Get
                |> Result.ofOption JoinConnectionAttemptError.PlayerConnectionNotFound

            let! offer =
                offerId
                |> offerStore.Get
                |> Result.ofOption JoinConnectionAttemptError.OfferNotFound

            // Add player to group
            do! hub.Groups.AddToGroupAsync(
                hub.Context.ConnectionId,
                offer.Id |> OfferId.raw
            )

            return offer.SdpDescription
        }

    member hub.SendAnswer (offerId: OfferId) (sdpDescription: SdpDescription) =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! _playerConn = // Check if client has a player connection
                playerConnId
                |> playerConnsStore.Get
                |> Result.ofOption SendAnswerError.PlayerConnectionNotFound

            let! offer =
                offerId
                |> offerStore.Get
                |> Result.ofOption SendAnswerError.OfferNotFound

            // Check if player is the answerer
            do! Result.requireEqual
                    offer.InitiatorConnectionId
                    playerConnId
                    SendAnswerError.NotAnswerer

            // Send answer to initiator
            do! hub.Clients.Client(offer.InitiatorConnectionId |> ConnId.raw).SdpAnswerReceived offerId sdpDescription
        }

    member hub.SendIceCandidate (offerId: OfferId) (iceCandidate: IceCandidate) =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! _playerConn = // Check if client has a player connection
                playerConnId
                |> playerConnsStore.Get
                |> Result.ofOption SendIceCandidateError.PlayerConnectionNotFound

            let! offer = // Check if offer exists
                offerId
                |> offerStore.Get
                |> Result.ofOption SendIceCandidateError.OfferNotFound

            // Send ice candidate to other peer
            do! hub.Clients.OthersInGroup(offer.Id |> OfferId.raw).IceCandidateReceived offerId iceCandidate
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
            let getOfferIdForPlayer (playerId, connId) =
                fun _ ->
                    hub.Clients.Client(connId |> ConnId.raw).CreateOffer(newPlayerId)
                    |> Task.catch
                    |> Task.map (function
                        | Choice1Of2 (Some offerId) -> Ok { PeerId = playerId; OfferId = offerId }
                        | Choice1Of2 None -> Error JoinRoomError.FailedToCreateOffer
                        | Choice2Of2 _ -> Error JoinRoomError.FailedToCreateOffer)

            let! offerIds =
                room.Players
                |> List.map getOfferIdForPlayer
                |> List.traverseTaskResultM (fun func -> func ())
                |> TaskResult.map List.toArray

            return { PeerId = newPlayerId; PlayersConnectionInfo = offerIds }
        }
