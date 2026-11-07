module Yession.Browser.Hosts

// The elements the passes after a render work on — an editor's host, a command line — known
// because each one SAYS it is in the page, rather than found by asking the document.
//
// A pass used to begin with `querySelectorAll` over the whole document for its hook, then
// diff what it found against what it had mounted, and sweep what had gone. That is a second
// render system after Lit's: a whole-document walk per pass per render (and per doc update,
// for the command lines), and a sweep that is only as right as the scan before it. The view
// already knows where each of these elements is — it drew them — so it marks each with a role
// and a key (`HostRole.mark`), and lit tells this registry when it connects one and when it
// takes one away (`Fable.LitExtras.host`). A pass walks what is registered; what leaves is
// disposed as it leaves, by whoever watches its role, so there is no sweep to forget.
//
// Lit tells a registration DURING its commit, and a new template's elements are registered
// before its fragment is in the page. So a watcher records, binds listeners and subscribes
// there — nothing that needs the element laid out — and the pass after the render does the
// rest.

open Fable.LitExtras
open Yession.App

/// What a role's watcher is told, for each element of its role, as lit connects it and takes
/// it away. The element itself both times.
type Watch =
    { Came : string -> Browser.Types.Element -> unit
      Went : string -> Browser.Types.Element -> unit }

/// One page's registry.
type Hosts =
    { /// The render's sink: `Render` hands it to lit as the render's host (`renderHosted`).
      Sink : HostSink
      /// The elements in `role`'s set now, each with its key, in the order they came.
      In : HostRole -> (Browser.Types.Element * string) list
      /// Be told of every element of `role` as it comes and goes, from now on. One watcher a
      /// role: the role is the pass, and a second pass over the same elements is the second
      /// copy of one job.
      Watch : HostRole -> Watch -> unit }

let create () : Hosts =
    // By element, and by IDENTITY. A list, because a set is a handful of elements (the
    // drafts, the queued messages, the command lines on screen) and a pass walks all of it;
    // an F# dictionary would hash an element structurally.
    let sets = System.Collections.Generic.Dictionary<HostRole, ResizeArray<Browser.Types.Element * string>> ()
    let watches = System.Collections.Generic.Dictionary<HostRole, Watch> ()
    let setOf (role: HostRole) =
        match sets.TryGetValue role with
        | true, set -> set
        | false, _ ->
            let set = ResizeArray<Browser.Types.Element * string> ()
            sets.[role] <- set
            set
    let indexOf (set: ResizeArray<Browser.Types.Element * string>) (element: Browser.Types.Element) =
        set.FindIndex (fun (held, _) -> obj.ReferenceEquals (held, element))
    // A role this registry does not know is a view marking something nothing reads: dropped,
    // and the pass that should have read it reads an empty set (`HostRole` says why the set
    // is closed).
    let sink =
        { new HostSink with
            member _.Came (role, key, element) =
                match HostRole.ofName role with
                | Some role ->
                    let set = setOf role
                    match indexOf set element with
                    | -1 -> set.Add ((element, key))
                    | i -> set.[i] <- (element, key)
                    match watches.TryGetValue role with
                    | true, watch -> watch.Came key element
                    | false, _ -> ()
                | None -> ()
            member _.Went (role, key, element) =
                match HostRole.ofName role with
                | Some role ->
                    let set = setOf role
                    // Only the registration this is about: an element that came again under
                    // another key before this one was told it went keeps the new one.
                    match indexOf set element with
                    | -1 -> ()
                    | i -> if snd set.[i] = key then set.RemoveAt i
                    match watches.TryGetValue role with
                    | true, watch -> watch.Went key element
                    | false, _ -> ()
                | None -> () }
    { Sink = sink
      In = fun role -> List.ofSeq (setOf role)
      Watch = fun role watch -> watches.[role] <- watch }
