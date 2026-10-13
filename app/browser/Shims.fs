module Yession.Browser.Shims

// The installing half of `Yession.App.Shims`. Bundled by the test build alone
// (`tests/browser/out/shims.js`) and handed to a page as an init script right after the
// statement that says which shims it is to have, so it runs before any script of the page's.
// Nothing in the product imports it.

open Browser.Types
open Fable.BrowserExtras
open Yession.App
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private gatheringNeverEnds () =
    PageGlobal.set Shims.candidates 0
    PagePatches.constructed "RTCPeerConnection" (fun made ->
        // Its listeners go on in the constructor, before the page can set a handler of its own,
        // so they are heard first and can stop the event there.
        let peer = made :?> RTCPeerConnection
        peer.addEventListener ("icecandidate", fun e ->
            let ice = e :?> RTCPeerConnectionIceEvent
            if Option.isNone ice.candidate then e.stopImmediatePropagation ()
            else PageGlobal.set Shims.candidates ((PageGlobal.tryGet Shims.candidates |> Option.defaultValue 0) + 1))
        peer.addEventListener ("icegatheringstatechange", fun e -> e.stopImmediatePropagation ()))

let private answerApplied () =
    PagePatches.afterEach "RTCPeerConnection" "setRemoteDescription" (fun () -> PageGlobal.set Shims.answered true)

let private firstOnTop (tabs: int) =
    let mutable watch : TreeWatch = null
    let look () =
        let found = Browser.Dom.document.querySelectorAll "[data-pane-tab]"
        if found.length = tabs then
            let shown = [ for i in 0 .. found.length - 1 -> found.[i] ]
            let top =
                shown
                |> List.tryFind (fun tab -> tab.getAttribute "aria-selected" = "true")
                |> Option.bind (fun tab -> tab.getAttribute "data-pane-tab" |> Option.ofObj)
            PageGlobal.set Shims.firstOnTop (top |> Option.defaultWith (fun () -> "nothing"))
            if not (isNull watch) then watch.disconnect ()
    watch <- PagePatches.watchTree Browser.Dom.document look

let private matching (query: string) =
    let asIs = PagePatches.matchMediaAsIs ()
    // `all` is the one query every browser matches, so its answer is a real list that matches —
    // with every listener method a page might call — rather than a shape spelled out here.
    PagePatches.replaceMatchMedia (fun asked -> if asked = query then asIs "all" else asIs asked)

let private install (shim: Shims.Shim) =
    match shim with
    | Shims.Shim.GatheringNeverEnds -> gatheringNeverEnds ()
    | Shims.Shim.AnswerApplied -> answerApplied ()
    | Shims.Shim.FirstOnTop tabs -> firstOnTop tabs
    | Shims.Shim.Matching query -> matching query

match PageGlobal.tryGet Shims.asked with
| None -> failwith "the shims bundle ran without being told which shims to install"
| Some asked ->
    match Decode.fromString (Decode.list Shims.Shim.decode) asked with
    | Ok shims -> List.iter install shims
    | Error e -> failwithf "the shims bundle cannot read what it was asked: %s" e
