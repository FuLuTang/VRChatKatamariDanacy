#!/usr/bin/env python3
"""Deterministic model of "how much does the ball grow per absorbed item?".

Three questions, one module, stdlib only:

1. CURRENT (this repository): the integer-unit ledger maps confirmed units to one
   physical sphere.  Parameters are the ones the StoryMode scene actually
   serializes (``baselineVolumeUnits = 3.4390244``, ``cubicMetersPerVolumeUnit =
   0.0041``, ``maxCoreRadiusWorldM = 1.1``), so a "1 unit" absorb is one acorn.
2. ORIGINAL (Katamari Damacy, from public data-mining, no decompilation): the
   documented mechanism is ``V += ratio(D) * V_item`` and the gate is
   ``V_ball >= 10 * V_item``; the per-mission ratio curve is piecewise linear.
3. LEVEL 1 trace: the greedy absorb order over
   ``Docs/Design/Levels/story_01_bench_size/placement.json``, which shows what a
   real play session's growth curve and silhouette look like.

Everything here is offline design support.  It is not a Unity run: no frame
timing, no camera, no player.  The geometry identities it relies on are:

    V = (baseline + confirmed) * cubicMetersPerUnit      (SizeModelMath.TryDerive)
    r = cbrt(3V / 4pi)                                   (sphere, metres)
    D_physical = 2r, D_semantic = 2r * semanticPerCore   (both ratios are 1 today)
    D_visual   = 2 * 0.95 * r                            (CONTACT_MODEL_V2 4A.2,
                                                          NOT used by BallView today)

Run:  python3 Docs/Design/BallPresentation/visual_growth/visual_growth_model.py
      python3 Docs/Design/BallPresentation/visual_growth/visual_growth_model.py \
          --csv /tmp/level1_absorb_growth.csv

The CSV goes to stdout unless --csv is given, so the module never writes into
the repository by accident.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import math
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[3]
LEVEL_ONE = REPO_ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"

# --------------------------------------------------------------------------- #
# Current parameters.  Source of truth: the serialized fields of the motor in
# VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity
# (component 1524211376) and KatamariBallMotor.cs defaults.
# --------------------------------------------------------------------------- #
BASELINE_VOLUME_UNITS = 3.4390244
CUBIC_METERS_PER_UNIT = 0.0041
MAX_CORE_RADIUS_M = 1.1
SEMANTIC_PER_CORE_DIAMETER = 1.0
COLLISION_PER_CORE_RADIUS = 1.0

# Human-visible design value from Docs/Design/BallMotor/CONTACT_MODEL_V2.md 4A.2.
# It is NOT applied today: BallView scales the core mesh by the semantic diameter.
VISUAL_CORE_PER_PHYSICAL = 0.95

# PickupAbsorbMath gate: an item is absorbable when V_item <= V_ball / 10.
ABSORB_VOLUME_RATIO = 10.0

# FollowRigMath eye point, the only thing between the ball and the player's
# perception: distance from the ball centre is (back, up) with both terms linear
# in the radius, so the camera pulls back exactly as the ball grows.
EYE_BASE_UP_M = 0.55
EYE_UP_PER_RADIUS = 2.4
EYE_BASE_BACK_M = 0.75
EYE_BACK_PER_RADIUS = 2.2

# A diameter change below this fraction of the diameter is treated as "not
# readable" in this document's tables.  1 % is already generous: at 1 m viewing
# distance a 1 % diameter change is about 2-3 screen pixels.
READABLE_RELATIVE_CHANGE = 1e-2


# --------------------------------------------------------------------------- #
# 1. Current mapping
# --------------------------------------------------------------------------- #
def volume_units_for_diameter(diameter_m: float) -> float:
    """TOTAL units (baseline + confirmed) whose physical sphere has this diameter."""
    volume_m3 = math.pi / 6.0 * diameter_m**3
    return volume_m3 / CUBIC_METERS_PER_UNIT


def confirmed_units_for_diameter(diameter_m: float) -> float:
    """CONFIRMED units (ledger value, baseline subtracted) for this diameter."""
    return volume_units_for_diameter(diameter_m) - BASELINE_VOLUME_UNITS


def core_radius_for_units(confirmed_units: float) -> float:
    """Physical core radius, cap included (SizeModelMath.TryDerive, float side)."""
    total = BASELINE_VOLUME_UNITS + max(0.0, float(confirmed_units))
    radius = (3.0 * total * CUBIC_METERS_PER_UNIT / (4.0 * math.pi)) ** (1.0 / 3.0)
    return min(radius, MAX_CORE_RADIUS_M)


def diameter_for_units(confirmed_units: float) -> float:
    """Semantic diameter (== physical diameter today, both ratios are 1)."""
    return 2.0 * core_radius_for_units(confirmed_units) * SEMANTIC_PER_CORE_DIAMETER


def visual_diameter_for_units(confirmed_units: float) -> float:
    """What CONTACT_MODEL_V2 4A.2 says the core mesh should be scaled to."""
    return 2.0 * core_radius_for_units(confirmed_units) * VISUAL_CORE_PER_PHYSICAL


def delta_diameter(units_added: float, diameter_before_m: float, capped: bool = True) -> float:
    """Absolute diameter change of one absorb, metres.

    ``capped=False`` ignores the presentation cap, which is only useful to show
    how small the change is *just below* the cap.
    """
    confirmed_before = confirmed_units_for_diameter(diameter_before_m)
    if capped:
        return diameter_for_units(confirmed_before + units_added) - diameter_for_units(confirmed_before)
    total_before = BASELINE_VOLUME_UNITS + confirmed_before
    raw_before = 2.0 * (3.0 * total_before * CUBIC_METERS_PER_UNIT / (4.0 * math.pi)) ** (1.0 / 3.0)
    raw_after = 2.0 * (
        3.0 * (total_before + units_added) * CUBIC_METERS_PER_UNIT / (4.0 * math.pi)
    ) ** (1.0 / 3.0)
    return raw_after - raw_before


def relative_growth(units_added: float, diameter_before_m: float, capped: bool = True) -> float:
    """Diameter change as a fraction of the diameter before the absorb."""
    before = diameter_before_m
    return delta_diameter(units_added, before, capped=capped) / before


# --------------------------------------------------------------------------- #
# 2. What the player can actually see: the ball is viewed through the follow rig
# --------------------------------------------------------------------------- #
def eye_distance_for_radius(radius_m: float) -> float:
    """Distance from the ball centre to the follow-rig eye, metres."""
    back = EYE_BASE_BACK_M + EYE_BACK_PER_RADIUS * radius_m
    up = EYE_BASE_UP_M + EYE_UP_PER_RADIUS * radius_m
    return math.hypot(back, up)


def apparent_relative_growth(units_added: float, diameter_before_m: float) -> float:
    """Change of the ball's apparent angular size, camera pull-back included.

    Small-angle approximation ``apparent ~ D / distance(eye, centre)``.  This is
    the number a player could actually notice, as opposed to ``relative_growth``.
    """
    r_before = 0.5 * diameter_before_m
    confirmed_before = confirmed_units_for_diameter(diameter_before_m)
    d_after = diameter_for_units(confirmed_before + units_added)
    r_after = 0.5 * d_after
    before = diameter_before_m / eye_distance_for_radius(r_before)
    after = d_after / eye_distance_for_radius(r_after)
    return after / before - 1.0


def is_readable(relative_change: float) -> bool:
    return abs(relative_change) >= READABLE_RELATIVE_CHANGE


def readable_until_diameter(units_added: float) -> float:
    """Diameter above which one absorb of this many units is below 1 % (inf if never).

    Solves ``(1 + n/T)^(1/3) = 1 + READABLE_RELATIVE_CHANGE`` for the total units T
    and converts T to a diameter.  A value above the presentation cap means the
    ball is capped before the change becomes unreadable.
    """
    factor = (1.0 + READABLE_RELATIVE_CHANGE) ** 3 - 1.0
    total_units = units_added / factor
    return (6.0 * total_units * CUBIC_METERS_PER_UNIT / math.pi) ** (1.0 / 3.0)


# --------------------------------------------------------------------------- #
# 3. The original's documented curve (public data mining; no decompilation)
# --------------------------------------------------------------------------- #
# Growth-document knots, (diameter_m, volume_ratio_added).  Piecewise linear in
# DIAMETER between knots, held constant outside them.  MAS5 starts at 10 cm and
# caps at 3 m, which is the closest documented mission to our 0.30 -> 2.20 m
# level; MAS4 is kept as a sensitivity case because it decays much earlier.
ORIGINAL_MAS5 = ((0.10, 0.80), (1.50, 0.80), (2.00, 0.40), (2.50, 0.16), (3.00, 0.08))
ORIGINAL_MAS4 = (
    (0.10, 0.80), (0.50, 0.80), (1.00, 0.48), (1.10, 0.32),
    (1.25, 0.12), (1.30, 0.08), (1.35, 0.00),
)


def original_ratio(diameter_m: float, knots=ORIGINAL_MAS5) -> float:
    """Fraction of an absorbed item's full volume the original adds at this size."""
    if diameter_m <= knots[0][0]:
        return knots[0][1]
    for (d0, f0), (d1, f1) in zip(knots, knots[1:]):
        if diameter_m <= d1:
            span = d1 - d0
            if span <= 0.0:
                return f1
            t = (diameter_m - d0) / span
            return f0 + (f1 - f0) * t
    return knots[-1][1]


