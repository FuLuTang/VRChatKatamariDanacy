"""Bread slice - 10 x 10 x 1.5 cm slice with a brown crust ring.

PROP_SET_PLAN.md section 3.4 gives the shape as ``10x10x1.5 cm 片，四周一圈
面包皮（双色材质）``.  A toasted slice is a square with rounded corners, so the
outline is a superellipse (|x|^p + |y|^p = r^p with p = 5): at 0/90 degrees it is
exactly the 5 cm half-width a square slice needs, and the corners are rounded off
by about 15%, which is what stops it reading as a tile of toast-shaped cardboard.
A 1.2% six-lobe wobble is layered on top for the crumbly edge.

``disc_grid`` gives the closed slab: a sampled top surface, one flat underside
fan, and the outer rim.  The top domes from 11 mm at the rim to 15 mm at the
centre so the slice has a little body instead of being a card.  Crust is a
material, not geometry: every face whose centroid sits past 80% of the outline
radius is brown, so the rim and the outer top ring read as the crust ring.  The
threshold has to clear the ring boundary by a wide margin: a 20-segment quad's
centroid is only cos(9 deg) = 0.988 of its own radius, and the six-lobe rim
wobble moves the local outline by another 1.2%, so a 90% threshold painted a
patchy, alternating crust in the first draft.

The bottom is a single flat fan at z=0, which means the underside is crumb
coloured - the slice lies on it, so that face is never seen; the alternative
(mirroring the top grid) doubles the triangle count and buys nothing.
"""

import math

from prop_lib import disc_grid

ID = 'bread_slice'
NAME_ZH = '面包片'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.10
CSV_GAME_LONGEST_M = 0.30
SOURCING = ('model ourselves：10×10×1.5 cm 片，四周一圈面包皮'
            '（CSV 提示 Kenney bread.glb，本批自行建模）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.10
TRI_BUDGET = 200
MATERIALS = [
    {'key': 'crumb', 'name_zh': '面包瓤米白', 'linear_rgb': [0.640, 0.470, 0.250],
     'roughness': 0.85},
    {'key': 'crust', 'name_zh': '面包皮棕', 'linear_rgb': [0.230, 0.095, 0.035],
     'roughness': 0.60},
]
NOTES = ('20 segments, three top rings (0.55 / 0.86 / 1.0) and a flat underside '
         'fan: 160 triangles.  Outline is a superellipse with p = 5, half-width '
         '0.050 m and a 1.2% six-lobe rim wobble, so the slice is a rounded '
         'square 0.100 x 0.100 m.  Top height field runs 0.0110 m at the rim to '
         '0.0145 m at the centre; the flat underside is z=0.  Crust is the outer '
         '10% of the outline radius (rim faces and the top border band), crumb '
         'everywhere else; the hidden underside stays crumb.  One closed shell, '
         'no separate crust geometry.')

SEGMENTS = 20
RING_FRACTIONS = [0.55, 0.86, 1.0]
HALF = 0.050
POWER = 5.0
WOBBLE = 0.012
TOP_RIM = 0.0110
TOP_CENTRE = 0.0145
CRUST_FRACTION = 0.80


def outline(angle):
    c = abs(math.cos(angle))
    s = abs(math.sin(angle))
    r = HALF / ((c ** POWER + s ** POWER) ** (1.0 / POWER))
    return r * (1.0 + WOBBLE * math.sin(6.0 * angle + 0.5))


def top(x, y):
    t = 1.0 - (x * x + y * y) / (HALF * HALF)
    return TOP_RIM + (TOP_CENTRE - TOP_RIM) * max(0.0, t)


def is_crust(centre):
    r = math.hypot(centre.x, centre.y)
    return r > CRUST_FRACTION * outline(math.atan2(centre.y, centre.x))


def build(b):
    b.add(disc_grid(SEGMENTS, RING_FRACTIONS, top, bottom_z=0.0,
                    radius=HALF, radius_fn=outline),
          'crumb', (0.640, 0.470, 0.250), 0.85, smooth=False, name='slice',
          extras=[('crust', (0.230, 0.095, 0.035), 0.60, is_crust)])
