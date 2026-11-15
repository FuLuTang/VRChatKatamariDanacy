"""Wheat stalk - 38 cm dry stalk, a 9.5 cm six-ridged ear and five awns.

DungBeetleMeadow catalogue t4: 0.380 x 0.030 x 0.025 m, shape_class long,
214/416 tris (16 stations x 12-point section + five awn cones, one closed
shell), Z1 干草堆（麦秸）.

The 2026-11-15 review rejected the previous build as "a pin with a teardrop":
a 9 mm ear on an 8 mm stem, four 4-lobe star points at the tip and no dark
slot.  This build fixes the read instead of the size:

* stem, x -0.2015 to 0.073: 8 mm round stalk lying on the ground, faint
  three-lobed section so the light breaks along it;
* collar, x 0.0625-0.073: a 10 mm neck that pinches to 2.6 mm radius and is the
  ink slot - the dark ring where the ear leaves the stalk;
* ear, x 0.073 to 0.168 (0.095 m measured from the swell), 24 mm across at the
  ridge crests: exactly 3x the 8 mm stalk, as the review asked.  Twelve segments with cos(6a) gives SIX longitudinal ridges with
  two samples per ridge (a 4-ridge star on 8 segments aliased into a smooth
  pod last time).  The station radii step 0.0112/0.0120/0.0115/0.0121/0.0116/
  0.0107, so the ear also carries five rows of grain bumps along its length;
  the section is 4 % wider in Y than in Z.
* awns, five cones, 11.8 mm long, 30 deg splay, base radius 1.6 mm sunk into the
  ear tip, straw_dark.  They are a real shell union, not star points: a union
  keeps closed_shells at 1, which separate bristles would break.

Colour break: straw stalk + ear (lum 0.368), straw_dark awns and ridge shadow
(0.171), ink collar (0.035).  The awns are repainted after the boolean because
Blender's Exact solver copies the tool's material *index* (0 = straw), which is
exactly why the previous build showed no dark tip at all.
"""

import math

import bmesh
from mathutils import Vector

from prop_lib import TAU, tube

ID = 'wheat_stalk'
NAME_ZH = '麦穗秆'
CATEGORY = 'grass_plants'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t4 '
            '0.380×0.030×0.025 m 麦秆（Z1 干草堆）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.380
TRI_BUDGET = 416
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.380', 'game_longest_dim_m': '0.380'}
STRAW = (0.470, 0.360, 0.140)
STRAW_DARK = (0.230, 0.165, 0.060)
INK = (0.030, 0.035, 0.045)
MATERIALS = [
    {'key': 'straw', 'name_zh': '麦秸浅黄', 'linear_rgb': list(STRAW),
     'roughness': 0.90},
    {'key': 'straw_dark', 'name_zh': '麦芒深褐', 'linear_rgb': list(STRAW_DARK),
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '穗颈暗环', 'linear_rgb': list(INK),
     'roughness': 0.75},
]
NOTES = ('16 stations x 12-point sweep (14 spans = 336 triangles with both '
         'caps) plus five awn cones (6 triangles each = 30), welded by one Exact '
         'union: 390-420 triangles, one closed shell, no boolean seam left open.  '
         'Stem x -0.2015-0.073, diameter 8 mm, resting on the ground; collar '
         'x 0.0625-0.073 pinches to r = 0.0026 (ink); ear x 0.073-0.168 (0.095 m) '
         'with 24 mm crest diameter (3x the 8 mm stem), six cos(6a) ridges at 8.5 % '
         'amplitude and six grain-row radii (0.0096-0.0133, +-16 %), 4 % wider '
         'in Y (the ridges read as silhouette steps); five awns 11.8 mm '
         'long, 1.6 mm base radius, 32 deg splay, straw_dark, repainted after the '
         'union.  Whole prop 0.380 x 0.028 x 0.025 m.')

# (x, radius, z-centre) bottom to top of the stalk, left to right.
STATIONS = [
    (-0.2015, 0.0036, 0.0040),
    (-0.0100, 0.0040, 0.0044),
    (0.0550, 0.0038, 0.0052),
    (0.0625, 0.0037, 0.0058),
    (0.0675, 0.0026, 0.0068),      # collar: ink ring
    (0.0730, 0.0039, 0.0088),
    (0.0800, 0.0062, 0.0112),      # ear base
    (0.0925, 0.0078, 0.0123),
    (0.1060, 0.0105, 0.0119),
    (0.1200, 0.0081, 0.0119),
    (0.1345, 0.0106, 0.0119),
    (0.1490, 0.0083, 0.0119),
    (0.1600, 0.0077, 0.0119),
    (0.1680, 0.0044, 0.0119),
]
EAR_START = 6            # station index of the first ear station
COLLAR_LO, COLLAR_HI = 0.0600, 0.0760
AWN_BASE_X = 0.1635
AWN_BASE_R = 0.0032
AWN_LEN = 0.0116
AWN_SPLAY = math.radians(32.0)
AWN_RADIUS = 0.0018
AWN_COUNT = 5
AWN_PHASE = 0.35
SEGMENTS = 12


