module Yession.Tests.TestHttp

// HTTP, as a test drives it.
//
// Every suite that drives a route over the wire wants the same few things back — the status,
// the body, sometimes one header — and ten of them used to bind their own `fetch` macro and
// declare their own reply type to get them. The copies had drifted: some asked for
// `cache: 'no-store'` and some did not, some defaulted a missing header to `''` inside the
// macro where nothing could read the decision, and two carried the same total-GET expression
// character for character.
//
// There are no bindings here at all now. The request is a list of `Fable.Fetch`'s own
// `RequestProperties` rather than a JavaScript object literal, the answer is its `Response`,
// and the one member that binding lacks — the whole set of headers an answer carried, which
// `Headers` will iterate and Fable.Fetch never declares — is `Fable.NodeExtras`, beside the
// response body it lacks for the same reason. What was logic — choosing the request,
// defaulting a header, deciding whether an answer counts as success — is F#.

open Fable.Core
open Fetch
open Fable.NodeExtras
open Yession.Host

/// What one round trip answered.
type Reply =
    { Status : int
      Body : string
      Url : string
      Headers : Map<string, string> }

/// A header's value, or nothing where the answer carried none. An OPTION rather than the
/// `|| ''` each macro used to bury: "absent" and "sent empty" are different answers about
/// a cache directive, and the case that asks is the one entitled to conflate them.
let header (name: string) (reply: Reply) : string option = Map.tryFind name reply.Headers

/// A header the case is ABOUT, which an answer carrying none fails by name. That is the
/// one thing the `|| ''` these macros buried could not do: a missing cache directive
/// arrived as `""` and failed as a mismatch against the empty string.
let requiredHeader (name: string) (reply: Reply) : string =
    match header name reply with
    | Some value -> value
    | None -> failwithf "the answer from %s carried no %s header" reply.Url name

/// The 2xx question `Response.ok` answers.
let ok (reply: Reply) : bool = reply.Status >= 200 && reply.Status < 300

/// The headers a case names, as the request property that carries them. `Custom` is the
/// case for a name Fable.Fetch does not enumerate, which is most of the ones a route of
/// ours is asked about (`x-yession-*`), and it erases to the same pair a literal held.
let private headersOf (headers: (string * string) list) : RequestProperties =
    requestHeaders [ for name, value in headers -> HttpRequestHeaders.Custom (name, box value) ]

/// `fetchUnsafe` rather than `fetch`, and the difference is the whole point of this helper:
/// Fable.Fetch's `fetch` RAISES on a status outside 2xx, which is a reasonable default for a
/// client that wanted the body and a wrong one for a suite whose subject IS the status. Half
/// the cases here assert a 307, a 401 or a 404 — `ok` below is what asks the 2xx question,
/// once, where a case can decide what the answer means.
let private send (props: RequestProperties list) (url: string) : Async<Reply> =
    async {
        let! response = fetchUnsafe url props |> Interop.awaitPromise
        let! body = response.text () |> Interop.awaitPromise
        return
            { Status = response.Status
              Body = body
              Url = response.Url
              Headers = Map.ofArray (headerPairs response.Headers) }
    }

/// A GET.
let get (url: string) : Async<Reply> = send [] url

/// A GET with headers, and the runtime told not to answer from a kept copy: a case that
/// asks twice means to ask twice.
let getNoStore (headers: (string * string) list) (url: string) : Async<Reply> =
    send [ RequestProperties.Cache RequestCache.Nostore; headersOf headers ] url

/// A GET with headers, and a redirect left UNFOLLOWED, so the route's own answer is
/// observable rather than the answer of whatever it points at. The headers are here because
/// a cookie-gated surface that redirects (the content surface) would otherwise need a second
/// spelling of this one request.
let getUnredirected (headers: (string * string) list) (url: string) : Async<Reply> =
    send [ RequestProperties.Redirect RedirectMode.Manual; headersOf headers ] url

/// A POST under `contentType`, with whatever other headers the route requires.
let post (headers: (string * string) list) (contentType: string) (body: string) (url: string) : Async<Reply> =
    send
        [ RequestProperties.Method HttpMethod.POST
          headersOf (("content-type", contentType) :: headers)
          RequestProperties.Body (BodyInit.Case3 body) ]
        url

/// A POST with its redirect left UNFOLLOWED: where a route points a browser next is the
/// contract, and an auto-following fetch would swallow it and assert the destination.
let postUnredirected (contentType: string) (body: string) (url: string) : Async<Reply> =
    send
        [ RequestProperties.Method HttpMethod.POST
          RequestProperties.Redirect RedirectMode.Manual
          headersOf [ "content-type", contentType ]
          RequestProperties.Body (BodyInit.Case3 body) ]
        url

let postJson (body: string) (url: string) : Async<Reply> = post [] "application/json" body url

let postForm (body: string) (url: string) : Async<Reply> =
    post [] "application/x-www-form-urlencoded" body url

/// A GET that answers even when the request never got one — a refused connection, a name
/// that does not resolve. `Error` carries what went wrong as text, which is what the
/// `String(e)` in the macros this replaces was for.
let attempt (url: string) : Async<Result<Reply, string>> =
    async {
        try
            let! reply = get url
            return Ok reply
        with ex ->
            return Error ex.Message
    }
