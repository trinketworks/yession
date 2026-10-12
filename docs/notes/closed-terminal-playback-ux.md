# Closed-terminal playback — UX tryout notes (2026-08-21)

> The state-machine fix below — terminal history as **position × fidelity** — has since
> shipped, along with items 1–6. Item 7 is the only one still open. The text records the code
> as it stood on 2026-08-21. The four pane fields and the messages named below are gone.
> `PaneMode` (`src/Yession.App/Model.fs:817`) replaced them.

Method: I booted the real app in the dev container (`node app/out/Main.js --auth localhost`, scratch
`YESSION_DATA_DIR`, `YESSION_NESTED_SANDBOX=weak`). It had one session, one terminal, and three
block commands (`ls -la`, a colored `printf`, `echo … && sleep 1 && echo …`). I killed the terminal
from the list. Then I clicked through every playback path in headless Chromium over CDP. I used real
mouse and key events, with focus emulation on, at 1440×900 and at a true 390×844 viewport. The
paths:

- Chat chip → block tab → *Play recording* → *Back to output* → *Play whole terminal* → *Back to
  blocks*.
- The *↑ play the recording* of a closed terminal.
- The closed-row verbs of the terminal list.

I read positions and timers from the live page, and recording and keyframe bytes from the
transcript sidecars.

## Broken

1. **Every replay renders staircased garbage.** Block-mode output is recorded with bare `\n` (no
   `\r`). Transcript records read `"total 4\ndrwxr-xr-x …"`. The keyframe sidecar serializes the
   resulting screen with the staircase baked in (`\x1b[7C`/`\x1b[49C` line starts in `…keys.jsonl`).
   A VT moves down WITHOUT a carriage return on LF. The player does this, and so does the Session
   Process emulator that produces keyframes. Every line starts where the previous one ended and
   wraps off the right edge. The text views split on `\n` and look right. Every player view (block
   range, whole terminal, desktop and phone) shows the garble. The upstream fix is at transcript
   append (or pty-ize block exec). The downstream emulator has the same bytes and the same problem.

2. **A chat chip tapped while the pane shows the terminal list looks dead.** `OpenPaneTabMsg`
   (`Model.fs` at that date, since removed) sets `PaneChoice` but does not clear `TerminalList`.
   The pane header retitles, and the body keeps showing the list: no tab, no block, nothing
   apparent. `SelectFromListMsg`,
   right below it, clears the flag, and its comment states exactly why. Measured: chip click →
   `data-pane-panel` absent, list still mounted. The phone screenshot in the task came from this
   state (list face up), so a real reader hits this path.

3. **"Play whole terminal" does not land on the block.** In three desktop runs, playback starts at
   ~01:45. The marker of the printf block sits at 02:06, so the ~21s gap plays as dead air in real
   time. One phone run landed at or near the final frame. No run landed on the block. Related
   observations from the same mounts:
   - `idleTimeLimit 2` (now `src/Yession.App/Codecs/Transcripts.fs:189`, passed to the player by
     `app/browser/Replay.fs`) has no visible effect. The 0:23 and 1:44 idle gaps play out at
     1×, and the total duration shows raw 02:07.
   - Only 2 of the 3 block markers render (`.ap-marker` count). The `echo` marker at ~127.6s of a
     ~128.6s cast is missing.

   Suspect that the raw-clock `startAt`/`markers` and `idleTimeLimit` disagree about whose timeline
   they are on. It needs a look at how asciinema-player 3.x combines those options.

   *(Follow-up, confirmed live:)* **the rewind verb of the terminal list does nothing but select.**
   Its click dispatched `RewindTerminalMsg` then `SelectFromListMsg` (`View.fs` at that date).
   The second reducer clears the `PanePlaying`/`PaneRewound` that the first just set. On an open terminal with
   recorded output, I saw no player, no *Jump to live*, and no behind-label. The ordinary blocks
   view and the composer rendered. The `↑ replay from the start` of the pane itself (one message)
   works: player, *Jump to live*, "behind live", focus handed on.

## The play state machine, as it stands

Four model fields have to agree for the pane to show the right thing. Every control writes a
different subset of them:

