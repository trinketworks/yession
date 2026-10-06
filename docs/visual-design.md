# Yession — visual design

This document is the doctrine. It says what the product looks like and why. It gives every
rule a test, so a visual question has a derivation and not a taste.

[`app/tokens.css`](../app/tokens.css) defines, once, every value the rules name — a colour, a
step on the type ramp, a length. That file is short on purpose. Read it with this one.
`src/Yession.App/Style.fs` composes what each surface wears. The architecture is
[`technical-design.md`](technical-design.md).

## Stance

Yession is a working room. It holds one conversation, a queue, and the means to intervene
fast. The design is flat type on black. It takes Metro and Zune's reduction, and wears it on a
Slack or Cursor anatomy: a sidebar, a conversation, a pane.

The product follows the Bauhaus workshop's method. A small set of primary forms. A grid that
places them. A vocabulary that names each move once. Nothing carries decoration. Everything you
can see does a job, and its appearance says what the job is.

## Principles

Each principle has a test. Apply the test before you change a surface.

1. **Content is the interface.** Chrome frames content and recedes behind it.
   *Test: remove the element. If the content is no harder to read or to act on, the element goes.*

2. **Typography carries hierarchy.** Importance is said by size, weight and position on one
   grid. It is never said by a box, a fill, a colour or an ornament around the text.
   *Test: view the surface in greyscale with every border removed. The order of importance
   must still be legible.*

3. **One verb per surface.** Each surface has one primary act, and only that act wears the
   fill. Every other control wears a border, nothing, or text.
   *Test: count the filled rectangles on screen. The count is one or zero.*

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

7. **Remove before you add.** When two solutions satisfy the rules above, the one with fewer
   elements wins.
   *Test: name what the extra element earns. If you cannot, it is ornament.*

## Materials

### Type

Three faces, one rule: **sans is the machine, serif is the person, mono is the terminal.**
`font-ui` is the chrome and the agent's voice. `font-human` is what a person wrote.
`font-terminal` is the shell. The typeface says who is talking before a reader reaches the author line.

Every size is a step on the ramp (`text-label` … `text-wordmark`, and `text-code` for mono).
A step sets the size and the line box together, on the 4px rhythm. Nothing sits off the
ramp.

The caps voice — `text-label`, `tracking-caps`, semibold — is worn by labels, statuses,
buttons and authors. A verb with room steps up one size (`text-small`). It never steps up a
weight. Headings are lowercase.

A section heading inside a drawer is one step above the field label beneath it. So two caps
lines never read as two headings.

### Colour

The ground is `bg`, black. Three hues mean three things:

- `blue` is interactive, and it is the agent.
- `green` is live, and it is the people.
- `err` is wrong. There is at most one red word on screen.

Blue is never a status. A wait is `ink-dim` text. Reconnecting, catching up, queued, looking
and no agent yet are waits. A wait may lead with a dot. The dot beats if the wait is live. Live
is `green` text. A running command, a connected peer and a session that runs are live. A record
of something that finished is `ink`. Wrong is `err`. The one red word on a surface is the
newest wrong thing. An older wrong thing is a record and wears `ink-dim`.
*Test: count the red words. The count is one or zero.*

People are told apart by square display pics, never by a name colour. The blue→green
gradient exists exactly once, on the composer's focus edge. It marks the one place where the
person and the agent meet.

A status is text in its colour, with at most a dot. It is never a filled badge and never a boxed
one. Dimming is a role (`ink-dim`, `ink-faint`), never opacity: opacity breaks the contrast
floor and dims the focus ring with it.

### Form

Two primitives. The **rectangle** is always sharp-cornered. The **point** is always round.
A rounded rectangle is a hybrid of the two, and it does not exist in this product. Icons
follow the same geometry: square caps, no curves, no fills, and a stroke weight matched to
the type it sits beside.

The brand mark (`Brand.mark`) is the one lit object. It is a picture, not a surface. The
"Honest materials" rule does not govern it. Nothing else may look lit.

### Stroke

One vocabulary, `Style.Stroke`, composes every border. Two widths, and only two:

- `ring`: all four sides, for what you type in or press.
- `lead`: the leading edge, for a listed thing, where two states share one surface.

A stroke also has a tone at rest and a rule for what interaction does to it. Nobody writes a bare
`border-*` utility outside `Stroke`. **Nothing but a field or a button carries a
border.**

### Space

Positions derive from the grid, never from the eye. Every control is one height,
`spacing-control`, so a row is things of one height. On a phone, controls grow to the touch
target. Rows align on the text baseline. Padding is spent outward, so text sits on the
column's rail.