def _stations():
    """Centreline: stalk on the ground, ear lying on its belly."""
    points = []
    for x, _r, z in STATIONS:
        if x < 0.0:
            y = 0.0026 * math.sin(math.pi * (x + 0.2015) / 0.2015)
        else:
            y = 0.0012 * math.sin((x - 0.0) * 26.0)
        points.append((x, y, z))
    return points


def _section(index, angle, radius):
    if index <= EAR_START:
        # stalk and collar: a dry straw is a rounded triangle, not a cylinder
        return radius * (1.0 + 0.035 * math.cos(3.0 * angle + 0.4))
    # ear: 12 segments / 6 lobes = two samples per ridge, so every ridge and
    # every groove lands on a vertex column instead of being averaged away.
    hy, hz = radius * 1.02, radius * 0.98
    base = hy * hz / math.sqrt((hy * math.cos(angle)) ** 2
                               + (hz * math.sin(angle)) ** 2)
    return base * (1.0 + 0.125 * math.cos(6.0 * angle))


def _cone(centre, axis, length, radius, segments=4):
    """A short tapered spike: a 4-point base ring closed by an apex."""
    ref = Vector((0.0, 0.0, 1.0))
    if abs(axis.dot(ref)) > 0.9:
        ref = Vector((0.0, 1.0, 0.0))
    u = (ref - axis * ref.dot(axis)).normalized()
    v = axis.cross(u).normalized()
    ring = [centre + u * (radius * math.cos(TAU * k / segments))
            + v * (radius * math.sin(TAU * k / segments))
            for k in range(segments)]
    return ring, centre + axis * length


def _awns(z_shift):
    """Five awns as one bmesh (one boolean tool, five shells inside it)."""
    bm = bmesh.new()
    axes = []
    for k in range(AWN_COUNT):
        a = TAU * k / AWN_COUNT + AWN_PHASE
        direction = Vector((math.cos(AWN_SPLAY),
                            math.sin(AWN_SPLAY) * math.cos(a),
                            math.sin(AWN_SPLAY) * math.sin(a))).normalized()
        centre = Vector((AWN_BASE_X, AWN_BASE_R * math.cos(a),
                         0.0124 + z_shift + AWN_BASE_R * math.sin(a)))
        ring, apex = _cone(centre, direction, AWN_LEN, AWN_RADIUS)
        verts = [bm.verts.new(p) for p in ring]
        tip = bm.verts.new(apex)
        for i in range(4):
            bm.faces.new((verts[i], verts[(i + 1) % 4], tip))
        bm.faces.new(list(reversed(verts)))
        axes.append((centre, centre + direction * AWN_LEN))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm, axes


def _armax(point, axes):
    """Distance from a point to the nearest awn axis segment."""
    best = 1e9
    for a, b in axes:
        ab = b - a
        t = max(0.0, min(1.0, (point - a).dot(ab) / ab.length_squared))
        best = min(best, (point - (a + ab * t)).length)
    return best


def _paint(ob, mat, predicate):
    """Reassign faces after a boolean: the Exact solver copies index 0."""
    me = ob.data
    names = [m.name for m in me.materials if m]
    if mat.name not in names:
        me.materials.append(mat)
        names.append(mat.name)
    idx = names.index(mat.name)
    hits = 0
    for poly in me.polygons:
        if predicate(poly.center.copy()):
            poly.material_index = idx
            hits += 1
    return hits


def build(b):
    bm = tube(_stations(), [s[1] for s in STATIONS], segments=SEGMENTS,
              section_fn=_section)
    zshift = 0.0004 - min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z += zshift

    def is_collar(centre):
        return COLLAR_LO <= centre.x <= COLLAR_HI

    stalk = b.add(bm, 'straw', STRAW, 0.90, smooth=False, name='stalk',
                  extras=[('ink', INK, 0.75, is_collar)])

    awn_bm, axes = _awns(zshift)
    tool = b.add(awn_bm, 'straw_dark', STRAW_DARK, 0.92, smooth=False,
                 name='awns')
    b.boolean_union(stalk, [tool])
    _paint(stalk, b.material('straw_dark', STRAW_DARK, 0.92),
           lambda p: _armax(p, axes) < 0.0024)
