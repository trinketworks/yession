module Yession.Host.WorkSandboxes

// The session's WorkSandboxes, by name (Plan 15, stage 2).
//
// A session used to have exactly one environment, so it needed no name. Now the agent can
// ask for a `test` sandbox beside the `default` one — and asking twice returns the SAME
// one. That idempotence is the whole point: it is what lets the declarative form
// (`yession.yaml`, the follow-up plan) be nothing more than a fold of the file into these
// commands at boot, run on every boot, converging rather than accumulating.
//
// The rule when an ask does NOT match what is running is the interesting half. Same name,
// same configuration: hand back the running one and record nothing, because nothing
// happened. Same name, DIFFERENT configuration: refuse, and say what differs. Never
// recreate silently — a sandbox has processes in it, and "converging" by killing
// somebody's build is not convergence.
//
// Credential forwarding is named, never a flag. `forward = ["github"]` provisions that
// credential for the TURN HUMAN (Plan 08 precedence, applied by the composition) into the
// sandbox at spawn. What "provisions" means is the source's: for `github` it is a route to
// the session's git gateway (`GitGateway`), which holds the credential and lends it per
// request — the sandbox never carries the value. The event records WHICH names and WHOSE,
// and the event type cannot carry a value.

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.Domain.Terminals
open Yession.SessionProcess

/// What a forwarded credential puts in a sandbox. Git's config is one variable everything
/// shares (`GIT_CONFIG_COUNT`) and has to be APPENDED to, where a plain variable is simply set
/// — `Sandboxes.withGitConfig` is the difference — and a route is only a route if the
/// sandbox's egress admits the host it names, its proxy is told to send that host's HTTPS
/// where the credential is, and the sandbox can read what it has to trust to get there.
type Provision =
    { Env : Map<string, string>
      GitConfig : (string * string) list
      /// Hosts the sandbox must be allowed to reach for the provision to work. Read by a
      /// backend that filters egress (srt); the rest have nothing to widen.
      Domains : string list
      /// Files the sandbox must be able to read — a trust bundle. Read by a backend that
      /// confines reads (srt); the rest read everything already.
      Reads : string list
      /// Hosts whose HTTPS the credential proxy answers (`Interception`).
      Intercept : Interception option
      /// Files the sandbox must SEE, at a path of its own — a trust bundle mounted into a
      /// container. Read by a backend that materialises mounts (docker); the rest see the
      /// host's paths and are told those instead.
      Binds : ResourceMount list }

module Provision =

    let empty : Provision = { Env = Map.empty; GitConfig = []; Domains = []; Reads = []; Intercept = None; Binds = [] }

    let merge (a: Provision) (b: Provision) : Provision =
        { Env = Sandboxes.mergeEnv a.Env b.Env
          GitConfig = a.GitConfig @ b.GitConfig
          Domains = List.distinct (a.Domains @ b.Domains)
          Reads = List.distinct (a.Reads @ b.Reads)
          // One credential proxy per session: two provisions naming it are the same socket,
          // and the hosts are the union of what each routes there.
          Intercept =
            match a.Intercept, b.Intercept with
            | Some x, Some y ->
                Some { Interception.Socket = y.Socket; Interception.Hosts = List.distinct (x.Hosts @ y.Hosts) }
            | x, None -> x
            | None, y -> y
          Binds = List.distinct (a.Binds @ b.Binds) }

/// What the session's credential proxy provides a sandbox whose declaration asks for it
/// (`${proxy.…}`). Asked only for what a declaration names: a sandbox that names none is one
/// the proxy never hears of.
type ProxyProvider =
    { /// The values asked for, and what the sandbox needs to use them — or why it cannot
      /// have them, which refuses its start.
      Provide : SandboxRef -> ProxyValue list -> Result<Map<ProxyValue, string> * Provision, string>
      /// The proxy forgets a sandbox it provided for.
      Release : SandboxRef -> unit }

module ProxyProvider =

    /// A composition with no credential proxy: a declaration that asks for one is refused.
    let none : ProxyProvider =
        { Provide = fun _ _ -> Error "this session runs no credential proxy, so nothing can provide a '${proxy.…}' reference"
          Release = ignore }

/// What forwarding one credential into one sandbox came to.
[<RequireQualifiedAccess>]
type CredentialForwarding =
    | Forwarded of Provision
    /// This sandbox cannot be given it, and why — a backend the source has no route into.
    | Unforwardable of string

