"""Ballpoint pen - 14.5 cm pen: red barrel, metal nose, pocket clip, grip rings.

Contract: 0.145 x 0.010 x 0.010 m (real size), plastic + metal + ink,
<= 180 triangles.

The body is one 8-sided sweep along +X: a 3.5 mm butt, the 5 mm barrel, a metal
cone to 1.5 mm at x = 60 mm, and a 0.35 mm point at 72.5 mm where the refill's
ball sits.  The sweep axis is at z = 5 mm, so the barrel - the widest part -
rests on z = 0 and the tapered nose lifts off the table exactly as a real pen
does.  Two 0.2 mm proud rings at x = -0.033 and -0.024 m are added as the grip
bands (two extra station pairs), so the grip is geometry, not a painted stripe.

The clip is a 1.2 mm-wide flat strip: a post inside the barrel at x = -0.062
rising to a 24 mm arm that stands 0.8 mm proud of the barrel.  Post and arm are
one 6-point L polygon extruded in Y, welded in by an exact union, so the pen
stays one closed shell.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): three palette
slots instead of an off-palette navy and a 0.70 bright steel.
  * `plastic_red` - the barrel (linear 0.480 / 0.075 / 0.060, rough 0.48)
  * `metal`       - the clip, the nose cone and the two grip rings (0.430 / 0.450
                    / 0.470, rough 0.32)
  * `metal_dark`  - the ball tip (0.090 / 0.095 / 0.105, rough 0.55), the dark slot
Materials are assigned after the union by face position - z above the barrel for
the clip, then the ring bands, then x for the nose and the ball - so the boolean
cannot reorder slots.
"""

from prop_lib import prism, tube

ID = 'pen_ballpoint'
NAME_ZH = '圆珠笔'
CATEGORY = 'stationery'
SOURCING = 'model ourselves：0.145 m 圆珠笔，红笔杆 + 金属握环 + 笔夹 + 金属笔尖'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.145
TRI_BUDGET = 288
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.145', 'game_longest_dim_m': '0.145'}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '红笔杆', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.48},
    {'key': 'metal', 'name_zh': '笔夹、笔尖与握环',
     'linear_rgb': [0.430, 0.450, 0.470], 'roughness': 0.32},
    {'key': 'metal_dark', 'name_zh': '墨珠与笔头', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
]
NOTES = ('8-sided sweep, 12 stations: butt x = -0.0725 r 0.0035, barrel r 0.005 '
         'with two 0.2 mm proud grip rings at x = -0.033 / -0.024 m, nose cone to '
         'r 0.0015 at x = 0.060, a 0.7 mm ball band from x = 0.0718 and the '
         'point at x = 0.0725 r 0.00035; axis at z = 0.005 '
         'm so the barrel rests on z = 0.  Clip: 6-point L prism 1.2 mm wide at '
         'y +/-0.0006, post buried to z = 0.0028, arm top at z = 0.0118, '
         'unioned.  Materials by face after the union (z > 0.0104 -> metal clip, '
         'ring x bands -> metal, x > 0.070 -> metal_dark ball, x > 0.045 -> '
         'metal nose, else plastic_red).  AABB 0.145 x 0.0104 x 0.0118 m.')

SEGMENTS = 8
AXIS_Z = 0.005
RING_R = 0.00520
STATIONS = (
    (-0.0725, 0.00350),
    (-0.0680, 0.00500),
    (-0.0335, 0.00500),
    (-0.0325, RING_R),
    (-0.0315, 0.00500),
    (-0.0245, 0.00500),
    (-0.0235, RING_R),
    (-0.0225, 0.00500),
    (0.0450, 0.00500),
    (0.0600, 0.00150),
    (0.0700, 0.00060),   # the dark ball band starts here
    (0.0718, 0.00045),
    (0.0725, 0.00035),
)
RINGS = ((-0.0340, -0.0310), (-0.0250, -0.0220))
CLIP_Y = 0.0006
CLIP_POLY = (
    (-0.0620, 0.0028),
    (-0.0620, 0.0118),
    (-0.0240, 0.0118),
    (-0.0240, 0.0106),
    (-0.0595, 0.0106),
    (-0.0595, 0.0028),
)
CLIP_TOP = 0.0104
NOSE_START = 0.0450
BALL_START = 0.0700
BARREL_RGB = (0.480, 0.075, 0.060)
METAL_RGB = (0.430, 0.450, 0.470)
INK_RGB = (0.090, 0.095, 0.105)


def _slot(centre):
    if centre.z > CLIP_TOP:
        return 1
    if centre.x > BALL_START:
        return 2
    if centre.x > NOSE_START:
        return 1
    for lo, hi in RINGS:
        if lo < centre.x < hi:
            return 1
    return 0


def build(b):
    body_bm = tube([(x, 0.0, AXIS_Z) for (x, _r) in STATIONS],
                   [r for (_x, r) in STATIONS], segments=SEGMENTS)
    body = b.add(body_bm, 'plastic_red', BARREL_RGB, 0.48, smooth=True,
                 name='body')
    clip = b.add(prism(CLIP_POLY, 2.0 * CLIP_Y, axis='y', start=-CLIP_Y),
                 'metal', METAL_RGB, 0.32, smooth=False, name='clip')
    b.boolean_union(body, [clip])

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
