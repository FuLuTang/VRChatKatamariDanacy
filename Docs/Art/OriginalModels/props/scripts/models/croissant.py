"""Croissant - 12 x 7 x 6 cm crescent roll, the catalog's baked-goods slot.

The plan asked for a baguette, but prop_catalog.csv has no baguette row and no
baguette real size; its bread entry is ``bread_slice`` and the CSV note records
that the whole-loaf concept was dropped.  Inventing a 0.65 m baguette would
therefore be a size with no source.  ``croissant`` is the catalog's other whole
baked good and it has a real three-axis size (0.12 x 0.07 x 0.06), so it fills
the same picnic-bread slot without a fabricated number.

The shape is a quadratic Bezier crescent swept with a 9-sided elliptical
section: 30 mm tall and 24 mm wide at the fat middle, tapering to about
1.6 mm at the two horns so the tips read as points rather than cut stubs.  The radii are modulated by a six-lobe cosine so the silhouette has
the banded bulges of a rolled croissant instead of reading as a smooth banana.
The crescent lies flat on the table, which is how the pastry actually sits.

The chord runs along Y and the opening faces +X, so the design's 0.12 m length
is the measured Y extent and the 0.07 m depth is X.  This orientation is not
arbitrary: the preview camera sits at (1.70, 1.15, 0.80), and with the opening
on that side it looks into the concave face and both horns stay visible.
"""

import math

from mathutils import Vector

from prop_lib import tube

ID = 'croissant'
NAME_ZH = '牛角包'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.12
CSV_GAME_LONGEST_M = 0.32
SOURCING = 'model ourselves：0.12 x 0.07 x 0.06 m 牛角包（替代无目录的 baguette）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.12
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'crust', 'name_zh': '牛角包金棕', 'linear_rgb': [0.520, 0.255, 0.055],
     'roughness': 0.55},
]
NOTES = ('21 path stations, 9 segments = 378 triangles.  Quadratic Bezier with '
         'tips at (0.020, +/-0.056) and the control at (-0.060, 0), all at '
         'z=0.030; the curve bows back to x=-0.020 and opens toward +X, so the '
         '0.12 m length is Y and the 0.07 m depth is X.  Section is an ellipse '
         '30 mm tall x 24 mm wide at the middle, tapered to 1.6 mm at the horns, '
         'with a six-lobe +/-15% radius wave.  Raw Y is about 0.118 m, so '
         'finish() scales by ~1.017: measured 0.069 x 0.120 x 0.0605 m.  One '
         'closed shell, no separate crumbs or scoring.')

STATIONS = 21
HALF_CHORD = 0.0560
TIP_X = 0.0200
BACK_X = -0.0600
CENTRE_Z = 0.0300
BASE_RADIUS = 0.0270
LOBE_DEPTH = 0.15
LOBE_COUNT = 6
WIDTH_RATIO = 0.80


def _bezier(t):
    # Tips at +X and +/-Y, control pulled back to -X: the concave side faces +X.
    a = Vector((TIP_X, -HALF_CHORD, CENTRE_Z))
    c = Vector((BACK_X, 0.0, CENTRE_Z))
    b = Vector((TIP_X, HALF_CHORD, CENTRE_Z))
    return (a * (1.0 - t) ** 2 + c * (2.0 * (1.0 - t) * t)
            + b * (t * t))


def _radius(t):
    # Fat middle, thin horns, then a six-lobe wave for the rolled bands.
    base = BASE_RADIUS * (0.06 + 0.94 * math.sin(math.pi * t) ** 0.55)
    band = 1.0 + LOBE_DEPTH * math.cos(2.0 * math.pi * LOBE_COUNT * t) \
        * math.sin(math.pi * t)
    return base * band


POINTS = [_bezier(i / (STATIONS - 1)) for i in range(STATIONS)]
RADII = [_radius(i / (STATIONS - 1)) for i in range(STATIONS)]


def _section(_station, angle, radius):
    # The sweep's cos(angle) axis is vertical (the path is horizontal), so this
    # makes a 30 mm tall, 24 mm wide ellipse rather than a circular sausage.
    return radius * math.sqrt(math.cos(angle) ** 2
                              + (WIDTH_RATIO * math.sin(angle)) ** 2)


def build(b):
    bm = tube(POINTS, RADII, segments=9, section_fn=_section)
    b.add(bm, 'crust', (0.520, 0.255, 0.055), 0.55, smooth=True)
