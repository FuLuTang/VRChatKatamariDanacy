#!/usr/bin/env python3
"""检查剧情关卡数据（Levels/*.json + categories.json）的内部一致性。

不验证 Unity、Udon、可玩性、帧率、舒适度或真人数据：本脚本只读磁盘上的
JSON/CSV，用标准库和 Docs/Design/Gameplay/round_model.py 的成长公式做算术。

用法：
    python3 validate_levels.py            # 在任意目录运行
    python3 validate_levels.py --json     # 额外输出机器可读摘要
    python3 validate_levels.py --no-model # 跳过 round_model 时间估算（只查数据）

检查项（编号对应 README §4.5）：
 1. schema_version / level_id 合法且 = 文件名；同目录 level_id 唯一；order 唯一。
 2. 剧情模式不变量：mode=story、player 1/1、bank.enabled=false、
    powerups.enabled=false、score_channel=none、ledger_write=false。
 3. map_id / spawn_id / map_expectations.zones / size_gate_ids 都能在 layout.json 解析。
 4. map_snapshot 两个 sha256 与实际地图文件一致（地图一改就报「地图已变更，请复核」）。
 5. map_expectations 的两个总量与快照实际值逐字相等。
 6. reach_target_diameter：目标在球径包线内；所需单位 ≤ 相关区合计 ×0.7，且 ≤ 全图合计；
    用尺寸门算「只走相关区能不能真的长到目标」；声明了 size_gate_ids 时，目标必须等于
    门的 absorbable_at_ball_diameter_m（「达成那一刻正好能吞掉门后物件」是设计承诺）。
 7. collect_category_count：类别/定义都存在且可卷；全图该类件数 ≥ 2×count；
    第 1 星 > 出生角该类件数；第 3 星 > 单区最大供给；相关区该类件数 ≥ 第 3 星。
    reach_target_diameter 的第 3 星若高于所列区的可达球径，必须带 design_note_zh
    明确写成「刻意离开路线」的扩展目标，否则报警告；说明过时（已在可达范围内）则报错。
 8. 时间可行性：用 round_model 单人贪心曲线估算达标时刻，要求 time_limit_s ≥ 3×估算。
 9. stars 恰好 3 条、1→3、requires 严格递增、第 1 档 = quest.objective。
10. partial_band ∈ [0.5,1.0]；time_limit_s ∈ [30,300]；>150 s 且无补货时报警。
11. 所有 *_id 引用只能来自 layout.json / prop_catalog.csv / categories.json。
12. 关卡文件里出现坐标、半径、净宽或逐定义件数 → 直接 FAIL（两个总量是唯一豁免）。

估算口径（检查 8）：完全复用 Docs/Design/Gameplay/round_model.py，不另写一套成长/移动
公式，也不复制一份地图读取器：round_model.load_map() 就是唯一解析器（它没有内置快照，
默认就读 layout.json + prop_catalog.csv）。单玩家、永不交分、EatEverything 选路、seed=7、
dt=0.25 s，然后对时长做二分，取「首次达标」的秒数。这条曲线是乐观上界，不是玩家数据。
"""
import argparse
import hashlib
import importlib.util
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
MAP_DIR = ROOT / "Docs/Design/Maps/PicnicLawn"
LAYOUT_PATH = MAP_DIR / "layout.json"
CATALOG_PATH = MAP_DIR / "prop_catalog.csv"
LEVELS_DIR = HERE / "Levels"
CATEGORIES_PATH = HERE / "categories.json"
ROUND_MODEL_PATH = ROOT / "Docs/Design/Gameplay/round_model.py"

