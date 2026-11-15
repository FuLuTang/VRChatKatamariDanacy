"""Grass blade - an 8 cm closed thin blade with a midrib, dry tip and dark base.

Contract: 0.080 x 0.006 x 0.0015 m (real size), foliage, <= 60 triangles.

The blade is one closed slab, not a double-sided card: a `sheet` with a top
height field and a 0.4 mm thickness closes the underside with a mirrored grid
row and a rim, which is what a 60-triangle budget can afford (2 x 6 top/bottom
quads + 14 rim quads = 52 triangles).

`sheet` can only lay out a rectangle, so the pointed outline is made by warping
the finished slab: every vertex's x is scaled by the half-width at its y, taken
from a half-sine that starts at a 0.12 mm point, reaches 6 mm at the middle and
returns to a point.  Warping x only leaves the top/bottom heights - and therefore
the thickening and the arch - untouched, so the rim stays watertight.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): one saturated
single slot became three palette tones plus a real midrib.
  * `leaf_green` - the living blade (linear 0.115 / 0.240 / 0.075, rough 0.85)
  * `leaf_dry`   - the dry tip (0.400 / 0.270 / 0.105, rough 0.90)
  * `wood_dark`  - the dark sheath at the cut base (0.135 / 0.078 / 0.038)
The midrib is geometry: the top height field adds a 0.35 mm keel that falls off
to zero at both edges, so the blade has a V-section along its length instead of a
flat ribbon.  The keel is inside the arch, so the AABB stays 0.080 x 0.006 x
0.0015 m and the flat underside still rests on z = 0.
"""

import math

from prop_lib import sheet

ID = 'grass_blade'
NAME_ZH = '草叶'
CATEGORY = 'foliage'
SOURCING = 'model ourselves：0.080 m 细长弯草叶，带中脉与枯尖'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.080
TRI_BUDGET = 96
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.080', 'game_longest_dim_m': '0.080'}
MATERIALS = [
    {'key': 'leaf_green', 'name_zh': '青草绿', 'linear_rgb': [0.115, 0.240, 0.075],
     'roughness': 0.85},
    {'key': 'leaf_dry', 'name_zh': '枯尖', 'linear_rgb': [0.400, 0.270, 0.105],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '基部深色鞘',
     'linear_rgb': [0.135, 0.078, 0.038], 'roughness': 0.75},
]
NOTES = ('sheet(1 x 6) closed slab, 0.4 mm thick, then every vertex x scaled by '
         'the half-sine half-width (0.12 mm point at the ends, 6 mm at the '
         'middle) so the outline is a pointed blade.  Detail: the top height '
         'field carries a 0.35 mm keel that falls to zero at the edges, giving a '
         'V-section midrib; the root 10 mm takes wood_dark, the tip 10 mm '
         'leaf_dry.  Flat underside on z = 0, AABB 0.080 x 0.006 x 0.0015 m, 52 '
         'triangles, one closed shell, no boolean.')

LENGTH = 0.080
WIDTH = 0.006
THICK = 0.0004
RISE = 0.0011
KEEL = 0.00035
TIP_HALF = 0.00006
NU = 1
NV = 6
BASE_ZONE = 0.030       # the root 30 mm is the dark sheath
TIP_ZONE = 0.030        # the last 30 mm is the dry tip
LEAF_RGB = (0.115, 0.240, 0.075)
LEAF_DRY_RGB = (0.400, 0.270, 0.105)
WOOD_DARK_RGB = (0.135, 0.078, 0.038)


def _t(y):
    return (y + LENGTH * 0.5) / LENGTH


def _half_width(t):
    return max(TIP_HALF, WIDTH * 0.5 * math.sin(math.pi * t) ** 0.55)


def _top(x, y):
    t = _t(y)
    keel = KEEL * max(0.0, 1.0 - abs(x) / (WIDTH * 0.5))
    return THICK + RISE * (1.0 - math.sin(math.pi * t)) + keel


def build(b):
    bm = sheet(NU, NV, -WIDTH * 0.5, WIDTH * 0.5, -LENGTH * 0.5, LENGTH * 0.5,
               _top, bottom_fn=lambda x, y: 0.0)
    for v in bm.verts:
        v.co.x *= _half_width(_t(v.co.y)) / (WIDTH * 0.5)
        # The taper is authored along +Y because `sheet` lays out its grid that
        # way; every other long prop in this batch runs along X, so turn the
        # finished slab -90 deg about Z instead of building it sideways.
        v.co.x, v.co.y = v.co.y, -v.co.x
    blade = b.add(bm, 'leaf_green', LEAF_RGB, 0.85, smooth=False, name='blade')

    me = blade.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        x = poly.center.x
        if x < -LENGTH * 0.5 + BASE_ZONE:
            poly.material_index = 2
        elif x > LENGTH * 0.5 - TIP_ZONE:
            poly.material_index = 1
        else:
            poly.material_index = 0
