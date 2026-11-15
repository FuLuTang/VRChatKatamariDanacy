"""Thermos - 30 cm vacuum flask: slim body, cup lid and a side handle.

The catalog gives 0.12 x 0.12 x 0.30 m (the game hint is a 0.35 x 0.90 m
revolved body plus a handle), and the user's note is explicit that this must
read as a tall thin cylinder rather than a box.  The body is a 0.106 m diameter
lathe with a 0.018 m waist, a shoulder to a 0.062 m neck and a 0.035 m cup lid;
the side handle is a 0.011 m tube unioned into the body, so the whole prop is
still one closed shell.  The handle adds about 8 mm on +X, which is why the
measured X is a little over the body diameter; the height stays the 0.30 m
longest edge.

The first shape that came to mind - a plain tall cylinder with a screw cap -
reads as a bottle, not a flask.  The cup lid step at 0.286 m and the D handle
are the two cues that make the silhouette say thermos.
"""

from mathutils import Vector

from prop_lib import lathe, tube

ID = 'thermos'
NAME_ZH = '保温壶'
CATEGORY = 'container'
CSV_REAL_LONGEST_M = 0.30
CSV_GAME_LONGEST_M = 0.90
SOURCING = 'model ourselves：Ø0.106 x 0.30 m 旋转体 + 杯盖 + 提手'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.30
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'body', 'name_zh': '保温壶红', 'linear_rgb': [0.420, 0.050, 0.035],
     'roughness': 0.40},
    {'key': 'cap', 'name_zh': '杯盖米白', 'linear_rgb': [0.640, 0.580, 0.440],
     'roughness': 0.50},
]
NOTES = ('16-segment body, 11 profile points, plus a 5-segment handle tube; '
         'about 368 triangles before the exact union and one shell after it.  '
         'Body 0.106 m diameter from z=0.018 to 0.210, shoulder to 0.266, neck '
         '0.062, cup lid 0.070 wide and 0.020 tall, total 0.300 m.  Handle '
         'centre-line at x=0.064, tube radius 5.5 mm, entering the body at '
         'z=0.055 and 0.205; it leaves a 5.5 mm finger gap.  Measured X is the '
         '0.106 m body plus the handle on one side, so it is ~0.122 m, not the '
         'catalog 0.12 m: the catalog value is the body diameter and cannot '
         'also include a usable handle.')

PROFILE = [
    (0.0000, 0.0000),   # base centre
    (0.0460, 0.0000),
    (0.0510, 0.0060),
    (0.0530, 0.0180),
    (0.0530, 0.2100),   # straight body
    (0.0420, 0.2500),   # shoulder
    (0.0320, 0.2660),
    (0.0310, 0.2800),   # neck
    (0.0350, 0.3000),   # cup lid
    (0.0280, 0.3005),
    (0.0000, 0.3005),   # lid top
]
HANDLE_POINTS = [
    Vector((0.0490, 0.0, 0.0550)),
    Vector((0.0600, 0.0, 0.0750)),
    Vector((0.0640, 0.0, 0.1300)),
    Vector((0.0600, 0.0, 0.1850)),
    Vector((0.0490, 0.0, 0.2050)),
]
HANDLE_RADIUS = 0.0055
CAP_ABOVE = 0.2830


def build(b):
    body = b.add(lathe(PROFILE, segments=16), 'body',
                 (0.420, 0.050, 0.035), 0.40, smooth=True, name='body')
    handle = b.add(tube(HANDLE_POINTS, [HANDLE_RADIUS] * 5, segments=5),
                   'body', (0.420, 0.050, 0.035), 0.40, smooth=True,
                   name='handle')
    b.boolean_union(body, [handle])

    # Re-assign materials after the union rather than trusting the modifier to
    # transfer the cup-lid material; the handle must stay body-coloured.
    me = body.data
    me.materials.clear()
    me.materials.append(b.material('body', (0.420, 0.050, 0.035), 0.40))
    me.materials.append(b.material('cap', (0.640, 0.580, 0.440), 0.50))
    for poly in me.polygons:
        poly.material_index = 1 if poly.center.z > CAP_ABOVE else 0
