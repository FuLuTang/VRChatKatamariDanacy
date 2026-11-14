#!/usr/bin/env python3
"""Scenario sweeps for the offline round model.

Runs the 2/3/4-player scenarios, a restock experiment and a one-at-a-time
+/-20% parameter sensitivity sweep, prints plain-text tables and writes
CSV + Markdown into reports/.

    python3 Docs/Design/Gameplay/simulate.py
    python3 Docs/Design/Gameplay/simulate.py --scenario four_mixed --seed 7
    python3 Docs/Design/Gameplay/simulate.py --list

Everything here is offline design support. It is not a Unity, network or
player-skill measurement, and the numbers are PROVISIONAL_NOT_TESTED.
"""

from __future__ import annotations

import argparse
import csv
import sys
from dataclasses import dataclass, replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from round_model import (
    AvoidThreats,
    EatEverything,
    PlayerProfile,
    Policy,
    RoundParams,
    RoundResult,
    RushBank,
    TIME_SINKS,
    simulate_round,
)

HERE = Path(__file__).resolve().parent
DEFAULT_OUT = HERE / "reports"
STATUS = "PROVISIONAL_NOT_TESTED"


# --------------------------------------------------------------------------
# Scenarios
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class Scenario:
    name: str
    params: RoundParams
    policies: tuple[Policy, ...]
    note: str


def _mixed_four(restock: bool) -> tuple[RoundParams, tuple[Policy, ...]]:
    profiles = (
        PlayerProfile("fast_eater", 1.10, 1.05),
        PlayerProfile("careful", 1.00, 1.00),
        PlayerProfile("slow_banker", 0.85, 0.90),
        PlayerProfile("average_eater", 0.95, 1.00),
    )
    policies = (EatEverything(), AvoidThreats(), RushBank(), EatEverything())
    params = RoundParams(profiles=profiles)
    if restock:
        params = replace(params, restock_period_s=30.0, restock_fraction=0.25)
    return params, policies


_FOUR_PARAMS, _FOUR_POLICIES = _mixed_four(restock=False)
_FOUR_RESTOCK_PARAMS, _FOUR_RESTOCK_POLICIES = _mixed_four(restock=True)


SCENARIOS: dict[str, Scenario] = {
    "duel_equal": Scenario(
        name="duel_equal",
        params=RoundParams(profiles=(
            PlayerProfile("player_a", 1.00, 1.00),
            PlayerProfile("player_b", 1.00, 1.00),
        )),
        policies=(EatEverything(), EatEverything()),
        note="2 players, identical skill, both greedy: the symmetric baseline.",
    ),
    "triple_mixed": Scenario(
        name="triple_mixed",
        params=RoundParams(profiles=(
            PlayerProfile("fast_eater", 1.10, 1.05),
            PlayerProfile("careful", 1.00, 1.00),
            PlayerProfile("slow_banker", 0.85, 0.90),
        )),
        policies=(EatEverything(), AvoidThreats(), RushBank()),
        note="3 players, mixed skill and policy.",
    ),
    "four_mixed": Scenario(
        name="four_mixed",
        params=_FOUR_PARAMS,
        policies=_FOUR_POLICIES,
        note="4 players, mixed skill and policy; also the sensitivity baseline.",
    ),
    "four_restock": Scenario(
        name="four_restock",
        params=_FOUR_RESTOCK_PARAMS,
        policies=_FOUR_RESTOCK_POLICIES,
        note="four_mixed plus 25% of each zone's s-tier stock every 30 s (T22 hook).",
    ),
}

SENSITIVITY_BASELINE = "four_mixed"

