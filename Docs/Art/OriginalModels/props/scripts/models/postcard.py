"""Postcard - 150 x 100 mm card with three printed bands and a scored line.

Design three-axis 0.150 x 0.100 x 0.001 m, built at 1.6 mm thickness (the floor
for a flat paper prop, as for the other sheets in this batch).

The contract asks for printed bands as separate materials, not as a texture.  A
1 x 11 `sheet` grid gives eleven strips across the card, so the bands are
individual quads with their own material index.  The Y rows are then moved off
the uniform grid, because the score line has to be a thin 1.5 mm strip instead of
a 12.5 mm stripe: cell 7 spans 0.0125-0.0140 m and its two boundary rows are
pushed 0.4 mm down, so the card carries a real scored crease rather than a
painted line.  Top and bottom rows of a cell share (x, y), so the rim stays
vertical and the shell stays closed.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, flat paper
class): three palette slots.
  * `paper`      - the card (linear 0.800 / 0.775 / 0.700, rough 0.90)
  * `ink`        - the three printed bands (0.030 / 0.035 / 0.045)
  * `paper_dark` - the scored crease floor and walls (0.360 / 0.320 / 0.250)
"""

from prop_lib import sheet

ID = 'postcard'
NAME_ZH = '明信片'
CATEGORY = 'debris'
SOURCING = 'model ourselves：150 x 100 mm 明信片，正面 3 条印刷色带 + 压痕'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.150
TRI_BUDGET = 128
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.150', 'game_longest_dim_m': '0.150'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '卡纸米白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '印刷墨块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'paper_dark', 'name_zh': '压痕深纸色', 'linear_rgb': [0.360, 0.320, 0.250],
     'roughness': 0.92},
]
NOTES = ('Thickness 1.6 mm instead of the catalogue 1.0 mm (flat-slab floor).  '
         'Sheet grid 1 x 11 = 92 triangles.  Y rows are moved to YB so the score '
         'line is a 1.5 mm cell (0.0125-0.0140 m) instead of a 12.5 mm stripe; '
         'its rows are pushed 0.4 mm down, which is a real crease.  Ink bands: '
         '-0.0375..-0.0250, -0.0125..0.0125, 0.0250..0.0375 m; the crease takes '
         'paper_dark.  Bands are material assignments on the grid, never a '
         'texture or an extra shell.')

NX, NY = 1, 11
HALF_L = 0.075          # 0.150 m, the target axis
HALF_W = 0.050          # 0.100 m
THICK = 0.0016
CREASE_DROP = 0.0004    # the scored line is a real 0.4 mm trough
# Non-uniform Y boundaries (12 values -> 11 cells).
YB = (-0.0500, -0.0375, -0.0250, -0.0125, -0.0050, 0.0000, 0.0050,
      0.0125, 0.0140, 0.0250, 0.0375, 0.0500)
INK_BANDS = ((-0.0375, -0.0250), (-0.0125, 0.0125), (0.0250, 0.0375))
CREASE = (0.0125, 0.0140)
PAPER_RGB = (0.800, 0.775, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
PAPER_DARK_RGB = (0.360, 0.320, 0.250)


def _row(y):
    return int(round((y + HALF_W) / (2.0 * HALF_W) * NY))


def _ink(centre):
    if centre.z < THICK - 1e-5:
        return False
    return any(lo < centre.y < hi for lo, hi in INK_BANDS)


def _crease(centre):
    if not (CREASE[0] - 1e-6 < centre.y < CREASE[1] + 1e-6):
        return False
    return 0.0001 < centre.z < THICK - 1e-5


def build(b):
    bm = sheet(NX, NY, -HALF_L, HALF_L, -HALF_W, HALF_W,
               lambda x, y: THICK, bottom_fn=lambda x, y: 0.0)
    for v in bm.verts:
        y = YB[_row(v.co.y)]
        v.co.y = y
        if v.co.z > 0.0 and CREASE[0] - 1e-6 <= y <= CREASE[1] + 1e-6:
            v.co.z = THICK - CREASE_DROP
    card = b.add(bm, 'paper', PAPER_RGB, 0.90, smooth=False, name='card')

    me = card.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        c = poly.center
        poly.material_index = 2 if _crease(c) else (1 if _ink(c) else 0)
