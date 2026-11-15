"""Watermelon - whole melon, 30 x 24 x 22 cm ellipsoid with painted stripes.

The plan asks for an ellipsoid 0.30 x 0.24 x 0.22 m with five to six paler
longitudinal stripes as a second flat material, no texture.  A UV sphere is
rotated so its poles sit on the long axis and then squashed to those three
axes.  That orientation matters: the sphere's own longitude bands then run end
to end along X, so the pale stripe material follows the tessellation instead of
cutting across it.  The first version kept the poles on Z and selected stripes
by an angle around X, which produced the blocky camouflage pattern visible in
the first preview.

Six stripes: two 15-degree faces pale and two dark per 60-degree cycle, which
gives an even six-stripe melon.  The stripes converge at the two ends exactly
where a real melon's stripes do.
"""

import math

from prop_lib import TAU, uv_sphere

ID = 'watermelon'
NAME_ZH = '西瓜'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.30
CSV_GAME_LONGEST_M = 0.72
SOURCING = 'model ourselves：整颗椭球，30×24×22 cm，深绿底 + 浅色纵纹'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.30
TRI_BUDGET = 600
MATERIALS = [
    {'key': 'rind', 'name_zh': '深绿瓜皮', 'linear_rgb': [0.035, 0.120, 0.030],
     'roughness': 0.40},
    {'key': 'stripe', 'name_zh': '浅绿瓜纹', 'linear_rgb': [0.220, 0.330, 0.090],
     'roughness': 0.40},
]
NOTES = ('24 x 12 UV sphere = 528 triangles, rotated so the poles are on X and '
         'then squashed to 0.30/0.24/0.22 m (the three axes the plan lists).  Six '
         'stripes: faces whose longitude around X falls within 15 degrees of one '
         'of six centres get the pale material, so each stripe is two whole faces '
         'wide and follows the sphere longitude end to end.')

SEGMENTS = 24
RINGS = 12
HALF_X = 0.150
HALF_Y = 0.120
HALF_Z = 0.110
STRIPES = 6
STRIPE_OFFSET = math.radians(7.5)
STRIPE_RAD = math.radians(6.5)


def stripe(centre):
    """True for faces inside one of the six longitudinal pale bands."""
    # Undo the ellipsoid squash before taking the angle: the UV sphere's
    # longitude is defined on the unit sphere, not on the scaled axes.
    angle = math.atan2(centre.z / HALF_Z, centre.y / HALF_Y)
    period = TAU / STRIPES
    band = ((angle - STRIPE_OFFSET) % period) - period * 0.5
    return abs(band) < STRIPE_RAD


def build(b):
    bm = uv_sphere(SEGMENTS, RINGS, 1.0)
    for v in bm.verts:
        x, y, z = v.co
        # +90 degrees about Y: poles move from +/-Z to +/-X, so the sphere's
        # longitude bands become stripes running end to end along the melon.
        v.co.x = z * HALF_X
        v.co.y = y * HALF_Y
        v.co.z = -x * HALF_Z
    b.add(bm, 'rind', (0.035, 0.120, 0.030), 0.40, smooth=True,
          extras=[('stripe', (0.220, 0.330, 0.090), 0.40, stripe)])