# (label, attribute, base value, why it is in this list)
SENSITIVITY_PARAMETERS: tuple[tuple[str, str, float, str], ...] = (
    ("radius_exponent", "radius_exponent", 1.0 / 3.0,
     "volume->radius shape; 1/3 is the documented sphere mapping"),
    ("units_to_max_diameter", "units_to_max_diameter", 1356.0,
     "how much placeholder value the 0.30->2.20 m span costs"),
    ("absorb_size_ratio", "absorb_size_ratio", 1.0,
     "item pickup gate vs ball diameter"),
    ("absorb_items_per_s", "absorb_items_per_s", 3.0,
     "pickup throughput"),
    ("base_speed_mps", "base_speed_mps", 3.0,
     "rolling speed"),
    ("bank_cycle_s", "bank_cycle_s", 45.0,
     "anchor rotation period"),
    ("bank_service_s", "bank_service_s", 0.5,
     "deposit dwell"),
    ("respawn_delay_s", "respawn_delay_s", 3.0,
     "KO downtime"),
    ("respawn_protection_s", "respawn_protection_s", 2.0,
     "post-respawn immunity"),
    ("swallow_diameter_ratio", "swallow_diameter_ratio", 1.35,
     "PVP swallow threshold"),
    ("steal_fraction", "steal_fraction", 0.25,
     "attacker share of victim unbanked volume"),
    ("magnet_duration_s", "magnet_duration_s", 4.0,
     "magnet uptime"),
    ("magnet_absorb_multiplier", "magnet_absorb_multiplier", 2.0,
     "magnet throughput"),
    ("rocket_duration_s", "rocket_duration_s", 2.5,
     "rocket uptime"),
    ("rocket_speed_multiplier", "rocket_speed_multiplier", 1.5,
     "rocket speed"),
)


# --------------------------------------------------------------------------
# Running
# --------------------------------------------------------------------------


def run(scenario: Scenario, seed: int) -> RoundResult:
    return simulate_round(scenario.params, scenario.policies, seed=seed)


def totals(result: RoundResult) -> dict[str, float]:
    return {
        "score": sum(p.score_units for p in result.players),
        "peak_m": max(p.peak_diameter_m for p in result.players),
        "absorbed_units": sum(p.absorbed_units for p in result.players),
        "items": sum(p.absorbed_items for p in result.players),
        "deposits": sum(p.deposits for p in result.players),
        "swallows": sum(p.swallowed for p in result.players),
        "downtime_s": sum(p.respawn_downtime_s for p in result.players),
        "stolen_units": sum(p.stolen_units for p in result.players),
        "gap": (max(p.score_units for p in result.players)
                - min(p.score_units for p in result.players)),
    }


def _mean_metrics(metrics: list[dict[str, float]]) -> dict[str, float]:
    keys = metrics[0].keys()
    return {key: sum(m[key] for m in metrics) / len(metrics) for key in keys}


def _spread(metrics: list[dict[str, float]], key: str) -> tuple[float, float]:
    values = [m[key] for m in metrics]
    return min(values), max(values)


def sweep(base: Scenario, seeds: list[int]) -> list[dict[str, object]]:
    """One-at-a-time +/-20% sweep.

    The model is deterministic per seed but chaotic around contested pickups,
    so every variant is averaged over several seeds; single-seed differences
    of a few percent are not a real trend.
    """
    rows: list[dict[str, object]] = []
    base_metrics = [totals(run(base, seed)) for seed in seeds]
    base_mean = _mean_metrics(base_metrics)
    for label, attribute, base_value, why in SENSITIVITY_PARAMETERS:
        for tag, factor in (("-20%", 0.8), ("base", 1.0), ("+20%", 1.2)):
            value = base_value * factor
            params = replace(base.params, **{attribute: value})
            metrics = [totals(simulate_round(params, base.policies, seed=seed))
                       for seed in seeds]
            mean = _mean_metrics(metrics)
            score_min, score_max = _spread(metrics, "score")
            rows.append({
                "parameter": label,
                "variant": tag,
                "value": value,
                "base_value": base_value,
                "seeds": len(seeds),
                "score_total": mean["score"],
                "score_min": score_min,
                "score_max": score_max,
                "score_delta_pct": (
                    (mean["score"] - base_mean["score"]) / base_mean["score"] * 100.0
                    if base_mean["score"] else 0.0),
                "peak_m": mean["peak_m"],
                "items": mean["items"],
                "absorbed_units": mean["absorbed_units"],
                "deposits": mean["deposits"],
                "swallows": mean["swallows"],
                "downtime_s": mean["downtime_s"],
                "gap": mean["gap"],
                "note": why,
            })
    return rows


def scenario_multiseed(scenario: Scenario, seeds: list[int]) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for seed in seeds:
        metric = totals(run(scenario, seed))
        rows.append({"scenario": scenario.name, "seed": seed, **metric})
    return rows


# --------------------------------------------------------------------------
# Plain-text output
# --------------------------------------------------------------------------


def _table(headers: list[str], rows: list[list[object]], align: str = ">") -> str:
    cells = [[str(cell) for cell in row] for row in rows]
    widths = [len(h) for h in headers]
    for row in cells:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(cell))
    fmt = "  ".join(f"{{:{align}{w}}}" for w in widths)
    lines = [fmt.format(*headers)]
    lines.append("  ".join("-" * w for w in widths))
    for row in cells:
        lines.append(fmt.format(*row))
    return "\n".join(lines)


