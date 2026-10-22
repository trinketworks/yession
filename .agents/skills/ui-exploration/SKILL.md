---
name: ui-exploration
description: Inspect and iterate on Yession's UI (the manager page, a session's page, or any surface a running bin serves) with a real browser. Boot the app, take honest screenshots at desktop and true mobile viewports, and measure layout. Read this before you review, restyle or debug any UI, and before you trust a headless-Chromium screenshot.
---

# UI exploration

Style code is not the UI. [`docs/visual-design.md`](../../../docs/visual-design.md) says what the
design is for, and `Style.fs` says what the author *intended*. Only a rendered page shows what a
user gets. This skill is the loop: boot the real app, take an honest picture, look at it, measure
what surprises you, fix, and do it again.

## The camera

One verb takes every picture, and it boots what it photographs:

```
dotnet fsi tasks.fsx frames --boot --still                          # a fresh session's page, phone
dotnet fsi tasks.fsx frames --boot --still --size 1440x900          # the same, desktop
dotnet fsi tasks.fsx frames --boot --still --page /                 # the Manager's own page
dotnet fsi tasks.fsx frames --boot --still --measure "<js>"         # and a measurement
```

It writes `still.png` under `--out` (default `./frames`). Beside it, it prints the ground truth:
`{"vw":390,"docW":390,"overflowX":false,"hover":false}`. Run it inside the dev shell
([AGENTS.md, "Bootstrap"](../../../AGENTS.md#bootstrap)). On a laptop, put `devenv shell --` (or
`nix develop --impure --command`) in front of it.

`--boot` builds this tree, starts a scratch Manager for the one picture, and then stops it. It
does correctly each thing that a hand-written boot does wrong:

- a port of its own
- a throwaway data dir outside `/tmp`
- ephemeral secrets
- this repository's resources profile (without it, a session has no sandboxes and no terminal
  opens)

It waits until the Manager *says* it is up. If the Manager dies first, it fails with the exit
code. Do not boot a Manager yourself with `&` and an `until curl` loop. That loop waits for ever
on a Manager that died at boot.

Some loops need ONE Manager across many pictures (seeded state, before/after). For these, run
`start --scratch` as a background command. It prints `management UI at <url>`. Point the camera at
it: `frames --manager <url> --still …`. Seed it through the real endpoint, so that the rows
exercise every state the view renders:
`curl -X POST <url>/sessions -H 'content-type: application/x-www-form-urlencoded' --data 'name=a much longer session name'`.

One picture cannot show a page that MOVES (a jump, a flash, a pane that opens and closes). For
this, remove `--still`, and the same verb films. It records every painted frame and every client
render, and labels each with the other. To read a film, read the header at the top of
`tools/Yession.Frames/Frames.fs`. That header is the guidance. This text is only the pointer.

## The traps the camera closes, and how to see them closed

**Fake mobile viewports.** Headless Chromium clamps its window to ~500px wide. So
`--window-size=390 --screenshot` gives a 500px layout cropped to 390. Overflow hides, and a phone's
wrap never occurs. The camera uses a true device viewport. Trust a picture only when `vw` is the
width you asked for. `overflowX: true` (or `docW > vw`) is overflow that a phone user cannot scroll
back from. That is a reachability bug, not a cosmetic one.

**Hover that never happens.** Tailwind wraps every `hover:` utility in `(hover: hover)`. A
headless browser gives the answer its host gives. The camera gives a mouse to a `wide` screen (the
stylesheet's `phone:` variant negated: width >= 768 and height > 500). It gives a finger to a
phone, and to a phone on its side too. It prints `hover` so that you can see which one you got. A
hover picture is a hover picture only when `hover` is `true`.

**Focus that never applies.** A headless page is not the frontmost window. So `:focus` matches
nothing, and a "focused" picture shows the rest state. The camera turns on focus emulation. Check
it with `--measure "document.activeElement.matches(':focus')"`.

## The loop

1. **Take pictures at three anchors:** 1440×900, 390×844 and 844×390. The shell's one breakpoint
   asks *is this screen a phone?* A phone on its side is still a phone. The `phone:`/`wide:` pair
   in `app/tailwind.css` answers by height as well as width. 844×390 is the case that was wrong
   when only width answered. Add widths between the anchors only where a reading-column rule
   applies (`max-md` = 768px).

2. **Look at the pictures.** Open `still.png` with your file reader, which shows images, and
   look at it. Never describe a picture that you did not look at. This checklist finds real
   defects:

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

   `docs/visual-design.md` is the doctrine. Its
   ["Deciding"](../../../docs/visual-design.md#deciding) section gives the order to settle a
   disagreement. Read it BEFORE you change a surface, not after a picture surprises you. The
   values it names are in `app/tokens.css`.

3. **Measure. Do not guess.** When a picture surprises you, ask the live page. Do not make a
   theory from the CSS. The camera evaluates `--measure` in the page after the page settles,
   awaits the result, and prints it:

   ```
   frames --boot --still --page / --measure \
     "JSON.stringify([...document.querySelectorAll('td')].map(t => Math.round(t.getBoundingClientRect().width)))"
   ```

   `--measure` can return a Promise. Use this to take a picture of a state that the page needs
   time to reach. Focus a control and resolve a few hundred ms later. The picture then shows where
   the transition arrived, not the frame where it started.

   Classic findings:

   - A flex child's `min-width:auto` follows a table's min-content and makes the page wider than
     the viewport. `min-w-0` fixes it.
   - A class is present, served and inert. Tailwind v4's `outline-none` sets
     `--tw-outline-style:none`, and every later outline utility resolves through that variable.
     So a control with it and a focus ring draws no ring. Only the computed style shows this.

4. **Fix and take the picture again.** `--boot` rebuilds, so a new picture shows the code as it
   is now. If you keep a Manager running, rebuild (`build`) and restart it before you blame the
   browser. `build` compiles the module with Fable AND scans the F# sources again with Tailwind.
   Tailwind never generates a class name that no `.fs` file writes as a literal.

## Before/after evidence

Pictures of the old code must come from the old code. A picture from memory, or no picture,
misleads. Check out the old commit in its own worktree (`git worktree add <dir> <commit>`) and take
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
- The `/open` screen is the one exception, on purpose. Its program is an inline `<script>` at the
  end of `<body>` (`ManagerUi.openingProgram`). Its dwell time starts when the script runs, and a
  served module runs late. Do not move it out.
