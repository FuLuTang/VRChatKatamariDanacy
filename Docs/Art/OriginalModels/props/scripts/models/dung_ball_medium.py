"""Medium dung ball - 25 cm dried soil ball, egg-tall, nine straw slivers.

DungBeetleMeadow catalogue t3: 0.250 x 0.245 x 0.240 m, shape_class round,
signature prop #2.

Deliberately a different mesh from dung_ball_small, not a scale pair: 26
segments x 7 bands instead of 22 x 6, a 5 % egg taper toward the crown, a 9 %
taller Z instead of an 8 % flatter one, a 9-fold crumb facet instead of 7, a
different thumb dent on the other side, a deeper flat bottom (0.88 R) and nine
slivers of different lengths at different azimuths.  Side by side the two read
as balls rolled on different days.

The slivers are Exact-difference pockets (28-34 mm long, 7.5-9 mm wide, 1.1 mm
deep at the ends), not painted grid cells: the 2026-11-15 review rejected
"big yellow squares".  Floors are straw, the 1 mm walls are ink, so each
sliver has a shadow line and a crisp outline no tessellation would give.
"""

import math

import bmesh
from mathutils import Vector

from prop_lib import TAU

ID = 'dung_ball_medium'
NAME_ZH = '中土球'
CATEGORY = 'soil_dung'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t3 '
            '0.250×0.245×0.240 m 中土球（招牌件）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.250
TRI_BUDGET = 700
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.250', 'game_longest_dim_m': '0.250'}
DUNG = (0.180, 0.130, 0.075)
STRAW = (0.470, 0.360, 0.140)
INK = (0.030, 0.035, 0.045)
MATERIALS = [
    {'key': 'dung', 'name_zh': '晒硬土棕', 'linear_rgb': list(DUNG),
     'roughness': 0.95},
    {'key': 'straw', 'name_zh': '草屑浅黄', 'linear_rgb': list(STRAW),
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '草屑凹缝', 'linear_rgb': list(INK),
     'roughness': 0.75},
]
NOTES = ('26 segments x 7 bands sphere grid (364 triangles, one closed shell, '
         'no loose grass geometry).  Radius sculpture: 5 % egg taper toward the '
         'crown, 7.5 % 2-lobe asymmetry, 5 % 3-lobe belly, 2.5 % 9-fold facet, '
         'two thumb dents; 3.5 % Y squash and 5.5 % Z stretch so the ball is '
         'TALLER (the small one is flatter), bottom cut at 0.88 R, sunk 1.5 mm.  '
         'Nine straw slivers cut by Exact difference: 28-34 mm long, 7.5-9 mm '
         'wide, 1.1 mm deep at the ends, tapered footprint; floor faces are '
         'straw, 1 mm wall faces ink, separated after the boolean by face normal '
         '(radial = floor, tangential = wall) inside each pocket volume.  AABB '
         '0.250 x 0.238 x 0.248 m.')

BALL_R = 0.1125
SEGMENTS = 26
BANDS = 7
SQUASH_Y = 0.965
SQUASH_Z = 1.055
FLOOR = -0.880
SINK = -0.0015
TS = [math.pi * i / BANDS for i in range(BANDS + 1)]
POCKETS = (
    (12.0, 0.58, 0.032, 0.0085, 12.0),
    (66.0, 0.26, 0.030, 0.0080, -34.0),
    (108.0, -0.10, 0.034, 0.0090, 48.0),
    (40.0, 0.82, 0.028, 0.0075, 78.0),
    (-30.0, 0.12, 0.031, 0.0085, -58.0),
    (150.0, 0.40, 0.029, 0.0080, 20.0),
    (88.0, -0.55, 0.033, 0.0085, -30.0),
    (196.0, 0.20, 0.030, 0.0080, 64.0),
    (128.0, 0.72, 0.029, 0.0075, -12.0),
)
POCKET_DEPTH = 0.0011


def _radial(angle, zn):
    """Egg-tapered, hand-pressed soil with two thumb marks."""
    return ((1.0 - 0.030 * zn - 0.045 * zn * zn)
            * (1.0
               + 0.075 * math.sin(2.0 * angle - 0.6) * math.cos(1.15 * zn + 0.5)
               + 0.050 * math.cos(3.0 * angle + 1.4) * math.cos(1.35 * zn - 0.2)
               + 0.025 * math.cos(9.0 * angle + 0.8)
               - 0.050 * math.exp(-(((zn - 0.40) / 0.30) ** 2))
               * math.exp(-(((angle - 4.10) / 0.55) ** 2))
               + 0.035 * math.exp(-(((zn + 0.55) / 0.30) ** 2))
               * math.cos(2.0 * angle + 2.2)))


def _surface(direction):
    d = direction.normalized()
    p = math.hypot(d.x, d.y)
    if p < 1e-6:
        return None, None
    phi = math.atan2(d.y, d.x) % TAU
    f = _radial(phi, d.z)
    t = f / math.sqrt(p * p + d.z * d.z * f * f)
    point = d * t
    normal = Vector((d.x, d.y / SQUASH_Y, d.z / SQUASH_Z)).normalized()
    point = Vector((point.x * BALL_R, point.y * BALL_R * SQUASH_Y,
                    point.z * BALL_R * SQUASH_Z))
    return point, normal


