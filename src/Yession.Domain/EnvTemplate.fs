namespace Yession.Domain.Sandboxes

open System

// A variable's value composed over what lies beneath it (`${env.NAME}`). Its own file, ahead of
// `Sandbox.fs`, because a sandbox's policy carries templates for its backend to resolve, and
// ahead of `Environment.fs`, whose declaration values can be one.

/// What a sandbox may ask the session's credential proxy for, by name (`${proxy.https}`). Asked
/// for, never given unasked: a declaration that names none of these is a sandbox the proxy
/// knows nothing about.
[<RequireQualifiedAccess>]
type ProxyValue =
    /// `${proxy.https}`: the URL a client is told as `HTTPS_PROXY` — this sandbox's own, with
    /// the capability that admits it.
    | Https
    /// `${proxy.ca-file}`: one file holding the proxy's authority AND every root the session
    /// trusts — a whole trust store, for a variable that replaces the client's.
    | CaFile
    /// `${proxy.ca-dir}`: a directory holding the proxy's authority alone — to ADD to a store
    /// the image already has (`${env.SSL_CERT_DIR}:${proxy.ca-dir}`).
    | CaDir

module ProxyValue =

    /// Each, as a reference writes it after `proxy.`.
    let all : (string * ProxyValue) list = [ "https", ProxyValue.Https; "ca-file", ProxyValue.CaFile; "ca-dir", ProxyValue.CaDir ]

    let name (value: ProxyValue) : string =
        all |> List.find (fun (_, v) -> v = value) |> fst

/// What the session says about itself, by name (`${session.version}`). Answered by the session
/// that starts the sandbox, from what it knows of its own process — never from the sandbox, and
/// never from a connection, so it needs nothing selected and is never refused.
[<RequireQualifiedAccess>]
type SessionValue =
    /// `${session.version}`: which build of Yession is running this session — what its
    /// `--version` says, and what its UI's footer shows (`dev`, `test` or `0.0.0-g<rev>` where
    /// a build cannot know a release number).
    | Version

module SessionValue =

    /// Each, as a reference writes it after `session.`.
    let all : (string * SessionValue) list = [ "version", SessionValue.Version ]

    let name (value: SessionValue) : string =
        all |> List.find (fun (_, v) -> v = value) |> fst

/// One piece of a variable's value that is composed rather than written out.
[<RequireQualifiedAccess>]
type TemplatePart =
    /// Text, as written.
    | Literal of string
    /// `${env.NAME}`: the value NAME would have in this sandbox without the declaration that
    /// says this — the image's own `ENV` under docker, the sandbox's baseline elsewhere, and
    /// whatever resources granted on top. Empty where it would have none.
    | Beneath of name: string
    /// `${proxy.…}`: something the session's credential proxy provides this sandbox, known
    /// once the sandbox is admitted to it — provided into the template (`provide`) before the
    /// sandbox is built, and so never left for a backend to resolve.
    | Proxy of ProxyValue
    /// `${session.…}`: something the session says about itself — answered into the template
    /// (`answer`) before the sandbox is built, like a proxy value, so a backend never sees one.
    | Session of SessionValue

/// A variable's value composed from pieces, rendered by the backend once it knows what lies
/// beneath — which for a container is only after its image has been pulled or built.
type EnvTemplate = TemplatePart list

