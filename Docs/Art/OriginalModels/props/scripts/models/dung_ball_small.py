"""Small dung ball - 12 cm sun-hardened ball with pressed-in straw slivers.

DungBeetleMeadow catalogue t1: 0.120 x 0.118 x 0.115 m, shape_class round,
Z1/Z3 招牌件.

The 2026-11-15 review rejected the previous build's grass marks as "big yellow
squares, like beach-ball patches".  Two things were wrong: the marks were whole
grid cells of a 24 x 7 sphere (about 15 x 28 mm blocks), and their colour was
one step off the soil (0.155 vs 0.225), so what showed was the BLOCK, not the
straw.  This build cuts the marks instead of painting cells:

* the ball is a 22-segment x 6-band sphere grid (264 triangles) sculpted per
  vertex by fixed lobes - 9.5 % two-lobe, 5.5 % three-lobe, 3 % seven-fold
  facet, a 4.5 % belly and a 5.5 % thumb dent - then squashed 0.92 in Z so it
  is a FLAT ball (the medium one is tall: the two are not a scale pair);
* seven slivers are cut into the surface by Exact difference, each a flat
  pocket 20-25 mm long, 6.5-8 mm wide and about 1.1 mm deep (the tool is placed
  against the measured surface normal and sunk by the chord sagitta, so the
  sliver keeps its depth at both ends instead of fading out).  A pocket has a
  crisp rectangular outline no grid resolution could give;
* the pocket floor is straw and its 1 mm walls are ink, which is what makes the
  sliver read as a pressed-in chip with a shadow line instead of a patch.

Colour: dung body (lum 0.137), straw slivers (0.368), ink walls (0.035).
straw_dark was NOT used for slivers - at 0.171 it sits within 0.03 of the soil
and would be invisible, which is the exact failure being fixed.
"""

import math

import bmesh
from mathutils import Vector

from prop_lib import TAU

ID = 'dung_ball_small'
NAME_ZH = '小土球'
CATEGORY = 'soil_dung'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t1 '
            '0.120×0.118×0.115 m 小土球（招牌件）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 400
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
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
NOTES = ('22 segments x 6 bands sphere grid (264 triangles, one closed shell, '
         'no loose grass geometry).  Radius sculpture 9.5 % 2-lobe / 5.5 % '
         '3-lobe / 3 % 7-fold facet / 4.5 % belly / 5.5 % thumb dent, 4 % Y '
         'stretch and 8 % Z squash (a FLAT ball, unlike the tall medium one), '
         'bottom cut at 0.90 R and sunk 1.5 mm.  Seven straw slivers are Exact '
         'differences: 20-25 mm long, 6.5-8 mm wide, 1.1 mm deep pockets with a '
         'flat floor (straw) and 1 mm walls (ink); the tool is placed on the '
         'sampled surface normal and sunk by the chord sagitta so the ends still '
         'cut.  Wall faces are found after the boolean by their normal (radial = '
         'floor, tangential = wall) inside each pocket volume, because the Exact '
         'solver copies material index 0 onto tool faces.  AABB 0.120 x 0.118 x '
         '0.111 m.')

BALL_R = 0.0545              # metres; finish() then normalises to 0.120
SEGMENTS = 22
BANDS = 6
SQUASH_Y = 1.040
SQUASH_Z = 0.920
FLOOR = -0.900
SINK = -0.0015
TS = [math.pi * i / BANDS for i in range(BANDS + 1)]
# (azimuth deg, direction z, length m, width m, tilt deg)
POCKETS = (
    (18.0, 0.62, 0.024, 0.0075, 8.0),
    (74.0, 0.34, 0.022, 0.0070, -28.0),
    (118.0, -0.04, 0.025, 0.0080, 42.0),
    (52.0, 0.84, 0.020, 0.0065, 72.0),
    (-24.0, 0.18, 0.023, 0.0075, -62.0),
    (156.0, 0.44, 0.021, 0.0070, 16.0),
    (96.0, -0.52, 0.024, 0.0075, -36.0),
)
POCKET_DEPTH = 0.0011       # nominal depth at the sliver ends


def _radial(angle, zn):
    """Hand-pressed soil: lobes, one crumb facet row and two thumb marks."""
    return (1.0
            + 0.095 * math.sin(2.0 * angle + 0.7) * math.cos(1.5 * zn + 0.3)
            + 0.055 * math.cos(3.0 * angle - 0.9) * math.cos(1.2 * zn + 0.2)
            + 0.030 * math.cos(7.0 * angle + 1.3)
            + 0.045 * math.exp(-(((zn - 0.25) / 0.40) ** 2))
            * math.cos(angle - 1.2)
            - 0.055 * math.exp(-(((zn + 0.35) / 0.28) ** 2))
            * math.exp(-(((angle - 2.30) / 0.60) ** 2)))


def _surface(direction):
    """Surface point and outward normal for a unit direction (unsquashed)."""
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


def _box_bmesh(loc, normal, e1, e2, length, width, depth, height=0.004,
               taper=0.62):
    """Tapered pocket tool: narrower at the -e1 end, so a sliver ends in a
    point instead of a perfect rectangle."""
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
    """Pocket volume containing ``point`` (or None) -> (normal, side)."""
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
        radius = loc.length
        sagitta = length * length / (8.0 * max(radius, 1e-2))
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
