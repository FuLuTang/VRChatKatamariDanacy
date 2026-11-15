"""Cookie - thick 7 cm biscuit with four pressed-in chip wells.

The plan asks for a Ø7 cm round biscuit 0.9 cm thick with four Ø0.6 cm dimples.
A disc slab (``disc_grid``) gives the thickness and the rim in one closed shell:
the top is a polar height field that carries the dome, the four dents and the
crumbly wavy outline, while the underside is one flat fan.  The teeth of the
outline are a six-lobe radius modulation, not noise, so the model stays
deterministic.

The four dents sit on the 0.78 ring at 0/90/180/270 degrees, so each one lands
on sampled vertices and is a real minimum of the height field instead of a
sub-grid detail.  ``on_chip`` paints a slightly wider patch than the dent
itself, which is what makes the four chocolate chips readable at preview size
on a flat-shaded face grid; without that the dents are only a shading change.
"""

import math

from prop_lib import disc_grid

ID = 'cookie'
NAME_ZH = '饼干'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.07
CSV_GAME_LONGEST_M = 0.09
SOURCING = 'model ourselves：Ø7 cm、厚 0.9 cm 圆饼，4 个 Ø0.6 cm 凹坑'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.07
TRI_BUDGET = 200
MATERIALS = [
    {'key': 'dough', 'name_zh': '饼干面团', 'linear_rgb': [0.420, 0.240, 0.100],
     'roughness': 0.80},
    {'key': 'chip', 'name_zh': '巧克力豆', 'linear_rgb': [0.055, 0.030, 0.020],
     'roughness': 0.60},
]
NOTES = ('192 triangles: 24 segments, three rings (0.45 / 0.78 / 1.0).  The four '
         'dents sit exactly on the 0.78 ring at 0/90/180/270 degrees, 3 mm deep, '
         'so each one is a sampled minimum of the height field; the face-material '
         'split paints the four faces around each dent as chocolate.  The outline '
         'carries six shallow scallops (3.5 % of the radius) so the rim reads as '
         'crumbled rather than machined.')

SEGMENTS = 24
RING_FRACTIONS = [0.45, 0.78, 1.0]
RADIUS = 0.035
THICK_CENTRE = 0.0095
DOME = 0.0028
PIT_R = 0.78 * RADIUS
PIT_DEPTH = 0.0030
PIT_SIGMA = 0.0028
CHIP_RADIUS = 0.0075
PIT_ANGLES = [0.0, 0.5 * math.pi, math.pi, 1.5 * math.pi]
PITS = [(PIT_R * math.cos(a), PIT_R * math.sin(a)) for a in PIT_ANGLES]


def outline(angle):
    """Six shallow scallops so the edge reads as crumbled, not stamped."""
    return RADIUS * (1.0 + 0.035 * math.sin(6.0 * angle + 0.4))


def top(x, y):
    r2 = x * x + y * y
    z = THICK_CENTRE - DOME * (r2 / (RADIUS * RADIUS))
    for (px, py) in PITS:
        d2 = (x - px) ** 2 + (y - py) ** 2
        z -= PIT_DEPTH * math.exp(-d2 / (2.0 * PIT_SIGMA ** 2))
    return z


def on_chip(centre):
    for (px, py) in PITS:
        if (centre.x - px) ** 2 + (centre.y - py) ** 2 < CHIP_RADIUS ** 2:
            return True
    return False


def build(b):
    bm = disc_grid(SEGMENTS, RING_FRACTIONS, top, bottom_z=0.0, radius=RADIUS,
                   radius_fn=outline)
    b.add(bm, 'dough', (0.420, 0.240, 0.100), 0.80, smooth=False,
          extras=[('chip', (0.055, 0.030, 0.020), 0.60, on_chip)])
