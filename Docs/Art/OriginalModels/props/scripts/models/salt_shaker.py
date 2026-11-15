"""Salt shaker - 45 mm glass body, 145 mm tall, with a three-hole metal cap.

The catalog gives 0.145 x 0.045 x 0.145 m.  A shaker stands, so the 145 mm is
the height and 45 mm the diameter; the model is one lathe from the recessed
underside through the straight glass body and the shoulder, out into the metal
cap skirt that overhangs the neck, and up a shallow cap dome to the axis.  The
cap is solid (the shaker is a placeholder, not a hollow vessel), which is what
keeps the whole thing one closed shell.

The three salt holes are blind conical dents cut into the cap dome with an Exact
boolean difference - the same technique the cheese slice uses for its blind
holes.  They sit on a 12 mm ring at 0/120/240 so they land on the 12-segment
dome's own azimuth samples.

Material pass (2026-11-15 rework): the old pair was a near-white glass body
(0.70/0.76/0.74) under a mid-grey cap - no dark slot, and the whole piece read as
a white bottle.  Three slots now: a blue-green glass body (glass_blue key), a brass cap, and
ink for both the label ring band and the inside of the three holes.  The cap is
brass rather than aluminium on purpose: glass_blue 0.36 and metal 0.47 differ by
only 0.11 in linear peak and 20 deg in hue, which the spec's contrast rule would
score as a single slot; brass (0.52, 43 deg) clears both tests against the glass
(0.16 and 144 deg).  A 30 mm label band and a 0.7 mm inward thread groove were added to
the profile so the band has real boundaries and the cap has a machined line; that
is 4 extra profile points, 208 -> about 256 triangles, and the 45 x 145 mm
envelope is untouched (the groove cuts inward).
"""

import math

from prop_lib import lathe

ID = 'salt_shaker'
NAME_ZH = '盐瓶'
CATEGORY = 'tableware'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.145', 'game_longest_dim_m': '0.145'}
SOURCING = 'model ourselves：Ø0.045 x 0.145 m 玻璃瓶身 + 金属盖（盖面 3 个凹孔）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.145
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'glass_blue', 'name_zh': '瓶身青玻璃', 'linear_rgb': [0.180, 0.330, 0.360],
     'roughness': 0.22},
    {'key': 'brass', 'name_zh': '黄铜金属盖', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'ink', 'name_zh': '标签环带/孔内暗色', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('12 segments, 12 profile points, 11 bands = 264 triangles before the '
         'dents.  Diameter 0.0450 m at the cap skirt (the glass body is 0.0430 m '
         'so the cap reads as a separate part), height 0.1450 m.  Underside '
         'recessed 10 mm; glass body straight from 35 to 95 mm; ink label band '
         '55-85 mm; cap skirt 112-139 mm with a 0.7 mm inward thread groove at '
         '115-118 mm; shallow cap dome to the axis.  Brass covers every face above '
         'z = 107 mm.  Three 2.2 mm holes on a 12 mm ring are cones cut 2.2 mm '
         'into the dome and their bores take the ink slot.')

SEGMENTS = 12
CAP_TOP = 0.1450
METAL_FROM = 0.1070
BAND_Z0 = 0.0540
BAND_Z1 = 0.0860
HOLE_RING = 0.0120
HOLE_RADIUS = 0.0022
HOLE_TOP_Z = 0.1470
HOLE_APEX_Z = 0.1396
HOLE_SEGMENTS = 6
HOLE_ANGLES = (0.0, 120.0, 240.0)

GLASS = (0.180, 0.330, 0.360)
BRASS = (0.520, 0.380, 0.120)
INK = (0.030, 0.035, 0.045)

PROFILE = [
    (0.0000, 0.0100),   # recessed underside centre
    (0.0215, 0.0000),   # ground contact edge
    (0.0215, 0.0350),   # glass body, widest below the cap
    (0.0214, 0.0550),   # label band, lower edge
    (0.0213, 0.0850),   # label band, upper edge
    (0.0212, 0.0950),   # glass body top
    (0.0178, 0.1080),   # neck shoulder under the cap
    (0.0225, 0.1120),   # cap skirt, overhangs the neck
    (0.0218, 0.1150),   # thread groove in
    (0.0225, 0.1180),   # thread groove out
    (0.0225, 0.1390),   # cap wall top
    (0.0000, 0.1450),   # shallow cap dome, pole
]


def cap(centre):
    """Cap material above the neck seam."""
    return centre.z > METAL_FROM


def dark(centre):
    """Ink faces: the label ring band and the inside of the three holes."""
    if BAND_Z0 < centre.z < BAND_Z1:
        return True
    if centre.z < 0.1350:
        return False
    for deg in HOLE_ANGLES:
        a = math.radians(deg)
        dx = centre.x - HOLE_RING * math.cos(a)
        dy = centre.y - HOLE_RING * math.sin(a)
        if dx * dx + dy * dy < 0.0016 ** 2:
            return True
    return False


def build(b):
    body = b.add(lathe(PROFILE, segments=SEGMENTS),
                 'glass_blue', GLASS, 0.22, smooth=True,
                 extras=[('brass', BRASS, 0.30, cap),
                         ('ink', INK, 0.75, dark)],
                 name='shaker')
    holes = []
    for i, deg in enumerate(HOLE_ANGLES):
        a = math.radians(deg)
        bm = lathe([(HOLE_RADIUS, HOLE_TOP_Z), (0.0, HOLE_APEX_Z)],
                   segments=HOLE_SEGMENTS)
        for v in bm.verts:
            v.co.x += HOLE_RING * math.cos(a)
            v.co.y += HOLE_RING * math.sin(a)
        holes.append(b.add(bm, 'ink', INK, 0.75,
                           smooth=False, name='hole%d' % i))
    b.boolean_difference(body, holes)
