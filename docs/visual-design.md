# Yession — visual design

This document is the doctrine: what the product looks like, and why. Every rule has a test, so
a visual question has a derivation and not a taste.

[`app/tokens.css`](../app/tokens.css) defines every value the rules name — a colour, a type
step, a length. Read it with this file. [`src/Yession.App/Style.fs`](../src/Yession.App/Style.fs)
composes what each surface wears. [`technical-design.md`](technical-design.md) owns the
architecture. [`brand/README.md`](brand/README.md) is the record of the brand mark.

## Stance

Yession is a working room. It holds one conversation, a queue, and the means to intervene
fast. Its anatomy is Slack's or Cursor's: a sidebar, a conversation, a pane. Its finish is
flat: sharp rectangles on black, and nothing that does no job.

## Principles

Apply each test before you change a surface.

1. **Content is the interface.** Chrome shows where things belong and which thing is the
   selection. It does nothing more.
   *Test: remove the element. If the content, its groups and the selection are no harder to
   see, the element goes.*

2. **Chrome shows structure. Type shows importance.** A surface step, a fill or a rule says
   grouping, nesting and selection. Within a group, size, weight and position say what
   matters. Words do not do a surface's job.
   *Test: blur the screen until no word can be read. The groups and the selected thing must
   still be visible.*

3. **One verb per surface.** Each surface has one primary act, and only that act wears the
   blue fill. Every other control wears a border, nothing, or text. An armed control's `err`
   fill is a different mark with a different job, and a surface may wear one of each.
   *Test: count the blue filled rectangles on a surface. The count is one or zero.*

4. **State is worn, not hinted.** What a thing is doing — waiting, held, armed, live, wrong —
   is visible at rest. A thumb has no hover. A reader has no tooltip.
   *Test: take a screenshot. You must be able to tell what each control is doing.*

5. **One way per job.** Every visual move is a named value composed from tokens. A second way
   to draw the same thing is a bug, not a variant.
   *Test: search for the raw utility. If it appears outside the vocabulary that owns it, fold
   it back in.*

6. **Honest materials.** Surfaces are flat. There are no shadows, no skeuomorphism, and one
   gradient. Depth is said by blur over something real (`acrylic`), never by a painted edge.
   *Test: no surface may look lit from somewhere.*

## Materials

### Type

Three faces, one rule: **sans is the machine, serif is the person, mono is the terminal.**
`font-ui` is the chrome and the agent's voice. `font-human` is what a person wrote.
`font-terminal` is the shell. The face says who is talking before the reader reaches the
author line.

Every size is a step on the ramp (`text-label` … `text-wordmark`, and `text-code` for mono). A
step sets the size and the line box together. Nothing sits off the ramp.

One exception: a field a person types into on a phone takes `text-touch` (`Style.touchType`).
Under it, a phone zooms to the field and stays zoomed, and the other columns leave the screen.
That floor is never traded for the ramp. The exemption is a field under a finger, not a size
to use elsewhere.

The caps voice — `text-label`, `tracking-caps`, semibold — is for short labels, statuses and
buttons. A stack of caps lines is a wall of text. Let a surface say that grouping instead.

### Colour

The ground is `bg`, black. It steps up in greys — `panel`, `surface`, `surface-2` — and each
step is one level of nesting or a selection. Where a step says a thing is chosen, a mark says
it too. The mark is the carrier and the step is emphasis. So hover, which is transient and
which no thumb has, may share the step. The mark is what a reader finds at rest. Three hues
mean three things:

- `blue` is interactive, and it is the agent. The agent's own marks (its diamond, its caret,
  the cube that turns while it thinks) wear `agent`, one tone under `blue`, so a reader can
  tell the agent from a thing to press. Text in either is the interactive blue, because a
  shade under it would fall below the text floor on the panels.
- `green` is live, and it is the people.
- `err` is wrong.

Blue is never a status, with one exception: the attention mark of an agent at work (Working,
see Attention) wears the agent's own blue, because it is a mark of the agent, and that blue is
the tone a control never wears. A wait is `ink-dim` text: reconnecting, catching up, queued,
looking and no agent yet. A wait leads with the spin mark (see Motion): two arcs, always grey,
turning in place rather than beating.

Live is `green` text: a running command, a connected peer, a session that runs. It leads with
the same spin mark a wait does — one shape for "still going", whichever of the two it is — and
the word beside it, not the mark, says which. A record of something that finished is `ink`.

Wrong is `err`. The one red word on a surface is the newest wrong thing. An older wrong thing
is a record and wears `ink-dim`. A repeated row, or a repeated item in the conversation, is a
surface of its own. Twenty failed rows are twenty surfaces, and each keeps its red word.
*Test: count the red words on a surface. The count is one or zero. Count a repeated row by
itself.*