/// A credential this session knows how to forward, by name. The registry holds the name;
/// what lands in a sandbox is the composition's business, and WHOSE is not a question a
/// sandbox answers at all: a forward is a route every block in the sandbox shares, and each
/// block is lent the credential of its own act (Plan 08 precedence, resolved per block).
/// A start therefore needs nobody signed in — a repo's file asking at boot gets its route,
/// and the first block somebody runs in there is the first thing that names a person.
type CredentialSource =
    { Name : ConnectionName
      /// The routes this source can forward by. A route it does not offer is refused when a
      /// sandbox needs it, rather than forwarded as nothing.
      Routes : ConnectionRoute list
      /// Provision these routes, and no others, into one sandbox. `Unforwardable` is a
      /// legible refusal rather than a silent start without them: a sandbox that was asked
      /// to forward `github` and did not is a sandbox whose `git push` fails much later,
      /// somewhere less informative.
      Provision : SandboxRef -> ConnectionRoute list -> Async<CredentialForwarding>
      /// Take back what `Provision` gave. Called when the sandbox stops, so that whatever a
      /// provision opened (a gateway route, the loans under it) lives exactly as long as
      /// the sandbox does.
      Revoke : SandboxRef -> unit
      /// What one BLOCK in the sandbox is lent for its act — put at the head of the block's
      /// line by the terminal manager. Per block where `Provision` is per sandbox: the
      /// sandbox carries what every block shares (a route), a block carries whose it is.
      /// Nothing, never a refusal: a block lent nothing runs on what its shell has, and what
      /// its git is then told is the gateway's sentence to say.
      ///
      /// Lent by the routes the sandbox forwards and no others: a block in a sandbox that
      /// holds `git` alone is lent what its git needs, and nothing its API client could spend.
      ///
      /// `lentInto` is the variables the sandbox's declaration lends this connection's token
      /// in (`${<connection>.token}`), which the source puts a stand-in in beside whatever it
      /// lends of its own accord.
      Lend : Authority -> SandboxRef -> TerminalId -> BlockId option -> ConnectionRoute list -> string list -> Async<BlockEnv>
      /// Whatever a terminal's last block was lent is returned (`BlockLoans.Retire`).
      Retire : TerminalId -> unit }

/// The variables a declaration lends each connection's token in (`${<connection>.token}`), by
/// connection.
let lentVariables (spec: EnvironmentSpec) : Map<ConnectionName, string list> =
    spec.EnvironmentVariables
    |> Map.toList
    |> List.choose (fun (variable, value) ->
        match value with
        | Lent connection -> Some (connection, variable)
        | PlainValue _
        | SecretRef _
        | Derived _ -> None)
    |> List.groupBy fst
    |> List.map (fun (connection, pairs) -> connection, pairs |> List.map snd)
    |> Map.ofList

/// One sandbox the session has. Present in the registry does NOT mean started — the
/// environment underneath is lazy, and `default` exists from boot without a sandbox
/// existing anywhere.
type RunningSandbox =
    { /// Which sandbox, scope included — a repo's `dev` and the session's own are two
      /// different sandboxes that happen to share a name.
      Ref : SandboxRef
      Backend : string
      /// What it was STARTED with, normalised — not what the composition then built around
      /// it. The session adds things of its own (the repos mount under docker), and a
      /// second ask that matched the request exactly would be refused for a difference the
      /// asker never asked for.
      ///
      /// Carrying the whole request rather than the forwarding alone is what lets "is this
      /// the same sandbox" be an equality instead of a judgement — which matters because
      /// the wrong answer either kills somebody's build or hands back a sandbox configured
      /// as something else.
      Request : SandboxRequest
      /// The connections it was provisioned with, and by which routes: what its selection
      /// reached on this host, less any route it only WANTED that this backend could not be
      /// reached by. What its blocks are lent for, and what a stop gives back.
      Forwarded : Map<ConnectionName, ConnectionRoute list>
      /// Who asked for it. `None` for `default`, which nobody asked for.
      StartedBy : ActorRef option
      StartedAt : DateTimeOffset option
      Environment : SessionEnvironment.SessionEnvironment }

/// A sandbox's forwards, said.
module ForwardedRoutes =

    let names (forwarded: Map<ConnectionName, ConnectionRoute list>) : ConnectionName list =
        forwarded |> Map.toList |> List.map fst

    /// `github (git, api)`, and so on — which connection, and by which routes.
    let describe (forwarded: Map<ConnectionName, ConnectionRoute list>) : string =
        forwarded
        |> Map.toList
        |> List.map (fun (connection, routes) ->
            sprintf "%s (%s)" (ConnectionName.value connection) (routes |> List.map ConnectionRoute.name |> String.concat ", "))
        |> String.concat ", "

    /// Two sets of forwards as one: a connection's routes are the union of both.
    let merge
        (a: Map<ConnectionName, ConnectionRoute list>)
        (b: Map<ConnectionName, ConnectionRoute list>)
        : Map<ConnectionName, ConnectionRoute list> =
        b
        |> Map.fold
            (fun merged connection routes ->
                let held = merged |> Map.tryFind connection |> Option.defaultValue []
                merged |> Map.add connection (List.distinct (held @ routes) |> List.sortBy ConnectionRoute.name))
            a

