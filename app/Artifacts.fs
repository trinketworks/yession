module Yession.Host.Artifacts

// The artifacts an agent has shared, on THIS filesystem: one directory per name, one file
// per version, and nothing else — the directory listing IS the version history, so there is
// no index to fall out of step with the bytes (`ArtifactRef`).
//
// Two halves meet here, and the split is deliberate. The STORE half (below) owns naming,
// the cap and the digest, and never asks anybody's permission: an artifact's address is
// this session's to mint, not a caller's to propose. The SHARE half copies bytes out of a
// sandbox, and it does that by asking the sandbox to copy them into a directory the session
// is holding open (`Sandboxes.artifactsVisibleAt`) rather than by carrying them through this
// process — a 100 MB file base64'd through a pipe would be 133 MB of string, and the only
// thing that buys is a crossing the bind mount already makes.

open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Node.Api

open Yession.App
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Content
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Domain.Artifacts
open Yession.Host.Interop
open Yession.SessionProcess

/// The most one shared file may weigh. Decimal, because `ContentSize.render` is decimal and
/// a refusal that disagrees with the number printed beside it is the worse answer.
let cap = 100_000_000L

/// The cap as a refusal says it.
let private capSaid = ContentSize.render cap

let private readdirSafe (dir: string) : string array =
    try fs.readdirSync (U2.Case1 dir) |> Array.ofSeq with _ -> [||]

let private sizeOf (path: string) : int64 option =
    try Some (int64 (fs.statSync (U2.Case1 path)).size) with _ -> None

/// Drop a half-written leaf. Quiet on purpose: this runs on the refusal path, where the
/// reason the caller is about to read is the finding and a failure to tidy up is not.
let private discard (path: string) : unit =
    try fs.unlinkSync (U2.Case1 path) with _ -> ()

/// SHA-256 of a file, streamed through the hash rather than read whole: this runs over
/// something that is allowed to be 100 MB, and the digest is an integrity fact, not a reason
/// to hold the file in memory.
let private digestOf (path: string) : ContentDigest option =
    try
        let hash = crypto.createHash "sha256"
        let fd = fs.openSync (U2.Case1 path, U2.Case1 "r")
        try
            let chunk = 1 <<< 20
            let buffer = JS.Constructors.Uint8Array.Create chunk
            // The read position is carried rather than left to the descriptor's own cursor:
            // the binding wants one, and an explicit offset is what makes this loop's
            // termination readable — it ends when a read at the end returns nothing.
            let mutable pos = 0.
            let mutable go = true
            while go do
                let read = fs.readSync (fd, unbox buffer, 0., float chunk, pos)
                if read <= 0. then go <- false
                else
                    pos <- pos + read
                    hash.update (buffer.slice (0, int read)) |> ignore
            ContentDigest.create (unbox<string> (hash.digest "hex")) |> Result.toOption
        finally
            fs.closeSync fd
    with _ -> None

/// Every version of one name, in the order `ArtifactRef` orders them. A leaf that is not a
/// version is SKIPPED rather than refused: the directory is on a filesystem, an editor or a
/// sync client can leave something in it, and a listing that failed on one stray file would
/// take the whole history down with it.
let versions (artifactsDir: string) (name: string) : ArtifactRef list =
    readdirSafe (sprintf "%s/%s" artifactsDir name)
    |> Array.toList
    |> List.choose (fun leaf -> ArtifactRef.ofLeaf name leaf |> Result.toOption)
    |> List.sortBy (fun ref -> ArtifactRef.seq ref, ArtifactStamp.value (ArtifactRef.stamp ref))

/// Every name in the store with its latest version — what the pane's list and the `artifacts`
/// query both show. A name with no readable version is not a name: an empty directory is what
/// a refused share leaves behind, and it is not something anybody shared.
let names (artifactsDir: string) : (string * ArtifactRef) list =
    readdirSafe artifactsDir
    |> Array.toList
    |> List.sort
    |> List.choose (fun name ->
        match ArtifactRef.latest (versions artifactsDir name) with
        | Some latest -> Some (name, latest)
        | None -> None)

