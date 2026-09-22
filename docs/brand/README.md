# Marks

Sixteen logo directions on one brief: a Y with three points, two green for the humans and one
blue for the agent. Nothing here is chosen yet — this is the broad pass, kept so the rejected
directions stay legible next to whichever one wins.

Every mark is drawn in tokens `app/tailwind.css` already defines — `#a8dd00` (green, human),
`#1ba1e2` (blue, agent) — on the product's black, at a 64px grid with a 5px safe margin. The
lockups are the same mark against `yession` set in Noto Sans 200, the weight the shell writes
its wordmark in.

| File | Direction |
| --- | --- |
| `trifoil.svg` | Three peers 120° apart; the hub is a gap, not a joint. |
| `letter.svg` | A true Y at letterform proportions — humans as one V, agent as the stem. |
| `stencil.svg` | One solid Y cut into three by hairlines. |
| `lowercase.svg` | The word's own lowercase y; the agent is the descender. |
| `blades.svg` | Tapered arms — weight and direction instead of a monoline. |
| `nodes.svg` | The three points drawn as points; strokes are only the relation. |
| `inward.svg` | Three arrowheads converging on a place none has reached. |
| `blend.svg` | Arms overlap rather than abut; the hub screens to near-white. |
| `beam.svg` | Two green strokes in, one gradient stroke out, with bloom. |
| `bloom.svg` | One continuous gradient stroke, heavy glow — the Zune end. |
| `tile.svg` | Metro's crop: the mark set far larger than its frame. |
| `room.svg` | The session as a ring the three arms reach. |
| `counterform.svg` | Three fields in a disc; the Y exists only as the cut between them. |
| `index.svg` | The square app mark, for the 16px end. |
| `lockup.svg` | Mark beside the word. |
| `substitute.svg` | Mark as the word's y. |

What the pass established: two humans read as two only when the arms stay separate (`letter`
and `bloom` join them and lose the count); green outweighs blue at equal area, so the agent's
stroke wants slightly more of it; only `letter`, `stencil`, `lowercase` and `index` survive
16px; and green on a light ground is 1.9:1, so any mark leaving the black app needs a plate
under it or a dark variant.

## Second pass: the cube and its projection

`cube/` narrows to the one direction worth pursuing. An isometric cube shows three faces
meeting at one interior vertex, and the three edges radiating from that vertex sit at exactly
120° — so the cube and the counterform disc are the same drawing with different silhouettes,
and neither draws the Y: it is the cut left between the fields.

| File | Variation |
| --- | --- |
| `cube.svg` | Baseline: blue top face, two green walls, 3.6px cut. |
| `cube-swap.svg` | Agent moved off the top plane onto a wall. |
| `cube-shaded.svg` | Two greens at two values — a lit solid instead of a flat one. |
| `cube-seam.svg` | Interior cut stops short of the silhouette; the outline stays whole. |
| `cube-round.svg` | Every corner taken off the same amount. |
| `cube-open.svg` | Faces pushed apart along their own normals. |
| `cube-inset.svg` | Faces inset on all edges — three panels holding a cube's shape. |
| `cube-wire.svg` | Hairline silhouette, colour spent only on the Y. |
| `cube-hollow.svg` | The agent's face drawn, not filled. |
| `cube-plate.svg` | App-icon build on its own dark ground. |
| `disc.svg` | The projection: same fields, circular silhouette. |
| `disc-hair.svg` / `disc-wide.svg` | The cut at 1.6px and at 7px. |
| `disc-weighted.svg` | Agent's field widened to 135° to balance green's luminance. |
| `square.svg` | Tile field; the Y travels three different distances. |
| `ring.svg` | Disc hollowed — one band divided three ways. |
| `partial.svg` | Cuts stop short of the rim; the fields stay one disc at the edge. |
| `lockup-cube.svg` | The solid against `yession` in Noto Sans 200. |

What this pass established: the agent belongs on the **top** face — with blue on a wall the two
greens become one L-shaped mass and the count goes, which is the failure `letter` and `bloom`
had; the hexagon holds at 16px where the circle becomes a dot; 3.6px on the 64px grid is where
the cut is both legible and quiet; and unlike the first pass's stroke marks, a field mark
survives a light ground, because the cut takes whatever is behind it.

## Third pass: the seam, expressed

`form/` keeps the seam — three faces cut apart where they meet and continuous at the outline,
so the Y is a gap the silhouette never opens — and treats it as the only fixed thing. Each face
is a quad with two kinds of edge: the two running from the hub are the seam and stay straight in
every study, because they are what the reader resolves the Y from; the two outer edges are the
silhouette, and that is where the expression is spent.

The levers are edge bow (perpendicular displacement of an edge's midpoint, positive outward),
per-corner radius, cut taper, and whether the faces sit in one plane at all.

| File | Register |
| --- | --- |
| `swell.svg` | generous — silhouette bowed out, seam straight |
| `petal.svg` | alive — bow pushed until each face is a leaf |
| `pebble.svg` | worn — no curves, corners taken off at increasing radii |
| `lean.svg` | leaning in — human faces widened at the shoulder |
| `draw.svg` | held — the bow inverted, mass pulled toward the hub |
| `crest.svg` | load-bearing — convex top, concave walls |
| `converge.svg` | closing in — the cut wide at the hub, closing at the rim |
| `turn.svg` | working — faces rotated, leading and trailing edges |
| `spiral.svg` | in motion — faces still, the cut bends |
| `forward.svg` | momentum — the whole solid sheared |
| `stack.svg` | layered — each face pushed a different distance out |
| `lift.svg` | arriving — the agent's plane floated off the solid |
| `breathe.svg` | opening — the cut hairline at the hub, wide at the rim |
| `hold.svg` | protective — the agent's face shrunk inside the human ones |
| `chamfer.svg` | machined — flats instead of radii |
| `bevel.svg` | engineered — a hairline run inside every face |
| `fold.svg` | made by hand — every plane creased, two values each |
| `still.svg` | composed — the control: no bow, minimal radius, hairline seam |

What this pass established: curvature is the loudest lever by a distance — four degrees of bow
moves the mark from a rendering of a solid to something with a temperament. Keeping the seam
straight is what keeps the set siblings. `stack`, `lift` and `breathe` still say
three-parties-in-one-thing at 16px, where `petal` and `draw` collapse into a blob and a star.
And `hold` changes the argument rather than the mood: a shrunken agent face says authority is
scoped and the small thing is held, which is a claim, not a style.

## Fourth pass: the folded Y

`fold/` takes the crease from `form/fold.svg` and treats it as the construction. Creasing each
face gives six triangles, so the hexagon's interior has six edges: three are the Y (60°, 180°,
300°) and three are folds (0°, 120°, 240°). The Y is cut; the folds exist only as a step in
value. Every study is a way of spending or saving that difference.

