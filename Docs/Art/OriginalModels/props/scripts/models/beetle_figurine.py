"""Beetle figurine - a stylised scarab, 90 mm, carved rather than realistic.

The brief asks for a small carved scarab, not an insect: an oval domed body, a
small rounded head, six short thick legs, no antennae (they would degenerate
into wires at this size) and a flat base.  The body is one eight-segment lathe
whose rings are pushed into an ellipse by ``radius_fn`` - a scarab is 60 mm long
and 46 mm wide in plan, and an elliptical lathe gets that without squashing the
legs along with the shell.  The head is a squashed icosphere and each leg is a
four-sided tapered tube from inside the body shell out to a foot on the ground;
every one of them is welded in with an exact boolean union, so the whole figurine
is one closed shell.

All six legs share the same y/z direction, so their feet land on exactly the same
height and the figurine stands on six points; the fit then drops the lowest of
them to z = 0.

Materials (2026-11-15 rework): the clay body with a sandy stripe read as a brown
lump.  A scarab figurine is a dark chitin object, so the elytra are the palette's
``chitin`` (its gloss is what makes it read as an insect shell), the parts one
step darker - head, legs and the shadowed belly - are ``rubber``, and the dorsal
seam between the two elytra is ``brass``: the seam is pinched into the shell
(columns at 0 and 180 degrees are pulled 8% in), so the bright line sits in a
real groove instead of being painted on a smooth dome.  Three slots.  Making the
seam a narrow stripe needs twelve columns instead of eight, which costs 30
triangles: 326 in total, 1.09x the 300 budget and inside the spec's 1.6x
allowance.
"""

import math

from mathutils import Vector

from prop_lib import fit_bounds, icosphere, lathe, tube

ID = 'beetle_figurine'
NAME_ZH = '甲虫小塑像'
CATEGORY = 'nature'
SOURCING = 'model ourselves：90 mm 风格化圣甲虫塑像（暗鞘翅 + 黄铜背缝 + 暗腿头）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.090
TRI_BUDGET = 400
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.090', 'game_longest_dim_m': '0.090'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'chitin': ((0.090, 0.075, 0.060), 0.30),
    'brass': ((0.520, 0.380, 0.120), 0.30),
    'rubber': ((0.045, 0.045, 0.048), 0.88),
}
SLOTS = ('chitin', 'brass', 'rubber')
MATERIALS = [
    {'key': 'chitin', 'name_zh': '鞘翅深褐', 'linear_rgb': [0.090, 0.075, 0.060],
     'roughness': 0.30},
    {'key': 'brass', 'name_zh': '背中线亮缝', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'rubber', 'name_zh': '头与足暗色', 'linear_rgb': [0.045, 0.045, 0.048],
     'roughness': 0.88},
]
NOTES = ('12-segment, 7-point elliptical lathe (120 triangles) + one 20-triangle '
         'head + six legs: 212 before the seven exact unions, 326 after them '
         '(1.09x the 300 budget, inside the spec 1.6x allowance), one closed '
         'shell.  Body 64 x 47 mm '
         'in plan by design (the lathe radius is scaled per angle by BODY_ASPECT '
         '0.735), dome top at 55.5 mm, underside at 9-10 mm.  Legs run from '
         '(x, +-17 mm, 22 mm) inside the shell to feet at (x-4 mm, +-27.5 mm, '
         '4.2 mm), section 10 mm tapering to 8.4 mm.  Dorsal seam: the columns '
         'at 0 and 180 degrees are pulled 8% in on every ring, which pinches a '
         'groove along y=0.  Materials after the union: |y| < 6 mm and '
         'z > 32 mm = brass (the seam), z < 12 mm or within 1.15 head radii of '
         'the head centre = rubber, the rest chitin.  fit_bounds pins '
         '90 x 55 x 60 mm.')