/// Where a version's bytes sit on this filesystem. The store's own answer, so no caller ever
/// builds this path — the one that did would be the one that could build a different one.
let pathOf (artifactsDir: string) (ref: ArtifactRef) : string =
    sprintf "%s/%s/%s" artifactsDir (ArtifactRef.name ref) (ArtifactRef.leaf ref)

/// The version a share of `name` by `actor` would mint: the first when the name is new, the
/// one after the latest when it is not. Never an address that already holds bytes — an
/// artifact is immutable, and "update" means another version, not other bytes.
let nextVersion (artifactsDir: string) (name: string) (actor: ActorRef) : Result<ArtifactRef, string> =
    let stamp = ArtifactStamp.ofActor actor
    match ArtifactRef.latest (versions artifactsDir name) with
    | None -> ArtifactRef.first name stamp
    | Some latest -> ArtifactRef.next stamp latest

/// The name a share takes when the caller did not say one: the file's own, as the last segment
/// of the path it came from. So `share_artifact` of `$TMPDIR/chart.png` is `chart.png`, and the
/// extension — which is what the media type is read from — survives by default rather than by
/// the agent remembering to repeat it.
let nameOfPath (path: string) : string =
    let trimmed = path.TrimEnd '/'
    let idx = trimmed.LastIndexOf '/'
    if idx >= 0 then trimmed.Substring (idx + 1) else trimmed

/// One version as a reader sees it: what it is called, which version it is, whether it is the
/// one a bare name resolves to, and how big it is. Read from the directory each time — the
/// listing IS the history, so there is nothing here that can disagree with the bytes.
[<RequireQualifiedAccess>]
type ArtifactListing =
    { Ref : ArtifactRef
      Latest : bool
      Bytes : int64 option }

/// Every version of every name: names in name order, and within a name oldest version first —
/// the order somebody reads a history in.
let listing (artifactsDir: string) : ArtifactListing list =
    readdirSafe artifactsDir
    |> Array.toList
    |> List.sort
    |> List.collect (fun name ->
        let all = versions artifactsDir name
        let newest = ArtifactRef.latest all
        all
        |> List.map (fun ref ->
            { ArtifactListing.Ref = ref
              ArtifactListing.Latest = (newest = Some ref)
              ArtifactListing.Bytes = sizeOf (pathOf artifactsDir ref) }))

let queryName : QueryName =
    match QueryName.create "artifacts" with
    | Ok name -> name
    | Error e -> failwithf "artifacts query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "artifacts"
      Description =
        "What has been shared into this session as an artifact: every version of every name, \
         with the address to point at it by. A name is never overwritten — sharing it again \
         adds a version, and the one marked latest is what the name on its own resolves to."
      Shape =
        Rows
            [ QueryColumn.create "artifact" "artifact"
              QueryColumn.create "version" "version"
              QueryColumn.create "latest" "latest"
              QueryColumn.create "size" "size"
              QueryColumn.create "address" "address" ]
      Legend = [] }

/// Register the listing as a query — which is why there is no `list_artifacts` tool: one
/// declaration, the agent's tool and the people's settings section both off it.
let query (artifactsDir: string) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        listing artifactsDir
                        |> List.map (fun item ->
                            [ "artifact", CellText (ArtifactRef.name item.Ref)
                              "version", CellText (string (ArtifactRef.seq item.Ref))
                              "latest", CellFlag item.Latest
                              "size",
                              (match item.Bytes with
                               | Some bytes -> CellText (ContentSize.render bytes)
                               | None -> CellAbsent)
                              "address", CellText (ArtifactRef.url item.Ref) ])))
            } }

// --- What the pane is shown (the HTTP read surface) -------------------------------------------

