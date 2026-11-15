"""Paper scrap - thin crumpled sheet modelled as a closed slab.

The catalogue hint asks for a one-sided sheet with a two-sided material.  A thin
closed slab gives the same read with plain single-sided materials and keeps the
mesh watertight for rolling/collision use.
"""

import math

from prop_lib import sheet

ID = 'paper_scrap'
NAME_ZH = '纸屑'
CATEGORY = 'debris'
CSV_REAL_LONGEST_M = 0.10
CSV_GAME_LONGEST_M = 0.10
SIZE_REFERENCE = 'real'
SOURCING = 'model ourselves：单面薄片，双面材质'
TARGET_LONGEST_M = 0.1
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'paper', 'name_zh': '旧纸白', 'linear_rgb': [0.62, 0.60, 0.54],
     'roughness': 0.8},
]
NOTES = ('No texture: the crumple is geometric.  0.10 x 0.07 m, 1.4 mm thick, '
         'closed slab with a curled edge and a torn outline instead of a two-sided plane.')


def top(x, y):
    curl = 0.0075 * max(0.0, (x + 0.020) / 0.080) ** 2
    return (0.0020
            + curl
            + 0.0028 * math.sin(19.0 * x + 1.1) * math.cos(23.0 * y + 0.6)
            + 0.0014 * math.sin(31.0 * (x + y) + 0.4))


def build(b):
    bm = sheet(6, 5, -0.050, 0.050, -0.035, 0.035, top, thickness=0.0014)
    # torn outline: rim vertices get a deterministic in-plane jitter; the top
    # and bottom vertex of each rim pair share (x, y), so both move together.
    for v in bm.verts:
        if abs(v.co.x) > 0.044 or abs(v.co.y) > 0.030:
            v.co.x += (0.0035 * math.sin(37.0 * v.co.y + 1.2)
                       + 0.0015 * math.sin(91.0 * v.co.x + 0.3))
            v.co.y += (0.0035 * math.cos(41.0 * v.co.x + 0.3)
                       + 0.0015 * math.cos(83.0 * v.co.y + 1.7))
    b.add(bm, 'paper', (0.62, 0.60, 0.54), 0.80, smooth=False)
