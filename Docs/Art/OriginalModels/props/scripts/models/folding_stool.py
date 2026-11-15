"""Folding stool - 40 x 30 x 35 cm open camp stool, canvas seat on two X frames.

PROP_SET_PLAN.md section 3.5 gives 0.40 x 0.35 x 0.32 m, "35 x 25 cm canvas seat
+ four X-shaped aluminium legs (O 1.5 cm tube), folded state".  The "folded
state" note contradicts a usable 35 cm seat and the map's role for the stool, so
this is the open stool: the seat is a flat 35 x 25 cm slab whose top is exactly
0.32 m, and the two X frames are 1.5 cm tubes with the feet on z = 0.  A folded
bundle would be a different silhouette and would leave nothing to stand on.

The two X frames are planar trapezoids: the top bar runs under the seat, the
bottom bar is 3.5 cm wider on the floor, and the side rails lean out.  Frame A's
top bar is on -Y and its feet on +Y; frame B is mirrored, so the two frames
cross at mid height on both sides - that crossing is what keeps the mesh one
closed shell even before the seat is welded on.  The four rails end a few
millimetres inside the top and bottom bars instead of butting against them, so
every union has real overlap rather than a face-to-face contact.

Leg gaps are the point of the prop: there is 0.30 m of clear space under the
seat between the two frames, so a 0.30 m ball can sit under it later.  No
collider in this batch; the suggested split is seat = 1 box, each frame = 1 box
(4 boxes total), with the gaps left empty.
"""

from prop_lib import prism, rounded_rect, tube

ID = 'folding_stool'
NAME_ZH = '折叠凳'
CATEGORY = 'furniture'
CSV_REAL_LONGEST_M = 0.40
CSV_GAME_LONGEST_M = 1.40
SOURCING = 'model ourselves：0.40 m 布面凳放大到 1.40 m'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.40
TRI_BUDGET = 700
MATERIALS = [
    {'key': 'canvas', 'name_zh': '座面帆布墨绿', 'linear_rgb': [0.085, 0.170, 0.095],
     'roughness': 0.90},
    {'key': 'frame', 'name_zh': '铝腿银灰', 'linear_rgb': [0.550, 0.570, 0.580],
     'roughness': 0.35},
]
NOTES = ('Seat slab 0.350 x 0.250 x 0.012 (R 20 mm) with its top at 0.320; '
         'two planar trapezoid frames of 1.5 cm tubes (r 0.0075): top bar '
         '0.336 long at y = -/+0.1175, z = 0.308, bottom bar 0.400 long at '
         'y = +/-0.1685, z = 0.0075, rails ending 7.5 mm inside each bar.  AABB '
         '0.400 x 0.350 x 0.320, feet on z = 0.  The two frames cross at '
         'mid-height, so the union is one shell.  Materials by face centre: '
         'canvas above z 0.3075 inside the seat outline, frame elsewhere.')

SEAT_W, SEAT_D, SEAT_T = 0.350, 0.250, 0.012
SEAT_TOP = 0.320
SEAT_Z0 = SEAT_TOP - SEAT_T
TOP_X, TOP_Y, TOP_Z = 0.168, 0.1175, 0.308
BOT_X, BOT_Y, BOT_Z = 0.200, 0.1685, 0.0075
RAIL_BOT_X, RAIL_TOP_X = 0.1925, 0.161
TUBE_R = 0.0075


def _bar(b, p0, p1, name, mat):
    return b.add(tube([p0, p1], [TUBE_R, TUBE_R], segments=6),
                 mat['key'], mat['linear_rgb'], mat['roughness'],
                 smooth=True, name=name)


def build(b):
    seat = b.add(prism(rounded_rect(SEAT_W, SEAT_D, 0.020), SEAT_T,
                       start=SEAT_Z0),
                 'canvas', MATERIALS[0]['linear_rgb'],
                 MATERIALS[0]['roughness'], smooth=False, name='seat')

    canvas, frame = MATERIALS
    for tag, top_y, bot_y in (('A', -TOP_Y, BOT_Y), ('B', TOP_Y, -BOT_Y)):
        top = _bar(b, (-TOP_X, top_y, TOP_Z), (TOP_X, top_y, TOP_Z),
                   'top' + tag, frame)
        b.boolean_union(seat, [top])
        for side in (-1.0, 1.0):
            rail = _bar(b,
                        (side * RAIL_BOT_X, bot_y, BOT_Z),
                        (side * RAIL_TOP_X, top_y, TOP_Z),
                        'rail%s%d' % (tag, 0 if side < 0 else 1), frame)
            b.boolean_union(seat, [rail])
        bottom = _bar(b, (-BOT_X, bot_y, BOT_Z), (BOT_X, bot_y, BOT_Z),
                      'bot' + tag, frame)
        b.boolean_union(seat, [bottom])

    me = seat.data
    me.materials.clear()
    me.materials.append(b.material('canvas', canvas['linear_rgb'],
                                   canvas['roughness']))
    me.materials.append(b.material('frame', frame['linear_rgb'],
                                   frame['roughness']))
    for poly in me.polygons:
        c = poly.center
        on_seat = (c.z > SEAT_Z0 - 0.0005
                   and abs(c.x) < SEAT_W * 0.5 + 0.001
                   and abs(c.y) < SEAT_D * 0.5 + 0.001)
        poly.material_index = 0 if on_seat else 1
    return b.finish(TARGET_LONGEST_M)