type WorkSandboxesConfig =
    { /// How a sandbox's backend describes itself, for the event and the query — a
      /// function of the ref because the backend is decided per SCOPE (the session's
      /// own keep the configured confinement, a repo's are containers), so one string
      /// for the whole registry would misdescribe half of it.
      Backend : SandboxRef -> string
      /// What a sandbox is FOR, when whoever declared it said — asked here for the same
      /// reason `Backend` is: it is a fact about the NAME, settled by whoever declared it,
      /// and this manager holds no declarations. Deliberately not on `SandboxRequest`: the
      /// request is what `Ensure` compares to decide two asks are the same sandbox, so a
      /// description in it would make editing prose in a repo's file read as a configuration
      /// change and refuse every running session until somebody stopped the container.
      Describe : SandboxRef -> string option
      /// Where a repo-owned sandbox sees its own checkout — `None` for the session's own,
      /// which has no repo. Asked here for the same reason `Backend` is: it is a fact about
      /// the NAME and the backend under it, and settling it anywhere earlier settles it in
      /// the wrong view.
      Checkout : SandboxRef -> string option
      Credentials : CredentialSource list
      /// The connections a spec's selection reaches on this host (`ForwardedConnections`),
      /// or why it reaches nothing. Asked here rather than carried on the request because it
      /// is the OPERATOR's profile that turns a name into a connection, and the request is
      /// what a repo's file said.
      Connections : EnvironmentSpec -> Result<ForwardedConnections, string>
      /// The session's credential proxy, for a sandbox whose declaration asks for it.
      Proxy : ProxyProvider
      /// The sandboxes the operator declared (`ProfileFile.Sandboxes`), as requests: the
      /// session has each from boot, as it has `default`. An operator's `default` replaces
      /// the built-in one.
      Standing : (SandboxName * SandboxRequest) list
      /// Build the environment for a sandbox: the spec it was asked to be, plus what the
      /// forwarded credentials provisioned. Synchronous and fallible — whether this
      /// backend can host what was asked for (a container under srt, say) is known without
      /// starting anything.
      Create : SandboxRef -> EnvironmentSpec -> Provision -> Result<SessionEnvironment.SessionEnvironment, string>
      Log : EventLog<SessionEvent>
      Clock : unit -> DateTimeOffset }

/// What an `Ensure` did, which is not the same question as what it returned.
///
/// A get-or-create tells its caller the sandbox is up; it does not tell them whether this
/// ask is what put it there, and some consequences belong only to the ask that did. A
/// repo's `setup:` is one: the fold that re-asks runs at boot and after every repo verb, so
/// a consequence attached to "the sandbox is up" would fire each time somebody touched a
/// checkout, while one attached to "this started it" fires once.
///
/// The registry is the only thing that can answer it — the equality that decides idempotence
/// is in here — so it says so rather than leaving each caller to infer it from a timestamp.
type SandboxOutcome =
    /// It was not running, and this ask started it.
    | SandboxStarted of RunningSandbox
    /// It was already running on this exact configuration. Nothing changed, and the
    /// timeline records nothing.
    | SandboxAlreadyRunning of RunningSandbox

module SandboxOutcome =

    /// The sandbox, however it came to be up — for the readers that do not care which.
    let sandbox =
        function
        | SandboxStarted entry
        | SandboxAlreadyRunning entry -> entry

type WorkSandboxes =
    { /// Get-or-create by name. Idempotent when the configuration matches; a legible
      /// error when it does not. The answer says which of those happened.
      /// Started on an AUTHORITY — the agent's, a person's, a repo's file's — and lent to
      /// nobody: what a sandbox's blocks spend is each block's own act's credential
      /// (`Loans`). The authority is here for attribution only: the start records who it
      /// was for. The cause is what made it come up, when that was not the ask itself.
      Ensure : Authority -> Cause option -> SandboxRef -> SandboxRequest -> Async<Result<SandboxOutcome, string>>
      Stop : ActorRef -> SandboxRef -> Async<Result<unit, string>>
      /// The environment a terminal runs in. Total, because a terminal has to be told no
      /// in the same shape it is told anything else — an unknown name resolves to an
      /// environment that refuses every spawn with the reason.
      EnvironmentFor : SandboxRef -> SessionEnvironment.SessionEnvironment
      /// What a block in a sandbox is lent for its act: every source the sandbox forwards,
      /// asked for the act's credential. Here beside `EnvironmentFor` because both are
      /// answered off what the sandbox was STARTED with — and a terminal that could spawn
      /// in a sandbox without asking would run its blocks on whoever started it.
      Loans : SessionTerminals.BlockLoans
      /// Every sandbox the session has, `default` first.
      Listed : unit -> RunningSandbox list
      /// Stop all of them — session shutdown. Sandbox lifetime is session lifetime.
      StopAll : unit -> Async<unit> }

/// Why a name found nothing, said with what WOULD have: the sandboxes there are. Three
/// sessions running spelt a repo's `dev` as `dev` and were told to start one — which the
/// repo's file had already done, under `owner/repo:dev`. The sentence has to name that.
let private unknownSandbox (name: SandboxRef) (existing: SandboxRef list) : string =
    // Who makes one is a different author per scope: the session's own are the operator's
    // (`sandboxes:` in the resources profile), and a repo's are its `yession.yaml`'s.
    let whoDeclares =
        match SandboxRef.scope name with
        | SessionOwned -> "this session's own sandboxes are the ones its operator declares, under `sandboxes:` in the resources profile"
        | RepoOwned _ -> "a repo's sandbox is one its yession.yaml declares, started with start_work_sandbox"
    match existing with
    | [] -> sprintf "there is no sandbox named '%s' in this session, and none at all — %s" (SandboxRef.render name) whoDeclares
    | existing ->
        sprintf
            "there is no sandbox named '%s' in this session — there is %s; %s"
            (SandboxRef.render name)
            (existing |> List.map (fun ref -> sprintf "'%s'" (SandboxRef.render ref)) |> String.concat ", ")
            whoDeclares

