"""Grass seed - elongated ovoid grain, any small grass seed, no markings."""

import math
from mathutils import Matrix

from prop_lib import lathe

ID = 'seed'
NAME_ZH = '草籽'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.02
CSV_GAME_LONGEST_M = 0.04
SIZE_REFERENCE = 'real'
SOURCING = 'model ourselves：Ø0.04 m 椭球 + 1 张 alpha 草卡'
TARGET_LONGEST_M = 0.02
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'body', 'name_zh': '麦秆黄', 'linear_rgb': [0.42, 0.30, 0.10],
     'roughness': 0.55},
]
NOTES = ('Hint asks for an ellipsoid plus one alpha grass card; the card would '
         'need a texture, so this batch keeps the solid grain only.')


def build(b):
    profile = [
        (0.0000, 0.0000),
        (0.0045, 0.0035),
        (0.0078, 0.0090),
        (0.0080, 0.0170),
        (0.0058, 0.0260),
        (0.0032, 0.0335),
        (0.0016, 0.0380),
        (0.0000, 0.0400),
    ]
    bm = lathe(profile, segments=12)
    rot = Matrix.Rotation(math.radians(-90.0), 4, 'X')
    for v in bm.verts:
        v.co = rot @ v.co
    for v in bm.verts:
        v.co.x *= 1.40
    b.add(bm, 'body', (0.42, 0.30, 0.10), 0.55, smooth=True)
