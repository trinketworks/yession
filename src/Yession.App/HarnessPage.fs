namespace Yession.App

open Fable.Core
open Fable.BrowserExtras

// The harness page's interface, shared: `app/browser/EditorHarness.fs` publishes each of these
// and the browser cases and bench scenarios call them (`tests/Yession.Tests/BrowserHarness.fs`).
// Here rather than in the harness because both sides must name the same key with the same type,
// and the key — a `PageGlobal` — is the type: a case that calls one with the wrong arguments, or
// reads one as the wrong thing, does not compile.

/// What this page publishes on `window`, which is the harness's INTERFACE: the browser cases
/// and the bench scenarios read and call these out of a Playwright `evaluate`, which can see
/// a global and cannot see a module binding. Each is the measurement's output or the
/// scenario's entry, not a global something forgot to scope. A function of more than one
/// argument is a delegate, because that is a JavaScript function of that many parameters
/// and an F# function of that type is not.
[<RequireQualifiedAccess>]
module HarnessPage =
    let md : PageGlobal<unit -> string> = PageGlobal.named "__md"
    let pushRemote : PageGlobal<string -> unit> = PageGlobal.named "__pushRemote"
    /// Mount the chaptered replay (`#replay-chapters`) at the end of the page.
    let chapters : PageGlobal<unit -> unit> = PageGlobal.named "__chapters"
    /// How many times Ctrl+Enter has asked to send. The harness mounts the editor exactly as
    /// the COMPOSER does (`onSubmit` supplied), so the E2E drives the real binding: plain
    /// Enter opens a paragraph and Ctrl+Enter (Cmd+Enter on macOS) sends. A counter rather
    /// than a callback because what the test needs to know is "did it fire", and the send
    /// itself belongs to the app.
    let sends : PageGlobal<int> = PageGlobal.named "__sends"
    /// Start or stop pushing presence decorations into the MIRROR on every animation frame.
    let caretStorm : PageGlobal<bool -> unit> = PageGlobal.named "__caretStorm"
    /// How many frames the storm has actually pushed on. Anti-vacuity: a convergence assertion
    /// passes trivially if the storm never ran, and "never ran" and "ran and was harmless" look
    /// identical from the outside.
    let caretPushes : PageGlobal<int> = PageGlobal.named "__caretPushes"
    /// The two counters are on `window` because that is this instrument's INTERFACE: the browser
    /// case reads them out of a Playwright `evaluate`, which can see a global and cannot see a
    /// module binding. They are the measurement's output, not a global something forgot to scope.
    let docUpdates : PageGlobal<int> = PageGlobal.named "__docUpdates"
    let writebacks : PageGlobal<int> = PageGlobal.named "__writebacks"
    let convState : PageGlobal<unit -> string> = PageGlobal.named "__convState"
    /// The typing diagnostic's counters, republished on every change (`TypingDiagnostic`).
    let benchDiagState : PageGlobal<{| hostKeydowns : int; docKeydowns : int; rafs : int; focus : string array |}> =
        PageGlobal.named "__benchDiagState"
    let benchDiag : PageGlobal<unit -> string> = PageGlobal.named "__benchDiag"
    let benchSeed : PageGlobal<int -> JS.Promise<int>> = PageGlobal.named "__benchSeed"
    let benchCarets : PageGlobal<int -> JS.Promise<string>> = PageGlobal.named "__benchCarets"
    let benchReset : PageGlobal<unit -> unit> = PageGlobal.named "__benchReset"
    let benchTyping : PageGlobal<unit -> string> = PageGlobal.named "__benchTyping"
    let benchTranscript : PageGlobal<System.Func<int, int, int, string>> = PageGlobal.named "__benchTranscript"
    let benchSettle : PageGlobal<unit -> JS.Promise<unit>> = PageGlobal.named "__benchSettle"
    /// Begin the scroll scenario: a conversation of `items`, `records` transcript records arriving
    /// one every `everyMs`, and the frame clock and render clock running. The FLING is the driver's
    /// to make, with real touch input, once this returns.
    ///
    /// `elsewhere` sends the records to a terminal the conversation does not draw — a sandbox
    /// running its setup behind a shut pane — so every one of them is a render that leaves the
    /// conversation exactly as it was. Off, they land in the burst card's running block, and the
    /// conversation grows under the reader with each.
    let benchScrollBegin : PageGlobal<System.Action<int, int, int, bool>> = PageGlobal.named "__benchScrollBegin"
    /// How many records the stream has sent so far. The driver flings until the stream is spent,
    /// so every size is measured over the same records rather than over however long one fling
    /// through it happened to take.
    let benchScrollSent : PageGlobal<unit -> int> = PageGlobal.named "__benchScrollSent"
    /// End it: stop the stream and the clocks, and hand back what they recorded.
    let benchScrollEnd : PageGlobal<unit -> string> = PageGlobal.named "__benchScrollEnd"
    /// Open a session the way the app opens one it has been to before — from what it kept — and
    /// say what the page did between its first paint and its connection: `items` conversation
    /// items' worth of events in the kept store, `perAnswer` events to each kept answer.
    let benchOpen : PageGlobal<System.Func<int, int, JS.Promise<string>>> = PageGlobal.named "__benchOpen"
    /// Open a session the way the app opens one it has never seen — everything over the
    /// network, `pageSize` events to a page, a page every `everyMs` — and say what the page did.
    let benchOpenCold : PageGlobal<System.Func<int, int, int, JS.Promise<string>>> = PageGlobal.named "__benchOpenCold"
    let typed : PageGlobal<string> = PageGlobal.named "__typed"
    /// Hand the shell a terminal SCREEN, as the Session does over the data channel
    /// (Plan 14, stage 6). Exposed so the E2E can drive the one path that puts a real emulator
    /// in a real browser: without it this bundle contains no xterm at all, and the browser tier
    /// silently proved nothing about the client's live screen — which is how a browser-only
    /// module-resolution failure got past it and into a release job.
    ///
    /// The size is the caller's to leave out — a case that only wants a screen on the page says
    /// nothing about how big it is, and the harness's own 80x24 is what it gets.
    let snapshot : PageGlobal<System.Action<string, int, string, int option, int option>> = PageGlobal.named "__snapshot"
    /// Hand the shell one transcript record, as the Session does as a terminal speaks.
    /// The companion to the snapshot: a snapshot is where a screen STARTS and records are what
    /// move it, and composing the two — including a resize reshaping the emulator mid-stream —
    /// is the client's own fold.
    let record : PageGlobal<System.Action<string, int, string, string>> = PageGlobal.named "__record"
    /// The size this client last told the Session its screen is. Read back by the E2E,
    /// because the question there is whether a box that changed without the model changing — a
    /// splitter dragged, a window resized — reached the pty at all.
    let resized : PageGlobal<string> = PageGlobal.named "__resized"
    /// The size this client last measured its OWN view of a terminal at — the width a command
    /// queued from that pane would claim (`PendingAct.Size`). A second hook rather than a reading
    /// of `window.__resized`, because the two are different facts: that one is what was sent to the
    /// pty for a lease this peer holds, and this one is measured in BLOCK mode, where nobody holds
    /// anything and nothing is sent at all.
    let viewport : PageGlobal<string> = PageGlobal.named "__viewport"
    /// Start an agent turn in the shell, as the Session does when the model begins to
    /// answer: the turn, the message it opens, and the first words of it, folded through the same
    /// page the real client reads. Exposed because the browser tier boots its session with NO
    /// model credential (deliberately — see `Browser.fs`), so a turn in flight is a state no
    /// amount of typing on this page can reach, and how many marks a person sees while one is
    /// running is a question only a laid-out page can answer.
    let agentTurn : PageGlobal<unit -> unit> = PageGlobal.named "__agentTurn"
    /// The same turn before its first word: begun, a message opened, nothing said. The agent
    /// is thinking, which is the mark that MOVES, and this harness runs no timers to let a
    /// written turn go quiet, so the case that measures the moving mark starts here instead.
    let agentThinks : PageGlobal<unit -> unit> = PageGlobal.named "__agentThinks"
    /// That turn's first word, after `__agentThinks`: the moment the mark leaves where the turn
    /// began for the end of what has been said.
    let agentSays : PageGlobal<unit -> unit> = PageGlobal.named "__agentSays"
    /// Hand a terminal's lease to this peer WITHOUT a press, as the alt-screen flip does: a block
    /// takes the screen and the Session gives its author the keyboard. Exposed for the
    /// same reason the snapshot is — it is the arrival of a fact from elsewhere, and a test that
    /// could only reach live mode by pressing `take` could never exercise the route that has no
    /// press to make.
    let take : PageGlobal<string -> unit> = PageGlobal.named "__take"
    /// Every terminal this page has asked the session to END, in the order it asked. Read back
    /// because a kill is two presses and only the second may ask: what the page SENT is the
    /// whole question, and with no session behind it nothing else would record it.
    let closed : PageGlobal<string array> = PageGlobal.named "__closed"
    /// Fold a page of events into the shell, as the server serves one — an envelope per line,
    /// in the wire's own encoding (`EventFetch.decodeLines`, the one decoder the real client
    /// reads pages with). How a fact that arrives from ELSEWHERE reaches this page: a terminal
    /// somebody ends, or one this page asked to end, comes back as an event, and what the list
    /// does when it lands is a question only a laid-out page can answer.
    let fold : PageGlobal<string -> unit> = PageGlobal.named "__fold"
    /// Swap the shell between the session's first screen and a conversation. Both, from one hook,
    /// because the question the card raises is about the two TOGETHER: the ask card stands only
    /// where nothing has been said and a message body only where something has, so the one column
    /// they are both supposed to start on can be measured no other way on one page.
    let launch : PageGlobal<bool -> unit> = PageGlobal.named "__launch"
    /// The session's first screen with the card standing over a conversation that is NOT
    /// empty — what a session looks like whose first acts were commands or a sandbox coming
    /// up, which do not begin it (`ClientModel.launchOffered`). Its own hook because the
    /// question it raises is where the card stands relative to what is already said: whether
    /// every item in the column can still be scrolled to and reached, which the empty first
    /// screen `__launch` draws cannot ask.
    let launchOver : PageGlobal<unit -> unit> = PageGlobal.named "__launchOver"
    /// The card the instant before its repositories arrive — the same first screen `__launch`
    /// draws, still LOOKING. Its own hook because the question it raises is about the step
    /// between looking and loaded: the card reserves the room its list will want, so that
    /// repositories filling in do not change its height and shove the conversation. Measured
    /// no other way — the loaded card and the looking card are one mount apart.
    let launchLooking : PageGlobal<unit -> unit> = PageGlobal.named "__launchLooking"
    /// Swap in the shell with one ACT on its timeline — a sandbox start whose sentence
    /// points at a sandbox and a connection — for the case that measures where a reference
    /// sits on its line. Its own hook rather than an item in the shared fixture, because
    /// every other case measures that fixture's geometry and an act note is a different
    /// shape of row to have standing in it.
    let acts : PageGlobal<unit -> unit> = PageGlobal.named "__acts"
    /// A collaborator's caret in a chapter's NAME, with no session to relay one from. The
    /// positions handed over are real relative positions over a real `Y.Text` on this page's doc,
    /// which is the whole of what the placement reads: it resolves them against the doc and
    /// measures the input's own value and box. So the question the browser is asked here is
    /// exactly the one the app asks it — given a caret that resolves, is the marker painted over
    /// the field it is in, at the offset it claims.
    ///
    /// Where a name lives IN the doc is the codec's answer and is pinned where it can be tested
    /// for a penny (`SyncedStateSync.chapterNameText`), not restated here: a fixture that wrote
    /// the layout out by hand would be a second copy of it, and the wrong one the day it moved.
    let chapterCaret : PageGlobal<System.Action<string, int, int>> = PageGlobal.named "__chapterCaret"
    /// Swap in the shell with Claude's credential needing a sign-in, so the prompt over the
    /// timeline is on the page. Only a browser can say where that prompt's button lands as its
    /// reason is opened and closed, and no session this harness talks to can spend a credential.
    let signInLost : PageGlobal<unit -> unit> = PageGlobal.named "__signInLost"
    /// Swap in the shell with nothing connected to run the agent, so the roster's row for an
    /// absent agent is on the page. Its geometry and its touch target are what only a laid-out
    /// page can say, and the shell this harness starts in has not heard from the credential
    /// stream, which draws no row for the agent at all.
    let noAgent : PageGlobal<unit -> unit> = PageGlobal.named "__noAgent"
    /// Swap in the shell with its terminals open and NOTHING in the strip — what a fresh
    /// viewer of somebody else's work has, since a strip holds only what its reader opened.
    /// Which terminal opened them is the fold's question and is pinned where it is cheap; the
    /// question here is where the pane lands when it is brought up over that, and on a phone
    /// whether what it lands on is really the thing ON SCREEN — which only a laid-out page
    /// can answer.
    let freshViewer : PageGlobal<unit -> unit> = PageGlobal.named "__freshViewer"
