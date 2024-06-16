namespace Behide.OnlineServices.Auth.Common

module Response =
    open Falco
    open Microsoft.AspNetCore.Http

    let clearAllCookies (ctx: HttpContext) =
        ctx
        |> Request.getCookie
        |> _.Keys
        |> Seq.iter ctx.Response.Cookies.Delete

        ctx