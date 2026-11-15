"""Ticket stub - 110 x 55 mm ticket with a torn end, an ink stub band, a crease.

Design three-axis 0.110 x 0.055 x 0.001 m.  Thickness is 1.5 mm, the modelling
floor for a flat paper prop: a 1.0 mm slab leaves almost nothing between the two
large faces once finish() welds at 1e-6 m, and the paper read survives on X/Y.

`sheet` (not `prism`) because the ink band has to be its own material on the top
surface: a prism closes its caps as n-gons, and a per-face material predicate can
only paint a whole cap.  The grid gives six X cells; the columns are then moved
off the uniform grid so cell 4 is only 1.7 mm wide - that cell is the scored fold
line between the ticket and the stub.  Its two boundary columns drop 0.4 mm, so
the crease is real geometry.  The three Y cells along the torn end let the tear
zigzag, and the top/bottom pair of a rim point moves together, so the slab stays
closed.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, flat paper
class): three palette slots instead of one near-white one plus an off-palette
ochre.
  * `paper`      - the ticket stock (linear 0.800 / 0.775 / 0.700, rough 0.90)
  * `ink`        - the torn-off stub band (0.030 / 0.035 / 0.045)
  * `paper_dark` - the scored fold line (0.360 / 0.320 / 0.250)

108 triangles: 3 x 6 top + 3 x 6 bottom + 18 rim quads.
"""

from prop_lib import sheet

ID = 'ticket_stub'
NAME_ZH = '票根'
CATEGORY = 'debris'
SOURCING = 'model ourselves：110 x 55 mm 票根，撕口一端 + 印刷色带 + 压痕折线'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.110
TRI_BUDGET = 128
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.110', 'game_longest_dim_m': '0.110'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '票据纸米黄', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '票根印刷墨块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'paper_dark', 'name_zh': '折线深纸色', 'linear_rgb': [0.360, 0.320, 0.250],
     'roughness': 0.92},
]
NOTES = ('Thickness 1.5 mm instead of the catalogue 1.0 mm (flat-slab floor).  '
         'Sheet grid 6 x 3 = 108 triangles.  X columns are moved to XB so cell 4 '
         'is the 1.7 mm fold line (0.0183-0.0200 m, rows dropped 0.4 mm, '
         'paper_dark); cell 5 (0.0200-0.0550 m) is the ink stub band.  The torn '
         'end zigzags 5.5 mm in the three Y cells and keeps its corners at '
         '0.055 m, so the 0.110 m target axis is unchanged.')

NX, NY = 6, 3
HALF_L = 0.055          # 0.110 m, the target axis
HALF_W = 0.0275         # 0.055 m
THICK = 0.0015
TEAR = 0.0055           # how far a torn notch recedes from the end
CREASE_DROP = 0.0004
# Non-uniform X boundaries (7 values -> 6 cells); cell 4 is the fold line.
XB = (-0.0550, -0.0367, -0.0183, 0.0000, 0.0183, 0.0200, 0.0550)
CREASE = (0.0183, 0.0200)
STUB_FROM = 0.0200      # ink band starts here (the sixth X cell)
PAPER_RGB = (0.800, 0.775, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
PAPER_DARK_RGB = (0.360, 0.320, 0.250)


def _col(x):
    return int(round((x + HALF_L) / (2.0 * HALF_L) * NX))


def _slot(centre):
    if centre.z < THICK - 1e-5 and centre.z > 0.0001 \
            and CREASE[0] - 1e-6 < centre.x < CREASE[1] + 1e-6:
        return 2
    # 0.00115 instead of "top face height": the crease rows are shared with the
    # stub cell, so the stub cell's top face slopes from 0.0015 down to 0.0011
    # and its centre sits at 0.0013 - below any "is it the top face" threshold.
    if centre.z > 0.00115 and centre.x > STUB_FROM:
        return 1
    return 0


def build(b):
    bm = sheet(NX, NY, -HALF_L, HALF_L, -HALF_W, HALF_W,
               lambda x, y: THICK, bottom_fn=lambda x, y: 0.0)
    for v in bm.verts:
        x = XB[_col(v.co.x)]
        v.co.x = x
        if v.co.z > 0.0 and CREASE[0] - 1e-6 <= x <= CREASE[1] + 1e-6:
            v.co.z = THICK - CREASE_DROP
        if x < HALF_L - 1e-9:
            continue
        j = int(round((v.co.y + HALF_W) / (2.0 * HALF_W) * NY))
        v.co.x = HALF_L - (TEAR if j % 2 else 0.0)
    ticket = b.add(bm, 'paper', PAPER_RGB, 0.90, smooth=False, name='ticket')

    me = ticket.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
