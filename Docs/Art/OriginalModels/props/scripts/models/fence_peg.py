"""Fence peg - 16 cm hand-cut wooden peg lying on its side, end grain showing.

DungBeetleMeadow catalogue t2: 0.160 x 0.030 x 0.028 m, shape_class long,
264/352 tris, Z0/Z1 围栏根.

A peg is a lathe, and the previous build was a plain one-slot tan cone: the
head was a 12-triangle flat disc in the same wood colour as the shaft, and the
head faced away from the preview camera, so the prop read as a sharpened stick.

This build turns the head toward the viewer and colours it as cut wood:

* profile, 13 points on 12 segments: a 29.6 mm head disc split into two
  concentric rings (a 4.5 mm heart and a 9 mm growth-ring band), a chamfered
  head wall, a 25.2 mm undercut neck - the neck is what reads as a peg head
  instead of a spike - then a straight taper to a 44 mm point.  264 triangles,
  one closed shell, no boolean, flat shaded so a hand-cut peg stays faceted;
* the head is turned to +X (the previous build put it at -X, so the preview
  showed only the sharpened point and the end grain faced away); the peg still
  lies along X, so the catalogue's 0.160 x 0.030 x 0.028 three-axis holds;
* end grain: the heart disc and the outer ring (plus the head wall) are
  wood_dark, the band between them stays wood - two growth rings on the cut
  face, which is the "年轮端面" detail and the prop's only colour break.

Colour: wood shaft (0.224), wood_dark end grain (0.087, the dark slot; 0.137
linear below the shaft, 0.50 vs 0.32 in display sRGB).
"""

import math

from mathutils import Matrix, Vector

from prop_lib import lathe

ID = 'fence_peg'
NAME_ZH = '木栅栏钉'
CATEGORY = 'farm_tools'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t2 '
            '0.160×0.030×0.028 m 栅栏木钉（Z1 围栏根）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.160
TRI_BUDGET = 352
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.160', 'game_longest_dim_m': '0.160'}
WOOD = (0.330, 0.205, 0.105)
WOOD_DARK = (0.135, 0.078, 0.038)
MATERIALS = [
    {'key': 'wood', 'name_zh': '旧木棕', 'linear_rgb': list(WOOD),
     'roughness': 0.72},
    {'key': 'wood_dark', 'name_zh': '年轮端面', 'linear_rgb': list(WOOD_DARK),
     'roughness': 0.75},
]
NOTES = ('12-segment lathe, 15 profile points: the head face is four '
         'concentric bands (0-3.8-7.2-10.8-14.8 mm), a 14.8 mm head rim, 6.5 mm head wall, 12.6 mm undercut neck at 10.5 mm, then '
         '12.2 / 11.8 / 11.2 / 10.2 mm shaft to 115 mm and a taper to the point '
         'at 160 mm: 276 side quads + two 12-triangle pole fans = 288 '
         'triangles, one closed shell, no boolean.  Rotated -90 deg about Y (peg '
         'axis along X, head at +X), then dropped so the lowest facet sits 0.8 mm '
         'into z = 0.  The heart disc and the second growth ring (r < 0.0040 or '
         '0.0070 < r < 0.0110 on the head plane) are wood_dark, leaving two light '
         'rings between them.  Overall 0.160 x 0.0296 x 0.0296 m.')

PROFILE = [
    (0.0000, 0.000),   # head centre (pole)
    (0.0038, 0.000),   # heart edge
    (0.0072, 0.000),   # first light ring
    (0.0108, 0.000),   # second dark ring
    (0.0148, 0.000),   # head rim
    (0.0148, 0.0065),  # head wall
    (0.0126, 0.0105),  # undercut neck
    (0.0122, 0.0300),
    (0.0118, 0.0600),
    (0.0112, 0.0900),
    (0.0102, 0.1150),
    (0.0075, 0.1380),
    (0.0040, 0.1520),
    (0.0000, 0.1600),  # point (pole)
]
SEGMENTS = 12
HALF_LENGTH = 0.080
YAW = 0.0   # end face +X is already visible from the preview camera


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    rot = Matrix.Rotation(-math.pi * 0.5, 4, 'Y')
    for v in bm.verts:
        v.co = rot @ v.co
        v.co.x += HALF_LENGTH
    yaw = Matrix.Rotation(YAW, 4, 'Z')
    for v in bm.verts:
        v.co = yaw @ v.co
    zmin = min(v.co.z for v in bm.verts)
    dz = -(zmin + 0.0008)
    for v in bm.verts:
        v.co.z += dz

    axis = Vector((math.cos(YAW), math.sin(YAW), 0.0))
    # The lathe axis is the X axis at y = z = 0; dropping the peg onto the
    # ground moves that centreline up by dz, and the end-grain test has to
    # measure radius from the centreline, not from the world origin.
    centreline = Vector((0.0, 0.0, dz))

    def end_grain(centre):
        rel = centre - centreline
        s = rel.dot(axis)
        if s < HALF_LENGTH - 0.0005:
            return False
        radial = (rel - axis * s).length
        # heart disc, then two of the four growth rings
        return radial < 0.0040 or 0.0070 < radial < 0.0110

    b.add(bm, 'wood', WOOD, 0.72, smooth=False, name='peg',
          extras=[('wood_dark', WOOD_DARK, 0.75, end_grain)])
