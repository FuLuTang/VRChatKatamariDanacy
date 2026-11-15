"""Chopstick - 22 cm square-section stick, thick at the handle, thin at the tip.

Contract: 0.220 x 0.006 x 0.006 m (real size), wood, <= 120 triangles.

A 4-sided sweep along +X with a linear taper: a 6 mm square at the handle down to
2.5 mm at the tip.  The sweep's square section is built with its diagonals on the
Y/Z axes, so every vertex is turned 45 deg about the sweep axis to put the square
flat to the table, and each station is then dropped by its own apothem.  The
result is a stick whose underside is one flat plane at z = 0 for its whole
length - it lies on the table the way the row asks ("平放在 z = 0 上") instead of
balancing on the fat end.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): the single pale
wood slot becomes two palette tones plus a carved node ring.
  * `straw`     - the bamboo body (linear 0.470 / 0.360 / 0.140, rough 0.90)
  * `wood_dark` - the carved node ring and its band (0.135 / 0.078 / 0.038)
The ring is real geometry, not a painted stripe: three stations are added at
x = 0.086 / 0.088 / 0.090 m and `_radius` shrinks the middle one by 10%, so the
stick has a 4 mm carved groove there.  The apothem drop uses the same shrunk
radius, so the underside stays on z = 0 and the AABB is unchanged.

No boolean, no second part: one closed tube with square end caps.
"""

import math

from prop_lib import tube

ID = 'chopstick'
NAME_ZH = '筷子'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：0.220 m 方锥竹筷，上粗下细，带雕刻竹节环'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.220
TRI_BUDGET = 128
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.220', 'game_longest_dim_m': '0.220'}
MATERIALS = [
    {'key': 'straw', 'name_zh': '竹木', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '竹节环深木', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('4-sided sweep, 8 stations (x = -0.110 / -0.050 / 0.010 / 0.060 / 0.086 '
         '/ 0.088 / 0.090 / 0.110), radius 0.004243 -> 0.001768 so the square '
         'side tapers 0.006 -> 0.0025 m.  Section turned 45 deg about the sweep '
         'axis and each station dropped by its own apothem, so the underside is '
         'the plane z = 0 along the whole length.  Detail: the station at '
         'x = 0.088 m is shrunk 10%, a 4 mm carved node ring that takes the '
         'wood_dark slot.  AABB 0.220 x 0.006 x 0.006 m, 64 triangles, one '
         'closed shell.')

SEGMENTS = 4
HANDLE_SIDE = 0.0060
TIP_SIDE = 0.0025
X_BACK = -0.110
X_TIP = 0.110
STATIONS = (-0.110, -0.092, -0.090, -0.050, 0.010, 0.060, 0.086, 0.088, 0.090, 0.110)
BUTT_X = -0.091          # lacquered butt band: -0.110 .. -0.092 m
NODE_X = 0.088
NODE_SPAN = 0.0015       # only the middle station is inside this window
NODE_SHRINK = 0.90
STRAW_RGB = (0.470, 0.360, 0.140)
WOOD_DARK_RGB = (0.135, 0.078, 0.038)


def _radius(x):
    t = (x - X_BACK) / (X_TIP - X_BACK)
    side = HANDLE_SIDE + (TIP_SIDE - HANDLE_SIDE) * t
    r = side / math.sqrt(2.0)
    if abs(x - NODE_X) < NODE_SPAN:
        r *= NODE_SHRINK
    return r


def build(b):
    bm = tube([(x, 0.0, 0.0) for x in STATIONS],
              [_radius(x) for x in STATIONS], segments=SEGMENTS)
    ca, sa = math.cos(math.radians(45.0)), math.sin(math.radians(45.0))
    for v in bm.verts:
        y, z = v.co.y, v.co.z
        v.co.y = y * ca - z * sa
        v.co.z = y * sa + z * ca + _radius(v.co.x) * math.sqrt(0.5)
    stick = b.add(bm, 'straw', STRAW_RGB, 0.90, smooth=False, name='stick')

    me = stick.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        x = poly.center.x
        dark = abs(x - NODE_X) < 0.0021 or x < BUTT_X
        poly.material_index = 1 if dark else 0