def print_result(scenario: Scenario, result: RoundResult) -> None:
    print(f"\n=== {scenario.name} | seed={result.seed} | {STATUS} ===")
    print(scenario.note)
    headers = ["player", "policy", "score", "peak_m", "items", "deposits",
               "swallowed", "downtime_s", "final_m"]
    rows = []
    for p in result.players:
        rows.append([p.name, p.policy, f"{p.score_units:.0f}",
                     f"{p.peak_diameter_m:.2f}", p.absorbed_items, p.deposits,
                     p.swallowed, f"{p.respawn_downtime_s:.1f}",
                     f"{p.final_diameter_m:.2f}"])
    print(_table(headers, rows))
    print(f"winner: {result.winner}")

    print("\ntier reach times (s; '-' = never reached in this round)")
    reach_headers = ["player"] + [f">={edge:.2f}m" for edge, _ in result.players[0].reach_times_s]
    reach_rows = []
    for p in result.players:
        reach_rows.append([p.name] + [("-" if seconds is None else f"{seconds:.1f}")
                                      for _, seconds in p.reach_times_s])
    print(_table(reach_headers, reach_rows))

    print("\ngrowth curve (ball diameter m at the end of each 15 s bucket)")
    growth_headers = ["bucket"] + [p.name for p in result.players]
    growth_rows = []
    for i, seconds in enumerate(result.bucket_seconds):
        growth_rows.append([f"{seconds:.0f}s"] + [f"{p.growth_curve_m[i]:.2f}"
                                                  for p in result.players])
    print(_table(growth_headers, growth_rows, align=">"))

    print("\ntop time sinks (sum over players, seconds)")
    totals_by_sink = {key: 0.0 for key in TIME_SINKS}
    for p in result.players:
        for key, seconds in p.time_sinks:
            totals_by_sink[key] += seconds
    ranked = sorted(totals_by_sink.items(), key=lambda kv: -kv[1])
    print("  " + ", ".join(f"{key}={seconds:.1f}" for key, seconds in ranked if seconds > 0.05))


def print_sweep(rows: list[dict[str, object]], base: Scenario) -> None:
    seeds = int(rows[0]["seeds"]) if rows else 0
    print(f"\n=== sensitivity: one parameter +/-20%, baseline {base.name}, "
          f"mean of {seeds} seeds ===")
    grouped: dict[str, list[dict[str, object]]] = {}
    for row in rows:
        grouped.setdefault(str(row["parameter"]), []).append(row)
    headers = ["parameter", "base", "-20% score", "+20% score", "worst dscore%",
               "swallows(-/base/+)", "gap(-/base/+)"]
    table_rows = []
    for parameter, variants in grouped.items():
        by_tag = {str(v["variant"]): v for v in variants}
        base_row = by_tag["base"]
        minus = by_tag["-20%"]
        plus = by_tag["+20%"]
        worst = max(abs(float(minus["score_delta_pct"])),
                    abs(float(plus["score_delta_pct"])))
        table_rows.append([
            parameter,
            f"{float(base_row['value']):.4g}",
            f"{float(minus['score_total']):.0f} ({float(minus['score_delta_pct']):+.0f}%)",
            f"{float(plus['score_total']):.0f} ({float(plus['score_delta_pct']):+.0f}%)",
            f"{worst:.0f}%",
            f"{float(minus['swallows']):.1f}/{float(base_row['swallows']):.1f}/{float(plus['swallows']):.1f}",
            f"{float(minus['gap']):.0f}/{float(base_row['gap']):.0f}/{float(plus['gap']):.0f}",
        ])
    table_rows.sort(key=lambda row: -float(str(row[4]).rstrip("%")))
    print(_table(headers, table_rows))
    print("worst dscore% = larger |score change| of the two variants; score is the "
          "sum of all players' banked units, averaged over seeds. gap is the "
          "winner-loser score spread (mean over seeds).")


# --------------------------------------------------------------------------
# File output
# --------------------------------------------------------------------------


def write_scenario_csv(rows: list[dict[str, object]], path: Path) -> None:
    fields = ["scenario", "seed", "player", "policy", "spawn_zone", "score",
              "peak_m", "final_m", "items", "absorbed_units", "deposits",
              "deposit_units", "swallows", "swallowed", "stolen_units",
              "downtime_s"]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(fields)
        for row in rows:
            writer.writerow([row[field] for field in fields])


