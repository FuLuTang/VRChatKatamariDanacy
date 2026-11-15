"""Orange - slightly oblate sphere with eight segment grooves.

The plan asks for an 8 cm sphere with eight shallow segment grooves, carved only
as dents (no displacement map).  The grooves come from ``lathe``'s per-segment
radius modulation: a cosine with eight periods around the axis sinks every
meridian smoothly, so 24 segments produce eight broad creases with no hard
single-vertex notch.  The first version dipped one meridian column at a time;
smooth shading averaged that notch away and the preview read as a plain ball.

The dip is 9 % of the radius at the groove minima (about 3.5 mm at this size).
The mesh is flat-shaded: with smooth vertex normals the shallow dents vanished at
preview size, while flat facets keep the eight creases readable.  The trade-off
is a faceted, low-poly orange, which matches the rest of this placeholder set.
"""

import math

from prop_lib import TAU, lathe

ID = 'orange'
NAME_ZH = '橙子'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.08
CSV_GAME_LONGEST_M = 0.30
SOURCING = 'model ourselves：Ø8 cm 球，表面 8 条浅瓣线'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.08
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'rind', 'name_zh': '橙皮', 'linear_rgb': [0.520, 0.160, 0.020],
     'roughness': 0.70},
]
NOTES = ('A 0.08 m sphere squashed to 0.072 m tall, so the longest edge is the '
         'diameter.  Eight cosine grooves on 24 meridians keep the triangle '
         'count at 288 within the 300 budget; the dip is 9 % of the radius '
         '(about 3 mm), applied at every sampled height so the crease follows '
         'the meridian instead of pinching one ring.')

SEGMENTS = 24
GROOVES = 8
GROOVE_DEPTH = 0.09


def groove(point_index, segment_index, radius):
    angle = TAU * segment_index / SEGMENTS
    return radius * (1.0 - GROOVE_DEPTH * 0.5 * (1.0 + math.cos(GROOVES * angle)))


def build(b):
    # sphere of radius 0.040 squashed vertically to +/-0.036, sampled at six
    # heights; both ends are poles so the shell closes without caps
    profile = [
        (0.0000, 0.0000),
        (0.0212, 0.0055),
        (0.0332, 0.0160),
        (0.0394, 0.0300),
        (0.0392, 0.0430),
        (0.0332, 0.0560),
        (0.0212, 0.0665),
        (0.0000, 0.0720),
    ]
    bm = lathe(profile, segments=SEGMENTS, radius_fn=groove)
    b.add(bm, 'rind', (0.520, 0.160, 0.020), 0.70, smooth=False)
