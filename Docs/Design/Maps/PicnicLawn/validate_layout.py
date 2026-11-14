#!/usr/bin/env python3
"""检查 PicnicLawn 灰盒数据的内部一致性（不验证 Unity、可玩性或实测数字）。

用法：
    python3 validate_layout.py            # 在任意目录运行
    python3 validate_layout.py --json     # 额外输出机器可读摘要

检查项：
 1. layout.json / prop_catalog.csv 结构、必填字段、ID 唯一。
 2. 区域矩形合法、面积与声明一致、互不重叠、都在边界内。
 3. 出生点在边界与所属区域内；每个出生点至少两条按 spawn_id 标记的出路。
 4. 交分台圆盘互不重叠、在区域内；轮换顺序覆盖全部锚点。
 5. 尺寸门引用的物件/通路存在，且 阻挡尺寸 < 可吸/可翻越尺寸。
 6. 散布区引用存在的区域与目录 ID；目录 count 与散布+门/结构的实例数一致。
 7. 路线点位在边界内、净宽不窄于最大球径、并打印余量。
 8. 目录里每个 ID 都被 layout.json 真正使用；区域 ID 全部被引用或明确留空。
"""
import argparse
import csv
import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
LAYOUT = HERE / "layout.json"
CATALOG = HERE / "prop_catalog.csv"

REQUIRED_CATALOG_COLS = [
    "id", "name_zh", "category", "size_tier", "real_longest_dim_m",
    "game_longest_dim_m", "absorb_value_placeholder", "count_in_map", "zone_ids",
    "sourcing_hint", "priority", "coverage_ref", "role", "notes",
]
TIERS = {"s", "m", "l"}
PRIORITIES = {"MVP", "later"}
ROLES = {"absorbable", "fixed"}
GATE_TYPES = {"absorb", "climb"}
# 这些装饰本来就在边界外或本身是边界/合并网格，允许 outside bounds。
OUTSIDE_OK = {"giant_picnic_table", "giant_oak", "distant_tree_line",
              "boundary_hedge", "invisible_wall", "ground_terrain", "path_mesh"}


def rect_area(shape):
    return (shape["max_x"] - shape["min_x"]) * (shape["max_z"] - shape["min_z"])


def overlap_area(a, b):
    w = min(a["max_x"], b["max_x"]) - max(a["min_x"], b["min_x"])
    h = min(a["max_z"], b["max_z"]) - max(a["min_z"], b["min_z"])
    return w * h if w > 0 and h > 0 else 0.0


