namespace Behide.OnlineServices.Hubs.Signaling

open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling
open Behide.OnlineServices
open Behide.OnlineServices.Signaling

type SignalingHub(connAttemptStore: IConnAttemptStore, roomStore: IRoomStore, playerConnectionStore: IPlayerConnectionStore) =
    inherit Hub<ISignalingClient>()
    // Should interface ISignalingHub, but it makes the methods not callable from the client

    // --- Player Connection Management ---
    override hub.OnConnectedAsync() =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let playerConn =
                { ConnectionId = playerConnId
                  ConnAttemptIds = []
                  Room = None }

            do! playerConnectionStore.Add
                    playerConnId
                    playerConn
                |> Result.requireTrue "Failed to add player connection"
        }
        |> TaskResult.mapError (printfn "Error occurred while registering player: %s")
        |> Task.map ignore
        :> Task

    override hub.OnDisconnectedAsync _exn =
        taskResult {
            let playerConnId = hub.Context.ConnectionId |> ConnId.parse

            let! playerConn =
                playerConnId
                |> playerConnectionStore.Get
                |> Result.ofOption "Player connection not found"

            // Remove player from room
            let! leaveRoomError =
                match playerConn.Room with
                | None -> None |> Task.singleton
                | Some _ ->
                    hub.LeaveRoom()
                    |> Task.map (function
                        | Ok _ -> None
                        | Error _ -> Some "Failed to remove player from it's room")

            // Remove connection attempts
            let removeConnAttemptsError =
                playerConn.ConnAttemptIds
                |> List.choose (fun connAttemptId ->
                    match connAttemptId |> connAttemptStore.Get with
                    | None -> None
                    | Some _ ->
                        match connAttemptId |> connAttemptStore.Remove with // TODO: Notify other player in the connection attempt
                        | true -> None
                        | false -> Some connAttemptId
                )
                |> function
                    | [] -> None
                    | failedConnAttempts ->
                        failedConnAttempts
                        |> sprintf "Failed to remove connection attempts: %A"
                        |> Some

            // Remove player connection
            let removePlayerConnectionError =
                playerConnectionStore.Remove playerConnId
                |> Result.requireTrue "Failed to remove player connection"
                |> function
                    | Ok _ -> None
                    | Error error -> Some error

            return!
                match leaveRoomError, removeConnAttemptsError, removePlayerConnectionError with
                | None, None, None -> Ok ()
                | _ ->
                    sprintf
                        "\nLeave room error: %s\nRemove connection attempt error: %s\nRemove player connection error: %s"
                        (leaveRoomError |> Option.defaultValue "None")
                        (removeConnAttemptsError |> Option.defaultValue "None")
                        (removePlayerConnectionError |> Option.defaultValue "None")
                    |> Error
        }
        |> TaskResult.mapError (printfn "Error occurred while deregistering player: %s")
        |> Task.map ignore
        :> Task

    // --- WebRTC Signaling ---
    member hub.StartConnectionAttempt (sdpDescription: SdpDescription) = WebRTCSignaling.startConnectionAttempt hub playerConnectionStore connAttemptStore sdpDescription
    member hub.JoinConnectionAttempt (connAttemptId: ConnAttemptId) = WebRTCSignaling.joinConnectionAttempt hub playerConnectionStore connAttemptStore connAttemptId
    member hub.SendAnswer (connAttemptId: ConnAttemptId) (answer: SdpDescription) = WebRTCSignaling.sendAnswer hub playerConnectionStore connAttemptStore connAttemptId answer
    member hub.SendIceCandidate (connAttemptId: ConnAttemptId) (iceCandidate: IceCandidate) = WebRTCSignaling.sendIceCandidate hub playerConnectionStore connAttemptStore connAttemptId iceCandidate
    member hub.EndConnectionAttempt (connAttemptId: ConnAttemptId) = WebRTCSignaling.endConnectionAttempt hub playerConnectionStore connAttemptStore connAttemptId

    // --- Rooms ---
    member hub.CreateRoom() = RoomManagement.createRoom hub playerConnectionStore connAttemptStore roomStore
    member hub.JoinRoom (roomId: RoomId) = RoomManagement.joinRoom hub playerConnectionStore connAttemptStore roomStore roomId
    member hub.ConnectToRoomPlayers() = RoomManagement.connectToRoomPlayers hub playerConnectionStore connAttemptStore roomStore
    member hub.LeaveRoom() = RoomManagement.leaveRoom hub playerConnectionStore connAttemptStore roomStore
