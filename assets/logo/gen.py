"""The yession mark, its intro, and every derived asset, from one description.

    python3 assets/logo/gen.py            # writes the SVGs beside this file
    python3 assets/logo/gen.py --png      # also the PNGs, through the Chromium at $CHROME

The mark is a blue cube — the agent — with two green half-depth panels — the people — hugging
its near faces, the hairline kerfs between them forming a Y; a pinhole lens two block-widths
away, aimed two above the centre; and a jelly material: a translucent body over its own back
faces, a hot rim, a fixed lamp reflected in the top plane, a little turbidity, a bloom. The
intro is the same scene sampled along a clock; the logo IS its last frame, so the two cannot
disagree. The design record is docs/brand/README.md. Requires fontTools (for the lockup's
wordmark, cut from the Noto Sans the product ships) and nothing else.
"""
import math, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
FONT = os.path.join(REPO, "app", "fonts", "noto-sans-latin-%d-normal.woff2")

# ---- palette ---------------------------------------------------------------------------------
G, B = "#a8dd00", "#1ba1e2"                      # the product's green and blue
G_HOT, B_HOT = "#d8f54a", "#6fd8ff"              # each hue run hotter, never to white (Zune)
G_DEEP, B_DEEP = "#6f9a00", "#0f6ea3"            # each hue in shadow
INK, INK_LIGHT = "#ffffff", "#14150f"            # the wordmark on black, and on paper

def mix(h, k, toward=(0, 0, 0)):
    r, g, b = int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16)
    f = lambda c, t: max(0, min(255, round(c*k + t*(1-k))))
    return f"#{f(r,toward[0]):02x}{f(g,toward[1]):02x}{f(b,toward[2]):02x}"
def dot(a, b): return sum(x*y for x, y in zip(a, b))
def norm(a):
    L = math.sqrt(dot(a, a)) or 1e-9; return tuple(x/L for x in a)
LIGHT = norm((-0.35, 0.55, 0.75))                # the diffuse key: what shades a side
def shade(col, nrm):
    k = max(0.0, dot(nrm, LIGHT))
    return col if k > 0.7 else (mix(col, 0.86) if k > 0.4 else mix(col, 0.66))

# ---- svg atoms -------------------------------------------------------------------------------
def n(v):
    t = f"{v:.2f}".rstrip("0").rstrip(".")
    return t if t != "-0" else "0"
def pathd(poly): return "M" + " L".join(f"{n(x)} {n(y)}" for x, y in poly) + " Z"
def polyline(pts): return "M" + " L".join(f"{n(x)} {n(y)}" for x, y in pts)
def blur(key, sd):
    return (f'<filter id="{key}" filterUnits="userSpaceOnUse" x="-40" y="-40" width="144" '
            f'height="144"><feGaussianBlur stdDeviation="{n(sd)}"/></filter>')
def svg(body, defs="", label="yession", box="0 0 64 64", extra=""):
    defs = f"\n  <defs>{defs}</defs>" if defs else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{box}" role="img" aria-label="{label}"{extra}>'
            f'{defs}\n  {body}\n</svg>\n')

# ---- the object ------------------------------------------------------------------------------
def ccw(poly):
    area = sum(x0*y1 - x1*y0 for (x0, y0), (x1, y1) in zip(poly, poly[1:] + poly[:1]))
    return poly if area > 0 else poly[::-1]
def prism_faces(poly, z0, z1):
    poly = ccw(poly)
    faces = [((0, 0, 1), [(x, y, z1) for x, y in poly]), ((0, 0, -1), [(x, y, z0) for x, y in poly])]
    for (x0, y0), (x1, y1) in zip(poly, poly[1:] + poly[:1]):
        dx, dy = x1-x0, y1-y0; L = math.hypot(dx, dy)
        faces.append(((dy/L, -dx/L, 0.0), [(x0, y0, z0), (x1, y1, z0), (x1, y1, z1), (x0, y0, z1)]))
    return faces
def plan(g=0.11, d=0.5):
    """The footprint: the cube in the far quadrant, a panel on each near face, kerf g between."""
    a = 1.0 + g/2
    cube = [(-g/2, -g/2), (-a, -g/2), (-a, -a), (-g/2, -a)]
    pl = [(-g/2, g/2), (-g/2, g/2 + d), (-a, g/2 + d), (-a, g/2)]
    pr = [(y, x) for x, y in pl]
    return cube, pl, pr

