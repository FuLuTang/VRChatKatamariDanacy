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

import json
import math
import os
import re
from pathlib import Path

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

#: ``D_req = (60 * a * b * c / pi) ** (1/3)`` -- the equivalent sphere diameter
#: whose volume is ten times the prop's box volume
#: (``a*b*c <= (pi/6) * D**3 / 10``).  The design quotes it as ``2.673 * cbrt(abc)``.
VOLUME_GATE_FACTOR = 60.0

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
#: Cap on per-check detail lines so one broken file cannot flood the console.
DETAIL_LIMIT = 25

PROP_ROLES = ("ground", "boundary", "box", "ramp")
SHAPES = ("box", "plane")
COLLISIONS = ("box", "none")
CAMERA_TYPES = ("perspective", "ortho_top")

ROOT = Path(__file__).resolve().parents[2]
PROPS_DIR = ROOT / "Docs/Art/OriginalModels/props"
PROPS_EXPORTS_DIR = PROPS_DIR / "exports"
PROP_CATALOG = ROOT / "Docs/Design/Maps/PicnicLawn/prop_catalog.csv"
LEVELS_DIR = ROOT / "Docs/Design/Levels"

PROP_KEYS = ("itemId", "def", "kind", "zone", "group", "position", "rotationQuat",
             "support", "stackedOn", "aabb", "absorbDiameterReq", "absorbable",
             "valueUnits", "notes")
STRUCTURE_KEYS = ("id", "kind", "role", "shape", "center", "size", "rotationQuat",
                  "collision", "absorbable", "support", "meets", "material", "notes")
CAMERA_KEYS = ("id", "type", "location", "target", "lens", "coverage_m")
PRESET_KEYS = ("id", "camera", "engine", "res", "samples")
DEF_KEYS = ("id", "kind", "valueUnits", "notes")
LEVEL_KEYS = ("id", "scene", "units", "convention", "ballStart", "playerSpawn",
              "ballDiameterStart", "ballDiameterMax", "groundY", "bounds",
              "spawnClearanceM", "budget", "expected", "zones")
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
    """``(normal, point)`` of the structure's +Y face."""
    normal = structure_up(structure)
    point = add(structure["center"], scale(normal, structure["size"][1] * 0.5))
    return normal, point


def plane_height_at(normal, point, x, z):
    """Height of a (non-vertical) plane at ``(x, z)``; ``None`` when vertical."""
    if abs(normal[1]) < 1e-6:
        return None
    return point[1] - (normal[0] * (x - point[0]) + normal[2] * (z - point[2])) / normal[1]


def structure_slope_deg(structure):
    """Tilt of the structure's +Y face away from world up."""
    up = structure_up(structure)
    cosine = max(-1.0, min(1.0, abs(up[1])))
    return math.degrees(math.acos(cosine))


def structure_world_aabb(structure):
    return world_aabb(structure["center"], structure["size"], structure["rotationQuat"])


def structure_top_height(structure):
    """Highest world Y of the structure (top of its world AABB)."""
    return structure_world_aabb(structure)[1][1]


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


def absorb_diameter_req(size):
    """``D_req = (60 * a * b * c / pi) ** (1/3)``: the ball that may absorb this box."""
    a, b, c = size
    if a <= 0.0 or b <= 0.0 or c <= 0.0:
        raise ValueError("absorb_diameter_req needs a positive box size, got %r" % (size,))
    return (VOLUME_GATE_FACTOR * a * b * c / math.pi) ** (1.0 / 3.0)


def is_absorbable(size, ball_diameter_max):
    return absorb_diameter_req(size) <= ball_diameter_max + 1e-9


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


def load_manifests(exports_dir=None):
    """``{def: {"aabb": (x, y, z), "category": str, "manifest": dict}}``."""
    directory = Path(exports_dir or PROPS_EXPORTS_DIR)
    manifests = {}
    if not directory.is_dir():
        return manifests
    for path in sorted(directory.glob("*.manifest.json")):
        document = json.loads(path.read_text(encoding="utf-8"))
        def_id = document.get("id") or path.name[:-len(".manifest.json")]
        dimensions = document.get("dimensions_m") or {}
        try:
            aabb = manifest_aabb(dimensions)
        except KeyError:
            continue
        manifests[def_id] = {
            "aabb": aabb,
            "category": document.get("category") or "",
            "longest_m": dimensions.get("longest"),
            "manifest": document,
        }
    return manifests


