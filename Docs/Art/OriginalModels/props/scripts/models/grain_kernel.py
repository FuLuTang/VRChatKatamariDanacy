"""Grain kernel - 20 mm teardrop grain, pointed at one end, grooved on top.

The smallest prop in the dung-meadow set, so the shape is one lathe and nothing
else: a six-point profile revolved over ten segments (80 triangles, exactly the
80-triangle budget).  The proportions carry the read, not the detail: the body
widens to the full radius within 12% of its length, peaks at 42%, and then
tapers over the remaining 58% to a single point.

Three materials, because a single mid-brown kernel reads as a featureless oval
at preview size (owner feedback, 2026-11-15):

* ``straw`` - the wheat-yellow body, the palette's hay/grain tone.
* ``paper`` - a pale starchy crest.  The 0-36 degree column is lifted 14% to
  raise a real ridge beside the groove, and those faces take the pale slot:
  that is the one light line a real wheat kernel has.
* ``wood_dark`` - the germ.  The blunt pole is pushed 7.5% of the body length
  into the kernel, which turns the ten-triangle end fan into a concave dimple,
  and the dimple faces take the dark slot.  No boolean and no extra rings: the
  pole move is free geometry.

The groove is still two adjacent vertex columns pulled in, now by 24% instead of
18% - a real groove would need an inset and a second loop of geometry the budget
does not have.  The columns are the ones that end up on the +Y flank after the
90-degree turn, i.e. the face the shared preview camera looks at.

A final 180-degree turn about Y puts the germ end and the crest on the side the
shared camera looks from; the underside column is then raised to 80% of the
radius so the grain rests on a small flat, and ``fit_bounds`` stretches the
result to exactly 20 x 13 x 12 mm - the profile only supplies the proportions.
"""

import math

from mathutils import Matrix

from prop_lib import fit_bounds, lathe

ID = 'grain_kernel'
NAME_ZH = '谷粒'
CATEGORY = 'food'
SOURCING = 'model ourselves：20 mm 椭球谷粒，一头略尖、一侧深沟 + 胚芽暗凹 + 浅色腹棱'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.020
TRI_BUDGET = 80
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.020', 'game_longest_dim_m': '0.020'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'straw': ((0.470, 0.360, 0.140), 0.90),
    'paper': ((0.800, 0.775, 0.700), 0.90),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('straw', 'paper', 'wood_dark')
MATERIALS = [
    {'key': 'straw', 'name_zh': '谷粒麦黄', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'paper', 'name_zh': '浅色胚乳棱', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '胚芽暗凹', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('10-segment, 6-point lathe: 80 triangles, one closed shell, three '
         'materials.  The profile is asymmetric in length: full width by 12% of '
         'the length, widest at 42%, then a 58%-long taper to the point.  Groove '
         '= the two vertex columns at 72/108 degrees pulled in 24%, not a cut; '
         'the column at 0-36 degrees is lifted 14% into a narrow pale starchy '
         'crest (paper slot).  The blunt pole is pushed 7.5% of the body length '
         'into the kernel, so the end fan becomes a concave germ dimple painted '
         'wood_dark, and a 180-degree turn about Y puts that germ and the '
         'crest on the side the shared camera sees.  The underside column is '
         'raised to 80% of the radius for a small resting flat.  fit_bounds '
         'pins 0.020/0.013/0.012 m, so finish() does not rescale.')

SIZE = (0.020, 0.013, 0.012)
SEGMENTS = 10
# (radius, z) bottom to top of the revolved body, before the 90-degree turn.
PROFILE = [
    (0.0000, 0.0000),   # blunt end pole
    (0.4600, 0.1200),   # full width almost at once
    (0.5000, 0.4200),   # widest, 42% of the length
    (0.3600, 0.6600),   # extra ring: gives the crest band a column to bend on
    (0.2600, 0.8200),   # long taper
    (0.0000, 1.0000),   # point
]
GROOVE_COLUMNS = (2, 3)     # 72 and 108 degrees: the flank facing the camera
GROOVE_DEPTH = 0.24
CREST_COLUMNS = (0,)        # 0-36 degrees: the narrow crest beside the groove
CREST_LIFT = 0.14
GERM_DEPRESSION = 0.075     # of the body length, pushed into the blunt pole
FLOOR_FRACTION = 0.80
TAU = math.pi * 2.0


def _section_angle(centre):
    """Angle around the long axis (X) after the 90-degree turn.

    The lathe builds (r cos t, r sin t, z) and the turn maps it to
    (z, r sin t, -r cos t), so the section angle is recovered from y and -z.
    """
    return math.atan2(centre.y, -centre.z) % TAU


def _in_columns(angle, columns):
    """True when the section angle falls in one of the named 36-degree columns."""
    for column in columns:
        low = TAU * column / SEGMENTS - TAU / (2 * SEGMENTS)
        high = TAU * (column + 1) / SEGMENTS - TAU / (2 * SEGMENTS)
        if low <= angle < high:
            return True
    return False


def build(b):
    def radius_fn(point, column, radius):
        if point in (0, len(PROFILE) - 1):
            return radius
        if column in GROOVE_COLUMNS:
            return radius * (1.0 - GROOVE_DEPTH)
        if column in CREST_COLUMNS:
            return radius * (1.0 + CREST_LIFT)
        return radius

    bm = lathe(PROFILE, segments=SEGMENTS, radius_fn=radius_fn)
    turn = Matrix.Rotation(math.radians(90.0), 4, 'Y')
    for v in bm.verts:
        v.co = turn @ v.co
    # Germ dimple: push the blunt pole into the kernel.  The pole is the only
    # vertex left at x = 0 after the turn, so the ten-triangle end fan becomes a
    # concave cone without touching the topology.
    for v in bm.verts:
        if v.co.x < 1e-9:
            v.co.x += GERM_DEPRESSION

    # Material split on the authoring frame (axis through the origin, before
    # fit_bounds): face order survives the fit, so labels are applied after it.
    labels = []
    for face in bm.faces:
        centre = face.calc_center_median()
        if centre.x < GERM_DEPRESSION + 0.055:
            labels.append(2)                     # germ dimple fan and its collar
        elif _in_columns(_section_angle(centre), CREST_COLUMNS):
            labels.append(1)                     # pale starchy crest
        else:
            labels.append(0)                     # wheat-yellow body

    # Turn the blunt end towards the preview camera: a 180-degree turn about Y
    # swaps x and z, so the germ scar ends up on the +X side the shared camera
    # looks from, and the crest (pale) rotates onto the top of the section.
    flip = Matrix.Rotation(math.pi, 4, 'Y')
    for v in bm.verts:
        v.co = flip @ v.co
    low = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z = max(v.co.z, low * FLOOR_FRACTION)

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['straw']
    me = b.add(bm, 'straw', rgb, rough, smooth=False, name='grain').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
