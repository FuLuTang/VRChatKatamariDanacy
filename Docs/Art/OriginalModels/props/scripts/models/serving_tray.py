"""Serving tray - 55 x 35 x 4 cm shallow wooden tray, flat rim and flat floor.

LEVEL_CONSTRUCTION.md section 4 (P2) gives serving_tray at 0.55 x 0.35 x 0.04 m
and 250 tris; section 3.2 lists the same prop as the second thin piece "more
like a board leaning against something".  No prop_catalog.csv row exists, so the
module declares DOC_ROW.

Shape: a 12-gon rounded-rect prism 0.550 x 0.350 x 0.040 with a boolean cut
that leaves a 12 mm floor and a 25 mm rim.  Both the rim top (z = 0.040) and the
floor (z = 0.012) are flat planes, so a ball or a plate can sit on it; the rim
is what makes it read as a tray and not as a second plank.
"""

from prop_lib import prism, rounded_rect

ID = 'serving_tray'
NAME_ZH = '托盘'
CATEGORY = 'structure'
SOURCING = 'model ourselves：LEVEL_CONSTRUCTION.md 4 P2（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.55
TRI_BUDGET = 250
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.55', 'game_longest_dim_m': '0.55'}
MATERIALS = [
    {'key': 'wood', 'name_zh': '浅木', 'linear_rgb': [0.470, 0.340, 0.190],
     'roughness': 0.70},
]
NOTES = ('Rounded-rect tray 0.550 x 0.350 x 0.040 (R 20 mm) with an inner cut '
         '0.500 x 0.300 from z 0.012, leaving a 12 mm floor and a 25 mm rim; '
         'rim top and floor are both flat.  Size from LEVEL_CONSTRUCTION.md '
         'section 4 P2 (no CSV row); budget 250 tris.')


def build(b):
    outer = b.add(prism(rounded_rect(0.550, 0.350, 0.020), 0.040),
                  'wood', MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
                  smooth=False, name='tray')
    inner = b.add(prism(rounded_rect(0.500, 0.300, 0.016), 0.040, start=0.012),
                  'wood', MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
                  smooth=False, name='cavity')
    b.boolean_difference(outer, [inner])
    return b.finish(TARGET_LONGEST_M)
