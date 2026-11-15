"""Jar lid - 100 mm screw-lid ring, 12 mm tall, with a knurled skirt.

The catalog asks for a 0.100 x 0.100 x 0.012 m shallow dish lid with a knurled
or grooved rim.  The lathe profile starts at the recessed underside centre,
runs out to the bottom rim that touches the ground, climbs the 9 mm skirt, rolls
over the top edge and comes back across the dished top to the axis, so the
underside cavity is part of the same closed surface and the lid is one shell.

The knurling is a per-segment radius modulation of the two skirt profile points
(the same trick as the bottle cap), which costs no extra triangles: 12 segments
and 5 bands give 96 triangles, inside the 140 budget.

Material pass (2026-11-15 rework): one bright tin slot (0.52/0.54/0.56) read as
a white disc.  The lid is now three flat colours with no extra geometry: a
gold-lacquered brass top (the dished face), a bright aluminium rolled rim and
underside, and a dark oxidised skirt.  Brass 0.52 / metal 0.47 / metal_dark 0.105
- the skirt is the required low-value slot and the brass-to-aluminium hue split
(43 deg vs 197 deg) is far over the 60 deg contrast rule.
"""

from prop_lib import lathe

ID = 'jar_lid'
NAME_ZH = '瓶盖圈'
CATEGORY = 'container'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.100', 'game_longest_dim_m': '0.100'}
SOURCING = 'model ourselves：Ø0.100 x 0.012 m 浅盘状旋盖，周边滚花'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.100
TRI_BUDGET = 140
MATERIALS = [
    {'key': 'brass', 'name_zh': '顶面漆金', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'metal', 'name_zh': '镀锡金属', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'metal_dark', 'name_zh': '氧化裙边', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
]
NOTES = ('12 segments, 6 profile points = 96 triangles.  Diameter 0.100 m, '
         'height 0.0120 m.  The skirt occupies z = 0-9 mm at r = 49-50 mm and is '
         'fluted: every second segment is pulled in to 96.5 % of the radius on '
         'the upper skirt point only, so the top roll and the dished top stay '
         'round.  Underside centre recessed 1.8 mm, dished top at 8.5 mm.  '
         'Slots by face centre: brass = z > 9.3 mm inside r 44.5 mm (dished '
         'top), metal_dark = z < 9.5 mm outside r 44.5 mm (skirt and its bottom '
         'bevel), metal = the rolled rim and the hidden underside.')

SEGMENTS = 12
SKIRT_POINTS = (3,)
FLUTE_SCALE = 0.965

BRASS = (0.520, 0.380, 0.120)
METAL = (0.430, 0.450, 0.470)
METAL_DARK = (0.090, 0.095, 0.105)

TOP_Z = 0.0093
SKIRT_Z = 0.0095
TOP_RADIUS = 0.0445

PROFILE = [
    (0.0000, 0.0018),   # recessed underside centre
    (0.0400, 0.0018),   # flat underside out to 40 mm
    (0.0500, 0.0000),   # bottom rim, ground contact, unfluted widest ring
    (0.0500, 0.0090),   # skirt top, outside
    (0.0450, 0.0120),   # rolled rim, top edge
    (0.0000, 0.0085),   # dished top centre
]


def knurl(point_index, segment_index, radius):
    """Alternating skirt radius: 24 flutes around a 12-segment ring."""
    if point_index in SKIRT_POINTS and segment_index % 2 == 1:
        return radius * FLUTE_SCALE
    return radius


def top(centre):
    """Gold-lacquered dished top."""
    return (centre.z > TOP_Z
            and centre.x * centre.x + centre.y * centre.y < TOP_RADIUS ** 2)


def skirt(centre):
    """Oxidised dark skirt and its bottom bevel."""
    return (centre.z < SKIRT_Z
            and centre.x * centre.x + centre.y * centre.y > TOP_RADIUS ** 2)


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS, radius_fn=knurl)
    b.add(bm, 'metal', METAL, 0.32, smooth=False,
          extras=[('brass', BRASS, 0.30, top),
                  ('metal_dark', METAL_DARK, 0.55, skirt)])
