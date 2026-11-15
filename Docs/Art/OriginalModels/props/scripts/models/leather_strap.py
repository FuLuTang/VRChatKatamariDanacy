"""Leather strap - 20 cm belt strap, folded back on itself, with a brass buckle.

DungBeetleMeadow catalogue t3: 0.200 x 0.035 x 0.008 m, shape_class flat,
202/288 tris, Z3 牧场 (a dropped harness strap).

Why the previous build showed no metal:  Blender's Exact boolean defaults to
``material_mode='INDEX'``, so the buckle tool's faces arrived in the strap's
slot 0 (leather) and the metal slot the manifest advertised had ZERO faces.
The buckle was there, in brown, on a brown strap - invisible.  This build
repaints the welded frame and prong after the union by their own volume, so
every slot really carries faces.

Shape, from the 2026-11-15 review:

* strap, an eight-point flat section (34 mm wide, 4.4 mm thick, chamfered
  edges), swept along ten stations: it runs from the buckle end (+0.100) flat
  on the ground, turns through a 180 degree fold at x = -0.098 (2.3 mm bend
  radius, 0.2 mm between layers) and comes back over itself to x = -0.050.
  The fold's outer face touches z = 0, so the strap lies on the ground where it
  bends instead of floating on an end.
* buckle, a square brass frame 22 x 22 mm outer, 4 mm bars (14 x 14 opening),
  4.9 mm tall: from z = 0.0020 (buried in the leather, which is what welds it
  into one shell) to z = 0.0069, i.e. 2.5 mm proud of the strap's top face.
* prong, a 17 mm tapered bar from the left bar, across the 14 mm opening, onto
  the right bar at z = 0.0056-0.0076, so it stands 0.7 mm above the frame and
  reads as a buckle tongue.

Colour: strap wood (tan leather, lum 0.224), frame brass (0.391, 0.30 rough),
prong + bar metal_dark (0.095, the dark slot).  wood/brass differ by 0.167 in
linear luminance, wood/metal_dark by 193 deg of hue.
"""

import math

import bmesh

ID = 'leather_strap'
NAME_ZH = '皮带扣带'
CATEGORY = 'animal_gear'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t3 '
            '0.200×0.035×0.008 m 皮革带（Z3 牧场，断掉的挽具带）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.200
TRI_BUDGET = 288
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.200', 'game_longest_dim_m': '0.200'}
WOOD = (0.330, 0.205, 0.105)
BRASS = (0.520, 0.380, 0.120)
METAL_DARK = (0.090, 0.095, 0.105)
MATERIALS = [
    {'key': 'wood', 'name_zh': '旧皮革棕', 'linear_rgb': list(WOOD),
     'roughness': 0.86},
    {'key': 'brass', 'name_zh': '黄铜扣框', 'linear_rgb': list(BRASS),
     'roughness': 0.30},
    {'key': 'metal_dark', 'name_zh': '暗铁扣针', 'linear_rgb': list(METAL_DARK),
     'roughness': 0.55},
]
NOTES = ('Strap: 10 stations x 8-point flat section (72 side quads + two 8-gon '
         'caps = 156 triangles), half-width 0.0170 m, half thickness 0.0022 m, '
         'folded 180 deg at x = -0.098 (outer face on z = 0) and returned to '
         'x = -0.050; buckle end at +0.100.  Buckle: square frame 22 x 22 mm '
         'outer, 4 mm bars (14 x 14 opening), z 0.0020-0.0069 (2.5 mm proud), 32 '
         'triangles; prong 17 mm tapered bar across the opening standing 0.7 mm '
         'above the frame, 12 triangles.  Frame and prong are welded to the '
         'strap by Exact unions (one closed shell) and repainted after the '
         'boolean, because the solver copies material index 0 onto tool faces.  '
         'Overall 0.200 x 0.034 x 0.0090 m.  Leather uses the palette wood tone '
         'at cloth/leather roughness 0.86.')

# (x, z centre, section tilt, half width); bottom layer first, then the fold,
# then the doubled-back layer.
STATIONS = [
    (-0.0500, 0.0068, math.pi, 0.0110),
    (-0.0820, 0.0068, math.pi, 0.0170),
    (-0.0925, 0.0060, 2.95, 0.0170),
    (-0.0972, 0.0048, 2.10, 0.0170),
    (-0.0978, 0.0036, 1.20, 0.0170),
    (-0.0930, 0.0024, 0.25, 0.0170),
    (-0.0600, 0.0022, 0.00, 0.0170),
    (0.0000, 0.0022, 0.00, 0.0170),
    (0.0600, 0.0022, 0.00, 0.0170),
    (0.1000, 0.0022, 0.00, 0.0158),
]
HALF_THICK = 0.0022
# flat strap section: (fraction of half-width, fraction of half-thickness)
SECTION = ((0.0, 1.0), (0.76, 0.82), (1.0, 0.0), (0.76, -0.82),
           (0.0, -1.0), (-0.76, -0.82), (-1.0, 0.0), (-0.76, 0.82))

BUCKLE_CX = 0.089
BUCKLE_Z = (0.0020, 0.0069)
BUCKLE_OUT = 0.011
BUCKLE_BAR = 0.004
PRONG_X = (0.0798, 0.0972)
PRONG_Y = (0.0006, 0.0011)
PRONG_Z = (0.0054, 0.0076)


