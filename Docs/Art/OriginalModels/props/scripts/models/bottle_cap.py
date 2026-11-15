"""Bottle cap - crimped crown cap, 20 flutes, closed solid, no text."""

from prop_lib import lathe

ID = 'bottle_cap'
NAME_ZH = '瓶盖'
CATEGORY = 'debris'
CSV_REAL_LONGEST_M = 0.03
CSV_GAME_LONGEST_M = 0.05
SIZE_REFERENCE = 'real'
SOURCING = 'model ourselves：Ø0.05 m 圆盘 + 齿边'
TARGET_LONGEST_M = 0.03
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'cap', 'name_zh': '暗蓝灰金属漆', 'linear_rgb': [0.10, 0.14, 0.19],
     'roughness': 0.45},
]
NOTES = ('Crown profile plus alternating skirt radius reads as the crimped rim; '
         'the underside is a closed disc, so the cap is watertight.')


def flutes(point_index, segment_index, radius):
    if point_index in (1, 2) and segment_index % 2 == 1:
        return radius * 0.90
    return radius


def build(b):
    profile = [
        (0.0195, 0.0000),
        (0.0250, 0.0015),
        (0.0250, 0.0085),
        (0.0235, 0.0110),
        (0.0180, 0.0130),
        (0.0000, 0.0145),
    ]
    bm = lathe(profile, segments=20, radius_fn=flutes)
    b.add(bm, 'cap', (0.10, 0.14, 0.19), 0.45, smooth=False)
