# The yession mark

`gen.py` generates everything here. Its docstring describes the object and the commands.
[`docs/brand/README.md`](../../docs/brand/README.md) is the design record. This file lists the
files.

    python3 assets/logo/gen.py            # the SVGs
    CHROME=<chromium> python3 assets/logo/gen.py --png   # and the PNGs, through Chromium

`logo.svg` is the intro's last frame, so regenerate everything together. Never hand-edit one
file.

| file | what it is for |
| --- | --- |
| `logo.svg` | The mark, for black and dark surfaces. The box is 64 units, and the object sits in it with 7 units of air so that the bloom has room. Pure SVG 1.1: paths, gradients, clip paths, and filter primitives, with no script and no raster. |
| `logo-light.svg` | The mark for paper and light surfaces. The geometry is the same. The body is denser (0.82 over 0.55), because white no longer darkens its middle. The bloom is all but gone (0.1 against 0.3), because a glow that is light on black is a smudge on white. The agent keeps its own shading, because on paper its sides already read against the ground. |
| `lockup.svg`, `lockup-light.svg` | The mark beside the wordmark. The wordmark is `yession` in Noto Sans 400 at −0.02em, cut to paths from the face the product ships, so the file carries no font. It is two steps heavier than the product's 200 wordmark, because beside a mark this dense both 200 and 300 read thin. It has no full stop. The file lifts the mark two units, because its mass sits low and the apex is a point. The x-height band then centres on where the lifted mark reads. |
| `logo-16.svg` | The mark for 16px. It is the same object with three changes. The kerfs are cut twice as wide, so the Y survives a quarter-pixel (three times as wide reads as a gap). The faces are flat, in the diffuse shading. There is no filter, because a filter has no room to work at that size. Use it as the SVG favicon and for anything under 24px. |
| `favicon-32.png`, `favicon-16.png` | `logo-16.svg` rasterised, for a browser that takes only PNG. |
| `icon.svg` | The app icon: the mark on off-black (`#111`, the product's surface token) in a 1024 box. Corners are at 22%, and a platform masks its own shape over this anyway. The mark's box is 84% of the icon. The mark's own 7 units of air in 64 put the object at about two thirds of the side. Past 86%, the panels' tips meet the rounded corner. The mark sits 2% above centre, because its weight sits low. |
| `icon-1024.png`, `icon-512.png`, `icon-192.png`, `icon-180.png` | The icon rasterised: the manifest's sizes, and 180 for `apple-touch-icon`. iOS takes only PNG for a home-screen icon. |
| `intro.svg` | The mark's intro: 2.4s, one SVG, no script. Click to replay. A page that respects `prefers-reduced-motion` shows `logo.svg` instead, because SMIL cannot read it. About 97 KB, or 14 KB gzipped. On a phone-class CPU it is the one asset that has to animate before anything else can happen. [The intro](../../docs/brand/README.md#the-intro) gives the clocks, the dolly and the frame-rate measurements. |

To match the mark without embedding it, use the blue `#1ba1e2` and the green `#a8dd00`, the
product's `--color-blue` and `--color-green`. Their hot and deep shades are under
[Material](../../docs/brand/README.md#material) (Hue). The product's faces are under
[Type](../../docs/brand/README.md#type).
