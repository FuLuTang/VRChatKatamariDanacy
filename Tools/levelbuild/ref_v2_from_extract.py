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


def chunk_layout(vertices, indices, limit=CHUNK_TRIANGLES, margin=0.001):
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


def def_collider(defn, models_root, table):
    """A fitted collider for one def, or ``None`` when the model cannot be fitted.

    The extraction's GLBs are raw rips whose axis convention it does not record, so the
    proxy is fitted on the model's *shape* after one uniform scale that keeps it inside the
    declared AABB: the result is conservative (never bigger than the box the placement
    declares) and never guesses a per-axis permutation.
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
                solid["proxySource"] = "def"
                solid["notes"] += "; contact uses the def's %s proxy" % collider.get("type")
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


def build_document(level_id, level_dir, *, stage_dir=None, with_colliders=True,
                   max_diameter_m=DESIGN_MAX_DIAMETER_M, volume_penalty=DEFAULT_VOLUME_PENALTY,
                   time_limit_s=420.0, position_source="instance"):
    """Build ``(document, sidecar, report)`` for one reference level."""
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
    boxes, chunks = chunk_layout(terrain["vertices"], terrain["indices"])
    boxes_raw = collision_boxes(collisions)
    zones, zone_names = zone_table(collisions, level_doc)

    defs_by_id = {entry["id"]: entry for entry in original.get("defs") or []}
    colliders_by_def = {}
    if with_colliders:
        for def_id, defn in defs_by_id.items():
            collider = def_collider(defn, models_root, model_table)
            if collider is not None:
                colliders_by_def[def_id] = collider
    prepared = ref_stage.prepare(directory, folder, level_doc)
    transforms = (prepared.get("transforms") or {}).get("byItemId") or {}

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
            "worldAabb": world,
            "notes": notes,
            "instance": instance,
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
                      scenery, area_info, zones, animators, animator_source),
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
                      colliders_by_def),
        "motions": [],
        "paths": [],
        "cameras": [],
        "renderPresets": [],
        "regions": [],
    }
    sidecar, index_width = sidecar_bytes(terrain)
    digest = hashlib.sha256(sidecar).hexdigest()
    document["terrain"] = _terrain_block(terrain, boxes, chunks, index_width, digest)
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
        "animators": {"count": animators, "source": animator_source},
    }
    return document, sidecar, report


def _meta(level_id, level_doc, placements, terrain, solids, rectangle, ground_y, kill_y,
          max_diameter_m, volume_penalty, time_limit_s, scenery, area_info, zones,
          animators=0, animator_source="none"):
    level = level_doc.get("level") or {}
    growth_total = sum(entry["growthMm3"] for entry in placements)
    overlap_budget = _overlap_pairs(placements)
    ball_start = _ball_start(level, terrain, placements)
    return {
        "id": level_id,
        "scene": level.get("scene") or "StoryMode",
        "units": "m",
        "convention": "unity-y-up-left-handed-quat-xyzw",
        "ball": {
            "startDiameterM": n4(0.222),
            "targetDiameterM": n4(0.80),
            "maxDiameterRef": "global",
            "start": vec3(ball_start),
            "spawnSource": "fieldCentreGuess",
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
            "notes": ("reference rebuild of the original stage; runtime ball cap is the "
                      "project global cap, but this level's own design ceiling is %.2f m "
                      "(the original's MAX_DIAMETER) and absorbable/structures are decided "
                      "against it. caps.animators = %d, source: %s (D-11: one Animator per "
                      "animated instance, no VAT)"
                      % (max_diameter_m, int(animators), animator_source)),
        },
        "caps": {
            "props": 512,
            "terrainTriangles": 4096,
            "terrainVertices": 8192,
            "staticSolids": 256,
            "activityWalls": 8,
            "movingProps": 0,
            "animators": int(animators),
        },
        "budget": {"planes": 8, "boxes": 64, "ramps": 4, "blocking": 6,
                   "propSupports": 6},
        "expected": {
            "propCount": len(placements),
            "growthMm3": growth_total,
            "structureCount": len(solids),
            "staticObbs": len(solids),
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


def _bounds(placements, margin=0.0):
    """The authoring frame: every prop's XZ footprint (not the play area, which is
    ``activityArea``).  v1 carried the same box (``[[-26.41,-32.36],[32.47,47.92]]``)."""
    low_x = min(entry["worldAabb"][0][0] for entry in placements) - margin
    low_z = min(entry["worldAabb"][0][2] for entry in placements) - margin
    high_x = max(entry["worldAabb"][1][0] for entry in placements) + margin
    high_z = max(entry["worldAabb"][1][2] for entry in placements) + margin
    return [[n4(low_x), n4(low_z)], [n4(high_x), n4(high_z)]]


def _ball_start(level, terrain, placements):
    """The original's spawn estimate: the field centre, on the terrain below it.

    The missions table gives no spawn position (ORIGINAL_TERRAIN §2.4), so this is the
    field-centre guess the format asks a reference import to declare (``fieldCentreGuess``).
    The height comes from a downward ray through the real terrain, so the ball starts on the
    ground rather than inside a prop or 0.8 m above it (which is what the v1 file's flattened
    y did).
    """
    bounds = level.get("bounds") or [[-10.0, -10.0], [10.0, 10.0]]
    centre_x = (bounds[0][0] + bounds[1][0]) / 2.0
    centre_z = (bounds[0][1] + bounds[1][1]) / 2.0
    lookup = ref_stage.terrain_lookup([
        (terrain["vertices"][terrain["indices"][index]],
         terrain["vertices"][terrain["indices"][index + 1]],
         terrain["vertices"][terrain["indices"][index + 2]])
        for index in range(0, len(terrain["indices"]), 3)])
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
        "behaviour": "static",
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
    if entry["notes"]:
        block["notes"] = "; ".join(entry["notes"])
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
          non_uniform_defs=(), colliders=None):
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
                   "[terrain_segment]", "[terrain_sidecar]", "[structure_key]",
                   "[structure_role]", "[structure_shape]", "[structure_rotation]",
                   "[structure_collision]", "[activity_wall]", "[activity_kill_plane]",
                   "[prop_key]", "[prop_id]", "[prop_def]", "[prop_kind]",
                   "[prop_position]", "[prop_rotation]", "[prop_aabb]", "[prop_absorbable]",
                   "[prop_zone]", "[prop_inherit]", "[prop_parent]", "[def_key]",
                   "[def_model]", "[motion_key]", "[path_key]", "[paths_id]",
                   "[path_points]", "[path_speed]", "[unknown_key]")


def structural_errors(document, sidecar_data=None):
    """Every *shape* problem in a v2 document (no design numbers).

    A thin filter over :func:`placement_v2.validate` so there is exactly one implementation
    of each shape rule; the model, terrain, prop and def checks all live in the format module.
    """
    report = placement_v2.validate(document, path=None, sidecar_bytes_=sidecar_data)
    problems = []
    for message in report.errors:
        if any(message.startswith(tag) for tag in STRUCTURAL_TAGS):
            problems.append(message)
    return problems


# --------------------------------------------------------------------------- #
# writing the pair of files (guarded)
# --------------------------------------------------------------------------- #
def write_outputs(folder, document, sidecar, *, dry_run=False):
    """Write ``placement.v2.json`` + ``terrain/<name>.katerrain`` (both path-guarded)."""
    folder = Path(folder)
    document_path = guard_write(folder / DOCUMENT_NAME)
    sidecar_path = guard_write(folder / "terrain" / SIDECAR_NAME)
    text = canonical_text(document)
    if dry_run:
        return {"document": document_path, "sidecar": sidecar_path, "written": False,
                "bytes": len(text.encode("utf-8")), "sidecarBytes": len(sidecar)}
    sidecar_path.parent.mkdir(parents=True, exist_ok=True)
    sidecar_path.write_bytes(sidecar)
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
        document, sidecar, report = build_document(
            args.level, folder, stage_dir=args.stage_dir,
            with_colliders=not args.no_colliders)
        written = write_outputs(folder, document, sidecar, dry_run=not args.write)
    except (RefV2Error, ref_stage.RefStageError, ref_models.RefAssetError, OSError) as error:
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
              "outside the collision space)"
              % (report["structures"]["withSolid"], report["structures"]["neverAbsorbable"],
                 report["structures"]["background"]))
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
