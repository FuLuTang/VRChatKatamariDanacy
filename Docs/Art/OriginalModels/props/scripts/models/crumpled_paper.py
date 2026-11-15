"""Crumpled paper - 60 x 55 x 50 mm ball of crushed paper: white sheet, dark creases.

MessyRoom catalogue row crumpled_paper: 0.060 x 0.055 x 0.050 m, round,
paper_stationery.  A paper ball is the one prop in this half where a sphere is
the wrong primitive: a smoothed sphere reads as a ball of dough, and a 20-face
icosahedron reads as a cut gem.  The shape is therefore a subdivision-2
icosphere - 42 vertices, 80 flat triangles - with every vertex pushed along its
own radius by a deterministic sum of five sine terms, so no two neighbouring
facets share a plane and the surface reads as folded paper.  Flat shading is what
turns those facets into creases.

Three material slots.  This is the one prop in the batch where the brief allows
the slot split to be a selection *over geometry by normal*: the sheet is `paper`,
a facet is `paper_dark` when it either faces away from +Z (the underside of the
ball, where a crushed sheet folds in on itself) or falls on a fixed sine mask of
its own position, and the few facets folded furthest under are `ink` - the deepest
underside creases, and the one slot at or below the 0.12 luminance that spec 1.2
requires (kept off the lit side on purpose: an 0.03 facet among white ones reads
as a hole, not a crease).  All three thresholds are pure
functions of the final geometry - no random module, no time, no file input - so
the split is reproducible and a rebuild gives the same face assignment.

The deformation is a pure function of the unit direction of each vertex: no
random module, no time, no file input.  After the push every axis is scaled to
its target extent (0.060 / 0.055 / 0.050) and the mesh is lifted so the lowest
vertex sits on z=0; ``finish`` then finds the longest edge already equal to
TARGET_LONGEST_M and does not rescale.

80 triangles, three material slots, one closed shell.
"""

import math

from prop_lib import icosphere

ID = 'crumpled_paper'
NAME_ZH = '纸团'
CATEGORY = 'paper_stationery'
SOURCING = 'model ourselves：60 x 55 x 50 mm 揉皱纸团，80 个硬折面'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.060
# 120 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t0 cap 300.
TRI_BUDGET = 192

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'paper': ((0.800, 0.775, 0.700), 0.90),
    'paper_dark': ((0.360, 0.320, 0.250), 0.92),
    'ink': ((0.030, 0.035, 0.045), 0.75),
}
MATERIALS = [
    {'key': 'paper', 'name_zh': '白纸', 'linear_rgb': list(PALETTE['paper'][0]),
     'roughness': PALETTE['paper'][1]},
    {'key': 'paper_dark', 'name_zh': '深色折面（纸背/折痕）',
     'linear_rgb': list(PALETTE['paper_dark'][0]),
     'roughness': PALETTE['paper_dark'][1]},
    {'key': 'ink', 'name_zh': '最深折缝（阴影侧）',
     'linear_rgb': list(PALETTE['ink'][0]), 'roughness': PALETTE['ink'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.060', 'game_longest_dim_m': '0.060'}
NOTES = ('Subdivision-2 icosphere (42 vertices, 80 triangles) with a radial '
         'displacement of +-0.18 from five fixed sine terms of the unit '
         'direction, then scaled per axis to 0.0600 x 0.0550 x 0.0500 and lifted '
         'to z=0.  Subdivision 1 would be the bare 20-face icosahedron (12 '
         'vertices), which reads as a cut stone - that is why the level is 2.  '
         'Flat shading, so all 80 triangles stay hard facets.  Three slots by '
         'facet, all pure functions of the geometry: ink when n.z < -0.78 (the '
         'deepest underside folds, about 6 of the 80 facets), paper_dark when '
         'n.z < -0.50 or the fixed sine mask sin(37x+61y+89z) > 0.72, paper '
         'otherwise.  One shell, no texture.')

EXTENT = (0.060, 0.055, 0.050)
SUBDIVISIONS = 2
DARK_NORMAL_Z = -0.50
DARK_MASK_LEVEL = 0.72
# A third, much darker slot on the few facets folded furthest under: paper_dark
# (0.323) is the darkest the two-tone scheme can reach and spec 1.2 wants one
# slot at or below 0.12, so the deepest underside creases take ink.  The first
# attempt also took a strict sine-mask subset and one of those facets landed in
# the middle of the lit side, where an 0.03 facet reads as a hole punched in the
# sheet rather than a crease; the normal test alone keeps the dark slot under the
# ball, where a crushed sheet actually folds back on itself.
CREASE_NORMAL_Z = -0.78


def _radius_scale(d):
    """Deterministic crumple: a fixed sum of sines of the unit direction."""
    return (1.0
            + 0.150 * math.sin(3.3 * d.x + 1.1)
            + 0.115 * math.sin(2.7 * d.y + 2.3)
            + 0.100 * math.sin(3.9 * d.z + 0.4)
            + 0.070 * math.sin(5.1 * (d.x + d.y) + 2.9)
            + 0.055 * math.cos(6.3 * (d.y - d.z) + 1.7))


def _slot_of(normal, centre):
    """Facet slot: 0 paper, 1 paper_dark, 2 ink - pure functions of geometry."""
    mask = math.sin(37.0 * centre.x + 61.0 * centre.y + 89.0 * centre.z)
    if normal.z < CREASE_NORMAL_Z:
        return 2
    if normal.z < DARK_NORMAL_Z or mask > DARK_MASK_LEVEL:
        return 1
    return 0


def build(b):
    paper_rgb, paper_rough = PALETTE['paper']
    bm = icosphere(SUBDIVISIONS, 1.0)
    for v in bm.verts:
        d = v.co.normalized()
        v.co = d * _radius_scale(d)

    mn = [min(v.co[i] for v in bm.verts) for i in range(3)]
    mx = [max(v.co[i] for v in bm.verts) for i in range(3)]
    span = [mx[i] - mn[i] for i in range(3)]
    for v in bm.verts:
        for i in range(3):
            # Stretch each axis to its own target extent, then sit on z=0.
            v.co[i] = (v.co[i] - mn[i]) * (EXTENT[i] / span[i])

    ob = b.add(bm, 'paper', paper_rgb, paper_rough, smooth=False, name='ball')
    me = ob.data
    me.materials.clear()
    for key in ('paper', 'paper_dark', 'ink'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        poly.material_index = _slot_of(poly.normal, poly.center)
