"""Feather - 12 cm dry feather with a raised dark rachis and a sawtooth vane.

DungBeetleMeadow catalogue t1: 0.120 x 0.030 x 0.008 m, shape_class long,
356/400 tris, Z1 围栏根（掉落羽毛）.

The 2026-11-15 review read the previous build as a plain beige slab: 4 outline
teeth at 16 % of the half-width were sampled by 8 rows (2 rows per tooth) but
amounted to 2 mm, and the "rachis" was a 5 mm Gaussian bump in the same colour
as the vane, so nothing marked it.  This build:

* vane, 9 columns x 8 rows (288 top+bottom triangles + 68 rim = 356): the
  outline alternates peak/valley every row - a 4-tooth triangle wave at 30 % of
  the half-width (about 4 mm on a 29 mm vane), so the silhouette is visibly
  sawtoothed and reads as barbs instead of a cut card;
* rachis, a real raised rib: the two vertices either side of the centre line are
  lifted 1.6 mm (a 3.2 mm flat-topped rib with steep shoulders, fading toward
  both ends) and that centre column is straw_dark, so the shaft is a colour
  break too, not just a bump;
* quill, the bare first 10 % of the length, is ink: a dark base at the narrow
  end, which is what tells the eye which end is the quill;
* the vane is asymmetric (quill side 72 % of the broad side) and carries two
  partings, and the slab is 2.8 mm thick with a flat underside at z = 0.0006,
  so the feather lies on the ground.

Colour: vane straw (0.368), rachis straw_dark (0.171), quill ink (0.035).
"""

import math

import bmesh

ID = 'feather'
NAME_ZH = '羽毛'
CATEGORY = 'animal_gear'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t1 '
            '0.120×0.030×0.008 m 羽毛（Z1 围栏根，掉落羽毛）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 400
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
STRAW = (0.470, 0.360, 0.140)
STRAW_DARK = (0.230, 0.165, 0.060)
INK = (0.030, 0.035, 0.045)
MATERIALS = [
    {'key': 'straw', 'name_zh': '干羽毛浅褐', 'linear_rgb': list(STRAW),
     'roughness': 0.90},
    {'key': 'straw_dark', 'name_zh': '羽轴深褐', 'linear_rgb': list(STRAW_DARK),
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '羽根暗色', 'linear_rgb': list(INK),
     'roughness': 0.75},
]
NOTES = ('9 columns x 8 rows grid slab (10 x 9 vertices): 288 top+bottom '
         'triangles + 68 rim = 356 triangles, one closed shell, no boolean.  '
         'Length 0.120 m along X; vane half-width 0.0158 m on the broad side and '
         '72 % of that on the quill side; a 4-tooth triangle wave at 30 % '
         'amplitude sampled by 8 rows (alternating peak/valley) gives the '
         'sawtooth silhouette; two partings at v = 0.34 / 0.62.  Rachis: the two '
         'vertices beside the centre line lift 1.6 mm (3.2 mm flat rib) and are '
         'straw_dark; the first 10 % of the length is ink (quill).  Vane top '
         '0.0040 m, flat underside 0.0006 m, so the slab is 3.4-5.2 mm thick.  '
         'Overall 0.120 x 0.030 x 0.0070 m.')

NU = 9
NV = 8
LENGTH = 0.120
VANE_TOP = 0.0040
FLOOR = 0.0006
HALF_BROAD = 0.0158
QUILL_SIDE = 0.72
TEETH = 4
TOOTH_AMP = 0.30
NOTCHES = ((0.34, 0.45), (0.62, 0.35))
RIDGE = 0.0018
RIDGE_U = 0.115
BOW = 0.0007
QUILL_END = 0.10


def tooth(v):
    """4-tooth triangle wave, peaks at v = 0, 0.25, 0.5, 0.75."""
    f = (v * TEETH) % 1.0
    return abs(2.0 * f - 1.0)


def half_width(v):
    t = min(1.0, max(0.0, v / 0.96))
    base = math.sin(math.pi * t) ** 0.72
    notch = 1.0
    for centre, depth in NOTCHES:      # two partings in the vane
        notch *= 1.0 - depth * math.exp(-((v - centre) / 0.05) ** 2)
    return 0.0012 + HALF_BROAD * base * notch * (
        0.70 + TOOTH_AMP * tooth(v))


def plan(u, v):
    x = (-0.5 + v) * LENGTH
    w = half_width(v)
    y = u * (w if u > 0.0 else w * QUILL_SIDE)
    return x, y


def top(u, v):
    z = VANE_TOP + BOW * math.sin(math.pi * v)
    if abs(u) <= RIDGE_U:
        z += RIDGE * (0.55 + 0.45 * math.sin(math.pi * v))
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

    def is_rachis(centre):
        return (abs(centre.y) < 0.0016
                and (-0.5 + QUILL_END) * LENGTH < centre.x < 0.40 * LENGTH)

    def is_quill(centre):
        return centre.x <= (-0.5 + QUILL_END) * LENGTH

    b.add(bm, 'straw', STRAW, 0.90, smooth=False, name='feather',
          extras=[('ink', INK, 0.75, is_quill),
                  ('straw_dark', STRAW_DARK, 0.92, is_rachis)])
