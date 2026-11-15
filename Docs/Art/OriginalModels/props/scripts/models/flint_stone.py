"""Flint stone - 70 mm knapped shard: a lopsided wedge, pale rind, dark fracture.

The one genuinely angular prop in the round set, so it is built from a cube
rather than a sphere.  The first version was a clean cube with a chamfer and a
taper, and it previewed as a machined brick: eight corners at exact right angles
are the strongest possible signal of "manufactured".  Every corner now carries
its own fixed 2-5 mm offset from a table, so no two edges are parallel and the
silhouette is irregular while the shape stays blocky.

Knapping (2026-11-15 rework): a single bevel gave 26 flat faces, which read as a
faceted brick rather than a struck stone.  Every face is now poked and its new
centre pushed along the face normal by a fixed sine field, so each plane breaks
into a shallow conchoidal scar with its own angle - that is what flint knapping
actually leaves, and it costs 96 triangles out of a 180 budget.  The low
amplitude sine displacement then takes the last 1.5 mm of machine-trueness off
the scars.

Materials are three palette slots, and the split follows the struck planes
rather than an arbitrary height band: the top plane (and the bevel facets that
share its orientation) is the fresh dark fracture surface (``flint``), every
other plane keeps the pale weathered cortex (``stone``), and the point end plus
the resting base take the ``wood_dark`` slot as an iron-stained broken corner.
Because the split is keyed on each poked face's *parent plane*, the two colours
meet along the knapped edges, not along a straight line.
"""

import math

import bmesh
from mathutils import Vector

from prop_lib import fit_bounds

ID = 'flint_stone'
NAME_ZH = '燧石'
CATEGORY = 'nature'
SOURCING = 'model ourselves：70 mm 打制燧石楔块（打制疤痕棱面 + 皮层/断口分色）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.070
TRI_BUDGET = 180
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.070', 'game_longest_dim_m': '0.070'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'stone': ((0.330, 0.330, 0.320), 0.92),
    'flint': ((0.155, 0.160, 0.175), 0.70),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('flint', 'stone', 'wood_dark')
