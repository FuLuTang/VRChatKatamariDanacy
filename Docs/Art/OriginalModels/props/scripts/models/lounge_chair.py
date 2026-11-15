"""Lounge chair - 160 x 60 x 35 cm flat sun-lounger cot, dead flat deck.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_lounge_chair_01 as lounge_chair,
0.60 x 0.35 x 1.60 m with yaw = 90, the only structure in the level that is
rotated; its local three-axis is therefore 1.60 (length, local X) x 0.60 (depth,
local Y) x 0.35 (height), which is also LEVEL_CONSTRUCTION.md 4 P1's
1.60 x 0.60 x 0.35 with a 900 tris budget.  prop_catalog.csv has no row, so the
module declares DOC_ROW.

The level's one "step on it, then eat it" piece: the layout uses its 0.35 m top
as terrain (bed top 0.26 -> chair 0.35 -> table 0.70), so the deck is a single
dead-flat plane at z = 0.350 and NOTHING rises above it - no backrest, no arm
rests, no rolled pillow.  A recliner with a raised back would be the obvious
shape and would break the one thing this prop is for.

What carries the "lounger" read without height is the frame: two 1.52 m side
rails and four legs of 28 mm tube under a 1.60 x 0.60 canvas deck, plus two
cross braces.  The blanket-soft head end is a colour split in the deck itself -
the last 250 mm at -X is pale (a folded towel under the head) and coplanar with
the rest, so it costs nothing and keeps the top one plane.
"""

from prop_lib import prism, rounded_rect, tube

ID = 'lounge_chair'
NAME_ZH = '折叠躺椅'
CATEGORY = 'furniture'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / LEVEL_CONSTRUCTION.md 4 P1（真实三轴，无 CSV 行，布局里 yaw=90）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 1.60
TRI_BUDGET = 900
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '1.60', 'game_longest_dim_m': '1.60'}
MATERIALS = [
    {'key': 'canvas', 'name_zh': '躺面赭布', 'linear_rgb': [0.300, 0.115, 0.060],
     'roughness': 0.90},
    {'key': 'towel', 'name_zh': '头端米色布', 'linear_rgb': [0.620, 0.575, 0.470],
     'roughness': 0.92},
    {'key': 'frame', 'name_zh': '铝架银灰', 'linear_rgb': [0.550, 0.570, 0.580],
     'roughness': 0.35},
]
NOTES = ('Deck 1.600 x 0.600 x 0.040 (R 100 mm, 16-gon) from z 0.310 to 0.350, '
         'one flat plane on top.  Frame: two 28 mm rails along X at '
         'y +/-0.286, z 0.300, x +/-0.760; four 28 mm legs at '
         '(+/-0.660, +/-0.240) from z 0 to 0.322 (12 mm inside the deck); two '
         '24 mm cross braces at '
         'x +/-0.660, z 0.220.  AABB 1.600 x 0.600 x 0.350 (the deck corners '
         'reach +/-0.800 / +/-0.300).  Materials by face extent: frame below '
         'z 0.308, pale towel colour for the 1 mm head-end plate (x '
         '-0.780..-0.550, y +/-0.260, top also z 0.350), canvas elsewhere.')

DECK_L, DECK_D, DECK_T = 1.600, 0.600, 0.040
DECK_TOP = 0.350
DECK_Z0 = DECK_TOP - DECK_T
RAIL_Y, RAIL_Z, RAIL_R, RAIL_X = 0.286, 0.300, 0.014, 0.760
# Legs stop 12 mm INSIDE the deck: a tube cap is flat, so a leg that
# ends at the deck underside would leave the whole frame a separate
# closed shell (first pass exported 3 shells exactly like that).
LEG_X, LEG_Y, LEG_R, LEG_TOP = 0.660, 0.240, 0.014, 0.322
BRACE_Z, BRACE_R = 0.220, 0.012
TOWEL_W, TOWEL_D = 0.230, 0.520
TOWEL_CX = -0.665
TOWEL_X = TOWEL_CX + TOWEL_W * 0.5


def _extent(me, poly):
    xs = [me.vertices[i].co.x for i in poly.vertices]
    zs = [me.vertices[i].co.z for i in poly.vertices]
    return (min(xs), max(xs), min(zs))


def _rod(b, p0, p1, radius, mat, name, segments=6):
    return b.add(tube([p0, p1], [radius, radius], segments=segments),
                 mat['key'], mat['linear_rgb'], mat['roughness'], smooth=True,
                 name=name)


def build(b):
    canvas, towel, frame = MATERIALS
    body = b.add(prism(rounded_rect(DECK_L, DECK_D, 0.100,
                                    corner_segments=3), DECK_T,
                       start=DECK_Z0),
                 'canvas', canvas['linear_rgb'], canvas['roughness'],
                 smooth=False, name='deck')
    parts = []
    for side in (-1.0, 1.0):
        parts.append(_rod(b, (-RAIL_X, side * RAIL_Y, RAIL_Z),
                          (RAIL_X, side * RAIL_Y, RAIL_Z), RAIL_R, frame,
                          'rail%d' % (side > 0)))
        for sx in (-1.0, 1.0):
            parts.append(_rod(b,
                              (sx * LEG_X, side * LEG_Y, 0.0),
                              (sx * LEG_X, side * LEG_Y, LEG_TOP),
                              LEG_R, frame,
                              'leg%d%d' % (sx > 0, side > 0)))
    for sx in (-1.0, 1.0):
        parts.append(_rod(b, (sx * LEG_X, -LEG_Y, BRACE_Z),
                          (sx * LEG_X, LEG_Y, BRACE_Z), BRACE_R, frame,
                          'brace%d' % (sx > 0)))
    # The pale head-end band is a 1 mm coplanar plate, not a material split of
    # the deck: a single 16-gon top face cannot be split into a band, and a
    # plate whose own top is z = 0.350 keeps the walkable plane flat.
    towel_plate = b.add(prism([(x + TOWEL_CX, y) for (x, y) in
                               rounded_rect(TOWEL_W, TOWEL_D, 0.060,
                                            corner_segments=2)], 0.001,
                              start=DECK_TOP - 0.001),
                        'towel', towel['linear_rgb'], towel['roughness'],
                        smooth=False, name='towel')
    parts.append(towel_plate)
    b.boolean_union(body, parts)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        x0, x1, z0 = _extent(me, poly)
        if z0 < DECK_Z0 - 0.002:
            poly.material_index = 2
        elif z0 > DECK_TOP - 0.0015 and x1 <= TOWEL_X + 0.0005:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return b.finish(TARGET_LONGEST_M)
