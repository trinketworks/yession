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
// The bindings below are one Fetch API member each. What was logic — choosing the request,
// defaulting a header, deciding whether an answer counts as success — is F#.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Host

[<Emit("fetch($0, $1)")>]
let private fetchWith (url: string) (init: obj) : JS.Promise<obj> = jsNative

[<Emit("$0.status")>]
let private statusOf (response: obj) : int = jsNative

/// The URL the answer came from: the last one, where a redirect was followed.
[<Emit("$0.url")>]
let private urlOf (response: obj) : string = jsNative

/// Every response header, as the pairs `Headers` iterates. Names arrive lowercased.
[<Emit("[...$0.headers]")>]
let private headerPairs (response: obj) : (string * string)[] = jsNative

[<Emit("$0.text()")>]
let private bodyOf (response: obj) : JS.Promise<string> = jsNative

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

let private headerObj (headers: (string * string) list) : obj =
    Fable.Core.JsInterop.createObj [ for name, value in headers -> name, box value ]

let private send (init: (string * obj) list) (url: string) : Async<Reply> =
    async {
        let! response = fetchWith url (Fable.Core.JsInterop.createObj init) |> Interop.awaitPromise
        let! body = bodyOf response |> Interop.awaitPromise
        return
            { Status = statusOf response
              Body = body
              Url = urlOf response
              Headers = Map.ofArray (headerPairs response) }
    }

/// A GET.
let get (url: string) : Async<Reply> = send [] url

/// A GET with headers, and the runtime told not to answer from a kept copy: a case that
/// asks twice means to ask twice.
let getNoStore (headers: (string * string) list) (url: string) : Async<Reply> =
    send [ "cache", box "no-store"; "headers", headerObj headers ] url

/// A GET with a redirect left UNFOLLOWED, so the route's own answer is observable rather
/// than the answer of whatever it points at.
let getUnredirected (url: string) : Async<Reply> = send [ "redirect", box "manual" ] url

/// A POST under `contentType`, with whatever other headers the route requires.
let post (headers: (string * string) list) (contentType: string) (body: string) (url: string) : Async<Reply> =
    send
        [ "method", box "POST"
          "headers", headerObj (("content-type", contentType) :: headers)
          "body", box body ]
        url

/// A POST with its redirect left UNFOLLOWED: where a route points a browser next is the
/// contract, and an auto-following fetch would swallow it and assert the destination.
let postUnredirected (contentType: string) (body: string) (url: string) : Async<Reply> =
    send
        [ "method", box "POST"
          "redirect", box "manual"
          "headers", headerObj [ "content-type", contentType ]
          "body", box body ]
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