**The Y as a letter.** The cut now stops inside the field — run it to the silhouette and a
terminal has nowhere to exist, which is why the first attempt's variations were all the same
drawing. `modul` (pen weight, thick at the junction), `sheared` (terminals cut on an angle),
`flared` (glyphic, widening in the last tenth), `joint` (a fillet where the strokes pool),
`penned` (arms bowed as a written Y's are), `stemmed` (short arms, long stem — a Y's actual
proportion), `offaxis` (arms at 44° instead of the isometric 60°).

**Quieter.** `whisper` (hairline), `tone` (no cut at all — the fold values arranged so the step
across the Y beats the step across a crease), `emboss` (hairline with a light edge under it),
`step` (half the hexagon 1.6px smaller, so the Y is a misalignment).

**The triangles.** `gem` (every corner radiused), `hub` (radius only at the centre, so the
junction softens and the rim stays sharp), `alternate` (colour by triangle, not by face),
`pinwheel` (the lit half rotated one step, so shading circles instead of describing a solid),
`deep` (fold contrast doubled on one face, halved on another).

**Light and depth.** `lit` (one light source resolved per facet, six gradients), `sweep` (each
face bleeding toward its neighbour's hue along the seam), `extrude` (a dark copy behind — the
only build here that still works in one colour).

Findings: `stemmed` and `offaxis` are the two that read as a letter rather than a construction,
at the price of the cube being geometrically true. `tone` is the quietest and the only mark with
no holes in it. Six flats survive 16px and six gradients do not — `lit` and `sweep` want a
second, flatter build rather than a compromise. And radius at the hub buys more than radius at
the rim, because the junction is the part a reader looks at.

## Fifth pass: Noto's own Y against the solid

`glyph/` stops drawing a Y and uses the one the shell already serves. The outline is read
straight out of `app/fonts/noto-sans-latin-<weight>-normal.woff2` with fontTools, junction
placed on the solid's hub.

The measurement that drives the whole pass: **Noto Sans cuts its capital Y with the arms 33°
off vertical and the junction at 39% of the cap height** (junction at 283,277 on a 1000 em, cap
at 714). An isometric cube puts its three interior edges at 60° and the junction dead centre.
27° apart, and every mark here is a position on that argument.

The solid is no longer fixed either. Any three vectors out of a hub are a legitimate parallel
projection of a cube corner, so the *view* is a free variable — which is what lets the geometry
move toward the letter instead of the letter being bent to fit 120°.

- **The ladder** — `arm60`, `arm52`, `arm45`, `arm38`, `arm33`: one variable, the arm angle,
  from the isometric down to Noto's own, with the solid re-derived at each step.
- **The shipped glyph** — `iso400`, `iso600`, `iso200`: three weights of the real letter as the
  cut, over the true isometric solid, so the cut crosses the folds.
- **Moving the view** — `wide66`, `wide72` (flattened: the top face widens until the letter has
  room), `rot7`, `rot14` (solid turned under an upright letter), `dimetric` (arms at 54° and 68°,
  so the cube is turned toward the viewer while the letter stays symmetrical).
