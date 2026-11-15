"""Eraser - 50 x 24 x 12 mm rounded block in a shorter paper sleeve.

The sleeve is a closed rectangular tube (outer 24 x 12 mm, wall about 2 mm)
running 40 mm along X, so 5 mm of the 50 mm block shows at both ends.  The
rubber core is deliberately wider and taller than the tube's cavity: the union
therefore welds into one closed solid instead of leaving a nested shell.

Material pass (2026-11-15 rework): the old pair was a pale pink rubber
(0.76/0.52/0.50) and a beige sleeve (0.68/0.61/0.41) - two mid-high slots within
0.08 of each other and no dark slot.  Now the sleeve is the pale paper key, the
two exposed ends are a blue rubber end and a red rubber end (a two-ended eraser),
and a 0.4 mm recessed label slot on the sleeve top carries the dark ink slot.
The recess is cut with a small Exact boolean difference, so the sleeve keeps its
12 mm height exactly - no sliver is added above the surface.
"""

from prop_lib import prism, rounded_rect

ID = 'eraser'
NAME_ZH = '橡皮'
CATEGORY = 'stationery'
SOURCING = 'model ourselves：50 x 24 x 12 mm 圆角橡皮 + 纸套'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.050
TRI_BUDGET = 160
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.050', 'game_longest_dim_m': '0.050'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '纸套米黄', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'plastic_blue', 'name_zh': '蓝橡皮端', 'linear_rgb': [0.075, 0.145, 0.400],
     'roughness': 0.48},
    {'key': 'plastic_red', 'name_zh': '红橡皮端', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.48},
    {'key': 'ink', 'name_zh': '纸套暗标签', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Rubber core 50 x 22 x 10 mm (R 2 mm); paper sleeve outer 50 x 24 x '
         '12 mm (R 2 mm) and 40 mm long, cavity 19 x 7 mm cut through so the '
         'sleeve is a closed ring, then unioned with the oversized core.  Exposed '
         'ends beyond |x| 20 mm are the rubber slots (blue at -X, red at +X); the '
         'sleeve is paper.  A 16 x 10 x 0.4 mm label recess is cut into the sleeve '
         'top and takes the ink slot, which is the dark print panel and keeps the '
         'overall height at 12 mm.')

CORE_L, CORE_W, CORE_H = 0.050, 0.022, 0.010
SLEEVE_L, SLEEVE_W, SLEEVE_H = 0.040, 0.024, 0.012
CAVITY_W, CAVITY_H = 0.019, 0.007
RUBBER_FROM = 0.0201
LABEL_X0, LABEL_X1 = -0.010, 0.006
LABEL_Y = 0.005
LABEL_DEPTH = 0.0004

PAPER = (0.800, 0.775, 0.700)
BLUE = (0.075, 0.145, 0.400)
RED = (0.480, 0.075, 0.060)
INK = (0.030, 0.035, 0.045)


def build(b):
    sleeve = b.add(
        prism(rounded_rect(SLEEVE_W, SLEEVE_H, 0.002, corner_segments=1),
              SLEEVE_L, axis='x', start=-SLEEVE_L * 0.5),
        'paper', PAPER, 0.90, smooth=False,
        name='sleeve')
    cavity = b.add(
        prism(rounded_rect(CAVITY_W, CAVITY_H, 0.0015, corner_segments=1),
              CORE_L + 0.004, axis='x', start=-(CORE_L + 0.004) * 0.5),
        'paper', PAPER, 0.90, smooth=False,
        name='cavity')
    b.boolean_difference(sleeve, [cavity])
    core = b.add(
        prism(rounded_rect(CORE_W, CORE_H, 0.002, corner_segments=1),
              CORE_L, axis='x', start=-CORE_L * 0.5),
        'paper', PAPER, 0.90, smooth=False,
        name='core')
    b.boolean_union(sleeve, [core])

    # Recessed print panel on the sleeve top (z = +6 mm): the cut box starts
    # just under the surface and stops above it, so only the recess survives.
    top = SLEEVE_H * 0.5
    label = b.add(
        prism([(LABEL_X0, -LABEL_Y), (LABEL_X1, -LABEL_Y),
               (LABEL_X1, LABEL_Y), (LABEL_X0, LABEL_Y)],
              LABEL_DEPTH + 0.001, axis='z',
              start=top - LABEL_DEPTH),
        'paper', PAPER, 0.90, smooth=False, name='label_cut')
    b.boolean_difference(sleeve, [label])

    info = b.finish(TARGET_LONGEST_M)
    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        c = poly.center
        if c.x < -RUBBER_FROM:
            poly.material_index = 1
        elif c.x > RUBBER_FROM:
            poly.material_index = 2
        elif (LABEL_X0 - 0.0005 < c.x < LABEL_X1 + 0.0005
              and abs(c.y) < LABEL_Y + 0.0005
              and c.z > top - LABEL_DEPTH - 0.0008):
            poly.material_index = 3
        else:
            poly.material_index = 0
    return info
