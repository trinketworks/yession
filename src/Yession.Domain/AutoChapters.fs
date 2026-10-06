namespace Yession.Domain.Chat

open Yession.Domain
open Yession.Domain.Prs

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

    /// The pull request an act is a milestone of, and the words that milestone is called by:
    /// opening one, and its merge. Nothing else opens a chapter by itself — a transcript where
    /// every check and every queue entry opens one has none — and the two are the ends of the
    /// story a pull request tells.
    ///
    /// Acts only, and that is load-bearing beyond this file: a chapter opened by nature has
    /// no entry in the doc, so it is never named by a model (`Naming.owed`) and wears its
    /// guess for good. These guesses are written to be kept; a policy that opened chapters at
    /// messages would want that line moved too.
    let rec private milestone (act: Act) : (string * PrRef * string option) option =
        match act with
        | Act.Noticed (_, inner) -> milestone inner
        | Act.PrCreated p -> Some ("Opened", p.Pr, Some p.Title)
        | Act.PrTransitioned p when p.Transition = PrTransition.Merged -> Some ("Merged", p.Pr, p.Title)
        | _ -> None

    let private actOf (item: ConversationItem) : Act option =
        match item.Content with
        | ItemContent.Act act -> Some act
        | ItemContent.Message _
        | ItemContent.Stopped _ -> None

    /// Today's rule reads the item alone; what came before it (`_before`, newest first) is
    /// there for the rule that wants it — a long silence, the first thing said after a
    /// stretch of the agent's own work.
    let private opensByNature (_before: ConversationItem list) (item: ConversationItem) : bool =
        actOf item |> Option.bind milestone |> Option.isSome

    /// "Opened PR #1234 Add feature", "Merged PR #1234 Add feature": the deed first, then the
    /// number a person says aloud, then what it is about. The repository is left off — a
    /// session's pull requests are nearly always one repository's, and the rule has 48
    /// characters to spend. A merge seen before transitions carried a title says the number
    /// alone rather than inventing one.
    let private guess (item: ConversationItem) : string option =
        actOf item
        |> Option.bind milestone
        |> Option.map (fun (deed, pr, title) ->
            match title with
            | Some title -> sprintf "%s PR #%d %s" deed pr.Number title
            | None -> sprintf "%s PR #%d" deed pr.Number)

    let policy : ChapterPolicy = { ChapterPolicy.OpensByNature = opensByNature; ChapterPolicy.Guess = guess }
