"""Build the reference level's v2 document from the stage extraction.

This is the reference-import adapter of ``LEVEL_FORMAT_V2`` §11.5: it reads the extractor's
already-exported JSON/GLB (never the game binaries, never anything inside the repository)
and writes one ``katamari.placement/2`` document plus its ``.katerrain`` sidecar into the
git-ignored ``Docs/Design/Levels/_ref_<name>/`` directory.

Very little maths lives here on purpose: the frame mapping, the axis-angle quaternions, the
instance ↔ itemId resolution and the collision-triangle expansion are ``ref_stage``'s
(the one implementation the web viewer already uses), and the growth/gate numbers are
``placement_core``'s.  What this module adds is the *document*: layers, ordering, the derived
rules below, and the canonical bytes.

The derived rules, stated once (they are the file's design decisions):

* **terrain** is the extractor's collision mesh, not the visual ``stage.glb``: 7,154 vertices
  / 3,456 triangles in the PS2 collision lists, put into world metres (``-0.01 * ps2``) and
  flipped once (swap b/c) so the floors' face normals point up (1,704 up / 319 down after the
  flip; the mirror image before).  One triangle has zero area and is dropped
  (``[terrain_mesh]`` forbids degenerates), leaving 3,455.
* **chunks** are consecutive runs of 512 triangles, and each chunk is also one broad-phase
  AABB: the JSON can then say "this box owns exactly these triangles" with no holes.
* **structures** are the props that are *never absorbable at this level's design ceiling*
  (gate > 1.20 m, the original's ``MAX_DIAMETER``) **and** whose AABB the extractor's
  collision space covers by at least half.  84 props are never absorbable; 35 of them sit in
  the covered core and get an analytic box; the other 49 (buildings and trees outside the
  collision mesh) are background scenery — the activity wall keeps the ball away from them,
  so they get no solid.  The collision AABBs are broad-phase volumes of the stage mesh, not
  per-object proxies, so they are used as *evidence of solid space*, never as solids
  themselves.
* **activityArea** is authored: the closed polygon is the union footprint of the collision
  AABBs (which is where the original actually has collision) expanded by 1 m, one wall of
  3 m height, plus a kill plane 1 m below the fallback floor.
* **props** keep every one of the 511 placements.  Position/rotation/scale are the
  *original instance's* (that is the point of a reference rebuild; the v1 file had the
  position flattened to y = 0 and every rotation identity), ``volumeK`` is the original's own
  solid/bbox ratio (``1 / bboxOverSolidVolume``, whose median is 0.700), ``growthMm3`` is
  ``round(0.8 * k * aabb)`` and ``absorbable`` follows the gate at 1.20 m.  The six non-unit
  scales are applied; the one non-uniform one is kept and marked (D-5: it may be a decal, so
  it stays out of gameplay).

Everything is deterministic: sorted ids, fixed rounding, no timestamps.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import collider_proxy  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import placement_v2  # noqa: E402
from Tools.levelbuild import ref_models  # noqa: E402
from Tools.levelbuild import ref_stage  # noqa: E402
from Tools.levelbuild import ref_stage_visual  # noqa: E402

SCHEMA_V2 = placement_v2.SCHEMA

#: Canonical bytes, the ``.katerrain`` codec and the sidecar constants are the *format*'s,
#: not the builder's: they live in ``placement_v2`` and are re-exported here for the callers
#: (and tests) that only import the builder.
canonical_text = placement_v2.canonical_text
sidecar_bytes = placement_v2.sidecar_bytes
read_sidecar = placement_v2.read_sidecar
SidecarError = placement_v2.SidecarError
CHUNK_TRIANGLES = placement_v2.CHUNK_TRIANGLES
SIDECAR_NAME = placement_v2.SIDECAR_NAME
TERRAIN_ID = placement_v2.TERRAIN_ID
KATERRAIN_MAGIC = placement_v2.KATERRAIN_MAGIC
KATERRAIN_VERSION = placement_v2.KATERRAIN_VERSION
U16_VERTEX_LIMIT = placement_v2.U16_VERTEX_LIMIT

#: Everything this builder may *read* lives under the owner's reference tree.
READ_ROOT = Path.home() / "KatamariRef"

#: Everything it may *write* lives in a git-ignored reference level directory.
LEVELS_ROOT = core.LEVELS_DIR

DOCUMENT_NAME = "placement.v2.json"
CANCER_AREA_NO = 2  # GAMEINFO_MIS_11 START_STAGE + initial playArea (0)
CANCER_AREA_TABLE = Path("kdrr/tables/cancer_instances_reroll.tsv")

#: The sidecar/binary names come from the format module (re-exported with it).

#: Grid cell for terrain queries (§3.3 suggests 0.5-4 m; 1 m keeps the core at 19x15).
GRID_CELL_M = 1.0

#: Vertex budget of the sidecar's u16 index width.

#: The original's skeletal-animation load for Make Cancer:
#: ``ORIGINAL_ANIMATION_OBSERVATIONS.md`` §4.3 — 153 of the 524 instances carry bone
#: animation, 152 of them with ``playAnimNo >= 0``, from 19 object types (5 clips x 126,
#: 4 clips x 25, 6 clips x 2).  It is the number the owner's D-11 ("照原作挂满、不做 VAT")
#: budgets Animators for.
ANIMATED_INSTANCES_REFERENCE = 153

#: Reference level design ceiling: the original's MAX_DIAMETER 120 cm.
DESIGN_MAX_DIAMETER_M = 1.20

#: The start ball's semantic diameter on every reference level: the original's `initDiam`
#: 22.2 cm.  The spawn rules are stated for this diameter (footprint/step/reach), so it is
#: one constant rather than a literal repeated at each call site.
START_DIAMETER_M = 0.222

#: The original's own recovery penalty (PARITY_AUDIT G06/G13).
DEFAULT_VOLUME_PENALTY = core.DEFAULT_VOLUME_PENALTY

#: A scenery prop needs at least this much of its AABB inside the extractor's collision space.
SCENERY_COVERAGE = 0.5



class RefV2Error(RuntimeError):
    """A path this builder refuses to read or write, or an extraction that changed shape."""


# --------------------------------------------------------------------------- #
# path guards: read only the owner's reference tree, write only _ref_* levels
# --------------------------------------------------------------------------- #
def _inside(root, path):
    try:
        Path(path).resolve().relative_to(Path(root).resolve())
        return True
    except (OSError, ValueError):
        return False


def guard_read(path):
    """Return ``path`` if it is inside :data:`READ_ROOT`, else refuse."""
    resolved = Path(path).expanduser()
    if not _inside(READ_ROOT, resolved):
        raise RefV2Error(
            "refusing to read %s: the reference rebuild may only read %s (the extraction "
            "and the ripped models live outside the repository)" % (resolved, READ_ROOT))
    return resolved


def guard_write(path):
    """Return ``path`` if it is inside a git-ignored ``_ref_*`` level directory."""
    resolved = Path(path).expanduser().resolve()
    if not _inside(LEVELS_ROOT, resolved):
        raise RefV2Error("refusing to write %s: only %s is writable" % (resolved, LEVELS_ROOT))
    try:
        relative = resolved.relative_to(Path(LEVELS_ROOT).resolve())
    except ValueError:                                   # pragma: no cover - guarded above
        raise RefV2Error("refusing to write %s" % resolved)
    if not relative.parts or not relative.parts[0].startswith(ref_models.REF_LEVEL_PREFIX):
        raise RefV2Error(
            "refusing to write %s: the first directory under the levels root must be a "
            "%s* reference level (this build is UNLICENSED reference data)"
            % (resolved, ref_models.REF_LEVEL_PREFIX))
    return resolved


def level_directory(level_dir):
    return guard_write(Path(level_dir) / DOCUMENT_NAME).parent


# --------------------------------------------------------------------------- #
# numbers
# --------------------------------------------------------------------------- #
def n4(value):
    return core.clean_zero(round(float(value), 4))


def n6(value):
    return core.clean_zero(round(float(value), 6))


def vec3(values, digits=4):
    rounded = n4 if digits == 4 else (lambda v: core.clean_zero(round(float(v), digits)))
    return [rounded(values[0]), rounded(values[1]), rounded(values[2])]


def quat4(quat):
    canonical = core.canonical_quat(tuple(float(value) for value in quat))
    return [n6(canonical[0]), n6(canonical[1]), n6(canonical[2]), n6(canonical[3])]


# --------------------------------------------------------------------------- #
# terrain: the extractor's collision mesh -> world metres + CCW winding
# --------------------------------------------------------------------------- #
def terrain_from_collision(collision_lists):
    """``(vertices, indices, zone_ids, classes, stats)`` from the collision lists.

    The lists are the extractor's ``collision_area<N>.json``: per entry an AABB, vertex
    groups (four floats per vertex, the fourth being the zone id's bit pattern) and
    triangles over **group-local** indices (``ref_stage.entry_triangles`` already expands
    the strips).  Vertices become world metres (``-0.01 * ps2``); each triangle is flipped
    once (``b, c = c, b``) because that mapping has determinant -1, so the floors' normals
    point up and ``classes`` is meaningful.
    """
    vertices = []
    vertex_zones = []
    triangles = []
    for entry in ref_stage.collision_entries(collision_lists):
        values = entry.get("verts") or []
        base = len(vertices)
        offset = 0
        for group in entry.get("groups") or []:
            count = int(group.get("vertex_count") or 0)
            zones = group.get("zones") or []
            for index in range(count):
                start = (offset + index) * 4
                chunk = values[start:start + 3]
                if len(chunk) != 3:
                    continue
                vertices.append([-float(chunk[0]) / 100.0, -float(chunk[1]) / 100.0,
                                 -float(chunk[2]) / 100.0])
                vertex_zones.append(int(zones[index]) if index < len(zones) else 0)
            offset += count
        cursor = 0
        vertex_offset = 0
        for group in entry.get("groups") or []:
            count = int(group.get("vertex_count") or 0)
            produced = max(0, count - 2) if group.get("is_strip") else count // 3
            for triangle in (entry.get("triangles") or [])[cursor:cursor + produced]:
                a, b, c = (int(triangle[0]), int(triangle[1]), int(triangle[2]))
                triangles.append((base + vertex_offset + a, base + vertex_offset + b,
                                  base + vertex_offset + c))
            cursor += produced
            vertex_offset += count
    return _finish_terrain(vertices, vertex_zones, triangles)


def _majority_zone(codes):
    """The triangle's zone code: most frequent of its three vertices, ties -> smaller."""
    best = None
    for code in codes:
        count = sum(1 for other in codes if other == code)
        if best is None or count > best[0] or (count == best[0] and code < best[1]):
            best = (count, code)
    return best[1]


def _triangle_normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx)


