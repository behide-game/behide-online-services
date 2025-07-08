namespace Behide.OnlineServices.Signaling.Errors

type StartConnectionAttemptError =
    | PlayerNotFound = 0
    | FailedToCreateConnectionAttempt = 1
    | FailedToUpdatePlayer = 2

type JoinConnectionAttemptError =
    | PlayerNotFound = 0
    | ConnectionAttemptNotFound = 1
    | ConnectionAttemptAlreadyAnswered = 2
    | InitiatorCannotJoin = 3
    | FailedToUpdateConnectionAttempt = 4

type SendAnswerError =
    | PlayerNotFound = 0
    | ConnectionAttemptNotFound = 1
    | NotAnswerer = 2
    | FailedToTransmitAnswer = 3

type SendIceCandidateError =
    | PlayerNotFound = 0
    | ConnectionAttemptNotFound = 1
    | NoAnswerer = 2
    | NotParticipant = 3
    | FailedToTransmitCandidate = 4

type EndConnectionAttemptError =
    | PlayerNotFound = 0
    | ConnectionAttemptNotFound = 1
    | NotParticipant = 2
    | FailedToRemoveConnectionAttempt = 3

type CreateRoomError =
    | PlayerNotFound = 0
    | PlayerAlreadyInARoom = 1
    | FailedToRegisterRoom = 2
    | FailedToUpdatePlayer = 3

type JoinRoomError =
    | PlayerNotFound = 0
    | PlayerAlreadyInARoom = 1
    | RoomNotFound = 2
    | FailedToUpdatePlayer = 4

type ConnectToRoomPlayersError =
    | PlayerNotFound = 0
    | NotInARoom = 1
    | PlayerNotInRoomPlayers = 2

type LeaveRoomError =
    | PlayerNotFound = 0
    | NotInARoom = 1
    | FailedToUpdateRoom = 2
    | FailedToRemoveRoom = 3
    | FailedToUpdatePlayer = 4
