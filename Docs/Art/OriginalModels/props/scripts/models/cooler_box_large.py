"""Large cooler box - 70 x 45 x 40 cm cooler with a recessed lid handle.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_cooler_large_01 as cooler_box_large,
0.70 x 0.40 x 0.45 m (x x height x z), D_req 1.340: the mid-lawn "first really
big thing" of the late phase, one of the four defs the layout added for the
1.10-1.60 gap (U3).  prop_catalog.csv has no row, so the module declares
DOC_ROW.

It is the same cooler family as the 0.55 m `cooler_box` (teal body, pale lid,
dark grip inserts, flat lid plane) at 0.70 x 0.45 x 0.40, so the two read as
small/large of one product.  What is new at this size is the lid: a 180 x 55 mm
handle recess in the lid centre.  A top handle was rejected for the small box
because the map uses its top as a table, but this one is never a support surface
(D_req 1.34, 4x its height is 1.60 m), and a 0.70 m cooler with no lid handle
reads as a bench.  The recess is 12 mm deep, so the AABB top stays the flat lid
rim at z = 0.400.

Grips: the same blind 160 x 60 x 30 mm hand recesses in the two short ends as
the small box, plus two 60 x 28 mm latch clips on the front under the lid lip.
Both get the dark insert material, which is the only way a 8-30 mm recess reads
at preview scale.
"""

from prop_lib import prism, rounded_rect

ID = 'cooler_box_large'
NAME_ZH = '大冷藏箱'
CATEGORY = 'container'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / U3（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.70
TRI_BUDGET = 600
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.70', 'game_longest_dim_m': '0.70'}
MATERIALS = [
    {'key': 'shell', 'name_zh': '箱体青蓝', 'linear_rgb': [0.045, 0.120, 0.150],
     'roughness': 0.55},
    {'key': 'lid', 'name_zh': '盖面浅灰', 'linear_rgb': [0.600, 0.615, 0.585],
     'roughness': 0.60},
    {'key': 'grip', 'name_zh': '握槽深灰', 'linear_rgb': [0.030, 0.035, 0.038],
     'roughness': 0.72},
]
NOTES = ('Body 0.690 x 0.440 (R 32 mm, 16-gon) from z 0 to 0.352; lid '
         '0.700 x 0.450 (R 40 mm) from z 0.345 to 0.400 (5 mm overhang, 7 mm '
         'step); two 0.160 x 0.060 x 0.030 grip recesses in the +/-X ends at '
         'z 0.190-0.250; a 0.180 x 0.055 x 0.012 handle recess in the lid top; '
         'two 0.060 x 0.028 latch clips on +Y at z 0.290-0.318, 4 mm proud of '
         'the body.  AABB 0.700 x 0.450 x 0.400, lid rim flat at z = 0.400.  '
         'Materials by face extent: dark grip/latch regions, lid above '
         'z 0.3444, shell elsewhere.')

BODY_W, BODY_D, BODY_Z1 = 0.690, 0.440, 0.352
LID_W, LID_D, LID_Z0, LID_Z1 = 0.700, 0.450, 0.345, 0.400
GRIP_W, GRIP_H, GRIP_D, GRIP_Z = 0.160, 0.060, 0.030, 0.190
RECESS_W, RECESS_D, RECESS_Z = 0.180, 0.055, 0.388
LATCH_W, LATCH_H, LATCH_Z = 0.060, 0.028, 0.290
LATCH_Y0, LATCH_Y1 = BODY_D * 0.5 - 0.004, LID_D * 0.5 - 0.002


def _extent(me, poly):
    xs = [me.vertices[i].co.x for i in poly.vertices]
    ys = [me.vertices[i].co.y for i in poly.vertices]
    zs = [me.vertices[i].co.z for i in poly.vertices]
    return (max(abs(v) for v in xs), max(abs(v) for v in ys),
            min(zs), max(zs))


def build(b):
    shell, lid_mat, grip = MATERIALS
    body = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.032,
                                    corner_segments=3), BODY_Z1),
                 'shell', shell['linear_rgb'], shell['roughness'],
                 smooth=False, name='body')
    lid = b.add(prism(rounded_rect(LID_W, LID_D, 0.040, corner_segments=3),
                      LID_Z1 - LID_Z0, start=LID_Z0),
                'lid', lid_mat['linear_rgb'], lid_mat['roughness'],
                smooth=False, name='lid')
    b.boolean_union(body, [lid])

    cuts = []
    grip_profile = [(-GRIP_W * 0.5, GRIP_Z), (GRIP_W * 0.5, GRIP_Z),
                    (GRIP_W * 0.5, GRIP_Z + GRIP_H),
                    (-GRIP_W * 0.5, GRIP_Z + GRIP_H)]
    for side in (-1.0, 1.0):
        outer = side * (BODY_W * 0.5 + 0.010)
        inner = side * (BODY_W * 0.5 - GRIP_D)
        cuts.append(b.add(prism(grip_profile, abs(outer - inner), axis='x',
                                start=min(outer, inner)),
                          'shell', shell['linear_rgb'], shell['roughness'],
                          smooth=False, name='grip%d' % (side > 0)))
    cuts.append(b.add(prism([(-RECESS_W * 0.5, -RECESS_D * 0.5),
                             (RECESS_W * 0.5, -RECESS_D * 0.5),
                             (RECESS_W * 0.5, RECESS_D * 0.5),
                             (-RECESS_W * 0.5, RECESS_D * 0.5)],
                            LID_Z1 - RECESS_Z + 0.006, start=RECESS_Z),
                      'shell', shell['linear_rgb'], shell['roughness'],
                      smooth=False, name='recess'))
    b.boolean_difference(body, cuts)

    latches = []
    for sx in (-1.0, 1.0):
        cx = sx * 0.150
        latches.append(b.add(
            prism([(cx - LATCH_W * 0.5, LATCH_Y0),
                   (cx + LATCH_W * 0.5, LATCH_Y0),
                   (cx + LATCH_W * 0.5, LATCH_Y1),
                   (cx - LATCH_W * 0.5, LATCH_Y1)],
                  LATCH_H, start=LATCH_Z),
            'grip', grip['linear_rgb'], grip['roughness'], smooth=False,
            name='latch%d' % (sx > 0)))
    b.boolean_union(body, latches)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        ax, ay, z0, z1 = _extent(me, poly)
        in_grip = (ax > BODY_W * 0.5 - GRIP_D - 0.0015
                   and ay < GRIP_W * 0.5 + 0.0015
                   and z0 > GRIP_Z - 0.0015 and z1 < GRIP_Z + GRIP_H + 0.0015)
        in_recess = (z0 > RECESS_Z - 0.0015 and ax < RECESS_W * 0.5 + 0.0015
                     and ay < RECESS_D * 0.5 + 0.0015)
        in_latch = (ay > BODY_D * 0.5 + 0.0015
                    and ax < 0.150 + LATCH_W * 0.5 + 0.0015
                    and z0 > LATCH_Z - 0.0015 and z1 < LATCH_Z + LATCH_H + 0.0015)
        if in_grip or in_recess or in_latch:
            poly.material_index = 2
        elif z0 >= LID_Z0 - 0.0006:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return b.finish(TARGET_LONGEST_M)