/// An environment for a name the session does not have. Refuses in the same shape a real
/// one does, naming what is wrong rather than the generic "no environment".
let private missing (reason: string) : SessionEnvironment.SessionEnvironment =
    { Ensure = fun _ _ -> async { return EnvironmentUnavailable reason }
      Spawn = fun _ _ -> async { return Error reason }
      SpawnPty = fun _ _ _ _ -> async { return Error reason }
      Stop = fun () -> async { return () }
      CurrentRef = fun () -> None
      Shell = fun () -> None
      Realisation = fun () -> [] }

let create (config: WorkSandboxesConfig) : Async<WorkSandboxes> =
    async {
        // Take back every provision a sandbox was given — the half of forwarding that a stop
        // owes, without which a route outlives the sandbox it was minted for. Hoisted above
        // the default's creation because the default is provisioned before its environment
        // exists, and a failed create has to give the route back.
        let revoke (name: SandboxRef) (forwarded: Map<ConnectionName, ConnectionRoute list>) : unit =
            for credential in ForwardedRoutes.names forwarded do
                config.Credentials
                |> List.tryFind (fun source -> source.Name = credential)
                |> Option.iter (fun source -> source.Revoke name)

        // Provision every named credential's routes into one sandbox, or say which one could
        // not be — revoking whatever was provisioned before the one that refused.
        let provisionForward
            (name: SandboxRef)
            (needed: Map<ConnectionName, ConnectionRoute list>)
            : Async<Result<Provision, string>> =
            async {
                let mutable provisioned = Provision.empty
                let mutable failure = None
                for credential, routes in Map.toList needed do
                    match failure with
                    | Some _ -> ()
                    | None ->
                        match config.Credentials |> List.tryFind (fun source -> source.Name = credential) with
                        | None ->
                            let known =
                                match config.Credentials |> List.map (fun s -> ConnectionName.value s.Name) with
                                | [] -> "this session forwards none"
                                | available -> "this session knows: " + String.concat ", " available
                            failure <-
                                Some (
                                    sprintf
                                        "there is no credential called '%s' (%s)"
                                        (ConnectionName.value credential)
                                        known)
                        | Some source ->
                            match routes |> List.filter (fun route -> not (List.contains route source.Routes)) with
                            | route :: _ ->
                                failure <-
                                    Some (
                                        sprintf
                                            "%s is not forwarded by %s here (it is by %s)"
                                            (ConnectionName.value credential)
                                            (ConnectionRoute.name route)
                                            (source.Routes |> List.map ConnectionRoute.name |> String.concat ", "))
                            | [] ->
                                match! source.Provision name routes with
                                | CredentialForwarding.Unforwardable reason -> failure <- Some reason
                                | CredentialForwarding.Forwarded provision ->
                                    provisioned <- Provision.merge provisioned provision
                match failure with
                | Some e ->
                    revoke name needed
                    return Error e
                | None -> return Ok provisioned
            }

        // What a spec's selection forwards, provisioned: every route it NEEDS or refuse, then
        // each route it only WANTS that this session has a source offering and this backend can
        // be reached by — a want is silent where it cannot be had, as it is for every leaf.
        let provisionConnections
            (name: SandboxRef)
            (spec: EnvironmentSpec)
            : Async<Result<Map<ConnectionName, ConnectionRoute list> * Provision, string>> =
            async {
                let routed (pairs: (string * ConnectionRoute) list) : Map<ConnectionName, ConnectionRoute list> =
                    pairs
                    |> List.choose (fun (connection, route) ->
                        ConnectionName.create connection |> Result.toOption |> Option.map (fun connection -> connection, [ route ]))
                    |> List.fold (fun held (connection, routes) -> ForwardedRoutes.merge held (Map.ofList [ connection, routes ])) Map.empty
                match config.Connections spec with
                | Error e -> return Error e
                | Ok connections ->
                    let needed = routed connections.Needed
                    match! provisionForward name needed with
                    | Error e -> return Error e
                    | Ok provision ->
                        let mutable provision = provision
                        let mutable forwarded = needed
                        for wanted, routes in Map.toList (routed connections.Wanted) do
                            match config.Credentials |> List.tryFind (fun source -> source.Name = wanted) with
                            | None -> ()
                            | Some source ->
                                let held = forwarded |> Map.tryFind wanted |> Option.defaultValue []
                                // One route at a time, so a want that cannot be had by one
                                // route is still had by the others.
                                for route in routes do
                                    if List.contains route source.Routes && not (List.contains route held) then
                                        match! source.Provision name [ route ] with
                                        | CredentialForwarding.Unforwardable _ -> ()
                                        | CredentialForwarding.Forwarded given ->
                                            provision <- Provision.merge provision given
                                            forwarded <- ForwardedRoutes.merge forwarded (Map.ofList [ wanted, [ route ] ])
                        return Ok (forwarded, provision)
            }

        // What a spec asks the credential proxy for, provided INTO the spec — so the sandbox
        // is built from values, and a backend never sees a reference nobody answered. The
        // request keeps the references as asked: it is what a second ask is compared to.
        let provideProxy (name: SandboxRef) (spec: EnvironmentSpec) : Result<EnvironmentSpec * Provision, string> =
            let asked =
                spec.EnvironmentVariables
                |> Map.toList
                |> List.collect (fun (_, value) ->
                    match value with
                    | Derived template -> EnvTemplate.proxies template
                    | PlainValue _
                    | SecretRef _
                    | Lent _ -> [])
                |> List.distinct
            match asked with
            | [] -> Ok (spec, Provision.empty)
            | asked ->
                match config.Proxy.Provide name asked with
                | Error e -> Error e
                | Ok (provided, provision) ->
                    let written =
                        spec.EnvironmentVariables
                        |> Map.toList
                        |> List.map (fun (variable, value) ->
                            match value with
                            | Derived template ->
                                EnvTemplate.provide provided template
                                |> Result.map (fun template -> variable, Derived template)
                            | other -> Ok (variable, other))
                    match written |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                    | Some e ->
                        config.Proxy.Release name
                        Error e
                    | None ->
                        let variables = written |> List.choose (function Ok pair -> Some pair | Error _ -> None) |> Map.ofList
                        Ok ({ spec with EnvironmentVariables = variables }, provision)

        // Everything a sandbox is built with beyond its declaration: the connections its
        // selection forwards, and what it asked the proxy for — and the spec with that
        // written in, which is what it is built FROM.
        // Whether a declaration's `${<connection>.token}` has anybody to lend it: the connection
        // forwarded by the route a token is spent through.
        let lendable (forwarded: Map<ConnectionName, ConnectionRoute list>) (connection: ConnectionName) : bool =
            forwarded |> Map.tryFind connection |> Option.exists (List.contains ConnectionRoute.Api)

        let provisionSelection
            (name: SandboxRef)
            (spec: EnvironmentSpec)
            : Async<Result<Map<ConnectionName, ConnectionRoute list> * Provision * EnvironmentSpec, string>> =
            async {
                match! provisionConnections name spec with
                | Error e -> return Error e
                // A token lent for a connection the sandbox does not forward would be lent by
                // nobody — every command would get nothing in that variable and no reason
                // why. So it refuses the start, naming what to select. A token is lent through
                // the credential proxy, so the route it needs is `api`.
                | Ok (forwarded, _) when
                    lentVariables spec
                    |> Map.exists (fun connection _ -> not (lendable forwarded connection))
                    ->
                    revoke name forwarded
                    let missing =
                        lentVariables spec
                        |> Map.toList
                        |> List.filter (fun (connection, _) -> not (lendable forwarded connection))
                        |> List.map (fun (connection, variables) ->
                            sprintf
                                "%s names '${%s.token}', and this sandbox does not forward '%s' by api"
                                (String.concat ", " variables)
                                (ConnectionName.value connection)
                                (ConnectionName.value connection))
                    return
                        Error (
                            sprintf
                                "%s — select a resource granting it under uses or wants, from a host that offers it"
                                (String.concat "; " missing)
                        )
                | Ok (forwarded, provision) ->
                    match provideProxy name spec with
                    | Error e ->
                        revoke name forwarded
                        return Error e
                    | Ok (built, proxied) -> return Ok (forwarded, Provision.merge provision proxied, built)
            }

        // The sandboxes this session has from boot: every one the operator declared, and no
        // other — `default` included, which is a name an operator gives a sandbox and not one
        // the session makes up. Each is created eagerly and started lazily, and its forward is
        // baked in HERE, before its environment, because a terminal reaches it through
        // `EnvironmentFor().Ensure`, which never runs the provisioning `ensure` below.
        let declared =
            config.Standing |> List.map (fun (name, request) -> SandboxRef.create SessionOwned name, request)

        // An operator's sandbox forwards what its selection reaches, as any sandbox does: a
        // connection it needs that cannot reach it is said — by the sandbox, which refuses
        // every spawn with the reason — rather than silently left out. The session still
        // boots: one mis-declared sandbox is not every sandbox.
        let standingEntry
            (ref: SandboxRef)
            (request: SandboxRequest)
            (provisioned: Result<Map<ConnectionName, ConnectionRoute list> * Provision * EnvironmentSpec, string>)
            : RunningSandbox =
            let forwarded, environment =
                match provisioned with
                | Error reason -> Map.empty, missing (sprintf "sandbox '%s' cannot start: %s" (SandboxRef.render ref) reason)
                | Ok (forwarded, provision, built) ->
                    match config.Create ref built provision with
                    | Ok environment -> forwarded, environment
                    | Error reason ->
                        revoke ref forwarded
                        config.Proxy.Release ref
                        Map.empty, missing (sprintf "sandbox '%s' cannot start: %s" (SandboxRef.render ref) reason)
            { Ref = ref
              Backend = config.Backend ref
              Request = request
              Forwarded = forwarded
              StartedBy = None
              StartedAt = None
              Environment = environment }

        let mutable standing : (SandboxRef * RunningSandbox) list = []
        for ref, request in declared do
            let! provisioned = provisionSelection ref request.Spec
            standing <- standing @ [ ref, standingEntry ref request provisioned ]

        /// What each standing sandbox resets to when it is stopped: it keeps its entry,
        /// because it is one the session has from boot, and its configuration.
        let standingRequests : Map<SandboxRef, SandboxRequest * Map<ConnectionName, ConnectionRoute list>> =
            standing
            |> List.map (fun (ref, entry) -> ref, (entry.Request, entry.Forwarded))
            |> Map.ofList

        // Keyed by the ref itself: it is a structural value, so a lookup is an equality
        // rather than a rendered string two call sites have to agree on how to spell.
        // `default` first, then the operator's, in the order declared.
        let mutable entries : (SandboxRef * RunningSandbox) list =
            standing
            |> List.sortBy (fun (ref, _) -> if ref = SandboxRef.defaultRef then 0 else 1)

        let find (name: SandboxRef) =
            entries |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd

        /// A bare name, when the session has no sandbox of its own by it but exactly one
        /// repo declares one: that one. `dev` for `octo/hello:dev` is what an agent
        /// writes after reading "started sandbox octo/hello:dev", and there is nothing
        /// else it could mean. Two repos both declaring `dev` is ambiguous and stays a
        /// refusal that names both.
        let resolve (name: SandboxRef) : RunningSandbox option =
            match find name, SandboxRef.scope name with
            | Some entry, _ -> Some entry
            | None, SessionOwned ->
                match
                    entries
                    |> List.filter (fun (key, _) -> SandboxRef.scope key <> SessionOwned && SandboxRef.name key = SandboxRef.name name)
                with
                | [ _, only ] -> Some only
                | _ -> None
            | None, RepoOwned _ -> None

        let mintMessageId () : MessageId =
            match MessageId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "message id invariant violated: %s" e

        let append (actor: ActorRef) (event: SessionEvent) : Async<unit> =
            async {
                let! _ = config.Log.Append actor event
                return ()
            }

        let ensure (authority: Authority) (causedBy: Cause option) (name: SandboxRef) (request: SandboxRequest) : Async<Result<SandboxOutcome, string>> =
            async {
                let actor = Authority.author authority
                let onBehalfOf = Authority.onBehalfOf authority
                let wanted = request
                match find name with
                | Some existing when existing.Request = wanted ->
                    // The idempotent case. Make sure it is actually up (the environment
                    // is lazy, and a stopped one recreates here), and record NOTHING — a
                    // second ask changed nothing, so the timeline should not claim it did.
                    match! existing.Environment.Ensure None (sprintf "sandbox '%s' was asked for" (SandboxRef.render name)) with
                    | EnvironmentUnavailable reason -> return Error reason
                    | EnvironmentAvailable -> return Ok (SandboxAlreadyRunning existing)
                | Some existing ->
                    // Say what differs, never recreate. `differences` is total over
                    // unequal requests, so this branch always has something to say —
                    // which is why the idempotence above is an equality on the same value
                    // rather than a second, looser rule that could disagree with it.
                    return
                        Error (
                            sprintf
                                "sandbox '%s' is already running and %s — stop_work_sandbox it first if you want to change that (anything running in it dies with it)"
                                (SandboxRef.render name)
                                (SandboxRequest.differences existing.Request wanted |> String.concat "; "))
                // The session's own sandboxes are the operator's to declare, and all of them
                // stand from boot, so a session-owned name not found is one nobody declared —
                // refused HERE, where the declarations are, rather than by whichever caller
                // remembered to look first.
                | None when SandboxRef.scope name = SessionOwned ->
                    return Error (unknownSandbox name (entries |> List.map fst))
                | None ->
                    match! provisionSelection name wanted.Spec with
                    | Error e -> return Error e
                    | Ok (forwarded, provision, built) ->
                        match config.Create name built provision with
                        | Error e ->
                            revoke name forwarded
                            config.Proxy.Release name
                            return Error e
                        | Ok environment ->
                            // One id for the whole coming-up: the RUNNING act this opens
                            // is the same item `WorkSandboxStarted`/`WorkSandboxStartFailed`
                            // below resolve in place. Emitted BEFORE `environment.Ensure`
                            // — creating, starting and verifying the container — so the
                            // timeline shows the sandbox coming up rather than dead air
                            // until it is already up. A provision or create failure above
                            // never reached here, so it opens no running act.
                            let messageId = mintMessageId ()
                            do!
                                append
                                    actor
                                    (SessionEvent.WorkSandboxStarting
                                        { MessageId = messageId
                                          Sandbox = name
                                          Backend = config.Backend name
                                          Description = config.Describe name
                                          Actor = actor
                                          OnBehalfOf = onBehalfOf
                                          CausedBy = causedBy })
                            match! environment.Ensure None (sprintf "sandbox '%s' was started" (SandboxRef.render name)) with
                            | EnvironmentUnavailable reason ->
                                do!
                                    append
                                        actor
                                        (SessionEvent.WorkSandboxStartFailed
                                            { MessageId = messageId
                                              Sandbox = name
                                              Reason = reason
                                              Actor = actor
                                              OnBehalfOf = onBehalfOf
                                              CausedBy = causedBy })
                                return Error reason
                            | EnvironmentAvailable ->
                                let startedAt = config.Clock ()
                                let entry =
                                    { Ref = name
                                      Backend = config.Backend name
                                      Request = wanted
                                      Forwarded = forwarded
                                      StartedBy = Some actor
                                      StartedAt = Some startedAt
                                      Environment = environment }
                                entries <- entries @ [ name, entry ]
                                do!
                                    append
                                        actor
                                        (SessionEvent.WorkSandboxStarted
                                            { MessageId = messageId
                                              Sandbox = name
                                              Backend = config.Backend name
                                              Description = config.Describe name
                                              Checkout = config.Checkout name
                                              Forwarded = ForwardedRoutes.names forwarded
                                              // Asked of the environment that just came
                                              // up, not computed here: what a sandbox
                                              // holds is settled by the policy it was
                                              // built from, and this manager never sees
                                              // one.
                                              Realisation = environment.Realisation ()
                                              Actor = actor
                                              OnBehalfOf = onBehalfOf
                                              CausedBy = causedBy })
                                return Ok (SandboxStarted entry)
            }

        let stop (actor: ActorRef) (name: SandboxRef) : Async<Result<unit, string>> =
            async {
                match resolve name with
                | None -> return Error (unknownSandbox name (entries |> List.map fst))
                | Some entry ->
                    let name = entry.Ref
                    do! entry.Environment.Stop ()
                    revoke name entry.Forwarded
                    // A standing sandbox keeps its ENTRY — it is one the session has
                    // from boot, and a terminal that names it must still find it — but
                    // resets to its own configuration. Any other name leaves entirely,
                    // which is what makes "stop it first, then start it as declared now"
                    // work.
                    match Map.tryFind name standingRequests with
                    | Some (request, forwarded) ->
                        entries <-
                            entries
                            |> List.map (fun (key, existing) ->
                                if key = name then
                                    key,
                                    { existing with
                                        Request = request
                                        Forwarded = forwarded
                                        StartedBy = None
                                        StartedAt = None }
                                else key, existing)
                    | None ->
                        // Only a sandbox that LEAVES gives its proxy admission back: a standing
                        // one comes up again on the policy it was built with, which carries the
                        // URL that admission opens.
                        config.Proxy.Release name
                        entries <- entries |> List.filter (fun (key, _) -> key <> name)
                    do!
                        append
                            actor
                            (SessionEvent.WorkSandboxStopped
                                { MessageId = mintMessageId (); Sandbox = name; Actor = actor })
                    return Ok ()
            }

        let environmentFor (name: SandboxRef) : SessionEnvironment.SessionEnvironment =
            match resolve name with
            | Some entry -> entry.Environment
            | None -> missing (unknownSandbox name (entries |> List.map fst))

        let stopAll () : Async<unit> =
            async {
                for name, entry in entries do
                    do! entry.Environment.Stop ()
                    revoke name entry.Forwarded
                    config.Proxy.Release name
            }

        /// What a block in `name` is lent for its act: each forwarded source's answer for
        /// the act's credential, merged. Nothing for a sandbox that forwards nothing, and
        /// nothing for a name this session does not have — its environment refuses the
        /// spawn anyway, with the reason.
        let lend (name: SandboxRef) (terminal: TerminalId) (block: BlockId option) (authority: Authority) : Async<BlockEnv> =
            async {
                match resolve name with
                | None -> return BlockEnv.none
                | Some entry ->
                    let mutable lent = BlockEnv.none
                    for forwarded, routes in Map.toList entry.Forwarded do
                        match config.Credentials |> List.tryFind (fun source -> source.Name = forwarded) with
                        | None -> ()
                        | Some source ->
                            let lentInto =
                                // A connection the declaration lends no variable is lent into
                                // none of them, which is the absent entry's meaning.
                                lentVariables entry.Request.Spec |> Map.tryFind forwarded |> Option.defaultValue []
                            let! given = source.Lend authority entry.Ref terminal block routes lentInto
                            lent <- BlockEnv.merge lent given
                    return lent
            }

        /// Every source, because a loan is a fact about a terminal and this manager does
        /// not keep which sandbox a terminal is in — the sources do, by what they lent.
        let retire (terminal: TerminalId) : unit =
            for source in config.Credentials do
                source.Retire terminal

        return
            { Ensure = ensure
              Stop = stop
              EnvironmentFor = environmentFor
              Loans = { Lend = lend; Retire = retire }
              Listed = fun () -> entries |> List.map snd
              StopAll = stopAll }
    }

