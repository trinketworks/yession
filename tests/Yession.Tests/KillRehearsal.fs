module Yession.Tests.KillRehearsal

// What a case and the kill rehearsal share: which kill the program makes, and the codes that
// it leaves with. `KillRehearsalMain.fs` is the program, and it says why there is one. Both
// directions are here, beside the type, so the case and the program cannot drift apart. A
// case opens this file and not the program, because the top level of the program runs.

/// One kill of a child that did not start, by who makes it and at which moment.
type Kill =
    /// A command of the `host` backend, at once: before Node reports the failed spawn.
    | CommandAtOnce
    /// A command of the `host` backend, after it reported that it did not start.
    | CommandAfterItsEnd
    /// The agent CLI's spawner, asked with a signal that fired before.
    | Agent

[<RequireQualifiedAccess>]
module Kill =

    let private each = [ CommandAtOnce; CommandAfterItsEnd; Agent ]

    /// The kill as the one argument of the program.
    let argument (kill: Kill) : string =
        match kill with
        | CommandAtOnce -> "command-at-once"
        | CommandAfterItsEnd -> "command-after-its-end"
        | Agent -> "agent"

    /// The kill that the arguments of the program name.
    let parse (words: string list) : Kill option =
        match words with
        | [ word ] -> each |> List.tryFind (fun kill -> argument kill = word)
        | _ -> None

/// The code that the program leaves with after a kill that ended nothing. It is not 0: Node
/// leaves with 0 when its loop has nothing left, so a program that made no kill would read as
/// one whose kill ended nothing.
[<Literal>]
let Survived = 42

/// The code that the program leaves with when the arrangement is not what the case names.
/// The program says why on its error stream.
[<Literal>]
let Refused = 43
