"""Collider-proxy fitting for props: the one place that turns a prop mesh into a
`defs[].collider` block (sphere / capsule / box / convex hull).

Why this module exists
----------------------
A prop's *gameplay* volume (``propBounds``, the absorb gate) and its *collision*
shape are different things (``PickupAbsorb`` / ``COLLIDER_LAYER``).  Until now a
prop's collision shape was exactly its visual-mesh AABB, so a watermelon blocked
as a 0.30 x 0.24 x 0.22 box - its corners stick ~40 % past the fruit.  This module
fits a cheap convex proxy that follows the visible envelope, while the absorb gate
keeps using the AABB volume unchanged.

The model that is emitted (all numbers are metres, in the prop's own local space,
which for an FBX baked with ``axis_up=Y`` is Unity's local frame; see
``Docs/Design/PickupAbsorb/COLLIDER_PROXY.md``):

    {"collider": {
        "type": "box" | "sphere" | "capsule" | "convex",
        "center": [x, y, z],
        "size": [x, y, z],          # box only: full edge lengths
        "radius": r,                # sphere / capsule
        "halfHeight": h,            # capsule only: half length of the segment
        "axis": "x" | "y" | "z",    # capsule only
        "hull": [[x, y, z], ...],   # convex only: 4..24 points
        "source": "auto" | "manual",
        "fit_error": e              # [0, 1], see below
    }}

Fit error
---------
``fit_error = max distance from the prop surface to the proxy surface / proxy
largest extent``.  The distance is **unsigned** (a surface is not a solid: an
ellipsoid inside its AABB has a large positive distance to the box surface), so a
proxy that is too big is penalised exactly like one that is too small.  The prop
surface is sampled at the vertices and triangle centroids; for a **concave** mesh
only samples on the outer envelope (within ``OUTER_ENVELOPE_TOLERANCE_RATIO`` of
the convex hull) are used, because no convex collider can follow a cavity and a
cavity a ball cannot enter is not a collision-shape requirement.  The envelope
fraction is reported so the decision is auditable.

The companion number is ``volume_ratio = proxy volume / convex-hull volume``:
1.0 means the proxy fills the envelope exactly, 1.91 is what a box does to any
ellipsoid (8abc / (4/3 pi abc) = 6/pi), and a value below 1 means the proxy is
smaller than the visible envelope (it would let the ball clip).

Pure standard library, deterministic: the same input gives byte-identical output
(no sets iterated as sets, no timestamps, no floating RNG).
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass, field
from pathlib import Path

# --------------------------------------------------------------------------- #
# schema constants
# --------------------------------------------------------------------------- #
COLLIDER_TYPES = ("box", "sphere", "capsule", "convex")
COLLIDER_SOURCES = ("auto", "manual")
CAPSULE_AXES = ("x", "y", "z")
HULL_MIN_POINTS = 4
HULL_MAX_POINTS = 24

#: Fields of one ``collider`` block, in canonical write order.
COLLIDER_KEYS = ("type", "center", "size", "radius", "halfHeight", "axis", "hull",
                 "source", "fit_error")

#: Geometry fields each type is allowed to carry.  An unknown or cross-type field is
#: an error, not a silently ignored extra: a typo such as ``raduis`` would otherwise
#: turn a sphere into a default box.
_TYPE_FIELDS = {
    "box": ("center", "size", "source", "fit_error"),
    "sphere": ("center", "radius", "source", "fit_error"),
    "capsule": ("center", "radius", "halfHeight", "axis", "source", "fit_error"),
    "convex": ("center", "hull", "source", "fit_error"),
}

#: Decision thresholds (see COLLIDER_PROXY.md for the geometry / perception
#: argument behind each number).
SPHERE_MAX_DEVIATION_RATIO = 0.08   # of the fitted radius
CAPSULE_MAX_DEVIATION_RATIO = 0.10  # of the fitted radius
BOX_MAX_ERROR_RATIO = 0.05          # of the box's largest extent
HULL_MAX_ERROR_RATIO = 0.10         # of the hull's largest extent

#: A sphere or capsule may not be more than this much larger than the convex
#: envelope.  Without it, a flat disk is "perfectly" fitted by a huge sphere whose
#: surface barely deviates from the disk over the small cap that contains it (the
#: plate test: a 0.45 m sphere for a 0.24 x 0.025 m plate, 64x the envelope volume).
#: A round proxy up to 1.5x the envelope still reads as the object; 1.91x is the
#: AABB-on-ellipsoid limit we are replacing, so 1.5 keeps a clear margin below it.
MAX_ROUND_PROXY_VOLUME_RATIO = 1.5

#: A box may not enclose more than this much more than the convex envelope.  The
#: one-sided surface error cannot see a box's corner excess when the object's own
#: surface is flush with the box faces (a round plate is flush with the top and
#: bottom faces, so its corner overhang is invisible to "mesh -> box" distance),
#: which is exactly the owner's complaint in the other direction.  A cylinder's
#: AABB/envelope is 4/pi = 1.27 and must be rejected; a bench is 1.08 and must
#: stay a box.  1.15 separates them and stays below the 1.91 an AABB does to an
#: ellipsoid.
MAX_BOX_VOLUME_RATIO = 1.15

#: Absolute error floor.  A proxy is accepted when its worst surface gap is at
#: most this, even if the relative threshold is tighter.  2 mm is below the ball's
#: 5 mm contact skin (``ContactTuning.ContactSkinM``) and below what a player can
#: see at arm's length, so a 12 mm button does not need a 24-point hull to save
#: 1 mm; a watermelon (300 mm) is unaffected because its gaps are 8-40 mm.
ABSOLUTE_ERROR_FLOOR_M = 0.002

#: A mesh sample counts as "outer envelope" when it is this close to the mesh's
#: convex hull.  It is a tessellation-noise floor (1 % of the largest extent, at
#: least 0.5 mm), not a shape tolerance.
OUTER_ENVELOPE_TOLERANCE_RATIO = 0.01
OUTER_ENVELOPE_TOLERANCE_MIN_M = 0.0005

#: Relative tolerance of the convexity check on an authored hull.
CONVEXITY_TOLERANCE_RATIO = 1e-3

#: Hull construction epsilon, relative to the point cloud's largest extent.
_HULL_EPS_RATIO = 1e-6

#: Cap on the number of surface samples used by a fit.  A 266-vertex fruit needs
#: far fewer; a 5 000-vertex model is decimated deterministically by stride.
_MAX_FIT_SAMPLES = 1200
_MAX_CAPSULE_SAMPLES = 600

_EPS = 1e-12


# --------------------------------------------------------------------------- #
# small vector helpers (tuples, no numpy)
# --------------------------------------------------------------------------- #
def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _mul(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _length(a):
    return math.sqrt(_dot(a, a))


def _distance(a, b):
    return _length(_sub(a, b))


def _mean(points):
    n = float(len(points))
    return (sum(p[0] for p in points) / n,
            sum(p[1] for p in points) / n,
            sum(p[2] for p in points) / n)


def _aabb(points):
    return ([min(p[i] for p in points) for i in range(3)],
            [max(p[i] for p in points) for i in range(3)])


def _extent(points):
    lo, hi = _aabb(points)
    return max(hi[i] - lo[i] for i in range(3))


def _finite_point(point):
    return (isinstance(point, (list, tuple)) and len(point) == 3
            and all(isinstance(c, (int, float)) and not isinstance(c, bool)
                    and math.isfinite(float(c)) for c in point))


# --------------------------------------------------------------------------- #
# hull: deterministic incremental convex hull
# --------------------------------------------------------------------------- #
def _face_normal(a, b, c):
    normal = _cross(_sub(b, a), _sub(c, a))
    length = _length(normal)
    if length <= _EPS:
        return None
    return _mul(normal, 1.0 / length)


def _signed_plane(point, a, normal):
    return _dot(normal, _sub(point, a))


def convex_hull(points):
    """Return ``(hull_points, faces)`` or ``None`` when the cloud is degenerate.

    QuickHull.  Faces are kept as an explicit list plus a per-face outside-point
    list; the visible set is grown by BFS from the face that owns the farthest
    point, so it is always one connected patch and an interior face created by a
    numerical tie cannot corrupt the topology (the earlier "global visible set"
    version did exactly that on a 146-vertex banana).  ``faces`` are index triples
    into ``hull_points`` with outward orientation.
    """
    unique = []
    seen = set()
    for point in points:
        key = (float(point[0]), float(point[1]), float(point[2]))
        if key in seen:
            continue
        seen.add(key)
        unique.append(key)
    if len(unique) < 4:
        return None
    epsilon = max(_HULL_EPS_RATIO * max(_extent(unique), 1e-9), 1e-12)

    # 1. an initial tetrahedron, built from extremes (stable tie-breaking by index).
    i0 = min(range(len(unique)), key=lambda i: (unique[i][0], unique[i][1], unique[i][2]))
    p0 = unique[i0]
    i1 = max(range(len(unique)), key=lambda i: (_distance(unique[i], p0), -i))
    if _distance(unique[i1], p0) <= epsilon:
        return None
    p1 = unique[i1]
    best = -1.0
    i2 = -1
    for i, point in enumerate(unique):
        area2 = _length(_cross(_sub(p1, p0), _sub(point, p0)))
        if area2 > best:
            best, i2 = area2, i
    if i2 < 0 or best <= epsilon:
        return None
    p2 = unique[i2]
    normal = _face_normal(p0, p1, p2)
    if normal is None:
        return None
    best = -1.0
    i3 = -1
    for i, point in enumerate(unique):
        distance = abs(_signed_plane(point, p0, normal))
        if distance > best:
            best, i3 = distance, i
    if i3 < 0 or best <= epsilon:
        return None

    interior = _mean([unique[i0], unique[i1], unique[i2], unique[i3]])

    face_verts = []
    face_normals = []
    face_offsets = []
    face_outside = []
    alive = []

    def add_face(a, b, c):
        normal = _face_normal(unique[a], unique[b], unique[c])
        if normal is None:
            return -1
        if _signed_plane(interior, unique[a], normal) > 0.0:
            b, c = c, b
            normal = _face_normal(unique[a], unique[b], unique[c])
            if normal is None:
                return -1
        face_verts.append((a, b, c))
        face_normals.append(normal)
        face_offsets.append(_dot(normal, unique[a]))
        face_outside.append([])
        alive.append(True)
        return len(face_verts) - 1

    for a, b, c in ((i0, i1, i2), (i0, i3, i1), (i0, i2, i3), (i1, i3, i2)):
        if add_face(a, b, c) < 0:
            return None
    if len(face_verts) != 4:
        return None

    def signed(index, face):
        normal = face_normals[face]
        return _dot(normal, unique[index]) - face_offsets[face]

    # 2. assign every other point to the face it is most outside of.
    pending = []
    for index in range(len(unique)):
        if index in (i0, i1, i2, i3):
            continue
        best_distance = epsilon
        best_face = -1
        for face in range(4):
            distance = signed(index, face)
            if distance > best_distance:
                best_distance, best_face = distance, face
        if best_face >= 0:
            face_outside[best_face].append(index)
        else:
            pending.append(index)  # inside the initial tetra: nothing to do

    # 3. expand until no face has an outside point.
    guard = 0
    while True:
        guard += 1
        if guard > 10000:
            return None
        chosen_face = -1
        chosen_point = -1
        chosen_distance = epsilon
        for face in range(len(face_verts)):
            if not alive[face]:
                continue
            for index in face_outside[face]:
                distance = signed(index, face)
                if distance > chosen_distance:
                    chosen_distance, chosen_face, chosen_point = distance, face, index
        if chosen_face < 0:
            break

        # adjacency of the current live hull, for the BFS.
        edges = {}
        for face in range(len(face_verts)):
            if not alive[face]:
                continue
            a, b, c = face_verts[face]
            for edge in ((a, b), (b, c), (c, a)):
                edges.setdefault(tuple(sorted(edge)), []).append(face)

        visible = {chosen_face}
        stack = [chosen_face]
        while stack:
            face = stack.pop()
            a, b, c = face_verts[face]
            for edge in ((a, b), (b, c), (c, a)):
                for neighbour in edges.get(tuple(sorted(edge)), ()):
                    if neighbour in visible or not alive[neighbour]:
                        continue
                    if signed(chosen_point, neighbour) > epsilon:
                        visible.add(neighbour)
                        stack.append(neighbour)

        horizon = []
        for face in visible:
            a, b, c = face_verts[face]
            for edge in ((a, b), (b, c), (c, a)):
                neighbours = [n for n in edges.get(tuple(sorted(edge)), ())
                              if n not in visible]
                if neighbours or tuple(sorted(edge)) not in edges:
                    horizon.append(edge)
        # de-duplicate while preserving the visible-face orientation.
        seen_edges = set()
        unique_horizon = []
        for edge in horizon:
            if edge in seen_edges:
                continue
            seen_edges.add(edge)
            unique_horizon.append(edge)

        outside_points = [chosen_point]
        for face in visible:
            outside_points.extend(face_outside[face])
            alive[face] = False
            face_outside[face] = []

        new_faces = []
        for a, b in unique_horizon:
            index = add_face(a, b, chosen_point)
            if index >= 0:
                new_faces.append(index)
        if not new_faces:
            return None

        # Reassign the points that belonged to the removed patch.  A point may be
        # outside several new faces; the most-outside one owns it.
        for index in outside_points:
            best_distance = epsilon
            best_face = -1
            for face in new_faces:
                distance = signed(index, face)
                if distance > best_distance:
                    best_distance, best_face = distance, face
            if best_face >= 0:
                face_outside[best_face].append(index)

    # 4. compact to the vertices the live faces actually use, preserving input order.
    live_faces = [face_verts[i] for i in range(len(face_verts)) if alive[i]]
    if len(live_faces) < 4:
        return None
    used = sorted({index for face in live_faces for index in face})
    remap = {old: new for new, old in enumerate(used)}
    hull_points = [unique[index] for index in used]
    hull_faces = [(remap[a], remap[b], remap[c]) for a, b, c in live_faces]

    # 5. fail closed if the result is not a valid convex hull of the input cloud.
    validation_tolerance = max(1e-6, 1e-5 * max(_extent(hull_points), 1e-9))
    for point in unique:
        for a, b, c in hull_faces:
            normal = _face_normal(hull_points[a], hull_points[b], hull_points[c])
            if normal is None:
                return None
            if _signed_plane(point, hull_points[a], normal) > validation_tolerance:
                return None
    return hull_points, hull_faces


def hull_volume(hull_points, hull_faces):
    """Signed volume of a closed triangle hull (absolute value)."""
    total = 0.0
    for a, b, c in hull_faces:
        total += _dot(hull_points[a], _cross(hull_points[b], hull_points[c]))
    return abs(total) / 6.0


# --------------------------------------------------------------------------- #
# static solids: a sparse model as a few boxes (the motor's static table is OBB-only)
# --------------------------------------------------------------------------- #
#: A convex proxy filling less than this share of its own AABB is split into parts.  A
#: laundry deck (one pole, a foot across the full width, a top tray) fills 0.30: its one
#: box is a 1.43 m x 2 m wall where the original's level collision has no triangle.  The
#: 0.6 line keeps every post, fence and box-like body (fill >= 0.69 in Cancer) as one box.
PART_SPLIT_FILL_MAX = 0.6

#: At most this many boxes per solid.  The static table is 256 OBBs; Cancer holds 35 solids,
#: so 10 sparse ones at 4 parts each stay far below it (65).
PART_MAX_COUNT = 4

#: A cut is taken only while it removes at least this share of the whole AABB volume:
#: below it the extra OBB costs more motor time than the empty space it removes.
PART_MIN_GAIN_RATIO = 0.02

#: Candidate cut positions per axis per part: ``1..N-1`` of ``N`` equal steps.
PART_CUT_STEPS = 16

#: No part is thinner than this on any axis (a single quad has zero thickness; the arena
#: refuses a degenerate box).  1 cm is below the ball's smallest radius.
PART_MIN_SIZE_M = 0.01

#: Keys of one ``structures.solids[].parts[]`` entry, in write order.
PART_KEYS = ("center", "size")


def hull_fill_ratio(block):
    """Convex-hull volume / hull-AABB volume of a ``convex`` collider block, else ``None``."""
    if not isinstance(block, dict) or block.get("type") != "convex":
        return None
    points = hull_points(block)
    if points is None or len(points) < HULL_MIN_POINTS:
        return None
    hull = convex_hull(points)
    if hull is None:
        return None
    low, high = _aabb(points)
    box = (high[0] - low[0]) * (high[1] - low[1]) * (high[2] - low[2])
    if box <= _EPS:
        return None
    return hull_volume(*hull) / box


def _clip_polygon(polygon, axis, cut, keep_below):
    """The part of one convex polygon on one side of the plane ``p[axis] = cut``."""
    out = []
    count = len(polygon)
    for index in range(count):
        current = polygon[index]
        following = polygon[(index + 1) % count]
        a = current[axis] - cut
        b = following[axis] - cut
        inside_a = a <= 0.0 if keep_below else a >= 0.0
        inside_b = b <= 0.0 if keep_below else b >= 0.0
        if inside_a:
            out.append(current)
        if inside_a != inside_b and a != b:
            t = a / (a - b)
            point = [current[k] + (following[k] - current[k]) * t for k in range(3)]
            point[axis] = cut
            out.append(tuple(point))
    return out


def _polygons_bounds(polygons):
    low = [math.inf, math.inf, math.inf]
    high = [-math.inf, -math.inf, -math.inf]
    for polygon in polygons:
        for point in polygon:
            for k in range(3):
                if point[k] < low[k]:
                    low[k] = point[k]
                if point[k] > high[k]:
                    high[k] = point[k]
    return low, high


def _box_volume(low, high):
    return (max(0.0, high[0] - low[0]) * max(0.0, high[1] - low[1])
            * max(0.0, high[2] - low[2]))


def split_mesh_into_boxes(vertices, faces, max_parts=PART_MAX_COUNT,
                          min_gain_ratio=PART_MIN_GAIN_RATIO, cut_steps=PART_CUT_STEPS):
    """Cover a triangle mesh with at most ``max_parts`` axis-aligned boxes (``(low, high)``).

    Top-down cuts: start from the mesh's AABB; every round tries each current part x each
    axis x ``cut_steps - 1`` equal cut positions, clips the part's triangles at the plane
    (so a triangle crossing the cut contributes its exact section to both sides and the two
    boxes meet without a gap), and keeps the one cut that removes the most empty volume.
    It stops at ``max_parts`` or when the best cut removes less than ``min_gain_ratio`` of
    the whole AABB.  Every surface point stays inside some box, so a closed model's solid
    volume is covered; what disappears is empty space between bars, legs and boards.

    Deterministic: candidates are scanned in a fixed order and a later candidate replaces
    the best only when it is strictly better.  The boxes are returned sorted by their low
    corner (y, x, z), so the same mesh always gives the same list in the same order.
    """
    polygons = []
    for a, b, c in _triangles(faces):
        polygon = [tuple(float(v) for v in vertices[i]) for i in (a, b, c)]
        if all(_finite_point(point) for point in polygon):
            polygons.append(polygon)
    if not polygons:
        return []
    whole_low, whole_high = _polygons_bounds(polygons)
    whole = _box_volume(whole_low, whole_high)
    groups = [polygons]
    bounds = [(whole_low, whole_high)]
    while len(groups) < max(1, int(max_parts)):
        best = None
        for index, group in enumerate(groups):
            low, high = bounds[index]
            volume = _box_volume(low, high)
            for axis in range(3):
                span = high[axis] - low[axis]
                if span <= _EPS:
                    continue
                for step in range(1, cut_steps):
                    cut = low[axis] + span * step / float(cut_steps)
                    below = []
                    above = []
                    for polygon in group:
                        part = _clip_polygon(polygon, axis, cut, True)
                        if len(part) >= 3:
                            below.append(part)
                        part = _clip_polygon(polygon, axis, cut, False)
                        if len(part) >= 3:
                            above.append(part)
                    if not below or not above:
                        continue
                    below_bounds = _polygons_bounds(below)
                    above_bounds = _polygons_bounds(above)
                    gain = volume - _box_volume(*below_bounds) - _box_volume(*above_bounds)
                    if best is None or gain > best[0]:
                        best = (gain, index, below, above, below_bounds, above_bounds)
        # A cut that removes nothing (a flat mesh has no volume to remove) is never taken.
        if best is None or best[0] <= 0.0 or best[0] < min_gain_ratio * whole:
            break
        _, index, below, above, below_bounds, above_bounds = best
        groups[index:index + 1] = [below, above]
        bounds[index:index + 1] = [below_bounds, above_bounds]
    boxes = [(list(low), list(high)) for low, high in bounds]
    boxes.sort(key=lambda box: (box[0][1], box[0][0], box[0][2], box[1][1], box[1][0],
                                box[1][2]))
    return boxes


def _floor4(value):
    return math.floor(value * 10000.0 - 1e-6) / 10000.0


def _ceil4(value):
    return math.ceil(value * 10000.0 + 1e-6) / 10000.0


def solid_parts(vertices, faces, max_parts=PART_MAX_COUNT):
    """``structures.solids[].parts`` for one model: ``[{"center", "size"}, ...]`` or ``[]``.

    The boxes of :func:`split_mesh_into_boxes`, grown to ``PART_MIN_SIZE_M`` on every axis
    and rounded OUTWARD to 0.1 mm (a rounded part never uncovers the surface it was cut
    from).  An empty list means "keep the one contact box": the model did not split.
    """
    boxes = split_mesh_into_boxes(vertices, faces, max_parts=max_parts)
    if len(boxes) < 2:
        return []
    parts = []
    for low, high in boxes:
        low = list(low)
        high = list(high)
        for k in range(3):
            if high[k] - low[k] < PART_MIN_SIZE_M:
                middle = (low[k] + high[k]) / 2.0
                low[k] = middle - PART_MIN_SIZE_M / 2.0
                high[k] = middle + PART_MIN_SIZE_M / 2.0
        low = [_floor4(value) for value in low]
        high = [_ceil4(value) for value in high]
        parts.append({"center": [_round((low[k] + high[k]) / 2.0) for k in range(3)],
                      "size": [_round(high[k] - low[k]) for k in range(3)]})
    return parts


def solid_parts_errors(parts, size=None):
    """Schema problems of one ``parts`` list (``size``: the solid's AABB, for containment)."""
    if not isinstance(parts, list):
        return ["parts must be an array of {center, size}"]
    errors = []
    if not 2 <= len(parts) <= PART_MAX_COUNT:
        errors.append("parts needs 2..%d boxes, got %d (a single box is the default: "
                      "leave parts out)" % (PART_MAX_COUNT, len(parts)))
    half = None
    if _as_vector3(size) is not None:
        half = [float(value) / 2.0 for value in size]
    for index, part in enumerate(parts):
        if not isinstance(part, dict):
            errors.append("parts[%d] must be an object" % index)
            continue
        for key in part:
            if key not in PART_KEYS:
                errors.append("parts[%d].%s is not a part field (allowed: %s)"
                              % (index, key, ", ".join(PART_KEYS)))
        center = _as_vector3(part.get("center"))
        extent = _as_vector3(part.get("size"))
        if center is None:
            errors.append("parts[%d].center must be 3 finite numbers" % index)
        if extent is None or any(value <= 0.0 for value in extent):
            errors.append("parts[%d].size must be 3 positive finite numbers" % index)
        if center is None or extent is None or half is None:
            continue
        for k in range(3):
            if (center[k] - extent[k] / 2.0 < -half[k] - 1e-3
                    or center[k] + extent[k] / 2.0 > half[k] + 1e-3):
                errors.append("parts[%d] reaches outside the solid's size box on axis %s"
                              % (index, "xyz"[k]))
                break
    return errors


# --------------------------------------------------------------------------- #
# unsigned distance from a point to a surface
# --------------------------------------------------------------------------- #
def _point_segment_distance(p, a, b):
    ab = _sub(b, a)
    denominator = _dot(ab, ab)
    if denominator <= _EPS:
        return _distance(p, a)
    t = _dot(_sub(p, a), ab) / denominator
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return _distance(p, _add(a, _mul(ab, t)))


def point_triangle_distance(p, a, b, c):
    """Unsigned distance from ``p`` to triangle ``abc`` (Ericson's algorithm)."""
    ab = _sub(b, a)
    ac = _sub(c, a)
    ap = _sub(p, a)
    d1 = _dot(ab, ap)
    d2 = _dot(ac, ap)
    if d1 <= 0.0 and d2 <= 0.0:
        return _distance(p, a)

    bp = _sub(p, b)
    d3 = _dot(ab, bp)
    d4 = _dot(ac, bp)
    if d3 >= 0.0 and d4 <= d3:
        return _distance(p, b)

    vc = d1 * d4 - d3 * d2
    if vc <= 0.0 and d1 >= 0.0 and d3 <= 0.0:
        v = d1 / (d1 - d3)
        return _distance(p, _add(a, _mul(ab, v)))

    cp = _sub(p, c)
    d5 = _dot(ab, cp)
    d6 = _dot(ac, cp)
    if d6 >= 0.0 and d5 <= d6:
        return _distance(p, c)

    vb = d5 * d2 - d1 * d6
    if vb <= 0.0 and d2 >= 0.0 and d6 <= 0.0:
        w = d2 / (d2 - d6)
        return _distance(p, _add(a, _mul(ac, w)))

    va = d3 * d6 - d5 * d4
    if va <= 0.0 and (d4 - d3) >= 0.0 and (d5 - d6) >= 0.0:
        w = (d4 - d3) / ((d4 - d3) + (d5 - d6))
        return _distance(p, _add(b, _mul(_sub(c, b), w)))

    denominator = va + vb + vc
    if denominator <= _EPS:
        return min(_point_segment_distance(p, a, b),
                   _point_segment_distance(p, b, c),
                   _point_segment_distance(p, c, a))
    v = vb / denominator
    w = vc / denominator
    return _distance(p, _add(a, _add(_mul(ab, v), _mul(ac, w))))


def distance_to_hull(p, hull_points, hull_faces):
    best = float("inf")
    for a, b, c in hull_faces:
        distance = point_triangle_distance(p, hull_points[a], hull_points[b],
                                           hull_points[c])
        if distance < best:
            best = distance
    return best


def hull_planes(hull_points, hull_faces):
    """Outward ``(normal, offset)`` for every hull face, computed once per hull."""
    planes = []
    for a, b, c in hull_faces:
        normal = _face_normal(hull_points[a], hull_points[b], hull_points[c])
        if normal is not None:
            planes.append((normal, _dot(normal, hull_points[a])))
    return planes


def near_hull_surface(point, planes, tolerance):
    """True when ``point`` is within ``tolerance`` of the hull surface.

    Plane distances are enough and much cheaper than point-triangle distances:
    for a convex hull every surface point lies on a face plane, so a point whose
    nearest plane is farther than the tolerance is also farther from the surface.
    The test is conservative when a perpendicular foot falls outside its face
    (it keeps the point), which is the safe direction for the outer-envelope
    selection.
    """
    maximum = -float("inf")
    minimum = float("inf")
    for normal, offset in planes:
        signed = _dot(normal, point) - offset
        if signed > maximum:
            maximum = signed
            if maximum > tolerance:
                return False
        absolute = -signed if signed < 0.0 else signed
        if absolute < minimum:
            minimum = absolute
    return minimum <= tolerance


# --------------------------------------------------------------------------- #
# mesh sampling and volume
# --------------------------------------------------------------------------- #
def _triangles(faces):
    triangles = []
    for face in faces or ():
        if not isinstance(face, (list, tuple)) or len(face) < 3:
            continue
        for index in range(1, len(face) - 1):
            triangles.append((int(face[0]), int(face[index]), int(face[index + 1])))
    return triangles


def surface_samples(vertices, faces=None):
    """Vertices plus (when faces are given) triangle centroids, deterministically capped."""
    samples = [tuple(float(c) for c in vertex) for vertex in vertices]
    for a, b, c in _triangles(faces):
        pa, pb, pc = samples[a], samples[b], samples[c]
        samples.append(((pa[0] + pb[0] + pc[0]) / 3.0,
                        (pa[1] + pb[1] + pc[1]) / 3.0,
                        (pa[2] + pb[2] + pc[2]) / 3.0))
    if len(samples) > _MAX_FIT_SAMPLES:
        stride = int(math.ceil(len(samples) / float(_MAX_FIT_SAMPLES)))
        samples = samples[::stride]
    return samples


def mesh_volume(vertices, faces=None):
    """Absolute signed volume of a closed triangle mesh, or ``None``."""
    triangles = _triangles(faces)
    if not triangles:
        return None
    total = 0.0
    for a, b, c in triangles:
        total += _dot(vertices[a], _cross(vertices[b], vertices[c]))
    volume = abs(total) / 6.0
    return volume if volume > _EPS else None


@dataclass
class Fit:
    """One fitted proxy plus the diagnostics the report needs."""

    kind: str
    center: tuple
    fit_error: float
    volume_ratio: float
    largest_extent: float
    accepted: bool = True
    reason: str = ""
    size: tuple | None = None
    radius: float | None = None
    half_height: float | None = None
    axis: str | None = None
    hull: list | None = None
    hull_faces: list | None = None
    notes: list = field(default_factory=list)
    outer_fraction: float = 1.0
    containment: float = 1.0
    max_error_m: float = 0.0
    mesh_volume_ratio: float | None = None

    def block(self, source="auto"):
        """The JSON block, in canonical field order, rounded for the placement file."""
        out = {"type": self.kind, "center": [_round(c) for c in self.center]}
        if self.kind == "box":
            out["size"] = [_round(c) for c in self.size]
        elif self.kind == "sphere":
            out["radius"] = _round(self.radius)
        elif self.kind == "capsule":
            out["radius"] = _round(self.radius)
            out["halfHeight"] = _round(self.half_height)
            out["axis"] = self.axis
        elif self.kind == "convex":
            out["hull"] = [_round(c) for point in self.hull for c in point]
        out["source"] = source
        out["fit_error"] = _round(self.fit_error)
        return {"collider": out}


def _round(value):
    value = round(float(value), 4)
    return 0.0 if value == 0.0 else value


# --------------------------------------------------------------------------- #
# the four fits
# --------------------------------------------------------------------------- #
def _solve4(matrix, rhs):
    """Gaussian elimination with partial pivoting; ``None`` when singular."""
    rows = [list(matrix[i]) + [rhs[i]] for i in range(4)]
    for column in range(4):
        pivot = max(range(column, 4), key=lambda r: abs(rows[r][column]))
        if abs(rows[pivot][column]) <= 1e-18:
            return None
        rows[column], rows[pivot] = rows[pivot], rows[column]
        scale = rows[column][column]
        for index in range(column, 5):
            rows[column][index] /= scale
        for row in range(4):
            if row == column:
                continue
            factor = rows[row][column]
            if factor == 0.0:
                continue
            for index in range(column, 5):
                rows[row][index] -= factor * rows[column][index]
    return [rows[i][4] for i in range(4)]


def _fit_sphere(samples):
    # Kasa algebraic least-squares sphere: exact for points on a sphere, and it
    # recovers the centre instead of taking the (biased) centroid.
    normal = [[0.0] * 4 for _ in range(4)]
    rhs = [0.0] * 4
    for point in samples:
        row = (2.0 * point[0], 2.0 * point[1], 2.0 * point[2], 1.0)
        value = point[0] * point[0] + point[1] * point[1] + point[2] * point[2]
        for i in range(4):
            rhs[i] += row[i] * value
            for j in range(4):
                normal[i][j] += row[i] * row[j]
    solution = _solve4(normal, rhs)
    if solution is not None:
        center = tuple(solution[0:3])
        squared_radius = solution[3] + _dot(center, center)
        radius = math.sqrt(squared_radius) if squared_radius > 0.0 else 0.0
    else:
        center = _mean(samples)
        squared = [_dot(_sub(p, center), _sub(p, center)) for p in samples]
        radius = math.sqrt(sum(squared) / len(squared))
    if radius <= _EPS or not math.isfinite(radius):
        return None
    deviation = 0.0
    for point in samples:
        gap = abs(_distance(point, center) - radius)
        if gap > deviation:
            deviation = gap
    error = deviation / (2.0 * radius)
    return Fit(kind="sphere", center=center, radius=radius, fit_error=error,
               volume_ratio=0.0, largest_extent=2.0 * radius, max_error_m=deviation,
               accepted=(deviation <= max(SPHERE_MAX_DEVIATION_RATIO * radius,
                                          ABSOLUTE_ERROR_FLOOR_M)),
               notes=["max deviation %.4f m = %.1f %% of radius r=%.4f m"
                      % (deviation, 100.0 * deviation / radius, radius)])


def _capsule_error_samples(samples, axis_index, half_height):
    """For a fixed segment half-length, the best radius is the midpoint of the
    per-point candidate distances; returns ``(radius, max_error)``."""
    lowest = float("inf")
    highest = 0.0
    for point in samples:
        t = abs(point[axis_index])
        rho = math.sqrt(sum(point[i] * point[i] for i in range(3) if i != axis_index))
        if t <= half_height:
            candidate = rho
        else:
            candidate = math.sqrt((t - half_height) ** 2 + rho * rho)
        if candidate < lowest:
            lowest = candidate
        if candidate > highest:
            highest = candidate
    radius = 0.5 * (lowest + highest)
    return radius, 0.5 * (highest - lowest)


def _fit_capsule(samples):
    best = None
    for axis_index, axis_name in enumerate(CAPSULE_AXES):
        t_values = [p[axis_index] for p in samples]
        t_min, t_max = min(t_values), max(t_values)
        half_span = 0.5 * (t_max - t_min)
        if half_span <= _EPS:
            continue
        mid = 0.5 * (t_min + t_max)
        # The axis line goes through the centroid of the other two axes, not through
        # the origin: a model whose pivot is not the mesh centre must not bias r/h.
        other_mid = [sum(p[i] for p in samples) / len(samples)
                     for i in range(3) if i != axis_index]
        centered = []
        for point in samples:
            values = list(point)
            values[axis_index] -= mid
            other = 0
            for i in range(3):
                if i == axis_index:
                    continue
                values[i] -= other_mid[other]
                other += 1
            centered.append(tuple(values))

        # 1-D Chebyshev fit over the segment half-length, coarse then refined.
        low, high = 0.0, half_span
        radius = error = 0.0
        for _ in range(5):
            steps = 24 if _ == 0 else 8
            best_h = low
            best_error = float("inf")
            best_radius = 0.0
            for step in range(steps + 1):
                h = low + (high - low) * step / steps
                candidate_radius, candidate_error = _capsule_error_samples(centered,
                                                                           axis_index, h)
                if candidate_error < best_error:
                    best_error, best_h, best_radius = candidate_error, h, candidate_radius
            radius, error = best_radius, best_error
            span = (high - low) / steps
            low = max(0.0, best_h - span)
            high = min(half_span, best_h + span)

        center = [0.0, 0.0, 0.0]
        other = 0
        for i in range(3):
            if i == axis_index:
                continue
            center[i] = other_mid[other]
            other += 1
        center[axis_index] = mid
        largest = 2.0 * (radius + best_h)
        candidate = Fit(kind="capsule", center=tuple(center), radius=radius,
                        half_height=best_h, axis=axis_name, fit_error=error / largest,
                        volume_ratio=0.0, largest_extent=largest, max_error_m=error,
                        accepted=(error <= max(CAPSULE_MAX_DEVIATION_RATIO * radius,
                                               ABSOLUTE_ERROR_FLOOR_M))
                        if radius > _EPS else False,
                        notes=["max deviation %.4f m = %.1f %% of radius r=%.4f m, "
                               "halfHeight=%.4f m"
                               % (error, 100.0 * error / radius if radius > 0 else 0.0,
                                  radius, best_h)])
        if best is None or candidate.fit_error < best.fit_error:
            best = candidate
    return best


def _distance_to_aabb(point, low, high):
    outside = 0.0
    inside = float("inf")
    for i in range(3):
        if point[i] < low[i]:
            outside += (low[i] - point[i]) ** 2
        elif point[i] > high[i]:
            outside += (point[i] - high[i]) ** 2
        else:
            inside = min(inside, point[i] - low[i], high[i] - point[i])
    if outside > 0.0:
        return math.sqrt(outside)
    return inside


def _fit_box(samples):
    lo, hi = _aabb(samples)
    center = tuple(0.5 * (lo[i] + hi[i]) for i in range(3))
    size = tuple(hi[i] - lo[i] for i in range(3))
    largest = max(size)
    if largest <= _EPS:
        return None
    error = max(_distance_to_aabb(point, lo, hi) for point in samples)
    return Fit(kind="box", center=center, size=size, fit_error=error / largest,
               volume_ratio=0.0, largest_extent=largest, max_error_m=error,
               accepted=(error <= max(BOX_MAX_ERROR_RATIO * largest,
                                      ABSOLUTE_ERROR_FLOOR_M)),
               notes=["max surface gap %.4f m = %.1f %% of the largest edge"
                      % (error, 100.0 * error / largest)])


def _fit_oriented_box(samples):
    """Diagnostic only: a PCA-oriented box the schema cannot store (no rotation field).

    Reported in the CSV so "the AABB failed only because the prop is rotated in its
    own mesh space" is visible instead of looking like a bad fit.
    """
    try:
        center = _mean(samples)
        centered = [_sub(p, center) for p in samples]
        covariance = [[0.0] * 3 for _ in range(3)]
        for p in centered:
            for i in range(3):
                for j in range(3):
                    covariance[i][j] += p[i] * p[j]
        # power iteration for the dominant axis, then deflate twice (deterministic).
        axes = []
        matrix = [row[:] for row in covariance]
        for _ in range(3):
            vector = (1.0, 0.61803398875, 0.41421356237)
            for _ in range(64):
                vector = tuple(sum(matrix[i][j] * vector[j] for j in range(3))
                               for i in range(3))
                length = _length(vector)
                if length <= _EPS:
                    return None
                vector = _mul(vector, 1.0 / length)
            axes.append(vector)
            # deflate
            for i in range(3):
                for j in range(3):
                    matrix[i][j] -= vector[i] * vector[j] * sum(
                        vector[k] * matrix[k][l] * vector[l]
                        for k in range(3) for l in range(3))
        local = []
        for p in samples:
            local.append(tuple(_dot(p, axis) for axis in axes))
        lo, hi = _aabb(local)
        size = [hi[i] - lo[i] for i in range(3)]
        largest = max(size)
        if largest <= _EPS:
            return None
        local_center = [0.5 * (lo[i] + hi[i]) for i in range(3)]
        error = max(_distance_to_aabb(p, lo, hi) for p in local)
        world_center = tuple(sum(axes[j][i] * local_center[j] for j in range(3))
                             for i in range(3))
        world_center = _add(center, world_center)
        fit = Fit(kind="box", center=world_center, size=tuple(size),
                  fit_error=error / largest, volume_ratio=0.0, largest_extent=largest,
                  max_error_m=error,
                  accepted=(error <= max(BOX_MAX_ERROR_RATIO * largest,
                                         ABSOLUTE_ERROR_FLOOR_M)),
                  notes=["oriented (PCA) diagnostic only; the schema has no box rotation"])
        return fit
    except (ValueError, ZeroDivisionError):
        return None


def _farthest_point_subset(points, limit):
    """Deterministic farthest-point sampling, starting at the point farthest from
    the centroid.  Returns at most ``limit`` points."""
    if len(points) <= limit:
        return list(points)
    center = _mean(points)
    first = max(range(len(points)), key=lambda i: (_dot(_sub(points[i], center),
                                                       _sub(points[i], center)), -i))
    selected = [first]
    nearest = [_dot(_sub(p, points[first]), _sub(p, points[first])) for p in points]
    while len(selected) < limit:
        best = -1.0
        best_index = -1
        for i, distance in enumerate(nearest):
            if i in selected:
                continue
            if distance > best:
                best, best_index = distance, i
        if best_index < 0 or best <= _EPS:
            break
        selected.append(best_index)
        pivot = points[best_index]
        for i, point in enumerate(points):
            d = _dot(_sub(point, pivot), _sub(point, pivot))
            if d < nearest[i]:
                nearest[i] = d
    return [points[i] for i in selected]


def _fit_hull(samples, all_points):
    hull = convex_hull(all_points)
    if hull is None:
        return None
    hull_points, hull_faces = hull
    if len(hull_points) <= HULL_MAX_POINTS:
        selected = hull_points
    else:
        selected = _farthest_point_subset(hull_points, HULL_MAX_POINTS)
    selected_hull = convex_hull(selected)
    if selected_hull is None:
        return None
    proxy_points, proxy_faces = selected_hull
    if len(proxy_points) < HULL_MIN_POINTS:
        return None
    largest = _extent(proxy_points)
    if largest <= _EPS:
        return None
    error = max(distance_to_hull(point, proxy_points, proxy_faces)
                for point in samples)
    volume = hull_volume(proxy_points, proxy_faces)
    low, high = _aabb(proxy_points)
    center = tuple(0.5 * (low[i] + high[i]) for i in range(3))
    rebased = [tuple(point[i] - center[i] for i in range(3)) for point in proxy_points]
    return Fit(kind="convex", center=center,
               hull=[list(point) for point in rebased],
               fit_error=error / largest, volume_ratio=0.0, largest_extent=largest,
               max_error_m=error,
               accepted=(error <= max(HULL_MAX_ERROR_RATIO * largest,
                                      ABSOLUTE_ERROR_FLOOR_M)),
               notes=["%d hull points selected from %d (budget %d); max surface gap "
                      "%.4f m = %.1f %% of the largest extent"
                      % (len(proxy_points), len(hull_points), HULL_MAX_POINTS, error,
                         100.0 * error / largest)],
               hull_faces=proxy_faces)


def _proxy_volume(fit):
    if fit.kind == "sphere":
        return 4.0 / 3.0 * math.pi * fit.radius ** 3
    if fit.kind == "capsule":
        return (math.pi * fit.radius ** 2 * (2.0 * fit.half_height)
                + 4.0 / 3.0 * math.pi * fit.radius ** 3)
    if fit.kind == "box":
        return fit.size[0] * fit.size[1] * fit.size[2]
    if fit.kind == "convex":
        return hull_volume([tuple(p) for p in fit.hull], fit.hull_faces)
    raise ValueError(fit.kind)


def _proxy_is_too_big(fit, reference_volume, ratio=MAX_ROUND_PROXY_VOLUME_RATIO):
    """Volume guard: the proxy must not dwarf the envelope it is standing in for."""
    if reference_volume is None or reference_volume <= _EPS:
        return False
    return _proxy_volume(fit) > ratio * reference_volume


def _containment(fit, samples):
    """Fraction of surface samples inside the proxy (a "does it clip?" diagnostic)."""
    inside = 0
    for point in samples:
        if fit.kind == "sphere":
            ok = _distance(point, fit.center) <= fit.radius + 1e-9
        elif fit.kind == "capsule":
            axis = CAPSULE_AXES.index(fit.axis)
            local = list(_sub(point, fit.center))
            t = local[axis]
            t = -fit.half_height if t < -fit.half_height else (
                fit.half_height if t > fit.half_height else t)
            local[axis] = t
            ok = _length(tuple(local)) <= fit.radius + 1e-9
        elif fit.kind == "box":
            half = [0.5 * c for c in fit.size]
            ok = all(abs(point[i] - fit.center[i]) <= half[i] + 1e-9 for i in range(3))
        elif fit.kind == "convex":
            ok = True
            local = _sub(point, fit.center)
            for a, b, c in fit.hull_faces or ():
                normal = _face_normal(tuple(fit.hull[a]), tuple(fit.hull[b]),
                                      tuple(fit.hull[c]))
                if normal is None:
                    continue
                if _signed_plane(local, tuple(fit.hull[a]), normal) > 1e-6:
                    ok = False
                    break
        else:
            ok = True
        if ok:
            inside += 1
    return inside / float(len(samples)) if samples else 1.0


def _clamp(value, low, high):
    return low if value < low else (high if value > high else value)


def _clamp_sphere(fit, samples, low, high):
    """Pull a fitted sphere inside the visual AABB and re-measure it."""
    half = [0.5 * (high[i] - low[i]) for i in range(3)]
    radius = min(fit.radius, min(half))
    center = [_clamp(fit.center[i], low[i] + radius, high[i] - radius)
              for i in range(3)]
    deviation = max(abs(_distance(point, center) - radius) for point in samples)
    fit.center = tuple(center)
    fit.radius = radius
    fit.largest_extent = 2.0 * radius
    fit.max_error_m = deviation
    fit.fit_error = deviation / (2.0 * radius) if radius > _EPS else 1.0
    fit.accepted = deviation <= max(SPHERE_MAX_DEVIATION_RATIO * radius,
                                    ABSOLUTE_ERROR_FLOOR_M)
    if abs(radius - fit.radius) > 1e-12 or any(
            abs(center[i] - fit.center[i]) > 1e-12 for i in range(3)):
        fit.notes.append("clamped inside the visual AABB")
    return fit


def _capsule_point_error(point, center, axis_index, radius, half_height):
    local = _sub(point, center)
    t = local[axis_index]
    rho = math.sqrt(sum(local[i] * local[i] for i in range(3)
                        if i != axis_index))
    if abs(t) <= half_height:
        return abs(rho - radius)
    return abs(math.sqrt((abs(t) - half_height) ** 2 + rho * rho) - radius)


def _clamp_capsule(fit, samples, low, high):
    """Pull a fitted capsule inside the visual AABB and re-measure it."""
    axis_index = CAPSULE_AXES.index(fit.axis)
    other = [i for i in range(3) if i != axis_index]
    half = [0.5 * (high[i] - low[i]) for i in range(3)]
    radius = min(fit.radius, min(half))
    half_height = min(fit.half_height, max(0.0, half[axis_index] - radius))
    center = list(fit.center)
    for i in other:
        center[i] = _clamp(center[i], low[i] + radius, high[i] - radius)
    span = half[axis_index] - half_height - radius
    if span < 0.0:
        half_height = max(0.0, half[axis_index] - radius)
        span = 0.0
    # With the radius clamped, re-optimise the half-length: the unconstrained fit
    # chose (r, h) together, so keeping its h after shrinking r would leave the
    # capsule short at the ends (watermelon: 28 cm vs the 30 cm fruit).
    center[axis_index] = 0.5 * (low[axis_index] + high[axis_index])
    max_half_height = max(0.0, half[axis_index] - radius)
    half_height = max_half_height
    error = max(_capsule_point_error(point, center, axis_index, radius,
                                     half_height) for point in samples)
    for step in range(24):
        candidate_h = max_half_height * step / 24.0
        candidate_error = max(_capsule_point_error(point, center, axis_index,
                                                   radius, candidate_h)
                              for point in samples)
        if candidate_error < error:
            error, half_height = candidate_error, candidate_h
    fit.center = tuple(center)
    fit.radius = radius
    fit.half_height = half_height
    fit.largest_extent = 2.0 * (radius + half_height)
    fit.max_error_m = error
    fit.fit_error = error / fit.largest_extent if fit.largest_extent > _EPS else 1.0
    fit.accepted = (radius > _EPS and
                    error <= max(CAPSULE_MAX_DEVIATION_RATIO * radius,
                                 ABSOLUTE_ERROR_FLOOR_M))
    fit.notes.append("clamped inside the visual AABB (r=%.4f m, h=%.4f m)"
                     % (radius, half_height))
    return fit


# --------------------------------------------------------------------------- #
# public entry point
# --------------------------------------------------------------------------- #
def fit_proxy(vertices, faces=None, *, apply_outer_envelope=True, bounds=None):
    """Fit the cheapest proxy to ``vertices``/``faces``; returns a :class:`Fit`.

    The order is sphere, capsule, box, convex hull.  The first type whose own
    threshold is met wins.  When no type passes, the box is returned with
    ``accepted=False`` and ``reason`` set - the honest "nothing representable"
    answer, because the schema has no "none" type and falling back to a box is
    what the game does today.
    """
    points = []
    for vertex in vertices:
        if not _finite_point(vertex):
            raise ValueError("vertex %r is not a finite 3-vector" % (vertex,))
        points.append(tuple(float(c) for c in vertex))
    if len(points) < 4:
        raise ValueError("a proxy needs at least 4 points, got %d" % len(points))

    samples = surface_samples(points, faces)
    reference_hull = convex_hull(points)
    hull_volume_reference = None
    outer_fraction = 1.0
    if reference_hull is not None:
        hull_points, hull_faces = reference_hull
        hull_volume_reference = hull_volume(hull_points, hull_faces)
        if apply_outer_envelope and hull_faces:
            tolerance = max(OUTER_ENVELOPE_TOLERANCE_MIN_M,
                            OUTER_ENVELOPE_TOLERANCE_RATIO * _extent(points))
            planes = hull_planes(hull_points, hull_faces)
            outer = [point for point in samples
                     if near_hull_surface(point, planes, tolerance)]
            if len(outer) >= 4:
                outer_fraction = len(outer) / float(len(samples))
                samples = outer

    if hull_volume_reference is None or hull_volume_reference <= _EPS:
        hull_volume_reference = None

    sphere = _fit_sphere(samples)
    if sphere is not None and bounds is not None:
        sphere = _clamp_sphere(sphere, samples, bounds[0], bounds[1])
    if sphere is not None and sphere.accepted:
        if _proxy_is_too_big(sphere, hull_volume_reference):
            sphere.accepted = False
            sphere.notes.append("rejected: sphere volume is more than %.1fx the "
                                "envelope (a flat shape fits a huge cap)"
                                % MAX_ROUND_PROXY_VOLUME_RATIO)
        else:
            return _finish(sphere, points, faces, hull_volume_reference, outer_fraction, samples,
                           "sphere: max deviation within %.0f %% of radius"
                           % (SPHERE_MAX_DEVIATION_RATIO * 100))

    capsule = _fit_capsule(samples)
    if capsule is not None and bounds is not None:
        capsule = _clamp_capsule(capsule, samples, bounds[0], bounds[1])
    if capsule is not None and capsule.accepted:
        if _proxy_is_too_big(capsule, hull_volume_reference):
            capsule.accepted = False
            capsule.notes.append("rejected: capsule volume is more than %.1fx the "
                                 "envelope" % MAX_ROUND_PROXY_VOLUME_RATIO)
        else:
            return _finish(capsule, points, faces, hull_volume_reference, outer_fraction, samples,
                           "capsule: max deviation within %.0f %% of radius"
                           % (CAPSULE_MAX_DEVIATION_RATIO * 100))

    box = _fit_box(samples)
    if box is not None and box.accepted:
        if _proxy_is_too_big(box, hull_volume_reference, MAX_BOX_VOLUME_RATIO):
            box.accepted = False
            box.notes.append("rejected: box volume is more than %.2fx the envelope "
                             "(corner overhang)" % MAX_BOX_VOLUME_RATIO)
        else:
            return _finish(box, points, faces, hull_volume_reference, outer_fraction,
                           samples, "box: max surface gap within %.0f %% of the "
                           "largest edge" % (BOX_MAX_ERROR_RATIO * 100))

    hull_fit = _fit_hull(samples, points)
    if hull_fit is not None and hull_fit.accepted:
        return _finish(hull_fit, points, faces, hull_volume_reference, outer_fraction, samples,
                       "convex: %d-point hull within %.0f %% of the largest extent"
                       % (len(hull_fit.hull), HULL_MAX_ERROR_RATIO * 100))

    if box is None and hull_fit is None:
        raise ValueError("the point cloud is degenerate (no positive box extent)")

    reasons = []
    if sphere is not None:
        reasons.append("sphere max gap %.1f %% of radius > %.0f %%"
                       % (100.0 * sphere.max_error_m / max(sphere.radius, _EPS),
                          SPHERE_MAX_DEVIATION_RATIO * 100))
    if capsule is not None:
        reasons.append("capsule max gap %.1f %% of radius > %.0f %%"
                       % (100.0 * capsule.max_error_m / max(capsule.radius, _EPS),
                          CAPSULE_MAX_DEVIATION_RATIO * 100))
    if hull_fit is not None:
        reasons.append("convex %.1f %% of the largest extent > %.0f %%"
                       % (100.0 * hull_fit.fit_error, HULL_MAX_ERROR_RATIO * 100))
    else:
        reasons.append("convex hull is degenerate or cannot be built from <=%d points"
                       % HULL_MAX_POINTS)
    oriented = _fit_oriented_box(samples)
    if oriented is not None and oriented.accepted:
        reasons.append("an oriented box would pass (%.1f %%) but the schema has no "
                       "box rotation" % (100.0 * oriented.fit_error))

    # No proxy met its target: return the one with the smallest actual surface gap,
    # not blindly the box.  A 24-point hull at 10.1 % is visibly better than the box
    # at 20.4 % on the same wool tuft; the box stays the fallback only when the hull
    # is degenerate or worse.
    best = box
    label = "box"
    if hull_fit is not None and (box is None
                                 or hull_fit.max_error_m < box.max_error_m):
        best = hull_fit
        label = "convex"
    best.accepted = False
    best.reason = ("no proxy meets its tolerance; best available %s (%.1f mm gap; %s)"
                   % (label, 1000.0 * best.max_error_m, "; ".join(reasons)))
    best.notes = list(best.notes) + reasons
    return _finish(best, points, faces, hull_volume_reference, outer_fraction, samples,
                   best.reason)


def _finish(fit, points, faces, hull_volume_reference, outer_fraction, samples, reason):
    proxy_volume = _proxy_volume(fit)
    if hull_volume_reference and hull_volume_reference > _EPS:
        fit.volume_ratio = proxy_volume / hull_volume_reference
    else:
        fit.volume_ratio = 1.0
    mesh = mesh_volume(points, faces)
    fit.mesh_volume_ratio = (proxy_volume / mesh) if mesh else None
    fit.outer_fraction = outer_fraction
    fit.containment = _containment(fit, samples)
    if not fit.reason:
        fit.reason = reason
    return fit


# --------------------------------------------------------------------------- #
# schema validation and canonical form (used by placement_core)
# --------------------------------------------------------------------------- #
def _as_vector3(value):
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        return None
    if not all(isinstance(c, (int, float)) and not isinstance(c, bool) for c in value):
        return None
    numbers = [float(c) for c in value]
    if any(not math.isfinite(c) for c in numbers):
        return None
    return numbers


def hull_points(block):
    """The hull as a list of ``(x, y, z)`` tuples, or ``None`` when malformed.

    The on-disk encoding is **flat** (``[x0,y0,z0, x1,y1,z1, ...]``) because
    ``JsonUtility`` cannot deserialize an array of arrays.  The nested
    ``[[x,y,z], ...]`` form is accepted on input (the report/fitter used it first)
    and canonicalised to flat, so old hand-edited files keep loading.
    """
    value = block.get("hull") if isinstance(block, dict) else None
    if not isinstance(value, list) or not value:
        return None
    if all(isinstance(item, (list, tuple)) for item in value):
        points = []
        for item in value:
            if len(item) != 3:
                return None
            points.append(tuple(float(c) for c in item))
        return points
    if all(isinstance(item, (int, float)) and not isinstance(item, bool)
           for item in value):
        if len(value) % 3 != 0:
            return None
        return [tuple(float(value[i + j]) for j in range(3))
                for i in range(0, len(value), 3)]
    return None


def collider_schema_errors(block):
    """Every schema problem in one ``collider`` block, as human-readable strings."""
    errors = []
    if not isinstance(block, dict):
        return ["collider must be an object"]

    kind = block.get("type")
    if kind not in COLLIDER_TYPES:
        errors.append("collider.type must be one of %s (got %r)"
                      % ("/".join(COLLIDER_TYPES), kind))
        return errors

    allowed = ("type",) + _TYPE_FIELDS[kind]
    for key in block:
        if key not in allowed:
            errors.append("collider.%s is not a %s field (allowed: %s)"
                          % (key, kind, ", ".join(allowed)))

    center = _as_vector3(block.get("center"))
    if center is None:
        errors.append("collider.center must be 3 finite numbers")
    for key in ("source", "fit_error"):
        if key in block:
            if key == "source" and block["source"] not in COLLIDER_SOURCES:
                errors.append("collider.source must be auto/manual (got %r)"
                              % (block["source"],))
            if key == "fit_error":
                value = block["fit_error"]
                if (not isinstance(value, (int, float)) or isinstance(value, bool)
                        or not math.isfinite(float(value)) or value < 0.0 or value > 1.0):
                    errors.append("collider.fit_error must be a number in [0, 1] (got %r)"
                                  % (value,))

    if kind == "box":
        size = _as_vector3(block.get("size"))
        if size is None:
            errors.append("collider.size must be 3 finite numbers (box)")
        elif any(c <= 0.0 for c in size):
            errors.append("collider.size must be positive on every axis (got %r)"
                          % (size,))
    elif kind == "sphere":
        radius = block.get("radius")
        if (not isinstance(radius, (int, float)) or isinstance(radius, bool)
                or not math.isfinite(float(radius)) or radius <= 0.0):
            errors.append("collider.radius must be a positive finite number (sphere)")
    elif kind == "capsule":
        radius = block.get("radius")
        if (not isinstance(radius, (int, float)) or isinstance(radius, bool)
                or not math.isfinite(float(radius)) or radius <= 0.0):
            errors.append("collider.radius must be a positive finite number (capsule)")
        height = block.get("halfHeight")
        if (not isinstance(height, (int, float)) or isinstance(height, bool)
                or not math.isfinite(float(height)) or height < 0.0):
            errors.append("collider.halfHeight must be a finite number >= 0 (capsule)")
        if block.get("axis") not in CAPSULE_AXES:
            errors.append("collider.axis must be x/y/z (capsule)")
    elif kind == "convex":
        points = hull_points(block)
        if points is None:
            errors.append("collider.hull must be a flat list of 3N finite numbers "
                          "(or an array of [x,y,z] points)")
        elif not (HULL_MIN_POINTS <= len(points) <= HULL_MAX_POINTS):
            errors.append("collider.hull needs %d..%d points, got %d"
                          % (HULL_MIN_POINTS, HULL_MAX_POINTS, len(points)))
        elif any(not all(math.isfinite(c) for c in point) for point in points):
            errors.append("collider.hull has a non-finite coordinate")
        else:
            errors.extend(_convexity_errors([list(point) for point in points]))
    return errors


def _convexity_errors(points):
    hull = convex_hull(points)
    if hull is None:
        return ["collider.hull is coplanar/degenerate (a convex proxy needs a volume)"]
    hull_points, hull_faces = hull
    if hull_volume(hull_points, hull_faces) <= _EPS:
        return ["collider.hull has zero volume"]
    # Hull numbers are stored to 0.1 mm, so a 0.1 mm absolute floor is rounding
    # noise rather than an "interior point"; 0.1 % of the extent is the same idea
    # on a 1 m prop.  A genuine interior point is orders of magnitude deeper.
    tolerance = max(1e-4, CONVEXITY_TOLERANCE_RATIO * _extent(points))
    errors = []
    for index, point in enumerate(points):
        gap = distance_to_hull(point, hull_points, hull_faces)
        if gap > tolerance:
            errors.append("collider.hull[%d] is %.6f m inside the hull of the other "
                          "points (tolerance %.6f m): not a convex vertex"
                          % (index, gap, tolerance))
    return errors


def collider_bounds(block):
    """World-local AABB of a collider block, or ``None`` when it cannot be computed."""
    kind = block.get("type")
    if kind == "box":
        center = _as_vector3(block.get("center"))
        size = _as_vector3(block.get("size"))
        if center is None or size is None:
            return None
        half = [0.5 * c for c in size]
        return ([center[i] - half[i] for i in range(3)],
                [center[i] + half[i] for i in range(3)])
    if kind == "sphere":
        center = _as_vector3(block.get("center"))
        radius = block.get("radius")
        if center is None or not isinstance(radius, (int, float)):
            return None
        return ([center[i] - radius for i in range(3)],
                [center[i] + radius for i in range(3)])
    if kind == "capsule":
        center = _as_vector3(block.get("center"))
        radius = block.get("radius")
        height = block.get("halfHeight")
        axis = block.get("axis")
        if (center is None or not isinstance(radius, (int, float))
                or not isinstance(height, (int, float)) or axis not in CAPSULE_AXES):
            return None
        half = [radius, radius, radius]
        half[CAPSULE_AXES.index(axis)] += height
        return ([center[i] - half[i] for i in range(3)],
                [center[i] + half[i] for i in range(3)])
    if kind == "convex":
        center = _as_vector3(block.get("center"))
        points = hull_points(block)
        if points is None or center is None:
            return None
        low, high = _aabb(points)
        return ([low[i] + center[i] for i in range(3)],
                [high[i] + center[i] for i in range(3)])
    return None


def canonical_collider(block):
    """The block in canonical field order, numbers rounded to 4 decimals."""
    out = {}
    for key in COLLIDER_KEYS:
        if key not in block:
            continue
        value = block[key]
        if key in ("center", "size"):
            value = [_round(c) for c in value]
        elif key in ("radius", "halfHeight", "fit_error"):
            value = _round(value)
        elif key == "hull":
            points = hull_points(block)
            if points is None:
                value = []
            else:
                value = [_round(c) for point in points for c in point]
        out[key] = value
    return out


# --------------------------------------------------------------------------- #
# sidecar loading + CLI (headless batch over the Blender exports)
# --------------------------------------------------------------------------- #
def load_sidecar(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(data, dict) or "vertices" not in data:
        raise ValueError("%s is not a collider-proxy point sidecar" % path)
    return data


def fit_sidecar(data):
    bounds = data.get("visual_bounds")
    if bounds is not None:
        bounds = (tuple(bounds[0]), tuple(bounds[1]))
    return fit_proxy(data["vertices"], data.get("faces"), bounds=bounds)


def _summary_row(name, fit, sidecar):
    mesh_volume_value = mesh_volume(
        [tuple(float(c) for c in v) for v in sidecar["vertices"]],
        sidecar.get("faces"))
    return {
        "def": name,
        "type": fit.kind,
        "accepted": 1 if fit.accepted else 0,
        "fit_error": round(fit.fit_error, 6),
        "max_error_m": round(fit.max_error_m, 6),
        "containment": round(fit.containment, 4),
        "volume_ratio": round(fit.volume_ratio, 6),
        "mesh_volume_ratio": (round(fit.mesh_volume_ratio, 6)
                              if fit.mesh_volume_ratio is not None else ""),
        "largest_extent": round(fit.largest_extent, 6),
        "center": [round(c, 6) for c in fit.center],
        "size": [round(c, 6) for c in fit.size] if fit.size else "",
        "radius": round(fit.radius, 6) if fit.radius is not None else "",
        "halfHeight": (round(fit.half_height, 6) if fit.half_height is not None else ""),
        "axis": fit.axis or "",
        "hull_points": len(fit.hull) if fit.hull else "",
        "hull": (";".join("%g %g %g" % tuple(point) for point in fit.hull)
                 if fit.hull else ""),
        "outer_fraction": round(fit.outer_fraction, 4),
        "mesh_volume_m3": round(mesh_volume_value, 8) if mesh_volume_value else "",
        "reason": fit.reason,
        "notes": " | ".join(fit.notes),
    }


def _write_csv(rows, path):
    columns = ["def", "type", "accepted", "fit_error", "max_error_m", "containment",
               "volume_ratio", "mesh_volume_ratio", "largest_extent", "hull_points",
               "hull", "outer_fraction", "mesh_volume_m3", "center", "size", "radius",
               "halfHeight", "axis", "reason", "notes"]
    lines = [",".join(columns)]
    for row in rows:
        cells = []
        for column in columns:
            value = row.get(column, "")
            if isinstance(value, list):
                value = " ".join("%g" % c for c in value)
            text = str(value).replace('"', "'")
            if any(ch in text for ch in ",\"\n"):
                text = '"' + text.replace('"', '""') + '"'
            cells.append(text)
        lines.append(",".join(cells))
    Path(path).write_text("\n".join(lines) + "\n", encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Fit collider proxies from point sidecars.")
    parser.add_argument("--points-dir", required=True,
                        help="directory of <def>.json point sidecars")
    parser.add_argument("--csv", help="write a per-prop CSV here")
    parser.add_argument("--json", help="write the full result JSON here")
    parser.add_argument("--only", help="comma-separated def names")
    args = parser.parse_args(argv)

    points_dir = Path(args.points_dir)
    wanted = set(args.only.split(",")) if args.only else None
    rows = []
    results = {}
    for path in sorted(points_dir.glob("*.json")):
        name = path.stem
        if wanted is not None and name not in wanted:
            continue
        sidecar = load_sidecar(path)
        fit = fit_sidecar(sidecar)
        block = fit.block("auto")["collider"]
        schema_errors = collider_schema_errors(block)
        if schema_errors:
            print("INVALID %s: %s" % (name, "; ".join(schema_errors)))
        rows.append(_summary_row(name, fit, sidecar))
        results[name] = {
            "block": fit.block("auto")["collider"],
            "accepted": fit.accepted,
            "reason": fit.reason,
            "notes": fit.notes,
            "volume_ratio": fit.volume_ratio,
            "mesh_volume_ratio": fit.mesh_volume_ratio,
            "outer_fraction": fit.outer_fraction,
        }
    if args.csv:
        _write_csv(rows, args.csv)
    if args.json:
        Path(args.json).write_text(
            json.dumps(results, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    counts = {}
    for row in rows:
        counts[row["type"]] = counts.get(row["type"], 0) + 1
    print("props=%d %s" % (len(rows), " ".join("%s=%d" % (k, counts[k])
                                               for k in sorted(counts))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
