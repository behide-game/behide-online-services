module Behide.OnlineServices.Hubs.Signaling.WebRTCSignaling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Signaling.Errors
open FsToolkit.ErrorHandling

type Hub = Microsoft.AspNetCore.SignalR.Hub<ISignalingClient>

let startConnectionAttempt (hub: Hub) (playerStore: IPlayerStore) (connectionAttemptStore: IConnectionAttemptStore) (offer: SdpDescription) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        let! player =
            playerId
            |> playerStore.Get
            |> Result.ofOption StartConnectionAttemptError.PlayerNotFound

        // Create connection attempt
        let connectionAttempt =
            { Id = ConnectionAttemptId.create ()
              InitiatorConnectionId = playerId
              Offer = offer
              Answerer = None }

        do! connectionAttemptStore.Add connectionAttempt.Id connectionAttempt
            |> Result.requireTrue StartConnectionAttemptError.FailedToCreateConnectionAttempt

        // Update player connection
        let newPlayer =
            { player with ConnectionAttemptIds = connectionAttempt.Id :: player.ConnectionAttemptIds }

        do! playerStore.Update
                playerId
                player
                newPlayer
            |> Result.requireTrue StartConnectionAttemptError.FailedToUpdatePlayer

        return connectionAttempt.Id
    }

/// Returns the offer sdp desc and allow to send the answer
let joinConnectionAttempt (hub: Hub) (playerStore: IPlayerStore) (connectionAttemptStore: IConnectionAttemptStore) (connectionAttemptId: ConnectionAttemptId) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        // Check if player exists
        do! playerId
            |> playerStore.Get
            |> Result.ofOption JoinConnectionAttemptError.PlayerNotFound
            |> Result.ignore

        // Retrieve connection attempt
        let! connectionAttempt =
            connectionAttemptId
            |> connectionAttemptStore.Get
            |> Result.ofOption JoinConnectionAttemptError.ConnectionAttemptNotFound

        // Check if connection attempt has not been answered
        do! connectionAttempt.Answerer
            |> Result.requireNone JoinConnectionAttemptError.ConnectionAttemptAlreadyAnswered

        // Check if the answerer is not the initiator
        do! connectionAttempt.InitiatorConnectionId <> playerId
            |> Result.requireTrue JoinConnectionAttemptError.InitiatorCannotJoin

        // Update connection attempt
        do! connectionAttemptStore.Update
                connectionAttempt.Id
                connectionAttempt
                { connectionAttempt with Answerer = Some playerId }
            |> Result.requireTrue JoinConnectionAttemptError.FailedToUpdateConnectionAttempt

        return connectionAttempt.Offer
    }

let sendAnswer (hub: Hub) (playerStore: IPlayerStore) (connectionAttemptStore: IConnectionAttemptStore) (connectionAttemptId: ConnectionAttemptId) (sdpDescription: SdpDescription) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        // Check if player exists
        do! playerId
            |> playerStore.Get
            |> Result.ofOption SendAnswerError.PlayerNotFound
            |> Result.ignore

        // Retrieve connection attempt
        let! connectionAttempt =
            connectionAttemptId
            |> connectionAttemptStore.Get
            |> Result.ofOption SendAnswerError.ConnectionAttemptNotFound

        // Check if the client is the answerer
        do! connectionAttempt.Answerer
            |> Result.ofOption SendAnswerError.NotAnswerer
            |> Result.bind (fun pId -> Result.requireEqual pId playerId SendAnswerError.NotAnswerer)

        // Send answer to initiator
        try
            do! hub.Clients.Client(connectionAttempt.InitiatorConnectionId |> PlayerId.raw).SdpAnswerReceived connectionAttemptId sdpDescription
        with _ ->
            return! Error SendAnswerError.FailedToTransmitAnswer
    }

let sendIceCandidate (hub: Hub) (playerStore: IPlayerStore) (connectionAttemptStore: IConnectionAttemptStore) (connectionAttemptId: ConnectionAttemptId) (iceCandidate: IceCandidate) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        // Check if player exists
        do! playerId
            |> playerStore.Get
            |> Result.ofOption SendIceCandidateError.PlayerNotFound
            |> Result.ignore

        let! connectionAttempt =
            connectionAttemptId
            |> connectionAttemptStore.Get
            |> Result.ofOption SendIceCandidateError.ConnectionAttemptNotFound

        let! answerer =
            connectionAttempt.Answerer
            |> Result.ofOption SendIceCandidateError.NoAnswerer

        // Check if the player is in the connection attempt
        do! (playerId = connectionAttempt.InitiatorConnectionId || playerId = answerer)
            |> Result.requireTrue SendIceCandidateError.NotParticipant

        // Determine the target player
        let targetPlayerId =
            match playerId = answerer with
            | true -> connectionAttempt.InitiatorConnectionId
            | false -> answerer

        // Send ice candidate to other player
        try
            do! hub.Clients.Client(targetPlayerId |> PlayerId.raw).IceCandidateReceived connectionAttemptId iceCandidate
        with _ ->
            return! Error SendIceCandidateError.FailedToTransmitCandidate
    }

let endConnectionAttempt (hub: Hub) (playerStore: IPlayerStore) (connectionAttemptStore: IConnectionAttemptStore) (connectionAttemptId: ConnectionAttemptId) =
    taskResult {
        let playerId = hub.Context.ConnectionId |> PlayerId.fromHubConnectionId

        // Check if player exists
        do! playerId
            |> playerStore.Get
            |> Result.ofOption EndConnectionAttemptError.PlayerNotFound
            |> Result.ignore

        let! connectionAttempt =
            connectionAttemptId
            |> connectionAttemptStore.Get
            |> Result.ofOption EndConnectionAttemptError.ConnectionAttemptNotFound

        // Check if the player is in the connection attempt
        match playerId = connectionAttempt.InitiatorConnectionId with
        | true -> ()
        | false ->
            do! connectionAttempt.Answerer
                |> Result.ofOption EndConnectionAttemptError.NotParticipant
                |> Result.bind ((=) playerId >> Result.requireTrue EndConnectionAttemptError.NotParticipant)

        // Remove connection attempt
        do! connectionAttemptStore.Remove connectionAttemptId
            |> Result.requireTrue EndConnectionAttemptError.FailedToRemoveConnectionAttempt
    }
