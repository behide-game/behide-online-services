module Behide.OnlineServices.Tests.Common

open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Behide.OnlineServices
open Behide.OnlineServices.Signaling
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

type AppFactory() =
    inherit WebApplicationFactory<Program>()

    override _.ConfigureWebHost(builder: IWebHostBuilder) =
        builder
            .UseEnvironment("Testing")
            .ConfigureLogging(fun logging -> // Disable server logs
                logging.ClearProviders() |> ignore
                logging.SetMinimumLevel(LogLevel.None) |> ignore
            )
        |> ignore

let createTestServer () =
    let app = new AppFactory()

    app.Server,
    app.Services.GetService<IConnectionAttemptStore>() :> Store.IStore<_, _>,
    app.Services.GetService<IRoomStore>() :> Store.IStore<_, _>,
    app.Services.GetService<IPlayerStore>() :> Store.IStore<_, _>

let fakeSdpDescription: SdpDescription =
    { ``type`` = "fake type"
      sdp = "fake sdp" }

let fakeIceCandidate: IceCandidate =
    { media = "fake media"
      index = 0
      name = "fake name" }

type TimedTaskCompletionSource<'A>(timeout: int) =
    let tcs = new System.Threading.Tasks.TaskCompletionSource<'A>()
    let cts = new System.Threading.CancellationTokenSource(timeout)

    do
        cts.Token.Register(fun _ -> tcs.TrySetCanceled() |> ignore)
        |> ignore

    member _.Task = tcs.Task
    member _.SetResult(result) = tcs.SetResult(result)
    member _.SetException(ex: exn) = tcs.SetException(ex)
