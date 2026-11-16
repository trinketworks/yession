module Yession.Tests.LaunchRehearsal

// What a case and the launch rehearsal share: which launch the rehearsal makes, and the codes
// that it leaves with. `LaunchRehearsalMain.fs` is the program, and it says why there is one.
// Both directions are here, beside the type, so the case and the program cannot drift apart.
// A case opens this file and not the program, because the top level of the program runs.

/// One program that the system does not run, by how Node says so.
type Program =
    /// No file system has it. The spawn answers with a child that has no pid, and Node
    /// reports the cause later, as the `error` of that child.
    | NotThere
    /// Its path goes through a file. Node throws from the spawn, with the code of the system.
    | ThroughAFile
    /// Its name has a NUL in it. Node throws from the spawn, with a sentence of its own.
    | NotAName

[<RequireQualifiedAccess>]
module Program =

    /// The path that the case gives the Manager as the program of a session.
    let path (program: Program) : string =
        match program with
        | NotThere -> "/yession-no-such-program"
        | ThroughAFile -> "/dev/null/session"
        | NotAName -> "/yession\000no-name"

/// One launch that the rehearsal makes.
type Launch =
    /// A Manager launches a session whose program is one that the system does not run.
    | Of of Program
    /// A launch alone, with no Manager around it, of a session that ends before it is ready.
    /// The deadline of the launch is far away.
    | EndsBeforeReady

[<RequireQualifiedAccess>]
module Launch =

    let private each = [ Of NotThere; Of ThroughAFile; Of NotAName; EndsBeforeReady ]

    /// The launch as the first argument of the rehearsal.
    let argument (launch: Launch) : string =
        match launch with
        | Of NotThere -> "not-there"
        | Of ThroughAFile -> "through-a-file"
        | Of NotAName -> "not-a-name"
        | EndsBeforeReady -> "ends-before-ready"

    /// The launch that an argument of the rehearsal names.
    let parse (word: string) : Launch option =
        each |> List.tryFind (fun launch -> argument launch = word)

/// The code that the rehearsal leaves with after the launch answered with a failure. It is
/// not 0: Node leaves with 0 when its loop has nothing left, so a rehearsal that got no
/// answer would read as one that got it.
[<Literal>]
let Answered = 42

/// The code that the rehearsal leaves with when what happened is not a failure of the launch:
/// the arrangement is wrong, the launch gave a session, or the launch threw. The rehearsal
/// says which on its error stream.
[<Literal>]
let Refused = 43