def _ring(x, zc, tilt, hw):
    ring = []
    for fy, fz in SECTION:
        ys, zs = fy * hw, fz * HALF_THICK
        ring.append((x + zs * math.sin(tilt), ys, zc + zs * math.cos(tilt)))
    return ring


def _strap_bmesh():
    bm = bmesh.new()
    n = len(SECTION)
    rings = [[bm.verts.new(p) for p in _ring(*st)] for st in STATIONS]
    for i in range(len(rings) - 1):
        lo, up = rings[i], rings[i + 1]
        for k in range(n):
            j = (k + 1) % n
            bm.faces.new((lo[k], lo[j], up[j], up[k]))
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _frame_bmesh():
    """Closed square buckle frame: 4 mm bars around a 14 x 14 mm opening."""
    o = BUCKLE_OUT
    i = BUCKLE_OUT - BUCKLE_BAR
    zb, zt = BUCKLE_Z
    bm = bmesh.new()
    corners = ((-1, -1), (1, -1), (1, 1), (-1, 1))
    outer_b = [bm.verts.new((BUCKLE_CX + sx * o, sy * o, zb)) for sx, sy in corners]
    outer_t = [bm.verts.new((BUCKLE_CX + sx * o, sy * o, zt)) for sx, sy in corners]
    inner_b = [bm.verts.new((BUCKLE_CX + sx * i, sy * i, zb)) for sx, sy in corners]
    inner_t = [bm.verts.new((BUCKLE_CX + sx * i, sy * i, zt)) for sx, sy in corners]
    for k in range(4):
        j = (k + 1) % 4
        bm.faces.new((outer_b[k], outer_b[j], outer_t[j], outer_t[k]))
        bm.faces.new((inner_b[k], inner_t[k], inner_t[j], inner_b[j]))
        bm.faces.new((outer_t[k], outer_t[j], inner_t[j], inner_t[k]))
        bm.faces.new((outer_b[k], inner_b[k], inner_b[j], outer_b[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _prong_bmesh():
    """Tapered tongue across the opening, resting on both side bars."""
    x0, x1 = PRONG_X
    yw, yt = PRONG_Y
    zb, zt = PRONG_Z
    bm = bmesh.new()
    lo = [(x0, -yw, zb), (x1, -yt, zb + 0.0002),
          (x1, yt, zb + 0.0002), (x0, yw, zb)]
    hi = [(x0, -yw, zt), (x1, -yt, zt - 0.0002),
          (x1, yt, zt - 0.0002), (x0, yw, zt)]
    lv = [bm.verts.new(c) for c in lo]
    hv = [bm.verts.new(c) for c in hi]
    for k in range(4):
        j = (k + 1) % 4
        bm.faces.new((lv[k], lv[j], hv[j], hv[k]))
    bm.faces.new(list(reversed(lv)))
    bm.faces.new(hv)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _bar_boxes():
    """The four bar volumes, used to repaint the frame after the union."""
    o = BUCKLE_OUT
    w = BUCKLE_BAR
    zb, zt = BUCKLE_Z
    cx = BUCKLE_CX
    e = 2.0e-4
    return [
        (cx - o - e, cx - o + w + e, -o - e, o + e, zb - e, zt + e),
        (cx + o - w - e, cx + o + e, -o - e, o + e, zb - e, zt + e),
        (cx - o + w - e, cx + o - w + e, -o - e, -o + w + e, zb - e, zt + e),
        (cx - o + w - e, cx + o - w + e, o - w - e, o + e, zb - e, zt + e),
    ]


def _in_box(p, box):
    return (box[0] <= p.x <= box[1] and box[2] <= p.y <= box[3]
            and box[4] <= p.z <= box[5])


def _paint(ob, mat, predicate):
    """Reassign faces after a boolean: the Exact solver copies index 0."""
    me = ob.data
    names = [m.name for m in me.materials if m]
    if mat.name not in names:
        me.materials.append(mat)
        names.append(mat.name)
    idx = names.index(mat.name)
    for poly in me.polygons:
        if predicate(poly.center.copy()):
            poly.material_index = idx


def build(b):
    strap = b.add(_strap_bmesh(), 'wood', WOOD, 0.86, smooth=False,
                  name='strap')
    frame = b.add(_frame_bmesh(), 'brass', BRASS, 0.30, smooth=False,
                  name='buckle')
    prong = b.add(_prong_bmesh(), 'metal_dark', METAL_DARK, 0.55,
                  smooth=False, name='prong')
    b.boolean_union(strap, [frame, prong])

    boxes = _bar_boxes()
    _paint(strap, b.material('brass', BRASS, 0.30),
           lambda p: any(_in_box(p, box) for box in boxes))
    prong_box = (PRONG_X[0] - 1e-4, PRONG_X[1] + 1e-4,
                 -PRONG_Y[0] - 1e-4, PRONG_Y[0] + 1e-4,
                 PRONG_Z[0] - 1e-4, PRONG_Z[1] + 1e-4)
    _paint(strap, b.material('metal_dark', METAL_DARK, 0.55),
           lambda p: _in_box(p, prong_box))
