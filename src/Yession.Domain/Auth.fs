namespace Yession.Domain.Access

open Yession.Domain
open Yession.Domain.Link
open System

/// The schemes a browser may send a session's auth cookie over.
[<RequireQualifiedAccess>]
type CookieScheme =
    /// `Secure`: the session is served over https, so the cookie never rides plain HTTP. A
    /// browser drops a `Secure` cookie set over http, so this is only right where the session's
    /// own address is https.
    | HttpsOnly
    /// No `Secure`: the session is served over plain HTTP, which is loopback (`--auth
    /// localhost`) or a deployment that fronts its sessions at an `http://` template.
    | AnyScheme

/// The words of a sign-in that the page does in the background, with no navigation.
///
/// A session keeps its sign-ins for one launch. The page of a session that relaunched opens
/// from the browser's own copy and finds itself signed out. A navigation to the sign-in loads
/// the page a second time. In the background the page asks three times and stays where it is:
///
/// 1. The session, for the address of the Manager's authorize endpoint (`loginQuery`).
/// 2. The Manager, at that address. The address asks for the code in the body of the answer
///    and not in a redirect (`responseMode`), so the page can read it. This request goes from
///    the session's origin to the Manager's, and the Manager answers it with the CORS headers
///    for the one origin it registered for that session.
/// 3. The session's callback, with that body as its query. The session sets the cookie.
///
/// A redirect chain cannot do this across origins: after a second cross-origin hop the
/// browser sends `Origin: null`, and no honest CORS answer exists for that.
module BackgroundSignIn =

    /// The query that asks the session's login route for the authorize address as text.
    let loginQuery = "mode=background"

    /// The name and the value of the authorize parameter that asks for the code in the body.
    let responseModeName = "response_mode"
    let responseMode = "body"

    /// The body of the Manager's answer, which is also the query of the callback.
    let grant (encode: string -> string) (code: string) (state: string) : string =
        sprintf "code=%s&state=%s" (encode code) (encode state)

/// Pure HTTP authorization primitives: cookie and form parsing for the session's
/// auth-gated surfaces. Deliberately small — the protocol itself (JWT, PKCE, discovery,
/// the code exchange) lives in the standard `jose` / `openid-client` libraries; only the
/// plumbing that gates plain `node:http` requests is hand-written, and it is pure so the
/// cheap test tier covers it.
module Cookies =

    /// Parse a `Cookie:` request header into (name, value) pairs. Tolerant: malformed
    /// segments are dropped, values keep any embedded `=`.
    let parse (header: string) : (string * string) list =
        header.Split ';'
        |> Array.toList
        |> List.choose (fun segment ->
            let segment = segment.Trim ()
            match segment.IndexOf '=' with
            | index when index > 0 -> Some (segment.Substring (0, index), segment.Substring (index + 1))
            | _ -> None)

    /// Find a cookie by name in an optional `Cookie:` header.
    let tryFind (name: string) (header: string option) : string option =
        header
        |> Option.bind (fun h -> parse h |> List.tryPick (fun (k, v) -> if k = name then Some v else None))

    /// Which schemes this deployment's session cookies may ride: `Secure` exactly where the
    /// sessions are served over https. Read off the session template rather than the
    /// Manager's origin, because the session's own response sets the cookie, and a browser
    /// keeps a `Secure` cookie only from an https response.
    let schemeFor (access: PublicAccess) : CookieScheme =
        if (PublicAccess.sessions access |> SessionTemplate.value).StartsWith "https://" then
            CookieScheme.HttpsOnly
        else
            CookieScheme.AnyScheme

    /// A `Set-Cookie:` value for an HttpOnly session cookie, scoped to the path the
    /// session is served under (`""` at an origin root ⇒ `Path=/`). No `Max-Age`/`Expires`:
    /// the cookie lives with the browser session, matching the server side (auth state is
    /// in-memory and dies with the process).
    ///
    /// Scoping is a real narrowing where sessions share a host: a path-mounted session's
    /// cookie is no longer sent to its siblings. At an origin root the behaviour is
    /// unchanged, and the id in the cookie's NAME still carries the separation cookies
    /// cannot get from a port.
    let set (scheme: CookieScheme) (name: string) (mount: string) (value: string) : string =
        let path = if mount = "" then "/" else mount + "/"
        let secure =
            match scheme with
            | CookieScheme.HttpsOnly -> "; Secure"
            | CookieScheme.AnyScheme -> ""
        sprintf "%s=%s; Path=%s; HttpOnly; SameSite=Lax%s" name value path secure

    /// The session's auth-cookie name. Cookies on 127.0.0.1 are NOT port-scoped — the
    /// Manager and every Session share one browser cookie jar — so each session's
    /// cookie must be namespaced by its id to avoid clobbering.
    let sessionCookieName (sessionId: SessionId) : string =
        "yession_auth_" + SessionId.value sessionId

/// `application/x-www-form-urlencoded` bodies, read and written (the token-endpoint request
/// format, and the shape every form this repository posts arrives in).
module Form =

    let parse (body: string) : Map<string, string> =
        body.Split '&'
        |> Array.toList
        |> List.choose (fun pair ->
            match pair.IndexOf '=' with
            | index when index > 0 ->
                let decode (s: string) = Uri.UnescapeDataString (s.Replace ("+", " "))
                Some (decode (pair.Substring (0, index)), decode (pair.Substring (index + 1)))
            | _ -> None)
        |> Map.ofList

    /// The same format, written — beside the reader because the two are each other's inverse,
    /// and a format read here and written somewhere else is a format with two authors. It had
    /// two: the broker escaped its own parameters into a query string, and the probe reached
    /// past F# for the platform's `URLSearchParams` in an `[<Emit>]` nothing could contradict.
    ///
    /// Names are escaped as well as values, which the broker's copy did not bother with — its
    /// names are the standard ones and escape to themselves — because a writer that only
    /// escapes half of a pair is a writer whose next caller supplies the other half.
    ///
    /// `EscapeDataString` spells a space `%20` rather than `+`; `parse` reads either.
    let encode (fields: (string * string) list) : string =
        fields
        |> List.map (fun (name, value) ->
            sprintf "%s=%s" (Uri.EscapeDataString name) (Uri.EscapeDataString value))
        |> String.concat "&"