def write_growth_csv(rows: list[tuple[str, RoundResult]], path: Path) -> None:
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["scenario", "seed", "player", "bucket_s", "diameter_m"])
        for scenario, result in rows:
            for player in result.players:
                for seconds, diameter in zip(result.bucket_seconds,
                                             player.growth_curve_m):
                    writer.writerow([scenario, result.seed, player.name,
                                     f"{seconds:.0f}", f"{diameter:.4f}"])


def write_time_sink_csv(rows: list[tuple[str, RoundResult]], path: Path) -> None:
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["scenario", "player", "category", "seconds"])
        for scenario, result in rows:
            for player in result.players:
                for category, seconds in player.time_sinks:
                    writer.writerow([scenario, player.name, category,
                                     f"{seconds:.3f}"])


def write_sensitivity_csv(rows: list[dict[str, object]], path: Path) -> None:
    fields = ["parameter", "variant", "value", "base_value", "seeds",
              "score_total", "score_min", "score_max", "score_delta_pct",
              "peak_m", "items", "absorbed_units", "deposits", "swallows",
              "downtime_s", "gap", "note"]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(fields)
        for row in rows:
            writer.writerow([row[field] for field in fields])


def _md_table(headers: list[str], rows: list[list[str]]) -> str:
    lines = ["| " + " | ".join(headers) + " |",
             "|" + "|".join("---" for _ in headers) + "|"]
    for row in rows:
        lines.append("| " + " | ".join(row) + " |")
    return "\n".join(lines)


