"""Pear - bell-shaped body with a stem, one closed surface of revolution.

The plan asks for a bell-shaped solid, 5.5 cm at its widest, tallest 9 cm.  The
bulb sits in the bottom third, the waist pinches in and the stem continues up
the axis as a narrow column (same trick as the apple: no second object), which
supplies the last 1.5 cm that takes the height from 8 cm to the 9 cm target.
"""

from prop_lib import lathe

ID = 'pear'
NAME_ZH = '梨'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.09
CSV_GAME_LONGEST_M = 0.32
SOURCING = 'model ourselves：8 cm 高钟形旋转体，最粗 Ø5.5'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.09
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'skin', 'name_zh': '梨皮黄绿', 'linear_rgb': [0.460, 0.400, 0.100],
     'roughness': 0.55},
    {'key': 'stem', 'name_zh': '果梗褐', 'linear_rgb': [0.100, 0.060, 0.020],
     'roughness': 0.75},
]
NOTES = ('Body 8 cm tall and 5.5 cm at the widest (z = 25 mm), stem 1.5 cm on '
         'the axis, so the 9 cm real-size target is the height.  The stem is the '
         'top of the same profile, capped flat; colour split by height.')

SEGMENTS = 14

PROFILE = [
    (0.0000, 0.0000),
    (0.0180, 0.0050),
    (0.0262, 0.0140),
    (0.0275, 0.0250),
    (0.0258, 0.0380),
    (0.0212, 0.0500),
    (0.0155, 0.0600),
    (0.0110, 0.0680),
    (0.0075, 0.0745),
    (0.0040, 0.0800),   # stem base
    (0.0030, 0.0900),   # stem tip, capped flat
]


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'skin', (0.460, 0.400, 0.100), 0.55, smooth=True,
          extras=[('stem', (0.100, 0.060, 0.020), 0.75,
                   lambda c: c.z > 0.0755)])
