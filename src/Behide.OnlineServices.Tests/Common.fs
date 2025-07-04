module Behide.OnlineServices.Tests.Common

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Falco.Extensions

open Behide.OnlineServices
open Behide.OnlineServices.Signaling

let createTestServer () =
    let offerStore = Hubs.Signaling.ConnAttemptStore()
    let roomStore = Hubs.Signaling.RoomStore()
    let playerConnStore = Hubs.Signaling.PlayerConnStore()

    let hostBuilder =
        WebHostBuilder()
            .ConfigureServices(fun services ->
                services |> Program.configureServices |> ignore

                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IConnAttemptStore, Hubs.Signaling.ConnAttemptStore>()) |> ignore
                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IRoomStore, Hubs.Signaling.RoomStore>()) |> ignore
                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IPlayerConnStore, Hubs.Signaling.PlayerConnStore>()) |> ignore

                services.AddSingleton<Hubs.Signaling.IConnAttemptStore>(offerStore) |> ignore
                services.AddSingleton<Hubs.Signaling.IRoomStore>(roomStore) |> ignore
                services.AddSingleton<Hubs.Signaling.IPlayerConnStore>(playerConnStore) |> ignore
            )
            .Configure(fun app ->
                app.UseRouting()
                    .UseEndpoints(fun endpoints ->
                        endpoints.MapHub<Behide.OnlineServices.Hubs.Signaling.SignalingHub>("/webrtc-signaling") |> ignore
                    )
                    .UseFalco(Program.appEndpoints) |> ignore
            )

    new TestServer(hostBuilder),
    offerStore :> Store.IStore<_, _>,
    roomStore :> Store.IStore<_, _>,
    playerConnStore :> Store.IStore<_, _>

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
