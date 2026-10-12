# The yession mark

This file records why the yession mark looks as it does. Part one describes the current mark:
what it is, how the generator builds it, the constraints it meets, and the rules for using it.
Part two lists the directions that lost, and why. Git history holds the full pass-by-pass log.

`assets/logo/gen.py` generates the mark and every file derived from it. `assets/logo/README.md`
lists each file and the surface it serves. The study files that part two names are the SVG
files in the directories of this folder, one directory per pass.

## The current mark

### What it is

The brief was a Y with three points: two green for the humans and one blue for the agent. The
mark answers with one object instead of a letter.

A blue cube, the agent, stands in the far quadrant of a two-by-two plan. Two green panels, the
people, lie against the cube's two near faces. Each panel has half the cube's depth. A hairline
kerf separates each piece from the next. The Y is the two kerfs and the cube's near vertical
corner. The stem of the Y is the corner slot between the two panels. Its length is the camera
elevation.

A camera renders the object, so a turned view is the same object as the front view. A drawn
mark cannot claim that.

The colours are the product's own tokens in `app/tokens.css`: `#a8dd00` (green, human) and
`#1ba1e2` (blue, agent). They sit on the product's black.

### Construction

`gen.py` holds these values (`H`, `GK`, `DD`, `DIST`, `UP1`, `PHI1`, `LAMP`). Change them there.
The block is 1 unit wide.

| Quantity | Value | Reason |
| --- | --- | --- |
| Block height | 1.3 | The stem is cheap: taller blocks lengthen it. Under a parallel view at 46°, stem : arm is 0.98. |
| Panel depth | 0.5 | The panels keep the cube's full width and lose half their depth. They lie against the cube, so the three pieces read as one solid. |
| Kerf | 0.11 | A hairline. `logo-16.svg` cuts the kerfs twice as wide. |
| Piece height | one height for all three | Slab height reads as a claim. Taller panels say the agent is inside the humans' space. Lower panels say it stands proud. Level is neutral and the only clean choice. |
| Camera | pinhole at 45° azimuth and 46° elevation, with the eye 2 block-widths from the aim point | See "Camera and fork". |
| Aim point | 2 above the cube's centre | The junction sits well below the optical axis. |
| Lamp | one fixed point light, behind-left and high | The logo and the intro share the lamp. |
| Box | 64 units, with 7 units of air around the object | The bloom and the arriving panels need room. Earlier passes used 5. |

### Camera and fork

The fork is the half-angle of the arms off vertical, measured on screen. A parallel camera
cannot narrow a square plan's fork below 45°. At 46° elevation the fork is 54°. A capital Y in
most text faces runs 30° to 35° off vertical. The parallel anchor has a stem : arm of 0.98 and a
silhouette of 1.02 : 1. It is a wide, short Y. That makes it read as an object first.

The mark uses a pinhole lens instead. At the picture centre every camera is orthographic. Off
the centre, the arms run to the horizon's vanishing points, and the fork closes to 41° with
every line straight. The plan stays square, so the cube stays a cube.

The lens has one cost. It sends the verticals to the nadir. In the lens study this made the
stem a quarter of the arm and the block a plate.

The elevation stays at 46°. Six degrees lower gives a longer stem and thinner top faces. The top
faces carry the gradient. Six degrees higher shrinks the stem toward a stub. At 40° the arms
start to foreshorten.

Any change to the plan that narrows the fork costs the cube. Eight degrees of skew is the far
end. See pass 20 below.

### Material

The material is jelly, not glass. It is semi-translucent acrylic or agar jelly.

- **Body.** Each piece is translucent over its own back faces. On black, the body is 0.6 opaque
  over back faces at 0.45, in the deep colour. Lowering opacity alone only darkens a body
  against black. The hidden geometry under the body makes it read as see-through. Tops sweep
  from the hot colour to the hue. Sides deepen downward.
- **Rim.** Each piece's edges are brighter than its middle, in its own hot colour. Light that
  enters a turbid body leaves at the nearest edge. Glass behaves the opposite way. The rim is the
  material, and every other layer refines it.
- **Sheen.** One broad soft highlight sits on each top. The highlight is the reflection of the
  fixed lamp in the shared top plane. The eye's line to the lamp's mirror image meets the plane
  at one point, and the highlight sits there, clipped into whichever tops it crosses. It slides
  over the surface as the camera moves. Each top's graded fill runs from its corner nearest the
  lamp. The lamp is not part of the material, so the sheen shows from the first frame. A hard
  reflection would say glass.