- `PaneChoice : PaneTab option` — which tab shows. It doubles as the preview slot.
- `PanePlaying : (PaneTab * int option) option` — "watch the recording of this tab". The `int` is a
  transcript line that only the `TerminalTab` mount reads (the start hint of the step-out).
- `PaneRewound : (TerminalId * int) option` — the DVR pin.
- `TerminalList : bool` — the census face. While true, it masks whatever the rest says.

| control → message | Choice | Playing | Rewound | List |
|---|---|---|---|---|
| chat chip → `OpenPaneTabMsg` | tab | ∅ | ∅ | **kept (bug 2)** |
| *Play recording* / *↑ play the recording* / *Play whole terminal* → `PlayRecordingMsg` | tab | (tab, seq?) | kept | kept |
| *Back to output* → `SelectPaneTabMsg` | tab | ∅ | ∅ | kept |
| *Back to blocks* → `SelectTerminalMsg` | terminal | ∅ | ∅ | kept |
| *↑ replay from the start* → `RewindTerminalMsg` | terminal | (terminal, ∅) | (t, len) | kept |
| *Jump to live* → `JumpToLiveMsg` | terminal | ∅ | ∅ | kept |
| list row → `SelectFromListMsg` | terminal | ∅ | ∅ | false |
| list rewind → `RewindTerminalMsg` **then** `SelectFromListMsg` | terminal | set, then ∅ | set, then ∅ | false |

Whether a player is actually on screen (`playsRecording`) depends on the tab kind:

- A `BlockTab` plays only when `PanePlaying` names it.
- A `StretchTab` always plays, because replay is its only read.
- A `TerminalTab` plays when `PanePlaying` names it, or when the affordance fold says
  `ReplayIsTheRead` (closed, no blocks).
- `TerminalList` overrides all of it at render.

Why it reads as a mess in the hand:

- **One message, two different acts.** `PlayRecordingMsg` on a `BlockTab` swaps the view INSIDE the
  tab. The exit is *Back to output*, in the bottom action row, which relabels in place. On a
  `TerminalTab`, the same message REPLACES the tab. The exit is *Back to blocks*, a floating
  overlay. Same verb, different blast radius, different exit control in a different slot.
- **The step-out eats your place.** The block tab is the preview. *Play whole terminal* replaces it
  with the terminal tab, so *Back to blocks* lands on the scrollback of the terminal. The block tab
  no longer exists. Only the chip in the chat leads back to it. A chip → block → whole → back round
  trip does not return.
- **Partial clears.** Each reducer resets only the fields that its author had in mind.
  `OpenPaneTabMsg` clears playing and pin but not the list face (bug 2). The list rewind pairs two
  messages whose clear-sets cancel. `PlayRecordingMsg` clears nothing and relies on every path into
  it having cleaned up first.
- **Four ways into a player** — block play, chip step-out, closed-terminal play, DVR rewind. Each
  has its own back-affordance, focus target and clear-set. A player can be "on" in three ways
  (`PanePlaying`, stretch-always, `ReplayIsTheRead`).

The shape suggests one explicit mode instead of four fields that must agree. For example:
`PaneMode = Reading of PaneTab | Watching of {Tab; StartLine option; Pin option}`. The list face
would become a fifth, `PaneTab`-like destination and not a boolean mask. Every transition then
states the whole next mode, and a control cannot half-clear its way into a state that no one
designed.

## Step back: what is the reader actually trying to do?

The controls name the nouns of the implementation: *blocks*, *recording*, *whole terminal*. A user
has none of these in their head. "Back to blocks" answers the developer's question "which projection
of the transcript am I mounted on". A reader in this flow has only three intents:

1. **"What did this command print?"** — the promise of the chip. Text answers it: skimmable,
   copyable, searchable. The block tab does this well.
2. **"What was going on around it?"** — context. The natural answer is the scrollback of the
   terminal, *scrolled to that command* — more of the same text. Today the step-out answers it with
   a VIDEO that opens twenty seconds of dead air away (bug 3). That is the most expensive
   presentation, at the wrong position, for an intent that wanted cheap text.
3. **"Show me what actually happened, as it happened"** — behavior: a TUI session, a progress bar,
   timing, the thing that text cannot carry. Only this intent wants a player at all.

