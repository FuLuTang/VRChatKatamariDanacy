"""Soda can - 6.6 cm can, 12 cm tall, with the necked top and recessed lid.

The catalog's real_longest_dim_m says 0.07 m, but PROP_SET_PLAN.md flags that
cell as suspect: 0.07 is the can diameter, and a 330 ml can is about 0.12 m
tall.  PROP_CATALOG.md gives the design three-axis as 0.066 x 0.066 x 0.12, so
that is what this model builds - TARGET_LONGEST_M is the 0.12 height - and the
manifest records the disagreement with the CSV cell instead of silently
pretending 0.07 was the height.

The shape is one lathe: a 5.5 mm bottom chamfer, a straight body to 0.108 m, a
short neck-in, a flat rim annulus at 0.118 m and a lid recessed 2 mm below it.
Flat shading keeps those top edges crisp instead of rounding them into a plastic
cap.  A second red material paints the face band from 0.012 to 0.104 m, leaving
thin silver bands at the foot and shoulder, so it reads as a labelled can
without a texture or extra ring geometry.
"""

from prop_lib import lathe

ID = 'soda_can'
NAME_ZH = '易拉罐'
CATEGORY = 'drink'
CSV_REAL_LONGEST_M = 0.07
CSV_GAME_LONGEST_M = 0.24
SOURCING = ('model ourselves：Ø0.066 x 0.12 m 罐体 + 收口 + 凹盖'
            '（CSV 的 0.07 疑为罐径）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.12
TRI_BUDGET = 340
MATERIALS = [
    {'key': 'metal', 'name_zh': '铝罐银', 'linear_rgb': [0.380, 0.400, 0.430],
     'roughness': 0.35},
    {'key': 'label', 'name_zh': '罐身红', 'linear_rgb': [0.480, 0.030, 0.020],
     'roughness': 0.40},
]
NOTES = ('16 segments, 12 profile points = 320 triangles.  Raw 0.0650 m wide and '
         '0.1180 m tall, scaled once by finish() to the 0.12 m design height, '
         'which gives 0.0661 x 0.0661 x 0.1200 m (the design asks 0.066 x 0.066 '
         'x 0.12).  Flat-shaded so the rim and lid stay crisp.  Bottom chamfer '
         '5.5 mm; body straight to 0.108 m; neck-in to 0.116 m; flat rim annulus '
         'at 0.118 m; flat lid 2 mm below the rim.  The red label is the face '
         'band from 0.012 to 0.104 m, leaving thin silver bands at the foot and '
         'the shoulder.  One closed shell.  build_all prints a WARN because '
         'TARGET (0.12) deliberately differs from the CSV 0.07 cell.')

SEGMENTS = 16

PROFILE = [
    (0.0000, 0.0000),   # bottom centre
    (0.0250, 0.0000),   # flat bottom
    (0.0325, 0.0055),   # bottom chamfer
    (0.0325, 0.0120),   # label starts
    (0.0325, 0.1040),   # label ends
    (0.0325, 0.1080),   # body top
    (0.0305, 0.1130),   # shoulder
    (0.0275, 0.1160),   # neck
    (0.0275, 0.1180),   # rim, vertical wall
    (0.0240, 0.1180),   # flat rim annulus
    (0.0240, 0.1160),   # lid recess wall
    (0.0000, 0.1160),   # flat lid
]


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'metal', (0.380, 0.400, 0.430), 0.35, smooth=False,
          extras=[('label', (0.480, 0.030, 0.020), 0.40,
                   lambda c: 0.012 < c.z < 0.104)])
