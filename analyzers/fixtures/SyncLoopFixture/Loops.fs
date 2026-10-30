module SyncLoopFixture.Loops

let private wait () = async { return () }

/// A `for` whose body waits on nothing: one nested continuation per element.
let folds (xs: int list) =
    async {
        let mutable total = 0
        for x in xs do  // YES019
            total <- total + x
        return total
    }

/// A `while` whose body waits on nothing, the same thing by the other loop.
let counts (n: int) =
    async {
        let mutable i = 0
        while i < n do  // YES019
            i <- i + 1
        return i
    }

/// Nested: the outer body holds only another loop, and the inner waits on nothing, so both are
/// the stack's.
let nested (xss: int list list) =
    async {
        let mutable total = 0
        for xs in xss do  // YES019
            for x in xs do  // YES019
                total <- total + x
        return total
    }

/// Each iteration waits, which hands the stack back: what a loop in an `async` block is for.
let waits (xs: int list) =
    async {
        for _ in xs do
            do! wait ()
    }

/// `match!` is a wait too.
let matches (xs: int list) =
    async {
        for _ in xs do
            match! async { return 1 } with
            | 1 -> ()
            | _ -> ()
    }

/// Folded outside the block and bound: the remedy.
let outside (xs: int list) =
    let total = List.sum xs
    async { return total }

/// A loop outside any `async` block is a loop.
let plain (xs: int list) =
    let mutable total = 0
    for x in xs do
        total <- total + x
    total
