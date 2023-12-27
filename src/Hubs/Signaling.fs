namespace Behide.OnlineServices.Hubs

open System.Threading.Tasks
open Behide.OnlineServices
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling


type ISignalingClient =
    abstract member CreateOffer : int -> Task<OfferId option>
    abstract member SdpAnswerReceived: OfferId -> SdpDescription -> Task
    abstract member IceCandidateReceived: OfferId -> IceCandidate -> Task

type SignalingHub(offerStore: Store.IStore<OfferId, SdpDescription>, roomStore: Store.IStore<RoomId, Room>) =
    inherit Hub<ISignalingClient>()

    // Called by OfferPeer
    member hub.AddOffer sdp =
        taskResult {
            let offerId = OfferId.create()

            do! offerStore.Add offerId sdp
                |> Result.requireTrue "Failed to create offer"

            do! hub.Groups.AddToGroupAsync (hub.Context.ConnectionId, offerId |> OfferId.raw)

            return offerId
        }

    // Called by AnswerPeer
    member hub.GetOffer offerId =
        task {
            do! hub.Groups.AddToGroupAsync (hub.Context.ConnectionId, offerId |> OfferId.raw)
            return
                offerStore.Get offerId
                |> Result.ofOption "Offer not found"
        }

    member hub.EndConnectionAttempt offerId =
        task {
            printfn "%A ended" offerId
            offerStore.Remove offerId |> ignore
            do! hub.Groups.RemoveFromGroupAsync (hub.Context.ConnectionId, offerId |> OfferId.raw)
        }

    member hub.SendAnswer offerId sdpAnswer =
        hub.Clients.OthersInGroup(offerId |> OfferId.raw).SdpAnswerReceived offerId sdpAnswer

    member hub.SendIceCandidate offerId iceCandidate =
        hub.Clients.OthersInGroup(offerId |> OfferId.raw).IceCandidateReceived offerId iceCandidate

    // --- Rooms ---
    member hub.CreateRoom() =
        taskResult {
            let connId = hub.Context.ConnectionId
            let roomId = RoomId.create()

            do! { RoomId = roomId
                  PlayersConnectionId = [| 1, connId |] } // Host peer id should always be 1
                |> roomStore.Add roomId
                |> Result.requireTrue "Failed to create room"

            return roomId
        }

    member hub.JoinRoom(roomId) =
        taskResult {
            let! room = roomStore.Get roomId |> Result.ofOption "Room not found"

            // Update room
            let newPlayerId = room.PlayersConnectionId.Length + 1
            let newPlayersConnId =
                Array.append
                    room.PlayersConnectionId
                    [| newPlayerId, hub.Context.ConnectionId |]

            let newRoom = { room with PlayersConnectionId = newPlayersConnId }

            do! roomStore.Update' room.RoomId room newRoom
                |> Result.requireTrue "Failed to update room"

            // Generate offer ids
            let getOfferIdForPlayer (playerId, connId) =
                fun _ ->
                    hub.Clients.Client(connId).CreateOffer(newPlayerId)
                    |> Task.catch
                    |> Task.map (function
                        | Choice1Of2 (Some offerId) -> Ok { PeerId = playerId; OfferId = offerId }
                        | Choice1Of2 None -> Error $"Failed to create offer for {playerId}"
                        | Choice2Of2 _ -> Error $"Error occurred when creating offer id")

            let! offerIds =
                room.PlayersConnectionId
                |> List.ofArray
                |> List.map getOfferIdForPlayer
                |> List.traverseTaskResultM (fun func -> func ())
                |> TaskResult.map List.toArray

            return { PeerId = newPlayerId; PlayersConnectionInfo = offerIds }
        }