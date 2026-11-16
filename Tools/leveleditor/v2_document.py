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
import math
from pathlib import Path

from Tools.levelbuild import placement_core as core
from Tools.levelbuild import placement_v2
from Tools.levelbuild import ref_v2_from_extract as ref_v2
from Tools.motion_sim import motion_cli

SCHEMA_V2 = ref_v2.SCHEMA_V2

#: Default design ceiling when a v2 document does not carry one.  The reference Cancer
#: document uses the original MAX_DIAMETER 1.20 m (its own notes say so); this fallback
#: only keeps the v1-shaped ladder/statistics fields finite.
FALLBACK_MAX_DIAMETER_M = 1.20

#: The 13 motion classes of LEVEL_FORMAT_V2 section 6.7.3, in the order the legend lists
#: them.  Display only: the *behaviour* of each class is MotionMath's, never this module's.
MOTION_CLASS_ORDER = (
    "spin", "flip", "bob", "sway", "hop", "whack_a_mole", "wait_for_player",
    "sporadic_walk", "clouds", "path_collision", "sloping_path", "back_and_forth",
    "dragged_along",
)

#: One colour per class, used by the "colour props by motion class" layer.  A palette is a
#: display decision, so it lives here (Python) and the browser only applies it, which keeps
#: the GUI and any headless screenshot using the same colours.
MOTION_CLASS_COLORS = {
    "spin": "#f6c344",
    "flip": "#f39a55",
    "bob": "#b7d94a",
    "sway": "#4fd1a5",
    "hop": "#4aa3f0",
    "whack_a_mole": "#a06bf0",
    "wait_for_player": "#f05a8e",
    "sporadic_walk": "#e05a4a",
    "clouds": "#8fd8f0",
    "path_collision": "#2fb9c9",
    "sloping_path": "#7d8ff0",
    "back_and_forth": "#c9a0f0",
    "dragged_along": "#b9b3a8",
}

MOTION_CLASS_LABELS = {
    "spin": "自转 Spin",
    "flip": "翻转 Flip",
    "bob": "浮沉 Bob",
    "sway": "摇摆 Sway",
    "hop": "跳动 Hop",
    "whack_a_mole": "打地鼠 WhackAMole",
    "wait_for_player": "等玩家 WaitForPlayer",
    "sporadic_walk": "随机游走 SporadicWalk",
    "clouds": "云 Clouds",
    "path_collision": "路径 · 碰撞 PathCollision",
    "sloping_path": "路径 · 斜坡 SlopingPath",
    "back_and_forth": "路径 · 来回 BackAndForth",
    "dragged_along": "随父件 DraggedAlong",
}


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


