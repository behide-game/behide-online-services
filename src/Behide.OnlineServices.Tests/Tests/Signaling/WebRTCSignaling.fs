﻿module Behide.OnlineServices.Tests.Signaling.WebRTCSignaling

open Expecto
open FsToolkit.ErrorHandling

open Behide.OnlineServices.Signaling
open Behide.OnlineServices.Tests
open Behide.OnlineServices.Tests.Signaling.Common

let tests testServer (connectionAttemptStore: IConnectionAttemptStore) =
    testList "WebRTC Signaling" [
        testList "StartConnectionAttempt" [
            testTask "Create connection attempt" {
                let! (signalingHub: TestHubClient) = testServer |> connectHub

                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Check if the offer was created
                let offer =
                    connectionAttemptStore.Get offerId
                    |> Flip.Expect.wantSome "Offer should be created"

                Expect.equal
                    offer.InitiatorConnectionId
                    signalingHub.PlayerId
                    "Offer initiator should be the player connection id"

                Expect.equal
                    offer.Offer
                    Common.fakeSdpDescription
                    "Offer SDP description should be the same"
            }
        ]

        testList "JoinConnectionAttempt" [
            testTask "Join connection attempt" {
                // Create a connection attempt
                let! (signalingHub1: TestHubClient) = testServer |> connectHub

                let originalSdpDesc = Common.fakeSdpDescription

                let! offerId =
                    originalSdpDesc
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Check if the offer has answerer (it should not)
                connectionAttemptStore.Get offerId
                |> Flip.Expect.wantSome "Offer should exist"
                |> _.Answerer
                |> Flip.Expect.isNone "Offer should be marked as not answered"

                // Join connection attempt
                let! (signalingHub2: TestHubClient) = testServer |> connectHub

                let! sdpDescription =
                    offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt joining should success")

                Expect.equal
                    sdpDescription
                    originalSdpDesc
                    "SDP description should be the same"

                // Check if the offer is marked as answered
                let offer =
                    connectionAttemptStore.Get offerId
                    |> Flip.Expect.wantSome "Offer should exist"

                Expect.isSome offer.Answerer "Offer should has an answerer"

                Expect.equal
                    offer.InitiatorConnectionId
                    signalingHub1.PlayerId
                    "Offer initiator should be the first player connection id"
            }

            testTask "Join nonexisting connection attempt" {
                let! (signalingHub: TestHubClient) = testServer |> connectHub

                let fakeOfferId = ConnectionAttemptId.create()

                let! (error: Errors.JoinConnectionAttemptError) =
                    fakeOfferId
                    |> signalingHub.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Joining nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.JoinConnectionAttemptError.ConnectionAttemptNotFound
                    "Nonexisting connection attempt joining should fail"
            }

            testTask "Join already answered connection attempt" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub
                let! (signalingHub3: TestHubClient) = testServer |> connectHub

                // Create a connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Try to join the same connection attempt again
                let! (error: Errors.JoinConnectionAttemptError) =
                    offerId
                    |> signalingHub3.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Joining already answered connection attempt should return an error")

                Expect.equal
                    error
                    Errors.JoinConnectionAttemptError.ConnectionAttemptAlreadyAnswered
                    "Already answered connection attempt joining should fail"
            }

            testTask "Join connection attempt as the initiator" {
                let! (signalingHub: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                let! (error: Errors.JoinConnectionAttemptError) =
                    offerId
                    |> signalingHub.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Joining connection attempt as the initiator should return an error")

                Expect.equal
                    error
                    Errors.JoinConnectionAttemptError.InitiatorCannotJoin
                    "Joining connection attempt as the initiator should fail"
            }
        ]

        testList "EndConnectionAttempt" [
            testTask "Initiator end connection attempt" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> hub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // End connection attempt
                do! offerId
                    |> hub1.EndConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt ending should success")

                // Check if the offer is removed
                connectionAttemptStore.Get offerId
                |> Flip.Expect.isNone "Offer should be removed"
            }

            testTask "Answerer end connection attempt" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> hub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // End connection attempt
                do! offerId
                    |> hub2.EndConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt ending should success")

                // Check if the offer is removed
                connectionAttemptStore.Get offerId
                |> Flip.Expect.isNone "Offer should be removed"
            }

            testTask "End nonexisting connection attempt" {
                let! (hub: TestHubClient) = testServer |> connectHub

                let fakeOfferId = ConnectionAttemptId.create()

                let! (error: Errors.EndConnectionAttemptError) =
                    hub.EndConnectionAttempt fakeOfferId
                    |> Task.map (Flip.Expect.wantError "Ending nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.EndConnectionAttemptError.ConnectionAttemptNotFound
                    "Ending nonexisting connection attempt should fail"
            }

            testTask "End ended connection attempt" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> hub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // End connection attempt
                do! offerId
                    |> hub1.EndConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt ending should success")

                // End connection attempt again
                let! (error: Errors.EndConnectionAttemptError) =
                    offerId
                    |> hub1.EndConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Ending already ended connection attempt should return an error")

                Expect.equal
                    error
                    Errors.EndConnectionAttemptError.ConnectionAttemptNotFound
                    "Ending already ended connection attempt should fail"
            }

            testTask "End connection attempt as not participant" {
                let! (hub1: TestHubClient) = testServer |> connectHub
                let! (hub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // End connection attempt
                let! (error: Errors.EndConnectionAttemptError) =
                    offerId
                    |> hub2.EndConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Ending connection attempt as not participant should return an error")

                Expect.equal
                    error
                    Errors.EndConnectionAttemptError.NotParticipant
                    "Ending connection attempt as not participant should fail"
            }

            testTask "End answered connection attempt as not participant" {
                let! (hub1: TestHubClient) = testServer |> connectHub // Initiator
                let! (hub2: TestHubClient) = testServer |> connectHub // Answerer
                let! (hub3: TestHubClient) = testServer |> connectHub // Other

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> hub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> hub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // End connection attempt as not participant
                let! (error: Errors.EndConnectionAttemptError) =
                    offerId
                    |> hub3.EndConnectionAttempt
                    |> Task.map (Flip.Expect.wantError "Ending connection attempt as not participant should return an error")

                Expect.equal
                    error
                    Errors.EndConnectionAttemptError.NotParticipant
                    "Ending connection attempt as not participant should fail"
            }
        ]

        testList "SendAnswer" [
            testTask "Send answer" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub

                // Subscribe to SdpAnswerReceived event
                let sdpAnswerReceivedTcs = Common.TimedTaskCompletionSource<ConnectionAttemptId * SdpDescription>(1000)

                signalingHub1.SetHandlerFor.SdpAnswerReceived(fun offerId sdpDescription ->
                    sdpAnswerReceivedTcs.SetResult(offerId, sdpDescription)
                )
                |> ignore

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Send answer
                let answerSdpDesc = Common.fakeSdpDescription
                do! answerSdpDesc
                    |> signalingHub2.SendAnswer offerId
                    |> Task.map (Flip.Expect.isOk "Answer sending should success")

                // Test received answer
                let! (receivedOfferId: ConnectionAttemptId, receivedSdpDesc: SdpDescription) = sdpAnswerReceivedTcs.Task

                Expect.equal
                    receivedOfferId
                    offerId
                    "Received offer ID should be the same"

                Expect.equal
                    receivedSdpDesc
                    answerSdpDesc
                    "Received SDP description should be the same"
            }

            testTask "Send answer to nonexisting connection attempt" {
                let! (signalingHub: TestHubClient) = testServer |> connectHub

                let fakeOfferId = ConnectionAttemptId.create()

                let! (error: Errors.SendAnswerError) =
                    signalingHub.SendAnswer
                        fakeOfferId
                        Common.fakeSdpDescription
                    |> Task.map (Flip.Expect.wantError "Sending answer to nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendAnswerError.ConnectionAttemptNotFound
                    "Sending answer to nonexisting connection attempt should fail"
            }

            testTask "Send answer to not joined connection attempt" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Sending answer
                let! (error: Errors.SendAnswerError) =
                    signalingHub2.SendAnswer
                        offerId
                        Common.fakeSdpDescription
                    |> Task.map (Flip.Expect.wantError "Sending answer to not joined connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendAnswerError.NotAnswerer
                    "Sending answer to not joined connection attempt should fail"
            }

            testTask "Send answer to joined connection attempt by another player" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub
                let! (signalingHub3: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Sending answer
                let! (error: Errors.SendAnswerError) =
                    signalingHub3.SendAnswer
                        offerId
                        Common.fakeSdpDescription
                    |> Task.map (Flip.Expect.wantError "Sending answer should return an error")

                Expect.equal
                    error
                    Errors.SendAnswerError.NotAnswerer
                    "Sending answer connection attempt should fail"
            }
        ]

        testList "SendIceCandidate" [
            testTask "Send ice candidate" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub

                // Subscribe to IceCandidateReceived event
                let iceCandidateReceivedTcs = Common.TimedTaskCompletionSource<ConnectionAttemptId * IceCandidate>(1000)

                signalingHub1.SetHandlerFor.IceCandidateReceived(fun offerId iceCandidate ->
                    iceCandidateReceivedTcs.SetResult(offerId, iceCandidate)
                )
                |> ignore

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Send ice candidate
                let iceCandidate = Common.fakeIceCandidate
                do! iceCandidate
                    |> signalingHub2.SendIceCandidate offerId
                    |> Task.map (Flip.Expect.isOk "Ice candidate sending should success")

                // Test received ice candidate
                let! (receivedOfferId: ConnectionAttemptId, receivedIceCandidate: IceCandidate) = iceCandidateReceivedTcs.Task

                Expect.equal
                    receivedOfferId
                    offerId
                    "Received offer ID should be the same"

                Expect.equal
                    receivedIceCandidate
                    iceCandidate
                    "Received ice candidate should be the same"
            }

            testTask "Send ice candidate to nonexisting connection attempt" {
                let! (signalingHub: TestHubClient) = testServer |> connectHub

                let fakeOfferId = ConnectionAttemptId.create()

                let! (error: Errors.SendIceCandidateError) =
                    signalingHub.SendIceCandidate
                        fakeOfferId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate to nonexisting connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.ConnectionAttemptNotFound
                    "Sending ice candidate to nonexisting connection attempt should fail"
            }

            testTask "Send ice candidate to not joined connection attempt" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Sending ice candidate
                let! (error: Errors.SendIceCandidateError) =
                    signalingHub2.SendIceCandidate
                        offerId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate to not joined connection attempt should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.NoAnswerer
                    "Sending ice candidate to not joined connection attempt should fail"
            }

            testTask "Send ice candidate to joined connection attempt by another player" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub
                let! (signalingHub3: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Join connection attempt
                do! offerId
                    |> signalingHub2.JoinConnectionAttempt
                    |> Task.map (Flip.Expect.isOk "Connection attempt joining should success")

                // Sending ice candidate
                let! (error: Errors.SendIceCandidateError) =
                    signalingHub3.SendIceCandidate
                        offerId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.NotParticipant
                    "Sending ice candidate connection attempt should fail"
            }

            testTask "Send ice candidate to connection attempt without answerer" {
                let! (signalingHub1: TestHubClient) = testServer |> connectHub
                let! (signalingHub2: TestHubClient) = testServer |> connectHub

                // Create connection attempt
                let! offerId =
                    Common.fakeSdpDescription
                    |> signalingHub1.StartConnectionAttempt
                    |> Task.map (Flip.Expect.wantOk "Connection attempt creation should success")

                // Sending ice candidate
                let! (error: Errors.SendIceCandidateError) =
                    signalingHub2.SendIceCandidate
                        offerId
                        Common.fakeIceCandidate
                    |> Task.map (Flip.Expect.wantError "Sending ice candidate should return an error")

                Expect.equal
                    error
                    Errors.SendIceCandidateError.NoAnswerer
                    "Sending ice candidate connection attempt should fail"
            }
        ]
    ]