# --------------------------------------------------------------------------- #
# canonical document (deterministic bytes; this is what --format writes)
# --------------------------------------------------------------------------- #
def _ordered(source, keys):
    return {key: source[key] for key in keys if key in source}


def _sort_key(item_id):
    return (str(item_id))


def canonical_doc(doc, manifests=None):
    """The document in canonical form: sorted, rounded, ``-0.0`` cleaned.

    ``absorbDiameterReq`` and ``absorbable`` are recomputed here (they are
    derived fields, design section 4.2), so a hand-edited file cannot keep a
    stale gate.
    """
    manifests = manifests if manifests is not None else load_manifests()
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
                value = int(value)
            out[key] = value
        aabb = out.get("aabb")
        if aabb and all(v > 0 for v in aabb):
            out["absorbDiameterReq"] = rounded(absorb_diameter_req(aabb), DIAMETER_DECIMALS)
            out["absorbable"] = bool(absorb_diameter_req(aabb) <= ball_max + 1e-9)
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
            elif key == "rotationQuat":
                value = rounded_seq(canonical_quat(tuple(float(v) for v in value)), QUAT_DECIMALS)
            out[key] = value
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
                value = int(value)
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


def _structure_world_aabb(structure, report):
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
        if manifests and def_id not in manifests:
            report.warn("%s def %r has no manifest in props/exports (no model yet)" % (label, def_id))
        value = entry.get("valueUnits")
        if value is None:
            report.err("%s.valueUnits is required (it is what the def is worth)" % label)
        elif not isinstance(value, int) or isinstance(value, bool) or value < 1:
            report.err("%s.valueUnits must be a whole number >= 1 (got %r)" % (label, value))
        kind = entry.get("kind")
        if kind is not None and (not isinstance(kind, str) or not kind.strip()):
            report.err("%s.kind must be a non-empty string when present" % label)


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
        elif def_id not in manifests:
            report.err("%s def %r has no manifest in %s" % (label, def_id, "props/exports"))

        kind = prop.get("kind")
        if kind is not None and (not isinstance(kind, str) or not kind.strip()):
            report.err("%s.kind must be a non-empty string when present" % label)
        if isinstance(kind, str) and kind.strip() and not kind.islower():
            report.warn("%s.kind %r is not lower case; the Unity kind folder uses it verbatim"
                        % (label, kind))
        zone = prop.get("zone")
        if zone is not None and not isinstance(zone, str):
            report.err("%s.zone must be a string" % label)

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

        if "absorbDiameterReq" in prop:
            dreq = absorb_diameter_req(aabb)
            declared = as_float(prop.get("absorbDiameterReq"), "%s.absorbDiameterReq" % label, report)
            if declared is not None and abs(declared - dreq) > 1e-3:
                report.err("%s absorbDiameterReq %.3f is stale; the aabb gives %.3f"
                           % (label, declared, dreq))
        ball_max = level.get("ballDiameterMax")
        if ball_max is not None:
            try:
                ball_max = float(ball_max)
            except (TypeError, ValueError):
                ball_max = None
        if ball_max is not None:
            derived = absorb_diameter_req(aabb) <= ball_max + 1e-9
            if "absorbable" in prop and bool(prop.get("absorbable")) != derived:
                report.err("%s absorbable=%r but D_req=%.3f vs ballDiameterMax=%.2f says %r"
                           % (label, prop.get("absorbable"), absorb_diameter_req(aabb), ball_max, derived))

        value_units = prop.get("valueUnits")
        if value_units is not None:
            if not isinstance(value_units, int) or isinstance(value_units, bool) or value_units < 1:
                report.err("%s.valueUnits must be a whole number >= 1 (got %r)" % (label, value_units))
        elif effective_value_units(prop, catalog) is None:
            report.err("%s has no valueUnits and def %r has no defs[].valueUnits entry"
                       % (label, prop.get("def")))

        boxes[item_id] = world_aabb(position, aabb, quat)

    return boxes


