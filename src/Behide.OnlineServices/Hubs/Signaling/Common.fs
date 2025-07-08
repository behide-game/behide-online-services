namespace Behide.OnlineServices.Hubs.Signaling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling

/// Store of player states in the signaling process
type IPlayerConnectionStore = Store.IStore<ConnId, PlayerConnection>
type PlayerConnectionStore = Store.Store<ConnId, PlayerConnection>

/// WebRTC connection attempts store
type IConnAttemptStore = Store.IStore<ConnAttemptId, ConnAttempt>
type ConnAttemptStore = Store.Store<ConnAttemptId, ConnAttempt>

type IRoomStore = Store.IStore<RoomId, Room>
type RoomStore = Store.Store<RoomId, Room>
