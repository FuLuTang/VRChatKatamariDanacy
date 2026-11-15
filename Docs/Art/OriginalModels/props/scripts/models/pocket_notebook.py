"""Pocket notebook - 150 x 100 mm closed notebook, boards overhanging the pages.

Design three-axis 0.150 x 0.100 x 0.012 m.  Three stacked prisms - bottom board,
page block, top board - are welded by two exact unions into one closed shell; the
pages are insetted 2 mm on three sides and lean 0.2 mm inside the boards at the
spine edge (+X), so the boards overhang by 2 mm and the pale page band is a real
recess, not a painted stripe.  The spine sits on a short edge as the contract
asks, and it is 0.2 mm proud of the pages, which is why its exposed face reads as
board rather than paper.

The overlap between a board and the pages is 1 mm at each interface, so no
coplanar face pair reaches the exact solver.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, flat paper
class): three palette slots instead of two (the old cover was an off-palette
navy at luma 0.08 with no printed face).
  * `plastic_red` - the cover boards (linear 0.480 / 0.075 / 0.060, rough 0.48)
  * `paper`       - the page block (0.800 / 0.775 / 0.700, rough 0.90)
  * `ink`         - a 0.4 mm deep blind pocket in the top cover, the printed
                    title panel (0.030 / 0.035 / 0.045)
The pocket is inside the top board, so the AABB is unchanged.
"""

from prop_lib import prism, rounded_rect

ID = 'pocket_notebook'
NAME_ZH = '口袋本'
CATEGORY = 'debris'
SOURCING = 'model ourselves：150 x 100 x 12 mm 口袋本，封面大 2 mm，书脊在短边'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.150
TRI_BUDGET = 224
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.150', 'game_longest_dim_m': '0.150'}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '封面红卡', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.48},
    {'key': 'paper', 'name_zh': '内页米白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '封面印块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Boards 0.150 x 0.100 m x 2.5 mm top and bottom; page block 0.146 x '
         '0.096 m x 9 mm, inset 2 mm on three sides and 0.2 mm inside the spine '
         'edge (+X), overlapping each board by 1 mm so two exact unions weld the '
         'stack into one shell.  Detail: a 0.062 x 0.036 m blind pocket 0.4 mm '
         'deep is subtracted from the top cover and takes the ink slot.  '
         'Materials by centroid: ink inside the pocket, paper for the page band '
         '(0.0035 < z < 0.0085 m), cover elsewhere.  One closed shell.')

X_MAX = 0.075
Y_MAX = 0.050
BOARD_T = 0.0025
PAGE_T = 0.0090
BOARD_BOTTOM = 0.0
PAGE_BASE = 0.0015                  # 1 mm inside the bottom board
BOARD_TOP = 0.0095                  # 1 mm below the page block's top face
PAGE_X_MIN = -0.071
PAGE_X_MAX = 0.0748                 # 0.2 mm inside the spine edge
PAGE_Y = 0.048
CARD_RGB = (0.480, 0.075, 0.060)
PAPER_RGB = (0.800, 0.775, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
PAGE_Z_LOW = 0.0035
PAGE_Z_HIGH = 0.0085
# Blind pocket in the top cover: 0.4 mm deep, inside the 0.150 x 0.100 m board.
POCKET_W, POCKET_D, POCKET_R = 0.062, 0.036, 0.004
POCKET_CX, POCKET_CY = -0.005, 0.0
POCKET_FLOOR = 0.0116
POCKET_TOP = 0.0125                 # overshoots the 0.012 m board top
COVER_TOP = BOARD_TOP + BOARD_T      # 0.012 m: the top cover's upper face


def _board():
    return [(-X_MAX, -Y_MAX), (X_MAX, -Y_MAX), (X_MAX, Y_MAX), (-X_MAX, Y_MAX)]


def _pages():
    return [(PAGE_X_MIN, -PAGE_Y), (PAGE_X_MAX, -PAGE_Y),
            (PAGE_X_MAX, PAGE_Y), (PAGE_X_MIN, PAGE_Y)]


def _in_pocket(c):
    return (abs(c.x - POCKET_CX) < POCKET_W * 0.5 + 1e-4
            and abs(c.y - POCKET_CY) < POCKET_D * 0.5 + 1e-4)


def _slot(centre):
    """Ink on the pocket floor and walls, paper for the page band, cover else.

    The pocket is identified by its exact z band (floor plane, then everything
    between the floor and the cover's top face) because the cover's top face is
    also "above the floor": a bbox + height test painted most of the cover ink.
    """
    c = centre
    if abs(c.z - POCKET_FLOOR) < 1e-5 or POCKET_FLOOR + 1e-5 < c.z < COVER_TOP - 1e-5:
        return 2
    if PAGE_Z_LOW < c.z < PAGE_Z_HIGH and abs(c.x) < 0.0745 and abs(c.y) < 0.0495:
        return 1
    return 0


def build(b):
    bottom = b.add(prism(_board(), BOARD_T, axis='z', start=BOARD_BOTTOM),
                   'plastic_red', CARD_RGB, 0.48, smooth=False,
                   name='board_bottom')
    pages = b.add(prism(_pages(), PAGE_T, axis='z', start=PAGE_BASE),
                  'paper', PAPER_RGB, 0.90, smooth=False, name='pages')
    top = b.add(prism(_board(), BOARD_T, axis='z', start=BOARD_TOP),
                'plastic_red', CARD_RGB, 0.48, smooth=False, name='board_top')
    b.boolean_union(bottom, [pages, top])

    pocket = b.add(prism(rounded_rect(POCKET_W, POCKET_D, POCKET_R, 1),
                         POCKET_TOP - POCKET_FLOOR, axis='z', start=POCKET_FLOOR),
                   'ink', INK_RGB, 0.75, smooth=False, name='pocket_cut')
    for v in pocket.data.vertices:
        v.co.x += POCKET_CX
        v.co.y += POCKET_CY
    b.boolean_difference(bottom, [pocket])

    me = bottom.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