/// A registry over ONE already-built environment, under the `default` name: the shape
/// every session had before Plan 15. Starting or stopping a second one is refused, because
/// there is no factory here to build it with — a composition that wants named sandboxes
/// hands `create` a `Create`.
let singleton (backend: string) (environment: SessionEnvironment.SessionEnvironment) : WorkSandboxes =
    let entry =
        { Ref = SandboxRef.defaultRef
          Backend = backend
          Request = SandboxRequest.defaults
          Forwarded = Map.empty
          StartedBy = None
          StartedAt = None
          Environment = environment }
    { Ensure =
        fun _ _ name _ ->
            async {
                if name <> SandboxRef.defaultRef then
                    return Error "this session has only its default sandbox"
                else
                    match! environment.Ensure None "the sandbox was asked for" with
                    | EnvironmentUnavailable reason -> return Error reason
                    // Always the already-running answer: this degenerate registry has one
                    // sandbox that exists from boot, so no ask of it is the ask that
                    // started it — and the safe direction for a once-only consequence is
                    // the one that does not fire.
                    | EnvironmentAvailable -> return Ok (SandboxAlreadyRunning entry)
            }
      Stop =
        fun _ name ->
            async {
                if name <> SandboxRef.defaultRef then
                    return Error "this session has only its default sandbox"
                else
                    do! environment.Stop ()
                    return Ok ()
            }
      EnvironmentFor = fun _ -> environment
      Loans = SessionTerminals.BlockLoans.none
      Listed = fun () -> [ entry ]
      StopAll = environment.Stop }

