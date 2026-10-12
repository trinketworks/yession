---
name: ui-exploration
description: Inspect and iterate on Yession's UI (the manager page, a session's page, or any surface a running bin serves) with a real browser. Boot the app, take honest screenshots at desktop and true mobile viewports, and measure layout. Read this before you review, restyle or debug any UI, and before you trust a headless-Chromium screenshot.
---

# UI exploration

Style code is not the UI. `docs/visual-design.md` says what the design is for, and `Style.fs` says
what was *intended*. Only a rendered page shows what a user gets. This skill is the loop: boot the
real thing, picture it honestly, look at the picture, measure what surprises you, fix, and repeat.

## The camera

One verb takes every picture, and it boots what it photographs:

```
dotnet fsi tasks.fsx frames --boot --still                          # a fresh session's page, phone
dotnet fsi tasks.fsx frames --boot --still --size 1440x900          # the same, desktop
dotnet fsi tasks.fsx frames --boot --still --page /                 # the Manager's own page
dotnet fsi tasks.fsx frames --boot --still --measure "<js>"         # and a measurement
```

It writes `still.png` under `--out` (default `./frames`) and prints the ground truth beside it —
`{"vw":390,"docW":390,"overflowX":false,"hover":false}`. Run it inside the dev shell. In a yession
work sandbox the dev shell already surrounds your shell. On a laptop, prefix `devenv shell --` (or
`nix develop --impure --command`).

`--boot` builds this tree and starts a scratch Manager for the one picture, then stops it. It
handles everything that a hand-written boot gets wrong. That is a port of its own, a throwaway
data dir outside `/tmp`, ephemeral secrets, and this repository's resources profile (without one,
a session has no sandboxes and no terminal opens). It waits until the Manager *says* it is up, and
it fails with the exit code when the Manager dies first. Do not boot a Manager yourself with `&` and an
`until curl` loop. That loop waits for ever on a Manager that died at launch.

When a loop needs ONE Manager across many pictures (seeded state, before/after), run
`start --scratch` as a background command. It prints `management UI at <url>`. Point the camera at
it: `frames --manager <url> --still …`. Seed it through the real endpoint, so the rows exercise
every state the view renders:
`curl -X POST <url>/sessions -H 'content-type: application/x-www-form-urlencoded' --data 'name=a much longer session name'`.

When the page MOVES — a jump, a flash, a pane that opens and shuts — one picture cannot show it.
Drop `--still` and the same verb films: every painted frame and every client render, each labelled
with the other. The header at the top of `tools/Yession.Frames/Frames.fs` explains how to read a
film. That header is the guidance, and this text is only the pointer.

## The traps the camera closes, and how to see they are closed

**Fake mobile viewports.** Headless Chromium clamps its window to ~500px wide. So
`--window-size=390 --screenshot` is a 500px layout cropped to 390. Overflow hides, and the wrapping
that a phone does never happens. The camera uses a true device viewport. Trust a picture only when
`vw` is the width you asked for. `overflowX: true` (or `docW > vw`) is overflow that a phone user
cannot scroll back from. That is a reachability bug, not a cosmetic one.

**Hover that never happens.** Tailwind wraps every `hover:` utility in `(hover: hover)`, and a
headless browser answers with whatever its host answers. The camera gives a `wide` screen (the
stylesheet's `phone:` variant negated: width >= 768 and height > 500) a mouse. It gives a phone, a
sideways one included, a finger. It prints `hover` so you can see which you got. A hover picture is
only a hover picture when `hover` is `true`.

**Focus that never applies.** A headless page is not the frontmost window, so `:focus` matches
nothing, and a "focused" picture is the rest state. The camera turns on focus emulation. Check it
with `--measure "document.activeElement.matches(':focus')"`.

## The loop

1. **Picture three anchors.** Use 1440×900, 390×844 and 844×390. The shell's one breakpoint asks
   *is this screen a phone?* A phone on its side is still a phone. The `phone:`/`wide:` pair in
   `app/tailwind.css` answers by height as well as width, and 844×390 is the case that was wrong
   when only width answered. Add widths between the anchors only where a reading-column rule sits
   (`max-md` = 768px).

2. **Look at the pictures.** Open `still.png` with your file reader, which shows images, and
   actually look. Never describe a picture that you have not looked at. This checklist catches
   real defects:

   - Is anything clipped or off-canvas?
   - Does hierarchy match importance? Is the thing a person scans for in the biggest, brightest
     type?
   - Do left edges align on one rail?
   - Are actions reachable and anchored?
   - Is there an empty state?
   - Does it hold the AGENTS.md "UI baseline" (WCAG 2.0 AA contrast, keyboard operability,
     visible focus)?
   - Does it hold `docs/visual-design.md`: one filled verb per surface, borders only on fields
     and buttons, state worn at rest?

   `docs/visual-design.md` is the doctrine, and its "Deciding" section is the order to settle a
   disagreement in. Read it BEFORE you change a surface, not after a picture surprises you. The
   values it names are `app/tokens.css`.

3. **Measure, don't guess.** When a picture surprises you, ask the live page. Do not theorise from
   CSS. `--measure` is evaluated in the page after it settles, awaited, and printed:

   ```
   frames --boot --still --page / --measure \
     "JSON.stringify([...document.querySelectorAll('td')].map(t => Math.round(t.getBoundingClientRect().width)))"
   ```

   `--measure` may return a Promise. That is how you picture a state that the page has to settle
   into. Focus a control and resolve a few hundred ms later, and the picture shows where the
   transition arrived and not the frame it started from.

   Classic findings:

   - A flex child's `min-width:auto` tracks a table's min-content and pushes the page wider than
     the viewport. `min-w-0` fixes it.
   - A class is present, served and inert. Tailwind v4's `outline-none` sets
     `--tw-outline-style:none`, which every later outline utility resolves through. So a control
     that wears it plus a focus ring draws no ring, and only the computed style says so.

4. **Fix and picture again.** `--boot` rebuilds, so a fresh picture shows the code as it stands.
   Against a Manager that you keep running, rebuild (`build`) and restart it before you blame the
   browser. `build` recompiles the module with Fable AND rescans the F# sources with Tailwind. A
   class name never written as a literal in `.fs` is never generated.

## Before/after evidence

Pictures of the old code must come from the old code. Reconstructing them from memory, or skipping
them, misleads. Check the old commit out in a worktree of its own
(`git worktree add <dir> <commit>`) and picture it there. Never `git stash`: its stack is shared
with every other checkout and session on the machine. Use the same seeded state, the same
viewports and the same camera on both sides.

## Contracts that must survive a restyle

- Tests pin behaviour, not looks: the `data-*` hooks and status words in
  `src/Yession.App/Dom.fs` (`Dom.Manager`), and the fragment-swap protocol. The row is the
  poll/action swap unit. The `[data-sessions]` element is the create swap unit, and whatever
  carries that attribute is what gets replaced wholesale.
- Run the suite before you call it done: `check Ports Native` where the box can host it
  (see AGENTS.md Testing).
- The page must remain local. Its program is F# (`app/browser/ManagerPage.fs`), served from the
  Manager's own asset set beside its stylesheet. Use no inline script and no CDN.
- The `/open` screen is the one exception, deliberately. Its program is an inline `<script>` at
  the end of `<body>` (`ManagerUi.openingProgram`). Its dwell is timed from when the script runs,
  and a served module runs late. Do not move it out.
