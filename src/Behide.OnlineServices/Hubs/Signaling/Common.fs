namespace Behide.OnlineServices.Hubs.Signaling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling

/// Store of player states in the signaling process
type IPlayerStore = Store.IStore<PlayerId, Player>
type PlayerStore = Store.Store<PlayerId, Player>

/// WebRTC connection attempts store
type IConnectionAttemptStore = Store.IStore<ConnectionAttemptId, ConnectionAttempt>
type ConnectionAttemptStore = Store.Store<ConnectionAttemptId, ConnectionAttempt>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>
