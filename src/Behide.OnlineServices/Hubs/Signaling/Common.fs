namespace Behide.OnlineServices.Signaling

open System.Collections.Generic
open Behide.OnlineServices
open Behide.OnlineServices.Signaling

type PlayerId =
    private | PlayerId of string
    static member fromHubConnectionId connId = PlayerId connId
    static member raw (PlayerId connId) = connId

/// A WebRTC connection attempt
type ConnectionAttempt =
    { Id: ConnectionAttemptId
      InitiatorConnectionId: PlayerId
      Offer: SdpDescription
      Answerer: PlayerId option }

/// A room, also a group of players that are connected to each other
type Room =
    { Id: RoomId
      /// Player peer ids by player id
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

// --- Stores ---
/// Store of player states in the signaling process
type IPlayerStore = Store.IStore<PlayerId, Player>
type PlayerStore = Store.Store<PlayerId, Player>

/// WebRTC connection attempts store
type IConnectionAttemptStore = Store.IStore<ConnectionAttemptId, ConnectionAttempt>
type ConnectionAttemptStore = Store.Store<ConnectionAttemptId, ConnectionAttempt>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>
