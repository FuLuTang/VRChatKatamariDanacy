"""Clover sprig - 6 cm three-leaflet clover on a real stalk, dark veins.

DungBeetleMeadow catalogue t0: 0.060 x 0.055 x 0.020 m, shape_class flat,
176/192 tris, Z0/Z1 草甸植被.

The previous build read as a green blob with a flap: 12 outline points put the
three leaflets 30 degrees apart with a 42 % valley, the "stalk" was a Gaussian
widened part of the same outline, and every slot was a shade of olive.

This build:

* plate, 24 outline points so the three lobes (90/210/330 degrees) land exactly
  on vertices and the notches between them are real V cuts (valley radius 36 %);
  one mid ring keeps the inner leaf flat and curls the outer band up to 19 mm,
  which is the dried-leaf curl;
* veins, three grooves pressed 0.9 mm along the lobe centres (a geometric
  midrib, not a painted stripe on a flat leaf), painted wood_dark with the rim;
* stalk, a SEPARATE 4-sided tube swept from r = 8 mm (inside the plate, which
  is what welds it) out to 46 mm at the 30 degree notch - so the stalk points
  toward the camera instead of being a flap of leaf.  The union keeps
  closed_shells at 1, and the stalk faces are repainted after it because the
  Exact solver copies material index 0;
* one leaflet (the 90 degree one) is turning: leaf_dry against the two
  leaf_green ones, which is the "局部异色" the spec asks a natural prop for.

Colour: leaf_green (0.202), leaf_dry (0.286, 71 deg of hue away from the green),
wood_dark stalk and rim (0.087, the dark slot); the midribs are grooves.
"""

import math

import bmesh
from mathutils import Vector

ID = 'clover_sprig'
NAME_ZH = '三叶草小枝'
CATEGORY = 'grass_plants'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t0 '
            '0.060×0.055×0.020 m 三叶草小枝（Z0 巢穴口植被）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.060
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.060', 'game_longest_dim_m': '0.060'}
LEAF_GREEN = (0.115, 0.240, 0.075)
LEAF_DRY = (0.400, 0.270, 0.105)
WOOD_DARK = (0.135, 0.078, 0.038)
MATERIALS = [
    {'key': 'leaf_green', 'name_zh': '三叶草绿', 'linear_rgb': list(LEAF_GREEN),
     'roughness': 0.85},
    {'key': 'leaf_dry', 'name_zh': '发黄叶', 'linear_rgb': list(LEAF_DRY),
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '暗叶脉与叶柄', 'linear_rgb': list(WOOD_DARK),
     'roughness': 0.75},
]
NOTES = ('Polar shell, 24 outline points, top with one mid ring (ring fraction '
         '0.58), a bottom fan and a 24-quad rim: 144 triangles; plus a 2-station '
         'x 4-segment stalk tube (12 triangles) welded by one Exact union -> '
         'about 176 triangles, one closed shell.  Outline 0.36-1.00 x 0.026 m '
         'with lobes at 90/210/330 deg and 0.9 mm midrib grooves along each lobe; '
         'centre 0.0046 m, mid ring 0.0050 m, outline 0.0040 m in the notches and '
         '0.0140 m at the tips; underside 3.4 mm below the top.  Stalk from r = '
         '0.008 m to 0.046 m at 30 deg, radius 1.5 -> 1.1 mm, lying on the ground.  '
         'Materials: the 90 deg leaflet is leaf_dry, the other two leaf_green, the '
         'stalk (repainted after the union) and the leaf rim wood_dark; the three '
         'midribs are 0.9 mm grooves (shading, not a painted stripe).')

SEGMENTS = 24
R_BASE = 0.026
VALLEY = 0.40
LOBE_POWER = 0.72
CENTRE_Z = 0.0046
MID_FRACTION = 0.58
MID_Z = 0.0050
NOTCH_Z = 0.0040
CURL = 0.0100
THICK = 0.0034
FLOOR = 0.0006
SCALE_X = 1.06
SCALE_Y = 1.30
GROOVE = 0.0009
GROOVE_WIDTH = 0.10
STEM_A = math.radians(30.0)
STEM_R = (0.008, 0.046)
STEM_RADII = (0.0015, 0.0011)
STEM_Z = 0.0016
LOBES = (math.pi * 0.5, math.pi * 0.5 + 2.0 * math.pi / 3.0,
         math.pi * 0.5 + 4.0 * math.pi / 3.0)
STEP_DRY = math.radians(55.0)


