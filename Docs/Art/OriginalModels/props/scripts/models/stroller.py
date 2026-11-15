"""Stroller - 100 x 55 x 100 cm pram: bassinet, canopy, four wheels, push bar.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_stroller_01 as stroller,
1.00 x 1.00 x 0.55 m, D_req 2.190 - the level's last bite (2.19 <= 2.20) and its
tallest landmark.  LEVEL_CONSTRUCTION.md 4 P1 carries the same piece as
1.00 x 0.55 x 1.00 with a 900 tris budget.  prop_catalog.csv has no row, so the
module declares DOC_ROW.

Axis note: the layout's triple is (x, height, z), so the pram is 1.00 m along
world X, 0.55 m along world Z and 1.00 m tall.  It is therefore modelled with
its travel direction on +X (rear at -X), not on +Y like the small props.  The
AABB is the gameplay contract here - D_req and the blocking box are computed
from it and the layout places this def with yaw = 0 - so the silhouette fills
exactly 1.00 x 0.55 x 1.00 instead of being turned 90 degrees to face +Y.

Parts: four O 0.18 m wheels that reach x = +/-0.500, y = +/-0.275 and sit on
z = 0; two axles; two chassis rails carrying a 0.52 x 0.40 under-basket; four
fork struts from the rails to the wheel hubs; a 0.62 x 0.42 open bassinet tub; a
half-cylinder canopy over the head end; and a U-shaped push bar whose top the
path puts at exactly z = 1.000.  Every part overlaps its neighbour: the first
pass had the bar starting outside the bassinet and the rails 100 mm away from
the wheels, and exported as four shells.

Materials: dark rubber wheels, grey metal chassis/bar, dark basket, pale
bassinet, slightly darker canopy.  All by geometric predicate - the wheel mask
is "within 0.098 m of a wheel axis while the face centre is outside |y| 0.230",
which is the only way to colour a wheel welded to an axle without giving it its
own shell.
"""

import math

from prop_lib import rounded_rect, prism, tube

ID = 'stroller'
NAME_ZH = '婴儿车'
CATEGORY = 'furniture'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / LEVEL_CONSTRUCTION.md 4 P1（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 1.00
TRI_BUDGET = 900
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '1.00', 'game_longest_dim_m': '1.00'}
MATERIALS = [
    {'key': 'rubber', 'name_zh': '轮胎深灰', 'linear_rgb': [0.028, 0.030, 0.032],
     'roughness': 0.88},
    {'key': 'metal', 'name_zh': '车架银灰', 'linear_rgb': [0.520, 0.545, 0.560],
     'roughness': 0.38},
    {'key': 'basket', 'name_zh': '底篮深布', 'linear_rgb': [0.055, 0.062, 0.070],
     'roughness': 0.90},
    {'key': 'body', 'name_zh': '睡篮米白', 'linear_rgb': [0.560, 0.545, 0.490],
     'roughness': 0.85},
    {'key': 'canopy', 'name_zh': '顶篷灰绿', 'linear_rgb': [0.115, 0.150, 0.120],
     'roughness': 0.88},
]
NOTES = ('Wheels O 0.180 x 0.040 at (+/-0.410, +/-0.255, 0.090) - the four '
         'extremes give x +/-0.500, y +/-0.275, z 0.  Axles O 24 mm at '
         'x +/-0.410, z 0.090.  Chassis rails O 28 mm at y +/-0.190, z 0.190; '
         'four O 24 mm fork struts from (+/-0.410, +/-0.190, 0.190) to '
         '(+/-0.410, +/-0.245, 0.090).  Under-basket 0.520 x 0.400 x 0.165 at '
         'z 0.135-0.300 (solid dark block; its top is welded into the tub '
         'floor, and the rails run through its side walls).  '
         'Bassinet '
         '0.620 x 0.420 (R 100 mm) at z 0.295-0.440 with a 0.560 x 0.360 '
         'cavity from z 0.345.  Canopy: half-cylinder R 200 mm, centre '
         '(-0.075, 0.430), 0.420 wide, top z 0.630.  Push bar: 32 mm tube from '
         'the rails (-0.400, +/-0.190, 0.190) up to (-0.460, +/-0.150, 0.980), '
         'plus a 0.300 m cross bar (O 32 mm 8-sided cylinder on Y) at z 0.984 '
         'whose 90-degree vertex puts the crown at exactly z = 1.000.  AABB 1.000 x 0.550 x 1.000.  Materials by face '
         'extent: rubber within 0.098 of a wheel axis while the face centre is '
         'outside |y| 0.230, metal below z 0.135 or beyond x -0.305 above '
         'z 0.430 (push bar), basket 0.135-0.300, canopy above z 0.4405, body '
         'between.')

