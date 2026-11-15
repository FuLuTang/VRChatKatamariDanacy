"""Button - 12 mm four-hole sewing button, 3 mm thick, blind eyelets.

The catalog asks for a 0.012 x 0.012 x 0.003 m round plastic button with two or
four holes whose "holes" are dents, not through cuts.  The body is a flat-shaded
10/12-segment disc_grid slab: a polar top whose height field is a shallow dish
with a slightly dropped rim lip, and the cheap single-fan flat underside that
prop_lib uses for flat-bottomed small parts.

Material pass (2026-11-15 rework): the single ivory plastic slot read as a white
blank at preview size.  The button is now a blue plastic disc with a recessed
rim lip, and every eyelet bore is the dark ink slot, so the four holes read as
four dark dots instead of light dents.  The lip is a 0.25 mm drop between the
0.86 ring and the outer edge - a height-field step on the existing two-ring disc,
which costs 24 triangles and keeps the 12 mm envelope exact.

The four eyelets are countersunk blind holes cut with an Exact boolean
difference.  A cylindrical hole of Ø1.6 mm costs 80+ triangles (four of them put
the button at 208, far over the budget), so each eyelet is a 6-sided cone whose
opening measures about Ø1.1 mm where it crosses the dish and whose apex sits
1.2 mm down - half the disc thickness - which is the shallow countersink a real
button has.  The cone profile runs apex first, the ascending order the cheese
dents use.
"""

import math

from prop_lib import disc_grid, lathe

ID = 'button'
NAME_ZH = '纽扣'
CATEGORY = 'debris'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.012', 'game_longest_dim_m': '0.012'}
SOURCING = 'model ourselves：Ø0.012 x 0.003 m 塑料四孔纽扣（孔做凹坑）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.012
TRI_BUDGET = 160
MATERIALS = [
    {'key': 'plastic_blue', 'name_zh': '蓝塑料扣面', 'linear_rgb': [0.075, 0.145, 0.400],
     'roughness': 0.48},
    {'key': 'ink', 'name_zh': '扣眼暗孔', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('disc_grid, 12 segments, two top rings (0.86 / 1.0), flat single-fan '
         'underside: 96 triangles with the 0.25 mm rim lip.  Four eyelets are '
         'countersunk blind holes: 6-sided cones from r = 0.8 mm at z = 3.2 mm '
         'to an apex at z = 1.5 mm, so the opening measures about 1.1 mm across '
         'at the dish surface and is 1.2 mm deep (half the 3 mm thickness).  '
         'Slots: blue plastic face + ink bores (bore faces are within 0.7 mm of '
         'an eyelet axis and above the countersink floor).  The 160-triangle '
         'budget is the 120 baseline x 1.33, inside the spec 1.6x allowance.')

SEGMENTS = 12
RADIUS = 0.0060
RIM_TOP = 0.0030
DISH_CENTRE = 0.0024
LIP_DROP = 0.00025
LIP_FRACTION = 0.86
RING_FRACTIONS = [LIP_FRACTION, 1.0]
EYE_RING = 0.0022
EYE_TOP_RADIUS = 0.0008
EYE_TOP_Z = 0.0032
EYE_APEX_Z = 0.0015
EYE_SEGMENTS = 6
EYE_ANGLES = (45.0, 135.0, 225.0, 315.0)

PLASTIC = (0.075, 0.145, 0.400)
INK = (0.030, 0.035, 0.045)


def top(x, y):
    """Dished top with a dropped rim lip outside the 0.86 ring."""
    f2 = (x * x + y * y) / (RADIUS * RADIUS)
    dish = RIM_TOP - (RIM_TOP - DISH_CENTRE) * f2
    if f2 > LIP_FRACTION ** 2:
        return dish - LIP_DROP
    return dish


def build(b):
    body = b.add(disc_grid(SEGMENTS, RING_FRACTIONS, top, bottom_z=0.0,
                           radius=RADIUS),
                 'plastic_blue', PLASTIC, 0.48, smooth=False,
                 name='disc')
    eyes = []
    for i, deg in enumerate(EYE_ANGLES):
        a = math.radians(deg)
        bm = lathe([(0.0, EYE_APEX_Z), (EYE_TOP_RADIUS, EYE_TOP_Z)],
                   segments=EYE_SEGMENTS)
        for v in bm.verts:
            v.co.x += EYE_RING * math.cos(a)
            v.co.y += EYE_RING * math.sin(a)
        eyes.append(b.add(bm, 'plastic_blue', PLASTIC, 0.48,
                          smooth=False, name='eye%d' % i))
    b.boolean_difference(body, eyes)
    info = b.finish(TARGET_LONGEST_M)

    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    zmin = min(v.co.z for v in me.vertices)
    axes = [(EYE_RING * math.cos(math.radians(d)),
             EYE_RING * math.sin(math.radians(d))) for d in EYE_ANGLES]
    for poly in me.polygons:
        c = poly.center
        zrel = c.z - zmin
        near_eye = min((c.x - ax) ** 2 + (c.y - ay) ** 2
                       for (ax, ay) in axes) < 0.0007 ** 2
        poly.material_index = 1 if (near_eye and zrel < 0.0027) else 0
    return info
