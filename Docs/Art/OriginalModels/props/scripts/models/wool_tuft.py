"""Wool tuft - an 11 cm clump of fleece with two creases and straw in it.

Not a sphere with a little noise on it: a fleece is a heap of lumps with soft
valleys between them.  Two approaches were tried and rejected.

* Eleven fused metaball lobes (first version) merged into one smooth dome,
  because the balls overlapped so much that the necks swallowed every valley; it
  previewed as a meringue.  Making the field finer fixed the valleys but not the
  cost: Blender's metaball tessellation starts at a few thousand triangles at any
  resolution that resolves the lobes, and prop_lib's decimation has a hard 2%
  floor, so the collapse down to the 260-triangle budget shatters the surface
  into long thin slivers - the second version looked like crumpled paper.
* A subdivided icosphere is cheap and deterministic but only carries ~12 lumps
  at the vertex scale, and at 110 mm its facets read as a faceted rock.

So the clump is a 14 x 8 UV sphere (196 triangles) with a three-term sine field
pushed through it.  The field's spatial frequency (about 3 radians per unit
radius) lays roughly ten large lumps plus a few secondary ones over the surface,
which is the "10-14 big undulations" the brief asks for, and because every vertex
moves smoothly there are no slivers or creases.

2026-11-15 rework: one grey-white slot made this the flattest prop in the set
(owner feedback).  The fleece now carries three palette slots - ``wool`` for the
body, ``straw`` for chaff caught in the fibres, and ``wood_dark`` for the deep
creases and the pressed underside - plus two carved creases: the radius is
pinched 9% along two fixed great circles, which gives the clump the "2-4 shallow
grooves" the spec asks a soft-body prop for.  All of it moves existing vertices,
so the triangle count and the 196-triangle read do not change.
"""

import math

from mathutils import Vector

from prop_lib import fit_bounds, uv_sphere

ID = 'wool_tuft'
NAME_ZH = '羊毛团'
CATEGORY = 'nature'
SOURCING = 'model ourselves：110 mm 不规则羊毛团（14x8 球面 + 正弦起伏 + 两道压痕 + 草屑）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.110
TRI_BUDGET = 260
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.110', 'game_longest_dim_m': '0.110'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'wool': ((0.700, 0.680, 0.630), 0.95),
    'straw': ((0.470, 0.360, 0.140), 0.90),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('wool', 'straw', 'wood_dark')
MATERIALS = [
    {'key': 'wool', 'name_zh': '脏白羊毛', 'linear_rgb': [0.700, 0.680, 0.630],
     'roughness': 0.95},
    {'key': 'straw', 'name_zh': '毛间草屑', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '毛缝与贴地暗面', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('14-segment, 8-ring UV sphere: 196 triangles, one closed shell.  Three '
         'sine terms of amplitude 0.16 / 0.14 / 0.11 at spatial frequency ~3 '
         'radians per unit radius give about ten large lumps plus secondary '
         'ones.  Two creases pinch the radius 9% along fixed great circles '
         '(normal (0.82, 0.44, 0.37) and (-0.35, 0.86, 0.37)).  Underside '
         'flattened by 10% of the depth for a resting flat, then fit_bounds pins '
         '110 x 95 x 85 mm.  Materials by face centre: the darkest creases '
         '(radius < 0.80) and the pressed underside are wood_dark, a fixed '
         'sine-patch pattern is straw (chaff), the rest wool.')

SIZE = (0.110, 0.095, 0.085)
SEGMENTS = 14
RINGS = 8
FLOOR_FRACTION = 0.90
CREASE_NORMALS = ((0.82, 0.44, 0.37), (-0.35, 0.86, 0.37))
CREASE_WIDTH = 0.26
CREASE_DEPTH = 0.09
VALLEY_RADIUS = 0.80       # faces closer than this to the centre are deep creases
CHIFF_PATCH = 0.72         # threshold of the deterministic straw-patch pattern
UNDERSIDE = 0.12           # fraction of the height treated as the pressed base


def build(b):
    creases = [Vector(n).normalized() for n in CREASE_NORMALS]
    bm = uv_sphere(SEGMENTS, RINGS, 1.0)
    for v in bm.verts:
        p = v.co.normalized()
        r = (1.0
             + 0.16 * math.sin(3.2 * p.x + 0.70) * math.cos(2.8 * p.y + 1.10)
             + 0.14 * math.sin(2.9 * p.z + 2.00) * math.cos(2.6 * p.x + 0.30)
             + 0.11 * math.sin(3.4 * p.y + 1.70))
        for normal in creases:
            offset = p.dot(normal) / CREASE_WIDTH
            r *= 1.0 - CREASE_DEPTH * math.exp(-offset * offset)
        v.co = p * r
    low = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z = max(v.co.z, low * FLOOR_FRACTION)
    height = max(v.co.z for v in bm.verts) - low

    labels = []
    for face in bm.faces:
        c = face.calc_center_median()
        valley = c.length < VALLEY_RADIUS or c.z < low + height * UNDERSIDE
        chaff = (math.sin(31.0 * c.x + 0.7) * math.sin(27.0 * c.y + 1.9)
                 * math.sin(23.0 * c.z + 0.3)) > CHIFF_PATCH
        if valley:
            labels.append(2)                     # deep crease / pressed base
        elif chaff:
            labels.append(1)                     # chaff caught in the fleece
        else:
            labels.append(0)

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['wool']
    me = b.add(bm, 'wool', rgb, rough, smooth=False, name='tuft').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
