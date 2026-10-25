module Yession.Host.KeyStore

// Where the secrets KEK lives (Plan 06): a function-shaped capability over the OS
// credential manager. The stored payload is "<kekId>:<keyB64u>" — id and key travel
// together, so the store can tell "sealed by a different key" from corruption. The
// product implementation (Keyring-backed, `detect`) arrives with the `Fable.Keyring`
// binding; `inMemory`/`random` serve tests and the ephemeral no-credential-manager
// path. There is deliberately NO plaintext-file store and NO env-var store: a host
// without a credential manager refuses persistence instead of degrading it.

type KeyStore =
    { Name : string
      /// Cheap probe: is the backing credential store actually usable here?
      Available : unit -> Async<bool>
      /// The stored KEK payload; None when absent — a first run, or a LOCKED macOS keychain,
      /// which reads as empty. `SecretStore.openStore` tells the two apart by its file.
      Get : unit -> Async<Result<string option, string>>
      Set : string -> Async<Result<unit, string>> }

/// Compose a KEK payload / split one. The id is any opaque string without ':'.
let payload (kekId: string) (kekB64u: string) : string = kekId + ":" + kekB64u

let splitPayload (raw: string) : Result<string * string, string> =
    match raw.IndexOf ':' with
    | i when i > 0 && i < raw.Length - 1 -> Ok (raw.Substring (0, i), raw.Substring (i + 1))
    | _ -> Error "malformed KEK payload (expected <kekId>:<key>)"

/// A mutable in-memory cell: the test double, and (seeded) the ephemeral path.
let inMemory () : KeyStore =
    let mutable stored : string option = None
    { Name = "in-memory"
      Available = fun () -> async { return true }
      Get = fun () -> async { return Ok stored }
      Set = fun value -> async { stored <- Some value; return Ok () } }

/// An in-memory store pre-seeded with a fresh random KEK — the ephemeral store's key:
/// it exists nowhere but this process and dies with it.
let random () : KeyStore =
    let store = inMemory ()
    let seeded = payload (Yession.Host.Interop.randomSecret ()) (SecretsCipher.generateKek ())
    { store with Get = fun () -> async { return Ok (Some seeded) } }

/// The product store: one OS credential-manager entry (service "yession", name after
/// the caller — the Manager uses "manager"), through the `@napi-rs/keyring` binding.
/// `Available` treats "no entry yet" as available and any backend error — no Secret
/// Service daemon, a locked keychain over SSH — as UNAVAILABLE, so the Manager refuses
/// persistence instead of failing or degrading.
let keyring (service: string) (account: string) : KeyStore =
    // Every call constructs + calls inside try: the Entry ctor itself may throw on
    // hosts with no credential store, and getPassword throws BOTH for "no entry" and
    // for backend failure — the message is the only discriminator keyring-rs exposes.
    let tryGet () : Result<string option, string> =
        try
            let entry = Fable.Keyring.Entry.entry service account
            try Ok (Some (entry.getPassword ()))
            with getError ->
                let message = getError.Message
                if message.Contains "No matching entry" || message.Contains "no matching entry" then Ok None
                else Error message
        with ctorError -> Error ctorError.Message
    { Name = sprintf "os-keyring (%s/%s)" service account
      Available =
        fun () ->
            async {
                match tryGet () with
                | Ok _ -> return true
                | Error _ -> return false
            }
      Get = fun () -> async { return tryGet () }
      Set =
        fun value ->
            async {
                try
                    (Fable.Keyring.Entry.entry service account).setPassword value
                    return Ok ()
                with setError -> return Error setError.Message
            } }

/// The product probe: the platform credential manager, or None — in which case the
/// Manager runs the ephemeral in-memory store (never a plaintext key file, never an
/// env-var key).
let detect () : Async<KeyStore option> =
    async {
        let store = keyring "yession" "manager"
        let! available = store.Available ()
        return if available then Some store else None
    }

// --- An operator's own store ----------------------------------------------------------------

/// How long a helper may take to answer. A KMS round trip is under a second; a helper
/// that has not answered in this long is stuck on something — a prompt, a network that
/// never comes — and the Manager would rather hear "failed" and retry than wait on it.
let private helperTimeoutMs = 30_000

/// How much a helper may write. A payload is a few hundred bytes; a kilobyte of stderr
/// is already a stack trace.
let private helperMaxBuffer = 64 * 1024

