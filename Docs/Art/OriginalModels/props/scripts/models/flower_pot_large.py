"""Large flower pot - O 34 top / O 24 base / 40 cm tall, empty, thick rim.

PROP_SET_PLAN.md section 3.5: 0.34 x 0.34 x 0.40 m, "O 34 top / O 24 base / 40 cm
tall tapered pot, rim thickened by 2 cm, empty".  Sixteen-segment lathe, flat
shaded, 288 triangles against the 400 budget.

The profile is the whole design: a straight taper from O 34 at the lip to O 24 at
the base with a small base fillet, a 2 cm thick flat lip - the rim is the one
detail that says "terracotta pot" from above - a 8-10 mm wall so the inside reads
as a real cavity, and a 22 mm floor.  The inside stays empty: no soil, no plant,
no drainage hole (a through-hole would make the shell genus-1 for no visual gain
at 0.40 m).  The bottom is flat at z = 0 so the pot can stand as a 40 cm step or
sit on the blanket without sinking.

The catalogue sources the shape from Kenney's pot_large.glb, which has a plant in
it; this is the empty pot the design asks for, made here rather than copied, so
the same pot can later take any plant without a second model.
"""

from prop_lib import lathe

ID = 'flower_pot_large'
NAME_ZH = '大花盆'
CATEGORY = 'garden'
CSV_REAL_LONGEST_M = 0.40
CSV_GAME_LONGEST_M = 1.00
SOURCING = 'Kenney Nature Kit 2.1 pot_large.glb（先看图确认无花）→ 空盆重画'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.40
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'terracotta', 'name_zh': '陶土砖红', 'linear_rgb': [0.400, 0.155, 0.075],
     'roughness': 0.88},
]
NOTES = ('Ten-point lathe profile, 16 segments: base 0 -> 0.118, fillet to 0.123 '
         'at z 0.010, outer wall to 0.170 at z 0.398, flat 2 cm lip at z 0.400 '
         '(outer 0.170, inner 0.152), inner wall down to 0.115 at z 0.022, flat '
         'floor to the axis.  Wall 8-18 mm, floor 22 mm.  AABB 0.340 x 0.340 x '
         '0.400, foot ring on z = 0, no plant and no drainage hole.')

PROFILE = [
    (0.000, 0.000),
    (0.118, 0.000),
    (0.123, 0.010),
    (0.162, 0.380),
    (0.170, 0.398),
    (0.170, 0.400),
    (0.152, 0.400),
    (0.152, 0.392),
    (0.115, 0.022),
    (0.000, 0.022),
]


def build(b):
    b.add(lathe(PROFILE, segments=16), 'terracotta',
          MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
          smooth=False, name='pot')
    return b.finish(TARGET_LONGEST_M)