WHEEL_R, WHEEL_W = 0.090, 0.040
WHEEL_X, WHEEL_Y, WHEEL_Z = 0.410, 0.255, 0.090
RAIL_Y, RAIL_Z, RAIL_R, RAIL_X = 0.190, 0.190, 0.014, 0.410
AXLE_R, AXLE_HALF, AXLE_Z = 0.012, 0.245, 0.090
FORK_R = 0.012

BASKET_W, BASKET_D, BASKET_Z0, BASKET_Z1 = 0.520, 0.400, 0.135, 0.300

BODY_W, BODY_D, BODY_Z0, BODY_Z1 = 0.620, 0.420, 0.295, 0.440
BODY_WALL, BODY_FLOOR = 0.030, 0.050

CANOPY_CX, CANOPY_CZ, CANOPY_R = -0.075, 0.430, 0.200
CANOPY_W, CANOPY_SEGS = 0.420, 8

BAR_R = 0.016
# Two uprights plus a straight cross bar.  A single swept tube through the
# shoulder bend gives a tilted ring there and tops out at z = 0.9937, and the
# bar top IS the AABB height the layout budgets for (1.00); a cylinder prism
# whose axis lies on Y puts its 90-degree crown at 0.984 + 0.016 = 1.000.
BAR_PATH = ((-0.400, -0.190, 0.190), (-0.440, -0.190, 0.850),
            (-0.460, -0.150, 0.980))
CROSS_CX, CROSS_CZ, CROSS_W = -0.460, 0.984, 0.300


def _circle(cx, cz, radius, segments):
    return [(cx + radius * math.cos(math.tau * i / segments),
             cz + radius * math.sin(math.tau * i / segments))
            for i in range(segments)]


def _extent(me, poly):
    xs = [me.vertices[i].co.x for i in poly.vertices]
    zs = [me.vertices[i].co.z for i in poly.vertices]
    return (min(xs), max(xs), min(zs))