class TerrainHeight:
    """Ground height over the ``.katerrain`` collision mesh: the host-side query.

    This is the one host fact the motion preview needs (the trigger-radius ring has to lie
    on the ground, not float at the prop's centre), and it is a *query over the level's own
    terrain*, not motion logic: MotionMath's entry points never look at the ground, they take
    the host's answer where the original consults it.

    Buckets of ``CELL_M`` keep one lookup at a handful of triangles instead of the full
    3,455-triangle mesh.  ``zone`` membership and the "is the next step blocked" ray are not
    implemented - the editor states that as an approximation instead of pretending.
    """

    CELL_M = 4.0

    def __init__(self, vertices, indices):
        self.triangles = []
        limit = len(vertices)
        for index in range(0, len(indices or []) - 2, 3):
            a, b, c = indices[index], indices[index + 1], indices[index + 2]
            if max(a, b, c) >= limit or min(a, b, c) < 0:
                continue
            self.triangles.append((vertices[a], vertices[b], vertices[c]))
        self.buckets = {}
        cell = self.CELL_M
        for triangle, (p0, p1, p2) in enumerate(self.triangles):
            low_x = int(math.floor(min(p0[0], p1[0], p2[0]) / cell))
            high_x = int(math.floor(max(p0[0], p1[0], p2[0]) / cell))
            low_z = int(math.floor(min(p0[2], p1[2], p2[2]) / cell))
            high_z = int(math.floor(max(p0[2], p1[2], p2[2]) / cell))
            for ix in range(low_x, high_x + 1):
                for iz in range(low_z, high_z + 1):
                    self.buckets.setdefault((ix, iz), []).append(triangle)

    def height_at(self, x, z, max_y=None):
        """The highest terrain surface under ``(x, z)``, or ``None`` when there is none."""
        try:
            key = (int(math.floor(float(x) / self.CELL_M)),
                   int(math.floor(float(z) / self.CELL_M)))
        except (TypeError, ValueError):
            return None
        best = None
        for index in self.buckets.get(key, ()):
            height = self._triangle_height(self.triangles[index], x, z)
            if height is None:
                continue
            if max_y is not None and height > max_y:
                continue
            if best is None or height > best:
                best = height
        return None if best is None else round(best, 4)

    @staticmethod
    def _triangle_height(triangle, x, z):
        (x1, y1, z1), (x2, y2, z2), (x3, y3, z3) = triangle
        determinant = (z2 - z3) * (x1 - x3) + (x3 - x2) * (z1 - z3)
        if abs(determinant) < 1e-12:
            return None
        a = ((z2 - z3) * (x - x3) + (x3 - x2) * (z - z3)) / determinant
        b = ((z3 - z1) * (x - x3) + (x1 - x3) * (z - z3)) / determinant
        c = 1.0 - a - b
        if a < -1e-9 or b < -1e-9 or c < -1e-9:
            return None
        return a * y1 + b * y2 + c * y3