/// A store an OPERATOR brought: `command`, run once per operation, with the operation as its
/// one argument. The protocol is the one git and Docker credential helpers taught:
///
///   <command> get    stdout: the payload, or nothing when no key exists yet. Exit 0.
///                    Exit non-zero (stderr says why): the backend failed, and the Manager
///                    retries — a role whose credentials are not minted yet at boot, a KMS
///                    that throttled, a network that is not up.
///   <command> set    stdin: the payload. Exit 0 only once it is DURABLE, because the
///                    Manager seals the first entry the moment this returns.
///
/// The payload is opaque to the helper and travels on the pipes and nowhere else: never in an
/// argument and never in a variable, since `/proc` shows both to every process of the same
/// user. The helper inherits this process's environment and runs as its user, so it can reach
/// whatever the Manager can — an instance role, a Vault token, a TPM — and it is trusted as
/// the Manager's own binary is: an operator chose it by path.
///
/// What this buys over the keyring is a key backend the HOST does not have to provide. A
/// headless server has no Secret Service; it does have `aws kms decrypt`, `systemd-creds` or
/// `vault kv get`, and any of them is a ten-line helper. What it does not change: the raw KEK
/// is in this process between `get` and `SecretsCipher.importKey`, as it is between the
/// keyring's D-Bus reply and the same import.
let exec (command: string) : KeyStore =
    /// What the helper wrote to stdout, or nothing — Node hands over no text for a child that
    /// wrote none, and for `get` that absence is the answer "no key yet", not a blank to fill.
    let run (operation: string) (input: string) : Async<Result<string option, string>> =
        async {
            let! result =
                Fable.NodeExtras.AsyncChildProcesses.execFile
                    command
                    [ operation ]
                    { Cwd = None
                      Env = Fable.NodeExtras.ChildEnv.Adding Map.empty
                      MaxBuffer = Some helperMaxBuffer
                      Timeout = Some helperTimeoutMs
                      KillSignal = None
                      Input = Some input }
            let said = result.Stderr |> Option.map (fun s -> s.Trim ()) |> Option.filter (fun s -> s <> "")
            match result.Status with
            | Some 0 -> return Ok result.Stdout
            | Some status ->
                return Error (sprintf "%s %s exited %d%s" command operation status (said |> Option.map (sprintf ": %s") |> Option.defaultValue ""))
            | None ->
                return
                    Error (
                        sprintf
                            "%s %s did not run to completion (not found, not executable, or still running after %d ms)%s"
                            command
                            operation
                            helperTimeoutMs
                            (said |> Option.map (sprintf ": %s") |> Option.defaultValue "")
                    )
        }
    let get () : Async<Result<string option, string>> =
        async {
            match! run "get" "" with
            | Error e -> return Error e
            | Ok out ->
                match out |> Option.map (fun text -> text.Trim ()) with
                | None
                | Some "" -> return Ok None
                | Some payload -> return Ok (Some payload)
        }
    { Name = sprintf "exec (%s)" command
      // No separate probe: an operator who NAMED a store asked for it, and a helper that
      // cannot answer yet is a failure `SecretStore.openStoreWaiting` retries, not a host
      // without a store. Probing it would turn "IAM is not ready yet" into a refused boot.
      Available = fun () -> async { return true }
      Get = get
      Set = fun payload -> async { let! r = run "set" payload in return r |> Result.map ignore } }

/// What `--keystore` names, before anything is probed or run. A separate type from `KeyStore`
/// because the option is parsed before the boot decides whether a store is needed at all
/// (`--secrets ephemeral` needs none), and the parse must refuse a spelling it cannot read.
type KeyStoreSpec =
    /// The OS credential manager, probed — the default, and the only store with a "not here"
    /// answer.
    | Keyring
    /// An operator's helper at this path (`exec`).
    | Exec of command: string

module KeyStoreSpec =

    /// Resolve a `--keystore` argument. Absent is the keyring, the same shape as
    /// `SecretsMode.ofName`: an unknown spelling fails the boot, never defaults.
    let ofName (name: string option) : Result<KeyStoreSpec, string> =
        match name with
        | None
        | Some "keyring" -> Ok Keyring
        | Some spelled when spelled.StartsWith "exec:" ->
            match spelled.Substring 5 with
            | "" -> Error "--keystore exec: needs a path (exec:/path/to/helper)"
            | command -> Ok (Exec command)
        | Some other -> Error (sprintf "unknown keystore '%s' (expected keyring or exec:<path>)" other)

    /// How an operator spells this back: `ofName (Some (describe s)) = Ok s`.
    let describe (spec: KeyStoreSpec) : string =
        match spec with
        | Keyring -> "keyring"
        | Exec command -> "exec:" + command

/// The store a spec names, or None where the host has none to offer. Only the keyring can
/// answer None: it is the one store that is DETECTED rather than chosen, so it is the one
/// whose absence is a fact about the host rather than a failure of the store.
let resolve (spec: KeyStoreSpec) : Async<KeyStore option> =
    match spec with
    | Keyring -> detect ()
    | Exec command -> async { return Some (exec command) }
