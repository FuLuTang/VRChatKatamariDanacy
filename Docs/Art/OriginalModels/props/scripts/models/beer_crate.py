"""Beer crate - 40 x 30 x 28 cm open plastic bottle crate, six cells.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_beer_crate_01/02 as beer_crate,
0.40 x 0.28 x 0.30 m (x x height x z), D_req 0.863; LEVEL_CONSTRUCTION.md 4 P2
carries it as 0.40 x 0.30 x 0.28 with a 400 tris budget.  prop_catalog.csv has
no row, so the module declares DOC_ROW.

The map uses the crate as the bed-south "2 cm step off the flower bed" (the
layout's climb_from = bed, rise 0.020) and as the second blocking piece of that
group.  A closed box would read as a wooden crate and duplicate `crate`, so this
is the real beer-crate shape: an open-topped plastic crate with a 16 mm wall, a
20 mm floor and six bottle cells.

Cells are 2 rows x 3 columns: one divider along X through the middle, two along
Y at x = +/-0.061.  The dividers are 8 mm webs that stop 20 mm below the rim, so
the crate reads as "bottles would stand in here" from the 3/4 angle without any
bottles (bottles would break the 0.28 m AABB the layout needs).  Two hand holes
are cut through the short ends; a crate you cannot pick up does not read as a
crate.  The rim stays a flat ring at z = 0.28, which is what the ball's step
height uses.
"""

from prop_lib import prism, rounded_rect

ID = 'beer_crate'
NAME_ZH = '啤酒箱'
CATEGORY = 'container'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / LEVEL_CONSTRUCTION.md 4 P2（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.40
TRI_BUDGET = 400
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.40', 'game_longest_dim_m': '0.40'}
MATERIALS = [
    {'key': 'shell', 'name_zh': '箱体瓶绿', 'linear_rgb': [0.035, 0.090, 0.055],
     'roughness': 0.58},
    {'key': 'web', 'name_zh': '隔板浅灰绿', 'linear_rgb': [0.180, 0.230, 0.195],
     'roughness': 0.62},
    {'key': 'floor', 'name_zh': '底板深灰', 'linear_rgb': [0.045, 0.050, 0.048],
     'roughness': 0.80},
]
NOTES = ('Outer 0.400 x 0.300 x 0.280 rounded-rect (R 12 mm, 12-gon) with a '
         'cavity 0.368 x 0.268 (R 8 mm) cut from z = 0.020 through the top, '
         'leaving a 16 mm wall and a 20 mm floor.  Six cells: one 8 mm web '
         'along X at y = 0, two along Y at x = +/-0.0613, all from z 0.020 to '
         '0.260.  Two 0.100 x 0.030 hand holes cut 22 mm into the +/-X ends at '
         'z 0.190-0.220.  AABB 0.400 x 0.300 x 0.280, rim flat at z = 0.280.  '
         'Materials by face centre: floor below z 0.0215, webs within 6 mm of '
         'the divider planes, shell elsewhere.')

OUTER_W, OUTER_D, OUTER_H = 0.400, 0.300, 0.280
WALL, FLOOR_T = 0.016, 0.020
WEB_T, WEB_TOP = 0.008, 0.260
CELL_X = 0.0613
HAND_W, HAND_H, HAND_D = 0.100, 0.030, 0.022
HAND_Z = 0.190


def build(b):
    shell, web, floor = MATERIALS
    body = b.add(prism(rounded_rect(OUTER_W, OUTER_D, 0.012,
                                    corner_segments=2), OUTER_H),
                 'shell', shell['linear_rgb'], shell['roughness'],
                 smooth=False, name='body')
    cavity = b.add(prism(rounded_rect(OUTER_W - 2 * WALL, OUTER_D - 2 * WALL,
                                      0.008, corner_segments=2),
                         OUTER_H - FLOOR_T + 0.010, start=FLOOR_T),
                   'shell', shell['linear_rgb'], shell['roughness'],
                   smooth=False, name='cavity')

    inner_w, inner_d = OUTER_W - 2 * WALL, OUTER_D - 2 * WALL
    webs = [b.add(prism([(-0.004, -inner_d * 0.5), (0.004, -inner_d * 0.5),
                         (0.004, inner_d * 0.5), (-0.004, inner_d * 0.5)],
                        WEB_TOP - FLOOR_T, start=FLOOR_T),
                  'web', web['linear_rgb'], web['roughness'], smooth=False,
                  name='webY')]
    for side in (-1.0, 1.0):
        cx = side * CELL_X
        webs.append(b.add(
            prism([(cx - 0.004, -inner_d * 0.5), (cx + 0.004, -inner_d * 0.5),
                   (cx + 0.004, inner_d * 0.5), (cx - 0.004, inner_d * 0.5)],
                  WEB_TOP - FLOOR_T, start=FLOOR_T),
            'web', web['linear_rgb'], web['roughness'], smooth=False,
            name='webX%d' % (1 if side > 0 else 0)))
    b.boolean_difference(body, [cavity])
    b.boolean_union(body, webs)

    holes = []
    profile = [(-HAND_W * 0.5, HAND_Z), (HAND_W * 0.5, HAND_Z),
               (HAND_W * 0.5, HAND_Z + HAND_H), (-HAND_W * 0.5, HAND_Z + HAND_H)]
    for side in (-1.0, 1.0):
        outer = side * (OUTER_W * 0.5 + 0.010)
        inner = side * (OUTER_W * 0.5 - HAND_D)
        holes.append(b.add(prism(profile, abs(outer - inner), axis='x',
                                 start=min(outer, inner)),
                           'shell', shell['linear_rgb'], shell['roughness'],
                           smooth=False, name='hand%d' % (1 if side > 0 else 0)))
    b.boolean_difference(body, holes)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        c = poly.center
        if c.z < 0.0215:
            poly.material_index = 2
        elif (FLOOR_T + 0.0004 < c.z < WEB_TOP + 0.0005
              and ((abs(c.y) <= WEB_T * 0.5 + 0.0005
                    and abs(c.x) <= inner_w * 0.5 - 0.005)
                   or (abs(abs(c.x) - CELL_X) <= WEB_T * 0.5 + 0.0005
                       and abs(c.y) <= inner_d * 0.5 - 0.005))):
            # A long wall face can also sit at y = 0 or x = 0, so "near a
            # divider plane" alone painted the outer walls; the face must
            # additionally lie clear of the wall band.
            poly.material_index = 1
        else:
            poly.material_index = 0
    return b.finish(TARGET_LONGEST_M)