/// A session composed without any sandbox at all. It still HAS a `default` — every
/// session does — but that default refuses everything, which is what a Host started
/// without the composition has always done. Not an empty registry: an empty one would
/// make `default` an unknown NAME, and "there is no sandbox named default" is a confusing
/// way to say "this session has no environment".
let unavailable : WorkSandboxes =
    let entry =
        { Ref = SandboxRef.defaultRef
          Backend = "none"
          Request = SandboxRequest.defaults
          Forwarded = Map.empty
          StartedBy = None
          StartedAt = None
          Environment = SessionEnvironment.unavailable }
    { Ensure = fun _ _ _ _ -> async { return Error "this session has no environment" }
      Stop = fun _ _ -> async { return Error "this session has no environment" }
      EnvironmentFor = fun _ -> SessionEnvironment.unavailable
      Loans = SessionTerminals.BlockLoans.none
      Listed = fun () -> [ entry ]
      StopAll = fun () -> async { return () } }

// --- the `work_sandboxes` query (Plan 15) -------------------------------------------------

let queryName : QueryName =
    match QueryName.create "work_sandboxes" with
    | Ok name -> name
    | Error e -> failwithf "work sandboxes query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "work sandboxes"
      Description =
        "The sandboxes commands can run in, each with the backend confining it, what it \
         runs, whether it is up, and which credentials were forwarded into it and by whom. \
         Read from the processes themselves. A degraded row says what this host could not \
         give exactly."
      Shape =
        Rows
            [ QueryColumn.create "name" "name"
              // `name` is the ADDRESS — what you type at a verb — and `declared_by` is the
              // attribution. The repo appears in both, and that is not a spare: the
              // address answers "how do I reach this", the attribution answers "who asked
              // for it", and only the second is a reason to look at what it runs.
              QueryColumn.create "declared_by" "declared by"
              QueryColumn.create "backend" "backend"
              QueryColumn.create "runs" "runs"
              QueryColumn.create "state" "state"
              QueryColumn.create "forwarding" "forwarding"
              // What this host could not give exactly. Absent is the ordinary case and says
              // nothing — a column reading "none" on every row is a column people stop
              // seeing, and this one is worth seeing on the day it is not empty.
              QueryColumn.create "degraded" "degraded"
              QueryColumn.create "started_by" "started by"
              QueryColumn.create "started_at" "started" ]
      // The `degraded` column is written in the grant notation, so the legend comes with
      // it. The whole of it rather than the part today's degradations happen to use: any
      // kind can be the one a host cannot give exactly, and a legend that had to be
      // predicted from the answers would be wrong on the day it mattered.
      Legend = GrantNotation.legend }