H, GK, DD = 1.3, 0.11, 0.5                       # block height, kerf, panel depth (block = 1)
A = 1.0 + GK/2
TARGET = (-A/2, -A/2, H/2)                       # the cube's centre
DIST, UP1, PHI0, PHI1 = 2.0, 2.0, 90.0, 46.0     # the wide lens: eye 2 away, aimed 2 above, 46° up
LAMP = (-2.11, -1.42, 4.0)                       # one fixed point light, behind-left and high
CUBE, PL, PR = plan(GK, DD)
def prisms_at(delta):
    """The panels delta further out along their own faces; delta 0 is the mark."""
    return [(CUBE, 0.0, H, B), ([(x, y+delta) for x, y in PL], 0.0, H, G), ([(x+delta, y) for x, y in PR], 0.0, H, G)]

# ---- the camera ------------------------------------------------------------------------------
class Lens:
    """A pinhole: elevation phi over the 45° azimuth, dist from an aim point up above the target."""
    def __init__(self, phi, dist, up=0.0, theta=45.0, target=(0, 0, 0)):
        t, p = math.radians(theta), math.radians(phi)
        self.v = (math.cos(p)*math.cos(t), math.cos(p)*math.sin(t), math.sin(p))
        self.r = (math.sin(t), -math.cos(t), 0.0)
        self.u = (-math.sin(p)*math.cos(t), -math.sin(p)*math.sin(t), math.cos(p))
        aim = (target[0], target[1], target[2] + up)
        self.eye = tuple(aim[i] + dist*self.v[i] for i in range(3))
        self.s, self.cx, self.cy = 20.0, 32.0, 32.0
    def project(self, p):
        q = tuple(p[i] - self.eye[i] for i in range(3))
        x, y, z = dot(q, self.r), dot(q, self.u), -dot(q, self.v)
        return (self.cx + x/z*self.s, self.cy - y/z*self.s)
    def depth(self, p): return -sum((p[i]-self.eye[i])**2 for i in range(3))
    def visible(self, nrm, cen):
        return dot(nrm, tuple(self.eye[i] - cen[i] for i in range(3))) > 1e-6
def fit(cam, prisms, pad=5.0):
    """Scale and centre the lens so the prisms' silhouette fills the 64 box less pad."""
    def pts():
        return [cam.project((x, y, z)) for poly, z0, z1, _ in prisms for x, y in poly for z in (z0, z1)]
    for _ in range(3):
        p = pts(); xs = [q[0] for q in p]; ys = [q[1] for q in p]
        cam.s *= (64-2*pad)/max(max(xs)-min(xs), max(ys)-min(ys))
        p = pts(); xs = [q[0] for q in p]; ys = [q[1] for q in p]
        cam.cx += 32 - (min(xs)+max(xs))/2; cam.cy += 32 - (min(ys)+max(ys))/2
    return cam
def highlight(eye, z=H):
    """Where the lamp reflects into the eye off the plane at z: the eye's line to the lamp's mirror image."""
    Lm = (LAMP[0], LAMP[1], 2*z - LAMP[2])
    s_ = (eye[2] - z)/(eye[2] - Lm[2])
    return tuple(eye[i] + s_*(Lm[i] - eye[i]) for i in range(3))

# ---- the clock -------------------------------------------------------------------------------
def bez(t, x1=0.42, y1=0.0, x2=0.58, y2=1.0):
    """CSS's symmetric ease-in-out, solved for y at x = t."""
    t = min(1.0, max(0.0, t))
    X = lambda u: 3*(1-u)**2*u*x1 + 3*(1-u)*u*u*x2 + u**3
    Y = lambda u: 3*(1-u)**2*u*y1 + 3*(1-u)*u*u*y2 + u**3
    lo, hi = 0.0, 1.0
    for _ in range(40):
        mid = (lo+hi)/2
        if X(mid) < t: lo = mid
        else: hi = mid
    return Y((lo+hi)/2)
def ramp(t, a, b): return bez((t-a)/(b-a))

