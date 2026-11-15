"""Glasses case - 170 x 70 x 45 mm rounded fabric case with zipper band and pull.

A single 170 x 68 x 45 mm 8-gon rounded-rect body carries a 4 mm tall metal
band that is 1 mm proud on both long sides; the band sets the 70 mm width and
the body sets the 170 mm length, so the AABB is exact.  The band and the small
pull tab are separate solids unioned into the body, which keeps the case a single
closed shell.

Material pass (2026-11-15 rework): the old body was a very dark green
(0.055/0.135/0.12) with a mid metal band - at preview size it read as a grey
slab, and the two slots were only 0.29/0.42 apart.  The body is now the denim
blue fabric key (0.120/0.170/0.290), the zipper band the dark oxidised metal key
and the pull tab bright aluminium, so the band is the required low-value slot and
the body/band/pull read as three separate materials.  No geometry change: 138
triangles.
"""

from prop_lib import prism, rounded_rect

ID = 'glasses_case'
NAME_ZH = '眼镜盒'
CATEGORY = 'container'
SOURCING = 'model ourselves：170 x 70 x 45 mm 拉链眼镜盒'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.170
TRI_BUDGET = 200
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.170', 'game_longest_dim_m': '0.170'}
MATERIALS = [
    {'key': 'fabric_blue', 'name_zh': '布面牛仔蓝', 'linear_rgb': [0.120, 0.170, 0.290],
     'roughness': 0.92},
    {'key': 'metal_dark', 'name_zh': '拉链齿暗金属', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.45},
    {'key': 'metal', 'name_zh': '拉链拉头亮金属', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
]
NOTES = ('Body 170 x 68 x 45 mm (R 10 mm); zipper band 170 x 70 x 4 mm at z '
         '19-23 mm, proud 1 mm on each long side; 8 x 2 x 14 mm metal pull '
         'tab on the +Y band reaches y = 35 mm.  AABB 170 x 70 x 45 mm.  A face '
         'is metal_dark only if its full z extent is inside the band; the pull '
         'tab alone takes the bright metal slot.')

CASE_L, CASE_W, CASE_H = 0.170, 0.068, 0.045
BAND_W = 0.070
BAND_Z0, BAND_Z1 = 0.019, 0.023
PULL_X0, PULL_X1 = 0.030, 0.038
PULL_Z0, PULL_Z1 = 0.010, 0.024
PULL_Y0, PULL_Y1 = 0.033, 0.035

FABRIC = (0.120, 0.170, 0.290)
BAND = (0.090, 0.095, 0.105)
PULL = (0.430, 0.450, 0.470)


def _z_extent(me, poly):
    return (min(me.vertices[i].co.z for i in poly.vertices),
            max(me.vertices[i].co.z for i in poly.vertices))


def build(b):
    body = b.add(prism(rounded_rect(CASE_L, CASE_W, 0.010, corner_segments=1),
                       CASE_H),
                 'fabric_blue', FABRIC, 0.92,
                 smooth=False, name='body')
    band = b.add(prism(rounded_rect(CASE_L, BAND_W, 0.010, corner_segments=1),
                       BAND_Z1 - BAND_Z0, start=BAND_Z0),
                 'fabric_blue', FABRIC, 0.92,
                 smooth=False, name='band')
    pull = b.add(prism([(PULL_X0, PULL_Z0), (PULL_X1, PULL_Z0),
                        (PULL_X1, PULL_Z1), (PULL_X0, PULL_Z1)],
                       PULL_Y1 - PULL_Y0, axis='y', start=PULL_Y0),
                 'fabric_blue', FABRIC, 0.92,
                 smooth=False, name='pull')
    b.boolean_union(body, [band, pull])
    info = b.finish(TARGET_LONGEST_M)

    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    zmin = min(v.co.z for v in me.vertices)
    for poly in me.polygons:
        z0, z1 = _z_extent(me, poly)
        z0 -= zmin
        z1 -= zmin
        c = poly.center
        on_pull = c.x > PULL_X0 - 0.001 and c.x < PULL_X1 + 0.001 \
            and c.y > PULL_Y0 - 0.001 and z0 > PULL_Z0 - 0.001
        in_band = z0 > BAND_Z0 - 0.0005 and z1 < BAND_Z1 + 0.0005
        if on_pull:
            poly.material_index = 2
        elif in_band:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return info
