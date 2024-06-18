namespace Behide.OnlineServices

open System

type UserId = UserId of Guid
module UserId =
    let tryParse (str: string) =
        Guid.TryParse(str)
        |> function
            | true, guid -> Some (UserId guid)
            | _ -> None
    let create () = Guid.NewGuid() |> UserId
    let raw (UserId guid) = guid
    let rawString (UserId guid) = guid.ToString()
    let rawBytes (UserId guid) = guid.ToByteArray()

type User = {
    Id: UserId
    Name: string
    AuthConnection: Auth.ProviderConnection
    TokenHashes: Auth.TokenHashes
}