# ---- the intro, and the mark as its last frame ----------------------------------------------
class Intro:
    """Every keyframe is the same scene at one instant; the file carries them as baked SMIL values
    on uniform keyTimes, so the easing lives in the samples and nothing on the page knows the curve."""
    def __init__(self, N=48, dur=2.4, delta=0.1, panels=(0.3, 1.0), fx=(0.0, 0.4), camera=(0.08, 1.0),
                 clarity=0.6, backs=0.45, rim=1.3, spill=0.5, bloom=0.3, margin=3.0, start_pad=15.5, end_pad=7.0):
        self.__dict__.update({k: v for k, v in locals().items() if k != "self"})
        self.cam1 = fit(Lens(PHI1, DIST, UP1, target=TARGET), prisms_at(0.0), pad=end_pad)
        self.cam0 = fit(Lens(PHI0, DIST, 0.0, target=TARGET), prisms_at(0.0)[:1], pad=start_pad)
        self.w0, self.w1 = self.top_width(self.cam0), self.top_width(self.cam1)
        # the collaborators may only fade in while, at their far position, they are wholly inside
        # the frame with room for their glow: nothing arrives cropped
        def inside(t):
            c = self.rig(ramp(t, *camera))
            pts = [c.project((x, y, z)) for poly, z0, z1, _ in prisms_at(delta)[1:] for x, y in poly for z in (z0, z1)]
            return all(margin <= v <= 64 - margin for p in pts for v in p)
        grid = [k/400 for k in range(401)]; ok = [inside(t) for t in grid]
        self.gate = next((t for t, o in zip(grid, ok) if o and all(ok[grid.index(t):])), None)
        assert self.gate is not None and panels[0] >= self.gate, f"panels start at {panels[0]:.0%} but fit the frame only from {self.gate}"
    @staticmethod
    def top_width(c):
        top = [c.project((x, y, H)) for x, y in ((-A, -A), (-A, -GK/2), (-GK/2, -GK/2), (-GK/2, -A))]
        return max(p[0] for p in top) - min(p[0] for p in top)
    def rig(self, e):
        """The camera at tilt e. The eye's path is near a straight line in the world; the scale is
        solved per frame so the agent's width on screen changes evenly, or the pull reads as two moves."""
        c = Lens(PHI0 + (PHI1-PHI0)*e, DIST, UP1*e, target=TARGET)
        for k in ("cx", "cy"):
            setattr(c, k, getattr(self.cam0, k) + (getattr(self.cam1, k) - getattr(self.cam0, k))*e)
        c.s = 1.0
        c.s = (self.w0 + (self.w1 - self.w0)*e)/self.top_width(c)
        return c
    def frame(self, t):
        """One instant: everything the drawing needs, as numbers."""
        e, ep, ef = ramp(t, *self.camera), ramp(t, *self.panels), ramp(t, *self.fx)
        c = self.rig(e); prisms = prisms_at(self.delta*(1-ep)); P = c.project
        f = dict(E=ef, P=ep, flat=1-ef, faces=[], edges=[], grad={})
        for pi, (poly, z0, z1, col) in enumerate(prisms):
            for nrm, corners in prism_faces(poly, z0, z1):
                cen = tuple(sum(q[i] for q in corners)/4 for i in range(3))
                f["faces"].append((pi, nrm, [P(q) for q in corners], c.visible(nrm, cen)))
            poly = ccw(poly); back = []
            for (x0, y0), (x1, y1) in zip(poly, poly[1:] + poly[:1]):
                dx, dy = x1-x0, y1-y0; L = math.hypot(dx, dy)
                back.append(not c.visible((dy/L, -dx/L, 0.0), ((x0+x1)/2, (y0+y1)/2, (z0+z1)/2)))
            for i in range(4):
                (x0, y0), (x1, y1) = poly[i], poly[(i+1) % 4]
                f["edges"].append((pi, [P((x0, y0, z0)), P((x1, y1, z0))], back[i]))
                f["edges"].append((pi, [P((x0, y0, z0)), P((x0, y0, z1))], back[i] and back[i-1]))
        f["J"] = P((0, 0, H))
        f["hl"] = P(highlight(c.eye))
        top = f["faces"][0][2]
        f["hlr"] = 0.9*max(max(p[0] for p in top) - min(p[0] for p in top), max(p[1] for p in top) - min(p[1] for p in top))
        f["ks"] = c.s/self.cam1.s
        for i, (pi, nrm, pts, _) in enumerate(f["faces"]):
            if nrm[2] < 0.5: continue
            poly = ccw(prisms[pi][0]); Lx, Ly = LAMP[0] - TARGET[0], LAMP[1] - TARGET[1]
            order = sorted(range(4), key=lambda k: (poly[k][0] - TARGET[0])*Lx + (poly[k][1] - TARGET[1])*Ly)
            f["grad"][i] = P((*poly[order[-1]], H)) + P((*poly[order[0]], H))
        return f
    def build(self, ts, key, static=False):
        """The drawing over the instants ts: an attribute where a value never changes, an <animate>
        over the frames otherwise. A static frame carries nothing invisible in it."""
        fr = [self.frame(t) for t in ts]
        kt = ";".join(n(t) for t in ts)
        beg = f'begin="0s;{key}.click"'
        def an(attr, vals, discrete=False):
            vals = [v if isinstance(v, str) else n(v) for v in vals]
            if static or all(v == vals[0] for v in vals): return f' {attr}="{vals[0]}"', ""
            return "", (f'<animate attributeName="{attr}" values="{";".join(vals)}" keyTimes="{kt}" '
                        f'calcMode="{"discrete" if discrete else "linear"}" dur="{self.dur}s" {beg} fill="freeze"/>')
        def el(tag, attrs, anims, inner=""):
            s, a = "", ""
            for attr, vals, *disc in anims:
                s_, a_ = an(attr, vals, bool(disc and disc[0])); s += s_; a += a_
            if static and ' opacity="0"' in s: return ""
            return f'<{tag}{attrs}{s}>{a}{inner}</{tag}>' if (a or inner) else f'<{tag}{attrs}{s}/>'
        def vis(i): return [("opacity", ["1" if f["faces"][i][3] else "0" for f in fr], True)]
        def gate(which): return [("opacity", [f[which] for f in fr])]
        def use(i, fill):
            if not any(f["faces"][i][3] for f in fr): return ""
            return el("use", f' href="#{key}-p{i}" fill="{fill}"', vis(i))
        nf = len(fr[0]["faces"]); prism = lambda i: fr[0]["faces"][i][0]; nrm = lambda i: fr[0]["faces"][i][1]
        col = lambda i: B if prism(i) == 0 else G
        hot = lambda i: B_HOT if prism(i) == 0 else G_HOT
        deep = lambda i: B_DEEP if prism(i) == 0 else G_DEEP
        defs = [el("path", f' id="{key}-p{i}"', [("d", [pathd(f["faces"][i][2]) for f in fr])]) for i in range(nf)]
        pcl = lambda pi: "".join(f'<use href="#{key}-p{i}"/>' for i in range(nf) if prism(i) == pi)
        defs += [f'<clipPath id="{key}-c{pi}">{pcl(pi)}</clipPath>' for pi in range(3)]
        for i in range(nf):
            if nrm(i)[2] > 0.5:
                defs.append(el("linearGradient", f' id="{key}-f{i}" gradientUnits="userSpaceOnUse"',
                               [(k, [f["grad"][i][j] for f in fr]) for j, k in enumerate(("x1", "y1", "x2", "y2"))],
                               f'<stop offset="0" stop-color="{hot(i)}"/><stop offset="0.5" stop-color="{col(i)}"/>'
                               f'<stop offset="1" stop-color="{mix(col(i), 0.9)}"/>'))
            else:
                c_ = shade(col(i), nrm(i))
                defs.append(f'<linearGradient id="{key}-f{i}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{c_}"/>'
                            f'<stop offset="0.55" stop-color="{mix(c_, 0.92)}"/><stop offset="1" stop-color="{deep(i)}"/></linearGradient>')
        for tag, hc in (("g", G_HOT), ("b", B_HOT)):
            defs.append(f'<filter id="{key}-rim{tag}" filterUnits="userSpaceOnUse" x="-8" y="-8" width="80" height="80">'
                        f'<feFlood flood-color="{hc}" flood-opacity="{min(1.0, 0.9*self.rim):.2f}" result="f"/>'
                        f'<feComposite in="f" in2="SourceAlpha" operator="out" result="o"/>'
                        f'<feGaussianBlur in="o" stdDeviation="{n(1.3*self.rim)}" result="b"/>'
                        f'<feComposite in="b" in2="SourceAlpha" operator="in" result="rim"/>'
                        f'<feBlend in="rim" in2="SourceGraphic" mode="screen"/></filter>')
        defs.append(f'<filter id="{key}-tb" filterUnits="userSpaceOnUse" x="0" y="0" width="64" height="64">'
                    f'<feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" seed="7" result="t"/>'
                    f'<feColorMatrix in="t" type="matrix" values="0.33 0.33 0.33 0 0 0.33 0.33 0.33 0 0 0.33 0.33 0.33 0 0 0 0 0 0 0.22" result="grey"/>'
                    f'<feComposite in="grey" in2="SourceAlpha" operator="in" result="tf"/>'
                    f'<feBlend in="tf" in2="SourceGraphic" mode="overlay"/></filter>')
        defs.append(blur(f"{key}-bl", 3.6)); defs.append(blur(f"{key}-hb", 0.45))
        Pg = lambda inner: el("g", "", gate("P"), inner)          # the panels' own arrival
        Eg = lambda inner: el("g", "", gate("E"), inner)          # the material's arrival
        body = ""
        # bloom
        body += Eg(f'<g filter="url(#{key}-bl)" opacity="{self.bloom}">'
                   + "".join(f'<use href="#{key}-p{i}" fill="{col(i)}"/>' for i in range(nf) if prism(i) == 0)
                   + Pg("".join(f'<use href="#{key}-p{i}" fill="{col(i)}"/>' for i in range(nf) if prism(i) != 0)) + '</g>')
        # the back faces, deep, under the body: what makes it see-through
        bk = lambda pi: "".join(el("use", f' href="#{key}-p{i}" fill="{deep(i)}"',
                                   [("opacity", ["0" if f["faces"][i][3] else "1" for f in fr], True)])
                                for i in range(nf) if prism(i) == pi and not all(f["faces"][i][3] for f in fr))
        body += Eg(f'<g opacity="{self.backs}">{bk(0)}{Pg(bk(1) + bk(2))}</g>')
        # the pieces: each a rimmed, translucent group, under one turbidity
        piece = lambda pi: (f'<g filter="url(#{key}-rim{"b" if pi == 0 else "g"})" opacity="{self.clarity}">'
                            + "".join(use(i, f"url(#{key}-f{i})") for i in range(nf) if prism(i) == pi) + '</g>')
        body += f'<g filter="url(#{key}-tb)">{piece(0)}{Pg(piece(1) + piece(2))}</g>'
        # hidden edges, soft, in the hot colour
        def edges(pi):
            out = ""
            for j, (q, _, _) in enumerate(fr[0]["edges"]):
                if q != pi or not any(f["edges"][j][2] for f in fr): continue
                out += el("path", f' stroke="{B_HOT if pi == 0 else G_HOT}" stroke-width="0.6" fill="none" stroke-linecap="round"',
                          [("d", [polyline(f["edges"][j][1]) for f in fr]),
                           ("opacity", ["1" if f["edges"][j][2] else "0" for f in fr], True)])
            return f'<g clip-path="url(#{key}-c{pi})" filter="url(#{key}-hb)" opacity="0.3">{out}</g>'
        body += Eg(edges(0) + Pg(edges(1) + edges(2)))
        # the blue through the greens
        cube = "".join(use(i, B_HOT) for i in range(nf) if prism(i) == 0 and nrm(i)[2] < 0.5)
        body += Eg(Pg(f'<g clip-path="url(#{key}-c1)" opacity="0.2">{cube}</g><g clip-path="url(#{key}-c2)" opacity="0.2">{cube}</g>'))
        # the spill at the junction, split so the panels' share arrives with them
        defs.append(el("radialGradient", f' id="{key}-sp" gradientUnits="userSpaceOnUse"',
                       [("cx", [f["J"][0] for f in fr]), ("cy", [f["J"][1] for f in fr]), ("r", [26*f["ks"] for f in fr])],
                       f'<stop offset="0" stop-color="#fff" stop-opacity="{self.spill}"/><stop offset="0.3" stop-color="#bfeaff" stop-opacity="{self.spill*0.4:.2f}"/>'
                       f'<stop offset="1" stop-color="#7fd0f5" stop-opacity="0"/>'))
        spl = lambda c_: f'<g clip-path="url(#{key}-{c_})"><rect width="64" height="64" fill="url(#{key}-sp)"/></g>'
        body += Eg(spl("c0") + Pg(spl("c1") + spl("c2")))
        # the solid start, shaded per face so the form is there the moment a side shows; gone by the end
        flat = lambda pi: "".join(use(i, col(i) if nrm(i)[2] > 0.5 else shade(col(i), nrm(i))) for i in range(nf) if prism(i) == pi)
        body += el("g", "", gate("flat"), flat(0) + Pg(flat(1) + flat(2)))
        # the lamp: one highlight on the shared top plane, clipped into whichever tops it falls
        # across, above everything because it is not part of the material
        defs.append(el("radialGradient", f' id="{key}-sh" gradientUnits="userSpaceOnUse"',
                       [("cx", [f["hl"][0] for f in fr]), ("cy", [f["hl"][1] for f in fr]), ("r", [f["hlr"] for f in fr])],
                       '<stop offset="0" stop-color="#fff" stop-opacity="0.42"/><stop offset="0.45" stop-color="#fff" stop-opacity="0.12"/>'
                       '<stop offset="1" stop-color="#fff" stop-opacity="0"/>'))
        for i in range(nf):
            if nrm(i)[2] < 0.5: continue
            defs.append(f'<clipPath id="{key}-tc{i}"><use href="#{key}-p{i}"/></clipPath>')
            r_ = f'<g clip-path="url(#{key}-tc{i})"><rect width="64" height="64" fill="url(#{key}-sh)"/></g>'
            body += r_ if prism(i) == 0 else Pg(r_)
        return body, "".join(defs)
    def intro(self, key="intro"):
        body, defs = self.build([k/self.N for k in range(self.N + 1)], key)
        return svg(body, defs, "yession", extra=f' id="{key}"')
    def mark(self, key="mark", label="yession"):
        body, defs = self.build([1.0], key, static=True)
        return svg(body, defs, label)

