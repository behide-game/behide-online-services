namespace Behide.OnlineServices.Hubs

open System.Threading.Tasks
open Behide.OnlineServices
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling


type ISignalingClient =
    abstract member CreateOffer : unit -> Task<OfferId option>
    abstract member SdpAnswerReceived: sdpDescription: SdpDescription -> Task
    abstract member IceCandidateReceived: iceCandidate: IceCandidate -> Task

type SignalingHub(offerStore: Store.IStore<OfferId, SdpDescription>, roomStore: Store.IStore<RoomId, Room>) =
    inherit Hub<ISignalingClient>()

    member hub.AddOffer sdp =
        taskOption {
            let offerId = OfferId.create()

            do! offerStore.Add offerId sdp
                |> function true -> Some () | false -> None

            do! hub.Groups.AddToGroupAsync (hub.Context.ConnectionId, offerId |> OfferId.raw)

            return offerId
        }

    member hub.GetOffer offerId = task {
        do! hub.Groups.AddToGroupAsync (hub.Context.ConnectionId, offerId |> OfferId.raw)
        return offerStore.Get offerId
    }

    member hub.SendAnswer offerId sdpAnswer =
        hub.Clients.OthersInGroup(offerId |> OfferId.raw).SdpAnswerReceived(sdpAnswer)

    member hub.SendIceCandidate offerId iceCandidate =
        hub.Clients.OthersInGroup(offerId |> OfferId.raw).IceCandidateReceived iceCandidate

    member _.DeleteOffer offerId =
        offerStore.Remove offerId |> Task.singleton

    // --- Rooms ---
    member hub.CreateRoom() =
        taskOption {
            let connId = hub.Context.ConnectionId
            let roomId = RoomId.create()

            do! roomStore.Add roomId { RoomId = roomId; HostConnectionId = connId }
                |> function true -> Some () | false -> None

            return roomId
        }

    member hub.JoinRoom(roomId) =
        taskOption {
            let! room = roomStore.Get roomId
            let hostConnId = room.HostConnectionId

            return! hub.Clients.Client(hostConnId).CreateOffer()
        }