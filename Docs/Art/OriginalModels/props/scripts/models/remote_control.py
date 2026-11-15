"""TV remote - rounded bar with a direction cross, 12 round keys, sensor window.

MessyRoom catalogue t2 / long / 0.180 x 0.050 x 0.020 m; no catalog row, so the
module carries ``DOC_ROW``.

A height field cannot resolve the keypad: at the 10 x 6 resolution the triangle
budget allows, a 5 mm key is smaller than a grid cell and the keys smear into a
soft ridge (the first draft read as a bar of soap).  So the keypad is real
geometry - twelve 5 mm round keys 1.2 mm proud, plus a plus-shaped direction
cross, all in *one* merged boolean tool so a single Exact union welds them onto
the body - and the sensor window is a rectangular pocket boolean-subtracted
from the front end.  With the single plastic slot the window has to read through
shadow rather than colour, which is what a pocket with vertical walls does in
flat shading.

Four material slots (2026-11-15 material pass) - the prop used to be one grey
plastic, which is exactly the "material not used" the owner flagged:

* ``plastic_grey`` - the body;
* ``plastic_yellow`` - the twelve 5 mm keys, one brightness step above the body
  so the keypad is legible straight away;
* ``metal`` - the plus-shaped direction cross: one step brighter again than the
  body (0.447 vs 0.234 linear, a 0.21 luminance gap).  The palette has no light
  grey plastic, and ``stone`` at 0.33 would sit only 0.095 away from the body
  grey - close enough that spec 1.4 counts it as the same slot.  A metal dome
  pad on a plastic remote is also what the parts look like;
* ``ink`` - the sensor window: the pocket is cut with the ink tool and the
  boolean transfers that material to the pocket walls, so the window reads as a
  dark screen instead of a shadow.

The lead raised this prop's budget to 320 triangles for exactly this reason; the
catalogue's 220 could only buy a featureless bar.
"""

import bmesh

from models._room_shapes import merge_bmeshes
from prop_lib import prism, rounded_rect

ID = 'remote_control'
NAME_ZH = '遥控器'
CATEGORY = 'electronics_cables'
SOURCING = 'model ourselves：圆角长条 + 12 个 Ø5 圆键 + 十字方向键 + 深色窗袋'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.180
TRI_BUDGET = 320
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.180', 'game_longest_dim_m': '0.180'}
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '机身灰', 'linear_rgb': [0.230, 0.235,
                                                             0.240],
     'roughness': 0.50},
    {'key': 'plastic_yellow', 'name_zh': '按键黄', 'linear_rgb': [0.640, 0.450,
                                                               0.060],
     'roughness': 0.48},
    {'key': 'metal', 'name_zh': '方向键金属环', 'linear_rgb': [0.430, 0.450,
                                                            0.470],
     'roughness': 0.32},
    {'key': 'ink', 'name_zh': '感应窗墨色', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.35},
]
NOTES = ('8-gon rounded-rect body 0.180 x 0.050 x 0.018 m; twelve 5 x 5 mm keys '
         'raised 1.2 mm, a 38 x 38 mm plus-shaped direction cross, and a 20 x '
         '22 mm sensor pocket 8 mm deep at the +X end.  Keys and cross are one '
         'merged tool -> one Exact union; the pocket is one difference.  The '
         'keys are square rather than round: 6-sided keys read the same at '
         '512 px but push the mesh to 402 triangles, over the 320 budget.  '
         'Slots: grey body, yellow keys, metal direction cross, ink window (the '
         'pocket tool carries the ink material, so the cut faces are ink).')

BODY_TOP = 0.0180
KEY_H = 0.0012
KEY_R = 0.0025
KEY_SEGMENTS = 4
PAD_X = (-0.070, -0.048, -0.026, -0.004)
PAD_Y = (-0.014, 0.0, 0.014)
CROSS = (0.036, 0.0)
CROSS_REACH = 0.019
CROSS_HALF = 0.0045
WINDOW_X = (0.060, 0.080)
WINDOW_Y = 0.011
WINDOW_FLOOR = 0.0100

PLUS = [(-CROSS_REACH, -CROSS_HALF), (-CROSS_HALF, -CROSS_HALF),
        (-CROSS_HALF, -CROSS_REACH), (CROSS_HALF, -CROSS_REACH),
        (CROSS_HALF, -CROSS_HALF), (CROSS_REACH, -CROSS_HALF),
        (CROSS_REACH, CROSS_HALF), (CROSS_HALF, CROSS_HALF),
        (CROSS_HALF, CROSS_REACH), (-CROSS_HALF, CROSS_REACH),
        (-CROSS_HALF, CROSS_HALF), (-CROSS_REACH, CROSS_HALF)]


def box(bm, cx, cy, hx, hy, z0, z1):
    """Append one closed box to ``bm`` (a cutter)."""
    res = bmesh.ops.create_cube(bm, size=1.0)
    for v in res['verts']:
        v.co.x = cx + v.co.x * 2.0 * hx
        v.co.y = cy + v.co.y * 2.0 * hy
        v.co.z = z0 + (v.co.z + 0.5) * (z1 - z0)


def key(bm, cx, cy):
    """One 4-sided key, raised KEY_H proud of the body top."""
    try:
        res = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False,
                                    segments=KEY_SEGMENTS, radius1=KEY_R,
                                    radius2=KEY_R, depth=KEY_H)
    except TypeError:  # pre-3.0 argument names
        res = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False,
                                    segments=KEY_SEGMENTS,
                                    diameter1=KEY_R * 2.0,
                                    diameter2=KEY_R * 2.0, depth=KEY_H)
    for v in res['verts']:
        v.co.x += cx
        v.co.y += cy
        v.co.z += BODY_TOP + KEY_H * 0.5


def is_cross(centre):
    """Everything of the merged key tool that sits at the cross, not the keypad."""
    return centre.x > 0.020


def build(b):
    body = prism(rounded_rect(0.180, 0.050, 0.012, corner_segments=1), BODY_TOP)
    body_ob = b.add(body, 'plastic_grey', (0.230, 0.235, 0.240), 0.50,
                    smooth=False, name='body')

    keys = bmesh.new()
    for cx in PAD_X:
        for cy in PAD_Y:
            key(keys, cx, cy)
    cross = prism(PLUS, KEY_H, start=BODY_TOP)
    bmesh.ops.translate(cross, vec=(CROSS[0], CROSS[1], 0.0),
                        verts=cross.verts[:])
    keys_ob = b.add(merge_bmeshes([keys, cross]), 'plastic_yellow',
                    (0.640, 0.450, 0.060), 0.48, smooth=False, name='keys',
                    extras=[('metal', (0.430, 0.450, 0.470), 0.32,
                             is_cross)])

    window = bmesh.new()
    box(window, (WINDOW_X[0] + WINDOW_X[1]) * 0.5, 0.0,
        (WINDOW_X[1] - WINDOW_X[0]) * 0.5, WINDOW_Y, WINDOW_FLOOR,
        BODY_TOP + 0.004)
    window_ob = b.add(window, 'ink', (0.030, 0.035, 0.045), 0.35,
                      smooth=False, name='window')

    b.boolean_union(body_ob, [keys_ob])
    b.boolean_difference(body_ob, [window_ob])
