"""Acorn - one closed lathe: nut body, cap lip, cap dome and stem tip."""

from prop_lib import lathe

ID = 'acorn'
NAME_ZH = '橡子'
CATEGORY = 'nature'
CSV_REAL_LONGEST_M = 0.03
CSV_GAME_LONGEST_M = 0.05
SIZE_REFERENCE = 'real'
SOURCING = 'model ourselves：0.05 m 橡子（低面数旋转体）'
TARGET_LONGEST_M = 0.03
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'nut', 'name_zh': '橡子果肉', 'linear_rgb': [0.36, 0.18, 0.06],
     'roughness': 0.6},
    {'key': 'cap', 'name_zh': '橡子壳斗', 'linear_rgb': [0.13, 0.06, 0.025],
     'roughness': 0.75},
]
NOTES = ('Cap colour is a second flat material assigned by height on the same '
         'closed lathe, so there is no separate shell and no hidden geometry.')


def build(b):
    profile = [
        (0.0000, 0.0000),
        (0.0055, 0.0015),
        (0.0110, 0.0050),
        (0.0150, 0.0110),
        (0.0165, 0.0180),
        (0.0160, 0.0260),
        (0.0150, 0.0300),
        (0.0190, 0.0320),
        (0.0200, 0.0370),
        (0.0150, 0.0430),
        (0.0060, 0.0460),
        (0.0038, 0.0500),
    ]
    bm = lathe(profile, segments=10)
    b.add(bm, 'nut', (0.36, 0.18, 0.06), 0.6, smooth=True,
          extras=[('cap', (0.13, 0.06, 0.025), 0.75,
                   lambda c: c.z > 0.0305)])
