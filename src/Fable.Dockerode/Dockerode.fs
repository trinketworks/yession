module Fable.Dockerode

// Fable bindings to the `dockerode` npm package — the de-facto Node client for the Docker
// Engine API (Docker ships no official Node SDK; only Go and Python are official). This is
// the binding layer only, mirroring how `Fable.Yjs` wraps `yjs`: it declares the slice of
// dockerode's surface the Docker backend uses and nothing more. The engine lives behind
// the sandbox seam (`CreateSandbox`), so nothing above this file knows dockerode exists.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node. Methods
// that return promises surface as `JS.Promise<_>` for `Async.AwaitPromise` at the call site.

open Fable.Core
open Fable.Core.JsInterop

/// The slice of a Node stream we consume. A dockerode stream IS a Node readable — the
/// hijacked exec duplex, a demuxed `PassThrough`, a progress stream — so it is one here too,
/// which is what lets a consumer tell it to decode (`Readables.text`) and listen to its
/// `end` and `error` by type. What is added is write/end for the hijacked exec duplex
/// (stdin rides the same socket the output is demuxed from), and what is written there is
/// only ever what somebody typed.
type [<AllowNullLiteral>] Stream =
    inherit Fable.NodeExtras.Readable
    abstract write: text: string -> bool
    abstract ``end``: unit -> unit

/// A `node:stream` PassThrough — a writable sink `demuxStream` pushes one output stream
/// into, and a readable we drain via its `'data'`/`'end'` events.
type [<AllowNullLiteral>] PassThrough =
    inherit Stream

/// What `exec.inspect()` answers with, of it: the exit code — `null` while the process is
/// still running, and for a container that was killed rather than exiting, which is `None`.
type [<AllowNullLiteral>] ExecInspect =
    abstract ExitCode: int option

/// The size `exec.resize` takes: rows and columns, in the Engine API's own spelling.
[<RequireQualifiedAccess>]
type ExecSize =
    { h: int
      w: int }

// --- What the Engine API is asked -----------------------------------------------------------
//
// Every request body and query dockerode forwards, as an interface built with `jsOptions`: a
// field nobody assigned is ABSENT, which is how the Engine API is told "your default" — an
// exec that names no `WorkingDir` runs where the container was created, and one that names
// no `Tty` is multiplexed. The names are the Engine API's own spelling, capitals and all,
// because dockerode hands most of them to the daemon verbatim.

/// Labels on a docker object: names to values, as the Engine API reads them. Opaque, and
/// made only by `Labels.ofList`, because a label's NAME is the caller's — the one this
/// repository sets is `yession-session`, and no interface could declare a member for it.
type Labels =
    interface end

[<RequireQualifiedAccess>]
module Labels =

    /// `Object.fromEntries` over the pairs, which Fable compiles to two-element arrays — the
    /// shape it takes.
    [<Emit("Object.fromEntries($0)")>]
    let private ofEntries (entries: (string * string) array) : Labels = jsNative

    let ofList (labels: (string * string) list) : Labels = ofEntries (Array.ofList labels)

/// What a `HostConfig.Mounts` entry's source is: a host path bound in, or a named volume.
[<StringEnum; RequireQualifiedAccess>]
type MountType =
    | Bind
    | Volume

/// One `HostConfig.Mounts` entry.
[<AllowNullLiteral>]
type Mount =
    abstract Type: MountType with get, set
    abstract Source: string with get, set
    abstract Target: string with get, set
    abstract ReadOnly: bool with get, set

/// The half of a container's creation that is about the host it runs on rather than the
/// process it runs.
[<AllowNullLiteral>]
type HostConfig =
    abstract Mounts: Mount array with get, set
    /// `name:address` lines for the container's `/etc/hosts`.
    abstract ExtraHosts: string array with get, set
    abstract CapDrop: string array with get, set
    abstract CapAdd: string array with get, set
    abstract SecurityOpt: string array with get, set
    /// `/dev/shm`, in bytes.
    abstract ShmSize: int with get, set