def write_markdown(path: Path, command: str, seed: int,
                   scenario_rows: list[tuple[Scenario, RoundResult]],
                   scenario_seed_rows: list[dict[str, object]],
                   sweep_rows: list[dict[str, object]]) -> None:
    lines = [
        "# 回合经济模拟报告（自动生成）",
        "",
        f"**状态：`{STATUS}`。** 本报告由 `simulate.py` 生成，是离线设计模型输出，"
        "不是 Unity、网络、物理或真人技术测量。",
        "",
        f"- 命令：`{command}`",
        f"- seed：`{seed}`",
        f"- 模型：`Docs/Design/Gameplay/round_model.py`（地图快照 21 区 / 452 件 / 1356 价值单位）",
        "",
        "## 场景总表",
        "",
    ]
    summary_rows = []
    for scenario, result in scenario_rows:
        for p in result.players:
            t2 = p.reach_times_s[1][1]
            summary_rows.append([
                scenario.name, p.name, p.policy, f"{p.score_units:.0f}",
                f"{p.peak_diameter_m:.2f}", str(p.absorbed_items),
                str(p.deposits), str(p.swallowed),
                f"{p.respawn_downtime_s:.1f}",
                "-" if t2 is None else f"{t2:.1f}",
            ])
    lines.append(_md_table(
        ["场景", "玩家", "策略", "分数", "峰值球径 m", "拾取件数", "交分次数",
         "被吞次数", "复活停机 s", "≥0.95m 时刻 s"], summary_rows))
    lines += ["", "## 15 s 成长曲线（球径 m）", ""]
    for scenario, result in scenario_rows:
        headers = ["bucket"] + [p.name for p in result.players]
        rows = []
        for i, seconds in enumerate(result.bucket_seconds):
            rows.append([f"{seconds:.0f}s"] + [f"{p.growth_curve_m[i]:.2f}"
                                               for p in result.players])
        lines += [f"### {scenario.name}", "", _md_table(headers, rows), ""]

    if scenario_seed_rows:
        grouped_seeds: dict[str, list[dict[str, object]]] = {}
        for row in scenario_seed_rows:
            grouped_seeds.setdefault(str(row["scenario"]), []).append(row)
        seed_table = []
        for scenario, variants in grouped_seeds.items():
            scores = [float(v["score"]) for v in variants]
            items = [float(v["items"]) for v in variants]
            swallows = [float(v["swallows"]) for v in variants]
            deposits = [float(v["deposits"]) for v in variants]
            seed_table.append([
                scenario,
                f"{sum(scores) / len(scores):.0f}",
                f"{min(scores):.0f}-{max(scores):.0f}",
                f"{sum(items) / len(items):.0f}",
                f"{sum(swallows) / len(swallows):.1f}",
                f"{sum(deposits) / len(deposits):.1f}",
            ])
        seeds = ", ".join(str(int(v["seed"])) for v in next(iter(grouped_seeds.values())))
        lines += ["", "## 多种子均值", "",
                  f"种子：{seeds}；总分是全场合计，范围给出种子之间的波动。", "",
                  _md_table(["场景", "总分均值", "总分范围", "拾取件数均值",
                             "被吞均值", "交分均值"], seed_table)]

    lines += ["## 时间去向（各场景合计，秒）", ""]
    sink_rows = []
    for scenario, result in scenario_rows:
        totals_by_sink = {key: 0.0 for key in TIME_SINKS}
        for p in result.players:
            for key, seconds in p.time_sinks:
                totals_by_sink[key] += seconds
        ranked = sorted(totals_by_sink.items(), key=lambda kv: -kv[1])[:3]
        sink_rows.append([scenario.name] + [f"{k} {v:.1f}" for k, v in ranked])
    lines.append(_md_table(["场景", "第 1", "第 2", "第 3"], sink_rows))

    lines += ["", f"## 参数 ±20% 敏感性（baseline `{SENSITIVITY_BASELINE}`）", ""]
    grouped: dict[str, list[dict[str, object]]] = {}
    for row in sweep_rows:
        grouped.setdefault(str(row["parameter"]), []).append(row)
    sens_rows = []
    for parameter, variants in grouped.items():
        by_tag = {str(v["variant"]): v for v in variants}
        base_row = by_tag["base"]
        minus, plus = by_tag["-20%"], by_tag["+20%"]
        worst = max(abs(float(minus["score_delta_pct"])),
                    abs(float(plus["score_delta_pct"])))
        sens_rows.append([
            parameter,
            f"{float(base_row['value']):.4g}",
            f"{float(minus['score_total']):.0f}",
            f"{float(base_row['score_total']):.0f}",
            f"{float(plus['score_total']):.0f}",
            f"{worst:.0f}%",
            f"{float(minus['gap']):.0f}/{float(base_row['gap']):.0f}/{float(plus['gap']):.0f}",
            str(base_row["note"]),
        ])
    sens_rows.sort(key=lambda row: -float(str(row[5]).rstrip("%")))
    lines.append(_md_table(
        ["参数", "原型值", "-20% 总分", "base 总分", "+20% 总分", "最大|Δ|",
         "分差 -/base/+", "含义"], sens_rows))
    lines += [
        "",
        "## 读法",
        "",
        "- 总分 = 全场所有玩家已交存单位之和；不是单人成绩。",
        "- 被吞次数 = 被吞事件数（每名受害者计一次）。",
        "- 曲线在某 15 s 桶突然掉回 0.30 m 表示一次交分或一次被吞后的重生。",
        "- 模型假设：玩家在区域内以固定吞吐拾取、按球径门槛过滤；区域库存共享；"
        "被吞/交分后回到起始球径。详见 `README.md`。",
        "",
        f"**{STATUS}**：任何数字都不得作为平衡 PASS 或实测证据。",
    ]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def _parse_seeds(text: str) -> list[int]:
    seeds = [int(part.strip()) for part in text.split(",") if part.strip()]
    if not seeds:
        raise SystemExit("empty seed list")
    return seeds


def print_multiseed(rows: list[dict[str, object]]) -> None:
    grouped: dict[str, list[dict[str, object]]] = {}
    for row in rows:
        grouped.setdefault(str(row["scenario"]), []).append(row)
    print(f"\n=== scenario means over {len(next(iter(grouped.values())))} seeds ===")
    table_rows = []
    for scenario, variants in grouped.items():
        scores = [float(v["score"]) for v in variants]
        table_rows.append([
            scenario,
            f"{sum(scores) / len(scores):.0f}",
            f"{min(scores):.0f}-{max(scores):.0f}",
            f"{sum(float(v['items']) for v in variants) / len(variants):.0f}",
            f"{sum(float(v['swallows']) for v in variants) / len(variants):.1f}",
            f"{sum(float(v['deposits']) for v in variants) / len(variants):.1f}",
            f"{sum(float(v['downtime_s']) for v in variants) / len(variants):.1f}",
        ])
    print(_table(["scenario", "score mean", "score range", "items mean",
                  "swallows mean", "deposits mean", "downtime mean s"], table_rows))


