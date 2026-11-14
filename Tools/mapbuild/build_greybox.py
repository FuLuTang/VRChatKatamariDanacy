#!/usr/bin/env python3
"""把灰盒关卡作者数据转成 Unity `JsonUtility` 可直接反序列化的扁平载荷（仅标准库）。

输入（作者手写、唯一真源）：
    Docs/Design/Maps/PicnicLawn/layout.json
    Docs/Design/Maps/PicnicLawn/prop_catalog.csv
输出（生成物，禁止手改）：
    Docs/Design/Maps/PicnicLawn/unity_greybox.json

为什么需要这一步：`layout.json` 用了任意嵌套、成对坐标数组、`null` 和字典键（`player_count_plans`
的 "2"/"3"/"4"）。Unity 的 `JsonUtility` 只认固定的 `[Serializable]` 形状：字段名必须匹配、
不支持字典、数组元素不能是裸数组。所以这里把结构拍平：

  * 每个记录只含标量（string / int / float）和 `string[]` / `int[]` / `float[]`；
  * 嵌套对象最多一层（顶层记录 → 标量记录，如 `bounds`、`scale`、`bank_rotation`）；
  * 成对数组改成并行数组（`waypoints_x[]` + `waypoints_z[]`，`prop_ids[]` + `prop_counts_min[]`…）；
  * 没有 `null`：可选引用写成空字符串，布尔写成 0/1；
  * 数字统一保留 3 位小数（米制），`-0.0` 归一成 `0.0`。

输出是确定性的：记录按 id（无 id 的按复合键）排序、键序固定、2 空格缩进、末尾换行、
UTF-8 无 BOM。同一输入重复运行逐字节相同，因此可以提交进仓库并用 `--check` 校验是否过期。

用法：
    python3 Tools/mapbuild/build_greybox.py            # 生成/覆盖输出
    python3 Tools/mapbuild/build_greybox.py --check    # 只检查，过期则退出码 1
    python3 Tools/mapbuild/build_greybox.py --summary  # 额外打印各集合记录数

退出码：0 成功；1 `--check` 发现过期/缺失；2 输入数据校验失败（错误信息带记录 id）。
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
from pathlib import Path

SCHEMA_VERSION = 1
PRECISION = 3  # 米制小数位；改动会让整份输出重排，必须先确认 Unity 侧精度需求

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_LAYOUT = REPO_ROOT / "Docs/Design/Maps/PicnicLawn/layout.json"
DEFAULT_CATALOG = REPO_ROOT / "Docs/Design/Maps/PicnicLawn/prop_catalog.csv"
DEFAULT_OUT = REPO_ROOT / "Docs/Design/Maps/PicnicLawn/unity_greybox.json"

GENERATOR = "Tools/mapbuild/build_greybox.py"
TIERS = ("s", "m", "l")
ROLES = ("absorbable", "fixed")
PRIORITIES = ("MVP", "later")
GATE_TYPES = ("absorb", "climb")

# ---- 2-D 放置可行性 -------------------------------------------------------- #
# 消费方（Unity 灰盒 Builder）的放置算法在 BuildProps/TryFindSpot 里：按 prop_ids
# 顺序逐个放置；每个点均匀随机采样最多 256 次，与已放点中心距 < min_spacing_m 就重试；
# 全局间距失败后只与同名定义比间距（per-definition），再失败才丢件。
#
# 这种「随机顺序吸附」（RSA）在 2-D 的圆盘覆盖率饱和值是 0.547（每个点自带半径
# S/2 的禁区圆盘），而六方最密的几何上限是 π/(2√3) ≈ 0.9069。也就是说：同一块区域，
# 随机采样只能摆到最密堆叠的 ~60%。作者数据必须按**消费者能摆到的**上限来写，否则
# 消费方就得替数据发明策略（2026-11 灰盒就是这么丢件/降级的）。
HEX_POINT_AREA = math.sqrt(3.0) / 2.0            # 六方最密：每个点占 (√3/2)·S²
RSA_DISC_COVERAGE = 0.547                        # 随机顺序吸附的圆盘覆盖率饱和值
RSA_SITE_AREA = math.pi / (4.0 * RSA_DISC_COVERAGE)  # 每个点平均占 ≈1.436·S²
CAPACITY_USAGE_WARN = 0.85                       # 超过消费者上限的 85% 就警告「没余量」

REQUIRED_LAYOUT_KEYS = (
    "map_id", "status", "bounds", "scale", "zones", "structures", "spawns",
    "bank", "size_gates", "prop_scatter", "routes", "chokepoints",
)
REQUIRED_CATALOG_COLUMNS = (
    "id", "name_zh", "category", "size_tier", "game_longest_dim_m",
    "absorb_value_placeholder", "count_in_map", "zone_ids", "role",
)

# 顶层键的固定顺序（测试与 README 都依赖它）。
TOP_LEVEL_KEYS = (
    "schema_version", "map_id", "status", "generator",
    "bounds", "scale",
    "zones", "structures", "spawns",
    "bank_anchors", "bank_rotation",
    "size_gates", "scatter_regions", "routes", "chokepoints",
    "props",
)


class BuildError(Exception):
    """输入数据校验失败；`errors` 每条都指明出错的记录 id。"""

    def __init__(self, errors):
        self.errors = list(errors)
        super().__init__(f"{len(self.errors)} validation error(s)")


# --------------------------------------------------------------------------- #
# 数值与键序
# --------------------------------------------------------------------------- #

def r3(value) -> float:
    """米制标量：保留 3 位小数，并把 -0.0 归一成 0.0（否则 diff 会抖）。"""
    out = round(float(value), PRECISION)
    return 0.0 if out == 0.0 else out


def number(value):
    """保留整数的整型形状（JSON 里写 196 而不是 196.0），浮点走 r3。"""
    if isinstance(value, bool):
        return 1 if value else 0
    if isinstance(value, int):
        return value
    return r3(value)


def flag(value) -> int:
    """布尔写成 0/1：Unity 侧用 int 字段读，避免 bool 字段名/默认值差异。"""
    return 1 if value else 0


def text(value) -> str:
    """没有 null：缺失/None 一律空字符串。"""
    return "" if value is None else str(value)


def string_list(values) -> list:
    return [text(v) for v in (values or [])]


def sorted_by(items, key):
    return sorted(items, key=key)


# --------------------------------------------------------------------------- #
# 输入
# --------------------------------------------------------------------------- #

def load_layout(path: Path) -> dict:
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise BuildError([f"{path}: 读不到 layout.json（{exc.strerror}）"]) from exc
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise BuildError([f"{path}: JSON 解析失败于第 {exc.lineno} 行：{exc.msg}"]) from exc
    if not isinstance(data, dict):
        raise BuildError([f"{path}: 顶层必须是对象"])
    return data


def load_catalog(path: Path) -> dict:
    try:
        with path.open(encoding="utf-8", newline="") as handle:
            reader = csv.DictReader(handle)
            columns = reader.fieldnames or []
            rows = list(reader)
    except OSError as exc:
        raise BuildError([f"{path}: 读不到 prop_catalog.csv（{exc.strerror}）"]) from exc

    errors = [f"prop_catalog.csv: 缺列 {col}" for col in REQUIRED_CATALOG_COLUMNS
              if col not in columns]
    props = {}
    for line_no, row in enumerate(rows, start=2):
        prop_id = (row.get("id") or "").strip()
        if not prop_id:
            errors.append(f"prop_catalog.csv 行 {line_no}: 空 id")
            continue
        if prop_id in props:
            errors.append(f"{prop_id}: prop_catalog.csv 里 id 重复（行 {line_no}）")
            continue
        props[prop_id] = {"row": row, "line": line_no}
    if errors:
        raise BuildError(errors)
    return props


# --------------------------------------------------------------------------- #
# 2-D 放置可行性的容量模型
# --------------------------------------------------------------------------- #

def rect_area(zone: dict) -> tuple:
    """(宽, 深, 面积)，只认 rect；区域记录的 area_m2 由 validate() 另行核对。"""
    shape = zone.get("shape") or {}
    width = float(shape["max_x"]) - float(shape["min_x"])
    depth = float(shape["max_z"]) - float(shape["min_z"])
    return width, depth, width * depth


def point_capacity(area_m2: float, spacing_m: float) -> tuple:
    """返回 (六方最密上限, 消费者上限)。

    两个值都是「这块区域、这个间距下最多能放几个点」，差别只在堆叠策略：

      * 六方最密：每点占 (√3/2)·S²，任何摆放（包括离线最优解）都超不过；
      * 消费者上限：消费方是 256 次均匀拒绝采样，等价的随机顺序吸附饱和覆盖率是
        0.547，每点平均占 π/(4·0.547)·S² ≈ 1.436·S²，只有最密堆叠的 ~60%。

    作者数据要按后者写：超过它，Unity 那边就会丢件或退回按定义间距。
    """
    if area_m2 <= 0 or spacing_m <= 0:
        return 0, 0
    s2 = spacing_m * spacing_m
    optimal = int(math.floor(area_m2 / (HEX_POINT_AREA * s2)))
    consumer = int(math.floor(area_m2 / (RSA_SITE_AREA * s2)))
    return optimal, consumer


def footprint_pair_floor(instances) -> tuple:
    """(区内任意两件的脚印间距下限, 说明)。

    `instances` 是 (id, 脚印长边 m) 的展开列表（每件一份）。两件 footprint 并排时
    中心距至少要 (d_i + d_j) / 2，否则两块几何会互相穿模。消费方只有一个标量
    min_spacing_m，所以作者必须把标量压在这个下限之上——这是「大件把区域撑爆」的
    真正原因，而不是脚印面积总和。
    """
    dims = sorted(instances, key=lambda item: (-item[1], item[0]))
    if len(dims) < 2:
        return 0.0, ""
    (id_a, d_a), (id_b, d_b) = dims[0], dims[1]
    return (d_a + d_b) / 2.0, f"{id_a} {d_a:g} m + {id_b} {d_b:g} m"


def scatter_feasibility(zone: dict, region: dict, props: dict) -> dict:
    """把一条 prop_scatter 记录折算成可判定的数字（供校验、--feasibility、测试共用）。"""
    width, depth, area = rect_area(zone)
    spacing = float(region["min_spacing_m"])
    instances = []
    by_definition = []
    for definition in region.get("definitions") or []:
        prop_id = text(definition.get("id"))
        if prop_id not in props:
            continue
        count = int(definition.get("count_max", 0))
        by_definition.append((prop_id, count, props[prop_id]["dim"]))
        instances.extend([(prop_id, props[prop_id]["dim"])] * max(0, count))
    optimal, consumer = point_capacity(area, spacing)
    floor_sep, floor_label = footprint_pair_floor(instances)
    biggest = max(instances, key=lambda item: item[1], default=(None, 0.0))
    total = len(instances)
    return {
        "zone_id": text(region.get("zone_id")),
        "width_m": width,
        "depth_m": depth,
        "area_m2": area,
        "spacing_m": spacing,
        "count": total,
        "capacity_optimal": optimal,
        "capacity_consumer": consumer,
        "usage": (total / consumer) if consumer else float("inf"),
        "footprint_floor_m": floor_sep,
        "footprint_label": floor_label,
        "biggest_id": biggest[0],
        "biggest_dim_m": biggest[1],
        "definitions": by_definition,
    }


# --------------------------------------------------------------------------- #
# 校验（错误信息必须指向出错的记录 id）
# --------------------------------------------------------------------------- #

def validate(layout: dict, catalog: dict) -> list:
    """校验输入；返回不影响生成的 warning 列表（错误一律抛 BuildError）。"""
    errors = []
    warnings = []

    for key in REQUIRED_LAYOUT_KEYS:
        if key not in layout:
            errors.append(f"layout.json: 缺顶层键 {key}")
    if errors:
        raise BuildError(errors)

    scale = layout["scale"]
    band_ids = [band["id"] for band in scale.get("ball_bands", [])]
    if not band_ids:
        errors.append("scale.ball_bands: 至少要有一个尺寸档")

    # ---- 区域
    zones = {}
    for zone in layout["zones"]:
        zone_id = text(zone.get("id")).strip()
        if not zone_id:
            errors.append("zones: 有空 id 的区域记录")
            continue
        if zone_id in zones:
            errors.append(f"{zone_id}: 区域 id 重复")
            continue
        zones[zone_id] = zone
        shape = zone.get("shape") or {}
        if shape.get("type") != "rect":
            errors.append(f"{zone_id}: shape.type 必须是 rect，实际 {shape.get('type')!r}")
            continue
        try:
            width = float(shape["max_x"]) - float(shape["min_x"])
            depth = float(shape["max_z"]) - float(shape["min_z"])
        except (KeyError, TypeError, ValueError):
            errors.append(f"{zone_id}: 区域矩形缺 min_x/max_x/min_z/max_z 或不是数字")
            continue
        if width <= 0 or depth <= 0:
            errors.append(f"{zone_id}: 区域矩形退化（宽 {width}，深 {depth}）")
        declared_area = zone.get("area_m2")
        try:
            if declared_area is not None and abs(width * depth - float(declared_area)) > 0.01:
                errors.append(f"{zone_id}: area_m2={declared_area} 与矩形 {width * depth:.3f} 不符")
        except (TypeError, ValueError):
            errors.append(f"{zone_id}: area_m2={declared_area!r} 不是数字")
        bands = zone.get("tier_band") or []
        if len(bands) != 2:
            errors.append(f"{zone_id}: tier_band 必须是 [最小档, 最大档]，实际 {bands!r}")
        for band in bands:
            if band not in band_ids:
                errors.append(f"{zone_id}: tier_band 引用未知尺寸档 {band!r}")

    # ---- 目录属性（先整理成按 id 索引，后续引用检查与计数都靠它）
    props = {}
    for prop_id, meta in catalog.items():
        row = meta["row"]
        tier = (row.get("size_tier") or "").strip()
        role = (row.get("role") or "").strip()
        priority = (row.get("priority") or "").strip()
        if tier not in TIERS:
            errors.append(f"{prop_id}: size_tier 必须是 {list(TIERS)}，实际 {tier!r}")
        if role not in ROLES:
            errors.append(f"{prop_id}: role 必须是 {list(ROLES)}，实际 {role!r}")
        if priority and priority not in PRIORITIES:
            errors.append(f"{prop_id}: priority 必须是 {list(PRIORITIES)}，实际 {priority!r}")
        try:
            count = int(row["count_in_map"])
            value = int(row["absorb_value_placeholder"])
            dim = float(row["game_longest_dim_m"])
        except (TypeError, ValueError):
            errors.append(f"{prop_id}: game_longest_dim_m / absorb_value_placeholder / count_in_map 必须是数字")
            continue
        if count < 0 or value < 0 or dim <= 0:
            errors.append(f"{prop_id}: count/value 不能为负，game_longest_dim_m 必须为正")
        zone_ids = [part.strip() for part in text(row.get("zone_ids")).split(";") if part.strip()]
        for zone_id in zone_ids:
            if zone_id.lower() in ("all", "none"):
                errors.append(f"{prop_id}: zone_ids 里的 {zone_id!r} 不是区域 id（该列应直接写区域 id 或留空）")
            elif zone_id not in zones:
                errors.append(f"{prop_id}: zone_ids 引用未知区域 {zone_id}")
        props[prop_id] = {"count": count, "value": value, "tier": tier, "role": role,
                          "dim": dim, "zone_ids": zone_ids, "line": meta["line"],
                          "row": row}
    if errors:
        raise BuildError(errors)

    # ---- 结构
    structure_use = {}
    for index, structure in enumerate(layout["structures"]):
        definition_id = text(structure.get("definition_id")).strip()
        label = f"structures[{index}] {definition_id or '<空 definition_id>'}"
        if definition_id not in props:
            errors.append(f"{label}: 未知目录 id {definition_id!r}")
            continue
        zone_id = structure.get("zone_id")
        if zone_id is not None and text(zone_id) not in zones:
            errors.append(f"{label}: 未知区域 {zone_id!r}")
        if not isinstance(structure.get("position"), list) or len(structure["position"]) != 3:
            errors.append(f"{label}: position 必须是 [x, y, z]")
        if not isinstance(structure.get("size_m"), list) or len(structure["size_m"]) != 3:
            errors.append(f"{label}: size_m 必须是 [x, y, z]")
        structure_use[definition_id] = structure_use.get(definition_id, 0) + 1

    # ---- 出生点
    spawns = {}
    for spawn in layout["spawns"]:
        spawn_id = text(spawn.get("id")).strip()
        if not spawn_id:
            errors.append("spawns: 有空 id 的出生点")
            continue
        if spawn_id in spawns:
            errors.append(f"{spawn_id}: 出生点 id 重复")
            continue
        spawns[spawn_id] = spawn
        if text(spawn.get("zone_id")) not in zones:
            errors.append(f"{spawn_id}: 未知区域 {spawn.get('zone_id')!r}")

    # ---- 交分台与轮换
    bank = layout["bank"]
    if text(bank.get("zone_id")) not in zones:
        errors.append(f"bank: 未知区域 {bank.get('zone_id')!r}")
    anchors = {}
    for anchor in bank.get("anchors", []):
        anchor_id = text(anchor.get("id")).strip()
        if not anchor_id:
            errors.append("bank.anchors: 有空 id 的锚点")
            continue
        if anchor_id in anchors:
            errors.append(f"{anchor_id}: 交分锚点 id 重复")
            continue
        anchors[anchor_id] = anchor
    rotation = bank.get("rotation") or {}
    order = [text(v) for v in rotation.get("order", [])]
    if sorted(order) != sorted(anchors):
        errors.append(f"bank.rotation.order 不是锚点集合的排列：{order} vs {sorted(anchors)}")
    if len(set(order)) != len(order):
        errors.append(f"bank.rotation.order 有重复锚点：{order}")
    activate_at = rotation.get("activate_at_s") or []
    if len(activate_at) != len(order):
        errors.append(f"bank.rotation.activate_at_s 长度 {len(activate_at)} 与轮换顺序 {len(order)} 不一致")

    # ---- 尺寸门
    gates = {}
    for gate in layout["size_gates"]:
        gate_id = text(gate.get("id")).strip()
        if not gate_id:
            errors.append("size_gates: 有空 id 的尺寸门")
            continue
        if gate_id in gates:
            errors.append(f"{gate_id}: 尺寸门 id 重复")
            continue
        gates[gate_id] = gate
        gate_type = text(gate.get("type"))
        if gate_type not in GATE_TYPES:
            errors.append(f"{gate_id}: 未知尺寸门类型 {gate_type!r}")
        definition_id = text(gate.get("definition_id"))
        if definition_id not in props:
            errors.append(f"{gate_id}: 未知目录 id {definition_id!r}")
        elif gate_type == "absorb" and props[definition_id]["role"] != "absorbable":
            errors.append(f"{gate_id}: absorb 型门的 {definition_id} 不是可卷物（role={props[definition_id]['role']}）")
        elif gate_type == "climb" and props[definition_id]["role"] != "fixed":
            errors.append(f"{gate_id}: climb 型门的 {definition_id} 不是固定地形（role={props[definition_id]['role']}）")
        try:
            blocked = float(gate["blocks_ball_diameter_below_m"])
            opened = float(gate["absorbable_at_ball_diameter_m"])
        except (KeyError, TypeError, ValueError):
            errors.append(f"{gate_id}: blocks_ball_diameter_below_m / absorbable_at_ball_diameter_m 必须是数字")
        else:
            if not blocked < opened:
                errors.append(f"{gate_id}: 阻挡尺寸 {blocked} 必须小于开启尺寸 {opened}")
        opens_path = text(gate.get("opens_path"))
        if opens_path not in zones and opens_path not in {text(r.get("id")) for r in layout["routes"]}:
            errors.append(f"{gate_id}: opens_path {opens_path!r} 既不是区域也不是路线")

    # ---- 散布区
    scatter = {}
    for region in layout["prop_scatter"]:
        zone_id = text(region.get("zone_id")).strip()
        if zone_id not in zones:
            errors.append(f"prop_scatter: 未知区域 {zone_id!r}")
            continue
        if zone_id in scatter:
            errors.append(f"prop_scatter: 区域 {zone_id} 有多条散布记录")
            continue
        scatter[zone_id] = region
        if text(region.get("tier_band")) not in band_ids:
            errors.append(f"prop_scatter[{zone_id}]: 未知尺寸档 {region.get('tier_band')!r}")
        try:
            spacing = float(region["min_spacing_m"])
        except (KeyError, TypeError, ValueError):
            errors.append(f"prop_scatter[{zone_id}]: min_spacing_m 必须是数字")
        else:
            if spacing <= 0:
                errors.append(f"prop_scatter[{zone_id}]: min_spacing_m 必须为正")
        if not region.get("definitions"):
            errors.append(f"prop_scatter[{zone_id}]: definitions 为空")
        for definition in region.get("definitions") or []:
            definition_id = text(definition.get("id"))
            label = f"prop_scatter[{zone_id}]/{definition_id or '<空 id>'}"
            if definition_id not in props:
                errors.append(f"{label}: 未知目录 id")
                continue
            if props[definition_id]["role"] != "absorbable":
                errors.append(f"{label}: 不是可卷物（role={props[definition_id]['role']}），不能进散布")
            try:
                count_min = int(definition["count_min"])
                count_max = int(definition["count_max"])
            except (KeyError, TypeError, ValueError):
                errors.append(f"{label}: count_min / count_max 必须是整数")
                continue
            if count_min < 1:
                errors.append(f"{label}: count_min 必须 ≥ 1，实际 {count_min}")
            if count_max < count_min:
                errors.append(f"{label}: count_max={count_max} 小于 count_min={count_min}")

    # ---- 2-D 放置可行性：作者要求必须真的摆得下（这是 2026-11 灰盒丢件的那道缺口）
    # 消费方只有一个 per-region 标量 min_spacing_m；它摆不下时只能丢件或退回
    # per-definition 间距。所以这里按「同一个标量 + 随机顺序采样」算上限，超了就报错。
    for zone_id in sorted(scatter):
        region = scatter[zone_id]
        try:
            info = scatter_feasibility(zones[zone_id], region, props)
        except (KeyError, TypeError, ValueError, ZeroDivisionError):
            continue  # 上面已经报过字段错误，这里不叠第二条
        label = f"prop_scatter[{zone_id}]"
        spacing = info["spacing_m"]
        total = info["count"]
        consumer = info["capacity_consumer"]
        optimal = info["capacity_optimal"]
        if spacing <= 0 or total <= 0:
            continue

        if info["biggest_dim_m"] + 0.1 > min(info["width_m"], info["depth_m"]):
            errors.append(
                f"{label}: {info['biggest_id']} 的脚印 {info['biggest_dim_m']:g} m 放不进 "
                f"{min(info['width_m'], info['depth_m']):.2f} m 宽的区域（左右各留 0.05 m）")

        if info["footprint_floor_m"] > spacing + 1e-9:
            errors.append(
                f"{label}: min_spacing_m={spacing:g} 小于区内最大两件的脚印间距 "
                f"{info['footprint_floor_m']:g} m（{info['footprint_label']}）："
                f"这两件会被摆到互相穿模。把 min_spacing_m 提到 ≥"
                f"{info['footprint_floor_m']:.2f} m，或减少/缩小大件")

        if total > optimal:
            errors.append(
                f"{label}: {total} 件在 {spacing:g} m 间距下几何上不可能摆下——"
                f"{info['area_m2']:.1f} m² 的六方最密上限是 {optimal} 件"
                f"（每件占 {HEX_POINT_AREA * spacing * spacing:.2f} m²）。"
                f"降件数、放宽 min_spacing_m 或改区域尺寸")
        elif total > consumer:
            errors.append(
                f"{label}: {total} 件在 {spacing:g} m 间距下摆不下——"
                f"{info['area_m2']:.1f} m² 的随机顺序摆放上限是 {consumer} 件"
                f"（六方最密上限 {optimal} 件，消费方只能到最密的 ~60%）。"
                f"Unity Builder 会为此丢件或退回 per-definition 间距，属于数据错误："
                f"把件数降到 ≤{consumer}、把 min_spacing_m 放宽到 ≥"
                f"{math.sqrt(info['area_m2'] / (RSA_SITE_AREA * total)):.2f} m，或改区域尺寸")
        elif total > CAPACITY_USAGE_WARN * consumer:
            warnings.append(
                f"{label}: {total}/{consumer} 件（{total / consumer:.0%}）已接近随机摆放上限，"
                f"只剩 {consumer - total} 件余量；建议 ≤{int(CAPACITY_USAGE_WARN * consumer)} 件")

    # ---- 路线
    routes = {}
    for route in layout["routes"]:
        route_id = text(route.get("id")).strip()
        if not route_id:
            errors.append("routes: 有空 id 的路线")
            continue
        if route_id in routes:
            errors.append(f"{route_id}: 路线 id 重复")
            continue
        routes[route_id] = route
        waypoints = route.get("waypoints") or []
        if len(waypoints) < 2:
            errors.append(f"{route_id}: 路线至少需要 2 个路点，实际 {len(waypoints)}")
        for point in waypoints:
            if not isinstance(point, list) or len(point) != 2:
                errors.append(f"{route_id}: 路点必须是 [x, z]，实际 {point!r}")
                break
        for key in ("width_m", "min_clear_width_m"):
            try:
                if float(route[key]) <= 0:
                    errors.append(f"{route_id}: {key} 必须为正")
            except (KeyError, TypeError, ValueError):
                errors.append(f"{route_id}: {key} 必须是数字")
        for key, table in (("spawn_id", spawns), ("bank_anchor_id", anchors), ("gate_id", gates)):
            reference = text(route.get(key))
            if reference and reference not in table:
                errors.append(f"{route_id}: 引用不存在的 {key} {reference!r}")

    # ---- 咽喉
    chokepoints = {}
    for chokepoint in layout["chokepoints"]:
        chokepoint_id = text(chokepoint.get("id")).strip()
        if not chokepoint_id:
            errors.append("chokepoints: 有空 id 的咽喉记录")
            continue
        if chokepoint_id in chokepoints:
            errors.append(f"{chokepoint_id}: 咽喉 id 重复")
            continue
        chokepoints[chokepoint_id] = chokepoint
        gate_id = text(chokepoint.get("gate_id"))
        if gate_id and gate_id not in gates:
            errors.append(f"{chokepoint_id}: 引用不存在的尺寸门 {gate_id!r}")
        try:
            if float(chokepoint["clear_width_m"]) <= 0:
                errors.append(f"{chokepoint_id}: clear_width_m 必须为正")
        except (KeyError, TypeError, ValueError):
            errors.append(f"{chokepoint_id}: clear_width_m 必须是数字")

    # ---- 交分锚点引用
    for anchor_id, anchor in anchors.items():
        for key in ("entrances", "approach_route_ids"):
            for reference in anchor.get(key) or []:
                if text(reference) not in routes:
                    errors.append(f"{anchor_id}: {key} 引用不存在的路线 {reference!r}")

    # ---- 计数一致性：目录 count = 散布 + 可吸门实例 / 固定结构实例
    scatter_use = {}
    for region in scatter.values():
        for definition in region.get("definitions") or []:
            definition_id = text(definition.get("id"))
            if definition_id in props:
                scatter_use[definition_id] = scatter_use.get(definition_id, 0) + int(definition["count_min"])
    gate_use = {}
    for gate in gates.values():
        if text(gate.get("type")) == "absorb":
            definition_id = text(gate.get("definition_id"))
            gate_use[definition_id] = gate_use.get(definition_id, 0) + 1

    for prop_id, meta in props.items():
        if meta["role"] == "absorbable":
            expected = scatter_use.get(prop_id, 0) + gate_use.get(prop_id, 0)
            breakdown = f"散布 {scatter_use.get(prop_id, 0)} + 可吸门 {gate_use.get(prop_id, 0)}"
        else:
            expected = structure_use.get(prop_id, 0)
            breakdown = f"结构实例 {expected}"
        if meta["count"] != expected:
            errors.append(f"{prop_id}: 目录 count_in_map={meta['count']} 与布局实例数 {expected} 不符（{breakdown}）")

    if errors:
        raise BuildError(errors)
    return warnings


# --------------------------------------------------------------------------- #
# 生成
# --------------------------------------------------------------------------- #

def _scale_record(scale: dict) -> dict:
    bands = scale.get("ball_bands") or []
    pickup = scale.get("item_tier_pickup_diameter_m") or {}
    camera = scale.get("camera_eye_height_m_by_band") or {}
    pickup_order = [tier for tier in TIERS if tier in pickup]
    camera_order = [band["id"] for band in bands if band["id"] in camera]

    record = {
        "status": text(scale.get("status")),
        "map_extent_x_m": number(scale["map_extent_x_m"]),
        "map_extent_z_m": number(scale["map_extent_z_m"]),
        "ball_start_collision_diameter_m": number(scale["ball_start_collision_diameter_m"]),
        "ball_max_collision_diameter_m": number(scale["ball_max_collision_diameter_m"]),
        "ball_band_count": len(bands),
        "ball_band_ids": [text(band["id"]) for band in bands],
        "ball_band_names_zh": [text(band["name_zh"]) for band in bands],
        "ball_band_min_m": [number(band["min_m"]) for band in bands],
        "ball_band_max_m": [number(band["max_m"]) for band in bands],
        "pickup_tier_ids": pickup_order,
        "pickup_diameter_min_m": [number(pickup[tier][0]) for tier in pickup_order],
        "pickup_diameter_max_m": [number(pickup[tier][1]) for tier in pickup_order],
        "max_traversable_slope_percent": number(scale["max_traversable_slope_percent"]),
        "max_route_step_m": number(scale["max_route_step_m"]),
        "boundary_hedge_height_m": number(scale["boundary_hedge_height_m"]),
        "boundary_hedge_depth_m": number(scale["boundary_hedge_depth_m"]),
        "core_hedge_height_m": number(scale["core_hedge_height_m"]),
        "core_hedge_depth_m": number(scale["core_hedge_depth_m"]),
        "core_entrance_clear_m": number(scale["core_entrance_clear_m"]),
        "core_door_clear_m": number(scale["core_door_clear_m"]),
        "route_width_main_m": number(scale["route_width_main_m"]),
        "route_width_spur_m": number(scale["route_width_spur_m"]),
        "min_route_margin_over_max_ball_m": number(scale["min_route_margin_over_max_ball_m"]),
        "bank_pad_radius_m": number(scale["bank_pad_radius_m"]),
        "bank_anchor_count": number(scale["bank_anchor_count"]),
        "bank_anchor_period_s": number(scale["bank_anchor_period_s"]),
        "bank_warning_s": number(scale["bank_warning_s"]),
        "spawn_to_bank_target_m": number(scale["spawn_to_bank_target_m"]),
        "camera_band_ids": camera_order,
        "camera_eye_height_m": [number(camera[band]) for band in camera_order],
    }
    return record


def _zone_records(layout: dict) -> list:
    records = []
    for zone in layout["zones"]:
        shape = zone["shape"]
        records.append({
            "id": text(zone["id"]),
            "name_zh": text(zone.get("name_zh")),
            "purpose": text(zone.get("purpose")),
            "shape_type": text(shape.get("type")),
            "min_x": number(shape["min_x"]),
            "max_x": number(shape["max_x"]),
            "min_z": number(shape["min_z"]),
            "max_z": number(shape["max_z"]),
            "ground_y_m": number(zone["ground_y_m"]),
            "tier_band_min": text((zone.get("tier_band") or ["", ""])[0]),
            "tier_band_max": text((zone.get("tier_band") or ["", ""])[1]),
            "area_m2": number(zone["area_m2"]),
            "surface": text(zone.get("surface")),
            "notes": text(zone.get("notes")),
        })
    return sorted_by(records, key=lambda record: record["id"])


def _structure_records(layout: dict) -> list:
    records = []
    for structure in layout["structures"]:
        x, y, z = (number(v) for v in structure["position"])
        size_x, size_y, size_z = (number(v) for v in structure["size_m"])
        records.append({
            "definition_id": text(structure["definition_id"]),
            "zone_id": text(structure.get("zone_id")),
            "kind": text(structure.get("kind")),
            "position_x": x,
            "position_y": y,
            "position_z": z,
            "size_x": size_x,
            "size_y": size_y,
            "size_z": size_z,
            "yaw_deg": number(structure.get("yaw_deg", 0)),
            "notes": text(structure.get("notes")),
        })
    # 结构没有 id：按 (definition_id, zone_id, x, y, z, yaw) 排序，重复运行稳定。
    records.sort(key=lambda record: (
        record["definition_id"], record["zone_id"],
        record["position_x"], record["position_y"], record["position_z"],
        record["size_x"], record["size_y"], record["size_z"], record["yaw_deg"],
    ))
    for index, record in enumerate(records):
        record_indexed = {"index": index}
        record_indexed.update(record)
        records[index] = record_indexed
    return records


def _spawn_records(layout: dict) -> list:
    records = []
    for spawn in layout["spawns"]:
        x, y, z = (number(v) for v in spawn["position"])
        records.append({
            "id": text(spawn["id"]),
            "zone_id": text(spawn.get("zone_id")),
            "position_x": x,
            "position_y": y,
            "position_z": z,
            "yaw_deg": number(spawn.get("yaw_deg", 0)),
            "facing": text(spawn.get("facing")),
            "notes": text(spawn.get("notes")),
        })
    return sorted_by(records, key=lambda record: record["id"])


def _bank_records(layout: dict) -> tuple:
    bank = layout["bank"]
    rotation = bank.get("rotation") or {}
    rules = bank.get("rules") or {}
    order = [text(v) for v in rotation.get("order", [])]
    activate_at = rotation.get("activate_at_s") or []
    activate_by_id = {anchor_id: activate_at[i] for i, anchor_id in enumerate(order)
                      if i < len(activate_at)}
    order_index = {anchor_id: i for i, anchor_id in enumerate(order)}

    anchors = []
    for anchor in bank.get("anchors", []):
        x, y, z = (number(v) for v in anchor["position"])
        anchor_id = text(anchor["id"])
        anchors.append({
            "id": anchor_id,
            "name_zh": text(anchor.get("name_zh")),
            "zone_id": text(bank.get("zone_id")),
            "position_x": x,
            "position_y": y,
            "position_z": z,
            "radius_m": number(anchor["radius_m"]),
            "rotation_order_index": order_index.get(anchor_id, -1),
            "activate_at_s": number(activate_by_id.get(anchor_id, 0)),
            "entrance_route_ids": string_list(anchor.get("entrances")),
            "approach_route_ids": string_list(anchor.get("approach_route_ids")),
            "notes": text(anchor.get("notes")),
        })
    anchors.sort(key=lambda record: record["id"])

    rotation_record = {
        "mode": text(rotation.get("mode")),
        "order": order,
        "period_s": number(rotation.get("period_s", 0)),
        "warning_s": number(rotation.get("warning_s", 0)),
        "activate_at_s": [number(v) for v in activate_at],
        "max_concurrent_bankers": number(rules.get("max_concurrent_bankers", 0)),
        "occupancy_lock": flag(rules.get("occupancy_lock")),
        "bank_resets_ball_to_start_size": flag(rules.get("bank_resets_ball_to_start_size")),
    }
    return anchors, rotation_record


def _gate_records(layout: dict) -> list:
    records = []
    for gate in layout["size_gates"]:
        x, y, z = (number(v) for v in gate["position"])
        records.append({
            "id": text(gate["id"]),
            "type": text(gate.get("type")),
            "definition_id": text(gate.get("definition_id")),
            "position_x": x,
            "position_y": y,
            "position_z": z,
            "yaw_deg": number(gate.get("yaw_deg", 0)),
            "clear_width_m": number(gate["clear_width_m"]),
            "blocks_ball_diameter_below_m": number(gate["blocks_ball_diameter_below_m"]),
            "absorbable_at_ball_diameter_m": number(gate["absorbable_at_ball_diameter_m"]),
            "opens_path": text(gate.get("opens_path")),
            "detour_m": number(gate.get("detour_m", 0)),
            "detour_route": text(gate.get("detour_route")),
            "notes": text(gate.get("notes")),
        })
    return sorted_by(records, key=lambda record: record["id"])


def _scatter_records(layout: dict) -> list:
    records = []
    for region in layout["prop_scatter"]:
        definitions = sorted(region.get("definitions") or [],
                             key=lambda definition: text(definition["id"]))
        counts_min = [int(definition["count_min"]) for definition in definitions]
        counts_max = [int(definition["count_max"]) for definition in definitions]
        records.append({
            "zone_id": text(region["zone_id"]),
            "tier_band": text(region.get("tier_band")),
            "min_spacing_m": number(region["min_spacing_m"]),
            "prop_definition_count": len(definitions),
            "prop_ids": [text(definition["id"]) for definition in definitions],
            "prop_counts_min": counts_min,
            "prop_counts_max": counts_max,
            "count_min_total": sum(counts_min),
            "count_max_total": sum(counts_max),
        })
    return sorted_by(records, key=lambda record: record["zone_id"])


def _route_records(layout: dict) -> list:
    records = []
    for route in layout["routes"]:
        waypoints = route.get("waypoints") or []
        records.append({
            "id": text(route["id"]),
            "kind": text(route.get("kind")),
            "is_closed": flag(route.get("closed")),
            "width_m": number(route["width_m"]),
            "min_clear_width_m": number(route["min_clear_width_m"]),
            "length_m": number(route.get("length_m", 0)),
            "waypoint_count": len(waypoints),
            "waypoints_x": [number(point[0]) for point in waypoints],
            "waypoints_z": [number(point[1]) for point in waypoints],
            "spawn_id": text(route.get("spawn_id")),
            "bank_anchor_id": text(route.get("bank_anchor_id")),
            "gate_id": text(route.get("gate_id")),
            "notes": text(route.get("notes")),
        })
    return sorted_by(records, key=lambda record: record["id"])


def _chokepoint_records(layout: dict) -> list:
    records = []
    for chokepoint in layout["chokepoints"]:
        x, y, z = (number(v) for v in chokepoint["position"])
        records.append({
            "id": text(chokepoint["id"]),
            "type": text(chokepoint.get("type")),
            "position_x": x,
            "position_y": y,
            "position_z": z,
            "clear_width_m": number(chokepoint["clear_width_m"]),
            "gate_id": text(chokepoint.get("gate_id")),
            "narrow_small_ball_lane": flag(chokepoint.get("narrow_small_ball_lane")),
            "notes": text(chokepoint.get("notes")),
        })
    return sorted_by(records, key=lambda record: record["id"])


def _prop_records(catalog: dict) -> list:
    records = []
    for prop_id, meta in catalog.items():
        row = meta["row"]
        records.append({
            "id": prop_id,
            "name_zh": text(row.get("name_zh")),
            "category": text(row.get("category")),
            "size_tier": text(row.get("size_tier")),
            "role": text(row.get("role")),
            "priority": text(row.get("priority")),
            "real_longest_dim_m": number(float(row.get("real_longest_dim_m") or 0)),
            "game_longest_dim_m": number(float(row["game_longest_dim_m"])),
            "absorb_value": int(row["absorb_value_placeholder"]),
            "count": int(row["count_in_map"]),
            "zone_ids": sorted({part.strip() for part in text(row.get("zone_ids")).split(";")
                                if part.strip()}),
        })
    return sorted_by(records, key=lambda record: record["id"])


def emit(layout: dict, catalog: dict) -> dict:
    """把已校验的输入拍平成 JsonUtility 友好的载荷（键序即此处字面顺序）。"""
    anchors, rotation = _bank_records(layout)
    bounds = layout["bounds"]
    payload = {
        "schema_version": SCHEMA_VERSION,
        "map_id": text(layout["map_id"]),
        "status": text(layout.get("status")),
        "generator": GENERATOR,
        "bounds": {
            "min_x": number(bounds["min_x"]),
            "max_x": number(bounds["max_x"]),
            "min_z": number(bounds["min_z"]),
            "max_z": number(bounds["max_z"]),
            "ground_y_m": number(bounds["ground_y_m"]),
        },
        "scale": _scale_record(layout["scale"]),
        "zones": _zone_records(layout),
        "structures": _structure_records(layout),
        "spawns": _spawn_records(layout),
        "bank_anchors": anchors,
        "bank_rotation": rotation,
        "size_gates": _gate_records(layout),
        "scatter_regions": _scatter_records(layout),
        "routes": _route_records(layout),
        "chokepoints": _chokepoint_records(layout),
        "props": _prop_records(catalog),
    }
    assert tuple(payload) == TOP_LEVEL_KEYS, "顶层键序必须与 TOP_LEVEL_KEYS 一致"
    return payload


def render(payload: dict) -> str:
    """确定性文本：2 空格缩进、UTF-8 中文不转义、末尾一个换行。"""
    return json.dumps(payload, ensure_ascii=False, indent=2) + "\n"


def build(layout_path: Path = DEFAULT_LAYOUT, catalog_path: Path = DEFAULT_CATALOG) -> str:
    """读入 → 校验 → 生成，返回完整输出文本。"""
    layout = load_layout(Path(layout_path))
    catalog = load_catalog(Path(catalog_path))
    validate(layout, catalog)
    try:
        payload = emit(layout, catalog)
    except (KeyError, TypeError, ValueError) as exc:
        # 新地图少写字段时给一条能定位的错误，而不是抛裸 KeyError 栈。
        raise BuildError([f"生成失败：缺少或类型不对的字段 {exc!r}（layout.json / prop_catalog.csv）"]) from exc
    return render(payload)


# --------------------------------------------------------------------------- #
# --feasibility 的放置可行性表
# --------------------------------------------------------------------------- #

def feasibility_rows(layout: dict, catalog: dict) -> list:
    """每个散布区的放置容量（与 validate() 同一套模型）。"""
    props = {}
    for prop_id, meta in catalog.items():
        row = meta["row"]
        try:
            props[prop_id] = {"dim": float(row["game_longest_dim_m"])}
        except (TypeError, ValueError):
            continue
    zones = {text(zone.get("id")): zone for zone in layout.get("zones") or []}
    rows = []
    for region in layout.get("prop_scatter") or []:
        zone_id = text(region.get("zone_id"))
        zone = zones.get(zone_id)
        if zone is None:
            continue
        try:
            rows.append(scatter_feasibility(zone, region, props))
        except (KeyError, TypeError, ValueError, ZeroDivisionError):
            continue
    return sorted(rows, key=lambda row: row["zone_id"])


def format_feasibility(rows) -> str:
    header = (f"{'region':15s} {'area':>6s} {'spacing':>7s} {'count':>5s} "
              f"{'cap_rsa':>7s} {'cap_hex':>7s} {'usage':>6s} {'foot':>5s}  status")
    lines = [header, "-" * len(header)]
    for row in rows:
        usage = row["usage"]
        if row["count"] > row["capacity_optimal"]:
            status = f"FAIL(几何上限 {row['capacity_optimal']})"
        elif row["count"] > row["capacity_consumer"]:
            status = f"FAIL(随机摆放上限 {row['capacity_consumer']})"
        elif usage > CAPACITY_USAGE_WARN:
            status = "WARN(余量不足)"
        else:
            status = "ok"
        lines.append(
            f"{row['zone_id']:15s} {row['area_m2']:6.1f} {row['spacing_m']:7.2f} "
            f"{row['count']:5d} {row['capacity_consumer']:7d} {row['capacity_optimal']:7d} "
            f"{usage:6.0%} {row['footprint_floor_m']:5.2f}  {status}")
    total = sum(row["count"] for row in rows)
    lines.append(f"{'total':15s} {'':6s} {'':7s} {total:5d}"
                 f"    # 摆放上限 = 随机顺序吸附(256 次拒绝采样) 的 0.547 覆盖率；六方最密上限仅作对照")
    return "\n".join(lines)


def print_feasibility(layout_path: Path = DEFAULT_LAYOUT,
                      catalog_path: Path = DEFAULT_CATALOG) -> None:
    """--feasibility：即使数据有错也要能打印表格（它是给设计迭代用的）。"""
    try:
        layout = load_layout(Path(layout_path))
        catalog = load_catalog(Path(catalog_path))
    except BuildError as exc:
        print(f"FAIL 读不到输入（{len(exc.errors)} 条）:", file=sys.stderr)
        for error in exc.errors:
            print(f"  x {error}", file=sys.stderr)
        return
    print(format_feasibility(feasibility_rows(layout, catalog)))



# --------------------------------------------------------------------------- #
# --check 的差异摘要
# --------------------------------------------------------------------------- #

def _collection_counts(text_payload: str) -> dict:
    try:
        payload = json.loads(text_payload)
    except json.JSONDecodeError:
        return {}
    return {key: len(value) for key, value in payload.items() if isinstance(value, list)}


def diff_summary(committed: str, generated: str, out_path: Path) -> str:
    """短摘要：变了哪些集合、第一处不同、行数变化。"""
    lines = [f"stale: {out_path.relative_to(REPO_ROOT) if out_path.is_relative_to(REPO_ROOT) else out_path}",
             "  作者数据变了，生成的载荷没有同步；运行 Tools/mapbuild/build_greybox.py 重新生成。"]
    old_counts = _collection_counts(committed)
    new_counts = _collection_counts(generated)
    count_diffs = [f"{key} {old_counts.get(key, '?')} → {new_counts.get(key, '?')}"
                   for key in sorted(set(old_counts) | set(new_counts))
                   if old_counts.get(key) != new_counts.get(key)]
    if count_diffs:
        lines.append("  记录数变化: " + ", ".join(count_diffs))

    old_lines = committed.splitlines()
    new_lines = generated.splitlines()
    first_index = None
    for index in range(min(len(old_lines), len(new_lines))):
        if old_lines[index] != new_lines[index]:
            first_index = index
            break
    if first_index is None and len(old_lines) != len(new_lines):
        first_index = min(len(old_lines), len(new_lines))
    if first_index is not None:
        old_line = old_lines[first_index] if first_index < len(old_lines) else "<文件结束>"
        new_line = new_lines[first_index] if first_index < len(new_lines) else "<文件结束>"
        lines.append(f"  第一处不同: 第 {first_index + 1} 行")
        lines.append(f"    - {old_line[:160]}")
        lines.append(f"    + {new_line[:160]}")
    lines.append(f"  行数: {len(old_lines)} → {len(new_lines)}")
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="把 PicnicLawn layout.json + prop_catalog.csv 转成 Unity JsonUtility 载荷。")
    parser.add_argument("--layout", type=Path, default=DEFAULT_LAYOUT,
                        help="作者数据 layout.json（默认仓库内 PicnicLawn）")
    parser.add_argument("--catalog", type=Path, default=DEFAULT_CATALOG,
                        help="道具目录 prop_catalog.csv")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT,
                        help="生成的载荷路径")
    parser.add_argument("--check", action="store_true",
                        help="只检查已提交文件是否过期（过期退出码 1，不写文件）")
    parser.add_argument("--summary", action="store_true",
                        help="成功后打印各集合记录数")
    parser.add_argument("--feasibility", action="store_true",
                        help="打印每个散布区的 2-D 放置容量表（校验前，便于设计迭代）")
    args = parser.parse_args(argv)

    if args.feasibility:
        print_feasibility(args.layout, args.catalog)

    try:
        generated = build(args.layout, args.catalog)
    except BuildError as exc:
        print(f"FAIL 输入校验失败（{len(exc.errors)} 条）:", file=sys.stderr)
        for error in exc.errors:
            print(f"  x {error}", file=sys.stderr)
        return 2

    if args.check:
        out_path = args.out
        if not out_path.exists():
            print(f"stale: {out_path} 不存在；运行 Tools/mapbuild/build_greybox.py 生成。",
                  file=sys.stderr)
            return 1
        # 按字节解码：不让通用换行把 CRLF 洗成 LF，否则 --check 会漏报。
        committed = out_path.read_bytes().decode("utf-8")
        if committed == generated:
            print(f"OK: {out_path} 与作者数据一致。")
            return 0
        print(diff_summary(committed, generated, out_path), file=sys.stderr)
        return 1

    args.out.parent.mkdir(parents=True, exist_ok=True)
    # 写字节：Python 3.9 的 Path.write_text 没有 newline 参数，字节写入也能保证 LF。
    args.out.write_bytes(generated.encode("utf-8"))
    print(f"wrote {args.out} ({len(generated.encode('utf-8'))} bytes)")
    if args.summary:
        payload = json.loads(generated)
        for key in TOP_LEVEL_KEYS:
            value = payload[key]
            if isinstance(value, list):
                print(f"  {key}: {len(value)}")
        print(f"  scatter prop rows: {sum(r['prop_definition_count'] for r in payload['scatter_regions'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