/// `createContainer`'s options: `name` rides the query string, the rest is the body.
[<AllowNullLiteral>]
type ContainerCreateOptions =
    abstract name: string with get, set
    abstract Image: string with get, set
    abstract Labels: Labels with get, set
    /// `NAME=value` lines — the container's WHOLE environment.
    abstract Env: string array with get, set
    abstract WorkingDir: string with get, set
    abstract Cmd: string array with get, set
    abstract HostConfig: HostConfig with get, set

/// `createVolume`'s options.
[<AllowNullLiteral>]
type VolumeCreateOptions =
    abstract Name: string with get, set
    abstract Labels: Labels with get, set

/// A container's or a volume's `remove`.
[<AllowNullLiteral>]
type RemoveOptions =
    /// Remove it even while it runs.
    abstract force: bool with get, set

/// `listContainers`' filters: each a list of values the daemon ORs together.
[<AllowNullLiteral>]
type ContainerFilters =
    /// `name=value` (or bare `name`) label matches.
    abstract label: string array with get, set

[<AllowNullLiteral>]
type ListContainersOptions =
    /// Stopped containers too, not only running ones.
    abstract all: bool with get, set
    abstract filters: ContainerFilters with get, set

/// What `listContainers` answers with one of per container. Nothing here reads into it.
type ContainerSummary =
    interface end

/// `container.logs`' options.
[<AllowNullLiteral>]
type LogsOptions =
    /// A stream that stays open as the process prints, rather than what it has said so far.
    abstract follow: bool with get, set
    abstract stdout: bool with get, set
    abstract stderr: bool with get, set

/// `container.exec`'s options: the process to run, and what it is attached to.
[<AllowNullLiteral>]
type ExecOptions =
    abstract Cmd: string array with get, set
    abstract AttachStdin: bool with get, set
    abstract AttachStdout: bool with get, set
    abstract AttachStderr: bool with get, set
    /// A controlling terminal. Absent, the output is multiplexed stdout and stderr.
    abstract Tty: bool with get, set
    /// `NAME=value` lines, over the container's own environment.
    abstract Env: string array with get, set
    /// Absent, the container's own working directory.
    abstract WorkingDir: string with get, set

/// `exec.start`'s options. The Engine API's exec-start takes its OWN `Tty`, which has to
/// agree with the create's, or a terminal's output arrives framed as a pipe's.
[<AllowNullLiteral>]
type ExecStartOptions =
    /// Take over the connection, so stdin rides the socket the output comes back on.
    abstract hijack: bool with get, set
    abstract stdin: bool with get, set
    abstract Tty: bool with get, set

/// `buildImage`'s context: a directory, and the files in it to send.
[<AllowNullLiteral>]
type BuildContext =
    abstract context: string with get, set
    abstract src: string array with get, set

/// `buildImage`'s options.
[<AllowNullLiteral>]
type BuildOptions =
    /// The tag the built image gets.
    abstract t: string with get, set
    /// Relative to the context. Absent, the context's own `Dockerfile`.
    abstract dockerfile: string with get, set

/// A running `docker exec` handle.
type [<AllowNullLiteral>] Exec =
    /// Start the exec; resolves to the (multiplexed) output stream.
    abstract start: options: ExecStartOptions -> JS.Promise<Stream>
    /// Inspect after completion.
    abstract inspect: unit -> JS.Promise<ExecInspect>
    /// The Engine API's exec-resize endpoint, which is what raises SIGWINCH in the program
    /// on the other side. Rejects when the exec is already gone.
    abstract resize: size: ExecSize -> JS.Promise<unit>

/// What the Engine API answered to a request whose answer nothing here reads — a start, a
/// removal, a ping, an inspect asked only for whether it succeeds. Opaque: the promise
/// SETTLING is the whole of what a caller learns, and a type with members would suggest
/// otherwise.
type Unread =
    interface end

/// A container handle (created, or looked up by name/id).
type [<AllowNullLiteral>] Container =
    /// The full container id assigned by the daemon.
    abstract id: string
    abstract start: unit -> JS.Promise<Unread>
    abstract remove: options: RemoveOptions -> JS.Promise<Unread>
    abstract exec: options: ExecOptions -> JS.Promise<Exec>
    abstract inspect: unit -> JS.Promise<Unread>
    /// The container's own output. With `follow: true` a stream that stays open as the
    /// process prints; multiplexed like an exec's, so `demuxStream` reads it.
    abstract logs: options: LogsOptions -> JS.Promise<Stream>