def write_multiseed_csv(rows: list[dict[str, object]], path: Path) -> None:
    fields = ["scenario", "seed", "score", "peak_m", "absorbed_units", "items",
              "deposits", "swallows", "downtime_s", "stolen_units"]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(fields)
        for row in rows:
            writer.writerow([f"{row[field]:.2f}" if isinstance(row[field], float)
                             else row[field] for field in fields])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--scenario", action="append", choices=sorted(SCENARIOS),
                        help="scenario to run; repeatable, default all")
    parser.add_argument("--seed", type=int, default=7,
                        help="seed for the printed single-run tables (default 7)")
    parser.add_argument("--sweep-seeds", default="1,2,3,4,5",
                        help="comma-separated seeds averaged by the sensitivity sweep")
    parser.add_argument("--scenario-seeds", default="1,2,3,4,5",
                        help="comma-separated seeds for the multi-seed scenario summary")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT,
                        help=f"report directory (default: {DEFAULT_OUT})")
    parser.add_argument("--dt", type=float, default=None,
                        help="simulation step in seconds (default: model value 0.25)")
    parser.add_argument("--no-sweep", action="store_true",
                        help="skip the +/-20%% sensitivity sweep")
    parser.add_argument("--list", action="store_true", help="list scenarios and exit")
    args = parser.parse_args(argv)

    if args.list:
        for name, scenario in SCENARIOS.items():
            print(f"{name:14s} {scenario.note}")
        return 0

    names = args.scenario or list(SCENARIOS)
    scenario_rows: list[tuple[Scenario, RoundResult]] = []
    for name in names:
        scenario = SCENARIOS[name]
        params = scenario.params
        if args.dt is not None:
            params = replace(params, dt_s=args.dt)
            scenario = replace(scenario, params=params)
        result = run(scenario, args.seed)
        scenario_rows.append((scenario, result))
        print_result(scenario, result)

    sweep_rows: list[dict[str, object]] = []
    if not args.no_sweep:
        base = SCENARIOS[SENSITIVITY_BASELINE]
        if args.dt is not None:
            base = replace(base, params=replace(base.params, dt_s=args.dt))
        sweep_rows = sweep(base, _parse_seeds(args.sweep_seeds))
        print_sweep(sweep_rows, base)

    scenario_seed_rows: list[dict[str, object]] = []
    for name in names:
        scenario = SCENARIOS[name]
        if args.dt is not None:
            scenario = replace(scenario, params=replace(scenario.params, dt_s=args.dt))
        scenario_seed_rows.extend(
            scenario_multiseed(scenario, _parse_seeds(args.scenario_seeds)))
    print_multiseed(scenario_seed_rows)

    args.out.mkdir(parents=True, exist_ok=True)
    write_scenario_csv([{
        "scenario": scenario.name, "seed": result.seed, "player": p.name,
        "policy": p.policy, "spawn_zone": p.spawn_zone,
        "score": f"{p.score_units:.1f}", "peak_m": f"{p.peak_diameter_m:.4f}",
        "final_m": f"{p.final_diameter_m:.4f}", "items": p.absorbed_items,
        "absorbed_units": f"{p.absorbed_units:.1f}", "deposits": p.deposits,
        "deposit_units": f"{p.deposit_units:.1f}", "swallows": p.swallows,
        "swallowed": p.swallowed, "stolen_units": f"{p.stolen_units:.1f}",
        "downtime_s": f"{p.respawn_downtime_s:.2f}",
    } for scenario, result in scenario_rows for p in result.players],
        args.out / "scenario_summary.csv")
    write_growth_csv([(scenario.name, result) for scenario, result in scenario_rows],
                     args.out / "growth_curves.csv")
    write_time_sink_csv([(scenario.name, result) for scenario, result in scenario_rows],
                        args.out / "time_sinks.csv")
    if sweep_rows:
        write_sensitivity_csv(sweep_rows, args.out / "sensitivity.csv")
    write_multiseed_csv(scenario_seed_rows, args.out / "scenario_multiseed.csv")
    write_markdown(
        args.out / "round_report.md",
        " ".join([Path(sys.argv[0]).name] + (argv if argv is not None else sys.argv[1:])),
        args.seed, scenario_rows, scenario_seed_rows, sweep_rows)

    print(f"\nwrote {args.out}/scenario_summary.csv")
    print(f"wrote {args.out}/growth_curves.csv")
    print(f"wrote {args.out}/time_sinks.csv")
    print(f"wrote {args.out}/scenario_multiseed.csv")
    if sweep_rows:
        print(f"wrote {args.out}/sensitivity.csv")
    print(f"wrote {args.out}/round_report.md")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