def original_per_item_relative(
    ratio: float, item_volume_over_ball: float = 1.0 / ABSORB_VOLUME_RATIO
) -> float:
    """Exact per-item relative diameter change: (1 + ratio*V_item/V_ball)^(1/3) - 1."""
    return (1.0 + ratio * item_volume_over_ball) ** (1.0 / 3.0) - 1.0


def original_gate_relative(diameter_m: float, knots=ORIGINAL_MAS5) -> float:
    """Largest possible per-item change: an item exactly at the 10x volume gate."""
    return original_per_item_relative(original_ratio(diameter_m, knots))


def original_typical_relative(
    diameter_m: float, item_diameter_ratio: float = 0.35, knots=ORIGINAL_MAS5
) -> float:
    """Per-item change for an item whose length is a fraction of the ball's diameter.

    The original's item stock is dominated by objects far smaller than the gate:
    MAS1's median absorbable item needs a 4.2 cm ball while the ball is 5 cm, so a
    typical early item is ~0.3-0.4 of the ball's diameter even at its own pickup
    size.  This column is the honest "one ordinary item" number.
    """
    volume_ratio = (item_diameter_ratio**3) / ABSORB_VOLUME_RATIO
    return original_per_item_relative(original_ratio(diameter_m, knots), volume_ratio)


