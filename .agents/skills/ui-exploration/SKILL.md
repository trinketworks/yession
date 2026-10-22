---
name: ui-exploration
description: Inspect and iterate on Yession's UI (the manager page, a session's page, or any surface a running bin serves) with a real browser. Boot the app, take honest screenshots at desktop and true mobile viewports, and measure layout. Read this before you review, restyle or debug any UI, and before you trust a headless-Chromium screenshot.
---

# UI exploration

Style code is not the UI. Only a rendered page shows what a user gets. The loop: boot the real
app, take an honest picture, look at it, measure what surprises you, fix, and do it again.

## The camera


```
dotnet fsi tasks.fsx frames --boot --still                          # a fresh session's page, phone
dotnet fsi tasks.fsx frames --boot --still --size 1440x900          # the same, desktop
dotnet fsi tasks.fsx frames --boot --still --page /                 # the Manager's own page
dotnet fsi tasks.fsx frames --boot --still --measure "<js>"         # and a measurement
```

It writes `still.png` under `--out` (default `./frames`) and prints the ground truth:
`{"vw":390,"docW":390,"overflowX":false,"hover":false}`. Run it inside the dev shell
([AGENTS.md, "Bootstrap"](../../../AGENTS.md#bootstrap)). On a laptop, put `devenv shell --` (or
`nix develop --impure --command`) in front of it.

`--boot` builds this tree and starts a scratch Manager for the one picture, with its own port, a
throwaway data dir outside `/tmp`, ephemeral secrets and this repository's resources profile
(without it, no terminal opens). It fails with the exit code if the Manager dies before it says
it is up. Do not boot a Manager yourself with `&` and an `until curl` loop: it waits for ever on a
dead Manager. Why is in `booted` in `tasks.fsx`.

Some loops need ONE Manager across many pictures (seeded state, before/after). For these, run
`start --scratch` as a background command. It prints `management UI at <url>`. Point the camera at
it: `frames --manager <url> --still …`. Seed it through the real endpoint, so that the rows
exercise every state the view renders:
`curl -X POST <url>/sessions -H 'content-type: application/x-www-form-urlencoded' --data 'name=a much longer session name'`.

For a page that MOVES (a jump, a flash, a pane that opens and closes), remove `--still` and the
verb films. The header of `tools/Yession.Frames/Frames.fs` says how to read a film.

## The traps the camera closes, and how to see them closed

Headless Chromium fakes a phone width, has no hover and never has focus. The camera corrects all
three. The comments in `Frames.fs`, where it launches Chromium, say how. Check each picture:

- **Viewport.** Trust a picture only when `vw` is the width you asked for. `overflowX: true` (or
  `docW > vw`) is overflow that a phone user cannot scroll back from: a reachability bug, not a
  cosmetic one.
- **Hover.** The camera gives a mouse to a `wide` screen (width >= 768 and height > 500) and a
  finger to a phone, upright or on its side. A hover picture is one only when `hover` is `true`.
- **Focus.** Check it with `--measure "document.activeElement.matches(':focus')"`.

## The loop

1. **Take pictures at three anchors:** 1440×900, 390×844 and 844×390 (a phone on its side, see
   [visual-design.md, "Phone and wide"](../../../docs/visual-design.md#phone-and-wide)). Add
   widths between the anchors only where a reading-column rule applies (`max-md` = 768px).

2. **Look at the pictures.** Open `still.png` with your file reader. Never describe a picture
   that you did not look at. Checklist:

   - Is anything clipped or off-canvas?
   - Does hierarchy match importance? Is the item a person scans for in the biggest, brightest
     type?
   - Do left edges align on one rail?
   - Can the user reach each action, and is each action anchored?
   - Is there an empty state?
   - Does it hold the [AGENTS.md "UI baseline"](../../../AGENTS.md#ui-baseline) (WCAG 2.0 AA
     contrast, keyboard operability, visible focus)?
   - Does it hold `docs/visual-design.md`: one filled verb per surface, borders only on fields
     and buttons, state worn at rest?

   Read `docs/visual-design.md` BEFORE you change a surface. Its
   ["Deciding"](../../../docs/visual-design.md#deciding) section settles a disagreement.

3. **Measure. Do not guess.** When a picture surprises you, ask the live page, not the CSS.
   `--measure` runs in the settled page and prints its awaited result:

   ```
   frames --boot --still --page / --measure \
     "JSON.stringify([...document.querySelectorAll('td')].map(t => Math.round(t.getBoundingClientRect().width)))"
   ```

   To picture a state the page needs time to reach, return a Promise: focus a control and
   resolve a few hundred ms later.

   Classic findings:

   - A flex child's `min-width:auto` follows a table's min-content and makes the page wider than
     the viewport. `min-w-0` fixes it.
   - A class is present, served and inert. Tailwind v4's `outline-none` sets
     `--tw-outline-style:none`, and every later outline utility resolves through that variable.
     So a control with it and a focus ring draws no ring. Only the computed style shows this.

4. **Fix and take the picture again.** `--boot` rebuilds. If you keep a Manager running,
   `build` and restart it before you blame the browser. Tailwind generates only class names that
   a `.fs` file writes as a literal.

## Before/after evidence

Pictures of the old code must come from the old code. Check out the old commit in its own worktree (`git worktree add <dir> <commit>`) and take
the picture there. Never use `git stash`: every other checkout and session on the machine shares
its stack. Use the same seeded state, the same viewports and the same camera on both sides.

## Contracts that must survive a restyle

- Tests pin behaviour, not looks. They pin the `data-*` hooks and status words in
  `src/Yession.App/Dom.fs` (`Dom.Manager`), and the fragment-swap protocol. The row is the
  poll/action swap unit. The `[data-sessions]` element is the create swap unit, and a create
  replaces the whole element that carries that attribute.
- Run the suite before you call the work done: `check Ports Native` where the box can host it
  (see [AGENTS.md, "Testing"](../../../AGENTS.md#testing)).
- The page must stay local. Its program is F# (`app/browser/ManagerPage.fs`). The Manager serves
  it from its own asset set, beside its stylesheet. Use no inline script and no CDN.
- The `/open` screen is the one exception, on purpose: an inline `<script>`. Do not move it out.
  The reason is on `ManagerUi.openingProgram`.
