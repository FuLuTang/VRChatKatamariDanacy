"""Bowl - 16 cm enamel bowl, 6 cm tall, hollow with a foot ring.

PROP_CATALOG.md gives 0.16 x 0.16 x 0.06 m.  The profile climbs the outside
from the recessed base through the foot ring and the flared lip, crosses the
5 mm rim and comes back down the inside to the floor, so the cavity is real and
the mesh is one closed shell.  This is the same lathe trick as the cup, but the
wall is thicker and the lip flares, which is what separates a bowl from a
straight tumbler at silhouette size.

Sixteen segments and 14 profile points give 384 triangles, under the 400
budget.  The base is 6.5 mm thick and the wall 4.5-5 mm, chunky enough that the
inside does not collapse into a dark unreadable hole in the preview.
"""

from prop_lib import lathe

ID = 'bowl'
NAME_ZH = '碗'
CATEGORY = 'tableware'
CSV_REAL_LONGEST_M = 0.16
CSV_GAME_LONGEST_M = 0.42
SOURCING = 'model ourselves：Ø0.16 x 0.06 m 搪瓷碗，空心 + 圈足'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.16
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'enamel', 'name_zh': '蓝灰搪瓷', 'linear_rgb': [0.150, 0.280, 0.300],
     'roughness': 0.35},
]
NOTES = ('16 segments, 14 profile points = 384 triangles.  Diameter 0.160 m at '
         'the lip, height 0.060 m.  Foot ring 0.045-0.055 m at z=0; outside '
         'flares from 0.060 m at z=0.006 to 0.080 m at the lip; inside runs from '
         '0.075 m at the rim to a 0.0065 m floor.  Wall 4.5-5 mm, base 6.5 mm.  '
         'One closed shell, no separate foot, no cap inside.')

PROFILE = [
    (0.0000, 0.0040),   # recessed underside centre
    (0.0450, 0.0000),   # foot ring
    (0.0550, 0.0000),
    (0.0600, 0.0060),
    (0.0680, 0.0220),
    (0.0740, 0.0420),
    (0.0800, 0.0600),   # lip, outside
    (0.0750, 0.0600),   # lip, inside (5 mm)
    (0.0715, 0.0500),
    (0.0680, 0.0340),
    (0.0620, 0.0180),
    (0.0480, 0.0090),
    (0.0300, 0.0068),
    (0.0000, 0.0065),   # floor centre
]


def build(b):
    bm = lathe(PROFILE, segments=16)
    b.add(bm, 'enamel', (0.150, 0.280, 0.300), 0.35, smooth=True)
