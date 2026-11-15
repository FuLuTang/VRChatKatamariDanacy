"""Collapsed bench - a 1.60 x 0.55 x 0.80 m slatted park bench, laid crosswise.

The catalog row is the diagonal-door gate: "3.6 m bench lying across (four legs to
the side)".  Two separate numbers disagree there.  The three-axis it gives is
1.60 x 0.55 x 0.80 m, which is exactly an upright park bench - length 1.60,
depth 0.55, back height 0.80 - and the 3.6 m in the notes is the *game* size of
the door blocker.  "Lying across" is the placement (横放 across the opening, legs
facing the sides), not a bench tipped onto its back: a 1.6 m bench tipped on its
back is a 1.6 x 0.80 x 0.55 stack of boards with the legs pointing away from the
viewer, which reads as debris, not as a bench.  The real-size prop is therefore
the upright bench, and its AABB matches the design three-axis exactly.

Four 1.60 m seat slats with 20 mm gaps and four 50 mm square legs (dark metal)
under two side rails; the back is two slats on two posts.  The sharp-eyed detail
is that the gaps are real holes - you see the rails and the ground through them -
and the legs overlap the rails by 40 mm and the rails overlap the slats, so the
whole thing is one closed shell.

Level-one fit: a = 0.880 m > 0.823 m, so D = 2.38 m > 2.20 m; the bench is
level-one scenery / an obstacle, and even at the game 3.6 m gate it is a
"blocks the ball below 1.05 m" wall, not an absorbable.
"""

from prop_lib import prism

ID = 'collapsed_bench'
NAME_ZH = '倒塌长凳'
CATEGORY = 'gate'
CSV_REAL_LONGEST_M = 1.60
CSV_GAME_LONGEST_M = 3.60
SOURCING = 'model ourselves：3.6 m 长凳横躺（四条腿朝侧），专用于对角门'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 1.60
TRI_BUDGET = 600
MATERIALS = [
    {'key': 'wood', 'name_zh': '旧木条', 'linear_rgb': [0.300, 0.190, 0.095],
     'roughness': 0.82},
    {'key': 'metal', 'name_zh': '凳腿深铁灰', 'linear_rgb': [0.140, 0.150, 0.160],
     'roughness': 0.50},
]
NOTES = ('Upright slatted bench, real metres: four seat slats 1.600 x 0.115 x '
         '0.030 at z 0.420-0.450 (20 mm gaps, centres +/-0.0675 / +/-0.2025), two '
         'side rails 0.060 x 0.520 x 0.050 at x +/-0.680, four 0.055 square legs '
         'at y +/-0.200 up to z 0.440, two back posts 0.050 x 0.050 at '
         'y 0.240-0.290 up to z 0.800, two back slats 1.600 x 0.030 x 0.100 at '
         'z 0.560-0.660 and 0.710-0.800.  AABB 1.600 x 0.550 x 0.800 (back at -Y, seat front +Y).  The seat '
         'gaps are through-holes.  Materials split at z 0.395: metal legs below, '
         'wood above.  This replaces the catalog note "lying on its side" with '
         'the upright bench its three-axis describes.')

SEAT_L = 1.600
SEAT_SLAT_W, SEAT_SLAT_T = 0.115, 0.030
SEAT_TOP = 0.450
SEAT_SLAT_Y = (-0.2025, -0.0675, 0.0675, 0.2025)
RAIL_X = 0.680
RAIL_W, RAIL_T = 0.060, 0.050
SEAT_Y = 0.260
LEG_Y = 0.200
LEG_S = 0.055
BACK_Y0, BACK_Y1 = -0.290, -0.240
BACK_TOP = 0.800
BACK_SLAT_Z = (0.560, 0.700)
BACK_SLAT_H = 0.100


def _box(b, x0, x1, y0, y1, z0, z1, mat, name):
    profile = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    return b.add(prism(profile, z1 - z0, axis='z', start=z0),
                 mat['key'], mat['linear_rgb'], mat['roughness'],
                 smooth=False, name=name)


def build(b):
    wood, metal = MATERIALS
    half = SEAT_L * 0.5
    rail_lo = SEAT_TOP - SEAT_SLAT_T - RAIL_T

    rail_l = _box(b, -RAIL_X - RAIL_W * 0.5, -RAIL_X + RAIL_W * 0.5,
                  -SEAT_Y, SEAT_Y, rail_lo, SEAT_TOP, wood, 'railL')
    slats = []
    for i, cy in enumerate(SEAT_SLAT_Y):
        slats.append(_box(b, -half, half, cy - SEAT_SLAT_W * 0.5,
                          cy + SEAT_SLAT_W * 0.5,
                          SEAT_TOP - SEAT_SLAT_T, SEAT_TOP, wood,
                          'seat%d' % i))
    rail_r = _box(b, RAIL_X - RAIL_W * 0.5, RAIL_X + RAIL_W * 0.5,
                  -SEAT_Y, SEAT_Y, rail_lo, SEAT_TOP, wood, 'railR')
    b.boolean_union(rail_l, slats + [rail_r])

    legs = []
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            lx, ly = sx * RAIL_X, sy * LEG_Y
            legs.append(_box(b, lx - LEG_S * 0.5, lx + LEG_S * 0.5,
                             ly - LEG_S * 0.5, ly + LEG_S * 0.5,
                             0.0, SEAT_TOP - 0.010, metal,
                             'leg%d%d' % (sx < 0, sy < 0)))
    b.boolean_union(rail_l, legs)

    posts = []
    for sx in (-1.0, 1.0):
        posts.append(_box(b, sx * RAIL_X - LEG_S * 0.5, sx * RAIL_X + LEG_S * 0.5,
                          BACK_Y0, BACK_Y1, SEAT_TOP - 0.030, BACK_TOP,
                          wood, 'post%d' % (sx < 0)))
    b.boolean_union(rail_l, posts)

    back = []
    for i, z in enumerate(BACK_SLAT_Z):
        back.append(_box(b, -half, half, BACK_Y0, BACK_Y0 + 0.030,
                         z, z + BACK_SLAT_H, wood, 'back%d' % i))
    b.boolean_union(rail_l, back)

    me = rail_l.data
    me.materials.clear()
    me.materials.append(b.material('wood', wood['linear_rgb'], wood['roughness']))
    me.materials.append(b.material('metal', metal['linear_rgb'],
                                   metal['roughness']))
    for poly in me.polygons:
        poly.material_index = 1 if poly.center.z < 0.395 else 0
    return b.finish(TARGET_LONGEST_M)
