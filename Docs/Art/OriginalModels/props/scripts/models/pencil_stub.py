"""Pencil stub - a 12 cm hexagonal pencil sharpened to a graphite point.

Contract: 0.120 x 0.008 x 0.008 m (real size), wood + paint + graphite,
<= 160 triangles.

One 6-sided sweep along +X: a flat painted butt, a hex barrel, a 0.15 mm proud
paint collar at the sharpened shoulder, a wood cone down to 1.3 mm, and a
graphite point ending at 0.12 mm - the same three materials every sharpened
pencil has, assigned by face centre after the sweep.

Orientation: the sweep's hexagonal section has vertices at the sampled angles, so
it would rest on a point.  Every vertex is therefore rotated 30 deg about the
sweep axis, which turns the section into a hexagon with a flat face up and a flat
face down, and the axis is placed at z = 0.866 * 4 mm so the flat underside sits
exactly on z = 0.  Across corners the section is 8 mm in Y (the row's number);
across flats it is 6.93 mm in Z, which is why the row's Z is slightly under its
nominal 8 mm - a real hex pencil is 6.9 mm across flats and 8 mm across corners.

The cone stations keep the axis height, so the point stays centred on the shaft
(a real sharpened pencil does not droop) and the wood/graphite cone is a true
frustum of the same hexagonal section.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): the off-palette
navy paint, light wood and grey graphite are replaced by three palette slots, and
the painted body now has a real step at the sharpened shoulder:
  * `plastic_yellow` - the painted barrel (linear 0.640 / 0.450 / 0.060, rough
                       0.48): a mid-tone body, not a near-black one
  * `wood`           - the sharpened cone (0.330 / 0.205 / 0.105, rough 0.72)
  * `metal_dark`     - the graphite point (0.090 / 0.095 / 0.105, rough 0.55),
                       the dark slot
The collar is geometry: the stations at x = 0.0392 m swell to a 4.15 mm radius
and fall back at 0.0400 m, so the paint stands 0.15 mm proud of the wood and the
paint/wood junction reads as a lip instead of a colour change on a flat surface.
"""

import math

from prop_lib import tube

ID = 'pencil_stub'
NAME_ZH = '铅笔头'
CATEGORY = 'stationery'
SOURCING = 'model ourselves：0.120 m 六棱铅笔，削出木锥、漆口台阶与石墨尖'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
MATERIALS = [
    {'key': 'plastic_yellow', 'name_zh': '漆面黄', 'linear_rgb': [0.640, 0.450, 0.060],
     'roughness': 0.48},
    {'key': 'wood', 'name_zh': '削出木面', 'linear_rgb': [0.330, 0.205, 0.105],
     'roughness': 0.72},
    {'key': 'metal_dark', 'name_zh': '石墨尖', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
]
NOTES = ('6-sided sweep, 6 stations (x = -0.060 / 0.0385 / 0.0392 / 0.0400 / '
         '0.0540 / 0.0600, radii 0.00400 / 0.00400 / 0.00415 / 0.00400 / 0.00130 '
         '/ 0.00012), section turned 30 deg about the sweep axis so the hexagon '
         'has a flat bottom; axis at z = 0.003464 m (0.866 x R) so the barrel '
         'rests on z = 0.  The 0.15 mm collar at the shoulder is the paint/wood '
         'step.  Materials by face centre x: plastic_yellow <= 0.0398, wood to '
         '0.0535, metal_dark beyond.  84 triangles, one closed shell, no '
         'boolean.  AABB 0.120 x 0.0083 x 0.00719 m.')

SEGMENTS = 6
BARREL_R = 0.0040
COLLAR_R = 0.00415
AXIS_Z = BARREL_R * math.sqrt(3.0) * 0.5
STATIONS = (
    (-0.0600, 0.00400),
    (0.0385, 0.00400),
    (0.0392, COLLAR_R),
    (0.0400, 0.00400),
    (0.0540, 0.00130),
    (0.0600, 0.00012),
)
PAINT_MAX = 0.0398
WOOD_MAX = 0.0535
PAINT_RGB = (0.640, 0.450, 0.060)
WOOD_RGB = (0.330, 0.205, 0.105)
GRAPHITE_RGB = (0.090, 0.095, 0.105)


def _slot(centre):
    if centre.x > WOOD_MAX:
        return 2
    if centre.x > PAINT_MAX:
        return 1
    return 0


def build(b):
    bm = tube([(x, 0.0, 0.0) for (x, _r) in STATIONS],
              [r for (_x, r) in STATIONS], segments=SEGMENTS)
    ca, sa = math.cos(math.radians(30.0)), math.sin(math.radians(30.0))
    for v in bm.verts:
        y, z = v.co.y, v.co.z
        v.co.y = y * ca - z * sa
        v.co.z = AXIS_Z + y * sa + z * ca
    pencil = b.add(bm, 'plastic_yellow', PAINT_RGB, 0.48, smooth=False,
                   name='pencil')

    me = pencil.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