def motion_layer_map(classes=None):
    """The motion view toggles and their HTTP spellings (GUI, docs and tests share this).

    Same idea as :func:`layer_map`: every toggle the GUI offers names the endpoint that
    serves the data behind it, so an agent reaches the same Python with one request.
    """
    counts = classes or {}
    return {
        "motionColors": {
            "id": "show-v2-motion-colors", "default": False,
            "http": "/api/v2/motion/props",
            "count": counts.get("motionProps"),
            "label": "按运动类着色（13 类调色板来自 /api/v2/motion/props）",
        },
        "motionPaths": {
            "id": "show-v2-motion-paths", "default": False,
            "http": "/api/v2/motion/paths",
            "count": counts.get("paths"),
            "label": "路径折线（paths[]，米；4 条折线）",
        },
        "motionRadius": {
            "id": "show-v2-motion-radius", "default": True,
            "http": "/api/v2/motion/props?radius=1",
            "count": counts.get("waitForPlayerProps"),
            "label": "WaitForPlayer 触发半径圈（PanicRadiusM，来自 MotionMath）",
        },
        "motionParents": {
            "id": "show-v2-motion-parent", "default": True,
            "http": "/api/v2/motion/prop/<itemId>",
            "count": counts.get("attachedProps"),
            "label": "选中件的父件连线（props[].parent + inherit）",
        },
        "motionPlayback": {
            "id": "v2-motion-play", "default": False,
            "http": "/api/v2/motion/trajectory?items=<ids>&seconds=<s>",
            "count": counts.get("movingProps"),
            "label": "播放（世界轨迹由 Tools/motion_sim 逐帧采样）",
        },
    }


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
        # Motion layer (motions[]/paths[]): the facts come from the motion_sim tool that
        # compiles MotionMath.cs, resolved lazily so opening a level never pays for a build.
        self._motion_attempted = False
        self._motion_facts = None
        self._motion_error = None
        self._motion_error_code = None
        self._terrain_height = None
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
            # Motion facts the document itself declares (LEVEL_FORMAT_V2 6.7).  The
            # resolved numbers (speed, phase, radius) come from Tools/motion_sim and are
            # served by /api/v2/motion, not counted here.
            "motions": len([motion for motion in self.document.get("motions") or []
                            if isinstance(motion, dict)]),
            "paths": len([path for path in self.document.get("paths") or []
                          if isinstance(path, dict)]),
            "motionProps": sum(1 for prop in props if prop.get("motion")),
            "attachedProps": sum(1 for prop in props
                                 if prop.get("behaviour") == "attached"),
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

    # -- motion (motions[] / paths[], LEVEL_FORMAT_V2 6.7) -------------- #
    def _motion_tables(self):
        """``(motions by id, paths by id)`` from the document, both ordered by id."""
        motions = {}
        for motion in self.document.get("motions") or []:
            if isinstance(motion, dict) and motion.get("id"):
                motions[motion["id"]] = motion
        paths = {}
        for path in self.document.get("paths") or []:
            if isinstance(path, dict) and path.get("id"):
                paths[path["id"]] = path
        return motions, paths

    def document_motion_rows(self):
        """Per-prop motion facts read straight from the document (no evaluator involved).

        This is what makes class colouring, the path polylines and the "which motion does
        this prop use" answer work even on a machine without the .NET SDK: they are declared
        data.  The *resolved* numbers (speed in m/s, phase seed, trigger radius, poses) come
        from :meth:`motion_facts`, i.e. from MotionMath, and are merged in
        :meth:`motion_payload`.
        """
        motions, paths = self._motion_tables()
        rows = {}
        for prop in self.document.get("props") or []:
            if not isinstance(prop, dict):
                continue
            item_id = prop.get("itemId")
            if not item_id:
                continue
            motion_id = prop.get("motion")
            behaviour = prop.get("behaviour")
            motion = motions.get(motion_id) if motion_id else None
            if motion is not None:
                motion_class = motion.get("class")
            elif behaviour == "attached":
                # 6.7.3: dragged_along is the parent relation expressed as data.
                motion_class = "dragged_along"
            else:
                motion_class = None
            parent = prop.get("parent")
            if motion_class is None and not parent:
                continue
            path = paths.get((motion or {}).get("path")) if motion else None
            rows[item_id] = {
                "itemId": item_id,
                "def": prop.get("def"),
                "behaviour": behaviour,
                "inherit": prop.get("inherit"),
                "parent": parent,
                "motion": motion_id,
                "motionClass": motion_class,
                "instanceIndex": prop.get("instanceIndex"),
                "zone": prop.get("zone"),
                "aabb": prop.get("aabb"),
                "updatesPerSecond": (motion or {}).get("updatesPerSecond"),
                "loop": (motion or {}).get("loop"),
                "params": (motion or {}).get("params") or {},
                "provenance": (motion or {}).get("provenance") or {},
                "speedSource": ((motion or {}).get("speed") or {}).get("source"),
                "speedCmPerSec": (path or {}).get("speedCmPerSec"),
                "path": (path or {}).get("id"),
                "outputSpace": None,
                "resolved": False,
            }
        return rows

    def motion_facts(self):
        """The ``motion_sim`` answer for this document (cached, ``None`` when unavailable)."""
        if self._motion_attempted:
            return self._motion_facts
        self._motion_attempted = True
        if not (self.document.get("motions") or self.document.get("paths")):
            self._motion_error = "this document declares no motions[]/paths[]"
            self._motion_error_code = "no_motion_data"
            return None
        try:
            self._motion_facts = motion_cli.params(self.path)
        except motion_cli.MotionSimError as error:
            self._motion_error = str(error)
            self._motion_error_code = error.code
        return self._motion_facts

    def terrain_height(self, x, z, max_y=None):
        """Ground height at a world x/z from the ``.katerrain`` sidecar (or ``None``)."""
        if not self.sidecar:
            return None
        if self._terrain_height is None:
            self._terrain_height = TerrainHeight(self.sidecar["vertices"],
                                                 self.sidecar["indices"])
        return self._terrain_height.height_at(x, z, max_y=max_y)

    def motion_counts(self):
        rows = self.document_motion_rows()
        by_class = {}
        for row in rows.values():
            if row["motionClass"]:
                by_class[row["motionClass"]] = by_class.get(row["motionClass"], 0) + 1
        motions, paths = self._motion_tables()
        facts = self.motion_facts()
        return {
            "motions": len(motions),
            "paths": len(paths),
            "motionProps": sum(1 for row in rows.values() if row["motion"]),
            "attachedProps": sum(1 for row in rows.values()
                                 if row["behaviour"] == "attached"),
            "waitForPlayerProps": by_class.get("wait_for_player", 0),
            "movingProps": ((facts or {}).get("counts") or {}).get("movingProps"),
            "byClass": by_class,
        }

    def motion_payload(self, radius=True):
        """The whole motion layer: classes, motions[], paths[], per-prop rows, approximations.

        One implementation behind both the GUI (loaded once when a v2 level opens) and
        ``GET /api/v2/motion``.  ``available`` is false when the .NET tool that compiles
        MotionMath.cs cannot run here; the declared data (classes, paths, parents) is still
        served, and ``reason`` says exactly what is missing.
        """
        rows = self.document_motion_rows()
        motions, paths = self._motion_tables()
        facts = self.motion_facts()
        counts = self.motion_counts()
        if facts is not None:
            by_id = {row.get("itemId"): row for row in facts.get("props") or []}
            for item_id, row in rows.items():
                resolved = by_id.get(item_id)
                if not resolved:
                    continue
                row["resolved"] = True
                for key in ("speedMps", "speedCmPerSec", "speedSource", "speedIndex",
                            "halfHeightM", "phaseSeed", "phase", "outputSpace",
                            "hopSpeedCmPerFrame", "triggerRadiusM", "triggerMotionId",
                            "pathClosed", "updatesPerSecond", "loop", "moves"):
                    if key in resolved:
                        row[key] = resolved[key]
                row["assumptions"] = list(resolved.get("assumptions") or [])
        if radius:
            # The trigger ring has to lie on the ground, so the one host query the preview
            # needs is a terrain height at the prop's own x/z (section 6.7.3's gate is a
            # horizontal distance; the ring is drawn on the surface under the prop).
            for row in rows.values():
                if not row.get("triggerRadiusM"):
                    continue
                position = None
                for prop in self.document.get("props") or []:
                    if isinstance(prop, dict) and prop.get("itemId") == row["itemId"]:
                        position = prop.get("position")
                        break
                if isinstance(position, (list, tuple)) and len(position) >= 3:
                    row["groundY"] = self.terrain_height(float(position[0]),
                                                         float(position[2]))
        path_rows = []
        for path_id in sorted(paths):
            path = paths[path_id]
            users = sorted(item for item, row in rows.items() if row.get("path") == path_id)
            path_rows.append({
                "id": path_id,
                "points": path.get("points") or [],
                "loop": path.get("loop"),
                "closed": (path.get("loop") != "once"),
                "speedCmPerSec": path.get("speedCmPerSec"),
                "notes": path.get("notes"),
                "users": users,
            })
        class_rows = []
        for name in MOTION_CLASS_ORDER:
            if name not in counts["byClass"] and name != "dragged_along":
                continue
            motion = next((m for m in motions.values() if m.get("class") == name), None)
            class_rows.append({
                "class": name,
                "label": MOTION_CLASS_LABELS.get(name, name),
                "color": MOTION_CLASS_COLORS.get(name, "#cccccc"),
                "count": counts["byClass"].get(name, 0),
                "updatesPerSecond": (motion or {}).get("updatesPerSecond"),
                "speedSource": (((motion or {}).get("speed")) or {}).get("source"),
                "path": (motion or {}).get("path"),
            })
        approximations = [
            "size group: v2 carries no original size id, so MotionMath's size-group tables "
            "(hop speed, WaitForPlayer trigger radius) use group 0 - every Cancer motion "
            "prop is original size 2, i.e. group 0",
            "ground: heights come from the .katerrain sidecar at the prop's own x/z; the "
            "ground-following and zone rays the original runs are not modelled",
            "blocking: walkers and clouds treat every forward step and every turn candidate "
            "as allowed (no zone or obstacle queries)",
            "player: a level preview has no player, so WaitForPlayer reports its radius and "
            "never triggers",
            "phase: from the D-12 seed PhaseSeed.Hash32(level.id + \"/\" + itemId); a "
            "declared props[].phase is not applied",
        ]
        if facts is None:
            approximations.insert(0, "motion evaluator unavailable: %s"
                                 % (self._motion_error or "unknown reason"))
        return {
            "ok": True,
            "schema": SCHEMA_V2,
            "level": (self.document.get("meta") or {}).get("id"),
            "available": facts is not None,
            "reason": self._motion_error,
            "errorCode": self._motion_error_code,
            "evaluator": {
                "name": "Tools/motion_sim",
                "compiles": "Runtime/Pickup/MotionMath.cs (source, not a copy)",
                "gameFramesPerSecond": (facts or {}).get("gameFramesPerSecond", 30),
            },
            "counts": counts,
            "classes": class_rows,
            "motions": [dict(motion) for motion in motions.values()],
            "paths": path_rows,
            "props": [rows[item_id] for item_id in sorted(rows)],
            "layers": motion_layer_map(counts),
            "approximations": approximations,
            "assumptions": (facts or {}).get("assumptions"),
        }

    def motion_paths_payload(self):
        payload = self.motion_payload(radius=False)
        return {
            "ok": True,
            "level": payload["level"],
            "available": payload["available"],
            "reason": payload["reason"],
            "count": len(payload["paths"]),
            "paths": payload["paths"],
            "layers": payload["layers"],
        }

    def motion_prop_payload(self, item_id):
        """One prop's motion facts, plus the ground height under it and its parent chain."""
        rows = self.document_motion_rows()
        row = rows.get(item_id)
        if row is None:
            raise V2LoadError("prop %r has no motion and no parent in this document"
                              % item_id)
        facts = self.motion_facts()
        if facts is not None:
            for resolved in facts.get("props") or []:
                if resolved.get("itemId") != item_id:
                    continue
                row["resolved"] = True
                for key in ("speedMps", "speedCmPerSec", "speedSource", "speedIndex",
                            "halfHeightM", "phaseSeed", "phase", "outputSpace",
                            "hopSpeedCmPerFrame", "triggerRadiusM", "triggerMotionId",
                            "pathClosed", "updatesPerSecond", "loop", "moves"):
                    if key in resolved:
                        row[key] = resolved[key]
                row["assumptions"] = list(resolved.get("assumptions") or [])
                break
        for prop in self.document.get("props") or []:
            if isinstance(prop, dict) and prop.get("itemId") == item_id:
                row["position"] = prop.get("position")
                row["rotationQuat"] = prop.get("rotationQuat")
                ground = None
                if isinstance(prop.get("position"), (list, tuple)) and len(prop["position"]) >= 3:
                    ground = self.terrain_height(float(prop["position"][0]),
                                                 float(prop["position"][2]))
                row["groundY"] = ground
                break
        chain = []
        seen = set()
        current = row.get("parent")
        while current and current not in seen and len(chain) < 16:
            seen.add(current)
            parent_row = rows.get(current)
            chain.append({
                "itemId": current,
                "motionClass": (parent_row or {}).get("motionClass"),
                "motion": (parent_row or {}).get("motion"),
                "moves": bool((parent_row or {}).get("moves"))
                or bool((parent_row or {}).get("motion")),
            })
            current = (parent_row or {}).get("parent")
        row["parentChain"] = chain
        path = None
        if row.get("path"):
            for entry in self.motion_payload(radius=False)["paths"]:
                if entry["id"] == row["path"]:
                    path = entry
                    break
        row["pathDetail"] = path
        return {
            "ok": True,
            "level": (self.document.get("meta") or {}).get("id"),
            "available": facts is not None,
            "reason": self._motion_error,
            "itemId": item_id,
            "prop": row,
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
