namespace Yession.Domain.Sandboxes

open System

// A variable's value composed over what lies beneath it (`${env.NAME}`). Its own file, ahead of
// `Sandbox.fs`, because a sandbox's policy carries templates for its backend to resolve, and
// ahead of `Environment.fs`, whose declaration values can be one.

/// One piece of a variable's value that is composed rather than written out.
[<RequireQualifiedAccess>]
type TemplatePart =
    /// Text, as written.
    | Literal of string
    /// `${env.NAME}`: the value NAME would have in this sandbox without the declaration that
    /// says this — the image's own `ENV` under docker, the sandbox's baseline elsewhere, and
    /// whatever resources granted on top. Empty where it would have none.
    | Beneath of name: string

/// A variable's value composed from pieces, rendered by the backend once it knows what lies
/// beneath — which for a container is only after its image has been pulled or built.
type EnvTemplate = TemplatePart list

module EnvTemplate =

    /// The one namespace this build knows. Others are refused by name, so a reference a later
    /// build would understand is never read as text this one silently kept.
    let private namespaces = [ "env" ]

    let private isNameChar (c: char) =
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'

    /// Whether `text` asks for composition at all. A value without `${` is plain text, and
    /// stays the case it always was.
    let composes (text: string) : bool = text.Contains "${"

    /// `text` as a template: `${env.NAME}` is a reference, `$${` is a literal `${`, and
    /// anything else is text. Refuses a reference it cannot read, naming it — an unclosed
    /// one, an unknown namespace, a name no environment could carry.
    let parse (text: string) : Result<EnvTemplate, string> =
        let literal (acc: TemplatePart list) (piece: string) =
            match acc with
            | TemplatePart.Literal before :: rest -> TemplatePart.Literal (before + piece) :: rest
            | _ -> TemplatePart.Literal piece :: acc
        let rec go (at: int) (acc: TemplatePart list) =
            if at >= text.Length then Ok (List.rev acc)
            elif text.Substring(at).StartsWith "$${" then go (at + 3) (literal acc "${")
            elif text.Substring(at).StartsWith "${" then
                match text.IndexOf ('}', at + 2) with
                | -1 -> Error (sprintf "'%s' opens a reference at '${' that never closes with '}'" text)
                | close ->
                    let inner = text.Substring (at + 2, close - at - 2)
                    match inner.Split ([| '.' |], 2) with
                    | [| space; name |] when List.contains space namespaces ->
                        if name <> "" && Seq.forall isNameChar name then go (close + 1) (TemplatePart.Beneath name :: acc)
                        else Error (sprintf "'${%s}' names no variable an environment could carry (letters, digits and '_')" inner)
                    | [| space; _ |] ->
                        Error (
                            sprintf
                                "'${%s}' names '%s', which this build does not know (it knows %s) — write '$${' for a literal '${'"
                                inner
                                space
                                (namespaces |> List.map (sprintf "%s.NAME") |> String.concat ", "))
                    | _ ->
                        Error (sprintf "'${%s}' is not a reference — write '${env.NAME}', or '$${' for a literal '${'" inner)
            else go (at + 1) (literal acc (string text.[at]))
        go 0 []

    /// The template as it would be written, so a declaration that crosses the command gate
    /// reads back as the file said it.
    /// Text as a template would write it: every `${` doubled. Split and joined rather than
    /// `Replace`d, because under Fable a replacement string is JavaScript's, where `$$` means
    /// one `$` — the escape would undo itself on the way out.
    let escape (text: string) : string = String.Join ("$${", text.Split ([| "${" |], System.StringSplitOptions.None))

    let render (template: EnvTemplate) : string =
        template
        |> List.map (fun part ->
            match part with
            | TemplatePart.Literal text -> escape text
            | TemplatePart.Beneath name -> sprintf "${env.%s}" name)
        |> String.concat ""

    /// The value, given what lies beneath this sandbox's declaration. A reference to a
    /// variable nothing beneath sets contributes no text — which is what a shell's `$NAME`
    /// does, and what an author appending to a search path wants of an image that never set
    /// one — so the absent case is the empty piece, not an absence passed on.
    let resolve (beneath: string -> string option) (template: EnvTemplate) : string =
        template
        |> List.map (fun part ->
            match part with
            | TemplatePart.Literal text -> [ text ]
            | TemplatePart.Beneath name -> beneath name |> Option.toList)
        |> List.concat
        |> String.concat ""

    /// Every template in `derived`, resolved over `beneath` — the environment the sandbox
    /// would have without them. So a variable composed over itself extends what it would
    /// otherwise have been, and one derived variable sees what lies beneath another, never
    /// what that other composed. Order-free, which is what makes it a map.
    let resolveAll (beneath: Map<string, string>) (derived: Map<string, EnvTemplate>) : Map<string, string> =
        derived |> Map.map (fun _ template -> resolve (fun name -> Map.tryFind name beneath) template)

