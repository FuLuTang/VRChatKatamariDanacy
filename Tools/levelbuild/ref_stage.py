"""The original stage mesh, collision volumes and true instance transforms.

A reference level (``Docs/Design/Levels/_ref_*``) carries the original level's placed
props, but not the level itself: the terrain, the collision volumes, the per-instance
rotations/scales and the real parent table all live in the byte-level extraction of the
retail stage package.  That extraction is **outside the repository** (its outputs are
git-ignored reference data, like the meshes) and this module is the one place that reads
it and converts it into the frame the level editor already draws.

What the extractor gives us, and what it means here (see
``Docs/Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md`` §9-§11):

* ``instances.json`` - one record per placed prop, in the **PS2 game frame** with
  centimetre units.  ``t`` is the translation, ``axis`` (+0x20, three floats) and
  ``angleRaw`` (+0x2C, stored negated) are the rotation, ``scale`` (+0x30) the scale,
  ``nameIdx`` the object type.  The measured mapping to this project's frame (the
  REROLL/Unity frame the placement files use) is ``reroll = -0.01 * ps2`` on all three
  axes, i.e. a point reflection plus a unit change: ``position`` in ``placement.model.json``
  and ``-0.01 * t`` agree for 422 of 511 props within 1 cm (median 0.0 m).
* ``stage.glb`` - the stage triangles, already written in that REROLL frame (metres,
  y-up).  The extractor kept the PS2 index order under a determinant -1 map, so that
  GLB is inside-out as REROLL data (the format note says to flip the winding when
  merging into REROLL).  The viewer's project -> three.js rule is a second reflection,
  so the two compose into a rotation: the served geometry is consistent again and no
  index flip is applied here.
* ``collision_area<N>.json`` - 68 collision entries: an AABB, vertex groups (four floats
  per vertex) and triangles over the group-local vertex indices with a zone id each.
* ``zones.json`` - the 7 zone polygons (a floor slab or a strip), in the same raw frame.

The level editor draws in three.js, whose frame is the project frame with **z negated**
(``three = (x, y, -z)``).  Everything this module hands the browser is therefore already
in that frame; nothing else in the editor has to know about the PS2 units.

Nothing here writes to the repository and nothing here is allowed to load data from
inside it: ``resolve_stage_dir`` refuses a directory under the checkout, exactly like
``ref_models.normalized_models_dir``.
"""

from __future__ import annotations

import json
import math
import os
import struct
from pathlib import Path

from Tools.levelbuild import placement_core as core
from Tools.levelbuild import ref_models

#: Environment override for the extraction output directory (a CLI flag wins over it).
STAGE_DIR_ENV = "VRC_KATAMARI_REF_STAGE"

#: The extractor's current output directory, and the older sibling it superseded.
DEFAULT_STAGE_DIR = Path.home() / "KatamariRef/noclip_export/kse_out"
FALLBACK_DIR_NAME = "out"

#: ``reroll_metres = REROLL_FROM_RAW * ps2_centimetres`` (measured, all three axes).
REROLL_FROM_RAW = -0.01

#: A prop and an instance position closer than this on x/z are "the same placement".
MATCH_TOLERANCE_M = 0.01

#: ``detect_frame`` refuses to call a mapping measured when its median error exceeds this.
FRAME_CONFIDENT_M = 0.5

#: Files the stage payload needs before it is worth offering at all.
REQUIRED_FILES = ("stage.glb", "instances.json", "report.json")

GLB_MAGIC = 0x46546C67
CHUNK_JSON = 0x4E4F534A
CHUNK_BIN = 0x004E4942

COMPONENT_FORMAT = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2),
                    5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
TYPE_WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


class RefStageError(RuntimeError):
    """The extraction is missing, unreadable, or not in the frame we can draw."""


# --------------------------------------------------------------------------- #
# frames
# --------------------------------------------------------------------------- #
def reroll_from_raw(vector):
    """PS2 centimetres (three axes negated by the measured mapping) -> REROLL metres."""
    return [REROLL_FROM_RAW * float(value) for value in vector]


def raw_to_viewer(vector):
    """PS2 centimetres -> three.js frame: ``reroll = -0.01 * raw``, then ``z -> -z``."""
    return [-float(vector[0]) / 100.0, -float(vector[1]) / 100.0, float(vector[2]) / 100.0]


def reroll_to_viewer(vector):
    """REROLL/Unity metres -> three.js frame (the editor's ``toThreePosition``)."""
    return [float(vector[0]), float(vector[1]), -float(vector[2])]


