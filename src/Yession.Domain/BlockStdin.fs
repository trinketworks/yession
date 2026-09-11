namespace Yession.Domain.Terminals

open Yession.Domain

/// Where a block's command reads its standard input from.
///
/// A block is typed at a live shell's prompt, so by default the command's stdin is the
/// terminal itself — whatever is typed into it while the command runs. That is right for a
/// person, whose keyboard it is. It is a trap for a command nobody is sitting at: `perl -i`
/// with no filename, `cat` with no argument, a `git commit` that opens an editor, all read from
/// the terminal and wait for bytes that never come. The block never ends, the queue behind it
/// never drains, and the only way out is closing the terminal. Measured in a real session: one
/// such block cost the agent its default terminal for the rest of the day, and it fled to a
/// work sandbox whose bare shell had none of the tools it needed.
///
/// Two things, kept apart. The TYPE and `wrap` are the mechanism — how a shell is made to give
/// a command end-of-file instead of the terminal — and know nothing about who is running what.
/// `BlockStdinPolicy` is the rule about WHOSE blocks read which, and knows nothing about shell
/// grammar. A policy change is a one-line edit that cannot break the wrapping, and a shell
/// dialect that needs different grammar cannot change who gets a keyboard.
[<RequireQualifiedAccess>]
type BlockStdin =
    /// The terminal's own: keystrokes reach the command while it runs.
    | Terminal
    /// Nothing: the command sees end-of-file the moment it reads.
    | Closed

module BlockStdin =

    /// The command line as the shell should read it, given where its input comes from.
    ///
    /// `Closed` wraps the WHOLE command in a brace group and redirects the group, rather than
    /// appending `</dev/null` to the line: appended, a redirection binds to the last simple
    /// command only (`a; b </dev/null` leaves `a` on the terminal), is swallowed by a trailing
    /// comment (`ls # check </dev/null`), and lands on a heredoc's delimiter line. The closing
    /// brace goes on a line of its own for the same two reasons — a comment cannot eat it, and
    /// a heredoc's body has ended by the time the shell reads it. Braces, not parentheses: a
    /// group runs in THIS shell, so `cd` in the block still moves the next one, which is the
    /// property blocks are typed into one shell to have.
    ///
    /// Known edge, not repaired: a command whose last line ends in a backslash continues onto
    /// the brace line and breaks. Nothing an agent composes ends that way on purpose.
    let wrap (stdin: BlockStdin) (command: string) : string =
        match stdin with
        | BlockStdin.Terminal -> command
        | BlockStdin.Closed -> "{ " + command + "\n} </dev/null"

/// Whose blocks read from the terminal.
module BlockStdinPolicy =

    /// The agent's do not, unless it ASKED. Its commands arrive whole, as blocks, and the one
    /// that hung a real session's terminal for a day (`perl -i` with no filename) was not
    /// meant to read anything — so an agent that says nothing gets end-of-file, the true
    /// state of affairs said promptly rather than discovered by a timeout. An agent that
    /// knows it is running a prompt says so (`stdin: true` on `execute_command`), and then
    /// the terminal is its to answer. Everyone else's read the terminal whatever they said: a
    /// person queuing `sudo` is at the keyboard for the password prompt, and a repo's
    /// `setup:` runs in a terminal a person can still reach.
    let forAct (author: ActorRef) (askedForStdin: bool) : BlockStdin =
        match author with
        | ActorRef.Agent when not askedForStdin -> BlockStdin.Closed
        | _ -> BlockStdin.Terminal