- **Depth.** The hidden edges show through the body at 0.3, under a blur. Crisp, they read as
  wireframe.
- **Spill.** The generator clips a radial wash at the junction to the object. The light is in
  the block, not on it.
- **Turbidity.** Fine noise is overlaid into the body at a fifth strength. A viewer feels it
  above 64px and does not see it. The noise must carry the luminance. A constant mid-grey overlay
  is the identity and does nothing.
- **Bloom.** A blur of each piece's own colour sits behind it, at 0.3 on black. The glow must be
  per piece. The blue glows blue beside two greens, so the count survives.
- **Hue.** Each hue runs to a hotter self and never to white: green `#a8dd00` to `#d8f54a`, blue
  `#1ba1e2` to `#6fd8ff`. Each hue also runs to a deep shade: `#6f9a00` and `#0f6ea3`.
- **Agent sides on black.** The generator darkens the agent's own sides by a fifth against its
  top. The eye reads the Y off the step in value from a top to the side beside it, and the jelly
  softens that step. On paper the sides already read against the ground, so `logo-light.svg`
  leaves them unaltered.

Three build rules keep the material sound:

1. Everything that spreads must reach zero inside the canvas. A wide blur lifts the whole
   viewport by a percent or two, and the edge clips it into a faint rectangle.
2. A zero-width shape has a degenerate `objectBoundingBox`. A vertical line takes neither a
   default `linearGradient` nor a default filter region. Both need `userSpaceOnUse`.
3. Each piece is one filtered group. This is sound only while the cube sits wholly behind both
   panels in depth and the panels do not overlap on screen.

### The intro

`intro.svg` is one SVG with no script. It runs 2.4s and frames the agent. `logo.svg` is its last
frame. The generator builds both from the same code, and builds the logo at t = 1. The two
cannot disagree. Three clocks overlap:

- **Material.** The material runs over the opening 40%. The agent starts as a solid blue diamond
  seen from above. A flat copy on top fades out while the jelly underneath fades in. From above,
  the jelly reveals the bottom face and its edges through the body.
- **Camera.** The camera runs from 8%, as the edges start to show. It pulls up and back into the
  wide lens. Its ease-in keeps it near still while the block is still becoming one. A pause
  before it moved read as a pause.
- **Collaborators.** The panels start at 30%. A gate checks that they are wholly inside the frame
  at their far position (0.1 of a block width out), with room for their glow from then on.
  Nothing fades in cropped.

The pull is one arc. The eye's path is already near a straight line in the world. With the lens
scale lerped, the agent shrank in the first third and tilted in the rest. That read as up, then
back. The generator solves the scale per frame, so the agent's width on screen changes evenly
(33 to 28 units). The tilt keeps pace. Its visible sign is the sides growing. The end frame has
7 units of air, not the 5 of the earlier build. The arriving panels and their bloom then clear
the edge, so the mark sits a little smaller in its box.

The eye also dollies. It starts three times `DIST` out and eases to `DIST`. The scale is already
solved per frame, so the framing does not move. Only the strength of the perspective changes.
The dolly has one reason. At two block-widths, a collaborator's near vertical corner projects
through horizontal around 1.7s. That puts a flat four-unit edge under the panel, where the mark
itself holds the edge at 43 degrees. A horizontal edge under a form reads as a slice, not a
corner. Further out, the edge stays a corner the whole way. The eye is back at `DIST` by the last
frame, so the mark does not change.

The run is SMIL with the easing baked into the samples. The ease is `cubic-bezier(0.42, 0, 0.58,
1)`, CSS's symmetric ease-in-out. Tailwind's `(0.4, 0, 0.2, 1)` left faster than it landed. Every
face is one path whose `d` carries 49 keyframes on uniform `keyTimes`. The clips, bloom, back
faces, hidden edges and solid start are each a `use` of it. The straight lens needs no
subdivision, so each face has four points.

Interpolating a `d` is the most expensive thing SMIL does per frame, so the file holds the count
down. The hidden edges were once a polyline apiece, each with its own morph. That made 22 morphs
against the faces' 18, which is more than half the budget for a layer at 0.3 under a blur. A
face turned away has exactly the hidden edges as its boundary. The file strokes that face
instead, and the faces already morph. The measurement used the starting screen's own layout at
224px and a 3x pixel ratio, under a 10x CPU throttle (a phone-class budget, paired runs):

