namespace Yession.Domain.Sandboxes

// A sandbox policy as a Seatbelt profile: the text that macOS's `sandbox-exec` confines a
// process with. Written here, as data, and not by a library.
//
// A profile is `(deny default)` and then ALLOWS, and nothing else. That one constraint is
// what makes it checkable. With no deny after the first line there is no carve-out, so what
// a sandbox holds is the union of its rules, the order they are written in means nothing,
// and one rule can be read on its own. Every property of this module follows from that, and
// `render` has no way to write a second deny.
//
// The rules come from two places, and the two stay apart. `essentials` is what any process
// needs before it can run at all (fork, a few Mach services, the sysctls a runtime reads at
// start), and it is the same text for every sandbox. A `SeatbeltRule` is what a POLICY
// decides: which files, which sockets, which port. Only the second kind names a file to
// read or to write, so `allows` answers for a whole profile from the rules alone.
//
// One kind of rule is not read: a rule that an operator wrote in Seatbelt's own language
// (`SeatbeltRule.Written`). `render` writes its text as it is, and `allows` does not look
// inside it. So `allows` is exact for a profile that holds no such rule. With one, the
// kernel allows what `allows` says and also what that rule says.
//
// `SeatbeltAllow.parse` checks the shape of such a rule: one form that opens with
// `(allow ` and holds no `(deny`. The check stops a mistake. It does not prove what
// Seatbelt's Scheme does with the text, and the operator who wrote the rule answers for it.
//
// Why not `@anthropic-ai/sandbox-runtime`, which wrote these profiles first. Measured on
// 0.0.81: its configuration cannot name ONE node (a .NET build stats `/tmp/`, a symlink
// whose own node must be readable while `/private/tmp` is not), it cannot add a sysctl (the
// Android emulator dies without `hw.cachelinesize`), and it opens `bind` on every interface
// when it is asked for the loopback. The first of those is what
// docs/decisions/2026-08-28-no-srt-fork-for-symlink-metadata.md could not get from a fork
// either. That library still filters egress: a profile names the port of its proxy, and
// holds no other route out.
//
// No IO, like the algebra this sits beside (`Resources.fs`). A path arrives canonical,
// because the kernel matches the path of the file and not the spelling somebody wrote
// (`OperatorResources.canonicalPaths`). Nothing here resolves one.

/// How much of the tree one rule about a path covers.
[<RequireQualifiedAccess>]
type SeatbeltPath =
    /// This node and nothing beneath it: the entry names of a directory without what the
    /// entries hold, the target of a symlink, one device. Seatbelt's `literal`.
    | Node of string
    /// This node and everything beneath it. Seatbelt's `subpath`.
    | Tree of string

/// One thing a profile allows, among the things a policy decides.
///
/// Every case is an allow, and there is no case for a deny (the header says why). A case
/// names one kind of access and what it is to, so two rules never need each other to be
/// understood, and a set of them is a profile.
[<RequireQualifiedAccess>]
type SeatbeltRule =
    /// Read it: its data, its metadata and, for a directory, its entry names.
    | Read of SeatbeltPath
    /// Write it: create, change and remove. Not read: a path that is written is read
    /// because `forPolicy` says both, never because one implies the other.
    | Write of SeatbeltPath
    /// The kind and the mode of any directory, and nothing that is in one. Resolving a
    /// path stats every directory on the way to it, so without this no path that was
    /// granted can be resolved. It also answers whether a directory nobody granted
    /// exists, which is what it costs.
    | DirectoryMetadata
    /// Make a unix socket at all. A `Socket` rule is about a path, and making the socket
    /// has none, so Seatbelt asks for this separately.
    | UnixSockets
    /// Bind and connect unix sockets at this path or beneath it.
    | Socket of path: string
    /// Connect to one TCP port on the loopback. The session's filtering proxy listens
    /// there, and it is the only route out of a sandbox that a profile from a policy
    /// names.
    | LoopbackPort of port: int
    /// A rule that an operator wrote in Seatbelt's own language. The profile holds its
    /// text as it is, and `allows` does not read it (`SeatbeltAllow` says what its check covers).
    | Written of SeatbeltAllow

