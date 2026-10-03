namespace Yession.Domain.Sandboxes

open Yession.Domain

// What is worth SAYING about a `yession.yaml` that decoded.
//
// A file is read in stages, each a pure function of the one before, so each can be pinned in
// the cheap tier on its own:
//
//   text ──parse──▶ tree + SourceIndex ──decode──▶ ConfigFile ──analyze──▶ LocatedFinding list
//
// Parsing is the bridge's (`RepoConfig.fs`: the only stage that touches a parser, and the only
// one that reads a file). Decoding is `ConfigFile.decoder` (Yession.Session), and what it refuses
// fails the file. This module is the stage after: a file that decoded is honoured as written,
// and an analyzer only has something to TELL its author — a thing that is legal and almost
// certainly not what they meant. A finding never changes what the file does; a refusal
// belongs in the decoder, where it fails the file before anything runs.
//
// An analyzer answers in key paths, never lines: a `ConfigFile` has no memory of where in the
// text a value was written, and should not — it is also what crosses the command gate, where
// there is no text. The `SourceIndex` the parse built puts the line back, and a finding the
// index cannot place is still said, just without one.

/// Where a thing was written: 1-based, as an editor counts.
[<RequireQualifiedAccess>]
type SourceSpan = { Line : int; Column : int }

/// One step from the root of a file to a value: a mapping key, or a position in a sequence.
[<RequireQualifiedAccess>]
type KeyStep =
    | Key of string
    | Index of int

/// A value's address in a file, as its author would write it: `sandboxes.dev.wants[1]`.
type KeyPath = KeyStep list

module KeyPath =

    let render (path: KeyPath) : string =
        path
        |> List.mapi (fun i step ->
            match step with
            | KeyStep.Key key when i = 0 -> key
            | KeyStep.Key key -> "." + key
            | KeyStep.Index index -> sprintf "[%d]" index)
        |> String.concat ""

    /// Where one sandbox's declaration is, which every per-sandbox path starts with.
    let sandbox (name: SandboxName) : KeyPath = [ KeyStep.Key "sandboxes"; KeyStep.Key (SandboxName.value name) ]

/// Where each key and each sequence item the file WROTE sits in its text. Only what was
/// written: a value an alias expanded into has no position of its own, and is not given one
/// it did not have.
type SourceIndex = Map<KeyPath, SourceSpan>

/// Something an analyzer has to tell a file's author. Never a refusal — see the header.
[<RequireQualifiedAccess>]
type Finding =
    { /// The declaration it is about, when it is about one.
      Sandbox : SandboxName option
      /// The value it is about.
      Path : KeyPath
      /// Said to the author, in the terms of what they wrote.
      Message : string }

/// A finding, and where the index places it.
[<RequireQualifiedAccess>]
type LocatedFinding = { Finding : Finding; At : SourceSpan option }

module LocatedFinding =

    /// Where, as a person reads it: `sandboxes.dev.wants[1], line 12`.
    let where (located: LocatedFinding) : string =
        let path = KeyPath.render located.Finding.Path
        match located.At with
        | Some at -> sprintf "%s, line %d" path at.Line
        | None -> path

/// One thing worth saying about a file's sandbox declarations — a repo's or an operator's,
/// which are written in one form under one `sandboxes:` key. Pure, and about the FILE: what
/// this host offers, and anything else a particular session knows, is not an analyzer's to
/// ask.
type Analyzer = Map<SandboxName, SandboxDecl> -> Finding list

