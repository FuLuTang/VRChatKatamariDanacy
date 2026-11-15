"""Cutting board - 280 x 200 x 15 mm board with a handle hole and a juice groove.

Design three-axis 0.280 x 0.200 x 0.015 m.  Twelve-point rounded rectangle (22 mm
corner radius) extruded 15 mm is the board; a 12-sided cylinder 4 mm longer than
the board is subtracted from it, which leaves a genuine through hole near the +X
end while keeping a single closed shell - the exact difference is the same
technique the cheese slice uses for its blind holes.

The 23 mm wall between the hole and the board's end is what lets a hole this size
read at preview scale without the corner rounding eating into it.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): the single
light-wood slot (linear 0.560 / 0.400 / 0.225) was the "too pale, no grain" case
the owner called out.  The spec's own prescription for this prop is
`wood` + `wood_dark` border or one oil groove, so the board now carries both:
  * `straw`     - the board face (linear 0.470 / 0.360 / 0.140, rough 0.90): a
                  mid-tone beech, inside the 0.30-0.65 body band, where the old
                  0.560 / 0.400 / 0.225 face sat at luma 0.42 and washed out
  * `wood_dark` - the 12 mm juice groove along the far edge and the whole edge
                  band (0.135 / 0.078 / 0.038, rough 0.75), which is what reads as
                  grain/end-grain at 512 px; the face/groove pair is a 0.28 luma
                  step, i.e. the two-tone read the spec asks for
The groove is real geometry: a 0.240 x 0.012 m tool is subtracted 7 mm into the
15 mm board, and it stops 16 mm short of both ends, so the shell stays single and
closed and the AABB is unchanged.
"""

from prop_lib import lathe, prism, rounded_rect

ID = 'cutting_board'
NAME_ZH = '小砧板'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：280 x 200 x 15 mm 圆角砧板 + 提手通孔 + 导油槽'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.280
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.280', 'game_longest_dim_m': '0.280'}
MATERIALS = [
    {'key': 'straw', 'name_zh': '砧板木面', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '导油槽与侧边深木',
     'linear_rgb': [0.135, 0.078, 0.038], 'roughness': 0.75},
]
NOTES = ('12-point rounded rectangle (0.280 x 0.200 m, corner radius 0.022 m, '
         'two segments per corner) extruded 0.015 m from z=0; a 12-sided, '
         '0.017 m radius cylinder at x=0.100 m, y=0 is subtracted through the '
         'full thickness, leaving a real hole 0.023 m from the board end.  '
         'Detail: a 0.240 x 0.012 m groove is subtracted 7 mm into the board at '
         'y 0.068-0.080 m (the oil groove); its floor, its walls and every side '
         'face take wood_dark.  One closed shell after the two differences.')

BOARD_W, BOARD_D, BOARD_T = 0.280, 0.200, 0.015
CORNER_R = 0.022
CORNER_SEGMENTS = 2
HOLE_R = 0.017
HOLE_X = 0.100
HOLE_SEGMENTS = 12
HOLE_OVERSHOOT = 0.002
WOOD_RGB = (0.470, 0.360, 0.140)
WOOD_DARK_RGB = (0.135, 0.078, 0.038)
# Juice groove: 12 mm wide, 7 mm deep, 240 mm long, set 20 mm in from the edge.
GROOVE_X = 0.120
GROOVE_Y0, GROOVE_Y1 = 0.068, 0.080
GROOVE_FLOOR = 0.008


def _groove_polygon():
    return [(-GROOVE_X, GROOVE_Y0), (GROOVE_X, GROOVE_Y0),
            (GROOVE_X, GROOVE_Y1), (-GROOVE_X, GROOVE_Y1)]


def _slot(centre):
    c = centre
    if GROOVE_FLOOR - 1e-4 < c.z < BOARD_T - 1e-4 \
            and GROOVE_Y0 - 1e-4 < c.y < GROOVE_Y1 + 1e-4:
        return 1
    if abs(c.z - BOARD_T * 0.5) < 1e-6:
        return 1
    return 0


def build(b):
    board = b.add(prism(rounded_rect(BOARD_W, BOARD_D, CORNER_R, CORNER_SEGMENTS),
                        BOARD_T, axis='z'),
                  'straw', WOOD_RGB, 0.90, smooth=False, name='board')
    hole = b.add(lathe([(HOLE_R, -HOLE_OVERSHOOT),
                        (HOLE_R, BOARD_T + HOLE_OVERSHOOT)],
                       segments=HOLE_SEGMENTS),
                 'straw', WOOD_RGB, 0.90, smooth=False, name='hole')
    for v in hole.data.vertices:
        v.co.x += HOLE_X
    groove = b.add(prism(_groove_polygon(), BOARD_T, axis='z', start=GROOVE_FLOOR),
                   'straw', WOOD_RGB, 0.90, smooth=False, name='groove')
    b.boolean_difference(board, [hole, groove])

    me = board.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
