module Behide.OnlineServices.Auth

open Falco
open Falco.Routing
open System
open Microsoft.AspNetCore.Http.Extensions
open System.Threading.Tasks

let clientId = ""
let clientSecret = ""

let challengeGoogle : HttpHandler =
    let queryBuilder = QueryBuilder()
    queryBuilder.Add("client_id", clientId)
    queryBuilder.Add("response_type", "code")
    queryBuilder.Add("scope", "openid profile email")
    queryBuilder.Add("redirect_uri", "http://localhost:5001/auth/signin-google")
    queryBuilder.Add("access_type", "offline")

    let uriBuilder = UriBuilder "https://accounts.google.com/o/oauth2/v2/auth"
    uriBuilder.Query <- queryBuilder.ToString()

    uriBuilder.ToString()
    |> Response.redirectTemporarily

let afterChallengingUser ctx : Task = task {
    let query = ctx |> Request.getQuery

    let errorOpt = query.TryGetString "error"
    let codeOpt = query.TryGetString "code"

    match codeOpt with
    | None -> return! Response.ofPlainText "Failed to sign in with Google" ctx
    | Some code ->
        let content = new Net.Http.FormUrlEncodedContent([
            "client_id", clientId
            "client_secret", clientSecret
            "code", code
            "grant_type", "authorization_code"
            "redirect_uri", "http://localhost:5001/auth/signin-google"
        ] |> dict)

        let uriBuilder = UriBuilder "https://oauth2.googleapis.com/token"

        let httpClient = new Net.Http.HttpClient()
        let! response = httpClient.PostAsync(uriBuilder.Uri, content)
        let! responseJson = response.Content.ReadAsStringAsync()
        // responseJson contains the access token and the ID_TOKEN !

        return! Response.ofJson responseJson ctx
}

let endpoints = [
    get "/auth/sign-in" challengeGoogle
    get "/auth/sign-out" (Response.ofPlainText "Sign out")

    get "/auth/signin-google" afterChallengingUser
    // get "/auth/signin-google" (fun ctx -> task {
    //     ctx |> Auth.getUser |> printfn "%A"
    //     return Response.ofPlainText "Signed in with Google" ctx
    // })
]