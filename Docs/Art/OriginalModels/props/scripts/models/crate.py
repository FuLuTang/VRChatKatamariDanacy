"""Wooden crate - 60 x 40 x 40 cm lidded storage crate, slatted sides, flat lid.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_crate_01/02 as crate,
0.60 x 0.40 x 0.40 m, D_req 1.224: the bed-south "0.40 top, 0.14 step up from
the flower bed" and the tree-corner blocker.  LEVEL_CONSTRUCTION.md 4 P0 carries
the same piece at 0.60 x 0.40 x 0.40 with a 600 tris budget.  prop_catalog.csv
has no row, so the module declares DOC_ROW.

The layout stacks two of these ("可叠"), so the lid has to be one flat plane and
nothing may stand on it: the lid top and the four corner-post tops are coplanar
at z = 0.400, and the posts are the only thing that breaks the lid outline.
That coplanar top is also the 0.40 m support height the layout's step rule uses.

Read: an inset 0.560 x 0.360 x 0.348 body under a 0.592 x 0.392 x 0.032 lid that
overhangs it by 16 mm, four 40 mm corner posts running the full height to the
lid plane, and two horizontal battens per long side (40 mm tall, 16 mm proud of
the body).  Posts and battens are what make a box read as a crate without any
plank gaps, and gaps are forbidden here: the body has to stay one closed shell
that a 0.30 m ball cannot slip through.
"""

from prop_lib import prism, rounded_rect

ID = 'crate'
NAME_ZH = '木箱收纳箱'
CATEGORY = 'container'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / LEVEL_CONSTRUCTION.md 4 P0（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.60
TRI_BUDGET = 600
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.60', 'game_longest_dim_m': '0.60'}
MATERIALS = [
    {'key': 'wood', 'name_zh': '箱体浅木', 'linear_rgb': [0.330, 0.215, 0.110],
     'roughness': 0.78},
    {'key': 'trim', 'name_zh': '盖板角柱深木', 'linear_rgb': [0.185, 0.108, 0.050],
     'roughness': 0.74},
]
NOTES = ('Body 0.560 x 0.360 x 0.348 (R 10 mm) from z 0.020; four 0.040 square '
         'corner posts at (+/-0.280, +/-0.180) from z 0 to 0.400; lid '
         '0.592 x 0.392 x 0.032 (R 12 mm) from z 0.368 to 0.400.  Two 0.480 x '
         '0.016 x 0.040 battens per long side, outer face at y = +/-0.196, at '
         'z 0.090 and 0.250, plus one 0.300 x 0.040 batten per short end at '
         'z 0.170 (outer face x = +/-0.288, 8 mm proud of the body).  AABB 0.600 x 0.400 x 0.400; the '
         'lid plane and the '
         'four post tops are coplanar at z = 0.400.  Two materials by face '
         'extent: trim (the lid slab, everything above z 0.3674) and every face '
         'outside the body outline (|y| > 0.1815 or |x| > 0.2815), wood '
         'elsewhere.')

BODY_W, BODY_D, BODY_Z0, BODY_Z1 = 0.560, 0.360, 0.020, 0.368
POST_X, POST_Y, POST_S = 0.280, 0.180, 0.040
LID_W, LID_D, LID_Z1 = 0.592, 0.392, 0.400
LID_T = LID_Z1 - BODY_Z1
BATTEN_L, BATTEN_T, BATTEN_H = 0.480, 0.016, 0.040
BATTEN_Y, BATTEN_Z = 0.188, (0.090, 0.250)
END_BATTEN_L, END_BATTEN_Z = 0.300, 0.170


def _box(b, x0, x1, y0, y1, z0, z1, mat, name):
    profile = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    return b.add(prism(profile, z1 - z0, axis='z', start=z0),
                 mat['key'], mat['linear_rgb'], mat['roughness'],
                 smooth=False, name=name)


def _extent(me, poly):
    """(min_z, max_abs_x, max_abs_y) of one face's corners."""
    zs = [me.vertices[i].co.z for i in poly.vertices]
    ax = max(abs(me.vertices[i].co.x) for i in poly.vertices)
    ay = max(abs(me.vertices[i].co.y) for i in poly.vertices)
    return min(zs), ax, ay


def build(b):
    wood, trim = MATERIALS
    body = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.010,
                                    corner_segments=1),
                       BODY_Z1 - BODY_Z0, start=BODY_Z0),
                 'wood', wood['linear_rgb'], wood['roughness'], smooth=False,
                 name='body')
    lid_ob = b.add(prism(rounded_rect(LID_W, LID_D, 0.012,
                                      corner_segments=1), LID_T,
                         start=BODY_Z1),
                   'trim', trim['linear_rgb'], trim['roughness'], smooth=False,
                   name='lid')
    posts = []
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            cx, cy = sx * POST_X, sy * POST_Y
            posts.append(_box(b, cx - POST_S * 0.5, cx + POST_S * 0.5,
                              cy - POST_S * 0.5, cy + POST_S * 0.5,
                              0.0, LID_Z1, trim,
                              'post%d%d' % (sx > 0, sy > 0)))
    battens = []
    for i, z in enumerate(BATTEN_Z):
        for sy in (-1.0, 1.0):
            cy = sy * BATTEN_Y
            battens.append(_box(b, -BATTEN_L * 0.5, BATTEN_L * 0.5,
                                cy - BATTEN_T * 0.5, cy + BATTEN_T * 0.5,
                                z, z + BATTEN_H, trim,
                                'batten%d%d' % (i, sy > 0)))
    # One horizontal batten per short end, otherwise the ends read as a plain
    # solid box from the two angles the preview does not show.
    for sx in (-1.0, 1.0):
        cx = sx * BODY_W * 0.5
        battens.append(_box(b, cx - BATTEN_T * 0.5, cx + BATTEN_T * 0.5,
                            -END_BATTEN_L * 0.5, END_BATTEN_L * 0.5,
                            END_BATTEN_Z, END_BATTEN_Z + BATTEN_H, trim,
                            'endbatten%d' % (sx > 0)))
    b.boolean_union(body, [lid_ob] + posts + battens)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        z_min, ax, ay = _extent(me, poly)
        trim_face = (z_min >= BODY_Z1 - 0.0006
                     or ax > POST_X + POST_S * 0.5 - 0.0185
                     or ay > BODY_D * 0.5 + 0.0015)
        poly.material_index = 1 if trim_face else 0
    return b.finish(TARGET_LONGEST_M)