def _finish_terrain(vertices, vertex_zones, triangles):
    cos45 = math.cos(math.radians(45.0))
    indices = []
    zone_ids = []
    classes = []
    dropped = 0
    up = down = 0
    for first, second, third in triangles:
        # The emitted order is (first, third, second): the world position map
        # (-0.01 * ps2) has determinant -1, so the PS2 winding is reversed once.  The
        # classes must come from the winding that is actually written.
        a, b, c = vertices[first], vertices[third], vertices[second]
        normal = _triangle_normal(a, b, c)
        length = math.sqrt(sum(value * value for value in normal))
        area = 0.5 * length
        if area < 1e-9:                       # [terrain_mesh] forbids degenerate triangles
            dropped += 1
            continue
        normal = tuple(value / length for value in normal)
        if normal[1] > cos45:
            classes.append(1)                 # ride
            up += 1
        elif normal[1] > 0.0:
            classes.append(2)                 # climb
        else:
            classes.append(3)                 # wall / ceiling
            down += 1
        indices.extend((first, third, second))
        zone_ids.append(_majority_zone((vertex_zones[first], vertex_zones[second],
                                        vertex_zones[third])))
    return {"vertices": vertices, "indices": indices, "zoneIds": zone_ids,
            "classes": classes,
            "stats": {"vertices": len(vertices), "triangles": len(indices) // 3,
                      "droppedDegenerate": dropped, "ride": classes.count(1),
                      "climb": classes.count(2), "wall": classes.count(3)}}


#: Chunking (2026-11-16).  The runtime's broad phase is one XZ AABB per chunk, tested for all
#: 17 probes every step, so a chunk must be *spatially compact*: consecutive-in-index chunks of
#: 512 triangles put the whole courtyard in every box and every probe then pays ~1,950 k_t.
#: Cutting 3 m XZ tiles and sorting inside a tile by height gives ~96-triangle chunks whose
#: boxes hold one small piece of the map; ``MaxChunks = 64`` (TerrainMeshMath) is the hard
#: limit, so the triangle budget leaves ~7 chunks of headroom for future edits.
#: Measured on the reference level: 3 m tiles / 60 triangles gives 58 chunks (6 below the
#: runtime's 64 cap, which is the owner's headroom for the reskin) and the best cost that fits
#: that cap.  A tighter tile stops helping (2 m: 428 vs 467 k_t, within noise) because a chunk
#: AABB is the union of whole triangles and 89 triangles here are wider than 3 m.
CHUNK_TILE_M = 3.0
CHUNK_LIMIT = 60
#: A chunk never spans a bigger vertical gap than this, so it cannot hold both the floor and
#: a roof (§ the runtime's prefilter has no max-Y: an upper chunk would stay a candidate).
CHUNK_Y_GAP_M = 2.0


def triangle_bounds(vertices, indices, corner):
    """``(low, high, centre)`` of one triangle, in triangle units."""
    points = [vertices[indices[corner * 3 + step]] for step in range(3)]
    low = [min(point[axis] for point in points) for axis in range(3)]
    high = [max(point[axis] for point in points) for axis in range(3)]
    return low, high, [(low[axis] + high[axis]) / 2.0 for axis in range(3)]


def spatial_triangle_order(vertices, indices, tile=CHUNK_TILE_M, limit=CHUNK_LIMIT,
                           gap=CHUNK_Y_GAP_M):
    """A spatially coherent triangle permutation: ``[triangle_index, ...]``.

    Deterministic: tiles are visited in ``(x, z)`` cell order, triangles inside a tile are
    sorted by height, and a run is cut early where the height jumps (floor vs roof).  The
    result is what makes ``triangleRange`` chunks compact *and* contiguous.
    """
    total = len(indices) // 3
    tiles = {}
    for corner in range(total):
        _low, _high, centre = triangle_bounds(vertices, indices, corner)
        tiles.setdefault((math.floor(centre[0] / tile), math.floor(centre[2] / tile)),
                         []).append((corner, centre))
    order = []
    for key in sorted(tiles):
        members = sorted(tiles[key], key=lambda item: (round(item[1][1], 3),
                                                       round(item[1][2], 3),
                                                       round(item[1][0], 3), item[0]))
        start = 0
        while start < len(members):
            stop = min(start + limit, len(members))
            if stop < len(members):
                window = range(start + max(1, limit // 2), stop)
                gaps = [(members[k + 1][1][1] - members[k][1][1], k) for k in window]
                if gaps:
                    biggest, split = max(gaps, key=lambda item: item[0])
                    if biggest > gap:
                        stop = split + 1
            order.extend(member[0] for member in members[start:stop])
            start = stop
    return order


def permute_terrain(terrain, order):
    """Rewrite ``indices`` and the per-triangle arrays in ``order`` (everything else stays)."""
    indices = terrain["indices"]
    permuted = []
    for corner in order:
        permuted.extend(indices[corner * 3:corner * 3 + 3])
    out = dict(terrain)
    out["indices"] = permuted
    for key in ("zoneIds", "classes"):
        values = terrain.get(key)
        if isinstance(values, list) and len(values) == len(order):
            out[key] = [values[corner] for corner in order]
    return out


def chunk_layout(vertices, indices, limit=CHUNK_LIMIT, margin=0.001):
    """``(boxes, chunks)``: consecutive ``limit``-triangle runs, one AABB each."""
    total = len(indices) // 3
    boxes = []
    chunks = []
    for index, start in enumerate(range(0, total, limit)):
        count = min(limit, total - start)
        low = [math.inf, math.inf, math.inf]
        high = [-math.inf, -math.inf, -math.inf]
        for corner in range(start, start + count):
            for offset in range(3):
                point = vertices[indices[corner * 3 + offset]]
                for axis in range(3):
                    low[axis] = min(low[axis], point[axis])
                    high[axis] = max(high[axis], point[axis])
        boxes.append({"id": "cb%02d" % index,
                      "min": vec3([low[0] - margin, low[1] - margin, low[2] - margin]),
                      "max": vec3([high[0] + margin, high[1] + margin, high[2] + margin]),
                      "triangleRange": [start, count]})
        chunks.append({"id": "c%02d" % index, "boxRange": [index, 1],
                       "triangleRange": [start, count]})
    return boxes, chunks


def grid_for(boxes, cell=GRID_CELL_M):
    """A ``grid`` object covering every broad-phase box (origin on the cell lattice)."""
    low_x = min(box["min"][0] for box in boxes)
    low_z = min(box["min"][2] for box in boxes)
    high_x = max(box["max"][0] for box in boxes)
    high_z = max(box["max"][2] for box in boxes)
    origin_x = math.floor(low_x / cell) * cell
    origin_z = math.floor(low_z / cell) * cell
    cells_x = max(1, int(math.ceil((high_x - origin_x) / cell)))
    cells_z = max(1, int(math.ceil((high_z - origin_z) / cell)))
    return {"cellM": cell, "origin": [n4(origin_x), n4(origin_z)],
            "cellsX": cells_x, "cellsZ": cells_z}


# --------------------------------------------------------------------------- #
# the .katerrain sidecar (LEVEL_FORMAT_V2 §8.3)
# --------------------------------------------------------------------------- #
# --------------------------------------------------------------------------- #
# the original's motion programs and paths (ORIGINAL_MOTION_PROGRAM §11-§14)
# --------------------------------------------------------------------------- #
#: Motion-table class name -> the v2 ``motions[].class`` name (LEVEL_FORMAT_V2 §6.7.3).
MOTION_CLASS_NAMES = {
    "SporadicWalk": "sporadic_walk", "Hop": "hop", "Sway": "sway",
    "DraggedAlong": "dragged_along", "WaitForPlayer": "wait_for_player",
    "WhackAMole": "whack_a_mole", "PathCollision": "path_collision", "Spin": "spin",
    "Clouds": "clouds", "Flip": "flip", "Bob": "bob", "SlopingPath": "sloping_path",
    "BackAndForthNoYaw": "back_and_forth",
}

#: Per-class update rate (§6.7.5: displacement decides the rate, not the class).
MOTION_RATES = {"sporadic_walk": 15, "hop": 30, "sway": 15, "dragged_along": 0,
                "wait_for_player": 5, "whack_a_mole": 30, "path_collision": 15, "spin": 5,
                "clouds": 10, "flip": 5, "bob": 10, "sloping_path": 15,
                "back_and_forth": 15}

#: Classes that only exist as ``behaviour:"attached"`` in v2 (§6.7.3: it is the parent
#: relation expressed as data, so it gets no ``motions[]`` entry of its own).
ATTACHED_CLASSES = ("dragged_along",)

#: Classes whose motion rides a ``paths[]`` entry.
PATH_CLASSES = ("path_collision", "sloping_path", "back_and_forth")

#: 1 cm/frame = 0.305 m/s at the game's 30 Hz (measured, motion_table.units).
CM_PER_SEC_PER_CM_PER_FRAME = 30.5


def load_motion_tables(stage_dir):
    """``(motion_document, path_document, reason)`` — both files or neither.

    A missing table is not an error: the level simply has no motion data, and the caller
    records *why* instead of writing half a table (a motion that references a path which was
    not loaded would be worse than no motion at all).
    """
    directory = Path(stage_dir)
    motion_path = directory / "motion_table.json"
    path_path = directory / "path_table.json"
    if not motion_path.is_file() or not path_path.is_file():
        missing = [name for name in ("motion_table.json", "path_table.json")
                   if not (directory / name).is_file()]
        return None, None, ("%s not in the extraction" % ", ".join(missing))
    motion = ref_stage.read_json(guard_read(motion_path))
    paths = ref_stage.read_json(guard_read(path_path))
    if motion.get("decodedLevel") != paths.get("decodedLevel"):
        return None, None, ("the two tables describe different levels (%r vs %r)"
                            % (motion.get("decodedLevel"), paths.get("decodedLevel")))
    return motion, paths, None


#: The v2 budget: one path is at most 64 points (§12.2), and it is an engine capacity (the
#: import bakes fixed-length arrays), so a denser original polyline is simplified, never
#: silently truncated.
MAX_PATH_POINTS = 62


def _segment_distance(point, start, end):
    ax, ay, az = start
    bx, by, bz = end
    px, py, pz = point
    dx, dy, dz = bx - ax, by - ay, bz - az
    length_sq = dx * dx + dy * dy + dz * dz
    if length_sq <= 1e-12:
        return math.dist(point, start)
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / length_sq))
    return math.dist(point, (ax + t * dx, ay + t * dy, az + t * dz))


def simplify_polyline(points, max_points=MAX_PATH_POINTS, start_tolerance=0.005):
    """Douglas-Peucker until the polyline fits ``max_points``; deterministic.

    Returns ``(points, tolerance, deviation)``: the kept points, the tolerance that achieved
    it, and the largest deviation of the original points from the simplified line (so the
    report can say exactly how much shape was traded for the budget).
    """
    if len(points) <= max_points:
        return points, 0.0, 0.0

    def reduce(tolerance):
        keep = [False] * len(points)
        keep[0] = keep[-1] = True
        stack = [(0, len(points) - 1)]
        while stack:
            first, last = stack.pop()
            worst = -1.0
            worst_index = None
            for index in range(first + 1, last):
                distance = _segment_distance(points[index], points[first], points[last])
                if distance > worst:
                    worst, worst_index = distance, index
            if worst_index is not None and worst > tolerance:
                keep[worst_index] = True
                stack.append((first, worst_index))
                stack.append((worst_index, last))
        return [point for point, flag in zip(points, keep) if flag]

    tolerance = start_tolerance
    simplified = reduce(tolerance)
    while len(simplified) > max_points:
        tolerance *= 1.6
        simplified = reduce(tolerance)
    # the largest distance from any original point to the simplified polyline
    deviation = 0.0
    for point in points:
        best = math.inf
        for index in range(1, len(simplified)):
            best = min(best, _segment_distance(point, simplified[index - 1], simplified[index]))
        deviation = max(deviation, best)
    return simplified, tolerance, deviation


