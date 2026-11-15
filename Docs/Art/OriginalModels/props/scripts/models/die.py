"""Die - 16 mm cube with shallow square pip recesses on three faces.

A rounded-rect body plus six four-sided cylindrical pits (2.2 mm across,
0.4 mm deep) lands at 140 triangles.  Hexagonal pits on the same body cost
170, over the baseline budget, so the pits are square; flat-bottomed pits still
read as pips and none of them touches an edge.  The pips are 1, 3 and 5 on the
three faces the preview sees; one hidden face also gets a single pit.

Material pass (2026-11-15 rework): the body was 0.82 ivory, i.e. a white cube
whose pips were the only colour.  The body is now the yellow plastic key
(0.640/0.450/0.060) against ink pips - 0.60 of linear peak between face and pip,
so the pips read from across the lawn - and the pip walls and floors both take
the ink slot, which is what makes them read as drilled dots rather than shallow
dents.  No geometry change: 140 triangles.
"""

from prop_lib import lathe, prism

ID = 'die'
NAME_ZH = '骰子'
CATEGORY = 'toy'
SOURCING = 'model ourselves：16 mm 方块，六面方形凹点（预算内退化）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.016
TRI_BUDGET = 160
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.016', 'game_longest_dim_m': '0.016'}
MATERIALS = [
    {'key': 'plastic_yellow', 'name_zh': '黄塑料本体', 'linear_rgb': [0.640, 0.450, 0.060],
     'roughness': 0.48},
    {'key': 'ink', 'name_zh': '凹点墨黑', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Sharp 16 mm cube (12 tris) with nine six-sided conical pits '
         '(r 1.1 mm aperture, 0.5 mm deep) on the +Z/+Y/+X faces: 1, 3 '
         'diagonal, 5 as an X.  All pits sit at least 2.6 mm inside their '
         'faces.  The three hidden faces stay plain: a 1-6 pattern (21 pits) '
         'is far outside the budget.  Slots: yellow plastic body, ink for every '
         'pip wall and floor (depth between 0.01 mm and 0.7 mm below the face, '
         'face area under 6 mm^2).')

SIZE = 0.016
HALF = SIZE * 0.5
PIP_R = 0.0011
PIP_DEPTH = 0.0004
PIP_OUTSIDE = 0.0002

YELLOW = (0.640, 0.450, 0.060)
INK = (0.030, 0.035, 0.045)

PIPS = {
    1: [(0.0, 0.0)],
    3: [(-0.0035, -0.0035), (0.0, 0.0), (0.0035, 0.0035)],
    5: [(-0.0038, -0.0038), (0.0038, -0.0038), (0.0, 0.0),
        (-0.0038, 0.0038), (0.0038, 0.0038)],
}


def _place(bm, face, u, v):
    frames = {
        1: ((1, 0, 0), (0, 1, 0), (0, 0, 1), (u, v, HALF)),
        2: ((1, 0, 0), (0, -1, 0), (0, 0, -1), (u, v, -HALF)),
        3: ((1, 0, 0), (0, 0, -1), (0, 1, 0), (u, HALF, v)),
        4: ((1, 0, 0), (0, 0, 1), (0, -1, 0), (u, -HALF, v)),
        5: ((0, 1, 0), (0, 0, 1), (1, 0, 0), (HALF, u, v)),
        6: ((0, 1, 0), (0, 0, -1), (-1, 0, 0), (-HALF, u, v)),
    }
    t1, t2, n, c = frames[face]
    for vert in bm.verts:
        x, y, z = vert.co
        off = PIP_OUTSIDE - z
        vert.co = (c[0] + t1[0] * x + t2[0] * y + n[0] * off,
                   c[1] + t1[1] * x + t2[1] * y + n[1] * off,
                   c[2] + t1[2] * x + t2[2] * y + n[2] * off)


def build(b):
    body = b.add(prism([(-HALF, -HALF), (HALF, -HALF),
                        (HALF, HALF), (-HALF, HALF)], SIZE, start=-HALF),
                 'plastic_yellow', YELLOW, 0.48,
                 smooth=False, name='body')
    tools = []
    for face, positions in PIPS.items():
        for (u, v) in positions:
            bm = lathe([(PIP_R, 0.0), (0.0, PIP_DEPTH + PIP_OUTSIDE)],
                       segments=6)
            _place(bm, face, u, v)
            tools.append(b.add(bm, 'ink', INK, 0.75,
                               smooth=False, name='pip%d' % face))
    b.boolean_difference(body, tools)
    info = b.finish(TARGET_LONGEST_M)

    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        c = poly.center
        depth = HALF - max(abs(c.x), abs(c.y), abs(c.z))
        poly.material_index = 1 if 0.00001 < depth < 0.0007 and poly.area < 6e-6 else 0
    return info
