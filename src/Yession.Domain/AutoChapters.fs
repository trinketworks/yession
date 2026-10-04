namespace Yession.Domain.Chat

// Where the session divides itself when nobody asked it to: the chapters' POLICY, and only
// that. `Chapters` is the mechanism it is handed to — a person's verdict per message and its
// precedence, what a chapter covers, what it is called — and none of that is decided here.
// Refining when chapters open by themselves is an edit to this file and to nothing else.

/// The chapter policy this product runs.
///
/// Whatever it answers, the mechanism guarantees the rest: a person's own verdict wins over
/// it in either direction, it is never stored (so changing it moves only the chapters nobody
/// decided about), and it is shown only what came BEFORE an item, so a chapter it opened
/// stays open however the session goes on.
module AutoChapters =

    /// Whether this act opens a chapter BY NATURE — one nobody had to ask for.
    ///
    /// A rule over the act rather than a flag set at every fold arm, for the reason the
    /// prose is: the act knows. What is notable is deliberately a short list — a transcript
    /// where everything opens a chapter has none.
    ///
    /// Acts only, and that is load-bearing beyond this file: a chapter opened by nature has
    /// no entry in the doc, so it is never named by a model (`Naming.owed`) and wears its
    /// guess for good. An act note is a sentence already written short, so that is right for
    /// these; a policy that opened chapters at messages would want that line moved too.
    let rec private notable (act: Act) : bool =
        match act with
        | Act.Noticed (_, inner) -> notable inner
        // Where the waiting began, and the news that follows it — unlike the unwatch, which
        // is where the story stops being told rather than a place worth coming back to.
        | Act.PrWatched _
        | Act.PrTransitioned _ -> true
        // Worth seeing when it FAILED: a sandbox whose setup never ran is a sandbox the next
        // command pays for in full, and that is the case somebody should be told loudly.
        | Act.SandboxSetupQueued q -> q.Problem.IsSome
        | Act.RepoAdded _
        | Act.RepoRemoved _
        | Act.RepoBranchSwitched _
        | Act.RepoCapabilitiesChanged _
        | Act.RepoCapabilitiesApproved _
        | Act.RepoConfigRefused _
        | Act.RepoConfigWarned _
        | Act.SandboxStarting _
        | Act.SandboxStarted _
        | Act.SandboxStartFailed _
        | Act.SandboxStopped _
        | Act.ShellProfileSet _
        | Act.FileChanged _
        | Act.ArtifactShared _
        | Act.CommandRefused _
        | Act.GatedCommandFailed _
        | Act.CredentialSpent _
        | Act.McpServerAvailable _
        | Act.McpServerUnavailable _
        | Act.PrUnwatched _
        | Act.SessionResumed _
        | Act.SessionStarted _ -> false

    /// Today's rule reads the item alone; what came before it (`_before`, newest first) is
    /// there for the rule that wants it — a long silence, the first thing said after a
    /// stretch of the agent's own work.
    let private opensByNature (_before: ConversationItem list) (item: ConversationItem) : bool =
        match item.Content with
        | ItemContent.Act act -> notable act
        | ItemContent.Message _
        | ItemContent.Stopped _ -> false

    let policy : ChapterPolicy = { ChapterPolicy.OpensByNature = opensByNature }
