namespace Behide.OnlineServices

open System

type SdpDescription =
    { ``type``: string
      sdp: string }

type IceCandidate =
    { media: string
      index: int
      name: string }


type OfferId =
    private | OfferId of Guid
    static member create () = Guid.NewGuid() |> OfferId
    static member raw (OfferId guid) = guid.ToString()


type RoomId =
    private | RoomId of string

    static member raw (RoomId str) = str

    static let chars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()
    static member create () =
        let rnd = Random()
        let getRandomChar () =
            rnd.NextDouble() * float chars.Length
            |> Math.Floor
            |> int
            |> fun idx -> Array.item idx chars
            |> string

        seq { for _ in 0..3 do getRandomChar() }
        |> String.concat ""
        |> RoomId

    static member tryParse (str: string) =
        let validStr =
            str.Length = 4
            && str |> Seq.forall (fun char -> Array.contains char chars)

        match validStr with
        | false -> None
        | true -> RoomId str |> Some


type Room =
    { RoomId: RoomId
      HostConnectionId: string }