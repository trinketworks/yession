namespace Yession.Domain.Sandboxes

open Yession.Domain

/// The facts of an entry: a process outside the session that runs one command in one sandbox
/// of the session, through the door of the session.
///
/// A terminal is a command that a member of the session runs. An entry is a command that a
/// program on the host runs for a person: a deployment that gives SSH into a sandbox makes
/// one entry for each connection. The session records the entry and its end. It does not
/// record the bytes.
/// See docs/decisions/2026-11-22-a-sandbox-has-a-door-on-a-socket.md.

/// The name of one entry. The Session makes it when the entry starts.
type EntryId = private EntryId of string

module EntryId =

    /// The longest id that a log can hold.
    let maxLength = 64

    let create (raw: string) : Result<EntryId, string> =
        if isNull (box raw) || System.String.IsNullOrWhiteSpace raw then Error "EntryId cannot be empty"
        elif raw.Trim().Length > maxLength then Error (sprintf "EntryId cannot be longer than %d characters" maxLength)
        else Ok (EntryId (raw.Trim ()))

    let value (EntryId id) : string = id

/// A command started in a sandbox for a caller of the door.
type SandboxEntered =
    { Entry : EntryId
      /// The sandbox that the command runs in.
      EnteredSandbox : SandboxRef
      /// Who entered, as the caller of the door said it. The Session does not check the name.
      /// It is text and not an `ActorRef`, because a `UserRef` is an identity that the
      /// Manager checked, and nothing checked this one.
      EnteredAs : string
      /// How the caller came, as the deployment labels it: `ssh`, for example. The Session
      /// shows the label and gives it no meaning.
      ViaLabel : string
      /// The command and its arguments.
      EnteredCommand : string list }

/// The command of an entry ended, or the session stopped following it.
type SandboxLeft =
    { LeftEntry : EntryId
      /// The exit code of the command. `None` when the command did not end with a code: the
      /// caller closed the door, a signal ended the command, or the session started again.
      ExitCode : int option
      /// Why the entry ended, in words.
      EndedBecause : string }

module SandboxLeft =

    /// The words for an entry that a session found open when it started. The process that
    /// held the entry is gone, and the command went with it.
    let sessionRestarted = "session restarted"

/// A deployment offered a way into a sandbox: an address that a person uses to come in
/// through the door of the session. For SSH it is the command line that a person runs.
///
/// The deployment makes the way (a listener on a port, for example) and knows its address.
/// The session does not. The deployment says the address through the door, and the page of
/// the session shows it on the sandbox. The session shows the text and gives it no meaning.
type SandboxWayOffered =
    { /// The sandbox that the way goes to.
      WayTo : SandboxRef
      /// How a person comes, as the deployment labels it: `ssh`, for example. The same
      /// label as the entries that come this way (`SandboxEntered.ViaLabel`). One sandbox
      /// has one way for each label, and a second offer replaces the first.
      WayVia : string
      /// What a person runs or opens to come this way, in the words of the deployment:
      /// `ssh -p 22005 root@host`, for example.
      WayAddress : string }

/// The deployment took a way back, or the session stopped following it.
type SandboxWayWithdrawn =
    { WithdrawnTo : SandboxRef
      WithdrawnVia : string }
