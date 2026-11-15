"""Small toy car - 150 x 80 x 60 mm painted body with four welded wheels.

A rounded chassis (150 x 76 x 22 mm, z 20-42 mm) and a narrower cabin
(72 x 70 x 20 mm, z 40-60 mm) give the blocky car silhouette; four 20 mm
radius, 12 mm wide wheels sit at the corners with their tops buried in the
chassis, so the Exact union is one shell with no floating parts.  The wheel
faces keep the rubber slot by distance to the four axle centres, and the cabin
sides above the beltline become the dark window band.  No badge, grille or
brand shape: the contract asks for a neutral toy car.

Material pass (2026-11-15 rework): the body was a mid red 0.52, the window band
a 0.09 navy and the tyres black - three slots but no metal part and the paint
was close to the spec's bright end.  Now the paint is the red plastic key, the
window band the dark metal key (a tinted dark glass), the tyres the rubber key,
and two chrome bumper bars were added at the lower front and rear: 24 extra
triangles, everything inside the existing 150 x 80 x 60 mm envelope.
"""

from prop_lib import lathe, prism, rounded_rect

ID = 'toy_car_small'
NAME_ZH = '小玩具车'
CATEGORY = 'toy'
SOURCING = 'model ourselves：150 x 80 x 60 mm 无品牌玩具车'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.150
TRI_BUDGET = 300
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.150', 'game_longest_dim_m': '0.150'}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '车漆红', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.40},
    {'key': 'metal_dark', 'name_zh': '深色车窗', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.35},
    {'key': 'rubber', 'name_zh': '轮胎黑胶', 'linear_rgb': [0.045, 0.045, 0.048],
     'roughness': 0.88},
    {'key': 'metal', 'name_zh': '镀铬保险杠', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
]
NOTES = ('Chassis 150 x 76 x 22 mm (R 12 mm) at z 20-42 mm; cabin 72 x 70 x 20 '
         'mm (R 10 mm) at z 40-60 mm; four 8-gon wheels r 20 mm, 12 mm wide at '
         'x +/-48 mm, y +/-34 mm, z 20 mm; two 11 x 68 x 14 mm chrome bumper bars '
         'at x +/-75 mm, z 10-24 mm.  AABB 150 x 80 x 60 mm.  Rubber by distance '
         '< 21 mm to an axle centre and |y| > 26 mm; metal on the bumper volumes '
         '(|x| > 63.5 mm, z < 24.5 mm); window band on the cabin sides above '
         'z 44.5 mm.')

CHASSIS_L, CHASSIS_W, CHASSIS_Z0, CHASSIS_Z1 = 0.150, 0.076, 0.020, 0.042
CABIN_L, CABIN_W, CABIN_Z0, CABIN_Z1 = 0.072, 0.070, 0.040, 0.060
WHEEL_R, WHEEL_W = 0.020, 0.012
WHEEL_X, WHEEL_Y, WHEEL_Z = 0.048, 0.034, 0.020
BUMPER_X0, BUMPER_X1 = 0.064, 0.075
BUMPER_Z0, BUMPER_Z1 = 0.010, 0.024
BUMPER_HALF_W = 0.034

RED = (0.480, 0.075, 0.060)
GLASS = (0.090, 0.095, 0.105)
RUBBER = (0.045, 0.045, 0.048)
CHROME = (0.430, 0.450, 0.470)


def build(b):
    body = b.add(prism(rounded_rect(CHASSIS_L, CHASSIS_W, 0.012,
                                    corner_segments=1),
                       CHASSIS_Z1 - CHASSIS_Z0, start=CHASSIS_Z0),
                 'plastic_red', RED, 0.40,
                 smooth=False, name='chassis')
    cabin = b.add(prism(rounded_rect(CABIN_L, CABIN_W, 0.010,
                                     corner_segments=1),
                        CABIN_Z1 - CABIN_Z0, start=CABIN_Z0),
                  'plastic_red', RED, 0.40,
                  smooth=False, name='cabin')
    parts = [cabin]
    for ix, sx in enumerate((-1.0, 1.0)):
        for iy, sy in enumerate((-1.0, 1.0)):
            bm = lathe([(WHEEL_R, 0.0), (WHEEL_R, WHEEL_W)], segments=8)
            for vert in bm.verts:
                x, y, z = vert.co
                vert.co = (sx * WHEEL_X + x,
                           sy * WHEEL_Y + (z - WHEEL_W * 0.5),
                           WHEEL_Z + y)
            parts.append(b.add(bm, 'rubber', RUBBER, 0.88, smooth=True,
                               name='wheel%d%d' % (ix, iy)))
    for i, sx in enumerate((-1.0, 1.0)):
        bx0, bx1 = sx * BUMPER_X0, sx * BUMPER_X1
        parts.append(b.add(
            prism([(bx0, BUMPER_Z0), (bx1, BUMPER_Z0),
                   (bx1, BUMPER_Z1), (bx0, BUMPER_Z1)],
                  BUMPER_HALF_W * 2.0, axis='y', start=-BUMPER_HALF_W),
            'metal', CHROME, 0.32, smooth=False, name='bumper%d' % i))
    b.boolean_union(body, parts)
    info = b.finish(TARGET_LONGEST_M)

    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    zmin = min(v.co.z for v in me.vertices)
    for poly in me.polygons:
        c = poly.center
        z = c.z - zmin
        near_wheel = min((c.x - sx * WHEEL_X) ** 2 + (z - WHEEL_Z) ** 2
                         for sx in (-1.0, 1.0)) < 0.021 ** 2
        if abs(c.x) > 0.0635 and z < 0.0245:
            poly.material_index = 3
        elif near_wheel and abs(c.y) > 0.026:
            poly.material_index = 2
        elif 0.0445 < z < 0.057 and (abs(c.x) > 0.030 or abs(c.y) > 0.030):
            poly.material_index = 1
        else:
            poly.material_index = 0
    return info
