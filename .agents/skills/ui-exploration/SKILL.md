---
name: ui-exploration
description: How to inspect and iterate on Yession's UI (the manager page, a session's page, or any surface a running bin serves) with a real browser — boot the app, take honest pictures at desktop and true mobile viewports, look at them, measure layout instead of guessing, and loop until clean. Read this before reviewing, restyling, or debugging any UI, and before trusting a headless-Chromium screenshot.
---

# UI exploration

Style code is not the UI. `docs/visual-design.md` says what the design is for, and
`Style.fs` says what was *intended*; only a rendered page tells you what a user gets. This
skill is the loop: boot the real thing, picture it honestly, look at the picture, measure what surprises you, fix, repeat.

## The camera

One verb takes every picture, and it boots what it photographs:

```
dotnet fsi tasks.fsx frames --boot --still                          # a fresh session's page, phone
dotnet fsi tasks.fsx frames --boot --still --size 1440x900          # the same, desktop
dotnet fsi tasks.fsx frames --boot --still --page /                 # the Manager's own page
dotnet fsi tasks.fsx frames --boot --still --measure "<js>"         # and a measurement
```

It writes `still.png` under `--out` (default `./frames`) and prints the ground truth beside
it — `{"vw":390,"docW":390,"overflowX":false,"hover":false}`. Run it inside the dev shell:
in a yession work sandbox that is already around your shell; on a laptop, prefix
`devenv shell --` (or `nix develop --impure --command`).

`--boot` builds this tree and starts a scratch Manager for the one picture, then stops it.
Everything a hand-written boot gets wrong is inside it — a port of its own, a throwaway data
dir outside `/tmp`, ephemeral secrets, this repository's resources profile (without one a
session has no sandboxes and no terminal opens) — and it waits on the Manager *saying* it is
up, failing with the exit code when it dies first. Do not boot one yourself with `&` and an
`until curl` loop: that waits for ever on a Manager that died at launch.

When a loop needs ONE Manager across many pictures (seeded state, before/after), run
`start --scratch` as a background command — it prints `management UI at <url>` — and point
the camera at it: `frames --manager <url> --still …`. Seed it through the real endpoint, so
the rows exercise every state the view renders:
`curl -X POST <url>/sessions -H 'content-type: application/x-www-form-urlencoded' --data 'name=a much longer session name'`.

When the page MOVES — a jump, a flash, a pane that opens and shuts — one picture cannot show
it. Drop `--still` and the same verb films: every painted frame and every client render,
each labelled with the other. How to read a film is at the top of
`tools/Yession.Frames/Frames.fs`; that header is the guidance, and this is only the pointer.

## The traps the camera closes, and how to see they are closed

**Fake mobile viewports.** Headless Chromium clamps its window to ~500px wide, so
`--window-size=390 --screenshot` is a 500px layout cropped to 390 — overflow hides and the
wrapping a phone does never happens. The camera uses a true device viewport. Trust a picture
only when `vw` is the width you asked for; `overflowX: true` (or `docW > vw`) is overflow a
phone user cannot scroll back from, which is a reachability bug, not a cosmetic one.

**Hover that never happens.** Tailwind wraps every `hover:` utility in `(hover: hover)`, and
a headless browser's answer is whatever its host's is. The camera gives a `wide` screen (the
stylesheet's `phone:` variant negated: width >= 768 and height > 500) a mouse, and a phone, a
sideways one included, a finger, and prints `hover` so you can see which you got. A hover
picture is only a hover picture when `hover` is `true`.

**Focus that never applies.** A headless page is not the frontmost window, so `:focus`
matches nothing and a "focused" picture is the rest state. The camera turns on focus
emulation; check with `--measure "document.activeElement.matches(':focus')"`.

## The loop

1. **Picture three anchors.** 1440×900, 390×844 and 844×390. The shell's one breakpoint asks
   *is this screen a phone?*, and a phone on its side is still a phone — the `phone:`/`wide:`
   pair in `app/tailwind.css` answers by height as well as width, and 844×390 is the case that
   was wrong when only width answered. Widths between the anchors only where a reading-column
   rule sits (`max-md` = 768px).

2. **Look at the pictures.** Open `still.png` with your file reader — it shows images — and
   actually look. Never describe a picture you have not looked at. A checklist that catches
   real defects: Is anything clipped or off-canvas? Does hierarchy match importance (is the
   thing a person scans for in the biggest, brightest type)? Do left edges align on one
   rail? Are actions reachable and anchored? Is there an empty state? Does it hold the
   AGENTS.md "UI baseline" (WCAG 2.0 AA contrast, keyboard operability, visible focus)? Does
   it hold `docs/visual-design.md` — one filled verb per surface, borders only on fields and
   buttons, state worn at rest? That document is the doctrine, and its "Deciding" section is
   the order to settle a disagreement in; read it BEFORE changing a surface, not after a
   picture surprises you. The values it names are `app/tokens.css`.

3. **Measure, don't guess.** When a picture surprises you, ask the live page rather than
   theorising from CSS. `--measure` is evaluated in the page after it settles, awaited, and
   printed:

   ```
   frames --boot --still --page / --measure \
     "JSON.stringify([...document.querySelectorAll('td')].map(t => Math.round(t.getBoundingClientRect().width)))"
   ```

   It may return a Promise, which is how you picture a state the page has to settle into:
   focus a control and resolve a few hundred ms later, and the picture shows where the
   transition arrived rather than the frame it started from.

   Classic findings: a flex child's `min-width:auto` tracking a table's min-content and
   pushing the page wider than the viewport (`min-w-0` fixes it); and a class that is
   present, served and inert — Tailwind v4's `outline-none` sets `--tw-outline-style:none`,
   which every later outline utility resolves through, so a control wearing it plus a focus
   ring draws no ring, and only the computed style says so.

4. **Fix and picture again.** `--boot` rebuilds, so a fresh picture is of the code as it
   stands. Against a Manager you keep running, rebuild (`build` — Fable recompiles the
   module AND Tailwind rescans the F# sources; a class name never written as a literal in
   `.fs` is never generated) and restart it before blaming the browser.

## Before/after evidence

Pictures of the old code must come from the old code — reconstructing them from memory or
skipping them misleads. Check the old commit out in a worktree of its own
(`git worktree add <dir> <commit>`) and picture it there; never `git stash`, whose stack is
shared with every other checkout and session on the machine. Same seeded state, same
viewports, same camera on both sides.

## Contracts that must survive a restyle

- Tests pin behaviour, not looks: the `data-*` hooks and status words in
  `src/Yession.App/Dom.fs` (`Dom.Manager`), and the fragment-swap protocol (the row is
  the poll/action swap unit; the `[data-sessions]` element is the create swap unit —
  whatever carries that attribute is what gets replaced wholesale).
- Run the suite before calling it done: `check Ports Native` where the box can host it
  (see AGENTS.md Testing).
- The page must remain local: its program is F# (`app/browser/ManagerPage.fs`), served
  from the Manager's own asset set beside its stylesheet — no inline script, no CDN.
- The `/open` screen is the one exception, and deliberately: its program is an inline
  `<script>` at the end of `<body>` (`ManagerUi.openingProgram`), because its dwell is timed
  from when the script runs and a served module runs late. Do not move it out.
