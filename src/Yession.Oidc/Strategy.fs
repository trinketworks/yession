namespace Yession.Oidc

/// How the provider decides WHO the human at /authorize is. A strategy is a value, not
/// a config flag: the provider core never knows which one is plugged in, so a BYO
/// integration is a second value of this type, not a change to the provider. This slice
/// ships three: deny-everything (`none`, the default), trust-localhost, and
/// trusted-headers (an operator-run authenticating proxy asserts the user in canonical
/// `x-yession-*` headers).

/// The slice of the /authorize request a strategy may inspect.
type AuthenticationContext =
    { /// The TCP peer address of the request (`socket.remoteAddress`).
      RemoteAddress : string option
      /// Query-parameter access, for strategies that carry credentials in the request.
      Query : string -> string option
      /// Request-header access (Node lowercases names), for proxy-asserted identity.
      Header : string -> string option }

/// The claims a strategy verified about the human. Subject is the only required claim;
/// Extra carries strategy-specific claims opaquely (e.g. a capabilities document) for
/// audit and future policy — nothing interprets them yet.
type UserClaims =
    { Subject : string
      DisplayName : string option
      Email : string option
      Picture : string option
      Extra : (string * string) list }

type AuthenticationOutcome =
    /// A real, durable user identity: events may be attributed to it.
    | Attributed of UserClaims
    /// A shared access grant with no attributable user behind it (trust-localhost).
    /// Access, not identity: downstream attribution falls back to the peer connection.
    | Unattributed of subject: string
    | Denied of reason: string

/// The identity a code grant carries from /authorize to /token: the subject plus the
/// claims behind it when the strategy attributed a real user (None = unattributed).
type GrantedIdentity =
    { Subject : string
      Claims : UserClaims option }

module GrantedIdentity =
    /// The grant an outcome earns; None when the outcome is a denial.
    let ofOutcome (outcome: AuthenticationOutcome) : GrantedIdentity option =
        match outcome with
        | Attributed claims -> Some { Subject = claims.Subject; Claims = Some claims }
        | Unattributed subject -> Some { Subject = subject; Claims = None }
        | Denied _ -> None

/// What a strategy's verdict rests on besides the request: the door it must have arrived
/// through for the verdict to be true. The Manager's listener answers it
/// (`ProcessManager.Listener.admits`), so a strategy and a listener that cannot be believed
/// together refuse the boot rather than authenticate anyone.
[<RequireQualifiedAccess>]
type Door =
    /// Any listener: the verdict reads nothing about the connection.
    | Any
    /// A TCP port on loopback: the verdict reads the peer address, which only a TCP
    /// connection has.
    | LoopbackPort
    /// A unix socket that only the authenticating proxy can open. The verdict reads headers,
    /// and a header is true only if nothing but the proxy could have sent it. A TCP port on
    /// loopback cannot say that: every process on the host, and every container whose
    /// network reaches the host loopback, dials it as loopback.
    | ProxyOnly

type AuthenticationStrategy =
    { Name : string
      /// The door this strategy's verdict needs.
      Door : Door
      Authenticate : AuthenticationContext -> Async<AuthenticationOutcome> }

