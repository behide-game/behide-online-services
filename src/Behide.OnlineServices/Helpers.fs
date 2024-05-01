[<AutoOpen>]
module Behide.OnlineServices.Helpers

module Result =
    let inline ofOption error opt =
        match opt with
        | Some value -> Ok value
        | None -> Error error

let (|Equals|_|) valueToMatch value =
    match value = valueToMatch with
    | true -> Some ()
    | false -> None