"""Smooth pebble - 50 mm water-worn sandstone pebble with a chipped facet.

Deliberately the round, soft counterpart of ``pebble`` (the rough 60 mm garden
stone): a subdivided icosphere (80 triangles) displaced by a gentle three-term
sine field, so the silhouette is a smooth oval with no crease, then flattened on
the underside so it lies on a facet instead of balancing on a point.

The displacement is small on purpose (about +-6% of the radius).  Anything
stronger reads as a lumpy potato, which is what ``pebble`` already does; a
stream-worn pebble is defined by *not* having features.

Materials (2026-11-15 rework): the single cool grey slot was the flattest prop in
the set - at preview size it was a grey ball with no read at all (owner
feedback).  Three palette slots now do the work, and two of them are geometry,
not just paint:

* ``shell`` - the warm sandstone body, the largest area.
* ``flint`` - a worn chip on the camera-facing side: the cap of faces inside
  ``CHIP_DIRECTION`` is clamped onto one plane, so that part of the pebble is a
  real flat facet with one normal, catching the key light as a single tone.
* ``wood_dark`` - a crack: every vertex within 20% of ``CRACK_NORMAL``'s plane is
  pulled inward, which pinches a groove across the stone, and the faces on the
  groove floor take the dark slot.

No extra triangles: the chip and the crack move existing vertices, so the build
stays at 80 triangles against the 140 budget.
"""

import math

from mathutils import Vector

from prop_lib import fit_bounds, icosphere

ID = 'smooth_pebble'
NAME_ZH = '卵石'
CATEGORY = 'nature'
SOURCING = 'model ourselves：50 mm 磨圆砂岩卵石（磨平面 + 裂纹凹槽）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.050
TRI_BUDGET = 140
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.050', 'game_longest_dim_m': '0.050'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'shell': ((0.560, 0.470, 0.330), 0.42),
    'flint': ((0.155, 0.160, 0.175), 0.70),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('shell', 'flint', 'wood_dark')
MATERIALS = [
    {'key': 'shell', 'name_zh': '暖砂岩卵石', 'linear_rgb': [0.560, 0.470, 0.330],
     'roughness': 0.42},
    {'key': 'flint', 'name_zh': '磨平断面', 'linear_rgb': [0.155, 0.160, 0.175],
     'roughness': 0.70},
    {'key': 'wood_dark', 'name_zh': '裂纹暗线', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('Subdivided icosphere (80 triangles, 42 vertices) with a deterministic '
         'sine displacement of +-6%, underside flattened only at 93% of the '
         'radius so it still reads as a water-worn pebble; fit_bounds pins '
         '50 x 42 x 35 mm.  One closed shell, no boolean, no extra triangles.  '
         'Chip: every face whose centre points within the CHIP_DIRECTION cap is '
         'collected and all of its vertices are clamped onto one plane 0.020 '
         'radii below the cap peak, so the cap becomes a single flat facet '
         '(flint slot).  Crack: every vertex within 0.20 of the CRACK_NORMAL plane is '
         'pulled inward by up to 9%, and the faces within 0.16 of it take the '
         'wood_dark slot.')

SIZE = (0.050, 0.042, 0.035)
FLOOR_FRACTION = 0.93
CHIP_DIRECTION = (0.42, 0.70, 0.50)
CHIP_CONE = 0.72         # face centres within this dot of CHIP_DIRECTION form the chip
CHIP_DEPTH = 0.020
CRACK_NORMAL = (0.78, -0.32, 0.54)
CRACK_WIDTH = 0.20       # distance from the crack plane, in radii
CRACK_DEPTH = 0.09
CRACK_LABEL = 0.16


def build(b):
    bm = icosphere(2, 1.0)
    for v in bm.verts:
        p = v.co.normalized()
        r = (1.0
             + 0.050 * math.sin(2.4 * p.x + 0.5) * math.cos(2.0 * p.y + 1.2)
             + 0.035 * math.sin(3.1 * p.z + 2.0))
        v.co = p * r

    chip = Vector(CHIP_DIRECTION).normalized()
    chip_faces = set()
    for face in bm.faces:
        if face.calc_center_median().normalized().dot(chip) > CHIP_CONE:
            chip_faces.add(face)
    chip_verts = {v for face in chip_faces for v in face.verts}
    peak = max(v.co.dot(chip) for v in chip_verts)
    plane = peak - CHIP_DEPTH
    for v in chip_verts:
        along = v.co.dot(chip)
        if along > plane:
            v.co -= chip * (along - plane)

    crack = Vector(CRACK_NORMAL).normalized()
    for v in bm.verts:
        offset = v.co.dot(crack)
        if abs(offset) < CRACK_WIDTH:
            depth = CRACK_DEPTH * (1.0 - abs(offset) / CRACK_WIDTH)
            v.co -= crack * math.copysign(depth, offset)

    low = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z = max(v.co.z, low * FLOOR_FRACTION)

    labels = []
    for face in bm.faces:
        centre = face.calc_center_median()
        if abs(centre.dot(crack)) < CRACK_LABEL:
            labels.append(2)                     # crack groove floor
        elif face in chip_faces:
            labels.append(1)                     # worn flat facet
        else:
            labels.append(0)

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['shell']
    me = b.add(bm, 'shell', rgb, rough, smooth=False, name='pebble').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
