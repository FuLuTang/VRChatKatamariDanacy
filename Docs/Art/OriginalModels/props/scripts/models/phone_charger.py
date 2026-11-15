"""Phone charger - a loosely coiled cable with a plug at one end and a connector.

MessyRoom catalogue t1 / long / 0.120 x 0.050 x 0.030 m; no catalog row, so the
module carries ``DOC_ROW``.

A charger cable is one physical piece, so it has to be one shell: a 6-sided
``tube`` swept along a loose 1.15-turn ellipse (5 mm across, never under 4 mm),
with both ends buried inside the two moulded ends - the wall plug at the outside
of the coil and the small metal connector that lies in the middle of it.  Two
Exact boolean unions weld the whole thing into one closed shell.

The coil is deliberately *loose*: the semi-axis shrinks from 46 mm to 22 mm over
1.15 turns, so the turns sit about 20 mm apart and never read as a solid disc.
The cable also rises only 2.2 mm across the coil, which is what a cable that has
just been dropped on a desk does, and the connector rests on the table in the
empty middle of the turn.
"""

import math

import bmesh

from prop_lib import prism, rounded_rect, tube

ID = 'phone_charger'
NAME_ZH = '充电线'
CATEGORY = 'electronics_cables'
SOURCING = 'model ourselves：盘线 + 插头，变半径 tube 布尔并集成单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 260
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '插头灰塑料', 'linear_rgb': [0.230, 0.235,
                                                                0.240],
     'roughness': 0.50},
    {'key': 'rubber', 'name_zh': '线皮橡胶', 'linear_rgb': [0.045, 0.045, 0.048],
     'roughness': 0.88},
    {'key': 'brass', 'name_zh': '触点黄铜', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
]
NOTES = ('Loose 1.15-turn elliptical coil (X semi-axis 0.046 -> 0.022 m, Y = 44% '
         'of X) as a 6-sided tube, cable radius 0.0026 m; wall plug 0.026 x '
         '0.018 x 0.028 m at the outer end, connector 0.016 x 0.011 x 0.009 m '
         'resting in the middle.  Both cable ends are inside those solids and '
         'welded by Exact boolean union: one shell.  Three slots: grey plug '
         'shell, rubber cable jacket, brass contacts (the connector and the '
         'metal contact plate on the plug top, both assigned by face centre so '
         'neither costs a triangle).')

TAU = math.pi * 2.0
SEGMENTS = 6
LOOP_POINTS = 10
TURNS = 1.15
ANGLE0 = math.radians(200.0)
PLUG = (-0.052, -0.010)
CONNECTOR = (0.002, 0.000)


def cable_path():
    pts = [(-0.052, -0.010, 0.0040)]
    for k in range(LOOP_POINTS):
        t = k / (LOOP_POINTS - 1.0)
        ang = ANGLE0 + TAU * TURNS * t
        a = 0.046 - 0.024 * t
        b = 0.44 * a
        pts.append((a * math.cos(ang), b * math.sin(ang),
                    0.0055 + 0.0022 * t))
    # inner end drops into the connector standing in the empty centre
    pts.append((CONNECTOR[0], CONNECTOR[1], 0.0045))
    return pts


def is_contact_plate(centre):
    """The plug's top face: the brass contact plate you press into a socket."""
    return centre.z > 0.0275


def build(b):
    plug = prism(rounded_rect(0.026, 0.018, 0.005, corner_segments=1), 0.028)
    bmesh.ops.translate(plug, vec=(PLUG[0], PLUG[1], 0.0), verts=plug.verts[:])
    plug_ob = b.add(plug, 'plastic_grey', (0.230, 0.235, 0.240), 0.50,
                    smooth=False, name='plug',
                    extras=[('brass', (0.520, 0.380, 0.120), 0.30,
                             is_contact_plate)])

    pts = cable_path()
    cable = tube(pts, [0.0026] * len(pts), segments=SEGMENTS)
    cable_ob = b.add(cable, 'rubber', (0.045, 0.045, 0.048), 0.88,
                     smooth=False, name='cable')

    connector = prism(rounded_rect(0.016, 0.011, 0.003, corner_segments=1),
                      0.009)
    bmesh.ops.translate(connector, vec=(CONNECTOR[0], CONNECTOR[1], 0.0),
                        verts=connector.verts[:])
    connector_ob = b.add(connector, 'brass', (0.520, 0.380, 0.120), 0.30,
                         smooth=False, name='connector')

    b.boolean_union(plug_ob, [cable_ob, connector_ob])