# ---- derived assets --------------------------------------------------------------------------
def inner(s):
    """An svg's defs and body, for nesting."""
    return re.search(r"<svg[^>]*>(.*)</svg>", s, re.S).group(1).strip()

WEIGHT = 400     # the product's wordmark is 200; beside a mark this dense, 300 still read thin
def wordmark(key, em, x, baseline, ink, weight=WEIGHT):
    """'yession' in Noto Sans at −0.02em, as paths; returns (markup, advance). The face is the one
    the product ships, read where it lives."""
    from fontTools.ttLib import TTFont
    from fontTools.pens.svgPathPen import SVGPathPen
    f = TTFont(FONT % weight); gs = f.getGlyphSet(); cmap = f.getBestCmap(); k = em/f["head"].unitsPerEm
    out, pen_x = "", x
    for ch in "yession":
        g = gs[cmap[ord(ch)]]; pen = SVGPathPen(gs, ntos=lambda v: n(v)); g.draw(pen)
        out += (f'<path transform="translate({n(pen_x)} {n(baseline)}) scale({n(k)} {n(-k)})" fill="{ink}" d="{pen.getCommands()}"/>')
        pen_x += g.width*k - 0.02*em
    return f'<g id="{key}-word">{out}</g>', pen_x - x + 0.02*em

