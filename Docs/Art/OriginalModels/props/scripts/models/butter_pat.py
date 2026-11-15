"""Butter pat - 100 x 60 mm butter slab on its own paper tray, with a cut line.

Design three-axis 0.100 x 0.060 x 0.012 m.  A pat served on paper is two solids -
the pale block and the tray it sits on - and a prop has to be one closed shell, so
the block is embedded 0.7 mm into the 2.2 mm tray and the exact union welds them.
The tray is 5 mm wider than the block on every side, which is the overhanging
paper lip that distinguishes "butter on paper" from "a yellow box"; the chamfered
corners of `rounded_rect` give the block its pressed, non-box-like silhouette.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, food class):
the old pair was two near-white slots (0.88 paper vs 0.87 butter) and read as one
white blob.  Three palette slots now:
  * `plastic_yellow` - the butter (linear 0.640 / 0.450 / 0.060, rough 0.48)
  * `paper`          - the paper tray (0.800 / 0.775 / 0.700, rough 0.90)
  * `ink`            - a real 1 mm knife cut across the block: a 1.2 mm wide tool
                       is subtracted 6 mm into the top, and its floor and walls
                       take the ink slot (0.030 / 0.035 / 0.045), so the butter
                       reads as cut rather than moulded
The cut stays inside the block, so the AABB is unchanged.
"""

from prop_lib import prism, rounded_rect

ID = 'butter_pat'
NAME_ZH = '黄油块'
CATEGORY = 'food'
SOURCING = 'model ourselves：100 x 60 x 12 mm 黄油块 + 底部纸托 + 刀切痕'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.100
TRI_BUDGET = 160
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.100', 'game_longest_dim_m': '0.100'}
MATERIALS = [
    {'key': 'plastic_yellow', 'name_zh': '黄油奶黄',
     'linear_rgb': [0.640, 0.450, 0.060], 'roughness': 0.48},
    {'key': 'paper', 'name_zh': '纸托米白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '刀切痕深色', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Tray: 0.100 x 0.060 m chamfered tray, 2.2 mm thick, flat on z=0.  '
         'Block: 0.090 x 0.051 m, 10.5 mm tall from z=0.0015 so the top plane '
         'lands on 0.012 m and finish() does not rescale.  Exact union of the '
         'two, then a 1.2 mm wide x 0.060 m long tool is subtracted from z=0.006 '
         'to 0.020 m (a 6 mm deep knife cut across the block).  Materials by '
         'centroid: ink inside the cut, butter above z=0.0025, paper below.  '
         'One closed shell.')

TRAY_W, TRAY_D, TRAY_T = 0.100, 0.060, 0.0022
TRAY_R = 0.010
BLOCK_W, BLOCK_D = 0.090, 0.051
BLOCK_R = 0.009
BLOCK_BASE = 0.0015                  # sunk into the tray so the union welds
BLOCK_H = 0.0105                     # 0.0015 + 0.0105 = 0.012 m top plane
CORNER_SEGMENTS = 1
BUTTER_RGB = (0.640, 0.450, 0.060)
PAPER_RGB = (0.800, 0.775, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
SPLIT_Z = 0.0025                     # tray top plane plus a little
CUT_X0, CUT_X1 = 0.0100, 0.0112      # 1.2 mm wide knife cut
CUT_Z = 0.0060                       # cut floor, 6 mm below the top plane
CUT_Y = 0.0290                       # cut runs past the block in Y


def _cut_polygon():
    """1.2 x 58 mm rectangle in XY, extruded upward from the cut floor."""
    return [(CUT_X0, -CUT_Y), (CUT_X1, -CUT_Y), (CUT_X1, CUT_Y), (CUT_X0, CUT_Y)]


def _slot(centre):
    """Ink in the knife cut, butter above the tray, paper below it."""
    if (centre.z > CUT_Z - 0.0006
            and CUT_X0 - 1e-4 < centre.x < CUT_X1 + 1e-4):
        return 2
    return 0 if centre.z > SPLIT_Z else 1


def build(b):
    tray = b.add(prism(rounded_rect(TRAY_W, TRAY_D, TRAY_R, CORNER_SEGMENTS),
                       TRAY_T, axis='z'),
                 'paper', PAPER_RGB, 0.90, smooth=False, name='tray')
    block = b.add(prism(rounded_rect(BLOCK_W, BLOCK_D, BLOCK_R, CORNER_SEGMENTS),
                        BLOCK_H, axis='z', start=BLOCK_BASE),
                  'plastic_yellow', BUTTER_RGB, 0.48, smooth=False,
                  name='block')
    b.boolean_union(tray, [block])
    # A knife cut: a 1.2 x 58 mm tool is subtracted from the cut floor up through
    # the top of the block, so the tray and the block's outline keep their shape.
    cut = b.add(prism(_cut_polygon(), 0.020, axis='z', start=CUT_Z),
                'ink', INK_RGB, 0.75, smooth=False, name='cut')
    b.boolean_difference(tray, [cut])

    me = tray.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
