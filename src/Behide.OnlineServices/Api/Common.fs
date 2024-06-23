namespace Behide.OnlineServices.Api.Common

module Falco =
    open Falco

    open System.Threading.Tasks
    open Microsoft.AspNetCore.Http

    type TRHttpHandler = HttpContext -> Task<Result<Task, Task>>

    let taskResultHandler (taskResultHandler: TRHttpHandler) : HttpHandler =
        fun ctx ->
            task {
                let! res = taskResultHandler ctx

                return res |> function
                    | Ok task
                    | Error task -> task
            }
            :> Task