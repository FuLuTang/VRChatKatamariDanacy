"""Picnic bench - 120 x 40 x 45 cm backless slatted bench, flat slat plane.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_bench_01 as picnic_bench,
1.20 x 0.45 x 0.40 m (x x height x z), D_req 1.604: the first big landmark next
to the spawn point, one of the four defs the layout added for the 1.10-1.60 gap
(U3).  prop_catalog.csv has no row, so the module declares DOC_ROW.

Backless on purpose.  The layout's height for the def is 0.45 m, which is a seat
height, and the bench is explicitly "never steppable" (4 x 0.45 > D_req), so a
backrest would be decoration outside the AABB the level budgets for.  What the
bench has to deliver is the silhouette: four 1.20 m slats with real 26 mm gaps,
a 45 mm seat plane, two end rails and four 50 mm legs with a low stretcher.

The gaps are through-holes, so the seat reads as slats from every angle and a
0.30 m ball looking at it sees a bench, not a solid box.  The seat is the only
thing at 0.45; legs, rails and stretcher stay below 0.415, which keeps the AABB
height exactly at the layout's 0.45.
"""

from prop_lib import prism, rounded_rect

ID = 'picnic_bench'
NAME_ZH = '野餐长凳'
CATEGORY = 'furniture'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / U3（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 1.20
TRI_BUDGET = 700
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '1.20', 'game_longest_dim_m': '1.20'}
MATERIALS = [
    {'key': 'wood', 'name_zh': '凳面旧木', 'linear_rgb': [0.230, 0.160, 0.100],
     'roughness': 0.84},
    {'key': 'metal', 'name_zh': '凳腿深铁灰', 'linear_rgb': [0.130, 0.140, 0.150],
     'roughness': 0.50},
]
NOTES = ('Three seat slats 1.200 x 0.116 x 0.035 (R 6 mm) with their tops at '
         'z 0.450, centres at y -0.142 / 0 / +0.142 (26 mm through-gaps, '
         'outer edges at y +/-0.200); two end rails 0.060 x 0.370 x 0.050 at '
         'x +/-0.520, z 0.365-0.415; four 0.050 square legs at '
         '(+/-0.520, +/-0.160); two stretchers 1.020 x 0.050 x 0.045 at '
         'y +/-0.160, z 0.140-0.185.  AABB 1.200 x 0.400 x 0.450, seat plane flat at '
         'z 0.450.  Materials by face extent: metal below z 0.210 (legs and '
         'stretcher), wood above.')

SEAT_L, SLAT_W, SLAT_T, SEAT_TOP = 1.200, 0.116, 0.035, 0.450
SLAT_Y = (-0.142, 0.0, 0.142)
RAIL_X, RAIL_L, RAIL_W, RAIL_H = 0.520, 0.370, 0.060, 0.050
LEG_X, LEG_Y, LEG_S = 0.520, 0.160, 0.050
STRETCH_L, STRETCH_W, STRETCH_H, STRETCH_Z = 1.020, 0.050, 0.045, 0.140


def _box(b, x0, x1, y0, y1, z0, z1, mat, name):
    return b.add(prism([(x0, y0), (x1, y0), (x1, y1), (x0, y1)],
                       z1 - z0, axis='z', start=z0),
                 mat['key'], mat['linear_rgb'], mat['roughness'],
                 smooth=False, name=name)


def _extent_z(me, poly):
    zs = [me.vertices[i].co.z for i in poly.vertices]
    return min(zs), max(zs)


def build(b):
    wood, metal = MATERIALS
    rail_z0 = SEAT_TOP - SLAT_T - RAIL_H
    # Rails stop 5 mm inside the slats so the union has real overlap instead of
    # a face-to-face contact at z = 0.415.
    rail_top = SEAT_TOP - SLAT_T + 0.005
    body = _box(b, -RAIL_X - RAIL_W * 0.5, -RAIL_X + RAIL_W * 0.5,
                -RAIL_L * 0.5, RAIL_L * 0.5, rail_z0, rail_top, wood, 'railL')
    slats = []
    for i, cy in enumerate(SLAT_Y):
        slats.append(b.add(
            prism([(x + 0.0, y + cy) for (x, y) in
                   rounded_rect(SEAT_L, SLAT_W, 0.006, corner_segments=1)],
                  SLAT_T, start=SEAT_TOP - SLAT_T),
            'wood', wood['linear_rgb'], wood['roughness'], smooth=False,
            name='slat%d' % i))
    rail_r = _box(b, RAIL_X - RAIL_W * 0.5, RAIL_X + RAIL_W * 0.5,
                  -RAIL_L * 0.5, RAIL_L * 0.5, rail_z0, rail_top, wood, 'railR')
    b.boolean_union(body, slats + [rail_r])

    legs = []
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            lx, ly = sx * LEG_X, sy * LEG_Y
            legs.append(_box(b, lx - LEG_S * 0.5, lx + LEG_S * 0.5,
                             ly - LEG_S * 0.5, ly + LEG_S * 0.5,
                             0.0, rail_z0 + 0.010, metal,
                             'leg%d%d' % (sx < 0, sy < 0)))
    # One stretcher per leg row: a single centre stretcher would have nothing
    # to weld to (the legs sit at y +/-0.160) and the bench would export as two
    # shells.
    for sy in (-1.0, 1.0):
        cy = sy * LEG_Y
        legs.append(_box(b, -STRETCH_L * 0.5, STRETCH_L * 0.5,
                         cy - STRETCH_W * 0.5, cy + STRETCH_W * 0.5,
                         STRETCH_Z, STRETCH_Z + STRETCH_H, metal,
                         'stretcher%d' % (sy > 0)))
    b.boolean_union(body, legs)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        z0, _z1 = _extent_z(me, poly)
        poly.material_index = 1 if z0 < 0.210 else 0
    return b.finish(TARGET_LONGEST_M)
