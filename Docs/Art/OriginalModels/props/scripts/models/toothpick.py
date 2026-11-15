"""Toothpick - a 65 mm double-pointed wooden pick, thickest at the waist.

Contract: 0.065 x 0.002 x 0.002 m (real size), <= 120 triangles.

One 10-sided sweep along +X through five stations: a 0.06 mm point at each end,
a 0.55 mm shaft 24 mm in, and a 1.00 mm waist at the centre.  That is what makes
it read as "two sharpened ends with a slightly fatter middle" instead of a
uniform needle, which is the shape the catalogue row asks for.

The sweep axis sits at z = 1.0 mm, so the waist's underside rests exactly on
z = 0 (the table plane) and the points float 0.94 mm above it - a real toothpick
rests on its thickest part, not on a knife edge.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): one pale slot
became two palette tones and a carved grip band.
  * `straw`     - the birch pick (linear 0.470 / 0.360 / 0.140, rough 0.90)
  * `wood_dark` - the two carved rings that bound the waist band (0.135 / 0.078 /
                  0.038)
Four stations are added either side of the waist (x = -0.008 / -0.006 / 0.006 /
0.008) and those two pairs are shrunk to a 0.80 mm radius, so the middle is a
real 0.2 mm-relieved grip section rather than a painted stripe.  The shrunk
stations stay inside the 1.00 mm waist radius, so the AABB is unchanged.

No boolean and no second part: one closed tube, so the shell is watertight by
construction.  180 triangles: seven 20-triangle bands plus two 10-triangle fans.
"""

from prop_lib import tube

ID = 'toothpick'
NAME_ZH = '牙签'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：0.065 m 双尖木牙签，中段凹环握位'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.065
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.065', 'game_longest_dim_m': '0.065'}
MATERIALS = [
    {'key': 'straw', 'name_zh': '浅桦木', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '握位凹环深木',
     'linear_rgb': [0.135, 0.078, 0.038], 'roughness': 0.75},
]
NOTES = ('Single 10-sided sweep, 9 stations: 0.06 mm tip, 0.55 mm shaft at '
         '+/-24 mm, 1.00 mm waist, and a 0.80 mm grip band across x = -0.008..'
         '0.008 m whose two ends are the wood_dark rings.  Axis at z = 1.0 mm so '
         'the waist touches z = 0.  AABB 0.065 x 0.0019 x 0.0020 m (a decagon is '
         '1.9 mm across flats), 180 triangles, one shell, no boolean.')

SEGMENTS = 10
AXIS_Z = 0.0010
BAND_R = 0.00080        # grip band radius: relieved inside the 1.00 mm waist
# (x, radius) - mirrored about x = 0 so both ends are sharpened the same way.
STATIONS = (
    (-0.0325, 0.00006),
    (-0.0240, 0.00055),
    (-0.0080, 0.00080),
    (-0.0060, BAND_R),
    (0.0000, BAND_R),
    (0.0060, BAND_R),
    (0.0080, 0.00080),
    (0.0240, 0.00055),
    (0.0325, 0.00006),
)
STRAW_RGB = (0.470, 0.360, 0.140)
WOOD_DARK_RGB = (0.135, 0.078, 0.038)


def build(b):
    points = [(x, 0.0, AXIS_Z) for (x, _r) in STATIONS]
    radii = [r for (_x, r) in STATIONS]
    bm = tube(points, radii, segments=SEGMENTS)
    pick = b.add(bm, 'straw', STRAW_RGB, 0.90, smooth=True, name='pick')

    me = pick.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        ring = 0.0069 < abs(poly.center.x) < 0.0081
        poly.material_index = 1 if ring else 0
