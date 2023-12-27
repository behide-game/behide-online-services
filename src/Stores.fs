module Behide.OnlineServices.Store

open System.Collections.Concurrent

// type IOfferStore =
//     abstract AddOffer: OfferId -> SdpDescription -> bool
//     abstract GetOffer: OfferId -> SdpDescription option
//     abstract RemoveOffer: OfferId -> bool

// type OfferStore() =
//     let offers = new ConcurrentDictionary<OfferId, SdpDescription>()

//     interface IOfferStore with
//         member _.AddOffer offerId sdp = offers.TryAdd(offerId, sdp)
//         member _.RemoveOffer offerId = offers.TryRemove(offerId) |> fst

//         member _.GetOffer offerId =
//             offers.TryGetValue(offerId)
//             |> function
//                 | false, _ -> None
//                 | true, sdp -> Some sdp

// type IRoomStore =
//     abstract AddRoom: Room -> bool
//     abstract GetRoom: RoomId -> Room option
//     abstract RemoveRoom: RoomId -> bool

// type RoomStore() =
//     let rooms = new ConcurrentDictionary<RoomId, Room>()

//     interface IRoomStore with
//         member _.AddRoom room = rooms.TryAdd(room.RoomId, room)
//         member _.RemoveRoom roomId = rooms.TryRemove(roomId) |> fst

//         member _.GetRoom roomId =
//             rooms.TryGetValue(roomId)
//             |> function
//                 | false, _ -> None
//                 | true, sdp -> Some sdp

type IStore<'Id, 'Item> =
    abstract Add: 'Id -> 'Item -> bool
    abstract Get: 'Id -> 'Item option
    abstract Remove: 'Id -> bool
    abstract Update: 'Id -> 'Item -> bool
    abstract Update': 'Id -> oldValue: 'Item -> newValue: 'Item -> bool

type Store<'Id, 'Item>() =
    let dict = new ConcurrentDictionary<'Id, 'Item>()

    interface IStore<'Id, 'Item> with
        member _.Add id item = dict.TryAdd(id, item)
        member _.Remove id = dict.TryRemove(id) |> fst
        member _.Get id =
            dict.TryGetValue(id)
            |> function
                | false, _ -> None
                | true, (item: 'Item) -> Some item

        member this.Update id newValue =
            let oldValueOpt = (this :> IStore<'Id, 'Item>).Get(id)

            match oldValueOpt with
            | None -> false
            | Some oldValue -> dict.TryUpdate(id, newValue, oldValue)

        member _.Update' id oldValue newValue =
            dict.TryUpdate(id, newValue, oldValue)



// let outrefToOption = function false, _ -> None | true, x -> Some x
// let boolToOption = function true -> Some () | false -> None

// type IRoomStore =
//     abstract CreateRoom: PlayerId -> SdpDescription -> (RoomId * string) option
//     abstract GetRoom: RoomId -> Room option
//     abstract AddPlayerSdpDescription: PlayerId -> SdpDescription -> RoomId -> bool
//     abstract AddPlayerIceCandidate: PlayerId -> IceCandidate -> RoomId -> bool

// type RoomStore() =
//     let rooms = new ConcurrentDictionary<RoomId, Room>()

//     interface IRoomStore with
//         member _.CreateRoom playerId sdpDescription =
//             let roomId = Guid.NewGuid() |> RoomId
//             let signalRGroupName = roomId |> RoomId.rawString

//             let room: Room =
//                 { SignalRGroupName = signalRGroupName
//                   HostPlayerId = playerId
//                   SdpDescriptions = [ playerId, sdpDescription ] |> dict |> Dictionary
//                   IceCandidates = [] |> dict |> Dictionary }

//             rooms.TryAdd(roomId, room)
//             |> function
//                 | true -> Some (roomId, signalRGroupName)
//                 | false -> None

//         member _.GetRoom roomId =
//             match rooms.TryGetValue(roomId) with
//             | false, _ -> None
//             | true, room -> Some room

//         member _.AddPlayerSdpDescription playerId sdpDescription roomId =
//             option {
//                 let! room =
//                     rooms.TryGetValue(roomId)
//                     |> outrefToOption

//                 let oldRoom = room

//                 do! room.SdpDescriptions.TryAdd(playerId, sdpDescription)
//                     |> boolToOption

//                 do! rooms.TryUpdate(roomId, room, oldRoom)
//                     |> boolToOption

//                 return ()
//             } |> Option.isSome

//         member _.AddPlayerIceCandidate playerId iceCandidate roomId =
//             option {
//                 let! room =
//                     rooms.TryGetValue(roomId)
//                     |> outrefToOption

//                 let oldRoom = room

//                 // Generate new ice candidate list for this player
//                 let iceCandidateList =
//                     room.IceCandidates.TryGetValue(playerId)
//                     |> outrefToOption
//                     |> Option.defaultValue List.empty

//                 let newIceCandidateList = iceCandidate :: iceCandidateList

//                 // Ice candidate dictionary
//                 playerId
//                 |> room.IceCandidates.Remove
//                 |> ignore

//                 do! room.IceCandidates.TryAdd(playerId, newIceCandidateList)
//                     |> boolToOption

//                 // Update room
//                 do! rooms.TryUpdate(roomId, room, oldRoom)
//                     |> boolToOption
//             } |> Option.isSome
