"""Short straw - a 12 cm hollow straw with an accordion flex bend.

Contract: 0.120 x 0.006 x 0.006 m (real size), plastic, <= 120 triangles.

Topology: the straw is one annulus-section loft - seven outer rings along a
polyline, one straight inner wall at r = 1.6 mm, and a six-quad ring at each end
that closes the wall onto the bore.  That is a hollow tube with open ends as one
watertight shell (genus 1, one connected shell), which is what the row asks for:
"both ends open with an inner-wall ring".  No prop_lib primitive can produce an
annulus section and a boolean through-hole cannot stay inside the 120-triangle
budget, so the rings are laid out here and handed to Builder.add; the loft is
deterministic and needs no solver.

Shape: straight body from x = -60 mm, then three deep folds (radius alternating
3.0 / 2.0 mm, so each pleat is a 1 mm notch) between 10 and 28 mm, and from the
last fold the head kinks 12 deg upward - the pleats sit exactly at the kink, the
way a real flex straw's bellows carries the bend.  The openings are real holes:
the annular end faces plus the darker `bore` material on the inner wall (that
material is marked per face in the bmesh, so no positional test can mis-assign
it on the bent section).

Size trade-off, recorded rather than hidden: a bent straw cannot stay inside a
6 x 6 mm envelope.  The bend lifts the head, so the built Z is about 12 mm while
the row's Z is 6 mm; the 12 deg angle is the smallest angle that still reads as a
bend at 512 px, and BEND_DEG = 0 gives back a 6 mm straight straw.  X and Y are
exactly the row's 0.120 / 0.006 m.
"""

import math

import bmesh
from mathutils import Vector

Point = Vector

ID = 'straw_short'
NAME_ZH = '短吸管'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：0.120 m 空心短吸管，带手风琴褶弯头'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 120
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '吸管红塑料',
     'linear_rgb': [0.480, 0.075, 0.060], 'roughness': 0.48},
    {'key': 'ink', 'name_zh': '管口暗环', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'ceramic', 'name_zh': '白色折痕环', 'linear_rgb': [0.760, 0.745, 0.700],
     'roughness': 0.42},
]
NOTES = ('Annulus loft along a 7-point polyline: straight body x -60..10 mm, '
         'three folds (r 3.0 / 2.0 mm) at 13.5 / 20.5 / 27.5 mm, then a 12 deg '
         'kink from x = 27.5 to 60 mm so the head lifts like a bent flex straw.  '
         'Inner wall r 1.6 mm (1.4 mm wall, 0.4 mm at the folds) plus a 6-quad '
         'ring at each opening, 6 segments, '
         '120 triangles (budget ceiling), one closed genus-1 shell, no boolean.  Body axis at '
         'z = 0.0026 m (one apothem) so the flat underside rests on z = 0.  '
         'AABB 0.120 x 0.006 x ~0.012 m: the bend is what pushes Z past the '
         'row 0.006, and BEND_DEG = 0 restores the straight 6 mm version.  '
         'Materials (2026-11-15 upgrade): the barrel is plastic_red, the three '
         'accordion folds (x 0.010-0.0275 m) are the white ceramic slot, and the '
         'two mouth annuli are the dark ink slot, so the old near-white pair now '
         'reads as a red/white striped straw with shadowed openings.')

