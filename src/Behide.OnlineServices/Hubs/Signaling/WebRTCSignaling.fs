module Behide.OnlineServices.Hubs.Signaling.WebRTCSignaling

open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Signaling.Errors
type Hub = Microsoft.AspNetCore.SignalR.Hub<ISignalingClient>

open FsToolkit.ErrorHandling

let startConnectionAttempt (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (connAttemptStore: IConnAttemptStore) (sdpDescription: SdpDescription) =
    taskResult {
        let playerConnId = hub.Context.ConnectionId |> ConnId.parse

        let! playerConn =
            playerConnId
            |> playerConnectionStore.Get
            |> Result.ofOption StartConnectionAttemptError.PlayerConnectionNotFound

        // Create connection attempt
        let connAttempt =
            { Id = ConnAttemptId.create ()
              InitiatorConnectionId = playerConnId
              SdpDescription = sdpDescription
              Answerer = None }

        do! connAttemptStore.Add
                connAttempt.Id
                connAttempt
            |> Result.requireTrue StartConnectionAttemptError.FailedToCreateConnAttempt

        // Update player connection
        let newPlayerConn =
            { playerConn with ConnAttemptIds = connAttempt.Id :: playerConn.ConnAttemptIds }

        do! playerConnectionStore.Update
                playerConnId
                playerConn
                newPlayerConn
            |> Result.requireTrue StartConnectionAttemptError.FailedToUpdatePlayerConnection

        return connAttempt.Id
    }

/// Returns the offer sdp desc and allow to send the answer
let joinConnectionAttempt (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (connAttemptStore: IConnAttemptStore) (connAttemptId: ConnAttemptId) =
    taskResult {
        let connId = hub.Context.ConnectionId |> ConnId.parse

        // Check if client has a player connection
        do! connId
            |> playerConnectionStore.Get
            |> Result.ofOption JoinConnectionAttemptError.PlayerConnectionNotFound
            |> Result.ignore

        // Retrieve connection attempt
        let! connAttempt =
            connAttemptId
            |> connAttemptStore.Get
            |> Result.ofOption JoinConnectionAttemptError.OfferNotFound

        // Check if connection attempt has not been answered
        do! connAttempt.Answerer
            |> Result.requireNone JoinConnectionAttemptError.OfferAlreadyAnswered

        // Check if the answerer is not the initiator
        do! connAttempt.InitiatorConnectionId <> connId
            |> Result.requireTrue JoinConnectionAttemptError.InitiatorCannotJoin

        // Update connection attempt
        do! connAttemptStore.Update
                connAttemptId
                connAttempt
                { connAttempt with Answerer = Some connId }
            |> Result.requireTrue JoinConnectionAttemptError.FailedToUpdateOffer

        return connAttempt.SdpDescription
    }

let sendAnswer (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (connAttemptStore: IConnAttemptStore) (connAttemptId: ConnAttemptId) (sdpDescription: SdpDescription) =
    taskResult {
        let connId = hub.Context.ConnectionId |> ConnId.parse

        // Check if client has a player connection
        do! connId
            |> playerConnectionStore.Get
            |> Result.ofOption SendAnswerError.PlayerConnectionNotFound
            |> Result.ignore

        // Retrieve connection attempt
        let! connAttempt =
            connAttemptId
            |> connAttemptStore.Get
            |> Result.ofOption SendAnswerError.OfferNotFound

        // Get answerer
        let! answerer = connAttempt.Answerer |> Result.ofOption SendAnswerError.NotAnswerer
        // Check if the client is the answerer
        do! connId = answerer |> Result.requireTrue SendAnswerError.NotAnswerer

        // Send answer to initiator
        try
            do! hub.Clients.Client(connAttempt.InitiatorConnectionId |> ConnId.raw).SdpAnswerReceived connAttemptId sdpDescription
        with _ ->
            return! Error SendAnswerError.FailedToTransmitAnswer
    }

let sendIceCandidate (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (connAttemptStore: IConnAttemptStore) (connAttemptId: ConnAttemptId) (iceCandidate: IceCandidate) =
    taskResult {
        let connId = hub.Context.ConnectionId |> ConnId.parse

        // Check if client has a player connection
        do! connId
            |> playerConnectionStore.Get
            |> Result.ofOption SendIceCandidateError.PlayerConnectionNotFound
            |> Result.ignore

        let! connAttempt =
            connAttemptId
            |> connAttemptStore.Get
            |> Result.ofOption SendIceCandidateError.OfferNotFound

        let! answerer =
            connAttempt.Answerer
            |> Result.ofOption SendIceCandidateError.NotAnswerer

        // Check if the client is in the connection attempt
        do! (connId = connAttempt.InitiatorConnectionId || connId = answerer)
            |> Result.requireTrue SendIceCandidateError.NotParticipant

        // Determine the target connection id
        let targetConnId =
            match connId = answerer with
            | true -> connAttempt.InitiatorConnectionId
            | false -> answerer

        // Send ice candidate to other peer
        try
            do! hub.Clients.Client(targetConnId |> ConnId.raw).IceCandidateReceived connAttemptId iceCandidate
        with _ ->
            return! Error SendIceCandidateError.FailedToTransmitCandidate
    }

let endConnectionAttempt (hub: Hub) (playerConnectionStore: IPlayerConnectionStore) (connAttemptStore: IConnAttemptStore) (connAttemptId: ConnAttemptId) =
    taskResult {
        let connId = hub.Context.ConnectionId |> ConnId.parse

        // Check if client has a player connection
        do! connId
            |> playerConnectionStore.Get
            |> Result.ofOption EndConnectionAttemptError.PlayerConnectionNotFound
            |> Result.ignore

        let! connAttempt =
            connAttemptId
            |> connAttemptStore.Get
            |> Result.ofOption EndConnectionAttemptError.OfferNotFound

        // Check if the client is in the connection attempt
        match connId = connAttempt.InitiatorConnectionId with
        | true -> ()
        | false ->
            do! connAttempt.Answerer
                |> Result.ofOption EndConnectionAttemptError.NotParticipant
                |> Result.bind ((=) connId >> Result.requireTrue EndConnectionAttemptError.NotParticipant)

        // Remove connection attempt
        do! connAttemptStore.Remove connAttemptId
            |> Result.requireTrue EndConnectionAttemptError.FailedToRemoveOffer
    }
