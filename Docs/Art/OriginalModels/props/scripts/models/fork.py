"""Fork - 19 cm dinner fork, four separate tines on a flat handle that curves up.

Contract: 0.190 x 0.025 x 0.012 m (real size), metal, <= 220 triangles.

The fork is one 2.5 mm stamped plate: a tapered handle, a short shoulder that
opens to the 25 mm head, and four tines cut apart by three slots that run from
the tip all the way back to the tine root.  The slots are 2.4 mm wide at the root
and 3.0 mm at the tip, so the gaps are real openings in the silhouette, and the
taper narrows each tine from 4.45 mm to 4.0 mm toward the point.

The outline is a single 26-point polygon - handle, head, four tines, three slots -
extruded in Z, so the tine roots are one solid with the head by construction: no
boolean, no separate tine objects, one closed shell.

Resting: the handle plate lies flat on z = 0 and the head sweeps up, so the tine
tips reach z = 0.012 m - the row's 12 mm.  The lift is a smoothstep applied per
vertex after extrusion, which keeps the plate a constant 2.5 mm thick along the
curve.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, tool class): a
single 0.62 steel slot could not satisfy the two-colour rule at all.
  * `metal`      - the body (linear 0.430 / 0.450 / 0.470, rough 0.32)
  * `ceramic`    - the polished tine tips, the brighter metal slot the spec asks
                   for on tooth tips (0.760 / 0.745 / 0.700, rough 0.35)
  * `metal_dark` - a stamped reinforcing trough down the handle (0.090 / 0.095 /
                   0.105, rough 0.55), the dark slot
The trough is real geometry: a 1.2 x 50 mm tool is subtracted 1.2 mm into the
2.5 mm handle, so the handle reads as pressed sheet rather than a flat bar.  It
stays 5 mm inside the butt, so the AABB is unchanged.
"""

from prop_lib import prism

