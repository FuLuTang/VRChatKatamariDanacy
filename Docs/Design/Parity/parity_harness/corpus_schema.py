"""One stage's parity-corpus schema, validator and loader (stdlib only).

A *corpus* is the original game's data for one stage, transcribed into a shape
our side can consume: stage parameters, the object catalogue (one row per
object *type*), and the object instances (one row per placed object, with the
original's real position when the data mine has one).

The schema is deliberately strict:

* unknown keys are an error, not silently dropped (a typo must not become a
  missing metric);
* every leaf value must be covered by an ``evidence`` entry (a field without a
  citation is a guess, and this project does not ship guesses);
* a ``null`` in a field listed in :data:`NULLABLE_WITH_TODO` must be named in
  ``unresolved`` — "we do not know" is a claim that has to be written down.

Only three things are optional by design: annotations (``_note`` / ``_todo`` /
``_source``), the per-instance position (a multi-area stage has no transforms),
and the derived time fields (the original's timer unit is unconfirmed).

Run ``python3 -m Docs.Design.Parity.parity_harness.corpus_schema --describe``
for the field list, or read :func:`describe`.
"""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

SCHEMA_VERSION = "parity_corpus/1"

#: Evidence levels used across the repository (``Docs/Research`` uses the same
#: vocabulary).  ``本项目设计`` is only legal on our side, never on a corpus.
EVIDENCE_LEVELS = ("数据挖掘", "源码", "官方", "专利", "社区实测", "推断")

#: Keys that carry prose, not data.  Allowed on any object node.
ANNOTATION_KEYS = ("_note", "_todo", "_source")

#: Nullable fields whose ``null`` must be explained in ``unresolved``.
NULLABLE_WITH_TODO = ("timebase.fixed_step_s", "parameters.timer_unit",
                      "parameters.timer_seconds")


class CorpusError(ValueError):
    """A corpus is not usable: unknown field, bad type, or missing evidence."""


# --------------------------------------------------------------------------- #
# spec nodes
# --------------------------------------------------------------------------- #
def num(nullable: bool = False, minimum: Optional[float] = None,
        maximum: Optional[float] = None, positive: bool = False) -> Dict[str, Any]:
    return {"kind": "num", "nullable": nullable, "minimum": minimum,
            "maximum": maximum, "positive": positive}


def text(nullable: bool = False, choices: Optional[Tuple[str, ...]] = None) -> Dict[str, Any]:
    return {"kind": "text", "nullable": nullable, "choices": choices}


def boolean(nullable: bool = False) -> Dict[str, Any]:
    return {"kind": "bool", "nullable": nullable}


def seq(item: Dict[str, Any], nullable: bool = False,
        min_items: int = 0, max_items: Optional[int] = None) -> Dict[str, Any]:
    return {"kind": "seq", "item": item, "nullable": nullable,
            "min_items": min_items, "max_items": max_items}


def obj(fields: Dict[str, Dict[str, Any]], required: Iterable[str] = (),
        nullable: bool = False, allow_annotations: bool = True,
        dynamic_key: Optional[str] = None) -> Dict[str, Any]:
    return {"kind": "obj", "fields": fields, "required": tuple(required),
            "nullable": nullable, "allow_annotations": allow_annotations,
            "dynamic_key": dynamic_key}


def mapof(item: Dict[str, Any], nullable: bool = False) -> Dict[str, Any]:
    """A dict whose keys are data (the object catalogue is keyed by mono idx)."""
    return {"kind": "map", "item": item, "nullable": nullable}


_GROWTH_POINT = obj(
    {
        "diameter_m": num(positive=True),
        "fraction": num(minimum=0.0, maximum=1.0),
        "evidence": text(nullable=True),
    },
    required=("diameter_m", "fraction"),
)

_CAMERA = obj(
    {
        "fov_deg": num(nullable=True, positive=True),
        "near_m": num(nullable=True, positive=True),
        "far_m": num(nullable=True, positive=True),
        "focus_distance_m": num(nullable=True, positive=True),
        "aperture_f": num(nullable=True, positive=True),
    },
    required=("fov_deg", "near_m", "far_m"),
)

_STAGE = obj(
    {
        "id": text(),
        "name": text(),
        "game": text(),
        "build": text(nullable=True),
        "source": text(),
        "single_area": boolean(),
    },
    required=("id", "name", "game", "source", "single_area"),
)

_UNITS = obj(
    {
        "length": text(choices=("m",)),
        "diameter": text(choices=("m",)),
        "volume": text(choices=("m3",)),
        "time": text(choices=("s",)),
    },
    required=("length", "diameter", "volume", "time"),
)