def lockup(mark_svg, ink, label, weight=WEIGHT):
    """The mark and the wordmark on one line: the x-height band centred on the mark's optical middle."""
    em = 32.0; word, adv = wordmark("lockup", em, 70.0, 40.5, ink, weight)
    w = 70.0 + adv + 4.0
    return svg(f'<g>{inner(mark_svg).replace("mark-", "lockup-")}</g>{word}', "", label, box=f"0 0 {n(w)} 64")

def small(cam, g=0.2, pad=1.0):
    """The mark for 16px: the same object with the kerfs cut twice as wide so the Y survives a
    quarter-pixel (three times read as a gap), flat faces in the diffuse shading, nothing else — a
    filter has no room to work."""
    cube, pl, pr = plan(g, DD)
    prisms = [(cube, 0.0, H, B), (pl, 0.0, H, G), (pr, 0.0, H, G)]
    a = 1.0 + g/2
    c = fit(Lens(PHI1, DIST, UP1, target=(-a/2, -a/2, H/2)), prisms, pad=pad)
    faces = []
    for poly, z0, z1, col in prisms:
        for nrm, corners in prism_faces(poly, z0, z1):
            cen = tuple(sum(q[i] for q in corners)/4 for i in range(3))
            if c.visible(nrm, cen): faces.append((c.depth(cen), [c.project(q) for q in corners], col if nrm[2] > 0.5 else shade(col, nrm)))
    faces.sort(key=lambda f: f[0])
    return svg("".join(f'<path d="{pathd(p)}" fill="{col}"/>' for _, p, col in faces), "", "yession")

