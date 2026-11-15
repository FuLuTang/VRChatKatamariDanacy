"""Stapler - 160 x 45 x 60 mm plastic base and top cover with a front hinge.

The base is a 14 mm plate; the cover is an 18 mm slab whose underside is at
42 mm, leaving a 28 mm mouth that is open at the front and on both sides.  A
rear riser welds cover to base, and the front hinge barrel sits on the front
top edge exactly above the mouth, so the opening stays connected while the
front-of-cover hinge the contract asks for is present.  The base also carries
a small metal anvil plate at the front.

Material pass (2026-11-15 rework): the old body was 0.085 near-black plastic
with a bright hinge - two slots, and the dark body swallowed the whole shape.
Now the body is the grey plastic key (0.230/0.235/0.240), the hinge, anvil and a
new staple-channel rail on the base top are bright metal, and a 2.5 mm rubber
sole runs under the base in the rubber key: three slots plus the dark sole, with
the mouth and the front hinge still reading clearly.  The sole is inset 4 mm on
all sides and the base plate starts on top of it, so the 60 mm overall height and
the 160 x 45 mm footprint are unchanged; the rail adds 12 triangles.
"""

from prop_lib import lathe, prism, rounded_rect

ID = 'stapler'
NAME_ZH = '订书机'
CATEGORY = 'stationery'
SOURCING = 'model ourselves：160 x 45 x 60 mm 塑料订书机'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.160
TRI_BUDGET = 260
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.160', 'game_longest_dim_m': '0.160'}
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '机身灰塑料', 'linear_rgb': [0.230, 0.235, 0.240],
     'roughness': 0.50},
    {'key': 'metal', 'name_zh': '铰链/砧板/钉槽金属', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'rubber', 'name_zh': '底部橡胶垫', 'linear_rgb': [0.045, 0.045, 0.048],
     'roughness': 0.88},
]
NOTES = ('Base 160 x 45 x 14 mm (R 4 mm) standing on a full-footprint 160 x 45 x 2.5 mm '
         'rubber sole, so the base plate runs z 2.5-14 mm and the sole z 0-2.5 mm '
         '(the dark 2.5 mm band shows all round the bottom edge); '
         'cover 140 x 42 x 18 mm at z 42-60 mm, rear riser 16 x 40 x 36 mm at '
         'x -78..-62 mm, front hinge barrel r 8 mm x 40 mm at x 62 mm, z 48 mm, '
         'a 30 x 30 x 3 mm metal anvil on the base front and a 95 x 24 x 1.5 mm '
         'staple-channel rail on the base top.  Mouth z 14-42 mm is open at +X '
         'and both sides.')

BASE_L, BASE_W = 0.160, 0.045
SOLE_H = 0.0025
BASE_TOP = 0.014
COVER_L, COVER_W, COVER_Z0, COVER_Z1 = 0.140, 0.042, 0.042, 0.060
RISER_X0, RISER_X1, RISER_W = -0.078, -0.062, 0.040
RISER_Z0, RISER_Z1 = 0.012, 0.048
HINGE_R, HINGE_L = 0.008, 0.040
HINGE_X, HINGE_Z = 0.062, 0.048
ANVIL_X0, ANVIL_X1, ANVIL_Z1 = 0.030, 0.060, 0.017
RAIL_X0, RAIL_X1, RAIL_Z1 = -0.040, 0.055, 0.0155
RAIL_HALF_W = 0.012
GRIP_X0, GRIP_X1 = -0.050, 0.035
GRIP_HALF_W = 0.014
GRIP_DEPTH = 0.0008

GREY = (0.230, 0.235, 0.240)
METAL = (0.430, 0.450, 0.470)
RUBBER = (0.045, 0.045, 0.048)


def build(b):
    sole = b.add(prism(rounded_rect(BASE_L, BASE_W, 0.004,
                                    corner_segments=1), SOLE_H),
                 'rubber', RUBBER, 0.88,
                 smooth=False, name='sole')
    base = b.add(prism(rounded_rect(BASE_L, BASE_W, 0.004, corner_segments=1),
                       BASE_TOP - SOLE_H, start=SOLE_H),
                 'plastic_grey', GREY, 0.50,
                 smooth=False, name='base')
    riser = b.add(prism([(RISER_X0, -RISER_W * 0.5), (RISER_X1, -RISER_W * 0.5),
                         (RISER_X1, RISER_W * 0.5), (RISER_X0, RISER_W * 0.5)],
                        RISER_Z1 - RISER_Z0, start=RISER_Z0),
                  'plastic_grey', GREY, 0.50,
                  smooth=False, name='riser')
    cover = b.add(prism(rounded_rect(COVER_L, COVER_W, 0.004, corner_segments=1),
                        COVER_Z1 - COVER_Z0, start=COVER_Z0),
                  'plastic_grey', GREY, 0.50,
                  smooth=False, name='cover')
    hinge_bm = lathe([(HINGE_R, 0.0), (HINGE_R, HINGE_L)], segments=8)
    for vert in hinge_bm.verts:
        x, y, z = vert.co
        vert.co = (HINGE_X + x, z - HINGE_L * 0.5, HINGE_Z + y)
    hinge = b.add(hinge_bm, 'metal', METAL, 0.32,
                  smooth=True, name='hinge')
    anvil = b.add(prism([(ANVIL_X0, -0.015), (ANVIL_X1, -0.015),
                         (ANVIL_X1, 0.015), (ANVIL_X0, 0.015)],
                        ANVIL_Z1 - BASE_TOP, start=BASE_TOP),
                  'metal', METAL, 0.32,
                  smooth=False, name='anvil')
    rail = b.add(prism([(RAIL_X0, -RAIL_HALF_W), (RAIL_X1, -RAIL_HALF_W),
                        (RAIL_X1, RAIL_HALF_W), (RAIL_X0, RAIL_HALF_W)],
                       RAIL_Z1 - BASE_TOP, start=BASE_TOP),
                 'metal', METAL, 0.32,
                 smooth=False, name='rail')
    b.boolean_union(sole, [base, riser, cover, hinge, anvil, rail])

    # Rubberised grip pad recessed 0.8 mm into the cover top: the sole alone is
    # hidden, and a stapler's top pad is what actually reads from above.
    grip = b.add(prism([(GRIP_X0, -GRIP_HALF_W), (GRIP_X1, -GRIP_HALF_W),
                        (GRIP_X1, GRIP_HALF_W), (GRIP_X0, GRIP_HALF_W)],
                       GRIP_DEPTH + 0.001, axis='z',
                       start=COVER_Z1 - GRIP_DEPTH),
                 'rubber', RUBBER, 0.88, smooth=False, name='grip_cut')
    b.boolean_difference(sole, [grip])
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
        hinge_dist = ((c.x - HINGE_X) ** 2 + (z - HINGE_Z) ** 2) ** 0.5
        on_hinge = hinge_dist < 0.0095
        on_anvil = poly.normal.z > 0.5 and z < 0.018 and c.x > 0.026
        on_rail = (BASE_TOP - 0.0002 < z < RAIL_Z1 + 0.0002
                   and abs(c.y) < RAIL_HALF_W + 0.0005
                   and RAIL_X0 - 0.001 < c.x < RAIL_X1 + 0.001)
        on_grip = (z > COVER_Z1 - GRIP_DEPTH - 0.0006
                   and GRIP_X0 - 0.0005 < c.x < GRIP_X1
                   and abs(c.y) < GRIP_HALF_W + 0.0005)
        if z < SOLE_H + 0.0003 or on_grip:
            poly.material_index = 2
        elif on_hinge or on_anvil or on_rail:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return info
