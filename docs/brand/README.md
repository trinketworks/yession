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
