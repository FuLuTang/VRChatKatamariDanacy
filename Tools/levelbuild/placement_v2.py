"""The ``katamari.placement/2`` validator (LEVEL_FORMAT_V2 §9).

One entry point — :func:`validate` — that takes a v2 document and returns a
:class:`placement_core.Report` with stable ``[tag]`` errors and warnings.  It is the
sibling of :func:`placement_core.validate` (v1), not a copy of it:

* every *number* rule is the same function v1 uses — ``absorb_diameter_req_mm``,
  ``item_growth_mm3``, ``volume_k_error``, ``volume_penalty_error``, ``world_aabb``,
  ``overlap_scan``, ``seat_verdict``, ``collider_schema_errors``;
* what this module adds is the **v2 layer semantics** (§2-§8: meta/terrain/structures/
  activityArea/props/defs/motions/paths) and the §9.2 tag set, each message carrying
  ``[tag] path: expected …, got …``;
* the canonical writer (:func:`canonical_text`) and the ``.katerrain`` codec
  (:func:`sidecar_bytes`/:func:`read_sidecar`) live here too, because they are part of the
  format — the reference builder imports them rather than keeping its own copies.

A report is green only when the *document* is sound.  Three things a dense reference level
legitimately carries are reported as notes instead of errors, because the format says so:
``expected.overlapBudget`` (a declared budget of prop/prop pairs), props whose AABBs sit
inside scenery solids (v2 has no rule for that — §9.2 only covers prop/prop), and
``absorbable`` disagreeing with the gate (in v2 ``absorbable`` is an authorial declaration,
not a derived column).
"""

from __future__ import annotations

import hashlib
import json
import math
import struct
from pathlib import Path

from Tools.levelbuild import collider_proxy
from Tools.levelbuild import placement_core as core

SCHEMA = "katamari.placement/2"
SCHEMA_V1 = core.SCHEMA

#: The owner's global ball cap (2026-11-16).  Only used to resolve ``maxDiameterRef`` when
#: nothing narrower is declared; a level's own design ceiling wins for wall/absorb checks.
GLOBAL_MAX_DIAMETER_M = 20.0

#: Triangles per terrain chunk and the Udon single-chunk vertex limit (§3.5).
CHUNK_TRIANGLES = 512
CHUNK_VERTICES = 1536

#: Sidecar magic / version / flags (§8.3) and the u16 index width limit.
SIDECAR_NAME = "cancer.katerrain"
TERRAIN_ID = "cancer_ground"
KATERRAIN_MAGIC = b"KTRN"
KATERRAIN_VERSION = 1
KATERRAIN_FLAG_ZONES = 0x1
KATERRAIN_FLAG_CLASSES = 0x2
U16_VERTEX_LIMIT = 65536

#: Grid cell range §3.3 allows.
GRID_CELL_MIN = 0.5
GRID_CELL_MAX = 4.0

#: A prop may leave ``meta.bounds`` by this much before ``[prop_bounds]`` fires.
BOUNDS_TOLERANCE_M = 0.05

#: Detail lines for the per-pair overlap rule when no budget is declared.
DETAIL_LIMIT = 24

#: ``motions[].class`` names (§16, D-10) and their ``updatesPerSecond`` guidance (§6.7.5).
MOTION_CLASSES = ("sporadic_walk", "hop", "sway", "dragged_along", "wait_for_player",
                  "whack_a_mole", "path_collision", "spin", "clouds", "flip", "bob",
                  "sloping_path", "back_and_forth")
PATH_CLASSES = ("path_collision", "sloping_path", "back_and_forth")
#: The §6.7.3/§6.7.5 guidance: ``hop`` and ``whack_a_mole`` must be 30 Hz (their whole
#: displacement lasts a few frames), ``sway``/path classes 15, ``wait_for_player`` checks at
#: 5 Hz, the slow presentation classes 5-10.
MOTION_RATE_HINT = {"sporadic_walk": 15.0, "hop": 30.0, "sway": 15.0, "dragged_along": 0.0,
                    "wait_for_player": 5.0, "whack_a_mole": 30.0, "path_collision": 15.0,
                    "spin": 5.0, "clouds": 10.0, "flip": 5.0, "bob": 10.0,
                    "sloping_path": 15.0, "back_and_forth": 15.0}
SPEED_SOURCES = ("explicit", "path", "type", "none")
BEHAVIOURS = ("static", "motion", "attached", "ambient", "stacked")
INHERIT_VALUES = ("full", "position", "none")
SPAWN_SOURCES = ("authored", "fieldCentreGuess", "reference")
SOURCE_KINDS = ("authored", "reference", "imported")
STRUCTURE_ROLES = ("box", "ramp", "curve_ramp")

# --------------------------------------------------------------------------- #
# key whitelists (LEVEL_FORMAT_V2 §9.3, plus the fields the repository's volume
# currency and reference import actually write; unknown keys are errors)
# --------------------------------------------------------------------------- #
DOC_ORDER = ("schema", "meta", "terrain", "structures", "activityArea", "props", "defs",
             "motions", "paths", "cameras", "renderPresets", "regions")
META_KEYS = ("id", "scene", "units", "convention", "ball", "timeLimitS", "playerSpawn",
             "groundY", "bounds", "spawnClearanceM", "zones", "arena", "caps", "budget",
             "expected", "source", "migration")
BALL_KEYS = ("startDiameterM", "targetDiameterM", "maxDiameterRef", "maxDiameterM", "start",
             "spawnSource")
CAPS_KEYS = ("props", "terrainTriangles", "terrainVertices", "staticSolids",
             "activityWalls", "movingProps", "animators")
BUDGET_KEYS = ("planes", "boxes", "ramps", "blocking", "propSupports")
EXPECTED_KEYS = ("propCount", "growthMm3", "structureCount", "staticObbs", "overlapBudget",
                 "valueUnits", "terrainTriangles")
SOURCE_KEYS = ("kind", "provenance", "windingFix", "licence")
ZONE_KEYS = ("id", "code", "kind")
ARENA_KEYS = ("id", "notes")
MIGRATION_KEYS = ("from", "maxDiameterM", "volumePenalty")
TERRAIN_KEYS = ("id", "kind", "winding", "vertices", "indices", "meshRef", "triangles",
                "broadPhase", "chunks", "grid", "materialSegments", "source")
MESHREF_KEYS = ("path", "sha256", "vertexCount", "indexCount", "triangleCount",
                "indexWidth", "zoneIds", "classes")
TRI_KEYS = ("count", "zoneIds", "classes", "zoneCounts")
BROAD_KEYS = ("kind", "boxes")
BROAD_BOX_KEYS = ("id", "min", "max", "triangleRange")
CHUNK_KEYS = ("id", "boxRange", "triangleRange")
GRID_KEYS = ("cellM", "origin", "cellsX", "cellsZ")
SEGMENT_KEYS = ("id", "firstIndex", "indexCount", "material")
STRUCTURE_KEYS = ("id", "kind", "role", "shape", "center", "size", "curve", "rotationQuat",
                  "collision", "absorbable", "support", "meets", "material", "notes",
                  "collider", "proxySource")
#: ``shape`` values: a box (the motor's OBB) or a fitted proxy (``collider`` in local space,
#: ``size`` kept as the support surface's AABB).
STRUCTURE_SHAPES = ("box", "proxy")
PROXY_SOURCES = ("def", "aabb-fallback", "manual")
SOLIDS_KEYS = ("solids",)
ACTIVITY_KEYS = ("walls", "killPlane", "softMarginM", "source")
WALL_KEYS = ("id", "points", "closed", "heightM", "thicknessM", "minBallDiameterM", "kind",
             "material", "notes")
KILL_KEYS = ("y", "respawn", "zoneId")
PROP_KEYS = ("itemId", "def", "kind", "behaviour", "motion", "inherit", "phase", "clipStart",
             "shakeOff", "zone", "position", "rotationQuat", "scale", "aabb", "volumeK",
             "volumePenalty", "volumeMm3", "absorbDiameterReqMm", "growthMm3", "absorbable",
             "valueUnits", "parent", "rigidGroup", "support", "stackedOn", "floatingOk",
             "notes", "instanceIndex")
DEF_KEYS = ("id", "kind", "model", "valueUnits", "volumeK", "volumePenalty", "collider",
            "scalePolicy", "speedIndex", "clips", "notes")
MOTION_KEYS = ("id", "class", "speed", "path", "params", "updatesPerSecond", "loop",
               "provenance", "notes")
PROVENANCE_KEYS = ("moveType", "motionID", "action", "backupIndex", "pathIndex")
SPEED_KEYS = ("source", "cmPerSec")
PATH_KEYS = ("id", "points", "loop", "speedCmPerSec", "notes")
CLIP_KEYS = ("id", "asset", "fps", "lengthSec")


# --------------------------------------------------------------------------- #
# canonical bytes (§8.1)
# --------------------------------------------------------------------------- #
def _clean_numbers(value):
    """``-0.0 -> 0.0`` everywhere (§8.1: global, nested arrays included)."""
    if isinstance(value, float):
        return 0.0 if value == 0.0 else value
    if isinstance(value, list):
        return [_clean_numbers(item) for item in value]
    if isinstance(value, dict):
        return {key: _clean_numbers(item) for key, item in value.items()}
    return value


def canonical_text(document):
    """The canonical bytes of a v2 document (§8.1): layer order, 2-space indent, LF.

    Values are already rounded by the writer (4 dp for lengths, 6 dp for quaternions); this
    function only enforces the layer order and the ``-0.0`` rule, so ``load -> dump`` is a
    no-op for a file it produced.
    """
    ordered = {key: document[key] for key in DOC_ORDER if key in document}
    for key in document:
        if key not in ordered:
            ordered[key] = document[key]
    return json.dumps(_clean_numbers(ordered), ensure_ascii=False, indent=2) + "\n"