def _tangent(normal, tilt):
    ref = Vector((0.0, 0.0, 1.0))
    if abs(normal.dot(ref)) > 0.9:
        ref = Vector((0.0, 1.0, 0.0))
    e1 = (ref - normal * ref.dot(normal)).normalized()
    return (e1 * math.cos(tilt)
            + normal.cross(e1) * math.sin(tilt)).normalized()


def _box_bmesh(loc, normal, e1, e2, length, width, depth, height=0.005,
               taper=0.62):
    corners = []
    for s1, s2 in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        w = width * (taper if s1 < 0 else 1.0)
        corners.append(loc + e1 * (s1 * length * 0.5)
                       + e2 * (s2 * w * 0.5))
    lo = [c - normal * depth for c in corners]
    hi = [c + normal * height for c in corners]
    bm = bmesh.new()
    lv = [bm.verts.new(c) for c in lo]
    hv = [bm.verts.new(c) for c in hi]
    for k in range(4):
        j = (k + 1) % 4
        bm.faces.new((lv[k], lv[j], hv[j], hv[k]))
    bm.faces.new(list(reversed(lv)))
    bm.faces.new(hv)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _pocket_of(point, pockets):
    for loc, normal, e1, e2, length, width, depth in pockets:
        d = point - loc
        dn = d.dot(normal)
        if dn < -depth - 2e-4 or dn > 0.0012:
            continue
        if (abs(d.dot(e1)) <= length * 0.5 + 2e-4
                and abs(d.dot(e2)) <= width * 0.5 + 2e-4):
            return normal, dn
    return None, 0.0


def _paint(ob, mat, predicate):
    me = ob.data
    names = [m.name for m in me.materials if m]
    if mat.name not in names:
        me.materials.append(mat)
        names.append(mat.name)
    idx = names.index(mat.name)
    for poly in me.polygons:
        if predicate(poly):
            poly.material_index = idx


def build(b):
    bm = bmesh.new()
    rings = []
    for i, t in enumerate(TS):
        if i == 0 or i == BANDS:
            rings.append([bm.verts.new((0.0, 0.0, -math.cos(t)))])
            continue
        ring = []
        for s in range(SEGMENTS):
            a = TAU * s / SEGMENTS
            r = math.sin(t) * _radial(a, -math.cos(t))
            ring.append(bm.verts.new((r * math.cos(a), r * math.sin(a),
                                      -math.cos(t))))
        rings.append(ring)

    for band in range(BANDS):
        lo, up = rings[band], rings[band + 1]
        for s in range(SEGMENTS):
            j = (s + 1) % SEGMENTS
            if len(lo) == 1:
                bm.faces.new((lo[0], up[s], up[j]))
            elif len(up) == 1:
                bm.faces.new((lo[s], lo[j], up[0]))
            else:
                bm.faces.new((lo[s], lo[j], up[j], up[s]))

    for v in bm.verts:
        v.co *= BALL_R
        v.co.y *= SQUASH_Y
        v.co.z *= SQUASH_Z
        if v.co.z < FLOOR * BALL_R:
            v.co.z = FLOOR * BALL_R
    shift = SINK - min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z += shift
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])

    ball = b.add(bm, 'dung', DUNG, 0.95, smooth=False, name='ball')

    pockets = []
    for phi_deg, z_dir, length, width, tilt_deg in POCKETS:
        phi = math.radians(phi_deg)
        horiz = math.sqrt(max(0.0, 1.0 - z_dir * z_dir))
        loc, normal = _surface(Vector((horiz * math.cos(phi),
                                       horiz * math.sin(phi), z_dir)))
        if loc is None:
            continue
        loc.z += shift
        sagitta = length * length / (8.0 * max(loc.length, 1e-2))
        depth = POCKET_DEPTH + sagitta
        e1 = _tangent(normal, math.radians(tilt_deg))
        e2 = normal.cross(e1).normalized()
        tool = b.add(_box_bmesh(loc, normal, e1, e2, length, width, depth),
                     'dung', DUNG, 0.95, smooth=False, name='pocket')
        b.boolean_difference(ball, [tool])
        pockets.append((loc.copy(), normal.copy(), e1, e2, length, width,
                        depth))

    straw = b.material('straw', STRAW, 0.90)
    ink = b.material('ink', INK, 0.75)

    def is_straw(poly):
        normal, _dn = _pocket_of(poly.center, pockets)
        return normal is not None and abs(poly.normal.dot(normal)) > 0.55

    def is_ink(poly):
        normal, _dn = _pocket_of(poly.center, pockets)
        return normal is not None and abs(poly.normal.dot(normal)) <= 0.55

    _paint(ball, straw, is_straw)
    _paint(ball, ink, is_ink)
