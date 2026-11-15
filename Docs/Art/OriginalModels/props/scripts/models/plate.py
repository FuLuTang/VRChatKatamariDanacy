"""Plate - 24 cm dinner plate, 2.5 cm tall, with a foot ring and a shallow well.

PROP_CATALOG.md gives the design three-axis as 0.24 x 0.24 x 0.025, so the plate
has to read as a thin disc, not a puck.  The lathe profile starts at the raised
underside centre, drops to the foot ring at z=0, rises under the rim, crosses
the 2.5 cm outer edge and comes back across the dished top to the centre.  Both
ends are poles, so the result is one closed shell with no cap inside.

Twenty-four segments keep the 24 cm rim from looking like a polygon at preview
size; the 10-point profile is 384 triangles, under the 400 budget.  The foot ring is the
part that makes a plate sit on a table instead of balancing on its whole
underside, and it is modelled as a real 12 mm flat rather than a painted line.
"""

from prop_lib import lathe

ID = 'plate'
NAME_ZH = '盘子'
CATEGORY = 'tableware'
CSV_REAL_LONGEST_M = 0.24
CSV_GAME_LONGEST_M = 0.50
SOURCING = 'model ourselves：Ø0.24 x 0.025 m 餐盘，圈足 + 浅盘心'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.24
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'ceramic', 'name_zh': '陶瓷米白', 'linear_rgb': [0.760, 0.735, 0.680],
     'roughness': 0.32},
]
NOTES = ('24 segments, 10 profile points = 384 triangles.  Diameter 0.240 m, '
         'height 0.025 m exactly.  Foot ring 0.060-0.072 m at z=0; the underside '
         'rises from 0.0035 m at the centre to the 0.025 m rim; the top well '
         'drops from the rim to 0.006 m at the centre, leaving a 2.5 mm centre '
         'and 3 mm rim.  One closed shell, no separate foot.')

PROFILE = [
    (0.0000, 0.0035),   # raised underside centre
    (0.0500, 0.0035),
    (0.0600, 0.0000),   # foot ring, outer contact
    (0.0720, 0.0000),
    (0.0900, 0.0090),   # underside rises under the rim
    (0.1200, 0.0250),   # outer rim
    (0.1140, 0.0220),   # rim top, inner edge
    (0.0900, 0.0130),   # dished top
    (0.0450, 0.0070),
    (0.0000, 0.0058),   # well centre
]


def build(b):
    bm = lathe(PROFILE, segments=24)
    b.add(bm, 'ceramic', (0.760, 0.735, 0.680), 0.32, smooth=True)
