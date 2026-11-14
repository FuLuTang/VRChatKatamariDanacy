"""Watering can - lathe body, swept spout and top handle unioned into one shell."""

from mathutils import Vector

from prop_lib import lathe, tube

ID = 'watering_can'
NAME_ZH = '洒水壶'
CATEGORY = 'garden'
CSV_REAL_LONGEST_M = 0.25
CSV_GAME_LONGEST_M = 0.58
SOURCING = 'model ourselves：壶身圆柱 + 壶嘴，0.58 m'
TARGET_LONGEST_M = 0.58
PIVOT = 'base'
TRI_BUDGET = 600
MATERIALS = [
    {'key': 'enamel', 'name_zh': '灰绿搪瓷', 'linear_rgb': [0.10, 0.16, 0.10],
     'roughness': 0.55},
]
NOTES = ('The three parts are boolean-unioned (exact solver) so the seams are '
         'welded and no cap faces are buried inside the body.  Longest axis is '
         'the spout, which points +Y (Blender forward).')


def build(b):
    body_profile = [
        (0.100, 0.000),
        (0.130, 0.020),
        (0.130, 0.270),
        (0.118, 0.300),
        (0.104, 0.300),
    ]
    body = b.add(lathe(body_profile, segments=16), 'enamel',
                 (0.10, 0.16, 0.10), 0.55, smooth=False, name='body')

    spout_points = [
        Vector((0.000, 0.060, 0.090)),
        Vector((0.000, 0.160, 0.100)),
        Vector((0.005, 0.280, 0.160)),
        Vector((0.010, 0.380, 0.220)),
        Vector((0.012, 0.440, 0.250)),
    ]
    spout_radii = [0.038, 0.033, 0.028, 0.023, 0.021]
    spout = b.add(tube(spout_points, spout_radii, segments=8), 'enamel',
                  (0.10, 0.16, 0.10), 0.55, smooth=False, name='spout')

    handle_points = [
        Vector((0.0, -0.112, 0.278)),
        Vector((0.0, -0.082, 0.348)),
        Vector((0.0, 0.000, 0.398)),
        Vector((0.0, 0.082, 0.348)),
        Vector((0.0, 0.112, 0.278)),
    ]
    handle = b.add(tube(handle_points, [0.017] * 5, segments=6), 'enamel',
                   (0.10, 0.16, 0.10), 0.55, smooth=False, name='handle')

    b.boolean_union(body, [spout, handle])
