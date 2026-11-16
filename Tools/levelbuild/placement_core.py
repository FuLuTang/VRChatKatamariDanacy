#!/usr/bin/env python3
"""Level placement data: schema, coordinates, AABB, absorb gate, support, budget.

This module is the **single implementation** of every geometric rule the level
authoring tool needs.  It imports nothing but the Python standard library (in
particular it must never import ``bpy`` or ``mathutils``), because the same code
runs in three places:

  * ``Tools/levelbuild/levelbuild.py``   - the CLI / validator (any machine),
  * ``Tools/levelbuild/blender/*.py``    - headless Blender 4.0.2 on Linux and
    Blender 5.3 on Windows (the scripts put the repo root on ``sys.path`` and
    ``import Tools.levelbuild.placement_core``),
  * ``Docs/tests/levelbuild/``           - the tests.

The one **non-standard-library** import is deliberate and lazy: the curved-ramp
expansion calls the design's reference model in
``Docs/Design/BallMotor/contact_v2/contact_v2.py`` (:func:`curve_reference` loads
it by path), because a second tessellator would be a second answer to the same
question (``CONTACT_MODEL_V2`` 7.2).  It is not a ``bpy`` import and it is not a
repo-root import, so the three consumers above are unaffected.

The one interface between the three authors (two Blender versions, one Unity) is
``placement.json``; see ``Docs/Design/Tools/LEVEL_AUTHORING.md``.

Conventions (each of these errors silently, hence written once here):

  * JSON positions are **Unity world metres, Y up**.  Blender is Z up, so a
    position converts by swapping the last two components: ``(x, y, z)`` in
    Blender becomes ``(x, z, y)`` in Unity.  That swap is an improper rotation
    (determinant -1), so quaternions must flip sign as well; see
    :func:`blender_to_unity_quat`.
  * JSON quaternions are **Unity order ``(x, y, z, w)``** and are always
    written in the canonical sign (:func:`canonical_quat`), so a round trip
    through the file and back cannot produce ``q`` vs ``-q`` byte noise.
  * ``aabb`` is the un-rotated model AABB **in Unity axes** ``(x, y=height, z)``.
    The prop manifests measure in Blender axes (``z`` is height), so
    :func:`manifest_aabb` reorders them; copying the manifest numbers straight
    into Unity leaves every tall prop lying on its side.
  * A prop's ``position`` is the AABB centre (the models are built with a
    centre pivot, ``manifest.pivot == "center"``), so a prop resting on a
    surface at height ``h`` has ``position.y == h + aabb.y / 2``.
"""

import importlib.util
import json
import math
import os
import re
import sys
from pathlib import Path

from Tools.levelbuild import collider_proxy

# --------------------------------------------------------------------------- #
# schema constants
# --------------------------------------------------------------------------- #
SCHEMA = "katamari.placement/0"
CONVENTION = "unity-y-up-left-handed-quat-xyzw"
QUAT_ORDER = "xyzw"

POSITION_DECIMALS = 4
QUAT_DECIMALS = 6
SIZE_DECIMALS = 4
DIAMETER_DECIMALS = 3
CAMERA_DECIMALS = 3

#: ``D_req = (60 * k * a * b * c / pi) ** (1/3)`` -- the equivalent sphere diameter whose
#: volume is ten times the prop's ORIGINAL volume ``V = k * a * b * c``
#: (``k*a*b*c <= (pi/6) * D**3 / 10``).  60 = 6 * 10, the 10x gate constant.
VOLUME_GATE_FACTOR = 60.0

#: The original's per-item volume factor: ``Volume = k * (a*b*c)`` with k = 0.70 for
#: 1,008 of 1,425 types (70.7%).  Absent from a ``defs[]``/``props[]`` row means this
#: value, and the canonical writer never inserts it (an existing file stays byte-identical).
#: Evidence: ``PARITY_AUDIT.md`` G13, ``meta/solid_volume/README.md``,
#: ``Docs/Research/ContentVolume/FIXED_POINT_VOLUME_IMPACT.md`` §2 O3.
DEFAULT_VOLUME_K = 0.70

#: The one range a declared ``volumeK`` must fall in: ``0 < k <= 32``.  0 (or a negative)
#: would give every item volume 0 and let a pinhead absorb a house; the ceiling is well
#: past the widest hand-tuned factor in the original (thin sheets reach 16.8) and is there
#: to catch a typo (``70`` for ``0.70``), not to reject a real author value.
VOLUME_K_MAX = 32.0

#: ``D_req`` and the ball diameter are compared as WHOLE MILLIMETRES (the original's
#: ``Req Pickup Size``), so a value that float error places a hair below an exact boundary
#: is lifted back onto it.  One micrometre (1e-3 mm) is ~4x the float32 ulp at a 2.2 m
#: diameter (2.4e-4 mm), i.e. large enough to repair the arithmetic, and 1/1000 of the
#: quantum, i.e. far below the 0.1 mm resolution of an authored AABB.  The C# twin
#: (``PickupAbsorbMath.MmFloorEpsilon``) uses this same value so the two gates cannot
#: disagree on a knife-edge item.
MM_EPSILON = 1e-3

#: ``rise <= step_limit(= STEP_RATIO * D)`` is terrain, above it is blocking
#: (``Docs/Design/BallMotor/TERRAIN_SUPPORT.md`` section 6.1).
STEP_RATIO = 0.25

#: Ball diameter tiers the population report is computed at (design section 8.13).
TIER_DIAMETERS = (0.30, 0.50, 0.80, 1.10, 1.50, 2.20)

#: A prop AABB may not leave ``level.bounds`` by more than this.
BOUNDS_MARGIN_M = 0.05
#: "Not sunk / not floating" tolerance against the declared support surface.
SINK_TOLERANCE_M = 0.01
FLOAT_TOLERANCE_M = 0.02
#: Stacked props: the upper one's bottom against the lower one's top.
STACK_TOLERANCE_M = 0.01
#: A *derived* support link (``parent`` + ``parentDerived``) is allowed to be embedded in
#: its supporter, or to hover above it, by a fraction of the smaller of the two heights:
#: ``bottom_child - top_parent`` in ``[-0.30, +0.50] * min(height_child, height_parent)``.
#: Calibrated on 1,259 parent links of the retail dump (median gap -0.03 m, 82% within
#: +-0.25 of a min height); evidence ``Docs/Research/KatamariBallRules/STACKS_IN_ORIGINAL.md``
#: sections 2.1/2.2.  ``stackedOn`` keeps the tighter, absolute ``STACK_TOLERANCE_M``: it is
#: a hand-authored seating claim, not a machine guess.
PARENT_EMBED_FRACTION = 0.30
PARENT_GAP_FRACTION = 0.50
#: A prop penetrating a solid structure deeper than this is an error.
STRUCTURE_PENETRATION_M = 0.02
#: Two props whose AABBs overlap by more than this fraction of the smaller
#: volume, without a declared relationship, are an error.
OVERLAP_FRACTION = 0.05
#: Manifest-vs-JSON AABB agreement (design section 5.5).
MANIFEST_AABB_TOLERANCE_M = 0.001
#: Ramp slope ceiling (design section 4.3 / 8.10).
RAMP_MAX_SLOPE_DEG = 45.0
#: Ramp foot may not rise more than this above the ground (or the step limit).
RAMP_FOOT_MAX_M = 0.05
#: Board thickness of an authored ramp.  A ``role=ramp`` structure is 4 cm thick and its
#: **+Y face is the walking surface** (``story_01``'s boards, ``LEVEL_AUTHORING`` 10).
RAMP_THICKNESS_M = 0.04
#: How far a ramp's *underside* may reach below the ground: one full board thickness
#: (plus the ordinary sink tolerance).  Because the authored ``bottom``/``top`` line is
#: the walking surface, the board hangs underneath it, so at a foot on the ground the low
#: corner is legitimately ``thickness * cos(slope)`` below ``y = ground`` - that is not a
#: sunk ramp, the ball still meets the top face at the design height.
RAMP_UNDERSIDE_ALLOWANCE_M = RAMP_THICKNESS_M + SINK_TOLERANCE_M

#: A curved ramp (``role=curve_ramp``) is authored as ONE arc and expanded into a chain of
#: slab segments (``CONTACT_MODEL_V2`` 5.7a).  ``curve_ramp`` is a *logical* structure: it
#: counts once against ``level.budget.ramps`` but its segments are static OBBs to the motor.
CURVE_CHORD_ERROR_PER_RADIUS = 0.05
#: The design's ``r_min``, the 0.30 m starter ball's radius (5.7's error budget base).  Used
#: when the level does not declare ``ballDiameterStart``.
CURVE_MIN_BALL_RADIUS_M = 0.15
#: The reference tessellator expands a **vertical** arc and nothing else.  ``left``/``right``
#: and ``helix`` are named in the design's JSON sketch but have no implementation, so they are
#: refused with a reason code instead of being silently bent the wrong way.
CURVE_BENDS = ("up", "down")
CURVE_UNSUPPORTED_BENDS = ("left", "right", "helix")
CURVE_MAX_ARC_DEG = 360.0
#: ``CONTACT_MODEL_V2`` 5.4/7.1: the motor's static OBB table holds this many solids.
STATIC_OBB_CAPACITY = 256
#: A segment's chord is stretched by ``1 + 2*s/L`` so the convex side has no crack; the
#: design caps that overlap at a quarter of the chord (5.7's "overlap > 0 and < 25 %").
CURVE_MAX_OVERLAP_FRACTION = 0.25
#: Curve numbers are stored like sizes (4 decimals); angles are degrees.
CURVE_DECIMALS = 4
#: The whole ``curve`` block.  ``segments: 0`` (or absent) means "auto by the chord budget".
CURVE_KEYS = ("radius_m", "arc_deg", "segments", "bend", "width_m", "thickness_m")
#: Cap on per-check detail lines so one broken file cannot flood the console.
DETAIL_LIMIT = 25

PROP_ROLES = ("ground", "boundary", "box", "ramp", "curve_ramp")
SHAPES = ("box", "plane")
COLLISIONS = ("box", "none")
CAMERA_TYPES = ("perspective", "ortho_top")

ROOT = Path(__file__).resolve().parents[2]
#: The Unity project root.  A def's ``model`` is a path **relative to this folder**
#: (``Assets/...``), which is exactly what ``AssetDatabase.LoadAssetAtPath`` takes.
UNITY_ROOT = ROOT / "VRChatKatamariDanacyUnity"
#: The one curved-ramp tessellator: the design's Python reference (see
#: :func:`curve_reference`).  The C# counterpart is ``Runtime/BallContact/CurveRampMath.cs``.
CURVE_REFERENCE_PATH = ROOT / "Docs/Design/BallMotor/contact_v2/contact_v2.py"
PROPS_DIR = ROOT / "Docs/Art/OriginalModels/props"
PROPS_EXPORTS_DIR = PROPS_DIR / "exports"
PROP_CATALOG = ROOT / "Docs/Design/Maps/PicnicLawn/prop_catalog.csv"
LEVELS_DIR = ROOT / "Docs/Design/Levels"

PROP_KEYS = ("itemId", "def", "kind", "zone", "group", "groupId", "position", "rotationQuat",
             "support", "stackedOn", "parent", "parentDerived", "aabb", "absorbDiameterReq",
             "absorbable", "valueUnits", "volumeK", "notes")
STRUCTURE_KEYS = ("id", "kind", "role", "shape", "center", "size", "curve", "rotationQuat",
                  "collision", "absorbable", "support", "meets", "material", "notes")
CAMERA_KEYS = ("id", "type", "location", "target", "lens", "coverage_m")
PRESET_KEYS = ("id", "camera", "engine", "res", "samples")
DEF_KEYS = ("id", "kind", "model", "valueUnits", "collider", "volumeK", "notes")
LEVEL_KEYS = ("id", "scene", "units", "convention", "ballStart", "playerSpawn",
              "ballDiameterStart", "ballDiameterMax", "groundY", "bounds",
              "spawnClearanceM", "budget", "expected", "zones", "arena")
ARENA_KEYS = ("id", "notes")
BUDGET_KEYS = ("planes", "boxes", "ramps", "blocking", "propSupports")
ENGINES = ("WORKBENCH", "EEVEE", "CYCLES")


# --------------------------------------------------------------------------- #
# numbers and vectors
# --------------------------------------------------------------------------- #
def clean_zero(value):
    """``-0.0`` -> ``0.0`` (JSON prints the sign, and a sign flip is diff noise)."""
    return 0.0 if value == 0 else value


def rounded(value, digits):
    return clean_zero(round(float(value), digits))


def rounded_seq(values, digits):
    return [rounded(v, digits) for v in values]


def as_float(value, what, report=None):
    """Float or ``None``; records a readable error instead of raising."""
    try:
        number = float(value)
    except (TypeError, ValueError):
        if report is not None:
            report.err("%s is not a number: %r" % (what, value))
        return None
    if math.isnan(number) or math.isinf(number):
        if report is not None:
            report.err("%s is not finite: %r" % (what, value))
        return None
    return number


def as_vec3(value, what, report=None):
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        if report is not None:
            report.err("%s must be a list of 3 numbers, got %r" % (what, value))
        return None
    numbers = [as_float(v, "%s[%d]" % (what, i), report) for i, v in enumerate(value)]
    if any(n is None for n in numbers):
        return None
    return tuple(numbers)


def as_quat_xyzw(value, what, report=None):
    if not isinstance(value, (list, tuple)) or len(value) != 4:
        if report is not None:
            report.err("%s must be a list of 4 numbers (x, y, z, w), got %r" % (what, value))
        return None
    numbers = [as_float(v, "%s[%d]" % (what, i), report) for i, v in enumerate(value)]
    if any(n is None for n in numbers):
        return None
    length = math.sqrt(sum(n * n for n in numbers))
    if length < 1e-6:
        if report is not None:
            report.err("%s is a zero quaternion" % what)
        return None
    if abs(length - 1.0) > 1e-3:
        if report is not None:
            report.err("%s is not unit length (|q| = %.6f)" % (what, length))
        return None
    return tuple(n / length for n in numbers)


def add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def scale(a, factor):
    return (a[0] * factor, a[1] * factor, a[2] * factor)


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def length(a):
    return math.sqrt(dot(a, a))


def normalized(a):
    size = length(a)
    if size < 1e-12:
        raise ValueError("cannot normalize a zero vector")
    return (a[0] / size, a[1] / size, a[2] / size)


def lerp(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)