A reader tells people apart by their square avatars, never by a name colour.

The blue→green gradient exists once, on the composer's focus edge, where the person and the
agent meet.

A fade is a gradient under another name, and the one gradient is spent. A mask that fades
content out is a fade. It never crosses text, because faded glyphs fall under the contrast
floor. Where a surface holds more than fits, it says so with a control or a rule.
*Test: find every mask and every fade. No glyph is under one.*

A status is text in its colour, and it leads with a mark: a dot if it has settled, or the spin
mark (see Motion) if it is still under way. It is never a filled or boxed badge. A verb never
leads with a mark. So the mark, not a tone, says that a word cannot be pressed: a tone is one
step of grey that a blur or a greyscale print hides, and principle 2 blurs the screen.
*Test: a caps word with a leading mark is a status. A caps word without one can be pressed.*

A status names a state. A reason says why. A reason is body text in the body voice, with no
mark and no caps. A surface may carry both, the state first and the reason under it.
*Test: do the words answer "what state" or "why"? What state leads with a mark. Why does not,
and is not in caps.*

A state has one name, on every screen, column and side of the wire. A token a test reads is
the same name spelled for a machine, not a second name. A state with two names is a state a
reader cannot look up.
*Test: find every face that draws one state. They all say one word.*

Dimming is a role (`ink-dim`, `ink-faint`), never opacity. Opacity breaks the contrast floor
and dims the focus ring with it.

Dimming has a limit. In a palette where every colour is already at the floor, no colour has a
dimmer one that still passes. A dim variant of that palette takes `ink-faint` for every
colour. Where ink sits on a fill and not on the ground, the pair sets the contrast, so the ink
keeps the colour its author chose.

### Form

Two primitives. The **rectangle** is always sharp-cornered. The **point** is always round. A
rounded rectangle is a hybrid of the two, and it does not exist in this product. Icons follow
the same geometry: square caps, no curves, no fills, and a stroke weight matched to the type
beside them.

The brand mark (`Brand.mark`) is the one lit object. It is a picture, not a surface, so
"Honest materials" does not govern it. [`brand/README.md`](brand/README.md) records how it is
lit. The exemption is that object, not that one value: a picture of the mark's object may carry
its shading anywhere. The agent's thinking cube (`Style.agentThinking`) is the mark's cube as
it turns, so it keeps its shaded faces. A picture holds no words and takes no press. Nothing
else may look lit.

### Stroke

`Style.Stroke` composes every border from a small, named set of strokes. The comment above
`Style.Stroke` names each one, what it is for, its tones, and what interaction does to it.
Nobody writes a bare `border-*` utility outside `Stroke`.

### Space

Positions derive from the grid, never from the eye. Every control with an edge is one height,
`spacing-control`, so a row is things of one height. A filled control, a bordered control and
a field have an edge. A bare verb has no edge and takes the height of its row. On a phone, a
control with an edge grows to the touch target. A bare verb grows only its hit area. Rows
align on the text baseline. Padding is spent outward, so text sits on the column's rail.

Each column has one rail. Every line of text in the column starts on it, and no screen width
moves it. A column also keeps an inset from the screen edge, which a phone can make smaller.
The inset is not the rail.

Columns do not share one rail. `spacing-rail` is the conversation column's rail, because that
is the reading column. A narrower column has a narrower rail, because two 32px rails spend 64
of a phone's 390 pixels. Such a column names its own value in `app/tokens.css`. Nobody writes
a rail as a number at the site.

A mark that leads a line — a prompt, a reply mark, a fold arrow, a dot — hangs in the gutter
left of the rail. A mark never pushes the words in.
*Test: draw a vertical line on one column's rail. Every first letter in that column touches the
line. Then make the screen a phone. The line keeps its distance from the column's inset.*

