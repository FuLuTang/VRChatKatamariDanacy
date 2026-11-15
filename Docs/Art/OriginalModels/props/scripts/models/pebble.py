"""Pebble - deformed icosphere with a natural, slightly irregular silhouette."""

import math

from prop_lib import icosphere

ID = 'pebble'
NAME_ZH = '小石子'
CATEGORY = 'nature'
CSV_REAL_LONGEST_M = 0.04
CSV_GAME_LONGEST_M = 0.06
SIZE_REFERENCE = 'real'
SOURCING = 'Kenney Nature Kit rock_small*（渲染不符，需人工复核）或 model ourselves'
TARGET_LONGEST_M = 0.04
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'stone', 'name_zh': '灰石', 'linear_rgb': [0.20, 0.20, 0.19],
     'roughness': 0.85},
]
NOTES = ('Chose the "model ourselves" option.  Deterministic sine deformation, '
         'flat shaded, 80 triangles.')


def build(b):
    bm = icosphere(2, 1.0)
    for v in bm.verts:
        p = v.co.normalized()
        r = (1.0
             + 0.16 * math.sin(3.1 * p.x + 0.7) * math.cos(2.7 * p.y + 1.3)
             + 0.08 * math.sin(5.3 * p.z + 2.1))
        v.co = p * r
    for v in bm.verts:
        v.co.x *= 0.0300
        v.co.y *= 0.0225
        v.co.z *= 0.0160
    b.add(bm, 'stone', (0.20, 0.20, 0.19), 0.85, smooth=False)
