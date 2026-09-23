namespace Yession.Domain.Content

open System
open Yession.Domain

/// What the session can SHOW: a path under its content root, and the artifacts that are one
/// directory of it.
///
/// The side pane used to be the terminal panel, and a terminal was the only thing it could
/// hold. An image an agent made is the same act of showing — so the unit the pane takes is a
/// path, not an artifact, and `artifacts/` is one directory in an address space that already
/// has another (`repos/`). That is the whole reason this type is not called `ArtifactPath`:
/// browsing a repo file later is this type with a different first segment, not a second
/// concept with its own route, its own chip and its own tab case.
///
/// The root itself is the host's to know (it is the default sandbox's workspace); nothing here
/// names a host path, which is what lets the client hold these values too.

/// A path under the session's content root, as segments — relative, always, and already
/// checked: no empty segment, no `.`/`..`, nothing starting with a dot, and no character that
/// would need escaping in the URL a chip links to. So a reader that holds one has nothing left
/// to validate, and the containment check at the file boundary is a second guard against a
/// different fault rather than the only one.
type ContentRef = private ContentRef of segments: string list

module ContentRef =

    /// How an agent writes a reference in a message, and the one spelling `EntityRef.said`
    /// uses: a `file:` URL rooted at the content root rather than at a disk. Three slashes
    /// because the authority is empty — the session IS the host.
    [<Literal>]
    let urlPrefix = "file:///"

    /// Segment and depth bounds. Generous for a repo path (`repos/owner/repo/src/A.fs` is
    /// five) and short of what any filesystem refuses.
    let private segmentCap = 120
    let private depthCap = 12

    let private isNameChar (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
        || c = '-' || c = '_' || c = '.'

    /// One legal segment. A leading dot is refused with the dot-segments, and for the same
    /// reason: a name the pane would show and a name that traverses out of the root should not
    /// be distinguishable only by how carefully the next reader looks.
    let private segmentOk (s: string) =
        s <> "" && s.Length <= segmentCap && s.[0] <> '.' && String.forall isNameChar s

    /// Parse a path, or the `file:///` URL of one. A leading `/` is accepted and dropped: a
    /// reference is always relative to the content root, so an absolute-looking path means the
    /// same thing rather than something this could not express.
    let create (raw: string) : Result<ContentRef, string> =
        let trimmed = raw |> Option.ofObj |> Option.map (fun r -> r.Trim ()) |> Option.defaultValue ""
        let path = if trimmed.StartsWith urlPrefix then trimmed.Substring urlPrefix.Length else trimmed
        let path = path.TrimStart '/'
        if path = "" then Error "a content path cannot be empty"
        else
            let segments = path.Split '/' |> List.ofArray
            if List.length segments > depthCap then
                Error (sprintf "'%s' is deeper than content goes (at most %d segments)" path depthCap)
            elif segments |> List.forall segmentOk then Ok (ContentRef segments)
            else
                Error (
                    sprintf
                        "'%s' is not a content path: segments of letters, digits, '-', '_' and '.', none starting with a dot"
                        path
                )

    let segments (ContentRef s) = s

    /// The canonical relative path — the codec's wire form, and what a route carries.
    let value (ContentRef s) = String.concat "/" s

    /// The URL form: what an agent writes in a message and what a chip's hook carries.
    let url (ref: ContentRef) : string = urlPrefix + value ref

    /// Which directory of the content root this is from — `artifacts`, and `repos` when repo
    /// browsing arrives. Total: a ref always has a first segment.
    let root (ContentRef s) = List.head s

    /// The last segment: the file as a person names it.
    let fileName (ContentRef s) = List.last s

    /// A child of this path, by one segment — checked like any other path.
    let child (name: string) (ref: ContentRef) : Result<ContentRef, string> =
        create (value ref + "/" + name)

/// The media type of a piece of content, decided by NAME.
///
/// By name and not by sniffing the bytes: the store records what it wrote, and a reader that
/// re-decides from content would be a second opinion about a fact already on the log. Sniffing
/// belongs where the bytes are (it can refuse a name that lies), not on every render.
module ContentMedia =

    /// What a name's extension means, for the types a session actually shows. `None` is "not a
    /// type this build claims to know", which is a download — never a guess.
    let ofName (name: string) : string option =
        let name = if isNull (box name) then "" else name
        let idx = name.LastIndexOf '.'
        let ext = if idx <= 0 || idx = name.Length - 1 then "" else name.Substring(idx + 1).ToLowerInvariant()
        match ext with
        | "png" -> Some "image/png"
        | "jpg" | "jpeg" -> Some "image/jpeg"
        | "gif" -> Some "image/gif"
        | "webp" -> Some "image/webp"
        | "svg" -> Some "image/svg+xml"
        | "avif" -> Some "image/avif"
        | _ -> None

    let ofRef (ref: ContentRef) : string option = ofName (ContentRef.fileName ref)

/// What the pane can MAKE of a piece of content. Two cases now and a third (text, a repo file)
/// when it is needed: a kind the pane cannot draw is a download, which is the fallback that
/// keeps "unsupported" from meaning "broken".
[<RequireQualifiedAccess>]
type ContentKind =
    | Image of mediaType: string
    | Download

module ContentKind =

    let ofMediaType (mediaType: string option) : ContentKind =
        match mediaType with
        | Some m when m.StartsWith "image/" -> ContentKind.Image m
        | Some _ | None -> ContentKind.Download

/// A size as a person reads it. Decimal units, because the cap a refusal quotes is decimal
/// ("100 MB") and a refusal that disagrees with the number beside it is the worse answer.
module ContentSize =

    let render (bytes: int64) : string =
        let scaled (unit: string) (divisor: float) =
            let n = float bytes / divisor
            if n >= 100.0 then sprintf "%.0f %s" n unit
            elif n >= 10.0 then sprintf "%.1f %s" n unit
            else sprintf "%.2f %s" n unit
        if bytes < 1_000L then sprintf "%d bytes" bytes
        elif bytes < 1_000_000L then scaled "kB" 1_000.0
        elif bytes < 1_000_000_000L then scaled "MB" 1_000_000.0
        else scaled "GB" 1_000_000_000.0

/// The digest of the bytes a version holds: SHA-256, lowercase hex. An integrity fact on the
/// log, and how a re-share of identical bytes is recognised as one.
type ContentDigest = private ContentDigest of string

module ContentDigest =

    let private isHex (c: char) = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

    let create (raw: string) : Result<ContentDigest, string> =
        let s = raw |> Option.ofObj |> Option.map (fun r -> r.Trim().ToLowerInvariant()) |> Option.defaultValue ""
        if s.Length = 64 && String.forall isHex s then Ok (ContentDigest s)
        else Error "a digest is 64 hex characters (SHA-256)"

    let value (ContentDigest s) = s

/// What distinguishes two versions of one artifact minted at the same sequence number.
///
/// Sharing is concurrent — two peers, or a peer and the agent, can both write version 3 of
/// `chart.png` before either sees the other — and the loser of that race must still be
/// addressable rather than overwritten. So the leaf carries a stamp as well as the number, and
/// the pair orders totally.
///
/// It is a HASH of the actor's token, not the token itself: an actor token holds a `:` and a
/// peer id comes from a browser, so putting one in a path would mean either a path escaping
/// rule or a refusal on an identity this session does not control. A collision between two
/// actors is harmless — it costs the loser one retry at the next sequence number — while WHO
/// shared a version is on the event, which is where identity belongs.
type ArtifactStamp = private ArtifactStamp of string

module ArtifactStamp =

    [<Literal>]
    let private prime = 16777619u

    /// `h * 16777619` mod 2^32, computed in 16-bit halves.
    ///
    /// NOT `h * prime`. Under Fable a `uint32` is a JS number and that product runs past 2^53,
    /// where a double stops being exact — so the same actor hashed in a browser and on the host
    /// came out two different stamps, which is two leaves for one version and a test that only
    /// fails on whichever platform it was not pinned from. Split this way every intermediate
    /// stays under 2^40 and both platforms agree exactly; the low 16 bits of a product survive
    /// .NET's own wrap, so the mask is the same arithmetic either side.
    let private scale (h: uint32) : uint32 =
        let lo = h &&& 0xFFFFu
        let hi = (h >>> 16) &&& 0xFFFFu
        ((lo * prime) + (((hi * prime) &&& 0xFFFFu) <<< 16)) &&& 0xFFFFFFFFu

    /// FNV-1a over the actor's token, low 24 bits, lowercase hex.
    let ofActor (actor: ActorRef) : ArtifactStamp =
        let mutable hash = 2166136261u
        for c in ActorRef.token actor do
            hash <- scale (hash ^^^ uint32 c)
        ArtifactStamp (sprintf "%06x" (int (hash &&& 0xFFFFFFu)))

    /// A stamp read back off a path. Lowercase only, and case is NOT folded: a leaf is a file
    /// name, so accepting `7F2A91` would hand back a ref that renders as `7f2a91` and points at
    /// something else. Nobody types a stamp — `ofActor` mints every one — so there is no
    /// convenience here worth a ref that does not address its own bytes.
    let create (raw: string) : Result<ArtifactStamp, string> =
        let s = raw |> Option.ofObj |> Option.map (fun r -> r.Trim()) |> Option.defaultValue ""
        if s.Length = 6 && String.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) s then
            Ok (ArtifactStamp s)
        else Error "an artifact stamp is 6 lowercase hex characters"

    let value (ArtifactStamp s) = s

