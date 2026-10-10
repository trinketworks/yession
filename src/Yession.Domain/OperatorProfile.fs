namespace Yession.Domain.Sandboxes

open System
open Yession.Domain
open Yession.Domain.Agent

// What an operator writes, and how it becomes a vocabulary.
//
// The counterpart of `Config.fs`: that file is what a REPO writes and it may only select;
// this one is what a HOST writes and it is the only place a path or a hostname is named. The
// division is the whole point — a repo cannot learn where this machine keeps its package
// caches, and an operator does not have to know which repos want them.
//
// Parser-free, like `Config.fs` and for the same reason: this decodes an already-parsed JSON
// tree, so it runs in the cheap tier on both runtimes and the YAML front end is somebody
// else's problem.
//
// The shape has one rule worth stating before the code: an OBJECT is a leaf and an ARRAY is
// a composition. One namespace, so a repo selecting `nix` cannot tell which it got — and an
// operator can therefore split a leaf into three, or gather three into one name, without any
// repo's file changing. That is what makes the vocabulary genuinely theirs.

/// How the operator keeps a shared named volume from growing without bound: the half of it
/// that has to happen INSIDE a sandbox.
///
/// A warm store is what every sandbox on the host writes to, and what makes the next session
/// fast. Collecting it needs to know what each sandbox still uses, and only the sandbox can
/// say: a Nix store's roots point into the sandbox's own checkout, and the processes holding
/// paths open are in the sandbox's own process table. A collector outside sees neither, so
/// it takes every other session's shell for garbage. So a session runs `Pin` in each of its
/// sandboxes holding the volume — when the sandbox starts, every `Every` after that, and once
/// more before it is removed — handing it a directory of its own on the volume, its LEASE.
/// What `Pin` writes there is the operator's business and never read here.
///
/// The other half, collecting, is not the product's at all: the operator's own sweep, run on
/// the operator's schedule, reads every lease and decides how long one outlives its session.
/// That is why this carries no retention and no schedule for a sweep: the product holds no
/// opinion about what a volume contains, and does not need one to keep leases current.
type VolumeMaintenance =
    { /// An executable inside the sandbox, run with the lease directory as its one argument.
      Pin : string
      /// How often a live sandbox renews its lease. The staleness a sweep has to allow for.
      Every : TimeSpan }

/// A whole profile: the vocabulary, and what every sandbox on this host holds without asking.
///
/// `Always` is the operator granting something to everything, which is a DIFFERENT act from
/// declaring that it exists — and keeping the two apart is the correction this model is built
/// around. `YESSION_SESSION_READ` was both at once, so an operator could not offer a path
/// without forcing it on every sandbox, and a repo asking for one could never obtain it.
///
/// So `resources` is the menu and `always` is what is served whether or not anybody ordered
/// it: every sandbox on this host holds it, no repo has to name it, and none can decline it.
/// A name declared and not in `always` is available and not granted — the state the old
/// variable could not express.
///
/// It was `default`, and the word was wrong twice over. A default is what you get unless you
/// say otherwise, and there is no otherwise to say — a repo's selection ADDS to this set and
/// can never subtract from it. And `default` already means something else two files away: the
/// sandbox a terminal that names none opens in (`SandboxRef.defaultRef`), so "the default resources" and "the
/// default sandbox's resources" were one phrase for two things.
///
/// `Agent` is the one thing here that is not about resources: what the operator says about the
/// agent. Its guidance is APPENDED after the product's own system prompt (`Prompting`'s
/// sections) and never replaces it, on the same principle that keeps a path out of a repo's
/// file: each author writes what only they know. The core prompt describes mechanics the build
/// defines — which tool reaches which sandbox, how a queued command comes back — and a copy of
/// it in an operator's file would describe the build that was current when they wrote it. What
/// an operator knows is this host: its conventions, what is slow here, what is never to be
/// pushed where. The operator also picks the strategy, by name, from the ones the build has.
type ProfileFile =
    { Resources : ResourceProfile
      Always : ResourceName list
      Agent : AgentProfile
      /// The sandboxes this host's sessions have from boot, in the form a repo declares its
      /// own (`ConfigFile.sandboxes`). Session-owned: scoped to no repo, and run on the
      /// backend this host configures for the session's own sandboxes.
      Sandboxes : Map<SandboxName, SandboxDecl>
      /// By Docker volume name, since that is what a lease belongs to: a volume is one thing
      /// on the host however many resources name it, so its maintenance is a fact about the
      /// volume and not about any grant of it. Declared on a `volume` leaf (`maintain`), and
      /// deliberately not part of the leaf itself, so what a sandbox is told it holds
      /// (`vol:NAME>AT`) does not change with how the operator looks after it.
      Maintenance : Map<string, VolumeMaintenance> }

module ProfileFile =

    let empty : ProfileFile =
        { Resources = ResourceProfile.empty
          Always = []
          Agent = AgentProfile.defaults
          Sandboxes = Map.empty
          Maintenance = Map.empty }
