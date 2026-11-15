"""Earbuds case - 70 x 50 x 25 mm clamshell: grey body, blue lid, dark parting line.

MessyRoom catalogue row earbuds_case: 0.070 x 0.050 x 0.025 m, blocky,
electronics_cables.  The silhouette is a rounded rectangle with a 14 mm corner
radius, lofted through seven outlines so the two horizontal edges are chamfered
and a 1.4 mm shallow groove runs right round the case at the parting line - the
one cue that separates "two moulded halves" from "a soap bar".

Three material slots, split by height across that groove: the body half is
`plastic_grey`, the lid half is `plastic_blue`, and the groove band itself is
`ink`, so the parting line is a dark seam instead of a shading accident.  The
seam is what makes the two halves read as two parts at preview size; the 1.4 mm
inset plus the dark slot is 22 px of the 512 px tile.

The groove has to be cut by the loft itself, not by a boolean: subtracting a ring
tool would need a ring-shaped cutter and would leave a seam wherever the ring
does not close cleanly.  Seven rings of a 12-point rounded rectangle share the
same topology, so the bands between them are plain quads and the mesh is closed
by two n-gon caps that ``finish`` triangulates.  The groove ring is the only
ring that is inset, and it sits at z=0.0110-0.0126 - just below the 0.0125 mid
height - so the lid half above it is the slightly taller one, as the brief asks.

One closed shell, flat shading (the only curved thing is the corner arcs, and a
4-point arc is as round as it needs to be at this size).
"""

import bmesh

from prop_lib import rounded_rect

ID = 'earbuds_case'
NAME_ZH = '耳机盒'
CATEGORY = 'electronics_cables'
SOURCING = 'model ourselves：70 x 50 x 25 mm 圆角盒 + 分模线浅槽，单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.070
# 180 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t0 cap 300.
TRI_BUDGET = 288

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'plastic_grey': ((0.230, 0.235, 0.240), 0.50),
    'plastic_blue': ((0.075, 0.145, 0.400), 0.48),
    'ink': ((0.030, 0.035, 0.045), 0.75),
}
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '灰塑料机身',
     'linear_rgb': list(PALETTE['plastic_grey'][0]),
     'roughness': PALETTE['plastic_grey'][1]},
    {'key': 'plastic_blue', 'name_zh': '蓝塑料盖',
     'linear_rgb': list(PALETTE['plastic_blue'][0]),
     'roughness': PALETTE['plastic_blue'][1]},
    {'key': 'ink', 'name_zh': '深色分模缝',
     'linear_rgb': list(PALETTE['ink'][0]), 'roughness': PALETTE['ink'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.070', 'game_longest_dim_m': '0.070'}
NOTES = ('Seven-ring loft of a 12-point rounded rectangle (0.070 x 0.050, '
         'R 14 mm, 3 points per corner): 6 bands x 12 quads = 144 triangles plus '
         'two 12-gon caps = 164.  Rings (z, inset): 0.0000/0.0024 bottom '
         'chamfer, 0.0024/0, 0.0110/0 (parting line, body side), 0.0118/0.0014 '
         '(groove floor), 0.0126/0 (parting line, lid side), 0.0228/0, '
         '0.0250/0.0026 top chamfer.  The lid half is 12.4 mm tall against the '
         '11.0 mm body half.  Three material slots by face height: body below '
         '0.0108 plastic_grey, groove band 0.0108-0.0128 ink, lid above '
         'plastic_blue.  One closed shell.')

CASE_W, CASE_D, CASE_R = 0.070, 0.050, 0.014
CORNER_SEGMENTS = 2
RINGS = [
    (0.0000, 0.0024),   # bottom chamfer edge
    (0.0024, 0.0000),
    (0.0110, 0.0000),   # parting line, body side
    (0.0118, 0.0014),   # groove floor
    (0.0126, 0.0000),   # parting line, lid side
    (0.0228, 0.0000),
    (0.0250, 0.0026),   # top chamfer edge
]
# Faces between these heights are the groove band (the dark parting seam).
GROOVE_Z0, GROOVE_Z1 = 0.0108, 0.0128


def _lofted(rings):
    """Closed loft through rounded-rect outlines: quads between, n-gon caps."""
    bm = bmesh.new()
    ring_verts = []
    for z, inset in rings:
        outline = rounded_rect(CASE_W - 2.0 * inset, CASE_D - 2.0 * inset,
                               CASE_R - inset, CORNER_SEGMENTS)
        ring_verts.append([bm.verts.new((x, y, z)) for (x, y) in outline])
    count = len(ring_verts[0])
    for k in range(len(ring_verts) - 1):
        lower, upper = ring_verts[k], ring_verts[k + 1]
        for i in range(count):
            j = (i + 1) % count
            bm.faces.new((lower[i], lower[j], upper[j], upper[i]))
    bm.faces.new(list(reversed(ring_verts[0])))
    bm.faces.new(ring_verts[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(b):
    grey, grey_rough = PALETTE['plastic_grey']
    ob = b.add(_lofted(RINGS), 'plastic_grey', grey, grey_rough,
               smooth=False, name='case')

    me = ob.data
    me.materials.clear()
    for key in ('plastic_grey', 'plastic_blue', 'ink'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        z = poly.center.z
        if z < GROOVE_Z0:
            poly.material_index = 0
        elif z <= GROOVE_Z1:
            poly.material_index = 2
        else:
            poly.material_index = 1