def raw_box_to_viewer(box):
    """A raw cm AABB -> ``(min, max)`` in the viewer frame (min/max swap on two axes)."""
    low = [float(value) for value in (box.get("min") or (0.0, 0.0, 0.0))[:3]]
    high = [float(value) for value in (box.get("max") or (0.0, 0.0, 0.0))[:3]]
    corner_a = raw_to_viewer(low)
    corner_b = raw_to_viewer(high)
    return ([min(corner_a[axis], corner_b[axis]) for axis in range(3)],
            [max(corner_a[axis], corner_b[axis]) for axis in range(3)])


# --------------------------------------------------------------------------- #
# locating the extraction
# --------------------------------------------------------------------------- #
def default_stage_dir() -> Path:
    """``$VRC_KATAMARI_REF_STAGE`` or the extractor's ``kse_out`` directory."""
    value = os.environ.get(STAGE_DIR_ENV)
    return Path(value).expanduser() if value else DEFAULT_STAGE_DIR


def resolve_stage_dir(stage_dir=None):
    """``(directory, reason)`` for the extraction output; ``(None, reason)`` when absent.

    A path **inside the repository** is refused outright (unlicensed reference data may
    not live in the checkout); a path that simply has no ``stage.glb`` yet is not an
    error - the editor keeps working without terrain and says why.
    """
    base = Path(stage_dir if stage_dir is not None else default_stage_dir()).expanduser()
    candidates = [base, base.parent / FALLBACK_DIR_NAME]
    seen = []
    for candidate in candidates:
        try:
            resolved = candidate.resolve(strict=True)
        except OSError:
            continue
        if resolved in seen:
            continue
        seen.append(resolved)
        try:
            resolved.relative_to(core.ROOT.resolve())
        except ValueError:
            pass
        else:
            raise RefStageError(
                "stage extraction directory %s is inside the repository; unlicensed "
                "reference data must stay outside it (e.g. ~/KatamariRef)" % resolved)
        missing = [name for name in REQUIRED_FILES if not (resolved / name).is_file()]
        if missing:
            continue
        return resolved, None
    return None, ("no stage extraction found (looked for %s under %s)"
                  % (", ".join(REQUIRED_FILES), base))