# --------------------------------------------------------------------------- #
# 4. Level 1: greedy absorb trace
# --------------------------------------------------------------------------- #
@dataclass(frozen=True)
class LevelProp:
    item_id: str
    value_units: int
    required_diameter_m: float
    half_longest_edge_m: float
    aabb_volume_m3: float


@dataclass(frozen=True)
class AbsorbRow:
    index: int
    item_id: str
    value_units: int
    confirmed_units: float
    diameter_physical_m: float
    diameter_visual_m: float
    delta_diameter_m: float
    relative_growth: float
    apparent_growth: float
    core_radius_m: float
    shell_radius_m: float
    visible_core_fraction: float
    swallowed_props: int
    swallowed_fraction: float


def load_level(path: Path = LEVEL_ONE) -> list[LevelProp]:
    """Read one level's props as the model needs them (absorbable props only)."""
    data = json.loads(path.read_text(encoding="utf-8"))
    props: list[LevelProp] = []
    for raw in data["props"]:
        if not raw.get("absorbable", True):
            continue
        aabb = raw["aabb"]
        props.append(
            LevelProp(
                item_id=raw["itemId"],
                value_units=int(raw["valueUnits"]),
                required_diameter_m=float(raw["absorbDiameterReq"]),
                half_longest_edge_m=0.5 * max(aabb),
                aabb_volume_m3=aabb[0] * aabb[1] * aabb[2],
            )
        )
    return props


