module Yession.Browser.Scan

// Every question this program asks the WHOLE document — `querySelector` and
// `querySelectorAll` on `document` — asked through one door that counts it.
//
// A whole-document scan is the expensive question on this page: an attribute selector is not
// indexed, so the walk visits every node, and one that MISSES cannot stop early. Measured on a
// session of 20,650 events (54k nodes, twelve terminals open): 616 viewport scans cost 5.3
// seconds, a third of the CPU a cold open spent. So the number of them is a cost worth counting,
// as a COUNT — the same on every box. `Render` reads it around each render and publishes what
// the renders made (`Probes.scans`), for the reason it publishes its render count: a test that
// hooked `querySelectorAll` to infer it would read a private detail of the walks below, and go
// quietly vacuous the day a walk changed shape.
//
// Scoped questions (`element.querySelector`) are not counted: they walk one subtree, and the
// subtree is the point of asking there. Some whole-document questions do not come here either,
// on purpose: the test build's (`Probes.fs`, `Measures.fs`, `Shims.fs`) and the bench harness's
// own (`EditorHarness.fs`), because they are the instrument, and counting the instrument would
// make every reading of it a reading of itself. And the Manager page's program
// (`ManagerPage.fs`) asks its own document its own questions: it is a different page, with no
// render loop this count is read against, bundled on its own.

open Browser.Types

let mutable private made = 0

/// How many whole-document scans this document has made since it loaded. A running total:
/// a reader takes it before and after what it is measuring (`Render`).
let count () : int = made

let private counted () : unit = made <- made + 1

/// The first element in the document matching `selector`, counted.
let one (selector: string) : Element option =
    counted ()
    Browser.Dom.document.querySelector selector |> Option.ofObj

/// Every element in the document matching `selector`, in document order, counted.
let all (selector: string) : Element list =
    counted ()
    let found = Browser.Dom.document.querySelectorAll selector
    [ for i in 0 .. found.length - 1 -> found.[i] ]
