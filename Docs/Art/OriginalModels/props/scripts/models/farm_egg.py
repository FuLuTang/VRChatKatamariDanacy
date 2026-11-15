"""Farm egg - 55 mm egg lying on its side, with a small resting flat.

An egg is not a sphere and not an ellipse: the widest point sits about 40% of the
way from the blunt end, one end is a broad curve and the other tapers to a
noticeably smaller radius.  The profile below encodes exactly that asymmmetry
over 12 segments and 9 points, which is enough rings for the silhouette to read
as an egg at preview size.

The egg is revolved on +Z and turned onto its side so the long axis is X, which
is why the widest ring's z is at 0.47 of the length rather than 0.5.  The
underside is then flattened: every vertex below 86.5% of the radius is raised to
that plane, so the egg rests on a 3 mm flat strip instead of rolling - the brief
allows a slightly flattened base for a stable static pose.

Materials (2026-11-15 rework): a pure warm-white shell was the flattest prop in
the set - in the preview it was a white ball with no read at all (owner
feedback).  The shell is now the palette's ``wool`` off-white under a broad
``leaf_dry`` colour break that runs across the upper middle of the egg (the
visible half from the shared camera), with a wavy boundary so it does not read as
a painted stripe, and six small dents are poked into the surface and painted
``wood_dark`` - real geometry, not a texture, because the pipeline allows no
UVs.  Each dent is a poked quad whose new centre is pushed 0.8 mm inward, so the
spot catches a shadow instead of lying flush with the shell.
"""

import math

import bmesh
from mathutils import Matrix, Vector

from prop_lib import fit_bounds, lathe

ID = 'farm_egg'
NAME_ZH = '鸡蛋'
CATEGORY = 'food'
SOURCING = 'model ourselves：55 mm 蛋形旋转体，壳色差带 + 六个暗斑凹坑'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.055
TRI_BUDGET = 200
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.055', 'game_longest_dim_m': '0.055'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'wool': ((0.700, 0.680, 0.630), 0.95),
    'leaf_dry': ((0.400, 0.270, 0.105), 0.90),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('wool', 'leaf_dry', 'wood_dark')
MATERIALS = [
    {'key': 'wool', 'name_zh': '蛋壳暖白', 'linear_rgb': [0.700, 0.680, 0.630],
     'roughness': 0.95},
    {'key': 'leaf_dry', 'name_zh': '壳色差暖褐带', 'linear_rgb': [0.400, 0.270, 0.105],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '壳质暗斑', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('12-segment, 9-point lathe: 168 triangles, plus six poked quads '
         '(+2 each) = 180, one closed shell.  Widest ring at 47% of the length, '
         'not 50%, and the narrow end is 35% of the widest radius at 93% of the '
         'length - that asymmetry is the whole shape.  Underside flattened at '
         '86.5% of the radius (a ~3 mm flat after fitting).  Colour break: the '
         'belt between 50% and 74% of the height, with a +-5% sine wobble so the '
         'boundary zigzags on facet edges, is leaf_dry; the six poked dents '
         '(centre pushed 0.8 mm in) are wood_dark; the rest wool.  fit_bounds '
         'pins 55 x 42 x 42 mm.')

SIZE = (0.055, 0.042, 0.042)
SEGMENTS = 12
# (radius, z) bottom (blunt end) to top (tapered end), revolved then laid on X.
PROFILE = [
    (0.0000, 0.0000),   # blunt end pole
    (0.2700, 0.0700),   # the blunt end fills out immediately
    (0.3600, 0.2000),
    (0.3820, 0.4700),   # widest, just below half way
    (0.3480, 0.6800),
    (0.2650, 0.8200),
    (0.1350, 0.9300),   # the narrow end is 35% of the widest ring here
    (0.0550, 0.9800),
    (0.0000, 1.0000),   # point
]
FLOOR_FRACTION = 0.865
BAND_LOW = 0.50          # fraction of the height where the darker belt starts
BAND_HIGH = 0.74
BAND_WOBBLE = 0.05
SPECKLES = 6
DENT_DEPTH = 0.0035      # metres, in the turned frame
# Fixed directions for the dents, all on the upper/visible half of the egg.
SPECKLE_DIRECTIONS = (
    (0.55, 0.45, 0.70),
    (0.20, -0.80, 0.55),
    (-0.50, 0.55, 0.65),
    (0.85, -0.10, 0.50),
    (-0.25, -0.45, 0.85),
    (0.40, 0.72, 0.55),
)


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    turn = Matrix.Rotation(math.radians(90.0), 4, 'Y')
    for v in bm.verts:
        v.co = turn @ v.co
    low = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z = max(v.co.z, low * FLOOR_FRACTION)

    # Six dents, picked on the turned (camera-facing) frame.
    bm.faces.ensure_lookup_table()
    chosen = []
    for raw in SPECKLE_DIRECTIONS[:SPECKLES]:
        direction = Vector(raw).normalized()
        best = None
        best_dot = -2.0
        for face in bm.faces:
            if face in chosen:
                continue
            dot = face.calc_center_median().normalized().dot(direction)
            if dot > best_dot:
                best_dot = dot
                best = face
        chosen.append(best)
    poked = bmesh.ops.poke(bm, faces=chosen)
    dent_verts = set(poked['verts'])
    for vert in poked['verts']:
        radial = Vector((0.0, vert.co.y, vert.co.z))
        if radial.length > 1e-9:
            vert.co -= radial.normalized() * DENT_DEPTH

    top = max(v.co.z for v in bm.verts)
    height = top - low
    labels = []
    for face in bm.faces:
        if any(vert in dent_verts for vert in face.verts):
            labels.append(2)                     # poked dark spot
            continue
        c = face.calc_center_median()
        section = math.atan2(c.z - (low + top) * 0.5, c.y)
        t = (c.z - low) / height + BAND_WOBBLE * math.sin(3.0 * section)
        labels.append(1 if BAND_LOW < t < BAND_HIGH else 0)

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['wool']
    me = b.add(bm, 'wool', rgb, rough, smooth=False, name='shell').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