def simulate_greedy(props: list[LevelProp]) -> list[AbsorbRow]:
    """Absorb in "smallest value first, ties by id" order, gate by required diameter.

    The rule is deterministic and needs no player path: at each step the ball may
    absorb any prop whose own required diameter is at most the ball's current
    diameter, and it takes the cheapest such prop.  That is the fastest way a
    player could possibly grow (it never rolls to a prop it cannot eat), so the
    resulting curve is a lower bound on the number of absorbs needed.
    """
    remaining = sorted(props, key=lambda p: (p.value_units, p.item_id))
    rows: list[AbsorbRow] = []
    confirmed = 0.0
    # (attach radius, radial half extent) per attached prop, frozen at attach time.
    attached: list[tuple[float, float]] = []
    index = 0
    while remaining:
        diameter = diameter_for_units(confirmed)
        eatable = [p for p in remaining if p.required_diameter_m <= diameter + 1e-12]
        if not eatable:
            break
        prop = eatable[0]
        remaining.remove(prop)
        before = diameter
        confirmed += prop.value_units
        index += 1
        after = diameter_for_units(confirmed)
        radius = 0.5 * after
        attached.append((0.5 * before, prop.half_longest_edge_m))
        outer = [r_attach + half for r_attach, half in attached]
        shell = max([radius] + outer)
        swallowed = sum(1 for value in outer if value <= radius + 1e-12)
        rows.append(
            AbsorbRow(
                index=index,
                item_id=prop.item_id,
                value_units=prop.value_units,
                confirmed_units=confirmed,
                diameter_physical_m=after,
                diameter_visual_m=2.0 * radius * VISUAL_CORE_PER_PHYSICAL,
                delta_diameter_m=after - before,
                relative_growth=(after - before) / before,
                apparent_growth=apparent_relative_growth(prop.value_units, before),
                core_radius_m=radius,
                shell_radius_m=shell,
                visible_core_fraction=radius / shell if shell > 0.0 else 1.0,
                swallowed_props=swallowed,
                swallowed_fraction=swallowed / len(attached),
            )
        )
    return rows


# --------------------------------------------------------------------------- #
# 5. Summaries (ASCII, no matplotlib)
# --------------------------------------------------------------------------- #
CURRENT_SIZES = (0.30, 0.60, 1.10, 2.20)
SAMPLE_UNITS = (1, 4, 100)


def current_table() -> str:
    """Summary 1: the current per-absorb table."""
    lines = [
        "CURRENT: rel = diameter change / diameter, app = after camera pull-back",
        "readable line (1 % of the diameter) is reached at: "
        + ", ".join(
            f"{n}u -> {readable_until_diameter(n):.3f} m" for n in SAMPLE_UNITS
        ),
        "  d_before  units   d_after     delta_mm      rel        app     readable",
    ]
    for diameter in CURRENT_SIZES:
        for units in SAMPLE_UNITS:
            delta = delta_diameter(units, diameter)
            rel = relative_growth(units, diameter)
            app = apparent_relative_growth(units, diameter)
            lines.append(
                f"  {diameter:8.2f}  {units:5d}  {diameter + delta:8.4f}"
                f"  {delta * 1000.0:10.4f}  {rel:9.5f}  {app:9.5f}   {is_readable(rel)!s:5s}"
            )
    return "\n".join(lines)


