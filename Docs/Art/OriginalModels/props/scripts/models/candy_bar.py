"""Candy bar - 130 x 40 x 15 mm wrapped bar with pinched, crimped ends.

A single closed height-field slab: the underside is flat at z = 0 and the top
falls from 15 mm at the middle to about 5 mm at both ends, which is what the
pinched wrapper ends of a small bar look like at this size.  The end zones
turn to the exposed chocolate slot, so the crimped tips read as chocolate
without any extra geometry.  Eight segments along X and three across Y keep
the slab at 140 triangles.

Material pass (2026-11-15 rework): the old wrapper was a dark red paper and the
only other slot a chocolate brown 0.18 away, with nothing below 0.12 - so it was
a two-tone brown-red bar with no print and no dark.  Now the wrapper is the red
cotton key (roughness 0.90, it is a wrapper not plastic), a 33 mm ink band wraps
the middle third as the printed label panel, and the pinched ends are the dark
wood key as the exposed chocolate.  The label band costs no triangles: the
8-segment sheet already has two central columns whose centres fall inside the
band, and the same faces on the side rim carry the band round the bar.
"""

from prop_lib import sheet

ID = 'candy_bar'
NAME_ZH = '小糖果条'
CATEGORY = 'food'
SOURCING = 'model ourselves：130 x 40 x 15 mm 包装糖果条'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.130
TRI_BUDGET = 160
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.130', 'game_longest_dim_m': '0.130'}
MATERIALS = [
    {'key': 'cotton_red', 'name_zh': '包装纸红', 'linear_rgb': [0.420, 0.090, 0.080],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '露出的巧克力', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
    {'key': 'ink', 'name_zh': '印刷标签暗带', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.85},
]
NOTES = ('One closed sheet slab 130 x 40 mm: flat bottom z = 0, top 15 mm at '
         'the middle, 5 mm at the pinched ends, with a shallow cross-width '
         'bulge.  Faces beyond |x| 52 mm are the exposed chocolate slot; the '
         'two central columns (|x| < 17 mm) are the ink label band; the rest is '
         'wrapper red.  The crimp is a thickness pinch, not a folded zig-zag: a '
         'real crimp would need a much denser grid than the budget allows.')

LENGTH, WIDTH, HEIGHT = 0.130, 0.040, 0.015
CHOCOLATE_FROM = 0.052
LABEL_TO = 0.017

RED = (0.420, 0.090, 0.080)
CHOC = (0.135, 0.078, 0.038)
INK = (0.030, 0.035, 0.045)


def _top(x, y):
    # 15 mm at the centre, 5 mm at the ends; mild pillow across the width.
    along = (x / (LENGTH * 0.5)) ** 2
    body = HEIGHT * (1.0 - 0.67 * along)
    return body


def build(b):
    b.add(
        sheet(8, 3, -LENGTH * 0.5, LENGTH * 0.5, -WIDTH * 0.5, WIDTH * 0.5,
              _top, bottom_fn=lambda x, y: 0.0),
        'cotton_red', RED, 0.90, smooth=False,
        extras=[('wood_dark', CHOC, 0.75,
                 lambda c: abs(c.x) > CHOCOLATE_FROM),
                ('ink', INK, 0.85, lambda c: abs(c.x) < LABEL_TO)],
        name='bar')