MATERIALS = [
    {'key': 'flint', 'name_zh': '燧石新鲜断口', 'linear_rgb': [0.155, 0.160, 0.175],
     'roughness': 0.70},
    {'key': 'stone', 'name_zh': '风化皮层', 'linear_rgb': [0.330, 0.330, 0.320],
     'roughness': 0.92},
    {'key': 'wood_dark', 'name_zh': '铁染裂隙', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('Cube -> eight jittered corners -> wedge taper -> one-segment bevel '
         '(offset 8 mm) -> poke every face and lift its centre along the normal '
         '-> 1.5 mm sine displacement: 96 triangles, one closed shell, no '
         'boolean.  Taper narrows the point end to 60% in y and 46% in z and '
         'lifts it 4.5 mm.  Corner offsets are a fixed table (2-5 mm) so no two '
         'edges are parallel.  Materials follow the struck planes, not a height '
         'band: the top plane and the bevel facets that share its '
         'orientation (parent normal n.z > 0.35) keep the pale weathered '
         'cortex, the front/side struck planes are the fresh dark flint, and the '
         'point end (n.x > 0.85) plus the resting base (n.z < -0.85) are the '
         'dark iron-stained slot.  fit_bounds pins 70 x 55 x 45 mm.')

SIZE = (0.070, 0.055, 0.045)
BEVEL = 0.0080
NOISE = 0.0015
FLOOR_FRACTION = 0.72
# Which struck plane gets which colour, decided from the parent plane's normal.
# The palest plane is the one facing the key light, so the weathered cortex sits
# on TOP (n.z > 0.35) and the fresh dark flint on the front/side planes: putting
# the dark tone under the key and the pale tone on the shaded side would have
# cancelled the 2:1 albedo difference against the 3.2:1 key/fill ratio of the
# shared preview rig.  The point end (n.x > 0.85) and the resting base are the
# dark iron-stained slot.
SCAR_TOP = 0.35          # n.z above this = struck top plane (and its bevels)
SCAR_TIP = 0.85          # n.x above this = the point-end face
SCAR_BASE = 0.85         # n.z below -this = the resting base
SCAR_LIFT = 0.0022          # how far a poked face centre is pushed out
# (sign x, sign y, sign z) of the unit cube corner -> metre offsets.
CORNER_JITTER = {
    (-1, -1, -1): (-0.0035, 0.0025, 0.0005),
    (-1, -1, 1): (-0.0025, -0.0030, 0.0035),
    (-1, 1, -1): (0.0030, 0.0015, -0.0010),
    (-1, 1, 1): (-0.0040, 0.0020, 0.0030),
    (1, -1, -1): (0.0035, -0.0020, 0.0015),
    (1, -1, 1): (0.0025, 0.0030, 0.0020),
    (1, 1, -1): (-0.0020, -0.0035, -0.0025),
    (1, 1, 1): (0.0040, -0.0015, 0.0045),
}


def _corner_key(vert):
    return (1 if vert.co.x > 0 else -1,
            1 if vert.co.y > 0 else -1,
            1 if vert.co.z > 0 else -1)


def build(b):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        key = _corner_key(v)
        jitter = CORNER_JITTER[key]
        t = v.co.x + 0.5                    # 0 at the butt, 1 at the point
        v.co.x = v.co.x * SIZE[0] + jitter[0]
        v.co.y = v.co.y * SIZE[1] * (1.0 - 0.40 * t) + jitter[1]
        v.co.z = (v.co.z * SIZE[2] * (1.0 - 0.54 * t)
                  + 0.09 * SIZE[2] * t + jitter[2])
    bmesh.ops.bevel(bm, geom=list(bm.edges), offset=BEVEL,
                    offset_type='OFFSET', segments=1, profile=0.5,
                    affect='EDGES')
    # One conchoidal scar per plane: poke every face, then lift its new centre
    # along the face normal by a fixed sine field so no two scars are parallel.
    # The parent plane of every child is remembered (centre + normal) so the
    # material split can follow the struck planes rather than a height band.
    bm.normal_update()
    parents = [(f.calc_center_median().copy(), f.normal.copy())
               for f in bm.faces]
    poked = bmesh.ops.poke(bm, faces=bm.faces[:])
    # Which struck plane each new triangle came from, resolved now while the
    # children still sit on their parent face (later the mesh is displaced and
    # fitted, so a nearest-centre search would be meaningless).
    bm.faces.ensure_lookup_table()
    parent_of = []
    for face in bm.faces:
        here = face.calc_center_median()
        parent_of.append(min(range(len(parents)),
                             key=lambda i: (here - parents[i][0]).length_squared))
    for vert in poked['verts']:
        centre = vert.co
        lift = SCAR_LIFT * (0.6 + 0.4 * math.sin(41.0 * centre.x + 0.9)
                            * math.cos(37.0 * centre.y + 1.7))
        normal = Vector((0.0, 0.0, 0.0))
        for face in vert.link_faces:
            normal += face.normal
        if normal.length > 1e-9:
            vert.co += normal.normalized() * lift
    for v in bm.verts:
        v.co.x += NOISE * math.sin(90.0 * v.co.x + 1.3) * math.cos(70.0 * v.co.y)
        v.co.y += NOISE * math.sin(80.0 * v.co.y + 0.7) * math.cos(60.0 * v.co.z)
        v.co.z += NOISE * math.sin(75.0 * v.co.z + 2.1) * math.cos(65.0 * v.co.x)
    low = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z = max(v.co.z, low * FLOOR_FRACTION)
    fit_bounds([bm], SIZE)
    bm.normal_update()

    labels = []
    for index, face in enumerate(bm.faces):
        parent_normal = parents[parent_of[index]][1]
        if parent_normal.z < -SCAR_BASE or parent_normal.x > SCAR_TIP:
            labels.append(2)                     # iron-stained broken tip / base
        elif parent_normal.z > SCAR_TOP:
            labels.append(1)                     # weathered pale cortex on top
        else:
            labels.append(0)                     # fresh dark flint struck face

    rgb, rough = PALETTE['flint']
    me = b.add(bm, 'flint', rgb, rough, smooth=False, name='flint').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