def original_table() -> str:
    """Summary 2: the original's documented curve at the same sizes."""
    lines = [
        "ORIGINAL: f = volume fraction documented for MAS5 (MAS4 sensitivity)",
        "  diameter  f_MAS5   max_rel(gate)  typical_rel(0.35 D)  f_MAS4  max_rel_MAS4",
    ]
    for diameter in CURRENT_SIZES:
        f5 = original_ratio(diameter, ORIGINAL_MAS5)
        f4 = original_ratio(diameter, ORIGINAL_MAS4)
        lines.append(
            f"  {diameter:8.2f}  {f5:6.2f}  {original_gate_relative(diameter):13.6f}"
            f"  {original_typical_relative(diameter):19.6f}  {f4:6.2f}"
            f"  {original_gate_relative(diameter, ORIGINAL_MAS4):13.6f}"
        )
    return "\n".join(lines)


def level_summary(rows: list[AbsorbRow], bucket: int = 10) -> str:
    """Summary 3: level 1 in buckets of absorbs (growth + silhouette)."""
    lines = [
        "LEVEL 1 greedy absorb (bucket = every 10 absorbs)",
        "  absorb  units  D_phys   D_vis   rel/bucket  app/bucket  shell/core  swallowed",
    ]
    lines.append(
        f"  {0:6d}  {0:5d}  {diameter_for_units(0):7.3f}  "
        f"{visual_diameter_for_units(0):6.3f}  {0.0:10.4f}  {0.0:10.4f}  {1.0:10.3f}  {0.0:9.3f}"
    )
    for start in range(0, len(rows), bucket):
        chunk = rows[start : start + bucket]
        last = chunk[-1]
        first_d = diameter_for_units(chunk[0].confirmed_units - chunk[0].value_units)
        rel = last.diameter_physical_m / first_d - 1.0
        app = apparent_relative_growth(
            sum(row.value_units for row in chunk),
            first_d,
        )
        lines.append(
            f"  {last.index:6d}  {int(last.confirmed_units):5d}  {last.diameter_physical_m:7.3f}"
            f"  {last.diameter_visual_m:6.3f}  {rel:10.4f}  {app:10.4f}"
            f"  {last.visible_core_fraction:10.3f}  {last.swallowed_fraction:9.3f}"
        )
    return "\n".join(lines)


CSV_FIELDS = (
    "index item_id value_units confirmed_units diameter_physical_m diameter_visual_m "
    "delta_diameter_m relative_growth apparent_growth core_radius_m shell_radius_m "
    "visible_core_fraction swallowed_props swallowed_fraction"
).split()


def csv_text(rows: list[AbsorbRow]) -> str:
    handle = io.StringIO()
    writer = csv.writer(handle)
    writer.writerow(CSV_FIELDS)
    for row in rows:
        writer.writerow([getattr(row, name) for name in CSV_FIELDS])
    return handle.getvalue()


def write_csv(rows: list[AbsorbRow], path: Path) -> Path:
    path.write_text(csv_text(rows), encoding="utf-8")
    return path


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--csv",
        default="-",
        help="write the per-absorb CSV here; '-' (default) prints it to stdout",
    )
    args = parser.parse_args(argv)
    props = load_level()
    rows = simulate_greedy(props)
    print(current_table())
    print()
    print(original_table())
    print()
    print(level_summary(rows))
    print()
    print(f"props={len(props)} absorbs={len(rows)} "
          f"units_total={int(rows[-1].confirmed_units) if rows else 0} "
          f"cap_at_confirmed_units={volume_units_for_diameter(2.2) - BASELINE_VOLUME_UNITS:.1f}")
    if args.csv == "-":
        print()
        print(csv_text(rows), end="")
    else:
        print(f"csv={write_csv(rows, Path(args.csv))}")


if __name__ == "__main__":
    main()
