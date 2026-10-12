# The yession mark

`gen.py` generates everything here from one description of the object. Its docstring describes
the object: a blue cube (the agent) with two green half-depth panels (the people) hugging its
near faces. The hairline kerfs between the pieces form a Y. A wide pinhole lens sees the
object, which is made of translucent jelly on black. On black, the agent's own sides are taken
down a fifth against its top. The Y is the two kerfs and the cube's near vertical corner, and
the eye reads all three off that step. The design record, and every step that led here, is
`docs/brand/README.md`.

    python3 assets/logo/gen.py            # the SVGs
    CHROME=<chromium> python3 assets/logo/gen.py --png   # and the PNGs, through Chromium

The mark and the intro cannot disagree. `logo.svg` is the intro's last frame, built as a static
file from the same code at t = 1. Regenerate everything together. Never hand-edit one file.

| file | what it is for |
| --- | --- |
| `logo.svg` | The mark, for black and dark surfaces. The box is 64 units, and the object sits in it with 7 units of air so that the bloom has room. Pure SVG 1.1: paths, gradients, clip paths, and filter primitives, with no script and no raster. |
| `logo-light.svg` | The mark for paper and light surfaces. The geometry is the same. The body is denser (0.82 over 0.55), because white no longer darkens its middle. The bloom is all but gone (0.1 against 0.3), because a glow that is light on black is a smudge on white. The agent keeps its own shading, because on paper its sides already read against the ground. |
| `lockup.svg`, `lockup-light.svg` | The mark beside the wordmark. The wordmark is `yession` in Noto Sans 400 at −0.02em, cut to paths from the face the product ships, so the file carries no font. It is two steps heavier than the product's 200 wordmark, because beside a mark this dense both 200 and 300 read thin. It has no full stop. The mark is lifted two units, because its mass sits low and the apex is a point. The x-height band is centred on where the lifted mark then reads. |
| `logo-16.svg` | The mark for 16px. It is the same object with three changes. The kerfs are cut twice as wide, so the Y survives a quarter-pixel (three times as wide reads as a gap). The faces are flat, in the diffuse shading. There is no filter, because a filter has no room to work at that size. Use it as the SVG favicon and for anything under 24px. |
| `favicon-32.png`, `favicon-16.png` | `logo-16.svg` rasterised, for a browser that takes only PNG. |
| `icon.svg` | The app icon: the mark on off-black (`#111`, the product's surface token) in a 1024 box. Corners are at 22%, and a platform masks its own shape over this anyway. The mark's box is 84% of the icon. The mark's own 7 units of air in 64 put the object at about two thirds of the side. Past 86%, the panels' tips meet the rounded corner. The mark sits 2% above centre, because its weight sits low. |
| `icon-1024.png`, `icon-512.png`, `icon-192.png`, `icon-180.png` | The icon rasterised: the manifest's sizes, and 180 for `apple-touch-icon`. iOS takes only PNG for a home-screen icon. |
| `intro.svg` | The mark's intro: 2.4s, one SVG, no script. A top-down view shows the agent as a solid blue diamond. It turns to jelly where it stands. Its bottom edges show through, so it is a block before it moves. The camera then pulls up and back into the wide lens while the collaborators slide in. It is SMIL with the easing (CSS ease-in-out) baked into 49 keyframes. Click to replay. SMIL cannot read `prefers-reduced-motion`, so a page that respects it shows `logo.svg` instead. The file is about 97 KB, or 15 KB gzipped. Only 18 `d` attributes morph, one per face, because interpolating a `d` is the most expensive thing SMIL does per frame. The hidden edges are strokes on the faces already turned away. This is worth about a sixth of the frame rate on a phone-class CPU. There, this is the one asset that has to animate before anything else can happen. The eye starts three times further out and comes in. `docs/brand/README.md` explains why. |

The blue `#1ba1e2` and the green `#a8dd00` are the colours for anything that must match the mark
without embedding it. Each runs to a hotter self, never to white: `#6fd8ff` and `#d8f54a`. Each
also runs to a deep shade: `#0f6ea3` and `#6f9a00`. The blue and the green are the product's
`--color-blue` and `--color-green`.

Type: the wordmark is Noto Sans, which is the product's chrome and the agent's voice. A person's
words (messages, drafts, the composer) are Source Serif 4 at 15px/350 (`--font-human` in
`app/tailwind.css`, vendored in `app/fonts/`). The typeface therefore says who is speaking.
`docs/brand/README.md` explains why the serif and why 350.
