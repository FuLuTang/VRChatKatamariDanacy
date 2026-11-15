"""Fallen leaf - 9 x 6 x 0.1 cm leaf blade with three curled spots.

PROP_SET_PLAN.md section 3.4 asks for ``9x6x0.1 cm 薄片，5 条叶脉浅凹，边缘 3
处微卷``.  The blade is one closed thin slab: a 6x6 grid mapped from (u, v) to a
pointed leaf outline (a narrow 1.6 mm petiole for the first 18% of the length,
then a 6.0 cm wide blade tapering to a 1.6 mm tip over a full half-sine, which
keeps the tip from ending in one blunt 1.5 cm facet), with a sampled top height
field, a bottom surface offset by the local thickness, and a rim connecting the
two.

Scale honesty: at 1 mm thick, five radial veins are below what a 512 px preview
can resolve, and a 6-across grid could only carry one.  The leaf therefore keeps
the cupped blade, a raised midrib and the three edge curls (two up, one down,
which is what shows in the silhouette) and drops the other four veins; that is a
recorded deviation, not a claim that the veins are there.

The slab is closed by construction (no boolean), so the mesh is one watertight
shell by topology rather than by a solver.
"""

import math

import bmesh

ID = 'fallen_leaf'
NAME_ZH = '落叶'
CATEGORY = 'nature'
CSV_REAL_LONGEST_M = 0.09
CSV_GAME_LONGEST_M = 0.10
SOURCING = 'model ourselves：9×6×0.1 cm 薄片，边缘微卷（CSV 提示 Kenney 叶片）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.09
TRI_BUDGET = 200
MATERIALS = [
    {'key': 'leaf', 'name_zh': '落叶橄榄绿', 'linear_rgb': [0.150, 0.220, 0.055],
     'roughness': 0.72},
]
NOTES = ('6 x 6 grid slab: 72 top + 72 bottom + 48 rim = 192 triangles.  Length '
         '0.090 m along Y, blade width 0.057 m (plus a 3 mm bend, so X is about '
         '0.060 m), thickness 0.45-0.70 mm, overall Z about 0.9 mm.  The petiole '
         'is part of the same outline (a 1.6 mm wide tail), so there is no '
         'second object and no boolean.  Three edge curls (up at v=0.30 and '
         '0.58, down at 0.86) and a raised midrib are modelled; the other four '
         'of the five planned veins are omitted because they are sub-pixel at '
         'this thickness.')

NU = 6                  # across the blade
NV = 6                  # along the length
LENGTH = 0.090
STEM_END = 0.18         # fraction of the length that is petiole
STEM_HALF = 0.0008
BLADE_HALF = 0.0313
BEND = 0.0030
THICK_CENTRE = 0.00070
THICK_EDGE = 0.00045
BASE_Z = 0.00035
CUP = 0.00022
MIDRIB = 0.00006
CURLS = [(0.30, 0.00040), (0.58, 0.00045), (0.86, -0.00030)]


def half_width(v):
    t = (v - STEM_END) / (1.0 - STEM_END)
    if t <= 0.0:
        return STEM_HALF
    return max(STEM_HALF, BLADE_HALF * math.sin(math.pi * t))


def plan(u, v):
    return (u * half_width(v) + BEND * v * v, -0.5 * LENGTH + LENGTH * v)


def top_z(u, v):
    z = BASE_Z + CUP * (1.0 - u * u)
    if abs(u) < 0.2:
        z += MIDRIB * (1.0 - abs(u) / 0.2)
    for vc, lift in CURLS:
        z += lift * math.exp(-((v - vc) / 0.10) ** 2) * u * u
    return z


def thickness(u):
    return THICK_EDGE + (THICK_CENTRE - THICK_EDGE) * (1.0 - u * u)


def build(b):
    bm = bmesh.new()
    top_rows, bot_rows = [], []
    for j in range(NV + 1):
        v = float(j) / NV
        row_t, row_b = [], []
        for i in range(NU + 1):
            u = -1.0 + 2.0 * float(i) / NU
            x, y = plan(u, v)
            zt = top_z(u, v)
            row_t.append(bm.verts.new((x, y, zt)))
            row_b.append(bm.verts.new((x, y, zt - thickness(u))))
        top_rows.append(row_t)
        bot_rows.append(row_b)

    for j in range(NV):
        for i in range(NU):
            bm.faces.new((top_rows[j][i], top_rows[j][i + 1],
                          top_rows[j + 1][i + 1], top_rows[j + 1][i]))
            bm.faces.new((bot_rows[j][i], bot_rows[j + 1][i],
                          bot_rows[j + 1][i + 1], bot_rows[j][i + 1]))
    for i in range(NU):        # stem end and tip end rims
        bm.faces.new((top_rows[0][i], bot_rows[0][i], bot_rows[0][i + 1],
                      top_rows[0][i + 1]))
        bm.faces.new((top_rows[NV][i + 1], bot_rows[NV][i + 1],
                      bot_rows[NV][i], top_rows[NV][i]))
    for j in range(NV):        # left and right blade rims
        bm.faces.new((top_rows[j][0], top_rows[j + 1][0], bot_rows[j + 1][0],
                      bot_rows[j][0]))
        bm.faces.new((top_rows[j][NU], bot_rows[j][NU], bot_rows[j + 1][NU],
                      top_rows[j + 1][NU]))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    b.add(bm, 'leaf', (0.150, 0.220, 0.055), 0.72, smooth=False)
