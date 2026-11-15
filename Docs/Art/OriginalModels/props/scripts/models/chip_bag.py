"""Chip bag - inflated pillow pouch with crimped, serrated end seals.

MessyRoom catalogue t3 / flat / 0.280 x 0.200 x 0.080 m; no catalog row, so the
module carries ``DOC_ROW``.

A bag of crisps is a pillow, not a tent: the height field is full height across
the flat middle and only rolls off over the last 45% of the distance to each
seal, so the top reads as an inflated panel with rounded shoulders instead of the
four-ridge peak a plain (1-u^2)(1-v^2) dome gives.  A small crown term keeps the
middle from looking like a dead-flat plate.

The two crimped seals get their own geometry in the same height field: a 7 mm
pitch ridge along the weld plus a zig-zag in plan on the end edges only, so the
ends read as the crushed weld of a real bag without a separate seal strip.

The print is three nested regions of the same height field, painted by top-face
centre, so it costs no triangles at all (2026-11-15 material pass):

* ``paper_dark`` - the two crimped end seals (|x| beyond 100 mm, where the
  crimp ridge ramps up), the crushed weld of the bag, one step darker than the
  film;
* ``cotton_red`` - the round brand badge, a wobbling ellipse in the middle;
* ``ink`` - the small core mark inside the badge, which is what keeps a dark
  slot on the prop.

No lettering, no texture: the badge is a shape, not a logo.

12 x 10 grid: 240 top + 44 underside fan + 88 rim = 372 triangles.  The grid is
denser than the first draft's 9 x 8 (246 tris) because a 31 x 25 mm cell cannot
resolve a round badge or a two-column seal band: the badge came back as two
rectangular patches.  The budget is 416 after the 2026-11-15 x1.6 detail
allowance.
"""

import math

from models._room_shapes import clamp, grid_slab, smoothstep

ID = 'chip_bag'
NAME_ZH = '薯片袋'
CATEGORY = 'packaging'
SOURCING = 'model ourselves：充气枕状袋 + 锯齿封口，单壳高度场'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.280
TRI_BUDGET = 416
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.280', 'game_longest_dim_m': '0.280'}
MATERIALS = [
    {'key': 'plastic_green', 'name_zh': '袋膜绿', 'linear_rgb': [0.090, 0.260,
                                                             0.130],
     'roughness': 0.42},
    {'key': 'paper_dark', 'name_zh': '压边纸板色', 'linear_rgb': [0.360, 0.320,
                                                              0.250],
     'roughness': 0.92},
    {'key': 'cotton_red', 'name_zh': '圆形标贴', 'linear_rgb': [0.420, 0.090,
                                                             0.080],
     'roughness': 0.60},
    {'key': 'ink', 'name_zh': '标贴内芯', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.55},
]
NOTES = ('Pillow height field: 4.5 mm at the seals, 79 mm at the middle, calm '
         'centre with a rounded shoulder over the outer 45%.  Serrated seals: '
         '7 mm-pitch crimp ridge plus a 2.2 mm zig-zag on the end edges only.  '
         'Print is three nested top-face regions: paper_dark crimp seals at '
         '|x| > 100 mm, a cotton_red round badge and an ink core mark inside '
         'it.  12 x 10 grid, 0.280 x 0.198 x 0.079 m.')

TAU = math.pi * 2.0
HALF_X = 0.140
HALF_Y = 0.100
DOME_X = 0.118
DOME_Y = 0.082
SEAL_Z = 0.0045


def triangle_wave(t):
    return 4.0 * abs(t - math.floor(t + 0.5)) - 1.0


def warp(u, v):
    x = -HALF_X + 2.0 * HALF_X * u
    y = -HALF_Y + 2.0 * HALF_Y * v
    if abs(2.0 * u - 1.0) > 0.999:
        # only the two welded ends are serrated in plan
        teeth = 0.0024 * triangle_wave(y / 0.011)
        x += teeth if u > 0.5 else -teeth
    return x, y


def top(x, y):
    ex = clamp((1.0 - abs(x) / DOME_X) / 0.45)
    ey = clamp((1.0 - abs(y) / DOME_Y) / 0.45)
    crown = (1.0 - (x / DOME_X) ** 2) * (1.0 - (y / DOME_Y) ** 2)
    z = SEAL_Z + 0.0700 * smoothstep(ex) * smoothstep(ey) + 0.0045 * max(
        0.0, crown)
    w = clamp((abs(x) - 0.100) / 0.018)
    z += 0.0025 * math.cos(TAU * y / 0.007) * w
    return z


def badge_radius(centre, sx=0.055, sy=0.042):
    dx = (centre.x - 0.008) / sx
    dy = (centre.y + 0.004) / sy
    r = math.hypot(dx, dy)
    wobble = 1.0 + 0.18 * math.sin(3.0 * math.atan2(dy, dx) + 0.8)
    return r < wobble


def is_seal(centre):
    """The two crimped end seals (they carry the crimp ridge in the field)."""
    return abs(centre.x) > 0.100 and centre.z < 0.022


def is_badge(centre):
    return centre.z > 0.025 and badge_radius(centre)


def is_core(centre):
    return centre.z > 0.025 and badge_radius(centre, 0.026, 0.018)


def build(b):
    bm = grid_slab(12, 10, -HALF_X, HALF_X, -HALF_Y, HALF_Y, top,
                   bottom_z=0.0,
                   warp=warp)
    b.add(bm, 'plastic_green', (0.090, 0.260, 0.130), 0.42, smooth=False,
          name='bag',
          extras=[('paper_dark', (0.360, 0.320, 0.250), 0.92, is_seal),
                  ('cotton_red', (0.420, 0.090, 0.080), 0.60, is_badge),
                  ('ink', (0.030, 0.035, 0.045), 0.55, is_core)])
