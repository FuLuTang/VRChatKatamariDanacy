"""Coin - 24 mm coin, 2 mm thick: 12-sided disc, flange, recessed relief ring.

Design three-axis 0.024 x 0.024 x 0.002 m.  The contract asks for a 12-16 sided
disc with a flange, so this is a lathe rather than a smooth disc: twelve segments
keep the rim visibly faceted at the 24 mm size without turning the coin into a
smooth cylinder.  Profile bottom to top: flat underside at z=0, vertical edge up
to 2.0 mm, the flange annulus sloping down to the flat field at 1.4 mm, then a
0.4 mm recessed relief ring and a raised inner disc at 1.45 mm.  The 0.6 mm step
at the rim is the raised border; the field stays flat so the coin reads as a
pressed disc and not as a shallow dish.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): the single brass
slot became the metal face plus the dark inner ring the spec asks for.
  * `brass`      - the disc, rim and inner field (linear 0.520 / 0.380 / 0.120,
                   rough 0.30)
  * `metal_dark` - the 0.4 mm recessed relief ring (0.090 / 0.095 / 0.105, rough
                   0.55), i.e. the dark engraved circle that makes a coin read as
                   a coin and not as a washer
168 triangles: twelve 12-triangle pole fans plus five 24-triangle quad bands plus
the 12-triangle inner cap.
"""

from prop_lib import lathe

ID = 'coin'
NAME_ZH = '硬币'
CATEGORY = 'debris'
SOURCING = 'model ourselves：Ø24 x 2 mm 12 边硬币，凸缘 + 凹刻浮雕环'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.024
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.024', 'game_longest_dim_m': '0.024'}
MATERIALS = [
    {'key': 'brass', 'name_zh': '硬币黄铜', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'metal_dark', 'name_zh': '凹刻浮雕环暗金属',
     'linear_rgb': [0.090, 0.095, 0.105], 'roughness': 0.55},
]
NOTES = ('12 segments, 8 profile points: one 12-triangle underside fan, five '
         '24-triangle quad bands and the 12-triangle inner cap = 168 triangles.  '
         'Radius 0.012 m, underside flat at z=0, vertical edge to 0.0020 m, '
         'flange annulus from r=0.0100 down to the 0.0014 m field, a 0.4 mm '
         'recessed ring at r 0.0080-0.0094 m (metal_dark) and a 0.00145 m inner '
         'disc.  AABB 0.024 x 0.024 x 0.0020 m, one closed shell.')

SEGMENTS = 12
PROFILE = [
    (0.0000, 0.0000),   # underside centre
    (0.0120, 0.0000),   # flat underside out to the edge
    (0.0120, 0.0020),   # vertical edge, full 2 mm thickness
    (0.0100, 0.0014),   # flange slope down to the field
    (0.0094, 0.0014),   # field in to the relief-ring lip
    (0.0094, 0.0010),   # relief-ring wall, 0.4 mm down
    (0.0080, 0.0010),   # relief-ring floor
    (0.0080, 0.00145),  # inner disc rise; cap_end closes it as a flat fan
]
BRASS_RGB = (0.520, 0.380, 0.120)
METAL_DARK_RGB = (0.090, 0.095, 0.105)


def _slot(centre):
    r = (centre.x ** 2 + centre.y ** 2) ** 0.5
    ring = 0.0079 < r < 0.0095 and centre.z < 0.00135
    return 1 if ring else 0


def build(b):
    coin = b.add(lathe(PROFILE, segments=SEGMENTS), 'brass', BRASS_RGB, 0.30,
                 smooth=False, name='coin')
    me = coin.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
