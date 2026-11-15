"""Large crate - 80 x 50 x 45 cm heavy wooden crate, flat lid, end battens.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_crate_large_01 as crate_large,
0.80 x 0.45 x 0.50 m (x x height x z), D_req 1.509: the tree-corner blocking
landmark, one of the four defs the layout added for the 1.10-1.60 gap (U3).
prop_catalog.csv has no row, so the module declares DOC_ROW.

It is deliberately the same family as `crate` (corner posts + a flat lid between
their tops + side battens) at 0.80 x 0.50 x 0.45, so the tree corner reads as
"two crates of different size" rather than two unrelated boxes.  What changes is
proportion: the posts are 55 mm instead of 40, the body sits 22.5 mm inside
them, and there is a batten on the short ends as well, so the silhouette is a
heavier frame around a recessed panel.

The lid is a single flat plane at z = 0.450 and the four post tops are coplanar
with it; nothing on the top rises above 0.45, which is the AABB height the
layout's blocking and step rules use.  No lid handle: a handle is what
`cooler_box_large` carries, and a raised one would break the flat top.
"""

from prop_lib import prism, rounded_rect

ID = 'crate_large'
NAME_ZH = '大收纳箱'
CATEGORY = 'container'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / U3（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.80
TRI_BUDGET = 700
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.80', 'game_longest_dim_m': '0.80'}
MATERIALS = [
    {'key': 'wood', 'name_zh': '箱体旧木', 'linear_rgb': [0.250, 0.155, 0.075],
     'roughness': 0.80},
    {'key': 'trim', 'name_zh': '框条深木', 'linear_rgb': [0.135, 0.082, 0.038],
     'roughness': 0.76},
]
NOTES = ('Body 0.700 x 0.400 (R 12 mm) from z 0.030 to 0.412; four 0.055 '
         'square posts at (+/-0.3725, +/-0.2225) from z 0 to 0.450; lid '
         '0.780 x 0.480 (R 14 mm) from z 0.412 to 0.450, recessed 10 mm from '
         'the post faces.  Two 0.660 x 0.020 x 0.040 battens per long side '
         '(y 0.195-0.215, z 0.115 and 0.300) and one 0.400 x 0.020 x 0.040 '
         'batten per short end (x 0.350-0.370, z 0.160).  AABB '
         '0.800 x 0.500 x 0.450; lid plane and post tops coplanar at z 0.450.  '
         'Materials by face extent: trim = lid slab above z 0.4114, anything '
         'outside the body outline (|x| > 0.3545 or |y| > 0.2045) and the end '
         'battens; wood elsewhere.')

BODY_W, BODY_D, BODY_Z0, BODY_Z1 = 0.700, 0.400, 0.030, 0.412
POST_X, POST_Y, POST_S = 0.3725, 0.2225, 0.055
LID_W, LID_D, LID_Z1 = 0.780, 0.480, 0.450
LID_Z0 = BODY_Z1
BATTEN_T, BATTEN_H = 0.020, 0.040
LONG_BATTEN_L, LONG_BATTEN_Y = 0.660, 0.205
LONG_BATTEN_Z = (0.115, 0.300)
END_BATTEN_L, END_BATTEN_X, END_BATTEN_Z = 0.400, 0.360, 0.160


def _box(b, x0, x1, y0, y1, z0, z1, mat, name):
    profile = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    return b.add(prism(profile, z1 - z0, axis='z', start=z0),
                 mat['key'], mat['linear_rgb'], mat['roughness'],
                 smooth=False, name=name)


def _extent(me, poly):
    xs = [me.vertices[i].co.x for i in poly.vertices]
    ys = [me.vertices[i].co.y for i in poly.vertices]
    zs = [me.vertices[i].co.z for i in poly.vertices]
    return (max(abs(v) for v in xs), max(abs(v) for v in ys),
            min(zs), max(zs))


def build(b):
    wood, trim = MATERIALS
    body = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.012,
                                    corner_segments=1),
                       BODY_Z1 - BODY_Z0, start=BODY_Z0),
                 'wood', wood['linear_rgb'], wood['roughness'], smooth=False,
                 name='body')
    lid = b.add(prism(rounded_rect(LID_W, LID_D, 0.014, corner_segments=1),
                      LID_Z1 - LID_Z0, start=LID_Z0),
                'trim', trim['linear_rgb'], trim['roughness'], smooth=False,
                name='lid')
    parts = [lid]
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            cx, cy = sx * POST_X, sy * POST_Y
            parts.append(_box(b, cx - POST_S * 0.5, cx + POST_S * 0.5,
                              cy - POST_S * 0.5, cy + POST_S * 0.5,
                              0.0, LID_Z1, trim,
                              'post%d%d' % (sx > 0, sy > 0)))
    for i, z in enumerate(LONG_BATTEN_Z):
        for sy in (-1.0, 1.0):
            cy = sy * LONG_BATTEN_Y
            parts.append(_box(b, -LONG_BATTEN_L * 0.5, LONG_BATTEN_L * 0.5,
                              cy - BATTEN_T * 0.5, cy + BATTEN_T * 0.5,
                              z, z + BATTEN_H, trim,
                              'batten%d%d' % (i, sy > 0)))
    for sx in (-1.0, 1.0):
        cx = sx * END_BATTEN_X
        parts.append(_box(b, cx - BATTEN_T * 0.5, cx + BATTEN_T * 0.5,
                          -END_BATTEN_L * 0.5, END_BATTEN_L * 0.5,
                          END_BATTEN_Z, END_BATTEN_Z + BATTEN_H, trim,
                          'endbatten%d' % (sx > 0)))
    b.boolean_union(body, parts)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        ax, ay, z0, _z1 = _extent(me, poly)
        trim_face = (z0 >= LID_Z0 - 0.0006
                     or ax > BODY_W * 0.5 + 0.0045
                     or ay > BODY_D * 0.5 + 0.0045)
        poly.material_index = 1 if trim_face else 0
    return b.finish(TARGET_LONGEST_M)