# --------------------------------------------------------------------------- #
# coordinate conversion: the single boundary (Blender Z-up <-> Unity Y-up)
# --------------------------------------------------------------------------- #
def blender_to_unity_position(position):
    """Blender ``(x, y, z)`` -> Unity ``(x, z, y)``; its own inverse."""
    return (position[0], position[2], position[1])


#: The position map is an involution, so one name is enough for both directions.
unity_to_blender_position = blender_to_unity_position


def blender_to_unity_quat(quat_wxyz):
    """Blender/mathutils ``(w, x, y, z)`` -> JSON ``(x, y, z, w)``.

    The axis swap is improper, so the conjugated rotation turns the opposite way
    about the mapped axis: Blender "Y +30 deg" is Unity "Z -30 deg"
    (``LEVEL_AUTHORING`` section 1.5).  Sign-flipping the vector part is exactly
    that, and the map is an involution like the position map.
    """
    w, x, y, z = quat_wxyz
    return (-x, -z, -y, w)


def unity_to_blender_quat(quat_xyzw):
    """JSON ``(x, y, z, w)`` -> mathutils ``(w, x, y, z)``."""
    x, y, z, w = quat_xyzw
    return (w, -x, -z, -y)


def canonical_quat(quat_xyzw):
    """Same rotation with a canonical sign, so ``q`` and ``-q`` cannot differ in a diff."""
    x, y, z, w = quat_xyzw
    length_ = math.sqrt(x * x + y * y + z * z + w * w)
    if length_ < 1e-12:
        raise ValueError("zero quaternion")
    x, y, z, w = x / length_, y / length_, z / length_, w / length_
    if w < 0.0:
        x, y, z, w = -x, -y, -z, -w
    elif w == 0.0:
        for component in (x, y, z):
            if component != 0.0:
                if component < 0.0:
                    x, y, z, w = -x, -y, -z, -w
                break
    return (clean_zero(x), clean_zero(y), clean_zero(z), clean_zero(w))


