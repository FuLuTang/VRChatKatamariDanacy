"""Acorn cap - the cup only: a 28 mm bowl with two staggered rows of scales.

No nut, only the cupule (the cap that falls off the acorn), because that is what
litters a meadow floor.  Three earlier versions modelled it as a solid dome and
all read as a lumpy brown boulder at 28 mm: a dome seen from above is a
featureless disk, and the scales had nowhere to show except as facets.  The shape
is now the cap's real geometry - a *bowl*: a shallow dish inside, a rim, and a
corrugated outer wall.  A closed solid bowl is still one closed shell (the
profile runs from the inner floor, out over the rim, down the outside and back to
the base centre), so no open surface and no second part.

Scales (2026-11-15 rework): the outer wall now has three rings instead of two, so
the column displacements form two staggered rows of five raised tiles rather than
one wavy band - ring A lifts the even columns, ring B the odd ones, ring C the
even ones again, with the columns between pulled in, so each row reads as a row
of overlapping scale plates.  The base ring stays round so the cup sits flat.

Materials are the other half of the rework: one flat bark colour left the cap as
a brown blob (owner feedback).  Three palette slots now carry the read - the pale
cut shell of the cupule (``shell``), the scale plates alternating between the
woody ``wood`` and the near-black ``wood_dark``, and the dark inner dish so the
bowl has a visible inside instead of a flat top.  The split is computed per face
from the radius and height of its centre, because the inner dish and the outer
wall span the same heights.
"""

import math

from prop_lib import fit_bounds, lathe

ID = 'acorn_cap'
NAME_ZH = '橡子壳斗'
CATEGORY = 'nature'
SOURCING = 'model ourselves：28 mm 壳斗碗 + 两排交错的鳞片凸台 + 暗色内腔'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.028
TRI_BUDGET = 140
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.028', 'game_longest_dim_m': '0.028'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'shell': ((0.560, 0.470, 0.330), 0.42),
    'wood': ((0.330, 0.205, 0.105), 0.72),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('shell', 'wood', 'wood_dark')
MATERIALS = [
    {'key': 'shell', 'name_zh': '壳斗浅褐', 'linear_rgb': [0.560, 0.470, 0.330],
     'roughness': 0.42},
    {'key': 'wood', 'name_zh': '鳞片木褐', 'linear_rgb': [0.330, 0.205, 0.105],
     'roughness': 0.72},
    {'key': 'wood_dark', 'name_zh': '鳞缝与内腔暗褐', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('10-segment, 9-point lathe: 140 triangles (the budget), one closed '
         'shell, no boolean.  The profile is a bowl, not a dome: inner floor at '
         'z=5 mm, inner rim at 11.5 mm, outer wall from 15 mm radius down to the '
         'base ring.  Scales are column displacements on rings A/B/C '
         '(10% / 12% / 8% out, 40% of that in on the columns between) with the '
         'raised columns staggered per row: two rows of five plates.  Materials '
         'by face centre: the inner dish (z>4 mm, r<12.2 mm) is wood_dark, the '
         'rim (z>10 mm) and the base skirt are shell, and the two outer-wall '
         'rows alternate wood / wood_dark per column.  The base ring stays round '
         'so the cup sits flat.')

SIZE = (0.028, 0.028, 0.012)
SEGMENTS = 10
# (radius, z): inner floor -> inner wall -> rim -> outer wall A/B/C -> base.
PROFILE = [
    (0.0000, 0.0050),   # inner floor centre
    (0.0096, 0.0052),   # inner floor
    (0.0124, 0.0086),   # inner wall up to the rim
    (0.0136, 0.0115),   # rim edge, the highest ring
    (0.0148, 0.0090),   # outer wall row A
    (0.0150, 0.0062),   # outer wall row B, the widest ring
    (0.0136, 0.0032),   # outer wall row C
    (0.0124, 0.0010),   # outer base
    (0.0000, 0.0000),   # base centre
]
# ring index -> (bump amplitude, raised-column parity); the rim and base stay round.
KNOBS = {4: (0.100, 0), 5: (0.120, 1), 6: (0.080, 0)}
RELIEF_RATIO = 0.40
# material split constants (metres, authoring frame before fit_bounds)
DISH_MAX_RADIUS = 0.0122
DISH_MIN_Z = 0.0040
RIM_MIN_Z = 0.0100
ROW0_MIN_Z = 0.0065
ROW1_MIN_Z = 0.0030
TAU = math.pi * 2.0


def _column(angle):
    """0..SEGMENTS-1 column index a point at ``angle`` sits in."""
    step = TAU / SEGMENTS
    return int(math.floor(((angle % TAU) + step * 0.5) / step)) % SEGMENTS


def build(b):
    def radius_fn(point, column, radius):
        amplitude, parity = KNOBS.get(point, (0.0, 0))
        if not amplitude:
            return radius
        if column % 2 == parity:
            return radius * (1.0 + amplitude)
        # The columns between two plates are pulled in, so the wall is tiled
        # rather than merely wavy.
        return radius * (1.0 - amplitude * RELIEF_RATIO)

    bm = lathe(PROFILE, segments=SEGMENTS, radius_fn=radius_fn)

    labels = []
    for face in bm.faces:
        c = face.calc_center_median()
        radius = math.hypot(c.x, c.y)
        if c.z > DISH_MIN_Z and radius < DISH_MAX_RADIUS:
            labels.append(2)                     # dark inner dish
        elif c.z > RIM_MIN_Z:
            labels.append(0)                     # pale cut rim
        elif c.z < ROW1_MIN_Z:
            labels.append(0)                     # base skirt, sits on the ground
        else:
            column = _column(math.atan2(c.y, c.x))
            row = 0 if c.z >= ROW0_MIN_Z else 1
            # Staggered rows: row 0 lights the even plates, row 1 the odd ones.
            light = (column % 2) == (0 if row == 0 else 1)
            labels.append(1 if light else 2)

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['shell']
    me = b.add(bm, 'shell', rgb, rough, smooth=False, name='cap').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
