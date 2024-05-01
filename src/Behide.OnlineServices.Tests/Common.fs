module Behide.OnlineServices.Tests.Common

open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection

open Behide.OnlineServices
open Behide.OnlineServices.Signaling

let createTestServer () =
    let offerStore = new Hubs.Signaling.ConnAttemptStore()
    let roomStore = new Hubs.Signaling.RoomStore()
    let playerConnsStore = new Hubs.Signaling.PlayerConnsStore()

    let hostBuilder =
        WebHostBuilder()
            .ConfigureServices(fun services ->
                services |> Program.configureServices

                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IConnAttemptStore, Hubs.Signaling.ConnAttemptStore>()) |> ignore
                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IRoomStore, Hubs.Signaling.RoomStore>()) |> ignore
                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IPlayerConnsStore, Hubs.Signaling.PlayerConnsStore>()) |> ignore

                services.AddSingleton<Hubs.Signaling.IConnAttemptStore>(offerStore) |> ignore
                services.AddSingleton<Hubs.Signaling.IRoomStore>(roomStore) |> ignore
                services.AddSingleton<Hubs.Signaling.IPlayerConnsStore>(playerConnsStore) |> ignore
            )
            .Configure(fun app ->
                app
                |> Program.appBuilder
                |> ignore
            )

    new TestServer(hostBuilder),
    offerStore :> Store.IStore<_, _>,
    roomStore :> Store.IStore<_, _>,
    playerConnsStore :> Store.IStore<_, _>

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
    member _.SetResult(result) = tcs.SetResult(result) |> ignore
    member _.SetException(ex: exn) = tcs.SetException(ex) |> ignore
