"""Dry leaf curl - 10 x 7.5 cm dead leaf with one margin rolled up.

DungBeetleMeadow catalogue t1: 0.100 x 0.075 x 0.025 m, shape_class flat,
212/224 tris, Z1 干草堆边 / Z4 果园落叶.

The previous build read as a curled strip of bark: the roll started at the
midrib so the whole leaf was a scoop, the outline was a plain ellipse and the
only colour break was a 12 %-long petiole.

This build keeps the pose (a dead leaf lying with one margin rolled) but gives
it a leaf:

* the inner 40 % of the blade stays flat (0.0034 m) and only the outer margin
  rolls - 20 mm on the +u side, 4 mm on the -u side - so the leaf has a flat
  face and a curled edge instead of being a trough;
* a 4.6 mm midrib FOLD lifts both halves of the blade off the crease, so the
  leaf reads as a folded dry leaf instead of a flat blob;
* the outline is a pointed-tip leaf (sin^0.7 along the length, 4 x 12 % wavy
  margin) with the first 12 % pinched to a 2.4 mm petiole that is part of the
  same closed outline - no separate stem, no second shell;
* the colour break is a real one: the outer grid column on the curled (+u)
  side, the rim and the petiole are wood_dark, so the leaf has a dark curled
  margin and a dark stalk against the leaf_dry blade.

Colour: blade leaf_dry (0.286), margin/petiole wood_dark (0.087, the dark slot).
"""

import math

import bmesh

ID = 'dry_leaf_curl'
NAME_ZH = '卷边枯叶'
CATEGORY = 'grass_plants'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t1 '
            '0.100×0.075×0.025 m 卷曲枯叶（Z1/Z4 落叶）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.100
TRI_BUDGET = 224
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.100', 'game_longest_dim_m': '0.100'}
LEAF_DRY = (0.400, 0.270, 0.105)
WOOD_DARK = (0.135, 0.078, 0.038)
MATERIALS = [
    {'key': 'leaf_dry', 'name_zh': '枯叶土黄', 'linear_rgb': list(LEAF_DRY),
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '枯叶暗边与叶柄', 'linear_rgb':
     list(WOOD_DARK), 'roughness': 0.75},
]
NOTES = ('5 x 8 grid slab (6 columns, 9 rows): 80 top+bottom quads + 26 rim '
         'quads = 212 triangles, one closed shell, no boolean.  Length 0.100 m '
         'along X, blade half-width 0.0350 m (Y 0.075 m) with a 4-tooth 12 % '
         'wavy margin, petiole 2.4 mm wide for the first 12 % of the length.  '
         'Top 0.0034 m at the midrib crease + 4.6 mm fold, +17 mm roll '
         'on the +u margin (starting at |u| = 0.40, so the inner blade is flat), '
         '+4 mm on the -u margin, +1.6 mm wave along the length; underside is '
         'the top minus 3.2 mm.  The outer columns, the rim and the petiole are '
         'wood_dark.  Overall 0.100 x 0.078 x 0.0255 m.')

NU = 5
NV = 8
LENGTH = 0.100
STEM_END = 0.12
STEM_HALF = 0.0012
BLADE_HALF = 0.0350
BASE = 0.0034
THICK = 0.0032
ROLL_RIGHT = 0.0170
ROLL_LEFT = 0.0045
WAVE = 0.0016
TIP_LIFT = 0.0035
MIDRIB = 0.0046        # midrib fold: the two halves angle up from the crease
TEETH = 4
TOOTH_AMP = 0.12


def half_width(v):
    if v <= STEM_END:
        return STEM_HALF
    t = (v - STEM_END) / (1.0 - STEM_END)
    wave = 1.0 + TOOTH_AMP * abs(2.0 * ((v * TEETH) % 1.0) - 1.0)
    return max(STEM_HALF, BLADE_HALF * math.sin(math.pi * t) ** 0.70 * wave)


def plan(u, v):
    return ((-0.5 + v) * LENGTH, u * half_width(v))


def top_z(u, v):
    z = BASE + MIDRIB * abs(u) ** 1.15
    z += ROLL_RIGHT * max(0.0, (u - 0.40) / 0.60) ** 2.0
    z += ROLL_LEFT * max(0.0, (-u - 0.55) / 0.45) ** 2.5
    z += WAVE * math.sin(2.6 * math.pi * v + 0.4)
    z += TIP_LIFT * max(0.0, (v - 0.72) / 0.28) ** 2
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
            zt = top_z(u, v)
            rt.append(bm.verts.new((x, y, zt)))
            rb.append(bm.verts.new((x, y, zt - THICK)))
        top_rows.append(rt)
        bot_rows.append(rb)

    for j in range(NV):
        for i in range(NU):
            bm.faces.new((top_rows[j][i], top_rows[j][i + 1],
                          top_rows[j + 1][i + 1], top_rows[j + 1][i]))
            bm.faces.new((bot_rows[j][i], bot_rows[j + 1][i],
                          bot_rows[j + 1][i + 1], bot_rows[j][i + 1]))
    for i in range(NU):
        bm.faces.new((top_rows[0][i], bot_rows[0][i], bot_rows[0][i + 1],
                      top_rows[0][i + 1]))
        bm.faces.new((top_rows[NV][i + 1], bot_rows[NV][i + 1],
                      bot_rows[NV][i], top_rows[NV][i]))
    for j in range(NV):
        bm.faces.new((top_rows[j][0], top_rows[j + 1][0], bot_rows[j + 1][0],
                      bot_rows[j][0]))
        bm.faces.new((top_rows[j][NU], bot_rows[j][NU], bot_rows[j + 1][NU],
                      top_rows[j + 1][NU]))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    zmin = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z -= zmin - 0.0005

    def is_edge(centre):
        if centre.x < (-0.5 + STEM_END) * LENGTH:
            return True
        v = (centre.x / LENGTH + 0.5)
        w = half_width(min(1.0, max(0.0, v)))
        return centre.y > 0.66 * w

    b.add(bm, 'leaf_dry', LEAF_DRY, 0.90, smooth=False, name='leaf',
          extras=[('wood_dark', WOOD_DARK, 0.75, is_edge)])
