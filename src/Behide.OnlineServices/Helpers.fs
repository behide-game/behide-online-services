[<AutoOpen>]
module Behide.OnlineServices.Helpers

module Result =
    let inline ofOption error opt =
        match opt with
        | Some value -> Ok value
        | None -> Error error