def inside(shape, x, z, eps=1e-6):
    return (shape["min_x"] - eps <= x <= shape["max_x"] + eps
            and shape["min_z"] - eps <= z <= shape["max_z"] + eps)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    errors, warnings, notes = [], [], []
    layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
    with CATALOG.open(encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        cat_cols = reader.fieldnames or []
        cat_rows = list(reader)

    for col in REQUIRED_CATALOG_COLS:
        if col not in cat_cols:
            errors.append(f"prop_catalog.csv 缺列: {col}")
    catalog = {}
    for i, row in enumerate(cat_rows, start=2):
        cid = row.get("id", "").strip()
        if not cid:
            errors.append(f"prop_catalog.csv 行 {i}: 空 id")
            continue
        if cid in catalog:
            errors.append(f"prop_catalog.csv 行 {i}: 重复 id {cid}")
        if row["size_tier"] not in TIERS:
            errors.append(f"{cid}: size_tier 必须是 {sorted(TIERS)}，实际 {row['size_tier']!r}")
        if row["priority"] not in PRIORITIES:
            errors.append(f"{cid}: priority 必须是 {sorted(PRIORITIES)}，实际 {row['priority']!r}")
        if row["role"] not in ROLES:
            errors.append(f"{cid}: role 必须是 {sorted(ROLES)}，实际 {row['role']!r}")
        try:
            count = int(row["count_in_map"])
            value = int(row["absorb_value_placeholder"])
        except ValueError:
            errors.append(f"{cid}: count_in_map / absorb_value_placeholder 必须是整数")
            continue
        if count < 0 or value < 0:
            errors.append(f"{cid}: count / value 不能为负")
        catalog[cid] = {"row": row, "count": count, "value": value,
                        "tier": row["size_tier"], "role": row["role"]}

    if layout.get("schema_version") != 1:
        errors.append("layout.json: schema_version 必须为 1")
    for key in ("map_id", "status", "coordinate_system", "scale", "bounds", "zones",
                "structures", "bank", "spawns", "size_gates", "prop_scatter",
                "routes", "chokepoints", "powerups", "respawn", "performance_budget"):
        if key not in layout:
            errors.append(f"layout.json: 缺顶层键 {key}")
    if errors:
        report(errors, warnings, notes, layout, args)
        return 1

    b = layout["bounds"]
    max_ball = float(layout["scale"]["ball_max_collision_diameter_m"])
    min_margin = float(layout["scale"]["min_route_margin_over_max_ball_m"])

    def in_bounds(x, z):
        return b["min_x"] <= x <= b["max_x"] and b["min_z"] <= z <= b["max_z"]

    # ---- zones
    zones = {}
    for z in layout["zones"]:
        zid = z["id"]
        if zid in zones:
            errors.append(f"重复区域 id: {zid}")
        zones[zid] = z
        s = z["shape"]
        if s.get("type") != "rect":
            errors.append(f"{zid}: 当前只支持 rect")
            continue
        if not (s["min_x"] < s["max_x"] and s["min_z"] < s["max_z"]):
            errors.append(f"{zid}: 矩形退化")
        if abs(rect_area(s) - z["area_m2"]) > 0.01:
            errors.append(f"{zid}: area_m2={z['area_m2']} 与矩形计算值 {rect_area(s):.3f} 不符")
        for x, zz in ((s["min_x"], s["min_z"]), (s["max_x"], s["max_z"])):
            if not in_bounds(x, zz):
                errors.append(f"{zid}: 矩形超出边界")
        if len(z["tier_band"]) != 2:
            errors.append(f"{zid}: tier_band 必须是 [最小档, 最大档]")
        for band in z["tier_band"]:
            if band not in {x["id"] for x in layout["scale"]["ball_bands"]}:
                errors.append(f"{zid}: 未知尺寸档 {band}")
        if not z["name_zh"] or not z["purpose"]:
            errors.append(f"{zid}: name_zh / purpose 不能为空")

    ids = list(zones)
    for i, a in enumerate(ids):
        for c in ids[i + 1:]:
            ov = overlap_area(zones[a]["shape"], zones[c]["shape"])
            if ov > 0.01:
                errors.append(f"区域重叠 {a} × {c}: {ov:.2f} m²")
    zone_area = sum(z["area_m2"] for z in zones.values())
    bounds_area = (b["max_x"] - b["min_x"]) * (b["max_z"] - b["min_z"])
    if zone_area > bounds_area + 1e-6:
        errors.append(f"区域面积和 {zone_area:.1f} m² 超过地图面积 {bounds_area:.1f} m²")
    notes.append(f"区域 {len(zones)} 个，合计 {zone_area:.1f} m²（地图 {bounds_area:.0f} m²，占 {100 * zone_area / bounds_area:.0f}%）")
    notes.append("散布密度 / 2-D 放置可行性不在这里判（见 README §3.3）："
                 "跑 python3 Tools/mapbuild/build_greybox.py --feasibility")

    # ---- structures
    struct_count = Counter()
    for s in layout["structures"]:
        did = s["definition_id"]
        if did not in catalog:
            errors.append(f"structure 引用不存在的目录 ID: {did}")
        struct_count[did] += 1
        if s["zone_id"] is not None and s["zone_id"] not in zones:
            errors.append(f"structure {did}: 未知区域 {s['zone_id']}")
        x, _, z = s["position"]
        if not in_bounds(x, z) and did not in OUTSIDE_OK:
            errors.append(f"structure {did} 在边界外 ({x}, {z})，但不在允许清单")

    # ---- spawns
    spawns = {}
    for sp in layout["spawns"]:
        sid = sp["id"]
        if sid in spawns:
            errors.append(f"重复出生点 id: {sid}")
        spawns[sid] = sp
        if sp["zone_id"] not in zones:
            errors.append(f"{sid}: 未知区域 {sp['zone_id']}")
            continue
        x, y, z = sp["position"]
        if not in_bounds(x, z):
            errors.append(f"{sid}: 出生点在地图外")
        if not inside(zones[sp["zone_id"]]["shape"], x, z):
            errors.append(f"{sid}: 出生点不在 {sp['zone_id']} 内")
        if not 0 <= sp["yaw_deg"] <= 360:
            errors.append(f"{sid}: yaw_deg 越界")
        if abs(y - zones[sp["zone_id"]]["ground_y_m"]) > 0.06:
            warnings.append(f"{sid}: y 与区域地面高度差 > 0.06 m")

    exits = defaultdict(list)
    for r in layout["routes"]:
        if r.get("spawn_id"):
            exits[r["spawn_id"]].append(r["id"])
    for sid in spawns:
        if len(exits[sid]) < 2:
            errors.append(f"{sid}: 只有 {len(exits[sid])} 条出路，至少 2 条")

    # ---- bank anchors
    bank = layout["bank"]
    anchors = {}
    for a in bank["anchors"]:
        aid = a["id"]
        anchors[aid] = a
        x, y, z = a["position"]
        if not in_bounds(x, z):
            errors.append(f"{aid}: 交分台在地图外")
        if bank["zone_id"] not in zones:
            errors.append(f"{aid}: 交分台区域 {bank['zone_id']} 不存在")
        elif not inside(zones[bank["zone_id"]]["shape"], x, z):
            errors.append(f"{aid}: 交分台不在 {bank['zone_id']} 内")
        if a["radius_m"] <= 0:
            errors.append(f"{aid}: 半径必须为正")
        for rid in a.get("approach_route_ids", []):
            if rid not in {r["id"] for r in layout["routes"]}:
                errors.append(f"{aid}: 引用了不存在的路线 {rid}")
        for e in a.get("entrances", []):
            if e not in {r["id"] for r in layout["routes"]}:
                errors.append(f"{aid}: 引用了不存在的入口路线 {e}")
    alist = list(anchors)
    for i, a in enumerate(alist):
        for c in alist[i + 1:]:
            pa, pc = anchors[a]["position"], anchors[c]["position"]
            d = math.dist((pa[0], pa[2]), (pc[0], pc[2]))
            sum_r = anchors[a]["radius_m"] + anchors[c]["radius_m"]
            if d <= sum_r:
                errors.append(f"交分台重叠 {a} × {c}: 圆心距 {d:.2f} m ≤ 半径和 {sum_r:.2f} m")
    order = bank["rotation"]["order"]
    if sorted(order) != sorted(alist) or len(set(order)) != len(order):
        errors.append(f"交分轮换顺序不是锚点集合的排列: {order}")

    # ---- size gates
    route_ids = {r["id"] for r in layout["routes"]}
    for g in layout["size_gates"]:
        gid = g["id"]
        if g["type"] not in GATE_TYPES:
            errors.append(f"{gid}: 未知尺寸门类型 {g['type']!r}")
        if g["definition_id"] not in catalog:
            errors.append(f"{gid}: 未知目录 ID {g['definition_id']}")
        x, _, z = g["position"]
        if not in_bounds(x, z):
            errors.append(f"{gid}: 尺寸门在地图外")
        blk, opn = g["blocks_ball_diameter_below_m"], g["absorbable_at_ball_diameter_m"]
        if not blk < opn:
            errors.append(f"{gid}: 阻挡尺寸 {blk} 必须小于开启尺寸 {opn}")
        if opn > max_ball + 1e-9:
            warnings.append(f"{gid}: 开启尺寸 {opn} m 超过最大球径 {max_ball} m，等于永不开启")
        if g["type"] == "climb" and catalog.get(g["definition_id"], {}).get("role") != "fixed":
            warnings.append(f"{gid}: climb 型门的物件应为固定地形（role=fixed）")
        if g["type"] == "absorb" and catalog.get(g["definition_id"], {}).get("role") != "absorbable":
            errors.append(f"{gid}: absorb 型门的物件必须是可卷物（role=absorbable）")
        if g["opens_path"] not in route_ids and g["opens_path"] not in zones:
            errors.append(f"{gid}: opens_path {g['opens_path']!r} 既不是路线也不是区域")

    # ---- prop scatter
    scatter_sum = Counter()
    scatter_zones = set()
    for reg in layout["prop_scatter"]:
        zid = reg["zone_id"]
        if zid not in zones:
            errors.append(f"散布区引用未知区域 {zid}")
            continue
        if zid in scatter_zones:
            errors.append(f"区域 {zid} 有多条散布记录，合并为一条")
        scatter_zones.add(zid)
        if not reg["definitions"]:
            errors.append(f"{zid}: 散布记录为空")
        if reg["min_spacing_m"] <= 0:
            errors.append(f"{zid}: min_spacing_m 必须为正")
        for d in reg["definitions"]:
            did = d["id"]
            if did not in catalog:
                errors.append(f"{zid}: 散布引用未知目录 ID {did}")
                continue
            if catalog[did]["role"] != "absorbable":
                errors.append(f"{zid}: {did} 不是可卷物，不能放进散布")
            if d["count_min"] < 1:
                errors.append(f"{zid}/{did}: count_min 必须 ≥ 1")
            if d["count_max"] < d["count_min"]:
                errors.append(f"{zid}/{did}: count_max < count_min")
            scatter_sum[did] += d["count_min"]

    gate_defs = Counter(g["definition_id"] for g in layout["size_gates"] if g["type"] == "absorb")
    for cid, meta in catalog.items():
        if meta["role"] == "absorbable":
            expect = scatter_sum[cid] + gate_defs[cid]
            if meta["count"] != expect:
                errors.append(f"{cid}: 目录 count={meta['count']} 但散布+门={expect}")
        else:
            if meta["count"] != struct_count[cid]:
                errors.append(f"{cid}: 目录 count={meta['count']} 但结构实例={struct_count[cid]}")

    # ---- routes
    for r in layout["routes"]:
        wps = r["waypoints"]
        if len(wps) < 2:
            errors.append(f"{r['id']}: 路线至少需要 2 个点")
        for x, z in wps:
            if not in_bounds(x, z):
                errors.append(f"{r['id']}: 路点 ({x}, {z}) 在地图外")
        if r["width_m"] <= 0 or r["min_clear_width_m"] <= 0:
            errors.append(f"{r['id']}: 宽度必须为正")
        if r["min_clear_width_m"] < max_ball:
            errors.append(f"{r['id']}: 净宽 {r['min_clear_width_m']} m 小于最大球径 {max_ball} m")
        elif r["min_clear_width_m"] - max_ball < min_margin - 1e-9:
            warnings.append(f"{r['id']}: 余量 {r['min_clear_width_m'] - max_ball:.2f} m 低于目标 {min_margin} m")
        for key in ("gate_id", "bank_anchor_id", "spawn_id"):
            if r.get(key) and r[key] not in (
                    {g["id"] for g in layout["size_gates"]} if key == "gate_id" else
                    anchors if key == "bank_anchor_id" else spawns):
                errors.append(f"{r['id']}: 引用不存在的 {key} {r[key]}")
        if r.get("closed") and math.dist(wps[0], wps[-1]) < 1e-6:
            warnings.append(f"{r['id']}: closed=True 但末点重复首点（闭合边已隐含，删掉重复点即可）")

    # ---- chokepoints
    for cp in layout["chokepoints"]:
        narrow_ok = bool(cp.get("narrow_small_ball_lane"))
        if cp["clear_width_m"] < max_ball:
            msg = (f"{cp['id']}: 咽喉净宽 {cp['clear_width_m']} m 小于最大球径 {max_ball} m")
            if narrow_ok:
                notes.append(f"{cp['id']}: 故意保留的小球侧路（{cp['clear_width_m']} m），最大球必须绕行")
            else:
                errors.append(msg + "，且未标 narrow_small_ball_lane")
        for key in ("gate_id",):
            if cp.get(key) and cp[key] not in {g["id"] for g in layout["size_gates"]}:
                errors.append(f"{cp['id']}: 引用不存在的尺寸门 {cp[key]}")

    # ---- powerups
    for p in layout["powerups"]["points"]:
        if p["type"] not in {"magnet", "rocket"}:
            errors.append(f"{p['id']}: 未知道具类型 {p['type']!r}")
        if p["zone_id"] not in zones:
            errors.append(f"{p['id']}: 未知区域 {p['zone_id']}")
        x, _, z = p["position"]
        if not in_bounds(x, z):
            errors.append(f"{p['id']}: 道具点在地图外")
        for sp in spawns.values():
            sx, _, sz = sp["position"]
            if math.dist((x, z), (sx, sz)) < 8.0:
                warnings.append(f"{p['id']}: 距 {sp['id']} 不足 8 m，可能被出生点直接吃走")

    # ---- catalog usage
    used = set(scatter_sum) | set(gate_defs) | set(struct_count)
    for cid, meta in catalog.items():
        if meta["count"] > 0 and cid not in used:
            errors.append(f"{cid}: 目录有 {meta['count']} 个实例，但 layout.json 里没有任何引用")
        if meta["count"] == 0 and cid in used:
            errors.append(f"{cid}: 目录 count=0 但有布局引用")

    # ---- catalog zone ids
    declared_zones = defaultdict(set)
    for reg in layout["prop_scatter"]:
        for d in reg["definitions"]:
            declared_zones[d["id"]].add(reg["zone_id"])
    for g in layout["size_gates"]:
        if g["type"] == "absorb":
            declared_zones[g["definition_id"]].add("core_lawn")
    for s in layout["structures"]:
        if s["zone_id"]:
            declared_zones[s["definition_id"]].add(s["zone_id"])
    for cid, meta in catalog.items():
        raw = (meta["row"]["zone_ids"] or "").strip()
        if raw.lower() in ("", "all", "none"):
            continue
        listed = {v.strip() for v in raw.split(";") if v.strip()}
        unknown = listed - set(zones)
        if unknown:
            errors.append(f"{cid}: zone_ids 含未知区域 {sorted(unknown)}")
        if listed != declared_zones.get(cid, set()):
            errors.append(f"{cid}: zone_ids={sorted(listed)} 与布局实际 {sorted(declared_zones.get(cid, set()))} 不一致")

    # ---- report
    route_rows = [{"id": r["id"], "kind": r["kind"], "length_m": r["length_m"],
                   "clear_m": r["min_clear_width_m"],
                   "margin_m": round(r["min_clear_width_m"] - max_ball, 2)}
                  for r in layout["routes"]]
    summary = {
        "status": "FAIL" if errors else "OK",
        "errors": len(errors), "warnings": len(warnings),
        "zones": len(zones), "zone_area_m2": round(zone_area, 1),
        "spawns": len(spawns), "bank_anchors": len(anchors), "gates": len(layout["size_gates"]),
        "scatter_regions": len(layout["prop_scatter"]),
        "catalog_rows": len(catalog),
        "absorbable_instances": sum(m["count"] for m in catalog.values() if m["role"] == "absorbable"),
        "fixed_instances": sum(m["count"] for m in catalog.values() if m["role"] == "fixed"),
        "route_margin_min_m": min(r["margin_m"] for r in route_rows) if route_rows else None,
        "routes": route_rows,
        "notes": notes,
    }
    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
    report(errors, warnings, notes, layout, args, summary)
    return 1 if errors else 0


def report(errors, warnings, notes, layout, args, summary=None):
    if not args.json:
        print(f"layout: {LAYOUT}")
        print(f"catalog: {CATALOG}")
        for n in notes:
            print(f"  · {n}")
    print()
    if warnings:
        print(f"WARN ({len(warnings)}):")
        for w in warnings:
            print(f"  ! {w}")
    if errors:
        print(f"ERROR ({len(errors)}):")
        for e in errors:
            print(f"  x {e}")
    if summary:
        print(f"\n结论: {summary['status']}  "
              f"(区域 {summary['zones']}, 出生点 {summary['spawns']}, 交分台 {summary['bank_anchors']}, "
              f"尺寸门 {summary['gates']}, 散布区 {summary['scatter_regions']}, "
              f"可卷实例 {summary['absorbable_instances']}, 固定实例 {summary['fixed_instances']}, "
              f"最小路线余量 {summary['route_margin_min_m']} m)")


if __name__ == "__main__":
    sys.exit(main())