def _level_entry(document, level_index, key):
    for entry in document.get("levels") or []:
        if isinstance(entry, dict) and entry.get("levelIndex") == level_index:
            return entry.get(key) or []
    return []


def build_motions(motion_document, path_document, level_index, orders_by_item):
    """``(motions, paths, props_by_order, summary)`` for one level.

    ``orders_by_item`` maps itemId -> the extractor's spawn-record index (``instanceIndex``),
    which is also the motion table's key.  Nothing is matched by name.
    """
    if motion_document is None or path_document is None:
        return [], [], {}, {"available": False}
    entries = _level_entry(motion_document, level_index, "motionEntries")
    raw_paths = _level_entry(path_document, level_index, "paths")

    paths = []
    path_ids = {}
    for path in raw_paths:
        index = path.get("pathIndex")
        raw_points = [[-float(value) / 100.0 for value in point]
                      for point in path.get("points") or []]
        points, tolerance, deviation = simplify_polyline(raw_points)
        points = [vec3(point) for point in points]
        speed_cm_per_frame = path.get("pathSpeedCmPerFrame")
        entry = {"id": "p%02d" % index, "points": points}
        if isinstance(speed_cm_per_frame, (int, float)) and speed_cm_per_frame > 0:
            cm_per_sec = round(float(speed_cm_per_frame) * CM_PER_SEC_PER_CM_PER_FRAME, 1)
            entry["speedCmPerSec"] = cm_per_sec
            entry["notes"] = ("%g cm/frame x 30.5 = %g cm/s = %g m/s (motion_table units)"
                              % (speed_cm_per_frame, cm_per_sec, round(cm_per_sec / 100.0, 3)))
        else:
            entry["notes"] = ("speed sentinel %r: the object's speedIndex picks the speed "
                              "(no path speed)" % (speed_cm_per_frame,))
        entry["notes"] += ("; %d original point(s)%s"
                           % (len(raw_points),
                              (", Douglas-Peucker simplified to %d at %.3f m (max deviation "
                               "%.4f m) for the 64-point budget" % (len(points), tolerance,
                                                                    deviation))
                              if len(raw_points) > len(points) else ""))
        path_ids[index] = entry["id"]
        paths.append(entry)
    paths.sort(key=lambda item: item["id"])

    motions = []
    motion_by_move_type = {}
    for entry in entries:
        move_type = entry.get("moveType")
        motion_class = MOTION_CLASS_NAMES.get(entry.get("motionClass"))
        if motion_class is None:
            continue
        if motion_class in ATTACHED_CLASSES:
            motion_by_move_type[move_type] = {"class": motion_class, "attached": True}
            continue
        motion_id = "mt%02d_%s" % (move_type, motion_class)
        motion = {"id": motion_id, "class": motion_class}
        if motion_class in PATH_CLASSES:
            motion["speed"] = {"source": "path"}
            reference = path_ids.get(entry.get("pathIndex"))
            if reference:
                motion["path"] = reference
        elif motion_class in ("sporadic_walk", "clouds"):
            motion["speed"] = {"source": "type"}
        else:
            motion["speed"] = {"source": "none"}
        motion["updatesPerSecond"] = MOTION_RATES[motion_class]
        motion["loop"] = motion_class != "whack_a_mole"
        provenance = {"moveType": move_type, "action": (entry.get("mainAction") or {}).get("name"),
                      "backupIndex": entry.get("backupIndex"),
                      "pathIndex": entry.get("pathIndex")}
        if entry.get("motionID") is not None:
            provenance["motionID"] = entry["motionID"]
        motion["provenance"] = provenance
        notes = ["%s (motionName %r, main action %r)"
                 % (entry.get("motionClass"), entry.get("motionName"),
                    (entry.get("mainAction") or {}).get("name"))]
        if motion_class == "wait_for_player":
            notes.append("triggerUnknown: the trigger condition is not measured "
                         "(inZone && parentRolled for 0x04, walk-up radius for the others); "
                         "params stay unset, the runtime uses PROVISIONAL defaults")
        if motion_class == "path_collision" and entry.get("motionID") == -1:
            notes.append("motionID -1: the action is backupIndex+1 (legacy escape)")
        motion["notes"] = "; ".join(notes)
        motion_by_move_type[move_type] = {"class": motion_class, "attached": False,
                                          "id": motion_id}
        motions.append(motion)
    motions.sort(key=lambda item: item["id"])

    # per prop: which motion (or attached class) the instance's own record says
    per_order = {}
    records = motion_document.get("instances") or []
    for record in records:
        index = record.get("instanceIndex")
        motion_class = MOTION_CLASS_NAMES.get(record.get("motionClass"))
        if motion_class is None:
            continue
        resolved = motion_by_move_type.get(record.get("moveType")) or {}
        per_order[index] = {"class": motion_class, "moveType": record.get("moveType"),
                            "motionId": resolved.get("id"),
                            "motionID": record.get("motionID"),
                            "pathIndex": record.get("pathIndex"),
                            "speedIndex": record.get("speedIndex"),
                            "speedCmPerFrame": record.get("speedCmPerFrame"),
                            "speedMps": record.get("speedMps"),
                            "animated": bool(record.get("animated")),
                            "inLevelProps": bool(record.get("inLevelProps"))}
    by_item = {}
    for item_id, order in orders_by_item.items():
        if order in per_order:
            by_item[item_id] = per_order[order]
    summary = {
        "available": True,
        "motions": len(motions),
        "paths": len(paths),
        "pathPoints": sum(len(path["points"]) for path in paths),
        "movingRecords": len(per_order),
        "movingProps": len(by_item),
        "withMotionField": sum(1 for info in by_item.values()
                               if not info["class"] in ATTACHED_CLASSES),
        "attached": sum(1 for info in by_item.values() if info["class"] in ATTACHED_CLASSES),
        "moveTypes": sorted(motion_by_move_type),
        "nonPropRecords": sorted(set(per_order) - set(orders_by_item.values())),
        "byClass": {},
    }
    for info in by_item.values():
        summary["byClass"][info["class"]] = summary["byClass"].get(info["class"], 0) + 1
    return motions, paths, by_item, summary


def motion_notes(info):
    """The per-prop notes a motion record contributes (class, unverified triggers)."""
    notes = ["motionClass=%s" % info["class"]]
    if info["class"] in ATTACHED_CLASSES:
        notes.append("dragged_along: expressed as behaviour 'attached' (no motions[] entry)")
    if info.get("moveType") == 10:
        notes.append("triggerUnknown: moveType 10 (0x04) needs inZone && parentRolled, "
                     "unverified - no parameters guessed")
    if info.get("motionID") is not None and info["class"] == "path_collision":
        notes.append("motionID=%s" % info["motionID"])
    return notes


# --------------------------------------------------------------------------- #
# props / defs / structures
# --------------------------------------------------------------------------- #
def animated_instances(stage_dir, props):
    """``(count, source)``: how many instances need an Animator (§6.7.6 / D-11).

    The extraction is the preferred source: a per-instance ``playAnimNo >= 0`` is the
    original's "start at clip N" flag and matches the 153 animated instances (152/153).  The
    current extractor does **not** export that field, so the documented reference count is
    used instead and the caller records which source it was; the count is capped by this
    document's props (a level cannot animate more props than it has).
    """
    records = ref_stage.load_instances(stage_dir)
    flagged = [record for record in records
               if isinstance(record.get("playAnimNo"), int) and record["playAnimNo"] >= 0]
    if flagged:
        return min(len(flagged), len(props)), "instances.json playAnimNo >= 0"
    return min(ANIMATED_INSTANCES_REFERENCE, len(props)), (
        "ORIGINAL_ANIMATION_OBSERVATIONS §4.3 (153/524; the extractor does not export "
        "playAnimNo)")


def def_volume_k(defn):
    """The original's own solid/bbox fill ratio, clamped to the schema's range."""
    ratio = (defn or {}).get("bboxOverSolidVolume")
    if isinstance(ratio, (int, float)) and not isinstance(ratio, bool) and ratio > 0.0:
        value = 1.0 / float(ratio)
    else:
        value = core.DEFAULT_VOLUME_K
    return max(0.01, min(core.VOLUME_K_MAX, value))


def collision_boxes(collision_lists):
    """The extractor's 68 broad-phase AABBs in world metres (``[min, max]``)."""
    boxes = []
    for entry in ref_stage.collision_entries(collision_lists):
        box = entry.get("bbox") or {}
        if not box.get("min") or not box.get("max"):
            continue
        low = [-float(value) / 100.0 for value in box["min"][:3]]
        high = [-float(value) / 100.0 for value in box["max"][:3]]
        boxes.append(([min(low[0], high[0]), min(low[1], high[1]), min(low[2], high[2])],
                      [max(low[0], high[0]), max(low[1], high[1]), max(low[2], high[2])]))
    return boxes


def coverage(aabb, boxes):
    """The largest fraction of ``aabb`` that sits inside one collision AABB."""
    low, high = aabb
    volume = ((high[0] - low[0]) * (high[1] - low[1]) * (high[2] - low[2]))
    if volume <= 0.0:
        return 0.0
    best = 0.0
    for box_low, box_high in boxes:
        ox = min(high[0], box_high[0]) - max(low[0], box_low[0])
        oy = min(high[1], box_high[1]) - max(low[1], box_low[1])
        oz = min(high[2], box_high[2]) - max(low[2], box_low[2])
        if ox > 0.0 and oy > 0.0 and oz > 0.0:
            best = max(best, (ox * oy * oz) / volume)
    return best


def zone_table(collision_lists, doc):
    """``(zones, code by prop itemId)``: named zones and the floor zone under each prop.

    ``meta.zones[].code`` is the collision triangle's zone id (0/1/3/5/8/24 in Cancer);
    ``props[].zone`` is the name.  ``ref_stage`` already resolves the surface under a prop
    (the same lookup the viewer uses), so this only turns codes into names.
    """
    lookup_codes = set()
    for entry in ref_stage.collision_entries(collision_lists):
        for group in entry.get("groups") or []:
            for code in group.get("zones") or []:
                lookup_codes.add(int(code))
    codes = sorted(lookup_codes) or [0]
    zones = []
    for code in codes:
        zones.append({"id": "default" if code == 0 else "zone_%d" % code,
                      "code": int(code),
                      "kind": "ground" if code == 0 else "other"})
    names = {zone["code"]: zone["id"] for zone in zones}
    return zones, names


