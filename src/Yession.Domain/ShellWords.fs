namespace Yession.Domain

/// A command line as a POSIX shell would read it into words — and nothing else a shell
/// would do. Quotes group, a backslash escapes, whitespace separates; no variable is
/// expanded, no glob, no redirection. This is how compose reads the string form of
/// `entrypoint` and `command`, and it is taken as it is so a line copied from a compose
/// file means the same thing here.
///
/// Not a shell: a string that needs expansion to mean what it says is one to write as a
/// list, or to run through an actual `sh -c` on purpose.
module ShellWords =

    /// The words of `raw`, or why it cannot be read as words (an unterminated quote, a
    /// trailing backslash).
    let split (raw: string) : Result<string list, string> =
        let words = ResizeArray<string> ()
        let word = System.Text.StringBuilder ()
        // `inWord` tells an empty quoted word (`''`) from no word at all.
        let mutable inWord = false
        let mutable quote : char option = None
        let mutable escaping = false
        let finish () =
            if inWord then
                words.Add (string word)
                word.Clear () |> ignore
                inWord <- false
        for c in raw do
            match quote, escaping with
            | _, true ->
                // Inside double quotes a backslash only escapes what it has to: the
                // quote, itself. Elsewhere it escapes anything.
                (match quote with
                 | Some '"' when c <> '"' && c <> '\\' -> word.Append('\\').Append c |> ignore
                 | _ -> word.Append c |> ignore)
                escaping <- false
            | Some '\'', false ->
                if c = '\'' then quote <- None else word.Append c |> ignore
            | Some '"', false ->
                if c = '"' then quote <- None
                elif c = '\\' then escaping <- true
                else word.Append c |> ignore
            | _, false ->
                if c = '\'' || c = '"' then
                    quote <- Some c
                    inWord <- true
                elif c = '\\' then
                    escaping <- true
                    inWord <- true
                elif System.Char.IsWhiteSpace c then finish ()
                else
                    word.Append c |> ignore
                    inWord <- true
        if escaping then Error "ends with a backslash that escapes nothing"
        elif quote.IsSome then Error (sprintf "a %c quote is never closed" quote.Value)
        else
            finish ()
            Ok (List.ofSeq words)