def read_json(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise RefStageError("cannot read %s: %s" % (path, error))


# --------------------------------------------------------------------------- #
# glTF binary: read, transform, rewrite
# --------------------------------------------------------------------------- #
def read_glb(data):
    """``(document, binary)`` of a GLB; raises :class:`RefStageError` otherwise."""
    data = bytes(data)
    if len(data) < 20:
        raise RefStageError("stage.glb is too short to be a GLB")
    magic, version, _length = struct.unpack_from("<III", data, 0)
    if magic != GLB_MAGIC or version != 2:
        raise RefStageError("stage.glb is not a glTF 2.0 binary")
    document = None
    binary = bytearray()
    offset = 12
    while offset + 8 <= len(data):
        chunk_length, chunk_type = struct.unpack_from("<II", data, offset)
        body = data[offset + 8: offset + 8 + chunk_length]
        if chunk_type == CHUNK_JSON:
            document = json.loads(body.decode("utf-8"))
        elif chunk_type == CHUNK_BIN:
            binary = bytearray(body)
        offset += 8 + chunk_length
    if document is None:
        raise RefStageError("stage.glb has no JSON chunk")
    return document, binary


def write_glb(document, binary):
    """A GLB with ``document`` as JSON and ``binary`` as the BIN chunk."""
    payload = json.dumps(document, separators=(",", ":")).encode("utf-8")
    while len(payload) % 4:
        payload += b" "
    blob = bytes(binary)
    while len(blob) % 4:
        blob += b"\0"
    document["buffers"] = [{"byteLength": len(blob)}]
    total = 12 + 8 + len(payload) + 8 + len(blob)
    out = bytearray()
    out += b"glTF"
    out += struct.pack("<II", 2, total)
    out += struct.pack("<I", len(payload)) + b"JSON" + payload
    out += struct.pack("<I", len(blob)) + b"BIN\0" + blob
    return bytes(out)


def _accessor_layout(document, index):
    accessor = document["accessors"][index]
    fmt, size = COMPONENT_FORMAT[accessor["componentType"]]
    width = TYPE_WIDTH[accessor["type"]]
    view = document["bufferViews"][accessor["bufferView"]]
    base = int(view.get("byteOffset") or 0) + int(accessor.get("byteOffset") or 0)
    stride = int(view.get("byteStride") or size * width)
    return accessor, fmt, width, size, base, stride


def accessor_values(document, binary, index):
    """The raw items of one accessor as tuples (no type interpretation)."""
    accessor, fmt, width, _size, base, stride = _accessor_layout(document, index)
    count = int(accessor["count"])
    items = []
    for item in range(count):
        offset = base + item * stride
        items.append(struct.unpack_from("<%d%s" % (width, fmt), binary, offset))
    return items


def store_accessor(document, binary, index, values):
    """Write float tuples back into the same accessor (size-preserving)."""
    accessor, fmt, width, _size, base, stride = _accessor_layout(document, index)
    if fmt not in ("f", "d"):
        raise RefStageError("accessor %d is not a float accessor" % index)
    if len(values) != int(accessor["count"]):
        raise RefStageError("accessor %d changed its item count" % index)
    for item, value in enumerate(values):
        offset = base + item * stride
        struct.pack_into("<%d%s" % (width, fmt), binary, offset, *[float(v) for v in value])


def _mirror_z(document, binary, index, mark_positions):
    """Negate z on a VEC3 float accessor; returns the new (min, max) when asked to."""
    values = [(item[0], item[1], -item[2]) for item in accessor_values(document, binary, index)]
    store_accessor(document, binary, index, values)
    if not mark_positions:
        return None
    mins = [min(item[axis] for item in values) for axis in range(3)]
    maxs = [max(item[axis] for item in values) for axis in range(3)]
    return mins, maxs


def convert_stage_glb(data):
    """The extractor's ``stage.glb`` in the viewer frame.

    * positions and normals get ``z -> -z`` (the editor's project -> three.js rule);
    * the index order is **kept**.  The extractor already applied a determinant -1 map
      (``reroll = -0.01 * ps2``) without flipping the winding, so one reflection is owed;
      this conversion is exactly that reflection.  Two reflections compose to a
      rotation, so the served geometry is not mirrored and its winding agrees with its
      normals again (14,672 of 14,734 triangles, measured).  The ``stage.glb`` itself is
      inside-out when loaded directly as REROLL data;
    * ``min``/``max`` of every POSITION accessor are recomputed (a stale bounding box
      would let the viewer cull the terrain away).
    """
    document, binary = read_glb(data)
    mirrored = set()
    for mesh in document.get("meshes") or []:
        for primitive in mesh.get("primitives") or []:
            attributes = primitive.get("attributes") or {}
            for name, is_position in (("POSITION", True), ("NORMAL", False)):
                index = attributes.get(name)
                if index is None or index in mirrored:
                    continue
                bounds = _mirror_z(document, binary, index, is_position)
                if bounds is not None:
                    accessor = document["accessors"][index]
                    accessor["min"] = list(bounds[0])
                    accessor["max"] = list(bounds[1])
                mirrored.add(index)
    document["asset"] = dict(document.get("asset") or {}, version="2.0")
    return write_glb(document, binary)


_GLB_CACHE = {}


def stage_glb_bytes(stage_dir):
    """Converted ``stage.glb`` bytes for one extraction directory (cached per mtime)."""
    path = Path(stage_dir) / "stage.glb"
    try:
        stat = path.stat()
    except OSError:
        raise RefStageError("stage.glb disappeared from %s" % stage_dir)
    key = (str(path), stat.st_mtime_ns, stat.st_size)
    if key not in _GLB_CACHE:
        _GLB_CACHE.clear()
        _GLB_CACHE[key] = convert_stage_glb(path.read_bytes())
    return _GLB_CACHE[key]


# --------------------------------------------------------------------------- #
# the instance table
# --------------------------------------------------------------------------- #
def mono_table(level_dir):
    """``{monoNameIndex: def id}`` from the level's own ``original_defs.json``."""
    path = Path(level_dir) / "original_defs.json"
    if not path.is_file():
        return {}
    document = read_json(path)
    table = {}
    for entry in document.get("defs") or []:
        if not isinstance(entry, dict):
            continue
        mono = entry.get("mono")
        def_id = entry.get("id")
        if isinstance(mono, int) and isinstance(def_id, str) and def_id:
            table[mono] = def_id
    return table


def instance_position(record, scale=REROLL_FROM_RAW):
    """A record's translation in REROLL metres for the measured ``scale``."""
    return [scale * float(value) for value in record.get("t") or (0.0, 0.0, 0.0)]


def _xz_distance(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def detect_frame(instances, doc, mono=None):
    """Which ``t -> world`` mapping the instance table is in, measured against the props.

    Returns ``{scale, matched, exact, medianError, confident, hypothesis}``.  The
    expected answer is ``-0.01`` (all three axes negated, centimetres -> metres);
    ``+0.01`` is the mirror the reference viewer draws and must never win.
    """
    mono = mono if mono is not None else {}
    props_by_def = {}
    for prop in doc.get("props") or []:
        if isinstance(prop, dict) and isinstance(prop.get("position"), list):
            props_by_def.setdefault(prop.get("def"), []).append(prop)
    by_def = {}
    for record in instances:
        def_id = mono.get(record.get("nameIdx"))
        if def_id is None:
            continue
        by_def.setdefault(def_id, []).append(record)

    def measure(scale):
        errors = []
        for def_id, records in by_def.items():
            props = props_by_def.get(def_id) or []
            if not props:
                continue
            for record in records:
                position = instance_position(record, scale)
                nearest = min((_xz_distance(position, prop["position"]) for prop in props),
                              default=None)
                if nearest is not None:
                    errors.append(nearest)
        if not errors:
            return {"scale": scale, "matched": 0, "exact": 0, "medianError": None,
                    "confident": False}
        errors.sort()
        median = errors[len(errors) // 2]
        return {"scale": scale, "matched": len(errors),
                "exact": sum(1 for value in errors if value <= MATCH_TOLERANCE_M),
                "medianError": median, "confident": median <= FRAME_CONFIDENT_M}

    candidates = [measure(REROLL_FROM_RAW), measure(-REROLL_FROM_RAW)]
    candidates.sort(key=lambda entry: (entry["medianError"] is None,
                                       entry["medianError"] or 0.0))
    best = dict(candidates[0])
    best["hypothesis"] = ("all three axes negated, centimetres -> metres"
                          if best["scale"] == REROLL_FROM_RAW else "axes negated, NOT mirrored")
    return best


def match_instances(doc, instances, scale=REROLL_FROM_RAW, mono=None):
    """Pair every prop with the instance it was converted from.

    Within one def the pairing is a greedy nearest match on x/z (the y of an instance
    is its origin while a placement prop is positioned by its AABB centre, so y cannot
    take part).  An instance whose ``nameIdx`` has no def in this level is a dummy
    attach anchor, not a prop.
    """
    mono = mono if mono is not None else {}
    instances = list(instances)
    pairs = []
    anchors = []
    unknown = {}
    for order, record in enumerate(instances):
        def_id = mono.get(record.get("nameIdx"))
        if def_id is None:
            anchors.append({"order": order, "nameIdx": record.get("nameIdx"),
                            "position": reroll_to_viewer(instance_position(record, scale)),
                            "tableIndex": record.get("tableIndex"),
                            "area": record.get("area")})
            continue
        unknown.setdefault(def_id, []).append((order, record))
    props_by_def = {}
    for prop in doc.get("props") or []:
        if isinstance(prop, dict):
            props_by_def.setdefault(prop.get("def"), []).append(prop)
    matches = []
    unmatched_props = []
    for def_id, entries in unknown.items():
        props = props_by_def.get(def_id) or []
        candidates = []
        for order, record in entries:
            position = instance_position(record, scale)
            for prop in props:
                candidates.append((_xz_distance(position, prop["position"]), order, prop))
        candidates.sort(key=lambda entry: (entry[0], entry[1], entry[2].get("itemId") or ""))
        used_props = set()
        used_orders = set()
        for distance, order, prop in candidates:
            item_id = prop.get("itemId")
            if item_id in used_props or order in used_orders:
                continue
            used_props.add(item_id)
            used_orders.add(order)
            matches.append({"itemId": item_id, "def": def_id, "order": order,
                            "instance": instances[order], "distance": distance})
        for prop in props:
            if prop.get("itemId") not in used_props:
                unmatched_props.append(prop.get("itemId"))
        for order, _record in entries:
            if order not in used_orders:
                anchors.append({"order": order, "nameIdx": instances[order].get("nameIdx"),
                                "position": reroll_to_viewer(
                                    instance_position(instances[order], scale)),
                                "tableIndex": instances[order].get("tableIndex"),
                                "area": instances[order].get("area"),
                                "reason": "no prop of this def is left to pair with"})
    exact = sum(1 for match in matches if match["distance"] <= MATCH_TOLERANCE_M)
    return {"matches": matches, "anchors": anchors, "unmatchedProps": unmatched_props,
            "exact": exact, "counts": {"instances": len(instances),
                                       "props": len(doc.get("props") or []),
                                       "matched": len(matches), "exact": exact,
                                       "anchors": len(anchors),
                                       "unmatchedProps": len(unmatched_props)}}


def instance_quaternion(record):
    """Rotation from ``axis`` (+0x20) and the **negated** ``angleRaw`` (+0x2C).

    A point reflection conjugates a rotation to itself, so the same axis/angle is valid
    in the REROLL frame without any sign juggling beyond the stored angle's own sign.
    """
    axis = [float(value) for value in record.get("axis") or (0.0, 0.0, 1.0)]
    angle = -float(record.get("angleRaw") or 0.0)
    if not any(axis):
        return [0.0, 0.0, 0.0, 1.0]
    return list(core.quat_axis_angle(axis, angle))


def angle_degrees(record):
    """The applied rotation in degrees, folded into ``(-180, 180]``.

    A record with a zero-length axis is the identity whatever its angle field says, so
    it reports 0 (``instance_quaternion`` returns the identity for it).
    """
    if not any(float(value) for value in record.get("axis") or ()):
        return 0.0
    angle = -math.degrees(float(record.get("angleRaw") or 0.0)) % 360.0
    if angle > 180.0:
        angle -= 360.0
    return angle


# --------------------------------------------------------------------------- #
# collision volumes + zones
# --------------------------------------------------------------------------- #
def collision_entries(collision_lists):
    entries = []
    for group in collision_lists or []:
        for entry in group or []:
            if isinstance(entry, dict):
                entries.append(entry)
    return entries


def entry_triangles(entry):
    """``[(a, b, c, zone)]`` in **viewer** coordinates for one collision entry.

    The extractor writes the entry's vertices as one flat list of four-float items but
    the triangle indices are local to their vertex group, so the group layout has to be
    walked to place the index into that flat list.  ``is_strip`` groups contribute
    ``n - 2`` triangles and polygon groups ``n // 3``, in file order.
    """
    verts = [float(value) for value in entry.get("verts") or ()]
    triangles = entry.get("triangles") or []
    out = []
    cursor = 0
    vertex_offset = 0
    for group in entry.get("groups") or []:
        count = int(group.get("vertex_count") or 0)
        produced = max(0, count - 2) if group.get("is_strip") else count // 3
        for triangle in triangles[cursor:cursor + produced]:
            if len(triangle) < 3:
                continue
            points = []
            for local in (int(triangle[0]), int(triangle[1]), int(triangle[2])):
                start = (vertex_offset + local) * 4
                chunk = verts[start:start + 3]
                if len(chunk) != 3:
                    points = []
                    break
                points.append(raw_to_viewer(chunk))
            if len(points) != 3:
                continue
            zone = int(triangle[3]) if len(triangle) > 3 else -1
            out.append((points[0], points[1], points[2], zone))
        cursor += produced
        vertex_offset += count
    return out


def collision_payload(collision_lists):
    """AABBs and a triangle soup in the viewer frame, for the browser's line overlay."""
    entries = collision_entries(collision_lists)
    boxes = []
    triangles = []
    zones = []
    for entry in entries:
        box = entry.get("bbox") or {}
        if box.get("min") and box.get("max"):
            low, high = raw_box_to_viewer(box)
            boxes.append({"min": [round(value, 4) for value in low],
                          "max": [round(value, 4) for value in high]})
        for first, second, third, zone in entry_triangles(entry):
            triangles.extend(round(value, 4) for point in (first, second, third)
                             for value in point)
            zones.append(zone)
    return {"aabbs": boxes, "triangles": triangles, "triangleZones": zones,
            "counts": {"entries": len(entries), "aabbs": len(boxes),
                       "triangles": len(triangles) // 9}}


def zone_boxes(zones):
    """``zones.json`` -> ``[{id, min, max}]`` in the viewer frame."""
    out = []
    for index, zone in enumerate(zones or []):
        box = (zone or {}).get("bbox") or {}
        if not box.get("min") or not box.get("max"):
            continue
        low, high = raw_box_to_viewer(box)
        out.append({"id": index, "min": [round(value, 4) for value in low],
                    "max": [round(value, 4) for value in high]})
    return out


def _ray_down_hits(point, first, second, third):
    """Height of the triangle under ``point`` (viewer frame), or ``None``."""
    origin_x, origin_z = point[0], point[2]
    ax, ay, az = first
    bx, by, bz = second
    cx, cy, cz = third
    denominator = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
    if abs(denominator) < 1e-12:
        return None
    l1 = ((bz - cz) * (origin_x - cx) + (cx - bx) * (origin_z - cz)) / denominator
    l2 = ((cz - az) * (origin_x - cx) + (ax - cx) * (origin_z - cz)) / denominator
    l3 = 1.0 - l1 - l2
    if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
        return None
    return l1 * ay + l2 * by + l3 * cy


def zone_lookup(collision_lists, cell=4.0):
    """A grid over the collision triangles: ``(x, z) -> highest surface below it``.

    The renderer's zone id is carried by the collision triangle of the surface the prop
    stands on (the zone polygons themselves are flat floors, so a bbox containment test
    misses almost every prop).  Returns a callable ``lookup(x, y, z) -> (zone, surfaceY)``.
    """
    buckets = {}
    for entry in collision_entries(collision_lists):
        for first, second, third, zone in entry_triangles(entry):
            low_x = min(first[0], second[0], third[0])
            high_x = max(first[0], second[0], third[0])
            low_z = min(first[2], second[2], third[2])
            high_z = max(first[2], second[2], third[2])
            for cell_x in range(int(math.floor(low_x / cell)), int(math.floor(high_x / cell)) + 1):
                for cell_z in range(int(math.floor(low_z / cell)),
                                    int(math.floor(high_z / cell)) + 1):
                    buckets.setdefault((cell_x, cell_z), []).append(
                        (first, second, third, zone))

    def lookup(x, y, z):
        key = (int(math.floor(x / cell)), int(math.floor(z / cell)))
        best = None
        for first, second, third, zone in buckets.get(key, ()):
            height = _ray_down_hits((x, y, z), first, second, third)
            if height is None or height > y + 1e-6:
                continue
            if best is None or height > best[1]:
                best = (zone, height)
        return best

    return lookup


# --------------------------------------------------------------------------- #
# the whole payload
# --------------------------------------------------------------------------- #
def load_instance_document(stage_dir):
    """The whole ``instances.json`` payload (``instances`` plus the area table)."""
    document = read_json(Path(stage_dir) / "instances.json")
    if not isinstance(document.get("instances"), list):
        raise RefStageError("instances.json has no instance list")
    return document


def load_instances(stage_dir):
    return load_instance_document(stage_dir)["instances"]


def _report(stage_dir):
    document = read_json(Path(stage_dir) / "report.json")
    return document.get("space"), document


def collision_file(stage_dir, first_area=None):
    """The collision list for the stage area the level actually uses.

    The extractor writes one ``collision_area<N>.json`` per active area and the older
    runs left copies behind (``collision_area0.json`` here is byte-identical to
    ``collision_area2.json``), so the choice is by the level's own first area, then by
    the conventional order, never by directory listing order.
    """
    directory = Path(stage_dir)
    names = []
    if isinstance(first_area, int):
        names.append("collision_area%d.json" % first_area)
    names.extend(["collision_area2.json", "collision_area1.json",
                  "collision_area0.json", "collision_area3.json"])
    for name in names:
        if (directory / name).is_file():
            return directory / name
    found = sorted(directory.glob("collision_area*.json"))
    return found[0] if found else None


def terrain_triangles(stage_dir, level_diagonal=None):
    """The stage's triangles in the viewer frame, without the far scenery.

    A mesh whose bounding box is more than twice the level's diagonal is the sky shell
    or the distant coast: it is around the map, not the ground under it, so a downward
    ray must not stop on it.
    """
    document, binary = read_glb(stage_glb_bytes(stage_dir))
    limit = 2.0 * level_diagonal if level_diagonal else None
    triangles = []
    skipped = 0
    for mesh in document.get("meshes") or []:
        primitives = mesh.get("primitives") or []
        if not primitives:
            continue
        position_index = (primitives[0].get("attributes") or {}).get("POSITION")
        if position_index is None:
            continue
        accessor = document["accessors"][position_index]
        if limit is not None and accessor.get("min") and accessor.get("max"):
            size = [accessor["max"][axis] - accessor["min"][axis] for axis in range(3)]
            if math.sqrt(sum(value * value for value in size)) > limit:
                skipped += 1
                continue
        points = accessor_values(document, binary, position_index)
        for primitive in primitives:
            indices = [item[0] for item in accessor_values(
                document, binary, primitive["indices"])]
            for offset in range(0, len(indices) - 2, 3):
                triangle = (points[indices[offset]], points[indices[offset + 1]],
                            points[indices[offset + 2]])
                if any(len(point) != 3 for point in triangle):
                    continue
                triangles.append(triangle)
    return triangles, skipped


def terrain_lookup(triangles, cell=4.0):
    """``(x, z, ceiling) -> highest triangle height at or below the ceiling``."""
    buckets = {}
    for first, second, third in triangles:
        low_x = min(first[0], second[0], third[0])
        high_x = max(first[0], second[0], third[0])
        low_z = min(first[2], second[2], third[2])
        high_z = max(first[2], second[2], third[2])
        for cell_x in range(int(math.floor(low_x / cell)), int(math.floor(high_x / cell)) + 1):
            for cell_z in range(int(math.floor(low_z / cell)),
                                int(math.floor(high_z / cell)) + 1):
                buckets.setdefault((cell_x, cell_z), []).append((first, second, third))

    def lookup(x, z, ceiling=None):
        best = None
        for first, second, third in buckets.get(
                (int(math.floor(x / cell)), int(math.floor(z / cell))), ()):
            height = _ray_down_hits((x, 0.0, z), first, second, third)
            if height is None:
                continue
            if ceiling is not None and height > ceiling:
                continue
            if best is None or height > best:
                best = height
        return best

    return lookup


_TERRAIN_CACHE = {}


def _terrain_triangles_cached(stage_dir, diagonal):
    path = Path(stage_dir) / "stage.glb"
    try:
        stat = path.stat()
    except OSError:
        return [], 0
    key = (str(path), stat.st_mtime_ns, stat.st_size,
           None if diagonal is None else round(diagonal, 3))
    if key not in _TERRAIN_CACHE:
        _TERRAIN_CACHE.clear()
        _TERRAIN_CACHE[key] = terrain_triangles(stage_dir, diagonal)
    return _TERRAIN_CACHE[key]


def contact_report(stage_dir, prepared, doc, tolerance=0.1):
    """How well the props stand on the extracted stage.

    For every prop the ray goes straight down from just above its AABB bottom, so the
    measurement is "is there ground within ``tolerance`` of where this prop's feet are".
    Two heights are reported for the same prop: the file's own ``y`` (which the
    reference placement flattened onto y = 0) and the original instance's ``y`` (which is
    what the viewer draws when ``原作高度`` is on).
    """
    empty = {"props": 0, "withTerrain": 0, "tolerance": tolerance,
             "current": None, "fileHeight": None, "rows": []}
    if stage_dir is None or not prepared or not prepared.get("available"):
        return empty
    transforms = (prepared.get("transforms") or {}).get("byItemId") or {}
    properties = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    xs = [float(prop["position"][0]) for prop in properties
          if isinstance(prop.get("position"), list)]
    zs = [float(prop["position"][2]) for prop in properties
          if isinstance(prop.get("position"), list)]
    bounds = (doc.get("level") or {}).get("bounds")
    if (isinstance(bounds, list) and len(bounds) == 2
            and all(isinstance(point, list) and len(point) == 2 for point in bounds)):
        diagonal = math.hypot(float(bounds[1][0]) - float(bounds[0][0]),
                              float(bounds[1][1]) - float(bounds[0][1]))
    elif xs and zs:
        diagonal = math.hypot(max(xs) - min(xs), max(zs) - min(zs))
    else:
        diagonal = None
    triangles, skipped = _terrain_triangles_cached(stage_dir, diagonal)
    lookup = terrain_lookup(triangles)
    rows = []
    for prop in properties:
        position = prop.get("position")
        if not isinstance(position, list) or len(position) != 3:
            continue
        height = float((prop.get("aabb") or [0, 0, 0])[1] or 0.0)
        x, y, z = (float(value) for value in position)
        instance = transforms.get(prop.get("itemId")) or {}
        instance_y = (instance.get("position") or [None, None, None])[1]
        bottom_file = y - height / 2.0
        bottom_instance = (instance_y - height / 2.0) if instance_y is not None else None
        ceiling = max([value for value in (bottom_file, bottom_instance, y)
                       if value is not None]) + 0.5
        surface = lookup(x, -z, ceiling)
        if surface is None:
            continue
        rows.append({
            "id": prop.get("itemId"), "def": prop.get("def"),
            "terrainY": round(surface, 4),
            "bottomFile": round(bottom_file, 4),
            "bottomInstance": (None if bottom_instance is None
                               else round(bottom_instance, 4)),
            "deltaFile": round(bottom_file - surface, 4),
            "deltaInstance": (None if bottom_instance is None
                              else round(bottom_instance - surface, 4)),
        })
    current_key = "deltaInstance"
    other_key = "deltaFile"
    measured = [row for row in rows if row[current_key] is not None]
    if not measured:
        current_key, other_key = "deltaFile", "deltaInstance"
        measured = [row for row in rows if row[current_key] is not None]

    def summarise(key):
        values = sorted(row[key] for row in measured if row.get(key) is not None)
        if not values:
            return {"within": 0, "fraction": 0.0, "median": None, "p10": None,
                    "p90": None, "min": None, "max": None}
        within = sum(1 for value in values if abs(value) <= tolerance)
        return {"within": within, "fraction": round(within / len(values), 4),
                "median": round(values[len(values) // 2], 4),
                "p10": round(values[int(len(values) * 0.1)], 4),
                "p90": round(values[int(len(values) * 0.9)], 4),
                "min": round(values[0], 4), "max": round(values[-1], 4)}

    return {"props": len(properties), "withTerrain": len(rows), "tolerance": tolerance,
            "heightMode": "instance" if current_key == "deltaInstance" else "file",
            "current": summarise(current_key), "fileHeight": summarise(other_key),
            "terrainTriangles": len(triangles), "skippedMeshes": skipped,
            "rows": rows}


def prepare(stage_dir, level_dir, doc):
    """Everything the viewer needs about the original stage for one level.

    ``stage_dir`` may be ``None`` (see :func:`resolve_stage_dir`); the result always has
    ``available``, ``reason`` and ``counts`` so the banner can be honest about why the
    terrain is or is not there.
    """
    if stage_dir is None:
        return {"available": False, "reason": "no stage extraction configured",
                "dir": None, "counts": {}, "frame": None, "transforms": None,
                "collision": None, "zones": []}
    space, report = _report(stage_dir)
    if space != "reroll":
        raise RefStageError(
            "the extraction at %s was written in %r space; the placement files are in "
            "the REROLL frame, so it cannot be drawn over them (re-run the extractor "
            "with --space reroll)" % (stage_dir, space))
    instances_document = load_instance_document(stage_dir)
    instances = instances_document["instances"]
    mono = mono_table(level_dir)
    frame = detect_frame(instances, doc, mono)
    matched = match_instances(doc, instances, frame["scale"], mono)
    collisions = read_json(collision_file(stage_dir, instances_document.get("firstArea")))
    collision = collision_payload(collisions)
    zones = zone_boxes(read_json(Path(stage_dir) / "zones.json"))
    lookup = zone_lookup(collisions)
    order_by_index = {}
    for order, record in enumerate(instances):
        if record.get("tableIndex") is not None:
            order_by_index[(record.get("area"), record.get("tableIndex"))] = order

    by_instance = {}
    by_item = {}
    for match in matched["matches"]:
        record = match["instance"]
        position = instance_position(record, frame["scale"])
        parent_instance = record.get("parentTableIndex")
        parent_order = None
        if parent_instance is not None:
            parent_order = order_by_index.get((record.get("area"), parent_instance))
        # The collision triangles are in the viewer frame (z negated), the record is in
        # the REROLL frame, so the ray has to be cast in the frame the mesh is drawn in.
        viewer = reroll_to_viewer(position)
        zone = lookup(viewer[0], viewer[1] + 0.5, viewer[2])
        by_instance[str(match["order"])] = match["itemId"]
        by_item[match["itemId"]] = {
            "instance": record.get("tableIndex"),
            "area": record.get("area"),
            "order": match["order"],
            "position": [round(value, 4) for value in position],
            "quat": [round(value, 6) for value in instance_quaternion(record)],
            "angleDeg": round(angle_degrees(record), 3),
            "axis": [round(float(value), 4) for value in record.get("axis") or ()],
            "scale": [round(float(value), 6) for value in record.get("scale") or ()],
            "parent": (None if parent_order is None
                       else by_instance.get(str(parent_order))),
            "parentInstance": parent_instance,
            "zone": None if zone is None else zone[0],
            "zoneSurfaceY": None if zone is None else round(zone[1], 4),
            "distance": round(match["distance"], 6),
        }
    # A parent can appear after its children in the table: resolve those in a second pass.
    for entry in by_item.values():
        if entry["parent"] is None and entry["parentInstance"] is not None:
            order = order_by_index.get((entry["area"], entry["parentInstance"]))
            entry["parent"] = (by_instance.get(str(order)) if order is not None else None)
    entries = list(by_item.values())
    counts = dict(matched["counts"])
    counts.update({
        "rotated": sum(1 for entry in entries if entry["angleDeg"]),
        "scaled": sum(1 for entry in entries if entry["scale"] != [1.0, 1.0, 1.0]),
        "withParent": sum(1 for entry in entries if entry["parentInstance"] is not None),
        "parentsResolved": sum(1 for entry in entries if entry["parent"]),
        "withZone": sum(1 for entry in entries if entry["zone"] is not None),
        "zones": len(zones),
        "aabbs": collision["counts"]["aabbs"],
        "triangles": collision["counts"]["triangles"],
        "meshVertices": (report.get("mesh") or {}).get("vertices"),
        "meshMaterials": (report.get("mesh") or {}).get("materials"),
        "stageBytes": (report.get("mesh") or {}).get("glbBytes"),
    })
    links = [{"child": item_id, "parent": entry["parent"],
              "parentInstance": entry["parentInstance"],
              "expected": bool(entry["parent"])}
             for item_id, entry in by_item.items()
             if entry["parentInstance"] is not None]
    return {"available": True, "reason": None,
            "dir": ref_models.display_path(stage_dir), "counts": counts, "frame": frame,
            "transforms": {"byItemId": by_item, "byInstance": by_instance,
                           "parents": links, "anchors": matched["anchors"],
                           "unmatchedProps": matched["unmatchedProps"],
                           "counts": counts, "frame": frame},
            "collision": collision, "zones": zones}