def def_mesh(defn, models_root, table):
    """``(points, faces, half)``: one def's model in its declared AABB frame, or ``None``.

    The extraction's GLBs are raw rips whose axis convention it does not record, so the
    model is used by its *shape* after one uniform scale that keeps it inside the declared
    AABB (centred on the origin, ``half`` = that AABB's half extents): everything fitted on
    it is conservative (never bigger than the box the placement declares) and never guesses
    a per-axis permutation.
    """
    if models_root is None:
        return None
    resolved = ref_models.resolve_model(defn.get("id"), None, table, models_root)
    if resolved is None or resolved.get("kind") != "glb":
        return None
    try:
        document, binary = ref_stage.read_glb(guard_read(resolved["path"]).read_bytes())
    except (OSError, ref_stage.RefStageError, RefV2Error):
        return None
    points = []
    faces = []
    for mesh in document.get("meshes") or []:
        for primitive in mesh.get("primitives") or []:
            index = (primitive.get("attributes") or {}).get("POSITION")
            if index is None:
                continue
            base = len(points)
            for item in ref_stage.accessor_values(document, binary, index):
                if len(item) == 3:
                    points.append(tuple(float(value) for value in item))
            indices = primitive.get("indices")
            if indices is None:
                continue
            local = [item[0] for item in ref_stage.accessor_values(document, binary, indices)]
            for offset in range(0, len(local) - 2, 3):
                faces.append((base + local[offset], base + local[offset + 1],
                              base + local[offset + 2]))
    if len(points) < 4 or not faces:
        return None
    target = defn.get("aabbM") or defn.get("modelBboxM")
    if not (isinstance(target, list) and len(target) == 3 and all(target)):
        return None
    low = [min(point[axis] for point in points) for axis in range(3)]
    high = [max(point[axis] for point in points) for axis in range(3)]
    extent = [high[axis] - low[axis] for axis in range(3)]
    centre = [(high[axis] + low[axis]) / 2.0 for axis in range(3)]
    ratios = [float(target[axis]) / extent[axis] for axis in range(3)
              if extent[axis] > 1e-9 and float(target[axis]) > 0.0]
    if not ratios:
        return None
    scale = min(ratios)
    shifted = [((point[0] - centre[0]) * scale, (point[1] - centre[1]) * scale,
                (point[2] - centre[2]) * scale) for point in points]
    half = [float(target[axis]) / 2.0 for axis in range(3)]
    return shifted, faces, half


def def_collider(defn, models_root, table, mesh=None):
    """A fitted collider for one def, or ``None`` when the model cannot be fitted.

    ``mesh`` is :func:`def_mesh`'s result when the caller already loaded it.
    """
    if mesh is None:
        mesh = def_mesh(defn, models_root, table)
    if mesh is None:
        return None
    shifted, faces, half = mesh
    try:
        fit = collider_proxy.fit_proxy(shifted, faces,
                                       bounds=((-half[0], -half[1], -half[2]),
                                               (half[0], half[1], half[2])))
        block = fit.block("auto") or {}
    except (ValueError, ZeroDivisionError, IndexError):
        return None
    collider = block.get("collider")
    if not isinstance(collider, dict):
        return None
    if collider_proxy.collider_schema_errors(collider):
        return None
    return collider


def def_parts(collider, mesh):
    """``parts`` for one def's static solid: the model as up to 4 boxes, or ``None``.

    Only a convex proxy that fills less than ``PART_SPLIT_FILL_MAX`` of its own AABB is
    split: the motor's static table is OBB-only, so such a solid otherwise reaches the scene
    as one box around the whole hull (a laundry deck becomes a 1.43 m x 2 m wall).  The boxes
    are cut from the model's triangles, not from the hull, because the hull of a pole with
    a full-width foot is itself a full-width wedge.
    """
    if mesh is None:
        return None
    fill = collider_proxy.hull_fill_ratio(collider)
    if fill is None or fill >= collider_proxy.PART_SPLIT_FILL_MAX:
        return None
    points, faces, _half = mesh
    return collider_proxy.solid_parts(points, faces) or None


def scaled_parts(parts, scale):
    """A def's ``parts`` under one instance's uniform ``scale``; ``None`` when non-uniform."""
    if not parts:
        return None
    if len({round(value, 6) for value in scale}) > 1:
        return None
    factor = float(scale[0])
    if factor == 1.0:
        return parts
    return [{"center": [n4(value * factor) for value in part["center"]],
             "size": [n4(value * factor) for value in part["size"]]} for part in parts]


def build_structures(placements, boxes, max_diameter_m=DESIGN_MAX_DIAMETER_M):
    """Split the 511 props into scenery-with-solid, scenery-background and pickups."""
    solids = []
    report = {"neverAbsorbable": 0, "withSolid": 0, "background": 0,
              "backgroundItems": [], "reasons": []}
    for entry in placements:
        gate = entry["absorbDiameterReqMm"]
        if gate <= core.floor_mm(max_diameter_m):
            continue
        report["neverAbsorbable"] += 1
        covered = coverage(entry["worldAabb"], boxes)
        if covered >= SCENERY_COVERAGE:
            solid = {
                "id": entry["itemId"],
                "kind": entry["def"],
                "role": "box",
                # The contact shape is the def's fitted proxy, not the raw AABB: a box that
                # wraps a tree or a lamppost reports "a crab is 60 cm inside the tree" for a
                # crab standing at its base.  ``size`` stays the AABB because it is the
                # *support* surface (the top face the ball can stand on).
                "shape": "proxy",
                "center": vec3(entry["position"]),
                "size": vec3(entry["aabb"]),
                "rotationQuat": entry["rotationQuat"],
                "collision": "box",
                "absorbable": False,
                "support": True,
                "notes": "scenery: gate %d mm; %.0f%% inside the extractor's collision "
                         "space" % (gate, covered * 100.0),
            }
            collider = entry.get("collider")
            if isinstance(collider, dict):
                solid["collider"] = collider
                parts = entry.get("parts")
                if parts:
                    solid["parts"] = parts
                solid["proxySource"] = "def"
                solid["notes"] += "; contact uses the def's %s proxy" % collider.get("type")
                if parts:
                    solid["notes"] += (" as %d static boxes cut from the model (hull fills "
                                       "%.2f of its box)"
                                       % (len(parts), collider_proxy.hull_fill_ratio(collider)))
                    report["splitSolids"] = report.get("splitSolids", 0) + 1
            else:
                solid["shape"] = "box"
                solid["proxySource"] = "aabb-fallback"
                solid["notes"] += "; no def collider: contact falls back to the AABB"
                report["aabbFallback"] = report.get("aabbFallback", 0) + 1
            solids.append(solid)
            report["withSolid"] += 1
        else:
            report["background"] += 1
            report["backgroundItems"].append(entry["itemId"])
    report["reasons"].append(
        "scenery = gate > %d mm (the original's MAX_DIAMETER) and >= %.0f%% of the AABB "
        "inside one of the %d extractor collision AABBs"
        % (core.floor_mm(max_diameter_m), SCENERY_COVERAGE * 100.0, len(boxes)))
    solids.sort(key=lambda solid: solid["id"])
    return solids, report


def activity_polygon(terrain, boxes, margin=1.0):
    """The authored play area: union footprint of the collision space, expanded.

    The original has no bounds field (§5.1); what it does have is *collision geometry*, and
    that geometry is the only place the ball can go.  So the polygon is the XZ union of the
    extractor's collision AABBs (which covers every collision triangle), grown by
    ``margin`` and rounded to centimetres.
    """
    low = [math.inf, math.inf]
    high = [-math.inf, -math.inf]
    for box_low, box_high in boxes:
        low[0] = min(low[0], box_low[0]); high[0] = max(high[0], box_high[0])
        low[1] = min(low[1], box_low[2]); high[1] = max(high[1], box_high[2])
    for point in terrain["vertices"]:
        low[0] = min(low[0], point[0]); high[0] = max(high[0], point[0])
        low[1] = min(low[1], point[2]); high[1] = max(high[1], point[2])
    low = [low[0] - margin, low[1] - margin]
    high = [high[0] + margin, high[1] + margin]
    points = [[n4(low[0]), n4(low[1])], [n4(high[0]), n4(low[1])],
              [n4(high[0]), n4(high[1])], [n4(low[0]), n4(high[1])]]
    return points, {"low": low, "high": high, "margin": margin}


# --------------------------------------------------------------------------- #
# the document
# --------------------------------------------------------------------------- #
def load_level_inputs(level_dir):
    """The v1 reference file (`placement.model.json`), defs and models root."""
    folder = Path(level_dir)
    doc = json.loads(Path(guard_write(folder / DOCUMENT_NAME)).parent.joinpath(
        "placement.model.json").read_text(encoding="utf-8"))
    original = json.loads((folder / "original_defs.json").read_text(encoding="utf-8"))
    models_root = None
    try:
        models_root = ref_models.normalized_models_dir(ref_models.default_models_dir())
    except ref_models.RefAssetError:
        models_root = None
    table = ref_models.load_model_table(folder)
    return doc, original, models_root, table


def apply_instance_hit_areas(document, rows, area_no):
    """Join the original simulation table by item ID; DummyProps have no placement ID."""
    by_id = {}
    for row in rows:
        item_id = row["our_itemId"]
        if not item_id:
            continue
        if item_id in by_id:
            raise RefV2Error("duplicate hit-area instance: %s" % item_id)
        by_id[item_id] = int(row["s8MonoHitOnAreaNo"])
    props = document["props"]
    solids = document["structures"]["solids"]
    prop_ids = {prop["itemId"] for prop in props}
    solid_ids = {solid["id"] for solid in solids}
    if prop_ids | solid_ids != by_id.keys():
        missing = sorted((prop_ids | solid_ids) - by_id.keys())
        extra = sorted(by_id.keys() - (prop_ids | solid_ids))
        raise RefV2Error("hit-area instance IDs differ: missing %r, extra %r" % (missing, extra))
    for group, id_key in ((props, "itemId"), (solids, "id")):
        for instance in group:
            instance["hitOnAreaNo"] = by_id[instance[id_key]]
    meta = document["meta"]
    meta["areaNo"] = area_no
    meta["expected"]["staticObbs"] = placement_v2.static_obb_count(solids, area_no)


def apply_cancer_hit_areas(document):
    with guard_read(READ_ROOT / CANCER_AREA_TABLE).open(encoding="utf-8", newline="") as table:
        apply_instance_hit_areas(document, csv.DictReader(table, delimiter="\t"), CANCER_AREA_NO)