def _check_structures(doc, report):
    """Structure field validity, the ramp/box identity rule and the ramp geometry."""
    structures = doc.get("structures")
    if not isinstance(structures, list):
        report.err("structures must be a list")
        return {}
    seen = {}
    ground = []
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
        for key in ("center", "size", "rotationQuat"):
            if key not in structure:
                report.err("%s.%s is required" % (label, key))
        size = as_vec3(structure.get("size"), "%s.size" % label, report)
        if size is not None and any(v <= 0.0 for v in size):
            report.err("%s.size must be positive on every axis, got %r" % (label, size))
        center = as_vec3(structure.get("center"), "%s.center" % label, report)
        quat = as_quat_xyzw(structure.get("rotationQuat"), "%s.rotationQuat" % label, report)
        if center is None or size is None or quat is None:
            continue

        if role == "boundary":
            up = structure_up(structure)
            if abs(up[1]) > 0.01:
                report.err("%s role=boundary needs a vertical plane (its local +Y is the inward "
                           "normal); this one is tilted" % label)

        slope = structure_slope_deg(structure)
        if role == "ramp":
            if slope > RAMP_MAX_SLOPE_DEG + 0.01:
                report.err("%s ramp %r is %.1f deg, over the %.0f deg ceiling; make it a box"
                           % (label, structure_id, slope, RAMP_MAX_SLOPE_DEG))
            corners = box_corners(center, size, quat)
            foot = min(corner[1] for corner in corners)
            floor = ground_level(doc)
            if foot < floor - SINK_TOLERANCE_M:
                report.err("%s ramp %r foot is %.3f m below the ground (%.3f); it is sunk"
                           % (label, structure_id, foot, floor))
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
    return seen


_BUDGET_ROLE = {"planes": "boundary", "boxes": "box", "ramps": "ramp"}


def _check_budget(doc, report, structures_by_id):
    level = doc.get("level") or {}
    budget = level.get("budget") or {}
    roles = {"boundary": [], "box": [], "ramp": []}
    for structure in structures_by_id.values():
        role = structure.get("role")
        if role in roles:
            roles[role].append(structure.get("id"))
    counts = {"planes": len(roles["boundary"]), "boxes": len(roles["box"]),
              "ramps": len(roles["ramp"])}
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
            report.err("%s=%d over the budget of %d: %s"
                       % (key, count, limit, ", ".join(roles[_BUDGET_ROLE[key]])))
    report.tables["budget"] = {"counts": counts,
                              "limits": {k: budget.get(k) for k in BUDGET_KEYS}}


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
    stack = prop.get("stackedOn")
    if stack:
        return ("stack", stack)
    support = prop.get("support")
    if support:
        structure = structures_by_id.get(support)
        if structure is None:
            report.err("%s.support %r is not a structure id" % (prop.get("itemId"), support))
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
                report.err("%s.stackedOn %r is not a prop itemId" % (item_id, payload))
                continue
            expected = boxes[payload][1][1]
            if abs(bottom - expected) > STACK_TOLERANCE_M:
                report.err("%s sits %.3f m off the top of %r (its bottom %.3f, that top %.3f)"
                           % (item_id, bottom - expected, payload, bottom, expected))
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

    # prop/prop interpenetration, ignoring a declared stack relationship
    ids = sorted(boxes)
    printed = 0
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
            if left.get("stackedOn") == right_id or right.get("stackedOn") == left_id:
                continue
            if printed < DETAIL_LIMIT:
                report.err("%s and %s overlap %.0f%% of the smaller AABB"
                           % (left_id, right_id, fraction * 100.0))
            printed += 1
    if printed > DETAIL_LIMIT:
        report.err("... %d more prop/prop overlap(s)" % (printed - DETAIL_LIMIT))

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
        for structure_id, structure in sorted(structures_by_id.items()):
            if structure.get("collision") == "none":
                continue
            try:
                worst = max(obb_penetration(corner, structure) for corner in corners)
            except (KeyError, TypeError, ValueError):
                continue
            if worst > STRUCTURE_PENETRATION_M:
                if printed < DETAIL_LIMIT:
                    report.err("%s penetrates structure %r by %.3f m"
                               % (item_id, structure_id, worst))
                printed += 1
    if printed > DETAIL_LIMIT:
        report.err("... %d more prop/structure penetration(s)" % (printed - DETAIL_LIMIT))


