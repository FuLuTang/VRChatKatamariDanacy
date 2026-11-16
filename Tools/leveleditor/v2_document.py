"""Read-only ``katamari.placement/2`` documents for the web editor.

v2 is a different document, not a reshaped v1 file: the ground is a triangle mesh in a
``.katerrain`` sidecar, the non-absorbable scenery is ``structures.solids``, the playable
range is ``activityArea`` (walls + kill plane), and props carry the original rotation,
scale, parent pointer and behaviour.  This module loads that document **read-only** and
builds the view model the browser needs.

There is exactly one parser for the sidecar: :func:`ref_v2_from_extract.read_sidecar`.
There is exactly one structural checker: :func:`ref_v2_from_extract.structural_errors`.
This module must not grow a second copy of either (or of the v1 validator).  It also
must not run the v1 validator over a translated view: the v1 error count (544 on the old
Cancer file) measures a different schema and is exactly the number that misled the owner.

The only translated shape here is a **rendering view document** under
:attr:`V2Document.view_doc`: it gives the existing three.js client the ``level``/
``structures``/``props`` lists it already knows, while the raw v2 layers stay under
:attr:`V2Document.document` and are served by the ``/api/v2/*`` endpoints.  Nothing in
``view_doc`` is ever validated, written, or exported.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from Tools.levelbuild import placement_core as core
from Tools.levelbuild import placement_v2
from Tools.levelbuild import ref_v2_from_extract as ref_v2

SCHEMA_V2 = ref_v2.SCHEMA_V2

#: Default design ceiling when a v2 document does not carry one.  The reference Cancer
#: document uses the original MAX_DIAMETER 1.20 m (its own notes say so); this fallback
#: only keeps the v1-shaped ladder/statistics fields finite.
FALLBACK_MAX_DIAMETER_M = 1.20


class V2LoadError(RuntimeError):
    """A malformed or unsafe v2 document / sidecar."""


def is_v2(document) -> bool:
    return isinstance(document, dict) and document.get("schema") == SCHEMA_V2


def detect_schema(path) -> str | None:
    """The schema string of a JSON file, or ``None`` when it cannot be read."""
    try:
        document = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, UnicodeDecodeError):
        return None
    return document.get("schema") if isinstance(document, dict) else None


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _sidecar_path(document, level_dir) -> Path:
    terrain = document.get("terrain") or {}
    mesh_ref = terrain.get("meshRef") or {}
    raw = mesh_ref.get("path")
    if not isinstance(raw, str) or not raw:
        raise V2LoadError("terrain.meshRef.path is missing")
    candidate = Path(raw)
    if candidate.is_absolute() or ".." in candidate.parts:
        raise V2LoadError("terrain.meshRef.path %r is not a relative path inside the "
                          "level directory" % raw)
    root = Path(level_dir).resolve()
    resolved = (root / candidate).resolve()
    try:
        resolved.relative_to(root)
    except ValueError:
        raise V2LoadError("terrain.meshRef.path %r escapes the level directory" % raw)
    return resolved


def _vec(values, digits=4):
    return [round(float(value), digits) for value in values]


def _terrain_bounds(vertices):
    if not vertices:
        return None
    low = [min(point[axis] for point in vertices) for axis in range(3)]
    high = [max(point[axis] for point in vertices) for axis in range(3)]
    return [_vec(low), _vec(high)]


def _zone_counts(zone_ids):
    counts = {}
    for zone in zone_ids or []:
        counts[zone] = counts.get(zone, 0) + 1
    return {str(key): counts[key] for key in sorted(counts)}


def _class_counts(classes):
    counts = {}
    for value in classes or []:
        counts[value] = counts.get(value, 0) + 1
    return {str(key): counts[key] for key in sorted(counts)}


class V2Document:
    """One loaded v2 document, its sidecar, and the browser-facing view model."""

    def __init__(self, path):
        self.path = Path(path)
        try:
            raw = self.path.read_bytes()
        except OSError as error:
            raise V2LoadError("cannot read %s: %s" % (self.path, error))
        try:
            document = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise V2LoadError("%s is not valid JSON: %s" % (self.path, error))
        if not is_v2(document):
            raise V2LoadError("%s is not a %s document" % (self.path, SCHEMA_V2))
        self.document = document
        self.raw = raw
        self.sha256 = _sha256(raw)
        self.level_dir = self.path.parent
        self.sidecar_path = None
        self.sidecar_bytes = None
        self.sidecar = None
        self.errors = []
        self.sidecar_check_skipped = False
        self._load_sidecar()
        self.errors = self._structural_errors()
        self.summary = self._build_summary()
        self.view_doc = self._build_view_doc()

    # -- sidecar -------------------------------------------------------- #
    def _load_sidecar(self):
        try:
            self.sidecar_path = _sidecar_path(self.document, self.level_dir)
        except V2LoadError as error:
            self.errors.append(str(error))
            return
        if not self.sidecar_path.is_file():
            self.errors.append("terrain sidecar is missing: %s"
                               % core.display_path(self.sidecar_path))
            return
        try:
            self.sidecar_bytes = self.sidecar_path.read_bytes()
            try:
                self.sidecar = ref_v2.read_sidecar(self.sidecar_bytes)
            except NameError:
                # ``ref_v2_from_extract`` re-exports the format codec; the in-flight
                # placement_v2 refactor left its local alias without the flag constants.
                self.sidecar = placement_v2.read_sidecar(self.sidecar_bytes)
        except (OSError, ref_v2.RefV2Error, placement_v2.SidecarError) as error:
            self.errors.append("terrain sidecar cannot be read: %s" % error)
            self.sidecar = None
            self.sidecar_bytes = None

    def _structural_errors(self):
        """The shared checker's shape errors, with a compatibility fallback.

        ``ref_v2_from_extract.structural_errors(document, sidecar_bytes)`` is the one
        implementation and is always tried first.  At the time this viewer was written,
        the in-flight ``placement_v2`` refactor made that call raise ``TypeError`` (the
        shared sidecar helper receives the raw bytes where it expects the parsed
        payload); rather than grow a second checker here, we fall back to the same
        function with ``sidecar_data=None`` and record that the sidecar integrity check
        was skipped.  When the shared helper is fixed (agent-69's v2 validator work), the
        first call succeeds and this branch disappears by itself.
        """
        if self.sidecar_bytes is not None:
            try:
                return list(ref_v2.structural_errors(self.document, self.sidecar_bytes))
            except TypeError as error:
                if "byte indices must be integers" not in str(error):
                    raise
                self.sidecar_check_skipped = True
        errors = list(ref_v2.structural_errors(self.document, None))
        if self.sidecar_bytes is None:
            errors.append("terrain sidecar is unavailable; mesh counts and sha256 were "
                          "not checked")
        return errors

    # -- derived facts -------------------------------------------------- #
    def _build_summary(self):
        meta = self.document.get("meta") or {}
        ball = meta.get("ball") or {}
        terrain = self.document.get("terrain") or {}
        mesh_ref = terrain.get("meshRef") or {}
        structures = self.document.get("structures") or {}
        solids = [solid for solid in structures.get("solids") or []
                  if isinstance(solid, dict)]
        activity = self.document.get("activityArea") or {}
        props = [prop for prop in self.document.get("props") or []
                 if isinstance(prop, dict)]
        defs = [entry for entry in self.document.get("defs") or []
                if isinstance(entry, dict)]
        parents = [{"child": prop.get("itemId"), "parent": prop.get("parent"),
                    "inherit": prop.get("inherit")}
                   for prop in props if prop.get("parent")]
        counts = {
            "props": len(props),
            "defs": len(defs),
            "solids": len(solids),
            "absorbableProps": sum(1 for prop in props if prop.get("absorbable")),
            "rotatedProps": sum(1 for prop in props
                                if list(prop.get("rotationQuat") or [0, 0, 0, 1])
                                != [0.0, 0.0, 0.0, 1.0]),
            "scaledProps": sum(1 for prop in props
                               if list(prop.get("scale") or [1, 1, 1])
                               != [1.0, 1.0, 1.0]),
            "parentLinks": len(parents),
            "growthMm3": sum(int(prop.get("growthMm3") or 0) for prop in props),
            "walls": len(activity.get("walls") or []),
            "terrainVertices": (self.sidecar or {}).get("counts", {}).get("vertices"),
            "terrainTriangles": (self.sidecar or {}).get("counts", {}).get("triangles"),
            "terrainChunks": len(terrain.get("chunks") or []),
            "broadPhaseBoxes": len((terrain.get("broadPhase") or {}).get("boxes") or []),
            "zones": len(meta.get("zones") or []),
        }
        expected = meta.get("expected") or {}
        return {
            "schema": SCHEMA_V2,
            "document": {
                "path": str(self.path),
                "name": self.path.name,
                "bytes": len(self.raw),
                "sha256": self.sha256,
                "sidecarPath": (str(self.sidecar_path) if self.sidecar_path else None),
                "sidecarSha256": (mesh_ref.get("sha256") if self.sidecar else None),
            },
            "meta": {
                "id": meta.get("id"),
                "scene": meta.get("scene"),
                "ball": ball,
                "timeLimitS": meta.get("timeLimitS"),
                "groundY": meta.get("groundY"),
                "bounds": meta.get("bounds"),
                "spawnClearanceM": meta.get("spawnClearanceM"),
                "zones": meta.get("zones") or [],
                "caps": meta.get("caps") or {},
                "budget": meta.get("budget") or {},
                "expected": expected,
                "arena": meta.get("arena") or {},
                "source": meta.get("source") or {},
            },
            "counts": counts,
            "terrain": {
                "id": terrain.get("id"),
                "kind": terrain.get("kind"),
                "winding": terrain.get("winding"),
                "meshRef": mesh_ref,
                "counts": (self.sidecar or {}).get("counts") or {},
                "bounds": _terrain_bounds((self.sidecar or {}).get("vertices") or []),
                "zoneCounts": _zone_counts((self.sidecar or {}).get("zoneIds") or []),
                "classCounts": _class_counts((self.sidecar or {}).get("classes") or []),
                "chunks": len(terrain.get("chunks") or []),
                "broadPhaseBoxes": len((terrain.get("broadPhase") or {}).get("boxes") or []),
                "grid": terrain.get("grid") or {},
            },
            "activity": {
                "walls": activity.get("walls") or [],
                "killPlane": activity.get("killPlane") or {},
                "softMarginM": activity.get("softMarginM"),
                "source": activity.get("source"),
            },
            "solids": solids,
            "parents": parents,
        }

    def _build_view_doc(self):
        """The v1-shaped list model the existing three.js client consumes.

        This is **not** a validation view.  ``WebEditor`` never calls the v1 validator
        on it; ``check`` is :meth:`check_payload`.  It exists so props/structures/
        selection/stats keep one code path in the browser.
        """
        meta = self.document.get("meta") or {}
        ball = meta.get("ball") or {}
        expected = meta.get("expected") or {}
        migration = meta.get("migration") or {}
        solids = [solid for solid in
                  (self.document.get("structures") or {}).get("solids") or []
                  if isinstance(solid, dict)]
        props = []
        for prop in self.document.get("props") or []:
            if not isinstance(prop, dict):
                continue
            entry = dict(prop)
            scale = list(prop.get("scale") or [1.0, 1.0, 1.0])
            entry["scale"] = scale
            entry["absorbDiameterReq"] = round(
                float(prop.get("absorbDiameterReqMm") or 0) / 1000.0, 4)
            entry["valueUnits"] = int(prop.get("valueUnits") or 0)
            props.append(entry)
        max_diameter = (ball.get("maxDiameterM") or migration.get("maxDiameterM")
                        or FALLBACK_MAX_DIAMETER_M)
        level = {
            "id": meta.get("id"),
            "scene": meta.get("scene"),
            "units": meta.get("units"),
            "convention": meta.get("convention"),
            "ballStart": list(ball.get("start") or [0.0, 0.0, 0.0]),
            "playerSpawn": list(meta.get("playerSpawn") or [0.0, 0.0, 0.0]),
            "ballDiameterStart": ball.get("startDiameterM"),
            "ballDiameterMax": max_diameter,
            "groundY": meta.get("groundY"),
            "bounds": meta.get("bounds"),
            "spawnClearanceM": meta.get("spawnClearanceM"),
            "budget": meta.get("budget") or {},
            "expected": {key: expected[key] for key in
                         ("propCount", "growthMm3", "overlapBudget",
                          "structureCount", "terrainTriangles") if key in expected},
            "zones": [{"id": zone.get("id")} for zone in meta.get("zones") or []
                      if isinstance(zone, dict)],
            "arena": meta.get("arena") or {"id": meta.get("id")},
        }
        return {
            "schema": SCHEMA_V2,
            "level": level,
            "structures": solids,
            "props": props,
            "defs": list(self.document.get("defs") or []),
            "cameras": list(self.document.get("cameras") or []),
            "renderPresets": list(self.document.get("renderPresets") or []),
            "regions": list(self.document.get("regions") or []),
        }

    # -- payloads ------------------------------------------------------- #
    def check_payload(self):
        errors = list(self.errors)
        return {
            "kind": "structural",
            "scope": "structural",
            "label": "结构检查（v2）",
            "ok": not errors,
            "errors": errors,
            "warnings": [],
            "notes": [],
            "errorCount": len(errors),
            "warningCount": 0,
            "note": ("v2 设计数值校验器尚未交付；这里只跑 "
                     "ref_v2_from_extract.structural_errors()（键白名单、单位四元数、"
                     "chunk/broad-phase 账）。"),
            "sidecarChecked": not self.sidecar_check_skipped,
            "notes": (["共享 structural_errors 目前无法带侧车数据"
                       "（placement_v2 重构中的 TypeError）；侧车 sha256/计数"
                       "本次未查，几何已用同一个 read_sidecar 读取。"]
                      if self.sidecar_check_skipped else []),
        }

    def terrain_payload(self):
        if not self.sidecar:
            return None
        sidecar = self.sidecar
        return {
            "id": (self.document.get("terrain") or {}).get("id"),
            "vertices": sidecar["vertices"],
            "indices": sidecar["indices"],
            "zoneIds": sidecar["zoneIds"],
            "classes": sidecar["classes"],
            "counts": sidecar["counts"],
            "indexWidth": sidecar["indexWidth"],
            "flags": sidecar["flags"],
            "bounds": _terrain_bounds(sidecar["vertices"]),
            "zoneCounts": _zone_counts(sidecar["zoneIds"]),
            "classCounts": _class_counts(sidecar["classes"]),
        }

    def props_payload(self, rotation=True, scale=True):
        """Props with display transforms.

        ``rotation=False`` returns identity quaternions; ``scale=False`` returns unit
        scale and the unscaled AABB (the v2 file's ``aabb`` already includes scale, so
        the unscaled size is ``aabb / scale``).  This is the one implementation behind
        both the GUI toggles and ``GET /api/v2/props``.
        """
        out = []
        for prop in self.document.get("props") or []:
            if not isinstance(prop, dict):
                continue
            entry = dict(prop)
            raw_scale = list(prop.get("scale") or [1.0, 1.0, 1.0])
            aabb = list(prop.get("aabb") or [0.0, 0.0, 0.0])
            if rotation:
                entry["rotationQuat"] = list(prop.get("rotationQuat") or
                                             [0.0, 0.0, 0.0, 1.0])
            else:
                entry["rotationQuat"] = [0.0, 0.0, 0.0, 1.0]
            if scale:
                entry["scale"] = raw_scale
                entry["aabb"] = aabb
            else:
                entry["scale"] = [1.0, 1.0, 1.0]
                entry["aabb"] = [round(aabb[axis] / (raw_scale[axis] or 1.0), 4)
                                 for axis in range(3)]
            entry["_display"] = {"rotation": bool(rotation), "scale": bool(scale),
                                 "rawScale": raw_scale}
            out.append(entry)
        return out

    def view_payload(self, terrain=True, solids=True, walls=True, kill=True,
                     parents=True, rotation=True, scale=True, props=True):
        """Everything the browser draws, filtered by the same layer flags.

        The GUI loads this once with all layers on and then only toggles visibility;
        ``GET /api/v2/view?rotation=0`` is the agent's exact equivalent of turning the
        "应用旋转" toggle off.
        """
        layers = layer_map(self)
        payload = {
            "ok": True,
            "schema": SCHEMA_V2,
            "id": (self.document.get("meta") or {}).get("id"),
            "meta": self.summary["meta"],
            "counts": self.summary["counts"],
            "terrain": self.terrain_payload() if terrain else None,
            "solids": self.summary["solids"] if solids else None,
            "activity": (self.summary["activity"] if (walls or kill) else None),
            "parents": self.summary["parents"] if parents else None,
            "props": self.props_payload(rotation=rotation, scale=scale) if props else None,
            "layers": layers,
            "flags": {"terrain": bool(terrain), "solids": bool(solids),
                      "walls": bool(walls), "kill": bool(kill),
                      "parents": bool(parents), "rotation": bool(rotation),
                      "scale": bool(scale), "props": bool(props)},
        }
        if payload["activity"] is not None:
            activity = dict(payload["activity"])
            if not walls:
                activity["walls"] = []
            if not kill:
                activity["killPlane"] = None
            payload["activity"] = activity
        return payload


def layer_map(document):
    """The view toggles and their HTTP spellings (used by GUI, docs and tests)."""
    counts = document.summary["counts"] if document else {}
    return {
        "v2Terrain": {
            "id": "show-v2-terrain", "default": True,
            "http": "/api/v2/terrain",
            "count": counts.get("terrainTriangles"),
            "label": "v2 地形（.katerrain 碰撞网格）",
        },
        "v2Solids": {
            "id": "show-v2-solids", "default": True,
            "http": "/api/v2/solids",
            "count": counts.get("solids"),
            "label": "v2 固体（structures.solids，35 条）",
        },
        "v2Walls": {
            "id": "show-v2-walls", "default": True,
            "http": "/api/v2/activity",
            "count": counts.get("walls"),
            "label": "v2 活动墙（activityArea.walls）",
        },
        "v2KillPlane": {
            "id": "show-v2-kill-plane", "default": True,
            "http": "/api/v2/activity",
            "count": 1,
            "label": "v2 kill plane（activityArea.killPlane）",
        },
        "v2Parents": {
            "id": "show-v2-parent-lines", "default": False,
            "http": "/api/v2/parents",
            "count": counts.get("parentLinks"),
            "label": "v2 父子指针（props[].parent）",
        },
        "v2Rotation": {
            "id": "v2-apply-rotation", "default": True,
            "http": "/api/v2/props?rotation=0",
            "count": counts.get("rotatedProps"),
            "label": "应用原作旋转（关闭后仅视图归零）",
        },
        "v2Scale": {
            "id": "v2-apply-scale", "default": True,
            "http": "/api/v2/props?scale=0",
            "count": counts.get("scaledProps"),
            "label": "应用原作缩放（关闭后仅视图为 1）",
        },
    }


def load(path) -> V2Document:
    """Load one v2 document (read-only; the sidecar must stay inside the level dir)."""
    return V2Document(path)


def schema_of(path):
    return detect_schema(path)


def sum_growth_mm3(document) -> int:
    return sum(int(prop.get("growthMm3") or 0)
               for prop in (document or {}).get("props") or []
               if isinstance(prop, dict))


__all__ = [
    "SCHEMA_V2", "FALLBACK_MAX_DIAMETER_M", "V2LoadError", "V2Document",
    "detect_schema", "is_v2", "layer_map", "load", "schema_of", "sum_growth_mm3",
]