/// One VERSION of one artifact — the thing a chip points at and the pane shows.
///
/// Artifacts are immutable: an update does not replace bytes, it adds a version, and the old
/// address keeps answering forever. The name is a DIRECTORY and each version is a file in it
/// (`artifacts/chart.png/0003-7f2a91`), which makes listing the directory the version history —
/// no index file to fall out of sync with the bytes, and no "latest" pointer to be wrong.
///
/// The extension lives on the directory, because the name is what a person says and what the
/// media type is read from; the leaf is a version, not a file name a human chose.
///
/// Two addresses, then, and they mean different things: `artifacts/chart.png` is whatever is
/// latest, resolved when it is asked for, and `artifacts/chart.png/0003-7f2a91` is these bytes
/// for good. The first is what an agent writes in a message; the second is what a served
/// response may be cached as immutable.
type ArtifactRef =
    private
        { Name : string
          Seq : int
          Stamp : ArtifactStamp }

module ArtifactRef =

    /// The one directory of the content root artifacts live in.
    [<Literal>]
    let root = "artifacts"

    let private leafOf (seq: int) (stamp: ArtifactStamp) = sprintf "%04d-%s" seq (ArtifactStamp.value stamp)

    /// Zero-padded to four digits so a directory listing sorts by version without parsing, and
    /// capped there: a name somebody shares ten thousand versions of has a different problem.
    let private seqCap = 9999

    /// A name, checked: the content-path rules, and ONE segment. An artifact is named, not
    /// placed — a name with a slash in it would be a caller choosing a directory layout this
    /// type owns.
    let private validName (name: string) : Result<string, string> =
        match ContentRef.create name with
        | Error e -> Error e
        | Ok parsed when List.length (ContentRef.segments parsed) <> 1 ->
            Error (sprintf "'%s' is an artifact's name, not a path" name)
        | Ok parsed -> Ok (ContentRef.value parsed)

    /// A version of a named artifact.
    let create (name: string) (seq: int) (stamp: ArtifactStamp) : Result<ArtifactRef, string> =
        if seq < 0 || seq > seqCap then
            Error (sprintf "%d is not a version of an artifact (0 to %d)" seq seqCap)
        else validName name |> Result.map (fun name -> { Name = name; Seq = seq; Stamp = stamp })

    /// The first version: what a share that names something new mints.
    let first (name: string) (stamp: ArtifactStamp) : Result<ArtifactRef, string> = create name 0 stamp

    let name (ref: ArtifactRef) = ref.Name
    let seq (ref: ArtifactRef) = ref.Seq
    let stamp (ref: ArtifactRef) = ref.Stamp

    /// Whether this is the version that first put the name there — the difference between a
    /// share and an update, read off the ref rather than carried beside it where the two could
    /// disagree.
    let isFirst (ref: ArtifactRef) = ref.Seq = 0

    /// The version after this one, by whoever is writing it. A `Result` because the cap is
    /// real: the answer to a name with ten thousand versions is a refusal, not a fifth digit
    /// that would sort before the rest.
    let next (stamp: ArtifactStamp) (ref: ArtifactRef) : Result<ArtifactRef, string> =
        create ref.Name (ref.Seq + 1) stamp

    /// The leaf that holds these bytes.
    let leaf (ref: ArtifactRef) = leafOf ref.Seq ref.Stamp

    /// This exact version's address. Constructed rather than parsed: `create` already put the
    /// name through the path rules.
    let content (ref: ArtifactRef) : ContentRef = ContentRef [ root; ref.Name; leafOf ref.Seq ref.Stamp ]

    /// The artifact's own address — the directory, which is also the address that resolves to
    /// whatever is latest.
    let directory (ref: ArtifactRef) : ContentRef = ContentRef [ root; ref.Name ]

    /// The named artifact's address without a version in hand: what a tool answers with and
    /// what an agent writes in a message.
    let directoryOf (name: string) : Result<ContentRef, string> =
        validName name |> Result.map (fun name -> ContentRef [ root; name ])

    /// The pinned URL — one version, for good.
    let url (ref: ArtifactRef) : string = ContentRef.url (content ref)

    /// The media type the name implies, which is what the pane will make of it.
    let mediaType (ref: ArtifactRef) : string option = ContentMedia.ofName ref.Name

    /// A version read back off a directory listing: the name it was found under, and its leaf.
    let ofLeaf (name: string) (leaf: string) : Result<ArtifactRef, string> =
        let idx = leaf.IndexOf '-'
        if idx <> 4 then Error (sprintf "'%s' is not an artifact version" leaf)
        else
            let digits = leaf.Substring (0, 4)
            match Int32.TryParse digits with
            | false, _ -> Error (sprintf "'%s' is not an artifact version" leaf)
            | true, seq ->
                ArtifactStamp.create (leaf.Substring (idx + 1))
                |> Result.bind (fun stamp -> create name seq stamp)

    /// A pinned address read back: the inverse of `content`.
    let ofContent (ref: ContentRef) : Result<ArtifactRef, string> =
        match ContentRef.segments ref with
        | [ r; name; leaf ] when r = root -> ofLeaf name leaf
        | _ -> Error (sprintf "'%s' is not an artifact version's path" (ContentRef.value ref))

    /// The latest of some versions — highest sequence number, stamp breaking a tie. The rule
    /// lives here rather than at the store that lists a directory and the client that holds a
    /// few: one order, so "latest" cannot mean two things on two surfaces.
    let latest (refs: ArtifactRef list) : ArtifactRef option =
        refs |> List.sortBy (fun r -> r.Seq, ArtifactStamp.value r.Stamp) |> List.tryLast
