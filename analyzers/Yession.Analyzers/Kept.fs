module Yession.Analyzers.Kept

open System.Runtime.CompilerServices
open FSharp.Compiler.CodeAnalysis

/// One project's answer to an expensive question, kept exactly as long as that project's check
/// results are.
///
/// A rule asks its question once per FILE and the answer is the same for every file in a
/// project, so the answer is computed once and kept. What it must not do is outlive the
/// project. These answers hold FCS symbols, and a symbol retains the whole project's check
/// results behind it, so a cache keyed by project with no lifetime keeps every project the run
/// has touched — and `lint` analyzes all of them in ONE process. Measured over the solution: the
/// analyzer climbed to 10.5 GB, released nothing until it exited, and was killed by GitHub's
/// 16 GB runners four times in one day.
///
/// So the key is the project's check results themselves, held weakly. The CLI holds them while
/// it analyzes that project's files and lets go when it is done, and the answer goes with them:
/// the lifetime a cache keyed by project needed, with nothing to evict by hand.
///
/// It replaced a single slot that held one project at a time. The CLI analyzes projects in
/// parallel, so the slot was handed back and forth between them, and each switch recomputed an
/// answer it had just dropped, under one lock every project's files waited on. Over the
/// solution, on a 4-core box, the analyzer run went from 389s to 226-232s and its peak from
/// 8.3 GB to at most 7.2 GB, with identical findings over the product and the fixtures.
type Answer<'a> () =
    let kept = ConditionalWeakTable<FSharpCheckProjectResults, Lazy<'a>> ()

    /// The answer for the project `results` describe, computed by the first of its files to
    /// ask. A file of the same project that asks meanwhile waits for that walk rather than
    /// starting its own; a file of another project does not wait at all.
    member _.For (results: FSharpCheckProjectResults, compute: unit -> 'a) : 'a =
        let create = ConditionalWeakTable<FSharpCheckProjectResults, Lazy<'a>>.CreateValueCallback (fun _ -> lazy (compute ()))
        kept.GetValue(results, create).Value