/// Register the sandboxes as a query. `state` is read from the RUNNING sandbox rather
/// than from what the registry was told — the same rule the repos listing follows, for
/// the same reason: a panel that can disagree with reality teaches people to distrust it.
///
/// It takes a GETTER, not a registry: the query surface is composed before the Host is
/// started and the Host is what builds the registry, so the value does not exist yet when
/// this is registered. A read happens later, by which time it does.
let query (current: unit -> WorkSandboxes) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        (current ()).Listed ()
                        |> List.map (fun entry ->
                            [ "name", CellText (SandboxRef.render entry.Ref)
                              "declared_by",
                              (match SandboxRef.scope entry.Ref with
                               | RepoOwned repo -> CellText (RepoRef.value repo)
                               | SessionOwned -> CellAbsent)
                              "backend", CellText entry.Backend
                              "runs", CellText (SandboxRuntime.describe entry.Request.Spec.Runtime)
                              "state",
                              CellText (
                                  match entry.Environment.CurrentRef () with
                                  | Some ref -> "running (" + ref + ")"
                                  | None -> "not started")
                              "forwarding",
                              (if Map.isEmpty entry.Forwarded then CellText "nothing"
                               else CellText (ForwardedRoutes.describe entry.Forwarded))
                              // From the RUNNING sandbox, like `state` above and for the same
                              // reason: what a sandbox holds is a fact about the one that
                              // exists, and a stopped entry that still claimed a widening
                              // would be the panel disagreeing with the machine.
                              "degraded",
                              (match entry.Environment.Realisation () with
                               | [] -> CellAbsent
                               | lines -> CellText (String.concat "; " lines))
                              "started_by",
                              (match entry.StartedBy with
                               | Some actor -> CellText (ActorRef.token actor)
                               | None -> CellAbsent)
                              "started_at",
                              (match entry.StartedAt with
                               | Some at -> CellText (at.ToString "o")
                               | None -> CellAbsent) ])))
            } }
