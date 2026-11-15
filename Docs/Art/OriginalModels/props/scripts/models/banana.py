"""Banana - 18 cm bowed tube with tapered ends and five longitudinal ridges.

The plan asks for an 18 cm curved tube of 3.2 cm section with both ends closed
off.  The path is a circular arc in the ground plane (chord 0.18 m, bow 0.052 m)
sampled at ten stations, swept by ``tub()`` with a per-angle section
modulation: five shallow ridges, which is what stops a swept tube from reading
as a sausage.  The radii taper to 1.2 mm at both ends, so the caps sit inside
the silhouette.
"""

import math

from prop_lib import tube

ID = 'banana'
NAME_ZH = '香蕉'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.18
CSV_GAME_LONGEST_M = 0.45
SOURCING = 'model ourselves：18 cm 弧形管，截面 Ø3.2，两端收口'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.18
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'skin', 'name_zh': '香蕉黄', 'linear_rgb': [0.720, 0.580, 0.060],
     'roughness': 0.45},
]
NOTES = ('Lies on its side: the bow is horizontal so the whole belly touches the '
         'table instead of the banana balancing on its tips.  Longest edge is '
         'the chord across X (0.18 m); the 3.2 cm section is the vertical extent.')

SEGMENTS = 15          # five ridges, three segments each
STATIONS = 10
ARC_DEG = 60.0         # half-angle of the circular path
ARC_R = 0.104          # chord = 2 * R * sin(60 deg) = 0.180 m
LAND_Z = 0.0015        # path height above the table at mid-span
TIP_LIFT = 0.011       # how far the ends rise (a banana never lies dead flat)
RIDGES = 5
RIDGE_DEPTH = 0.070
RADII = [0.0012, 0.0105, 0.0140, 0.0155, 0.0160,
         0.0158, 0.0150, 0.0135, 0.0105, 0.0012]


def ridge(station, angle, radius):
    return radius * (1.0 + RIDGE_DEPTH * math.cos(RIDGES * angle))


def build(b):
    points = []
    for i in range(STATIONS):
        t = -1.0 + 2.0 * i / (STATIONS - 1)
        theta = math.radians(ARC_DEG) * t
        x = ARC_R * math.sin(theta)
        y = -ARC_R * (1.0 - math.cos(theta))
        z = LAND_Z + TIP_LIFT * abs(t) ** 3
        points.append((x, y, z))
    bm = tube(points, RADII, segments=SEGMENTS, section_fn=ridge)
    b.add(bm, 'skin', (0.720, 0.580, 0.060), 0.45, smooth=True)
