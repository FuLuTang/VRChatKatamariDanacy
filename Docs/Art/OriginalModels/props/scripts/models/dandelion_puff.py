"""Dandelion puff - 70 mm seed head: a dark core, twenty thin rays, a straw stem.

The cheapest way to get real radiating spikes on one watertight shell is to grow
them out of the core's own faces.  Twenty faces of a subdivided icosphere are
picked (one per direction of a dodecahedron's twenty vertices, greedily taking
the nearest unused face, so the selection is deterministic and evenly spread),
then extruded radially: the extruded triangle stays as the tip and the three
side quads become the tapered stalk.  Nothing is a separate closed shell, so the
result is one manifold surface with no boolean and no buried caps.

The first version scaled the tips to 55%, which at 70 mm made every "pappus" a
4 mm chip and the head read as a spiky ball (owner feedback, 2026-11-15).  The
tips are now 20% and the rays are longer (1.30 core radii), so the silhouette is
filaments rather than shards, and the eye goes to the gaps between them.

Materials are three palette slots, and the middle one is new: the core ball is
``chitin``, so the spaces between the thin rays read as a dark seed head instead
of pale fuzz on pale fuzz; the rays are ``wool`` (the palette's warm off-white);
and the stalk is ``straw``.  The stalk is real geometry, grown by extruding the
core's own bottom face twice - a separate tube would have been a second shell,
which the closed-shell contract forbids.
"""

import math

import bmesh
from mathutils import Vector

from prop_lib import fit_bounds, icosphere

ID = 'dandelion_puff'
NAME_ZH = '蒲公英绒球'
CATEGORY = 'nature'
SOURCING = 'model ourselves：70 mm 绒球，暗色花托 + 20 根细冠毛 + 干草色茎（同一闭合壳）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.070
TRI_BUDGET = 280
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.070', 'game_longest_dim_m': '0.070'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'wool': ((0.700, 0.680, 0.630), 0.95),
    'chitin': ((0.090, 0.075, 0.060), 0.30),
    'straw': ((0.470, 0.360, 0.140), 0.90),
}
SLOTS = ('wool', 'chitin', 'straw')
MATERIALS = [
    {'key': 'wool', 'name_zh': '冠毛米白', 'linear_rgb': [0.700, 0.680, 0.630],
     'roughness': 0.95},
    {'key': 'chitin', 'name_zh': '花托暗芯', 'linear_rgb': [0.090, 0.075, 0.060],
     'roughness': 0.30},
    {'key': 'straw', 'name_zh': '干草色茎', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
]
NOTES = ('Subdivision-2 icosphere (80 triangles) with 20 faces extruded into '
         'rays and the bottom face extruded twice into a stalk: 216 triangles, '
         'one closed shell, no boolean.  Ray tips are scaled to 0.20 and pushed '
         '0.95 core radii outward, so the pappus reads as filaments.  Stalk: two '
         'extrude steps down (0.42 and 0.42 core radii, taper 0.55).  Materials '
         'by face centre: within 1.03 core radii of the centre = chitin (dark '
         'seed head), below -1.02 = straw (stalk), the rays = wool.  fit_bounds '
         'pins 70 x 70 x 65 mm.')

SIZE = (0.070, 0.070, 0.065)
CORE_RADIUS = 1.0
RAY_LENGTH = 1.30
RAY_TIP_SCALE = 0.20
STEM_STEPS = 2
STEM_STEP = 0.42
STEM_TAPER = 0.55
CORE_LABEL_RADIUS = 1.03
STEM_LABEL_Z = -1.02
PHI = 1.6180339887
INV_PHI = 1.0 / PHI


def ray_directions():
    """Twenty evenly spread directions: the vertices of a dodecahedron."""
    dirs = []
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            for sz in (-1.0, 1.0):
                dirs.append(Vector((sx, sy, sz)))
    for sy in (-1.0, 1.0):
        for sz in (-1.0, 1.0):
            dirs.append(Vector((0.0, sy * INV_PHI, sz * PHI)))
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            dirs.append(Vector((sx * INV_PHI, sy * PHI, 0.0)))
    for sx in (-1.0, 1.0):
        for sz in (-1.0, 1.0):
            dirs.append(Vector((sx * PHI, 0.0, sz * INV_PHI)))
    return [d.normalized() for d in dirs]


def _extrude_stem(bm):
    """Grow the stalk out of the core's own bottom face, twice.

    A separate tube would be a second closed shell; extruding the bottom face of
    the core keeps one watertight surface and costs 14 triangles.
    """
    bm.faces.ensure_lookup_table()
    down = Vector((0.0, 0.0, -1.0))
    best = None
    best_dot = -2.0
    for face in bm.faces:
        dot = face.calc_center_median().normalized().dot(down)
        if dot > best_dot:
            best_dot = dot
            best = face
    for _ in range(STEM_STEPS):
        grown = bmesh.ops.extrude_discrete_faces(bm, faces=[best])
        cap = grown['faces'][0]
        centre = cap.calc_center_median()
        for vert in cap.verts:
            vert.co = centre + (vert.co - centre) * STEM_TAPER
            vert.co.z -= STEM_STEP
        best = cap


def build(b):
    bm = icosphere(2, CORE_RADIUS)
    bm.faces.ensure_lookup_table()
    used = set()
    chosen = []
    for direction in ray_directions():
        best = None
        best_dot = -2.0
        for face in bm.faces:
            if face.index in used:
                continue
            dot = face.calc_center_median().normalized().dot(direction)
            if dot > best_dot:
                best_dot = dot
                best = face
        chosen.append(best)
        used.add(best.index)
    grown = bmesh.ops.extrude_discrete_faces(bm, faces=chosen)
    for cap in grown['faces']:
        centre = cap.calc_center_median()
        outward = centre.normalized()
        for vert in cap.verts:
            vert.co = (centre + (vert.co - centre) * RAY_TIP_SCALE
                       + outward * RAY_LENGTH)

    _extrude_stem(bm)

    labels = []
    for face in bm.faces:
        centre = face.calc_center_median()
        if centre.z < STEM_LABEL_Z:
            labels.append(2)                     # straw stalk
        elif centre.length < CORE_LABEL_RADIUS:
            labels.append(1)                     # dark seed-head core
        else:
            labels.append(0)                     # pale pappus

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['wool']
    me = b.add(bm, 'wool', rgb, rough, smooth=False, name='puff').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
