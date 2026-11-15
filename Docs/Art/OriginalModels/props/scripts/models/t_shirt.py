"""T-shirt - flat laid-out tee silhouette with a raised collar band.

MessyRoom catalogue t6 / flat / 0.700 x 0.500 x 0.020 m; no catalog row, so the
module carries ``DOC_ROW``.

The silhouette is the whole shape: the plan is a *strip warp*, i.e. every row of
the grid is stretched to the tee's half-width at that height.  The width profile
is piecewise in four parts - hem to chest, the underarm diagonal, a flat sleeve
edge, then the shoulder slope into the collar - and the grid rows land on the
breakpoints, so the outline really is a T-shirt: 700 mm across the sleeve tips,
430 mm across the chest, 500 mm from shoulder to hem.  A height field could never
produce that outline, and a boolean of three boxes would leave interior faces.

The collar is one ``annulus`` welded on with an Exact boolean union: a raised
19 mm-wide band standing 4 mm proud of the cloth.  It is a ring, not a hole - the
shirt's surface runs underneath it - which keeps the garment one closed shell.

Three material slots (2026-11-15 material pass), all assigned by face centre on
that one shell:

* ``fabric_grey`` - the cloth;
* ``fabric_blue`` - the collar band and the bound sleeve/hem edges (the rim
  faces at the sleeve tips and along the bottom hem), i.e. the ribbing that a
  real tee has in a second colour;
* ``ink`` - a printed block on the chest: the top faces inside a 170 x 110 mm
  rectangle, so the print is geometry-free and has no lettering.

Four slots would say nothing more, so the rest of the cloth stays grey.

9 x 10 grid: 180 top + 38 underside fan + 76 rim = 294 triangles, plus the
collar and its seam, inside the 420 budget.
"""

import math

from models._room_shapes import annulus, grid_slab, lerp, smoothstep

ID = 't_shirt'
NAME_ZH = 'T恤'
CATEGORY = 'clothes_fabric'
SOURCING = 'model ourselves：平铺剪影 + 凸起领口圈，单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.700
TRI_BUDGET = 420
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.700', 'game_longest_dim_m': '0.700'}
MATERIALS = [
    {'key': 'fabric_grey', 'name_zh': '棉布花灰', 'linear_rgb': [0.270, 0.255,
                                                              0.230],
     'roughness': 0.94},
    {'key': 'fabric_blue', 'name_zh': '领口/包边蓝', 'linear_rgb': [0.120, 0.170,
                                                               0.290],
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '胸前印块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.80},
]
NOTES = ('Strip-warped 9 x 10 grid: half-width 0.215 at the hem, 0.205 at the '
         'chest (y 0.10), underarm diagonal to 0.350 at the sleeve edge '
         '(y 0.13-0.20), shoulder slope to 0.090 at the top edge (y 0.25).  '
         'Cloth 14 mm thick with a 1 mm undulation; collar is a 10-segment '
         'annulus (R 36/55 mm) standing 6 mm proud, unioned in.  '
         '0.700 x 0.500 x 0.020 m, ~400 triangles.  Slots: grey cloth, blue '
         'collar + bounded sleeve/hem rim, ink chest print block (170 x 110 mm '
         'of top faces).')

HALF_Y = 0.250
CLOTH = 0.0140
COLLAR_CY = 0.190
COLLAR_R_OUT = 0.055
COLLAR_R_IN = 0.036
COLLAR_Z0 = 0.010
COLLAR_Z1 = 0.020


def tee_half(y):
    if y <= 0.10:
        return lerp(0.205, 0.215, (0.10 - y) / 0.35)
    if y <= 0.13:
        return lerp(0.215, 0.350, smoothstep((y - 0.10) / 0.03))
    if y <= 0.20:
        return 0.350
    return lerp(0.350, 0.090, smoothstep((y - 0.20) / 0.05))


def warp(u, v):
    y = -HALF_Y + 2.0 * HALF_Y * v
    return (2.0 * u - 1.0) * tee_half(y), y


def top(x, y):
    return CLOTH + 0.0010 * math.sin(11.0 * x + 2.0) * math.cos(9.0 * y + 0.5)


def is_rim(centre):
    """Side wall of the cloth slab: between the underside fan and the top."""
    return 0.002 < centre.z < 0.0125


def is_binding(centre):
    """Bound edges: the sleeve tips and the bottom hem."""
    return is_rim(centre) and (abs(centre.x) > 0.30 or centre.y < -0.235)


def is_print(centre):
    """Printed chest block: top faces only, 170 x 110 mm over the chest."""
    return (centre.z > 0.0130 and abs(centre.x) < 0.085
            and 0.010 < centre.y < 0.120)


def build(b):
    cloth = grid_slab(9, 10, -1.0, 1.0, -1.0, 1.0, top, bottom_z=0.0, warp=warp)
    cloth_ob = b.add(cloth, 'fabric_grey', (0.270, 0.255, 0.230), 0.94,
                     smooth=False, name='cloth',
                     extras=[('fabric_blue', (0.120, 0.170, 0.290), 0.92,
                              is_binding),
                             ('ink', (0.030, 0.035, 0.045), 0.80, is_print)])

    collar = annulus(0.0, COLLAR_CY, COLLAR_Z0, COLLAR_Z1, COLLAR_R_OUT,
                     COLLAR_R_IN, segments=10)
    collar_ob = b.add(collar, 'fabric_blue', (0.120, 0.170, 0.290), 0.92,
                      smooth=False, name='collar')

    b.boolean_union(cloth_ob, [collar_ob])
