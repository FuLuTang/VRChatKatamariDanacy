"""Juice carton - 6 x 6 x 10 cm gable-top carton with a printed front.

The catalog id is ``juice_carton`` (not ``juice_box``) and its real size is
0.10 m; PROP_CATALOG.md gives the three-axis as 0.06 x 0.06 x 0.10.  The body is
a pentagonal prism: a 6 cm square footprint up to 7.4 cm, then a gable that
closes at 10 cm.  Extruding the house-shaped section along Y makes the two
triangular gable ends the front and back faces, which is the silhouette of a
gable-top carton rather than a plain box.

``prism`` closes the two pentagon ends with n-gons; ``finish`` triangulates
them.  The orange material is the whole front pentagon, so the carton has a
printed face without a texture, UVs or extra geometry.  16 triangles total.
"""

from prop_lib import prism

ID = 'juice_carton'
NAME_ZH = '果汁盒'
CATEGORY = 'drink'
CSV_REAL_LONGEST_M = 0.10
CSV_GAME_LONGEST_M = 0.34
SOURCING = 'model ourselves：0.06 x 0.06 x 0.10 m 山墙顶纸盒'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.10
TRI_BUDGET = 200
MATERIALS = [
    {'key': 'card', 'name_zh': '纸盒米白', 'linear_rgb': [0.700, 0.640, 0.500],
     'roughness': 0.70},
    {'key': 'panel', 'name_zh': '正面橙汁色', 'linear_rgb': [0.560, 0.190, 0.020],
     'roughness': 0.60},
]
NOTES = ('Ten-point section in the XZ plane: 0.060 m wide, square body to '
         '0.080 m, gable to 0.094 m, then a 4 mm wide, 6 mm tall fin seal up to '
         'the 0.100 m target; extruded 0.060 m along Y.  The front cap '
         '(y=+0.030) is the orange printed panel, the sides and the back are '
         'cream.  The fin is what separates the silhouette from a house roof.  '
         '36 triangles, one closed shell.  No straw or screw cap: either would '
         'push an axis past the 0.06 x 0.06 x 0.10 design box.')

SECTION = [  # (x, z), gable-top carton seen from the front
    (-0.0300, 0.0000),
    (0.0300, 0.0000),
    (0.0300, 0.0800),   # top of the square body
    (0.0060, 0.0940),   # gable shoulder
    (0.0020, 0.0940),   # fin base
    (0.0020, 0.1000),   # fin top, the sealed ridge of a gable carton
    (-0.0020, 0.1000),
    (-0.0020, 0.0940),
    (-0.0060, 0.0940),
    (-0.0300, 0.0800),
]
DEPTH = 0.060


def build(b):
    bm = prism(SECTION, DEPTH, axis='y', start=-0.030)
    b.add(bm, 'card', (0.700, 0.640, 0.500), 0.70, smooth=False,
          extras=[('panel', (0.560, 0.190, 0.020), 0.60,
                   lambda c: c.y > 0.0295)])