A table's column is a column, so this rule governs it. "The layout of one surface"
([Not governed](#not-governed)) does not exempt a column of text from its rail. Where a form
edits what a table lists, the form's fields stand on that table's columns. Two surfaces that
list the same kind of thing agree on their columns, or they list two kinds of thing.
*Test: draw a vertical line down a table's column. The field that edits it touches the line.*

The rhythm governs a line box and a position. A mark's size and a type size are neither. A
mark takes its size from the type beside it, and its position still derives from the grid.

A column is drawn only while it has room for one item: its shortest item, plus whatever it
pins above its items. With less room, it is not drawn, because a column that scrolls and
cannot be touched promises what it cannot give. Nothing is drawn in its place. The surface that
took the room carries the way back, as one control. A person who cannot see the column must
still be able to reach it. This governs every column: a pane, a list and a strip each answer
it for themselves.
*Test: shorten the screen until a column has less room than one item. The column is gone, and
one press on the surface over it brings the column back.*

The header band is `spacing-band` tall, and `spacing-band-phone` on a phone. That is the
height of a one-row head, and every such head shares it. The hairline under it runs across
every column at one height.

A head with two rows of controls is taller, because each row meets the touch target and the
floor is never traded for the band. Such a head states its rows, not its total. Its hairline is
its own and lines up with nothing — unless the head pins its FIRST row to the band instead of
letting the band be the rows' sum, which is how a column with a two-row head aligns its hairline
with a sibling's single-row one (the session pane's tab strip, `Style.panePivotRow`, against the
chat header). Each row still meets the touch target: a control whose own ground should read as
the row (a selected tab) stretches to fill the taller row edge to edge, so that ground runs all
the way to the hairline instead of stopping short of it with a gap of the strip's own ground
showing; a control with its own fixed height (`paneListButtonBase`, `paneStripAct`) stays
centred in it instead (`self-center`).

A thing that leaves the ground sits on a named layer. `app/tokens.css` names the layers, low to
high, under "Layers". A phone stacks several at once: a drawer over a scrim over the page, with a
notice the reader must still be able to answer. Nobody writes a stacking order as a number.
*Test: search for a `z-` utility with a number in it. There are none.*

### Motion

One vocabulary. Things arrive by sliding a little and fading in, at `pace`, eased out. Press
is a tilt: in fast, back with a hair of overshoot. A pressed control is *held* (see State).

A settled status's dot is still; nothing left of it beats. A status still under way — a
command live now, or a wait — turns instead: two arcs, always `ink-dim`, passing each other as
they travel opposite ways round a ring, easing in and out each lap rather than at a constant
rate, never varying in colour or opacity. Pending's own attention mark (see Attention) is the
very same two arcs, at whatever ink its level carries instead of `ink-dim`: a machine's work is
the same "still going" as any other, so it wears the one mark rather than a shape of its own.
Working alone moves differently: a line round its own diamond, same ink as the fill it rides
(never a second colour arriving), tracing the edge it belongs to rather than turning a ring, at
a constant rate. Every one of these owes the surface 3:1 at every frame of its travel.
`motion-reduce` rides every one: it keeps the fill and drops the geometry.

### State

A control with nothing to act on wears the *waiting* face. It stays in the layout and in focus
order. Its border drops to the quiet rim, and its word stays legible. A bare verb has no
border, so its word drops to `ink-faint`, which keeps AA on every surface. A control whose act
is landing is `aria-busy` and wears the *held* face (the `pressed` variant) until the act
lands. Neither is `disabled`. The attribute `disabled` does not exist in this product.
*Test: search for `disabled`. It appears nowhere.*

An armed control is a destructive act one tap from done. It wears the `err` fill at rest
(principle 3).

### Attention

A status says what state a thing is in. Attention says how much it wants a person. A session
has several statuses and one attention: the level of its most urgent standing
(`Standing.worst`). The attention mark (`Icon.attention`) is the one point that ranks by
shape, because a list of sessions is read at a glance, in a blur, and colour alone may not
carry a rank (WCAG 1.4.1).

| Level | Shape | Ink | Motion | Means |
|---|---|---|---|---|
| Wrong | disc in a ring, the whole box | `err` | the ring draws round and the core springs in, once | nothing moves until a person fixes it: sign in again, a PR conflicted or failed |
| Wanted | disc | `green` | the arc closes and fills, then a halo breathes out three times | a person's turn: the agent is waiting |
| Working | solid diamond, a little larger than the other marks' radius | `agent` | a line runs once round its edge, 1.6s, for as long as the turn does | the agent at work: a turn is running |
| Pending | two arcs passing round a ring (the status spin, `Icon.spin`) | `ink-dim` | the arcs ease round, 1.6s a lap, for as long as the work does | a machine's work, needs nobody: a command the agent left running, checks, the queue, a launch |
| Absent | empty ring | `ink-dim` | none | no agent, chosen knowingly |
| Rest | point | `ink-faint` | none | nothing to say: stopped, or everything watched is fine |

Waiting outranks working. Work under way needs nobody, and the ball with a person does. But an
idle agent is only a person's turn while nothing the session watches is under way: with checks
still running it is waiting on them, so it says "waiting" at Rest and the checks lead
(`AgentStatus.withWatched`).
Wanted is green because green is the people, not because it is good.

Working outranks Pending: the agent thinking is nearer a person's turn than a machine
finishing. A command the agent left running is Pending, because no turn is thinking and the
agent wakes when it ends, as it does for checks. Working wears its own diamond — the agent's
shape, nowhere else in the five other levels — rather than Pending's spin, so a reader does not
need the ink to tell whose work is under way; the word beside the mark still says which.

Attention is ink: each level carries more than the one below it, so the order survives
greyscale and a blur. They share one 12px box, centre and the chrome's 1.5px stroke. The
mark is larger than a status's 6px dot because its shape carries the rank.

Motion goes only where a state is live or asks. Working and Pending keep going because the work does,
one tracing its edge and one spinning two arcs round a ring. Wanted asks a few times and then sits still, so a session left
waiting does not nag. Wrong never fidgets: a mark that kept moving would read as a spinner, not
a fault. What moves is geometry, never the ink the meaning rests on. `motion-reduce` keeps every
still face.

The mark never stands alone. A word beside it, or the name of the control it sits in, says
the state.

Where a session is listed or entered, its mark leads it. On the Manager's roster the mark sits
on the page's rail and the name starts 32px in, where a phone's session page starts its title beside
the chevron that wears the same mark. The roster's mark is a press: it unfolds the row to its
words and its verbs, so a row at rest is a mark and a name and nothing to press by mistake.
Unfolding, the mark is not drawn twice: it rides down to the first line, the thing it ranked first,
and leaves a track behind it that starts at a point on its seat, so a reader sees where it
came from and the line has no squared end. The track is a causal link's rail, one pixel of
`edge`: it joins two things the way the timeline's arrows do, and it must never weigh what the
mark does.
*Test: print the roster in greyscale and blur it. The six levels still read in order.*

## Hierarchy of controls

Four weights, in this order of scarcity:

| Weight | Looks like | Means | Count per surface |
|---|---|---|---|
| **Filled** | solid `blue` rectangle, text in `bg` | *the* verb of this surface | 0 or 1 |
| **Bordered** | transparent rectangle, `ring` | a standalone act on the ground | few |
| **Bare** | a word or an icon, no rectangle | a verb that rides the thing it acts on | as needed |
| **Text** | coloured caps that lead with a dot | a status, not pressable | as needed |

Count fills on a rendered surface. A value in `Style.fs` cannot know how many of its wearers
meet on one screen.

A bordered button *is* its border. Without it, it is a different kind of control, not a quieter
button. A setting is bare. When two controls fight, the one closer to the surface's verb keeps
its weight, and the other drops one.

There is one bordered face: `ring` in the `edge` tone, with `ink` text. Interaction brightens
the ring. There is no blue-ringed button. Blue text belongs to a link (`askLink`), to the fill,
and to the composer's Send, a bare blue word.

A word and an icon never stand as bare verbs on the same row. Prefer the word: it is its own
accessible name, and a reader learns no glyph. Where the words do not fit, make every bare verb
on that row an icon, because a list pays each word's width on every row. An icon verb carries
an accessible name.

While a flow is under way, its awaited step wears the fill. Every other verb on that surface
drops to bordered or bare. A verb that rides a listed row is bare and `ink-dim` at rest.
*Test: list every button face on a surface. Filled, bordered, bare: three at most, each drawn
one way.*

## Phone and wide

The shell's layout has one breakpoint, and it is a question: *is this screen a phone?* A short
screen is a phone however wide it is. The answers are the exact-complement variants `phone:`
and `wide:` in `app/tailwind.css`, whose comment gives the queries and the case that set them.

Use `phone:` and `wide:` for the shell's layout. Use `md:` and `max-md:` only for how wide a
reading column is, where width is the question. A `md:` beside a `phone:` is a bug: on a phone
turned sideways, both match.

Picture three anchors: desktop, phone upright, and phone sideways.

## Deciding

When two rules disagree, this is the order:

1. **The floor.** WCAG 2.0 AA contrast, keyboard operability, visible focus, the touch target.
   Never traded.
2. **Honesty.** The state must be visible at rest.
3. **One verb.** The hierarchy of controls above.
4. **Vocabulary.** Use the named value that exists. Do not make a second.
5. **Grid.** Position derives.
6. **Less.** Then, and only then, taste. Taste prefers removal.

When the code disagrees with this document, the document wins. File the disagreement at the
code: a comment at the site that names the rule it breaks, or a fix. The code is a draft of the
doctrine. It does not amend the doctrine by example.

Before you change a surface, read this document. After, picture all three anchors and look at
them. Looking answers *is it any good*. The doctrine answers *is it right*. Ask both, in that
order.

## Not governed

Copy. Which element marks an empty state. The layout of one surface, except a column's rail
([Space](#space)). Whether a thing is worth building. These are the design changing, which is
what a design is for.