/// Whether a path the store built still sits inside the store, after every symlink on it has
/// been followed. `ContentRef` already refuses a dot-segment and `pathOf` is the only thing
/// that builds one of these, so this is the downstream guard rather than the rule: it catches a
/// LEAF that is a symlink out — something a sandbox with the store bind-mounted into it can
/// make, and which no parse of the URL could ever see.
let private containedIn (artifactsDir: string) (path: string) : bool =
    let real (p: string) = try Some (fs.realpathSync (U2.Case1 p) |> string) with _ -> None
    match real artifactsDir, real path with
    | Some root, Some resolved -> resolved.StartsWith (root.TrimEnd '/' + "/")
    | _ -> false

/// What a browser is told about bytes it did not get from this build.
///
/// An artifact is a file somebody else chose the contents of, served from the session's OWN
/// origin, which is where every cookie and every peer token in this session lives. So the type
/// is the one the store recorded and never a sniffed one (`nosniff`), anything this build does
/// not claim to know is an `octet-stream` download rather than a guess, and the sandboxing CSP
/// is what makes an SVG — markup that can carry script, and the one image type that can —
/// inert if somebody navigates straight at it instead of letting the pane draw it.
let private headersFor (ref: ArtifactRef) (bytes: int64) =
    let name = ArtifactRef.name ref
    let contentType, disposition =
        match ContentKind.ofMediaType (ArtifactRef.mediaType ref) with
        | ContentKind.Image media -> media, sprintf "inline; filename=\"%s\"" name
        | ContentKind.Download -> "application/octet-stream", sprintf "attachment; filename=\"%s\"" name
    createObj
        [ "content-type", box contentType
          "content-length", box (string bytes)
          "content-disposition", box disposition
          "cache-control", box CachePolicy.contentVersion
          "x-content-type-options", box "nosniff"
          "content-security-policy", box "default-src 'none'; sandbox" ]

let private notFound (res: ServerResponse) =
    res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` "not found"

/// One version's bytes, piped rather than read: this is allowed to be 100 MB, and a slow
/// viewer must not cost the session a copy of it in its heap.
let private serveVersion (artifactsDir: string) (ref: ArtifactRef) (res: ServerResponse) =
    let path = pathOf artifactsDir ref
    match (if containedIn artifactsDir path then sizeOf path else None) with
    | None -> notFound res
    | Some bytes ->
        res.writeHead (200, headersFor ref bytes) |> ignore
        let file = openFileStream path
        // The head has gone out, so there is no status left to say this with: a read that
        // fails now can only end the response early, which is what a truncated body is. The
        // alternative — buffering the file to be sure of it first — is the thing streaming is
        // for.
        file.onError (fun _ -> res.destroy ())
        file.pipe res

/// The session's content surface: everything the pane can show, by path.
///
/// Cookie-gated, exactly as the query stream is, and for the same reason — an artifact is
/// session state, and everybody in the session reads it. No `?token=` leg: this answers a
/// browser fetching an `<img>`, which carries the cookie, and a second way in would be a
/// second thing to keep right on the surface that serves files.
///
/// Here rather than in `Signalling` with the event and transcript surfaces, because every
/// decision in it is about THIS store: which addresses exist, what "latest" resolves to, and
/// where the bytes are. The composition root is handed a handler, not the artifacts directory
/// and instructions.
let routes (auth: SessionAuth.Auth) (artifactsDir: string) (mount: string) : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        match SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0]) with
        | Some (SessionRoute.Content ref) ->
            if (auth.IdentityOf req).IsNone then
                res.writeHead (401, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` "unauthorized"
            else
                match ContentRef.segments ref with
                // A pinned version: these bytes, for good.
                | [ _; _; _ ] ->
                    match ArtifactRef.ofContent ref with
                    | Ok version -> serveVersion artifactsDir version res
                    | Error _ -> notFound res
                // The NAME, which resolves to whatever is latest — and answers with a redirect
                // to the address that holds it rather than with the bytes. The cursor-and-range
                // shape the event log and the transcripts already use: what a client keeps is
                // an address whose bytes cannot change under it, and what moves is never
                // cached. An `<img>` follows this without knowing versions exist.
                | [ root; name ] when root = ArtifactRef.root ->
                    match ArtifactRef.latest (versions artifactsDir name) with
                    | Some latest ->
                        let target = RelativeUrl.under mount (SessionRoute.relative (SessionRoute.Content (ArtifactRef.content latest)))
                        res.writeHead (307, createObj [ "location", box target; "cache-control", box CachePolicy.contentLatest ]) |> ignore
                        res.``end`` ""
                    | None -> notFound res
                // The content root has one directory in it so far. A `repos/…` path is a real
                // address this build cannot serve yet, and it 404s like any other.
                | _ -> notFound res
            true
        | Some _
        | None -> false