_TIMEBASE = obj(
    {
        "fixed_step_hz": num(positive=True),
        "fixed_step_s": num(nullable=True, positive=True),
        "frame_interpolation": text(nullable=True),
        "source": text(),
    },
    required=("fixed_step_hz", "source"),
)

_PARAMETERS = obj(
    {
        "start_diameter_m": num(positive=True),
        "clear_diameter_m": num(positive=True),
        "max_diameter_m": num(positive=True),
        "area_count": num(minimum=1),
        "instance_count": num(minimum=0),
        "type_count": num(minimum=0),
        "timer_raw": num(nullable=True, minimum=0),
        "timer_unit": text(nullable=True, choices=("min", "s")),
        "timer_seconds": num(nullable=True, positive=True),
        "clear_type": text(nullable=True),
        "gate_volume_ratio": num(positive=True),
        "gate_floor_m": num(minimum=0.0),
        "hud_diameter_offset_m": num(minimum=0.0),
        "growth_curve": seq(_GROWTH_POINT),
        "rating_diameters_m": seq(num(positive=True)),
        "zone_gates_m": seq(num(positive=True)),
        "camera": _CAMERA,
        "spawn": obj(
            {
                "position": seq(num(), min_items=3, max_items=3),
                "rot_y_rad": num(nullable=True),
                "frame_note": text(nullable=True),
            },
            required=("position",),
        ),
    },
    required=("start_diameter_m", "clear_diameter_m", "max_diameter_m",
              "area_count", "gate_volume_ratio", "gate_floor_m",
              "hud_diameter_offset_m", "growth_curve", "zone_gates_m",
              "camera", "spawn"),
)

_CATALOGUE_ENTRY = obj(
    {
        "name": text(),
        "internal_name": text(nullable=True),
        "req_pickup_mm": num(positive=True),
        "volume_m3": num(positive=True),
        "own_diam_mm": num(nullable=True, positive=True),
        "category": text(nullable=True),
        "unit_type": text(nullable=True),
        "kind": text(nullable=True),
        "path_speed_index": text(nullable=True),
        "vault_points": num(nullable=True, minimum=0),
        "hit_lod": num(nullable=True, minimum=0),
        "penalty": num(nullable=True, minimum=0.0, maximum=1.0),
        "volume_factor": num(nullable=True, positive=True),
    },
    required=("name", "req_pickup_mm", "volume_m3"),
)

_INSTANCE = obj(
    {
        "mono_idx": text(),
        "position": seq(num(nullable=True), min_items=3, max_items=3,
                        nullable=True),
        "placed": boolean(),
        "parent_idx": text(nullable=True),
        "play_anim_no": num(nullable=True),
        "shake_off": boolean(nullable=True),
        "loading_area": num(nullable=True, minimum=0),
    },
    required=("mono_idx", "placed"),
)

_ZONE = obj(
    {
        "area": num(minimum=0),
        "instances": num(minimum=0),
        "has_growth_bands": boolean(),
        "min_mm": num(positive=True),
        "p50_mm": num(positive=True),
        "clear_cm": num(positive=True),
        "eat_now": num(minimum=0.0, maximum=1.0),
        "min_ratio_to_ball": num(minimum=0.0),
        "p50_ratio_to_ball": num(minimum=0.0),
        "never_eatable_scenery": num(minimum=0.0, maximum=1.0),
        "small_1_10cm_share": num(minimum=0.0, maximum=1.0),
        "spearman_size_radius": num(minimum=-1.0, maximum=1.0),
    },
    required=("area",),
)

_EVIDENCE_ENTRY = obj(
    {
        "source": text(),
        "level": text(choices=EVIDENCE_LEVELS),
        "note": text(nullable=True),
    },
    required=("source", "level"),
)

TOP = obj(
    {
        "schema": text(choices=(SCHEMA_VERSION,)),
        "stage": _STAGE,
        "units": _UNITS,
        "timebase": _TIMEBASE,
        "parameters": _PARAMETERS,
        "catalogue": mapof(_CATALOGUE_ENTRY),
        "instances": seq(_INSTANCE, min_items=1),
        "evidence": mapof(_EVIDENCE_ENTRY),
        "unresolved": seq(text()),
        "volume_factor": obj(
            {
                "source": text(),
                "default_k": num(positive=True),
                "measured_types": num(minimum=0),
                "exact_default_share": num(minimum=0.0, maximum=1.0),
                "median_k": num(positive=True),
            },
            required=("source", "default_k"),
            nullable=True,
        ),
        "zone_profile": obj(
            {
                "source": text(),
                "areas": seq(_ZONE),
            },
            required=("source", "areas"),
            nullable=True,
        ),
    },
    required=("schema", "stage", "units", "timebase", "parameters",
              "catalogue", "instances", "evidence", "unresolved"),
)


