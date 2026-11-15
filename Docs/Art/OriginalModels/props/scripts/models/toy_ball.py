"""Toy ball - low-poly sphere with two muted colour bands (no markings)."""

from prop_lib import uv_sphere

ID = 'toy_ball'
NAME_ZH = '小皮球'
CATEGORY = 'toy'
CSV_REAL_LONGEST_M = 0.15
CSV_GAME_LONGEST_M = 0.40
SIZE_REFERENCE = 'real'
SOURCING = 'model ourselves：Ø0.40 m 低面数球 + 双色条纹'
TARGET_LONGEST_M = 0.15
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'shell', 'name_zh': '米白球面', 'linear_rgb': [0.70, 0.66, 0.58],
     'roughness': 0.55},
    {'key': 'band', 'name_zh': '暗红球带', 'linear_rgb': [0.34, 0.05, 0.045],
     'roughness': 0.55},
]
NOTES = ('Two flat materials on one sphere: the bands are assigned by face '
         'height, no stripe texture.  Pivot at the sphere centre so a rolling '
         'or spinning ball rotates cleanly.')


def build(b):
    bm = uv_sphere(16, 10, 0.20)
    b.add(bm, 'shell', (0.70, 0.66, 0.58), 0.55, smooth=True,
          extras=[('band', (0.34, 0.05, 0.045), 0.55,
                   lambda c: 0.055 < abs(c.z) < 0.155)])
