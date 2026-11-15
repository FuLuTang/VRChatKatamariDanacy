"""Strawberry - tapered conical body with a shoulder and a draping star calyx.

The 2026-11-15 prop plan asks for a 4 cm tall heart-shaped solid, 3.5 cm at its
widest, plus five sepals, as a single shell.  The body is a surface of
revolution: a blunt tip, a straight cone that swells to the widest point just
above two thirds of the height, then a short shoulder that pulls in to a neck.
The calyx is the same lathe continued: level with the neck the surface steps
out to a five-pointed ring, then rises to a shallow crown.  The ring is a
shallow scallop (the valleys sit at 75 % of the point radius) so the points
stand a little proud of the red shoulder instead of forming a flat plate.

Material split: five green leaf sectors, two faces wide each, with the red top
left visible in the notches between them.  There is deliberately no green hub,
because a green centre joined the five leaves into the "helmet" the first
version suffered from.

Triangles: 20 segments x 6 ring bands (pole to pole) = 240, inside the 250
budget.  Twenty segments give four faces per lobe, so a leaf can be two faces
wide; the five lobes need a segment count that is a multiple of five.
"""

import math

from prop_lib import lathe

ID = 'strawberry'
NAME_ZH = '草莓'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.04
CSV_GAME_LONGEST_M = 0.06
SOURCING = 'model ourselves：4 cm 高心形旋转体 + 5 片萼，单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.04
TRI_BUDGET = 250
MATERIALS = [
    {'key': 'skin', 'name_zh': '草莓红', 'linear_rgb': [0.42, 0.030, 0.040],
     'roughness': 0.45},
    {'key': 'calyx', 'name_zh': '萼片绿', 'linear_rgb': [0.055, 0.130, 0.030],
     'roughness': 0.70},
]
NOTES = ('Blunt tip at z=0, widest at 66 % of the 4 cm height, shoulder at '
         'z=3.25 cm, neck at z=3.45 cm.  The five sepal points flare to r=1.9 cm '
         'at z=3.45 cm, 2 mm above the shoulder, then the crown closes at '
         'z=4 cm.  The ring is a shallow scallop (valleys at 75 % of the point '
         'radius), and green is restricted to five leaf sectors (two faces each), so '
         'the red top is visible in the notches between the sepals instead of '
         'forming a green helmet.  One shell.')

SEGMENTS = 20          # four faces per lobe so a leaf can be two faces wide
LOBES = 5
SEGMENTS_PER_LOBE = SEGMENTS // LOBES

# (radius, z) bottom to top; the first and last points are poles.  Eight
# points keeps the 20-segment lathe at 2 * 20 * 6 = 240 triangles.
PROFILE = [
    (0.0000, 0.0000),   # blunt tip
    (0.0090, 0.0055),
    (0.0162, 0.0180),
    (0.0174, 0.0270),   # widest, just above two thirds of the height
    (0.0162, 0.0325),   # shoulder
    (0.0188, 0.0345),   # sepal ring, level with the neck, shallow scallop
    (0.0090, 0.0390),   # crown
    (0.0000, 0.0400),   # top pole
]

CALYX_RING = 5
CALYX_ABOVE = 0.0320
LEAF_HALF_ANGLE = math.radians(15.0)


def calyx(point_index, segment_index, radius):
    """Turn the sepal ring into five points with deep valleys between them."""
    if point_index != CALYX_RING:
        return radius
    if segment_index % SEGMENTS_PER_LOBE == 0:
        return radius
    return radius * 0.75


def calyx_face(centre):
    """Five green leaf sectors plus a small centre; red between the leaves.

    A green-by-height split painted the whole crown, which read as a helmet.
    The angular test keeps the red body visible in the notches between the five
    sepals, and the small radius test keeps a green calyx centre at the top.
    """
    if centre.z < CALYX_ABOVE:
        return False
    angle = math.atan2(centre.y, centre.x)
    period = math.tau / LOBES
    delta = abs(((angle + period * 0.5) % period) - period * 0.5)
    return delta < LEAF_HALF_ANGLE


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS, radius_fn=calyx)
    b.add(bm, 'skin', (0.42, 0.030, 0.040), 0.45, smooth=True,
          extras=[('calyx', (0.055, 0.130, 0.030), 0.70, calyx_face)])