/// A named-volume handle.
type [<AllowNullLiteral>] Volume =
    abstract remove: options: RemoveOptions -> JS.Promise<Unread>

/// What an image says about the containers made from it, of it: the environment its
/// `ENV` lines set. Absent on an image that sets none.
type [<AllowNullLiteral>] ImageConfig =
    abstract Env: string array option

/// What `image.inspect()` answers with, of it.
type [<AllowNullLiteral>] ImageInspect =
    abstract Config: ImageConfig option

[<RequireQualifiedAccess>]
module ImageInspect =

    /// The image's `ENV`, as a map. Docker reports each as `NAME=value`, split at the FIRST
    /// `=`: a value may carry more, a name may not.
    let environment (inspected: ImageInspect) : Map<string, string> =
        inspected.Config
        |> Option.bind (fun config -> config.Env)
        |> Option.defaultValue [||]
        |> Array.choose (fun pair ->
            match pair.IndexOf '=' with
            | -1 -> None
            | at -> Some (pair.Substring (0, at), pair.Substring (at + 1)))
        |> Map.ofArray

/// An image handle — tested for local presence before pulling, and asked what it sets.
type [<AllowNullLiteral>] Image =
    /// Resolves if the image exists locally; rejects otherwise.
    abstract inspect: unit -> JS.Promise<ImageInspect>

/// docker-modem: the low-level plumbing dockerode exposes for stream handling.
type [<AllowNullLiteral>] Modem =
    /// Split Docker's multiplexed exec stream into stdout/stderr sinks.
    abstract demuxStream: source: Stream * stdout: PassThrough * stderr: PassThrough -> unit
    /// Drain a build/pull progress stream, calling back once when it finishes: `(err, output)`,
    /// where `err` is `null` — `None` — when it finished well.
    abstract followProgress: source: Stream * onFinished: (Fable.NodeExtras.StreamError option -> Unread -> unit) -> unit

/// The dockerode client, bound to the local daemon socket.
type [<AllowNullLiteral>] Docker =
    /// Resolves when the daemon answers; rejects when it is unreachable.
    abstract ping: unit -> JS.Promise<Unread>
    abstract createContainer: options: ContainerCreateOptions -> JS.Promise<Container>
    abstract getContainer: id: string -> Container
    abstract createVolume: options: VolumeCreateOptions -> JS.Promise<Unread>
    abstract getVolume: name: string -> Volume
    abstract getImage: name: string -> Image
    /// Pull an image; resolves to the progress stream to drain before use.
    abstract pull: tag: string -> JS.Promise<Stream>
    /// Build an image from a directory and the files in it to send.
    abstract buildImage: context: BuildContext * options: BuildOptions -> JS.Promise<Stream>
    abstract listContainers: options: ListContainersOptions -> JS.Promise<ContainerSummary array>
    abstract modem: Modem

/// The options either constructor below is handed: none set, which is how each is told "your
/// default" — the local daemon socket for dockerode, a plain byte stream for `PassThrough`.
type ConstructorOptions =
    interface end

/// The dockerode class, as the one thing done with it: construct a client.
type private DockerClass =
    [<EmitConstructor>]
    abstract Create : options: ConstructorOptions -> Docker

/// `node:stream`'s `PassThrough` class, likewise.
type private PassThroughClass =
    [<EmitConstructor>]
    abstract Create : options: ConstructorOptions -> PassThrough

[<ImportDefault("dockerode")>]
let private dockerClass: DockerClass = jsNative

[<Import("PassThrough", "node:stream")>]
let private passThroughClass: PassThroughClass = jsNative

/// A client on the default local socket (`/var/run/docker.sock`, or the platform default).
let create () : Docker = dockerClass.Create (jsOptions<ConstructorOptions> ignore)

/// A fresh PassThrough sink for demuxing exec output.
let createPassThrough () : PassThrough = passThroughClass.Create (jsOptions<ConstructorOptions> ignore)
