"""Napkin - one folded square: height-field top over a flat underside."""

from prop_lib import sheet

ID = 'napkin'
NAME_ZH = '餐巾'
CATEGORY = 'tableware'
CSV_REAL_LONGEST_M = 0.15
CSV_GAME_LONGEST_M = 0.30
SOURCING = 'model ourselves：折叠面片，0.30 m'
TARGET_LONGEST_M = 0.30
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'cloth', 'name_zh': '亚麻米色', 'linear_rgb': [0.60, 0.56, 0.48],
     'roughness': 0.9},
]
NOTES = ('Fold is a smooth height field, not stacked sheets, so the mesh stays '
         'a single closed solid with no interior faces.')


def top(x, y):
    t = (y + 0.015) / 0.030
    t = max(0.0, min(1.0, t))
    t = t * t * (3.0 - 2.0 * t)
    return 0.006 + 0.022 * t


def build(b):
    bm = sheet(6, 6, -0.15, 0.15, -0.15, 0.15, top,
               bottom_fn=lambda x, y: 0.0)
    b.add(bm, 'cloth', (0.60, 0.56, 0.48), 0.90, smooth=False)
