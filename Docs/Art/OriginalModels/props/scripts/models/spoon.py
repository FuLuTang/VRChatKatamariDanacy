"""Spoon - 18 cm spoon: shallow oval bowl plus a flat tapered handle.

PROP_SET_PLAN.md section 3.4: ``18 cm 长，柄宽 1.2，头部 3.5x5 cm 浅凹``.  The
bowl is a 10-point lathe revolved around Z and then squashed to 70% in X, which
turns the 5 cm circle into the 3.5 x 5 cm oval the plan asks for; its rim sits at
11.5 mm and its well floor at 5.6 mm, so the dish is 5.9 mm deep and the bowl
reads as a dish instead of a paddle.  The handle is a swept flat section (6 mm
half-width at the bowl tapering to 5.6 mm) whose centreline rises a little off
the table, and it starts 13 mm inside the bowl so the exact union welds the two
parts with no cap buried in the joint.

The bowl's underside is flat at z=0: a real spoon rests on the rim and the
belly, not on a knife edge, and a flat contact keeps the prop from floating.
"""

import math

from mathutils import Vector

from prop_lib import lathe, tube

ID = 'spoon'
NAME_ZH = '勺子'
CATEGORY = 'tableware'
CSV_REAL_LONGEST_M = 0.18
CSV_GAME_LONGEST_M = 0.34
SOURCING = ('model ourselves：18 cm 长，柄宽 1.2 cm，头部 3.5×5 cm 浅凹'
            '（CSV 提示 Kenney utensil-spoon.glb）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.18
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'steel', 'name_zh': '不锈钢', 'linear_rgb': [0.560, 0.570, 0.580],
     'roughness': 0.28},
]
NOTES = ('Bowl: 16-segment, 8-point lathe scaled to 70% in X = 0.035 x 0.050 m '
         'oval, rim 0.0115 m, well floor 0.0056 m (5.9 mm deep), underside flat '
         'at z=0.  Handle: 5 stations, 6-segment flat elliptical sweep from the '
         'bowl centre to y=-0.155, half-width 0.0075 -> 0.0032 m, half-thickness '
         '0.0018 -> 0.0010 m, welded by an exact union.  Total length 0.180 m '
         'along Y, one closed shell, about 250 triangles before the union.')

BOWL_PROFILE = [
    (0.0000, 0.0000),   # underside centre
    (0.0120, 0.0000),   # flat contact patch
    (0.0230, 0.0020),   # underside rises to the rim
    (0.0250, 0.0100),   # outer rim
    (0.0245, 0.0115),   # rim top
    (0.0225, 0.0085),   # inside wall
    (0.0100, 0.0058),   # well floor
    (0.0000, 0.0056),   # well centre
]
BOWL_SEGMENTS = 16
BOWL_X_SCALE = 0.70

HANDLE_POINTS = [
    Vector((0.0, -0.020, 0.0045)),
    Vector((0.0, -0.060, 0.0042)),
    Vector((0.0, -0.110, 0.0038)),
    Vector((0.0, -0.145, 0.0032)),
    Vector((0.0, -0.155, 0.0029)),
]
HANDLE_HALF_WIDTH = [0.0075, 0.0070, 0.0062, 0.0056, 0.0032]
HANDLE_HALF_THICK = [0.0018, 0.0018, 0.0017, 0.0016, 0.0010]


def _flat_section(index, angle, radius):
    """Elliptical section: wide across the table, thin vertically."""
    a = HANDLE_HALF_WIDTH[index]
    b = HANDLE_HALF_THICK[index]
    c, s = math.cos(angle), math.sin(angle)
    return a * b / math.sqrt((b * c) ** 2 + (a * s) ** 2)


def build(b):
    bowl_bm = lathe(BOWL_PROFILE, segments=BOWL_SEGMENTS)
    for v in bowl_bm.verts:
        v.co.x *= BOWL_X_SCALE
    bowl = b.add(bowl_bm, 'steel', (0.560, 0.570, 0.580), 0.28, smooth=True,
                 name='bowl')

    handle = b.add(tube(HANDLE_POINTS, [1.0] * len(HANDLE_POINTS),
                        segments=6, section_fn=_flat_section),
                   'steel', (0.560, 0.570, 0.580), 0.28, smooth=True,
                   name='handle')
    b.boolean_union(bowl, [handle])
