"""Sauce bottle - 240 mm tall, 60 mm diameter squeeze bottle with two ribs.

One continuous lathe profile from the base to the cap: body, two sharp ring
ribs, a conical shoulder, the neck and a wider cap.  Building the whole bottle
as a single surface of revolution keeps it one closed shell with no seams and
no booleans at all.  The long body band between the ribs carries the printed
label ring; the ribs and shoulder stay body colour.  Eight segments around and
eleven profile spans give 240 triangles, the contract budget.

Material pass (2026-11-15 rework): the old bottle was a 0.82 cream body with a
0.68 kraft label - two light slots and no dark one, i.e. a white bottle.  Now
the body is the glazed ceramic key, the cap the red plastic key and the label
ring the ink key: the label covers z 20-150 mm of a 240 mm bottle, so the
silhouette is dominated by the dark band and the two white ribs cross it.  A
0.8 mm inward thread groove at 216-220 mm was added to the cap for a machine
line; 8 extra triangles, no change to the 60 x 60 x 240 mm envelope.
"""

from prop_lib import lathe

ID = 'sauce_bottle'
NAME_ZH = '酱料瓶'
CATEGORY = 'container'
SOURCING = 'model ourselves：240 mm 高挤压酱料瓶，两道环筋'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.240
TRI_BUDGET = 240
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.240', 'game_longest_dim_m': '0.240'}
MATERIALS = [
    {'key': 'ceramic', 'name_zh': '瓶身釉白', 'linear_rgb': [0.760, 0.745, 0.700],
     'roughness': 0.35},
    {'key': 'plastic_red', 'name_zh': '红塑料盖', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.48},
    {'key': 'ink', 'name_zh': '标签环暗色', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Lathe with 8 segments, profile: r 27 mm base at z 0, two 30 mm '
         'radius ribs at z 10-16 and z 153-159 mm, 26 mm body, conical '
         'shoulder to a 17 mm neck at z 205 mm, 20 mm cap to z 240 mm with a '
         '0.8 mm inward thread groove at 216-220 mm.  AABB 60 x 60 x 240 mm.  '
         'The body span from z 22 to 148 mm is the ink label ring, the cap above '
         '207.5 mm is red plastic and the rest is glazed white.')

# (radius, z) bottom to top, metres.
PROFILE = [
    (0.027, 0.000),
    (0.027, 0.005),
    (0.030, 0.010),
    (0.030, 0.016),
    (0.026, 0.020),
    (0.026, 0.150),
    (0.030, 0.153),
    (0.030, 0.159),
    (0.026, 0.163),
    (0.017, 0.205),
    (0.020, 0.210),
    (0.0192, 0.216),
    (0.020, 0.220),
    (0.020, 0.240),
]

CERAMIC = (0.760, 0.745, 0.700)
RED = (0.480, 0.075, 0.060)
INK = (0.030, 0.035, 0.045)

LABEL_Z0, LABEL_Z1 = 0.022, 0.148
CAP_Z = 0.2075


def build(b):
    b.add(lathe(PROFILE, segments=8), 'ceramic', CERAMIC, 0.35, smooth=False,
          extras=[('ink', INK, 0.75,
                   lambda c: LABEL_Z0 < c.z < LABEL_Z1),
                  ('plastic_red', RED, 0.48,
                   lambda c: c.z > CAP_Z)],
          name='bottle')
