# Yession — visual design

This document is the doctrine. It says what the product looks like and why. It gives every
rule a test, so a visual question has a derivation and not a taste.

[`app/tokens.css`](../app/tokens.css) defines, once, every value the rules name — a colour, a
step on the type ramp, a length. That file is short on purpose. Read it with this one.
`src/Yession.App/Style.fs` composes what each surface wears. The architecture is
[`technical-design.md`](technical-design.md).

## Stance

Yession is a working room. It holds one conversation, a queue, and the means to intervene
fast. Its anatomy is Slack's or Cursor's: a sidebar, a conversation, a pane. Its finish is
flat: sharp rectangles on black, and nothing that does no job.

## Principles

Each principle has a test. Apply the test before you change a surface.

1. **Content is the interface.** Chrome frames content: it shows where things belong and
   which one is selected, then gets out of the way.
   *Test: remove the element. If the content, its groups and the selection are no harder to
   see, the element goes.*

2. **Chrome shows structure. Type shows importance.** Grouping, nesting and selection are
   said by a surface step, a fill or a rule. Within a group, size, weight and position say
   what matters. Words do not do a surface's job.
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
`font-terminal` is the shell. The typeface says who is talking before a reader reaches the
author line.

Every size is a step on the ramp (`text-label` … `text-wordmark`, and `text-code` for mono).
A step sets the size and the line box together, on the rhythm that `app/tokens.css` names for
the ramp. Nothing sits off the ramp.

The caps voice — `text-label`, `tracking-caps`, semibold — is for short labels, statuses and
buttons. A stack of caps lines is a wall of text. Where it would form one, let a surface say
the grouping instead.

### Colour

The ground is `bg`, black. It steps up in greys — `panel`, `surface`, `surface-2` — and each
step is one level of nesting or a selection. Three hues mean three things:

- `blue` is interactive, and it is the agent.
- `green` is live, and it is the people.
- `err` is wrong. There is at most one red word on a surface.

Blue is never a status. A wait is `ink-dim` text. Reconnecting, catching up, queued, looking
and no agent yet are waits. A wait leads with a dot. The dot beats if the wait is live.

Live is `green` text. A running command, a connected peer and a session that runs are live. A
record of something that finished is `ink`.

Wrong is `err`. The one red word on a surface is the newest wrong thing. An older wrong thing
is a record and wears `ink-dim`. A repeated row is a surface of its own. So is a repeated item
in the conversation. Twenty rows that failed are twenty surfaces, and each one keeps its red
word.
*Test: count the red words on a surface. The count is one or zero. Count a repeated row by
itself.*

People are told apart by square display pics, never by a name colour.

The blue→green gradient exists exactly once, on the composer's focus edge. It marks the one
place where the person and the agent meet.

A status is text in its colour, and it leads with a dot. It is never a filled badge and never
a boxed one. A verb never leads with a mark. So the dot, and not a tone, says that a word
cannot be pressed. A tone is one step of grey that a blur or a greyscale print hides, and
principle 2 blurs the screen.
*Test: a caps word with a dot is a status. A caps word without one can be pressed.*

Dimming is a role (`ink-dim`, `ink-faint`), never opacity. Opacity breaks the contrast floor
and dims the focus ring with it.

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
`border-*` utility outside `Stroke`.

### Space

Positions derive from the grid, never from the eye. Every control with an edge is one height,
`spacing-control`, so a row is things of one height. A filled control, a bordered control and
a field have an edge. A bare verb has no edge. It takes the height of its row. On a phone, a
control with an edge grows to the touch target. A bare verb grows only its hit area to that
target. Rows align on the text baseline. Padding is spent outward, so text sits on the
column's rail.

Each column has one rail. Every line of text in the column starts on it. The rail belongs to
the column, and no screen width moves it. A column also keeps an inset from the screen edge,
and a phone can make that inset smaller. The inset is not the rail.

Columns do not share one rail. `spacing-rail` is the conversation column's rail, because the
conversation is the reading column this rule is for. A narrower column has a narrower rail,
because two rails of 32 pixels spend 64 of a phone's 390 pixels. Such a column names its own
value in `app/tokens.css`. Nobody writes a rail as a number at the site.