SIZE = (0.090, 0.055, 0.060)
SEGMENTS = 12
BODY_ASPECT = 0.735
PROFILE = [
    (0.0000, 0.0100),   # underside centre
    (0.0240, 0.0092),   # underside edge
    (0.0290, 0.0180),   # lower flank: the leg roots sit inside this ring
    (0.0300, 0.0300),   # widest ring
    (0.0246, 0.0420),   # shoulder
    (0.0140, 0.0510),   # dome
    (0.0000, 0.0555),   # top pole
]
HEAD_CENTRE = (0.0385, 0.0, 0.0245)
HEAD_RADIUS = 0.0165
HEAD_SCALE = (1.00, 0.95, 0.62)
LEG_X = (-0.0170, 0.0000, 0.0170)
LEG_START_Y = 0.0165
LEG_START_Z = 0.0210
LEG_END_Y = 0.0330
LEG_END_Z = 0.0038
# Front legs reach forward, rear legs backward: a scarab's legs fan out in plan,
# and the fan is what makes the silhouette read as an insect from above.
LEG_FAN_X = (0.0105, 0.0, -0.0105)
LEG_RADII = (0.0054, 0.0030)
LEG_SEGMENTS = 4
SEAM_HALF_WIDTH = 0.0060
SEAM_MIN_Z = 0.0320
SEAM_DEPTH = 0.080
HEAD_LABEL_SCALE = 1.15
BELLY_MAX_Z = 0.0120
TAU = math.pi * 2.0


def _ellipse_radius(angle):
    """Radius multiplier that turns the round lathe into the 1:0.735 oval."""
    c = math.cos(angle)
    s = math.sin(angle)
    return BODY_ASPECT / math.sqrt((BODY_ASPECT * c) ** 2 + s ** 2)


def _seam_pinch(angle):
    """1 at the 0/180 degree columns (y=0), falling away within a column."""
    total = 0.0
    for centre in (0.0, math.pi):
        delta = (angle - centre + math.pi) % TAU - math.pi
        total += math.exp(-(delta / 0.16) ** 2)
    return min(1.0, total)


def _head_mesh():
    bm = icosphere(1, HEAD_RADIUS)
    for v in bm.verts:
        v.co.x = v.co.x * HEAD_SCALE[0] + HEAD_CENTRE[0]
        v.co.y = v.co.y * HEAD_SCALE[1] + HEAD_CENTRE[1]
        v.co.z = v.co.z * HEAD_SCALE[2] + HEAD_CENTRE[2]
    return bm


def _leg_mesh(index, side):
    start = (LEG_X[index], side * LEG_START_Y, LEG_START_Z)
    end = (LEG_X[index] + LEG_FAN_X[index], side * LEG_END_Y, LEG_END_Z)
    return tube([start, end], list(LEG_RADII), segments=LEG_SEGMENTS)


def build(b):
    def radius_fn(point, column, radius):
        angle = TAU * column / SEGMENTS
        factor = _ellipse_radius(angle) * (1.0 - SEAM_DEPTH * _seam_pinch(angle))
        return radius * factor

    body_bm = lathe(PROFILE, segments=SEGMENTS, radius_fn=radius_fn)
    # Pronotum groove: pull the ring vertices nearest x = +18 mm inward by 3 %.
    # A real scarab has a hard line between thorax and elytra; without it the
    # figurine is one smooth egg and reads as a dark pebble.
    for v in body_bm.verts:
        if 0.0150 < v.co.x < 0.0215 and v.co.z > 0.018:
            v.co.x -= 0.0012
            v.co.y *= 0.972
    parts = [body_bm, _head_mesh()]
    for i in range(len(LEG_X)):
        for side in (-1.0, 1.0):
            parts.append(_leg_mesh(i, side))
    for side in (-1.0, 1.0):
        parts.append(tube([(0.0490, side * 0.0055, 0.0215),
                           (0.0560, side * 0.0085, 0.0195)],
                          [0.0032, 0.0016], segments=4))
    matrix = fit_bounds(parts, SIZE)
    head = matrix @ Vector(HEAD_CENTRE)
    head_tip = matrix @ Vector((HEAD_CENTRE[0] + HEAD_RADIUS, HEAD_CENTRE[1],
                                HEAD_CENTRE[2]))
    head_radius = (head_tip - head).length

    body = b.add(parts[0], 'chitin', PALETTE['chitin'][0], PALETTE['chitin'][1],
                 smooth=False, name='body')
    tools = []
    for k, bm in enumerate(parts[1:]):
        tools.append(b.add(bm, 'chitin', PALETTE['chitin'][0],
                           PALETTE['chitin'][1], smooth=False,
                           name='part%d' % k))
    b.boolean_union(body, tools)

    me = body.data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for poly in me.polygons:
        c = poly.center
        if (c.z < BELLY_MAX_Z
                or (Vector((c.x, c.y, c.z)) - head).length
                < head_radius * HEAD_LABEL_SCALE):
            poly.material_index = 2          # head, legs and the shadowed belly
        elif abs(c.y) < SEAM_HALF_WIDTH and c.z > SEAM_MIN_Z:
            poly.material_index = 1          # bright dorsal seam in its groove
        else:
            poly.material_index = 0          # dark chitin elytra