def quat_mul(a, b):
    """Hamilton product: the result applies ``b`` first, then ``a``."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def quat_rotate(quat_xyzw, vector):
    """Rotate ``vector`` by the rotation ``quat_xyzw``."""
    x, y, z, w = quat_xyzw
    m = quat_to_matrix((x, y, z, w))
    return (m[0][0] * vector[0] + m[0][1] * vector[1] + m[0][2] * vector[2],
            m[1][0] * vector[0] + m[1][1] * vector[1] + m[1][2] * vector[2],
            m[2][0] * vector[0] + m[2][1] * vector[1] + m[2][2] * vector[2])


def quat_to_matrix(quat_xyzw):
    """Row-major 3x3 rotation matrix (column-vector convention)."""
    x, y, z, w = quat_xyzw
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    return ((1.0 - 2.0 * (yy + zz), 2.0 * (xy - wz), 2.0 * (xz + wy)),
            (2.0 * (xy + wz), 1.0 - 2.0 * (xx + zz), 2.0 * (yz - wx)),
            (2.0 * (xz - wy), 2.0 * (yz + wx), 1.0 - 2.0 * (xx + yy)))


def quat_axis_angle(axis, angle_rad):
    axis = normalized(axis)
    half = angle_rad * 0.5
    sin_half = math.sin(half)
    return canonical_quat((axis[0] * sin_half, axis[1] * sin_half, axis[2] * sin_half, math.cos(half)))


def quat_from_yaw(yaw_rad):
    """Yaw about Unity +Y (positive = the project's clockwise yaw convention)."""
    return quat_axis_angle((0.0, 1.0, 0.0), yaw_rad)


def quat_between(from_vector, to_vector):
    """Shortest-arc rotation taking ``from_vector`` onto ``to_vector``."""
    a = normalized(from_vector)
    b = normalized(to_vector)
    d = max(-1.0, min(1.0, dot(a, b)))
    if d > 1.0 - 1e-12:
        return (0.0, 0.0, 0.0, 1.0)
    if d < -1.0 + 1e-12:
        # Antiparallel: any perpendicular axis is a valid 180 degree turn.
        helper = (1.0, 0.0, 0.0) if abs(a[0]) < 0.9 else (0.0, 1.0, 0.0)
        axis = normalized(cross(a, helper))
        return (axis[0], axis[1], axis[2], 0.0)
    axis = cross(a, b)
    return canonical_quat((axis[0], axis[1], axis[2], 1.0 + d))


def quat_from_axes(axis_x, axis_y, axis_z):
    """Quaternion (xyzw) of the right-handed rotation with these local axes as columns.

    ``axis_x``/``axis_y``/``axis_z`` are the world directions of the box's local axes, so
    ``quat_rotate(quat, (1,0,0)) == axis_x`` - the exact inverse of :func:`quat_to_matrix`,
    which is what a curve segment's frame needs (its axes are built from the arc, not from
    an angle about one axis).
    """
    m00, m10, m20 = axis_x
    m01, m11, m21 = axis_y
    m02, m12, m22 = axis_z
    trace = m00 + m11 + m22
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        w, x, y, z = 0.25 * s, (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2.0
        w, x, y, z = (m21 - m12) / s, 0.25 * s, (m01 + m10) / s, (m02 + m20) / s
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2.0
        w, x, y, z = (m02 - m20) / s, (m01 + m10) / s, 0.25 * s, (m12 + m21) / s
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11) * 2.0
        w, x, y, z = (m10 - m01) / s, (m02 + m20) / s, (m12 + m21) / s, 0.25 * s
    return canonical_quat((x, y, z, w))


def align_up_to(normal, yaw_rad=0.0):
    """Quaternion that puts a model's local +Y on ``normal``, yawed about it.

    This is the fix for the measured authoring bug "an apple on the tilted plank
    was half-buried": a prop seated with ``y = height / 2`` only touches an
    inclined surface if it is also *turned* onto that surface.
    """
    align = quat_between((0.0, 1.0, 0.0), normal)
    return canonical_quat(quat_mul(quat_axis_angle(normal, yaw_rad), align))


# --------------------------------------------------------------------------- #
# AABB
# --------------------------------------------------------------------------- #
def world_half_extents(size, quat_xyzw):
    """Half extents of the world-axis-aligned box around a rotated box."""
    m = quat_to_matrix(quat_xyzw)
    hx, hy, hz = size[0] * 0.5, size[1] * 0.5, size[2] * 0.5
    return (abs(m[0][0]) * hx + abs(m[0][1]) * hy + abs(m[0][2]) * hz,
            abs(m[1][0]) * hx + abs(m[1][1]) * hy + abs(m[1][2]) * hz,
            abs(m[2][0]) * hx + abs(m[2][1]) * hy + abs(m[2][2]) * hz)


def world_aabb(position, size, quat_xyzw):
    """``((minx, miny, minz), (maxx, maxy, maxz))`` of a rotated box."""
    half = world_half_extents(size, quat_xyzw)
    return (sub(position, half), add(position, half))


def box_corners(position, size, quat_xyzw):
    """The eight world corners of a rotated box."""
    m = quat_to_matrix(quat_xyzw)
    hx, hy, hz = size[0] * 0.5, size[1] * 0.5, size[2] * 0.5
    corners = []
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            for sz in (-1.0, 1.0):
                local = (sx * hx, sy * hy, sz * hz)
                corners.append(add(position, (m[0][0] * local[0] + m[0][1] * local[1] + m[0][2] * local[2],
                                             m[1][0] * local[0] + m[1][1] * local[1] + m[1][2] * local[2],
                                             m[2][0] * local[0] + m[2][1] * local[1] + m[2][2] * local[2])))
    return corners


def aabb_volume(box):
    low, high = box
    return max(0.0, high[0] - low[0]) * max(0.0, high[1] - low[1]) * max(0.0, high[2] - low[2])


def aabb_intersection(a, b):
    """Intersection volume of two axis-aligned boxes; 0 when they only touch."""
    low = (max(a[0][0], b[0][0]), max(a[0][1], b[0][1]), max(a[0][2], b[0][2]))
    high = (min(a[1][0], b[1][0]), min(a[1][1], b[1][1]), min(a[1][2], b[1][2]))
    if high[0] <= low[0] or high[1] <= low[1] or high[2] <= low[2]:
        return 0.0
    return (high[0] - low[0]) * (high[1] - low[1]) * (high[2] - low[2])


def aabb_distance_to_point(box, point):
    """Distance from a point to the nearest point of a box (0 when inside)."""
    low, high = box
    total = 0.0
    for i in range(3):
        if point[i] < low[i]:
            total += (low[i] - point[i]) ** 2
        elif point[i] > high[i]:
            total += (point[i] - high[i]) ** 2
    return math.sqrt(total)


def plane_distance(normal, point_on_plane, point):
    """Signed distance from ``point`` to the plane (positive on the ``normal`` side)."""
    return dot(normal, sub(point, point_on_plane))


# --------------------------------------------------------------------------- #
# structures
# --------------------------------------------------------------------------- #
def structure_up(structure):
    return quat_rotate(structure["rotationQuat"], (0.0, 1.0, 0.0))


def boundary_plane(structure):
    """The plane a ``role=boundary`` structure stands for: through its centre, normal +Y.

    The generic :func:`structure_top_plane` is for boxes with a walkable top; a wall is a
    plane, and its own centre is on it. ``LevelArenaHost`` reads a wall's local +Z as the
    normal and its world position as the offset, so the importer turns the object to look
    along this normal.
    """
    return structure_up(structure), structure["center"]


def structure_top_plane(structure):
    """``(normal, point)`` of the structure's +Y face.

    For ``role=ramp`` this face is the **walking surface**: it is the plane the layout's
    ``bottom``/``top`` line lands on (the 4 cm board hangs below it, see
    ``RAMP_THICKNESS_M``).  Props with ``support=<ramp id>`` are seated on this plane, and
    P3's ``AddRamp(center, n, right, halfWidth, halfRun)`` has to take ``center`` from it
    rather than from the box centre.

    A ``role=curve_ramp`` has no single top face (its surface bends), so it raises: a prop
    cannot be seated on a curve, and the validator refuses that declaration first.
    """
    if structure.get("role") == "curve_ramp":
        raise ValueError("structure %r is a curve_ramp: it has no single top plane"
                         % structure.get("id"))
    normal = structure_up(structure)
    point = add(structure["center"], scale(normal, structure["size"][1] * 0.5))
    return normal, point


def plane_height_at(normal, point, x, z):
    """Height of a (non-vertical) plane at ``(x, z)``; ``None`` when vertical."""
    if abs(normal[1]) < 1e-6:
        return None
    return point[1] - (normal[0] * (x - point[0]) + normal[2] * (z - point[2])) / normal[1]


def structure_slope_deg(structure):
    """Tilt of the structure's +Y face away from world up.

    A curve is reported by its **steepest** point, ``arc_deg`` (the surface starts flat at
    the start frame and is ``arc_deg`` off it at the far end).
    """
    if structure.get("role") == "curve_ramp":
        try:
            return abs(float(structure["curve"]["arc_deg"]))
        except (KeyError, TypeError, ValueError):
            return 0.0
    up = structure_up(structure)
    cosine = max(-1.0, min(1.0, abs(up[1])))
    return math.degrees(math.acos(cosine))


def structure_world_aabb(structure, ball_radius_m=None):
    if structure.get("role") == "curve_ramp":
        return curve_world_aabb(structure, ball_radius_m)
    return world_aabb(structure["center"], structure["size"], structure["rotationQuat"])


def structure_top_height(structure, ball_radius_m=None):
    """Highest world Y of the structure (top of its world AABB)."""
    return structure_world_aabb(structure, ball_radius_m)[1][1]


def to_local(point, center, quat_xyzw):
    return quat_rotate((quat_xyzw[0], quat_xyzw[1], quat_xyzw[2], -quat_xyzw[3]),
                       sub(point, center))


def obb_penetration(point, structure):
    """How deep ``point`` sits inside the structure's oriented box (<= 0 outside).

    Uses the smallest distance to a face, which is the penetration depth along
    the closest face for a point inside the box.
    """
    local = to_local(point, structure["center"], structure["rotationQuat"])
    half = (structure["size"][0] * 0.5, structure["size"][1] * 0.5, structure["size"][2] * 0.5)
    return min(half[i] - abs(local[i]) for i in range(3))


def structure_contains_point(point, structure, margin=0.0):
    local = to_local(point, structure["center"], structure["rotationQuat"])
    half = (structure["size"][0] * 0.5 + margin,
            structure["size"][1] * 0.5 + margin,
            structure["size"][2] * 0.5 + margin)
    return all(abs(local[i]) <= half[i] for i in range(3))


# --------------------------------------------------------------------------- #
# curved ramps: one arc in the data, a chain of slab segments to the motor
# --------------------------------------------------------------------------- #
def curve_reference():
    """The design's curved-ramp reference module (``contact_v2``), loaded from its file.

    This is the **one** tessellator: ``AGENTS.md`` forbids a second copy of a piece of
    logic, and ``CONTACT_MODEL_V2`` 7.2 keeps the Python model and the C#
    (``Runtime/BallContact/CurveRampMath.cs``) numerically identical.  The module is not
    importable as a package when the CLI runs from outside the repo root, so it is loaded
    by path once and cached in :data:`sys.modules`; it is standard-library only, so this
    also works inside Blender.
    """
    name = "katamari_contact_v2_reference"
    module = sys.modules.get(name)
    if module is not None:
        return module
    if not CURVE_REFERENCE_PATH.is_file():
        raise FileNotFoundError("the curved-ramp reference is missing: %s"
                                % CURVE_REFERENCE_PATH)
    spec = importlib.util.spec_from_file_location(name, str(CURVE_REFERENCE_PATH))
    if spec is None or spec.loader is None:
        raise ImportError("cannot load %s" % CURVE_REFERENCE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    sys.modules[name] = module
    return module


def curve_ball_radius_m(level):
    """``r_min``: half the level's starter ball, never below the design's 0.15 m.

    The chord budget is stated per ball radius, and the *smallest* ball a level plays with
    is the one that must not feel the chord bump (``CONTACT_MODEL_V2`` 5.7).
    """
    diameter = None
    if isinstance(level, dict):
        diameter = as_float(level.get("ballDiameterStart"), "level.ballDiameterStart")
    if diameter is None or diameter <= 0.0:
        return CURVE_MIN_BALL_RADIUS_M
    return max(diameter * 0.5, CURVE_MIN_BALL_RADIUS_M)


def auto_curve_segments(radius_m, arc_deg, ball_radius_m):
    """Smallest segment count whose sagitta fits the 5 %-of-``r_min`` budget (5.7's table).

    The predicate is the reference's :func:`curve_chord_budget`, so the Python side and the
    C# ``CurveRampMath.IsCurveWithinChordBudget`` loop answer the same count.
    """
    budget = curve_reference().curve_chord_budget
    for count in range(1, STATIC_OBB_CAPACITY + 1):
        if budget(radius_m, count, arc_deg, ball_radius_m, CURVE_CHORD_ERROR_PER_RADIUS):
            return count
    return STATIC_OBB_CAPACITY


def curve_segment_count(structure, ball_radius_m=None):
    """Resolved segment count of one curve (0 when the entry cannot be read).

    A count outside ``1..STATIC_OBB_CAPACITY`` is 0 here: the entry is invalid (the
    validator reports it with ``[curve_segments]``), and expanding it would cost the
    capacity check a huge allocation for a document that is already red.
    """
    try:
        curve = structure["curve"]
        declared = curve.get("segments", 0) or 0
        declared = int(declared)
        if declared < 0 or declared > STATIC_OBB_CAPACITY:
            return 0
        if declared > 0:
            return declared
        return auto_curve_segments(float(curve["radius_m"]), float(curve["arc_deg"]),
                                   CURVE_MIN_BALL_RADIUS_M if ball_radius_m is None
                                   else ball_radius_m)
    except (KeyError, TypeError, ValueError):
        return 0


def expand_curve_ramp(structure, ball_radius_m=None):
    """One ``role=curve_ramp`` entry as its deterministic chain of slab segments.

    Returns ``(segments, metrics)``.  Each segment is a dict with the frame the Unity
    importer builds (``center``, ``rotationQuat``, ``size``, ``normal``, ``tangent``,
    ``axisX``, ``index``, ``id``); ``metrics`` is the reference model's own read-out
    (``segments``, ``delta_deg``, ``sagitta_m``, ``normal_jump_deg``, ``segment_length_m``,
    ``overlap``).

    The arc maths is ``contact_v2.tessellate_vertical_curve`` - not a second copy.  Two
    conventions sit on top of it:

    * ``structure.center`` is the arc's start point on the **slab centrelines** (the first
      segment's centre), exactly like a straight ramp's ``center`` is its box centre; the
      walking surface is half a thickness along the local +Y at that point.
    * ``structure.rotationQuat`` is the start frame: local +Z is ``forward`` (the direction
      the arc leaves in, horizontal), local +Y is the start surface normal.

    ``bend: "down"`` is the same up-arc tessellated in a mirrored frame (``up`` negated) and
    then un-mirrored: the centres and tangents are already the descending arc's, and the
    surface normal is the negated one.  ``CurveRampMath.cs`` does exactly that, so neither
    side needs a second arc formula (the C# is not this file's to extend).
    """
    curve = structure["curve"]
    center = tuple(float(v) for v in structure["center"])
    quat = tuple(float(v) for v in structure["rotationQuat"])
    radius = float(curve["radius_m"])
    arc = float(curve["arc_deg"])
    width = float(curve["width_m"])
    thickness = float(curve["thickness_m"])
    bend = curve.get("bend") or "up"
    if bend not in CURVE_BENDS:
        raise ValueError("bend %r is not one of %s" % (bend, list(CURVE_BENDS)))
    segments = curve_segment_count(structure, ball_radius_m)
    if segments < 1 or segments > STATIC_OBB_CAPACITY:
        raise ValueError("segments %d is outside 1..%d" % (segments, STATIC_OBB_CAPACITY))
    forward = quat_rotate(quat, (0.0, 0.0, 1.0))
    up = quat_rotate(quat, (0.0, 1.0, 0.0))
    mirrored = bend == "down"
    obbs, metrics = curve_reference().tessellate_vertical_curve(
        center, forward, (-up[0], -up[1], -up[2]) if mirrored else up,
        radius, arc, segments, width, thickness)
    metrics = dict(metrics)
    metrics["segments"] = segments
    metrics["bend"] = bend
    chain = []
    for index, obb in enumerate(obbs):
        normal = obb.axis_y
        if mirrored:
            normal = (-normal[0], -normal[1], -normal[2])
        tangent = obb.axis_z
        axis_x = cross(normal, tangent)
        chain.append({
            "index": index,
            "id": "%s_%02d" % (structure.get("id"), index),
            "center": obb.center,
            "rotationQuat": quat_from_axes(axis_x, normal, tangent),
            "size": (width, thickness, metrics["segment_length_m"] * metrics["overlap"]),
            "normal": normal,
            "tangent": tangent,
            "axisX": axis_x,
        })
    return chain, metrics


def curve_world_aabb(structure, ball_radius_m=None):
    """Union of a curve's segment AABBs (a curve has no single authored box)."""
    try:
        chain, _ = expand_curve_ramp(structure, ball_radius_m)
    except (KeyError, TypeError, ValueError, FileNotFoundError):
        try:
            center = tuple(float(v) for v in structure["center"])
        except (KeyError, TypeError, ValueError):
            center = (0.0, 0.0, 0.0)
        return (center, center)
    low = chain[0]["center"]
    high = chain[0]["center"]
    for segment in chain:
        box = world_aabb(segment["center"], segment["size"], segment["rotationQuat"])
        low = tuple(min(low[i], box[0][i]) for i in range(3))
        high = tuple(max(high[i], box[1][i]) for i in range(3))
    return (low, high)


def structure_solid_boxes(structure, ball_radius_m=None):
    """The oriented boxes a structure is *solid* of, as ``(center, size, quat)`` dicts.

    A box/ramp is one; a curve is its whole slab chain, so a prop buried in a curve is
    caught by the same penetration rule as a prop buried in a box.
    """
    if structure.get("role") == "curve_ramp":
        chain, _ = expand_curve_ramp(structure, ball_radius_m)
        return [{"center": segment["center"], "size": segment["size"],
                 "rotationQuat": segment["rotationQuat"]} for segment in chain]
    return [structure]


def static_obb_count(doc, ball_radius_m=None):
    """Static OBBs the motor will bake: boxes, straight ramp slabs and curve segments."""
    radius = ball_radius_m if ball_radius_m is not None \
        else curve_ball_radius_m(doc.get("level"))
    total = 0
    for structure in doc.get("structures") or []:
        if not isinstance(structure, dict):
            continue
        role = structure.get("role")
        if role in ("box", "ramp"):
            total += 1
        elif role == "curve_ramp":
            total += curve_segment_count(structure, radius)
    return total


# --------------------------------------------------------------------------- #
# seating and the absorb gate
# --------------------------------------------------------------------------- #
def seat_position(position, size, support_normal, support_point):
    """Put a box's bottom face on a plane, keeping its centre as close as possible.

    The current centre is projected onto the plane and pushed out along the
    normal by half the box height, which is exact once the box is aligned to the
    normal (:func:`align_up_to`).
    """
    projected = sub(position, scale(support_normal, plane_distance(support_normal, support_point, position)))
    return add(projected, scale(support_normal, size[1] * 0.5))


def seat_pose(position, size, support_normal, support_point, yaw_rad=0.0):
    """``(position, rotationQuat)`` of a prop seated on a (possibly tilted) surface."""
    quat = align_up_to(support_normal, yaw_rad)
    return seat_position(position, size, support_normal, support_point), quat


def seat_pose_on_structure(position, size, structure, yaw_rad=0.0):
    normal, point = structure_top_plane(structure)
    return seat_pose(position, size, normal, point, yaw_rad)


def align_quat_to_normal(quat_xyzw, normal):
    """Turn an existing rotation onto ``normal`` with the smallest extra rotation.

    Used by ``levelbuild.py seat``: it keeps whatever yaw the prop already has
    instead of inventing one, so re-seating an author's arrangement does not
    spin every prop to a new heading.
    """
    up = quat_rotate(quat_xyzw, (0.0, 1.0, 0.0))
    return canonical_quat(quat_mul(quat_between(up, normal), quat_xyzw))


def reseat(position, size, quat_xyzw, normal, point):
    """``(position, quat)`` with the box's bottom face on the plane, yaw kept."""
    new_quat = align_quat_to_normal(quat_xyzw, normal)
    return seat_position(position, size, normal, point), new_quat


def floor_mm(meters):
    """Length in WHOLE millimetres, floored with :data:`MM_EPSILON` on the boundary."""
    return int(math.floor(float(meters) * 1000.0 + MM_EPSILON))


def volume_k_error(volume_k):
    """Why a declared ``volumeK`` is unusable, or ``None`` when it is a legal factor."""
    if isinstance(volume_k, bool) or not isinstance(volume_k, (int, float)):
        return "must be a number (the original's default is %.2f)" % DEFAULT_VOLUME_K
    value = float(volume_k)
    if not math.isfinite(value):
        return "must be finite"
    if value <= 0.0:
        return "must be > 0 (0 would price every item at zero volume)"
    if value > VOLUME_K_MAX:
        return "must be <= %g (0.70 is the original default; a typo like 70 is refused)" % VOLUME_K_MAX
    return None


def effective_volume_k(prop, catalog=None):
    """The ``k`` of one prop: its own ``volumeK``, else its def's, else 0.70."""
    catalog = catalog or {}
    value = prop.get("volumeK")
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return float(value)
    entry = catalog.get(prop.get("def")) or {}
    value = entry.get("volumeK")
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return float(value)
    return DEFAULT_VOLUME_K


def absorb_diameter_req_exact(size, volume_k=DEFAULT_VOLUME_K):
    """``D_req`` in metres, unrounded: ``(60 * k * a * b * c / pi) ** (1/3)``.

    ``V = k * a * b * c`` is the original's item volume (``PARITY_AUDIT.md`` G13), so this is
    the smallest ball whose own volume is ten of the item's.  Only the comparison quantity
    (:func:`absorb_diameter_req`) is floored; this one exists for callers that want the
    continuous number (the parity harness's "distance to the boundary").
    """
    a, b, c = size
    if a <= 0.0 or b <= 0.0 or c <= 0.0:
        raise ValueError("absorb_diameter_req needs a positive box size, got %r" % (size,))
    if volume_k is None:
        volume_k = DEFAULT_VOLUME_K
    return (VOLUME_GATE_FACTOR * float(volume_k) * a * b * c / math.pi) ** (1.0 / 3.0)


def absorb_diameter_req_mm(size, volume_k=DEFAULT_VOLUME_K):
    """The original's ``Req Pickup Size``: ``D_req`` FLOORED to whole millimetres."""
    return floor_mm(absorb_diameter_req_exact(size, volume_k))


def absorb_diameter_req(size, volume_k=DEFAULT_VOLUME_K):
    """``Req`` in metres as an exact whole-millimetre value (0.176, not 0.1762...).

    One comparison quantity for the data side: a prop is absorbable by a ball of diameter
    ``D`` iff ``floor(1000*D) >= absorb_diameter_req_mm(size, k)`` (the runtime's
    ``PickupAbsorbMath.IsBeyondAbsorbLimit``).  Because it is floors to mm, the value is
    exactly representable with three decimals, which is what ``absorbDiameterReq`` stores.
    """
    return absorb_diameter_req_mm(size, volume_k) / 1000.0


def is_absorbable(size, ball_diameter_max, volume_k=DEFAULT_VOLUME_K):
    """Whole-mm comparison, the same one the runtime gate makes."""
    return absorb_diameter_req_mm(size, volume_k) <= floor_mm(ball_diameter_max)


# --------------------------------------------------------------------------- #
# prop manifests (real size + centre pivot are the truth, not the JSON copy)
# --------------------------------------------------------------------------- #
def manifest_path(def_id, exports_dir=None):
    return Path(exports_dir or PROPS_EXPORTS_DIR) / (def_id + ".manifest.json")


def load_manifest(def_id, exports_dir=None):
    path = manifest_path(def_id, exports_dir)
    if not path.is_file():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def manifest_aabb(dimensions_m):
    """Manifest ``dimensions_m`` (Blender axes, z = height) -> Unity ``aabb``."""
    return (clean_zero(round(dimensions_m["x"], SIZE_DECIMALS)),
            clean_zero(round(dimensions_m["z"], SIZE_DECIMALS)),
            clean_zero(round(dimensions_m["y"], SIZE_DECIMALS)))


def _manifest_entry(document, source=None):
    """One manifest document reduced to the facts the validator reads, or ``None``."""
    dimensions = document.get("dimensions_m") or {}
    try:
        aabb = manifest_aabb(dimensions)
    except (KeyError, TypeError):
        return None
    return {
        "aabb": aabb,
        "category": document.get("category") or "",
        "longest_m": dimensions.get("longest"),
        "manifest": document,
        "source": str(source) if source is not None else None,
    }


def _is_repo_exports_manifest(entry):
    """Did this entry come from the repo's own ``props/exports`` tree?"""
    source = (entry or {}).get("source")
    if not source:
        return False
    try:
        Path(source).resolve().relative_to(PROPS_EXPORTS_DIR.resolve())
    except (OSError, ValueError):
        return False
    return True


def load_manifests(exports_dir=None):
    """``{def: {"aabb": (x, y, z), "category": str, "manifest": dict}}``."""
    directory = Path(exports_dir or PROPS_EXPORTS_DIR)
    manifests = {}
    if not directory.is_dir():
        return manifests
    for path in sorted(directory.glob("*.manifest.json")):
        document = json.loads(path.read_text(encoding="utf-8"))
        def_id = document.get("id") or path.name[:-len(".manifest.json")]
        entry = _manifest_entry(document, path)
        if entry is not None:
            manifests[def_id] = entry
    return manifests


def model_manifest_path(model):
    """``<modelDir>/../exports/<def>.manifest.json`` for a def's declared ``model``.

    A level built from an external asset pack keeps its manifests next to the models
    (``Assets/<pack>/Models/<def>/<def>.fbx`` beside
    ``Assets/<pack>/Models/exports/<def>.manifest.json``).  So the rule is one directory
    up from the model's own folder, and ``--manifests DIR`` still wins when it carries
    the def.
    """
    path = Path(model)
    name = path.name
    stem = name[:-len(".fbx")] if name.endswith(".fbx") else path.stem
    return UNITY_ROOT / path.parent.parent / "exports" / (stem + ".manifest.json")


def load_model_manifest(model):
    """The manifest shipped next to a def's model, or ``None`` when there is none."""
    path = model_manifest_path(model)
    if not path.is_file():
        return None
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    return _manifest_entry(document, path)


def manifests_with_models(doc, manifests=None):
    """Reconcile a declared ``model`` with the manifest trees.

    A def that names its own model is no longer defined by a repo asset
    (``Docs/Art/OriginalModels/props/<def>``).  So the **repo's own** manifest for that id
    must not be applied - it describes a different file (the ``cookie`` / ``strawberry`` /
    ``watermelon`` id collisions).  The caller's manifest wins when it is not from the repo
    exports tree (``--manifests DIR``, a level's own tree); otherwise the manifest next to
    the model (:func:`model_manifest_path`) is used.  A def with neither is reported by
    ``_check_defs``/``_check_props`` rather than silently skipped.
    """
    if manifests is None:
        manifests = load_manifests()
    merged = dict(manifests)
    for entry in doc.get("defs") or []:
        if not isinstance(entry, dict):
            continue
        def_id = entry.get("id")
        model = entry.get("model")
        if not isinstance(def_id, str) or not def_id:
            continue
        if not isinstance(model, str) or not model:
            continue
        if def_id in merged and not _is_repo_exports_manifest(merged[def_id]):
            continue
        merged.pop(def_id, None)
        loaded = load_model_manifest(model)
        if loaded is not None:
            merged[def_id] = loaded
    return merged


def def_model(catalog, def_id):
    """The def's declared ``model`` (a Unity-relative .fbx path), or ``None``."""
    entry = catalog.get(def_id) or {}
    model = entry.get("model")
    if isinstance(model, str) and model.strip():
        return model.strip()
    return None


def overlap_budget(doc):
    """``level.expected.overlapBudget`` (a whole number >= 0) or ``None``.

    Absent means the historic rule (every pair over 5 % is an error); a declared budget
    turns the count into the gate, so one dense authentic room can be validated without
    relaxing the rule for every level.  A malformed value is reported by
    ``_check_expected`` and ignored here.
    """
    expected = (doc.get("level") or {}).get("expected")
    if not isinstance(expected, dict):
        return None
    value = expected.get("overlapBudget")
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        return None
    return value


# --------------------------------------------------------------------------- #
# canonical document (deterministic bytes; this is what --format writes)
# --------------------------------------------------------------------------- #
def _ordered(source, keys):
    return {key: source[key] for key in keys if key in source}


def _sort_key(item_id):
    return (str(item_id))


def _canonical_curve(curve):
    """A ``curve`` block in canonical form.

    ``segments`` is kept only when the author declared it (absent or 0 = auto by the chord
    budget), because writing the resolved count back would turn a later radius edit into a
    stale "segments_coarse" error instead of an automatic re-tessellation.
    """
    out = {}
    for key in CURVE_KEYS:
        if key not in curve:
            continue
        value = curve[key]
        if key == "segments":
            out[key] = int(value)
        elif key == "bend":
            out[key] = str(value)
        else:
            out[key] = rounded(value, CURVE_DECIMALS)
    return out


def canonical_doc(doc, manifests=None):
    """The document in canonical form: sorted, rounded, ``-0.0`` cleaned.

    ``absorbDiameterReq`` and ``absorbable`` are recomputed here (they are
    derived fields, design section 4.2), so a hand-edited file cannot keep a
    stale gate.
    """
    manifests = manifests if manifests is not None else load_manifests()
    catalog = def_catalog(doc)
    level = dict(doc.get("level") or {})
    ball_max = level.get("ballDiameterMax")
    ball_max = 0.0 if ball_max is None else float(ball_max)

    level_out = {}
    for key in LEVEL_KEYS:
        if key not in level:
            continue
        value = level[key]
        if key in ("ballStart", "playerSpawn"):
            value = rounded_seq(value, POSITION_DECIMALS)
        elif key in ("ballDiameterStart", "ballDiameterMax", "groundY", "spawnClearanceM"):
            value = rounded(value, SIZE_DECIMALS)
        elif key == "bounds":
            value = [rounded_seq(pair, POSITION_DECIMALS) for pair in value]
        elif key == "zones":
            value = [_ordered(zone, ("id", "rect", "notes")) for zone in sorted(
                value, key=lambda zone: str(zone.get("id", "")))]
        elif key == "arena":
            value = _ordered(value, ARENA_KEYS)
        level_out[key] = value

    props_out = []
    for prop in sorted(doc.get("props") or [], key=lambda p: _sort_key(p.get("itemId", ""))):
        out = {}
        for key in PROP_KEYS:
            if key not in prop:
                continue
            value = prop[key]
            if key == "position":
                value = rounded_seq(value, POSITION_DECIMALS)
            elif key == "rotationQuat":
                value = rounded_seq(canonical_quat(tuple(float(v) for v in value)), QUAT_DECIMALS)
            elif key == "aabb":
                value = rounded_seq(value, SIZE_DECIMALS)
            elif key == "absorbDiameterReq":
                continue      # recomputed below
            elif key == "absorbable":
                continue      # recomputed below
            elif key == "valueUnits":
                # A JSON integer, never a float (Docs/OPEN_DECISIONS.md (j)). Python ints are
                # unbounded, so a declared value past int32 - the widened host column's range,
                # (r) step B - is stored and re-emitted verbatim; int() is the identity for it.
                value = int(value)
            elif key == "volumeK":
                value = rounded(value, 4)
            out[key] = value
        aabb = out.get("aabb")
        if aabb and all(v > 0 for v in aabb):
            volume_k = effective_volume_k(prop, catalog)
            out["absorbDiameterReq"] = rounded(absorb_diameter_req(aabb, volume_k),
                                               DIAMETER_DECIMALS)
            out["absorbable"] = is_absorbable(aabb, ball_max, volume_k)
        props_out.append(_ordered(out, PROP_KEYS))

    structures_out = []
    for structure in sorted(doc.get("structures") or [], key=lambda s: _sort_key(s.get("id", ""))):
        out = {}
        for key in STRUCTURE_KEYS:
            if key not in structure:
                continue
            value = structure[key]
            if key == "center":
                value = rounded_seq(value, POSITION_DECIMALS)
            elif key == "size":
                value = rounded_seq(value, SIZE_DECIMALS)
            elif key == "curve":
                value = _canonical_curve(value)
            elif key == "rotationQuat":
                value = rounded_seq(canonical_quat(tuple(float(v) for v in value)), QUAT_DECIMALS)
            out[key] = value
        curve = structure.get("curve")
        if structure.get("role") == "curve_ramp" and isinstance(curve, dict):
            # `size` is derived for a curve (there is no authored box): [width, thickness, 0].
            try:
                out["size"] = [rounded(curve["width_m"], SIZE_DECIMALS),
                               rounded(curve["thickness_m"], SIZE_DECIMALS), 0.0]
            except KeyError:
                pass
        structures_out.append(_ordered(out, STRUCTURE_KEYS))

    cameras_out = []
    for camera in sorted(doc.get("cameras") or [], key=lambda c: _sort_key(c.get("id", ""))):
        out = {}
        for key in CAMERA_KEYS:
            if key not in camera:
                continue
            value = camera[key]
            if key in ("location", "target"):
                value = rounded_seq(value, CAMERA_DECIMALS)
            elif key == "coverage_m":
                value = rounded(value, CAMERA_DECIMALS)
            elif key == "lens":
                number = float(value)
                value = int(number) if number.is_integer() else rounded(number, 1)
            out[key] = value
        cameras_out.append(_ordered(out, CAMERA_KEYS))

    presets_out = []
    for preset in sorted(doc.get("renderPresets") or [], key=lambda p: _sort_key(p.get("id", ""))):
        out = {}
        for key in PRESET_KEYS:
            if key not in preset:
                continue
            value = preset[key]
            if key == "res":
                value = [int(v) for v in value]
            elif key == "samples":
                value = int(value)
            out[key] = value
        presets_out.append(_ordered(out, PRESET_KEYS))

    defs_out = []
    for entry in sorted(doc.get("defs") or [], key=lambda item: _sort_key(item.get("id", ""))):
        out = {}
        for key in DEF_KEYS:
            if key not in entry:
                continue
            value = entry[key]
            if key == "valueUnits":
                # Same rule as props[].valueUnits above: an integer, unbounded, no float hop.
                value = int(value)
            elif key == "collider" and isinstance(value, dict):
                value = collider_proxy.canonical_collider(value)
            elif key == "volumeK":
                value = rounded(value, 4)
            out[key] = value
        defs_out.append(_ordered(out, DEF_KEYS))

    result = {"schema": doc.get("schema", SCHEMA)}
    result["level"] = level_out
    if "defs" in doc:
        result["defs"] = defs_out
    result["props"] = props_out
    result["structures"] = structures_out
    if "cameras" in doc:
        result["cameras"] = cameras_out
    if "renderPresets" in doc:
        result["renderPresets"] = presets_out
    return result


def dump_doc(doc, manifests=None):
    """Canonical UTF-8 text: 2-space indent, trailing newline, no BOM."""
    return json.dumps(canonical_doc(doc, manifests), indent=2, ensure_ascii=False) + "\n"


def write_doc(doc, path, manifests=None):
    text = dump_doc(doc, manifests)
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return text


def load_doc(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def def_catalog(doc):
    """``{def: {"id", "kind", "valueUnits"}}`` from the document's ``defs[]`` block.

    The Lead's decision (2026-11-15) moved the value unit off
    ``PickupFieldSync``'s longest-edge table and onto a per-def declaration, so a
    level states "a watermelon is worth 4" once instead of relying on a pricing
    rule.  A per-prop ``valueUnits`` still overrides, for the rare special case.
    """
    catalog = {}
    for entry in doc.get("defs") or []:
        if not isinstance(entry, dict) or not entry.get("id"):
            continue
        catalog[str(entry["id"])] = entry
    return catalog


def effective_kind(prop, catalog, manifests=None):
    """Kind of a prop: its own field, else the def's, else the manifest category."""
    if isinstance(prop.get("kind"), str) and prop["kind"]:
        return prop["kind"]
    entry = catalog.get(prop.get("def"))
    if entry and isinstance(entry.get("kind"), str) and entry["kind"]:
        return entry["kind"]
    if manifests and prop.get("def") in manifests:
        category = manifests[prop["def"]].get("category")
        if category:
            return category
    return None


def effective_value_units(prop, catalog):
    """Declared value of a prop: its own field, else its def's; ``None`` when neither."""
    value = prop.get("valueUnits")
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    entry = catalog.get(prop.get("def")) or {}
    value = entry.get("valueUnits")
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    return None


#: The one place "what is this prop's parent" is answered, so the runtime field and the
#: older authoring spelling can never disagree in a consumer that uses this helper.
def prop_parent(prop):
    """ItemId of the prop this one is carried by, or ``None``.

    ``parent`` is the field the runtime reads (fall propagation: eat the parent and the
    children come down with it).  ``stackedOn`` predates it and means the same relation
    for the only case it was ever used for ("this prop sits on that prop's top"), so it is
    read as the same pointer when ``parent`` is absent.  A document that carries both must
    have them agree (``_check_parents``), and ``derive_supports`` never proposes a link
    where ``stackedOn`` already says the same thing.
    """
    if not isinstance(prop, dict):
        return None
    parent = prop.get("parent")
    if isinstance(parent, str) and parent:
        return parent
    stack = prop.get("stackedOn")
    if isinstance(stack, str) and stack:
        return stack
    return None


def parent_gap_window(child_box, parent_box):
    """Allowed ``bottom(child) - top(parent)`` of a *derived* support link.

    One implementation for the validator, the derivation and the report: the window
    scales with the smaller of the two **world** heights, so a 3 cm eraser is not allowed
    the slack of a 2 m crate.  Returns ``(low, high)`` in metres.
    """
    smallest = min(child_box[1][1] - child_box[0][1], parent_box[1][1] - parent_box[0][1])
    return (-PARENT_EMBED_FRACTION * smallest, PARENT_GAP_FRACTION * smallest)


# --------------------------------------------------------------------------- #
# validation
# --------------------------------------------------------------------------- #
class Report:
    """Errors decide the exit code; warnings are for the author; notes are facts."""

    def __init__(self):
        self.errors = []
        self.warnings = []
        self.notes = []
        self.tables = {}

    def err(self, message):
        self.errors.append(message)

    def warn(self, message):
        self.warnings.append(message)

    def note(self, message):
        self.notes.append(message)

    @property
    def ok(self):
        return not self.errors


def _index_structures(doc):
    return {structure.get("id"): structure for structure in doc.get("structures") or []
            if isinstance(structure, dict)}


def ground_plane(doc):
    """``(normal, offset)`` of the level floor.

    ``role=ground`` is a *plane through the structure's centre*, exactly like a boundary
    (a solid ground slab would be a box and eat the box budget). ``offset`` is
    ``dot(normal, centre)``, which is also what the Unity importer hands to
    ``LevelArenaHost.groundOffset``.
    """
    for structure in doc.get("structures") or []:
        if structure.get("role") != "ground":
            continue
        try:
            normal = structure_up(structure)
            return normal, dot(normal, tuple(float(v) for v in structure["center"]))
        except (KeyError, TypeError, ValueError):
            break
    level = doc.get("level") or {}
    try:
        normal = normalized(tuple(float(v) for v in level.get("groundNormal", (0.0, 1.0, 0.0))))
    except (TypeError, ValueError):
        normal = (0.0, 1.0, 0.0)
    try:
        return normal, float(level.get("groundY", 0.0))
    except (TypeError, ValueError):
        return normal, 0.0


def ground_level(doc):
    """Floor offset (height for a flat floor) along the ground normal."""
    return ground_plane(doc)[1]


def _prop_world_aabb(prop, report):
    position = as_vec3(prop.get("position"), "props[%s].position" % prop.get("itemId"), report)
    size = as_vec3(prop.get("aabb"), "props[%s].aabb" % prop.get("itemId"), report)
    quat = as_quat_xyzw(prop.get("rotationQuat"), "props[%s].rotationQuat" % prop.get("itemId"), report)
    if position is None or size is None or quat is None:
        return None
    return world_aabb(position, size, quat)


def _structure_world_aabb(structure, report, ball_radius_m=None):
    if structure.get("role") == "curve_ramp":
        return curve_world_aabb(structure, ball_radius_m)
    center = as_vec3(structure.get("center"), "structures[%s].center" % structure.get("id"), report)
    size = as_vec3(structure.get("size"), "structures[%s].size" % structure.get("id"), report)
    quat = as_quat_xyzw(structure.get("rotationQuat"),
                        "structures[%s].rotationQuat" % structure.get("id"), report)
    if center is None or size is None or quat is None:
        return None
    return world_aabb(center, size, quat)


def _check_schema(doc, report):
    schema = doc.get("schema")
    if schema != SCHEMA:
        report.err("schema is %r, expected %r" % (schema, SCHEMA))
    level = doc.get("level")
    if not isinstance(level, dict):
        report.err("level must be an object")
    else:
        for key in ("id", "ballDiameterMax", "bounds", "budget", "expected"):
            if key not in level:
                report.err("level.%s is required" % key)
        convention = level.get("convention")
        if convention is not None and convention != CONVENTION:
            report.warn("level.convention is %r; the tool writes %r" % (convention, CONVENTION))
        if level.get("units", "m") != "m":
            report.err("level.units must be 'm' (got %r)" % level.get("units"))
        budget = level.get("budget")
        if isinstance(budget, dict):
            for key in BUDGET_KEYS:
                if key not in budget:
                    report.warn("level.budget.%s is missing" % key)
        elif level.get("budget") is not None:
            report.err("level.budget must be an object")


def _check_defs(doc, report, manifests, catalog):
    """``defs[]``: unique ids, known defs, sane kinds and values."""
    entries = doc.get("defs")
    if entries is None:
        return
    if not isinstance(entries, list):
        report.err("defs must be a list")
        return
    seen = set()
    for index, entry in enumerate(entries):
        label = "defs[%d]" % index
        if not isinstance(entry, dict):
            report.err("%s must be an object" % label)
            continue
        def_id = entry.get("id")
        if not isinstance(def_id, str) or not def_id:
            report.err("%s.id is required" % label)
            continue
        if def_id in seen:
            report.err("%s def %r is duplicated" % (label, def_id))
        seen.add(def_id)
        model = entry.get("model")
        if manifests and def_id not in manifests:
            if isinstance(model, str) and model.strip():
                report.warn("%s def %r declares model %r but no manifest was found for it "
                            "(aabb is not checked); put one in --manifests or in "
                            "<modelDir>/../exports/" % (label, def_id, model))
            else:
                report.warn("%s def %r has no manifest in props/exports (no model yet)"
                            % (label, def_id))
        if model is not None:
            if not isinstance(model, str) or not model.strip():
                report.err("%s [def_model] model must be a non-empty string (a Unity-relative "
                           ".fbx path under Assets/), got %r" % (label, model))
            else:
                if not model.startswith("Assets/"):
                    report.err("%s [def_model] def %r model %r must start with 'Assets/' "
                               "(a path relative to the Unity project)" % (label, def_id, model))
                if not model.endswith(".fbx"):
                    report.err("%s [def_model] def %r model %r must end with '.fbx'"
                               % (label, def_id, model))
                if ".." in model.replace("\\", "/").split("/"):
                    report.err("%s [def_model] def %r model %r must not contain '..'"
                               % (label, def_id, model))
        value = entry.get("valueUnits")
        if value is None:
            report.err("%s.valueUnits is required (it is what the def is worth)" % label)
        elif not isinstance(value, int) or isinstance(value, bool) or value < 1:
            report.err("%s.valueUnits must be a whole number >= 1 (got %r)" % (label, value))
        kind = entry.get("kind")
        if kind is not None and (not isinstance(kind, str) or not kind.strip()):
            report.err("%s.kind must be a non-empty string when present" % label)
        if "volumeK" in entry:
            reason = volume_k_error(entry["volumeK"])
            if reason is not None:
                report.err("%s.volumeK %r %s" % (label, entry["volumeK"], reason))
        _check_collider(entry, label, def_id, manifests, report)


def _check_collider(entry, label, def_id, manifests, report):
    """``defs[].collider``: schema, then "inside the visual AABB" (COLLIDER_LAYER 4.1).

    The collider block is optional.  A prop whose def has none keeps the visual
    AABB as its collision shape, exactly as before this field existed; that is the
    compatibility promise, and ``canonical_doc`` only touches the field when it is
    present.
    """
    block = entry.get("collider")
    if block is None:
        return
    for message in collider_proxy.collider_schema_errors(block):
        report.err("%s [collider] def %r: %s" % (label, def_id, message))
    if not isinstance(block, dict) or def_id not in manifests:
        return
    bounds = collider_proxy.collider_bounds(block)
    if bounds is None:
        return
    half = [0.5 * value for value in manifests[def_id]["aabb"]]
    low, high = bounds
    for axis, name in enumerate("xyz"):
        if low[axis] < -half[axis] - MANIFEST_AABB_TOLERANCE_M:
            report.err("%s [collider] def %r: collider reaches %.4f m on -%s, outside the "
                       "visual AABB (half extent %.4f m)" % (label, def_id, low[axis],
                                                             name, half[axis]))
        if high[axis] > half[axis] + MANIFEST_AABB_TOLERANCE_M:
            report.err("%s [collider] def %r: collider reaches %.4f m on +%s, outside the "
                       "visual AABB (half extent %.4f m)" % (label, def_id, high[axis],
                                                             name, half[axis]))


def _check_props(doc, report, manifests, catalog):
    """Rules 1-8, 12 on the prop list; returns ``{itemId: world AABB}``."""
    props = doc.get("props")
    if not isinstance(props, list):
        report.err("props must be a list")
        return {}

    seen = {}
    boxes = {}
    level = doc.get("level") or {}
    for index, prop in enumerate(props):
        label = "props[%d]" % index
        if not isinstance(prop, dict):
            report.err("%s must be an object" % label)
            continue
        item_id = prop.get("itemId")
        if not isinstance(item_id, str) or not item_id.strip():
            report.err("%s.itemId must be a non-empty string" % label)
            continue
        if item_id != item_id.strip():
            report.err("%s.itemId %r has leading/trailing whitespace" % (label, item_id))
        if item_id in seen:
            report.err("%s itemId %r is duplicated (also props[%s]); ids are the import key"
                       % (label, item_id, seen[item_id]))
            continue
        seen[item_id] = index

        def_id = prop.get("def")
        if not isinstance(def_id, str) or not def_id:
            report.err("%s.def is required" % label)
        elif def_id not in manifests and not def_model(catalog, def_id):
            # A def whose id is not a repo prop is allowed **only** when it declares its
            # own model; then the manifest comes from --manifests or from beside the model.
            report.err("%s def %r has no manifest in props/exports and declares no model"
                       % (label, def_id))

        kind = prop.get("kind")
        if kind is not None and (not isinstance(kind, str) or not kind.strip()):
            report.err("%s.kind must be a non-empty string when present" % label)
        if isinstance(kind, str) and kind.strip() and not kind.islower():
            report.warn("%s.kind %r is not lower case; the Unity kind folder uses it verbatim"
                        % (label, kind))
        zone = prop.get("zone")
        if zone is not None and not isinstance(zone, str):
            report.err("%s.zone must be a string" % label)
        group_id = prop.get("groupId")
        if group_id is not None and (not isinstance(group_id, str) or not group_id.strip()):
            report.err("%s.groupId must be a non-empty string when present" % label)

        aabb = as_vec3(prop.get("aabb"), "%s.aabb" % label, report)
        if aabb is not None:
            if any(v <= 0.0 for v in aabb):
                report.err("%s.aabb must be positive on every axis, got %r" % (label, aabb))
            elif def_id in manifests:
                expected = manifests[def_id]["aabb"]
                worst = max(abs(aabb[i] - expected[i]) for i in range(3))
                if worst > MANIFEST_AABB_TOLERANCE_M:
                    report.err("%s def %r aabb %r disagrees with manifest %r by %.4f m"
                               % (label, def_id, aabb, expected, worst))

        position = as_vec3(prop.get("position"), "%s.position" % label, report)
        quat = as_quat_xyzw(prop.get("rotationQuat"), "%s.rotationQuat" % label, report)
        if aabb is None or position is None or quat is None:
            continue

        if "volumeK" in prop:
            reason = volume_k_error(prop["volumeK"])
            if reason is not None:
                report.err("%s.volumeK %r %s" % (label, prop["volumeK"], reason))
        volume_k = effective_volume_k(prop, catalog)
        if "absorbDiameterReq" in prop:
            dreq = absorb_diameter_req(aabb, volume_k)
            declared = as_float(prop.get("absorbDiameterReq"), "%s.absorbDiameterReq" % label, report)
            if declared is not None and abs(declared - dreq) > 5e-4:
                report.err("%s absorbDiameterReq %.3f is stale; the aabb (with k=%.2f) gives %.3f"
                           % (label, declared, volume_k, dreq))
        ball_max = level.get("ballDiameterMax")
        if ball_max is not None:
            try:
                ball_max = float(ball_max)
            except (TypeError, ValueError):
                ball_max = None
        if ball_max is not None:
            derived = is_absorbable(aabb, ball_max, volume_k)
            if "absorbable" in prop and bool(prop.get("absorbable")) != derived:
                report.err("%s absorbable=%r but D_req=%.3f m (k=%.2f) vs ballDiameterMax=%.2f says %r"
                           % (label, prop.get("absorbable"), absorb_diameter_req(aabb, volume_k),
                              volume_k, ball_max, derived))

        value_units = prop.get("valueUnits")
        if value_units is not None:
            if not isinstance(value_units, int) or isinstance(value_units, bool) or value_units < 1:
                report.err("%s.valueUnits must be a whole number >= 1 (got %r)" % (label, value_units))
        elif effective_value_units(prop, catalog) is None:
            report.err("%s has no valueUnits and def %r has no defs[].valueUnits entry"
                       % (label, prop.get("def")))

        boxes[item_id] = world_aabb(position, aabb, quat)

    return boxes


def _curve_error(report, label, structure_id, code, message):
    """One curve problem, with its reason code in brackets (the stable part)."""
    report.err("%s curve_ramp %r [%s]: %s" % (label, structure_id, code, message))


def _check_curve(structure, label, report, ball_radius_m):
    """Validate one ``role=curve_ramp`` entry; ``(segments, metrics)`` or ``None``.

    Reason codes: ``curve_missing``, ``curve_unknown_field``, ``curve_radius``,
    ``curve_arc``, ``curve_width``, ``curve_thickness``, ``curve_bend``,
    ``curve_bend_unsupported``, ``curve_segments``, ``curve_segments_coarse``,
    ``curve_overlap``, ``curve_size``, ``curve_shape``, ``curve_collision``,
    ``curve_meets``, ``curve_expand``.

    The bounds are the design's (``CONTACT_MODEL_V2`` 5.7): ``radius_m > 0``,
    ``0 < arc_deg <= 360``, ``width_m > 0``, ``thickness_m > 0``, ``segments`` a whole number
    in ``0..256`` (0 or absent = auto), and the resolved chain's sagitta at most 5 % of the
    smallest ball radius.
    """
    structure_id = structure.get("id")
    ok = True
    curve = structure.get("curve")
    if not isinstance(curve, dict):
        _curve_error(report, label, structure_id, "curve_missing",
                     '"curve" must be an object with radius_m, arc_deg, width_m, thickness_m '
                     "(and optionally segments / bend)")
        return None

    unknown = sorted(set(curve) - set(CURVE_KEYS))
    if unknown:
        _curve_error(report, label, structure_id, "curve_unknown_field",
                     "unknown curve field(s) %s; allowed: %s (pitch_m / bank_deg / axis belong "
                     "to the helix extension, which the reference tessellator does not "
                     "implement - see CONTACT_MODEL_V2 5.7b)"
                     % (", ".join(unknown), ", ".join(CURVE_KEYS)))
        ok = False

    numbers = {}
    for key, code in (("radius_m", "curve_radius"), ("arc_deg", "curve_arc"),
                      ("width_m", "curve_width"), ("thickness_m", "curve_thickness")):
        value = curve.get(key)
        number = None
        if isinstance(value, (int, float)) and not isinstance(value, bool) \
                and math.isfinite(float(value)):
            number = float(value)
        if number is None or number <= 0.0:
            _curve_error(report, label, structure_id, code,
                         "%s must be a finite number > 0 (got %r)" % (key, value))
            ok = False
        else:
            numbers[key] = number

    arc = numbers.get("arc_deg")
    if arc is not None and arc > CURVE_MAX_ARC_DEG:
        _curve_error(report, label, structure_id, "curve_arc",
                     "arc_deg %.3f is over %.0f deg (a full turn is the maximum)"
                     % (arc, CURVE_MAX_ARC_DEG))
        ok = False

    bend = curve.get("bend", "up")
    if not isinstance(bend, str) or bend not in CURVE_BENDS:
        if isinstance(bend, str) and bend in CURVE_UNSUPPORTED_BENDS:
            _curve_error(report, label, structure_id, "curve_bend_unsupported",
                         "bend %r is drawn in the design sketch but has no tessellator: the "
                         'reference expands a vertical arc only (bend "up" or "down"). A '
                         "horizontal S-bend or a helix needs the analytic patch that 5.7(b) "
                         "rules out" % (bend,))
        else:
            _curve_error(report, label, structure_id, "curve_bend",
                         'bend %r must be "up" or "down"' % (bend,))
        ok = False

    declared = curve.get("segments", 0)
    if declared is None:
        declared = 0
    if isinstance(declared, bool) or not isinstance(declared, int):
        _curve_error(report, label, structure_id, "curve_segments",
                     "segments must be a whole number (0 or absent = auto by the chord "
                     "budget), got %r" % (declared,))
        ok = False
    elif declared < 0 or declared > STATIC_OBB_CAPACITY:
        _curve_error(report, label, structure_id, "curve_segments",
                     "segments %d is outside 0..%d (0 = auto)"
                     % (declared, STATIC_OBB_CAPACITY))
        ok = False

    if structure.get("shape", "box") != "box":
        _curve_error(report, label, structure_id, "curve_shape",
                     "a curve_ramp must be shape=box (its segments are boxes); got %r"
                     % (structure.get("shape"),))
        ok = False
    if structure.get("collision", "box") != "box":
        _curve_error(report, label, structure_id, "curve_collision",
                     "a curve_ramp must be collision=box: the motor needs the segment colliders "
                     "the importer bakes; got %r" % (structure.get("collision"),))
        ok = False
    if "meets" in structure:
        _curve_error(report, label, structure_id, "curve_meets",
                     "a curved ramp has no straight top edge, so the step rule cannot check "
                     "'meets'; drop it and let the arc's end pose be the reference")
        ok = False

    if "size" in structure:
        declared_size = None
        if isinstance(structure["size"], (list, tuple)) and len(structure["size"]) == 3:
            try:
                declared_size = tuple(float(v) for v in structure["size"])
            except (TypeError, ValueError):
                declared_size = None
        wanted = (numbers.get("width_m"), numbers.get("thickness_m"), 0.0)
        if declared_size is None or any(abs(declared_size[i] - wanted[i]) > 1e-3
                                        for i in range(3)):
            _curve_error(report, label, structure_id, "curve_size",
                         "size is derived as [width_m, thickness_m, 0] = %r; %r disagrees"
                         % (list(wanted), structure.get("size")))
            ok = False

    if not ok:
        return None

    try:
        chain, metrics = expand_curve_ramp(structure, ball_radius_m)
    except (KeyError, TypeError, ValueError, FileNotFoundError) as error:
        _curve_error(report, label, structure_id, "curve_expand", str(error))
        return None

    budget = CURVE_CHORD_ERROR_PER_RADIUS * ball_radius_m
    if metrics["sagitta_m"] > budget + 1e-9:
        needed = auto_curve_segments(numbers["radius_m"], numbers["arc_deg"], ball_radius_m)
        _curve_error(report, label, structure_id, "curve_segments_coarse",
                     "segments=%d gives a %.2f mm sagitta, over the %.2f mm budget (%.0f %% of "
                     "the %.3f m smallest ball radius); use at least %d"
                     % (metrics["segments"], metrics["sagitta_m"] * 1000.0, budget * 1000.0,
                        CURVE_CHORD_ERROR_PER_RADIUS * 100.0, ball_radius_m, needed))
        ok = False

    overlap = metrics["overlap"] - 1.0
    if not 0.0 < overlap < CURVE_MAX_OVERLAP_FRACTION:
        _curve_error(report, label, structure_id, "curve_overlap",
                     "the segment overlap is %.3f of a chord; it must be > 0 and < %.2f so the "
                     "chain neither cracks on the convex side nor doubles back"
                     % (overlap, CURVE_MAX_OVERLAP_FRACTION))
        ok = False

    return (metrics["segments"], metrics) if ok else None


def _check_structures(doc, report):
    """Structure field validity, the ramp/box identity rule and the ramp geometry."""
    structures = doc.get("structures")
    if not isinstance(structures, list):
        report.err("structures must be a list")
        return {}
    seen = {}
    ground = []
    curve_rows = []
    ball_radius_m = curve_ball_radius_m(doc.get("level"))
    for index, structure in enumerate(structures):
        label = "structures[%d]" % index
        if not isinstance(structure, dict):
            report.err("%s must be an object" % label)
            continue
        structure_id = structure.get("id")
        if not isinstance(structure_id, str) or not structure_id.strip():
            report.err("%s.id must be a non-empty string" % label)
            continue
        role = structure.get("role")
        if role not in PROP_ROLES:
            report.err("%s role %r must be one of %s" % (label, role, list(PROP_ROLES)))
            continue
        if structure_id in seen:
            other = seen[structure_id]
            if {other["role"], role} == {"box", "ramp"} or "box" in (other["role"], role) and \
                    "ramp" in (other["role"], role):
                report.err("%s id %r is both a box and a ramp; a ramp must never also be a box "
                           "(an invisible wall appears at its foot)" % (label, structure_id))
            else:
                report.err("%s id %r is duplicated" % (label, structure_id))
            continue
        seen[structure_id] = structure

        shape = structure.get("shape", "box")
        if shape not in SHAPES:
            report.err("%s.shape %r must be one of %s" % (label, shape, list(SHAPES)))
        if role == "ground":
            ground.append(structure_id)
            if shape != "plane":
                report.err("%s role=ground must be shape=plane (got %r)" % (label, shape))
        if role == "boundary" and shape != "plane":
            report.err("%s role=boundary must be shape=plane; a box boundary adds a walkable "
                       "ledge and eats the box budget" % label)
        collision = structure.get("collision", "box")
        if collision not in COLLISIONS:
            report.err("%s.collision %r must be one of %s" % (label, collision, list(COLLISIONS)))
        # A curve has no single authored box: its `size` is derived from the arc and may be
        # omitted, so only the pose is required here.
        required = ("center", "rotationQuat") if role == "curve_ramp" \
            else ("center", "size", "rotationQuat")
        for key in required:
            if key not in structure:
                report.err("%s.%s is required" % (label, key))
        size = None
        if role != "curve_ramp":
            size = as_vec3(structure.get("size"), "%s.size" % label, report)
            if size is not None and any(v <= 0.0 for v in size):
                report.err("%s.size must be positive on every axis, got %r" % (label, size))
        center = as_vec3(structure.get("center"), "%s.center" % label, report)
        quat = as_quat_xyzw(structure.get("rotationQuat"), "%s.rotationQuat" % label, report)
        if center is None or quat is None or (role != "curve_ramp" and size is None):
            continue

        if role == "boundary":
            up = structure_up(structure)
            if abs(up[1]) > 0.01:
                report.err("%s role=boundary needs a vertical plane (its local +Y is the inward "
                           "normal); this one is tilted" % label)

        slope = structure_slope_deg(structure)
        if role == "curve_ramp":
            resolved = _check_curve(structure, label, report, ball_radius_m)
            if resolved is not None:
                segments, metrics = resolved
                curve_rows.append({
                    "id": structure_id, "segments": segments,
                    "deltaDeg": round(metrics["delta_deg"], 2),
                    "sagittaM": round(metrics["sagitta_m"], 5),
                    "normalJumpDeg": round(metrics["normal_jump_deg"], 2),
                    "overlap": round(metrics["overlap"] - 1.0, 4),
                    "bend": metrics["bend"], "arcDeg": round(slope, 2),
                    "radiusM": round(float(structure["curve"]["radius_m"]), 3)})
            continue
        if role == "ramp":
            # Hard errors are only for values a solid board cannot have: a walking face that
            # points down (inverted) or a board standing on edge (90 deg is a wall). The old
            # 45 deg *ceiling* is a warning now (S4 of CONTACT_MODEL_V2 1.5/3.10: v2 climbs a
            # steep slope slowly instead of refusing it).
            if structure_up(structure)[1] < -1e-6:
                report.err("%s ramp %r is inverted: its local +Y (the walking face) points "
                           "down. A board the ball can ride must face up; flip its "
                           "rotationQuat" % (label, structure_id))
            elif slope >= 90.0 - 0.01:
                report.err("%s ramp %r is vertical (%.1f deg); a slab on edge is a wall, not a "
                           "ramp - author it as role=box" % (label, structure_id, slope))
            elif slope > RAMP_MAX_SLOPE_DEG + 0.01:
                report.warn("%s ramp %r is %.1f deg, steeper than the %.0f deg guideline; the v2 "
                           "motor climbs it slowly instead of rolling it (CONTACT_MODEL_V2 3.10). "
                           "Keep it a ramp, or make it a box if it must block"
                           % (label, structure_id, slope, RAMP_MAX_SLOPE_DEG))
            corners = box_corners(center, size, quat)
            foot = min(corner[1] for corner in corners)
            floor = ground_level(doc)
            # The authored line is the walking surface, so the board itself hangs below
            # it: one thickness of underside under the ground is by design, deeper is sunk.
            if foot < floor - RAMP_UNDERSIDE_ALLOWANCE_M:
                report.err("%s ramp %r foot is %.3f m below the ground (%.3f); it is sunk "
                           "(a ramp may hang at most one board thickness - %.3f m - under the "
                           "ground, because its authored line is the walking surface)"
                           % (label, structure_id, foot, floor, RAMP_UNDERSIDE_ALLOWANCE_M))
            elif foot > floor + RAMP_FOOT_MAX_M:
                report.err("%s ramp %r foot floats %.3f m above the ground; the ball cannot get on it"
                           % (label, structure_id, foot - floor))
            meets = structure.get("meets")
            if meets is not None:
                target = _index_structures(doc).get(meets)
                if target is None:
                    report.err("%s ramp %r meets unknown structure %r" % (label, structure_id, meets))
                else:
                    radius = (doc.get("level") or {}).get("ballDiameterStart", 0.0)
                    try:
                        step = STEP_RATIO * float(radius)
                    except (TypeError, ValueError):
                        step = 0.0
                    top = max(corner[1] for corner in corners)
                    gap = top - _structure_world_aabb(target, report)[1][1]
                    if abs(gap) > max(step, 0.01):
                        report.err("%s ramp %r top is %.3f m off the %r top face (step limit %.3f m)"
                                   % (label, structure_id, gap, meets, step))
        elif slope > 1.0 and role == "box":
            report.warn("%s %r is a tilted box (%.1f deg); a tilted box degrades to its world "
                        "AABB in the motor - author it as a ramp instead"
                        % (label, structure_id, slope))

    if len(ground) != 1:
        report.err("structures must contain exactly one role=ground (found %d: %s)"
                   % (len(ground), ground if ground else "none"))
    # One row per logical curve ramp, its expanded segment count beside it (the "one logical
    # ramp, N segments" report the operator reads in `check --report`).
    report.tables["curves"] = curve_rows
    report.tables["curveSummary"] = {
        "ramps": len(curve_rows),
        "segments": sum(row["segments"] for row in curve_rows),
        "sagittaMaxM": max([row["sagittaM"] for row in curve_rows] or [0.0]),
        "ballRadiusM": round(ball_radius_m, 4),
    }
    return seen


_LIMIT_KEY_OF_ROLE = {"boundary": "planes", "box": "boxes", "ramp": "ramps",
                      "curve_ramp": "ramps"}


def _check_budget(doc, report, structures_by_id):
    level = doc.get("level") or {}
    budget = level.get("budget") or {}
    roles = {"boundary": [], "box": [], "ramp": [], "curve_ramp": []}
    for structure in structures_by_id.values():
        role = structure.get("role")
        if role in roles:
            roles[role].append(structure.get("id"))
    # A curve_ramp is ONE logical ramp: one authoring entry, one structure object, one line in
    # the design table.  Its expanded slab segments are static OBBs and are counted by
    # `staticObbs` below instead ("one logical ramp, N segments reported").
    counts = {"planes": len(roles["boundary"]), "boxes": len(roles["box"]),
              "ramps": len(roles["ramp"]) + len(roles["curve_ramp"])}
    for key, count in counts.items():
        limit = budget.get(key)
        if limit is None:
            continue
        try:
            limit = int(limit)
        except (TypeError, ValueError):
            report.err("level.budget.%s must be an integer (got %r)" % (key, budget.get(key)))
            continue
        if count > limit:
            ids = sorted(structure_id
                         for role, structure_ids in roles.items()
                         if _LIMIT_KEY_OF_ROLE[role] == key
                         for structure_id in structure_ids)
            report.err("%s=%d over the budget of %d: %s"
                       % (key, count, limit, ", ".join(ids)))
    curve_summary = report.tables.get("curveSummary") or {"segments": 0}
    curve_segments = int(curve_summary.get("segments", 0))
    static_obbs = counts["boxes"] + len(roles["ramp"]) + curve_segments
    if static_obbs > STATIC_OBB_CAPACITY:
        report.err("static OBBs=%d over the motor's %d-solid capacity (boxes + ramp slabs + "
                   "curve segments; CONTACT_MODEL_V2 5.4/7.1): %s"
                   % (static_obbs, STATIC_OBB_CAPACITY,
                      ", ".join(sorted(roles["box"] + roles["ramp"] + roles["curve_ramp"]))))
    report.tables["budget"] = {"counts": counts,
                               "curveRamps": len(roles["curve_ramp"]),
                               "curveSegments": curve_segments,
                               "staticObbs": static_obbs,
                               "limits": {k: budget.get(k) for k in BUDGET_KEYS}}


def _check_arena(doc, report, structures_by_id, path=None):
    """The ``level.arena`` contract: one document describes one repeatedly-applied arena.

    A cell document exists because the motor's static capacity (8 planes / 8 boxes /
    4 ramps) is smaller than a whole test field, so the field switches one arena at a
    time (``PHYSICS_TEST_FIELD`` section 3).  The extra rules here are:

    * the arena needs an id, because the Unity importer uses it as the managed-root
      child and the cell component finds its ``LevelArenaHost`` by it;
    * the id must equal the containing directory name, so a copied file cannot
      silently keep the old cell's identity;
    * every box/ramp must lie inside ``level.bounds``, the field extent the arena
      registers against (a coarse guard: a typo that puts a box a hundred metres out
      is caught here).

    The runner's own data - where the ball starts, and which rect makes this cell the
    active one - is NOT here: it lives once, in the field's criteria file
    (``cells[].start_pose`` / ``cells[].footprint``), because the runner is the only
    consumer.  ``Docs/tests/levelbuild/test_physics_test_field_cells.py`` keeps the two
    files honest by refusing a cell whose geometry leaves its own footprint.
    """
    level = doc.get("level") or {}
    arena = level.get("arena")
    if arena is None:
        return
    if not isinstance(arena, dict):
        report.err("level.arena must be an object with an id")
        return

    arena_id = arena.get("id")
    if not isinstance(arena_id, str) or not arena_id.strip():
        report.err("level.arena.id is required (the Unity importer names the managed root "
                   "child after it)")
        arena_id = None

    if path is not None and arena_id:
        folder = Path(path).resolve().parent.name
        if folder and folder != arena_id:
            report.err("level.arena.id is %r but the file lives in %r; a copied cell file "
                       "must be renamed together with its arena id" % (arena_id, folder))

    bounds = level.get("bounds")
    if not (isinstance(bounds, list) and len(bounds) == 2):
        return      # _check_bounds_and_spawn already reports this
    low = (float(bounds[0][0]), float(bounds[0][1]))
    high = (float(bounds[1][0]), float(bounds[1][1]))
    ball_radius_m = curve_ball_radius_m(level)
    for structure_id, structure in sorted(structures_by_id.items()):
        if structure.get("role") not in ("box", "ramp", "curve_ramp"):
            continue
        box = _structure_world_aabb(structure, report, ball_radius_m)
        if box is None:
            continue
        over = 0.0
        axis = ""
        for index, axis_name in ((0, "x"), (2, "z")):
            low_index = 0 if index == 0 else 1
            if box[0][index] < low[low_index] - BOUNDS_MARGIN_M:
                delta = low[low_index] - box[0][index]
                if delta > over:
                    over, axis = delta, axis_name + " low"
            if box[1][index] > high[low_index] + BOUNDS_MARGIN_M:
                delta = box[1][index] - high[low_index]
                if delta > over:
                    over, axis = delta, axis_name + " high"
        if over > 0.0:
            report.err("%s leaves level.bounds by %.3f m (%s); a cell document's bounds are the "
                       "88 m field box, so this is a coordinate mistake"
                       % (structure_id, over, axis))

    report.tables.setdefault("arena", {})
    report.tables["arena"].update({"id": arena_id, "bounds": [list(low), list(high)]})


def _check_bounds_and_spawn(doc, report, boxes):
    level = doc.get("level") or {}
    bounds = level.get("bounds")
    margin = BOUNDS_MARGIN_M
    if isinstance(bounds, list) and len(bounds) == 2:
        low = as_vec3((bounds[0][0], 0.0, bounds[0][1]), "level.bounds[0]", report)
        high = as_vec3((bounds[1][0], 0.0, bounds[1][1]), "level.bounds[1]", report)
    else:
        report.err("level.bounds must be [[minX, minZ], [maxX, maxZ]]")
        low = high = None

    if low is not None and high is not None:
        for item_id, box in sorted(boxes.items()):
            over = 0.0
            axis = ""
            for i, axis_name in ((0, "x"), (2, "z")):
                if box[0][i] < low[i] - margin:
                    delta = low[i] - box[0][i]
                    if delta > over:
                        over, axis = delta, axis_name + " low"
                if box[1][i] > high[i] + margin:
                    delta = box[1][i] - high[i]
                    if delta > over:
                        over, axis = delta, axis_name + " high"
            if over > 0.0:
                report.err("%s leaves level.bounds by %.3f m (%s)" % (item_id, over, axis))

    ball_start = level.get("ballStart")
    clearance = float(level.get("spawnClearanceM", 1.0))
    if ball_start is not None:
        point = as_vec3(ball_start, "level.ballStart", report)
        if point is not None:
            inside = sorted(item_id for item_id, box in boxes.items()
                            if aabb_distance_to_point(box, point) < clearance)
            if inside:
                report.err("%d prop(s) within %.2f m of ballStart: %s"
                           % (len(inside), clearance, ", ".join(inside[:DETAIL_LIMIT])))
    player_spawn = level.get("playerSpawn")
    if player_spawn is not None:
        point = as_vec3(player_spawn, "level.playerSpawn", report)
        if point is not None:
            inside = sorted(item_id for item_id, box in boxes.items()
                            if aabb_distance_to_point(box, point) < 1.0)
            if inside:
                report.err("%d prop(s) within 1.0 m of playerSpawn: %s"
                           % (len(inside), ", ".join(inside[:DETAIL_LIMIT])))


def _support_surface(doc, prop, structures_by_id, report):
    """What the prop declares it rests on, as ``(kind, payload)``.

    ``("stack", itemId)`` / ``("plane", (normal, point))`` / ``("floor", height)``,
    or ``(None, None)`` when the declaration points at something that does not
    exist (already reported).
    """
    stack = prop_parent(prop)
    if stack:
        return ("stack", stack)
    support = prop.get("support")
    if support:
        structure = structures_by_id.get(support)
        if structure is None:
            report.err("%s.support %r is not a structure id" % (prop.get("itemId"), support))
            return (None, None)
        if structure.get("role") == "curve_ramp":
            report.err("%s.support %r is a curve_ramp; it has no single top plane to seat a "
                       "prop on - support it on a box, or drop the prop"
                       % (prop.get("itemId"), support))
            return (None, None)
        return ("plane", structure_top_plane(structure))
    return ("floor", ground_level(doc))


def _check_support_and_overlap(doc, report, boxes, structures_by_id):
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    by_id = {prop.get("itemId"): prop for prop in props}

    for prop in props:
        item_id = prop.get("itemId")
        if item_id not in boxes:
            continue
        box = boxes[item_id]
        try:
            aabb = tuple(float(v) for v in prop["aabb"])
            position = tuple(float(v) for v in prop["position"])
            quat = tuple(float(v) for v in prop["rotationQuat"])
        except (KeyError, TypeError, ValueError):
            continue
        bottom = box[0][1]
        kind, payload = _support_surface(doc, prop, structures_by_id, report)
        if kind is None:
            continue

        if kind == "stack":
            other = by_id.get(payload)
            if other is None or other.get("itemId") not in boxes:
                report.err("%s.%s %r is not a prop itemId"
                           % (item_id, "stackedOn" if prop.get("stackedOn") else "parent",
                              payload))
                continue
            expected = boxes[payload][1][1]
            gap = bottom - expected
            if prop.get("stackedOn") == payload:
                # A hand-authored seating claim: tight, absolute.
                if abs(gap) > STACK_TOLERANCE_M:
                    report.err("%s sits %.3f m off the top of %r (its bottom %.3f, that top "
                               "%.3f)" % (item_id, gap, payload, bottom, expected))
            elif prop.get("parentDerived"):
                # A machine-derived link: the calibrated contact window (same numbers as
                # `_check_parents`, one shared function).
                low, high = parent_gap_window(box, boxes[payload])
                if gap < low or gap > high:
                    report.err("%s [parent_seat]: bottom is %+.3f m from the top of parent %r "
                               "(a derived link must stay within %+.3f .. %+.3f m)"
                               % (item_id, gap, payload, low, high))
            # A hand-authored attachment (`parent` without `parentDerived`) is not a
            # seating claim: a flag on a mast is legitimately off the top, so no check.
            continue

        if kind == "floor":
            normal, point = ground_plane(doc)[0], scale(ground_plane(doc)[0], payload)
            label = "the ground"
        else:
            normal, point = payload
            label = "support %r" % prop.get("support")

        if abs(normal[1]) < 1e-6:
            report.err("%s rests on %s, which is vertical" % (item_id, label))
            continue

        # Measure with the lowest corner, not the world AABB bottom: on a tilted
        # surface those differ by half the prop's width times the slope, which is
        # exactly how the measured "apple half-buried in the plank" happened
        # (LEVEL_AUTHORING section 13).  Both a sunk prop and one that is merely
        # lifted without being turned onto the surface fail here.
        worst = min(plane_distance(normal, point, corner)
                    for corner in box_corners(position, aabb, quat))
        if worst < -SINK_TOLERANCE_M:
            report.err("%s is sunk %.3f m into %s (its lowest corner is under the surface)"
                       % (item_id, -worst, label))
        elif worst > FLOAT_TOLERANCE_M:
            report.err("%s floats %.3f m above %s" % (item_id, worst, label))
        if bottom < -SINK_TOLERANCE_M:
            report.err("%s reaches %.3f m below y=0" % (item_id, bottom))

    # prop/prop interpenetration, ignoring a declared stack relationship and members of
    # one group (three biscuits in a pile are one authored object, not three accidents).
    # ``level.expected.overlapBudget`` (optional) changes only the *gate*: with a budget the
    # pair count is compared against it instead of every pair being an error.  Absent =
    # the historic rule, byte-for-byte.
    budget = overlap_budget(doc)
    ids = sorted(boxes)
    printed = 0
    pairs = []
    for i, left_id in enumerate(ids):
        for right_id in ids[i + 1:]:
            left_box, right_box = boxes[left_id], boxes[right_id]
            overlap = aabb_intersection(left_box, right_box)
            if overlap <= 0.0:
                continue
            smaller = min(aabb_volume(left_box), aabb_volume(right_box))
            if smaller <= 0.0:
                continue
            fraction = overlap / smaller
            if fraction <= OVERLAP_FRACTION:
                continue
            left = by_id.get(left_id) or {}
            right = by_id.get(right_id) or {}
            if prop_parent(left) == right_id or prop_parent(right) == left_id:
                continue
            group = left.get("groupId")
            if isinstance(group, str) and group and group == right.get("groupId"):
                continue
            if budget is None:
                if printed < DETAIL_LIMIT:
                    report.err("%s and %s overlap %.0f%% of the smaller AABB"
                               % (left_id, right_id, fraction * 100.0))
                printed += 1
            else:
                pairs.append((left_id, right_id))
    if budget is None:
        if printed > DETAIL_LIMIT:
            report.err("... %d more prop/prop overlap(s)" % (printed - DETAIL_LIMIT))
    else:
        examples = ", ".join("%s+%s" % pair for pair in pairs[:3])
        report.warn("%d prop/prop overlap pair(s) (level.expected.overlapBudget %d)%s"
                    % (len(pairs), budget, (": " + examples) if examples else ""))
        if len(pairs) > budget:
            report.err("prop/prop overlap pairs %d exceed level.expected.overlapBudget %d (%+d)%s"
                       % (len(pairs), budget, len(pairs) - budget,
                          ("; e.g. " + examples) if examples else ""))

    # props inside solid structures (design rule 8)
    printed = 0
    for prop in props:
        item_id = prop.get("itemId")
        if item_id not in boxes:
            continue
        try:
            aabb = tuple(float(v) for v in prop["aabb"])
            position = tuple(float(v) for v in prop["position"])
            quat = tuple(float(v) for v in prop["rotationQuat"])
        except (KeyError, TypeError, ValueError):
            continue
        corners = box_corners(position, aabb, quat)
        ball_radius_m = curve_ball_radius_m(doc.get("level"))
        for structure_id, structure in sorted(structures_by_id.items()):
            if structure.get("collision") == "none":
                continue
            try:
                solids = structure_solid_boxes(structure, ball_radius_m)
            except (KeyError, TypeError, ValueError, FileNotFoundError):
                continue
            worst = 0.0
            for solid in solids:
                try:
                    worst = max(worst, max(obb_penetration(corner, solid) for corner in corners))
                except (KeyError, TypeError, ValueError):
                    continue
            if worst > STRUCTURE_PENETRATION_M:
                if printed < DETAIL_LIMIT:
                    report.err("%s penetrates structure %r by %.3f m"
                               % (item_id, structure_id, worst))
                printed += 1
    if printed > DETAIL_LIMIT:
        report.err("... %d more prop/structure penetration(s)" % (printed - DETAIL_LIMIT))


def _check_parents(doc, report, boxes):
    """``parent`` / ``parentDerived``: existence, self, cycles and the seat window.

    ``parent`` is the *runtime* pointer (eat the parent and the children come down with
    it), not a seating claim like ``stackedOn``.  A **derived** link must sit inside the
    calibrated contact window, because derivation looked at geometry and nothing else; an
    authored link may be a rigid attachment (a flag on a mast, a fish in a school) and is
    not held to it.
    """
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    by_id = {}
    for prop in props:
        item_id = prop.get("itemId")
        if isinstance(item_id, str) and item_id:
            by_id.setdefault(item_id, prop)

    links = {}
    for prop in props:
        item_id = prop.get("itemId")
        if not isinstance(item_id, str) or not item_id:
            continue
        parent = prop.get("parent")
        derived = prop.get("parentDerived")
        if derived is not None and not isinstance(derived, bool):
            report.err("%s.parentDerived must be true or false (got %r)" % (item_id, derived))
        if parent is None:
            if derived:
                report.err("%s [parent_derived_orphan]: parentDerived is true but the prop has "
                           "no parent" % item_id)
            continue
        if not isinstance(parent, str) or not parent.strip():
            report.err("%s [parent_missing]: parent must be a prop itemId (got %r)"
                       % (item_id, parent))
            continue
        if parent == item_id:
            report.err("%s [parent_self]: a prop cannot be its own parent" % item_id)
            continue
        if parent not in by_id:
            report.err("%s [parent_missing]: parent %r is not a prop itemId" % (item_id, parent))
            continue
        stack = prop.get("stackedOn")
        if isinstance(stack, str) and stack and stack != parent:
            report.err("%s [parent_conflict]: parent %r and stackedOn %r name different props"
                       % (item_id, parent, stack))
            continue
        links[item_id] = parent

    # A cycle is a ring in the parent chain; report each ring once, at its smallest id.
    reported = set()
    for item_id in sorted(links):
        chain, seen = [item_id], {item_id}
        while True:
            nxt = links.get(chain[-1])
            if nxt is None:
                break
            if nxt in seen:
                ring = chain[chain.index(nxt):]
                smallest = min(ring)
                if smallest not in reported:
                    reported.add(smallest)
                    report.err("%s [parent_cycle]: the parent chain loops (%s)"
                               % (smallest, " -> ".join(ring + [nxt])))
                break
            chain.append(nxt)
            seen.add(nxt)

    for item_id in sorted(links):
        prop = by_id.get(item_id) or {}
        if not prop.get("parentDerived"):
            continue
        child_box, parent_box = boxes.get(item_id), boxes.get(links[item_id])
        if child_box is None or parent_box is None:
            continue
        gap = child_box[0][1] - parent_box[1][1]
        low, high = parent_gap_window(child_box, parent_box)
        if low == 0.0 and high == 0.0:
            continue
        smallest = min(child_box[1][1] - child_box[0][1], parent_box[1][1] - parent_box[0][1])
        if gap < low or gap > high:
            report.err("%s [parent_seat]: bottom is %+.3f m from the top of parent %r "
                       "(a derived link must stay within %+.3f .. %+.3f m, i.e. %.0f%%/%.0f%% "
                       "of the smaller height %.3f m)"
                       % (item_id, gap, links[item_id], low, high,
                          PARENT_EMBED_FRACTION * 100.0, PARENT_GAP_FRACTION * 100.0, smallest))


def _check_expected(doc, report):
    level = doc.get("level") or {}
    expected = level.get("expected")
    if not isinstance(expected, dict):
        report.err("level.expected must be an object with propCount and valueUnits")
        return
    overlap = expected.get("overlapBudget")
    if overlap is not None:
        if isinstance(overlap, bool) or not isinstance(overlap, int) or overlap < 0:
            report.err("level.expected.overlapBudget must be a whole number >= 0 (got %r); "
                       "it is the maximum number of prop/prop overlap pairs this level may "
                       "have" % (overlap,))
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    count = len(props)
    units = 0
    missing = 0
    catalog = def_catalog(doc)
    for prop in props:
        value = effective_value_units(prop, catalog)
        if value is not None:
            units += value
        else:
            missing += 1
    if missing:
        report.warn("%d prop(s) have no valueUnits; the total cannot match level.expected" % missing)
    wanted_count = expected.get("propCount")
    if wanted_count is not None and count != int(wanted_count):
        report.err("propCount %d does not match level.expected.propCount %d (%+d)"
                   % (count, int(wanted_count), count - int(wanted_count)))
    wanted_units = expected.get("valueUnits")
    if wanted_units is not None and not missing and units != int(wanted_units):
        report.err("valueUnits %d does not match level.expected.valueUnits %d (%+d)"
                   % (units, int(wanted_units), units - int(wanted_units)))

    # Optional design totals for the structure side. They exist because a curve_ramp makes
    # "how many structures" ambiguous: it is ONE logical ramp but N static OBBs. So the two
    # counters are separate and a curve counts once in `structureCount` and N times in
    # `staticObbs` (CONTACT_MODEL_V2 5.7's "one logical ramp, N segments").
    structures = [structure for structure in doc.get("structures") or []
                  if isinstance(structure, dict)]
    wanted_structures = expected.get("structureCount")
    if wanted_structures is not None:
        try:
            wanted_structures = int(wanted_structures)
        except (TypeError, ValueError):
            report.err("level.expected.structureCount must be a whole number (got %r)"
                       % (expected.get("structureCount"),))
        else:
            if len(structures) != wanted_structures:
                report.err("structureCount %d does not match level.expected.structureCount %d "
                           "(%+d); a curve_ramp counts once however many segments it expands to"
                           % (len(structures), wanted_structures,
                              len(structures) - wanted_structures))
    wanted_obbs = expected.get("staticObbs")
    if wanted_obbs is not None:
        try:
            wanted_obbs = int(wanted_obbs)
        except (TypeError, ValueError):
            report.err("level.expected.staticObbs must be a whole number (got %r)"
                       % (expected.get("staticObbs"),))
        else:
            obbs = static_obb_count(doc)
            if obbs != wanted_obbs:
                report.err("staticObbs %d does not match level.expected.staticObbs %d (%+d); it "
                           "counts boxes + ramp slabs + curve segments"
                           % (obbs, wanted_obbs, obbs - wanted_obbs))


def _check_determinism(path, doc, report):
    if path is None:
        return
    path = Path(path)
    if not path.is_file():
        return
    canonical_text = dump_doc(doc)
    actual_text = path.read_text(encoding="utf-8")
    if canonical_text != actual_text:
        report.warn("%s is not in canonical form (run `levelbuild.py format %s`)"
                    % (path, path))
    if actual_text.startswith("\ufeff"):
        report.err("%s starts with a UTF-8 BOM" % path)


def validate(doc, path=None, manifests=None):
    """Run every hard rule; returns a :class:`Report` (``report.ok`` is the gate)."""
    report = Report()
    manifests = manifests if manifests is not None else load_manifests()
    report.manifests = manifests
    if not isinstance(doc, dict):
        report.err("the document must be a JSON object")
        return report

    # A def that declares its own ``model`` may keep its manifest beside the model; the
    # caller's manifests win, and the merge happens after the shape check so a malformed
    # document still gets the one clean error it always did.
    manifests = manifests_with_models(doc, manifests)
    report.manifests = manifests
    _check_schema(doc, report)
    catalog = def_catalog(doc)
    _check_defs(doc, report, manifests, catalog)
    boxes = _check_props(doc, report, manifests, catalog)
    structures_by_id = _check_structures(doc, report)
    _check_budget(doc, report, structures_by_id)
    _check_arena(doc, report, structures_by_id, path)
    _check_bounds_and_spawn(doc, report, boxes)
    _check_support_and_overlap(doc, report, boxes, structures_by_id)
    _check_parents(doc, report, boxes)
    _check_expected(doc, report)
    _check_determinism(path, doc, report)
    report.tables.update(population_tables(doc, manifests))
    return report


# --------------------------------------------------------------------------- #
# population / three-state prediction tables (design section 8.13 and 10.3)
# --------------------------------------------------------------------------- #
def population_tables(doc, manifests=None):
    """Zone, definition, tier and three-state tables for ``check --report``."""
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    level = doc.get("level") or {}
    floor = ground_level(doc)
    structures_by_id = _index_structures(doc)

    def prop_box(prop):
        """World AABB of a prop, or ``None`` when its fields do not parse."""
        try:
            return world_aabb(tuple(float(v) for v in prop["position"]),
                              tuple(float(v) for v in prop["aabb"]),
                              tuple(float(v) for v in prop["rotationQuat"]))
        except (KeyError, TypeError, ValueError):
            return None

    def support_height(prop, box):
        """Height of the surface under the prop (its declared support's top)."""
        if prop.get("stackedOn"):
            return box[0][1]
        support = prop.get("support")
        if support and support in structures_by_id:
            try:
                normal, point = structure_top_plane(structures_by_id[support])
                height = plane_height_at(normal, point, float(prop["position"][0]),
                                         float(prop["position"][2]))
                if height is not None:
                    return height
            except (KeyError, TypeError, ValueError):
                pass
        return floor

    def prop_rise(prop, box):
        """``rise`` of design section 10.3: top of the prop above its support surface."""
        return max(0.0, box[1][1] - support_height(prop, box))

    catalog = def_catalog(doc)
    zones = {}
    defs = {}
    for prop in props:
        zone = prop.get("zone") or "(none)"
        entry = zones.setdefault(zone, {"zone": zone, "props": 0, "valueUnits": 0, "absorbable": 0})
        entry["props"] += 1
        value = effective_value_units(prop, catalog)
        if value is not None:
            entry["valueUnits"] += value
        if prop.get("absorbable") is True:
            entry["absorbable"] += 1
        def_id = prop.get("def") or "(none)"
        definition = defs.setdefault(def_id, {"def": def_id, "props": 0, "valueUnits": 0,
                                             "volumeK": effective_volume_k({"def": def_id},
                                                                           catalog)})
        definition["props"] += 1
        if value is not None:
            definition["valueUnits"] += value

    tiers = []
    seen_absorbable = set()
    for diameter in TIER_DIAMETERS:
        step = STEP_RATIO * diameter
        ball_mm = floor_mm(diameter)
        row = {"diameter": diameter, "ballMm": ball_mm, "absorbableProps": 0,
               "absorbableUnits": 0, "newProps": 0, "newUnits": 0, "terrain": 0,
               "blocking": 0, "food": 0}
        for prop in props:
            try:
                aabb = tuple(float(v) for v in prop["aabb"])
            except (KeyError, TypeError, ValueError):
                continue
            req_mm = absorb_diameter_req_mm(aabb, effective_volume_k(prop, catalog))
            value = effective_value_units(prop, catalog) or 0
            if req_mm <= ball_mm:
                row["food"] += 1
                row["absorbableProps"] += 1
                row["absorbableUnits"] += value
                item_id = prop.get("itemId")
                if item_id not in seen_absorbable:
                    row["newProps"] += 1
                    row["newUnits"] += value
            else:
                box = prop_box(prop)
                rise = prop_rise(prop, box) if box is not None else 0.0
                if rise <= step + 1e-9:
                    row["terrain"] += 1
                else:
                    row["blocking"] += 1
        tiers.append(row)
        for prop in props:
            try:
                aabb = tuple(float(v) for v in prop["aabb"])
            except (KeyError, TypeError, ValueError):
                continue
            if absorb_diameter_req_mm(aabb, effective_volume_k(prop, catalog)) <= ball_mm:
                seen_absorbable.add(prop.get("itemId"))

    structure_rows = []
    ball_radius_m = curve_ball_radius_m(level)
    for structure_id in sorted(structures_by_id):
        structure = structures_by_id[structure_id]
        try:
            top = structure_top_height(structure, ball_radius_m)
        except (KeyError, TypeError, ValueError):
            continue
        if structure.get("role") in ("boundary", "ground"):
            # A wall's "rise" is not a climb: it is a plane the motor never stands on.
            try:
                height = float(structure["center"][1])
            except (KeyError, TypeError, ValueError):
                height = top
            structure_rows.append({
                "id": structure_id, "role": structure.get("role"), "top": round(height, 3),
                "rise": None if structure.get("role") == "boundary" else 0.0,
                "terrainAtDiameter": None if structure.get("role") == "boundary" else 0.0,
                "slopeDeg": round(structure_slope_deg(structure), 2)})
            continue
        rise = max(0.0, top - floor)
        threshold = rise / STEP_RATIO if STEP_RATIO else float("inf")
        structure_rows.append({
            "id": structure_id,
            "role": structure.get("role"),
            "top": round(top, 3),
            "rise": round(rise, 3),
            "terrainAtDiameter": round(threshold, 3),
            "slopeDeg": round(structure_slope_deg(structure), 2),
        })

    start = level.get("ballDiameterStart")
    return {
        "zones": sorted(zones.values(), key=lambda row: row["zone"]),
        "defs": sorted(defs.values(), key=lambda row: row["def"]),
        "tiers": tiers,
        "structures": structure_rows,
        "startDiameter": start,
        "groundLevel": round(floor, 4),
    }


def format_report_tables(tables, expected=None):
    """Render the tables as plain text (the CLI prints this; no third-party deps)."""
    lines = []
    lines.append("%-14s %5s %6s %10s" % ("zone", "props", "units", "absorbable"))
    for row in tables.get("zones", []):
        lines.append("%-12s %5d %6d %10d" % (row["zone"], row["props"], row["valueUnits"],
                                             row["absorbable"]))
    lines.append("")
    lines.append("%-22s %5s %6s %7s" % ("def", "props", "units", "k"))
    for row in tables.get("defs", []):
        lines.append("%-22s %5d %6d %7.2f" % (row["def"], row["props"], row["valueUnits"],
                                             row.get("volumeK", DEFAULT_VOLUME_K)))
    lines.append("")
    lines.append("%-6s %6s %5s %6s %5s %5s %7s %8s"
                 % ("tier D", "ball mm", "food", "units", "new", "newU", "terrain", "blocking"))
    for row in tables.get("tiers", []):
        lines.append("%.2f %7d %6d %6d %5d %5d %8d %9d"
                     % (row["diameter"], row.get("ballMm", floor_mm(row["diameter"])),
                        row["food"], row["absorbableUnits"], row["newProps"],
                        row["newUnits"], row["terrain"], row["blocking"]))
    lines.append("")
    lines.append("%-16s %-9s %6s %7s %10s %6s"
                 % ("structure", "role", "top", "rise", "terrain@D", "slope"))
    for row in tables.get("structures", []):
        terrain_at = row["terrainAtDiameter"]
        if terrain_at is None:
            rise_text, terrain_text = "wall", "-"
        else:
            rise_text = "%7.3f" % row["rise"]
            terrain_text = ("%.2f" % terrain_at) if terrain_at < 1e4 else "never"
        lines.append("%-16s %-9s %6.3f %s %10s %6.1f"
                     % (row["id"], row["role"], row["top"], rise_text, terrain_text,
                        row["slopeDeg"]))
    curves = tables.get("curves") or []
    if curves:
        lines.append("")
        lines.append("%-16s %-5s %6s %5s %5s %9s %7s %6s"
                     % ("curve", "bend", "R", "arc", "segs", "sagitta", "overlap", "jump"))
        for row in curves:
            lines.append("%-16s %-5s %6.2f %5.1f %5d %8.1fmm %7.3f %5.1f"
                         % (row["id"], row["bend"], row["radiusM"], row["arcDeg"],
                            row["segments"], row["sagittaM"] * 1000.0, row["overlap"],
                            row["normalJumpDeg"]))
        summary = tables.get("curveSummary") or {}
        lines.append("curve ramps: %d logical / %d slab segment(s); smallest ball r=%.3f m"
                     % (summary.get("ramps", 0), summary.get("segments", 0),
                        summary.get("ballRadiusM", CURVE_MIN_BALL_RADIUS_M)))
    budget = tables.get("budget") or {}
    if budget:
        lines.append("static OBBs: %s/%d (boxes %s + ramp slabs %s + curve segments %s)"
                     % (budget.get("staticObbs"), STATIC_OBB_CAPACITY,
                        budget.get("counts", {}).get("boxes"),
                        budget.get("counts", {}).get("ramps", 0) - budget.get("curveRamps", 0),
                        budget.get("curveSegments", 0)))
    if expected:
        lines.append("")
        lines.append("expected: %s" % json.dumps(expected, ensure_ascii=False, sort_keys=True))
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# small path helpers shared by the CLI and the Blender scripts
# --------------------------------------------------------------------------- #
#: A level id is its directory under ``Docs/Design/Levels``.  One level of nesting is
#: allowed so a field made of many small arenas can keep one document per cell
#: (``physics_test_field/C14``) instead of one document per scene, which is what the
#: motor's per-arena capacity forces (see ``_check_arena``).
#: The first character may be ``_`` so a local (git-ignored) probe level such as
#: ``_probe`` can be addressed by id; the shape is still one safe path component (or
#: one nested field cell), never an escape.
_LEVEL_ID_RE = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.-]*(/[A-Za-z0-9][A-Za-z0-9_.-]*)?$")


def is_valid_level_id(level_id):
    return bool(level_id) and bool(_LEVEL_ID_RE.match(level_id)) and ".." not in level_id


def level_folder(level_id, levels_dir=None):
    if not is_valid_level_id(level_id):
        raise ValueError("bad level id %r (letters, digits, '_', '-', '.', an optional "
                         "leading '_', and at most one '/' for a field cell)" % (level_id,))
    return Path(levels_dir or LEVELS_DIR) / level_id


def placement_path(level_id, levels_dir=None):
    return level_folder(level_id, levels_dir) / "placement.json"


def prop_fbx(def_id, props_dir=None, model=None):
    """The Unity model asset this tool instantiates for a def.

    A def-level ``model`` (a Unity-relative ``.fbx`` path) **wins**: it is what lets a
    def stop being defined by its id, so an external asset pack does not have to
    masquerade as a repo prop.  Without one the historic repo path is unchanged.
    """
    if model:
        return UNITY_ROOT / model
    return Path(props_dir or UNITY_ROOT / "Assets/VRC_Katamari/Art/Props") \
        / def_id / (def_id + ".fbx")


def prop_glb(def_id, exports_dir=None):
    """The Blender library asset for a def (the GLB, not the FBX)."""
    return Path(exports_dir or PROPS_EXPORTS_DIR) / (def_id + ".glb")


def is_valid_item_id(item_id):
    return isinstance(item_id, str) and item_id != "" and item_id.strip() == item_id \
        and "/" not in item_id and "\\" not in item_id


def sorted_prop_ids(doc):
    return sorted(prop.get("itemId") for prop in doc.get("props") or []
                  if isinstance(prop, dict) and isinstance(prop.get("itemId"), str))


def value_units_total(doc):
    """Sum of the effective value units (per-prop field, else ``defs[].valueUnits``)."""
    catalog = def_catalog(doc)
    total = 0
    for prop in doc.get("props") or []:
        if not isinstance(prop, dict):
            continue
        value = effective_value_units(prop, catalog)
        if value is not None:
            total += value
    return total


def expected_totals(doc):
    """The counters ``level.expected`` may declare, derived from the document.

    ``propCount``/``valueUnits`` are the content totals; ``structureCount`` counts
    ``structures[]`` entries (a curve_ramp is ONE) and ``staticObbs`` counts the solids the
    motor bakes (a curve contributes its N segments).  Keeping the pair separate is what
    lets a level say "4 ramps, 3 of them curves with 12 segments each" without the curve
    inflating the structure count.
    """
    return {
        "propCount": len([prop for prop in doc.get("props") or [] if isinstance(prop, dict)]),
        "valueUnits": value_units_total(doc),
        "structureCount": len([structure for structure in doc.get("structures") or []
                               if isinstance(structure, dict)]),
        "staticObbs": static_obb_count(doc),
    }


def display_path(path):
    """Repo-relative path when possible (reports must not depend on the checkout)."""
    path = Path(path).resolve()
    try:
        return str(path.relative_to(ROOT))
    except ValueError:
        return str(path)