A mark that leads a line hangs in the gutter to the left of the rail. A prompt, a reply mark, a
fold arrow and a dot are such marks. A mark never pushes the words in.
*Test: draw a vertical line on one column's rail. Every first letter in that column touches the
line. Then make the screen a phone. The line keeps its distance from the column's inset.*

The header band is `spacing-band` tall. On a phone it is `spacing-band-phone`. Every head on a
surface shares the band. The hairline under it runs across every column at one height.

### Motion

One vocabulary. Things arrive by sliding a little and fading in, at `pace`, eased out. Press
is a tilt: in fast, back with a hair of overshoot. A pressed control is *held* (see State).
The `pressed` variant covers `:active` and `aria-busy`.

A wait is a beat (`animate-beat`), not a spinner. A dot that beats animates its colour between
a role and its dim, never its opacity. `motion-reduce` rides every one of them. It keeps the
fill and drops the geometry.

### State

A control with nothing to act on wears the *waiting* face. It stays in the layout and in focus
order. Its border drops to the quiet rim, and its word stays legible. A bare verb has no
border. Its word drops to `ink-faint`, which keeps AA on every surface. A control whose act is
landing is `aria-busy`. It wears the *held* face, which is the `pressed` variant, until the act
lands. Neither is `disabled`. The attribute `disabled` does not exist in this product.
*Test: search for `disabled`. It appears nowhere.*

An armed control is a destructive act that is one tap from done. It wears the `err` fill at
rest. The `err` fill is not the surface's verb, so principle 3 does not count it.

## Hierarchy of controls

Four weights. A surface uses them in this order of scarcity:

| Weight | Looks like | Means | Count per surface |
|---|---|---|---|
| **Filled** | solid `blue` rectangle, text in `bg` | *the* verb of this surface | 0 or 1 |
| **Bordered** | transparent rectangle, `ring` | a standalone act on the ground | few |
| **Bare** | a word or an icon, no rectangle | a verb that rides the thing it acts on | as needed |
| **Text** | coloured caps that lead with a dot | a status, not pressable | as needed |

Count the filled rectangles on a rendered surface, because a value in `Style.fs` cannot know
how many of its wearers meet on one screen.

A bordered button *is* its border. Remove the border and you have a different kind of control,
not a quieter button. A setting is bare. A status is never pressable. When two controls fight,
the one closer to the surface's verb keeps its weight, and the other drops one.

There is one bordered face. It is `ring` in the `edge` tone, with `ink` text. Interaction
brightens the ring. There is no blue-ringed button. Blue text belongs to a link (`askLink`), to
the fill, and to the composer's Send, which says its verb as a bare blue word.

A word and an icon never stand as bare verbs on the same row. Prefer the word, because a word
is its own accessible name and a reader learns no glyph. Where the words do not fit the row,
make every bare verb on that row an icon, because a list pays each word's width on every row.
An icon verb carries an accessible name.

While a flow is under way, its awaited step wears the fill. Every other verb on that surface
drops to bordered or bare. A verb that rides a listed row is bare and `ink-dim` at rest.
*Test: list every button face on a surface. Filled, bordered, bare: three at most, each drawn
one way.*

## Phone and wide

The shell's layout has one breakpoint, and it is a question: *is this screen a phone?* Width
and height both answer it. A short screen is a phone however wide it is. The two answers are
the variants `phone:` and `wide:`, and they are exact complements.

Use `phone:` and `wide:` for the shell's layout. Use `md:` and `max-md:` only for how wide a
reading column is, because there the width is the question. A reading column's width is not the
shell's layout, so the `md:` pair is not a second layout breakpoint. A `md:` beside a `phone:`
is the bug the pair exists to stop: on a phone turned sideways, both match.

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

When the code disagrees with this document, the document wins. File the disagreement where the
code is: a comment at the site that names the rule it breaks, or a fix. The code is a draft of
the doctrine. It does not amend the doctrine by example.

Before you change a surface, read this document. After you change it, picture all three
anchors and look at them. Looking answers *is it any good*. The doctrine answers *is it
right*. Ask both questions, in that order.

## Not governed

Copy. Which element marks an empty state. The layout of one surface. Whether a thing is
worth building. These are the design changing, which is what a design is for.