- Frame rate rose from 25.9 fps to 30.0 fps.
- File size fell from 135 KB to 97 KB.
- Stripping every filter buys 2 fps at that size.
- Rebuilding at 17 keyframes instead of 49 measures the same.

So neither the filters nor the keyframe count is what costs. A face's visibility flips
discretely. This is safe, because a face only turns over when it is edge-on.

### Constraints it meets

- **The count.** Two humans must read as two. They read as two only when their pieces stay
  separate. Green outweighs blue at equal area, so the agent's share of the mark needs slightly
  more area.
- **The top plane.** Blue holds the top. With blue on a wall, the two greens became one L-shaped
  mass and the count was lost.
- **A light ground.** Green on a light ground is 1.9:1. A mark that leaves the black app needs a
  plate under it or a darker variant. `logo-light.svg` is the variant for light grounds. Its
  geometry is the same, and its material changes. A field mark survives a light ground better
  than a stroke mark, because the cut takes whatever is behind it.
- **Small sizes.** Glow does not survive 16px. The mark is lit at the sizes where light is
  visible and flat where it is not.
- **Filters.** SVG filters are slow at scale. They render differently across engines, and
  several icon and email pipelines drop them. The mark depends on filters. The 16px cut has none.
- **One lit object.** The brand mark is the one lit object in the product. Nothing else may
  look lit. `docs/visual-design.md` (Form) states the rule.

### Type

Sans is the machine: the chrome, and the agent, which is part of it. Serif is the person. Noto
Sans sets the chrome and the agent. Source Serif 4 sets a person's words: messages, drafts, and
the composer. It uses 15px at weight 350. The terminal keeps Monaspace Neon.

The typeface says who is speaking before a reader reaches the author line. Source Serif 4 is the
companion of Source Sans, so the difference between the two registers is one of class alone. A
conversation then reads as one voice in two registers, not two typefaces arguing.

The 350 is an instance cut from the variable font and weighted by eye against Noto Sans 300 on
black. `app/tailwind.css` declares the face (`@font-face 'Source Serif 4'`). `app/tokens.css`
names it (`--font-human`, beside `--font-ui` and `--font-agent`). The files sit in `app/fonts/`
(`source-serif-4-latin-{350,400,600}-normal.woff2`, SIL OFL 1.1). The comment above the
declarations in `app/tailwind.css` holds the full face story.

### Rules for use

1. Take every mark file from `assets/logo/`. Regenerate all files together with `gen.py`. Do
   not hand-edit one generated file.
2. Use `logo.svg` on black and dark surfaces. Use `logo-light.svg` on paper and light surfaces.
3. Use `logo-16.svg` for anything under 24px, and as the SVG favicon.
4. Show `logo.svg`, not the intro, to a page that respects `prefers-reduced-motion`. SMIL cannot
   read that preference.
5. Do not make anything else in the product look lit.
6. Set the agent and the chrome in the sans. Set a person's words in the serif.
7. For file sizes, the wordmark and the icon, read `assets/logo/README.md`.

## Rejected directions

Each entry gives the pass, its directory, the idea, and why it lost. Some entries also record
what the pass gave to the current mark. The study files are static. This repository holds no
generator for them.

- **Pass 1, the SVG files in this folder.** Sixteen stroke and field marks on the brief:
  `trifoil.svg`, `letter.svg`, `stencil.svg`, `lowercase.svg`, `blades.svg`, `nodes.svg`,
  `inward.svg`, `blend.svg`, `beam.svg`, `bloom.svg`, `tile.svg`, `room.svg`, `counterform.svg`,
  `index.svg`, `lockup.svg` and `substitute.svg`. Lost: `letter` and `bloom` join the arms and
  lose the count of two. Only `letter`, `stencil`, `lowercase` and `index` survive 16px. Green
  on a light ground is 1.9:1.
- **Pass 2, `cube/`.** An isometric cube. Its three interior edges sit at 120°, and the Y is the
  cut between the fields. Lost: with blue on a wall (`cube-swap`), the greens merge into one
  L-shaped mass. The disc projection (`disc.svg`) becomes a dot at 16px, and the hexagon holds.
  A cut of 3.6px on the 64px grid is legible and quiet.
- **Pass 3, `form/`.** Edge bow, radius, taper, shear and layering on a solid with a straight
  seam. Curvature is the loudest lever: four degrees of bow turns a rendering of a solid into
  something with a temperament. Lost: `petal` and `draw` collapse into a blob and a star at
  16px. The straight seam keeps the set consistent. `hold` shrank the agent's face inside the
  human ones. That changes the argument, not the mood: authority is scoped, and the small thing
  is held.
