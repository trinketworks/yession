module Yession.Tests.DockerFixture

// What a Docker suite leaves on the daemon: nothing. Every docker object a case makes is
// named through `name`, and `owned` removes each one after the case, on red as well as
// green.
//
// A sandbox's `Dispose` removes its container and keeps its workspace volume. That is the
// product's contract: a session's workspace outlives a container restart. So a case that
// disposed correctly still left one volume behind, and a case that failed before its
// trailing `Dispose` left the container too. About five hundred volumes collected on one
// operator's daemon before anybody looked.
//
// The sweep is by NAME, never by the `yession-session` label alone. The daemon that runs
// these suites can be an operator's own, with live sessions on it, and those sessions'
// objects carry the same label. Only the names this run minted are its to remove.

open Fable.Core.JsInterop
open Fable.Pyxpecto.Model
open Yession.Domain
open Yession.Host

module DK = Fable.Dockerode

/// The names minted and not yet swept. Pyxpecto runs one case at a time, so after each case
/// these are the names that case made.
let private minted = System.Collections.Generic.HashSet<string> ()

/// A fresh docker object name that this run owns: a session id, as the product names a
/// sandbox's container and its workspace volume. Use it for every container or volume a
/// Docker case makes, so that `owned` removes it.
let name () : string =
    let fresh = SessionId.value (SessionId.mint ())
    minted.Add fresh |> ignore
    fresh

/// The containers a name labels, removed forced: a running one goes too.
let private removeContainers (client: DK.Docker) (owned: string) : Async<unit> =
    async {
        try
            let! containers =
                client.listContainers (
                    jsOptions<DK.ListContainersOptions> (fun o ->
                        o.all <- true
                        o.filters <- jsOptions<DK.ContainerFilters> (fun f -> f.label <- [| sprintf "yession-session=%s" owned |]))
                )
                |> Interop.awaitPromise
            for c in containers do
                do!
                    client.getContainer(c.Id).remove (jsOptions<DK.RemoveOptions> (fun o -> o.force <- true))
                    |> Interop.awaitPromise
                    |> Async.Ignore
        with ex -> eprintfn "docker fixture: could not remove the containers of %s: %s" owned ex.Message
    }

/// The volume of that name. A name that never became a volume is not an error.
let private removeVolume (client: DK.Docker) (owned: string) : Async<unit> =
    async {
        try
            do! client.getVolume(owned).remove (jsOptions<DK.RemoveOptions> ignore) |> Interop.awaitPromise |> Async.Ignore
        with ex ->
            if not (ex.Message.Contains "no such volume") then
                eprintfn "docker fixture: could not remove the volume %s: %s" owned ex.Message
    }

/// Remove everything minted since the last sweep. Every container first, then every volume:
/// a volume that a container still holds cannot be removed, and one case's volume can be
/// held by a container under another of its names.
let private sweep () : Async<unit> =
    async {
        let names = List.ofSeq minted
        minted.Clear ()
        if not names.IsEmpty then
            let client = DK.create ()
            for owned in names do
                do! removeContainers client owned
            for owned in names do
                do! removeVolume client owned
    }

/// One async case, followed by the sweep however it ended. The case's own failure is raised
/// again after the sweep, unchanged.
let private bracketed (body: Async<unit>) : Async<unit> =
    async {
        let! outcome = Async.Catch body
        do! sweep ()
        match outcome with
        | Choice1Of2 () -> return ()
        | Choice2Of2 ex -> return raise ex
    }

/// A Docker suite, with every async case followed by the sweep. Synchronous cases cannot
/// reach the daemon, so they pass through unchanged.
let rec owned (test: TestCase) : TestCase =
    match test with
    | SyncTest _ -> test
    | AsyncTest (label, body, focus) -> AsyncTest (label, bracketed body, focus)
    | TestList (label, cases, focus) -> TestList (label, List.map owned cases, focus)
    | TestListSequential (label, cases, focus) -> TestListSequential (label, List.map owned cases, focus)