def build_document(level_id, level_dir, *, stage_dir=None, with_colliders=True,
                   max_diameter_m=DESIGN_MAX_DIAMETER_M, volume_penalty=DEFAULT_VOLUME_PENALTY,
                   time_limit_s=420.0, position_source="instance", visual=None):
    """Build ``(document, sidecar, report)`` for one reference level.

    ``visual`` is :func:`ref_stage_visual.load`'s result: when given, its ``terrain.visual``
    block is put in the document (the contact terrain is unchanged either way).
    """
    folder = Path(level_dir)
    level_doc, original, models_root, model_table = load_level_inputs(folder)
    if not ref_models.is_ref_level(level_id):
        raise RefV2Error("%r is not a reference level (_ref_*)" % (level_id,))
    directory, reason = ref_stage.resolve_stage_dir(stage_dir)
    if directory is None:
        raise RefV2Error(reason or "no stage extraction")
    guard_read(directory)
    collisions = ref_stage.read_json(ref_stage.collision_file(directory))
    terrain = terrain_from_collision(collisions)
    # Spatial (not index-order) chunks: the runtime probes one XZ AABB per chunk.
    order = spatial_triangle_order(terrain["vertices"], terrain["indices"])
    terrain = permute_terrain(terrain, order)
    boxes, chunks = chunk_layout(terrain["vertices"], terrain["indices"])
    chunk_triangles = sorted(chunk["triangleRange"][1] for chunk in chunks)
    chunk_xz = sorted(math.hypot(box["max"][0] - box["min"][0], box["max"][2] - box["min"][2])
                      for box in boxes)
    boxes_raw = collision_boxes(collisions)
    zones, zone_names = zone_table(collisions, level_doc)

    defs_by_id = {entry["id"]: entry for entry in original.get("defs") or []}
    colliders_by_def = {}
    parts_by_def = {}
    if with_colliders:
        for def_id, defn in defs_by_id.items():
            mesh = def_mesh(defn, models_root, model_table)
            collider = def_collider(defn, models_root, model_table, mesh=mesh)
            if collider is not None:
                colliders_by_def[def_id] = collider
                parts = def_parts(collider, mesh)
                if parts:
                    parts_by_def[def_id] = parts
    prepared = ref_stage.prepare(directory, folder, level_doc)
    transforms = (prepared.get("transforms") or {}).get("byItemId") or {}

    # The original's motion programs and paths are a per-level table; a prop's entry is
    # resolved by its OWN spawn-record index (never by name).
    motion_document, path_document, motion_reason = load_motion_tables(directory)
    motions, paths, motion_by_item, motion_summary = build_motions(
        motion_document, path_document,
        (motion_document or {}).get("decodedLevel"),
        {item_id: info["order"] for item_id, info in transforms.items()})
    motion_summary["reason"] = motion_reason
    speed_index_by_def = {}
    if motion_document:
        seen = {}
        for record in motion_document.get("instances") or []:
            if MOTION_CLASS_NAMES.get(record.get("motionClass")) is None:
                continue
            name_id = record.get("nameIdx")
            seen.setdefault(name_id, set()).add(record.get("speedIndex"))
        for prop in level_doc.get("props") or []:
            definition = defs_by_id.get(prop.get("def")) or {}
            values = seen.get(definition.get("mono"))
            if values and len(values) == 1:
                speed_index_by_def[prop["def"]] = int(next(iter(values)))

    placements = []
    scavenged = {"positionMoved": 0, "positionMaxShift": 0.0, "scaleApplied": 0,
                 "nonUniform": [], "missingInstance": [], "zoneUnknown": 0}
    for prop in level_doc.get("props") or []:
        instance = transforms.get(prop.get("itemId")) or {}
        scale = [float(value) for value in (instance.get("scale") or [1.0, 1.0, 1.0])]
        # The declared aabb is the source of every derived number (gate, growth): it is
        # rounded once, here, and the validator recomputes from these exact values.
        size = [n4(float(prop["aabb"][axis]) * scale[axis]) for axis in range(3)]
        quat = quat4(instance.get("quat") or prop.get("rotationQuat") or [0, 0, 0, 1])
        if position_source == "instance" and instance.get("position"):
            position = [float(value) for value in instance["position"]]
            placed = [float(value) for value in prop["position"]]
            horizontal = math.hypot(position[0] - placed[0], position[2] - placed[2])
            if horizontal > 0.01:
                scavenged["positionMoved"] += 1
                scavenged["positionMaxShift"] = max(
                    scavenged["positionMaxShift"], horizontal)
            if abs(position[1] - placed[1]) > 0.01:
                scavenged["heightChanged"] = scavenged.get("heightChanged", 0) + 1
        else:
            position = [float(value) for value in prop["position"]]
        if not instance:
            scavenged["missingInstance"].append(prop.get("itemId"))
        if scale != [1.0, 1.0, 1.0]:
            scavenged["scaleApplied"] += 1
            if len({round(value, 6) for value in scale}) > 1:
                scavenged["nonUniform"].append(prop.get("itemId"))
        defn = defs_by_id.get(prop.get("def")) or {}
        # k is rounded once, like the aabb: every derived number comes from the stored value.
        volume_k = n4(def_volume_k(defn))
        gate_mm = core.absorb_diameter_req_mm(size, volume_k)
        growth = core.item_growth_mm3(size, volume_k, volume_penalty)
        non_uniform = len({round(value, 6) for value in scale}) > 1
        # D-5 (owner 2026-11-16): the one non-uniform scale the extraction carries has
        # unverified semantics (a 1.7x0.3x0.3 cm sheet for a 13 m building), so it is kept for
        # the audit trail but declared out of gameplay: not absorbable, no collider proxy.
        absorbable = (not non_uniform) and gate_mm <= core.floor_mm(max_diameter_m)
        world = core.world_aabb(tuple(position), tuple(size), tuple(quat))
        zone_code = instance.get("zone")
        zone = zone_names.get(zone_code) if zone_code is not None else None
        if zone is None:
            zone = "default"
            if instance:
                scavenged["zoneUnknown"] += 1
        parent = instance.get("parent")
        notes = []
        if instance.get("moveType") is not None:
            notes.append("moveType=%d" % instance["moveType"])
        if instance.get("instance") is not None:
            notes.append("instance=#%d" % instance["instance"])
        if parent:
            notes.append("parent=extractor")
        if non_uniform:
            notes.append("non-uniform scale (D-5: unverified semantics; not absorbable, "
                         "no collider)")
        elif scale != [1.0, 1.0, 1.0]:
            notes.append("scale=%s" % ",".join("%g" % value for value in scale))
        placements.append({
            "itemId": prop.get("itemId"),
            "def": prop.get("def"),
            "position": position,
            "rotationQuat": quat,
            "scale": scale,
            "aabb": size,
            "volumeK": volume_k,
            "absorbDiameterReqMm": gate_mm,
            "growthMm3": growth,
            "parent": parent,
            "absorbable": absorbable,
            "nonUniform": non_uniform,
            "zone": zone,
            "collider": colliders_by_def.get(prop.get("def")),
            "parts": scaled_parts(parts_by_def.get(prop.get("def")), scale),
            "motion": motion_by_item.get(prop.get("itemId")),
            "worldAabb": world,
            "notes": notes,
            "instance": instance,
            "instanceOrder": int(instance.get("order") or 0),
            "positionSource": position_source,
            "v1Position": [float(value) for value in prop["position"]],
        })

    animators, animator_source = animated_instances(directory, placements)
    solids, scenery = build_structures(placements, boxes_raw, max_diameter_m)
    rectangle, area_info = activity_polygon(terrain, boxes_raw)
    terrain_min_y = min(point[1] for point in terrain["vertices"])
    ground_y = n4(math.floor(terrain_min_y) - 1.0)
    kill_y = n4(ground_y - 1.0)

    document = {
        "schema": SCHEMA_V2,
        "meta": _meta(level_id, level_doc, placements, terrain, solids, rectangle,
                      ground_y, kill_y, max_diameter_m, volume_penalty, time_limit_s,
                      scenery, area_info, zones, animators, animator_source,
                      motion_summary),
        "terrain": None,          # filled below (needs the sidecar hash)
        "structures": {"solids": solids},
        "activityArea": {
            "walls": [{
                "id": "authored_core",
                "points": rectangle,
                "closed": True,
                "heightM": 3.0,
                "thicknessM": 0.2,
                "kind": "invisible",
                "notes": "derived from the extractor's collision footprint + %.1f m margin"
                         % area_info["margin"],
            }],
            "killPlane": {"y": kill_y, "respawn": "ballStart", "zoneId": 0},
            "softMarginM": 0.0,
            "source": "authored",
        },
        "props": [_prop_block(entry) for entry in sorted(placements, key=lambda item: item["itemId"])],
        "defs": _defs(level_doc, original, models_root, model_table, with_colliders,
                      {entry["def"] for entry in placements if entry.get("nonUniform")},
                      colliders_by_def, speed_index_by_def),
        "motions": motions,
        "paths": paths,
        "cameras": [],
        "renderPresets": [],
        "regions": [],
    }
    if level_id == "_ref_cancer":
        apply_cancer_hit_areas(document)
    sidecar, index_width = sidecar_bytes(terrain)
    digest = hashlib.sha256(sidecar).hexdigest()
    document["terrain"] = _terrain_block(terrain, boxes, chunks, index_width, digest)
    if visual is not None:
        with_visual = {}
        for key, value in document["terrain"].items():
            if key == "source":
                with_visual["visual"] = visual["block"]
            with_visual[key] = value
        document["terrain"] = with_visual
    report = {
        "level": level_id,
        "document": DOCUMENT_NAME,
        "terrain": terrain["stats"],
        "chunks": {"count": len(chunks), "limit": CHUNK_TRIANGLES,
                   "triangles": [chunk["triangleRange"][1] for chunk in chunks]},
        "sidecar": {"path": "terrain/" + SIDECAR_NAME, "bytes": len(sidecar),
                    "sha256": digest, "indexWidth": index_width},
        "structures": scenery,
        "activityArea": {"polygon": rectangle, "groundY": ground_y, "killPlaneY": kill_y,
                         "terrainMinY": n4(terrain_min_y)},
        "props": {"total": len(placements),
                  "absorbable": sum(1 for entry in placements if entry["absorbable"]),
                  "neverAbsorbable": sum(1 for entry in placements if not entry["absorbable"]),
                  "withParent": sum(1 for entry in placements if entry["parent"]),
                  "zones": {zone["id"]: sum(1 for entry in placements if entry["zone"] == zone["id"])
                            for zone in zones},
                  "scavenged": scavenged},
        "defs": len(document["defs"]),
        "volumeK": {"min": min(entry["volumeK"] for entry in placements),
                    "max": max(entry["volumeK"] for entry in placements)},
        "expected": document["meta"]["expected"],
        "chunks": {"count": len(chunks),
                   "triangles": [chunk_triangles[0], chunk_triangles[len(chunk_triangles) // 2],
                                 chunk_triangles[-1]],
                   "xzMedianM": round(chunk_xz[len(chunk_xz) // 2], 3),
                   "xzMaxM": round(chunk_xz[-1], 3),
                   "budget": 64, "tileM": CHUNK_TILE_M, "limit": CHUNK_LIMIT},
        "animators": {"count": animators, "source": animator_source},
        "motions": motion_summary,
    }
    return document, sidecar, report


def _meta(level_id, level_doc, placements, terrain, solids, rectangle, ground_y, kill_y,
          max_diameter_m, volume_penalty, time_limit_s, scenery, area_info, zones,
          animators=0, animator_source="none", motions_summary=None):
    level = level_doc.get("level") or {}
    growth_total = sum(entry["growthMm3"] for entry in placements)
    overlap_budget = _overlap_pairs(placements)
    ball_start, spawn_source, spawn_note, player_start = _spawn(level_id, level, terrain,
                                                                placements)
    notes = ("reference rebuild of the original stage; runtime ball cap is the "
             "project global cap, but this level's own design ceiling is %.2f m "
             "(the original's MAX_DIAMETER) and absorbable/structures are decided "
             "against it. caps.animators = %d, source: %s (D-11: one Animator per "
             "animated instance, no VAT). motions: %s"
             % (max_diameter_m, int(animators), animator_source,
                _motion_note(motions_summary)))
    if spawn_note is not None:
        notes = notes + ". " + spawn_note
    meta = {
        "id": level_id,
        "scene": _scene_name(level_id, level),
        "units": "m",
        "convention": "unity-y-up-left-handed-quat-xyzw",
        "ball": {
            "startDiameterM": n4(START_DIAMETER_M),
            "targetDiameterM": n4(0.80),
            "maxDiameterRef": "global",
            "start": vec3(ball_start),
            "spawnSource": spawn_source,
        },
        "timeLimitS": int(round(time_limit_s)) if float(time_limit_s).is_integer()
                      else n4(time_limit_s),
        "playerSpawn": vec3([ball_start[0], n4(ball_start[1] + 0.4), ball_start[2]]),
        "groundY": ground_y,
        "bounds": _bounds(placements),
        "spawnClearanceM": 0.5,
        "zones": [{"id": zone["id"], "code": zone["code"], "kind": zone["kind"]}
                  for zone in zones],
        "arena": {
            "id": level_id,
            "notes": notes,
        },
        "caps": {
            "props": 512,
            "terrainTriangles": 4096,
            "terrainVertices": 8192,
            "staticSolids": 256,
            "activityWalls": 8,
            "movingProps": int((motions_summary or {}).get("movingProps") or 0),
            "animators": int(animators),
        },
        "budget": {"planes": 8, "boxes": 64, "ramps": 4, "blocking": 6,
                   "propSupports": 6},
        "expected": {
            "propCount": len(placements),
            "growthMm3": growth_total,
            "structureCount": len(solids),
            "staticObbs": placement_v2.static_obb_count(solids),
            "overlapBudget": overlap_budget,
            "terrainTriangles": terrain["stats"]["triangles"],
        },
        "source": {
            "kind": "reference",
            "provenance": ref_models.display_path(ref_stage.resolve_stage_dir()[0]),
            "licence": "unlicensed reference data - never distribute, upload or commit",
        },
        "migration": {"from": "katamari.placement/0",
                      "maxDiameterM": n4(max_diameter_m),
                      "volumePenalty": n4(volume_penalty)},
    }
    if player_start is not None:
        # right after playerSpawn, so the two spawn keys read together
        ordered = {}
        for key, value in meta.items():
            ordered[key] = value
            if key == "playerSpawn":
                ordered["playerStart"] = player_start
        meta = ordered
    return meta


#: The importer's menu matches the *active Unity scene name* against ``meta.scene``, so a
#: reference level must name its own scene (``Assets/_RefUnlicensed/Scenes/RefCancer.unity``),
#: not the game mode it plays like.
REFERENCE_SCENES = {"_ref_cancer": "RefCancer"}


def _scene_name(level_id, level):
    """The Unity scene a level is imported into (the menu matches on this)."""
    known = REFERENCE_SCENES.get(level_id)
    if known:
        return known
    return level.get("scene") or "StoryMode"


def _motion_note(summary):
    """One sentence for ``meta.arena.notes``: the 129/128 split, kept visible."""
    if not summary or not summary.get("available"):
        return "none (%s)" % (summary or {}).get("reason", "no motion table")
    return ("%d records with a program, %d rendered props (%d carry props[].motion, %d are "
            "behaviour 'attached'); %d motions[], %d paths[]"
            % (summary["movingRecords"], summary["movingProps"], summary["withMotionField"],
               summary["attached"], summary["motions"], summary["paths"]))


def _bounds(placements, margin=0.0):
    """The authoring frame: every prop's XZ footprint (not the play area, which is
    ``activityArea``).  v1 carried the same box (``[[-26.41,-32.36],[32.47,47.92]]``)."""
    low_x = min(entry["worldAabb"][0][0] for entry in placements) - margin
    low_z = min(entry["worldAabb"][0][2] for entry in placements) - margin
    high_x = max(entry["worldAabb"][1][0] for entry in placements) + margin
    high_z = max(entry["worldAabb"][1][2] for entry in placements) + margin
    return [[n4(low_x), n4(low_z)], [n4(high_x), n4(high_z)]]


#: A reference import has to *choose* a spawn: the original's tables carry no spawn position
#: (ORIGINAL_TERRAIN_AND_LAYOUT §2.4).  The field-centre guess is good enough when the centre
#: happens to be open ground; ``_ref_cancer``'s is not.  Its field centre lands at the edge
#: of a 1.2 x 0.9 m floor slot -- vertical faces 0.006-0.08 m from the ball centre to the
#: sides and behind, a 15 m building wall 0.09 m ahead on the default +z heading, a platform
#: 1.53 m overhead -- so a 0.222 m ball has no free 0.1 m step in any direction.  A
#: field-centre guess would repeat that trap for any level whose centre is not open.
#:
#: The replacement is chosen on the built document (terrain sidecar + props +
#: ``structures.solids`` + ``activityArea``) by ``Tools/levelbuild/spawn_reach.py`` (``scan``):
#: reachability with the props the start diameter cannot absorb as obstacles, ranked by how
#: many absorbable props the ball reaches within 3 m of path.  The rules are in
#: ORIGINAL_TERRAIN_AND_LAYOUT §2.4 and LEVEL_FORMAT_V2 §ball.  ``ground`` pins the terrain
#: face the ball point was chosen on, so a terrain rebuild that moves it fails the build
#: instead of spawning the ball in mid-air or inside a step.
#:
#: ``playerStart`` is the VRChat descriptor spawn (feet position) under OUR rule, not the
#: original's: 2.5 m behind the ball along its initial heading (heading 0 = +z), on a terrain
#: face, 0.4 m free of walls and of props the start diameter cannot absorb, walkable to the
#: seat trigger; when 2.5 m back fails, the nearest feasible point 1.8-3.5 m back within
#: +-30 degrees (``spawn_reach.player_start``).  It is written to the document as
#: ``meta.playerStart`` (``position`` = feet, ``facingDeg``, ``source``); the importer builds
#: ``Markers/PlayerSpawn`` and the world descriptor's spawn from it.  ``meta.playerSpawn``
#: stays ball + 0.4 m: it is the authoring/editor eye point, a different thing.
AUTHORED_SPAWNS = {
    "_ref_cancer": {
        "start": (-5.00, -0.9990, 2.50),         # x, ball-centre y, z (world metres)
        "ground": -1.1100,                       # the flat face under it (garden lawn)
        "why": ("flat garden-lawn face (n.y >= 0.94, 0.6 m disc within 0.05 m of its height) "
                "in the main strongly connected region of the start-diameter graph; "
                "43 props absorbable at the start diameter within 3 m of rolling path and 135 "
                "within 8 m (nearest 0.48 m), the route needs a 0.105 m step at most, nearest "
                "wall/solid 0.54 m, nearest prop box 0.51 m, 6.49 m inside the activity "
                "boundary, 1.5 m of headroom, 67.4 m2 of ground reachable at the start "
                "diameter"),
        "playerStart": {
            "position": (-5.647, -1.0398, 0.0852),   # feet
            "facingDeg": 15.0,                       # towards the ball (heading 0 = +z)
            "distanceM": 2.5,                        # behind the ball; found at +15 degrees
            "source": "our rule (spawn_reach.player_start), not the original's",
        },
    },
}


def _terrain_lookup(terrain):
    """``(x, z, ceiling) -> the highest terrain face at or below the ceiling``."""
    return ref_stage.terrain_lookup([
        (terrain["vertices"][terrain["indices"][index]],
         terrain["vertices"][terrain["indices"][index + 1]],
         terrain["vertices"][terrain["indices"][index + 2]])
        for index in range(0, len(terrain["indices"]), 3)])


def _spawn(level_id, level, terrain, placements):
    """``(ball_start, spawn_source, note, player_start)``: the authored point, else the guess.

    An authored level gets ``spawnSource: "authored"`` and a note for ``meta.arena.notes``,
    plus its ``playerStart`` block when ``AUTHORED_SPAWNS`` carries one (``None`` otherwise);
    everything else keeps the guessed ``fieldCentreGuess`` and has nothing to add.
    """
    authored = AUTHORED_SPAWNS.get(level_id)
    if authored is None:
        return _ball_start(level, terrain, placements), "fieldCentreGuess", None, None
    start = [n4(value) for value in authored["start"]]
    surface = _terrain_lookup(terrain)(
        start[0], start[2], start[1] - START_DIAMETER_M / 2.0 + 1e-4)
    if surface is None or abs(surface - authored["ground"]) > 0.02:
        raise RefV2Error(
            "%s: the authored spawn %r was measured on a face at y=%.4f, but the terrain "
            "under it is now %r -- a terrain change moved the point; re-measure it against "
            "the rules in ORIGINAL_TERRAIN_AND_LAYOUT §2.4 before rebuilding"
            % (level_id, start, authored["ground"], surface))
    player = authored.get("playerStart")
    player_start = None
    if player is not None:
        feet = player["position"]
        floor = _terrain_lookup(terrain)(feet[0], feet[2], feet[1] + 1e-4)
        if floor is None or abs(floor - feet[1]) > 0.02:
            raise RefV2Error(
                "%s: the authored player start %r is not on a terrain face any more (the face "
                "under it is %r); re-run Tools/levelbuild/spawn_reach.py before rebuilding"
                % (level_id, feet, floor))
        player_start = {"position": vec3(feet),
                        "facingDeg": n4(player["facingDeg"]),
                        "source": player["source"]}
    return start, "authored", ("Ball spawn: authored, not a field-centre guess -- "
                               + authored["why"]), player_start


def _ball_start(level, terrain, placements):
    """The fallback spawn: the field centre, on the terrain below it.

    The missions table gives no spawn position (ORIGINAL_TERRAIN §2.4), so this is the
    field-centre guess the format asks a reference import to declare (``fieldCentreGuess``)
    when it has no measured point.  The height comes from a downward ray through the real
    terrain, so the ball starts on the ground rather than inside a prop or 0.8 m above it
    (which is what the v1 file's flattened y did).
    """
    bounds = level.get("bounds") or [[-10.0, -10.0], [10.0, 10.0]]
    centre_x = (bounds[0][0] + bounds[1][0]) / 2.0
    centre_z = (bounds[0][1] + bounds[1][1]) / 2.0
    lookup = _terrain_lookup(terrain)
    footprint = [(entry["position"][0], entry["position"][2],
                  max(entry["aabb"][0], entry["aabb"][2]) / 2.0) for entry in placements]

    def clear(x, z, radius=0.5):
        for prop_x, prop_z, half in footprint:
            if math.hypot(x - prop_x, z - prop_z) < half + radius:
                return False
        return True

    # A spawn inside a prop is not a spawn: walk out from the centre in 0.5 m rings over the
    # collision core until the ball fits, then put it on the terrain under that point.
    for step in range(0, 24):
        for dx, dz in ((0, 0), (step * 0.5, 0), (-step * 0.5, 0), (0, step * 0.5),
                       (0, -step * 0.5), (step * 0.5, step * 0.5),
                       (-step * 0.5, -step * 0.5), (step * 0.5, -step * 0.5),
                       (-step * 0.5, step * 0.5)):
            x, z = centre_x + dx, centre_z + dz
            if not clear(x, z):
                continue
            # Ceiling at 0.8 m: the play band, not the roof of the nearest building (the
            # terrain mesh carries walls and roofs too, and the highest surface at the
            # field centre is a rooftop).
            surface = lookup(x, z, 0.8)
            if surface is None:
                continue
            return [n4(x), n4(surface + 0.12), n4(z)]
    nearest = min(placements, key=lambda entry: math.hypot(
        entry["position"][0] - centre_x, entry["position"][2] - centre_z))
    return [nearest["position"][0], n4(nearest["position"][1] + 0.15),
            nearest["position"][2]]


def _overlap_pairs(placements):
    """How many prop/prop pairs the validator's overlap gate counts.

    It reuses the validator's own helpers (``aabb_intersection``/``aabb_volume``/
    ``prop_parent``/``OVERLAP_FRACTION``) instead of restating the rule, so the declared
    budget is exactly what ``levelbuild.py check`` counts: pairs over 5 % of the smaller
    AABB, minus parent/child pairs.  A dense authentic town is not a design error, so the
    level declares its own count as the budget (the field the format added for exactly this).
    """
    by_id = {entry["itemId"]: entry for entry in placements}
    boxes = {item_id: entry["worldAabb"] for item_id, entry in by_id.items()}
    props = [{"itemId": entry["itemId"], "parent": entry.get("parent")}
             for entry in placements]

    class _View:
        pass

    view = {"props": props}
    ids = sorted(boxes)
    pairs = 0
    for index, left_id in enumerate(ids):
        left_box = boxes[left_id]
        left_volume = core.aabb_volume(left_box)
        for right_id in ids[index + 1:]:
            right_box = boxes[right_id]
            overlap = core.aabb_intersection(left_box, right_box)
            if overlap <= 0.0:
                continue
            smaller = min(left_volume, core.aabb_volume(right_box))
            if smaller <= 0.0 or overlap / smaller <= core.OVERLAP_FRACTION:
                continue
            left = by_id[left_id]
            right = by_id[right_id]
            if left.get("parent") == right_id or right.get("parent") == left_id:
                continue
            pairs += 1
    void = _View, view
    del void
    return pairs


def _prop_block(entry):
    block = {
        "itemId": entry["itemId"],
        "def": entry["def"],
        "kind": "reference",
        "behaviour": "static",          # overwritten below when the instance has a program
        "zone": entry["zone"],
        "position": vec3(entry["position"]),
        "rotationQuat": entry["rotationQuat"],
    }
    if entry["scale"] != [1.0, 1.0, 1.0]:
        block["scale"] = vec3(entry["scale"])
    block["aabb"] = vec3(entry["aabb"])
    block["volumeK"] = n4(entry["volumeK"])
    block["volumePenalty"] = n4(DEFAULT_VOLUME_PENALTY)
    block["absorbDiameterReqMm"] = int(entry["absorbDiameterReqMm"])
    block["growthMm3"] = int(entry["growthMm3"])
    block["absorbable"] = bool(entry["absorbable"])
    block["inherit"] = "full" if entry["parent"] else "none"
    if entry["parent"]:
        block["parent"] = entry["parent"]
    motion = entry.get("motion") or {}
    motion_class = motion.get("class")
    if motion_class in ATTACHED_CLASSES:
        # §6.7.3: dragged_along is the parent relation as data, not a program of its own.
        block["behaviour"] = "attached"
    elif motion_class == "clouds":
        block["behaviour"] = "ambient"
        if motion.get("motionId"):
            block["motion"] = motion["motionId"]
    elif motion_class:
        block["behaviour"] = "motion"
        if motion.get("motionId"):
            block["motion"] = motion["motionId"]
    block["instanceIndex"] = int(entry["instanceOrder"])
    notes = list(entry["notes"])
    if motion_class:
        notes.extend(motion_notes(motion))
    if notes:
        block["notes"] = "; ".join(notes)
    return block


def _terrain_block(terrain, boxes, chunks, index_width, digest):
    zone_counts = {}
    for code in terrain["zoneIds"]:
        zone_counts[str(code)] = zone_counts.get(str(code), 0) + 1
    return {
        "id": TERRAIN_ID,
        "kind": "mesh",
        "winding": "ccw",
        "meshRef": {"path": "terrain/" + SIDECAR_NAME, "sha256": digest,
                    "vertexCount": terrain["stats"]["vertices"],
                    "indexCount": len(terrain["indices"]),
                    "triangleCount": terrain["stats"]["triangles"],
                    "indexWidth": index_width, "zoneIds": True, "classes": True},
        "triangles": {"count": terrain["stats"]["triangles"],
                      "zoneCounts": dict(sorted(zone_counts.items(), key=lambda item: int(item[0])))},
        "broadPhase": {"kind": "aabb", "boxes": boxes},
        "chunks": chunks,
        "grid": grid_for(boxes),
        "materialSegments": [{"id": "collision", "firstIndex": 0,
                              "indexCount": len(terrain["indices"])}],
        "source": {"kind": "reference", "windingFix": "flip",
                   "provenance": "PS2 stage collision lists -> kse_out/collision_area2.json"},
    }


def _defs(level_doc, original, models_root, model_table, with_colliders,
          non_uniform_defs=(), colliders=None, speed_index=None):
    defs = {entry["id"]: entry for entry in original.get("defs") or []}
    models = {entry.get("id"): entry.get("model") for entry in level_doc.get("defs") or []}
    blocks = []
    for def_id in sorted(models):
        source = defs.get(def_id) or {}
        block = {
            "id": def_id,
            "kind": "reference",
            "model": models[def_id],
            "volumeK": n4(def_volume_k(source)),
            "volumePenalty": n4(DEFAULT_VOLUME_PENALTY),
            "scalePolicy": "uniform",
            "notes": "k=1/bboxOverSolidVolume=%s" % (
                source.get("bboxOverSolidVolume") if source.get("bboxOverSolidVolume")
                else "default 0.70"),
        }
        if speed_index and def_id in speed_index:
            block["speedIndex"] = speed_index[def_id]
        collider = (colliders or {}).get(def_id)
        if not with_colliders or def_id in non_uniform_defs:
            collider = None
        if collider is not None:
            block["collider"] = collider
        blocks.append(block)
    return blocks


# --------------------------------------------------------------------------- #
# canonical bytes / sidecar / structural check: the format module owns them
# --------------------------------------------------------------------------- #
#: The §9.2 tags that describe the document's *shape* (keys, ordering, counts, quaternions,
#: sidecar bookkeeping) rather than its design numbers.  ``structural_errors`` keeps just
#: these, so a builder can assert "the file is well formed" without asserting gameplay.
STRUCTURAL_TAGS = ("[schema]", "[schema_legacy]", "[meta_unknown_key]", "[meta_units]",
                   "[meta_convention]", "[meta_id]", "[meta_caps]", "[meta_budget]",
                   "[meta_expected]", "[meta_source]", "[meta_migration]", "[meta_arena]",
                   "[meta_zone_code]", "[meta_bounds]", "[terrain_key]", "[terrain_mesh]",
                   "[terrain_winding]", "[terrain_budget]", "[terrain_zone]",
                   "[terrain_classes]", "[terrain_broadphase]", "[terrain_grid]",
                   "[terrain_segment]", "[terrain_sidecar]", "[terrain_visual]",
                   "[terrain_visual_key]", "[terrain_visual_sidecar]",
                   "[terrain_visual_part]", "[terrain_visual_material]",
                   "[terrain_visual_texture]", "[terrain_visual_winding]", "[structure_key]",
                   "[structure_role]", "[structure_shape]", "[structure_rotation]",
                   "[structure_collision]", "[activity_wall]", "[activity_kill_plane]",
                   "[prop_key]", "[prop_id]", "[prop_def]", "[prop_kind]",
                   "[prop_position]", "[prop_rotation]", "[prop_aabb]", "[prop_absorbable]",
                   "[prop_zone]", "[prop_inherit]", "[prop_parent]", "[def_key]",
                   "[def_model]", "[motion_key]", "[path_key]", "[paths_id]",
                   "[path_points]", "[path_speed]", "[unknown_key]")


def structural_errors(document, sidecar_data=None, visual_files=None):
    """Every *shape* problem in a v2 document (no design numbers).

    A thin filter over :func:`placement_v2.validate` so there is exactly one implementation
    of each shape rule; the model, terrain, prop and def checks all live in the format module.
    """
    report = placement_v2.validate(document, path=None, sidecar_bytes_=sidecar_data,
                                   visual_files=visual_files)
    problems = []
    for message in report.errors:
        if any(message.startswith(tag) for tag in STRUCTURAL_TAGS):
            problems.append(message)
    return problems


# --------------------------------------------------------------------------- #
# writing the pair of files (guarded)
# --------------------------------------------------------------------------- #
def write_outputs(folder, document, sidecar, *, dry_run=False, visual=None):
    """Write ``placement.v2.json`` + ``terrain/<name>.katerrain`` (both path-guarded), and,
    with ``visual``, the ``.katvisual`` sidecar and its PNGs (:func:`ref_stage_visual.write`)."""
    folder = Path(folder)
    document_path = guard_write(folder / DOCUMENT_NAME)
    sidecar_path = guard_write(folder / "terrain" / SIDECAR_NAME)
    text = canonical_text(document)
    if visual is not None:
        ref_stage_visual.write(folder, visual, dry_run=True)       # guard the paths either way
    if dry_run:
        return {"document": document_path, "sidecar": sidecar_path, "written": False,
                "bytes": len(text.encode("utf-8")), "sidecarBytes": len(sidecar)}
    sidecar_path.parent.mkdir(parents=True, exist_ok=True)
    sidecar_path.write_bytes(sidecar)
    if visual is not None:
        ref_stage_visual.write(folder, visual)
    document_path.write_text(text, encoding="utf-8")
    return {"document": document_path, "sidecar": sidecar_path, "written": True,
            "bytes": len(text.encode("utf-8")), "sidecarBytes": len(sidecar),
            "sha256": hashlib.sha256(sidecar).hexdigest()}


# --------------------------------------------------------------------------- #
# v1 view: the repository's validator only speaks katamari.placement/0
# --------------------------------------------------------------------------- #
def to_v1_view(document):
    """A ``katamari.placement/0`` view of a v2 document, for the existing validator.

    ``levelbuild.py check`` implements ~2,000 lines of rules against v1 field names
    (overlap, seating, bounds, budgets, expected totals).  Rather than write a second
    validator, a v2 document is translated into that shape and run through the same rules;
    the translation is lossy on purpose (v2-only fields are dropped, the activity polygon
    becomes the four boundary planes v1 needed, ``meta.groundY`` becomes the ground plane).
    """
    meta = document.get("meta") or {}
    ball = meta.get("ball") or {}
    terrain = document.get("terrain") or {}
    expected = meta.get("expected") or {}
    structures = (document.get("structures") or {}).get("solids") or []
    walls = (document.get("activityArea") or {}).get("walls") or []
    kill = (document.get("activityArea") or {}).get("killPlane") or {}
    view_structures = []
    for solid in structures:
        view_structures.append({
            "id": solid["id"], "kind": solid.get("kind"), "role": "box", "shape": "box",
            "center": list(solid["center"]), "size": list(solid["size"]),
            "rotationQuat": list(solid["rotationQuat"]),
            "collision": "box", "absorbable": False, "support": True,
            "notes": solid.get("notes", ""),
        })
    ground_y = float(meta.get("groundY") or 0.0)
    view_structures.append({
        "id": "ground", "kind": "reference", "role": "ground", "shape": "plane",
        "center": [0.0, ground_y, 0.0],
        "size": [200.0, 0.04, 200.0],
        "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "none", "absorbable": False,
        "support": True,
    })
    for wall in walls:
        points = wall.get("points") or []
        if len(points) < 2:
            continue
        closed = bool(wall.get("closed"))
        pairs = list(zip(points, points[1:] + ([points[0]] if closed else [])))
        for index, (start, end) in enumerate(pairs):
            dx = float(end[0]) - float(start[0])
            dz = float(end[1]) - float(start[1])
            length = math.hypot(dx, dz)
            if length <= 1e-6:
                continue
            # v1 boundary planes are "the plane through centre, local +Y is the normal".
            inward = (-(dz / length), dx / length)
            quat = core.quat_between((0.0, 1.0, 0.0), (inward[0], 0.0, inward[1]))
            view_structures.append({
                "id": "%s_%02d" % (wall.get("id", "wall"), index),
                "kind": "reference", "role": "boundary", "shape": "plane",
                "center": [(float(start[0]) + float(end[0])) / 2.0,
                           float(meta.get("groundY") or 0.0),
                           (float(start[1]) + float(end[1])) / 2.0],
                "size": [n4(length / 2.0), 0.04, n4(float(wall.get("heightM") or 1.0) / 2.0)],
                "rotationQuat": quat4(quat),
                "collision": "none", "absorbable": False, "support": False,
            })
    props = []
    for prop in document.get("props") or []:
        entry = {
            "itemId": prop["itemId"], "def": prop["def"], "kind": prop.get("kind"),
            "zone": prop.get("zone"), "position": list(prop["position"]),
            "rotationQuat": list(prop["rotationQuat"]), "aabb": list(prop["aabb"]),
            "absorbDiameterReq": n4(float(prop["absorbDiameterReqMm"]) / 1000.0),
            "absorbable": bool(prop["absorbable"]),
            "growthMm3": int(prop["growthMm3"]),
            "volumeK": n4(prop["volumeK"]),
            "volumePenalty": n4(prop.get("volumePenalty", DEFAULT_VOLUME_PENALTY)),
        }
        if prop.get("parent"):
            # v1's runtime pointer is ``parent`` (``prop_parent`` reads ``parent`` /
            # ``stackedOn``); ``group``/``groupId`` are the older visual-attach pair and do
            # not carry the extractor's parent table meaning.
            entry["parent"] = prop["parent"]
        if prop.get("notes"):
            entry["notes"] = prop["notes"]
        props.append(entry)
    defs = []
    for entry in document.get("defs") or []:
        block = {"id": entry["id"], "kind": entry.get("kind"), "model": entry.get("model"),
                 "volumeK": entry.get("volumeK"),
                 "volumePenalty": entry.get("volumePenalty")}
        if entry.get("collider"):
            block["collider"] = entry["collider"]
        defs.append(block)
    return {
        "schema": core.SCHEMA,
        "level": {
            "id": meta.get("id"),
            "scene": meta.get("scene"),
            "units": meta.get("units"),
            "convention": meta.get("convention"),
            "ballStart": list(ball.get("start") or [0.0, 0.15, 0.0]),
            "playerSpawn": list(meta.get("playerSpawn") or [0.0, 0.5, 0.0]),
            "ballDiameterStart": ball.get("startDiameterM"),
            "ballDiameterMax": float((meta.get("migration") or {}).get("maxDiameterM")
                                     or DESIGN_MAX_DIAMETER_M),
            "groundY": meta.get("groundY"),
            "bounds": meta.get("bounds"),
            "spawnClearanceM": meta.get("spawnClearanceM"),
            "budget": meta.get("budget"),
            "expected": {key: expected[key] for key in
                         ("propCount", "growthMm3", "overlapBudget") if key in expected},
            "zones": [{"id": zone["id"]} for zone in meta.get("zones") or []],
            "arena": {"id": meta.get("id"), "notes": (meta.get("arena") or {}).get("notes")},
        },
        "structures": view_structures,
        "props": sorted(props, key=lambda prop: prop["itemId"]),
        "defs": sorted(defs, key=lambda entry: entry["id"]),
        "cameras": list(document.get("cameras") or []),
        "renderPresets": list(document.get("renderPresets") or []),
        "regions": list(document.get("regions") or []),
    }


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def build_parser():
    parser = argparse.ArgumentParser(
        prog="ref_v2_from_extract.py",
        description="Build Docs/Design/Levels/_ref_*/placement.v2.json + its .katerrain "
                    "from the stage extraction (UNLICENSED reference data).")
    parser.add_argument("--level", default="_ref_cancer",
                        help="reference level id under Docs/Design/Levels (default _ref_cancer)")
    parser.add_argument("--levels-dir", default=None, help="override Docs/Design/Levels")
    parser.add_argument("--stage-dir", default=None,
                        help="the extraction directory (default $VRC_KATAMARI_REF_STAGE or "
                             "~/KatamariRef/noclip_export/kse_out)")
    parser.add_argument("--write", action="store_true", help="write the files (default: dry run)")
    parser.add_argument("--visual-source", default=None,
                        help="the textured stage.glb for terrain.visual (default %s)"
                             % ref_stage_visual.DEFAULT_SOURCE)
    parser.add_argument("--no-visual", action="store_true",
                        help="leave terrain.visual out (the contact terrain is unaffected)")
    parser.add_argument("--no-colliders", action="store_true",
                        help="skip the per-def collider fit (faster; the fields stay absent)")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    parser.add_argument("--check", action="store_true",
                        help="after building, run the repository validator on the v1 view")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    levels_dir = Path(args.levels_dir) if args.levels_dir else LEVELS_ROOT
    folder = levels_dir / args.level
    try:
        visual = None
        if not args.no_visual:
            source = Path(args.visual_source) if args.visual_source \
                else ref_stage_visual.DEFAULT_SOURCE
            if args.visual_source or source.is_file():
                visual = ref_stage_visual.load(source)
            else:
                print("ref_v2_from_extract: no textured stage.glb at %s; terrain.visual is "
                      "left out" % source, file=sys.stderr)
        document, sidecar, report = build_document(
            args.level, folder, stage_dir=args.stage_dir,
            with_colliders=not args.no_colliders, visual=visual)
        written = write_outputs(folder, document, sidecar, dry_run=not args.write,
                                visual=visual)
    except (RefV2Error, ref_stage.RefStageError, ref_models.RefAssetError, OSError,
            ref_stage_visual.RefVisualError, ValueError, KeyError) as error:
        print("ref_v2_from_extract: %s" % error, file=sys.stderr)
        return 2
    report["written"] = {"document": core.display_path(written["document"]),
                         "sidecar": core.display_path(written["sidecar"]),
                         "bytes": written["bytes"], "sidecarBytes": written["sidecarBytes"],
                         "write": bool(args.write)}
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        text = canonical_text(document)
        report["documentBytes"] = len(text.encode("utf-8"))
        print("ref_v2_from_extract: %s %s (%.1f KB) + %s (%.1f KB)"
              % ("wrote" if args.write else "dry run:", core.display_path(written["document"]),
                 report["documentBytes"] / 1024.0,
                 core.display_path(written["sidecar"]), report["sidecar"]["bytes"] / 1024.0))
        print("  terrain: %d vertices / %d triangles (%d degenerate dropped), %d chunks "
              "of <= %d triangles"
              % (report["terrain"]["vertices"], report["terrain"]["triangles"],
                 report["terrain"]["droppedDegenerate"], report["chunks"]["count"],
                 report["chunks"]["limit"]))
        print("  classes: ride %d / climb %d / wall %d"
              % (report["terrain"]["ride"], report["terrain"]["climb"],
                 report["terrain"]["wall"]))
        print("  props: %d (%d absorbable, %d never, %d with a parent); defs: %d"
              % (report["props"]["total"], report["props"]["absorbable"],
                 report["props"]["neverAbsorbable"], report["props"]["withParent"],
                 report["defs"]))
        print("  structures: %d solids (of %d never-absorbable props; %d are background "
              "outside the collision space); %d split into parts, %d static OBBs"
              % (report["structures"]["withSolid"], report["structures"]["neverAbsorbable"],
                 report["structures"]["background"],
                 report["structures"].get("splitSolids", 0),
                 placement_v2.static_obb_count(document["structures"]["solids"],
                                                 document["meta"].get("areaNo"))))
        print("  original position: %d props moved in x/z (max %.2f m), %d heights "
              "replaced by the original; %d scales applied"
              % (report["props"]["scavenged"]["positionMoved"],
                 report["props"]["scavenged"]["positionMaxShift"],
                 report["props"]["scavenged"].get("heightChanged", 0),
                 report["props"]["scavenged"]["scaleApplied"]))
        print("  activityArea: %s .. %s, groundY %s, kill %s"
              % (report["activityArea"]["polygon"][0], report["activityArea"]["polygon"][2],
                 report["activityArea"]["groundY"], report["activityArea"]["killPlaneY"]))
        print("  sidecar sha256: %s" % report["sidecar"]["sha256"])
        if visual is not None:
            summary = visual["report"]
            print("  terrain.visual: %d parts (%d stage), %d vertices, %d triangles, %d "
                  "materials %s; sidecar sha256 %s"
                  % (summary["parts"], summary["stageParts"], summary["vertices"],
                     summary["triangles"], summary["materials"], summary["blends"],
                     summary["sidecarSha256"]))
    if args.check:
        view = to_v1_view(document)
        result = core.validate(view, path=None)
        for error in result.errors[:20]:
            print("  ! %s" % error)
        print("  validate(v1 view): %d error(s), %d warning(s)"
              % (len(result.errors), len(result.warnings)))
        return 0 if result.ok else 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