- **Pass 4, `fold/`.** A crease in each hexagon face gives six triangles. Three interior edges
  are the Y and three are folds, which show only as a step in value. Lost: `stemmed` and
  `offaxis` read as a letter, but the cube stops being geometrically true. Six flats survive
  16px and six gradients do not. Radius at the hub buys more than radius at the rim, because
  the junction is what a reader looks at.
- **Pass 5, `glyph/`.** Noto Sans's own capital Y as the cut. Noto cuts its Y with the arms 33°
  off vertical and the junction at 39% of the cap height. An isometric cube puts its interior
  edges at 60° and the junction at the centre. The two are 27° apart. Lost: moving the solid
  toward the letter costs the solid. 45° is the floor, and below it the hexagon becomes a leaf.
  Flattening the view to 66° works, because it falsifies nothing. Stretching the glyph costs
  less than bending the cube: at 170% it is still Noto's Y. Seven degrees of rotation reads as
  intent and fourteen reads as an accident. Semibold survives 16px, because a counterform must
  read as a gap, not a scratch.
- **Pass 6, `pair/`.** A green solid with Noto's Y cut through it and a blue cube held in the
  fork. `prongs` keeps both readings, because a narrow cut leaves the large solid whole. Lost:
  past about 40px of reach, the cut severs the solid. `bracket`, `claw` and `deepfork` then read
  as two green pieces flanking a blue one. That is the original brief from the opposite
  direction. Clearance does more work than size: `tight` holds and `float` is merely near. The
  large solid must give up blue, or the pair has two agents.
- **Pass 7, `void/`.** The second cube as a cube-shaped corner removed from the first. In an
  isometric projection the void's inner corner lands on the outer one, so nothing is drawn
  twice. The solid is green, and the only blue is the removed part. Lost: the concave and convex
  readings are ambiguous. `bite` and `flip` are the same polygons and differ only in the order
  of three values, so some readers see the other one. `rimmed` settles it. The void holds at
  16px better than `pair/`.
- **Pass 8, `fork/`.** The held cube seated in the isometric Y's 120° fork. A hexagon's bottom
  corner measures 120°, so the held cube's lower edges lie along the arms' inner edges. `flush`
  (46%) is the one ratio that is not a preference, because the top vertex lands on the large
  cube's. Clearance decides one object or two: `fit` and `clear` are the same geometry 1.3px
  apart. It holds at 16px. Not pursued further. `narrow/` used the letter's fork instead.
- **Pass 9, `narrow/`.** The same held cube in the letter's fork, about 33° off vertical. A
  hexagon's 120° corner cannot descend into a V that tight, so the two inner edges catch the
  cube and wedge it high, held rather than seated. Weight moves the seat: 600 holds and 200 does
  not. With the held edge at 30% of the large solid's, the two silhouettes share a top corner
  exactly (`flush`). `contained` is the 16px build, because a larger cube breaks the outline.
  `stretched` (155%) buys a bigger cube inside the silhouette. Not pursued further.
- **Pass 10, `dice/`.** The shortlist (`fold/whisper.svg` and `fold/modul.svg`) broken into
  eleven dimensions and thrown independently: silhouette, arm angle, cut weight, cut profile,
  cut reach, corners, fold, colour, surface, extra and rotation. The pass used seed 7 and
  eighteen throws with no curation. `rolls.json` records each throw. Lost: glow and heavy
  round-ended cuts. Every roll that carries them is muddier, and worst at 16px. A blue Y drawn
  into the cut on an all-green solid (rolls 01, 04, 05, 08, 14) makes the letter an object, not
  an absence. The squircle is the only silhouette that reads as a made object. A glyph cut gains
  from rotation: roll 11 at −14° looks placed, and roll 03 at the same angle looks broken. Roll
  13 has no cut and says nothing, so the letter is not decoration.
- **Pass 11, `tile/`.** Superellipse tiles (n = 4). A hexagon draws a solid, and a squircle is
  an object: a key, a window, a lens, a card. The families are `panes`, `aperture`, `keycap`,
  `overlap`, `bars`, `contained` and `stack`. Lost: `overlap` reads as a face at every
  arrangement, and `chip` is a costume. Worth taking further: `panes` (the Y is the gutter
  between three panes, so the mark is about the product), `aperture` (an honest animation),
  `keycap-lit` (the Y as a lit object) and `bars` (three separate objects, so the two humans
  are plainly two).
