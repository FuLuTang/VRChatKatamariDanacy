"""Keychain - a small plastic fob welded to a solid metal key ring.

MessyRoom catalogue t0 / flat / 0.070 x 0.030 x 0.008 m; no catalog row, so the
module carries ``DOC_ROW``.

The ring is a real washer (``annulus``): closed, with a hole through it, not a
split ring bent into a circle.  It is welded to the tag with an Exact boolean
union, and the two shapes overlap by 3.5 mm along the X axis while the tag stops
2.3 mm short of the ring's hole - so the union is a single shell and the hole
stays open instead of being plugged by the tag's end.

Three material slots (2026-11-15 material pass):

* ``plastic_red`` - the fob body;
* ``metal`` - the key ring;
* ``ink`` - the raised label plate on the fob (a real 0.5 mm step, so the print
  reads as an embossed block, not as a flat colour change) and the inside wall
  of the ring's hole, which is the one part of the ring you look *through*.

The tag is a four-ring ``stack`` instead of a plain prism: the last two rings
step in to 36 x 18 mm, which is the label plate.  That keeps the tag one closed
solid with no boolean of its own, and the 8 mm catalogue height is unchanged -
the plate is taken out of the tag's thickness, not added on top.
"""

import bmesh

from models._room_shapes import annulus, stack
from prop_lib import rounded_rect

ID = 'keychain'
NAME_ZH = '钥匙扣'
CATEGORY = 'tools_hardware'
SOURCING = 'model ourselves：挂牌 + 实心钥匙圈，布尔并集成单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.070
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.070', 'game_longest_dim_m': '0.070'}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '红挂牌', 'linear_rgb': [0.480, 0.075,
                                                              0.060],
     'roughness': 0.48},
    {'key': 'metal', 'name_zh': '磨砂钢圈', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'ink', 'name_zh': '挂牌标签/孔内壁', 'linear_rgb': [0.030, 0.035,
                                                            0.045],
     'roughness': 0.75},
]
NOTES = ('8-segment closed washer (outer R 15 mm, hole R 9.2 mm) welded to an '
         '8-gon rounded tag by Exact boolean union: one shell, hole open, '
         '3.5 mm of overlap.  Tag 0.050 x 0.028 x 0.008, ring 0.030 across and '
         '5 mm thick, total 0.070 x 0.030 x 0.008 m.  The tag is a 4-ring stack '
         'whose top 0.5 mm steps in to 36 x 18 mm and carries ink, so the label '
         'is an embossed block; the ring hole inner wall is ink too.')

TAG_CENTRE_X = -0.0125
RING_CENTRE_X = 0.0240
RING_R_OUT = 0.0150
RING_R_IN = 0.0092
TAG_Z = 0.0080
LABEL_Z = 0.0075
RING_Z = 0.0050
LABEL_W = 0.036
LABEL_D = 0.018
LABEL_DX = -0.0030


def tag_stack():
    body = rounded_rect(0.050, 0.028, 0.006, corner_segments=1)
    label = rounded_rect(LABEL_W, LABEL_D, 0.004, corner_segments=1)
    body = [(x + TAG_CENTRE_X, y) for (x, y) in body]
    label = [(x + TAG_CENTRE_X + LABEL_DX, y) for (x, y) in label]
    return stack([
        (0.0, body),
        (LABEL_Z, body),
        (LABEL_Z, label),
        (TAG_Z, label),
    ])


def is_label(centre):
    return centre.z > LABEL_Z + 0.0002


def is_hole_wall(centre):
    r = ((centre.x - RING_CENTRE_X) ** 2 + centre.y ** 2) ** 0.5
    return r < RING_R_IN + 0.0008


def build(b):
    tag_ob = b.add(tag_stack(), 'plastic_red', (0.480, 0.075, 0.060), 0.48,
                   smooth=False, name='tag',
                   extras=[('ink', (0.030, 0.035, 0.045), 0.75, is_label)])

    ring = annulus(RING_CENTRE_X, 0.0, 0.0, RING_Z, RING_R_OUT, RING_R_IN,
                   segments=8)
    ring_ob = b.add(ring, 'metal', (0.430, 0.450, 0.470), 0.32, smooth=False,
                    name='ring',
                    extras=[('ink', (0.030, 0.035, 0.045), 0.75,
                             is_hole_wall)])

    b.boolean_union(tag_ob, [ring_ob])