- **Manipulating the letter** — `stretch17` (glyph at 170% width: arms walk out to ~50°),
  `stretch27` (at 267%: arms land exactly on the solid's 60° edges).
- **Finish** — `inlay` (the letter as a lightened region, nothing cut away), `hub` (Noto 300
  over a flattened solid with radiused hub corners), `overshoot` (letter larger than the solid,
  cutting out through the silhouette).
- **Retained** — `retained-whisper`, `retained-modulated`, carried unchanged from `fold/`.

Findings: 45° is the floor for the solid — below it the hexagon becomes a leaf, so pulling the
geometry all the way to the letter costs the thing the letter was sitting on. Flattening the
view to 66° is the move that works, because nothing has to be falsified: you are simply standing
higher up. Stretching the glyph is cheaper than bending the cube — at 170% it is still
recognisably Noto's Y. Seven degrees of rotation reads as intent and fourteen reads as an
accident. And semibold is the weight that survives 16px: a counterform has to read as a gap,
not a scratch.

## Sixth pass: the held cube

`pair/` follows `glyph/overshoot.svg` — where the letter is set larger than the solid and cuts
out through the silhouette — and puts something in the fork it opens. The mark becomes two
objects and one relationship: a green solid with Noto's Y cut through it, and a blue solid it
has hold of.

Built in three steps: the folded solid, the letter cut through it overshooting the silhouette,
then the second solid dropped in with its own clearance punched out of the first — so the gap
around it is the big cube's material removed, not a stroke drawn on top.

- **Held** — `cradle`, `tight` (0.9px clearance), `perch` (larger, breaking the silhouette),
  `float` (2.6px, nothing touching), `sunk` (behind the solid, revealed only by the cut).
- **Gripped** — `prongs` (narrow cut, the wedge between the arms survives and the small cube
  sits on it), `grip` (cube at the junction, arms rising from behind it), `bracket`, `claw`,
  `deepfork` (arms reaching past it on both sides), `core` (at the junction, behind).
- **Ratio, colour, orientation** — `small30`, `small60`, `twin` (green: a session inside a
  session), `socket` (unfilled: the space kept for one), `turned` (18° off the large one's
  axis), `mixed` (the large solid keeping its blue top — two things claiming to be the agent).
- **Without the cut** — `stack`: the same two solids corner to corner, no Y anywhere.
- `lockup.svg` — the pair against the name, the small solid sitting where an ascender would.

Findings: `prongs` is the one that keeps both readings, because a narrow cut leaves the large
solid whole. Past about 40px of reach the cut severs it, and `bracket`/`claw`/`deepfork` stop
being one object holding another and become two green pieces flanking a blue one — which is the
original brief (two humans either side of an agent) arrived at from the opposite direction, and
worth keeping as its own line. Clearance does more work than size: `tight` and `float` are the
same solids in the same places, and one is holding while the other is merely near. And the
large solid has to give up blue entirely, or the pair has two agents in it.

## Seventh pass: cube minus cube

`pair/` drew the second cube as a literal object perched in the fork. `void/` makes it the piece
that was taken away.

Remove a cube-shaped corner from the cube facing you. In an isometric projection the three edge
vectors sum to zero, so the void's inner corner lands exactly on the outer one and its three
faces project as a small hexagon — the silhouette of a small cube. Nothing is drawn twice: the
second solid is the absence, and the three edges running out of it are already the Y. The
construction is the same at both scales, with the small cube's interior Y rotated 180° from the
large one's.

- **The void** — `bite` (34% of the edge), `bite-small` (22%), `bite-deep` (50%, the solid
  reduced to three L-plates), `folded` (each plate creased, six greens and three blues),
  `hollow` (the void punched through to the ground), `open` (hole and seams together).
- **Which way it reads** — `flip` (the void's values in the outer order, so it reads convex: a
  small cube in front rather than a hole), `rimmed` (clearance around the opening so the two
  never touch), `suggested` (the void in the solid's own colours, a shade off), `ghost` (blue
  mixed back toward green until it is a tint).
- **With the letter** — `seamed` (the three edges the bite leaves, opened into a cut — both Y's
  visible at once and nothing added), `glyphed` (Noto's Y arriving at the void).
- **Elsewhere** — `corner`: the same subtraction at the top vertex, where it notches the
  silhouette.
- `lockup.svg` — seamed against the name.

Findings: blue belongs to the void. The solid is green throughout and the only blue in the mark
is the part that was removed, which is a more precise claim about a scoped agent than a small
cube perched on top. The concave/convex reading is genuinely ambiguous — `bite` and `flip` are
the same polygons and differ only in the order of three values, so some readers will see the
other one; `rimmed` settles it. And it holds at 16px better than `pair/` did: one silhouette
instead of two, with the void a solid shape rather than a gap between things.

## Eighth pass: the cube in the fork

`pair/` perched the second cube above the mark; `void/` made it the piece removed. `fork/` puts
it where it was asked for: seated in the fork of the cut Y.

It needs no fitting. The Y cut out of an isometric solid leaves a fork of exactly 120°, and 120°
is what a hexagon's bottom corner measures — so the held cube's two lower edges lie along the
arms' own inner edges. Both the fork and the cube are the same construction; they were always
going to meet.

- **In the fork** — `fit` (seated, filled), `clear` (1.3px of ground opened around it), `fit-hole`
  (the opening left empty and still cube-shaped), `fit-tint` (blue mixed back toward green),
  `fit-edges` (three strokes in the hole — the cube's own interior Y and nothing else),
  `concave` (values reversed, so it reads as a socket).
- **How big, how high** — `snug` (30%), `flush` (46%: the held cube's top vertex lands exactly on
  the large one's), `filling` (52%), `raised` (lifted off the junction), `brim` (arms running past
  its shoulders and out through the silhouette), `short` (arms stopping at its shoulders, so the
  cut is one shape — a Y with a cube for a head).
- **Finish** — `folded` (the large solid creased, six greens), `tapered` (the cut given a pen's
  taper rather than parallel sides).
- `lockup.svg` — against the name.

Findings: `flush` is the only ratio that is not a preference — at 46% the two silhouettes share a
corner and the top face fills edge to edge. Clearance is what decides whether the mark reads as
one object or two: `fit` and `clear` are the same geometry 1.3px apart. `fit-edges` is the most
suggested build that still reads — nine lines, two cubes, nothing drawn that the cut did not
already imply. And unlike `pair/`, it holds at 16px, because the held cube is a solid shape with
its own colour rather than a gap between things.

## Ninth pass: the narrow fork

`fork/` used the isometric Y, whose fork is 120° — so the held cube seated in its corner.
`narrow/` uses the letter's fork instead. Noto's capital Y opens at about 33° off vertical, and a
hexagon's bottom corner is 120°, so it cannot descend into a V that tight: it is caught on the
two inner edges and wedged high, held rather than seated.

The seat is solved rather than nudged. Contact is linear in the cube's height, so one Newton step
from the support function lands it exactly tangent to both inner edges — `seat()` in the
generator. For the shipped glyph the inner V's apex and edge direction come from Noto's own
outline (the inner apex is at (283, 363) in font units, the left inner terminal at (98, 714)).

- **How narrow** — `a24`, `a30`, `a33`, `a42`: the fork angle as the single variable. The
  narrower it is the higher the cube rests; by 42° it has started to descend toward the corner.
- **The shipped letter** — `noto400`, `noto600` (heavier arms raise the inner V and lift the cube
  with it), `noto200` (barely an arm to hold anything), `wide13`, `stretched` (155%, which opens
  the fork enough to take a larger cube and still keep it inside the silhouette).
- **How big it sits** — `flush` (the held cube's centre lands 1.83 edge lengths above the apex, so
  its top vertex is 2.83 above: set the edge to 30% of the big solid's and the two silhouettes
  share a top corner exactly), `contained` (wholly inside the outline — the favicon build),
  `deep`, `proud`, `clear` (1.2px of ground around it).
- **Finish** — `hole`, `tint`, `edges` (the held cube's own interior Y, inside the big one's).
- `lockup.svg` — against the name it came from.

Findings: weight moves the seat, so a cut that is going to hold something wants weight — 600
holds, 200 does not. `flush` is decided by the construction rather than by taste. `contained` is
the one for 16px, because anything larger breaks the outline and a broken outline at that size is
two specks rather than one mark. And `stretched` is the compromise worth testing: a wider letter
buys a bigger held cube without it leaving the silhouette.

## Tenth pass: eleven dice

The shortlist is `fold/whisper.svg` and `fold/modul.svg`, and they differ in exactly one property
— so the property list is the instrument. `dice/` breaks the mark into eleven dimensions, every
one of them something those two already decide silently, and throws all of them independently.

| Dimension | Levels |
| --- | --- |
| silhouette | hexagon, rounded hexagon, circle, chamfered, squircle |
| arm angle | 60° (isometric), 45°, 33° (letterform), Noto glyph |
| cut weight | none, hairline 1.2, light 2.4, medium 3.8, heavy 5.4 |
| cut profile | parallel, tapered, reverse taper, flared, round-ended |
| cut reach | short, to the rim, overshoot |
| corners | sharp, soft 1.6, round 3.2, hub only |
| fold | flat, subtle, normal, strong, inverted |
| colour | blue top, blue right, blue left, split, green + blue cut |
| surface | flat, per-face gradient, sweep, glow |
| extra | none, bevel, rim, shadow |
| rotation | 0, −7, +7, −14 |

Seed 7, eighteen throws, no curation — `rolls.json` records what each one drew, so any result can
be reproduced, half-kept, or bred with another. Re-roll by changing `SEED` in the generator.

What the dice turned up that nine deliberate passes had not: **a blue Y drawn INTO the cut on an
all-green solid** (rolls 01, 04, 05, 08, 14). Every previous mark made the letter an absence;
none of them made it an object. Roll 14 is the striking one. The **squircle** is also better than
expected (06, 09, 12, 14) — the only silhouette that reads as a made object rather than a diagram
of a solid. Rotation is worth more under a glyph cut than a symmetric one: roll 11 at −14° looks
placed, roll 03 at the same angle looks broken. Roll 13 draws "no cut" with every other dimension
working and says nothing at all, which settles whether the letter is decoration. And glow and
heavy round-ended cuts are the two levels to retire — every roll carrying them is muddier for it
and worst at 16px.

## Eleventh pass: seven ideas from a tile

The dice turned up the squircle, so `tile/` chases what that silhouette unlocks rather than
re-permuting the cube. A hexagon is a drawing of a solid; a squircle is an object — a key, a
window, a lens, a card — and each of those is a different family. Drawn as real superellipses
(n = 4, sampled), not rounded rectangles.

- **Panes** — `panes`, `panes-inset`, `panes-asym`, `panes-chrome`: the tile as a window, the Y as
  the gutter between three panes. The only family here that draws what the product IS rather than
  what its initial looks like.
- **Aperture** — `aperture`, `aperture-open`: three blades swung in from the rim like a lens iris.
  The opening is the junction, the seams are the arms, and the mark has a state.
- **Object** — `keycap` (base, dished top, letter cut into it), `keycap-pressed`, `keycap-lit`
  (the Y filled in blue: a lit key), `emboss` (the letter pressed into one green surface, no cut
  and no second colour), `chip`.
- **Overlap** — `overlap`, `overlap-tight`, `overlap-loose`: three tiles cleared of each other, the
  Y in the gap. Kept as a negative result — at every arrangement tried it reads as a face.
- **Bars** — `bars`, `bars-gap`, `bars-hub`, `bars-tile`: the letter built out of three
  squircle-ended bars rather than cut into a shape.
- **Contained** — `contained`, `bleed`: the folded solid given the tile as a ground, at icon
  padding and cropped.
- **Stack** — `stack`: the tile repeated, newest in front.

Worth taking further: `panes` (the Y falls out of a shared window split three ways — the first
mark that is about the product rather than the letter), `aperture` (the best drawing here, and the
only one with an honest animation in it), `keycap-lit` (answers the dice's discovery — the Y as a
lit object rather than an absence), and `bars` (three separate objects meeting, so the two humans
are finally plainly two). `overlap` and `chip` are recorded as failures: a face and a costume.

## Twelfth pass: lighting whisper

`neon/` returns to `fold/whisper.svg` and lights its cut. A lit tube is not one blur: the core is
nearly white and thinner than expected, the falloff comes in stages rather than one, the hue
shifts deeper as it spreads, and — the part usually missed — it lights whatever stands next to
it. Each family below isolates one of those mechanisms; the last puts them back together.

- **Painted light** (no filters at all) — `spill`, `spill-deep`, `spill-cold`. Every facet of this
  mark is bounded by exactly one slot and one crease, so light from the cut is a linear gradient
  running between them. Resolution-independent, free to render, and works in any pipeline that
  can draw a gradient.
- **The tube** — `tube` (three stages: near-white core, tight halo at the hue, wide dim wash),
  `chroma` (outermost stage pushed to a deeper, more saturated blue), `faint`.
- **A lighting model** — `pointlit` (feDiffuseLighting with a point source in the junction),
  `twolight` (green source up the arms, blue down the stem), `specular` (feSpecularLighting, so
  the facets read as having a finish).
- **The room** — `ambient` (wash under the whole mark), `halo` (wash outside the silhouette only),
  `grain` (feTurbulence in the bloom, because a perfectly smooth falloff bands on a real screen
  and reads as a gradient rather than as light).
- **State** — `unlit` (a recessed grey slot, no wash), `warm` (lit green), `pulse` (gradient along
  the run, as an unevenly excited tube does).
- **Assembled** — `assembled` (all of it, each layer weak), `dual` (arms green, stem blue, each
  facet washed by whichever lights it), `slot` (the cut given thickness: dark inner edge, bright
  lip), `edge` (no bloom at all — two bright hairlines on the lips plus painted spill).

Findings: `spill` costs nothing and is the mechanism to build on. `dual` is the only lighting that
keeps the mark's argument — lighting everything one colour throws away the two-humans-and-an-agent
reading. `edge` is the one to trust: it reads as lit, survives print, and is the only assembled
build still legible at 16px. Note that every tube, lighting and room build depends on SVG filters,
which are slow at scale, render differently across engines and are dropped by several icon and
email pipelines. And none of the glow builds survive 16px — which is the right answer: lit at the
sizes where light is visible, flat where it is not.

Two filter gotchas found the hard way, both the same bug: an `objectBoundingBox` region is
degenerate for a zero-width shape. A vertical line takes neither a default `linearGradient` nor a
default filter region — both need `userSpaceOnUse`.

## Thirteenth pass: blue in the fork

`bluefork/` returns to the narrow letter's fork (`narrow/`) and removes the green from it. Instead
of a cube resting in a green V, the V itself is blue — a cube corner that does not have to be
geometrically true, pointing out of the letter at you.

- **The fork, filled** — `two` (the V split down the axis into two values), `three` (a point on the
  axis and three facets meeting at it: a cube corner drawn without the geometry closing),
  `three-low`, `three-high` (the corner at 40% and 74% of the way up).
- **The letter's own counter** — `noto`, `noto-bold`, `noto-wide`: Noto's capital Y as the cut with
  its actual inner V filled, apex and arm angle read off the glyph outline. `noto-seam` leaves the
  glyph's arms uncut, so the counter is the only blue and the stem the only slot.
- **A cube with its edges on the arms** — `emerge` (a sheared cube whose lower edges lie along the
  arms, side vertices at the rim, top rising clear of the solid), `emerge-short` (arms cut only
  to its side vertices), `emerge-both`, `flush` (sized so its top vertex lands on the big solid's).
- **Seams** — `seam` (no slots for the arms: the upper strokes are where blue meets green, the stem
  a hairline), `seam-two`, `seam-none` (nothing cut at all — the stem is the big solid's own fold),
  `seam-lit` (the top face washed from the blue outward, painted).

Findings: `seam` is the one — a green solid with a blue corner driven into its top, the Y simply
the outline of the blue, and the first build in fourteen passes where nothing is cut away to make
the letter. `seam-none` works, which was the surprise: with the arms as seams the stem is already
the boundary between the two green walls. The letter's own counter is a gift — fill Noto's inner V
and the blue is exactly the kite the type designer left there, and it holds at 16px better than
any drawn wedge. `emerge` is the loud one: a crystal coming out of the fork, a splash screen
rather than a favicon.

## Fourteenth pass: seam lit, and the sketch as an object

Two threads.

`seamlit/` takes `bluefork/seam-two.svg` — the blue wedge driven into the green solid, one
crease up its middle, arms as seams, stem a hairline — and lights it with the mechanisms from
`neon/`. The only slot in the mark is the stem, so that is where a tube can go; the seams and
the crease are edges, and take light the way edges do. `parent` (unlit), painted (`spill`,
`spill-deep`, `source` — the wedge lit from its own crease), tube (`stem-tube`, `crease-tube`,
`both-tubes` — one bright axis from the top corner to the bottom vertex), assembled (`assembled`,
`dual`, `edge`, `unlit`). `crease-tube` is the find: the wedge's near edge lit and the stem
dark, so the cube in the fork finally looks like a cube because its nearest edge catches light.
`edge` still wins on cost.

`blocks/` reads a pen sketch as a thing rather than a drawing: four tall blocks in a two-by-two,
the near one missing, hairline kerfs between the three that remain. Rendered through an actual
orthographic camera in the generator — azimuth, elevation, back-face culling, painter's sort —
so every view is the same object and the Y is what it looks like from there: two kerfs on top
and the concave corner the far block shows through. `sketch` (as drawn: h = 1.8× footprint,
θ = 45°, φ = 35.26°), `cubes`, `tall`, `nogap` (the arms vanish into the top surface and only the
concave stem survives as shading), `wide`; `blue-right`, `blue-left`, `stepped`, `low-far`;
`above`, `low`, `turned` (θ = 32°), `turned-more` (θ = 20°); `ghost` (the missing near block's
top drawn as a faint plate — three stand around a space shaped exactly like one of them) and
`plinth`.

Height is the dial that matters for the blocks: as cubes it is a stair, at 1.8 it is three things
standing, at 2.6 it is pillars and the letter shrinks to a notch. The sketch's proportion is
about right. And a camera is the honest instrument here — `turned` is the same object as
`sketch`, which no drawn mark can claim.

## Fifteenth pass: the chevron

`chevron/` takes `blocks/` from above and thins the green blocks to half width. Same object,
same camera — the generator imports `blocks`' camera and renderer — with two new dials: the
slabs' width and which half of their quadrant they keep. From above the greens become two arms
hanging off the blue cube and the whole thing is an upward chevron with the agent at its apex.

- **From above, as cubes** — `cubes-48`, `cubes-62`, `cubes-plan` (straight down: three squares,
  two hairlines, the third arm of the Y the corner they leave open), `tall-55`.
- **Half width** — `chevron` (48°), `chevron-62`, `chevron-plan` (flat: two bars and a diamond,
  stencil-cuttable), `chevron-30`, `chevron-turned` (θ = 32°).
- **Where the slabs sit** — `inner` (inner halves: the concave stem survives, the chevron's
  outline does not), `centred`, `slabs-tall` (walls with a cube tucked between them), `slabs-low`
  (the cube stands proud), `thin` (quarter width).
- **The kerf** — `nokerf`, `widekerf`.
- **The Y in it** — it is the cube's two near top edges and its near vertical edge. `y-lit`
  (those three edges given a hairline and a weak halo), `y-floor` (the third stroke as a thin
  blue strip laid on the ground out through the empty quadrant), `y-shadow` (the cube casting
  forward between the slabs — nothing added a light would not add), `y-plan`, `y-plan-thin`
  (the flat chevron with a stroke down into the V).

Findings: `chevron-62` is the mark — two bars meeting at a square, and the first silhouette in
the whole exploration that is a symbol rather than a solid. Plan is a second mark for free, and
provably the same object as the dimensional one. The stem wants to be on the floor: lighting the
edges makes a Y but leaves the chevron as it was; the floor strip turns the chevron into the
letter. Outer halves or inner halves is the fork in the road — a chevron without a stem, or a
stem without a chevron. Slab height reads as a claim: taller than the cube says the agent is
inside the humans' space, lower says it stands proud, level is neutral and the only clean one.

## Sixteenth pass: panels, and a lens

`panels/` takes the half width on the other axis. The green blocks keep the cube's full width and
lose half their depth, so they lie against the cube's two near faces as panels with a kerf
between, rather than running out from it as arms. Same object and camera as `blocks/`.

- **The other way** — `hug` (48°), `hug-62` (the panels a thick V wrapped round the cube's near
  corner, the kerfs drawing the fork of the Y where a letter would put it), `hug-plan` (a square
  with two bars along its lower edges — the chevron inverted), `hug-30`.
- **How the panels stand** — `hug-tall` (two walls, the cube seen over them), `hug-low` (the cube
  on a V-shaped step), `far` (pushed to the far ends of their quadrants, ground between),
  `hug-thin` (fins).
- **Through a lens** — a barrel term applied after projection, every edge subdivided first so
  straight edges come out as shallow arcs: `blocks-lens` (the sketch, k = 0.14), `chevron-lens`,
  `hug-lens`, `cubes-lens`, `plan-lens` (k = 0.2), `chevron-lens-strong` (k = 0.32, a fisheye —
  past the edge, kept to show where it is).

Findings: the panels make it one solid — `hug-62` is the tightest three-part mark in the set and
the first where the Y sits exactly where a letter would put it. Plan inverts the chevron. The lens
works only because the edges bow: a radial scale alone swells the mark, and subdividing every
edge before warping is what turns straight lines into arcs — the whole difference between plotted
and photographed. The tighter the arrangement, the better it takes the bulge.

## Seventeenth pass: convex, lit, glossed

`convex/` corrects the lens and lights the object. The previous pass's barrel term had the sign
reversed — magnification rising with radius is pincushion, and an off-centre straight line bows
toward the centre. With magnification falling with radius the line bows away: outward, convex.

- **Convex** — `hug-convex`, `chevron-convex`, `blocks-convex` (the sketch; its vertical edges
  have the most to bow), `cubes-convex`, `hug-convex-strong` (k = 0.3, a fisheye, past the edge).
- **Neon on the kerfs** — the three kerfs are the cube's two near top edges and its near vertical
  edge, which is to say they are the Y. `hug-neon`, `chevron-neon`, `blocks-neon` (the stem a long
  vertical tube seen down the slot between the front blocks), `hug-warm` (lit green — a blue tube
  along a blue cube's edge loses the arm; green keeps all three strokes), `hug-spill` (the light
  the junction would throw, painted), `hug-assembled` (tubes, spill, halo, each weak).
- **Gradient flourishes** — `hug-graded` (a gradient per face: tops lighter toward their far end,
  sides darker toward the ground), `hug-gloss` (plus a specular on the cube's far corner — a
  moulded, polished solid), `chevron-gloss`, `hug-sheen` (one diagonal sheen over the whole
  object), `hug-rim` (a hairline along the top edges plus a little spill — the only lit build
  that still reads at 16px).

The lens lines bow with the object: the neon strokes go through the same subdivided warp as
the faces.

## Eighteenth pass: flat and a little 3D, the Zune way

`zune/` drops the lens and dresses the panels-on-a-cube the way Zune dressed a tile: a hue sweep
inside each shape that runs saturated to more saturated (green to `#d8f54a`, blue to `#6fd8ff`)
and never to white, and each piece's own colour bloomed softly behind it on black.

- **Flat** — the object straight down (φ = 89.5°): `flat`, `flat-glow` (per-piece glow: the blue
  glows blue beside two greens, so the count survives), `flat-gradient`, `flat-gradient-glow` (the
  Zune tile), `flat-sweep` (one green-to-blue gradient under the whole mark — the prettiest and
  the least honest, since the blue corner is just where the sweep ends), `flat-drop` (a deeper
  copy of each piece 1.6px under it — 3D without a camera), `chevron-flat-glow`, `cubes-flat-glow`.
- **A little 3D** — φ = 66°, a sliver of side under each top: `shallow`, `shallow-glow`,
  `shallow-gradient` (tops sweep hot-to-hue, sides run to deep), `shallow-thin` (height at a third
  — three tiles with a little thickness, the Zune's own physical vocabulary), `shallow-chevron`,
  `shallow-lit` (a faint white wash on the tops where the three meet — the kerfs are the Y and this
  is where they join).

Findings: `flat-gradient-glow` is the Zune tile and the same object as everything since the
sketch. The glow has to be per piece. `shallow-thin` is the build that belongs beside a Metro
surface. `flat-drop` does most of what the shallow elevation does while staying a flat SVG with
no projection in it.

## Nineteenth pass: lower for the stem, then lit in stages

`ladder/` drops the camera on the panels-on-a-cube and builds the glow back up one mechanism at
a time.

- **The angle** — the stem of the implied Y is the corner slot between the two panels, and its
  length is the elevation. `a66` (a stub), `a54`, `a46` (the stem as long as the arms are wide —
  the angle the rest uses), `a40` (the arms begin to foreshorten), `a46-tall` (blocks at 1.3×
  their footprint, which lengthens the stem without dropping further).
- **The glow, one mechanism at a time** — eight stages, each keeping every one before it:
  `g0` base (graded faces), `g1` + bloom (each piece's own colour blurred behind it — where most
  "add a glow" stops), `g2` + halo on the kerfs (the light gains a source), `g3` + core (a
  near-white hairline down each kerf; without it the halo reads as paint), `g4` + spill (a radial
  wash at the junction, clipped to the object), `g5` + chromatic wash (a wider bloom in a deeper,
  more saturated colour — light shifts hue as it spreads), `g6` + grain (noise multiplied into the
  wide wash at very low amplitude), `g7` + ambient.
- **On the others** — `chevron-full`, `cubes-full`: the top of the ladder on the chevron and on
  the sketch's own arrangement.

One rule, found the hard way twice: everything that spreads must reach zero inside the canvas.
A wide blur lifts the whole viewport by a percent or two and the edge clips it into a faint
rectangle. The wide washes are masked by a radial that hits black at r = 30, and the ambient
gradient stops there too.

## Twentieth pass: the implied Y, measured

`proportion/` holds the anchor (46°, blocks at 1.3× their footprint, half-depth panels, kerf at
0.11) and one proportion varied at a time. Every build is measured on screen: projected stem
against projected arm, the fork's half-angle off vertical, and the silhouette's width for one
of height.

- **Anchor** — `anchor`: stem : arm 0.98, fork 54°, silhouette 1.02 : 1. A 1 : 1 letter; a
  capital Y in most text faces runs about √φ stem to arm and 30–35° off vertical, so the anchor
  is a wide, short Y — which is what makes it read as an object first.
- **Stem against arm** — block height solved for the ratio, camera fixed: `ratio-0_62` (1 : φ,
  h 0.82), `ratio-1_00` (h 1.32, the anchor's own), `ratio-1_27` (√φ : 1, h 1.68, where most
  capital Ys sit), `ratio-1_62` (φ : 1, h 2.14, a lowercase y's tail).
- **The fork** — nothing symmetric narrows it except skewing the plan axes toward the diagonal:
  `fork-00`, `fork-08` (fork 46°, the cube still a cube), `fork-15` (39°, a lozenge), `fork-22`
  (31°, past a letter). The cube pays for every degree the Y gains.
- **The chevron** — the two panels and the cube's near faces read from above: `chev-shallow`
  (depth 1/φ², two strokes and a cube), `chev-golden` (depth 1/φ — the panel faces and the
  cube's exposed faces divide the arms golden, silhouette 1.07 : 1), `chev-40` (six degrees
  lower: longer stem, thinner tops), `chev-52` (six higher: the stem back toward a stub).
- **Golden** — `golden` (stem √φ, depth 1/φ, kerf 1/φ⁵ ≈ 0.09), `golden-fork` (the same with
  the plan skewed 12°: stem 1.36, fork 42° — the nearest this object comes to a typographic Y
  while still being a cube).

Findings: the stem is cheap (grow the blocks) and the fork is not (skew the plan, lose the
cube); 8° is the far end. The chevron wants 1/φ panels. 46° stays the angle: six degrees down
buys stem and loses the top faces that carry the gradient. Golden is a candidate, not a proof —
its value is that every proportion is on one ratio.

## Twenty-first pass: letter first, then the block

`letter/` starts from the typeface's capital Y, bends it for the block, and lays it over the
block as the feedback while the block's parameters move.

- **The letter** — `noto` (Noto Sans's own capital: fork 28.6° off vertical, box √φ tall, stem
  0.126 of cap, stem 0.62 of arm — golden already), `rebuilt` (the same letter regenerated from
  four numbers — fork, weight, stem : arm = 1/φ, mitred junction — so it can be bent; its box
  comes out √φ on its own). Opening the fork forces a choice: `golden-box` (39°, box held — the
  stem grows tall, arms too short to leave a block) or `golden-stem` (39°, stem held — the box
  goes wide and the arms run long). `mid-stem` (42°), `open-stem` (45°). The stem wins.
- **Why the anchor is not a letter** — `anchor-y`: the 39° letter over last pass's anchor, stem
  to stem. The camera cannot narrow a square plan's fork below 45°; at 46° elevation it is 54°.
- **Kerfs turned** — the plan stays square and only the kerfs turn: each arm kerf leaves the
  near corner a few degrees into the cube's quadrant and exits through the SIDE of the block,
  not at a corner, so the arm tips are lost off the edge and the angle says they continue. The
  blue piece becomes a kite prism; the green panels taper to keep the kerf a hairline.
  `turned-39` (kerfs turned 15° in plan, cube corner 60°), `turned-42` (12°, 66°), `turned-45`
  (9°, 71°), each with a `-y` twin carrying the letter at 34% white. `turned-39-tall` (1.7×).
- **Two other ways** — `flare-39` (panels kept as true blocks, the kerf widening toward the
  tips — reads as ground showing through, not a stroke), `weight-39` (kerf widened to the
  letter's own stroke — the Y becomes a blue positive between two green blocks: the blue-fork
  idea arriving by another road).

Findings: Noto's Y is golden in the stem and √φ in the box, and opening the fork makes those
part company; holding the stem is what lets the arms run 1.7× the visible kerf. Turning the
kerfs rather than the plan keeps the square silhouette and charges the whole cost to the blue
top's near corner. 42° is the balance: the top still reads as a square seen from a corner, the
fork still reads as a letter's. A 24-cell grid (fork × elevation × height × mode) was judged
on a sheet; only these cells said something.

## Twenty-second pass: a lens instead of a skew

`lens/` narrows the fork by the camera rather than by the object, the letter laid over each
build at the fork it actually produces (`-y` twins).

- **A real lens** — `wide`: a pinhole camera two block-widths away, aimed above the block so the
  junction sits well below the optical axis. At the picture centre every camera is orthographic;
  off it the arms run to the horizon's vanishing points and the fork closes to 41° with every line
  straight — and the same lens sends the verticals to the nadir, so the stem is a quarter of the
  arm and the block a plate.
- **The anamorphic** — every parallel projection of the block is the orthographic one followed
  by an affine map of the picture, and the symmetric ones are vertical stretches: every angle off
  vertical tightens by its tangent, lines stay lines, the block gets taller by the same factor.
  `stretch-13` (1.3×, fork 47°, silhouette 0.79 : 1), `stretch-13-low` (the same on blocks a
  footprint tall, so the stem is the anchor's and only the tops carry the stretch — the one lens
  build that keeps the block a block), `stretch-145` (44°; already a stretched picture).
- **The bent lenses** — a radial term about a centre on the stem's axis bends whatever misses
  that centre. `bowed` (pincushion 0.08 about the stem's foot over 1.15×: the arms leave the
  junction at 44° and bow outward toward the block's own angle, the tops pinch into a gem),
  `cushioned` (barrel 0.10 about the junction over 1.3×: all three kerfs are radial so they stay
  straight; the outline swells — a flourish, not a fix).

Findings: the only lens that narrows the fork and keeps the lines is the anamorphic, and it is
the same thing as an oblique parallel projection; its cost is the block's proportion. Against
the turned kerfs of the pass before: the turn gets 42° with the plan square and the cube's
corner at 66°, the lens gets 47° with a cube that is a tall box. Two honest routes; the lens
costs the block's proportion, the turn costs the cube's corner. A 24-cell pinhole/fisheye grid
and a 15-cell picture-space grid were judged on sheets.

The far corner, pressed. What `cushioned` has that the others do not is the blue far corner
brought down; a radial lens cannot do that without bringing the greens' outer corners in too,
because they sit at the same radius (`fisheye`, a stereographic mapping about the junction, is
the cushion again). A lens with curvature in ONE axis can: `pressed` is the cushioned build's
barrel with its horizontal term removed — a cylindrical lens, y' = y(1 − k y²) with x untouched —
so vertical edges stay straight, the greens keep their sides, and the far corner, being the
highest point, drops most; `pressed-soft` at six hundredths; `pressed-foot` centred on the stem's
foot so nothing below moves, where the fork pays instead (vertical compression at the junction
flattens the arms).

## Twenty-third pass: jelly, not glass

`jelly/` cuts the foot cylinder down until the blocks read as cubes, then dresses the result as a
material: acrylic, or agar jelly — semi-translucent, never glass. Built by `gen25.py`.

- **The height** — the cylinder about the foot (`pressed-foot`'s lens: 1.4× squeeze, barrel
  0.02 about the stem's foot) on shorter blocks. A block reads as a cube when the green's full
  face is square, which under the squeeze is at 0.75× the footprint: `cube` (stem : visible arm
  0.76, the letter's own is 0.62; `cube-y` with the letter over), `cube-tall` (0.9×, still a
  tall block), `cube-low` (0.62×, a tile). The press and the fork (48°) are unchanged by the
  height because the cylinder is scaled to the stem. A 16-cell grid over stretch × press × height
  was judged on a sheet; barrel 0.04 folds the top at any height.
- **The material, built up** — one mechanism per rung, each keeping the ones below: `j0` body
  (tops sweep hot to hue, sides deepen downward), `j1` + bloom (the glow, kept), `j2` + rim
  (each piece's edges brightened from inside in its own hot colour — the layer that says jelly:
  light entering a turbid body leaves at the nearest edge, so edges are brighter than the middle,
  which is the opposite of glass), `j3` + sheen (one broad soft highlight per top toward the
  light; a hard reflection would say glass), `j4` + depth (the hidden edges through the body and
  the cube's near faces leaking blue through the panels, blurred at a fifth — crisp they read as
  wireframe), `j5` + inner light (the kerfs' halo and core from the neon ladder, wider, dimmer
  and clipped to the object with a spill at the junction: the light is in the block, not on it),
  `j6` + turbidity (fine noise overlaid into the body at a fifth; felt above 64px, not seen; the
  first attempt overlaid a constant mid-grey, which is the identity — the noise must carry the
  luminance), `j7` + ground (a reflection under the foot, fading fast).
- **The top of the ladder** — `jelly`, every layer but the ground, for the lockup and the
  favicon; `j7` with it, for presentation.

Findings: the rim is the material and everything above it refines; the mark survives to 16px
with the rim and bloom alone doing the work there. Each piece is rendered as one filtered group,
which is sound here because the cube sits wholly behind both panels in depth and the panels do
not overlap on screen.

The wide lens, in jelly. `wide-jelly` is the lens pass's pinhole build (eye two block-widths
away, aimed two above centre, fork 41° with every line straight) given the material whole, with
the neon ladder's core line cut from the lighting: the kerfs are lit by the halo, a wide soft
stroke clipped into the object, and the spill at the junction. `wide-spill` drops the halo too
and strokes nothing. `wide-ground` adds the reflection, which under this lens has to anchor on
the object's lowest point on screen rather than the stem's foot — the panels' near corners sit
below it. Built by `gen26.py`, whose material is written against a projector so the pinhole
needed one adapter: a face is visible when it faces the eye, not one view direction.

Clearer, with pizazz. `clear` draws the body at 0.72 opaque over its own back faces at 0.35, in
the deep colour, so the far edges of each piece show through it — lowering opacity alone only
darkens a body against black; what reads as see-through is the hidden geometry underneath.
`clearer` is 0.6 over 0.45 with a heavier rim, the far end of the range. On the clearer body,
one at a time: `clear-lit` adds three points of light on the corners that face the light (the
apex and the two outer corners; one on every corner read as an effect), each hue thrown into
the other across the kerfs (green in the blue across the arms, blue in the greens across the
stem — the pieces colour each other where they meet), and a wet gleam across the blue top as a
band twice as wide and twice as blurred as the first attempt, which read as the core line
coming back; `clear-caustic` pools each piece's colour on the ground under it, the one layer
that shows the material without touching the object; `clear-ground` adds the reflection to
that. Built by `gen27.py`.

Locked: `clearer`. It is copied to `mark.svg` at the top of this directory as the mark; the
ladder under `jelly/` stays as the record of how it was reached.

The pull back. `intro` is the mark's intro as one SVG, no script: a top-down view of the agent
as a solid blue diamond, framed on the agent, the camera pulling up and back into the wide lens
while the collaborators slide in from 0.12 of a block width further out, from nothing, and the
material arrives. The solid start is a flat copy on top, shaded per face so the cube is a cube
the moment the camera tilts; only the material underneath, rim to spill, fades in after it. The
collaborators start arriving at the first instant they would be wholly inside the frame at
their far position, so nothing fades in cropped. It ends on `clearer-clean`, the clearer body
without its sparkle, bleed, gleam and caustics; `intro-lit` is the same run ending on `clearer`
itself. The run is SMIL with the easing baked into the samples: every face is one path whose
`d` carries 37 keyframes on uniform `keyTimes`, and the clips, bloom, back faces and solid start
are each a `use` of it; a face's visibility flips discretely, which is safe because a face only
turns over when it is edge-on. Camera, travel and fades are all sampled through
cubic-bezier(0.42, 0, 0.58, 1), CSS's symmetric ease-in-out: Tailwind's (0.4, 0, 0.2, 1) left
faster than it landed. The sheen is the reflection of one fixed point light in the shared top
plane — the eye's line to the lamp's mirror image meets the plane at one point, and that is
where the highlight is, clipped into whichever tops it falls across — so it slides over the
surface as the camera moves instead of riding on it, and each top's graded fill runs from its
corner nearest the lamp; the lamp is not part of the material, so the sheen is there from the
first frame. The last frame is `clearer-clean` but for that sheen: same rig and material, with
four-point faces (the straight lens needs no subdivision). About 110 KB, 18 KB gzipped; SMIL cannot read `prefers-reduced-motion`, so a page that
respects it shows the static mark instead. Click to replay. Built by `gen28.py`.