- **Pass 12, `neon/`.** A lit cut on `fold/whisper.svg`: painted spill, a tube, lighting
  filters, a room, and assembled states. Painted `spill` costs nothing and works in any
  pipeline that draws a gradient. `dual` (arms green, stem blue) keeps the
  two-humans-and-an-agent reading, and lighting everything one colour loses it. `edge` reads as
  lit, survives print, and is the only assembled build legible at 16px. Lost: the tube, lighting
  and room builds. They depend on SVG filters. None of the glow builds survive 16px.
- **Pass 13, `bluefork/`.** The narrow letter's fork filled blue, as a cube corner that need
  not be geometrically true. `seam` is the best: a green solid with a blue corner driven into its
  top, and the Y is the outline of the blue. It is the first build where nothing is cut away.
  Noto's own inner V filled gives a kite that holds at 16px better than any drawn wedge. Lost:
  `emerge`, a crystal coming out of the fork. It suits a splash screen, not a favicon.
- **Pass 14, `seamlit/` and `blocks/`.** `seamlit/` lit `bluefork/seam-two.svg` with the `neon/`
  mechanisms. `crease-tube` lights the wedge's near edge and leaves the stem dark, and then the
  cube looks like a cube. `edge` still wins on cost. `blocks/` read a pen sketch as four tall
  blocks in a two-by-two with the near one missing, and hairline kerfs between the three that
  remain. A camera renders it, and that approach stays. `sketch` uses a height of 1.8 times the
  footprint, θ = 45° and φ = 35.26°. Height is the dial. Cubes make a stair, 1.8 makes three
  things standing, and 2.6 makes pillars with the letter shrunk to a notch.
- **Pass 15, `chevron/`.** The green blocks thinned to half width and seen from above. Two arms
  hang off the blue cube, and the whole is a chevron with the agent at the apex. `chevron-62` is
  the first silhouette that is a symbol, not a solid. The plan view is a second mark and is
  provably the same object. Lost: the chevron has no stem. Lighting the cube's three edges makes
  a Y and leaves the chevron as it was. A thin blue strip on the ground (`y-floor`) turns the
  chevron into the letter. Outer halves or inner halves is the fork in the road: a chevron
  without a stem, or a stem without a chevron.
- **Pass 16, `panels/`.** The green blocks keep the cube's full width and lose half their
  depth. These are the panels of the current mark. `hug-62` is the tightest three-part mark in
  the set, and the Y sits where a letter would put it. Not chosen: `hug-tall` (walls), `hug-low`
  (a V-shaped step), `far` (panels at the far ends of their quadrants) and `hug-thin` (fins).
  Lost: the barrel lens (`hug-lens` at k = 0.14, `plan-lens` at k = 0.2). A radial scale alone
  swells the mark. Every edge needs subdividing before the warp, and then straight edges come
  out as arcs. The tighter the arrangement, the better it takes the bulge. `chevron-lens-strong`
  (k = 0.32) is a fisheye, kept to show where the edge is.
- **Pass 17, `convex/`.** The pass fixed the lens. Pass 16 had the sign reversed: magnification
  that rises with radius is pincushion, and an off-centre straight line bows toward the centre.
  Magnification that falls with radius bows the line outward. The pass also lit the kerfs
  (`hug-neon`, `chevron-neon`, `blocks-neon`) and glossed the faces (`hug-graded`, `hug-gloss`,
  `hug-sheen`). `hug-warm` is lit green, because a blue tube along a blue cube's edge loses the
  arm and green keeps all three strokes. `hug-rim` (a hairline along the top edges plus a
  little spill) is the only lit build that still reads at 16px. Lost: the bent lens. Pass 22
  found it a flourish, not a fix. `hug-convex-strong` (k = 0.3) is a fisheye, past the edge.
- **Pass 18, `zune/`.** Flat, and a little 3D at φ = 66°, in the manner of a Zune tile. A hue
  sweep runs inside each shape, from saturated to more saturated and never to white. Each
  piece's own colour blooms behind it. Kept: the hot hues and the per-piece glow.
  `flat-gradient-glow` is the Zune tile. Lost: `flat-sweep`, one green-to-blue gradient under the
  whole mark. It is the prettiest and the least honest, because the blue corner is just where
  the sweep ends. `flat-drop` does most of what the shallow elevation does, as a flat SVG.
  `shallow-thin` (height at a third) belongs beside a Metro surface.