def icon(mark_svg):
    """The app icon: the mark on the product's black, in a 1024 box with the corners a platform
    will mask anyway rounded to 22%; the mark at 72% so the bloom has its room."""
    return svg(f'<rect width="1024" height="1024" rx="228" fill="#000"/>'
               f'<svg x="144" y="144" width="736" height="736" viewBox="0 0 64 64">{inner(mark_svg).replace("mark-", "icon-")}</svg>',
               "", "yession", box="0 0 1024 1024")

# ---- rasters ---------------------------------------------------------------------------------
# iOS takes only PNG for a home-screen icon and the manifest wants sizes, so the icon is also
# rasterised, through Chromium because that is the renderer the product is looked at in. A
# small window is not painted whole by headless Chromium, so the page is rendered inside a
# larger one and the PNG cropped here; PNG is simple enough to crop without a library.
def png_crop(data, w, h):
    import struct, zlib
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, chunks, idat = 8, [], b""
    while pos < len(data):
        ln, = struct.unpack(">I", data[pos:pos+4]); typ = data[pos+4:pos+8]; body = data[pos+8:pos+8+ln]
        if typ == b"IHDR": W, Hh, depth, ctype = struct.unpack(">IIBB", body[:10]); assert depth == 8 and ctype == 6
        if typ == b"IDAT": idat += body
        pos += 12 + ln
    raw = zlib.decompress(idat); bpp, stride = 4, W*4
    rows, prev, off = [], bytearray(stride), 0
    for _ in range(Hh):
        ft = raw[off]; line = bytearray(raw[off+1:off+1+stride]); off += 1 + stride
        for i in range(stride):
            a = line[i-bpp] if i >= bpp else 0; b = prev[i]; c = prev[i-bpp] if i >= bpp else 0
            if ft == 1: line[i] = (line[i] + a) & 255
            elif ft == 2: line[i] = (line[i] + b) & 255
            elif ft == 3: line[i] = (line[i] + (a + b)//2) & 255
            elif ft == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2*c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(bytes(line)); prev = line
    out = b"".join(b"\x00" + r[:w*4] for r in rows[:h])
    def chunk(t, b): return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(out, 9)) + chunk(b"IEND", b""))

