module Behide.OnlineServices.Tests.Common

open Falco
open FsToolkit.ErrorHandling
open Expecto.Tests

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
                |> fun builder -> builder.UseFalco(Program.appEndpoints)
                |> ignore
            )

    new TestServer(hostBuilder),
    {| OfferStore = offerStore :> Store.IStore<_, _>
       RoomStore = roomStore :> Store.IStore<_, _>
       PlayerConnsStore = playerConnsStore :> Store.IStore<_, _> |}

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

module Serialization =
    open Thoth.Json.Net

    let private decoder<'T> = Decode.Auto.generateDecoderCached<'T>()

    let decode<'T> = Decode.fromString decoder<'T>
    let wantDecodable<'T> message =
        decode<'T> >> Result.defaultWith (failtestf "Failed to decode: %s: %s" message)

    let isDecodable<'T> message = wantDecodable<'T> message >> ignore

    let decodeHttpResponse<'T> (response: System.Net.Http.HttpResponseMessage) =
        response.Content.ReadAsStringAsync()
        |> Task.map (Decode.fromString decoder<'T>)
        |> TaskResult.defaultWith (fun _ -> failtest "Failed to decode response")

module User =
    open Behide.OnlineServices.Api
    open Behide.OnlineServices.Repository

    let createUser () =
        { Id = UserId.create()
          Name = sprintf "fake name: %i" (System.DateTimeOffset.Now.ToUnixTimeMilliseconds())
          AuthConnection = Auth.ProviderConnection.Google "fake google id"
          RefreshTokenHashes = Array.empty }

    let putInDatabase user =
        task {
            do! user |> Database.Users.insert
            return user
        }

    let putRefreshTokenHashInDb (user: User) refreshToken =
        Database.Users.addRefreshTokenHashToUser user.Id refreshToken
        |> TaskResult.defaultWith (fun _ -> failtest "Failed to add refresh token to user")