module EnvTemplate =

    /// The namespaces this build knows. Others are refused by name, so a reference a later
    /// build would understand is never read as text this one silently kept.
    let private namespaces = [ "env.NAME"; "proxy.https"; "proxy.ca-file"; "proxy.ca-dir"; "session.version" ]

    let private isNameChar (c: char) =
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'

    /// Whether `text` asks for composition at all. A value without `${` is plain text, and
    /// stays the case it always was.
    let composes (text: string) : bool = text.Contains "${"

    /// `text` as a template: `${env.NAME}` is a reference, `$${` is a literal `${`, and
    /// anything else is text. Refuses a reference it cannot read, naming it — an unclosed
    /// one, an unknown namespace, a name no environment could carry.
    /// The connection a value lends, when the value is exactly `${<connection>.token}` — the
    /// one place a token may be named (`EnvironmentVariableRef.Lent`).
    let lent (text: string) : string option =
        let trimmed = text.Trim ()
        if trimmed.StartsWith "${" && trimmed.EndsWith ".token}" && trimmed.IndexOf '}' = trimmed.Length - 1 then
            let connection = trimmed.Substring (2, trimmed.Length - 2 - ".token}".Length)
            if connection <> "" && connection <> "env" && connection <> "proxy" && connection <> "session" && not (connection.Contains ".") then Some connection
            else None
        else None

    let parse (text: string) : Result<EnvTemplate, string> =
        let literal (acc: TemplatePart list) (piece: string) =
            match acc with
            | TemplatePart.Literal before :: rest -> TemplatePart.Literal (before + piece) :: rest
            | _ -> TemplatePart.Literal piece :: acc
        let rec go (at: int) (acc: TemplatePart list) =
            if at >= text.Length then Ok (List.rev acc)
            elif text.Substring(at).StartsWith "$${" then go (at + 3) (literal acc "${")
            elif text.Substring(at).StartsWith "${" then
                match text.IndexOf ('}', at + 2) with
                | -1 -> Error (sprintf "'%s' opens a reference at '${' that never closes with '}'" text)
                | close ->
                    let inner = text.Substring (at + 2, close - at - 2)
                    let unknown () =
                        Error (
                            sprintf
                                "'${%s}' is not a reference this build knows (it knows %s) — write '$${' for a literal '${'"
                                inner
                                (namespaces |> List.map (sprintf "${%s}") |> String.concat ", "))
                    match inner.Split ([| '.' |], 2) with
                    | [| "env"; name |] ->
                        if name <> "" && Seq.forall isNameChar name then go (close + 1) (TemplatePart.Beneath name :: acc)
                        else Error (sprintf "'${%s}' names no variable an environment could carry (letters, digits and '_')" inner)
                    | [| "proxy"; field |] ->
                        match ProxyValue.all |> List.tryFind (fun (name, _) -> name = field) with
                        | Some (_, value) -> go (close + 1) (TemplatePart.Proxy value :: acc)
                        | None -> unknown ()
                    | [| "session"; field |] ->
                        match SessionValue.all |> List.tryFind (fun (name, _) -> name = field) with
                        | Some (_, value) -> go (close + 1) (TemplatePart.Session value :: acc)
                        | None -> unknown ()
                    // A token composed into a larger value: the one place `lent` did not
                    // already take it, so the one place it is refused.
                    | [| _; "token" |] ->
                        Error (
                            sprintf
                                "'${%s}' is lent to each command for the act it runs as, and returned when the next one starts, so it can only be a variable's whole value — see 'A lent token rotates per command' in docs/GAPS.md"
                                inner
                        )
                    | [| _; _ |] -> unknown ()
                    | _ ->
                        Error (sprintf "'${%s}' is not a reference — write '${env.NAME}', or '$${' for a literal '${'" inner)
            else go (at + 1) (literal acc (string text.[at]))
        go 0 []

    /// The template as it would be written, so a declaration that crosses the command gate
    /// reads back as the file said it.
    /// Text as a template would write it: every `${` doubled. Split and joined rather than
    /// `Replace`d, because under Fable a replacement string is JavaScript's, where `$$` means
    /// one `$` — the escape would undo itself on the way out.
    let escape (text: string) : string = String.Join ("$${", text.Split ([| "${" |], System.StringSplitOptions.None))

    let render (template: EnvTemplate) : string =
        template
        |> List.map (fun part ->
            match part with
            | TemplatePart.Literal text -> escape text
            | TemplatePart.Beneath name -> sprintf "${env.%s}" name
            | TemplatePart.Proxy value -> sprintf "${proxy.%s}" (ProxyValue.name value)
            | TemplatePart.Session value -> sprintf "${session.%s}" (SessionValue.name value))
        |> String.concat ""

    /// What `template` asks the credential proxy for.
    let proxies (template: EnvTemplate) : ProxyValue list =
        template
        |> List.choose (fun part ->
            match part with
            | TemplatePart.Proxy value -> Some value
            | _ -> None)

    /// `template` with what the proxy provided written in, as text — and a reference to
    /// something it did not provide refused by name, so a sandbox is never built with a
    /// reference nobody answered.
    let provide (provided: Map<ProxyValue, string>) (template: EnvTemplate) : Result<EnvTemplate, string> =
        let rec go (acc: TemplatePart list) (parts: TemplatePart list) =
            match parts with
            | [] -> Ok (List.rev acc)
            | TemplatePart.Proxy value :: rest ->
                match Map.tryFind value provided with
                | Some text -> go (TemplatePart.Literal text :: acc) rest
                | None -> Error (sprintf "'${proxy.%s}' was asked for and not provided" (ProxyValue.name value))
            | part :: rest -> go (part :: acc) rest
        go [] template

    /// `template` with what the session says about itself written in, as text. Total: a
    /// session always knows its own values, so there is nothing to refuse.
    let answer (session: SessionValue -> string) (template: EnvTemplate) : EnvTemplate =
        template
        |> List.map (fun part ->
            match part with
            | TemplatePart.Session value -> TemplatePart.Literal (session value)
            | other -> other)

    /// The value, given what lies beneath this sandbox's declaration. A reference to a
    /// variable nothing beneath sets contributes no text — which is what a shell's `$NAME`
    /// does, and what an author appending to a search path wants of an image that never set
    /// one — so the absent case is the empty piece, not an absence passed on.
    let resolve (beneath: string -> string option) (template: EnvTemplate) : string =
        template
        |> List.map (fun part ->
            match part with
            | TemplatePart.Literal text -> [ text ]
            | TemplatePart.Beneath name -> beneath name |> Option.toList
            // Never reached with one: a proxy reference is provided into the template before
            // the sandbox is built (`provide`, in `WorkSandboxes`, which refuses the start
            // when it cannot be), so what reaches a backend holds none.
            | TemplatePart.Proxy _ -> []
            // Nor with one of these: answered (`answer`, in `WorkSandboxes`) before the build.
            | TemplatePart.Session _ -> [])
        |> List.concat
        |> String.concat ""

    /// Every template in `derived`, resolved over `beneath` — the environment the sandbox
    /// would have without them. So a variable composed over itself extends what it would
    /// otherwise have been, and one derived variable sees what lies beneath another, never
    /// what that other composed. Order-free, which is what makes it a map.
    let resolveAll (beneath: Map<string, string>) (derived: Map<string, EnvTemplate>) : Map<string, string> =
        derived |> Map.map (fun _ template -> resolve (fun name -> Map.tryFind name beneath) template)

