namespace Behide.OnlineServices.Hubs.Signaling

open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling
open Behide.OnlineServices
open Behide.OnlineServices.Signaling

type SignalingHub(connectionAttemptStore: IConnectionAttemptStore, roomStore: IRoomStore, playerStore: IPlayerStore) =
    inherit Hub<ISignalingClient>()
    // Should interface ISignalingHub, but it makes the methods not callable from the client

    // --- Player Connection Management ---
    override hub.OnConnectedAsync() =
        taskResult {
            let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

            let player =
                { Id = playerId
                  ConnectionAttemptIds = List.empty
                  Room = None }

            do! playerStore.Add playerId player
                |> Result.requireTrue "Failed to add player connection"
        }
        |> TaskResult.mapError (printfn "Error occurred while registering player: %s")
        |> Task.map ignore
        :> Task

    override hub.OnDisconnectedAsync _exn =
        taskResult {
            let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

            let! player =
                playerId
                |> playerStore.Get
                |> Result.ofOption "Player connection not found"

            // Remove player from room
            let! leaveRoomError =
                match player.Room with
                | None -> None |> Task.singleton
                | Some _ ->
                    hub.LeaveRoom()
                    |> Task.map (function
                        | Ok _ -> None
                        | Error _ -> Some "Failed to remove player from it's room")

            // Remove connection attempts
            let removeConnectionAttemptsError =
                player.ConnectionAttemptIds
                |> List.filter (fun connAttemptId ->
                    match connAttemptId |> connectionAttemptStore.Get with
                    | None -> false
                    | Some _ -> connAttemptId |> connectionAttemptStore.Remove // TODO: Notify other player in the connection attempt
                )
                |> function
                    | [] -> None
                    | failedConnectionAttempts ->
                        failedConnectionAttempts
                        |> sprintf "Failed to remove connection attempts: %A"
                        |> Some

            // Remove player connection
            let removePlayerConnectionError =
                playerStore.Remove playerId
                |> Result.requireTrue "Failed to remove player"
                |> function
                    | Ok _ -> None
                    | Error error -> Some error

            return!
                match leaveRoomError, removeConnectionAttemptsError, removePlayerConnectionError with
                | None, None, None -> Ok ()
                | _ ->
                    Error <| sprintf
                        "\nLeave room error: %s\nRemove connection attempt error: %s\nRemove player error: %s"
                        (leaveRoomError |> Option.defaultValue "None")
                        (removeConnectionAttemptsError |> Option.defaultValue "None")
                        (removePlayerConnectionError |> Option.defaultValue "None")
        }
        |> TaskResult.mapError (printfn "Error occurred while deregistering player: %s")
        |> Task.map ignore
        :> Task

    // --- WebRTC Signaling ---
    member hub.StartConnectionAttempt (sdpDescription: SdpDescription) = WebRTCSignaling.startConnectionAttempt hub playerStore connectionAttemptStore sdpDescription
    member hub.JoinConnectionAttempt (connAttemptId: ConnectionAttemptId) = WebRTCSignaling.joinConnectionAttempt hub playerStore connectionAttemptStore connAttemptId
    member hub.SendAnswer (connAttemptId: ConnectionAttemptId) (answer: SdpDescription) = WebRTCSignaling.sendAnswer hub playerStore connectionAttemptStore connAttemptId answer
    member hub.SendIceCandidate (connAttemptId: ConnectionAttemptId) (iceCandidate: IceCandidate) = WebRTCSignaling.sendIceCandidate hub playerStore connectionAttemptStore connAttemptId iceCandidate
    member hub.EndConnectionAttempt (connAttemptId: ConnectionAttemptId) = WebRTCSignaling.endConnectionAttempt hub playerStore connectionAttemptStore connAttemptId

    // --- Rooms ---
    member hub.CreateRoom() = RoomManagement.createRoom hub playerStore connectionAttemptStore roomStore
    member hub.JoinRoom (roomId: RoomId) = RoomManagement.joinRoom hub playerStore connectionAttemptStore roomStore roomId
    member hub.ConnectToRoomPlayers() = RoomManagement.connectToRoomPlayers hub playerStore connectionAttemptStore roomStore
    member hub.LeaveRoom() = RoomManagement.leaveRoom hub playerStore connectionAttemptStore roomStore