The reader always looks at ONE thing: the history of this terminal. They look at a **position**
(which command, or how far in), in one of two **fidelities** (text, or playback). The axes are
orthogonal. Every confusion above comes from encoding them as *destinations you travel between*:

- "Play recording" / "Back to output" is a fidelity toggle in navigation words. It behaves like the
  toggle it is (same slot, focus kept). Its label is wrong.
- "Play whole terminal" fuses a POSITION change (this command → its surroundings) to a fidelity
  change (text → playback). The trip mutates two axes and the exit restores one, so "back" has
  nowhere to return to.
- "Back to blocks" is the fidelity toggle again. Entry conflated the axes, so exit is a different
  control with a different name in a different slot. The name is the internal noun.
- The DVR is the same fidelity swap on a live terminal (position pinned at "now"), and "Jump to
  live" is its way back.

The reframe: **position is navigation, fidelity is a mode, and the mode never moves you.**
Concretely:

- The context intent of a chip gets its own verb: **"Show in terminal"**. It opens the ordinary text
  scrollback of the closed terminal, scrolled to that command and highlighting it, with no player.
  This is what "play whole terminal" was reaching for. Text answers it instantly, with none of the
  dead air of bug 3.
- One **watch/read toggle**, with one label pair everywhere (*Watch* / *Show output* — words that a
  user has), in one slot, with focus kept. It works on a block, on a closed terminal, and on a live
  one (where *Watch* means the DVR and the way back is *Live*). Toggling fidelity NEVER changes
  position, so every "where did my tab go" case disappears without a rule to remember.
- The player, when entered from a command, starts AT that command because position carried over. No
  `startAt` hint needs to ride a message and survive the right subset of reducers.

`position × fidelity` is the `PaneMode` record above, reached from the side of the user. Four fields
keep half-agreeing today because the vocabulary of the UI never named these two axes.

## Rough edges

4. **The first row of a chat group hides under its sticky author header.** The header
   (`sticky top-0 z-10 bg-bg pt-6 -mt-6`) paints over the top 16px of the first chip. This holds
   even with the scroller at `scrollTop 0`. Measured: header box 112–160, chip 144–172.
   `elementFromPoint` at the upper half of the chip returns the header. Those pixels also swallow
   the tap. This is visible in every desktop screenshot as `$ ls -la` clipped behind WARM-IBEX. The
   phone overlaps by a few px (max-md paddings). That is visually fine, but the tap target is
   thinner.

5. **Focus strands on two swaps.** Both replace the pressed control with a different surface. After
   *Play whole terminal* (block tab → terminal tab player) and after a kill of a terminal from the
   list, `document.activeElement` is `<body>`. The neighbours do it right. *Play recording*/*Back to
   output* relabel in place and keep focus. *Back to blocks* hands focus to *↑ play the recording*.
   A chip hands focus to the panel. UI-baseline rule: a swap that removes the focused element
   refocuses its replacement.

6. **"CLOSED — CLOSED BY A PEER" discards who.** `src/Yession.Session/Commands.fs:102` hardcodes
   the reason string `"closed by a peer"`. At that date the `peerId` of the closer was dropped, so
   no client could attribute the close. The attribution invariant (a thing that was done is
   attributed to whoever did it) says this should be a name. It is one now. The close records the
   closer (`By = Some by`, `src/Yession.Session/Terminals.fs:1908`), and the closed band says "you
   killed" or "killed by" (`closedFor` in `src/Yession.App/View.fs`).

7. **Sub-second commands make zero-length recordings.** The scrub bar of a fast block reads
   `00:00-00:00`, and play is a flash. The text-first design absorbs it, but the player timeline
   reads broken. Consider not offering *Play recording* under some minimum recorded duration, or
   showing the wall-clock span of the range.

## Worked well

- The read of a closed terminal: full scrollback with colors. *↑ play the recording* sits at the top
  of history. *Back to blocks* floats in the slot of the reader. The closed band sits at the bottom.
- Chip → block tab → output as text first, with the recording one press away. The pin/preview
  semantics. The pane on the phone (column swap, actions reachable, focus into the panel).
- Player chrome: control bar, keyboard shortcuts, fullscreen. The chapters concept (when the markers
  render).
