"""Sheep bell - 90 mm flared bell with a leather bail welded into its crown.

The silhouette is what makes a bell read, and the first version did not have
one: its widest ring sat 14 mm up the wall with a narrower mouth face below it,
so from the shared 26-degree camera the prop was a smooth dome with a handle - a
pudding, not a bell.  The profile now puts the widest ring at the lip, with the
mouth face below it: the bottom of the silhouette flares out like a trumpet
skirt, and the wall above it only ever narrows.

The mouth is a real cup (2026-11-15 rework).  The earlier version closed the
bottom with a flat disc, which was acceptable only because the preview looks at
the bell from above; the brief now asks for a dark mouth interior, and a cup is
also what the object is.  The profile starts at the cavity ceiling, runs down the
inner wall to the inner rim, crosses the 4.8 mm mouth annulus, climbs the outer
lip and continues up the outside - still one closed shell, and the union with the
bail is unaffected because the bail's legs stop at z=39.5 mm, above the 22.5 mm
cavity ceiling.

Materials are four palette slots, because one "old bronze" colour left the bell
as a brown lump (owner feedback): ``brass`` bell body, ``metal`` for the bright
mouth rim and lip band, ``metal_dark`` for the inside of the cup, and
``wood_dark`` for the leather bail.  The bail is re-assigned by distance to its
own centreline after the boolean, since the modifier hands the tool's material to
the target's first slot.
"""

import math

from mathutils import Vector

from prop_lib import fit_bounds, lathe, tube

ID = 'sheep_bell'
NAME_ZH = '羊铃'
CATEGORY = 'nature'
SOURCING = 'model ourselves：90 mm 外张铃身（黄铜）+ 明色口沿 + 暗色铃腔 + 深色皮带'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.090
TRI_BUDGET = 300
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.090', 'game_longest_dim_m': '0.090'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'brass': ((0.520, 0.380, 0.120), 0.30),
    'metal': ((0.430, 0.450, 0.470), 0.32),
    'metal_dark': ((0.090, 0.095, 0.105), 0.55),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('brass', 'metal', 'metal_dark', 'wood_dark')
MATERIALS = [
    {'key': 'brass', 'name_zh': '黄铜铃身', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'metal', 'name_zh': '口沿亮金属', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'metal_dark', 'name_zh': '铃腔暗金属', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
    {'key': 'wood_dark', 'name_zh': '皮带深褐', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('10-segment, 12-point lathe (200 triangles) plus a 6-station, '
         '5-segment flat strap (60 triangles); 288 after the union, one closed '
         'shell.  Hollow cup: cavity ceiling at z=22.5 mm, inner rim at '
         'r=40 mm, 4.8 mm mouth annulus, outer lip at r=44.8 mm (the widest '
         'ring), then the wall narrows monotonically to the 52 mm crown.  '
         'Strap: 12 mm wide x 6.4 mm thick, legs at x=+-9.5 mm entering '
         'z=39.5 mm inside the crown, flat top at z=60.5 mm.  Materials are '
         'assigned after the union, in authoring space (inverse fit matrix): '
         'bail centreline distance < 8.5 mm = wood_dark, r<40.2 mm and '
         'z<23 mm = metal_dark (cavity), z<4.5 mm = metal (mouth rim and lip '
         'band), the rest brass.')

SIZE = (0.090, 0.070, 0.070)
SEGMENTS = 10
PROFILE = [
    (0.0000, 0.0225),   # cavity ceiling centre
    (0.0200, 0.0220),   # cavity ceiling
    (0.0330, 0.0150),   # inner wall
    (0.0395, 0.0055),   # inner wall down to the inner rim
    (0.0400, 0.0000),   # inner rim, the lowest inner point
    (0.0448, 0.0000),   # mouth annulus: the widest ring, on the ground
    (0.0448, 0.0055),   # outer lip, straight up
    (0.0420, 0.0130),   # outer wall
    (0.0375, 0.0250),   # wall narrows monotonically from here up
    (0.0315, 0.0360),   # shoulder
    (0.0230, 0.0450),   # crown
    (0.0000, 0.0520),   # top pole
]
STRAP_POINTS = [
    (-0.0095, 0.0, 0.0395),
    (-0.0132, 0.0, 0.0505),
    (-0.0100, 0.0, 0.0605),
    (0.0100, 0.0, 0.0605),
    (0.0132, 0.0, 0.0505),
    (0.0095, 0.0, 0.0395),
]
STRAP_HALF_WIDTH = 0.0060     # tangential (Y): the 12 mm face of the strap
STRAP_HALF_THICK = 0.0032     # radial: how far the strap stands off the crown
STRAP_SEGMENTS = 5
LEATHER_RADIUS = 0.0085
CAVITY_RADIUS = 0.0402
CAVITY_TOP_Z = 0.0230
RIM_BAND_Z = 0.0045


def _strap_section(index, angle, radius):
    a = STRAP_HALF_WIDTH
    b = STRAP_HALF_THICK
    c, s = math.cos(angle), math.sin(angle)
    return a * b / math.sqrt((b * c) ** 2 + (a * s) ** 2)


def _segment_distance(point, start, end):
    span = end - start
    length2 = span.length_squared
    if length2 <= 1e-18:
        return (point - start).length
    t = max(0.0, min(1.0, (point - start).dot(span) / length2))
    return (point - (start + span * t)).length


def _strap_distance(point, path):
    return min(_segment_distance(point, path[i], path[i + 1])
               for i in range(len(path) - 1))


def build(b):
    bell_bm = lathe(PROFILE, segments=SEGMENTS)
    path = [Vector(p) for p in STRAP_POINTS]
    strap_bm = tube(path, [1.0] * len(path), segments=STRAP_SEGMENTS,
                    section_fn=_strap_section)
    # Fit both parts together, before either is baked into an object, so the
    # strap's centrelines can be mapped into the fitted frame for the split.
    matrix = fit_bounds([bell_bm, strap_bm], SIZE)
    strap_path = [matrix @ p for p in path]
    inverse = matrix.inverted()

    bell = b.add(bell_bm, 'brass', PALETTE['brass'][0], PALETTE['brass'][1],
                 smooth=False, name='bell')
    strap = b.add(strap_bm, 'wood_dark', PALETTE['wood_dark'][0],
                  PALETTE['wood_dark'][1], smooth=False, name='strap')
    b.boolean_union(bell, [strap])

    me = bell.data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for poly in me.polygons:
        centre = poly.center
        if _strap_distance(centre, strap_path) < LEATHER_RADIUS:
            poly.material_index = 3
            continue
        # The fit squashes y (90 x 70 mm plan), so the radial tests run in the
        # authoring frame where the bell is still round.
        auth = inverse @ centre
        radius = math.hypot(auth.x, auth.y)
        if radius < CAVITY_RADIUS and auth.z < CAVITY_TOP_Z:
            poly.material_index = 2          # inside of the cup
        elif auth.z < RIM_BAND_Z:
            poly.material_index = 1          # bright mouth rim and lip band
        else:
            poly.material_index = 0          # brass body
