"""Wood plank - 140 x 35 x 2.5 cm flat board, the symbolic ramp piece.

LEVEL_CONSTRUCTION.md section 4 (P0, id + budget list) gives wood_plank at
1.40 x 0.35 x 0.025 m, 250 tris; section 3.2 of the same document quotes the
same piece at 1.20 x 0.25 x 0.02 / D 0.486.  The P0 table is the modelling list
that carries the id and the budget, so its 1.40 x 0.35 x 0.025 is used; the
difference is recorded in the batch notes.  prop_catalog.csv has no row for
this id, so the module declares DOC_ROW and build_all reads the size from here.

The board is one rounded-corner prism with a dead flat top.  At 2.5 cm it is a
thin wall a ball cannot climb today; with a later slope/support pass it is the
ramp.  No bevels beyond the 15 mm corner radius - a plank is a plank.
"""

from prop_lib import prism, rounded_rect

ID = 'wood_plank'
NAME_ZH = '木板'
CATEGORY = 'structure'
SOURCING = 'model ourselves：LEVEL_CONSTRUCTION.md 4 P0（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 1.40
TRI_BUDGET = 250
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '1.40', 'game_longest_dim_m': '1.40'}
MATERIALS = [
    {'key': 'wood', 'name_zh': '原木', 'linear_rgb': [0.415, 0.285, 0.150],
     'roughness': 0.72},
]
NOTES = ('One 12-gon rounded-rect prism 1.400 x 0.350 x 0.025 (R 15 mm), top '
         'and bottom flat, pivot at the AABB centre.  Size from '
         'LEVEL_CONSTRUCTION.md section 4 P0 (no CSV row); budget 250 tris.')


def build(b):
    b.add(prism(rounded_rect(1.400, 0.350, 0.015), 0.025),
          'wood', MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
          smooth=False, name='plank')
    return b.finish(TARGET_LONGEST_M)
