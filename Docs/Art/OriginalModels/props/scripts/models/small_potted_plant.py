"""Small potted plant - 14 cm tapered pot with six leaves, 22 cm overall.

PROP_SET_PLAN.md section 3.4: ``Ø14x12 cm 锥台盆 + 22 cm 高叶丛（5-7 片薄叶）``.
The pot is a hollow tapered lathe (0.10 m across the base, 0.14 m at the rim,
0.12 m tall) whose cavity is only 16 mm deep: the solid above the pot floor is
the soil, so the soil surface is real geometry with no second object.  Six
leaves grow from buried bases at z=0.096 (inside the soil solid) and arc up to
z=0.220; each leaf is a swept flat lens section 23 mm wide and 2.6 mm thick at
its widest, tapering to a 2 mm tip.

Leaves are placed 60 degrees apart starting at 15 degrees, so the blade tips stay
inside the pot's 0.070 m radius (tip reach 0.059 m) and the AABB is exactly
0.14 x 0.14 x 0.22 m.  Each leaf arcs up to a 0.220 m tip
while standing 58 mm out, with a 23 mm wide blade that tapers over its last 25%
- a drooping tip made the first revision's blades end in a flat, cut-off ribbon.
Six leaves with round bases would collide at the centre, so the bases sit on a
12 mm ring instead: 12.6 mm of arc between neighbours for a 9 mm wide base, which
keeps the exact unions clean.

Materials are assigned *after* the unions: the boolean modifier maps the tool's
own material onto the target's first slot, so six leaves came out terracotta in
the first draft.  The rule is geometric - above the rim (z > 0.1215) or above the
soil inside r < 0.045 is leaf, the flat soil disc at z = 0.104 is soil, everything
else is pot.

One closed shell after the unions.
"""

import math

from mathutils import Vector

from prop_lib import lathe, tube

ID = 'small_potted_plant'
NAME_ZH = '小盆栽'
CATEGORY = 'garden'
CSV_REAL_LONGEST_M = 0.22
CSV_GAME_LONGEST_M = 0.55
SOURCING = ('model ourselves：Ø14×12 cm 锥台盆 + 22 cm 高叶丛'
            '（CSV 提示 Kenney pot_small + flower_redA，本批自行建模）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.22
TRI_BUDGET = 500
MATERIALS = [
    {'key': 'pot', 'name_zh': '陶土盆', 'linear_rgb': [0.420, 0.160, 0.080],
     'roughness': 0.80},
    {'key': 'soil', 'name_zh': '盆土深褐', 'linear_rgb': [0.075, 0.048, 0.028],
     'roughness': 0.95},
    {'key': 'leaf', 'name_zh': '叶片绿', 'linear_rgb': [0.120, 0.230, 0.060],
     'roughness': 0.65},
]
NOTES = ('Pot: 16-segment, 8-point lathe, base 0.090 m, rim 0.140 m at z=0.120, '
         '4 mm rim wall, soil surface at z=0.104: 192 triangles.  Six leaves: '
         '5-segment flat lens sweep with 4 stations each (40 triangles), bases '
         'on a 12 mm radius at z=0.096, rising to tips at r=0.058 and 0.198-0.220 m '
         '(one leaf at the full 0.220 m, the rest scaled 0.90-0.98 so the '
         'cluster is not a fan of identical blades); '
         'azimuths 15/75/135/195/255/315 degrees, welded with exact unions.  Materials are re-assigned by '
         'position after the union (leaf above the rim or above the soil inside '
         'r<0.045).  Total about 450 triangles, one closed shell.')

SEGMENTS = 14
POT_PROFILE = [
    (0.0000, 0.0000),   # base centre
    (0.0450, 0.0000),   # base disc, 0.090 m across
    (0.0470, 0.0040),   # base fillet
    (0.0700, 0.1170),   # outer wall, 0.140 m at the rim
    (0.0675, 0.1200),   # rim top
    (0.0635, 0.1135),   # inner rim
    (0.0560, 0.1040),   # inner wall down to the soil
    (0.0000, 0.1040),   # soil surface
]
LEAF_AZIMUTHS = [15.0, 75.0, 135.0, 195.0, 255.0, 315.0]
# Six leaves reaching the same height look like a fan; one leaf keeps the full
# 0.220 m, the rest stop short by 2-10%, so the AABB is still 0.22 m tall.
LEAF_HEIGHT_SCALE = [1.00, 0.93, 0.97, 0.90, 0.95, 0.98]
LEAF_PATH = [(0.0120, 0.0960), (0.0300, 0.1580), (0.0480, 0.2000),
             (0.0580, 0.2200)]
LEAF_HALF_WIDTH = [0.0050, 0.0115, 0.0088, 0.0010]
LEAF_HALF_THICK = [0.0013, 0.0013, 0.0010, 0.0004]
LEAF_SEGMENTS = 5


def _leaf_section(index, angle, radius):
    a = LEAF_HALF_WIDTH[index]
    b = LEAF_HALF_THICK[index]
    c, s = math.cos(angle), math.sin(angle)
    return a * b / math.sqrt((b * c) ** 2 + (a * s) ** 2)


def leaf_points(azimuth_deg, height_scale=1.0):
    ca = math.cos(math.radians(azimuth_deg))
    sa = math.sin(math.radians(azimuth_deg))
    base = LEAF_PATH[0][1]
    return [Vector((r * ca, r * sa, base + (z - base) * height_scale))
            for (r, z) in LEAF_PATH]


def build(b):
    pot = b.add(lathe(POT_PROFILE, segments=SEGMENTS), 'pot',
                (0.420, 0.160, 0.080), 0.80, smooth=False, name='pot')

    leaves = []
    for i, az in enumerate(LEAF_AZIMUTHS):
        bm = tube(leaf_points(az, LEAF_HEIGHT_SCALE[i]),
                  [1.0] * len(LEAF_PATH),
                  segments=LEAF_SEGMENTS, section_fn=_leaf_section)
        leaves.append(b.add(bm, 'leaf', (0.120, 0.230, 0.060), 0.65,
                            smooth=True, name='leaf%d' % i))
    b.boolean_union(pot, leaves)

    # The union maps every leaf face onto the target's first slot (pot), so the
    # materials are re-assigned by position afterwards: leaf = anything above the
    # rim, or above the soil while still inside r=0.045; soil = the flat disc at
    # z=0.104; pot = the rest (base, wall, rim).
    me = pot.data
    me.materials.clear()
    me.materials.append(b.material('pot', (0.420, 0.160, 0.080), 0.80))
    me.materials.append(b.material('soil', (0.075, 0.048, 0.028), 0.95))
    me.materials.append(b.material('leaf', (0.120, 0.230, 0.060), 0.65))
    for poly in me.polygons:
        c = poly.center
        r = math.hypot(c.x, c.y)
        if c.z > 0.1215 or (c.z > 0.1045 and r < 0.045):
            poly.material_index = 2
        elif 0.1035 <= c.z <= 0.1045 and r < 0.056:
            poly.material_index = 1
        else:
            poly.material_index = 0
