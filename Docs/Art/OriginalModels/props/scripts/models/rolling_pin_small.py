"""Rolling pin, small - 280 mm long, 35 mm barrel, lying along X on z = 0.

The catalog gives 0.280 x 0.035 x 0.035 m and says lying with the axis along X,
so the part is built as a lathe around Z (handles 6 mm, tapered shoulders,
35 mm barrel) and then rotated 90 degrees about Y, which puts the axis on X and
the radius on Z.  It is then dropped onto the ground and centred in X, so the
barrel's lowest line touches z = 0 before ``finish`` re-centres the pivot.

12 segments and 8 bands give 192 triangles, inside the 180-triangle baseline
budget x 1.6 (288) if the shoulder step is kept.

Material pass (2026-11-15 rework): one flat wood slot (0.48/0.30/0.15) for the
whole pin.  Three slots now, assigned straight from the distance to the axis end,
which is free: the barrel is the shared wood key, the two handles the dark wood
key, and the outer 3.5 mm of each handle the dark ink key - a turned pin with
ebonised grip ends, and the required low-value slot.  Barrel and handle differ by
0.195 in linear peak, well over the 0.15 contrast rule.
"""

import math

from mathutils import Matrix

from prop_lib import lathe

ID = 'rolling_pin_small'
NAME_ZH = '小擀面杖'
CATEGORY = 'tableware'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.280', 'game_longest_dim_m': '0.280'}
SOURCING = 'model ourselves：0.280 x 0.035 x 0.035 m 木擀面杖，横放、轴向 X'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.280
TRI_BUDGET = 180
MATERIALS = [
    {'key': 'wood', 'name_zh': '榉木轴身', 'linear_rgb': [0.330, 0.205, 0.105],
     'roughness': 0.72},
    {'key': 'wood_dark', 'name_zh': '深色手柄', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
    {'key': 'ink', 'name_zh': '手柄暗端', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('12 segments, 8 profile points, 7 bands = 168 triangles.  Axis built on '
         'Z from 0 to 280 mm then rotated 90 degrees about Y: handles 12 mm '
         'across and 38 mm long, shoulders 60-220 mm carrying the 35 mm barrel, '
         'totals 0.280 x 0.035 x 0.035 m.  After the rotation every vertex is '
         'moved by (-140 mm on X, +17.5 mm on Z) so the barrel rests on z = 0 and '
         'the part is centred before finish() re-centres the pivot.  Slots by '
         '|x|: barrel inside 81.5 mm, dark handles to 134.5 mm, ink grip ends '
         'beyond that.')

SEGMENTS = 12
HALF_LENGTH = 0.140
BARREL_RADIUS = 0.0175
BARREL_END = 0.0815
HANDLE_END = 0.1345

WOOD = (0.330, 0.205, 0.105)
WOOD_DARK = (0.135, 0.078, 0.038)
INK = (0.030, 0.035, 0.045)

PROFILE = [
    (0.0000, 0.0000),   # handle end, flat pole
    (0.0060, 0.0010),
    (0.0060, 0.0380),   # left handle
    (0.0175, 0.0600),   # shoulder into the barrel
    (0.0175, 0.2200),   # barrel
    (0.0060, 0.2420),   # right shoulder
    (0.0060, 0.2790),   # right handle
    (0.0000, 0.2800),   # right end, flat pole
]


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    rot = Matrix.Rotation(math.radians(90.0), 4, 'Y')
    for v in bm.verts:
        v.co = rot @ v.co
        v.co.x -= HALF_LENGTH
        v.co.z += BARREL_RADIUS
    b.add(bm, 'wood', WOOD, 0.72, smooth=True,
          extras=[('wood_dark', WOOD_DARK, 0.75,
                   lambda c: abs(c.x) > BARREL_END),
                  ('ink', INK, 0.75,
                   lambda c: abs(c.x) > HANDLE_END)])
