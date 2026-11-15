"""Straw tuft - 13 x 8 x 6 cm sheaf of dry straw with three visible bundles.

DungBeetleMeadow catalogue t1: 0.130 x 0.080 x 0.060 m, shape_class long,
308/352 tris, Z1 干草堆与围栏根.

The 2026-11-15 review asked for "3-4 clear lengthwise ridges / separate
bundles": the previous build carried three ridges sampled by 9 columns (three
samples each) at 18 % valley depth, so the surface read as one smooth fan.
This build spends 12 columns on three bundles:

* ridge(u) = 0.14 + 0.86 * ((1 + cos(3*pi*u)) / 2)^1.25, sampled by 12
  columns: each bundle gets a valley column, two shoulder columns and a peak
  column, and the valleys fall to 14 % of the peak height - deep enough that
  the three bundles are separate ridges in the silhouette, not a wavy fan;
* the valleys are painted straw_dark while the ridge crowns stay straw, so the
  separation also survives flat shading (a geometry-only valley is invisible
  from the preview's three-quarter view);
* the sheaf is tied at the grip point: the band v = 0.10-0.34 is lifted 1.6 mm
  and those faces are ink, which reads as a dark twine wrap across all three
  bundles (in fan coordinates, so the band follows the fan instead of cutting a
  radius slice through it);
* the fan still opens from a 12.5 mm root arc to per-column tip radii of
  0.1225-0.1325 m, so the tips end at different lengths, and the rim tapers to
  3.4 mm.

Colour: straw straw (0.368), valleys straw_dark (0.171), twine ink (0.035).
"""

import math

import bmesh

ID = 'straw_tuft'
NAME_ZH = '草茎束'
CATEGORY = 'grass_plants'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t1 '
            '0.130×0.080×0.060 m 干草簇（Z1 围栏根草堆）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.130
TRI_BUDGET = 352
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.130', 'game_longest_dim_m': '0.130'}
STRAW = (0.470, 0.360, 0.140)
STRAW_DARK = (0.230, 0.165, 0.060)
INK = (0.030, 0.035, 0.045)
MATERIALS = [
    {'key': 'straw', 'name_zh': '干草土黄', 'linear_rgb': list(STRAW),
     'roughness': 0.90},
    {'key': 'straw_dark', 'name_zh': '草束阴影', 'linear_rgb': list(STRAW_DARK),
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '捆绳暗带', 'linear_rgb': list(INK),
     'roughness': 0.75},
]
NOTES = ('One closed fan slab, 12 columns x 5 rows: 120 top/bottom quads + 34 '
         'rim quads = 308 triangles, one shell, no boolean, no second shell.  '
         'Footprint a fan from a 0.0125 m root arc to per-column tip radii '
         '0.1225-0.1325 m.  Three bundles from cos(3*pi*u) ridges (valley 10 % '
         'of peak, broad crowns) sampled by 12 columns, plus 1.6 mm lift over fan v 0.10-0.34 '
         'band.  Top 0.0585 m at the grip point (v = 0.3), 4.5 mm at the loose '
         'ends, (1-u^2)^0.45 edge falloff to a 3.4 mm rim; underside flat at '
         'z = 0.0006.  finish() scales the 0.116 m fan to 0.130 m, which puts Y '
         'at 0.080 m and Z at 0.060 m.  Valleys straw_dark, tie band ink; both painted on the top surface only.')

NU = 12
NV = 5
THETA = 0.293
THETA_WOBBLE = 0.012
ROOT_R = 0.0125
TIP_BASE = 0.1270
TIP_SWING = 0.0100
FLOOR = 0.0006
EDGE_MIN = 0.0040
TIE_V = (0.10, 0.34)
TIE_LIFT = 0.0016


def theta(u):
    return u * (THETA + THETA_WOBBLE * math.cos(2.0 * math.pi * u))


