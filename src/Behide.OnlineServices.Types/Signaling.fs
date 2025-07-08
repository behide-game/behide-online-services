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

type ConnectionAttemptId =
    private | ConnectionAttemptId of Guid
    static member create () = Guid.NewGuid() |> ConnectionAttemptId
    static member raw (ConnectionAttemptId guid) = guid.ToString()

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
        let loweredStr = str.ToLowerInvariant()
        let validStr =
            loweredStr.Length = 4
            && loweredStr |> Seq.forall (fun char -> Array.contains char chars)

        match validStr with
        | false -> None
        | true -> RoomId loweredStr |> Some


/// <summary>Information needed to connect to a player</summary>
/// <remarks>Used when a player join a room and need to connect to the other players</remarks>
type PlayerConnectionInfo = { PeerId: int; ConnectionAttemptId: ConnectionAttemptId }
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
