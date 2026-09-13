namespace Yession.App

open Yession.Domain

/// A stable colour per editor, derived from who they are so every replica agrees without
/// coordination. One source of truth for presence colour — title markers, chapter rules and
/// body cursor decorations all use it, so somebody is the same colour wherever they appear.
///
/// Keyed by `ActorRef` rather than `PeerId` because the Session Process edits here too
/// (Plan 25) and never joined as a peer. The hash reads `ActorRef.token`, which is the same
/// string for a given peer as its raw id was prefixed — so a peer's hue MOVES once, on the
/// change that introduced this, and is stable for ever after. Nothing depends on a particular
/// hue; what matters is that two replicas agree on it.
module EditorColour =

    /// The editor's hue (0-359), hashed from its token.
    let private hueOf (who: ActorRef) : int =
        let s = ActorRef.token who
        let mutable h = 0
        for i in 0 .. s.Length - 1 do
            // int32 arithmetic wraps like the JS `| 0`, so this matches across runtimes.
            h <- h * 31 + int s.[i]
        ((h % 360) + 360) % 360

    /// A solid `hsl(...)` colour (the caret bar and label).
    let ofEditor (who: ActorRef) : string = sprintf "hsl(%d, 70%%, 55%%)" (hueOf who)

    /// A translucent `hsla(...)` of the same hue (the selection highlight).
    let translucent (who: ActorRef) : string = sprintf "hsla(%d, 70%%, 55%%, 0.25)" (hueOf who)
