"""Glass bottle - 7 cm x 28 cm soda bottle, hollow with an open mouth.

PROP_CATALOG.md gives the design three-axis as 0.07 x 0.07 x 0.28, and the CSV
real longest edge is 0.28.  The profile runs up the outside from the punt to the
shoulder, the 2.2 cm neck and the lipped mouth, crosses the 3.5 mm rim, then
descends the inside to the inner floor.  That is one closed shell, not a solid
bottle with a painted dark disc: the mouth is a real opening and the cavity is
visible from the preview camera.

The body is 0.070 m at its widest, the neck 0.025 m, and the shoulder starts at
0.205 m.  Twelve segments and 18 profile points are 384 triangles; the segment count is
even on purpose, because an odd-count lathe has no vertex at x = -r and its
AABB would come out narrower than the 0.07 m diameter.  Smooth normals keep the
glass from reading as a faceted polygon.
"""

from prop_lib import lathe

ID = 'glass_bottle'
NAME_ZH = '玻璃瓶'
CATEGORY = 'drink'
CSV_REAL_LONGEST_M = 0.28
CSV_GAME_LONGEST_M = 0.52
SOURCING = 'model ourselves：Ø0.07 x 0.28 m 玻璃瓶，空心 + 开口'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.28
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'glass', 'name_zh': '深绿玻璃', 'linear_rgb': [0.025, 0.085, 0.035],
     'roughness': 0.18},
]
NOTES = ('12 segments, 18 profile points = 384 triangles.  Outside: foot 0.052 m, '
         'body 0.070 m from z=0.070 to 0.160, shoulder 0.205, neck 0.025, mouth '
         '0.029 at 0.280.  Wall 3 mm through the body, 2.5 mm at the neck, rim '
         '3.5 mm; inner floor 4 mm above the punt.  One closed shell with an '
         'open mouth, no separate cap or label.  Glass is a flat Principled '
         'base colour at roughness 0.18 - no transmission, since the batch is '
         'plain-colour by decision.')

PROFILE = [
    (0.0000, 0.0000),   # punt
    (0.0260, 0.0000),
    (0.0300, 0.0040),
    (0.0350, 0.0700),
    (0.0350, 0.1600),
    (0.0310, 0.2050),
    (0.0200, 0.2380),
    (0.0125, 0.2600),
    (0.0145, 0.2800),   # mouth outside
    (0.0110, 0.2800),   # rim inside
    (0.0100, 0.2620),
    (0.0200, 0.2300),
    (0.0280, 0.2050),
    (0.0320, 0.1600),
    (0.0320, 0.0700),
    (0.0300, 0.0250),
    (0.0260, 0.0040),
    (0.0000, 0.0040),   # inner floor
]


def build(b):
    bm = lathe(PROFILE, segments=12)
    b.add(bm, 'glass', (0.025, 0.085, 0.035), 0.18, smooth=True)