def _check_expected(doc, report):
    level = doc.get("level") or {}
    expected = level.get("expected")
    if not isinstance(expected, dict):
        report.err("level.expected must be an object with propCount and valueUnits")
        return
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

    _check_schema(doc, report)
    catalog = def_catalog(doc)
    _check_defs(doc, report, manifests, catalog)
    boxes = _check_props(doc, report, manifests, catalog)
    structures_by_id = _check_structures(doc, report)
    _check_budget(doc, report, structures_by_id)
    _check_bounds_and_spawn(doc, report, boxes)
    _check_support_and_overlap(doc, report, boxes, structures_by_id)
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
        definition = defs.setdefault(def_id, {"def": def_id, "props": 0, "valueUnits": 0})
        definition["props"] += 1
        if value is not None:
            definition["valueUnits"] += value

    tiers = []
    seen_absorbable = set()
    for diameter in TIER_DIAMETERS:
        step = STEP_RATIO * diameter
        row = {"diameter": diameter, "absorbableProps": 0, "absorbableUnits": 0,
               "newProps": 0, "newUnits": 0, "terrain": 0, "blocking": 0, "food": 0}
        for prop in props:
            try:
                aabb = tuple(float(v) for v in prop["aabb"])
            except (KeyError, TypeError, ValueError):
                continue
            dreq = absorb_diameter_req(aabb)
            value = effective_value_units(prop, catalog) or 0
            if dreq <= diameter + 1e-9:
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
            if absorb_diameter_req(aabb) <= diameter + 1e-9:
                seen_absorbable.add(prop.get("itemId"))

    structure_rows = []
    for structure_id in sorted(structures_by_id):
        structure = structures_by_id[structure_id]
        try:
            top = structure_top_height(structure)
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
    lines.append("%-22s %5s %6s" % ("def", "props", "units"))
    for row in tables.get("defs", []):
        lines.append("%-22s %5d %6d" % (row["def"], row["props"], row["valueUnits"]))
    lines.append("")
    lines.append("%-6s %5s %6s %5s %5s %7s %8s"
                 % ("tier D", "food", "units", "new", "newU", "terrain", "blocking"))
    for row in tables.get("tiers", []):
        lines.append("%.2f %7d %6d %5d %5d %8d %9d"
                     % (row["diameter"], row["food"], row["absorbableUnits"], row["newProps"],
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
    if expected:
        lines.append("")
        lines.append("expected: %s" % json.dumps(expected, ensure_ascii=False, sort_keys=True))
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# small path helpers shared by the CLI and the Blender scripts
# --------------------------------------------------------------------------- #
_LEVEL_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")


def is_valid_level_id(level_id):
    return bool(level_id) and bool(_LEVEL_ID_RE.match(level_id)) and ".." not in level_id


def level_folder(level_id, levels_dir=None):
    if not is_valid_level_id(level_id):
        raise ValueError("bad level id %r (letters, digits, '_', '-', '.' only)" % (level_id,))
    return Path(levels_dir or LEVELS_DIR) / level_id


def placement_path(level_id, levels_dir=None):
    return level_folder(level_id, levels_dir) / "placement.json"


def prop_fbx(def_id, props_dir=None):
    """The Unity model asset this tool instantiates for a def."""
    return Path(props_dir or ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Art/Props") \
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


def display_path(path):
    """Repo-relative path when possible (reports must not depend on the checkout)."""
    path = Path(path).resolve()
    try:
        return str(path.relative_to(ROOT))
    except ValueError:
        return str(path)
