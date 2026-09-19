module Yession.Host.Assets

// The static asset service: the files a build ships, read once and served by path, by whichever
// process is running. Both bins use it — the Session Process for the client shell, the Manager
// for its own page — and neither one names a single asset.
//
// That ignorance is the point. A session may be upgraded on its own (`--spawn-bin`
// points at a floating install, Plan 11) and can then bring a typeface, a stylesheet, an image
// that the Manager beside it has never heard of. Nothing here has to learn about it: the
// directory the build left is the whole contract, and the route (`SessionRoute.Asset`) names a
// path and nothing else.
//
// The digest addresses the SET, not each file. One address for the whole directory is what lets
// a stylesheet reference a face by a plain relative name — `url(fonts/x.woff2)` inside
// `assets/<build>/app.css` resolves to `assets/<build>/fonts/x.woff2` — so nothing rewrites CSS
// at build time and nothing has to agree about naming. Immutability holds all the same: any
// byte of any file changes the digest, which changes every address in the set.

open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Node.Api
open Node.Buffer
open Yession.App
open Yession.Host.Interop

[<Import("fileURLToPath", "node:url")>]
let private fileUrlToPath (url: obj) : string = jsNative

/// The packaged location: `assets/` beside the running bundle. `import.meta.url` resolves to
/// the module, which is the package root once esbuild has flattened everything into one file.
[<Emit("new URL('./assets', import.meta.url)")>]
let private packagedAssets : obj = jsNative

/// Why a root did not yield a declared file.
///
/// Two facts wearing one answer is how a permissions fault came to read as "run `build`". They
/// are told apart by the errno Node puts on the error, and only `ENOENT` is an absence: every
/// other one — `EACCES`, `EISDIR`, `EIO`, `EPERM` — says the path is THERE and this process
/// cannot have it.
[<RequireQualifiedAccess>]
type private Missing =
    /// Not there. The un-built case, and the only reason to try the next root.
    | Absent
    /// There and unreadable — a permission, a directory where a file belongs, a mount that
    /// went away. No later root answers for this, because nothing about it is about absence.
    | Unreadable of path: string * reason: string

/// The errno a Node filesystem error carries, or nothing for a failure that is not one of
/// Node's.
///
/// An option rather than `""`, because the two absences here — a throw with no error at all,
/// and an error naming no errno — are both "this fault has no errno", and a reader that has to
/// tell an errno from a hole must be given one it cannot mistake. `ErrnoException` is
/// `Fable.Node`'s name for the shape Node gives such an error, and `code` is read through it
/// rather than through `($0.code || null)` — with the one case that `||` folded in written
/// out: an errno spelled `''` names no more of a fault than a missing one does.
let private errnoOf (error: exn) : string option =
    if isNullOrUndefined (box error) then
        None
    else
        match (unbox<Node.Base.ErrnoException> (box error)).code with
        | None | Some "" -> None
        | code -> code

/// One declared file's bytes from ONE root, or why that root did not have them.
///
/// Bytes, not text: a set holds woff2 as readily as CSS, and `utf8` would mangle it.
let private readFrom (root: string) (path: string) : Result<Buffer, Missing> =
    let full = root + "/" + path
    try Ok (fs.readFileSync full)
    with error ->
        match errnoOf error with
        | Some "ENOENT" -> Error Missing.Absent
        | _ -> Error (Missing.Unreadable (full, error.Message))

/// One declared file's bytes, from the package's `assets/` when installed and the build output
/// when developing.
///
/// A root that does not HAVE the file is not a failure, it is the other root's turn, and both
/// roots not having it is `Missing.Absent` — the un-built case, which `load` boots as an empty
/// set rather than a crash. A root that has it and cannot read it stops there: the next root
/// cannot speak to a fault that is not about absence, and finding the file elsewhere would
/// leave a broken deployment serving as if it were whole.
let private readFile (packaged: string) (fallback: string) (path: string) : Result<Buffer, Missing> =
    let rec pick roots =
        match roots with
        | [] -> Error Missing.Absent
        | root :: rest ->
            match readFrom root path with
            | Error Missing.Absent -> pick rest
            | answer -> answer
    pick [ packaged; fallback ]

