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
