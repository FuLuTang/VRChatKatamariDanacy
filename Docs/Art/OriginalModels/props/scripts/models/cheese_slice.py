"""Cheese slice - 6 x 5 x 0.8 cm wedge with three blind 1 cm holes.

PROP_SET_PLAN.md section 3.3: ``6x5x0.8 cm 三角薄片，3 个 Ø1 cm 孔（孔做凹不做
穿）``.  The slab is a triangular prism (a right triangle with 6 cm and 5 cm
legs, so the AABB is exactly 0.06 x 0.05 x 0.008).  The three holes are blind
dents 4 mm deep, cut in with an Exact boolean difference: a dent that does not
break through cannot be built by welding shells, which is why ``boolean_difference``
exists in prop_lib.

The three dents sit inside the inscribed circle (the triangle's inradius is
16 mm, each dent's outer edge is 12 mm from the incentre), so no dent touches an
edge and the slice stays a single closed shell with no slivers.
"""

from prop_lib import lathe, prism

ID = 'cheese_slice'
NAME_ZH = '奶酪片'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.06
CSV_GAME_LONGEST_M = 0.08
SOURCING = ('model ourselves：6×5×0.8 cm 三角片，3 个 Ø1 cm 凹孔'
            '（原概念“楔形”不成立，改片状）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.06
TRI_BUDGET = 200
MATERIALS = [
    {'key': 'cheese', 'name_zh': '奶酪浅黄', 'linear_rgb': [0.720, 0.520, 0.120],
     'roughness': 0.55},
]
NOTES = ('Right-triangle prism, 0.060 x 0.050 m footprint and 0.008 m thick: '
         '8 triangles.  Three 10-segment dents of 5 mm radius and 4 mm depth are '
         'subtracted from the top face (exact boolean), leaving a closed solid '
         'with no through holes; the final count is about 150 triangles.  Dent '
         'centres (0.01405, -0.00205), (0.00799, -0.01255) and (0.02011, '
         '-0.01255) m, i.e. within 12 mm of the incentre, so every dent stays '
         'inside the 15.95 mm incentre radius and the wedge keeps its edges.')

THICKNESS = 0.008
SECTION = [(-0.0300, -0.0250), (0.0300, -0.0250), (0.0300, 0.0250)]
DENT_RADIUS = 0.0050
DENT_FLOOR = 0.0040
DENT_TOP = 0.0120
DENT_SEGMENTS = 10
DENT_CENTRES = [(0.01405, -0.00205), (0.00799, -0.01255), (0.02011, -0.01255)]


def build(b):
    body = b.add(prism(SECTION, THICKNESS, axis='z', start=0.0),
                 'cheese', (0.720, 0.520, 0.120), 0.55, smooth=False,
                 name='wedge')
    dents = []
    for i, (cx, cy) in enumerate(DENT_CENTRES):
        bm = lathe([(DENT_RADIUS, DENT_FLOOR), (DENT_RADIUS, DENT_TOP)],
                   segments=DENT_SEGMENTS)
        for v in bm.verts:
            v.co.x += cx
            v.co.y += cy
        dents.append(b.add(bm, 'cheese', (0.720, 0.520, 0.120), 0.55,
                           smooth=False, name='dent%d' % i))
    b.boolean_difference(body, dents)