/// One thing a process might try, as a question a profile answers (`SeatbeltProfile.allows`).
///
/// Only what a `SeatbeltRule` decides. Whether a process may fork, or look up a Mach
/// service, is the same for every profile and is not a question about one.
[<RequireQualifiedAccess>]
type SeatbeltAsk =
    | Read of path: string
    | Write of path: string
    /// Bind or connect a unix socket at this path.
    | Socket of path: string
    /// Connect to this TCP port on the loopback.
    | Dial of port: int

/// What a profile holds that is a fact about the host and the session, not about the
/// policy. Beside the policy and not on it, for the reason `SrtTools.Runtime` is: every
/// sandbox on one host is given the same.
type SeatbeltHost =
    { /// The host files that every sandbox may read whatever its policy says: the
      /// interpreter its commands run, the libraries that interpreter links, the trust
      /// store a TLS client opens.
      RuntimePaths : string list
      /// The sandbox's temporary directory. Written and read like a granted path.
      Tmp : string
      /// The loopback port of the filtering proxy. `None` is a sandbox with no route out.
      ProxyPort : int option }

/// Where one sandbox's filtering proxy listens, and what admits a client to it.
///
/// Qualified, because `Port` is a label half the records about a listener carry, and a bare
/// `{ Port = …; Token = … }` must not come to mean this one somewhere it was never meant.
[<RequireQualifiedAccess>]
type SeatbeltProxy =
    { Port : int
      /// The proxy answers only a client that presents this. Without it, any process on the
      /// host that found the port could use one sandbox's allowlist as its own.
      Token : string }

module SeatbeltProxy =

    /// What a client is told to dial directly, past the proxy: the loopback and the private
    /// ranges. Directly is into the profile's deny, so nothing here is a route. It is the
    /// list `@anthropic-ai/sandbox-runtime`'s own wrapper gives, and `Sandboxes.hostAddressFrom`
    /// and `hostRouteFrom` are written against a client that has it.
    let private direct : string =
        "localhost,127.0.0.1,::1,169.254.0.0/16,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"

    /// The variables that send a sandbox's clients to its proxy.
    ///
    /// The proxy is named by ADDRESS, `127.0.0.1`, and not as `localhost`: a name has to be
    /// resolved, a confined process on macOS has no resolver to ask (the profile admits
    /// neither mDNSResponder's socket nor its Mach service), and Node gives up there where
    /// libc falls back to `/etc/hosts`. The profile's own rule says `localhost`, which is
    /// Seatbelt's word for that address.
    ///
    /// Only what the common clients read: curl, git, Node, Python and Go take one of these
    /// six. A JVM reads none of them, and is not sent to the proxy by this.
    let environment (proxy: SeatbeltProxy) : Map<string, string> =
        let url = sprintf "http://yession:%s@127.0.0.1:%d" (System.Uri.EscapeDataString proxy.Token) proxy.Port
        Map.ofList
            [ "HTTP_PROXY", url
              "HTTPS_PROXY", url
              "ALL_PROXY", url
              "http_proxy", url
              "https_proxy", url
              "all_proxy", url
              "NO_PROXY", direct
              "no_proxy", direct ]

/// A policy's rules, validated. Private, so `forPolicy` is the only way to hold one and
/// `render` only ever writes paths that `forPolicy` accepted.
type SeatbeltProfile = private { Held : Set<SeatbeltRule> }