# --------------------------------------------------------------------------- #
# validation
# --------------------------------------------------------------------------- #
def _fmt(value: Any) -> str:
    if isinstance(value, dict):
        return "{" + ", ".join(sorted(value)) + "}"
    return repr(value)


def _check(node: Dict[str, Any], value: Any, path: str) -> None:
    if value is None:
        if node["nullable"]:
            return
        raise CorpusError("%s: null is not allowed here (declare it nullable in "
                          "the schema, or give the value)" % path)
    kind = node["kind"]
    if kind == "num":
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise CorpusError("%s: expected a number, got %s" % (path, _fmt(value)))
        if not math.isfinite(float(value)):
            raise CorpusError("%s: number is not finite" % path)
        if node["positive"] and value <= 0:
            raise CorpusError("%s: must be > 0, got %r" % (path, value))
        if node["minimum"] is not None and value < node["minimum"]:
            raise CorpusError("%s: must be >= %r, got %r"
                              % (path, node["minimum"], value))
        if node["maximum"] is not None and value > node["maximum"]:
            raise CorpusError("%s: must be <= %r, got %r"
                              % (path, node["maximum"], value))
        return
    if kind == "text":
        if not isinstance(value, str):
            raise CorpusError("%s: expected a string, got %s" % (path, _fmt(value)))
        if node["choices"] is not None and value not in node["choices"]:
            raise CorpusError("%s: %r is not one of %r"
                              % (path, value, list(node["choices"])))
        return
    if kind == "bool":
        if not isinstance(value, bool):
            raise CorpusError("%s: expected true/false, got %s" % (path, _fmt(value)))
        return
    if kind == "seq":
        if not isinstance(value, list):
            raise CorpusError("%s: expected a list, got %s" % (path, _fmt(value)))
        if len(value) < node["min_items"]:
            raise CorpusError("%s: needs at least %d item(s), got %d"
                              % (path, node["min_items"], len(value)))
        if node["max_items"] is not None and len(value) > node["max_items"]:
            raise CorpusError("%s: allows at most %d item(s), got %d"
                              % (path, node["max_items"], len(value)))
        for index, item in enumerate(value):
            _check(node["item"], item, "%s[]" % path)
        return
    if kind == "map":
        if not isinstance(value, dict):
            raise CorpusError("%s: expected an object, got %s" % (path, _fmt(value)))
        for key in sorted(value):
            _check(node["item"], value[key], "%s.%s" % (path, key))
        return
    if kind == "obj":
        if not isinstance(value, dict):
            raise CorpusError("%s: expected an object, got %s" % (path, _fmt(value)))
        allowed = set(node["fields"])
        if node["allow_annotations"]:
            allowed.update(ANNOTATION_KEYS)
        unknown = sorted(set(value) - allowed)
        if unknown:
            raise CorpusError("%s: unknown field(s) %r — refusing to ignore them "
                              "(known: %r)" % (path, unknown, sorted(allowed)))
        for key in node["required"]:
            if key not in value:
                raise CorpusError("%s: missing required field %r" % (path, key))
        for key in sorted(value):
            if key in ANNOTATION_KEYS:
                if not isinstance(value[key], str):
                    raise CorpusError("%s.%s: annotations are strings" % (path, key))
                continue
            _check(node["fields"][key], value[key], "%s.%s" % (path, key))
        return
    raise CorpusError("%s: internal schema error, unknown kind %r" % (path, kind))


def _leaves(value: Any, path: str, out: List[str]) -> None:
    if isinstance(value, dict):
        for key in sorted(value):
            if key in ANNOTATION_KEYS:
                continue
            _leaves(value[key], "%s.%s" % (path, key) if path else key, out)
        return
    if isinstance(value, list):
        for item in value:
            _leaves(item, "%s[]" % path, out)
        return
    out.append(path)


def _match(pattern: str, path: str) -> bool:
    """Glob with a single wildcard (``*``) and no bracket classes.

    ``fnmatch`` is wrong here: field paths contain ``[]`` (list indices) and
    ``fnmatch`` reads that as a character class, so every list field would look
    unevidenced.
    """
    if "*" not in pattern:
        return pattern == path
    head, *middles, tail = pattern.split("*")
    if not path.startswith(head):
        return False
    position = len(head)
    for middle in middles:
        found = path.find(middle, position)
        if found < 0:
            return False
        position = found + len(middle)
    if not path.endswith(tail):
        return False
    return len(path) - len(tail) >= position


