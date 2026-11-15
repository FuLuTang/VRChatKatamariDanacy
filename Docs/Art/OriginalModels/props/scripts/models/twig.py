"""Twig - a 9 cm bent, tapered, bark-covered stick with a bud node and a nub.

Contract: 0.090 x 0.008 x 0.007 m (real size), wood + bark, <= 140 triangles.

Shape: one 6-sided sweep along +X that bows 1 mm in Y, tapers from a 7 mm broken
butt to a 3 mm tip, and carries a 0.6 mm bud swelling at x = -0.017.  The butt
and the tip are cut flat, which is the one place a cross-section of pale wood is
exposed - that is what tells a stick apart from a plain dowel at this size.

The small side nub that leaves the body at x = 0 is a 5-sided sweep welded in by
an exact union.  It stays inside the 8 x 7 mm envelope (it reaches y = 2.6 mm,
z = 6.0 mm) because the catalogue row is only 8 mm wide.  Both parts are welded,
so the result is still one closed shell - the multi-shell trap the prop guide
warns about.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, natural class):
the old pair was a dark bark plus an off-palette light cut; now three palette
tones, with the green as a local second colour instead of a single-colour body.
  * `wood_dark` - the bark (linear 0.135 / 0.078 / 0.038, rough 0.75)
  * `straw`     - the pale cut sections at the butt and the tip (0.470 / 0.360 /
                  0.140)
  * `leaf_green`- the growing tip (0.115 / 0.240 / 0.075) and a collar around the
                  bud node at x = -0.017 m, i.e. the two live parts of a twig
"""

from prop_lib import tube

ID = 'twig'
NAME_ZH = '小树枝'
CATEGORY = 'nature'
SOURCING = 'model ourselves：0.090 m 微弯小树枝，带芽点与侧芽、绿色新梢'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.090
TRI_BUDGET = 140
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.090', 'game_longest_dim_m': '0.090'}
MATERIALS = [
    {'key': 'wood_dark', 'name_zh': '深褐树皮', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
    {'key': 'straw', 'name_zh': '浅色断面', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'leaf_green', 'name_zh': '新梢与芽环', 'linear_rgb': [0.115, 0.240, 0.075],
     'roughness': 0.85},
]
NOTES = ('Main sweep: 6 stations, 6 segments, radii 0.0024 -> 0.0036 (bud node '
         'at x = -0.017) -> 0.0014 tip, centreline y -1.0 -> +1.0 mm, axis at '
         'z = 3.4 mm so the butt rests on z = 0.  Nub: 5-segment 2-station sweep '
         'from inside the body to (0.004, 0.0020, 0.0054), welded by an exact '
         'union; it reaches y = 2.6 mm / z = 6.0 mm, inside the 8 x 7 mm row.  '
         'Materials by face centre after the union: cut faces (x <= -0.0449 / '
         'x >= 0.0449) are straw, the last 15 mm of the tip and an 11 mm collar '
         'at the bud are leaf_green, the rest is bark.  AABB 0.090 x 0.0078 x '
         '0.0068 m.')

SEGMENTS = 6
AXIS_Z = 0.0034
BODY = (
    (-0.0450, -0.0014, 0.0024),
    (-0.0300, -0.0008, 0.0030),
    (-0.0170, 0.0000, 0.0036),
    (-0.0120, 0.0005, 0.0031),
    (0.0150, 0.0011, 0.0028),
    (0.0450, 0.0014, 0.0014),
)
NUB = (
    (-0.0020, 0.0002, 0.0026, 0.00130),
    (0.0040, 0.0020, 0.0054, 0.00055),
)
CUT = 0.0449
GREEN_TIP = 0.0300      # from here on the shoot is green
BUD_LO, BUD_HI = -0.0225, -0.0115
BARK_RGB = (0.135, 0.078, 0.038)
WOOD_RGB = (0.470, 0.360, 0.140)
GREEN_RGB = (0.115, 0.240, 0.075)


def _slot(centre):
    x = centre.x
    if x <= -CUT or x >= CUT:
        return 1
    if x > GREEN_TIP or BUD_LO < x < BUD_HI:
        return 2
    return 0


def build(b):
    body_bm = tube([(x, y, AXIS_Z) for (x, y, _r) in BODY],
                   [r for (_x, _y, r) in BODY], segments=SEGMENTS)
    body = b.add(body_bm, 'wood_dark', BARK_RGB, 0.75, smooth=True, name='body')

    nub_bm = tube([(x, y, z) for (x, y, z, _r) in NUB],
                  [r for (_x, _y, _z, r) in NUB], segments=5)
    nub = b.add(nub_bm, 'wood_dark', BARK_RGB, 0.75, smooth=True, name='nub')
    b.boolean_union(body, [nub])

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