def lobe(a):
    return abs(math.cos(1.5 * (a - 0.5 * math.pi)))


def outline_radius(a):
    return R_BASE * (VALLEY + (1.0 - VALLEY) * lobe(a) ** LOBE_POWER)


def lift(a):
    f = max(0.0, (lobe(a) - VALLEY) / (1.0 - VALLEY))
    return CURL * f ** 1.2


def lobe_distance(a):
    d = min(abs((a - lo + math.pi) % (2.0 * math.pi) - math.pi) for lo in LOBES)
    return d


def top_z(a):
    return NOTCH_Z + lift(a)


def groove(a):
    return -GROOVE * math.exp(-((lobe_distance(a) / GROOVE_WIDTH) ** 2))


def stem_points():
    pts = []
    for r in STEM_R:
        pts.append(Vector((r * math.cos(STEM_A), r * math.sin(STEM_A),
                           STEM_Z)))
    return pts


def _stem_bmesh():
    pts = stem_points()
    bm = bmesh.new()
    rings = []
    for i, p in enumerate(pts):
        ring = []
        r = STEM_RADII[i]
        for k in range(4):
            a = math.pi * 0.5 * k + math.radians(45.0)
            # section in the vertical plane perpendicular to the stalk
            ring.append(bm.verts.new((p.x + r * math.cos(a) * -math.sin(STEM_A),
                                      p.y + r * math.cos(a) * math.cos(STEM_A),
                                      p.z + r * math.sin(a))))
        rings.append(ring)
    for i in range(len(rings) - 1):
        lo, up = rings[i], rings[i + 1]
        for k in range(4):
            j = (k + 1) % 4
            bm.faces.new((lo[k], lo[j], up[j], up[k]))
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _paint(ob, mat, predicate):
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
    bm = bmesh.new()
    ct = bm.verts.new((0.0, 0.0, CENTRE_Z))
    cb = bm.verts.new((0.0, 0.0, FLOOR))
    mids, tops, bots = [], [], []
    for k in range(SEGMENTS):
        a = 2.0 * math.pi * k / SEGMENTS
        r = outline_radius(a)
        c, s = math.cos(a), math.sin(a)
        mt = top_z(a) + groove(a)
        mids.append(bm.verts.new((SCALE_X * r * MID_FRACTION * c,
                                  SCALE_Y * r * MID_FRACTION * s,
                                  MID_Z + (mt - MID_Z) * 0.20)))
        tops.append(bm.verts.new((SCALE_X * r * c, SCALE_Y * r * s, mt)))
        bots.append(bm.verts.new((SCALE_X * r * c, SCALE_Y * r * s,
                                  max(FLOOR, mt - THICK))))
    for k in range(SEGMENTS):
        j = (k + 1) % SEGMENTS
        bm.faces.new((ct, mids[k], mids[j]))
        bm.faces.new((mids[k], tops[k], tops[j], mids[j]))
        bm.faces.new((cb, bots[j], bots[k]))
        bm.faces.new((tops[k], tops[j], bots[j], bots[k]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])

    def polar(centre):
        a = math.atan2(centre.y / SCALE_Y, centre.x / SCALE_X) % (2.0 * math.pi)
        r = math.hypot(centre.x / SCALE_X, centre.y / SCALE_Y)
        return a, r

    def is_dry(centre):
        a, _r = polar(centre)
        return abs((a - math.pi * 0.5 + math.pi) % (2.0 * math.pi)
                   - math.pi) < STEP_DRY


    def is_rim(centre):
        a, r = polar(centre)
        return r > 0.97 * R_BASE * (VALLEY + (1.0 - VALLEY) * lobe(a))

    plate = b.add(bm, 'leaf_green', LEAF_GREEN, 0.85, smooth=False, name='sprig',
                  extras=[('leaf_dry', LEAF_DRY, 0.90, is_dry),
                          ('wood_dark', WOOD_DARK, 0.75, is_rim)])
    stem = b.add(_stem_bmesh(), 'wood_dark', WOOD_DARK, 0.75, smooth=False,
                 name='stalk')
    b.boolean_union(plate, [stem])

    p0, p1 = stem_points()

    def on_stem(centre):
        d = centre - p0
        ab = p1 - p0
        t = max(0.0, min(1.0, d.dot(ab) / ab.length_squared))
        return (centre - (p0 + ab * t)).length < 0.0024

    _paint(plate, b.material('wood_dark', WOOD_DARK, 0.75), on_stem)