def check_evidence(data: Dict[str, Any]) -> None:
    """Every datum needs a citation; every citation needs a datum."""
    evidence = data["evidence"]
    for pattern in evidence:
        if pattern.strip("*") == "":
            raise CorpusError("evidence.%s: a bare '*' is not a citation; name "
                              "the fields it covers" % pattern)
    leaves: List[str] = []
    for key in sorted(data):
        # `evidence` cites, `unresolved` is prose and `schema` is the format
        # tag: none of the three is a datum that needs a citation.
        if key in ("evidence", "unresolved", "schema") or key in ANNOTATION_KEYS:
            continue
        _leaves(data[key], key, leaves)
    used: Dict[str, int] = {pattern: 0 for pattern in evidence}
    for leaf in leaves:
        hits = [pattern for pattern in evidence if _match(pattern, leaf)]
        if not hits:
            raise CorpusError("%s: no evidence entry covers this field; add a "
                              "pattern to `evidence` or set it to null"
                              % leaf)
        for pattern in hits:
            used[pattern] += 1
    for pattern in sorted(used):
        if used[pattern] == 0:
            raise CorpusError("evidence.%s: matches no field (typo?)" % pattern)


_MISSING = object()


def _get_path(data: Dict[str, Any], dotted: str) -> Any:
    node: Any = data
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return _MISSING
        node = node[part]
    return node


def check_unresolved(data: Dict[str, Any]) -> None:
    """A nullable field left null must be named in ``unresolved``."""
    notes = " ".join(data["unresolved"])
    for dotted in NULLABLE_WITH_TODO:
        value = _get_path(data, dotted)
        if value is _MISSING:
            continue
        if value is None:
            leaf = dotted.rsplit(".", 1)[-1]
            if leaf not in notes and dotted not in notes:
                raise CorpusError("unresolved: %s is null but no entry mentions "
                                  "%r (state the open question, do not leave it "
                                  "silent)" % (dotted, leaf))


def validate_corpus(data: Dict[str, Any]) -> Dict[str, Any]:
    """Validate + evidence-check a corpus dict; returns it unchanged."""
    if not isinstance(data, dict):
        raise CorpusError("top level: expected an object, got %s" % _fmt(data))
    _check(TOP, data, "corpus")
    check_evidence(data)
    check_unresolved(data)
    return data


# --------------------------------------------------------------------------- #
# derived views
# --------------------------------------------------------------------------- #
class Corpus:
    """Thin typed view over a validated corpus dict."""

    def __init__(self, data: Dict[str, Any], path: Optional[Path] = None) -> None:
        self.data = data
        self.path = path

    # -- stage ------------------------------------------------------------
    @property
    def stage_id(self) -> str:
        return self.data["stage"]["id"]

    @property
    def stage_name(self) -> str:
        return self.data["stage"]["name"]

    @property
    def single_area(self) -> bool:
        return self.data["stage"]["single_area"]

    # -- parameters -------------------------------------------------------
    @property
    def params(self) -> Dict[str, Any]:
        return self.data["parameters"]

    @property
    def start_diameter_m(self) -> float:
        return float(self.params["start_diameter_m"])

    @property
    def clear_diameter_m(self) -> float:
        return float(self.params["clear_diameter_m"])

    @property
    def max_diameter_m(self) -> float:
        return float(self.params["max_diameter_m"])

    def growth_fraction(self, diameter_m: float) -> Optional[float]:
        """Piecewise-linear ``size -> fraction of the object volume received``.

        The original's curves are lists of ``(size, ratio)`` knots, linearly
        interpolated per level (``/tmp/orig_physics/reroll_numbers.md`` §3.1).
        Below the first knot and above the last one the nearest knot holds.
        """
        points = sorted((float(p["diameter_m"]), float(p["fraction"]))
                        for p in self.params["growth_curve"])
        if not points:
            return None
        if diameter_m <= points[0][0]:
            return points[0][1]
        if diameter_m >= points[-1][0]:
            return points[-1][1]
        for (d0, f0), (d1, f1) in zip(points, points[1:]):
            if d0 <= diameter_m <= d1:
                if d1 - d0 <= 0.0:
                    return f1
                t = (diameter_m - d0) / (d1 - d0)
                return f0 + (f1 - f0) * t
        return points[-1][1]

    # -- content ----------------------------------------------------------
    @property
    def catalogue(self) -> Dict[str, Dict[str, Any]]:
        return self.data["catalogue"]

    @property
    def instances(self) -> List[Dict[str, Any]]:
        return self.data["instances"]

    def placed(self) -> List[Dict[str, Any]]:
        return [i for i in self.instances if i.get("placed")]

    def instances_with_position(self) -> List[Dict[str, Any]]:
        out = []
        for inst in self.instances:
            pos = inst.get("position")
            if pos and all(v is not None for v in pos) and any(abs(v) > 1e-12 for v in pos):
                out.append(inst)
        return out

    def req_pickup_m(self, instance: Dict[str, Any]) -> float:
        entry = self.catalogue[instance["mono_idx"]]
        return float(entry["req_pickup_mm"]) / 1000.0

    def volume_m3(self, instance: Dict[str, Any]) -> float:
        return float(self.catalogue[instance["mono_idx"]]["volume_m3"])

    def absorbable_at(self, diameter_m: float) -> List[Dict[str, Any]]:
        return [i for i in self.instances
                if self.req_pickup_m(i) <= diameter_m + 1e-12]


