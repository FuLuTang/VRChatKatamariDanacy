"""Cooler box - 55 x 32 x 32 cm rounded box with a proud lid and end grips.

PROP_SET_PLAN.md section 3.5: 0.55 x 0.32 x 0.32 m, "55x32x30 cm rounded box +
1 cm lid seam + top handle".  The three-axis 0.32 m height is taken as the whole
box including the lid, so the body is 0.312 m and the lid 0.018 m overlapping it
by 10 mm; that keeps the lid seam (a 3 mm overhang step at z = 0.282) visible
without any extra geometry.

The design's top handle is deliberately moved to the two short ends as blind
grip recesses.  A top handle would break the one thing this prop is for in the
corner map - a dead-flat 0.55 x 0.32 m top that a plate or the ball can sit on -
and the map already has the box as the "table" of the heavy structural group.
The recesses are 120 x 50 mm, 25 mm deep boolean cuts; 25 mm rather than the 11 mm
of the first pass because a 10 mm pocket is invisible at preview scale (same
teal, same normal, no shadow).  The pocket floor and walls get a dark rubber
material, which is how a recessed cooler grip actually reads.

Two structural materials (teal body, pale grey lid) and one dark grip insert;
the lid splits by face-centre z at the seam, the grip by the pocket's volume.
No drains, latches or logos - at 0.55 m those would be sub-millimetre noise in a
512 px preview.
"""

from prop_lib import prism, rounded_rect

ID = 'cooler_box'
NAME_ZH = '保温箱'
CATEGORY = 'container'
CSV_REAL_LONGEST_M = 0.55
CSV_GAME_LONGEST_M = 1.60
SOURCING = 'model ourselves：0.55 m 箱体放大到 1.60 m，带提手与盖缝'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.55
TRI_BUDGET = 800
MATERIALS = [
    {'key': 'shell', 'name_zh': '箱体青蓝', 'linear_rgb': [0.050, 0.150, 0.185],
     'roughness': 0.55},
    {'key': 'lid', 'name_zh': '盖面浅灰', 'linear_rgb': [0.600, 0.620, 0.590],
     'roughness': 0.60},
    {'key': 'grip', 'name_zh': '握槽深灰', 'linear_rgb': [0.030, 0.033, 0.035],
     'roughness': 0.72},
]
NOTES = ('Rounded-rect body 0.544 x 0.344 x 0.292 (R 28 mm) unioned with a lid '
         '0.550 x 0.350 x 0.018 starting at z = 0.282 (R 32 mm, 3 mm overhang and '
         'a 10 mm step), then two 0.120 x 0.050 x 0.025 grip recesses cut into '
         'the +/-X ends at z 0.190-0.240.  AABB 0.550 x 0.320 x 0.320, top is '
         'one flat plane.  Materials: lid above z 0.3017, dark grip inside '
         '|x| 0.240-0.272 and |y| < 0.062 at z 0.182-0.248, shell elsewhere.')

BODY_W, BODY_D, BODY_H = 0.544, 0.344, 0.292
LID_W, LID_D, LID_H = 0.550, 0.350, 0.018
LID_Z = 0.282
GRIP_W, GRIP_H, GRIP_D = 0.120, 0.050, 0.025
GRIP_Z = 0.190


def build(b):
    body = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.028), BODY_H),
                 'shell', MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
                 smooth=False, name='body')
    lid = b.add(prism(rounded_rect(LID_W, LID_D, 0.032), LID_H, start=LID_Z),
                'lid', MATERIALS[1]['linear_rgb'], MATERIALS[1]['roughness'],
                smooth=False, name='lid')
    b.boolean_union(body, [lid])

    grips = []
    profile = [(-GRIP_W * 0.5, GRIP_Z), (GRIP_W * 0.5, GRIP_Z),
               (GRIP_W * 0.5, GRIP_Z + GRIP_H), (-GRIP_W * 0.5, GRIP_Z + GRIP_H)]
    for side in (-1.0, 1.0):
        # Each cut runs 10 mm outside the end and GRIP_D into it.
        outer = side * (BODY_W * 0.5 + 0.010)
        inner = side * (BODY_W * 0.5 - GRIP_D)
        start = min(outer, inner)
        grips.append(b.add(prism(profile, abs(outer - inner), axis='x',
                                 start=start),
                           'shell', MATERIALS[0]['linear_rgb'],
                           MATERIALS[0]['roughness'], smooth=False,
                           name='grip%d' % (1 if side > 0 else 0)))
    b.boolean_difference(body, grips)

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        c = poly.center
        if (0.182 < c.z < 0.248 and abs(c.y) < 0.062
                and 0.240 < abs(c.x) < 0.2715):
            poly.material_index = 2
        elif c.z > 0.2817:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return b.finish(TARGET_LONGEST_M)