module Analyzers =

    /// A resource named under both `uses` and `wants`. Legal — the need wins, and the want
    /// says nothing — but one of the two lines is dead, and which one the author meant is
    /// exactly the question: a need refuses on a host that does not offer it, a want does not.
    let selectedTwice : Analyzer =
        fun sandboxes ->
            sandboxes
            |> Map.toList
            |> List.collect (fun (name, decl) ->
                decl.Wants
                |> List.indexed
                |> List.filter (fun (_, wanted) -> List.contains wanted decl.Uses)
                |> List.map (fun (index, wanted) ->
                    { Finding.Sandbox = Some name
                      Finding.Path = KeyPath.sandbox name @ [ KeyStep.Key "wants"; KeyStep.Index index ]
                      Finding.Message =
                        sprintf
                            "'%s' is under both uses and wants: uses already requires it, so this want says nothing — keep the one you mean (uses refuses on a host that does not offer it, wants does not)"
                            (ResourceName.value wanted) }))

    /// Each variable a declaration sets, with where it is written.
    let private variables (name: SandboxName) (decl: SandboxDecl) : (string * EnvironmentVariableRef * KeyPath) list =
        decl.EnvironmentVariables
        |> Map.toList
        |> List.map (fun (variable, value) -> variable, value, KeyPath.sandbox name @ [ KeyStep.Key "env"; KeyStep.Key variable ])

    let private proxies (value: EnvironmentVariableRef) : ProxyValue list =
        match value with
        | Derived template -> EnvTemplate.proxies template
        | PlainValue _
        | SecretRef _
        | Lent _ -> []

    let private asksFor (decl: SandboxDecl) (wanted: ProxyValue list) : bool =
        decl.EnvironmentVariables |> Map.exists (fun _ value -> proxies value |> List.exists (fun v -> List.contains v wanted))

    /// A sandbox told the proxy's URL and nothing of its authority. Legal — a client may carry
    /// its own trust — but a client that trusts its image's store alone refuses every
    /// connection the proxy answers, because the proxy answers them with its own certificate.
    let proxyWithoutTrust : Analyzer =
        fun sandboxes ->
            sandboxes
            |> Map.toList
            |> List.collect (fun (name, decl) ->
                if asksFor decl [ ProxyValue.CaFile; ProxyValue.CaDir ] then []
                else
                    variables name decl
                    |> List.filter (fun (_, value, _) -> List.contains ProxyValue.Https (proxies value))
                    |> List.map (fun (variable, _, path) ->
                        { Finding.Sandbox = Some name
                          Finding.Path = path
                          Finding.Message =
                            sprintf
                                "%s sends HTTPS through the credential proxy, and nothing here trusts the proxy's authority — a client that trusts only its image's roots refuses every host the proxy answers; add SSL_CERT_FILE: ${proxy.ca-file}, or ${proxy.ca-dir} composed into SSL_CERT_DIR"
                                variable }))

    /// A token lent with no proxy URL beside it. A stand-in is worth something only on its way
    /// through the proxy, which swaps it; a sandbox that reaches the network directly (docker,
    /// the host) sends it nowhere near the proxy unless told `${proxy.https}`. Under srt the
    /// proxy is reached without being named, which is why this is a note and not a refusal.
    let tokenWithoutProxy : Analyzer =
        fun sandboxes ->
            sandboxes
            |> Map.toList
            |> List.collect (fun (name, decl) ->
                if asksFor decl [ ProxyValue.Https ] then []
                else
                    variables name decl
                    |> List.choose (fun (variable, value, path) ->
                        match value with
                        | Lent connection ->
                            Some
                                { Finding.Sandbox = Some name
                                  Finding.Path = path
                                  Finding.Message =
                                    sprintf
                                        "%s is lent a stand-in for %s, which is worth something only through the credential proxy — in a container or on the host, add HTTPS_PROXY: ${proxy.https} (srt reaches the proxy without it)"
                                        variable
                                        (ConnectionName.value connection) }
                        | _ -> None))

    /// The proxy's authority directory set as a variable's WHOLE store: it holds that one
    /// authority, so a client told only it trusts the proxy and no other host on the internet.
    /// `${proxy.ca-file}` is the whole-store answer; a directory is for adding to one.
    let trustReplacesStore : Analyzer =
        fun sandboxes ->
            sandboxes
            |> Map.toList
            |> List.collect (fun (name, decl) ->
                variables name decl
                |> List.choose (fun (variable, value, path) ->
                    match value with
                    | Derived template when
                        List.contains ProxyValue.CaDir (EnvTemplate.proxies template)
                        && not (template |> List.contains (TemplatePart.Beneath variable))
                        ->
                        Some
                            { Finding.Sandbox = Some name
                              Finding.Path = path
                              Finding.Message =
                                sprintf
                                    "%s is the proxy's authority directory and nothing else, which holds that one authority — every other host's certificate would fail; compose it over what is there (${env.%s}:${proxy.ca-dir}), or use ${proxy.ca-file}, which carries every root"
                                    variable
                                    variable }
                    | _ -> None))

    /// Every analyzer a file is read with.
    let all : Analyzer list = [ selectedTwice; proxyWithoutTrust; tokenWithoutProxy; trustReplacesStore ]

module ConfigAnalysis =

    /// Everything `analyzers` find in a file's `sandboxes`, placed by `index`, in the order
    /// the file reads.
    let run (analyzers: Analyzer list) (index: SourceIndex) (sandboxes: Map<SandboxName, SandboxDecl>) : LocatedFinding list =
        analyzers
        |> List.collect (fun analyze -> analyze sandboxes)
        |> List.map (fun finding -> { LocatedFinding.Finding = finding; LocatedFinding.At = Map.tryFind finding.Path index })
        |> List.sortBy (fun located ->
            (located.At |> Option.map (fun at -> at.Line, at.Column) |> Option.defaultValue (System.Int32.MaxValue, 0)),
            KeyPath.render located.Finding.Path)
