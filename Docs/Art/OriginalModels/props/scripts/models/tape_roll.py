"""Tape roll - 100 mm roll, 20 mm wide, with a real through hole and a paper core.

The catalog asks for 0.100 x 0.100 x 0.020 m with an outer tape ring and an
inner paper core, and the hole has to be a real through hole in a single shell.
A lathe profile normally closes itself on the axis; here the profile is written
as a *closed cross-section loop* instead - bottom face, outer wall, top face,
inner chamfer, inner wall, bottom chamfer, back to the first point - and revolved
with both caps off.  The first and last rings are generated from identical
(r, z) values, so ``Builder.finish`` welds them with its 1e-6 merge and the
result is one closed torus-like shell whose hole is genuinely empty.

12 segments and 6 bands give 144 triangles, inside the 180 budget.

Material pass (2026-11-15 rework): the old pair was a 0.64 beige tape with a
0.78 paper core - two light slots, no dark one, and the core barely separated
from the tape.  Now the tape is the straw key (packing-tape amber), the core the
pale paper key (0.80 vs 0.47 peak, a 0.33 step) and one 30-degree strip of the
outer wall is the dark ink key: that is the free tape-end / print marker the spec
asks for, and it supplies the required low-value slot.
"""

import math

from prop_lib import lathe

ID = 'tape_roll'
NAME_ZH = '胶带卷'
CATEGORY = 'debris'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.100', 'game_longest_dim_m': '0.100'}
SOURCING = 'model ourselves：Ø0.100 x 0.020 m 胶带卷，外圈胶带 + 内圈纸芯，真通孔'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.100
TRI_BUDGET = 180
MATERIALS = [
    {'key': 'straw', 'name_zh': '琥珀胶带', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.50},
    {'key': 'paper', 'name_zh': '纸芯', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '胶带端头暗标', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Revolved closed cross-section, 12 segments x 6 bands = 144 triangles.  '
         'Outer radius 50 mm, inner radius 30 mm, height 20 mm.  The profile '
         'starts and ends at (0.0300, 0.0000) with cap_start/cap_end off, so '
         'finish() welds the coincident seam rings and the hole stays open as one '
         'shell (a second cylinder would be a second shell).  Paper is every face '
         'whose centre is inside r = 31.6 mm, i.e. the inner wall and its two '
         'chamfers; the ink marker is the single 30-degree outer-wall segment '
         'starting at +X (r > 46 mm, 1-19 mm up; the polygon centre of an '
         'arc face sits at r cos(15 deg) = 48.3 mm).  Tape faces are the rest of the '
         'outer wall, top and bottom.')

SEGMENTS = 12
PAPER_RADIUS = 0.0316
MARK_RADIUS = 0.0460
MARK_SPAN_DEG = 30.0

STRAW = (0.470, 0.360, 0.140)
PAPER = (0.800, 0.775, 0.700)
INK = (0.030, 0.035, 0.045)

PROFILE = [
    (0.0300, 0.0000),   # inner bottom edge (seam, repeated at the end)
    (0.0500, 0.0000),   # bottom face
    (0.0500, 0.0200),   # outer wall
    (0.0305, 0.0200),   # top face inward
    (0.0300, 0.0190),   # inner top chamfer
    (0.0300, 0.0010),   # inner wall
    (0.0300, 0.0000),   # inner bottom chamfer, closes the loop
]


def paper(centre):
    """Paper core: everything close to the axis."""
    return centre.x * centre.x + centre.y * centre.y < PAPER_RADIUS ** 2


def marker(centre):
    """One outer-wall segment in ink: the tape end / print marker."""
    r2 = centre.x * centre.x + centre.y * centre.y
    if r2 < MARK_RADIUS ** 2:
        return False
    if not (0.001 < centre.z < 0.019):
        return False
    angle = math.degrees(math.atan2(centre.y, centre.x)) % 360.0
    return angle < MARK_SPAN_DEG


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS, cap_start=False, cap_end=False)
    b.add(bm, 'straw', STRAW, 0.50, smooth=False,
          extras=[('paper', PAPER, 0.90, paper),
                  ('ink', INK, 0.75, marker)])
