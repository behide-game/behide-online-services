namespace Behide.OnlineServices.Signaling

open System
open System.Collections.Generic
open System.Threading.Tasks

type Pair<'T when 'T: comparison> = // TODO: Add tests
    { First: 'T; Second: 'T }

module Pair =
    let create first second =
        { First = min first second
          Second = max first second }

    let isInPair pair value = pair.First = value || pair.Second = value

type SdpDescription =
    { ``type``: string
      sdp: string }

type IceCandidate =
    { media: string
      index: int
      name: string }

type PlayerId =
    private | PlayerId of string
    static member fromHubConnectionId connId = PlayerId connId
    static member raw (PlayerId connId) = connId


// --- Connection attempt
type ConnectionAttemptId =
    private | ConnectionAttemptId of Guid
    static member create () = Guid.NewGuid() |> ConnectionAttemptId
    static member raw (ConnectionAttemptId guid) = guid.ToString()

/// A WebRTC connection attempt
type ConnectionAttempt =
    { Id: ConnectionAttemptId
      InitiatorConnectionId: PlayerId
      Offer: SdpDescription
      Answerer: PlayerId option }

// --- Room
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
      Initiator: PlayerId
      /// <summary>Player peer ids by player id</summary>
      /// <remarks>Contains the initiator</remarks>
      Players: Dictionary<PlayerId, int>
      /// A list of the connections between the peers
      Connections: HashSet<PlayerId Pair>
      ConnectionsInProgress: HashSet<PlayerId Pair>
      Semaphore: System.Threading.SemaphoreSlim }


/// Player state in the signaling process
/// Only for the server to keep track of the player state
type Player =
    { Id: PlayerId
      ConnectionAttemptIds: ConnectionAttemptId list
      Room: {| Id: RoomId; PeerId: int |} option }


/// <summary>Information needed to connect to a player</summary>
/// <remarks>Used when a player join a room and need to connect to the other players</remarks>
type PlayerConnectionInfo = { PeerId: int; ConnAttemptId: ConnectionAttemptId }
type RoomConnectionInfo =
    { PlayersConnectionInfo: PlayerConnectionInfo array
      FailedCreations: int array }


open Behide.OnlineServices.Signaling.Errors

// Members with several parameters should have their parameters named
// Otherwise, the library TypedSignalR.Client generate invalid C# code
type ISignalingHub =
    abstract member StartConnectionAttempt : SdpDescription -> Task<Result<ConnectionAttemptId, StartConnectionAttemptError>>
    /// Returns the offer sdp desc and allow to send the answer
    abstract member JoinConnectionAttempt : ConnectionAttemptId -> Task<Result<SdpDescription, JoinConnectionAttemptError>>
    abstract member SendAnswer : ConnectionAttemptId -> answer: SdpDescription -> Task<Result<unit, SendAnswerError>>
    abstract member SendIceCandidate : ConnectionAttemptId -> iceCandidate: IceCandidate -> Task<Result<unit, SendIceCandidateError>>
    abstract member EndConnectionAttempt : ConnectionAttemptId -> Task<Result<unit, EndConnectionAttemptError>>

    abstract member CreateRoom : unit -> Task<Result<RoomId, CreateRoomError>>
    /// Return the peerId of the player in the room
    abstract member JoinRoom : RoomId -> Task<Result<int, JoinRoomError>>
    abstract member ConnectToRoomPlayers : unit -> Task<Result<RoomConnectionInfo, ConnectToRoomPlayersError>>
    abstract member LeaveRoom : unit -> Task<Result<unit, LeaveRoomError>>

type ISignalingClient =
    abstract member ConnectionRequested: applicantPeerId: int -> Task<ConnectionAttemptId | null>
    abstract member SdpAnswerReceived: ConnectionAttemptId -> SdpDescription -> Task
    abstract member IceCandidateReceived: ConnectionAttemptId -> IceCandidate -> Task
