module Behide.OnlineServices.Auth

open Falco
open Falco.Routing
open Microsoft.AspNetCore.Authentication
open Common.Auth
open Microsoft.AspNetCore.Http
open System.Net
open System.Threading.Tasks
open FsToolkit.ErrorHandling

type Provider =
    | Discord
    | Google
    | Microsoft

    static member parse (provider: string) =
        match provider with
        | "discord" -> Some Discord
        | "google" -> Some Google
        | "microsoft" -> Some Microsoft
        | _ -> None

    static member toString (provider: Provider) =
        match provider with
        | Discord -> "discord"
        | Google -> "google"
        | Microsoft -> "microsoft"

let signUpHandler (ctx: HttpContext) : Task = task {
    let route = ctx |> Request.getRoute
    let providerOpt =
        route.TryGetString "provider"
        |> Option.bind Provider.parse

    return! providerOpt |> function
        | None ->
            ctx
            |> Response.withStatusCode (HttpStatusCode.BadRequest |> int)
            |> Response.ofPlainText "Invalid provider"
        | Some provider ->
            Response.challengeWithRedirect
                (provider |> Provider.toString)
                "/auth/sign-up-complete"
                ctx
}

let endpoints = [
    get "/auth/sign-up/{provider:alpha}" signUpHandler
    get "/auth/sign-up-complete" (fun ctx -> task {
        let getToken tokenName =
            tokenName
            |> ctx.GetTokenAsync
            |> Task.map Option.ofNull

        let! token = task {
            let! idToken = getToken "id_token"

            match idToken with
            | Some _ -> return idToken
            | None -> return! getToken "access_token"
        }

        let! refreshToken = getToken "refresh_token"

        return
            ctx
            |> Response.clearAllCookies
            |> Response.ofJson {|
                token = token
                refreshToken = refreshToken
            |}
    })
]