def tip_radius(u):
    return TIP_BASE + TIP_SWING * math.sin(2.0 * math.pi * u)


def height(v):
    """Thick at the grip point (v = 0.3), thin at both loose ends."""
    base = 0.0045 + 0.063 * (math.sin(math.pi * min(1.0, v * 1.15)) ** 0.75) \
        * (1.0 - 0.45 * v)
    return base + 0.007 * math.exp(-((v / 0.08) ** 2))


def ridge(u):
    """Three bundles: broad crowns at u = 0, +-2/3, narrow deep valleys between."""
    return 0.10 + 0.90 * ((1.0 + math.cos(3.0 * math.pi * u)) * 0.5) ** 0.75


def edge(u):
    return max(0.0, 1.0 - u * u) ** 0.45


def plan(u, v):
    r = ROOT_R + v * (tip_radius(u) - ROOT_R)
    t = theta(u)
    return r * math.cos(t), r * math.sin(t)


def top(u, v):
    h = height(v)
    z = EDGE_MIN + (h - EDGE_MIN) * ridge(u) * edge(u)
    if TIE_V[0] <= v <= TIE_V[1]:          # raised twine wrap
        z += TIE_LIFT * edge(u)
    return z


def build(b):
    bm = bmesh.new()
    top_rows, bot_rows = [], []
    for j in range(NV + 1):
        v = float(j) / NV
        rt, rb = [], []
        for i in range(NU + 1):
            u = -1.0 + 2.0 * float(i) / NU
            x, y = plan(u, v)
            rt.append(bm.verts.new((x, y, top(u, v))))
            rb.append(bm.verts.new((x, y, FLOOR)))
        top_rows.append(rt)
        bot_rows.append(rb)

    for j in range(NV):
        for i in range(NU):
            bm.faces.new((top_rows[j][i], top_rows[j][i + 1],
                          top_rows[j + 1][i + 1], top_rows[j + 1][i]))
            bm.faces.new((bot_rows[j][i], bot_rows[j + 1][i],
                          bot_rows[j + 1][i + 1], bot_rows[j][i + 1]))
    for i in range(NU):        # root end and loose-end rims
        bm.faces.new((top_rows[0][i], bot_rows[0][i], bot_rows[0][i + 1],
                      top_rows[0][i + 1]))
        bm.faces.new((top_rows[NV][i + 1], bot_rows[NV][i + 1],
                      bot_rows[NV][i], top_rows[NV][i]))
    for j in range(NV):        # left and right fan rims
        bm.faces.new((top_rows[j][0], top_rows[j + 1][0], bot_rows[j + 1][0],
                      bot_rows[j][0]))
        bm.faces.new((top_rows[j][NU], bot_rows[j][NU], bot_rows[j + 1][NU],
                      top_rows[j + 1][NU]))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])

    def angle_of(centre):
        return math.atan2(centre.y, centre.x)

    def radius_of(centre):
        return math.hypot(centre.x, centre.y)

    def uv(centre):
        """Fan coordinates of a face centre (u across the fan, v along it)."""
        u = max(-1.0, min(1.0, angle_of(centre) / THETA))
        r = radius_of(centre)
        span = tip_radius(u) - ROOT_R
        v = (r - ROOT_R) / span if span > 1e-6 else 0.0
        return u, v

    def on_top(centre):
        return centre.z > 0.0012          # skip the flat underside grid

    def is_tie(centre):
        if not on_top(centre):
            return False
        _u, v = uv(centre)
        return TIE_V[0] <= v <= TIE_V[1]

    def is_valley(centre):
        if not on_top(centre) or is_tie(centre):
            return False
        u, _v = uv(centre)
        return ridge(u) < 0.35

    b.add(bm, 'straw', STRAW, 0.90, smooth=False, name='tuft',
          extras=[('ink', INK, 0.75, is_tie),
                  ('straw_dark', STRAW_DARK, 0.92, is_valley)])