/// One digest over the whole set: every path and every byte, in the map's own (sorted) order,
/// so the same set always addresses the same. Twelve base64url characters, the same content
/// address `Interop.contentDigest` gives a document.
let private digestEntries (entries: (string * Buffer) array) : string =
    let hash = crypto.createHash "sha256"
    for path, bytes in entries do
        hash.update path |> ignore
        hash.update bytes |> ignore
    // The digest as bytes, encoded here rather than by `digest(encoding)` — which Fable.Node
    // types as returning `obj`, so the string would be an unchecked cast away.
    (hash.digest().toString base64url).Substring (0, 12)

/// What a build ships, and the one address it all sits under.
type AssetSet =
    { Build: AssetBuild
      Files: Map<string, Buffer> }

/// Read the asset set once, at boot. Per process, never per request: the addresses a shell
/// hands out have to be the addresses this process will answer for, and a re-read could drift
/// from the document that named them.
///
/// The DECLARED set is what makes a short one readable: a file no root has was never built, and
/// an empty set is the developer case `serve` names. So absence boots, and a declared file that
/// is there and unreadable does NOT — it is a fault in this deployment, and a Manager serving a
/// blank shell while telling whoever looks to run `build` is an empty set standing in for a
/// permissions fault. The refusal names the file and what the OS said about it, which is the
/// only place that diagnosis still exists.
let load (fallbackDir: string) : AssetSet =
    let packaged = fileUrlToPath packagedAssets
    let files =
        AssetFile.all
        |> List.choose (fun file ->
            match readFile packaged fallbackDir (AssetFile.path file) with
            | Ok bytes -> Some (AssetFile.path file, bytes)
            | Error Missing.Absent -> None
            | Error (Missing.Unreadable (path, reason)) ->
                failwithf
                    "declared asset %s is there and unreadable at %s: %s"
                    (AssetFile.path file)
                    path
                    reason)
        |> Map.ofList
    { Build = AssetBuild (digestEntries (Map.toArray files))
      Files = files }

/// The set this process serves: the build output, or wherever `YESSION_ASSETS` says an
/// operator has put it. Read HERE, once, rather than by each server that wants one — a second
/// reader is a second default, and a deployment that gave the two of them different answers
/// would serve a stylesheet from one build beside the faces of another. One variable for the
/// whole directory, for the same reason `load` addresses the set rather than each file.
let configured () : AssetSet = load (envOr "YESSION_ASSETS" "app/out/public/assets")

/// Serve one file, but only at this build's address.
///
/// A `build` that is not ours is a stale document asking for a set this process no longer
/// ships, and it gets the same 404 an unknown path does — which sends the browser back to the
/// shell, where it revalidates and learns the set that does exist. Answering with CURRENT bytes
/// at an OLD address would write them into an `immutable` cache entry: wrong for a year, and
/// unfixable from the server.
let serve (assets: AssetSet) (build: string) (path: string) (res: ServerResponse) =
    let found =
        if AssetBuild build = assets.Build then
            AssetFile.ofPath path
            |> Option.bind (fun file -> Map.tryFind path assets.Files |> Option.map (fun bytes -> file, bytes))
        else None
    match found with
    | Some (file, bytes) ->
        res.writeHead (
            200,
            createObj
                [ "content-type", box (AssetFile.contentType file)
                  "cache-control", box CachePolicy.asset ])
        |> ignore
        res.``end`` (unbox bytes)
    | None ->
        res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
        // An EMPTY set is the developer case — nothing was built — and saying so beats a bare
        // 404 on an address that looks perfectly reasonable.
        res.``end`` (if Map.isEmpty assets.Files then "not built (run: build)" else "stale asset address (reload)")
