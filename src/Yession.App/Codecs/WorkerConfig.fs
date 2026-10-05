namespace Yession.App.Codecs

open Thoth.Json
open Fable.BrowserExtras

/// What the session shell's service worker is told about the build it serves, and how it is
/// told. The worker is a compiled program (`Yession.Browser.ServiceWorker`), one bundle for
/// every session; what varies is what only the SERVER knows — which asset set it read at boot,
/// and where under its mount each thing is. So the server serves the bundle with this record in
/// front of it, and the worker decodes it before it does anything else.
///
/// In front of the bundle rather than anywhere else, and the alternatives are why:
///
/// * **Not baked in at build time.** The build digest is a digest of the asset set, which this
///   bundle is part of — the bundle cannot contain the address of the set that contains it.
/// * **Not a query string on the registration URL.** The page that registers the worker would
///   then have to know the set's file list, which only the server reading the directory knows.
/// * **Not fetched by the worker.** A worker is stopped and restarted at the browser's whim,
///   and `fetch` has to be answered synchronously from state it already has — a config that
///   arrived over the network after start-up would be a config the first event did not have.
///
/// And it keeps the property the browser's update check depends on: `Build` is in the bytes, so
/// a new build is a byte-different worker — the browser installs it, and `activate` drops every
/// cache that is not this build's.
module WorkerConfig =

    /// One build's worth of what the worker keeps. Every address is relative to the worker's
    /// own scope (`DocumentBase.serviceWorker`), which is the mount root — the worker resolves
    /// them against `self.registration.scope`, so a path-mounted session keeps its own.
    [<RequireQualifiedAccess>]
    type Config =
        { /// The asset set's digest. Names the cache, so one build's keep is one store.
          Build: string
          /// The shell document's address.
          Shell: string
          /// Where every static file sits (`SessionRoute.assetsPrefix`).
          Assets: string
          /// What this build ships, named by the server that ships it — read from the same map
          /// it answers from, never a list kept by hand.
          Keep: string list }

    let private encoder (config: Config) =
        Encode.object
            [ "build", Encode.string config.Build
              "shell", Encode.string config.Shell
              "assets", Encode.string config.Assets
              "keep", config.Keep |> List.map Encode.string |> Encode.list ]

    let private decoder: Decoder<Config> =
        Decode.object (fun get ->
            { Config.Build = get.Required.Field "build" Decode.string
              Config.Shell = get.Required.Field "shell" Decode.string
              Config.Assets = get.Required.Field "assets" Decode.string
              Config.Keep = get.Required.Field "keep" (Decode.list Decode.string) })

    let toJson (config: Config) : string = config |> encoder |> Encode.toString 0

    let ofJson (json: string) : Result<Config, string> = Decode.fromString decoder json

    /// Where the config sits between the two: a property of the worker's global scope, set by
    /// the statement in front of the bundle and read by the bundle.
    let private carried : PageGlobal<string> = PageGlobal.named "yessionWorkerConfig"

    /// The worker as served for one build: the config published, then the program.
    let script (config: Config) (program: string) : string =
        PageGlobal.script carried (toJson config) + program

    /// The config this worker was served with — the worker's half of `script`. An `Error` is a
    /// worker served without one, or with one it cannot read: never a default, because a
    /// worker guessing which build it belongs to would keep the wrong one.
    let served () : Result<Config, string> =
        match PageGlobal.tryGet carried with
        | Some json -> ofJson json
        | None -> Error "served without a config (the server puts one in front of the program)"
