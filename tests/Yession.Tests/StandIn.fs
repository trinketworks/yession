module Yession.Tests.StandIn

// A child process for a case that needs one to do something small: exit with a code, wait to
// be killed, say what it can see. Each used to be a JavaScript program passed to `node -e`,
// and nothing type-checked those strings: a misspelt `process.env` field read as `undefined`
// and the case went red, or green, for a reason its source did not contain.
//
// What a stand-in does is an `Act`, and a case starts one with `command`. The act travels as
// the child's arguments, and `StandInMain.fs`, the program the child runs, reads them back
// with `parse`. Both directions are here, beside the type, so they cannot drift apart.

open Node.Api

/// Something about the child's environment that an `ExitIf` asks of it.
type Condition =
    | Equal of name: string * value: string
    | Set of name: string
    | Unset of name: string

/// What a stand-in does once it has started.
type Act =
    /// Exit at once with this code.
    | Exit of code: int
    /// Do nothing until killed, for at most a minute, so a case that never kills it leaves
    /// nothing behind for long.
    | Linger
    /// Exit with `matched` when every condition holds, and `otherwise` when one does not.
    | ExitIf of conditions: Condition list * matched: int * otherwise: int
    /// Exit with `matched` when the child was started in this directory, and `otherwise` when
    /// it was not. Both sides are resolved first, so a symlinked temp directory still matches.
    | ExitIfStartedIn of directory: string * matched: int * otherwise: int
    /// Exit with `matched` when exactly the standard streams marked `true` are character
    /// devices, in the order stdin, stdout, stderr, and `otherwise` when they are not. An
    /// ignored stream is `/dev/null`, which is one, and a pipe never is.
    | ExitIfDevices of standard: (bool * bool * bool) * matched: int * otherwise: int
    /// Print `<name>=<value>`, or `<name>=absent` when the variable is not set.
    | PrintVariable of name: string

[<RequireQualifiedAccess>]
module Act =

    let private condition (condition: Condition) : string =
        match condition with
        | Equal (name, value) -> sprintf "equal:%s=%s" name value
        | Set name -> "set:" + name
        | Unset name -> "unset:" + name

    let private flag (device: bool) : string = if device then "c" else "-"

    /// The act as the child's arguments.
    let arguments (act: Act) : string list =
        match act with
        | Exit code -> [ "exit"; string code ]
        | Linger -> [ "linger" ]
        | ExitIf (conditions, matched, otherwise) ->
            [ "exit-if"; string matched; string otherwise ] @ List.map condition conditions
        | ExitIfStartedIn (directory, matched, otherwise) ->
            [ "exit-if-started-in"; directory; string matched; string otherwise ]
        | ExitIfDevices ((stdin, stdout, stderr), matched, otherwise) ->
            [ "exit-if-devices"; flag stdin + flag stdout + flag stderr; string matched; string otherwise ]
        | PrintVariable name -> [ "print-variable"; name ]

    let private code (text: string) : Result<int, string> =
        match System.Int32.TryParse text with
        | true, value -> Ok value
        | false, _ -> Error (sprintf "not an exit code: %s" text)

    let private codes (matched: string) (otherwise: string) : Result<int * int, string> =
        match code matched, code otherwise with
        | Ok m, Ok o -> Ok (m, o)
        | Error e, _
        | _, Error e -> Error e

    let private conditionOf (text: string) : Result<Condition, string> =
        match text.Split ([| ':' |], 2) with
        | [| "equal"; assignment |] ->
            match assignment.Split ([| '=' |], 2) with
            | [| name; value |] -> Ok (Equal (name, value))
            | _ -> Error (sprintf "not an assignment: %s" assignment)
        | [| "set"; name |] -> Ok (Set name)
        | [| "unset"; name |] -> Ok (Unset name)
        | _ -> Error (sprintf "not a condition: %s" text)

    let private flagOf (text: char) : Result<bool, string> =
        match text with
        | 'c' -> Ok true
        | '-' -> Ok false
        | other -> Error (sprintf "not a device flag: %c" other)

    let private collect (parsed: Result<Condition list, string>) (next: Result<Condition, string>) =
        match parsed, next with
        | Ok conditions, Ok condition -> Ok (condition :: conditions)
        | Error e, _
        | _, Error e -> Error e

    /// The act the child's arguments name, or why they name none.
    let parse (arguments: string list) : Result<Act, string> =
        match arguments with
        | [ "exit"; c ] -> code c |> Result.map Exit
        | [ "linger" ] -> Ok Linger
        | "exit-if" :: m :: o :: conditions ->
            match codes m o, conditions |> List.map conditionOf |> List.fold collect (Ok []) with
            | Ok (m, o), Ok conditions -> Ok (ExitIf (List.rev conditions, m, o))
            | Error e, _
            | _, Error e -> Error e
        | [ "exit-if-started-in"; directory; m; o ] ->
            codes m o |> Result.map (fun (m, o) -> ExitIfStartedIn (directory, m, o))
        | [ "exit-if-devices"; flags; m; o ] when flags.Length = 3 ->
            match flagOf flags.[0], flagOf flags.[1], flagOf flags.[2], codes m o with
            | Ok stdin, Ok stdout, Ok stderr, Ok (m, o) -> Ok (ExitIfDevices ((stdin, stdout, stderr), m, o))
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e
        | [ "print-variable"; name ] -> Ok (PrintVariable name)
        | _ -> Error (sprintf "not a stand-in act: %s" (String.concat " " arguments))

/// The compiled `StandInMain.fs`, beside the suite's own entry, which is this process's
/// script. Absolute, so a child started in another directory still finds it.
let private entry () : string = path.join (path.dirname ``process``.argv.[1], "StandInMain.js")

/// The arguments that start a stand-in doing `act`, after the path of Node itself.
let command (act: Act) : string list = entry () :: Act.arguments act