/// Sharing artifacts, as the session does it: the copy in, and the two readings that make the
/// versions addressable without one.
[<RequireQualifiedAccess>]
type SessionArtifacts =
    { /// Copy a file out of a sandbox into the store as the next version of `name` (the file's
      /// own name when none is given), and record the act. The answer is the fact that went on
      /// the log, so a caller never has to re-derive the address it minted.
      Share : ActorRef -> SandboxRef -> string -> string option -> Async<Result<ArtifactShared, string>>
      /// The versions of one name, oldest first.
      Versions : string -> ArtifactRef list
      /// Every name, with its latest version.
      Names : unit -> (string * ArtifactRef) list }

let unavailable : SessionArtifacts =
    { SessionArtifacts.Share = fun _ _ _ _ -> async { return Error "this session has nowhere to keep artifacts" }
      SessionArtifacts.Versions = fun _ -> []
      SessionArtifacts.Names = fun () -> [] }

let create
    // Where a share is recorded (`ArtifactShared`), by the verb that made it — after the bytes
    // landed and were measured, so the log never carries an artifact that is not there.
    (log: EventLog<SessionEvent>)
    (artifactsDir: string)
    (environmentFor: SandboxRef -> SessionEnvironment.SessionEnvironment)
    // The same directory as the sandbox sees it — the bind mount's target under docker, the
    // directory itself otherwise (`Sandboxes.artifactsVisibleAt`).
    (artifactsPathIn: SandboxRef -> string)
    // Where a relative source path is meant: the directory a terminal in that sandbox would
    // start in, which is the vocabulary every other file verb already answers in.
    (workingDirectoryFor: SandboxRef -> string option)
    (shell: TerminalShell)
    : SessionArtifacts =

    /// The id the fact carries, minted here as every service that appends one mints its own:
    /// an id has to come from the act, and a projection that minted one would answer
    /// differently on every re-read of the same events.
    let mintMessageId () : MessageId =
        match MessageId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "message id invariant violated: %s" e

    /// One bare spawn inside the sandbox: `script` under the shell with `args` as its positional
    /// parameters, and both streams back whole. Nothing here reads a file's CONTENT, so there is
    /// no output cap to get right — the outputs are a byte count and a complaint.
    let run (sandbox: SandboxRef) (reason: string) (script: string) (args: string list) : Async<Result<int * string * string, string>> =
        async {
            match! (environmentFor sandbox).Ensure None reason with
            | EnvironmentUnavailable reason -> return Error reason
            | EnvironmentAvailable ->
                let out = System.Text.StringBuilder ()
                let err = System.Text.StringBuilder ()
                let! spawned =
                    (environmentFor sandbox).Spawn
                        { Executable = shell.Executable
                          Arguments = shell.Arguments @ [ script; "sh" ] @ args
                          Env = Map.empty
                          WorkingDirectory = workingDirectoryFor sandbox
                          Via = Direct }
                        (fun (stream, chunk) ->
                            match stream with
                            | Stdout -> out.Append chunk |> ignore
                            | Stderr -> err.Append chunk |> ignore)
                match spawned with
                | Error reason -> return Error reason
                | Ok handle ->
                    match! handle.Exited with
                    | SandboxExited code -> return Ok (code, out.ToString (), err.ToString ())
                    | SandboxRunFailed reason -> return Error reason
        }

    /// How big the file the caller named is, asked BEFORE anything is copied: a refusal that
    /// arrives after 100 MB has been written is a refusal that already cost what it was for.
    let weigh (sandbox: SandboxRef) (path: string) : Async<Result<int64, string>> =
        async {
            match! run sandbox "an artifact was shared" "wc -c < \"$1\"" [ path ] with
            | Error reason -> return Error reason
            | Ok (0, out, _) ->
                match System.Int64.TryParse (out.Trim ()) with
                | true, bytes -> return Ok bytes
                | false, _ -> return Error (sprintf "could not tell how big %s is" path)
            | Ok (_, _, err) ->
                let said = err.Trim ()
                return
                    Error (
                        if said = "" then sprintf "%s is not a file this sandbox can read" path
                        else said)
        }

    /// The bytes, copied by the SANDBOX into the store — which it can do because the store is
    /// mounted there, and this process could not do because under docker the source is inside a
    /// container it cannot read. Landed under a `.part` leaf, so what appears at the version's
    /// own address appears whole (the rename is this side's, below).
    let copyIn (sandbox: SandboxRef) (source: string) (ref: ArtifactRef) : Async<Result<unit, string>> =
        async {
            let target = sprintf "%s/%s/%s.part" (artifactsPathIn sandbox) (ArtifactRef.name ref) (ArtifactRef.leaf ref)
            match! run sandbox "an artifact was shared" "mkdir -p -- \"$(dirname -- \"$2\")\" && cp -- \"$1\" \"$2\"" [ source; target ] with
            | Error reason -> return Error reason
            | Ok (0, _, _) -> return Ok ()
            | Ok (_, _, err) ->
                let said = err.Trim ()
                return Error (if said = "" then sprintf "copying %s into the artifacts store failed" source else said)
        }

    let share (actor: ActorRef) (sandbox: SandboxRef) (source: string) (named: string option) : Async<Result<ArtifactShared, string>> =
        async {
            let name = named |> Option.defaultValue (nameOfPath source)
            match nextVersion artifactsDir name actor with
            | Error e -> return Error e
            | Ok ref ->
                match! weigh sandbox source with
                | Error reason -> return Error reason
                | Ok bytes when bytes > cap ->
                    return
                        Error (
                            sprintf
                                "%s is %s, and an artifact may be at most %s — share a smaller cut of it, or leave it in the sandbox and say where it is"
                                source
                                (ContentSize.render bytes)
                                capSaid)
                | Ok _ ->
                    match! copyIn sandbox source ref with
                    | Error reason -> return Error reason
                    | Ok () ->
                        let landed = pathOf artifactsDir ref
                        let part = landed + ".part"
                        // What ARRIVED, measured on this side. The check above refused a file that
                        // was too big when it was asked about; this one refuses a file that is too
                        // big now — a source that grew between the two, a sandbox that copied
                        // something else — and it is the one that guards the bytes a browser will
                        // be served.
                        match sizeOf part with
                        | None -> return Error (sprintf "nothing arrived for %s" name)
                        | Some landedBytes when landedBytes > cap ->
                            discard part
                            return
                                Error (
                                    sprintf
                                        "%s grew to %s while it was being shared, past the %s an artifact may be"
                                        source
                                        (ContentSize.render landedBytes)
                                        capSaid)
                        | Some landedBytes ->
                            match digestOf part with
                            | None ->
                                discard part
                                return Error (sprintf "could not read back what was shared as %s" name)
                            | Some digest ->
                                Fs.rename part landed
                                let fact =
                                    { ArtifactShared.MessageId = mintMessageId ()
                                      ArtifactShared.Ref = ref
                                      ArtifactShared.MediaType = ArtifactRef.mediaType ref
                                      ArtifactShared.Bytes = landedBytes
                                      ArtifactShared.Digest = digest
                                      ArtifactShared.Actor = actor }
                                let! _ = log.Append actor (SessionEvent.ArtifactShared fact)
                                return Ok fact
        }

    { SessionArtifacts.Share = share
      SessionArtifacts.Versions = versions artifactsDir
      SessionArtifacts.Names = fun () -> names artifactsDir }
