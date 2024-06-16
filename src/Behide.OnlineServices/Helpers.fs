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

module TaskOption =
    open System.Threading.Tasks

    let inline orElseWith (defThunk: unit -> Task<'a option>) (taskOpt: Task<'a option>) =
        task {
            let! opt = taskOpt

            match opt with
            | Some value -> return Some value
            | None -> return! defThunk ()
        }