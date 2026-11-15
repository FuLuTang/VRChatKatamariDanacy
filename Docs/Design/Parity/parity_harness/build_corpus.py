"""Regenerate the two parity corpora from the local data-mine export.

The corpora are **not** committed: they are exports of an unlicensed data mine
of a commercial game (`.gitignore` excludes `parity_harness/corpus_*.json`).  This
script is the reproducible recipe — same input, same bytes out, no network, no
writes outside ``--out``.

    python3 Docs/Design/Parity/parity_harness/build_corpus.py
    python3 Docs/Design/Parity/parity_harness/build_corpus.py --meta ~/KatamariRef/meta
    python3 Docs/Design/Parity/parity_harness/build_corpus.py --stage mas1 --out /tmp

Inputs (all read-only, all under ``--meta``):

| file | what |
|---|---|
| ``tables/raw_reroll_dump__missions.tsv`` | level parameters (start/clear/max, timer, areas, rating, camera) |
| ``tables/raw_reroll_dump__object_dump.tsv`` | the 28,698 placed instances (position, category, parent, anim) |
| ``tables/raw_reroll_dump__all_objects.tsv`` | per-type ``Req Pickup Size (mm)`` + ``Volume (m^3)`` |
| ``tables/raw_reroll_dump__collectible_objects.tsv`` | per-type ``Pickup Volume Penalty`` |
| ``tables/raw_reroll_dump__name_props_0x30.tsv`` | per-type ``pathSpeedIndex``, ``num vault pts`` |
| ``tables/raw_reroll_dump__swirls.tsv`` | the size gate that plays the whirlpool |
| ``tables/raw_reroll_dump__sim_missions.tsv`` | spawn position / rotation (a different frame) |

Every number in the output is copied or derived from those columns; nothing is
invented.  Values with no public source stay ``null`` and are named in
``unresolved`` (the growth curve for Make Cancer, the timer unit for both).
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

REPO_ROOT = Path(__file__).resolve().parents[4]
if str(REPO_ROOT) not in sys.path:  # allow `python3 <this file>` from anywhere
    sys.path.insert(0, str(REPO_ROOT))

DEFAULT_META = Path(os.environ.get("KATAMARI_REF", "/home/fulutang/KatamariRef")) / "meta"
SCHEMA_VERSION = "parity_corpus/1"

#: Longest-edge-agnostic: the original's own diameter is the equal-volume sphere
#: of the item, i.e. ``Req Pickup Size / 2.154``; we store both so a reader can
#: pick the right one instead of re-deriving it.
STAGES = {
    "mas1": {
        "label": "Make a Star 1",
        "growth": [(0.05, 0.8), (0.10, 0.8), (0.15, 0.4), (0.20, 0.16)],
        "growth_note": "raw_growth_mechanism.txt §3.1：MAS1 (5cm,.8)(10cm,.8)(15cm,.4)(20cm,.16)",
        "zone_from_swirl": True,
        "zone_note": "swirls 表 MAS1 只有 1 个值 7 cm（单 area，1 次 pre-goal nonload）",
        "extra_unresolved": [],
    },
    "cancer": {
        "label": "Make Cancer",
        "growth": [],
        "growth_note": "无：Cancer 的分段回收比例不在任何公开来源里（只给了 MAS1–9 与 MTM）",
        "zone_from_swirl": False,
        "zone_note": "swirls 表 Cancer 行 Initial size=5 与 START_DIAMETER=22.2 不自洽，整行不采信",
        "extra_unresolved": [
            "Make Cancer 的分段回收比例（Pickup Volume Penalty 的关卡曲线）没有公开来源；"
            "语料里 growth_curve 为空，G06 因此报‘原作未知’",
            "Make Cancer 的 swirls 行自相矛盾（Initial size 5 vs START_DIAMETER 22.2），"
            "换区阈值没有可信来源",
        ],
    },
}


class BuildError(RuntimeError):
    pass


def _open_rows(path: Path) -> List[Dict[str, str]]:
    if not path.is_file():
        raise BuildError("missing input: %s\n(point --meta at the data-mine export "
                         "directory, default %s)" % (path, DEFAULT_META))
    delimiter = "\t" if path.suffix == ".tsv" else ","
    with open(path, newline="", encoding="utf-8-sig") as fh:
        return list(csv.DictReader(fh, delimiter=delimiter))


def _num(text: Optional[str], default=None):
    text = (text or "").strip()
    if text in ("", "-", "None"):
        return default
    try:
        return float(text)
    except ValueError:
        return default


def _idx(text: Optional[str]) -> str:
    """Normalise a dumped integer key ("2" or "2.0" → "2")."""
    value = _num(text, None)
    if value is None:
        return (text or "").strip()
    return str(int(round(value)))


def _comma_floats(text: Optional[str]) -> List[float]:
    out = []
    for part in (text or "").split(","):
        value = _num(part, None)
        if value is not None:
            out.append(value)
    return out


def _one(rows: List[Dict[str, str]], key: str, value: str, what: str):
    for row in rows:
        if row.get(key) == value:
            return row
    raise BuildError("%s: no row with %s == %r" % (what, key, value))


def build_stage(key: str, spec: Dict[str, Any], tables: Dict[str, List[Dict[str, str]]]):
    mission = _one(tables["missions"], "Note", spec["label"], "missions")
    sim = _one(tables["sim_missions"], "", spec["label"], "sim_missions")
    english = {r["Internal name"]: r["English name"] for r in tables["all_objects"]}
    penalty = {r["English name"]: _num(r.get("Pickup Volume Penalty"))
               for r in tables["collectible_objects"]}
    props = {_idx(r["nameIndex"]): r for r in tables["name_props"]}

    # ---- instances (area 0 is the only area with transforms) --------------
    instances: List[Dict[str, Any]] = []
    first_type: Dict[str, Dict[str, str]] = {}
    for row in tables["object_dump"]:
        if row["mission"] != spec["label"]:
            continue
        idx = _idx(row["u16MonoNameIdx"])
        first_type.setdefault(idx, row)
        position = None
        placed = False
        text = (row.get("position") or "").strip()
        if text.startswith("("):
            parts = [p.strip() for p in text.strip("()").split(",")]
            if len(parts) == 3:
                values = [float(p) for p in parts]
                if any(abs(v) > 1e-9 for v in values):
                    position = [round(v, 6) for v in values]
                    placed = True
        instances.append({
            "mono_idx": idx,
            "position": position,
            "placed": placed,
            "parent_idx": (_idx(row.get("parentProp.monoControlIndex"))
                           if (row.get("parentProp.monoControlIndex") or "").strip() else None),
            "play_anim_no": _num(row.get("playAnimNo")),
            "shake_off": (_num(row.get("u8MonoShakeOffFlag"), 0) or 0) != 0,
            "loading_area": int(_num(row.get("loading area"), 0) or 0),
        })

    # ---- catalogue --------------------------------------------------------
    all_objects = {_idx(r["Object Type ID (monoNameIdx)"]): r
                   for r in tables["all_objects"]}
    catalogue: Dict[str, Dict[str, Any]] = {}
    for idx in sorted(first_type, key=lambda s: int(s)):
        row = all_objects.get(idx)
        if row is None:
            raise BuildError("%s: mono idx %s missing from all_objects" % (key, idx))
        internal = row["Internal name"]
        name = english.get(internal, row["English name"])
        volume = _num(row["Volume (m^3)"])
        own_mm = None
        if volume:
            own_mm = round((volume * 6.0 / math.pi) ** (1.0 / 3.0) * 1000.0, 3)
        prop = props.get(idx, {})
        catalogue[idx] = {
            "name": name,
            "internal_name": internal,
            "req_pickup_mm": round(_num(row["Req Pickup Size (mm)"]) or 0.0, 3),
            "volume_m3": volume,
            "own_diam_mm": own_mm,
            "category": first_type[idx].get("u8monoCategory") or None,
            "unit_type": first_type[idx].get("u8UnitType") or None,
            "kind": ("dummy_hit"
                     if internal.startswith("DummyProps[DUMMYHIT") else None),
            "path_speed_index": (prop.get("pathSpeedIndex") or None),
            "vault_points": int(_num(prop.get("num vault pts"), 0) or 0),
            "hit_lod": int(_num(first_type[idx].get("u8HitLod"), 0) or 0),
            "penalty": penalty.get(name),
        }

    instances.sort(key=lambda i: (int(i["mono_idx"]), i["position"] or [0, 0, 0]))

    # ---- parameters -------------------------------------------------------
    start_cm = float(mission["START_DIAMETER"])
    max_cm = float(mission["MAX_DIAMETER"])
    gates: List[float] = []
    if spec["zone_from_swirl"]:
        swarm = _one(tables["swirls"], "Mission name", spec["label"], "swirls")
        gates = [v / 100.0 for v in _comma_floats(swarm.get("Swirl sizes"))]
    params = {
        "start_diameter_m": start_cm / 100.0,
        "clear_diameter_m": float(mission["CLEAR_DIAMETER"]) / 100.0,
        "max_diameter_m": max_cm / 100.0,
        "area_count": int(float(mission["AREA_NUM"])),
        "instance_count": len(instances),
        "type_count": len(catalogue),
        "timer_raw": _num(mission["GAME_TIME"]),
        "timer_unit": None,
        "timer_seconds": None,
        "clear_type": mission["CLEAR_TYPE"] or None,
        "gate_volume_ratio": 10.0,
        "gate_floor_m": 0.001,
        "hud_diameter_offset_m": 0.0002,
        "growth_curve": [{"diameter_m": d, "fraction": f,
                          "evidence": spec["growth_note"]} for d, f in spec["growth"]],
        "rating_diameters_m": [v / 100.0
                               for v in _comma_floats(mission["RATING_DIAMETER"])],
        "zone_gates_m": gates,
        "camera": {
            "fov_deg": (_comma_floats(mission["CAMERA_FOV"]) or [None])[0],
            "near_m": (_comma_floats(mission["CAMERA_NEAR"]) or [None])[0],
            "far_m": (_comma_floats(mission["CAMERA_FAR"]) or [None])[0],
            "focus_distance_m": (_comma_floats(mission["FOCUS_DISTANCE"]) or [None])[0],
            "aperture_f": (_comma_floats(mission["APERTURE"]) or [None])[0],
        },
        "spawn": {
            "position": [float(sim["initPos0_x"]), float(sim["initPos0_y"]),
                         float(sim["initPos0_z"])],
            "rot_y_rad": _num(sim["initRot0"]),
            "frame_note": "sim missions 的 initPos 与实例坐标不同框（实例是 area 局部坐标）；"
                          "本关只有 area 0 有坐标",
        },
        "_todo": "timer_unit / timer_seconds 未证实（GAME_TIME=%s）" % mission["GAME_TIME"],
    }
    return {
        "_note": "UNLICENSED REFERENCE MATERIAL — 由公开数据挖掘导出（见 stage.source）。"
                 "只用于一次性重建试验：不得进入世界内容、不得随世界分发；本文件不入库"
                 "（.gitignore 排除 parity_harness/corpus_*.json），由 build_corpus.py 本地生成。",
        "schema": SCHEMA_VERSION,
        "stage": {
            "id": key,
            "name": spec["label"],
            "game": "Katamari Damacy REROLL（PS2KatamariSimulation.dll）",
            "build": "REROLL，Unity 2018.1.x（/tmp/orig_physics/reroll_datamodel.md §0）",
            "source": "REROLL Dump（missions / object dump / all objects / name_props / swirls）",
            "single_area": int(float(mission["AREA_NUM"])) == 1,
        },
        "units": {"length": "m", "diameter": "m", "volume": "m3", "time": "s"},
        "timebase": {
            "fixed_step_hz": 30.0,
            "fixed_step_s": 1.0 / 30.0,
            "frame_interpolation": "画面由 RerollPatcher 插值（逻辑仍 30 Hz）",
            "source": "reroll_datamodel.md §0（IL：deltaFrame>=1/30 → DoTick(1/30f)）",
        },
        "parameters": params,
        "catalogue": catalogue,
        "instances": instances,
        "evidence": {
            "stage.*": {"source": "missions.Note / object dump mission 列", "level": "数据挖掘"},
            "units.*": {"source": "DATAMODEL_NOTES.md §3（三源互证到 mm）", "level": "数据挖掘"},
            "timebase.*": {"source": "reroll_datamodel.md §0（FramerateUncap IL）", "level": "源码"},
            "parameters.start_diameter_m": {"source": "missions.START_DIAMETER（cm）", "level": "数据挖掘"},
            "parameters.clear_diameter_m": {"source": "missions.CLEAR_DIAMETER（cm）", "level": "数据挖掘"},
            "parameters.max_diameter_m": {"source": "missions.MAX_DIAMETER（cm）", "level": "数据挖掘"},
            "parameters.area_count": {"source": "missions.AREA_NUM / sim missions.numAreas", "level": "数据挖掘"},
            "parameters.instance_count": {"source": "object dump 行数", "level": "数据挖掘"},
            "parameters.type_count": {"source": "object dump 去重 u16MonoNameIdx", "level": "数据挖掘"},
            "parameters.timer_raw": {"source": "missions.GAME_TIME（单位未证实）", "level": "数据挖掘"},
            "parameters.timer_unit": {"source": "reroll_numbers.md §6.1（单位未知）", "level": "推断"},
            "parameters.timer_seconds": {"source": "reroll_numbers.md §6.1（单位未知）", "level": "推断"},
            "parameters.clear_type": {"source": "missions.CLEAR_TYPE", "level": "数据挖掘"},
            "parameters.gate_volume_ratio": {"source": "raw_growth_mechanism.txt §2；1,449/1,450 行 1 mm 内自洽",
                                             "level": "数据挖掘"},
            "parameters.gate_floor_m": {"source": "raw_growth_mechanism.txt §2（向下取整到 mm）", "level": "数据挖掘"},
            "parameters.hud_diameter_offset_m": {"source": "raw_growth_mechanism.txt §1（+0.02 cm）",
                                                 "level": "社区实测"},
            "parameters.growth_curve[].diameter_m": {"source": spec["growth_note"] or "无来源（空表）",
                                                     "level": "社区实测"},
            "parameters.growth_curve[].fraction": {"source": spec["growth_note"] or "无来源（空表）",
                                                   "level": "社区实测"},
            "parameters.growth_curve[].evidence": {"source": spec["growth_note"] or "无来源（空表）",
                                                   "level": "社区实测"},
            "parameters.rating_diameters_m[]": {"source": "missions.RATING_DIAMETER（4 星阈值，cm）",
                                                "level": "数据挖掘"},
            "parameters.zone_gates_m[]": {"source": spec["zone_note"], "level": "数据挖掘"},
            "parameters.camera.*": {"source": "missions.CAMERA_FOV/NEAR/FAR、FOCUS_DISTANCE、APERTURE",
                                    "level": "数据挖掘"},
            "parameters.spawn.*": {"source": "sim missions.initPos0_*/initRot0", "level": "数据挖掘"},
            "catalogue.*.name": {"source": "all objects.English name（经 internal name 连接）", "level": "数据挖掘"},
            "catalogue.*.internal_name": {"source": "all objects.Internal name", "level": "数据挖掘"},
            "catalogue.*.req_pickup_mm": {"source": "all objects.Req Pickup Size (mm)", "level": "数据挖掘"},
            "catalogue.*.volume_m3": {"source": "all objects.Volume (m^3)", "level": "数据挖掘"},
            "catalogue.*.own_diam_mm": {"source": "(V·6/π)^(1/3) 由 volume 推出", "level": "推断"},
            "catalogue.*.category": {"source": "object dump.u8monoCategory", "level": "数据挖掘"},
            "catalogue.*.unit_type": {"source": "object dump.u8UnitType", "level": "数据挖掘"},
            "catalogue.*.kind": {"source": "内部名判据：DummyProps[DUMMYHIT*] 是碰撞代理不是可收集件",
                                 "level": "推断"},
            "catalogue.*.path_speed_index": {"source": "name_props.pathSpeedIndex（语义未证实）",
                                             "level": "数据挖掘"},
            "catalogue.*.vault_points": {"source": "name_props.'num vault pts'", "level": "数据挖掘"},
            "catalogue.*.hit_lod": {"source": "object dump.u8HitLod", "level": "数据挖掘"},
            "catalogue.*.penalty": {"source": "collectible objects.Pickup Volume Penalty", "level": "数据挖掘"},
            "instances[].mono_idx": {"source": "object dump.u16MonoNameIdx", "level": "数据挖掘"},
            "instances[].position[]": {"source": "object dump.position（米，area 局部；0,0,0 表示随机位置组）",
                                       "level": "数据挖掘"},
            "instances[].placed": {"source": "position 是否为非零真值", "level": "数据挖掘"},
            "instances[].parent_idx": {"source": "object dump.parentProp.monoControlIndex", "level": "数据挖掘"},
            "instances[].play_anim_no": {"source": "object dump.playAnimNo", "level": "数据挖掘"},
            "instances[].shake_off": {"source": "object dump.u8MonoShakeOffFlag", "level": "数据挖掘"},
            "instances[].loading_area": {"source": "object dump.loading area", "level": "数据挖掘"},
        },
        "unresolved": [
            "parameters.timer_unit=null 且 parameters.timer_seconds=null：timer_raw=%s 的单位未证实"
            "（若按分钟则 240 s）；未在实机确认前不给 timer_seconds" % mission["GAME_TIME"],
            "实例只有位置，没有旋转与缩放（DATAMODEL_NOTES.md §5.1）；重建必须自定朝向",
            "DummyProps[DUMMYHIT*] 是不可见的碰撞代理，有 Req Pickup Size 但没有形状；"
            "本语料把它们保留并标 kind=dummy_hit，任何‘物件数’统计都该先剔除",
            "只有 loading area 0 有坐标；本关单 area，所以这一条不影响本语料",
        ] + spec["extra_unresolved"],
    }


def _drop_unused_evidence(corpus: Dict[str, Any]) -> None:
    """Keep one evidence pattern per field: a stale pattern is a renamed field."""
    from Docs.Design.Parity.parity_harness import corpus_schema as cs

    leaves: List[str] = []
    for key in sorted(corpus):
        if key in ("evidence", "unresolved", "schema"):
            continue
        cs._leaves(corpus[key], key, leaves)
    corpus["evidence"] = {pattern: note for pattern, note in corpus["evidence"].items()
                          if any(cs._match(pattern, leaf) for leaf in leaves)}
    if any(i["position"] is None for i in corpus["instances"]):
        corpus["evidence"]["instances[].position"] = {
            "source": "未放置实例（position 为 0,0,0 或随机位置组）",
            "level": "数据挖掘"}


def load_tables(meta: Path) -> Dict[str, List[Dict[str, str]]]:
    tables_dir = meta / "tables"
    if not tables_dir.is_dir():
        raise BuildError("missing %s — point --meta at the data-mine export "
                         "(default %s)" % (tables_dir, DEFAULT_META))
    names = {
        "missions": "raw_reroll_dump__missions.tsv",
        "object_dump": "raw_reroll_dump__object_dump.tsv",
        "all_objects": "raw_reroll_dump__all_objects.tsv",
        "collectible_objects": "raw_reroll_dump__collectible_objects.tsv",
        "name_props": "raw_reroll_dump__name_props_0x30.tsv",
        "swirls": "raw_reroll_dump__swirls.tsv",
        "sim_missions": "raw_reroll_dump__sim_missions.tsv",
    }
    return {key: _open_rows(tables_dir / filename)
            for key, filename in names.items()}


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--meta", default=str(DEFAULT_META),
                        help="data-mine export directory (default %(default)s)")
    parser.add_argument("--out", default=str(Path(__file__).resolve().parent),
                        help="where to write corpus_*.json (default: next to this file)")
    parser.add_argument("--stage", choices=sorted(STAGES) + ["all"], default="all")
    args = parser.parse_args(list(argv) if argv is not None else None)

    meta = Path(args.meta).expanduser()
    out = Path(args.out).expanduser()
    out.mkdir(parents=True, exist_ok=True)
    try:
        tables = load_tables(meta)
    except BuildError as error:
        print("build_corpus: %s" % error, file=sys.stderr)
        return 2

    keys = sorted(STAGES) if args.stage == "all" else [args.stage]
    for key in keys:
        try:
            corpus = build_stage(key, STAGES[key], tables)
        except BuildError as error:
            print("build_corpus: %s" % error, file=sys.stderr)
            return 2
        _drop_unused_evidence(corpus)
        filename = "corpus_star1.json" if key == "mas1" else "corpus_%s.json" % key
        text = json.dumps(corpus, ensure_ascii=False, indent=1, sort_keys=True)
        (out / filename).write_text(text + "\n", encoding="utf-8")
        placed = sum(1 for i in corpus["instances"] if i["placed"])
        print("%-18s %4d instances (%4d placed) %4d types  %7d bytes"
              % (filename, len(corpus["instances"]), placed,
                 len(corpus["catalogue"]), len(text)))
    return 0


if __name__ == "__main__":  # pragma: no cover - CLI
    raise SystemExit(main())
