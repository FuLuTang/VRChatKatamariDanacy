"""Storage bin - 55 x 35 x 40 cm lidded plastic tote, ribbed body, flat lid.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_storage_01 as storage_bin,
0.55 x 0.40 x 0.35 m (x x height x z), D_req 1.137: the low cabinet that stands
between the folding table and the flower bed.  It is one of the four defs the
layout added to fill the 1.10-1.60 gap (the doc's U3), and prop_catalog.csv has
no row, so this module declares DOC_ROW.

The layout already has a wooden `crate` at 0.60 x 0.40 x 0.40 in the same
corner, so this one must not read as a second wooden box: it is the plastic
household tote - pale grey straight-walled body, two moulded ribs around the
body, and a blue lid with a 5 mm overhang and a 8 mm step at z = 0.342.  The
lid's top plane is z = 0.400 and the ribs and label stay below the lid edge, so
the 0.40 m top is one flat plane and the layout's 0.35-0.40 m blocking height is
exactly the AABB.

The label plate on the front is the only small detail: 6 mm proud of the body,
pale, 130 x 70 mm, at z 0.130-0.200.  Without it the bin is a featureless box at
preview scale; a 4 mm recess would be invisible (same colour, same normal), so
it is an applied plate rather than a cut.
"""

from prop_lib import prism, rounded_rect

ID = 'storage_bin'
NAME_ZH = '收纳箱小'
CATEGORY = 'container'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / U3（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.55
TRI_BUDGET = 600
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.55', 'game_longest_dim_m': '0.55'}
MATERIALS = [
    {'key': 'body', 'name_zh': '箱体浅灰', 'linear_rgb': [0.400, 0.395, 0.365],
     'roughness': 0.62},
    {'key': 'lid', 'name_zh': '箱盖深蓝', 'linear_rgb': [0.055, 0.090, 0.135],
     'roughness': 0.58},
    {'key': 'label', 'name_zh': '标签米色', 'linear_rgb': [0.600, 0.565, 0.460],
     'roughness': 0.85},
]
NOTES = ('Body 0.540 x 0.340 (R 22 mm, 12-gon) from z 0 to 0.350; two ribs '
         '0.546 x 0.346 (R 23 mm) at z 0.055-0.075 and 0.235-0.255; lid '
         '0.550 x 0.350 (R 28 mm) from z 0.342 to 0.400, 5 mm overhang and an '
         '8 mm step; front label plate 0.130 x 0.070 x 0.006 at y 0.169-0.175, '
         'z 0.130-0.200.  AABB 0.550 x 0.350 x 0.400, flat lid at z = 0.400.  '
         'Materials by face extent: label (beyond y 0.172 above z 0.128), lid '
         '(front face above z 0.3415), body elsewhere.')

BODY_W, BODY_D, BODY_Z1 = 0.540, 0.340, 0.350
RIB_W, RIB_D, RIB_T = 0.546, 0.346, 0.020
RIB_Z = (0.055, 0.235)
LID_W, LID_D, LID_Z0, LID_Z1 = 0.550, 0.350, 0.342, 0.400
LABEL_W, LABEL_Z0, LABEL_Z1, LABEL_T = 0.130, 0.130, 0.200, 0.006


def _extent(me, poly):
    xs = [me.vertices[i].co.x for i in poly.vertices]
    ys = [me.vertices[i].co.y for i in poly.vertices]
    zs = [me.vertices[i].co.z for i in poly.vertices]
    return (max(abs(v) for v in xs), max(abs(v) for v in ys),
            min(zs), max(zs))


def build(b):
    body_mat, lid_mat, label_mat = MATERIALS
    body = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.022,
                                    corner_segments=2), BODY_Z1),
                 'body', body_mat['linear_rgb'], body_mat['roughness'],
                 smooth=False, name='body')
    ribs = []
    for i, z in enumerate(RIB_Z):
        ribs.append(b.add(prism(rounded_rect(RIB_W, RIB_D, 0.023,
                                             corner_segments=2), RIB_T,
                                start=z),
                          'body', body_mat['linear_rgb'], body_mat['roughness'],
                          smooth=False, name='rib%d' % i))
    lid = b.add(prism(rounded_rect(LID_W, LID_D, 0.028, corner_segments=2),
                      LID_Z1 - LID_Z0, start=LID_Z0),
                'lid', lid_mat['linear_rgb'], lid_mat['roughness'],
                smooth=False, name='lid')
    label_y0 = BODY_D * 0.5 - 0.001
    label_y1 = BODY_D * 0.5 + LABEL_T - 0.001
    label = b.add(prism([(-LABEL_W * 0.5, label_y0),
                         (LABEL_W * 0.5, label_y0),
                         (LABEL_W * 0.5, label_y1),
                         (-LABEL_W * 0.5, label_y1)],
                        LABEL_Z1 - LABEL_Z0, start=LABEL_Z0),
                  'label', label_mat['linear_rgb'], label_mat['roughness'],
                  smooth=False, name='label')
    b.boolean_union(body, ribs + [lid, label])

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        ax, ay, z0, z1 = _extent(me, poly)
        if ay > BODY_D * 0.5 + 0.002 and z0 > LABEL_Z0 - 0.0025 \
                and z1 < LABEL_Z1 + 0.0025:
            poly.material_index = 2
        elif z0 >= LID_Z0 - 0.0006:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return b.finish(TARGET_LONGEST_M)
