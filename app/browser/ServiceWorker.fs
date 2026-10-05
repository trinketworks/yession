module Yession.Browser.ServiceWorker

// The service worker, for one build. Registered from the client bundle; served at the mount
// root, because a worker controls its own path and below (Plan 20). One program for every
// build: what a build changes arrives as data in front of it (`Codecs.WorkerConfig`).
//
// It keeps exactly two things, and the split is the whole design:
//
// * **the shell** — network-first, because it is `no-cache` for a real reason (it NAMES the
//   fingerprinted assets, so a stale one pins the whole UI to a build that is gone). The cached
//   copy is the fallback, and it is what makes a cold open possible at all.
// * **fingerprinted assets** — cache-first to SERVE, but PRECACHED at install from `Keep`, not
//   kept on the way past. The install handler below says why that is the whole difference
//   between working and not. And `Keep` is not a second place that has to agree with the
//   build: the server that ships the assets generates it from the same map it answers from
//   (`app/Signalling.fs`), so nothing here is written by hand.
//
// And it keeps NOTHING else. Not the event log — the page owns that cache directly and a copy
// here would be the redundant spare. Not `/me`, `/signal`, `/queries`, `/claude*`: those are
// liveness questions, and a cached answer to "can I reach this session" is a wrong answer.
//
// It was a JavaScript program in a string, rendered by the server per build. It is F# now for
// the reason every other program in this repository is: a string is a program nothing
// type-checks and nothing even parses.
//
// A classic script, not a module: it is bundled as an IIFE (`tasks.fsx`), because a module
// worker needs `register(url, { type: 'module' })` and a browser that supports it, and nothing
// here needs `import`.

open System
open Fable.Core
open Fetch.Types
open Fable.BrowserExtras
open Yession.App.Codecs

let private scope = ServiceWorkerScope.current

/// What this build is, as the server that served this script said. A worker that cannot read
/// it refuses to START — the registration fails and the client carries on without one, which
/// is exactly a client whose browser refused the worker — rather than guess which build it
/// keeps.
let private config =
    match WorkerConfig.served () with
    | Ok config -> config
    | Error reason -> failwithf "yession/sw: %s" reason

/// Every store this worker has ever made starts with this, and only the current build's is
/// called exactly `cacheName`.
let private cachePrefix = "yession/shell/"

/// `Build` names the cache, so a new build is a byte-different worker: the browser installs
/// it, and `activate` drops every cache that is not this build's.
let private cacheName = cachePrefix + config.Build

/// A relative address from the config, resolved against where this worker is registered.
let private inScope (relative: string) =
    Urls.resolve relative scope.registration.scope

let private shell = inScope config.Shell
let private assets = inScope config.Assets

// What this build ships, named by the server that ships it — never a list kept by hand.
let private keep = config.Keep |> List.map inScope

/// Fetch one file into the store at install. Each file settles on its own: a set where one
/// entry 404s is a set that should still open offline missing that one thing, not an install
/// that fails and leaves nothing at all.
let private precache (store: CacheStorage.Cache) (href: string) : JS.Promise<unit> =
    promise {
        try
            let! response = Fetch.fetchUnsafe href [ RequestProperties.Cache RequestCache.Reload ]
            if response.Ok then do! store.keep (RequestInfo.Url href, response)
        with _ ->
            // Offline at install time: the next load will fill it.
            ()
    }

scope.onInstall (fun event ->
    // FETCHED here rather than kept on the way past, and that is the whole difference between
    // working and not: the first navigation happens before this worker controls anything, so
    // nothing it would have "kept as it went" was ever seen by it. A client that installed a
    // worker and then went offline would have an empty cache and a dead page.
    event.waitUntil (
        promise {
            let! store = scope.caches.openStore cacheName
            let! _ = shell :: keep |> List.map (precache store) |> Promise.all
            // Take over at once: the alternative is a client that installed a worker and is
            // still waiting for a tab it will never close.
            do! scope.skipWaiting ()
        }))

scope.onActivate (fun event ->
    event.waitUntil (
        promise {
            let! names = scope.caches.names ()
            for name in names do
                if name.StartsWith (cachePrefix, StringComparison.Ordinal) && name <> cacheName then
                    let! _ = scope.caches.drop name
                    ()
            do! scope.clients.claim ()
        }))

/// The shell, and ONLY the shell. A session's other navigations are the sign-in bounce
/// (`/login`, `/callback`), and inside a worker a navigation request carries
/// `redirect: 'manual'` — so fetching one returns an opaque redirect, whose `status` is 0 and
/// whose `ok` is false. Treating that as a failure (it looks exactly like one) swallowed the
/// bounce and left the client on a page that never arrived. Nothing here has any business
/// touching them: they are the one part of this surface that MUST reach the network.
let private isShell (asked: string) =
    Urls.origin asked = Urls.origin shell && Urls.pathname asked = Urls.pathname shell

/// Keep a good answer under `request` on its way to the page, and hand it on.
let private keepAnswer (request: RequestInfo) (response: Response) : JS.Promise<Response> =
    promise {
        if response.Ok then
            let! store = scope.caches.openStore cacheName
            do! store.keep (request, response.clone ())
        return response
    }

/// The shell: the network first, the kept copy when the network does not answer.
///
/// `not response.Ok` counts as failure, and that is not defensive coding. A session behind an
/// operator's proxy that is still up answers a DEAD session with 502 — a perfectly successful
/// fetch carrying an error — and a worker that only caught thrown requests would serve that 502
/// as the page, which is the exact case this exists for.
let private shellFirst (request: Request) : JS.Promise<Response> =
    promise {
        try
            let! fresh = GlobalFetch.fetch (RequestInfo.Req request)
            if not fresh.Ok then failwithf "shell answered %d" fresh.Status
            return! keepAnswer (RequestInfo.Url shell) fresh
        with err ->
            let! kept = scope.caches.lookup (RequestInfo.Url shell)
            // The one moment this worker makes a decision nobody can otherwise see: the session
            // did not answer, and either there is a shell kept here or the page is about to be a
            // browser error. Debugging it without this line means inferring it from a timeout,
            // which cost three full runs of the gate once. Open devtools -> Application ->
            // Service Workers to read it; Playwright cannot (no `ServiceWorkers` on a context).
            JS.console.debug (
                "yession/sw: shell unreachable",
                {| asked = request.url
                   reason = string err
                   served = (if kept.IsSome then "kept copy" else "nothing kept") |}
            )
            match kept with
            | Some response -> return response
            | None -> return raise err
    }

/// A fingerprinted asset: what is kept is served, and what is not is fetched and kept. An
/// address names its bytes for ever, so a kept copy is never stale.
let private assetFirst (request: Request) : JS.Promise<Response> =
    promise {
        match! scope.caches.lookup (RequestInfo.Req request) with
        | Some kept -> return kept
        | None ->
            let! fresh = GlobalFetch.fetch (RequestInfo.Req request)
            return! keepAnswer (RequestInfo.Req request) fresh
    }

scope.onFetch (fun event ->
    let request = event.request
    // Not answering is an answer: the browser goes to the network as if there were no worker.
    if request.``method`` = "GET" then
        let url = request.url
        if ServiceWorkerScope.isNavigation request && isShell url then
            event.respondWith (shellFirst request)
        elif url.StartsWith (assets, StringComparison.Ordinal) then
            event.respondWith (assetFirst request))