def build(b):
    rubber, metal, basket, body, canopy = MATERIALS
    parts = []

    # 12 segments, not 10: a 10-gon starting at angle 0 has no vertex at the
    # bottom, so the wheels float 4.4 mm above z = 0 and the whole AABB height
    # comes out at 0.995 instead of the layout's 1.000.  12 segments give
    # vertices at 0/90/180/270, i.e. exact x and z extremes.
    for sx in (-1.0, 1.0):
        cx = sx * WHEEL_X
        for sy in (-1.0, 1.0):
            parts.append(b.add(
                prism(_circle(cx, WHEEL_Z, WHEEL_R, 12), WHEEL_W, axis='y',
                      start=sy * WHEEL_Y - WHEEL_W * 0.5),
                'rubber', rubber['linear_rgb'], rubber['roughness'],
                smooth=False, name='wheel%d%d' % (sx > 0, sy > 0)))
        parts.append(b.add(
            prism(_circle(cx, AXLE_Z, AXLE_R, 6), 2 * AXLE_HALF, axis='y',
                  start=-AXLE_HALF),
            'metal', metal['linear_rgb'], metal['roughness'], smooth=True,
            name='axle%d' % (sx > 0)))

    for sy in (-1.0, 1.0):
        parts.append(b.add(
            tube([(-RAIL_X, sy * RAIL_Y, RAIL_Z), (RAIL_X, sy * RAIL_Y, RAIL_Z)],
                 [RAIL_R, RAIL_R], segments=6),
            'metal', metal['linear_rgb'], metal['roughness'], smooth=True,
            name='rail%d' % (sy > 0)))
        for sx in (-1.0, 1.0):
            # Fork from the rail end down to the wheel hub; without it the rail
            # floats 100 mm above the axle and the whole chassis is a second
            # shell.
            parts.append(b.add(
                tube([(sx * RAIL_X, sy * RAIL_Y, RAIL_Z),
                      (sx * RAIL_X, sy * WHEEL_Y, WHEEL_Z)],
                     [FORK_R, FORK_R], segments=5),
                'metal', metal['linear_rgb'], metal['roughness'], smooth=True,
                name='fork%d%d' % (sx > 0, sy > 0)))

    # Under-basket is a solid dark block, not an open tray: the bassinet sits
    # directly on it (the tub is wider in plan, so a tray opening would be
    # closed from above and its inner surface would become an enclosed void -
    # that is exactly what exported as a second shell in the first two passes).
    tray = b.add(prism(rounded_rect(BASKET_W, BASKET_D, 0.030,
                                    corner_segments=1),
                       BASKET_Z1 - BASKET_Z0, start=BASKET_Z0),
                 'basket', basket['linear_rgb'], basket['roughness'],
                 smooth=False, name='basket')

    tub = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.100, corner_segments=2),
                      BODY_Z1 - BODY_Z0, start=BODY_Z0),
                'body', body['linear_rgb'], body['roughness'], smooth=True,
                name='tub')
    tub_cut = b.add(prism(rounded_rect(BODY_W - 2 * BODY_WALL,
                                       BODY_D - 2 * BODY_WALL, 0.080,
                                       corner_segments=2),
                          BODY_Z1 - BODY_Z0 - BODY_FLOOR + 0.012,
                          start=BODY_Z0 + BODY_FLOOR),
                    'body', body['linear_rgb'], body['roughness'], smooth=True,
                    name='tubcut')
    b.boolean_difference(tub, [tub_cut])

    arc = []
    for i in range(CANOPY_SEGS + 1):
        a = math.pi - math.pi * i / CANOPY_SEGS
        arc.append((CANOPY_CX + CANOPY_R * math.cos(a),
                    CANOPY_CZ + CANOPY_R * math.sin(a)))
    hood = b.add(prism(arc, CANOPY_W, axis='y', start=-CANOPY_W * 0.5),
                 'canopy', canopy['linear_rgb'], canopy['roughness'],
                 smooth=True, name='canopy')

    bars = []
    for side in (-1.0, 1.0):
        # BAR_PATH is the -y upright; the +y one is its mirror.  Mirroring it
        # the other way round (y * -side for side < 0) is the identity, so the
        # first pass built the same upright twice and the bar had one end in
        # mid air.
        path = [(x, y, z) if side < 0 else (x, -y, z) for (x, y, z) in BAR_PATH]
        bars.append(b.add(tube(path, [BAR_R] * len(path), segments=5),
                          'metal', metal['linear_rgb'], metal['roughness'],
                          smooth=True, name='pushbar%d' % (side > 0)))
    bars.append(b.add(
        prism(_circle(CROSS_CX, CROSS_CZ, BAR_R, 8), CROSS_W, axis='y',
              start=-CROSS_W * 0.5),
        'metal', metal['linear_rgb'], metal['roughness'], smooth=False,
        name='crossbar'))

    b.boolean_union(tray, [tub, hood] + bars)
    b.boolean_union(tray, parts)

    me = tray.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        _x0, x1, z0 = _extent(me, poly)
        c = poly.center
        near_wheel = min((c.x - sx * WHEEL_X) ** 2 + (c.z - WHEEL_Z) ** 2
                         for sx in (-1.0, 1.0)) <= 0.098 ** 2
        if abs(c.y) > 0.230 and near_wheel:
            poly.material_index = 0
        elif z0 < BASKET_Z0 or (x1 < -0.305 and z0 > 0.430):
            poly.material_index = 1
        elif z0 < BODY_Z0 and z0 >= BASKET_Z0:
            poly.material_index = 2
        elif z0 >= BODY_Z1 + 0.0005:
            poly.material_index = 4
        else:
            poly.material_index = 3
    return b.finish(TARGET_LONGEST_M)