ID = 'fork'
NAME_ZH = '餐叉'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：0.190 m 四齿餐叉，齿根连成一块 + 柄部压槽'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.190
TRI_BUDGET = 352
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.190', 'game_longest_dim_m': '0.190'}
MATERIALS = [
    {'key': 'metal', 'name_zh': '不锈钢', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'ceramic', 'name_zh': '抛光齿尖', 'linear_rgb': [0.760, 0.745, 0.700],
     'roughness': 0.35},
    {'key': 'metal_dark', 'name_zh': '压槽暗钢', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
]
NOTES = ('One 34-point comb polygon (handle, head, 4 tines, 3 through-slots) '
         'extruded 0.0025 m in Z, then a smoothstep lift of up to 0.0095 m '
         'applied to every vertex by x, so the head curves up and the tine tips '
         'land at z = 0.012 m while the handle stays flat on z = 0.  Slots are '
         'open from the tip back to the tine root: 2.4 mm wide at the root and '
         '3.0 mm at the tip; tines taper 4.45 -> 4.0 mm; head 25 mm wide; each '
         'tine edge carries a midpoint at x = 0.062 so the polished tip owns real '
         'quads.  '
         'Detail: a 1.2 x 50 mm tool is subtracted 1.2 mm into the handle '
         '(stamped trough, metal_dark), and x > 0.060 takes the polished ceramic '
         'tine tips.  AABB 0.190 x 0.025 x 0.012 m, one closed shell.')

X_BACK = -0.095
X_NECK = -0.030
X_ROOT = 0.018
X_TIP = 0.095
HANDLE_BACK = 0.0055
HANDLE_NECK = 0.0065
HEAD_HALF = 0.0125
THICK = 0.0025
LIFT = 0.0095
LIFT_START = -0.030
LIFT_SPAN = 0.085
# Tine / slot boundaries across the head, left (-Y) to right (+Y): 4 tines of
# 4.45 mm and 3 slots of 2.40 mm at the root, 4.0 / 3.0 mm at the tip.
ROOT_EDGES = (-0.0125, -0.00805, -0.00565, -0.00120,
              0.00120, 0.00565, 0.00805, 0.0125)
TIP_EDGES = (-0.0125, -0.00850, -0.00550, -0.00150,
             0.00150, 0.00550, 0.00850, 0.0125)
# Stamped trough in the handle, and where the polished tips start.
TROUGH_X0, TROUGH_X1 = -0.090, -0.040
TROUGH_HALF = 0.0012
TROUGH_FLOOR = 0.0013
POLISHED_FROM = 0.062
X_MID = 0.062
EDGE_WAIST = 0.00008
METAL_RGB = (0.430, 0.450, 0.470)
CERAMIC_RGB = (0.760, 0.745, 0.700)
METAL_DARK_RGB = (0.090, 0.095, 0.105)


def _lift(x):
    t = (x - LIFT_START) / LIFT_SPAN
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return LIFT * t * t * (3.0 - 2.0 * t)


def _edge_at(root_y, tip_y):
    """Y of a tine edge at X_MID, interpolated between its root and tip ends.

    The 0.08 mm inward waist matters for more than looks: a midpoint that is
    exactly collinear with its two neighbours makes the n-gon cap's BEAUTY
    triangulation emit a sliver, which showed up as 3 non-manifold edges after
    finish().  A slight waist keeps every cap triangle valid and leaves the
    0.025 m head width untouched.
    """
    t = (X_MID - X_ROOT) / (X_TIP - X_ROOT)
    y = root_y + (tip_y - root_y) * t
    return y - EDGE_WAIST if y > 0.0 else y + EDGE_WAIST


def _outline():
    pts = [(X_BACK, -HANDLE_BACK), (X_NECK, -HANDLE_NECK),
           (-0.018, -0.0098), (-0.004, -0.0118), (0.006, -HEAD_HALF),
           (X_ROOT, -HEAD_HALF)]
    for i in range(4):
        # Each tine's two long edges carry a midpoint at X_MID: without it a
        # tine side is one quad from root to tip whose centre (x = 0.0565) is
        # inside the metal slot, so the polished tip could never own any face.
        pts.append((X_MID, _edge_at(ROOT_EDGES[2 * i], TIP_EDGES[2 * i])))
        pts.append((X_TIP, TIP_EDGES[2 * i]))
        pts.append((X_TIP, TIP_EDGES[2 * i + 1]))
        pts.append((X_MID, _edge_at(TIP_EDGES[2 * i + 1], ROOT_EDGES[2 * i + 1])))
        pts.append((X_ROOT, ROOT_EDGES[2 * i + 1]))
        if i < 3:
            pts.append((X_ROOT, ROOT_EDGES[2 * i + 2]))
    pts += [(0.006, HEAD_HALF), (-0.004, 0.0118), (-0.018, 0.0098),
            (X_NECK, HANDLE_NECK), (X_BACK, HANDLE_BACK)]
    return pts


def _trough_polygon():
    return [(TROUGH_X0, -TROUGH_HALF), (TROUGH_X1, -TROUGH_HALF),
            (TROUGH_X1, TROUGH_HALF), (TROUGH_X0, TROUGH_HALF)]


def _slot(centre):
    c = centre
    if (0.0002 < c.z < THICK - 1e-4
            and TROUGH_X0 - 1e-4 < c.x < TROUGH_X1 + 1e-4
            and abs(c.y) < TROUGH_HALF + 1e-4):
        return 2
    if c.x > POLISHED_FROM:
        return 1
    return 0


def build(b):
    bm = prism(_outline(), THICK, axis='z', start=0.0)
    for v in bm.verts:
        v.co.z += _lift(v.co.x)
    fork = b.add(bm, 'metal', METAL_RGB, 0.32, smooth=False, name='fork')
    trough = b.add(prism(_trough_polygon(), THICK, axis='z',
                         start=TROUGH_FLOOR),
                   'metal_dark', METAL_DARK_RGB, 0.55, smooth=False,
                   name='trough')
    b.boolean_difference(fork, [trough])

    me = fork.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
