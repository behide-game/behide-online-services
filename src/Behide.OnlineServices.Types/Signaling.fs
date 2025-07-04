namespace Behide.OnlineServices.Signaling

open System
open System.Threading.Tasks

type SdpDescription =
    { ``type``: string
      sdp: string }

type IceCandidate =
    { media: string
      index: int
      name: string }

/// Represents a SignalR connection id
type ConnId =
    private | ConnId of string
    static member parse connId = ConnId connId
    static member raw (ConnId connId) = connId


// --- Connection attempt
/// Represents an connection attempt id
type ConnAttemptId =
    private | ConnAttemptId of Guid
    static member create () = Guid.NewGuid() |> ConnAttemptId
    static member raw (ConnAttemptId guid) = guid.ToString()

/// A peer to peer connection attempt
type ConnAttempt =
    { Id: ConnAttemptId
      InitiatorConnectionId: ConnId
      SdpDescription: SdpDescription
      Answerer: ConnId option }


// --- Room
/// Represents a room id
type RoomId =
    private | RoomId of string

    override this.ToString() = this |> RoomId.raw

    static member raw (RoomId str) = str

    static let chars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()
    static member create () =
        let rnd = Random()
        let getRandomChar () =
            rnd.NextDouble() * float chars.Length
            |> Math.Floor
            |> int
            |> fun idx -> Array.item idx chars
            |> string

        seq { for _ in 0..3 do getRandomChar() }
        |> String.concat ""
        |> RoomId

    static member tryParse (str: string) =
        let validStr =
            str.Length = 4
            && str |> Seq.forall (fun char -> Array.contains char chars)

        match validStr with
        | false -> None
        | true -> RoomId str |> Some

/// A room, also a group of players that are connected to each other
type Room =
    { Id: RoomId
      Initiator: ConnId
      /// Contains the initiator
      Players: (int * ConnId) list
      /// A list of the connections between the peers
      Connections: (ConnId * ConnId) list }



/// Player state in the signaling process
/// Only for the server to keep track of the player state
type PlayerConnection =
    { ConnectionId: ConnId
      ConnAttemptIds: ConnAttemptId list
      Room: RoomId option }


/// The connection info of a player
/// With its peer id that represent his id in the room
/// And the connection attempt id to connect to the player
/// Used when a player join a room and need to connect to the other players
type PlayerConnectionInfo = { PeerId: int; ConnAttemptId: ConnAttemptId }
type RoomConnectionInfo =
    { PlayersConnInfo: PlayerConnectionInfo array
      FailedCreations: int array }


module Errors =
    type StartConnectionAttemptError =
        | PlayerConnectionNotFound = 0
        | FailedToCreateConnAttempt = 1
        | FailedToUpdatePlayerConnection = 2

    type JoinConnectionAttemptError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1
        | OfferAlreadyAnswered = 2
        | InitiatorCannotJoin = 3
        | FailedToUpdateOffer = 4

    type SendAnswerError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1
        | NotAnswerer = 2
        | FailedToTransmitAnswer = 3

    type SendIceCandidateError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1
        | NotAnswerer = 2
        | NotParticipant = 3
        | FailedToTransmitCandidate = 4

    type EndConnectionAttemptError =
        | PlayerConnectionNotFound = 0
        | OfferNotFound = 1
        | NotParticipant = 2
        | FailedToRemoveOffer = 3

    type CreateRoomError =
        | PlayerConnectionNotFound = 0
        | PlayerAlreadyInARoom = 1
        | FailedToRegisterRoom = 2
        | FailedToUpdatePlayerConnection = 3

    type JoinRoomError =
        | PlayerConnectionNotFound = 0
        | PlayerAlreadyInARoom = 1
        | RoomNotFound = 2
        | FailedToUpdateRoom = 3
        | FailedToUpdatePlayerConnection = 4

    type ConnectToRoomPlayersError =
        | PlayerConnectionNotFound = 0
        | NotInARoom = 1
        | PlayerNotInRoomPlayers = 2
        | FailedToUpdateRoom = 3

    type LeaveRoomError =
        | PlayerConnectionNotFound = 0
        | NotInARoom = 1
        | FailedToUpdateRoom = 2
        | FailedToRemoveRoom = 3
        | FailedToUpdatePlayerConnection = 4

open Errors

// Members with several parameters should have their parameters named
// Otherwise, the library TypedSignalR.Client generate invalid C# code
type ISignalingHub =
    abstract member StartConnectionAttempt : SdpDescription -> Task<Result<ConnAttemptId, StartConnectionAttemptError>>
    abstract member JoinConnectionAttempt : ConnAttemptId -> Task<Result<SdpDescription, JoinConnectionAttemptError>>
    abstract member SendAnswer : ConnAttemptId -> answer: SdpDescription -> Task<Result<unit, SendAnswerError>>
    abstract member SendIceCandidate : ConnAttemptId -> iceCandidate: IceCandidate -> Task<Result<unit, SendIceCandidateError>>
    abstract member EndConnectionAttempt : ConnAttemptId -> Task<Result<unit, EndConnectionAttemptError>>

    abstract member CreateRoom : unit -> Task<Result<RoomId, CreateRoomError>>
    /// Return the peerId of the player in the room
    abstract member JoinRoom : RoomId -> Task<Result<int, JoinRoomError>>
    abstract member ConnectToRoomPlayers : unit -> Task<Result<RoomConnectionInfo, ConnectToRoomPlayersError>>
    abstract member LeaveRoom : unit -> Task<Result<unit, LeaveRoomError>>

type ISignalingClient =
    abstract member ConnectionRequested: applicantPeerId: int -> Task<ConnAttemptId | null>
    abstract member SdpAnswerReceived: ConnAttemptId -> SdpDescription -> Task
    abstract member IceCandidateReceived: ConnAttemptId -> IceCandidate -> Task