- **Pass 19, `ladder/`.** The elevation sets the stem's length: `a66` is a stub, `a46` has a stem
  as long as the arms are wide, and `a40` starts to foreshorten the arms. Kept: 46°. The pass
  also built a glow in eight stages, one mechanism per stage and each keeping the ones before
  it: bloom, a halo on the kerfs, a near-white core, a spill at the junction, a deeper chromatic
  wash, grain, and ambient light. Kept: the rule that everything that spreads must reach zero
  inside the canvas. The jelly material replaced the stack, and the core line was cut.
- **Pass 20, `proportion/`.** One proportion varied at a time from the anchor (46°, blocks 1.3
  times the footprint, half-depth panels, kerf 0.11). The pass measured every build on screen.
  Stem against arm ran from `ratio-0_62` (1 : φ) to `ratio-1_62` (φ : 1). `ratio-1_27` (√φ : 1)
  is where most capital Ys sit. Skewing the plan narrows the fork at the cube's expense.
  `fork-08` has a 46° fork and keeps the cube a cube. `fork-15` (39°) is a lozenge. `fork-22`
  (31°) is past a letter. Lost: golden proportions. `golden` uses stem √φ, depth 1/φ and kerf
  1/φ⁵ ≈ 0.09. `golden-fork` skews the plan 12° for stem 1.36 and fork 42°. Golden is a
  candidate, not a proof. Its one value is that every proportion sits on one ratio. The chevron
  wants 1/φ panels.
- **Pass 21, `letter/`.** Noto's capital Y bent for the block and laid over it. Noto's Y has its
  fork 28.6° off vertical, a box √φ tall, and a stem of 0.126 of the cap and 0.62 of the arm.
  Opening the fork forces a choice. If the box holds, the stem grows tall and the arms run too
  short to leave a block. If the stem holds, the box goes wide and the arms run long. The stem
  wins. Turning only the kerfs keeps the plan square: `turned-42` gives a 42° fork, with the
  kerfs turned 12° in plan and the cube corner at 66°. At 42° the top still reads as a square
  seen from a corner, and the fork still reads as a letter's. Lost: the turn charges its whole
  cost to the blue top's near corner. `flare-39` reads as ground showing through, not a stroke.
  `weight-39` makes the Y a blue positive between two green blocks, which is the blue-fork
  idea by another road.
- **Pass 22, `lens/`.** The fork narrowed by the camera, not the object. The wide pinhole won
  and part one describes it. Lost: the anamorphic lens, an affine vertical stretch that keeps
  lines straight and equals an oblique parallel projection. `stretch-13` gives a 47° fork and a
  silhouette of 0.79 : 1. `stretch-145` gives 44°. `stretch-13-low` keeps the block a block. The
  cost is the block's proportion, which becomes a tall box. The turned kerfs of pass 21 cost the
  cube's corner instead. Also lost: the bent lenses. `bowed` (pincushion 0.08) and `cushioned`
  (barrel 0.10, with radial kerfs that stay straight) are flourishes, not fixes. A radial lens
  cannot press the blue far corner without pulling in the greens' outer corners. `fisheye` is
  the cushion again. A cylindrical lens, y′ = y(1 − k y²) with x untouched, can. `pressed`
  keeps the vertical edges straight and the greens' sides, and drops the far corner most.
  `pressed-soft` uses 0.06. `pressed-foot` centres on the stem's foot so nothing below moves,
  and the fork pays: vertical compression at the junction flattens the arms.
- **Pass 23, `jelly/`.** The cylinder about the foot (the `pressed-foot` lens, a 1.4× squeeze
  with barrel 0.02) on shorter blocks. A block reads as a cube when the green's full face is
  square, which under the squeeze happens at 0.75 times the footprint. `cube-tall` is 0.9 times
  and `cube-low` is 0.62 times, a tile. Barrel 0.04 folds the top at any height. Lost: the wide
  pinhole lens replaced the cylinder, and the jelly material stayed. Also lost: `clear` (0.72
  over 0.35), which `clearer` (0.6 over 0.45, with a heavier rim) replaced. `clearer` is the
  body the mark was chosen from. Four layers stay out of the mark:
  - The sparkle: three points of light on the corners that face the light. One on every corner
    read as an effect.
  - The bleed: each hue thrown into the other across the kerfs.
  - The gleam: a wet band across the blue top. The first try read as the core line coming back,
    so the band is twice as wide and twice as blurred.
  - The caustics: each piece's colour pooled on the ground under it. `clear-ground` adds a
    reflection to them.
