"""Differential parity runner: original stage data in, gap table out.

Usage (from the repository root):

    python3 Docs/Design/Parity/parity_harness/run_parity.py
    python3 Docs/Design/Parity/parity_harness/run_parity.py \
        --corpus Docs/Design/Parity/parity_harness/corpus_star1.json \
        --ours Docs/Design/Levels/story_01_bench_size/placement.json \
        --out /tmp/parity_gap.md

What it does
------------
For every metric in :data:`LAYERS` it puts two numbers next to each other:

* **original** — read from the corpus (a validated transcription of the
  original's public data mine), or ``None`` when the original value does not
  exist in any public source;
* **ours** — produced by *our* existing Python reference models
  (``contact_v2``, ``shapes``, ``size_model``, ``placement_core``) and by
  reading our own level data, never by a second implementation written here.

Metrics with no Python model on our side are declared ``仅 Unity`` and carry the
C#/scene location instead of a number.  Nothing is measured by running Unity.

Determinism
-----------
Rows come from an ordered list, floats are formatted with ``%.4g``, no clock, no
environment, no network.  Two runs on the same inputs produce byte-identical
output; ``Docs/tests/parity`` pins that.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

REPO_ROOT = Path(__file__).resolve().parents[4]
if str(REPO_ROOT) not in sys.path:  # allow `python3 <this file>` from anywhere
    sys.path.insert(0, str(REPO_ROOT))

from Docs.Design.BallLayers.SizeModel import size_model  # noqa: E402
from Docs.Design.BallMotor.contact_v2 import contact_v2  # noqa: E402
from Docs.Design.BallMotor.contact_v2 import shapes  # noqa: E402
from Docs.Design.Parity.parity_harness.corpus_schema import (  # noqa: E402
    Corpus,
    CorpusError,
    load_corpus,
)
from Tools.levelbuild import placement_core  # noqa: E402


# --------------------------------------------------------------------------- #
# verdict vocabulary (stable strings: the tests and the audit doc quote them)
# --------------------------------------------------------------------------- #
CONSISTENT = "一致"
PARAM = "参数差"
MISSING = "缺机制"
DIFFERENT = "不同机制"
ACCEPTABLE = "可接受偏差"
UNKNOWN = "原作未知"
UNITY_ONLY = "仅 Unity"
NO_MODEL = "无模型"

CLASSES = (CONSISTENT, PARAM, MISSING, DIFFERENT, ACCEPTABLE, UNKNOWN,
           UNITY_ONLY, NO_MODEL)

#: The owner's decisions of 2026-11-15, with the rows they will move.  Kept here
#: so the report states *why* those rows are still red, instead of leaving a
#: reader to guess whether the table already reflects the decision.
DECISIONS = (
    ("D1", "规则/数值 1:1：原版已确证的规则逐条照抄", ("G05", "G06", "G07", "G08", "G09")),
    ("D2", "采纳 0.8 惩罚 + 每关分段曲线 + +0.02 cm + 门槛取整到 mm", ("G06", "G08", "G09")),
    ("D3", "不乘 6，全案真实尺寸；缩放版只作诊断", ("S03", "S04")),
    ("D4", "还原阶段直接用解包物件，不补我们的新物件", ("C01", "C02", "C03", "G10")),
    ("D5", "体积改连续（1 mm³ 定点），HUD/评分用显示单位", ("G06", "S02")),
    ("D6", "起始/目标/上限是每关参数，不是全局常数", ("G01", "G02", "G03")),
    ("D7", "双模式：_ref_mas1 重建关", ("G01", "C03")),
)

LAYERS = {
    "T": "时间与积分",
    "M": "运动与地形",
    "G": "尺寸与成长",
    "C": "内容与行为",
    "L": "关卡规则与呈现",
    "S": "最小尺寸与尺度不变性",
}


# --------------------------------------------------------------------------- #
# our side: constants, each with the file:line that owns it
# --------------------------------------------------------------------------- #
@dataclass(frozen=True)
class Constant:
    value: float
    source: str


# Every number here is a *copy* of a value owned by another document or file;
# the source column is the contract.  If a file moves, fix the line, not the
# number.
#: Our story-mode star thresholds (m).  Kept as a tuple because the *list* is the
#: datum; interpolating it would invent a threshold nobody wrote down.
OUR_STARS: Tuple[float, ...] = (1.25, 1.45, 1.60)
STAR_SOURCE = "repricing_proposal.md §1（1.25/1.45/1.60 m）"

OURS: Dict[str, Constant] = {
    "fixed_step_s": Constant(0.02, "Docs/Design/BallMotor/README.md:295"),
    "max_steps_per_frame": Constant(5, "Docs/Design/BallMotor/README.md:295"),
    "max_speed_mps": Constant(4.0, "Docs/Design/BallMotor/README.md:298"),
    "accel_mps2": Constant(5.0, "Docs/Design/BallMotor/README.md:298"),
    "brake_mps2": Constant(8.0, "Docs/Design/BallMotor/README.md:298"),
    "growth_speed_factor": Constant(0.25, "Docs/Design/BallMotor/README.md:300"),
    "baseline_units": Constant(3.4390244, "KatamariBallMotor.cs:193-197"),
    "c_per_unit": Constant(0.0041, "KatamariBallMotor.cs:193-197"),
    "max_core_radius_m": Constant(1.1, "KatamariBallMotor.cs:195"),
    "absorb_volume_ratio": Constant(10.0, "PickupAbsorbMath.cs AbsorbVolumeRatio（原作的 10×）"),
    "step_ratio": Constant(0.25, "KatamariBallMotor.cs:110"),
    "support_slope_limit_deg": Constant(45.0, "SupportSurfaceMath.cs:480"),
    "theta_ride_deg": Constant(contact_v2.THETA_RIDE_DEG,
                               "contact_v2.py:53 / CONTACT_MODEL_V2 §3.5"),
    "climb_time_s": Constant(contact_v2.CLIMB_TIME_S, "contact_v2.py:65"),
    "v_min_gate": Constant(contact_v2.V_MIN_GATE, "contact_v2.py:62"),
    "k_h": Constant(contact_v2.K_H, "contact_v2.py:71"),
    "kappa": Constant(contact_v2.KAPPA_SUBSTEP, "contact_v2.py:56"),
    "max_substeps": Constant(contact_v2.MAX_SUBSTEPS, "contact_v2.py:57"),
    "contact_skin_m": Constant(contact_v2.CONTACT_SKIN_M, "contact_v2.py:60"),
    "contact_slop_m": Constant(contact_v2.CONTACT_SLOP_M, "contact_v2.py:59"),
    "solver_iterations": Constant(contact_v2.SOLVER_ITERATIONS, "contact_v2.py:58"),
    "v_slide_max_mps": Constant(contact_v2.V_SLIDE_MAX, "contact_v2.py:76"),
    "climb_cooldown_s": Constant(contact_v2.CLIMB_COOLDOWN_S, "contact_v2.py:74"),
    "climb_cap_margin_m": Constant(contact_v2.CLIMB_CAP_MARGIN_M, "contact_v2.py:75"),
    "protrusion_cap_m": Constant(shapes.NEEDLE_PROTRUSION_WORLD_M, "shapes.py:36"),
    "growth_push_per_step_m": Constant(0.05, "CONTACT_MODEL_V2.md §4.2"),
    # `STACK_TOLERANCE_M` (10 mm) is gone: the owner ruled it out on 2026-11-16 - an
    # authored parent pointer is a report, not a geometry gate, and the contact window is
    # now dimensionless (`parent_gap_window`, a fraction of the smaller height), so it is
    # not a metre constant to compare against the ball.  See PARITY_AUDIT divergences row 3.
    "hud_offset_m": Constant(0.0002, "KatamariBallMotor.GetHudDiameterWorldM (+0.02 cm)"),
    "clear_target_m": Constant(1.25,
                               "StoryResultPresenter.star1DiameterM / Docs/Design/StoryMode/ROUND_RULES.md"),
    "star_span_m": Constant(0.35, "repricing_proposal.md §1（1.60 − 1.25）"),
    "ramp_tangential_scale": Constant(1.0, "KatamariBallMotor.rampTangentialGravityScale"),
    "gate_floor_m": Constant(0.001,
                             "placement_core.floor_mm / PickupAbsorbMath.MmFloorEpsilon"
                             "（Req 与球径都向下取整到整 mm）"),
}

#: Absolute-metre constants of our engine whose *ratio to the ball* changes with
#: scale.  These are the ones the scale-invariance check looks at.
ABSOLUTE_CONSTANTS: Tuple[Tuple[str, str, Constant], ...] = (
    ("contact_skin", "接触注册 skin", OURS["contact_skin_m"]),
    ("contact_slop", "解算 slop", OURS["contact_slop_m"]),
    ("growth_push", "逐帧长大推挤上限", OURS["growth_push_per_step_m"]),
    ("protrusion_cap", "长杆突出 cap", OURS["protrusion_cap_m"]),
    ("climb_cap_margin", "到顶判定带宽", OURS["climb_cap_margin_m"]),
    ("support_drop", "支撑掉走容差", Constant(0.01, "Docs/Design/BallMotor/README.md:210")),
)


def _v(name: str) -> float:
    return OURS[name].value


# --------------------------------------------------------------------------- #
# our level: read-only view of the placement data
# --------------------------------------------------------------------------- #
class OursLevel:
    """Our stage as our own data describes it (read-only; nothing is written)."""

    def __init__(self, data: Dict[str, Any], path: Optional[Path] = None) -> None:
        self.data = data
        self.path = path
        level = data.get("level") or {}
        self.id = str(level.get("id", "?"))
        self.start_d = float(level["ballDiameterStart"])
        self.max_d = float(level["ballDiameterMax"])
        self.ground_y = float(level.get("groundY", 0.0))
        self.zones = [z.get("id") for z in (level.get("zones") or [])]
        # Content total in the new currency (derived growth, mm^3); the deprecated
        # valueUnits total is only kept for the migration cross-check.
        expected = level.get("expected") or {}
        self.declared_growth_mm3 = (int(expected["growthMm3"])
                                    if expected.get("growthMm3") is not None else None)
        self.declared_units = (int(expected["valueUnits"])
                               if expected.get("valueUnits") is not None else None)
        self.props = list(data.get("props") or [])
        self.defs = list(data.get("defs") or [])
        self.structures = list(data.get("structures") or [])
        self.items: List[Dict[str, Any]] = []
        catalog = placement_core.def_catalog(data)
        for prop in self.props:
            aabb = tuple(float(x) for x in prop["aabb"])
            volume = aabb[0] * aabb[1] * aabb[2]
            volume_k = placement_core.effective_volume_k(prop, catalog)
            self.items.append({
                "item_id": prop.get("itemId"),
                "def": prop.get("def"),
                "kind": prop.get("kind"),
                "zone": prop.get("zone"),
                "aabb": aabb,
                # The ORIGINAL quantity: V = k * a*b*c (k defaults to 0.70).
                "volume_k": volume_k,
                "volume_m3": volume,
                "original_volume_m3": volume_k * volume,
                "d_req_m": placement_core.absorb_diameter_req(aabb, volume_k),
                "d_req_mm": placement_core.absorb_diameter_req_mm(aabb, volume_k),
                # Unrounded, for "how far past the boundary" diagnostics only.
                "d_req_exact_m": placement_core.absorb_diameter_req_exact(aabb, volume_k),
                "volume_penalty": placement_core.effective_volume_penalty(prop, catalog),
                # The ledger gain one absorb is worth, mm^3 (original form penalty*k*bbox).
                "growth_mm3": placement_core.item_growth_mm3(
                    aabb, volume_k, placement_core.effective_volume_penalty(prop, catalog)),
                # Deprecated (0 once the data is regenerated); cross-check only.
                "value_units": int(prop.get("valueUnits", 0)),
            })

    # -- content -----------------------------------------------------------
    @property
    def count(self) -> int:
        return len(self.items)

    @property
    def type_count(self) -> int:
        return len({item["def"] for item in self.items})

    def absorbable_at(self, diameter_m: float) -> List[Dict[str, Any]]:
        # The original's comparison: whole-millimetre ball vs whole-millimetre Req.
        ball_mm = placement_core.floor_mm(diameter_m)
        return [i for i in self.items if i["d_req_mm"] <= ball_mm]

    def original_volume_absorbable_at(self, diameter_m: float) -> float:
        """Σ V of the absorbable items in the ORIGINAL's volume definition (k·bbox).

        The gate and every content/supply comparison must use the same volume on both
        sides; ``volume_m3`` (the raw bbox) is kept for cross-checks and for the ledger
        side, which is still unit-based until steps C/D.
        """
        return sum(i["original_volume_m3"] for i in self.absorbable_at(diameter_m))

    def growth_total_mm3(self) -> int:
        """Σ growth of every prop (the content total the ledger would receive)."""
        return sum(i["growth_mm3"] for i in self.items)

    def growth_volume_m3(self) -> float:
        """Σ growth in cubic metres (display/analysis only; never accumulated)."""
        return self.growth_total_mm3() / placement_core.MM3_PER_M3

    def ball_volume(self, diameter_m: float) -> float:
        return math.pi / 6.0 * diameter_m ** 3

    def octave_bands(self) -> List[int]:
        """Instances per doubling of the required diameter, from the start."""
        counts: List[int] = []
        low = self.start_d
        while low < self.max_d * (1.0 - 1e-12):
            high = low * 2.0
            counts.append(sum(1 for i in self.items
                              if low <= i["d_req_m"] < high))
            low = high
        return counts


def _ball_volume(diameter_m: float) -> float:
    return math.pi / 6.0 * diameter_m ** 3


def _octave_bands(start_d: float, max_d: float) -> List[Tuple[float, float]]:
    """Half-open ``[lo, hi)`` bands, each one doubling above the start diameter."""
    bands: List[Tuple[float, float]] = []
    low = start_d
    while low < max_d * (1.0 - 1e-12):
        high = min(low * 2.0, max_d)
        bands.append((low, high))
        low = high
    return bands


def corpus_supply(corpus: Corpus, default_penalty: float = 0.8):
    """Original-side effective supply: credited volume / volume needed to reach the cap.

    ``credited = penalty x item volume`` — the original only banks a fraction of
    what it swallows (F5).  The denominator is the volume the ball must gain to
    go from its start diameter to its cap, i.e. what the stage *asks for*.
    """
    start_d, max_d = corpus.start_diameter_m, corpus.max_diameter_m
    need = _ball_volume(max_d) - _ball_volume(start_d)
    credited = 0.0
    missing = 0
    for inst in corpus.instances:
        if corpus.req_pickup_m(inst) > max_d + 1e-12:
            continue
        penalty = corpus.catalogue[inst["mono_idx"]].get("penalty")
        if penalty is None:
            penalty = default_penalty
            missing += 1
        credited += corpus.volume_m3(inst) * float(penalty)
    return (credited / need if need > 0 else float("inf")), missing


def _runway(pairs, start_d: float, max_d: float):
    """Cumulative runway per band: edible-so-far / absorbs still needed.

    At band edge ``hi`` the pool is every instance with ``d_req <= hi`` — the
    ones already eaten to get there are exactly the ones the climb consumed, so
    "edible and not yet eaten" is this count.  What is still needed is the
    remaining volume gap divided by the pool's own average credit (a pool of
    tiny items needs more absorbs, not fewer).  The tightest band wins; a value
    below 1 means the stage cannot be finished on its own content.
    """
    best: Optional[Tuple[float, str]] = None
    parts: List[str] = []
    for _, high in _octave_bands(start_d, max_d):
        pool = [(req, credit) for req, credit in pairs if req <= high + 1e-12]
        if not pool:
            parts.append("%.3g:0/0" % high)
            if best is None or 0.0 < best[0]:
                best = (0.0, "%.3g:0/0" % high)
            continue
        mean_credit = sum(credit for _, credit in pool) / len(pool)
        need = _ball_volume(high) - _ball_volume(start_d)
        needed = int(math.ceil(need / mean_credit)) if mean_credit > 0 else 0
        ratio = (len(pool) / needed) if needed > 0 else float("inf")
        parts.append("%.3g:%.0f/%.0f" % (high, len(pool), needed))
        if best is None or ratio < best[0]:
            best = (ratio, "%.3g:%.0f/%.0f" % (high, len(pool), needed))
    return (best[0] if best else 0.0), "; ".join(parts)


def corpus_starvation(corpus: Corpus, default_penalty: float = 0.8):
    pairs = [(corpus.req_pickup_m(inst),
              corpus.volume_m3(inst)
              * float(corpus.catalogue[inst["mono_idx"]].get("penalty")
                      if corpus.catalogue[inst["mono_idx"]].get("penalty") is not None
                      else default_penalty))
             for inst in corpus.instances]
    return _runway(pairs, corpus.start_diameter_m, corpus.max_diameter_m)


def ours_supply(level: "OursLevel"):
    """Our side at the SAME rule as the corpus: Σ(penalty·k·bbox) over absorbable items."""
    need = _ball_volume(level.max_d) - _ball_volume(level.start_d)
    credited = sum(item["original_volume_m3"] * item["volume_penalty"]
                   for item in level.absorbable_at(level.max_d))
    return (credited / need if need > 0 else float("inf"))


def ours_starvation(level: "OursLevel"):
    pairs = [(item["d_req_m"], item["original_volume_m3"] * item["volume_penalty"])
             for item in level.items]
    return _runway(pairs, level.start_d, level.max_d)


def _ranks(values: Sequence[float]) -> List[float]:
    """Average ranks (1-based), ties share the mean rank."""
    order = sorted(range(len(values)), key=lambda i: values[i])
    ranks = [0.0] * len(values)
    index = 0
    while index < len(order):
        end = index
        while end + 1 < len(order) and values[order[end + 1]] == values[order[index]]:
            end += 1
        average = (index + end) / 2.0 + 1.0
        for position in range(index, end + 1):
            ranks[order[position]] = average
        index = end + 1
    return ranks


def spearman(pairs: Sequence[Tuple[float, float]]) -> Optional[float]:
    """Rank correlation without scipy; ``None`` when it is undefined."""
    if len(pairs) < 3:
        return None
    xs = _ranks([p[0] for p in pairs])
    ys = _ranks([p[1] for p in pairs])
    n = float(len(pairs))
    mean_x = sum(xs) / n
    mean_y = sum(ys) / n
    cov = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
    var_x = sum((x - mean_x) ** 2 for x in xs)
    var_y = sum((y - mean_y) ** 2 for y in ys)
    if var_x <= 0.0 or var_y <= 0.0:
        return None
    return cov / math.sqrt(var_x * var_y)


def ours_zone_stats(level: "OursLevel") -> Dict[str, Any]:
    """The four zone numbers, computed from our own level data."""
    start_d = level.start_d
    ball_start = (level.data.get("level") or {}).get("ballStart")
    if ball_start and len(ball_start) == 3:
        cx, cz = float(ball_start[0]), float(ball_start[2])
    else:
        cx = sum(float(p.get("position", [0, 0, 0])[0]) for p in level.props) / max(1, len(level.props))
        cz = sum(float(p.get("position", [0, 0, 0])[2]) for p in level.props) / max(1, len(level.props))
    pairs = []
    for prop, item in zip(level.props, level.items):
        position = prop.get("position") or [0.0, 0.0, 0.0]
        radius = math.hypot(float(position[0]) - cx, float(position[2]) - cz)
        pairs.append((item["d_req_m"], radius))
    small = sum(1 for item in level.items if 0.01 <= item["d_req_m"] < 0.10)
    edible = len(level.absorbable_at(start_d))
    return {
        "min_ratio": min((item["d_req_m"] for item in level.items), default=0.0) / start_d,
        "eat_now": edible / max(1, level.count),
        "small_share": small / max(1, level.count),
        "spearman": spearman(pairs),
    }


def load_ours_level(path: Path) -> OursLevel:
    return OursLevel(json.loads(Path(path).read_text(encoding="utf-8")), Path(path))


# --------------------------------------------------------------------------- #
# rows
# --------------------------------------------------------------------------- #
@dataclass
class Row:
    id: str
    layer: str
    name: str
    unit: str
    original: Any
    ours: Any
    klass: str
    orig_source: str
    ours_source: str
    meaning: str
    ratio: Optional[float] = None
    note: str = ""

    def delta_text(self) -> str:
        if self.ratio is None or abs(self.ratio - 1.0) <= 1e-2:
            return "—"
        if self.original is not None and self.ours is not None \
                and not isinstance(self.original, str) and not isinstance(self.ours, str):
            if float(self.original) > float(self.ours):
                return "原作 %.2f×" % self.ratio
            return "我方 %.2f×" % self.ratio
        return "—"


def _ratio(original: Any, ours: Any) -> Optional[float]:
    if isinstance(original, bool) or isinstance(ours, bool):
        return None
    if isinstance(original, str) or isinstance(ours, str):
        return None
    if original is None or ours is None:
        return None
    a, b = float(original), float(ours)
    if a == 0.0 or b == 0.0:
        return None
    return max(abs(a), abs(b)) / min(abs(a), abs(b))


def _fmt(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "是" if value else "否"
    if isinstance(value, str):
        # A pipe would split the Markdown cell; the full-width bar reads the same.
        return value.replace("|", "／").replace("\n", " ")
    if isinstance(value, int):
        return str(value)
    return "%.4g" % float(value)


def _fmt_source(text_value: str) -> str:
    return text_value.replace("|", "/").replace("\n", " ")


# --------------------------------------------------------------------------- #
# the metric table
# --------------------------------------------------------------------------- #
def build_rows(corpus: Corpus, ours: OursLevel) -> List[Row]:
    rows: List[Row] = []
    params = corpus.params
    start_d = corpus.start_diameter_m
    max_d = corpus.max_diameter_m

    def add(metric_id: str, name: str, unit: str, original: Any, ours_value: Any,
            klass: str, orig_source: str, ours_source: str, meaning: str,
            note: str = "") -> None:
        layer = LAYERS[metric_id[0]]
        rows.append(Row(metric_id, layer, name, unit, original, ours_value, klass,
                        orig_source, ours_source, meaning,
                        _ratio(original, ours_value), note))

    # A declared parameter gap collapses to 一致 when the numbers actually
    # match, and to 原作未知 / 仅 Unity when one side has no value.  Declaring
    # "PARAM" is a statement about *what kind* of comparison it is, not a
    # verdict that skips the comparison.
    def _settle(row: Row) -> None:
        if row.klass not in (PARAM, MISSING):
            return
        if row.original is None:
            # The missing input is the original's, so the gap is unjudgeable —
            # even when our own side also has nothing to put in the cell.
            row.klass = UNKNOWN
        elif row.ours is None:
            row.klass = UNITY_ONLY
        elif row.ratio is not None and abs(row.ratio - 1.0) <= 1e-2:
            row.klass = CONSISTENT

    # ------------------------------------------------------------------ T --
    fixed_hz = float(corpus.data["timebase"]["fixed_step_hz"])
    ours_hz = 1.0 / _v("fixed_step_s")
    add("T01", "定步频率", "Hz", fixed_hz, ours_hz, PARAM,
        "corpus.timebase.fixed_step_hz ← reroll_datamodel.md §0（IL: DoTick(1/30f)）",
        "Docs/Design/BallMotor/README.md:295 (fixedStepSeconds 0.02)",
        "原作 30 Hz + 帧间插值；我们 50 Hz 累加器。步长越细，单位位移的采样越密，"
        "但时间积分仍是显式欧拉")

    add("T02", "积分与接触求解", "—",
        "原生 DLL（不可见）", "显式欧拉 + 子步 + 最近点推挤（3 次迭代）", UNKNOWN,
        "reroll_datamodel.md §0/§6.1：物理在 PS2KatamariSimulation.dll，无公开符号",
        "contact_v2.py:462 step() / §2.3 推挤",
        "原作的积分阶数与接触常数不可见；我们无法声称‘同阶’，只能说‘不同实现’")

    disp = _v("max_speed_mps") * _v("fixed_step_s")
    radius = ours.start_d / 2.0
    subs = contact_v2.max_substeps((disp, 0.0, 0.0), radius)
    add("T03", "隧穿界（每子步位移/半径）", "r", None,
        round(disp / subs / radius, 4), DIFFERENT,
        "reroll_numbers.md §4：无 CCD、30 Hz、粗碰撞 + 掉出世界救回（[社区实测]）",
        "contact_v2.max_substeps（κ=%.2f, N≤%d）" % (_v("kappa"), _v("max_substeps")),
        "原作靠‘慢 + 粗 + 救回’，我们靠子步采样的 2r 下界；机制不同，"
        "但都把安全边界放在设计里而不是求解器里")

    add("T04", "碰撞形状", "—",
        "碰撞网格 + 每物件包围盒 + vault 点", "球 r_phys + 16 根针尖（OBB 最近点）",
        DIFFERENT,
        "reroll_numbers.md §4（Practice Mod F4/F5）；US7402104B2（等体积盒）",
        "CONTACT_MODEL_V2.md §4A.2 / shapes.py",
        "原作不是解析球；我们把‘凸出物’并入针尖接触。形状差异会直接改变贴墙/上台的手感")

    reach_axis = shapes.pickup_reach((1.0, 0.0, 0.0), radius) / radius
    reach_corner = shapes.pickup_reach((1.0, 1.0, 1.0), radius) / radius
    add("T05", "拾取触发体（轴/角，×r）", "r",
        "Mh 包围盒（实形/朝向未知）+ 体积门", round(reach_corner, 4), DIFFERENT,
        "US7402104B2 Mh；ABSORB/10× 门；PARITY_AUDIT 吸收触发几何行",
        "shapes.pickup_reach / KatamariBallMotor.GetPickupReachWorldM（碰撞球）",
        "我们采用内接等效球，道具侧是自身有向盒；不并入针尖、不随方向扩大。"
        "原作 Mh 实形未知，本项目选择球以保证接触处冻结"
        "（轴 %.2fr、角 %.2fr）" % (reach_axis, reach_corner))

    # ------------------------------------------------------------------ M --
    add("M01", "最大平面速度", "m/s", None, _v("max_speed_mps"), UNKNOWN,
        "reroll_numbers.md §6.2：公开源码无任何速度数值",
        "Docs/Design/BallMotor/README.md:298 (maxSpeedMps 4)",
        "无法校准：我们能证明的是‘我们的上限’，不是‘原作的’")

    add("M02", "加速度 / 刹车", "m/s²", None, _v("accel_mps2"), UNKNOWN,
        "同上（无公开数值）", "Docs/Design/BallMotor/README.md:298 (5 / 8)",
        "同上；原作的‘速度门’只说明存在加速度，不说明大小")

    add("M03", "坡面切向重力（打滑）", "—", "有打滑/失控（[社区实测]）",
        _v("ramp_tangential_scale"), UNKNOWN,
        "/tmp/orig_physics/report.md §3.1（媒体与玩家：斜面打滑、缓坡也吃力）",
        "Docs/Design/BallMotor/BALL_INERTIA.md §4（rampTangentialGravityScale=1，静止 0.3 s 后顺坡）",
        "我们静止 0.3 s 后且坡角 >2° 才顺坡下滑；原作有‘站不住’的证据")

    add("M04", "滚/爬分界角", "deg", None, _v("theta_ride_deg"), UNKNOWN,
        "/tmp/orig_physics/report.md §1.4：70°/90° 都无来源",
        "contact_v2.py:53 (θ_ride 45°)",
        "原作的墙可爬，但角度阈值不存在于任何公开资料；45° 是我方设计值")

    climb_cap = contact_v2.climb_height_cap(radius)
    add("M05", "爬升高度上限 / 球径", "×D", "≈1 个球径（手册定性）",
        round(climb_cap / ours.start_d, 4), ACCEPTABLE,
        "We Love Katamari 手册 ‘walls about the same size as your katamari’（[官方]）",
        "contact_v2.climb_height_cap（k_h=%.1f）" % _v("k_h"),
        "唯一有官方锚点的量：H_cap≈D。我们取 k_h=1.0，与原作口径同量级")

    v_gate = _v("max_speed_mps") * contact_v2.K_INTENT
    climb_v = contact_v2.climb_speed(1.0, v_gate, 90.0, 0.0, climb_cap)
    add("M06", "90° 爬升速度（满推）", "m/s", None, round(climb_v, 4), UNKNOWN,
        "/tmp/orig_physics/report.md §1.4（无速度-角度曲线）",
        "contact_v2.climb_speed（T_climb=%.1f s）" % _v("climb_time_s"),
        "原作只留下‘90° 极慢地蹭’的体感描述，没有数；我们的 0.23 m/s 是设计值")

    add("M07", "爬升是否需要按键", "—", "否（双摇杆前推）", "否（推入即爬）", CONSISTENT,
        "We Love Katamari 手册 + keymap.io OUAK 操作表（[官方]/[社区实测]）",
        "CONTACT_MODEL_V2.md §8 拍板 3",
        "输入语义一致：贴墙前推即爬，不新增按钮")

    add("M08", "回滑与锁定", "—", None,
        "v_slide≤%.1f m/s + 到顶锁定 + %.2f s 冷却" % (_v("v_slide_max_mps"),
                                                     _v("climb_cooldown_s")),
        UNKNOWN,
        "无公开资料（只有‘超过高度就掉回来’的手册口径）",
        "contact_v2.py:337 climb_eligible / §3.3 回滑",
        "‘一次推入只给一次机会’是我方设计，原作未知")

    step_limit = _v("step_ratio") * ours.start_d
    add("M09", "台阶阈值 / 球径", "×D", None, _v("step_ratio"), UNKNOWN,
        "官方只说‘变大能越段差’（[官方]），无数值",
        "KatamariBallMotor.cs:110（stepHeightOverBallDiameter 0.25）；v2 改为 h<r",
        "0.25D 是 v1 的规则，v2 的几何门槛是 0.5D（h<r）。两套都在，标清楚才不会混",
        note="我方 v1 在起始球径上的 stepLimit = %.4g m" % step_limit)

    # ------------------------------------------------------------------ G --
    scale = ours.start_d / start_d
    add("G01", "起始球径", "m", start_d, ours.start_d, PARAM,
        "corpus.parameters.start_diameter_m ← missions.START_DIAMETER=%.4g cm"
        % (start_d * 100.0),
        "placement.json level.ballDiameterStart=%.4g" % ours.start_d,
        "我们开局是原作的 %.2f 倍；任何绝对米制常数在这一档的相对权重都会被放大同样的倍数"
        "（见 S03）" % scale)

    add("G02", "通关目标直径", "m", corpus.clear_diameter_m,
        _v("clear_target_m"), PARAM,
        "missions.CLEAR_DIAMETER=%.4g cm（CLEAR_TYPE=%s）"
        % (corpus.clear_diameter_m * 100.0, params.get("clear_type") or "?"),
        "StoryRoundController.targetDiameterM / Docs/Design/StoryMode/ROUND_RULES.md（%.4g m）"
        % _v("clear_target_m"),
        "目标值相差 %.1f×；" % (_v("clear_target_m") / corpus.clear_diameter_m)
        + "在这个量级上，我们的目标相当于原作里更后期关卡的一段")

    add("G03", "上限直径", "m", max_d, ours.max_d, PARAM,
        "missions.MAX_DIAMETER=%.4g cm" % (max_d * 100.0),
        "placement.json level.ballDiameterMax=%.4g" % ours.max_d,
        "上限差 %.1f×；原作的上限只是包线（目标×%.1f），我们的上限也远高于目标"
        % (ours.max_d / max_d, corpus.clear_diameter_m and max_d / corpus.clear_diameter_m))

    add("G04", "目标 / 上限", "—", corpus.clear_diameter_m / max_d,
        round(_v("clear_target_m") / ours.max_d, 4), PARAM,
        "missions：%.4g/%.4g cm" % (corpus.clear_diameter_m * 100.0, max_d * 100.0),
        "story_01：%.4g/%.4g" % (_v("clear_target_m"), ours.max_d),
        "原作用‘上限=目标×3’留余量；我们只留 1.76×，所以‘冲上限’比原作容易")

    volume_curve = "D=(6V/π)^(1/3)"
    add("G05", "体积→直径曲线", "—", volume_curve + "（+0.02 cm）",
        volume_curve + "（显示 +0.02 cm；门/尺寸模型用未偏置值）", CONSISTENT,
        "raw_growth_mechanism.txt §1（+0.02 cm fudge）",
        "SizeModelMath.TryDerive（D=2·cbrt(3V/4π)）",
        "换算同形；只差一个 0.2 mm 的显示偏置（见 G09）")

    fraction = corpus.growth_fraction(start_d)
    # Ours is the default 0.8; the corpus curve is the original's.  An absent curve is
    # "unknown", not 0.8, so the status is derived instead of hard-coded.
    if fraction is None:
        g06_klass = UNKNOWN
    elif abs(float(fraction) - placement_core.DEFAULT_VOLUME_PENALTY) < 1e-9:
        g06_klass = CONSISTENT
    else:
        g06_klass = PARAM
    add("G06", "单件吸收比例（起始处）", "×V_item",
        None if fraction is None else round(fraction, 4),
        placement_core.DEFAULT_VOLUME_PENALTY, g06_klass,
        "corpus.parameters.growth_curve ← raw_growth_mechanism.txt §3.1",
        "PickupFieldAuthoringRules.TryComputeGrowthMm3（round(penalty·k·a·b·c·1e9)，"
        "penalty 缺省 0.8，逐件 volumePenalty 可覆盖）",
        "已还原（2026-11-16 C+D）：一件的成长 = penalty·k·bbox，缺省 0.8。"
        "原作按球径分段（起点 0.8，末端更低）仍未落，本轮只落缺省值",
        note="原作分段曲线（按球径）尚未实现；本行只比缺省 0.8")

    add("G07", "10× 体积门", "×V_item", float(params["gate_volume_ratio"]),
        _v("absorb_volume_ratio"), CONSISTENT,
        "missions 类型表自洽：πD³/6≈10V（1,449/1,450 行 1 mm 内）；raw_growth §2",
        "PickupAbsorbMath.AbsorbVolumeRatio=10（Req=cbrt(60·k·a·b·c/π)）",
        "门 = 10 倍体积，两边同形同值；体积的定义也已经是原作的 k·bbox（G13）")

    add("G08", "门槛取整", "m", float(params["gate_floor_m"]),
        float(_v("gate_floor_m")),
        CONSISTENT if abs(float(params["gate_floor_m"]) - float(_v("gate_floor_m"))) < 1e-12
        else PARAM,
        "raw_growth_mechanism.txt §2：门槛向下取整到 mm（蚂蚁实际只需 ~8.8×）",
        "placement_core.absorb_diameter_req_mm / PickupAbsorbMath.MmFloorEpsilon"
        "（Req 与球径都取整到整 mm，ε 只修浮点误差）",
        "两边同一口径：Req_mm = floor(1000·cbrt(60·k·abc/π))，可吸 iff "
        "floor(1000·D) ≥ Req_mm。1 mm 在起始球上是 %.1f%% 的直径，只有最小件受它影响"
        % (0.1 / start_d))

    vf = corpus.data.get("volume_factor") or {}
    stage_k = [e.get("volume_factor") for e in corpus.catalogue.values()
               if e.get("volume_factor")]
    median_k = (sorted(stage_k)[len(stage_k) // 2] if stage_k
                else vf.get("median_k") or vf.get("default_k"))
    if median_k is None:
        g13_klass = UNKNOWN
    elif abs(float(median_k) - placement_core.DEFAULT_VOLUME_K) < 1e-9:
        g13_klass = CONSISTENT
    else:
        g13_klass = PARAM
    add("G13", "体积系数 k（Volume = k·bbox）", "×bbox",
        None if median_k is None else round(float(median_k), 4),
        placement_core.DEFAULT_VOLUME_K, g13_klass,
        "corpus.volume_factor / catalogue[].volume_factor ← meta/solid_volume/"
        "fill_factors.csv（默认 0.70；薄片抬到 8–16、蓬松压到 0.05–0.07、镂空家具 0.3–0.5）",
        "placement_core.DEFAULT_VOLUME_K / defs[].volumeK（缺省 0.70）"
        " / PickupAbsorbMath.DefaultVolumeK",
        "原作者手调的系数，不是任何几何体积：默认 k=0.70。门与成长现在都按 k·bbox 算"
        "（成长再乘 penalty 0.8，G06），同一件在两边是同一个体积，长度当量上不再偏严"
        "（此前偏严 (1/0.70)^(1/3)=1.126，与 gateRatio 中位 1.13 吻合）",
        note="原作类型里 %.1f%% 恰好等于 k=0.70（README 报的是 70.7%%；本文用本语料能连上的 %d 型算中位）"
             % (100.0 * (vf.get("exact_default_share") or 0.0), len(stage_k)))

    add("G09", "HUD 直径偏置", "m", float(params["hud_diameter_offset_m"]),
        _v("hud_offset_m"),
        CONSISTENT if abs(float(params["hud_diameter_offset_m"]) - _v("hud_offset_m")) < 1e-12
        else PARAM,
        "raw_growth_mechanism.txt §1：换算后 +0.02 cm（MAS1 显示 5.02）",
        "KatamariBallMotor.GetHudDiameterWorldM（语义直径 + 0.0002 m）",
        "已还原（2026-11-16 C+D）：显示读数 +0.02 cm；门与尺寸模型用未偏置的语义直径，"
        "所以偏置不会移动门槛")

    bands = ours.octave_bands()
    ours_per_octave = ours.count / max(1e-9, math.log(max(ours.max_d / ours.start_d, 2.0), 2.0))
    orig_per_octave = len(corpus.instances) / max(
        1e-9, math.log(max(max_d / start_d, 2.0), 2.0))
    add("G10", "每个‘直径翻倍’档的可吸实例", "件/octave",
        round(orig_per_octave, 1), round(ours_per_octave, 1), PARAM,
        "corpus.instances（%d 件 / log2(%.4g/%.4g)=%.2f 档）"
        % (len(corpus.instances), max_d, start_d,
           math.log(max_d / start_d, 2.0)),
        "placement.json（%d 件 / log2(%.4g/%.4g)=%.2f 档）"
        % (ours.count, ours.max_d, ours.start_d,
           math.log(ours.max_d / ours.start_d, 2.0)),
        "归一到‘每翻一倍’后，原作 %.0f 件/档，我们 %.0f 件/档。"
        "原作单关每 0.1 m 档 20–180 件（reroll_numbers §2.2）"
        % (orig_per_octave, ours_per_octave),
        note="我方各档实测：%s（另有 %d 件 D_req 低于起始球）"
        % ("/".join(str(b) for b in bands), ours.count - sum(bands)))

    supply_orig, missing_penalty = corpus_supply(corpus)
    supply_ours = ours_supply(ours)
    add("G11", "供给比（含回收惩罚）", "×", round(supply_orig, 3),
        round(supply_ours, 3), PARAM,
        "corpus：Σ(penalty·V) for D_req≤%.4g m ÷ (V(%.4g)−V(%.4g))"
        % (max_d, max_d, start_d),
        "placement.json：Σ(penalty·k·bbox) for Req≤%.4g ÷ 同一分母（k 取 defs[].volumeK，"
        "缺省 0.70；penalty 缺省 0.8，逐件 volumePenalty 可覆盖）"
        % ours.max_d,
        "道具供给 / 通关所需。原作 %.2f×（远远多于所需，不可能卡）；我们 %.2f×"
        "（<1 意味着物理上到不了上限）。两边现在同一口径（0.8 惩罚 + k·bbox）"
        % (supply_orig, supply_ours),
        note="原作有 %d 个实例的类型缺 penalty，按文档默认 0.8 计" % missing_penalty
        if missing_penalty else "")

    starve_orig, starve_orig_bands = corpus_starvation(corpus)
    starve_ours, starve_ours_bands = ours_starvation(ours)
    add("G12", "饥饿检查（最紧档的余量）", "×", round(starve_orig, 2),
        round(starve_ours, 2), PARAM,
        "corpus：每翻倍档「可吸件数 ÷ 走完该档所需件数」，取最紧档",
        "placement.json：同一算法，惩罚按 0.8 / 逐件覆盖",
        "供给比是全局平均；这一条看**每一档**够不够。原作最紧档仍有 %.1f× 余量，"
        "我们最紧档 %.2f× —— 后者 <1 就是「走到这里卡住」"
        % (starve_orig, starve_ours),
        note="原作各档（可吸件/所需件）：%s｜我方：%s"
        % (starve_orig_bands, starve_ours_bands))

    # ------------------------------------------------------------------ C --
    add("C01", "实例数", "件", len(corpus.instances), ours.count, PARAM,
        "corpus.instances（%d 件，%d 有坐标）"
        % (len(corpus.instances), len(corpus.instances_with_position())),
        "placement.json props（%d）" % ours.count,
        "内容量差 %.1f×；这是‘吃两下就变大’最直接的物理解释"
        % (len(corpus.instances) / max(1, ours.count)))

    add("C02", "类型数", "种", len(corpus.catalogue), ours.type_count, PARAM,
        "corpus.catalogue（%d）" % len(corpus.catalogue),
        "placement.json defs（%d）" % ours.type_count,
        "类型数差 %.1f×。原作靠种类密度维持‘每一步都有新东西’"
        % (len(corpus.catalogue) / max(1, ours.type_count)))

    orig_absorbable = corpus.absorbable_at(max_d)
    orig_vol = sum(corpus.volume_m3(i) for i in orig_absorbable)
    orig_ratio = orig_vol / (math.pi / 6.0 * max_d ** 3)
    ours_ratio = ours.original_volume_absorbable_at(ours.max_d) / ours.ball_volume(ours.max_d)
    add("C03", "可吸内容体积 / 满径球体积", "×", round(orig_ratio, 3),
        round(ours_ratio, 3), PARAM,
        "corpus：D_req≤%.4g m 的 %d 件合计 %.4g m³ / V=%.4g m³"
        % (max_d, len(orig_absorbable), orig_vol, math.pi / 6.0 * max_d ** 3),
        "placement.json：D_req≤%.4g 的件合计 %.4g m³ / V=%.4g m³"
        % (ours.max_d, ours.original_volume_absorbable_at(ours.max_d),
           ours.ball_volume(ours.max_d)),
        "原作可吸内容体积是满径球的 %.2f 倍，我们只有 %.2f 倍。"
        "即使按 k·bbox 口径重算，我们整关内容也不够走到上限（repricing_proposal.md §0）"
        % (orig_ratio, ours_ratio))

    paths = sorted({str(e.get("path_speed_index")) for e in corpus.catalogue.values()
                    if e.get("path_speed_index") not in (None, "")})
    vault_types = sum(1 for e in corpus.catalogue.values()
                      if (e.get("vault_points") or 0) > 0)
    add("C04", "逐物件行为字段", "—",
        "pathSpeedIndex %d 种取值 / %d/%d 型有 vault 点 / 逐实例 playAnimNo"
        % (len(paths), vault_types, len(corpus.catalogue)),
        "数据里没有 behaviour 字段；运行时不读", MISSING,
        "corpus.catalogue ← name_props 表；object dump playAnimNo",
        "PROP_BEHAVIOURS.md §0.5（support/stackedOn 只喂校验器）；§3.1 五种 kind 是提案",
        "原作数据模型自带移动/翻越/动画参数；我们的 placement 没有这一列，"
        "运行期也没有行为宿主。这是‘内容能搬、行为不能搬’的分界线")

    add("C05", "堆叠支撑图", "—", "无公开证据（未知）",
        "数据里有 support/stackedOn，运行时不读；Python 参考模型有 CSR 图", MISSING,
        "PROP_BEHAVIOURS.md §5.1（公开材料无堆叠描述；专利也没有）",
        "prop_behaviours/support_graph.py + placement_core.py:1268（单支撑者）",
        "原作怎么让摞着的东西倒下来未知；我们连运行时都还没有（只有烘焙期校验与 Python 模型）")

    add("C06", "吸附物是否跟着放大", "—", "否（保持吸附瞬间位姿）", "否（只缩放 CoreMesh）",
        CONSISTENT,
        "US7402104B2：吸附记录位姿、父子挂接、不缩放（[专利]）",
        "BallView.cs:19-22；PickupFlowHost.AttachProp（ball_audit §E）",
        "两边都不缩放，且都允许互相穿插。这条已经对齐")

    add("C07", "埋没物件的处理", "—", "按 display level 停止绘制", "无策略（一直画）",
        MISSING,
        "US7402104B2 display level（[专利]）+ 日文 wiki/5ch（[社区实测]）",
        "ball_audit §E：无 burial 代码；CONTACT_MODEL_V2 §4.3 只是设计",
        "我们球长大后旧物被吞进去却还在画，原作会 cull。"
        "这是‘秃球/穿插’观感差异的来源之一")

    zones = (corpus.data.get("zone_profile") or {}).get("areas") or []
    area0 = next((z for z in zones if z.get("area") == 0), zones[0] if zones else {})
    ours_zone = ours_zone_stats(ours)
    zone_source = "corpus.zone_profile.areas[0]（meta/zone_profile/，仅 5/19 关能拆区）"
    add("C08", "区域最小件 / 入场球径", "×",
        area0.get("min_ratio_to_ball"), round(ours_zone["min_ratio"], 3), PARAM,
        zone_source, "placement.json：min(d_req)/起始球径",
        "入场时看到的最小件相对球有多大。原作 0.28（32 个区域中位），"
        "我们 %.2f——越小越'有东西可吃'，越大越像'开局就只剩大件'"
        % ours_zone["min_ratio"])

    add("C09", "入场可吃占比", "—", area0.get("eat_now"),
        round(ours_zone["eat_now"], 3), PARAM,
        zone_source, "placement.json：可吸件数/总件数（起始球径）",
        "一进场就有多少比例能吃。原作 12–76%%、典型 ~26%%；我们 %.0f%%"
        % (100.0 * ours_zone["eat_now"]))

    add("C10", "1–10 cm 类占比", "—", area0.get("small_1_10cm_share"),
        round(ours_zone["small_share"], 3), PARAM,
        zone_source + "；跨区衰减：MAS4 60%→12%→5.5%→0、MAS8 16.6%→0（size_class_mix）",
        "placement.json：0.01 ≤ d_req < 0.10 的件数占比",
        "小球尺寸类的占比。原作按区衰减到 0（第二区起不再放新小件）；"
        "我们 %.0f%%，且没有'区'的概念所以永远不会衰减" % (100.0 * ours_zone["small_share"]))

    add("C11", "尺寸-半径空间分布（Spearman）", "ρ",
        area0.get("spearman_size_radius"),
        None if ours_zone["spearman"] is None else round(ours_zone["spearman"], 3),
        PARAM,
        "corpus.zone_profile（spatial_summary.json）；Cancer +0.354、MAS1 +0.219",
        "run_parity.spearman(d_req, 到起始点的水平距离)",
        "正相关 = 小件在中央、大件在外围稀疏。原作两个可测关都是正；"
        "我们的布局按区域撒点，相关系数说明'中央是否真的是饭碗'")

    # ------------------------------------------------------------------ L --
    gates = params["zone_gates_m"]
    add("L01", "换区/换档尺寸阈值", "个", len(gates), 0, MISSING,
        "corpus.parameters.zone_gates_m ← swirls 表（MAS1 7/10/20/30/50/75/120 cm）",
        "placement.json：12 个 zone 是作者数据，没有尺寸阈值表",
        "原作到点播换区特效（不重载场景）；我们换了区不触发任何东西，"
        "也没有‘下一个尺寸档’的概念")

    timer_seconds = params.get("timer_seconds")
    add("L02", "时限", "s", timer_seconds, 120.0, UNKNOWN,
        "corpus.parameters.timer_raw=4，单位未证实（reroll_numbers §6.1）",
        "StoryRoundController.timeLimitSeconds=120（Docs/Design/StoryMode/ROUND_RULES.md）",
        "原作 4 个‘时间单位’，若按分钟是 240 s（我们的 2 倍）。"
        "单位不确认前不给比值——这正是语料里留 null 的地方")

    cam = params["camera"]
    fov = cam.get("fov_deg")
    add("L03", "相机垂直 FOV", "deg", None if fov is None else float(fov), 60.0, PARAM,
        "corpus.parameters.camera.fov_deg ← missions.CAMERA_FOV=37",
        "Docs/Design/BallMotor/README.md:248（60° 垂直 FOV）",
        "原作 37° 比我们窄得多；同样的球径在画面里更大，"
        "‘看得见前方多少’完全不同")

    add("L04", "HUD 报什么", "—", "直径（cm，含 +0.02 cm）", "语义直径（m）", ACCEPTABLE,
        "KD 2004 手册 ‘current diameter of the katamari’（[官方]）",
        "ball_audit §C：GetSemanticDiameterWorldM → HUD",
        "量相同（直径）、单位不同。可接受偏差，但换算/取整规则要写死一处")

    ratings = params["rating_diameters_m"]
    star_n = len(OUR_STARS)
    star_span = _v("star_span_m") / _v("clear_target_m")
    orig_span = ((ratings[-1] - ratings[0]) / ratings[0]) if len(ratings) > 1 else 0.0
    add("L05", "评分钩子（星级阈值）", "个", len(ratings), star_n, PARAM,
        "corpus.parameters.rating_diameters_m ← missions.RATING_DIAMETER（%s cm）"
        % "/".join("%.4g" % (r * 100.0) for r in ratings),
        STAR_SOURCE,
        "原作 %d 档，星级带跨目标 %.0f%%；我们 %d 档、跨 %.0f%%。"
        "档数与带宽都不同：原作的星级是‘继续长大还有分’，我们的是‘同一段的三小步’"
        % (len(ratings), 100.0 * orig_span, star_n, 100.0 * star_span),
        note="原作档位：%s m" % "/".join("%.2f" % r for r in ratings))

    # ------------------------------------------------------------------ S --
    min_req = min(corpus.req_pickup_m(i) for i in corpus.instances)
    ours_min = min(i["d_req_m"] for i in ours.items)
    add("S01", "最小可吸道具（D_req）", "m", min_req, round(ours_min, 4), PARAM,
        "corpus：最小 Req Pickup Size = %.0f mm" % (min_req * 1000.0),
        "placement.json 最小 absorbDiameterReq = %.4g" % ours_min,
        "两边的最小件都远小于起始球；差别在‘最小件/起始球’这个比例（S02）")

    add("S02", "最小可吸 / 起始球径", "—", round(min_req / start_d, 4),
        round(ours_min / ours.start_d, 4), PARAM,
        "%.0f/%.0f mm" % (min_req * 1000.0, start_d * 1000.0),
        "%.4g/%.4g" % (ours_min, ours.start_d),
        "原作最小件是起始球的 %.0f%%，我们是 %.0f%%。"
        "这意味着我们的‘第一口’比原作更无关痛痒，也让整数量化显得更粗暴"
        % (100.0 * min_req / start_d, 100.0 * ours_min / ours.start_d))

    over_orig_names = [label for _, label, c in ABSOLUTE_CONSTANTS
                       if c.value > 0.10 * start_d]
    over_ours_names = [label for _, label, c in ABSOLUTE_CONSTANTS
                       if c.value > 0.10 * ours.start_d]
    add("S03", "绝对米制常数 >10% 起始球径的个数",
        "个/共%d" % len(ABSOLUTE_CONSTANTS),
        len(over_orig_names), len(over_ours_names), DIFFERENT,
        "corpus 起始 %.4g m：%s 越线"
        % (start_d, "、".join(over_orig_names) if over_orig_names else "无"),
        "我方起始 %.4g m：%s 越线"
        % (ours.start_d, "、".join(over_ours_names) if over_ours_names else "无"),
        "同一个引擎跑 %.4g m 的球时，5 mm 的 skin 就是球径的 %.0f%%、300 mm 的突出 cap 是 %.1f 个球径。"
        "这些常数是米制的，不会随球缩小——所以‘把原作关卡搬进来’必须先决定缩放策略"
        % (start_d, 100.0 * _v("contact_skin_m") / start_d,
           _v("protrusion_cap_m") / start_d))

    homog = ("体积门、D(V)、h<r 滚越、H_cap∝D、0.25D 台阶")
    nonhomog = ("skin/slop/推挤 50 mm/突出 cap 300 mm/到顶 20 mm + 时基 30 vs 50 Hz + g=9.81"
                "（叠放 10 mm 已取消，见 divergences 第 3 行）")
    add("S04", "尺度不变性（长度×6）", "—",
        "未知（原作没有缩放实验）",
        "同构 5 条 / 非齐次 6 条 + 时基与重力", DIFFERENT,
        "corpus 起始 %.4g m 与我们的 %.4g m 是 %.2f×"
        % (start_d, ours.start_d, ours.start_d / start_d),
        "run_parity.S04（本项由 ABSOLUTE_CONSTANTS 与 OURS 直接算出）",
        "把长度统一乘 %.2f 后：百分比类规则（体积门、比例阈值）保持等价；"
        "米制常数、30/50 Hz 时基、g 都不跟着变 → 手感不等价。"
        "同构：%s；非齐次：%s"
        % (ours.start_d / start_d, homog, nonhomog))

    for row in rows:
        _settle(row)
    return rows


# --------------------------------------------------------------------------- #
# rendering
# --------------------------------------------------------------------------- #
def summarize(rows: Sequence[Row]) -> List[Tuple[str, int]]:
    counts: Dict[str, int] = {name: 0 for name in CLASSES}
    for row in rows:
        counts[row.klass] = counts.get(row.klass, 0) + 1
    return [(name, counts[name]) for name in CLASSES if counts[name]]


def render(corpus: Corpus, ours: OursLevel, rows: Sequence[Row],
           ours_ref: Optional[OursLevel] = None) -> str:
    params = corpus.params
    lines: List[str] = []
    lines.append("# 差分 gap 表：%s × %s" % (corpus.stage_name, ours.id))
    lines.append("")
    lines.append("- 语料：`%s`（schema %s，%d 实例 / %d 类型）"
                 % (corpus.path.name if corpus.path else "(内存)",
                    corpus.data["schema"], len(corpus.instances),
                    len(corpus.catalogue)))
    lines.append("- 我方关卡：`%s`（%d 件 / %d 型，起始 %.4g m，上限 %.4g m）"
                 % (ours.path.name if ours.path else "(内存)", ours.count,
                    ours.type_count, ours.start_d, ours.max_d))
    lines.append("- 原作：起始 %.4g m → 目标 %.4g m（上限 %.4g m），区域 %d，"
                 "定步 %.4g Hz"
                 % (corpus.start_diameter_m, corpus.clear_diameter_m,
                    corpus.max_diameter_m, int(params["area_count"]),
                    float(corpus.data["timebase"]["fixed_step_hz"])))
    lines.append("- 判定词汇：" + " / ".join(CLASSES))
    lines.append("")
    summary = summarize(rows)
    lines.append("**合计 %d 条指标**：" % len(rows)
                 + "，".join("%s %d" % (name, count) for name, count in summary))
    lines.append("")
    lines.append("| # | 层 | 指标 | 单位 | 原作 | 我方 | 差 | 判定 |")
    lines.append("|---|---|---|---|---|---|---|---|")
    for row in rows:
        lines.append("| %s | %s | %s | %s | %s | %s | %s | %s |"
                     % (row.id, row.layer, row.name, row.unit,
                        _fmt(row.original), _fmt(row.ours), row.delta_text(),
                        row.klass))
    lines.append("")
    lines.append("## 逐条依据与 gap 含义")
    lines.append("")
    lines.append("| # | 原作侧读数（证据） | 我方侧读数（file:line / 模型） | gap 含义 |")
    lines.append("|---|---|---|---|")
    for row in rows:
        lines.append("| %s | %s | %s | %s |"
                     % (row.id, _fmt_source(row.orig_source),
                        _fmt_source(row.ours_source), _fmt_source(row.meaning)))
    by_id = {row.id: row for row in rows}
    lines.append("")
    lines.append("## owner 决定（2026-11-15）与它们会动的行")
    lines.append("")
    lines.append("| # | 决定 | 受影响指标（当前判定） |")
    lines.append("|---|---|---|")
    for decision_id, text_value, metric_ids in DECISIONS:
        affected = "、".join("%s（%s）" % (metric_id, by_id[metric_id].klass)
                            for metric_id in metric_ids if metric_id in by_id)
        lines.append("| %s | %s | %s |" % (decision_id, text_value, affected))
    lines.append("")
    lines.append("表里仍是 `参数差` 的行，就是「决定已下、代码未改」的行；"
                 "改完重跑，它们应变成 `一致`。")
    notes = [row for row in rows if row.note]
    if notes:
        lines.append("")
        lines.append("## 附注")
        lines.append("")
        for row in notes:
            lines.append("- **%s** %s" % (row.id, row.note))
    if ours_ref is not None:
        lines.append("")
        lines.append("## 供给比：两关我方数据（story_01 / _ref_mas1）")
        lines.append("")
        lines.append("| 关卡 | 起始→上限 (m) | 件数 | 供给比 | 最紧档（可吸件/所需件） |")
        lines.append("|---|---|---|---|---|")
        for label, level in (("story_01", ours), ("_ref_mas1", ours_ref)):
            starve, bands = ours_starvation(level)
            lines.append("| %s | %.4g → %.4g | %d | %.2f× | %.2f×（%s） |"
                         % (level.id, level.start_d, level.max_d, level.count,
                            ours_supply(level), starve, bands))
        lines.append("")
        lines.append("供给比 = 可吸总体积 ÷ 从起始长到上限所需体积；<1 表示单靠本关内容走不到上限。")
        lines.append("_ref_mas1 的物件与坐标来自原作数据，用于与 story_01 对照，不参与正式关卡。")
    unresolved = corpus.data.get("unresolved") or []
    if unresolved:
        lines.append("")
        lines.append("## 语料里仍未解决（不给数，不猜）")
        lines.append("")
        for item in unresolved:
            lines.append("- %s" % item)
    lines.append("")
    lines.append("## 参考模型来源（本表没有第二份实现）")
    lines.append("")
    lines.append("- `Docs/Design/BallMotor/contact_v2/contact_v2.py` — 步进、"
                 "子步、分类、爬升")
    lines.append("- `Docs/Design/BallMotor/contact_v2/shapes.py` — 三层形状、"
                 "拾取触发体")
    lines.append("- `Docs/Design/BallLayers/SizeModel/size_model.py` — 体积→直径")
    lines.append("- `Tools/levelbuild/placement_core.py` — 10× 体积门、"
                 "叠放容差")
    lines.append("")
    lines.append("生成命令：`python3 Docs/Design/Parity/parity_harness/run_parity.py`")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
CORPUS_HINT = (
    "语料不入库（商业游戏数据挖掘导出，.gitignore 排除）。本地生成：\n"
    "  python3 Docs/Design/Parity/parity_harness/build_corpus.py\n"
    "  （默认读 ~/KatamariRef/meta，可用 --meta 指到别处）")


def default_paths() -> Tuple[Path, Path, Path]:
    here = Path(__file__).resolve().parent
    corpus = here / "corpus_star1.json"
    ours = REPO_ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"
    ours_ref = REPO_ROOT / "Docs/Design/Levels/_ref_mas1/placement.json"
    return corpus, ours, ours_ref


def main(argv: Optional[Sequence[str]] = None) -> int:
    corpus_default, ours_default, ref_default = default_paths()
    parser = argparse.ArgumentParser(
        description="Original-stage data vs our reference models: gap table.")
    parser.add_argument("--corpus", default=str(corpus_default),
                        help="stage corpus JSON (default: %(default)s). "
                             "The corpora are NOT committed; build them locally "
                             "with build_corpus.py")
    parser.add_argument("--ours", default=str(ours_default))
    parser.add_argument("--ours-ref", default=str(ref_default),
                        help="second our-side level for the supply comparison "
                             "(_ref_mas1; skipped when absent)")
    parser.add_argument("--out", default=None,
                        help="write the Markdown here instead of stdout")
    args = parser.parse_args(list(argv) if argv is not None else None)
    corpus_path = Path(args.corpus)
    if not corpus_path.is_file():
        print("corpus missing: %s\n%s" % (corpus_path, CORPUS_HINT),
              file=sys.stderr)
        return 2
    try:
        corpus = load_corpus(corpus_path)
    except CorpusError as error:
        print("corpus refused: %s" % error, file=sys.stderr)
        return 2
    ours_path = Path(args.ours)
    if not ours_path.is_file():
        print("our level data missing: %s" % ours_path, file=sys.stderr)
        return 2
    ours = load_ours_level(ours_path)
    ours_ref = None
    ref_path = Path(args.ours_ref)
    if ref_path.is_file() and ref_path.resolve() != ours_path.resolve():
        ours_ref = load_ours_level(ref_path)
    text = render(corpus, ours, build_rows(corpus, ours), ours_ref)
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
        print("wrote %s (%d lines)" % (args.out, text.count("\n")))
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":  # pragma: no cover - CLI
    raise SystemExit(main())