Every column has one rail at `spacing-rail` from its edge. On a phone it is
`spacing-rail-phone`. Every line of text in the column starts on it. A mark that leads a line hangs in the gutter to the left of the rail. A prompt, a
reply mark, a fold arrow and a dot are such marks. A mark never pushes the words in. The header
band is `spacing-band` tall. On a phone it is `spacing-band-phone`. Every head on a surface
shares the band. The hairline under it runs across every column at one height.
*Test: draw a vertical line on the rail. Every first letter touches it.*

Height is spent on content, not on chrome. A surface that is mostly empty box is spending the
conversation's room.

### Motion

One vocabulary. Things arrive by sliding a little and fading in, at `pace`, eased out. Press
is the Metro tilt: in fast, back with a hair of overshoot. Pressed is *held* — the `pressed`
variant covers `:active` and `aria-busy` — and the control stays filled until the act lands.
A wait is a beat (`animate-beat`), not a spinner. A dot that beats animates its colour between
a role and its dim, never its opacity. `motion-reduce` rides every one of them. It
keeps the fill and drops the geometry.

### State

A control with nothing to act on wears the *waiting* face. It stays in the layout and in focus
order. Its border drops to the quiet rim, and its word stays legible. A control whose act is
landing is `aria-busy`. It wears the *held* face, which is the `pressed` variant, until the act
lands. Neither is `disabled`. The attribute `disabled` does not exist in this product.
*Test: search for `disabled`. It appears nowhere.*

An armed
control — a destructive act one tap from done — wears the `err` fill at rest. Live is green
text. Wrong is the one red word.

## Hierarchy of controls

Four weights. A surface uses them in this order of scarcity:

| Weight | Looks like | Means | Count per surface |
|---|---|---|---|
| **Filled** | solid `blue` rectangle, text in `bg` | *the* verb of this surface | 0 or 1 |
| **Bordered** | transparent rectangle, `ring` | a standalone act on the ground | few |
| **Bare** | a word or an icon, no rectangle | a verb that rides the thing it acts on | as needed |
| **Text** | coloured caps, maybe a dot | a status, not pressable | as needed |

A bordered button *is* its border. Remove the border and you have a different kind of control,
not a quieter button. A setting is bare. A status is never pressable. When two controls fight,
the one closer to the surface's verb keeps its weight, and the other drops one.

There is one bordered face. It is `ring` in the `edge` tone, with `ink` text. Interaction
brightens the ring. There is no blue-ringed button. Blue text belongs to a link (`askLink`) and
to the fill. While a flow is under way, its awaited step wears the fill. Every other verb on
that surface drops to bordered or bare. Bare verbs speak in one voice, the caps label step. A
verb that rides a listed row is bare and `ink-dim` at rest.
*Test: list every button face on a surface. Filled, bordered, bare: three at most, each drawn
one way.*

## Phone and wide

There is one layout breakpoint, and it is a question: *is this screen a phone?* Width and
height both answer it. A short screen is a phone however wide it is. The two answers are the
variants `phone:` and `wide:`, and they are exact complements.

Use `phone:` and `wide:` for the shell's layout. Use `md:` and `max-md:` only for how wide a
reading column is, because there the width is the question. A `md:` beside a `phone:` is the
bug the pair exists to stop: on a phone turned sideways, both match.

Picture three anchors: desktop, phone upright, and phone sideways.

On a short screen, a card that takes a share of the column takes enough of it to hold its
field, one row and its verb. The share is a floor, not a fraction.

## Deciding

When two rules disagree, this is the order:

1. **The floor.** WCAG 2.0 AA contrast, keyboard operability, visible focus, the touch target.
   Never traded.
2. **Honesty.** The state must be visible at rest.
3. **One verb.** The hierarchy of controls above.
4. **Vocabulary.** Use the named value that exists. Do not make a second.
5. **Grid.** Position derives.
6. **Less.** Then, and only then, taste. Taste prefers removal.

When the code disagrees with this document, the document wins. File the disagreement where the
code is: a comment at the site that names the rule it breaks, or a fix. The code is a draft of
the doctrine. It does not amend the doctrine by example.

Before you change a surface, read this document. After you change it, picture all three
anchors and look at them. Looking answers *is it any good*. The doctrine answers *is it
right*. Ask both questions, in that order.

## Not governed

Copy. Which element marks an empty state. The layout of one surface. Whether a thing is
worth building. These are the design changing, which is what a design is for.