module SeatbeltProfile =

    /// What any process needs before it can run, the same for every sandbox.
    ///
    /// Transcribed, not derived: this is the fixed part of the profile that
    /// `@anthropic-ai/sandbox-runtime` 0.0.81 generates (Apache-2.0), which follows
    /// Chromium's sandbox policy. Under it a cold `devenv shell`, a .NET build of an
    /// Android app, its JDK tools and the Android emulator all ran. Narrow it by
    /// measuring, one line at a time.
    ///
    /// Two differences from that source. `hw.cachelinesize` is added: the emulator's
    /// qemu reads it at start and dies with an illegal instruction when it is refused,
    /// and the library lists only `hw.cachelinesize_compat`. Its rule for reading and
    /// writing `/dev/null` is not here, because a file to read or write is a
    /// `SeatbeltRule` (`baseline`), never a line of this text.
    let essentials : string =
        """; Processes: run and fork, and see and signal only the processes of this sandbox.
(allow process-exec)
(allow process-fork)
(allow process-info* (target same-sandbox))
(allow signal (target same-sandbox))
(allow mach-priv-task-port (target same-sandbox))

(allow user-preference-read)

; Mach services, by name.
(allow mach-lookup
  (global-name "com.apple.audio.systemsoundserver")
  (global-name "com.apple.distributed_notifications@Uv3")
  (global-name "com.apple.FontObjectsServer")
  (global-name "com.apple.fonts")
  (global-name "com.apple.logd")
  (global-name "com.apple.lsd.mapdb")
  (global-name "com.apple.PowerManagement.control")
  (global-name "com.apple.system.logger")
  (global-name "com.apple.system.notification_center")
  (global-name "com.apple.system.opendirectoryd.libinfo")
  (global-name "com.apple.system.opendirectoryd.membership")
  (global-name "com.apple.bsd.dirhelper")
  (global-name "com.apple.securityd.xpc")
  (global-name "com.apple.coreservices.launchservicesd")
  (global-name "com.apple.SecurityServer")
)

; POSIX shared memory and semaphores.
(allow ipc-posix-shm)
(allow ipc-posix-sem)

(allow iokit-open
  (iokit-registry-entry-class "IOSurfaceRootUserClient")
  (iokit-registry-entry-class "RootDomainUserClient")
  (iokit-user-client-class "IOSurfaceSendRight")
)
(allow iokit-get-properties)

; A system socket that carries no network traffic.
(allow system-socket (require-all (socket-domain AF_SYSTEM) (socket-protocol 2)))

(allow sysctl-read
  (sysctl-name "hw.activecpu")
  (sysctl-name "hw.busfrequency_compat")
  (sysctl-name "hw.byteorder")
  (sysctl-name "hw.cacheconfig")
  (sysctl-name "hw.cachelinesize")
  (sysctl-name "hw.cachelinesize_compat")
  (sysctl-name "hw.cpufamily")
  (sysctl-name "hw.cpufrequency")
  (sysctl-name "hw.cpufrequency_compat")
  (sysctl-name "hw.cputype")
  (sysctl-name "hw.l1dcachesize_compat")
  (sysctl-name "hw.l1icachesize_compat")
  (sysctl-name "hw.l2cachesize_compat")
  (sysctl-name "hw.l3cachesize_compat")
  (sysctl-name "hw.logicalcpu")
  (sysctl-name "hw.logicalcpu_max")
  (sysctl-name "hw.machine")
  (sysctl-name "hw.memsize")
  (sysctl-name "hw.ncpu")
  (sysctl-name "hw.nperflevels")
  (sysctl-name "hw.packages")
  (sysctl-name "hw.pagesize_compat")
  (sysctl-name "hw.pagesize")
  (sysctl-name "hw.physicalcpu")
  (sysctl-name "hw.physicalcpu_max")
  (sysctl-name "hw.tbfrequency_compat")
  (sysctl-name "hw.vectorunit")
  (sysctl-name "kern.argmax")
  (sysctl-name "kern.bootargs")
  (sysctl-name "kern.hostname")
  (sysctl-name "kern.maxfiles")
  (sysctl-name "kern.maxfilesperproc")
  (sysctl-name "kern.maxproc")
  (sysctl-name "kern.ngroups")
  (sysctl-name "kern.osproductversion")
  (sysctl-name "kern.osrelease")
  (sysctl-name "kern.ostype")
  (sysctl-name "kern.osvariant_status")
  (sysctl-name "kern.osversion")
  (sysctl-name "kern.secure_kernel")
  (sysctl-name "kern.tcsm_available")
  (sysctl-name "kern.tcsm_enable")
  (sysctl-name "kern.usrstack64")
  (sysctl-name "kern.version")
  (sysctl-name "kern.willshutdown")
  (sysctl-name "machdep.cpu.brand_string")
  (sysctl-name "machdep.ptrauth_enabled")
  (sysctl-name "security.mac.lockdown_mode_state")
  (sysctl-name "sysctl.proc_cputype")
  (sysctl-name "vm.loadavg")
  (sysctl-name-prefix "hw.optional.arm")
  (sysctl-name-prefix "hw.optional.arm.")
  (sysctl-name-prefix "hw.optional.armv8_")
  (sysctl-name-prefix "hw.perflevel")
  (sysctl-name-prefix "kern.proc.all")
  (sysctl-name-prefix "kern.proc.pgrp.")
  (sysctl-name-prefix "kern.proc.pid.")
  (sysctl-name-prefix "machdep.cpu.")
  (sysctl-name-prefix "net.routetable.")
)
(allow sysctl-write (sysctl-name "kern.tcsm_enable"))

(allow distributed-notification-post)

; Device control. What reads or writes a device is a rule after this text.
(allow file-ioctl
  (literal "/dev/null")
  (literal "/dev/zero")
  (literal "/dev/random")
  (literal "/dev/urandom")
  (literal "/dev/dtracehelper")
  (literal "/dev/tty")
)"""

    /// The rules about files that every profile holds, whatever its policy and its host.
    ///
    /// Each one is a node, so none opens what is beneath it, with one exception:
    /// `/dev/fd` is a tree, and what is beneath it is the descriptors the process already
    /// holds.
    let baseline : Set<SeatbeltRule> =
        let read (path: string) = SeatbeltRule.Read (SeatbeltPath.Node path)
        let readWrite (path: string) =
            [ SeatbeltRule.Read (SeatbeltPath.Node path); SeatbeltRule.Write (SeatbeltPath.Node path) ]
        Set.ofList
            [ // The entry names of `/`, which every profile for this platform has let a
              // process see. Nothing in `/` is granted by it.
              yield read "/"
              yield SeatbeltRule.DirectoryMetadata
              // The three symlinks that most paths on a Mac go through. Their own nodes,
              // so a program that stats `/tmp/` or reads the link `/etc` gets an answer,
              // and nothing in the directories they lead to. Measured: without the node
              // `/tmp`, `stat("/tmp/")` is refused and .NET's first-run check fails.
              yield read "/tmp"
              yield read "/var"
              yield read "/etc"
              yield read "/dev/random"
              yield read "/dev/urandom"
              yield read "/dev/zero"
              yield! readWrite "/dev/null"
              yield! readWrite "/dev/tty"
              // The loader of every process opens these two. Refusing them breaks nothing
              // and puts two denials in the log for each process started.
              yield! readWrite "/dev/dtracehelper"
              yield! readWrite "/dev/autofs_nowait"
              // The standard streams by path. Each is a symlink into `/dev/fd`, so a
              // redirect to `/dev/stdout` needs the link's node AND the descriptor it
              // names. Measured: with the three nodes alone `echo x > /dev/stdout` is
              // refused, and without `/dev/fd` so is `cat <(echo x)`.
              yield! readWrite "/dev/stdin"
              yield! readWrite "/dev/stdout"
              yield! readWrite "/dev/stderr"
              yield SeatbeltRule.Read (SeatbeltPath.Tree "/dev/fd")
              yield SeatbeltRule.Write (SeatbeltPath.Tree "/dev/fd") ]

    /// A character that would need an escape this module does not write. A profile is
    /// text, a path goes into it between two quotes, and the two escapes that are known
    /// to hold under `sandbox-exec` are the quote and the backslash (measured on a path
    /// with both). Anything else that could end a line or a string is refused instead.
    let private unwritable (c: char) : bool = c < ' ' || c = '\127'

    /// `path` as it is shown in a refusal, with whatever made it unwritable made visible.
    let private shown (path: string) : string =
        path
        |> Seq.map (fun c ->
            if unwritable c then
                let digits = "0123456789abcdef"
                sprintf "\\x%c%c" digits.[int c / 16] digits.[int c % 16]
            else string c)
        |> String.concat ""

    /// One path, as a rule may name it: from the root, and writable into a profile. A
    /// trailing slash is dropped, because Seatbelt compares paths and `/a/` is not `/a`.
    let private named (what: string) (path: string) : Result<string, string> =
        if not (path.StartsWith "/") then
            Error (
                sprintf
                    "a Seatbelt profile cannot hold %s '%s'. It does not start at /. Seatbelt matches a whole path, from the root"
                    what
                    (shown path))
        elif path |> Seq.exists unwritable then
            Error (
                sprintf
                    "a Seatbelt profile cannot hold %s '%s'. It has a control character in it, and a profile is lines of text"
                    what
                    (shown path))
        else
            let trimmed = path.TrimEnd '/'
            Ok (if trimmed = "" then "/" else trimmed)

    /// Every path of `paths`, named, or the first refusal.
    let private allNamed (what: string) (paths: string list) : Result<string list, string> =
        paths
        |> List.fold
            (fun acc path ->
                match acc, named what path with
                | Error refusal, _ -> Error refusal
                | Ok _, Error refusal -> Error refusal
                | Ok kept, Ok path -> Ok (path :: kept))
            (Ok [])
        |> Result.map List.rev

    /// A policy as the rules a sandbox on this host holds.
    ///
    /// The baseline, the host's runtime to read, the temporary directory to read and
    /// write, and then what the policy names: its read paths to read, its write paths to
    /// write AND read (a workspace that can be written and not read is no workspace), its
    /// sockets, and its Seatbelt rules. Nothing else, so a policy that names nothing gets
    /// nothing more, and what two policies hold together is what each holds.
    ///
    /// `AllowedDomains` is not read. Which hosts may be reached is the proxy's to enforce,
    /// and all a profile says about the network is the proxy's port.
    ///
    /// An unconfined policy reads and writes everywhere (`FilesystemConfinement` says
    /// who asks for that). Its sockets and its port are still only the ones named.
    ///
    /// Refuses a path that a profile cannot hold as written (`named`), and a port that is
    /// not one. Both are faults in whatever built the policy, said here because here is
    /// the last place that can say them before `sandbox-exec` reads the text.
    let forPolicy (host: SeatbeltHost) (policy: SandboxPolicy) : Result<SeatbeltProfile, string> =
        let trees (make: SeatbeltPath -> SeatbeltRule) (paths: string list) =
            paths |> List.map (fun path -> make (SeatbeltPath.Tree path))
        let proxyRules =
            match host.ProxyPort with
            | None -> Ok []
            | Some port when port >= 1 && port <= 65535 -> Ok [ SeatbeltRule.LoopbackPort port ]
            | Some port ->
                Error (sprintf "a Seatbelt profile cannot name the proxy's port %d. A port is from 1 to 65535" port)
        let fileRules =
            match policy.Filesystem with
            | Unconfined ->
                Ok [ SeatbeltRule.Read (SeatbeltPath.Tree "/"); SeatbeltRule.Write (SeatbeltPath.Tree "/") ]
            | Confined ->
                allNamed "the temporary directory" [ host.Tmp ]
                |> Result.bind (fun tmp ->
                    allNamed "the host runtime path" host.RuntimePaths
                    |> Result.bind (fun runtime ->
                        allNamed "the read path" policy.ReadPaths
                        |> Result.bind (fun readable ->
                            allNamed "the write path" policy.WritePaths
                            |> Result.map (fun writable ->
                                let written = writable @ tmp
                                trees SeatbeltRule.Read (runtime @ readable @ written)
                                @ trees SeatbeltRule.Write written))))
        let socketRules =
            allNamed "the socket" policy.Sockets
            |> Result.map (fun paths ->
                match paths with
                | [] -> []
                | _ -> SeatbeltRule.UnixSockets :: (paths |> List.map SeatbeltRule.Socket))
        proxyRules
        |> Result.bind (fun proxy ->
            fileRules
            |> Result.bind (fun files ->
                socketRules
                |> Result.map (fun sockets ->
                    let written = policy.SeatbeltRules |> List.map SeatbeltRule.Written
                    { Held = Set.union baseline (Set.ofList (proxy @ files @ sockets @ written)) })))

    /// What the profile holds, each rule once.
    let rules (profile: SeatbeltProfile) : Set<SeatbeltRule> = profile.Held

    /// Whether `path` is the node a rule names, or beneath the tree it names. A tree
    /// ends at a `/`: `/data/work-2` is not beneath `/data/work`.
    let private covers (where: SeatbeltPath) (path: string) : bool =
        match where with
        | SeatbeltPath.Node node -> path = node
        | SeatbeltPath.Tree "/" -> path.StartsWith "/"
        | SeatbeltPath.Tree tree -> path = tree || path.StartsWith (tree + "/")

    /// Whether a process confined by this profile may do what `ask` says.
    ///
    /// The model of a profile, and it can be one because every rule is an allow: the
    /// answer is whether SOME rule covers the ask, and no rule can take an answer back.
    /// It reads the same rules `render` writes, so what it says of a profile is what the
    /// kernel says of the text, as far as `literal` and `subpath` mean what Seatbelt's
    /// documentation says they mean. A path is asked about in its canonical spelling,
    /// the one the kernel would check.
    ///
    /// A `Written` rule answers no ask here. The kernel reads it and this model does not,
    /// so a profile that holds one may allow more than this says, and never less.
    let allows (profile: SeatbeltProfile) (ask: SeatbeltAsk) : bool =
        let some (matches: SeatbeltRule -> bool) = profile.Held |> Set.exists matches
        match ask with
        | SeatbeltAsk.Read path ->
            some (function
                | SeatbeltRule.Read where -> covers where path
                | _ -> false)
        | SeatbeltAsk.Write path ->
            some (function
                | SeatbeltRule.Write where -> covers where path
                | _ -> false)
        | SeatbeltAsk.Socket path ->
            Set.contains SeatbeltRule.UnixSockets profile.Held
            && some (function
                | SeatbeltRule.Socket socket -> covers (SeatbeltPath.Tree socket) path
                | _ -> false)
        | SeatbeltAsk.Dial port -> Set.contains (SeatbeltRule.LoopbackPort port) profile.Held

    /// A path between two quotes, the way a profile's reader takes it back as the same
    /// path. `forPolicy` already refused whatever these two escapes do not cover.
    let private quoted (path: string) : string =
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let private filter (where: SeatbeltPath) : string =
        match where with
        | SeatbeltPath.Node path -> sprintf "(literal %s)" (quoted path)
        | SeatbeltPath.Tree path -> sprintf "(subpath %s)" (quoted path)

    /// One rule as its lines of a profile. Total over the rules, and every line it can
    /// write opens with `(allow`.
    let private written (rule: SeatbeltRule) : string list =
        match rule with
        | SeatbeltRule.Read where -> [ sprintf "(allow file-read* %s)" (filter where) ]
        | SeatbeltRule.Write where -> [ sprintf "(allow file-write* %s)" (filter where) ]
        | SeatbeltRule.DirectoryMetadata -> [ "(allow file-read-metadata (vnode-type DIRECTORY))" ]
        | SeatbeltRule.UnixSockets -> [ "(allow system-socket (socket-domain AF_UNIX))" ]
        | SeatbeltRule.Socket path ->
            [ sprintf "(allow network-bind (local unix-socket (subpath %s)))" (quoted path)
              sprintf "(allow network-outbound (remote unix-socket (subpath %s)))" (quoted path) ]
        | SeatbeltRule.LoopbackPort port -> [ sprintf "(allow network-outbound (remote ip \"localhost:%d\"))" port ]
        | SeatbeltRule.Written rule -> [ SeatbeltAllow.text rule ]

    /// The profile as the text `sandbox-exec` reads: the one deny, the essentials, and
    /// then one allow for each rule. The rules are written in the order a set keeps them,
    /// so one profile is one text on both runtimes.
    let render (profile: SeatbeltProfile) : string =
        [ "(version 1)"; "(deny default)"; essentials ]
        @ (profile.Held |> Set.toList |> List.collect written)
        |> String.concat "\n"