def rasterise(svg_text, px, path):
    import subprocess, tempfile
    chrome = os.environ.get("CHROME", "chromium")
    s = re.sub(r"<svg ", f'<svg width="{px}" height="{px}" ', svg_text, count=1)
    with tempfile.TemporaryDirectory() as d:
        page = os.path.join(d, "x.html"); shot = os.path.join(d, "x.png")
        open(page, "w").write('<style>html,body{margin:0;background:transparent}svg{display:block}</style>' + s)
        subprocess.run([chrome, "--headless=new", "--no-sandbox", "--hide-scrollbars", "--default-background-color=00000000",
                        "--virtual-time-budget=2000", f"--screenshot={shot}", f"--window-size={px+400},{px+400}", page],
                       check=True, capture_output=True)
        open(path, "wb").write(png_crop(open(shot, "rb").read(), px, px))

if __name__ == "__main__":
    I = Intro()
    out = {}
    out["intro.svg"] = I.intro()
    out["logo.svg"] = I.mark()
    # on paper the black no longer darkens the body's middle, so the light variant is denser,
    # and a bloom that is light on black is a smudge on white, so it is all but gone
    out["logo-light.svg"] = Intro(clarity=0.82, backs=0.55, bloom=0.1).mark()
    out["lockup.svg"] = lockup(out["logo.svg"], INK, "yession")
    out["lockup-light.svg"] = lockup(out["logo-light.svg"], INK_LIGHT, "yession")
    out["logo-16.svg"] = small(I.cam1)
    out["icon.svg"] = icon(out["logo.svg"])
    for name, s in out.items():
        open(os.path.join(HERE, name), "w").write(s)
        print(f"{name:18} {len(s.encode()):7} bytes")
    print(f"intro: {I.N} keyframes over {I.dur}s, panels from {I.panels[0]:.0%} (fit the frame from {I.gate:.0%})")
    if "--png" in sys.argv:
        for src, sizes in (("icon.svg", (1024, 512, 192, 180)), ("logo-16.svg", (32, 16))):
            for px in sizes:
                name = f"{'icon' if src == 'icon.svg' else 'favicon'}-{px}.png"
                rasterise(out[src], px, os.path.join(HERE, name)); print(f"{name:18} {os.path.getsize(os.path.join(HERE, name)):7} bytes")