def load_corpus(path: Path) -> Corpus:
    """Read, validate and wrap one corpus file."""
    raw = Path(path).read_text(encoding="utf-8")
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as error:
        raise CorpusError("%s: not valid JSON (%s)" % (path, error)) from None
    validate_corpus(data)
    return Corpus(data, Path(path))


# --------------------------------------------------------------------------- #
# description (the "JSON-Schema-ish" part, for humans and for --describe)
# --------------------------------------------------------------------------- #
def _describe_node(node: Dict[str, Any], path: str, out: List[str],
                   indent: int = 0) -> None:
    pad = "  " * indent
    kind = node["kind"]
    if kind == "obj":
        out.append("%s%s: object%s" % (pad, path, " (nullable)" if node["nullable"] else ""))
        for key in sorted(node["fields"]):
            child = "%s.%s" % (path, key) if path else key
            _describe_node(node["fields"][key], child, out, indent + 1)
        if node["required"]:
            out.append("%s  required: %s" % (pad, ", ".join(node["required"])))
    elif kind == "seq":
        out.append("%s%s: list%s" % (pad, path, " (nullable)" if node["nullable"] else ""))
        _describe_node(node["item"], "[]", out, indent + 1)
    elif kind == "map":
        out.append("%s%s: object keyed by data" % (pad, path))
        _describe_node(node["item"], "<key>", out, indent + 1)
    elif kind == "num":
        bits = ["number"]
        if node["positive"]:
            bits.append(">0")
        if node["minimum"] is not None:
            bits.append(">=" + repr(node["minimum"]))
        if node["maximum"] is not None:
            bits.append("<=" + repr(node["maximum"]))
        if node["nullable"]:
            bits.append("null allowed")
        out.append("%s%s: %s" % (pad, path, ", ".join(bits)))
    elif kind == "text":
        bits = ["string"]
        if node["choices"] is not None:
            bits.append("one of " + repr(list(node["choices"])))
        if node["nullable"]:
            bits.append("null allowed")
        out.append("%s%s: %s" % (pad, path, ", ".join(bits)))
    elif kind == "bool":
        out.append("%s%s: boolean" % (pad, path))
    else:  # pragma: no cover - schema is closed
        out.append("%s%s: <%s>" % (pad, path, kind))


def describe() -> str:
    """Deterministic field-by-field outline of one corpus (for ``--describe``)."""
    out: List[str] = [
        "schema: %s" % SCHEMA_VERSION,
        "notes:",
        "  - unknown keys are rejected, not dropped",
        "  - every leaf must be covered by an `evidence` pattern",
        "  - evidence patterns name fields (a bare '*' is refused)",
        "  - null is legal only where the schema says so; %s must be named in"
        % ", ".join(NULLABLE_WITH_TODO),
        "    `unresolved` when null",
        "  - annotations `_note` / `_todo` / `_source` are allowed on any object",
        "",
    ]
    _describe_node(TOP, "corpus", out)
    return "\n".join(out)


def main(argv: Optional[List[str]] = None) -> int:
    import sys

    args = list(sys.argv[1:] if argv is None else argv)
    if args and args[0] in ("--describe", "-d"):
        print(describe())
        return 0
    if args and args[0] in ("--validate", "-v"):
        if len(args) != 2:
            print("usage: corpus_schema.py --validate <corpus.json>")
            return 2
        try:
            corpus = load_corpus(Path(args[1]))
        except CorpusError as error:
            print("INVALID: %s" % error)
            return 1
        print("ok: %s (%d instance(s), %d type(s))"
              % (corpus.stage_name, len(corpus.instances), len(corpus.catalogue)))
        return 0
    print(__doc__.strip())
    return 0


if __name__ == "__main__":  # pragma: no cover - CLI
    raise SystemExit(main())
