module Yession.Analyzers.Kept

/// One project's answer to an expensive question, kept only while that project is the one
/// being analyzed.
///
/// A rule asks its question once per FILE and the answer is the same for every file in a
/// project, so the answer is computed once and kept. What it must not do is keep the previous
/// project's as well. These answers hold FCS symbols, and a symbol retains the whole project's
/// check results behind it, so a cache keyed by project with no lifetime keeps every project
/// the run has touched — and `lint` analyzes all of them in ONE process. Measured over the
/// solution: the analyzer climbed to 10.5 GB, released nothing until it exited, and was killed
/// by GitHub's 16 GB runners four times in one day. Holding one project at a time peaks at
/// 4.1 GB and finishes in half the wall-clock, with byte-identical findings.
///
/// One slot is not a smaller cache, it is the right shape: `of'` is only ever asked about the
/// project it was handed, so a second entry could never be read before it was replaced.
type Answer<'a> () =
    let gate = obj ()
    let mutable kept: (string * 'a) option = None

    /// The answer for `project`, computed when the slot holds someone else's. Computing under
    /// the lock is deliberate — it is what stops two files of the same project each starting
    /// the walk the other is already doing.
    member _.For (project: string, compute: unit -> 'a) =
        lock gate (fun () ->
            match kept with
            | Some (p, answer) when p = project -> answer
            | _ ->
                let answer = compute ()
                kept <- Some (project, answer)
                answer)
