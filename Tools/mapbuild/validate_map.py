#!/usr/bin/env python3
"""通用地图作者数据校验器：任何按本 schema 写的地图都能跑（仅标准库）。

用法：
    python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/ToyRoom
    python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/PicnicLawn
    python3 Tools/mapbuild/validate_map.py <任意目录>/layout.json --json
    python3 Tools/mapbuild/validate_map.py <dir> --catalog <dir>/prop_catalog.csv

参数可以给**地图目录**（读其中的 `layout.json` + `prop_catalog.csv`），也可以给
`layout.json` 的路径（目录与目录里的 `prop_catalog.csv` 由它推出）。

退出码：`0` 全部通过（允许没有 ERROR 的 warning）/ `1` 有 ERROR / `2` 读不到或解析不了输入。

## 这个工具和另外两个校验器的分工

| 工具 | 管什么 | 谁的责任 |
|---|---|---|
| `Tools/mapbuild/build_greybox.py` | 生成前的**消费方契约**：字段形状、目录计数、2-D 放置可行性 | 生成 Unity 载荷的唯一门槛 |
| `Tools/mapbuild/validate_map.py`（本文） | **作者层**一致性：几何、引用、路线余量、档位/包线、容量与"摆得下"的可读报告 | 改数据的人先跑这个 |
| `Docs/Design/Maps/PicnicLawn/validate_layout.py` | 野餐草坪历史自检（PicnicLawn 专用） | **应退役**：它检查的每一项都被本文覆盖，见 README §17 |

本文**不重复实现** 2-D 放置容量模型：`min_spacing_m` 是否摆得下由
`build_greybox.scatter_feasibility()` 判定（RSA 0.547 覆盖率、脚印下限、消费方上限），
本文只调用它并把数字整理成人能读的表。也不重复实现目录计数一致性以外的生成逻辑。

## 检查项

1. **结构与 ID**：顶层键、`schema_version`、区域/出生点/交分台/尺寸门/路线/咽喉 id 唯一。
2. **目录**：列名、枚举（size_tier / role / priority）、数值合法、每个 id 都被布局真正使用、
   `count_in_map` = 散布 + 可吸门实例（`role=absorbable`）或结构实例（`role=fixed`）、
   `zone_ids` 与实际使用位置完全一致。
3. **区域**：只认 `rect`；面积与矩形一致；在边界内；**互不重叠**（除非在
   `zone_overlap_exemptions` 里显式声明并写理由）；`tier_band` 是合法档位且 min ≤ max。
4. **引用**：所有「引用了某个 id」的字段都必须在同文件里定义（含 `detour_route`、
   `approach_route_ids`、`entrances`、`gate_id`、`powerups.zone_id`、`respawn.candidate_spawn_ids`、
   `player_count_plans` 的 id、`bank.rotation.order` 的排列性）。
5. **出生点几何**：在自己声明的区域内、`y` 与区域地面一致、`yaw_deg` 合法、**至少 2 条出路**；
   并给出「出生 → 交分」的设计路程（出路 + 接得上的台面接近路线）与
   `scale.spawn_to_bank_target_m` 的偏差。
6. **交分台**：在 `bank.zone_id` 内、圆盘互不重叠、`rotation.order` 是锚点集合的排列、
   `activate_at_s` 与 `period_s` 一致、`warning_s < period_s`、`bank_anchor_count` 与 `period_s`
   与 `scale` 一致。
7. **尺寸门**：类型只有 absorb/climb；`absorb` 的物件必须可卷、`climb` 的必须是固定地形；
   `blocks < absorbable_at`；两个阈值都落在 [起始球径, 最大球径]；`opens_path` 存在；
   `climb` 门的 `opens_path` 必须落在**抬高**的区域（`ground_y_m > 0`）且门槛 y 与该区地面一致；
   `detour_route` 存在。
8. **散布与容量**：复用 `build_greybox` 的容量模型——件数 ≤ 消费方（RSA）上限、用量 ≤ 85%、
   `min_spacing_m` ≥ 区内最大两件的脚印间距、最大件能整块放进区域；`tier_band` 必须落在
   所属区域的档位范围内。
9. **路线**：≥2 个路点、路点在边界内、净宽 ≥ 最大球径、余量 ≥ `min_route_margin_over_max_ball_m`
   （不够只警告，并打印实际余量）。
10. **咽喉**：`clear_width_m` < 最大球径时必须标 `narrow_small_ball_lane`，且不得窄于起始球径；
    还必须在**任何散布区之外**——否则小球会在里面吃到东西长大、把"能进的缝"变成"出不去"。
11. **档位与球包线**：`ball_bands` 从起始球径连续覆盖到最大球径；拾取档 s/m/l 有序不重叠、
    上界不超过最大球径；每个可卷物的**拾取门槛**（`clamp(game_longest_dim_m, 档窗口)`）
    都 ≤ 最大球径（否则永远吃不到）；每个档位至少被一条区域声明覆盖（提示级）。
12. **道具 / 重生 / 人数 / 预算**：道具类型与区域、与出生点距离；重生候选与延迟；
    `player_count_plans` 的人数和 id；`performance_budget` 的数字与目录一致。

所有数值都是**作者数据**，本工具不验证 Unity、可玩性、公平性或性能。
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import build_greybox as greybox  # noqa: E402  （容量模型/矩形的唯一实现）

GATE_TYPES = ("absorb", "climb")
TIERS = ("s", "m", "l")
ROLES = ("absorbable", "fixed")
PRIORITIES = ("MVP", "later")
POWERUP_TYPES = ("magnet", "rocket")

REQUIRED_LAYOUT_KEYS = (
    "schema_version", "map_id", "status", "coordinate_system", "scale", "bounds",
    "zones", "structures", "bank", "spawns", "size_gates", "prop_scatter",
    "routes", "chokepoints", "powerups", "respawn", "player_count_plans",
    "performance_budget",
)
REQUIRED_CATALOG_COLUMNS = (
    "id", "name_zh", "category", "size_tier", "real_longest_dim_m",
    "game_longest_dim_m", "absorb_value_placeholder", "count_in_map", "zone_ids",
    "sourcing_hint", "priority", "coverage_ref", "role", "notes",
)
SUPPORTED_SCHEMA_VERSIONS = (1,)
EPS = 1e-6


# --------------------------------------------------------------------------- #
# 报告
# --------------------------------------------------------------------------- #
class Report:
    """errors 决定退出码；warnings 只提示；notes 是给人看的事实。"""

    def __init__(self):
        self.errors: list[str] = []
        self.warnings: list[str] = []
        self.notes: list[str] = []

    def err(self, message: str) -> None:
        self.errors.append(message)

    def warn(self, message: str) -> None:
        self.warnings.append(message)

    def note(self, message: str) -> None:
        self.notes.append(message)

    @property
    def ok(self) -> bool:
        return not self.errors


# --------------------------------------------------------------------------- #
# 输入
# --------------------------------------------------------------------------- #
def resolve_inputs(target: Path, catalog: Path | None) -> tuple[Path, Path]:
    """目录 → (dir/layout.json, dir/prop_catalog.csv)；layout 路径 → 同目录的 catalog。"""
    target = target.expanduser()
    if target.is_dir():
        layout = target / "layout.json"
        cat = catalog or (target / "prop_catalog.csv")
    else:
        layout = target
        cat = catalog or (target.parent / "prop_catalog.csv")
    return layout, cat


def load_inputs(layout_path: Path, catalog_path: Path):
    """返回 (layout, catalog_rows)；失败时抛 InputError（退出码 2）。"""
    if not layout_path.is_file():
        raise InputError(f"找不到 layout.json：{layout_path}")
    if not catalog_path.is_file():
        raise InputError(f"找不到 prop_catalog.csv：{catalog_path}")
    try:
        layout = json.loads(layout_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise InputError(f"{layout_path}: JSON 解析失败于第 {exc.lineno} 行：{exc.msg}") from exc
    if not isinstance(layout, dict):
        raise InputError(f"{layout_path}: 顶层必须是对象")
    with catalog_path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        columns = reader.fieldnames or []
        rows = list(reader)
    return layout, {"columns": columns, "rows": rows}


class InputError(Exception):
    """输入读不到/解析不了：不是数据错误，是调用方式问题（退出码 2）。"""


# --------------------------------------------------------------------------- #
# 小工具
# --------------------------------------------------------------------------- #
def rect_of(zone) -> dict | None:
    shape = zone.get("shape") or {}
    if shape.get("type") != "rect":
        return None
    try:
        return {k: float(shape[k]) for k in ("min_x", "max_x", "min_z", "max_z")}
    except (KeyError, TypeError, ValueError):
        return None


def rect_area(shape: dict) -> float:
    return (shape["max_x"] - shape["min_x"]) * (shape["max_z"] - shape["min_z"])


def overlap_area(a: dict, b: dict) -> float:
    w = min(a["max_x"], b["max_x"]) - max(a["min_x"], b["min_x"])
    h = min(a["max_z"], b["max_z"]) - max(a["min_z"], b["min_z"])
    return w * h if w > 0 and h > 0 else 0.0


def contains(shape: dict | None, x: float, z: float, eps: float = EPS) -> bool:
    if shape is None:
        return False
    return (shape["min_x"] - eps <= x <= shape["max_x"] + eps
            and shape["min_z"] - eps <= z <= shape["max_z"] + eps)


def zone_at(zones: dict, x: float, z: float, exclude: set[str] | None = None) -> list[str]:
    """包含该点的区域 id 列表（边界算包含）。"""
    hits = []
    for zid, zone in zones.items():
        if exclude and zid in exclude:
            continue
        if contains(zone["_rect"], x, z):
            hits.append(zid)
    return hits


def polyline_length(points) -> float:
    return sum(math.dist(a, b) for a, b in zip(points, points[1:]))


def as_float(value, default=None):
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def as_int(value, default=None):
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


# --------------------------------------------------------------------------- #
# 主校验（纯函数，便于将来加单测）
# --------------------------------------------------------------------------- #
def validate(layout: dict, catalog: dict) -> Report:
    rep = Report()
    scale = layout.get("scale") or {}
    bands_meta = {b.get("id"): b for b in (scale.get("ball_bands") or [])}
    pickup = scale.get("item_tier_pickup_diameter_m") or {}

    # ---------------- 1. 顶层结构 ---------------------------------------- #
    for key in REQUIRED_LAYOUT_KEYS:
        if key not in layout:
            rep.err(f"layout.json：缺顶层键 {key}")
    if layout.get("schema_version") not in SUPPORTED_SCHEMA_VERSIONS:
        rep.err(f"layout.json：schema_version 必须是 {SUPPORTED_SCHEMA_VERSIONS}，"
                f"实际 {layout.get('schema_version')!r}")
    if not rep.ok:
        return rep  # 结构缺失时后面的检查没有意义

    max_ball = as_float(scale.get("ball_max_collision_diameter_m"))
    min_ball = as_float(scale.get("ball_start_collision_diameter_m"))
    margin_target = as_float(scale.get("min_route_margin_over_max_ball_m"), 0.0)
    if max_ball is None or min_ball is None:
        rep.err("scale：ball_start/ball_max_collision_diameter_m 缺失或不是数字")
        return rep
    if max_ball <= min_ball:
        rep.err(f"scale：最大球径 {max_ball} 必须大于起始球径 {min_ball}")

    bounds = layout["bounds"]
    bx0, bx1 = as_float(bounds.get("min_x")), as_float(bounds.get("max_x"))
    bz0, bz1 = as_float(bounds.get("min_z")), as_float(bounds.get("max_z"))
    if None in (bx0, bx1, bz0, bz1) or bx0 >= bx1 or bz0 >= bz1:
        rep.err("bounds：min/max 缺失、不是数字或退化")

    def in_bounds(x: float, z: float) -> bool:
        return bx0 - EPS <= x <= bx1 + EPS and bz0 - EPS <= z <= bz1 + EPS

    # ---------------- 11. 档位与球包线 ----------------------------------- #
    bands = scale.get("ball_bands") or []
    if not bands:
        rep.err("scale.ball_bands：至少要有一个尺寸档")
    else:
        if abs(as_float(bands[0].get("min_m"), 0) - min_ball) > 1e-6:
            rep.warn(f"scale.ball_bands[0].min_m={bands[0].get('min_m')} 与起始球径 {min_ball} 不一致")
        for i, band in enumerate(bands):
            lo, hi = as_float(band.get("min_m")), as_float(band.get("max_m"))
            if lo is None or hi is None or not lo < hi:
                rep.err(f"ball_bands[{band.get('id')}]：min_m/max_m 非法（{lo}, {hi}）")
                continue
            if i and abs(as_float(bands[i - 1].get("max_m"), 0) - lo) > 1e-6:
                rep.warn(f"尺寸档 {bands[i - 1].get('id')} → {band.get('id')} 不连续："
                         f"{bands[i - 1].get('max_m')} vs {lo}")
        if abs(as_float(bands[-1].get("max_m"), 0) - max_ball) > 1e-6:
            rep.err(f"最后一个尺寸档 max_m={bands[-1].get('max_m')} 必须等于最大球径 {max_ball}")

    if sorted(pickup) != ["l", "m", "s"]:
        rep.err(f"scale.item_tier_pickup_diameter_m：必须是 s/m/l 三档，实际 {sorted(pickup)}")
    else:
        windows = {tier: (as_float(pickup[tier][0]), as_float(pickup[tier][1])) for tier in TIERS
                   if isinstance(pickup[tier], list) and len(pickup[tier]) == 2}
        if len(windows) != 3 or any(v[0] is None or v[1] is None for v in windows.values()):
            rep.err("scale.item_tier_pickup_diameter_m：每档必须是 [下界, 上界] 两个数字")
        else:
            if not windows["s"][1] < windows["m"][0]:
                rep.err(f"拾取档 s 的上界 {windows['s'][1]} 必须小于 m 的下界 {windows['m'][0]}")
            if not windows["m"][1] < windows["l"][0]:
                rep.err(f"拾取档 m 的上界 {windows['m'][1]} 必须小于 l 的下界 {windows['l'][0]}")
            if windows["s"][0] < 0:
                rep.err("拾取档 s 的下界不能为负")
            if windows["l"][1] > max_ball + EPS:
                rep.err(f"拾取档 l 的上界 {windows['l'][1]} 超过最大球径 {max_ball}："
                        f"该档物件永远吃不到")
            if windows["l"][1] < max_ball - 0.3:
                rep.warn(f"拾取档 l 的上界 {windows['l'][1]} 比最大球径 {max_ball} 低很多，"
                         f"大档缺最高一截")
            rep.note(f"球包线 {min_ball}–{max_ball} m，{len(bands)} 档；"
                     f"拾取门槛 s {windows['s'][0]}–{windows['s'][1]} / "
                     f"m {windows['m'][0]}–{windows['m'][1]} / l {windows['l'][0]}–{windows['l'][1]} m")

    # ---------------- 3. 区域 -------------------------------------------- #
    zones: dict[str, dict] = {}
    for zone in layout["zones"]:
        zid = str(zone.get("id") or "").strip()
        if not zid:
            rep.err("zones：有空 id 的区域记录")
            continue
        if zid in zones:
            rep.err(f"{zid}：区域 id 重复")
            continue
        rect = rect_of(zone)
        if rect is None:
            rep.err(f"{zid}：shape.type 必须是 rect 且含 min_x/max_x/min_z/max_z")
            continue
        if rect["min_x"] >= rect["max_x"] or rect["min_z"] >= rect["max_z"]:
            rep.err(f"{zid}：区域矩形退化")
        declared = as_float(zone.get("area_m2"))
        if declared is None:
            rep.err(f"{zid}：area_m2 缺失或不是数字")
        elif abs(rect_area(rect) - declared) > 0.01:
            rep.err(f"{zid}：area_m2={declared} 与矩形计算值 {rect_area(rect):.3f} 不符")
        for x, z in ((rect["min_x"], rect["min_z"]), (rect["max_x"], rect["max_z"])):
            if not in_bounds(x, z):
                rep.err(f"{zid}：矩形超出 bounds")
        band_pair = zone.get("tier_band") or []
        if len(band_pair) != 2:
            rep.err(f"{zid}：tier_band 必须是 [最小档, 最大档]（单档区写两次同一档），"
                    f"实际 {band_pair!r}")
        else:
            for band in band_pair:
                if band not in bands_meta:
                    rep.err(f"{zid}：tier_band 引用未知尺寸档 {band!r}")
            if all(b in bands_meta for b in band_pair):
                order = [b.get("id") for b in bands]
                if order.index(band_pair[0]) > order.index(band_pair[1]):
                    rep.err(f"{zid}：tier_band 顺序反了 {band_pair}")
        if not str(zone.get("name_zh") or "").strip():
            rep.err(f"{zid}：name_zh 不能为空")
        if not str(zone.get("surface") or "").strip():
            rep.err(f"{zid}：surface 不能为空（区域靠地面材质判读）")
        zone["_rect"] = rect
        zones[zid] = zone
    if not zones:
        rep.err("zones：没有任何合法区域")
        return rep

    exemptions = layout.get("zone_overlap_exemptions") or []
    exempt_pairs = set()
    for item in exemptions:
        a, b = item.get("a"), item.get("b")
        if a not in zones or b not in zones:
            rep.err(f"zone_overlap_exemptions：{a!r} × {b!r} 里有未知区域")
            continue
        if not str(item.get("reason") or "").strip():
            rep.err(f"zone_overlap_exemptions：{a} × {b} 必须写 reason")
        exempt_pairs.add(frozenset((a, b)))

    ids = list(zones)
    zone_area = 0.0
    for i, a in enumerate(ids):
        zone_area += rect_area(zones[a]["_rect"]) if zones[a]["_rect"] else 0.0
        for b in ids[i + 1:]:
            ov = overlap_area(zones[a]["_rect"], zones[b]["_rect"])
            if ov > 0.01 and frozenset((a, b)) not in exempt_pairs:
                rep.err(f"区域重叠（未声明）：{a} × {b} = {ov:.2f} m²")
    bounds_area = (bx1 - bx0) * (bz1 - bz0)
    if zone_area > bounds_area + 1e-6:
        rep.err(f"区域面积和 {zone_area:.1f} m² 超过地图面积 {bounds_area:.1f} m²")
    rep.note(f"区域 {len(zones)} 个，合计 {zone_area:.1f} m²"
             f"（地图 {bounds_area:.0f} m²，占 {100 * zone_area / bounds_area:.0f}%）")

    # ---------------- 2. 目录 -------------------------------------------- #
    catalog_rows = catalog.get("rows") or []
    catalog_columns = catalog.get("columns") or []
    for col in REQUIRED_CATALOG_COLUMNS:
        if col not in catalog_columns:
            rep.err(f"prop_catalog.csv：缺列 {col}")
    props: dict[str, dict] = {}
    for line_no, row in enumerate(catalog_rows, start=2):
        pid = (row.get("id") or "").strip()
        if not pid:
            rep.err(f"prop_catalog.csv 行 {line_no}：空 id")
            continue
        if pid in props:
            rep.err(f"prop_catalog.csv 行 {line_no}：id 重复 {pid}")
            continue
        tier = (row.get("size_tier") or "").strip()
        role = (row.get("role") or "").strip()
        priority = (row.get("priority") or "").strip()
        if tier not in TIERS:
            rep.err(f"{pid}：size_tier 必须是 {list(TIERS)}，实际 {tier!r}")
        if role not in ROLES:
            rep.err(f"{pid}：role 必须是 {list(ROLES)}，实际 {role!r}")
        if priority and priority not in PRIORITIES:
            rep.err(f"{pid}：priority 必须是 {list(PRIORITIES)}，实际 {priority!r}")
        count = as_int(row.get("count_in_map"))
        value = as_int(row.get("absorb_value_placeholder"))
        dim = as_float(row.get("game_longest_dim_m"))
        if count is None or value is None or dim is None:
            rep.err(f"{pid}：count_in_map/absorb_value_placeholder/game_longest_dim_m 必须是数字")
            continue
        if count < 0 or value < 0 or dim <= 0:
            rep.err(f"{pid}：count/value 不能为负，game_longest_dim_m 必须为正")
        zone_ids = [p.strip() for p in str(row.get("zone_ids") or "").split(";") if p.strip()]
        for zid in zone_ids:
            if zid.lower() in ("all", "none"):
                rep.err(f"{pid}：zone_ids 里的 {zid!r} 不是区域 id（写区域 id 或留空）")
            elif zid not in zones:
                rep.err(f"{pid}：zone_ids 引用未知区域 {zid}")
        # 拾取门槛 = clamp(游戏尺寸, 档窗口)；门槛超过最大球径的物件永远吃不到
        if tier in TIERS and len([t for t in TIERS if isinstance(pickup.get(t), list)]) == 3:
            lo, hi = as_float(pickup[tier][0]), as_float(pickup[tier][1])
            threshold = min(max(dim, lo), hi)
            if threshold > max_ball + EPS:
                rep.err(f"{pid}：拾取门槛 {threshold:.2f} m 超过最大球径 {max_ball} m，永远吃不到")
            if role == "absorbable" and threshold > max_ball - 0.02:
                rep.warn(f"{pid}：拾取门槛 {threshold:.2f} m 贴着最大球径 {max_ball} m，"
                         f"只有接近满径的球才吃得到")
        props[pid] = {"row": row, "count": count, "value": value, "tier": tier,
                      "role": role, "dim": dim, "zone_ids": zone_ids, "line": line_no}

    # ---------------- 4. 结构 -------------------------------------------- #
    struct_count = Counter()
    struct_zones: dict[str, set[str]] = defaultdict(set)
    for index, structure in enumerate(layout["structures"]):
        defn = str(structure.get("definition_id") or "").strip()
        label = f"structures[{index}] {defn or '<空 definition_id>'}"
        if defn not in props:
            rep.err(f"{label}：未知目录 id {defn!r}")
            continue
        struct_count[defn] += 1
        zid = structure.get("zone_id")
        if zid is not None and str(zid) not in zones:
            rep.err(f"{label}：未知区域 {zid!r}")
        if zid is not None:
            struct_zones[defn].add(str(zid))
        pos = structure.get("position")
        size = structure.get("size_m")
        if not (isinstance(pos, list) and len(pos) == 3):
            rep.err(f"{label}：position 必须是 [x, y, z]")
            continue
        if not (isinstance(size, list) and len(size) == 3):
            rep.err(f"{label}：size_m 必须是 [x, y, z]")
        x, y, z = (as_float(v) for v in pos)
        if None in (x, y, z):
            rep.err(f"{label}：position 必须是数字")
            continue
        if not in_bounds(x, z):
            # 无归属区域的结构 = 声明为全图/边界外装饰（地面、墙、天花板、远景）
            if zid is not None:
                rep.err(f"{label}：有归属区域却在边界外 ({x}, {z})")
            else:
                rep.note(f"{label}：全局装饰在边界外 ({x:g}, {z:g})，zone_id 为空，允许")
        elif zid is not None and str(zid) in zones and not contains(zones[str(zid)]["_rect"], x, z):
            rep.warn(f"{label}：position ({x:g}, {z:g}) 不在声明的区域 {zid} 内")
        if as_float(structure.get("yaw_deg")) is None:
            rep.err(f"{label}：yaw_deg 缺失或不是数字")

    # ---------------- 5. 出生点 ------------------------------------------ #
    spawns: dict[str, dict] = {}
    for spawn in layout["spawns"]:
        sid = str(spawn.get("id") or "").strip()
        if not sid:
            rep.err("spawns：有空 id 的出生点")
            continue
        if sid in spawns:
            rep.err(f"{sid}：出生点 id 重复")
            continue
        spawns[sid] = spawn
        zid = str(spawn.get("zone_id") or "")
        if zid not in zones:
            rep.err(f"{sid}：未知区域 {zid!r}")
            continue
        pos = spawn.get("position")
        if not (isinstance(pos, list) and len(pos) == 3):
            rep.err(f"{sid}：position 必须是 [x, y, z]")
            continue
        x, y, z = (as_float(v) for v in pos)
        if None in (x, y, z):
            rep.err(f"{sid}：position 必须是数字")
            continue
        if not in_bounds(x, z):
            rep.err(f"{sid}：出生点在地图外")
        if not contains(zones[zid]["_rect"], x, z):
            rep.err(f"{sid}：出生点不在自己声明的区域 {zid} 内")
        ground = as_float(zones[zid].get("ground_y_m"), 0.0)
        if abs(y - ground) > 0.06:
            rep.err(f"{sid}：y={y} 与区域地面 {ground} 相差超过 0.06 m")
        yaw = as_float(spawn.get("yaw_deg"))
        if yaw is None or not 0 <= yaw <= 360:
            rep.err(f"{sid}：yaw_deg 缺失或越界")

    # ---------------- 6. 交分台 ------------------------------------------ #
    bank = layout["bank"]
    bank_zone = str(bank.get("zone_id") or "")
    if bank_zone not in zones:
        rep.err(f"bank.zone_id：未知区域 {bank_zone!r}")
    anchors: dict[str, dict] = {}
    for anchor in bank.get("anchors") or []:
        aid = str(anchor.get("id") or "").strip()
        if not aid:
            rep.err("bank.anchors：有空 id 的锚点")
            continue
        if aid in anchors:
            rep.err(f"{aid}：交分锚点 id 重复")
            continue
        anchors[aid] = anchor
        pos = anchor.get("position")
        if not (isinstance(pos, list) and len(pos) == 3):
            rep.err(f"{aid}：position 必须是 [x, y, z]")
            continue
        x, y, z = (as_float(v) for v in pos)
        if None in (x, y, z):
            rep.err(f"{aid}：position 必须是数字")
            continue
        radius = as_float(anchor.get("radius_m"))
        if radius is None or radius <= 0:
            rep.err(f"{aid}：radius_m 必须为正")
        if not in_bounds(x, z):
            rep.err(f"{aid}：交分台在地图外")
        if bank_zone in zones and not contains(zones[bank_zone]["_rect"], x, z):
            rep.err(f"{aid}：交分台不在 bank.zone_id（{bank_zone}）内")
        r = radius or 0.0
        if bank_zone in zones and r > 0:
            rect = zones[bank_zone]["_rect"]
            margin = min(x - rect["min_x"], rect["max_x"] - x,
                         z - rect["min_z"], rect["max_z"] - z)
            if margin < r - EPS:
                rep.err(f"{aid}：圆盘半径 {r} m 超出 {bank_zone} 的边（余量 {margin:.2f} m）")
    alist = list(anchors)
    for i, a in enumerate(alist):
        for b in alist[i + 1:]:
            pa, pb = anchors[a]["position"], anchors[b]["position"]
            d = math.dist((pa[0], pa[2]), (pb[0], pb[2]))
            sum_r = (as_float(anchors[a].get("radius_m"), 0) or 0) + \
                    (as_float(anchors[b].get("radius_m"), 0) or 0)
            if d <= sum_r + EPS:
                rep.err(f"交分台重叠 {a} × {b}：圆心距 {d:.2f} m ≤ 半径和 {sum_r:.2f} m")
            elif d < sum_r + 1.0:
                rep.warn(f"交分台 {a} × {b}：圆心距 {d:.2f} m，只比半径和多 {d - sum_r:.2f} m")

    rotation = bank.get("rotation") or {}
    order = [str(v) for v in rotation.get("order") or []]
    if sorted(order) != sorted(alist) or len(set(order)) != len(order):
        rep.err(f"bank.rotation.order 不是锚点集合的排列：{order} vs {sorted(alist)}")
    period = as_int(rotation.get("period_s"))
    warning = as_int(rotation.get("warning_s"))
    activate = rotation.get("activate_at_s") or []
    if period is None or period <= 0:
        rep.err(f"bank.rotation.period_s 必须是正整数，实际 {rotation.get('period_s')!r}")
    if warning is None or warning < 0:
        rep.err(f"bank.rotation.warning_s 必须是 ≥0 的整数，实际 {rotation.get('warning_s')!r}")
    elif period and warning >= period:
        rep.err(f"bank.rotation.warning_s={warning} 必须小于 period_s={period}")
    if period:
        expected = [i * period for i in range(len(order))]
        if [as_int(v) for v in activate] != expected:
            rep.err(f"bank.rotation.activate_at_s={activate} 与 period_s={period} 不符"
                    f"（应为 {expected}）")
        rounds = as_float(scale.get("bank_anchor_period_s"))
        if rounds is not None and abs(rounds - period) > 1e-9:
            rep.err(f"bank.rotation.period_s={period} 与 scale.bank_anchor_period_s={rounds} 不一致")
    for key, table in (("bank_anchor_count", len(anchors)),):
        declared = as_int(scale.get(key))
        if declared is not None and declared != table:
            rep.err(f"scale.{key}={declared} 与实际锚点数 {table} 不一致")
    declared_warning = as_int(scale.get("bank_warning_s"))
    if declared_warning is not None and warning is not None and declared_warning != warning:
        rep.err(f"scale.bank_warning_s={declared_warning} 与 bank.rotation.warning_s={warning} 不一致")
    pad_radius = as_float(scale.get("bank_pad_radius_m"))
    for aid, anchor in anchors.items():
        r = as_float(anchor.get("radius_m"))
        if pad_radius is not None and r is not None and abs(pad_radius - r) > 1e-9:
            rep.warn(f"{aid}：radius_m={r} 与 scale.bank_pad_radius_m={pad_radius} 不一致")

    # ---------------- 9. 路线 -------------------------------------------- #
    routes: dict[str, dict] = {}
    for route in layout["routes"]:
        rid = str(route.get("id") or "").strip()
        if not rid:
            rep.err("routes：有空 id 的路线")
            continue
        if rid in routes:
            rep.err(f"{rid}：路线 id 重复")
            continue
        routes[rid] = route
        wps = route.get("waypoints") or []
        if len(wps) < 2:
            rep.err(f"{rid}：路线至少需要 2 个路点，实际 {len(wps)}")
            continue
        clean = []
        for point in wps:
            if not (isinstance(point, list) and len(point) == 2):
                rep.err(f"{rid}：路点必须是 [x, z]，实际 {point!r}")
                clean = []
                break
            x, z = as_float(point[0]), as_float(point[1])
            if x is None or z is None:
                rep.err(f"{rid}：路点必须是数字，实际 {point!r}")
                clean = []
                break
            if not in_bounds(x, z):
                rep.err(f"{rid}：路点 ({x:g}, {z:g}) 在边界外")
            clean.append((x, z))
        width = as_float(route.get("width_m"))
        clear = as_float(route.get("min_clear_width_m"))
        if width is None or width <= 0:
            rep.err(f"{rid}：width_m 必须是正数")
        if clear is None or clear <= 0:
            rep.err(f"{rid}：min_clear_width_m 必须是正数")
        elif clear < max_ball - EPS:
            rep.err(f"{rid}：净宽 {clear} m 小于最大球径 {max_ball} m（大球过不去）")
        elif clear - max_ball < margin_target - EPS:
            rep.warn(f"{rid}：余量 {clear - max_ball:.2f} m 低于目标 {margin_target} m")
        declared_len = as_float(route.get("length_m"))
        if clean and declared_len is not None:
            actual = polyline_length(clean)
            if route.get("closed"):
                actual += math.dist(clean[-1], clean[0])  # 闭合环的收尾段
            if abs(actual - declared_len) > 0.05:
                rep.warn(f"{rid}：length_m={declared_len} 与路点折线 {actual:.2f} m 不符")
        for key, table in (("spawn_id", spawns), ("bank_anchor_id", anchors),
                           ("gate_id", {})):  # gate_id 在尺寸门建好后补查
            ref = str(route.get(key) or "")
            if ref and key != "gate_id" and ref not in table:
                rep.err(f"{rid}：{key} 引用不存在的 {ref!r}")
        if route.get("closed") and clean and math.dist(clean[0], clean[-1]) < EPS:
            rep.warn(f"{rid}：closed=True 但末点重复首点（闭合边已隐含）")

    # ---------------- 7. 尺寸门 ------------------------------------------ #
    gates: dict[str, dict] = {}
    for gate in layout["size_gates"]:
        gid = str(gate.get("id") or "").strip()
        if not gid:
            rep.err("size_gates：有空 id 的尺寸门")
            continue
        if gid in gates:
            rep.err(f"{gid}：尺寸门 id 重复")
            continue
        gates[gid] = gate
        gtype = str(gate.get("type") or "")
        defn = str(gate.get("definition_id") or "")
        if gtype not in GATE_TYPES:
            rep.err(f"{gid}：未知尺寸门类型 {gtype!r}（只允许 {list(GATE_TYPES)}）")
        if defn not in props:
            rep.err(f"{gid}：未知目录 id {defn!r}")
        else:
            role = props[defn]["role"]
            if gtype == "absorb" and role != "absorbable":
                rep.err(f"{gid}：absorb 门的 {defn} 必须是可卷物（role={role}）")
            if gtype == "climb" and role != "fixed":
                rep.err(f"{gid}：climb 门的 {defn} 必须是固定地形（role={role}）")
        pos = gate.get("position")
        if not (isinstance(pos, list) and len(pos) == 3):
            rep.err(f"{gid}：position 必须是 [x, y, z]")
            continue
        x, y, z = (as_float(v) for v in pos)
        if None in (x, y, z):
            rep.err(f"{gid}：position 必须是数字")
            continue
        if not in_bounds(x, z):
            rep.err(f"{gid}：尺寸门在地图外")
        blocked = as_float(gate.get("blocks_ball_diameter_below_m"))
        opened = as_float(gate.get("absorbable_at_ball_diameter_m"))
        clear = as_float(gate.get("clear_width_m"))
        if blocked is None or opened is None:
            rep.err(f"{gid}：blocks_ball_diameter_below_m / absorbable_at_ball_diameter_m 缺失")
        else:
            if not blocked < opened:
                rep.err(f"{gid}：阻挡尺寸 {blocked} 必须小于开启尺寸 {opened}")
            if blocked < min_ball - EPS:
                rep.err(f"{gid}：阻挡尺寸 {blocked} m 小于起始球径 {min_ball} m，出生就挡不住")
            if opened > max_ball + EPS:
                rep.err(f"{gid}：开启尺寸 {opened} m 超过最大球径 {max_ball} m，等于永不开启")
            # 门槛要落在某个档位边界附近（±0.20 m），否则玩家读不出"为什么是这个尺寸"
            near = [b.get("id") for b in bands
                    if any(abs(edge - opened) <= 0.20 + EPS for edge in
                           (as_float(b.get("min_m"), 0), as_float(b.get("max_m"), 0)))]
            if not near:
                rep.warn(f"{gid}：开启尺寸 {opened} m 不靠近任何档位边界"
                         f"（{[(b.get('id'), b.get('min_m'), b.get('max_m')) for b in bands]}）")
            else:
                rep.note(f"{gid}：{gtype} 门 {blocked}→{opened} m，落在档位边界 {near}")
        if clear is None or clear <= 0:
            rep.err(f"{gid}：clear_width_m 必须是正数")
        opens = str(gate.get("opens_path") or "")
        if opens not in zones and opens not in routes:
            rep.err(f"{gid}：opens_path {opens!r} 既不是区域也不是路线")
        detour = as_float(gate.get("detour_m"))
        if detour is None or detour <= 0:
            rep.err(f"{gid}：detour_m 必须是正数（每个门都要有绕行）")
        detour_route = str(gate.get("detour_route") or "")
        if detour_route and detour_route not in routes:
            rep.err(f"{gid}：detour_route 引用不存在的路线 {detour_route!r}")
        if gtype == "climb" and opens in zones:
            ground = as_float(zones[opens].get("ground_y_m"), 0.0)
            if ground <= EPS:
                rep.err(f"{gid}：climb 门的 opens_path={opens} 地面高 {ground}，"
                        f"不是抬高地形（climb 门必须翻上一个高台）")
            if abs(y - ground) > 0.06:
                rep.warn(f"{gid}：门槛 y={y} 与 {opens} 地面 {ground} 不一致")
        elif gtype == "absorb":
            hosts = zone_at(zones, x, z)
            if not hosts:
                rep.warn(f"{gid}：门的位置不在任何区域内，灰盒里会浮在空地上")
    for route in routes.values():
        ref = str(route.get("gate_id") or "")
        if ref and ref not in gates:
            rep.err(f"{route['id']}：gate_id 引用不存在的尺寸门 {ref!r}")

    # ---------------- 8. 散布与容量 -------------------------------------- #
    scatter_regions: dict[str, dict] = {}
    scatter_use = Counter()
    scatter_zones: dict[str, set[str]] = defaultdict(set)
    gate_use = Counter()
    for gate in gates.values():
        if str(gate.get("type")) == "absorb":
            gate_use[str(gate.get("definition_id"))] += 1
    cap_props = {pid: {"dim": meta["dim"]} for pid, meta in props.items()}
    total_scatter = 0
    usage_rows = []
    for region in layout["prop_scatter"]:
        zid = str(region.get("zone_id") or "")
        if zid not in zones:
            rep.err(f"prop_scatter：未知区域 {zid!r}")
            continue
        if zid in scatter_regions:
            rep.err(f"prop_scatter：区域 {zid} 有多条散布记录（合并为一条）")
            continue
        scatter_regions[zid] = region
        band = str(region.get("tier_band") or "")
        if band not in bands_meta:
            rep.err(f"prop_scatter[{zid}]：未知尺寸档 {band!r}")
        else:
            lo, hi = (zones[zid].get("tier_band") or [None, None])[:2]
            order = [b.get("id") for b in bands]
            if lo in order and hi in order and not (order.index(lo) <= order.index(band) <= order.index(hi)):
                rep.err(f"prop_scatter[{zid}]：tier_band={band} 落在区域档位 {lo}–{hi} 之外")
        spacing = as_float(region.get("min_spacing_m"))
        if spacing is None or spacing <= 0:
            rep.err(f"prop_scatter[{zid}]：min_spacing_m 必须是正数")
            continue
        defs = region.get("definitions") or []
        if not defs:
            rep.err(f"prop_scatter[{zid}]：definitions 为空")
            continue
        for definition in defs:
            did = str(definition.get("id") or "")
            if did not in props:
                rep.err(f"prop_scatter[{zid}]/{did or '<空 id>'}：未知目录 id")
                continue
            if props[did]["role"] != "absorbable":
                rep.err(f"prop_scatter[{zid}]/{did}：不是可卷物（role={props[did]['role']}）")
            cmin, cmax = as_int(definition.get("count_min")), as_int(definition.get("count_max"))
            if cmin is None or cmax is None:
                rep.err(f"prop_scatter[{zid}]/{did}：count_min/count_max 必须是整数")
                continue
            if cmin < 1:
                rep.err(f"prop_scatter[{zid}]/{did}：count_min 必须 ≥ 1，实际 {cmin}")
            if cmax < cmin:
                rep.err(f"prop_scatter[{zid}]/{did}：count_max={cmax} 小于 count_min={cmin}")
            scatter_use[did] += cmin
            scatter_zones[did].add(zid)
        try:
            info = greybox.scatter_feasibility(zones[zid], region, cap_props)
        except (KeyError, TypeError, ValueError, ZeroDivisionError):
            # 上面已经逐条报过字段错误，这里不叠第二条、也不让坏数据把报告打断
            continue
        total_scatter += info["count"]
        label = f"prop_scatter[{zid}]"
        if info["biggest_dim_m"] + 0.1 > min(info["width_m"], info["depth_m"]):
            rep.err(f"{label}：{info['biggest_id']} 的脚印 {info['biggest_dim_m']:g} m 放不进 "
                    f"{min(info['width_m'], info['depth_m']):.2f} m 宽的区域（每边留 0.05 m）")
        if info["footprint_floor_m"] > spacing + 1e-9:
            rep.err(f"{label}：min_spacing_m={spacing:g} 小于区内最大两件的脚印间距 "
                    f"{info['footprint_floor_m']:.2f} m（{info['footprint_label']}）：会互相穿模")
        consumer = info["capacity_consumer"]
        optimal = info["capacity_optimal"]
        if info["count"] > optimal:
            rep.err(f"{label}：{info['count']} 件在 {spacing:g} m 间距下几何上不可能摆下"
                    f"（六方最密上限 {optimal} 件）")
        elif info["count"] > consumer:
            rep.err(f"{label}：{info['count']} 件超过随机顺序摆放上限 {consumer} 件"
                    f"（六方最密 {optimal}）：构建器会丢件或退回按定义间距")
        elif info["count"] > greybox.CAPACITY_USAGE_WARN * consumer:
            rep.warn(f"{label}：{info['count']}/{consumer} 件"
                     f"（{info['count'] / consumer:.0%}）已接近随机摆放上限，只剩 "
                     f"{consumer - info['count']} 件余量")
        usage_rows.append((zid, info["area_m2"], spacing, info["count"], consumer, optimal,
                           info["usage"], info["footprint_floor_m"]))
    if usage_rows:
        worst = max(usage_rows, key=lambda row: row[6])
        rep.note(f"散布区 {len(usage_rows)} 个，合计 {total_scatter} 件；"
                 f"用量最高 {worst[0]} {worst[6]:.0%}（{worst[3]}/{worst[4]} 件）")

    # ---------------- 2b. 目录计数与 zone_ids ---------------------------- #
    for pid, meta in props.items():
        if meta["role"] == "absorbable":
            expected = scatter_use.get(pid, 0) + gate_use.get(pid, 0)
            if meta["count"] != expected:
                rep.err(f"{pid}：目录 count_in_map={meta['count']} 与布局实例数 {expected} 不符"
                        f"（散布 {scatter_use.get(pid, 0)} + 可吸门 {gate_use.get(pid, 0)}）")
        else:
            expected = struct_count.get(pid, 0)
            if meta["count"] != expected:
                rep.err(f"{pid}：目录 count_in_map={meta['count']} 与结构实例数 {expected} 不符")
        used = scatter_use.get(pid, 0) + gate_use.get(pid, 0) + struct_count.get(pid, 0)
        if used == 0 and meta["count"] > 0:
            rep.err(f"{pid}：目录有 {meta['count']} 个实例，但 layout.json 里没有任何引用")
        if meta["count"] == 0 and used > 0:
            rep.err(f"{pid}：目录 count_in_map=0 但有 {used} 处布局引用")
        # zone_ids 必须与实际落点完全一致
        actual: set[str] = set(scatter_zones.get(pid, set())) | set(struct_zones.get(pid, set()))
        for gate in gates.values():
            if str(gate.get("definition_id")) == pid:
                gx, gz = as_float(gate["position"][0]), as_float(gate["position"][2])
                actual |= set(zone_at(zones, gx, gz))
        listed = set(meta["zone_ids"])
        if listed != actual:
            missing = sorted(actual - listed)
            extra = sorted(listed - actual)
            rep.err(f"{pid}：zone_ids 与实际落点不一致（缺 {missing}，多 {extra}）")

    # ---------------- 10. 咽喉 ------------------------------------------- #
    narrow = []
    for chokepoint in layout["chokepoints"]:
        cid = str(chokepoint.get("id") or "").strip()
        if not cid:
            rep.err("chokepoints：有空 id 的咽喉记录")
            continue
        pos = chokepoint.get("position")
        if not (isinstance(pos, list) and len(pos) == 3):
            rep.err(f"{cid}：position 必须是 [x, y, z]")
            continue
        x, z = as_float(pos[0]), as_float(pos[2])
        if x is None or z is None:
            rep.err(f"{cid}：position 必须是数字")
            continue
        if not in_bounds(x, z):
            rep.err(f"{cid}：咽喉在地图外")
        clear = as_float(chokepoint.get("clear_width_m"))
        if clear is None or clear <= 0:
            rep.err(f"{cid}：clear_width_m 必须是正数")
            continue
        gate_ref = str(chokepoint.get("gate_id") or "")
        if gate_ref and gate_ref not in gates:
            rep.err(f"{cid}：gate_id 引用不存在的尺寸门 {gate_ref!r}")
        if clear < max_ball - EPS:
            narrow.append((cid, clear))
            if not chokepoint.get("narrow_small_ball_lane"):
                rep.err(f"{cid}：净宽 {clear} m 小于最大球径 {max_ball} m，"
                        f"必须显式标 narrow_small_ball_lane 并说明为什么不是死路")
            if clear < min_ball - EPS:
                rep.err(f"{cid}：窄道净宽 {clear} m 小于起始球径 {min_ball} m，出生就过不去")
            # 窄道里不能有可卷物：否则小球会在里面吃到东西长大，把"能进的缝"变成"出不去"
            hosts = [zid for zid in zone_at(zones, x, z) if zid in scatter_regions]
            if hosts:
                rep.err(f"{cid}：窄道落在散布区 {hosts} 内——里面的可卷物会让小球在缝里长大卡住；"
                        f"把该点挪到散布区外（或让该散布区不含这个点）")
            if chokepoint.get("narrow_small_ball_lane"):
                rep.note(f"{cid}：故意保留的小球道（{clear} m），最大球必须绕行；"
                         f"点在所有散布区之外，缝内没有可卷物")
        elif clear - max_ball < margin_target - EPS:
            rep.warn(f"{cid}：净宽 {clear} m 对最大球只剩 {clear - max_ball:.2f} m 余量")

    # ---------------- 12. 道具 / 重生 / 人数 / 预算 ----------------------- #
    for point in (layout["powerups"] or {}).get("points") or []:
        pid = str(point.get("id") or "")
        ptype = str(point.get("type") or "")
        if ptype not in POWERUP_TYPES:
            rep.err(f"{pid}：未知道具类型 {ptype!r}（只允许 {list(POWERUP_TYPES)}）")
        zid = str(point.get("zone_id") or "")
        if zid not in zones:
            rep.err(f"{pid}：未知区域 {zid!r}")
        pos = point.get("position") or []
        if not (isinstance(pos, list) and len(pos) == 3):
            rep.err(f"{pid}：position 必须是 [x, y, z]")
            continue
        x, z = as_float(pos[0]), as_float(pos[2])
        if x is None or z is None:
            rep.err(f"{pid}：position 必须是数字")
            continue
        if not in_bounds(x, z):
            rep.err(f"{pid}：道具点在地图外")
        if zid in zones and not contains(zones[zid]["_rect"], x, z):
            rep.warn(f"{pid}：位置 ({x:g}, {z:g}) 不在声明的区域 {zid} 内")
        for sid, spawn in spawns.items():
            sp = spawn.get("position") or []
            if len(sp) == 3 and math.dist((x, z), (as_float(sp[0]), as_float(sp[2]))) < 8.0:
                rep.warn(f"{pid}：距 {sid} 不足 8 m，可能被出生点直接吃走")

    respawn = layout["respawn"]
    for sid in respawn.get("candidate_spawn_ids") or []:
        if str(sid) not in spawns:
            rep.err(f"respawn.candidate_spawn_ids：未知出生点 {sid!r}")
    for key in ("min_distance_from_enemy_m", "respawn_delay_s", "protection_s"):
        if as_float(respawn.get(key)) is None:
            rep.err(f"respawn.{key} 缺失或不是数字")
    if (as_float(respawn.get("min_distance_from_enemy_m")) or 0) <= 0:
        rep.err("respawn.min_distance_from_enemy_m 必须为正")

    plans = layout["player_count_plans"]
    for key, value in plans.items():
        if not key.isdigit():
            continue
        count = int(key)
        if not isinstance(value, list) or len(value) != count:
            rep.err(f"player_count_plans[{key}]：需要正好 {count} 个出生点，实际 {value!r}")
            continue
        if len(set(value)) != len(value):
            rep.err(f"player_count_plans[{key}]：出生点重复 {value!r}")
        for sid in value:
            if str(sid) not in spawns:
                rep.err(f"player_count_plans[{key}]：未知出生点 {sid!r}")
        if count > len(spawns):
            rep.err(f"player_count_plans[{key}]：{count} 人超过出生点总数 {len(spawns)}")

    budget = layout["performance_budget"]
    absorbable_instances = sum(m["count"] for m in props.values() if m["role"] == "absorbable")
    absorbable_defs = sum(1 for m in props.values() if m["role"] == "absorbable")
    fixed_instances = sum(m["count"] for m in props.values() if m["role"] == "fixed")
    for key, expected in (("absorbable_instances", absorbable_instances),
                          ("absorbable_definition_count", absorbable_defs),
                          ("structure_instances", fixed_instances)):
        declared = as_int(budget.get(key))
        if declared is not None and declared != expected:
            rep.err(f"performance_budget.{key}={declared} 与目录统计 {expected} 不一致")
    tier_inst = Counter()
    tier_value = Counter()
    for meta in props.values():
        if meta["role"] != "absorbable":
            continue
        tier_inst[meta["tier"]] += meta["count"]
        tier_value[meta["tier"]] += meta["count"] * meta["value"]
    total_inst = sum(tier_inst.values())
    total_value = sum(tier_value.values())
    rep.note(f"可卷实例 {total_inst}，占位价值 {total_value} 单位"
             f"（s {tier_inst['s']}/{tier_value['s']}，m {tier_inst['m']}/{tier_value['m']}，"
             f"l {tier_inst['l']}/{tier_value['l']}）；固定实例 {fixed_instances}")
    if fixed_instances and as_int(budget.get("structure_instances")) is None:
        rep.warn("performance_budget.structure_instances 缺失")

    # ---------------- 5b. 出生 → 交分设计路程 ---------------------------- #
    approaches = [r for r in routes.values() if r.get("bank_anchor_id")]
    exits: dict[str, list[dict]] = defaultdict(list)
    for route in routes.values():
        if route.get("spawn_id"):
            exits[str(route["spawn_id"])].append(route)
    design_values = []
    for sid in spawns:
        if len(exits[sid]) < 2:
            rep.err(f"{sid}：只有 {len(exits[sid])} 条出路，至少要 2 条独立出路")
        best = None
        for exit_route in exits[sid]:
            end = (exit_route.get("waypoints") or [[]])[-1]
            if len(end) != 2:
                continue
            for approach in approaches:
                start = (approach.get("waypoints") or [[]])[0]
                if len(start) != 2:
                    continue
                if math.dist(end, start) <= 0.6:
                    total = (as_float(exit_route.get("length_m"), 0) or 0) + \
                            (as_float(approach.get("length_m"), 0) or 0)
                    if best is None or total < best:
                        best = total
        if best is None:
            rep.warn(f"{sid}：没有任何「出路 + 台面接近」的接续，算不出到交分的路程")
            continue
        design_values.append((sid, best))
    target = as_float(scale.get("spawn_to_bank_target_m"))
    if design_values:
        values = [v for _, v in design_values]
        rep.note(f"出生→交分设计路程 {min(values):.1f}–{max(values):.1f} m"
                 f"（目标 {target} m）")
        if max(values) - min(values) > 1.0:
            rep.err(f"四个出生点到交分的路程最大差 {max(values) - min(values):.2f} m > 1.0 m")
        if target is not None:
            for sid, value in design_values:
                if abs(value - target) > 0.25 * target:
                    rep.warn(f"{sid}：到交分 {value:.1f} m 与设计目标 {target} m 偏差 >25%")

    # ---------------- 11b. 每档是否被覆盖 -------------------------------- #
    order = [b.get("id") for b in bands]
    covered = set()
    for zone in zones.values():
        pair = zone.get("tier_band") or []
        if len(pair) == 2 and pair[0] in order and pair[1] in order:
            for band in order[order.index(pair[0]):order.index(pair[1]) + 1]:
                covered.add(band)
    for band in order:
        if band not in covered:
            rep.warn(f"尺寸档 {band} 没有被任何区域声明覆盖（没有专属资源区）")

    return rep


# --------------------------------------------------------------------------- #
# 输出
# --------------------------------------------------------------------------- #
def format_table(rows) -> str:
    header = ("region", "area", "spacing", "count", "cap_rsa", "cap_hex", "usage", "foot")
    lines = [f"{header[0]:<16}{header[1]:>7}{header[2]:>8}{header[3]:>7}"
             f"{header[4]:>8}{header[5]:>8}{header[6]:>7}{header[7]:>7}"]
    for zid, area, spacing, count, consumer, optimal, usage, floor in rows:
        lines.append(f"{zid:<16}{area:>7.1f}{spacing:>8.2f}{count:>7d}"
                     f"{consumer:>8d}{optimal:>8d}{usage:>6.0%}{floor:>7.2f}")
    return "\n".join(lines)


def report_text(rep: Report, layout_path: Path, catalog_path: Path, rows, verbose: bool) -> str:
    out = [f"layout : {layout_path}", f"catalog: {catalog_path}", ""]
    for note in rep.notes:
        out.append(f"  · {note}")
    if verbose and rows:
        out += ["", "散布区容量（2-D 随机顺序吸附上限，由 build_greybox 计算）：",
                format_table(rows), ""]
    if rep.warnings:
        out.append(f"WARN ({len(rep.warnings)}):")
        out += [f"  ! {w}" for w in rep.warnings]
        out.append("")
    if rep.errors:
        out.append(f"ERROR ({len(rep.errors)}):")
        out += [f"  x {e}" for e in rep.errors]
        out.append("")
    out.append(f"结论: {'OK' if rep.ok else 'FAIL'}  "
               f"(errors {len(rep.errors)}, warnings {len(rep.warnings)})")
    return "\n".join(out)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="校验任何按本 schema 写的地图作者数据（layout.json + prop_catalog.csv）。")
    parser.add_argument("target", type=Path, help="地图目录，或 layout.json 的路径")
    parser.add_argument("--catalog", type=Path, default=None, help="显式指定 prop_catalog.csv")
    parser.add_argument("--json", action="store_true", help="额外输出机器可读摘要")
    parser.add_argument("--verbose", "-v", action="store_true", help="打印逐区容量表")
    args = parser.parse_args(argv)

    layout_path, catalog_path = resolve_inputs(args.target, args.catalog)
    try:
        layout, catalog = load_inputs(layout_path, catalog_path)
    except InputError as exc:
        print(f"输入错误：{exc}", file=sys.stderr)
        return 2

    rep = validate(layout, catalog)

    rows = []
    scale = layout.get("scale") or {}
    cap_props = {}
    for row in catalog["rows"]:
        dim = as_float(row.get("game_longest_dim_m"))
        if dim is not None:
            cap_props[(row.get("id") or "").strip()] = {"dim": dim}
    zones = {}
    for zone in layout.get("zones") or []:
        rect = rect_of(zone)
        if rect is not None:
            zones[str(zone.get("id"))] = {**zone, "_rect": rect}
    for region in layout.get("prop_scatter") or []:
        zid = str(region.get("zone_id"))
        if zid in zones:
            try:
                info = greybox.scatter_feasibility(zones[zid], region, cap_props)
            except (KeyError, TypeError, ValueError, ZeroDivisionError):
                continue
            rows.append((zid, info["area_m2"], info["spacing_m"], info["count"],
                         info["capacity_consumer"], info["capacity_optimal"],
                         info["usage"], info["footprint_floor_m"]))
    rows.sort(key=lambda row: -row[6])

    if args.json:
        dict_rows = [{"zone_id": zid, "area_m2": area, "spacing_m": spacing, "count": count,
                      "capacity_consumer": consumer, "capacity_optimal": optimal,
                      "usage": round(usage, 4), "footprint_floor_m": round(floor, 3)}
                     for zid, area, spacing, count, consumer, optimal, usage, floor in rows]
        print(json.dumps({
            "status": "OK" if rep.ok else "FAIL",
            "layout": str(layout_path), "catalog": str(catalog_path),
            "map_id": layout.get("map_id"),
            "errors": rep.errors, "warnings": rep.warnings, "notes": rep.notes,
            "scatter": dict_rows,
        }, ensure_ascii=False, indent=2))
    else:
        print(report_text(rep, layout_path, catalog_path, rows, args.verbose))
    return 0 if rep.ok else 1


if __name__ == "__main__":
    sys.exit(main())
