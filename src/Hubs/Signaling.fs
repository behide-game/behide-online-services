namespace Behide.OnlineServices.Hubs

open System.Threading.Tasks
open Behide.OnlineServices
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling


type ISignalingClient =
    abstract member CreateOffer : unit -> Task<struct(int * OfferId) option>
    abstract member SdpAnswerReceived: OfferId -> SdpDescription -> Task
    abstract member IceCandidateReceived: OfferId -> IceCandidate -> Task

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
        |> Task.map (function
            | Some offerId -> Ok offerId
            | None -> Error "Failed to create offer")

    member hub.GetOffer offerId =
        task {
            do! hub.Groups.AddToGroupAsync (hub.Context.ConnectionId, offerId |> OfferId.raw)
            return offerStore.Get offerId
        }
        |> Task.map (function
            | Some offerId -> Ok offerId
            | None -> Error "Offer not found")

    member hub.SendAnswer offerId sdpAnswer =
        task {
            do! hub.Clients.OthersInGroup(offerId |> OfferId.raw).SdpAnswerReceived offerId sdpAnswer
            offerStore.Remove offerId |> ignore
        } :> Task

    member hub.SendIceCandidate offerId iceCandidate =
        hub.Clients.OthersInGroup(offerId |> OfferId.raw).IceCandidateReceived offerId iceCandidate

    // --- Rooms ---
    member hub.CreateRoom() =
        taskOption {
            let connId = hub.Context.ConnectionId
            let roomId = RoomId.create()

            do! roomStore.Add roomId { RoomId = roomId; HostConnectionId = connId }
                |> function true -> Some () | false -> None

            return roomId
        }
        |> Task.map (function
            | Some roomId -> Ok roomId
            | None -> Error "Failed to create room")

    member hub.JoinRoom(roomId) =
        taskResult {
            let! room =
                roomStore.Get roomId
                |> function
                    | Some room -> Ok room
                    | None -> Error "Room not found"
            let hostConnId = room.HostConnectionId

            let! offerOpt = hub.Clients.Client(hostConnId).CreateOffer()

            match offerOpt with
            | Some offer -> return offer
            | None -> return! Error "Failed to create offer"
        }