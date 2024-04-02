module Behide.OnlineServices.Tests.Common

open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection

open Behide.OnlineServices

let createTestServer () =
    let offerStore = new Hubs.Signaling.OfferStore()
    let roomStore = new Hubs.Signaling.RoomStore()
    let playerConnsStore = new Hubs.Signaling.PlayerConnsStore()

    let hostBuilder =
        WebHostBuilder()
            .ConfigureServices(fun services ->
                services |> Program.configureServices

                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IOfferStore, Hubs.Signaling.OfferStore>()) |> ignore
                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IRoomStore, Hubs.Signaling.RoomStore>()) |> ignore
                services.Remove(ServiceDescriptor.Singleton<Hubs.Signaling.IPlayerConnsStore, Hubs.Signaling.PlayerConnsStore>()) |> ignore

                services.AddSingleton<Hubs.Signaling.IOfferStore>(offerStore) |> ignore
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