LEVEL_IDS = re.compile(r"^story_[a-z0-9_]+$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
ID_TOKEN = re.compile(r"^[a-z][a-z0-9_]*$")

STATUSES = {"PROVISIONAL_NOT_TESTED", "PROVISIONAL_TESTED", "CONFIRMED"}
QUEST_TYPES = {"reach_target_diameter", "collect_category_count", "absorb_specific_target"}
PRESENTERS = {"quest_giver"}
RETRY = re.compile(r"^(unlimited|limited:[1-9][0-9]*)$")

# README §2.2/§6-B8：无补货，120 s 已吃掉约一半地图；再长就会「吃空」。
NO_RESTOCK_SOFT_LIMIT_S = 150
TIME_LIMIT_RANGE_S = (30, 300)
PARTIAL_BAND_RANGE = (0.5, 1.0)
FEASIBILITY_FACTOR = 3.0        # time_limit_s ≥ 3 × 估算达标时刻（README §2.2）
ZONE_BUDGET_HEADROOM = 0.7      # units_for(target) ≤ 相关区合计 × 0.7（README §4.5 规则 6）

# 唯一允许出现在关卡文件里的「尺寸」键：它是玩法阈值，不是几何。
ALLOWED_METRE_KEYS = {"target_diameter_m"}
# 出现即 FAIL：关卡不得复制几何或逐定义件数（README §4.1/规则 12）。
FORBIDDEN_KEYS = {
    "position", "positions", "position_m", "position_xz", "x", "y", "z",
    "yaw", "yaw_deg", "radius_m", "clear_width_m", "min_clear_width_m",
    "width_m", "height_m", "area_m2", "count_in_map", "instances",
    "per_definition", "definition_counts", "spawn_position", "zone_shape",
}


@dataclass
class Report:
    errors: list = field(default_factory=list)
    warnings: list = field(default_factory=list)
    notes: list = field(default_factory=list)
    summary: dict = field(default_factory=dict)

    @property
    def ok(self):
        return not self.errors


# --------------------------------------------------------------------------
# 地图数据（唯一解析器在 round_model.load_map，本文件不复制一份）
# --------------------------------------------------------------------------


def file_sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


# --------------------------------------------------------------------------
# round_model（成长公式与移动/拾取常数的唯一实现，不为校验器复制一份）
# --------------------------------------------------------------------------


_MODEL = {}


def _rm():
    if "module" in _MODEL:
        return _MODEL["module"]
    if not ROUND_MODEL_PATH.is_file():
        raise SystemExit(f"ERROR: 找不到成长模型 {ROUND_MODEL_PATH}；"
                         "校验器的可达性估算依赖它（README §4.5 规则 8）。")
    spec = importlib.util.spec_from_file_location("gameplay_round_model",
                                                  ROUND_MODEL_PATH)
    module = importlib.util.module_from_spec(spec)
    # dataclasses 靠 sys.modules 里的 __module__ 解析注解，必须先注册再执行。
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    _MODEL["module"] = module
    return module


def _params(duration_s=120.0):
    return _rm().RoundParams(duration_s=float(duration_s),
                             profiles=(_rm().PlayerProfile("P1"),))


def load_map(map_dir=MAP_DIR):
    """与 round_model 同一个解析器：地图改了，校验器和模型一起看到新数。"""
    return _rm().load_map(map_dir)


def model_spec(map_data, spawn_id):
    return map_data.map_spec((spawn_id,))


def _no_bank_policy():
    if "policy" in _MODEL:
        return _MODEL["policy"]
    rm = _rm()

    class NoBank(rm.EatEverything):
        """EatEverything 的选路 + 永不交分（剧情模式没有银行）。"""

        def wants_bank(self, ctx):
            return False

    _MODEL["policy"] = NoBank()
    return _MODEL["policy"]


def model_first_time(map_data, spawn_id, *, diameter=None, items=None, cap_s=300.0):
    """round_model 单人贪心曲线首次达标的秒数；达不到返回 None。

    对时长二分（成长对时长单调），每次跑一遍 simulate_round，seed=7、dt=0.25 s。
    结果按 (内容指纹) 缓存，避免同一进程内重复模拟。
    """
    key = (map_data.absorbable_total, map_data.units_total, spawn_id,
           round(diameter, 6) if diameter is not None else None, items)
    if key in _MODEL:
        return _MODEL[key]
    spec = model_spec(map_data, spawn_id)

    def meets(duration):
        params = _params(duration)
        result = _rm().simulate_round(params, _no_bank_policy(), spec, seed=7)
        player = result.players[0]
        if diameter is not None:
            return player.final_diameter_m + 1e-9 >= diameter
        return player.absorbed_items >= items

    if not meets(cap_s):
        _MODEL[key] = None
        return None
    low, high = 0.0, cap_s
    for _ in range(12):
        middle = (low + high) / 2.0
        if meets(middle):
            high = middle
        else:
            low = middle
    _MODEL[key] = high
    return high


# --------------------------------------------------------------------------
# 校验
# --------------------------------------------------------------------------


def _scan_forbidden(node, path, errors):
    """坐标 / 半径 / 净宽 / 逐定义件数一律不许进关卡文件（README §4.1）。"""
    if isinstance(node, dict):
        for key, value in node.items():
            where = f"{path}.{key}" if path else key
            if key in FORBIDDEN_KEYS:
                errors.append(f"{where}: 关卡文件不得含几何键 {key!r}"
                              "（几何只有 layout.json 一个真源）")
            elif key.endswith("_m") and key not in ALLOWED_METRE_KEYS:
                errors.append(f"{where}: 只允许 target_diameter_m 这一个尺寸阈值，"
                              f"不得出现 {key!r}")
            _scan_forbidden(value, where, errors)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            _scan_forbidden(value, f"{path}[{index}]", errors)


def _require(obj, keys, where, errors):
    for key in keys:
        if key not in obj:
            errors.append(f"{where}: 缺必填字段 {key}")


def _require_keys_exact(obj, allowed, where, errors):
    for key in obj:
        if key not in allowed:
            errors.append(f"{where}: 未知字段 {key!r}（schema 之外，防止夹带坐标或件数）")


def _check_categories(categories_doc, map_data, levels, report):
    errors = report.errors
    if not isinstance(categories_doc, dict):
        errors.append("categories.json: 必须是对象")
        return {}
    _require_keys_exact(categories_doc,
                        {"schema_version", "status", "map_id", "note_zh",
                         "categories", "notes_zh"},
                        "categories.json", errors)
    _require(categories_doc, ["schema_version", "map_id", "note_zh", "categories"],
             "categories.json", errors)
    if categories_doc.get("schema_version") != 1:
        errors.append("categories.json: schema_version 必须为 1")
    if not str(categories_doc.get("note_zh") or "").strip():
        errors.append("categories.json: note_zh 必填——必须写明为什么不能用 CSV 的 category 列")
    if categories_doc.get("map_id") != map_data.map_id:
        errors.append(f"categories.json: map_id={categories_doc.get('map_id')!r} "
                      f"与地图 {map_data.map_id!r} 不符")

    used_by = {}
    for level in levels:
        quest = level.get("quest") or {}
        objective = quest.get("objective") or {}
        category_id = objective.get("category_id")
        if category_id:
            used_by.setdefault(category_id, set()).add(level.get("level_id"))
        for star in level.get("stars") or []:
            requires = (star or {}).get("requires") or {}
            star_category = requires.get("category_id")
            if star_category:
                used_by.setdefault(star_category, set()).add(level.get("level_id"))

    categories = categories_doc.get("categories") or {}
    if not categories:
        errors.append("categories.json: categories 不能为空")
    for category_id, body in categories.items():
        where = f"categories.json:{category_id}"
        if not ID_TOKEN.match(category_id):
            errors.append(f"{where}: 类别 id 必须匹配 {ID_TOKEN.pattern}")
        if not isinstance(body, dict):
            errors.append(f"{where}: 必须是对象")
            continue
        _require_keys_exact(body, {"name_zh", "definition_ids", "exclude_definition_ids",
                                   "notes_zh", "used_by_levels"}, where, errors)
        if not str(body.get("name_zh") or "").strip():
            errors.append(f"{where}: name_zh 必填")
        definition_ids = body.get("definition_ids")
        if not isinstance(definition_ids, list) or not definition_ids:
            errors.append(f"{where}: definition_ids 必须是非空数组")
            continue
        if len(set(definition_ids)) != len(definition_ids):
            errors.append(f"{where}: definition_ids 有重复")
        for definition_id in definition_ids:
            if definition_id not in map_data.catalog:
                errors.append(f"{where}: definition_id {definition_id!r} 不在 "
                              "prop_catalog.csv 里")
                continue
            meta = map_data.catalog[definition_id]
            if meta["role"] != "absorbable":
                errors.append(f"{where}: {definition_id} 的 role={meta['role']}，"
                              "类别只能由可卷物构成")
            if meta["count"] <= 0:
                errors.append(f"{where}: {definition_id} 在地图上有 0 个实例")
            if definition_id not in map_data.used_definitions:
                errors.append(f"{where}: {definition_id} 在目录里存在，但 "
                              f"{map_data.map_id} 没有铺任何实例")
        exclude = body.get("exclude_definition_ids", [])
        if not isinstance(exclude, list):
            errors.append(f"{where}: exclude_definition_ids 必须是数组")
            exclude = []
        for definition_id in exclude:
            if definition_id not in map_data.catalog:
                errors.append(f"{where}: exclude_definition_ids 里的 {definition_id!r} "
                              "不在 prop_catalog.csv 里")
            if definition_id in definition_ids:
                errors.append(f"{where}: {definition_id} 同时在 definition_ids 与 "
                              "exclude_definition_ids 里")
        declared = body.get("used_by_levels")
        if declared is not None:
            actual = sorted(used_by.get(category_id, set()))
            if sorted(declared) != actual:
                errors.append(f"{where}: used_by_levels={sorted(declared)} 与实际引用 "
                              f"{actual} 不符")
        instances = sum(map_data.catalog[d]["count"] for d in definition_ids
                        if d in map_data.catalog)
        units = sum(map_data.catalog[d]["count"] * map_data.catalog[d]["value"]
                    for d in definition_ids if d in map_data.catalog)
        report.notes.append(f"{where}: {body.get('name_zh')} — {len(definition_ids)} 个定义 / "
                            f"{instances} 件 / {units} 单位")
    for category_id in used_by:
        if category_id not in categories:
            errors.append(f"关卡引用了 categories.json 里没有的类别 {category_id!r}")
    return categories


def _check_level(level, filename, map_data, categories, report, model=True):
    errors = report.errors
    warnings = report.warnings
    notes = report.notes
    where = filename

    if not isinstance(level, dict):
        errors.append(f"{where}: 关卡文件必须是 JSON 对象")
        return None
    _scan_forbidden(level, where, errors)
    _require_keys_exact(
        level,
        {"schema_version", "level_id", "status", "mode", "order", "map_id",
         "ruleset_id", "map_snapshot", "quest", "time_limit_s", "spawn_id",
         "player", "bank", "powerups", "stars", "evaluation",
         "map_expectations", "notes_zh"},
        where, errors)
    _require(level, ["schema_version", "level_id", "status", "mode", "order",
                     "map_id", "ruleset_id", "map_snapshot", "quest",
                     "time_limit_s", "spawn_id", "player", "bank", "powerups",
                     "stars", "evaluation", "map_expectations"], where, errors)
    if errors and any(e.startswith(where) for e in errors):
        return None

    # ---- 1/2 标识与剧情模式不变量
    if level["schema_version"] != 1:
        errors.append(f"{where}: schema_version 必须为 1")
    level_id = level["level_id"]
    if not LEVEL_IDS.match(str(level_id)):
        errors.append(f"{where}: level_id 必须匹配 {LEVEL_IDS.pattern}")
    if Path(filename).stem != level_id:
        errors.append(f"{where}: level_id={level_id!r} 与文件名不符")
    if level["status"] not in STATUSES:
        errors.append(f"{where}: status 必须是 {sorted(STATUSES)}")
    if level["mode"] != "story":
        errors.append(f"{where}: mode 必须为 'story'（本目录只放剧情关）")
    if not isinstance(level["order"], int) or level["order"] < 1:
        errors.append(f"{where}: order 必须是 ≥1 的整数")
    if not ID_TOKEN.match(str(level["ruleset_id"])):
        errors.append(f"{where}: ruleset_id 必须是 id 形态（共享规则集引用，"
                      "关卡不得复制球径包线/吸收比/成长标定）")
    player = level["player"]
    if not isinstance(player, dict) or player.get("min") != 1 or player.get("max") != 1:
        errors.append(f"{where}: 剧情模式 player 必须是 min=max=1")
    elif set(player) != {"min", "max"}:
        errors.append(f"{where}: player 只允许 min/max 两个键")
    bank = level["bank"]
    if not isinstance(bank, dict) or bank.get("enabled") is not False:
        errors.append(f"{where}: bank.enabled 必须显式为 false（剧情模式没有交分）")
    elif set(bank) != {"enabled"}:
        errors.append(f"{where}: bank 只允许 enabled 一个键（锚点/轮换属于 layout.json）")
    powerups = level["powerups"]
    if not isinstance(powerups, dict) or powerups.get("enabled") is not False:
        errors.append(f"{where}: powerups.enabled 必须为 false（首版两关不投放道具）")
    elif set(powerups) != {"enabled"}:
        errors.append(f"{where}: powerups 只允许 enabled 一个键")
    evaluation = level["evaluation"]
    if not isinstance(evaluation, dict):
        errors.append(f"{where}: evaluation 必须是对象")
        evaluation = {}
    else:
        _require_keys_exact(evaluation,
                            {"presenter_id", "partial_band", "retry",
                             "score_channel", "ledger_write"}, f"{where}:evaluation",
                            errors)
        if evaluation.get("presenter_id") not in PRESENTERS:
            errors.append(f"{where}: evaluation.presenter_id 必须是 {sorted(PRESENTERS)}")
        if evaluation.get("score_channel") != "none":
            errors.append(f"{where}: 剧情模式 evaluation.score_channel 必须为 'none'")
        if evaluation.get("ledger_write") is not False:
            errors.append(f"{where}: evaluation.ledger_write 必须为 false（不写 PvP 账本）")
        retry = str(evaluation.get("retry"))
        if not RETRY.match(retry):
            errors.append(f"{where}: evaluation.retry 必须是 'unlimited' 或 'limited:N'")
        band = evaluation.get("partial_band")
        if not isinstance(band, (int, float)) or not (
                PARTIAL_BAND_RANGE[0] <= band <= PARTIAL_BAND_RANGE[1]):
            errors.append(f"{where}: partial_band 必须在 {PARTIAL_BAND_RANGE} 内")

    # ---- 3 地图引用
    if level["map_id"] != map_data.map_id:
        errors.append(f"{where}: map_id={level['map_id']!r} 与地图 {map_data.map_id!r} 不符")
    spawn_id = level["spawn_id"]
    if spawn_id not in map_data.spawns:
        errors.append(f"{where}: spawn_id {spawn_id!r} 不在 layout.json.spawns 里")
    expectations = level["map_expectations"]
    if not isinstance(expectations, dict):
        errors.append(f"{where}: map_expectations 必须是对象")
        expectations = {}
    else:
        _require_keys_exact(expectations,
                            {"zones", "absorbable_instances_total", "gain_units_total",
                             "size_gate_ids", "notes_zh"},
                            f"{where}:map_expectations", errors)
    zones = expectations.get("zones")
    if not isinstance(zones, list) or not zones:
        errors.append(f"{where}: map_expectations.zones 必须是非空数组")
        zones = []
    if len(set(zones)) != len(zones):
        errors.append(f"{where}: map_expectations.zones 有重复")
    for zone_id in zones:
        if zone_id not in map_data.zone_shape:
            errors.append(f"{where}: map_expectations.zones 引用了不存在的区域 {zone_id!r}")
    gate_ids = expectations.get("size_gate_ids") or []
    if not isinstance(gate_ids, list):
        errors.append(f"{where}: size_gate_ids 必须是数组")
        gate_ids = []
    for gate_id in gate_ids:
        if gate_id not in map_data.gates:
            errors.append(f"{where}: size_gate_ids 引用了不存在的尺寸门 {gate_id!r}")

    # ---- 4/5 地图快照
    snapshot = level["map_snapshot"]
    if not isinstance(snapshot, dict):
        errors.append(f"{where}: map_snapshot 必须是对象")
        snapshot = {}
    actual = {"layout_sha256": file_sha256(LAYOUT_PATH),
              "catalog_sha256": file_sha256(CATALOG_PATH)}
    for key, path in (("layout_sha256", "layout.json"),
                      ("catalog_sha256", "prop_catalog.csv")):
        pinned = str(snapshot.get(key) or "")
        if not HEX64.match(pinned):
            errors.append(f"{where}: map_snapshot.{key} 必须是小写 sha256")
            continue
        if pinned != actual[key]:
            errors.append(
                f"{where}: 地图已变更（{path} 快照 {pinned[:8]}… 实际 "
                f"{actual[key][:8]}…）。复核目标/阈值/件数后，把新的 sha256 写进 "
                "map_snapshot 再跑本校验器；本脚本不会自动改数。")
    for key in ("absorbable_instances_total", "gain_units_total"):
        pinned = expectations.get(key)
        real = (map_data.absorbable_total if key == "absorbable_instances_total"
                else map_data.units_total)
        if pinned != real:
            errors.append(f"{where}: map_expectations.{key}={pinned} 与地图实际 {real} "
                          "不符（地图漂移，请复核后改数）")

    # ---- 6/7 委托与预算
    quest = level["quest"]
    if not isinstance(quest, dict):
        errors.append(f"{where}: quest 必须是对象")
        return None
    _require_keys_exact(quest, {"type", "text_zh", "text_ja", "text_en", "objective"},
                        f"{where}:quest", errors)
    quest_type = quest.get("type")
    if quest_type not in QUEST_TYPES:
        errors.append(f"{where}: quest.type 必须是 {sorted(QUEST_TYPES)}")
        return None
    if quest_type == "absorb_specific_target":
        errors.append(f"{where}: absorb_specific_target 在本 schema 版本未实现"
                      "（地图没有唯一实例，见 README §6-B6）")
        return None
    text_zh = quest.get("text_zh")
    if not isinstance(text_zh, str) or not 1 <= len(text_zh) <= 200:
        errors.append(f"{where}: quest.text_zh 必须是 1–200 字的中文正文")
    for key in ("text_ja", "text_en"):
        if quest.get(key) is not None and not isinstance(quest.get(key), str):
            errors.append(f"{where}: quest.{key} 必须是字符串或 null")
    objective = quest.get("objective")
    if not isinstance(objective, dict):
        errors.append(f"{where}: quest.objective 必须是对象")
        return None

    zone_units = map_data.zone_units(zones)
    zone_items = map_data.zone_items(zones)
    reach_diameter, reach_units, _ = map_data.reach_field(
        zones, map_data.start_diameter_m)
    map_diameter, _, _ = map_data.reach_field(
        list(map_data.zone_shape), map_data.start_diameter_m)
    notes.append(f"{where}: 相关区 {len(zones)} 个 — {zone_items} 件 / {zone_units} 单位；"
                 f"只走这些区最多长到 {reach_diameter:.3f} m "
                 f"（{reach_units} 单位）；全图 {map_data.absorbable_total} 件 / "
                 f"{map_data.units_total} 单位 → {map_diameter:.3f} m")

    model_time = None
    # 出生点/区域都解析不到时不再跑模拟：报错已经给出，模拟只会连带抛异常。
    model_ready = spawn_id in map_data.spawns and bool(zones)
    if quest_type == "reach_target_diameter":
        _require_keys_exact(objective, {"target_diameter_m"},
                            f"{where}:quest.objective", errors)
        target = objective.get("target_diameter_m")
        if not isinstance(target, (int, float)):
            errors.append(f"{where}: objective.target_diameter_m 必须是数字")
            return None
        if not (map_data.start_diameter_m <= target
                <= map_data.max_diameter_m + 1e-9):
            errors.append(f"{where}: target {target} m 超出球径包线 "
                          f"[{map_data.start_diameter_m}, {map_data.max_diameter_m}]")
        needed = _rm().units_for_diameter(float(target), _params())
        budget = zone_units * ZONE_BUDGET_HEADROOM
        notes.append(f"{where}: {target} m 需要 {needed:.2f} 单位 = 相关区的 "
                     f"{100.0 * needed / max(zone_units, 1e-9):.0f}%（预算上限 "
                     f"{budget:.1f} = {zone_units}×{ZONE_BUDGET_HEADROOM}）")
        if needed > budget:
            errors.append(f"{where}: {target} m 需要 {needed:.1f} 单位，超过相关区合计 "
                          f"{zone_units} × {ZONE_BUDGET_HEADROOM} = {budget:.1f}："
                          "只走列出的区不够，请加区或降目标")
        if needed > map_data.units_total:
            errors.append(f"{where}: {target} m 需要 {needed:.1f} 单位，全图只有 "
                          f"{map_data.units_total}：整张图吃光也达不到")
        if target > reach_diameter + 1e-9:
            errors.append(f"{where}: 只走 map_expectations.zones 最多长到 "
                          f"{reach_diameter:.3f} m（尺寸门与拾取门槛限制），到不了 "
                          f"{target} m")
        # 关卡把目标挂在尺寸门上时，「达成那一刻正好能吞掉门后那件东西」是设计承诺，
        # 必须和地图上的门阈值逐字一致；地图改阈值而关卡不改就等于悄悄换了教学。
        gate_values = {gate_id: map_data.gates[gate_id]["absorbable_at_ball_diameter_m"]
                       for gate_id in gate_ids if gate_id in map_data.gates}
        if gate_values:
            gate_value = max(gate_values.values())
            if len(set(gate_values.values())) > 1:
                errors.append(f"{where}: size_gate_ids 列出的门阈值不一致 "
                              f"{sorted(set(gate_values.values()))}，目标无法同时对齐")
            if abs(target - gate_value) > 1e-9:
                errors.append(
                    f"{where}: 目标 {target} m 与所列尺寸门的可吸阈值 {gate_value} m 不等："
                    "「达成即能吞掉门后物件」的教学承诺失效，请复核目标或门")
        if model_ready:
            model_time = model_first_time(map_data, spawn_id, diameter=float(target))

    elif quest_type == "collect_category_count":
        _require_keys_exact(objective, {"category_id", "count"},
                            f"{where}:quest.objective", errors)
        category_id = objective.get("category_id")
        count = objective.get("count")
        if category_id not in categories:
            errors.append(f"{where}: objective.category_id {category_id!r} 不在 "
                          "categories.json 里")
            return None
        if not isinstance(count, int) or count < 1:
            errors.append(f"{where}: objective.count 必须是 ≥1 的整数")
            return None
        definition_ids = categories[category_id]["definition_ids"]
        map_supply = map_data.category_counts(list(map_data.zone_shape),
                                             definition_ids)
        map_total = sum(map_supply.values())
        zone_supply = map_data.category_counts(zones, definition_ids)
        zone_total = sum(zone_supply.values())
        spawn_zone = map_data.spawns.get(spawn_id, {}).get("zone_id")
        spawn_supply = map_supply.get(spawn_zone, 0)
        richest_zone = max(map_supply.values()) if map_supply else 0
        notes.append(f"{where}: 类别 {category_id} 全图 {map_total} 件"
                     f"（{len(definition_ids)} 个定义）；相关区 {zone_total} 件；"
                     f"出生角 {spawn_zone} {spawn_supply} 件；单区最多 "
                     f"{richest_zone} 件；目标 {count} 件")
        if map_total < count:
            errors.append(f"{where}: 目标 {count} 件，但全图 {category_id} 只有 "
                          f"{map_total} 件：数量上不可达")
        if map_total < 2 * count:
            errors.append(f"{where}: 全图 {category_id} {map_total} 件 < 2×{count}："
                          "余量不足一半，补货也没有（restock=0）")
        if zone_total < count:
            errors.append(f"{where}: 相关区只有 {zone_total} 件 {category_id}，"
                          f"但目标要 {count} 件")
        if spawn_supply >= count:
            errors.append(f"{where}: 出生角 {spawn_zone} 就有 {spawn_supply} 件 "
                          f"{category_id} ≥ 目标 {count}：站着不动就能完成")
        # 第 3 星的「必须换区」判据在看星星那一节，这里只备数据。
        if model_ready:
            model_time = _collect_worst_case_time(map_data, spawn_id, zones,
                                                  definition_ids, count)

    # ---- 9/10 星级与时限
    stars = level["stars"]
    if not isinstance(stars, list) or len(stars) != 3:
        errors.append(f"{where}: stars 必须恰好 3 条（1→3 星）")
        stars = []
    thresholds = []
    for index, star in enumerate(stars):
        spot = f"{where}:stars[{index}]"
        if not isinstance(star, dict):
            errors.append(f"{spot}: 必须是对象")
            continue
        _require_keys_exact(star, {"stars", "requires", "comment_id",
                                   "design_note_zh"}, spot, errors)
        if star.get("stars") != index + 1:
            errors.append(f"{spot}: stars 必须依次为 1,2,3")
        requires = star.get("requires")
        if not isinstance(requires, dict):
            errors.append(f"{spot}: requires 必须是对象")
            continue
        if quest_type == "reach_target_diameter":
            _require_keys_exact(requires, {"target_diameter_m"}, f"{spot}.requires",
                                errors)
            value = requires.get("target_diameter_m")
        else:
            _require_keys_exact(requires, {"category_id", "count"}, f"{spot}.requires",
                                errors)
            if requires.get("category_id") != objective.get("category_id"):
                errors.append(f"{spot}: requires.category_id 必须与 quest.objective 一致")
            value = requires.get("count")
        if not isinstance(value, (int, float)):
            errors.append(f"{spot}: requires 的目标值必须是数字")
            continue
        note = star.get("design_note_zh")
        if note is not None:
            if not isinstance(note, str) or not 1 <= len(note) <= 200:
                errors.append(f"{spot}: design_note_zh 必须是 1–200 字的说明")
            elif index != 2:
                errors.append(f"{spot}: design_note_zh 只允许出现在第 3 星"
                              "（只有「离路线」目标需要说明）")
        thresholds.append(value)
        if quest_type == "reach_target_diameter" and not (
                map_data.start_diameter_m <= value <= map_data.max_diameter_m + 1e-9):
            errors.append(f"{spot}: 阈值 {value} m 超出球径包线")
    if len(thresholds) == 3:
        if any(b <= a for a, b in zip(thresholds, thresholds[1:])):
            errors.append(f"{where}: stars 的 requires 必须严格递增，实际 {thresholds}")
        if quest_type == "reach_target_diameter" and thresholds[0] != objective.get(
                "target_diameter_m"):
            errors.append(f"{where}: 第 1 星阈值 {thresholds[0]} 必须等于目标 "
                          f"{objective.get('target_diameter_m')}")
        if quest_type == "collect_category_count" and thresholds[0] != objective.get("count"):
            errors.append(f"{where}: 第 1 星件数 {thresholds[0]} 必须等于目标 "
                          f"{objective.get('count')}")
        if quest_type == "reach_target_diameter":
            if thresholds[-1] > map_diameter + 1e-9:
                errors.append(f"{where}: 第 3 星 {thresholds[-1]} m 超过全图可达 "
                              f"{map_diameter:.3f} m：永远拿不到")
            else:
                note = stars[2].get("design_note_zh") if len(stars) == 3 else None
                if thresholds[-1] > reach_diameter + 1e-9:
                    if isinstance(note, str) and note.strip():
                        notes.append(
                            f"{where}: 第 3 星 {thresholds[-1]} m 超出所列区的可达球径 "
                            f"{reach_diameter:.3f} m，已声明为刻意的离路线目标"
                            f"（全图可达 {map_diameter:.3f} m）：{note}")
                    else:
                        warnings.append(
                            f"{where}: 第 3 星 {thresholds[-1]} m 超出 "
                            f"map_expectations.zones 的可达球径 {reach_diameter:.3f} m"
                            "——拿 3 星必须离开所列路线。若这是刻意的，请在第 3 星加 "
                            "design_note_zh 说明；否则加区或降目标"
                            f"（全图可达 {map_diameter:.3f} m）")
                elif isinstance(note, str) and note.strip():
                    errors.append(
                        f"{where}: 第 3 星 {thresholds[-1]} m 已在所列区的可达范围 "
                        f"（{reach_diameter:.3f} m）内，design_note_zh 已过时："
                        "删掉说明或复核目标")
        else:
            definition_ids = categories.get(objective.get("category_id"),
                                            {}).get("definition_ids", [])
            map_supply = map_data.category_counts(list(map_data.zone_shape),
                                                 definition_ids)
            zone_supply = map_data.category_counts(zones, definition_ids)
            richest = max(map_supply.values()) if map_supply else 0
            zone_total = sum(zone_supply.values())
            if thresholds[-1] <= richest:
                errors.append(f"{where}: 第 3 星 {thresholds[-1]} 件不大于单区最大供给 "
                              f"{richest} 件：一圈就能刷满，教不会跑第二条路线")
            if thresholds[-1] > zone_total:
                errors.append(f"{where}: 第 3 星 {thresholds[-1]} 件超过相关区该类合计 "
                              f"{zone_total} 件：所列路线拿不到 3 星")
            if thresholds[-1] > sum(map_supply.values()):
                errors.append(f"{where}: 第 3 星 {thresholds[-1]} 件超过全图该类 "
                              f"{sum(map_supply.values())} 件")

    time_limit = level["time_limit_s"]
    if not isinstance(time_limit, int) or isinstance(time_limit, bool):
        errors.append(f"{where}: time_limit_s 必须是整数")
        time_limit = None
    elif not TIME_LIMIT_RANGE_S[0] <= time_limit <= TIME_LIMIT_RANGE_S[1]:
        errors.append(f"{where}: time_limit_s={time_limit} 超出 "
                      f"{TIME_LIMIT_RANGE_S[0]}–{TIME_LIMIT_RANGE_S[1]} s")
    elif time_limit > NO_RESTOCK_SOFT_LIMIT_S:
        warnings.append(f"{where}: time_limit_s={time_limit} > "
                        f"{NO_RESTOCK_SOFT_LIMIT_S} s 且没有补货（restock=0）："
                        "单人模型 120 s 已吃掉约一半地图，再长会「吃空」")
    if time_limit is not None and model is not False and model_ready:
        if model_time is None:
            errors.append(f"{where}: round_model 在 300 s 内都达不到委托目标："
                          "当前地图数据下这一关不可能完成")
        else:
            notes.append(f"{where}: 模型估算达标时刻 {model_time:.1f} s = 限时的 "
                         f"{100.0 * model_time / time_limit:.0f}%（门槛 "
                         f"{time_limit} ≥ {FEASIBILITY_FACTOR:g}×{model_time:.1f}"
                         f"={FEASIBILITY_FACTOR * model_time:.1f}）")
            if time_limit < FEASIBILITY_FACTOR * model_time:
                errors.append(f"{where}: time_limit_s={time_limit} < "
                              f"{FEASIBILITY_FACTOR:g} × 模型达标时刻 {model_time:.1f} s"
                              f"（={FEASIBILITY_FACTOR * model_time:.1f} s）："
                              "余量不足，只有接近完美的操作才做得到")

    report.summary.setdefault("levels", {})[level_id] = {
        "quest": quest_type,
        "objective": objective,
        "time_limit_s": time_limit,
        "spawn_id": spawn_id,
        "stars": thresholds,
        "zone_units": zone_units,
        "zone_items": zone_items,
        "zone_reach_diameter_m": round(reach_diameter, 3),
        "map_reach_diameter_m": round(map_diameter, 3),
        "model_meet_time_s": None if model_time is None else round(model_time, 1),
        "feasibility_requirement_s": (None if model_time is None else
                                      round(FEASIBILITY_FACTOR * model_time, 1)),
    }
    return level_id


def _collect_worst_case_time(map_data, spawn_id, zones, definition_ids, count):
    """最坏顺序下要清掉多少件才能凑够 count 件（README §3.2 的口径）。

    取「相关区里某区件数 − 该区类别件数 + count」的最大值：假设该类物件排在该区
    最后。件数曲线仍由 round_model 给（含走路），只是把「何时吸够 N 件」当判据。
    """
    per_zone_items = {z: map_data.zone_items([z]) for z in zones}
    per_zone_category = map_data.category_counts(zones, definition_ids)
    worst = 0
    for zone_id in zones:
        if per_zone_category.get(zone_id, 0) <= 0:
            continue
        worst = max(worst, per_zone_items[zone_id] - per_zone_category[zone_id] + count)
    if worst == 0:
        return None
    return model_first_time(map_data, spawn_id, items=worst)


def validate(map_dir=MAP_DIR, levels=None, categories=None, model=True):
    """跑全部检查。levels / categories 传 None 表示读磁盘（测试可传内存副本）。"""
    report = Report()
    map_data = load_map(map_dir)
    if categories is None:
        try:
            categories_doc = json.loads(CATEGORIES_PATH.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            report.errors.append(f"categories.json: 读不出来（{exc}）")
            return report
    else:
        categories_doc = categories
    if levels is None:
        level_list = []
        if not LEVELS_DIR.is_dir():
            report.errors.append(f"缺少关卡目录 {LEVELS_DIR}")
        else:
            for path in sorted(LEVELS_DIR.glob("*.json")):
                try:
                    level_list.append((path.name,
                                       json.loads(path.read_text(encoding="utf-8"))))
                except ValueError as exc:
                    report.errors.append(f"{path.name}: JSON 解析失败（{exc}）")
    else:
        level_list = list(levels)

    if not level_list:
        report.errors.append("Levels/ 里没有任何关卡 JSON")
        return report

    categories_map = _check_categories(categories_doc, map_data,
                                       [level for _, level in level_list
                                        if isinstance(level, dict)], report)

    seen_ids = {}
    seen_orders = {}
    for filename, level in level_list:
        level_id = _check_level(level, filename, map_data, categories_map, report,
                                model=model)
        if not level_id:
            continue
        if level_id in seen_ids:
            report.errors.append(f"重复 level_id {level_id}（{seen_ids[level_id]} 与 "
                                 f"{filename}）")
        seen_ids[level_id] = filename
        order = level.get("order")
        map_id = level.get("map_id")
        key = (map_id, order)
        if key in seen_orders:
            report.errors.append(f"{filename}: order={order} 与 "
                                 f"{seen_orders[key]} 重复（同一 map_id 内必须唯一）")
        seen_orders[key] = filename

    report.notes.append(f"地图快照: {map_data.map_id} — {map_data.absorbable_total} 件 / "
                        f"{map_data.units_total} 单位；球径包线 "
                        f"{map_data.start_diameter_m}–{map_data.max_diameter_m} m")
    report.summary.update({
        "status": "FAIL" if report.errors else "OK",
        "map_id": map_data.map_id,
        "map_absorbable_instances": map_data.absorbable_total,
        "map_gain_units": map_data.units_total,
        "layout_sha256": file_sha256(Path(map_dir) / "layout.json"),
        "catalog_sha256": file_sha256(Path(map_dir) / "prop_catalog.csv"),
        "levels": report.summary.get("levels", {}),
        "categories": {cid: {"definitions": len(body.get("definition_ids", [])),
                             "instances": sum(map_data.catalog[d]["count"]
                                              for d in body.get("definition_ids", [])
                                              if d in map_data.catalog)}
                       for cid, body in (categories_map or {}).items()},
        "errors": len(report.errors),
        "warnings": len(report.warnings),
        "notes": report.notes,
    })
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="输出机器可读摘要")
    parser.add_argument("--no-model", action="store_true",
                        help="跳过 round_model 时间可行性估算（调试用）")
    options = parser.parse_args()
    report = validate(model=False if options.no_model else True)
    if not options.json:
        print(f"levels: {LEVELS_DIR}")
        print(f"categories: {CATEGORIES_PATH}")
        print(f"map: {LAYOUT_PATH}")
        print(f"     {CATALOG_PATH}")
        for note in report.notes:
            print(f"  · {note}")
        print()
    if report.warnings:
        print(f"WARN ({len(report.warnings)}):")
        for warning in report.warnings:
            print(f"  ! {warning}")
    if report.errors:
        print(f"ERROR ({len(report.errors)}):")
        for error in report.errors:
            print(f"  x {error}")
    if options.json:
        print(json.dumps(report.summary, ensure_ascii=False, indent=2))
    else:
        levels = report.summary.get("levels", {})
        print(f"\n结论: {report.summary['status']}  "
              f"(关卡 {len(levels)}: {', '.join(levels)}, "
              f"类别 {len(report.summary.get('categories', {}))}, "
              f"地图 {report.summary.get('map_absorbable_instances')} 件 / "
              f"{report.summary.get('map_gain_units')} 单位, "
              f"错误 {len(report.errors)}, 警告 {len(report.warnings)})")
        print("PROVISIONAL_NOT_TESTED：本脚本只做数据自洽与算术，"
              "没有 Unity、Udon、VR、真人或性能证据。")
    return 1 if report.errors else 0


if __name__ == "__main__":
    sys.exit(main())
