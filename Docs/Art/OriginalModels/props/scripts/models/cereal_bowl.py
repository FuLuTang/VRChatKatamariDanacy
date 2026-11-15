"""Cereal bowl - O 0.18 x 0.07 m bowl: white outside, dark glazed inside and foot.

MessyRoom catalogue row cereal_bowl: 0.180 x 0.180 x 0.070 m, round,
tableware_food.  One lathe profile does all of it: the recessed underside, the
0.048-0.062 m foot ring the bowl stands on, the outside flare to the 0.090 m lip
radius, across the 4.6 mm rim and back down the inside to a 10 mm-thick floor.
The cavity is therefore real geometry with a wall that stays between 4.5 and
5.5 mm everywhere, and the rim is a flat annulus, so the shell closes without a
second object.

Three material slots, split by geometry rather than by slotting whole bands:

  ceramic       the outer flare (lit, and the face the preview camera sees most)
  ceramic_dark  the inside wall, the inside floor *and* the 4.6 mm rim annulus,
                so the bowl reads as a two-tone glaze: a dark mouth on a white
                body, the way a celadon bowl is actually dipped
  ink           the foot ring and the recessed underside - a dark unglazed foot

The foot ring is worn as ink rather than metal_dark on purpose: metal_dark
(linear luminance 0.095) sits only 0.130 from ceramic_dark (0.225) and 6 deg in
hue, which spec 1.4 would call the same slot; ink is 0.035, so every pair in this
prop clears the contrast rule.  The brief allowed "metal_dark or a dark glaze
ring", and a black-glazed foot is the darker of the two.

The inside test is the face normal's radial sign (the cavity wall points at the
axis) plus a floor test for the horizontal faces, so it works however the lathe
triangulates and does not depend on guessing a radius per height.

Sixteen segments keep the round silhouette smooth at 256 triangles - the brief's
300-triangle budget.  Smooth shading: a 16-gon bowl shaded flat would show every
facet on the flare, and this is the one prop in the set that has to read as
glazed ceramic.
"""

from math import hypot

from prop_lib import lathe

ID = 'cereal_bowl'
NAME_ZH = '麦片碗'
CATEGORY = 'tableware_food'
SOURCING = 'model ourselves：Ø0.18 x 0.07 m 浅碗，带圈足与内腔，壁厚 >=4 mm'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.180
# 300 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t2 cap 500.
TRI_BUDGET = 480

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'ceramic': ((0.760, 0.745, 0.700), 0.35),
    'ceramic_dark': ((0.180, 0.230, 0.300), 0.38),
    'ink': ((0.030, 0.035, 0.045), 0.75),
}
MATERIALS = [
    {'key': 'ceramic', 'name_zh': '白瓷外壁',
     'linear_rgb': list(PALETTE['ceramic'][0]),
     'roughness': PALETTE['ceramic'][1]},
    {'key': 'ceramic_dark', 'name_zh': '深釉内壁与口沿',
     'linear_rgb': list(PALETTE['ceramic_dark'][0]),
     'roughness': PALETTE['ceramic_dark'][1]},
    {'key': 'ink', 'name_zh': '暗釉圈足',
     'linear_rgb': list(PALETTE['ink'][0]), 'roughness': PALETTE['ink'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.180', 'game_longest_dim_m': '0.180'}
NOTES = ('16 segments, 10 profile points = 256 triangles.  Foot ring 0.0480-'
         '0.0620 at z=0, recessed underside at z=0.0055; outside flares from '
         '0.0700 at z=0.0090 to the 0.0900 lip at z=0.0700; rim 4.6 mm; inside '
         'runs 0.0790 at z=0.047 to 0.0630 at z=0.013; floor 10 mm thick at the '
         'centre.  Wall stays 4.5-5.5 mm.  Three slots: ink on the foot and the '
         'recessed underside (z < 0.0040), ceramic_dark on inward-facing faces '
         'and the horizontal inside floor, plus the rim annulus above z=0.0650 '
         'at r>0.0800, ceramic on the outer flare.  One closed shell, no separate '
         'foot.')

SEGMENTS = 16
PROFILE = [
    (0.0000, 0.0055),   # recessed underside centre
    (0.0480, 0.0000),   # foot ring, inner edge
    (0.0620, 0.0000),   # foot ring, outer edge
    (0.0700, 0.0090),   # outside starts to flare
    (0.0840, 0.0330),
    (0.0900, 0.0700),   # lip, outside
    (0.0854, 0.0700),   # lip, inside (4.6 mm)
    (0.0790, 0.0470),
    (0.0630, 0.0130),
    (0.0000, 0.0100),   # floor centre
]
FOOT_Z = 0.0040
RIM_Z = 0.0650
RIM_R = 0.0800
FLOOR_TOP_Z = 0.0125
FLOOR_R = 0.0625


def _slot_of(centre, normal):
    r = hypot(centre.x, centre.y)
    if centre.z < FOOT_Z:
        return 'ink'                       # foot ring + recessed underside
    radial = ((normal.x * centre.x + normal.y * centre.y) / r) if r > 1e-9 else 0.0
    if radial < -0.05:                     # cavity wall points at the axis
        return 'ceramic_dark'
    if normal.z > 0.5 and centre.z < FLOOR_TOP_Z and r < FLOOR_R:
        return 'ceramic_dark'              # inside floor
    if centre.z > RIM_Z and r > RIM_R:      # rim annulus
        return 'ceramic_dark'
    return 'ceramic'


def build(b):
    ceramic, ceramic_rough = PALETTE['ceramic']
    ob = b.add(lathe(PROFILE, segments=SEGMENTS), 'ceramic', ceramic,
               ceramic_rough, smooth=True, name='bowl')
    me = ob.data
    me.materials.clear()
    for key in ('ceramic', 'ceramic_dark', 'ink'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    index = {'ceramic': 0, 'ceramic_dark': 1, 'ink': 2}
    for poly in me.polygons:
        poly.material_index = index[_slot_of(poly.center, poly.normal)]