module Strategy =

    /// Deny-everything: the default when the operator configured no strategy. Nothing
    /// authenticates until a strategy is chosen explicitly (`--auth`), so an exposed
    /// endpoint can never fall back to an unintended trust rule.
    let none : AuthenticationStrategy =
        { Name = "none"
          Door = Door.Any
          Authenticate = fun _ -> async { return Denied "no authentication strategy configured (start the Manager with --auth)" } }

    let private loopback = Set.ofList [ "127.0.0.1"; "::1"; "::ffff:127.0.0.1" ]

    /// Trust-localhost: any request arriving over the loopback interface IS the single
    /// local user (`sub = "local"`) — unattributed, because everyone on this machine
    /// shares that subject. Locality is the security boundary, made explicit here.
    let localhost : AuthenticationStrategy =
        { Name = "localhost"
          Door = Door.LoopbackPort
          Authenticate =
            fun context ->
                async {
                    match context.RemoteAddress with
                    | Some address when Set.contains address loopback ->
                        return Unattributed "local"
                    | _ ->
                        return Denied "the localhost strategy only authenticates loopback requests"
                } }

    /// The canonical identity headers an authenticating proxy asserts.
    /// The proxy in front of the Manager translates whatever its authenticator gives it
    /// (e.g. Tailscale serve identity headers) into these, and MUST strip them from
    /// inbound client requests — anything that reaches this strategy is trusted verbatim.
    /// That is why the strategy's door is `ProxyOnly`: the Manager serves it on a unix
    /// socket the proxy alone can open, never on a TCP port.
    [<Literal>]
    let SubjectHeader = "x-yession-user"
    [<Literal>]
    let NameHeader = "x-yession-user-name"
    [<Literal>]
    let EmailHeader = "x-yession-user-email"
    [<Literal>]
    let PictureHeader = "x-yession-user-picture"
    /// A JSON object of additional claims, carried verbatim into `UserClaims.Extra`
    /// (e.g. Tailscale app capabilities) — opaque to the Manager today.
    [<Literal>]
    let ClaimsHeader = "x-yession-user-claims"

    let private nonBlank (value: string option) : string option =
        value |> Option.bind (fun v -> let t = v.Trim () in if t = "" then None else Some t)

    /// An RFC 2047 "encoded word": `=?charset?encoding?text?=`. HTTP header values are
    /// meant to be US-ASCII, so an asserting proxy built on an upstream that only hands it
    /// an ASCII header falls back to the same MIME encoding email headers use for a non-ASCII
    /// name — Tailscale's `Tailscale-User-Name` does this verbatim. The deployment docs
    /// promise `x-yession-*` values are plain UTF-8, so this decodes on the way in rather
    /// than making every reader of `DisplayName` understand RFC 2047.
    let private encodedWord = System.Text.RegularExpressions.Regex (@"=\?[^?\s]+\?([BbQq])\?([^?]*)\?=")

    /// The Q encoding: quoted-printable with `_` standing for space.
    let private decodeQ (text: string) : byte[] =
        let text = text.Replace ('_', ' ')
        let bytes = ResizeArray<byte> ()
        let mutable i = 0
        while i < text.Length do
            if text.[i] = '=' && i + 2 < text.Length then
                bytes.Add (System.Convert.ToByte (text.Substring (i + 1, 2), 16))
                i <- i + 3
            else
                bytes.Add (byte text.[i])
                i <- i + 1
        bytes.ToArray ()

    /// Decodes every RFC 2047 encoded word `value` carries to UTF-8 text. A value with none
    /// is returned unchanged; a word this cannot decode is left as it was rather than
    /// dropped, because a header we misread is still better evidence than a blank one.
    let private decodeEncodedWords (value: string) : string =
        encodedWord.Replace (
            value,
            fun m ->
                try
                    let encoding, text = m.Groups.[1].Value, m.Groups.[2].Value
                    let bytes =
                        if encoding = "B" || encoding = "b" then System.Convert.FromBase64String text
                        else decodeQ text
                    System.Text.Encoding.UTF8.GetString bytes
                with _ -> m.Value)

    /// Trusted-headers: the operator's proxy authenticated the human and asserts the
    /// identity in `x-yession-*` headers. Replaces (never composes with) `localhost` —
    /// behind a loopback-terminating proxy every request is loopback, so composing
    /// would authenticate header-less requests as the local user.
    let trustedHeaders : AuthenticationStrategy =
        { Name = "trusted-headers"
          Door = Door.ProxyOnly
          Authenticate =
            fun context ->
                async {
                    match nonBlank (context.Header SubjectHeader) with
                    | None ->
                        return Denied "no x-yession-user identity header; is the authenticating proxy in front of this port?"
                    | Some subject ->
                        return
                            Attributed
                                { Subject = subject
                                  DisplayName = context.Header NameHeader |> Option.map decodeEncodedWords |> nonBlank
                                  Email = nonBlank (context.Header EmailHeader)
                                  Picture = nonBlank (context.Header PictureHeader)
                                  Extra =
                                    match nonBlank (context.Header ClaimsHeader) with
                                    | Some claims -> [ ClaimsHeader, claims ]
                                    | None -> [] }
                } }

    /// Resolve a `--auth` argument to a strategy. None (no argument) is deny-everything;
    /// an unknown name is an error the boot must fail loudly on, never a silent default.
    let ofName (name: string option) : Result<AuthenticationStrategy, string> =
        match name with
        | None -> Ok none
        | Some "none" -> Ok none
        | Some "localhost" -> Ok localhost
        | Some "trusted-headers" -> Ok trustedHeaders
        | Some other -> Error (sprintf "unknown authentication strategy '%s' (expected none, localhost or trusted-headers)" other)
