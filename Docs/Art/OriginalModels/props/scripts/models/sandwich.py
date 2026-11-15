"""Sandwich - 12 x 9 x 4 cm triangular sandwich with two visible fillings.

PROP_SET_PLAN.md section 3.4: ``12x9x4 cm 三角柱，两层夹心（三色材质）``.  A
sandwich cut on the diagonal is a triangle in plan with the layers running
through its thickness, so the model is one prism whose section is the rounded
triangle and whose extrusion is split into five z rings: bread, filling, filling,
bread.  Colouring the side walls band by band is what makes the cut faces read
as a sandwich; a single-colour block would just be a wedge.

The section is a 12 x 9 cm isosceles triangle with each corner replaced by a
3-point quadratic chamfer, so the crust edges are rounded like real bread instead
of razor sharp.  The long 12 cm side faces +Y, i.e. the camera: that side is the
diagonal cut, so the four layers are what the preview sees first.  The first
draft had the apex pointing at the camera and read as a slice of cake - one big
bread gable and no filling face.

Two details keep it from reading as a slice of cake.  The side walls are not one
brown bread band per slice: each bread layer is pale crumb with a 3 mm crust
strip at the very top and bottom of the sandwich, so the cut face shows brown
edge / pale crumb / ham / cheese / pale crumb / brown edge, which is what a
sandwich edge actually looks like.  And the top face is pale crumb with a thin
8% crust border around it, like the bread slice in the same batch, instead of a
single brown plate or a big pale "icing" triangle.

Layer heights: 3 mm crust, 9 mm crumb, 8 mm ham, 8 mm cheese, 9 mm crumb, 3 mm
crust = 40 mm.
"""

import math

import bmesh

ID = 'sandwich'
NAME_ZH = '三明治'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.12
CSV_GAME_LONGEST_M = 0.38
SOURCING = ('model ourselves：12×9×4 cm 三角柱，两层夹心三色'
            '（CSV 提示 Kenney sandwich.glb）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.12
TRI_BUDGET = 400
MATERIALS = [
    # Same two bread colours as bread_slice.py, so the two props sit together.
    {'key': 'crust', 'name_zh': '面包皮棕', 'linear_rgb': [0.230, 0.095, 0.035],
     'roughness': 0.60},
    {'key': 'crumb', 'name_zh': '面包瓤米白', 'linear_rgb': [0.640, 0.470, 0.250],
     'roughness': 0.85},
    {'key': 'ham', 'name_zh': '火腿粉', 'linear_rgb': [0.480, 0.145, 0.135],
     'roughness': 0.55},
    {'key': 'cheese', 'name_zh': '夹心奶酪黄', 'linear_rgb': [0.780, 0.560, 0.150],
     'roughness': 0.50},
]
NOTES = ('Nine-point rounded-triangle section (12 cm long cut face at y=+0.045, '
         'apex at y=-0.045, 12 mm corner chamfers) extruded through seven z rings '
         'at 0 / 3 / 12 / 20 / 28 / 37 / 40 mm, plus an inset top ring at 92% for '
         'the crumb: 6 x 9 side quads + 9 top ring quads + two 9-gon caps + the '
         'crumb cap = 140 triangles.  Side bands by face-centre z: 3 mm crust, '
         '9 mm crumb, 8 mm ham, 8 mm cheese, 9 mm crumb, 3 mm crust, so both cut '
         'faces read as brown edge / pale crumb / ham / cheese / pale crumb / '
         'brown edge.  The bottom cap is crust; the top is a thin crust border '
         'with a pale crumb centre.  Footprint 0.120 x 0.090 m, height 0.040 m, '
         'one closed shell.')

# The vertices sit slightly outside the 12 x 9 cm silhouette because the corner
# chamfers cut it back: with these numbers the chamfered outline is exactly
# 0.1200 x 0.0900 m, so finish() has nothing to rescale and the 40 mm height
# stays 40 mm.  With a plain 0.06 / 0.045 triangle the outline was only
# 0.1107 x 0.085 m, finish() scaled everything up by 8.45%, and the prop came out
# 12.0 x 9.2 x 4.34 cm instead of 12 x 9 x 4.
BASE_HALF = 0.0647
BASE_Y = 0.045          # the long cut face looks at the camera (+Y)
APEX_Y = -0.050
CHAMFER = 0.012
INSET = 0.92            # top crumb cap, as a fraction of the section
LAYERS = [0.000, 0.003, 0.012, 0.020, 0.028, 0.037, 0.040]


def _corner(vertex, prev, nxt, samples=3):
    """Quadratic chamfer of one triangle corner, three points per corner."""
    p0 = (vertex[0] + (prev[0] - vertex[0]) * CHAMFER / _dist(prev, vertex),
          vertex[1] + (prev[1] - vertex[1]) * CHAMFER / _dist(prev, vertex))
    p2 = (vertex[0] + (nxt[0] - vertex[0]) * CHAMFER / _dist(nxt, vertex),
          vertex[1] + (nxt[1] - vertex[1]) * CHAMFER / _dist(nxt, vertex))
    pts = []
    for i in range(samples):
        t = float(i) / (samples - 1)
        x = (1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * vertex[0] + t * t * p2[0]
        y = (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * vertex[1] + t * t * p2[1]
        pts.append((x, y))
    return pts


def _dist(a, b):
    return ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5


def section():
    tri = [(-BASE_HALF, BASE_Y), (0.0, APEX_Y), (BASE_HALF, BASE_Y)]
    pts = []
    n = len(tri)
    for i in range(n):
        pts.extend(_corner(tri[i], tri[i - 1], tri[(i + 1) % n]))
    return pts


def build(b):
    pts = section()
    n = len(pts)
    bm = bmesh.new()
    rings = [[bm.verts.new((x, y, z)) for (x, y) in pts] for z in LAYERS]
    for k in range(len(rings) - 1):
        lower, upper = rings[k], rings[k + 1]
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((lower[i], lower[j], upper[j], upper[i]))
    top = rings[-1]
    hub = bm.verts.new((sum(v.co.x for v in top) / n,
                        sum(v.co.y for v in top) / n, LAYERS[-1]))
    crumb_ring = [bm.verts.new(hub.co + (v.co - hub.co) * INSET) for v in top]
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((top[i], top[j], crumb_ring[j], crumb_ring[i]))
    bm.faces.new(crumb_ring)
    hub_centre = hub.co.copy()
    bm.verts.remove(hub)
    bm.verts.index_update()
    bm.faces.new(list(reversed(rings[0])))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])

    crumb_x, crumb_y = hub_centre.x, hub_centre.y

    def is_ham(c):
        return 0.0125 <= c.z < 0.0205

    def is_cheese(c):
        return 0.0205 <= c.z < 0.0285

    def is_crumb(c):
        if c.z > 0.0395:                       # the pale centre of the top face
            return math.hypot(c.x - crumb_x, c.y - crumb_y) < 0.010
        return 0.0035 < c.z < 0.0118 or 0.0282 < c.z < 0.0368

    b.add(bm, 'crust', (0.230, 0.095, 0.035), 0.60, smooth=False,
          name='sandwich',
          extras=[('crumb', (0.640, 0.470, 0.250), 0.85, is_crumb),
                  ('ham', (0.480, 0.145, 0.135), 0.55, is_ham),
                  ('cheese', (0.780, 0.560, 0.150), 0.50, is_cheese)])