# --------------------------------------------------------------------------- #
# the .katerrain sidecar (§8.3)
# --------------------------------------------------------------------------- #
def sidecar_bytes(terrain):
    """``(bytes, indexWidth)`` for one terrain dict of ``vertices``/``indices``/… ."""
    vertices = terrain["vertices"]
    indices = terrain["indices"]
    zone_ids = terrain.get("zoneIds") or []
    classes = terrain.get("classes") or []
    index_width = 2 if len(vertices) < U16_VERTEX_LIMIT else 4
    flags = 0
    if zone_ids:
        flags |= KATERRAIN_FLAG_ZONES
    if classes:
        flags |= KATERRAIN_FLAG_CLASSES
    out = bytearray()
    out += KATERRAIN_MAGIC
    out += struct.pack("<H", KATERRAIN_VERSION)
    out += struct.pack("<B", index_width)
    out += struct.pack("<B", flags)
    out += struct.pack("<III", len(vertices), len(indices), len(indices) // 3)
    for point in vertices:
        out += struct.pack("<3f", float(point[0]), float(point[1]), float(point[2]))
    out += struct.pack("<%d%s" % (len(indices), "H" if index_width == 2 else "I"), *indices)
    if zone_ids:
        out += bytes(bytearray(min(255, max(0, int(code))) for code in zone_ids))
    if classes:
        out += bytes(bytearray(min(255, max(0, int(value))) for value in classes))
    return bytes(out), index_width


class SidecarError(RuntimeError):
    """A ``.katerrain`` payload that cannot be parsed."""


def read_sidecar(data):
    """Parse a ``.katerrain`` payload into its sections (and their counts)."""
    data = bytes(data)
    if len(data) < 20 or data[:4] != KATERRAIN_MAGIC:
        raise SidecarError("magic is not KTRN")
    version, index_width, flags = struct.unpack_from("<HBB", data, 4)
    if version != KATERRAIN_VERSION:
        raise SidecarError("version %d is not %d" % (version, KATERRAIN_VERSION))
    vertex_count, index_count, triangle_count = struct.unpack_from("<III", data, 8)
    if index_width not in (2, 4):
        raise SidecarError("indexWidth %d is not 2 or 4" % index_width)
    offset = 20
    needed = offset + vertex_count * 12 + index_count * index_width
    if len(data) < needed:
        raise SidecarError("truncated: %d bytes, the counts need %d" % (len(data), needed))
    vertices = [struct.unpack_from("<3f", data, offset + 12 * index)
                for index in range(vertex_count)]
    offset += vertex_count * 12
    width = 2 if index_width == 2 else 4
    indices = list(struct.unpack_from("<%d%s" % (index_count, "H" if width == 2 else "I"),
                                      data, offset))
    offset += index_count * width
    zones = []
    classes = []
    if flags & KATERRAIN_FLAG_ZONES:
        zones = list(data[offset:offset + triangle_count])
        offset += triangle_count
    if flags & KATERRAIN_FLAG_CLASSES:
        classes = list(data[offset:offset + triangle_count])
        offset += triangle_count
    return {"vertices": vertices, "indices": indices, "zoneIds": zones, "classes": classes,
            "indexWidth": index_width, "flags": flags, "trailing": len(data) - offset,
            "counts": {"vertices": vertex_count, "indices": index_count,
                       "triangles": triangle_count}}


def sidecar_errors(raw, mesh_ref):
    """``(problems, parsed)`` for one ``.katerrain`` payload (raw bytes).

    Checks the sha256 the document pins, the three counts, the index width and that the file
    has no trailing bytes — everything §8.3 says the validator must re-derive.
    """
    try:
        payload = read_sidecar(raw)
    except SidecarError as error:
        return ["[terrain_sidecar] %s: expected a KTRN v%d payload, got %s"
                % (mesh_ref.get("path") or "sidecar", KATERRAIN_VERSION, error)], None
    problems = []
    digest = hashlib.sha256(raw).hexdigest()
    if digest != mesh_ref.get("sha256"):
        problems.append("[terrain_sidecar] terrain.meshRef.sha256: expected %r, got %r"
                        % (mesh_ref.get("sha256"), digest))
    declared = {"vertices": mesh_ref.get("vertexCount"), "indices": mesh_ref.get("indexCount"),
                "triangles": mesh_ref.get("triangleCount")}
    for key, want in declared.items():
        got = payload["counts"][key]
        if want is not None and got != want:
            problems.append("[terrain_sidecar] terrain.meshRef.%sCount: expected %r, got %d"
                            % (key, want, got))
    if payload["counts"]["indices"] != 3 * payload["counts"]["triangles"]:
        problems.append("[terrain_sidecar] sidecar.indices: expected %d (3 per triangle), "
                        "got %d" % (3 * payload["counts"]["triangles"],
                                    payload["counts"]["indices"]))
    if payload["trailing"]:
        problems.append("[terrain_sidecar] sidecar: expected no trailing bytes, got %d"
                        % payload["trailing"])
    if payload["counts"]["vertices"] >= U16_VERTEX_LIMIT and payload["indexWidth"] != 4:
        problems.append("[terrain_sidecar] sidecar.indexWidth: expected 4 (>= %d vertices), "
                        "got %d" % (U16_VERTEX_LIMIT, payload["indexWidth"]))
    if mesh_ref.get("indexWidth") is not None \
            and payload["indexWidth"] != mesh_ref.get("indexWidth"):
        problems.append("[terrain_sidecar] terrain.meshRef.indexWidth: expected %r, got %d"
                        % (mesh_ref.get("indexWidth"), payload["indexWidth"]))
    return problems, payload


# --------------------------------------------------------------------------- #
# small helpers
# --------------------------------------------------------------------------- #
def _num(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) \
        and math.isfinite(float(value))


def _vec(value, length=3):
    return isinstance(value, (list, tuple)) and len(value) == length and all(_num(v) for v in value)


def _unit(quat, tolerance=1e-4):
    return _vec(quat, 4) and abs(sum(float(v) * float(v) for v in quat) - 1.0) <= tolerance


def _unknown(report, block, allowed, where):
    """``[<layer>_key]`` for every key outside the whitelist (§9.3: never silently dropped)."""
    if not isinstance(block, dict):
        return
    tag = {"meta": "[meta_unknown_key]", "ball": "[meta_unknown_key]",
           "caps": "[meta_unknown_key]", "budget": "[meta_budget]",
           "expected": "[meta_expected]", "source": "[meta_source]",
           "migration": "[meta_migration]", "arena": "[meta_arena]",
           "zones": "[meta_zone_code]", "terrain": "[terrain_key]",
           "meshRef": "[terrain_sidecar]", "broadPhase": "[terrain_broadphase]",
           "broadBoxes": "[terrain_broadphase]", "chunks": "[terrain_budget]",
           "grid": "[terrain_grid]", "segments": "[terrain_segment]",
           "props": "[prop_key]", "defs": "[def_key]", "clips": "[def_clips]",
           "motions": "[motion_key]", "speed": "[motion_speed]",
           "provenance": "[motion_key]", "paths": "[path_key]",
           "structures": "[structure_key]", "walls": "[activity_wall]",
           "killPlane": "[activity_kill_plane]"}.get(where, "[unknown_key]")
    for key in block:
        if key not in allowed:
            report.err("%s %s: unknown key %r (allowed: %s)"
                       % (tag, where, key, ", ".join(allowed)))


def design_ceiling_m(document):
    """The diameter the *level* was designed for (walls, absorbable, budget sanity).

    ``meta.migration.maxDiameterM`` (a migrated per-level cap) wins, then an explicit
    ``ball.maxDiameterM`` for a ``maxDiameterRef:"level"`` document, then the global cap.
    """
    meta = document.get("meta") or {}
    migration = meta.get("migration") or {}
    for candidate in (migration.get("maxDiameterM"),
                      (meta.get("ball") or {}).get("maxDiameterM")):
        if _num(candidate) and float(candidate) > 0:
            return float(candidate)
    return GLOBAL_MAX_DIAMETER_M


def _def_catalog(document):
    catalog = {}
    for entry in document.get("defs") or []:
        if isinstance(entry, dict) and isinstance(entry.get("id"), str):
            catalog[entry["id"]] = entry
    return catalog


# --------------------------------------------------------------------------- #
# the validator
# --------------------------------------------------------------------------- #
def validate(document, path=None, manifests=None, sidecar_bytes_=None, strict=False,
             rel_tolerance=1e-9):
    """Run every v2 rule; returns a :class:`placement_core.Report`.

    ``path`` is the document's file (used for the sidecar's relative path and the canonical
    byte check); ``sidecar_bytes_`` lets a caller hand over the sidecar directly (tests, or a
    document that is not on disk yet).
    """
    report = core.Report()
    if not isinstance(document, dict):
        report.err("[schema] document: expected an object, got %r" % (type(document).__name__,))
        return report
    schema = document.get("schema")
    if schema == SCHEMA_V1:
        report.err("[schema_legacy] document: is %r, expected %r - run the v1->v2 converter "
                   "first (Tools/levelbuild/ref_v2_from_extract.py regenerates the reference "
                   "levels)" % (schema, SCHEMA))
        return report
    if schema != SCHEMA:
        report.err("[schema] document: expected schema %r, got %r" % (SCHEMA, schema))
        return report

    catalog = _def_catalog(document)
    meta = document.get("meta") if isinstance(document.get("meta"), dict) else {}
    _check_meta(document, meta, report, path)
    terrain = document.get("terrain")
    lowest_y = None
    if isinstance(terrain, dict):
        lowest_y = _check_terrain(document, terrain, meta, report, path, sidecar_bytes_,
                                  rel_tolerance)
    else:
        report.err("[terrain_mesh] terrain: expected an object, got %r"
                   % (type(terrain).__name__,))
    _check_structures(document, meta, report)
    _check_activity(document, meta, report, lowest_y)
    _check_defs(document, report, catalog)
    motions, paths = _check_motions_paths(document, report)
    boxes, props = _check_props(document, report, catalog, motions)
    _check_support_and_overlap(document, props, boxes, report)
    _check_parents(document, props, report)
    _check_expected(document, meta, props, boxes, report)
    _check_caps(document, meta, report, terrain)
    if strict:
        report.errors.extend(report.warnings)
        report.warnings = []
    return report


# -- meta ------------------------------------------------------------------- #
def _check_meta(document, meta, report, path):
    _unknown(report, meta, META_KEYS, "meta")
    if meta.get("units") != "m":
        report.err("[meta_units] meta.units: expected 'm', got %r" % (meta.get("units"),))
    if meta.get("convention") != "unity-y-up-left-handed-quat-xyzw":
        report.err("[meta_convention] meta.convention: expected the Unity convention, got %r"
                   % (meta.get("convention"),))
    level_id = meta.get("id")
    if not isinstance(level_id, str) or not level_id.strip():
        report.err("[meta_id] meta.id: expected a non-empty string, got %r" % (level_id,))
    if path is not None and isinstance(level_id, str) and level_id:
        folder = Path(path).resolve().parent.name
        if folder and folder != level_id:
            report.err("[meta_id] meta.id: expected %r (the directory name), got %r"
                       % (folder, level_id))

    ball = meta.get("ball") if isinstance(meta.get("ball"), dict) else {}
    _unknown(report, ball, BALL_KEYS, "ball")
    start = ball.get("startDiameterM")
    if not _num(start) or float(start) <= 0.0:
        report.err("[meta_start_diameter] meta.ball.startDiameterM: expected a positive "
                   "number, got %r" % (start,))
    cap_ref = ball.get("maxDiameterRef")
    if cap_ref not in ("global", "level"):
        report.err("[meta_max_ref] meta.ball.maxDiameterRef: expected 'global' or 'level', "
                   "got %r" % (cap_ref,))
    if cap_ref == "level":
        cap = ball.get("maxDiameterM")
        if not _num(cap) or float(cap) <= 0.0:
            report.err("[meta_max_ref] meta.ball.maxDiameterM: expected a positive number "
                       "when maxDiameterRef is 'level', got %r" % (cap,))
        elif _num(start) and float(start) > float(cap):
            report.err("[meta_start_diameter] meta.ball.startDiameterM: expected <= "
                       "maxDiameterM %r, got %r" % (cap, start))
        if meta.get("scene") == "StoryMode":
            report.warn("[meta_cap_override] meta.ball.maxDiameterRef: expected 'global' for "
                        "a story level, got 'level' (cap %r)" % (cap,))
    target = ball.get("targetDiameterM")
    if target is not None and (not _num(target) or float(target) <= 0.0):
        report.err("[meta_target_diameter] meta.ball.targetDiameterM: expected a positive "
                   "number, got %r" % (target,))
    if not _vec(ball.get("start")):
        report.err("[meta_ball_start] meta.ball.start: expected 3 numbers, got %r"
                   % (ball.get("start"),))
    if ball.get("spawnSource") not in SPAWN_SOURCES:
        report.err("[meta_spawn_source] meta.ball.spawnSource: expected one of %s, got %r"
                   % ("/".join(SPAWN_SOURCES), ball.get("spawnSource")))

    time_limit = meta.get("timeLimitS")
    if meta.get("scene") == "StoryMode" and not (_num(time_limit) and float(time_limit) > 0.0):
        report.err("[meta_time_limit] meta.timeLimitS: expected a positive number for a story "
                   "level, got %r" % (time_limit,))
    if time_limit is not None and (not _num(time_limit) or float(time_limit) < 0.0):
        report.err("[meta_time_limit] meta.timeLimitS: expected >= 0, got %r" % (time_limit,))
    if not _vec(meta.get("playerSpawn")):
        report.err("[meta_player_spawn] meta.playerSpawn: expected 3 numbers, got %r"
                   % (meta.get("playerSpawn"),))
    if meta.get("groundY") is not None and not _num(meta.get("groundY")):
        report.err("[meta_ground_y] meta.groundY: expected a number, got %r"
                   % (meta.get("groundY"),))
    bounds = meta.get("bounds")
    if not (isinstance(bounds, list) and len(bounds) == 2 and all(_vec(point, 2) for point in bounds)):
        report.err("[meta_bounds] meta.bounds: expected [[minX,minZ],[maxX,maxZ]], got %r"
                   % (bounds,))
    elif bounds[0][0] >= bounds[1][0] or bounds[0][1] >= bounds[1][1]:
        report.err("[meta_bounds] meta.bounds: expected min < max on both axes, got %r"
                   % (bounds,))
    if not (_num(meta.get("spawnClearanceM")) and float(meta["spawnClearanceM"]) >= 0.0):
        report.err("[meta_spawn_clearance] meta.spawnClearanceM: expected a number >= 0, got %r"
                   % (meta.get("spawnClearanceM"),))
    if not isinstance(meta.get("arena"), dict):
        report.err("[meta_arena] meta.arena: expected an object, got %r"
                   % (type(meta.get("arena")).__name__,))
    else:
        _unknown(report, meta["arena"], ARENA_KEYS, "arena")
    caps = meta.get("caps")
    if not isinstance(caps, dict):
        report.err("[meta_caps] meta.caps: expected an object, got %r"
                   % (type(caps).__name__,))
    else:
        _unknown(report, caps, CAPS_KEYS, "caps")
        for key in CAPS_KEYS:
            if key not in caps:
                report.err("[meta_caps] meta.caps.%s: expected a capacity, got nothing" % key)
            elif not isinstance(caps[key], int) or isinstance(caps[key], bool) or caps[key] < 0:
                report.err("[meta_caps] meta.caps.%s: expected a whole number >= 0, got %r"
                           % (key, caps[key]))
    budget = meta.get("budget")
    if not isinstance(budget, dict):
        report.err("[meta_budget] meta.budget: expected an object, got %r"
                   % (type(budget).__name__,))
    else:
        _unknown(report, budget, BUDGET_KEYS, "budget")
    expected = meta.get("expected")
    if not isinstance(expected, dict):
        report.err("[meta_expected] meta.expected: expected an object with propCount and "
                   "growthMm3, got %r" % (type(expected).__name__,))
    else:
        _unknown(report, expected, EXPECTED_KEYS, "expected")
    source = meta.get("source")
    if not isinstance(source, dict):
        report.err("[meta_source] meta.source: expected an object, got %r"
                   % (type(source).__name__,))
    else:
        _unknown(report, source, SOURCE_KEYS, "source")
        if source.get("kind") not in SOURCE_KINDS:
            report.err("[meta_source] meta.source.kind: expected one of %s, got %r"
                       % ("/".join(SOURCE_KINDS), source.get("kind")))
        if source.get("kind") == "reference" and not source.get("provenance"):
            report.err("[meta_source] meta.source.provenance: expected the outside-the-repo "
                       "path for a reference level, got nothing")
    migration = meta.get("migration")
    if migration is not None:
        if not isinstance(migration, dict):
            report.err("[meta_migration] meta.migration: expected an object, got %r"
                       % (type(migration).__name__,))
        else:
            _unknown(report, migration, MIGRATION_KEYS, "migration")

    # zones: unique codes, 0 declared at most once, kind one of the four names
    zones = meta.get("zones") or []
    if not isinstance(zones, list):
        report.err("[meta_zone_code] meta.zones: expected an array, got %r"
                   % (type(zones).__name__,))
        zones = []
    seen_codes = []
    seen_ids = set()
    for index, zone in enumerate(zones):
        where = "meta.zones[%d]" % index
        if not isinstance(zone, dict):
            report.err("[meta_zone_code] %s: expected an object, got %r" % (where, zone))
            continue
        _unknown(report, zone, ZONE_KEYS, "zones")
        zone_id = zone.get("id")
        if not isinstance(zone_id, str) or not zone_id.strip():
            report.err("[meta_zone_code] %s.id: expected a non-empty string, got %r"
                       % (where, zone_id))
        elif zone_id in seen_ids:
            report.err("[meta_zone_code] %s.id: expected a unique id, got %r (duplicate)"
                       % (where, zone_id))
        else:
            seen_ids.add(zone_id)
        code = zone.get("code")
        if not isinstance(code, int) or isinstance(code, bool) or not 0 <= code <= 255:
            report.err("[meta_zone_code] %s.code: expected an integer 0..255, got %r"
                       % (where, code))
        else:
            seen_codes.append(code)
        if zone.get("kind") not in (None, "ground", "room", "void", "other"):
            report.err("[meta_zone_code] %s.kind: expected ground/room/void/other, got %r"
                       % (where, zone.get("kind")))
    duplicates = sorted({code for code in seen_codes if seen_codes.count(code) > 1})
    if duplicates:
        report.err("[meta_zone_code] meta.zones[].code: expected unique codes, got duplicates "
                   "%s" % (duplicates,))
    if seen_codes.count(0) > 1:
        report.err("[meta_zone_code] meta.zones[].code: expected code 0 at most once, got %d"
                   % seen_codes.count(0))


# -- terrain ---------------------------------------------------------------- #
def _check_terrain(document, terrain, meta, report, path, given_sidecar, rel_tolerance):
    _unknown(report, terrain, TERRAIN_KEYS, "terrain")
    if terrain.get("kind") != "mesh":
        report.err("[terrain_mesh] terrain.kind: expected 'mesh', got %r" % (terrain.get("kind"),))
    if terrain.get("winding") != "ccw":
        report.err("[terrain_winding] terrain.winding: expected 'ccw', got %r"
                   % (terrain.get("winding"),))
    mesh_ref = terrain.get("meshRef")
    triangles = terrain.get("triangles") if isinstance(terrain.get("triangles"), dict) else {}
    inline = "vertices" in terrain or "indices" in terrain
    if mesh_ref is not None and inline:
        report.err("[terrain_mesh] terrain: expected either meshRef or inline vertices/indices, "
                   "got both")
    payload = None
    if mesh_ref is not None:
        _unknown(report, mesh_ref, MESHREF_KEYS, "meshRef")
        payload = _load_sidecar(terrain, mesh_ref, path, report, given_sidecar)
    elif inline:
        vertices = terrain.get("vertices")
        indices = terrain.get("indices")
        if not isinstance(vertices, list) or not all(_vec(point) for point in vertices):
            report.err("[terrain_mesh] terrain.vertices: expected an array of [x,y,z], got %r"
                       % (type(vertices).__name__,))
            vertices = []
        if not isinstance(indices, list) or not all(_num(value) and float(value).is_integer()
                                                    for value in indices):
            report.err("[terrain_mesh] terrain.indices: expected an array of whole numbers, "
                       "got %r" % (type(indices).__name__,))
            indices = []
        payload = {"vertices": vertices, "indices": [int(value) for value in indices],
                   "zoneIds": triangles.get("zoneIds") or [], "classes": triangles.get("classes") or [],
                   "counts": {"vertices": len(vertices), "indices": len(indices),
                              "triangles": len(indices) // 3}}
    else:
        report.err("[terrain_mesh] terrain: expected meshRef or inline vertices/indices, got "
                   "neither")

    declared_count = triangles.get("count")
    count = payload["counts"]["triangles"] if payload else 0
    if not isinstance(declared_count, int):
        report.err("[terrain_mesh] terrain.triangles.count: expected an integer, got %r"
                   % (declared_count,))
    elif payload is not None and declared_count != count:
        report.err("[terrain_mesh] terrain.triangles.count: expected %r, got %d"
                   % (declared_count, count))
    if payload is None:
        return None
    vertices = payload["vertices"]
    lowest_y = min((float(point[1]) for point in vertices), default=None)
    indices = payload["indices"]
    if len(indices) % 3:
        report.err("[terrain_mesh] terrain.indices: expected a length divisible by 3, got %d"
                   % len(indices))
    vertex_count = len(vertices)
    for index, value in enumerate(indices):
        if not 0 <= value < vertex_count:
            report.err("[terrain_mesh] terrain.indices[%d]: expected 0..%d, got %d"
                       % (index, vertex_count - 1, value))
            break
    degenerate = 0
    for offset in range(0, len(indices) - 2, 3):
        triangle = indices[offset:offset + 3]
        if any(index < 0 or index >= vertex_count for index in triangle):
            break                     # already reported above; do not index out of range
        first, second, third = (vertices[index] for index in triangle)
        area = 0.5 * math.sqrt(sum(v * v for v in _normal(first, second, third)))
        if area < 1e-9:
            degenerate += 1
    if degenerate:
        report.err("[terrain_mesh] terrain: expected no degenerate triangles (area < 1e-9 m2), "
                   "got %d" % degenerate)

    # winding: no shared edge may be traversed the same way by both its triangles
    first_direction = {}
    same_direction = 0
    for offset in range(0, len(indices) - 2, 3):
        triangle = (indices[offset], indices[offset + 1], indices[offset + 2])
        if any(index < 0 or index >= vertex_count for index in triangle):
            break
        for a, b in ((triangle[0], triangle[1]), (triangle[1], triangle[2]),
                     (triangle[2], triangle[0])):
            key = (a, b) if a < b else (b, a)
            direction = 1 if (a, b) == key else -1
            if key in first_direction:
                if first_direction[key] == direction:
                    same_direction += 1
            else:
                first_direction[key] = direction
    if same_direction:
        report.err("[terrain_winding] terrain.indices: expected every shared edge traversed in "
                   "opposite directions, got %d edge(s) traversed the same way (a triangle is "
                   "reversed)" % same_direction)

    # zone codes: declared in meta.zones, length == triangle count
    declared_codes = {zone.get("code") for zone in meta.get("zones") or []
                      if isinstance(zone, dict)}
    zone_ids = payload.get("zoneIds") or []
    if zone_ids:
        if len(zone_ids) != count:
            report.err("[terrain_zone] terrain.triangles.zoneIds: expected %d entries, got %d"
                       % (count, len(zone_ids)))
        unknown = sorted({int(code) for code in zone_ids} - {code for code in declared_codes
                                                             if isinstance(code, int)})
        if unknown:
            report.err("[terrain_zone] terrain.triangles.zoneIds: expected only declared zone "
                       "codes, got %s" % (unknown,))
    classes = payload.get("classes") or []
    if classes:
        if len(classes) != count:
            report.err("[terrain_classes] terrain.triangles.classes: expected %d entries, got "
                       "%d" % (count, len(classes)))
        wrong = sorted({value for value in classes} - {1, 2, 3})
        if wrong:
            report.err("[terrain_classes] terrain.triangles.classes: expected 1/2/3, got %s"
                       % (wrong,))

    # budget
    caps = meta.get("caps") or {}
    for key, actual in (("terrainTriangles", count), ("terrainVertices", vertex_count)):
        cap = caps.get(key)
        if isinstance(cap, int) and actual > cap:
            report.err("[terrain_budget] meta.caps.%s: expected >= %d, got %d" % (key, actual, cap))
    chunks = terrain.get("chunks") or []
    for chunk in chunks:
        if not isinstance(chunk, dict):
            report.err("[terrain_budget] terrain.chunks[]: expected an object, got %r" % (chunk,))
            continue
        _unknown(report, chunk, CHUNK_KEYS, "chunks")
        tri_range = chunk.get("triangleRange") or [None, None]
        box_range = chunk.get("boxRange") or [None, None]
        for label, pair, limit in (("triangleRange", tri_range, CHUNK_TRIANGLES),
                                   ("boxRange", box_range, None)):
            count_in_range = pair[1] if isinstance(pair, list) and len(pair) == 2 else None
            if limit is not None and isinstance(count_in_range, int) and count_in_range > limit:
                report.err("[terrain_budget] terrain.chunks[%r].%s: expected <= %d, got %d"
                           % (chunk.get("id"), label, limit, count_in_range))
    if chunks:
        covered = sum(int(chunk.get("triangleRange", [0, 0])[1] or 0) for chunk in chunks
                      if isinstance(chunk, dict))
        if covered != count:
            report.err("[terrain_budget] terrain.chunks: expected to cover %d triangles, got %d"
                       % (count, covered))
    if not chunks and count > CHUNK_TRIANGLES:
        report.err("[terrain_budget] terrain.chunks: expected chunks of <= %d triangles for "
                   "%d triangles, got none" % (CHUNK_TRIANGLES, count))

    _check_broadphase(terrain, vertices, indices, count, report)

    grid = terrain.get("grid")
    if grid is not None:
        _unknown(report, grid, GRID_KEYS, "grid")
        cell = grid.get("cellM")
        if not (_num(cell) and GRID_CELL_MIN <= float(cell) <= GRID_CELL_MAX):
            report.err("[terrain_grid] terrain.grid.cellM: expected %g..%g, got %r"
                       % (GRID_CELL_MIN, GRID_CELL_MAX, cell))
        origin = grid.get("origin")
        if not _vec(origin, 2):
            report.err("[terrain_grid] terrain.grid.origin: expected [x, z], got %r" % (origin,))
        else:
            boxes = (terrain.get("broadPhase") or {}).get("boxes") or []
            if boxes:
                low_x = min(box.get("min", [0, 0, 0])[0] for box in boxes if isinstance(box, dict))
                low_z = min(box.get("min", [0, 0, 0])[2] for box in boxes if isinstance(box, dict))
                high_x = max(box.get("max", [0, 0, 0])[0] for box in boxes if isinstance(box, dict))
                high_z = max(box.get("max", [0, 0, 0])[2] for box in boxes if isinstance(box, dict))
                if float(origin[0]) > low_x or float(origin[1]) > low_z:
                    report.err("[terrain_grid] terrain.grid.origin: expected <= the broad-phase "
                               "minimum (%r, %r), got %r" % (low_x, low_z, origin))
                if _num(cell) and float(cell) > 0:
                    cover_x = float(origin[0]) + int(grid.get("cellsX") or 0) * float(cell)
                    cover_z = float(origin[1]) + int(grid.get("cellsZ") or 0) * float(cell)
                    if cover_x < high_x - 1e-6 or cover_z < high_z - 1e-6:
                        report.err("[terrain_grid] terrain.grid: expected the cells to cover the "
                                   "broad phase up to (%r, %r), got (%r, %r)"
                                   % (high_x, high_z, cover_x, cover_z))

    segments = terrain.get("materialSegments") or []
    coverage = 0
    for index, segment in enumerate(segments):
        if not isinstance(segment, dict):
            report.err("[terrain_segment] terrain.materialSegments[%d]: expected an object" % index)
            continue
        _unknown(report, segment, SEGMENT_KEYS, "segments")
        first_index = segment.get("firstIndex")
        if first_index != coverage:
            report.err("[terrain_segment] terrain.materialSegments[%d].firstIndex: expected %d, "
                       "got %r" % (index, coverage, first_index))
        coverage += int(segment.get("indexCount") or 0)
    if segments and coverage != len(indices):
        report.err("[terrain_segment] terrain.materialSegments: expected to cover %d indices, "
                   "got %d" % (len(indices), coverage))

    # meta.groundY is the fallback floor: with a terrain it must sit below the mesh, or it
    # pushes through the ground (§2.2 [meta_ground_y]).
    ground = meta.get("groundY")
    if lowest_y is not None and _num(ground) and float(ground) >= lowest_y:
        report.err("[meta_ground_y] meta.groundY: expected < the terrain's lowest point %r, "
                   "got %r" % (lowest_y, ground))
    return lowest_y


def _normal(first, second, third):
    ux, uy, uz = second[0] - first[0], second[1] - first[1], second[2] - first[2]
    vx, vy, vz = third[0] - first[0], third[1] - first[1], third[2] - first[2]
    return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx)


def _load_sidecar(terrain, mesh_ref, path, report, given):
    """Read the sidecar (given bytes, or the file next to the document) and check it."""
    relative = mesh_ref.get("path")
    if not isinstance(relative, str) or not relative:
        report.err("[terrain_sidecar] terrain.meshRef.path: expected a relative path, got %r"
                   % (relative,))
        return None
    if relative.startswith("/") or ".." in Path(relative).parts:
        report.err("[terrain_sidecar] terrain.meshRef.path: expected a path inside the level "
                   "directory (no '..', no absolute), got %r" % (relative,))
        return None
    data = given
    if data is None:
        if path is None:
            return None
        candidate = Path(path).resolve().parent / relative
        if not candidate.is_file():
            report.err("[terrain_sidecar] %s: expected the sidecar file, got nothing"
                       % (relative,))
            return None
        data = candidate.read_bytes()
    problems, payload = sidecar_errors(data, mesh_ref)
    for problem in problems:
        report.err(problem)
    return payload


def _check_broadphase(terrain, vertices, indices, count, report):
    broad = terrain.get("broadPhase")
    if not isinstance(broad, dict):
        report.err("[terrain_broadphase] terrain.broadPhase: expected an object, got %r"
                   % (type(broad).__name__,))
        return
    _unknown(report, broad, BROAD_KEYS, "broadPhase")
    if broad.get("kind") != "aabb":
        report.err("[terrain_broadphase] terrain.broadPhase.kind: expected 'aabb', got %r"
                   % (broad.get("kind"),))
    boxes = broad.get("boxes")
    if not isinstance(boxes, list) or not boxes:
        report.err("[terrain_broadphase] terrain.broadPhase.boxes: expected a non-empty array, "
                   "got %r" % (type(boxes).__name__,))
        return
    expected_start = 0
    for index, box in enumerate(boxes):
        where = "terrain.broadPhase.boxes[%d]" % index
        if not isinstance(box, dict):
            report.err("[terrain_broadphase] %s: expected an object, got %r" % (where, box))
            return
        _unknown(report, box, BROAD_BOX_KEYS, "broadBoxes")
        low, high = box.get("min"), box.get("max")
        if not _vec(low) or not _vec(high):
            report.err("[terrain_broadphase] %s.min/max: expected 3 numbers each, got %r / %r"
                       % (where, low, high))
            return
        if any(low[axis] > high[axis] for axis in range(3)):
            report.err("[terrain_broadphase] %s: expected min <= max, got %r > %r"
                       % (where, low, high))
        span = box.get("triangleRange")
        if not (isinstance(span, list) and len(span) == 2
                and all(isinstance(value, int) for value in span)):
            report.err("[terrain_broadphase] %s.triangleRange: expected [start, count], got %r"
                       % (where, span))
            return
        start, length = span
        if start != expected_start:
            report.err("[terrain_broadphase] %s.triangleRange[0]: expected %d (contiguous, "
                       "sorted, no holes), got %d" % (where, expected_start, start))
        expected_start = start + length
        margin = 1e-3
        for triangle in range(start, min(start + length, count)):
            for corner in range(3):
                if triangle * 3 + corner >= len(indices):
                    break
                vertex_index = indices[triangle * 3 + corner]
                if not 0 <= vertex_index < len(vertices):
                    continue
                point = vertices[vertex_index]
                for axis in range(3):
                    if point[axis] < low[axis] - margin or point[axis] > high[axis] + margin:
                        report.err("[terrain_broadphase] %s: expected the box to contain "
                                   "triangle %d, got axis %d at %r outside [%r, %r]"
                                   % (where, triangle, axis, point[axis], low[axis], high[axis]))
                        return
    if expected_start != count:
        report.err("[terrain_broadphase] terrain.broadPhase.boxes: expected to cover %d "
                   "triangles, got %d" % (count, expected_start))


# -- structures ------------------------------------------------------------- #
def _check_structures(document, meta, report):
    structures = document.get("structures")
    if not isinstance(structures, dict):
        report.err("[structure_role] structures: expected an object with 'solids', got %r"
                   % (type(structures).__name__,))
        return
    _unknown(report, structures, SOLIDS_KEYS, "structures")
    solids = structures.get("solids")
    if not isinstance(solids, list):
        report.err("[structure_role] structures.solids: expected an array, got %r"
                   % (type(solids).__name__,))
        return
    seen = set()
    for index, solid in enumerate(solids):
        where = "structures.solids[%d]" % index
        if not isinstance(solid, dict):
            report.err("[structure_role] %s: expected an object, got %r" % (where, solid))
            continue
        _unknown(report, solid, STRUCTURE_KEYS, "structures")
        solid_id = solid.get("id")
        if not isinstance(solid_id, str) or not solid_id.strip():
            report.err("[structure_role] %s.id: expected a non-empty string, got %r"
                       % (where, solid_id))
        elif solid_id in seen:
            report.err("[structure_role] %s.id: expected a unique id, got %r (duplicate)"
                       % (where, solid_id))
        else:
            seen.add(solid_id)
        role = solid.get("role")
        if role in ("boundary", "ground"):
            report.err("[structure_role] %s.role: expected box/ramp/curve_ramp in a v2 document "
                       "(boundary -> activityArea.walls, ground -> meta.groundY), got %r"
                       % (where, role))
        elif role not in STRUCTURE_ROLES:
            report.err("[structure_role] %s.role: expected one of %s, got %r"
                       % (where, "/".join(STRUCTURE_ROLES), role))
        if not _vec(solid.get("center")):
            report.err("[structure_shape] %s.center: expected 3 numbers, got %r"
                       % (where, solid.get("center")))
        if not _vec(solid.get("size")) or any(float(value) <= 0 for value in solid["size"]):
            report.err("[structure_shape] %s.size: expected 3 positive numbers, got %r"
                       % (where, solid.get("size")))
        if not _unit(solid.get("rotationQuat")):
            report.err("[structure_rotation] %s.rotationQuat: expected a unit quaternion, got %r"
                       % (where, solid.get("rotationQuat")))
        shape = solid.get("shape")
        if shape not in STRUCTURE_SHAPES:
            report.err("[structure_shape] %s.shape: expected one of %s, got %r"
                       % (where, "/".join(STRUCTURE_SHAPES), shape))
        collider = solid.get("collider")
        if shape == "proxy":
            # Contact uses the proxy, not the box: it must be a valid collider block, in the
            # solid's local frame (the same encoding defs[].collider uses).
            if not isinstance(collider, dict):
                report.err("[structure_proxy] %s.collider: expected a collider block for "
                           "shape 'proxy', got %r" % (where, collider))
            else:
                for problem in collider_proxy.collider_schema_errors(collider):
                    report.err("[structure_proxy] %s.collider: %s" % (where, problem))
            if solid.get("proxySource") not in PROXY_SOURCES:
                report.err("[structure_proxy] %s.proxySource: expected one of %s, got %r"
                           % (where, "/".join(PROXY_SOURCES), solid.get("proxySource")))
        elif collider is not None:
            report.err("[structure_proxy] %s.collider: expected nothing when shape is 'box', "
                       "got %r" % (where, type(collider).__name__))
        if solid.get("collision") not in (None, "box", "none"):
            report.err("[structure_collision] %s.collision: expected box/none, got %r"
                       % (where, solid.get("collision")))
        if solid.get("collision") == "none" and solid.get("support"):
            report.err("[structure_collision] %s.support: expected false when collision is "
                       "'none', got true" % where)
    caps = meta.get("caps") or {}
    cap = caps.get("staticSolids")
    if isinstance(cap, int) and len(solids) > cap:
        report.err("[structure_capacity] meta.caps.staticSolids: expected >= %d, got %d"
                   % (len(solids), cap))


# -- activityArea ------------------------------------------------------------ #
def _check_activity(document, meta, report, lowest_y=None):
    activity = document.get("activityArea")
    if not isinstance(activity, dict):
        report.err("[activity_wall] activityArea: expected an object, got %r"
                   % (type(activity).__name__,))
        return
    _unknown(report, activity, ACTIVITY_KEYS, "activityArea")
    walls = activity.get("walls")
    if not isinstance(walls, list):
        report.err("[activity_wall] activityArea.walls: expected an array, got %r"
                   % (type(walls).__name__,))
        walls = []
    caps = meta.get("caps") or {}
    cap = caps.get("activityWalls")
    segments = 0
    ball = meta.get("ball") or {}
    start_diameter = ball.get("startDiameterM")
    ceiling = design_ceiling_m(document)
    for index, wall in enumerate(walls):
        where = "activityArea.walls[%d]" % index
        if not isinstance(wall, dict):
            report.err("[activity_wall] %s: expected an object, got %r" % (where, wall))
            continue
        _unknown(report, wall, WALL_KEYS, "walls")
        wall_id = wall.get("id")
        if not isinstance(wall_id, str) or not wall_id.strip():
            report.err("[activity_wall] %s.id: expected a non-empty string, got %r"
                       % (where, wall_id))
        points = wall.get("points")
        if not (isinstance(points, list) and len(points) >= 2 and all(_vec(p, 2) for p in points)):
            report.err("[activity_wall] %s.points: expected >= 2 [x,z] points, got %r"
                       % (where, points))
            continue
        segments += len(points) if wall.get("closed") else len(points) - 1
        if wall.get("closed") and _self_intersects(points):
            report.err("[activity_wall] %s.points: expected a simple polygon, got a "
                       "self-intersection" % where)
        if not (_num(wall.get("heightM")) and float(wall["heightM"]) > 0):
            report.err("[activity_wall] %s.heightM: expected a positive number, got %r"
                       % (where, wall.get("heightM")))
        elif float(wall["heightM"]) <= ceiling / 2.0:
            report.warn("[activity_wall_height] %s.heightM: expected > the largest ball radius "
                        "%r, got %r" % (where, round(ceiling / 2.0, 4), wall["heightM"]))
        if not (_num(wall.get("thicknessM")) and float(wall["thicknessM"]) > 0):
            report.err("[activity_wall] %s.thicknessM: expected a positive number, got %r"
                       % (where, wall.get("thicknessM")))
        if wall.get("kind") not in ("visible", "invisible"):
            report.err("[activity_wall] %s.kind: expected visible/invisible, got %r"
                       % (where, wall.get("kind")))
        gate = wall.get("minBallDiameterM")
        if gate is not None:
            if not (_num(gate) and float(gate) > 0):
                report.err("[activity_wall] %s.minBallDiameterM: expected a positive number, "
                           "got %r" % (where, gate))
            elif _num(start_diameter) and float(gate) <= float(start_diameter):
                report.warn("[activity_wall_gate] %s.minBallDiameterM: expected > the start "
                            "diameter %r (otherwise the gate is open from frame one), got %r"
                            % (where, start_diameter, gate))
    if isinstance(cap, int) and segments > cap:
        report.err("[activity_wall_capacity] meta.caps.activityWalls: expected >= %d segments, "
                   "got %d" % (segments, cap))
    kill = activity.get("killPlane")
    if not isinstance(kill, dict):
        report.err("[activity_kill_plane] activityArea.killPlane: expected an object, got %r"
                   % (type(kill).__name__,))
        return
    _unknown(report, kill, KILL_KEYS, "killPlane")
    y = kill.get("y")
    ground = meta.get("groundY")
    floor = None
    if _num(ground):
        floor = float(ground)
    if _num(lowest_y):
        floor = min(floor, float(lowest_y)) if floor is not None else float(lowest_y)
    if not _num(y):
        report.err("[activity_kill_plane] activityArea.killPlane.y: expected a number, got %r"
                   % (y,))
    elif floor is not None and float(y) >= floor:
        report.err("[activity_kill_plane] activityArea.killPlane.y: expected < %r (below "
                   "groundY and the terrain's lowest point), got %r" % (floor, y))
    respawn = kill.get("respawn")
    if respawn != "ballStart" and not _vec(respawn):
        report.err("[activity_kill_plane] activityArea.killPlane.respawn: expected 'ballStart' "
                   "or [x,y,z], got %r" % (respawn,))
    zone_id = kill.get("zoneId")
    if zone_id is not None and (not isinstance(zone_id, int) or isinstance(zone_id, bool)):
        report.err("[activity_kill_plane] activityArea.killPlane.zoneId: expected a whole "
                   "number, got %r" % (zone_id,))


def _self_intersects(points):
    """True when a closed polygon's non-adjacent edges cross."""
    count = len(points)
    if count < 4:
        return False

    def cross(o, a, b):
        return ((a[0] - o[0]) * (b[1] - o[1])) - ((a[1] - o[1]) * (b[0] - o[0]))

    def intersects(p1, p2, p3, p4):
        d1 = cross(p3, p4, p1)
        d2 = cross(p3, p4, p2)
        d3 = cross(p1, p2, p3)
        d4 = cross(p1, p2, p4)
        return (d1 * d2 < 0) and (d3 * d4 < 0)

    for i in range(count):
        a1, a2 = points[i], points[(i + 1) % count]
        for j in range(i + 2, count):
            if i == 0 and j == count - 1:
                continue
            b1, b2 = points[j], points[(j + 1) % count]
            if intersects(a1, a2, b1, b2):
                return True
    return False


# -- defs ------------------------------------------------------------------- #
def _check_defs(document, report, catalog):
    defs = document.get("defs")
    if not isinstance(defs, list):
        report.err("[def_key] defs: expected an array, got %r" % (type(defs).__name__,))
        return
    seen = set()
    names = set()
    for entry in defs:
        if isinstance(entry, dict):
            name = entry.get("id")
            if not isinstance(name, str) or not name.strip():
                report.err("[def_key] defs[].id: expected a non-empty string, got %r" % (name,))
                continue
            if name in seen:
                report.err("[def_key] defs[%r]: expected a unique id, got a duplicate" % name)
            seen.add(name)
    for index, entry in enumerate(defs):
        where = "defs[%d]" % index
        if not isinstance(entry, dict):
            report.err("[def_key] %s: expected an object, got %r" % (where, entry))
            continue
        _unknown(report, entry, DEF_KEYS, "defs")
        name = entry.get("id")
        if not isinstance(name, str) or not name.strip():
            continue
        where = "defs[%r]" % name
        model = entry.get("model")
        if not isinstance(model, str) or not model:
            report.err("[def_model] %s.model: expected an Assets/ relative path, got %r"
                       % (where, model))
        elif model.startswith("/") or ".." in Path(model).parts or not model.startswith("Assets/"):
            report.err("[def_model] %s.model: expected a path under Assets/ without '..', got %r"
                       % (where, model))
        elif names and model in names:
            pass
        volume_k = entry.get("volumeK")
        if volume_k is not None:
            reason = core.volume_k_error(volume_k)
            if reason:
                report.err("[def_volume_k] %s.volumeK: %s (got %r)" % (where, reason, volume_k))
        penalty = entry.get("volumePenalty")
        if penalty is not None:
            reason = core.volume_penalty_error(penalty)
            if reason:
                report.err("[def_volume_penalty] %s.volumePenalty: %s (got %r)"
                           % (where, reason, penalty))
        if entry.get("scalePolicy") not in (None, "uniform", "nonuniform"):
            report.err("[def_scale_policy] %s.scalePolicy: expected uniform/nonuniform, got %r"
                       % (where, entry.get("scalePolicy")))
        speed = entry.get("speedIndex")
        if speed is not None and (not isinstance(speed, int) or isinstance(speed, bool)
                                  or not 0 <= speed <= 11):
            report.err("[def_speed_index] %s.speedIndex: expected an integer 0..11, got %r"
                       % (where, speed))
        clips = entry.get("clips")
        if clips is not None:
            if not isinstance(clips, list):
                report.err("[def_clips] %s.clips: expected an array, got %r"
                           % (where, type(clips).__name__))
            else:
                if len(clips) > 6:
                    report.err("[def_clips] %s.clips: expected <= 6 clips, got %d"
                               % (where, len(clips)))
                seen_clips = set()
                for clip in clips:
                    if not isinstance(clip, dict):
                        report.err("[def_clips] %s.clips[]: expected an object, got %r"
                                   % (where, clip))
                        continue
                    _unknown(report, clip, CLIP_KEYS, "clips")
                    clip_id = clip.get("id")
                    if not isinstance(clip_id, str) or not clip_id.strip():
                        report.err("[def_clips] %s.clips[].id: expected a non-empty string, "
                                   "got %r" % (where, clip_id))
                    elif clip_id in seen_clips:
                        report.err("[def_clips] %s.clips[].id: expected a unique id, got %r"
                                   % (where, clip_id))
                    else:
                        seen_clips.add(clip_id)
                    fps = clip.get("fps")
                    if fps is not None and (not _num(fps) or float(fps) <= 0):
                        report.err("[def_clips] %s.clips[%r].fps: expected > 0, got %r"
                                   % (where, clip_id, fps))
                    if not clip.get("asset"):
                        report.warn("[def_clips] %s.clips[%r].asset: expected an animation "
                                    "asset, got nothing (stored as a spec only)" % (where, clip_id))
        collider = entry.get("collider")
        if collider is not None:
            for problem in collider_proxy.collider_schema_errors(collider):
                report.err("[collider] %s.collider: %s" % (where, problem))


# -- motions / paths -------------------------------------------------------- #
def _check_motions_paths(document, report):
    motions = document.get("motions")
    paths = document.get("paths")
    motion_ids = set()
    if not isinstance(motions, list):
        report.err("[motion_key] motions: expected an array, got %r" % (type(motions).__name__,))
        motions = []
    if not isinstance(paths, list):
        report.err("[path_key] paths: expected an array, got %r" % (type(paths).__name__,))
        paths = []
    path_ids = set()
    for index, path in enumerate(paths):
        where = "paths[%d]" % index
        if not isinstance(path, dict):
            report.err("[path_key] %s: expected an object, got %r" % (where, path))
            continue
        _unknown(report, path, PATH_KEYS, "paths")
        path_id = path.get("id")
        if not isinstance(path_id, str) or not path_id.strip():
            report.err("[paths_id] %s.id: expected a non-empty string, got %r" % (where, path_id))
        elif path_id in path_ids:
            report.err("[paths_id] %s.id: expected a unique id, got %r (duplicate)"
                       % (where, path_id))
        else:
            path_ids.add(path_id)
        points = path.get("points")
        if not (isinstance(points, list) and 2 <= len(points) <= 64
                and all(_vec(point) for point in points)):
            report.err("[path_points] %s.points: expected 2..64 [x,y,z] points, got %r"
                       % (where, points if not isinstance(points, list) else len(points)))
        else:
            for i in range(1, len(points)):
                gap = math.dist(points[i - 1], points[i])
                if gap < 0.05:
                    report.err("[path_points] %s.points[%d]: expected adjacent points >= 0.05 m "
                               "apart, got %.4f" % (where, i, gap))
        speed = path.get("speedCmPerSec")
        if speed is not None and not (_num(speed) and 0 <= float(speed) <= 500):
            report.err("[path_speed] %s.speedCmPerSec: expected 0..500, got %r" % (where, speed))
    motions_by_id = {}
    for index, motion in enumerate(motions):
        where = "motions[%d]" % index
        if not isinstance(motion, dict):
            report.err("[motion_key] %s: expected an object, got %r" % (where, motion))
            continue
        _unknown(report, motion, MOTION_KEYS, "motions")
        motion_id = motion.get("id")
        if not isinstance(motion_id, str) or not motion_id.strip():
            report.err("[motion_id] %s.id: expected a non-empty string, got %r"
                       % (where, motion_id))
            continue
        where = "motions[%r]" % motion_id
        if motion_id in motion_ids:
            report.err("[motion_id] %s: expected a unique id, got a duplicate" % where)
        motion_ids.add(motion_id)
        motions_by_id[motion_id] = motion
        motion_class = motion.get("class")
        if motion_class not in MOTION_CLASSES:
            report.err("[motion_class] %s.class: expected one of %s, got %r"
                       % (where, "/".join(MOTION_CLASSES), motion_class))
        speed = motion.get("speed")
        if not isinstance(speed, dict):
            report.err("[motion_speed] %s.speed: expected an object, got %r"
                       % (where, type(speed).__name__))
        else:
            _unknown(report, speed, SPEED_KEYS, "speed")
            source = speed.get("source")
            if source not in SPEED_SOURCES:
                report.err("[motion_speed] %s.speed.source: expected one of %s, got %r"
                           % (where, "/".join(SPEED_SOURCES), source))
            cm = speed.get("cmPerSec")
            if source == "explicit" and not _num(cm):
                report.err("[motion_speed] %s.speed.cmPerSec: expected a number for "
                           "'explicit', got %r" % (where, cm))
            if cm is not None and (not _num(cm) or not 0 <= float(cm) <= 500):
                report.err("[motion_speed] %s.speed.cmPerSec: expected 0..500, got %r"
                           % (where, cm))
            if source in ("none", "type") and cm is not None:
                report.err("[motion_speed] %s.speed.cmPerSec: expected nothing for source %r, "
                           "got %r" % (where, source, cm))
        path_ref = motion.get("path")
        if motion_class in PATH_CLASSES:
            if not isinstance(path_ref, str) or not path_ref:
                report.err("[motion_path] %s.path: expected a path id for class %r, got %r"
                           % (where, motion_class, path_ref))
            elif path_ref not in path_ids:
                report.err("[motion_path] %s.path: expected a declared path id, got %r"
                           % (where, path_ref))
        elif path_ref is not None:
            report.err("[motion_path] %s.path: expected nothing for class %r, got %r"
                       % (where, motion_class, path_ref))
        params = motion.get("params")
        if params is not None and not isinstance(params, dict):
            report.err("[motion_params] %s.params: expected an object, got %r"
                       % (where, type(params).__name__))
        elif isinstance(params, dict):
            for key, value in params.items():
                if not _num(value):
                    report.err("[motion_params] %s.params[%r]: expected a finite number, got %r"
                               % (where, key, value))
        rate = motion.get("updatesPerSecond")
        if rate is not None:
            if not (_num(rate) and float(rate) > 0):
                report.err("[motion_rate] %s.updatesPerSecond: expected > 0, got %r"
                           % (where, rate))
            elif motion_class in MOTION_RATE_HINT and float(rate) != MOTION_RATE_HINT[motion_class]:
                report.warn("[motion_rate] %s.updatesPerSecond: expected the guided %g Hz for "
                            "class %r (allowed, but say why), got %r"
                            % (where, MOTION_RATE_HINT[motion_class], motion_class, rate))
        provenance = motion.get("provenance")
        if provenance is not None:
            if not isinstance(provenance, dict):
                report.err("[motion_key] %s.provenance: expected an object, got %r"
                           % (where, type(provenance).__name__))
            else:
                _unknown(report, provenance, PROVENANCE_KEYS, "provenance")
    return motions_by_id, path_ids


# -- props ------------------------------------------------------------------ #
def _check_props(document, report, catalog, motion_ids):
    props = document.get("props")
    if not isinstance(props, list):
        report.err("[prop_key] props: expected an array, got %r" % (type(props).__name__,))
        return {}, []
    by_id = {}
    for index, prop in enumerate(props):
        if not isinstance(prop, dict):
            report.err("[prop_key] props[%d]: expected an object, got %r" % (index, prop))
            continue
        _unknown(report, prop, PROP_KEYS, "props")
        item_id = prop.get("itemId")
        if not isinstance(item_id, str) or not item_id.strip():
            report.err("[prop_id] props[%d].itemId: expected a non-empty string, got %r"
                       % (index, item_id))
            continue
        if item_id != item_id.strip():
            report.err("[prop_id] props[%r].itemId: expected no surrounding whitespace, got %r"
                       % (item_id, item_id))
        if item_id in by_id:
            report.err("[prop_id] props[%r].itemId: expected a unique id, got a duplicate"
                       % item_id)
            continue
        by_id[item_id] = prop

    boxes = {}
    instance_indices = {}
    structures = {solid.get("id") for solid in
                  (document.get("structures") or {}).get("solids") or []
                  if isinstance(solid, dict)}
    ceiling = design_ceiling_m(document)
    zone_ids = {zone.get("id") for zone in (document.get("meta") or {}).get("zones") or []
                if isinstance(zone, dict)}
    defs = catalog
    for item_id, prop in by_id.items():
        where = "props[%r]" % item_id
        name = prop.get("def")
        definition = defs.get(name) if isinstance(name, str) else None
        if definition is None:
            report.err("[prop_def] %s.def: expected a declared def id, got %r" % (where, name))
        if not isinstance(prop.get("kind"), str) or not prop["kind"].strip():
            report.err("[prop_kind] %s.kind: expected a non-empty string, got %r"
                       % (where, prop.get("kind")))
        behaviour = prop.get("behaviour")
        if behaviour not in BEHAVIOURS:
            report.err("[prop_behaviour] %s.behaviour: expected one of %s, got %r"
                       % (where, "/".join(BEHAVIOURS), behaviour))
        motion = prop.get("motion")
        if behaviour in ("motion", "ambient"):
            if not isinstance(motion, str) or not motion:
                report.err("[prop_behaviour] %s.motion: expected a motion id for behaviour %r, "
                           "got %r" % (where, behaviour, motion))
            elif motion not in motion_ids:
                report.err("[prop_motion] %s.motion: expected a declared motion id, got %r"
                           % (where, motion))
        elif motion is not None:
            report.err("[prop_behaviour] %s.motion: expected nothing for behaviour %r, got %r"
                       % (where, behaviour, motion))
        if behaviour == "ambient" and prop.get("absorbable") is True:
            report.err("[prop_behaviour] %s.absorbable: expected false for a behaviour "
                       "'ambient' prop, got true" % where)
        inherit = prop.get("inherit")
        parent = prop.get("parent")
        if inherit is not None and inherit not in INHERIT_VALUES:
            report.err("[prop_inherit] %s.inherit: expected one of %s, got %r"
                       % (where, "/".join(INHERIT_VALUES), inherit))
        if inherit in ("full", "position") and not parent:
            report.err("[prop_inherit] %s.inherit: expected nothing (or 'none') without a "
                       "parent, got %r" % (where, inherit))
        if prop.get("rigidGroup") is not None and parent:
            report.err("[prop_parent] %s: expected either parent or rigidGroup, got both"
                       % where)
        if prop.get("rigidGroup") is not None and motion:
            report.err("[prop_motion_group] %s.motion: expected no motion on a rigidGroup "
                       "member, got %r" % (where, motion))
        if parent is not None and (not isinstance(parent, str) or not parent):
            report.err("[prop_parent] %s.parent: expected an itemId or nothing, got %r"
                       % (where, parent))
        if prop.get("zone") is not None and prop["zone"] not in zone_ids:
            report.err("[prop_zone] %s.zone: expected a declared meta.zones id, got %r"
                       % (where, prop["zone"]))
        if prop.get("stackedOn") is not None and prop.get("support") is not None:
            report.err("[prop_support] %s: expected at most one of support/stackedOn, got both"
                       % where)
        if prop.get("floatingOk") is not None and not isinstance(prop["floatingOk"], bool):
            report.err("[prop_float] %s.floatingOk: expected a bool, got %r"
                       % (where, prop.get("floatingOk")))
        position = prop.get("position")
        if not _vec(position):
            report.err("[prop_position] %s.position: expected 3 numbers, got %r"
                       % (where, position))
        quat = prop.get("rotationQuat")
        if not _unit(quat):
            report.err("[prop_rotation] %s.rotationQuat: expected a unit quaternion, got %r"
                       % (where, quat))
        elif float(quat[3]) < -1e-9:
            report.err("[prop_rotation] %s.rotationQuat: expected the canonical sign (w >= 0), "
                       "got w=%r" % (where, quat[3]))
        aabb = prop.get("aabb")
        if not _vec(aabb):
            report.err("[prop_aabb] %s.aabb: expected 3 numbers, got %r" % (where, aabb))
        elif any(float(value) <= 0 for value in aabb):
            report.err("[prop_aabb] %s.aabb: expected positive sides, got %r" % (where, aabb))
        scale = prop.get("scale")
        if scale is not None:
            if not _vec(scale) or any(not _num(value) or float(value) <= 0 for value in scale):
                report.err("[prop_scale] %s.scale: expected 3 positive numbers, got %r"
                           % (where, scale))
            else:
                uniform = len({round(float(value), 6) for value in scale}) == 1
                if parent and not uniform:
                    report.err("[prop_scale_attached] %s.scale: expected a uniform scale on an "
                               "attached prop, got %r" % (where, scale))
                if not uniform:
                    # §6.4 ③: non-uniform is only allowed with no parent, a static behaviour
                    # and a box/sphere proxy.  An *absent* collider is allowed too: that is
                    # exactly D-5's handling of the original's one non-uniform instance
                    # ("照写 scale、标 absorbable:false、不写 collider、按无碰撞处理"), and a prop
                    # with no proxy has nothing whose contact shape could go wrong.
                    collider_type = ((definition or {}).get("collider") or {}).get("type")
                    if parent or behaviour != "static" \
                            or collider_type not in (None, "box", "sphere"):
                        report.err("[prop_scale_nonuniform] %s.scale: expected no parent, "
                                   "behaviour 'static' and a box/sphere (or no) collider, got "
                                   "%r (parent %r, behaviour %r, collider %r)"
                                   % (where, scale, parent, behaviour, collider_type))
                    elif collider_type is None:
                        report.note("%s: non-uniform scale with no collider proxy (D-5: kept "
                                    "out of contact and out of the absorb gate, information "
                                    "only)" % item_id)
        # ``instanceIndex`` is reference-import provenance: the original's spawn-record index.
        # It must be a distinct non-negative integer; there is deliberately **no upper bound**
        # (a level may reference records the extractor no longer lists) - see the importer's
        # ``[prop_instance_index]`` rule.
        if "instanceIndex" in prop:
            index_value = prop["instanceIndex"]
            if not isinstance(index_value, int) or isinstance(index_value, bool) \
                    or index_value < 0:
                report.err("[prop_instance_index] %s.instanceIndex: expected a whole number "
                           ">= 0 (the original's spawn-record index), got %r"
                           % (where, index_value))
            else:
                owner = instance_indices.get(index_value)
                if owner is not None:
                    report.err("[prop_instance_index] %s.instanceIndex: expected a unique "
                               "index, got %r (already used by %r)" % (where, index_value, owner))
                else:
                    instance_indices[index_value] = item_id
        if prop.get("phase") is not None and not (_num(prop["phase"])
                                                  and 0.0 <= float(prop["phase"]) <= 1.0):
            report.err("[prop_phase] %s.phase: expected 0..1, got %r" % (where, prop.get("phase")))
        clip_start = prop.get("clipStart")
        if clip_start is not None:
            clips = (definition or {}).get("clips") or []
            if not isinstance(clip_start, int) or isinstance(clip_start, bool) or clip_start < -1:
                report.err("[prop_clip] %s.clipStart: expected an integer >= -1, got %r"
                           % (where, clip_start))
            elif clip_start >= 0 and clip_start >= len(clips):
                report.err("[prop_clip] %s.clipStart: expected < %d (the def's clips), got %r"
                           % (where, len(clips), clip_start))
        supports = [prop.get("support"), prop.get("stackedOn")]
        for support in supports:
            if support is None:
                continue
            if support not in structures and support not in by_id:
                report.err("[prop_support] %s.support: expected a structure id or a prop "
                           "itemId, got %r" % (where, support))
        for key in ("volumeK", "volumePenalty"):
            if key == "volumeK" and prop.get("volumeK") is not None:
                reason = core.volume_k_error(prop["volumeK"])
                if reason:
                    report.err("[prop_volume_k] %s.volumeK: %s (got %r)"
                               % (where, reason, prop["volumeK"]))
            if key == "volumePenalty" and prop.get("volumePenalty") is not None:
                reason = core.volume_penalty_error(prop["volumePenalty"])
                if reason:
                    report.err("[prop_volume_penalty] %s.volumePenalty: %s (got %r)"
                               % (where, reason, prop["volumePenalty"]))
        # derived columns: the repo's growthMm3 and the doc's volumeMm3/absorbDiameterReqMm
        if _vec(aabb) and all(float(value) > 0 for value in aabb):
            size = tuple(float(value) for value in aabb)
            volume_k = core.effective_volume_k(prop, defs)
            volume_mm3 = volume_k * size[0] * size[1] * size[2] * 1e9
            declared_volume = prop.get("volumeMm3")
            if declared_volume is not None:
                if not isinstance(declared_volume, int) or isinstance(declared_volume, bool):
                    report.err("[prop_volume_stale] %s.volumeMm3: expected a whole number of "
                               "mm3, got %r" % (where, declared_volume))
                elif abs(float(declared_volume) - volume_mm3) > 1.0:
                    report.err("[prop_volume_stale] %s.volumeMm3: expected %d (k=%r x aabb), "
                               "got %r" % (where, round(volume_mm3), volume_k, declared_volume))
                else:
                    volume_mm3 = float(declared_volume)
            gate = core.absorb_diameter_req_mm(size, volume_k)
            declared_gate = prop.get("absorbDiameterReqMm")
            if declared_gate is not None and declared_gate != gate:
                report.err("[prop_absorb_req_stale] %s.absorbDiameterReqMm: expected %d "
                           "(from volumeMm3 %d, k=%r), got %r"
                           % (where, gate, round(volume_mm3), volume_k, declared_gate))
            declared_growth = prop.get("growthMm3")
            if declared_growth is not None:
                penalty = core.effective_volume_penalty(prop, defs)
                growth = core.item_growth_mm3(size, volume_k, penalty)
                if declared_growth != growth:
                    report.err("[prop_growth_stale] %s.growthMm3: expected %d "
                               "(penalty %r x k %r x aabb), got %r"
                               % (where, growth, penalty, volume_k, declared_growth))
            if declared_gate is None and declared_growth is None and declared_volume is None:
                report.err("[prop_derived_missing] %s: expected at least one derived column "
                           "(volumeMm3 / absorbDiameterReqMm / growthMm3), got none" % where)
            # absorbable is an authorial declaration in v2: surface a disagreement, do not fail
            if isinstance(prop.get("absorbable"), bool) and declared_gate is not None:
                gate_says = gate <= core.floor_mm(ceiling)
                if prop["absorbable"] != gate_says:
                    report.note("%s: absorbable=%r but the gate at %.2f m says %r "
                                "(authorial in v2; information only)"
                                % (item_id, prop["absorbable"], ceiling, gate_says))
        if not isinstance(prop.get("absorbable"), bool):
            report.err("[prop_absorbable] %s.absorbable: expected a bool, got %r"
                       % (where, prop.get("absorbable")))
        if _vec(position) and _vec(aabb) and _unit(quat):
            boxes[item_id] = core.world_aabb(tuple(float(v) for v in position),
                                             tuple(float(v) for v in aabb),
                                             tuple(float(v) for v in quat))
    # bounds
    bounds = (document.get("meta") or {}).get("bounds")
    if isinstance(bounds, list) and len(bounds) == 2 and all(_vec(point, 2) for point in bounds):
        low_x, low_z = float(bounds[0][0]), float(bounds[0][1])
        high_x, high_z = float(bounds[1][0]), float(bounds[1][1])
        outside = 0
        for item_id, box in boxes.items():
            over = max(low_x - box[0][0], box[1][0] - high_x,
                       low_z - box[0][2], box[1][2] - high_z, 0.0)
            if over > BOUNDS_TOLERANCE_M:
                outside += 1
                if outside <= 8:
                    report.err("[prop_bounds] props[%r]: expected inside meta.bounds "
                               "(x %r..%r, z %r..%r), got %r by %.3f m outside"
                               % (item_id, low_x, high_x, low_z, high_z, box, over))
        if outside > 8:
            report.err("[prop_bounds] props: expected every prop inside meta.bounds, got %d "
                       "prop(s) outside (8 listed)" % outside)
    return boxes, list(by_id.values())


def _check_support_and_overlap(document, props, boxes, report):
    if not boxes:
        return
    by_id = {prop.get("itemId"): prop for prop in props}
    expected = (document.get("meta") or {}).get("expected") or {}
    budget = expected.get("overlapBudget")
    if budget is not None and (not isinstance(budget, int) or isinstance(budget, bool)
                               or budget < 0):
        report.err("[prop_overlap] meta.expected.overlapBudget: expected a whole number >= 0, "
                   "got %r" % (budget,))
        budget = None

    def exempt(left_id, right_id):
        left = by_id.get(left_id) or {}
        right = by_id.get(right_id) or {}
        if left.get("parent") == right_id or right.get("parent") == left_id:
            return True
        if left.get("stackedOn") == right_id or right.get("stackedOn") == left_id:
            return True
        group = left.get("rigidGroup")
        return bool(isinstance(group, str) and group and group == right.get("rigidGroup"))

    pairs = core.overlap_scan(boxes, exempt)
    if budget is None:
        for left_id, right_id, fraction in pairs[:DETAIL_LIMIT]:
            report.err("[prop_overlap] props[%r] + props[%r]: expected no interpenetration "
                       "above %.0f%% of the smaller AABB, got %.0f%%"
                       % (left_id, right_id, core.OVERLAP_FRACTION * 100.0, fraction * 100.0))
        if len(pairs) > DETAIL_LIMIT:
            report.err("[prop_overlap] props: expected no further interpenetrations, got %d more"
                       % (len(pairs) - DETAIL_LIMIT))
    else:
        examples = ", ".join("%s+%s" % pair[:2] for pair in pairs[:3])
        if len(pairs) > budget:
            report.err("[prop_overlap] meta.expected.overlapBudget: expected >= %d pairs, got "
                       "%d (%+d)%s" % (len(pairs), budget, len(pairs) - budget,
                                       ("; e.g. " + examples) if examples else ""))
        else:
            report.warn("[prop_overlap] meta.expected.overlapBudget: %d pair(s) declared, %d "
                        "measured%s" % (budget, len(pairs),
                                        ("; e.g. " + examples) if examples else ""))
    # scenery solids: v2 has no rule for a prop inside a structure box; report the count with
    # the *same* corner-depth rule v1 uses, so the number means the same thing in both reports.
    solids_by_id = {solid.get("id"): solid for solid in
                    (document.get("structures") or {}).get("solids") or []
                    if isinstance(solid, dict)}
    if solids_by_id:
        ceiling = design_ceiling_m(document) / 2.0
        proxied = core.structure_penetrations(props, boxes, solids_by_id, ceiling,
                                              proxy="prefer")
        boxed = core.structure_penetrations(props, boxes, solids_by_id, ceiling,
                                            proxy="ignore")
        if proxied or boxed:
            worst = max(hit[2] for hit in proxied) if proxied else 0.0
            report.note("%d prop(s) sink into a scenery solid's fitted proxy (deepest %.3f m; "
                        "the raw-AABB criterion would count %d). §9.2 has no rule for "
                        "prop-inside-structure - only prop/prop overlap is an error - so this is "
                        "a note, see Docs/OPEN_DECISIONS.md"
                        % (len(proxied), worst, len(boxed)))


def _check_parents(document, props, report):
    by_id = {prop.get("itemId"): prop for prop in props}
    for item_id, prop in by_id.items():
        parent = prop.get("parent")
        if not isinstance(parent, str) or not parent:
            continue
        if parent not in by_id:
            report.err("[prop_parent] props[%r].parent: expected a prop in this document, got "
                       "%r (not found)" % (item_id, parent))
            continue
        if parent == item_id:
            report.err("[prop_parent] props[%r].parent: expected another prop, got itself"
                       % item_id)
            continue
        seen = {item_id}
        cursor = parent
        while isinstance(cursor, str) and cursor:
            if cursor in seen:
                report.err("[prop_parent] props[%r].parent: expected an acyclic chain, got a "
                           "cycle through %r" % (item_id, cursor))
                break
            seen.add(cursor)
            cursor = (by_id.get(cursor) or {}).get("parent")


# -- expected totals -------------------------------------------------------- #
def _check_expected(document, meta, props, boxes, report):
    expected = meta.get("expected")
    if not isinstance(expected, dict):
        return
    count = len(props)
    wanted = expected.get("propCount")
    if wanted is not None and wanted != count:
        report.err("[expected_prop_count] meta.expected.propCount: expected %d, got %r (%+d)"
                   % (count, wanted, count - wanted))
    growth = sum(int(prop.get("growthMm3") or 0) for prop in props)
    wanted_growth = expected.get("growthMm3")
    if wanted_growth is not None and wanted_growth != growth:
        report.err("[expected_growth] meta.expected.growthMm3: expected %d, got %r (%+d)"
                   % (growth, wanted_growth, growth - wanted_growth))
    solids = (document.get("structures") or {}).get("solids") or []
    wanted_solids = expected.get("structureCount")
    if wanted_solids is not None and wanted_solids != len(solids):
        report.err("[expected_structure_count] meta.expected.structureCount: expected %d, "
                   "got %r (%+d)" % (len(solids), wanted_solids, len(solids) - wanted_solids))
    wanted_obbs = expected.get("staticObbs")
    if wanted_obbs is not None and wanted_obbs != len(solids):
        report.err("[expected_static_obbs] meta.expected.staticObbs: expected %d, got %r (%+d)"
                   % (len(solids), wanted_obbs, len(solids) - wanted_obbs))
    terrain = document.get("terrain") or {}
    triangles = (terrain.get("triangles") or {}).get("count")
    wanted_triangles = expected.get("terrainTriangles")
    if wanted_triangles is not None and triangles is not None \
            and wanted_triangles != triangles:
        report.err("[expected_terrain_triangles] meta.expected.terrainTriangles: expected %d, "
                   "got %r (%+d)" % (triangles, wanted_triangles, triangles - wanted_triangles))
    wanted_units = expected.get("valueUnits")
    if wanted_units is not None:
        units = sum(int(prop.get("valueUnits") or 0) for prop in props)
        if units and wanted_units != units:
            report.err("[expected_value_units] meta.expected.valueUnits: expected %d, got %r "
                       "(%+d)" % (units, wanted_units, units - wanted_units))


# -- caps ------------------------------------------------------------------- #
def _check_caps(document, meta, report, terrain):
    caps = meta.get("caps") or {}
    props = document.get("props") or []
    count = len(props) if isinstance(props, list) else 0
    cap = caps.get("props")
    if isinstance(cap, int) and count > cap:
        report.err("[caps_props] meta.caps.props: expected >= %d, got %d" % (count, cap))
    moving = caps.get("movingProps")
    animators = caps.get("animators")
    if isinstance(moving, int) and moving > count:
        report.err("[caps_motion] meta.caps.movingProps: expected <= props %d, got %d"
                   % (count, moving))
    if isinstance(animators, int) and animators > count:
        report.err("[caps_motion] meta.caps.animators: expected <= props %d, got %d"
                   % (count, animators))
    # anim_budget: instances that actually need an Animator (behaviour motion + a clip index)
    defs = _def_catalog(document)
    needing = 0
    for prop in props:
        if not isinstance(prop, dict):
            continue
        if prop.get("behaviour") in ("motion", "ambient") and isinstance(prop.get("clipStart"), int) \
                and prop["clipStart"] >= 0:
            clips = (defs.get(prop.get("def")) or {}).get("clips") or []
            if clips:
                needing += 1
    if isinstance(animators, int) and needing > animators:
        report.warn("[anim_budget] meta.caps.animators: expected >= %d animated instance(s), "
                    "got %d (the rest must be vertex/material animation or static)"
                    % (needing, animators))
    terrain_triangles = (terrain or {}).get("triangles") or {}
    if terrain_triangles.get("count") is not None:
        cap = caps.get("terrainTriangles")
        if isinstance(cap, int) and terrain_triangles["count"] > cap:
            report.err("[terrain_budget] meta.caps.terrainTriangles: expected >= %d, got %d"
                       % (terrain_triangles["count"], cap))


# --------------------------------------------------------------------------- #
# summaries for the CLI
# --------------------------------------------------------------------------- #
def summary_lines(document):
    """The human banner ``levelbuild.py check`` prints for a v2 document."""
    meta = document.get("meta") or {}
    ball = meta.get("ball") or {}
    terrain = document.get("terrain") or {}
    triangles = (terrain.get("triangles") or {}).get("count")
    solids = (document.get("structures") or {}).get("solids") or []
    props = document.get("props") or []
    absorbable = sum(1 for prop in props if isinstance(prop, dict) and prop.get("absorbable"))
    parents = sum(1 for prop in props if isinstance(prop, dict) and prop.get("parent"))
    zones = meta.get("zones") or []
    lines = [
        "schema=%s  level=%s  scene=%s" % (document.get("schema"), meta.get("id"),
                                           meta.get("scene")),
        "ball %.3f -> %s m  (%s)  timeLimit %ss  groundY %s"
        % (float(ball.get("startDiameterM") or 0.0),
           ball.get("targetDiameterM"), ball.get("maxDiameterRef"),
           meta.get("timeLimitS"), meta.get("groundY")),
        "props %d (%d absorbable, %d with a parent)  defs %d  zones %d"
        % (len(props), absorbable, parents, len(document.get("defs") or []), len(zones)),
        "structures %d  terrain %s triangles  chunks %d  motions %d  paths %d"
        % (len(solids), triangles if triangles is not None else "-",
           len(terrain.get("chunks") or []), len(document.get("motions") or []),
           len(document.get("paths") or [])),
    ]
    expected = meta.get("expected") or {}
    if expected:
        lines.append("expected " + ", ".join("%s=%s" % (key, expected[key])
                                             for key in EXPECTED_KEYS if key in expected))
    return lines