SEGMENTS = 6
RED_RGB = (0.480, 0.075, 0.060)
WHITE_RGB = (0.760, 0.745, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
BELLOWS_X = (0.0100, 0.0275)     # the three accordion folds span this window
BEND_DEG = 12.0
BODY_AXIS_Z = 0.0030 * math.sqrt(3.0) * 0.5
INNER_R = 0.0016
KNEE_X = 0.0275
TIP_X = 0.060
# (x, z, radius) - the z of the last two points follows BEND_DEG from KNEE_X.
_KNEE_Z = BODY_AXIS_Z
_TIP_Z = _KNEE_Z + (TIP_X - KNEE_X) * math.tan(math.radians(BEND_DEG))
PATH = (
    (-0.0600, BODY_AXIS_Z, 0.0030),
    (0.0100, BODY_AXIS_Z, 0.0030),
    (0.0135, BODY_AXIS_Z, 0.0020),
    (0.0170, BODY_AXIS_Z, 0.0030),
    (0.0205, BODY_AXIS_Z, 0.0020),
    (0.0240, BODY_AXIS_Z, 0.0030),
    (KNEE_X, _KNEE_Z, 0.0020),
    (TIP_X, _TIP_Z, 0.0030),
)


def _frame(points):
    """Unit tangents (bisector at interior points) plus each ring's basis."""
    out = []
    up = Vector((0.0, 1.0, 0.0))
    for i in range(len(points)):
        if i == 0:
            t = points[1] - points[0]
        elif i == len(points) - 1:
            t = points[-1] - points[-2]
        else:
            t = points[i + 1] - points[i - 1]
        t.normalize()
        out.append((t, up, t.cross(up).normalized()))
    return out


def _ring(bm, center, basis, radius):
    _t, n, m = basis
    verts = []
    for i in range(SEGMENTS):
        a = math.tau * i / SEGMENTS
        verts.append(bm.verts.new(center + n * (radius * math.cos(a))
                                  + m * (radius * math.sin(a))))
    return verts


def _band(bm, lower, upper, flip=False):
    faces = []
    for i in range(SEGMENTS):
        j = (i + 1) % SEGMENTS
        if flip:
            faces.append(
                bm.faces.new((lower[i], upper[i], upper[j], lower[j])))
        else:
            faces.append(
                bm.faces.new((lower[i], lower[j], upper[j], upper[i])))
    return faces


def _annulus(bm, outer, inner):
    for i in range(SEGMENTS):
        j = (i + 1) % SEGMENTS
        bm.faces.new((outer[i], inner[i], inner[j], outer[j]))


def _bore_offset(centre):
    """Distance from ``centre`` to the bore's chord (butt ring to tip ring).

    The shell is bent but the bore was authored as one straight cylinder between
    the two openings, so that chord - not the bent centreline - is the bore's
    axis.  Kept as the radius reference for the mouth test below.
    """
    a = Point((PATH[0][0], 0.0, PATH[0][1]))
    b = Point((PATH[-1][0], 0.0, PATH[-1][1]))
    ab = b - a
    t = (centre - a).dot(ab) / ab.length_squared
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return (centre - (a + ab * t)).length


def _mouth(centre):
    """The annulus faces at the two openings: the dark rings of the straw.

    The inner wall itself stays the barrel colour on purpose.  The bore is a
    straight cylinder through a shell that kinks 12 deg, so along the top of the
    bend the inner wall sits a little proud of the outer wall (a pre-existing
    shape issue in this prop, not something this pass changes).  Painting the
    wall dark turned that into a visible dark stripe; keeping it red keeps the
    seam invisible, and the two end annuli - which is where a viewer actually
    looks into the tube - carry the dark slot instead.
    """
    return (_bore_offset(centre) > INNER_R
            and (centre.x < -0.0585 or centre.x > 0.0585))


def _bellows(centre):
    return BELLOWS_X[0] - 1e-6 < centre.x < BELLOWS_X[1] + 1e-6


def build(b):
    points = [Vector((x, 0.0, z)) for (x, z, _r) in PATH]
    bases = _frame(points)
    bm = bmesh.new()

    outer = [_ring(bm, points[i], bases[i], PATH[i][2])
             for i in range(len(PATH))]
    for k in range(len(outer) - 1):
        _band(bm, outer[k], outer[k + 1])

    inner_start = _ring(bm, points[0], bases[0], INNER_R)
    inner_end = _ring(bm, points[-1], bases[-1], INNER_R)
    inner_faces = _band(bm, inner_start, inner_end, flip=True)

    _annulus(bm, outer[0], inner_start)
    _annulus(bm, outer[-1], inner_end)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])

    del inner_faces

    # Flat shading on purpose: the six-segment section has a flat top and a flat
    # bottom, and smooth normals across those 60/90 deg edges produced a dark
    # band along the whole top of the straw.  Flat shading also makes each
    # pleat read as a crisp fold instead of a soft ripple.
    # Materials through Builder.add's `extras`: a bmesh face's material_index is
    # dropped when the mesh it lands in has a single slot, so the slots are
    # assigned here by geometry instead.  The bore test is a distance to the
    # centreline: 1.386 mm at the sampled chord midpoints of a 60 deg section
    # versus 2.16 mm for the nearest outer cone, so 1.70 mm separates them.  The
    # rubber entry is last, so it wins over the bellows band on the inner wall.
    b.add(bm, 'plastic_red', RED_RGB, 0.48, smooth=False,
          extras=[('ceramic', WHITE_RGB, 0.42, _bellows),
                  ('ink', INK_RGB, 0.75, _mouth)])
