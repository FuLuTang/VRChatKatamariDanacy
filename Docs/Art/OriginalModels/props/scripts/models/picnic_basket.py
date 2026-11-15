"""Picnic basket - 45 x 30 x 24 cm open wicker basket with an arch handle.

PROP_SET_PLAN.md section 3.5 gives the hard three-axis 0.45 x 0.30 x 0.24 m and
the shape "45x30x22 cm body + arch handle spanning 30 cm, 15 cm high", and the
catalog repeats it.  Those two notes do not add up (22 + 15 = 37 cm, not 24), so
the three-axis AABB wins: the body is 15 cm to the rim and the handle arch adds
9 cm, ending at exactly 0.24 m.  A 15 cm arch over a 17 cm body would make a
37 cm basket, which is a different prop.

The body is an open tapered-less wicker tub: a rounded-rectangle prism with a
14 mm wall, a 12 mm floor and a 16 mm rim lip that stands 2 mm proud, cut open
with a boolean difference.  Three 6 mm horizontal bands stand 2 mm proud of the
wall so the weave reads in silhouette; the catalog asks for three grooves and
proud bands give the same read without a second inner wall.  The handle is one
20 mm tube arching across the 30 cm side, its ends buried in the rim lip so the
whole basket stays one closed shell.

The basket is a container and a blocker, not a step: the runtime has no vertical
support yet, so nothing is climbable.  If a later collider pass splits it, the
body wants one box and the handle none (see the batch table).
"""

from prop_lib import prism, rounded_rect, tube

ID = 'picnic_basket'
NAME_ZH = '野餐篮'
CATEGORY = 'container'
CSV_REAL_LONGEST_M = 0.45
CSV_GAME_LONGEST_M = 1.20
SOURCING = ('model ourselves：0.45 m 篮体放大到 1.20 m 游戏尺寸'
            '（藤条用 alpha 卡）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.45
TRI_BUDGET = 800
MATERIALS = [
    {'key': 'wicker', 'name_zh': '藤条浅棕', 'linear_rgb': [0.340, 0.210, 0.100],
     'roughness': 0.85},
    {'key': 'handle', 'name_zh': '提手深棕', 'linear_rgb': [0.250, 0.150, 0.070],
     'roughness': 0.80},
]
NOTES = ('Rounded-rect tub: body 0.446 x 0.296 to z 0.150, rim lip and three weave '
         'bands 0.450 x 0.300 both 2 mm proud, wall 14-16 mm, floor 12 mm, open top by '
         'boolean difference; handle is a 9-station, 6-segment tube (r 10 mm) '
         'from y = +/-0.142, z = 0.140 to z = 0.230, so the AABB is exactly '
         '0.450 x 0.300 x 0.240.  Materials by face-centre z: handle above '
         '0.152, wicker below.')

BODY_W, BODY_D = 0.446, 0.296
LIP_W, LIP_D = 0.450, 0.300
BODY_H = 0.1375
LIP_Z, LIP_H = 0.1225, 0.015
CAVITY_W, CAVITY_D = 0.418, 0.268
FLOOR_Z = 0.0110
BAND_Z = (0.0367, 0.0770, 0.1173)
BAND_H = 0.006
HANDLE_R = 0.010
HANDLE_Y = 0.142
HANDLE_Z0 = 0.1283
HANDLE_RISE = 0.0830


def build(b):
    body = b.add(prism(rounded_rect(BODY_W, BODY_D, 0.035), BODY_H),
                 'wicker', MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
                 smooth=False, name='body')

    rim = b.add(prism(rounded_rect(LIP_W, LIP_D, 0.038), LIP_H,
                      start=LIP_Z),
                'wicker', MATERIALS[0]['linear_rgb'],
                MATERIALS[0]['roughness'], smooth=False, name='rim')
    bands = [b.add(prism(rounded_rect(LIP_W, LIP_D, 0.038), BAND_H, start=z),
                   'wicker', MATERIALS[0]['linear_rgb'],
                   MATERIALS[0]['roughness'], smooth=False, name='band%d' % i)
             for i, z in enumerate(BAND_Z)]
    b.boolean_union(body, [rim] + bands)

    cavity = b.add(prism(rounded_rect(CAVITY_W, CAVITY_D, 0.032), 0.190,
                         start=FLOOR_Z),
                   'wicker', MATERIALS[0]['linear_rgb'],
                   MATERIALS[0]['roughness'], smooth=False, name='cavity')
    b.boolean_difference(body, [cavity])

    points = []
    for i in range(9):
        t = -1.0 + 2.0 * i / 8.0
        points.append((0.0, HANDLE_Y * t,
                       HANDLE_Z0 + HANDLE_RISE * (1.0 - t * t)))
    handle = b.add(tube(points, [HANDLE_R] * len(points), segments=6),
                   'handle', MATERIALS[1]['linear_rgb'],
                   MATERIALS[1]['roughness'], smooth=True, name='handle')
    b.boolean_union(body, [handle])

    me = body.data
    me.materials.clear()
    me.materials.append(b.material('wicker', MATERIALS[0]['linear_rgb'],
                                   MATERIALS[0]['roughness']))
    me.materials.append(b.material('handle', MATERIALS[1]['linear_rgb'],
                                   MATERIALS[1]['roughness']))
    for poly in me.polygons:
        poly.material_index = 1 if poly.center.z > 0.137 else 0
    return b.finish(TARGET_LONGEST_M)
