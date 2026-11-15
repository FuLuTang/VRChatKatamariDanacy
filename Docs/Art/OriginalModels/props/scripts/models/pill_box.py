"""Pill box - 120 x 55 x 20 mm rounded flat box, lid seam and end label.

The body is split into a lower shell and a lid that is inset 1 mm on all
sides, so the step at z = 11 mm is a clear horizontal lid seam at the top
third.  A shallow divider groove still crosses the lid at x = 0, a dark label
plate sits on one end of the top, and a barrel hinge at the other end reaches
exactly the 20 mm overall height.

Material pass (2026-11-15 rework): the old pair was a near-white body
(0.80/0.79/0.74) with a dark hinge; the seam step was invisible because both
halves were the same colour.  Now the lower shell is the red plastic key and the
inset lid the yellow plastic key, so the 1 mm seam reads as a colour break, the
label plate is ink and the hinge and divider groove stay metal.  Four slots, no
geometry change: 162 triangles.
"""

from prop_lib import lathe, prism, rounded_rect

ID = 'pill_box'
NAME_ZH = '药盒'
CATEGORY = 'container'
SOURCING = 'model ourselves：120 x 55 x 20 mm 分格药盒，明显开盖缝'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 180
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '盒身红塑料', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.48},
    {'key': 'plastic_yellow', 'name_zh': '盒盖黄塑料', 'linear_rgb': [0.640, 0.450, 0.060],
     'roughness': 0.48},
    {'key': 'ink', 'name_zh': '标签暗面', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'metal', 'name_zh': '铰链金属', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
]
NOTES = ('Lower shell 120 x 55 x 12 mm (R 10 mm); inset lid 118 x 53 x 8 mm '
         'from z 11 to 19 mm, giving a 1 mm seam step at z 11; 1.4 x 1 mm '
         'divider groove at x 0; 30 x 18 x 1 mm ink label plate on the top at '
         'x -45..-15 mm; 40 mm x r 2.5 mm metal hinge at x 52 mm.  Height 20 mm.  '
         'Slots: metal hinge (z > 14.8 mm, x > 46 mm), ink label (z > 19.5 mm, '
         'x < -10 mm), yellow lid above the seam, red lower shell below it.')

BODY_W, BODY_D = 0.120, 0.055
LOWER_H = 0.012
LID_W, LID_D = 0.118, 0.053
LID_Z0, LID_Z1 = 0.011, 0.019
LABEL_X0, LABEL_X1 = -0.045, -0.015
LABEL_Y0, LABEL_Y1 = -0.009, 0.009
GROOVE_W, GROOVE_DEPTH = 0.0014, 0.0010
HINGE_R, HINGE_L = 0.0025, 0.040
HINGE_X, HINGE_Z = 0.052, 0.0175
SEAM_Z = 0.01085

RED = (0.480, 0.075, 0.060)
YELLOW = (0.640, 0.450, 0.060)
INK = (0.030, 0.035, 0.045)
METAL = (0.430, 0.450, 0.470)


def build(b):
    lower = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.010, corner_segments=2),
                        LOWER_H),
                  'plastic_red', RED, 0.48,
                  smooth=False, name='lower')
    lid = b.add(prism(rounded_rect(LID_W, LID_D, 0.010, corner_segments=2),
                      LID_Z1 - LID_Z0, start=LID_Z0),
                'plastic_yellow', YELLOW, 0.48,
                smooth=False, name='lid')
    label = b.add(prism([(LABEL_X0, LABEL_Y0), (LABEL_X1, LABEL_Y0),
                         (LABEL_X1, LABEL_Y1), (LABEL_X0, LABEL_Y1)],
                        0.001, start=LID_Z1),
                  'ink', INK, 0.75,
                  smooth=False, name='label')
    hinge_bm = lathe([(HINGE_R, 0.0), (HINGE_R, HINGE_L)], segments=8)
    for vert in hinge_bm.verts:
        x, y, z = vert.co
        vert.co = (HINGE_X + x, z - HINGE_L * 0.5, HINGE_Z + y)
    hinge = b.add(hinge_bm, 'metal', METAL, 0.32,
                  smooth=True, name='hinge')
    b.boolean_union(lower, [lid, label, hinge])

    groove = b.add(
        prism([(-GROOVE_W * 0.5, -BODY_D), (GROOVE_W * 0.5, -BODY_D),
               (GROOVE_W * 0.5, BODY_D), (-GROOVE_W * 0.5, BODY_D)],
              0.003, start=LID_Z1 - GROOVE_DEPTH),
        'metal', METAL, 0.32, smooth=False,
        name='groove')
    b.boolean_difference(lower, [groove])
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
        if z > 0.0148 and c.x > 0.046:
            poly.material_index = 3       # barrel hinge
        elif z > 0.0195 and c.x < -0.010:
            poly.material_index = 2       # ink label plate
        elif z > SEAM_Z:
            poly.material_index = 1       # yellow lid
        else:
            poly.material_index = 0       # red lower shell
    return info
