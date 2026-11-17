#!/usr/bin/env python3
"""Supply plan: can one ``placement.json`` feed the ball all the way to its goal?

The question this answers is not "how much content is in the level" but "does the
content still feed the ball at ball diameter D".  At 0.30 m the ball can eat 96 of
``story_01``'s 130 props; at 0.55 m it has eaten them all and nothing is left.  A
count of props cannot show that, a ladder of ball diameters can.

Read-only.  Every geometric and growth quantity comes from
:mod:`Tools.levelbuild.placement_core` -- the absorb gate
(``absorb_diameter_req_mm``), the one growth rule (``growth_mm3`` =
``round(0.8 * k * a*b*c * 1e9)``), the ball volume (``ball_volume_mm3``) and the
volume->diameter inverse (``diameter_for_volume_mm3``).  This module only *walks*
them; it never re-derives the gate, so a change to the original's rule changes one
file and both the runtime and this analysis follow.

What a rung row says
--------------------
For the rung ``[prev, D)`` of ball diameter:

``new``     props whose ``d_req_mm`` first falls at or below ``floor(1000*D)``.  These
            are the ones that became edible *on this rung*; the ball cannot reach them
            one rung down.
``cum``     props edible at ``D`` (``new`` accumulated from the start).
``items``   absorbs the rung costs: ``needV / mean credit`` where ``needV`` is the
            volume the ball must gain to cross it and ``credit`` is what one absorb is
            worth (``penalty * k * a*b*c``).  This is the parity harness's ``_runway``
            denominator (``PARITY_AUDIT`` G12).
``runway``  ``cum / items`` -- edible props per absorb still needed.  Below 1 the rung
            cannot be crossed on this level's own content (G12's "tightest band").
``supply``  ``sum(credit for d_req<=D) / (V(D) - V(start))`` -- the volume-based
            supply ratio of everything edible so far against everything needed so far
            (``PARITY_AUDIT`` G11, ``ContentVolume/README.md`` section 4.5.1).
``step%``   median per-absorb diameter step of the owner-like play trace while the ball
            was in this rung.  The original's HUD reference is 1.4-2.3 % (see
            :data:`ORIGINAL_HUD_ANCHORS`); 0.4 % is what "a bowl is food at 0.30 m"
            actually pays.

Two climb traces are simulated, because "eat everything" and "play like the owner"
are different games:

``greedy``  always eat the largest already-edible prop.  The ceiling of the level.
``owner``   eat the prop closest to 0.35 x D (the owner's measured rate of item size to
            ball size in the original), never above it when something smaller exists.
            The realistic curve.

Both stop when nothing edible is left -- that stop is the number to look at.  A level
whose greedy trace stalls below its 1-star diameter is unwinnable however good the
player is, and no count of props reveals it.

Run::

    python3 Tools/levelbuild/supply_plan.py Docs/Design/Levels/story_01_bench_size/placement.json
    python3 Tools/levelbuild/supply_plan.py <placement.json> --additions proposed_additions.json
    python3 Tools/levelbuild/supply_plan.py <placement.json> --json --csv ladder.csv
"""

import argparse
import json
import math
import statistics
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import editor_core  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402

#: The rung width of the default ladder, in metres.  0.05 m is the resolution at which
#: "became edible on this rung" still means something: one rung is ~10-17 % of the ball
#: diameter early, ~3 % late, so a gap of one rung is already a visible stall.
DEFAULT_STEP_M = 0.05

#: ``story_01_bench_size``'s star diameters, as the level declares them today.  The
#: analysis takes them as an argument (``--stars``); these are only the default so the
#: report can talk about "1 star" without a second file.
DEFAULT_STARS = (1.25, 1.45, 1.60)

#: The owner's original-game HUD reading, used as the reference growth curve:
#: ``(D0_m, items_eaten, D_m)`` for Make Cancer from 22.2 cm -- 29.3/34.5/45.4 cm after
#: 12/24/42 items.  Per-item diameter step of the three blocks: +2.34 %, +1.36 %,
#: +1.53 %; the whole 42-item run averages **+1.71 % per item**.
ORIGINAL_HUD_ANCHORS = ((0.222, 12, 0.293), (0.222, 24, 0.345), (0.222, 42, 0.454))

#: Absorbs per second used for the time proxy.  0.3-0.4/s is the naive probe
#: (one item per ~2.5-3 s, the ball walks between props); a human rolling through a
#: dense patch is faster.  The report prints all three so a recommendation can cite
#: the assumption instead of hiding it.
ABSORBS_PER_SECOND = (0.4, 0.6, 0.8)

#: The owner's measured "players eat items about 0.35 x the ball's diameter".
OWNER_ITEM_RATIO = 0.35

#: The original's design principle for the supply ratio (``ContentVolume/README.md``
#: section 4.5.1: the design floor is 3x, the goal 5-10x).
DEFAULT_SUPPLY_TARGET = 3.0

#: The original's design principle for per-tier supply: "at least this many absorbable,
#: not-yet-eaten items per tier" (``ContentVolume/README.md`` section 4.4, D4).
DEFAULT_MIN_PER_BAND = 40

#: Floor area one placed prop costs, relative to its own footprint: props have to be
#: reachable and rollable-around, so a band of ``n`` props occupies about
#: ``n * footprint * AREA_SPACING``.  This is what decides whether a lawn can hold a band
#: at all, and it is the number that says "this floor is too small" rather than "this
#: level needs more props".
AREA_SPACING = 1.6

#: Ball-diameter buckets of the "per-item step %" table.  The measured pathology of
#: ``story_01`` was 0.448 % / 0.054 % / 4.7 % / 0.21 % on these buckets.
STEP_BUCKETS = ((0.0, 0.5), (0.5, 0.75), (0.75, 1.0), (1.0, 1.5), (1.5, 3.0))

#: What one absorb pays at the absorb gate: an item exactly at the gate has
#: ``k*a*b*c = V_ball/10``, so the credit is ``0.8/10 = 8 %`` of the ball's volume and the
#: diameter grows by ``1.08**(1/3) - 1 = 2.6 %``.  That is the ceiling of one absorb; an
#: item at ``u`` of the gate pays ``1 + 0.08*u**3`` in volume.
GATE_CREDIT_FRACTION = 0.08
GATE_DIAMETER_CEILING = (1.0 + GATE_CREDIT_FRACTION) ** (1.0 / 3.0) - 1.0


# --------------------------------------------------------------------------- #
# the level as the analysis needs it (read-only)
# --------------------------------------------------------------------------- #
def _credit_m3(size, volume_k, penalty):
    """What one absorb is worth, in m^3: ``penalty * k * a*b*c`` (the gate's volume)."""
    return float(penalty) * float(volume_k) * size[0] * size[1] * size[2]


def build_items(doc):
    """``[{item_id, def, kind, zone, aabb, longest, credit_m3, growth_mm3, d_req_mm, ...}]``.

    One pass over ``props[]`` with the shared helpers: ``credit_m3`` is the gate's
    volume ``k*a*b*c`` times the penalty (what the ledger banks), ``growth_mm3`` is the
    ledger's integer gain of the same absorb, ``d_req_mm`` is the gate.
    """
    catalog = core.def_catalog(doc)
    level = doc.get("level") or {}
    ball_max = level.get("ballDiameterMax")
    ball_max = float(ball_max) if ball_max is not None else 0.0
    items = []
    for prop in doc.get("props") or []:
        if not isinstance(prop, dict):
            continue
        try:
            aabb = tuple(float(v) for v in prop["aabb"])
            position = tuple(float(v) for v in prop["position"])
        except (KeyError, TypeError, ValueError):
            continue
        volume_k = core.effective_volume_k(prop, catalog)
        penalty = core.effective_volume_penalty(prop, catalog)
        items.append({
            "item_id": prop.get("itemId"),
            "def": prop.get("def"),
            "kind": prop.get("kind"),
            "zone": prop.get("zone"),
            "support": prop.get("support"),
            "aabb": aabb,
            "position": position,
            "longest": max(aabb),
            "volume_k": volume_k,
            "volume_penalty": penalty,
            "credit_m3": _credit_m3(aabb, volume_k, penalty),
            "growth_mm3": core.item_growth_mm3(aabb, volume_k, penalty),
            "d_req_mm": core.absorb_diameter_req_mm(aabb, volume_k),
            "d_req_m": core.absorb_diameter_req(aabb, volume_k),
            # Declared vs derived: the file carries `absorbable` for the level's own cap.
            "declared_absorbable": prop.get("absorbable"),
            "declared_d_req_m": prop.get("absorbDiameterReq"),
            "beyond_cap": core.absorb_diameter_req_mm(aabb, volume_k) > core.floor_mm(ball_max),
        })
    items.sort(key=lambda item: (item["d_req_mm"], str(item["item_id"])))
    return items


class Level:
    """A placement document plus the derived numbers the ladder walks."""

    def __init__(self, doc, path=None):
        self.doc = doc
        self.path = Path(path) if path is not None else None
        level = doc.get("level") or {}
        self.id = str(level.get("id", "?"))
        self.start_m = float(level.get("ballDiameterStart") or core.DEFAULT_START_DIAMETER_M)
        self.max_m = float(level.get("ballDiameterMax") or self.start_m)
        self.items = build_items(doc)
        self.start_volume_mm3 = core.ball_volume_mm3(self.start_m)

    # -- content ---------------------------------------------------------- #
    @property
    def count(self):
        return len(self.items)

    @property
    def type_count(self):
        return len({item["def"] for item in self.items})

    @property
    def credit_total_m3(self):
        return sum(item["credit_m3"] for item in self.items)

    @property
    def growth_total_mm3(self):
        return sum(item["growth_mm3"] for item in self.items)

    def diameter_after_eating_all(self, ids=None):
        """The ball diameter if every given prop is eaten (no gate, an upper bound)."""
        chosen = self.items if ids is None else [i for i in self.items if i["item_id"] in ids]
        volume = self.start_volume_mm3 + sum(item["growth_mm3"] for item in chosen)
        return core.diameter_for_volume_mm3(volume)

    def volume_m3(self, diameter_m):
        return math.pi / 6.0 * float(diameter_m) ** 3


def load_placement(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


# --------------------------------------------------------------------------- #
# additions (a dry-run patch, never merged on disk here)
# --------------------------------------------------------------------------- #
def load_additions(path):
    """Read a ``katamari.additions/0`` patch: extra props (+ optional extra defs)."""
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError("%s: an additions patch must be a JSON object" % path)
    props = document.get("props")
    if not isinstance(props, list):
        raise ValueError("%s: the patch needs a props[] list" % path)
    return document


def merge_additions(doc, patch):
    """A NEW document = ``doc`` + the patch's props/defs, with ``level.expected`` re-pinned.

    Nothing is written: the caller decides whether to dump the result.  The merge is
    what makes "would this pass the gate?" answerable without touching
    ``placement.json`` -- ``expected.propCount``/``growthMm3`` are design totals, so
    they must be re-pinned by the one implementation that owns them
    (:func:`editor_core.refresh_expected`), not by this file.
    """
    merged = json.loads(json.dumps(doc))          # deep copy, JSON semantics
    existing = {prop.get("itemId") for prop in merged.get("props") or []
                if isinstance(prop, dict)}
    for prop in patch.get("props") or []:
        item_id = prop.get("itemId")
        if item_id in existing:
            raise ValueError("additions: itemId %r already exists in the placement" % (item_id,))
        existing.add(item_id)
        merged.setdefault("props", []).append(dict(prop))
    defs = merged.setdefault("defs", [])
    known = {entry.get("id") for entry in defs if isinstance(entry, dict)}
    for entry in patch.get("defs") or []:
        if entry.get("id") not in known:
            defs.append(dict(entry))
    editor_core.refresh_expected(merged)
    return merged


# --------------------------------------------------------------------------- #
# ladder
# --------------------------------------------------------------------------- #
def rung_diameters(start_m, stop_m, step_m=DEFAULT_STEP_M):
    """``start`` then ``start + step`` ... up to and including ``stop`` (no duplicates)."""
    if step_m <= 0.0:
        raise ValueError("step must be > 0")
    diameters = [round(float(start_m), 6)]
    index = 1
    while True:
        value = start_m + index * step_m
        if value > stop_m + 1e-9:
            break
        diameters.append(round(value, 6))
        index += 1
    # The last generated rung can sit a hair below ``stop`` (start 0.2997 + 26 * 0.05 =
    # 1.5997 for a 1.60 stop).  Snap instead of appending, or the ladder ends in two
    # rungs 0.3 mm apart and the last one has no content of its own.
    if abs(diameters[-1] - stop_m) > 1e-9:
        if float(stop_m) - diameters[-1] < 0.5 * step_m and len(diameters) > 1:
            diameters[-1] = round(float(stop_m), 6)
        else:
            diameters.append(round(float(stop_m), 6))
    return diameters


def ladder(items, start_m, stop_m, step_m=DEFAULT_STEP_M):
    """One row per rung of the ball-diameter ladder.

    See the module docstring for the column meanings.  ``supply`` and ``runway`` are
    the two parity quantities computed on the same credit the gate uses, so the numbers
    are directly comparable with ``PARITY_AUDIT`` G11/G12.
    """
    diameters = rung_diameters(start_m, stop_m, step_m)
    rows = []
    previous_mm = None
    start_volume = math.pi / 6.0 * float(start_m) ** 3
    for index, diameter in enumerate(diameters):
        ball_mm = core.floor_mm(diameter)
        pool = [item for item in items if item["d_req_mm"] <= ball_mm]
        new = pool if previous_mm is None else [
            item for item in pool if item["d_req_mm"] > previous_mm]
        need_m3 = (math.pi / 6.0 * diameter ** 3) - (
            math.pi / 6.0 * diameters[index - 1] ** 3) if index else 0.0
        mean_credit = (sum(item["credit_m3"] for item in pool) / len(pool)) if pool else 0.0
        needed_items = int(math.ceil(need_m3 / mean_credit)) if mean_credit > 0.0 else 0
        credited = sum(item["credit_m3"] for item in pool)
        rows.append({
            "index": index,
            "diameter_m": diameter,
            "ball_mm": ball_mm,
            "prev_mm": previous_mm,
            "new": len(new),
            "new_ids": [item["item_id"] for item in new],
            "cum": len(pool),
            "need_m3": need_m3,
            "mean_credit_m3": mean_credit,
            "needed_items": needed_items,
            "runway": (len(pool) / needed_items) if needed_items else None,
            "credited_m3": credited,
            "supply": (credited / (math.pi / 6.0 * diameter ** 3 - start_volume))
                      if index else None,
        })
        previous_mm = ball_mm
    return rows


def supply_ratio(items, start_m, target_m):
    """``sum(credit) / (V(target) - V(start))`` over everything edible at ``target``.

    The parity audit's G11 quantity (``ours_supply``): the penalty is already in
    ``credit_m3``, so this is "what the level hands back / what the climb costs".
    """
    ball_mm = core.floor_mm(target_m)
    credited = sum(item["credit_m3"] for item in items if item["d_req_mm"] <= ball_mm)
    need = math.pi / 6.0 * float(target_m) ** 3 - math.pi / 6.0 * float(start_m) ** 3
    if need <= 0.0:
        return float("inf")
    return credited / need


# --------------------------------------------------------------------------- #
# climb traces
# --------------------------------------------------------------------------- #
def _pick_greedy(candidates, diameter):
    """The largest already-edible prop (ties: the one that was harder to reach)."""
    return max(candidates, key=lambda item: (item["growth_mm3"], item["d_req_mm"],
                                             str(item["item_id"])))


def _pick_owner(candidates, diameter, ratio=OWNER_ITEM_RATIO):
    """The prop whose size is closest to ``ratio * D`` -- a size-class player.

    "Players eat items about 0.35 x the ball's diameter" is a statement about the items
    a player *aims at*, not a hard ceiling: a player crossing a lawn at 0.30 m takes the
    0.10 m cups, and once those are gone takes the 0.15 m bowl rather than hunting for a
    3 cm seed.  So the pick minimises ``|longest / (ratio*D) - 1|`` and prefers the
    bigger item only as a tie-break.  Distinction from ``greedy``: greedy always takes
    the largest edible prop, which is the gate boundary -- a speedrun, not a player.
    """
    target = ratio * diameter
    return min(candidates, key=lambda item: (abs(item["longest"] / target - 1.0),
                                             -item["growth_mm3"], str(item["item_id"])))


POLICIES = {"greedy": _pick_greedy, "owner": _pick_owner}


def climb(items, start_m, stop_m, policy="greedy", ratio=OWNER_ITEM_RATIO):
    """Simulate eating until nothing edible is left (or ``stop_m`` is reached).

    Returns ``{reached_m, stalls, absorbs, steps, dead_end_mm}`` where ``steps`` is one
    entry per absorb: ``(diameter_before, diameter_after, item, step_pct)``.  The gate
    is re-checked after every absorb, which is the whole point -- the ball's own growth
    is what unlocks the next prop.
    """
    if policy not in POLICIES:
        raise ValueError("unknown policy %r (have %s)" % (policy, sorted(POLICIES)))
    pick = POLICIES[policy]
    if policy == "owner":
        pick = lambda candidates, diameter: _pick_owner(candidates, diameter, ratio)
    volume = core.ball_volume_mm3(start_m)
    diameter = float(start_m)
    eaten = set()
    steps = []
    while True:
        ball_mm = core.floor_mm(diameter)
        candidates = [item for item in items
                      if item["d_req_mm"] <= ball_mm and item["item_id"] not in eaten]
        if not candidates:
            break
        chosen = pick(candidates, diameter)
        eaten.add(chosen["item_id"])
        before = diameter
        volume += chosen["growth_mm3"]
        diameter = core.diameter_for_volume_mm3(volume)
        steps.append({
            "before_m": before,
            "after_m": diameter,
            "step_pct": (diameter / before - 1.0) * 100.0,
            "item_id": chosen["item_id"],
            "def": chosen["def"],
            "d_req_mm": chosen["d_req_mm"],
            "longest": chosen["longest"],
            "gate_use": chosen["d_req_mm"] / float(ball_mm) if ball_mm else 0.0,
            "credit_m3": chosen["credit_m3"],
        })
        if diameter >= stop_m:
            break
    ball_mm = core.floor_mm(diameter)
    next_req = min((item["d_req_mm"] for item in items if item["d_req_mm"] > ball_mm),
                   default=None)
    return {
        "policy": policy,
        "reached_m": diameter,
        "absorbs": len(steps),
        "steps": steps,
        "stalled": diameter < stop_m,
        "next_req_mm": next_req,
        "uneaten": len(items) - len(steps),
    }


def step_buckets(steps, buckets=STEP_BUCKETS):
    """Median/mean per-item diameter step per ball-diameter bucket."""
    rows = []
    for low, high in buckets:
        values = [step["step_pct"] for step in steps if low <= step["before_m"] < high]
        rows.append({
            "bucket": (low, high),
            "n": len(values),
            "median_pct": statistics.median(values) if values else None,
            "mean_pct": (sum(values) / len(values)) if values else None,
        })
    return rows


# --------------------------------------------------------------------------- #
# target curve
# --------------------------------------------------------------------------- #
def original_step_rates():
    """Per-item diameter step implied by :data:`ORIGINAL_HUD_ANCHORS`, per block.

    Returns ``[(items_from, items_to, rate)]`` where ``rate = (D_to/D_from)**(1/dn) - 1``
    for the block's ``dn`` items.  The first block is the fast one (2.3 %/item) and the
    run averages 1.7 %/item -- both matter: a level only has to be fast *early*.
    """
    rates = []
    for (start, k_from, d_from), (_, k_to, d_to) in zip(ORIGINAL_HUD_ANCHORS,
                                                         ORIGINAL_HUD_ANCHORS[1:]):
        count = k_to - k_from
        rates.append((k_from, k_to, (d_to / d_from) ** (1.0 / count) - 1.0))
    first = ORIGINAL_HUD_ANCHORS[0]
    last = ORIGINAL_HUD_ANCHORS[-1]
    overall = (last[2] / first[2]) ** (1.0 / (last[1] - first[1])) - 1.0
    return rates, overall


def diameter_for_items(start_m, count, rate):
    """``D(k) = D0 * (1 + rate)**k`` -- the target growth curve."""
    return float(start_m) * (1.0 + float(rate)) ** int(count)


def items_for_diameter(start_m, target_m, rate):
    """Inverse of :func:`diameter_for_items`, rounded up (a whole absorb)."""
    if target_m <= start_m:
        return 0
    return int(math.ceil(math.log(target_m / start_m) / math.log(1.0 + rate)))


def star_progress(trace, stars):
    """Absorbs the trace spends before the ball first passes each star diameter.

    ``None`` when the trace never gets there -- the honest answer for a level whose food
    chain dead-ends, and the number a time limit must be compared with.
    """
    out = []
    for index, diameter in enumerate(stars, start=1):
        count = None
        for position, step in enumerate(trace["steps"], start=1):
            if step["after_m"] >= diameter:
                count = position
                break
        out.append({"star": index, "diameter_m": diameter, "absorbs": count})
    return out


def star_schedule(start_m, stars, rate):
    """``[{star, diameter_m, items, seconds at each ABSORBS_PER_SECOND}]``."""
    schedule = []
    for index, diameter in enumerate(stars, start=1):
        items = items_for_diameter(start_m, diameter, rate)
        schedule.append({
            "star": index,
            "diameter_m": diameter,
            "items": items,
            "seconds": {rate_per_s: items / rate_per_s for rate_per_s in ABSORBS_PER_SECOND},
        })
    return schedule


# --------------------------------------------------------------------------- #
# verdict
# --------------------------------------------------------------------------- #
def _rung_containing(rows, diameter):
    """The ladder index whose rung contains ``diameter``."""
    for row in rows:
        if diameter <= row["diameter_m"] + 1e-9:
            return row["index"]
    return rows[-1]["index"] if rows else None


def analyse(path, additions=None, stars=DEFAULT_STARS, step_m=DEFAULT_STEP_M,
            stop_m=None, min_per_band=DEFAULT_MIN_PER_BAND,
            band_m=0.10, supply_target=DEFAULT_SUPPLY_TARGET, min_per_rung=None):
    """The whole report as a JSON-ready dict.

    ``stop_m`` defaults to the furthest star (the 3-star diameter); ``ballDiameterMax``
    is reported separately because it is the ladder top, not necessarily a goal.
    """
    doc = load_placement(path)
    patch = load_additions(additions) if additions else None
    merged = merge_additions(doc, patch) if patch else doc
    level = Level(merged, path)

    stop = float(stop_m) if stop_m is not None else max(stars)
    # The >= 40 per tier rule, expressed on a rung: a tier is ``band_m`` wide, so a rung
    # of ``step_m`` owes ``40 * step/band`` new props (20 at 0.05/0.10).
    if min_per_rung is None:
        min_per_rung = max(1, int(round(min_per_band * step_m / band_m)))
    rows = ladder(level.items, level.start_m, stop, step_m)
    for row in rows:
        row["thin"] = row["index"] > 0 and row["new"] < min_per_rung
    greedy = climb(level.items, level.start_m, stop, "greedy")
    owner = climb(level.items, level.start_m, stop, "owner")

    # What each rung costs in the owner-like trace: absorbs spent while the ball's
    # diameter was inside the rung.  This is the honest "items needed" column.
    absorbs_per_rung = [0] * len(rows)
    for step in owner["steps"]:
        index = _rung_containing(rows, step["before_m"])
        if index is not None:
            absorbs_per_rung[index] += 1
    for row, count in zip(rows, absorbs_per_rung):
        row["owner_absorbs"] = count

    # Step % per rung, from the owner trace (the play being designed for).
    for row in rows:
        in_rung = [step["step_pct"] for step in owner["steps"]
                   if row["prev_mm"] is not None
                   and row["prev_mm"] < core.floor_mm(step["before_m"]) <= row["ball_mm"]]
        row["owner_median_step_pct"] = statistics.median(in_rung) if in_rung else None

    bands = band_table(level.items, level.start_m, stop, band_m)
    rates, overall_rate = original_step_rates()

    result = {
        "placement": str(path),
        "additions": str(additions) if additions else None,
        "level": level.id,
        "start_m": level.start_m,
        "ladder_top_m": level.max_m,
        "ladder_stop_m": stop,
        "step_m": step_m,
        "props": level.count,
        "types": level.type_count,
        "start_volume_m3": level.volume_m3(level.start_m),
        "growth_total_mm3": level.growth_total_mm3,
        "credit_total_m3": level.credit_total_m3,
        # The upper bound: eat every prop, ignoring whether the ball could reach it.
        "diameter_when_all_eaten_m": level.diameter_after_eating_all(),
        "greedy": {key: greedy[key] for key in
                   ("reached_m", "absorbs", "stalled", "next_req_mm", "uneaten")},
        "owner": {key: owner[key] for key in
                  ("reached_m", "absorbs", "stalled", "next_req_mm", "uneaten")},
        "supply": {("star%d" % index): supply_ratio(level.items, level.start_m, diameter)
                   for index, diameter in enumerate(stars, start=1)},
        "supply_at_cap": supply_ratio(level.items, level.start_m, level.max_m),
        "supply_at_stop": supply_ratio(level.items, level.start_m, stop),
        "star_schedule": star_schedule(level.start_m, stars, overall_rate),
        "star_progress": {
            "greedy": star_progress(greedy, stars),
            "owner": star_progress(owner, stars),
            "seconds_at": list(ABSORBS_PER_SECOND),
        },
        "original_hud_anchors": [
            {"d0_m": start, "items": count, "diameter_m": diameter}
            for start, count, diameter in ORIGINAL_HUD_ANCHORS],
        "original_step_rates": [
            {"items_from": a, "items_to": b, "rate": rate} for a, b, rate in rates],
        "original_overall_rate": overall_rate,
        "gate_diameter_ceiling": GATE_DIAMETER_CEILING,
        "ladder": rows,
        "bands": bands,
        "step_buckets_greedy": step_buckets(greedy["steps"]),
        "step_buckets_owner": step_buckets(owner["steps"]),
        "min_per_band": min_per_band,
        "min_per_rung": min_per_rung,
        "band_m": band_m,
        "supply_target": supply_target,
    }
    result["area"] = {
        "needed_m2_for_full_bands": sum(
            min_per_band * band["mean_footprint_m2"] * AREA_SPACING
            for band in bands
            if band["index"] and band["mean_footprint_m2"]),
        "used_by_content_m2": sum(item["aabb"][0] * item["aabb"][2] for item in level.items),
        "spacing": AREA_SPACING,
    }
    result["flags"] = flags(result, min_per_band=min_per_band,
                            supply_target=supply_target)
    return result


def band_table(items, start_m, stop_m, band_m=0.10):
    """Supply per ``d_req`` band: how many types/instances the level offers there.

    This is the original's own granularity (``ContentVolume/README.md`` section 4.4.1
    counts instances per 0.1 m band), and the unit the ">= 40 absorbable-new props per
    tier" rule is written in.  ``d_req`` bands, not ball-diameter rungs: a prop belongs
    to the band of the ball that can first eat it.
    """
    bands = []
    ball_mm = core.floor_mm(start_m)
    stop_mm = core.floor_mm(stop_m)
    index = 0
    while True:
        low_mm = ball_mm + int(round(index * band_m * 1000.0))
        high_mm = low_mm + int(round(band_m * 1000.0))
        if index and low_mm > stop_mm:
            break
        members = [item for item in items if low_mm < item["d_req_mm"] <= high_mm]
        bands.append({
            "index": index,
            "low_mm": low_mm,
            "high_mm": high_mm,
            "label": "%.2f-%.2f" % (low_mm / 1000.0, high_mm / 1000.0),
            "instances": len(members),
            "types": len({item["def"] for item in members}),
            "credit_m3": sum(item["credit_m3"] for item in members),
            "footprint_m2": sum(item["aabb"][0] * item["aabb"][2] for item in members),
            "mean_footprint_m2": (sum(item["aabb"][0] * item["aabb"][2] for item in members)
                                  / len(members)) if members else None,
            # How chunky the band's props are: a*b*c / longest^3.  This -- not the count --
            # is what sets the per-item step: a cube at 0.35*D pays 1.5 %/absorb, a plate
            # at the same longest extent pays 0.3 %.
            "fill": (sum(item["aabb"][0] * item["aabb"][1] * item["aabb"][2]
                         / item["longest"] ** 3 for item in members) / len(members))
                    if members else None,
            "defs": sorted({item["def"] for item in members}),
        })
        index += 1
    return bands


def flags(result, min_per_band=DEFAULT_MIN_PER_BAND, supply_target=DEFAULT_SUPPLY_TARGET):
    """The verdict lines: dead ends, thin rungs, supply shortfall, star reachability."""
    out = []
    greedy = result["greedy"]
    owner = result["owner"]
    if greedy["stalled"]:
        out.append("DEAD END: eating everything edible stops at D=%.3f m (next prop needs "
                   "%s mm); the level cannot reach %.2f m on its own content"
                   % (greedy["reached_m"],
                      "%.0f" % greedy["next_req_mm"] if greedy["next_req_mm"] else "-",
                      result["ladder_stop_m"]))
    if owner["stalled"] and not greedy["stalled"]:
        out.append("PLAY STALL: owner-like play stops at D=%.3f m while greedy reaches "
                   "%.3f m -- the items are there but not at the size the player eats"
                   % (owner["reached_m"], greedy["reached_m"]))
    empty = [row for row in result["ladder"] if row["new"] == 0]
    if empty:
        out.append("GAP: %d rung(s) bring no new prop at all (%s)"
                   % (len(empty), ", ".join("%.2f" % row["diameter_m"] for row in empty[:8])
                      + (" ..." if len(empty) > 8 else "")))
    thin = [row for row in result["ladder"]
            if row["new"] and row["runway"] is not None and row["runway"] < 1.0]
    if thin:
        worst = min(thin, key=lambda row: row["runway"])
        out.append("THIN: %d rung(s) have runway < 1 (worst %.2f at D=%.2f m, %d edible "
                   "for %d needed)" % (len(thin), worst["runway"], worst["diameter_m"],
                                       worst["cum"], worst["needed_items"]))
    thin_rungs = [row for row in result["ladder"] if row.get("thin")]
    if thin_rungs:
        worst = min(thin_rungs, key=lambda row: row["new"])
        out.append("THIN RUNGS: %d/%d rungs bring fewer than %d new props (worst %.2f m: "
                   "%d)" % (len(thin_rungs), len(result["ladder"]) - 1,
                            result["min_per_rung"], worst["diameter_m"], worst["new"]))
    short = [band for band in result["bands"] if band["instances"] < min_per_band]
    if short:
        out.append("SUPPLY: %d/%d bands have fewer than %d new props (%s)"
                   % (len(short), len(result["bands"]), min_per_band,
                      ", ".join("%s:%d" % (band["label"], band["instances"])
                                for band in short[:8])
                      + (" ..." if len(short) > 8 else "")))
    for key, value in sorted(result["supply"].items()):
        if value < supply_target:
            out.append("SUPPLY RATIO: %s is %.2fx, below the %.1fx floor"
                       % (key, value, supply_target))
    return out


# --------------------------------------------------------------------------- #
# presentation
# --------------------------------------------------------------------------- #
def _fmt(value, spec):
    return "-" if value is None else (spec % value)


def format_report(result):
    """The human table: facts, ladder, buckets, targets, verdict."""
    lines = []
    lines.append("supply plan: %s" % result["level"])
    lines.append("  placement   %s" % result["placement"])
    if result["additions"]:
        lines.append("  additions   %s (dry-run overlay)" % result["additions"])
    lines.append("  content     %d props / %d types, credited %.3f m3, growth %d mm3"
                 % (result["props"], result["types"], result["credit_total_m3"],
                    result["growth_total_mm3"]))
    lines.append("  ball        start %.4f m (V0 %.4f m3), ladder top %.2f m, ladder stop %.2f m"
                 % (result["start_m"], result["start_volume_m3"],
                    result["ladder_top_m"], result["ladder_stop_m"]))
    lines.append("  all eaten   D = %.4f m (upper bound, ignores reachability)"
                 % result["diameter_when_all_eaten_m"])
    lines.append("  greedy      D = %.4f m after %d absorbs%s"
                 % (result["greedy"]["reached_m"], result["greedy"]["absorbs"],
                    ", STALLED (next prop %s mm)" % ("%.0f" % result["greedy"]["next_req_mm"])
                    if result["greedy"]["stalled"] and result["greedy"]["next_req_mm"]
                    else ""))
    lines.append("  owner-like  D = %.4f m after %d absorbs%s"
                 % (result["owner"]["reached_m"], result["owner"]["absorbs"],
                    ", STALLED (next prop %s mm)" % ("%.0f" % result["owner"]["next_req_mm"])
                    if result["owner"]["stalled"] and result["owner"]["next_req_mm"] else ""))
    lines.append("  supply      star %s | cap %.2f m %.2fx | gate ceiling %.2f %%/absorb"
                 % ("  ".join("%s %.2fx" % (key, value)
                              for key, value in sorted(result["supply"].items())),
                    result["ladder_top_m"], result["supply_at_cap"],
                    result["gate_diameter_ceiling"] * 100.0))
    lines.append("")
    header = ("   D(m)  new*  cum   need m3  items  runway  supply  ownerN  medStep%"
              "   (* = fewer than " + str(result["min_per_rung"]) + " new props)")
    lines.append(header)
    lines.append("  " + "-" * (len(header) - 2))
    for row in result["ladder"]:
        lines.append("  %5.2f  %3d%s %4d  %8.4f  %5d  %6s  %6s  %6d  %7s"
                     % (row["diameter_m"], row["new"], "*" if row.get("thin") else " ",
                        row["cum"], row["need_m3"], row["needed_items"],
                        _fmt(row["runway"], "%.2f"), _fmt(row["supply"], "%.2fx"),
                        row["owner_absorbs"],
                        _fmt(row["owner_median_step_pct"], "%.2f")))
    lines.append("")
    lines.append("  per-item diameter step by ball bucket (original HUD: 1.4-2.3 %)")
    lines.append("   bucket          n(greedy)  med%  n(owner)  med%")
    for greedy_row, owner_row in zip(result["step_buckets_greedy"],
                                     result["step_buckets_owner"]):
        lines.append("   %-13s %9d %6s %9d %6s"
                     % ("%.2f-%.2f" % greedy_row["bucket"], greedy_row["n"],
                        _fmt(greedy_row["median_pct"], "%.3f"), owner_row["n"],
                        _fmt(owner_row["median_pct"], "%.3f")))
    lines.append("")
    lines.append("  d_req bands of %.2f m (%-2d instances target)" % (result["band_m"],
                                                                     result["min_per_band"]))
    lines.append("   band       instances types  credit m3  footprint m2  fill  @%-3d = m2"
                 % result["min_per_band"])
    for band in result["bands"]:
        need = ("%.1f" % (result["min_per_band"] * band["mean_footprint_m2"] * AREA_SPACING)
                if band["mean_footprint_m2"] else "-")
        lines.append("   %-9s %9d %5d %10.4f %13.2f %5s %10s"
                     % (band["label"], band["instances"], band["types"], band["credit_m3"],
                        band["footprint_m2"], _fmt(band["fill"], "%.2f"), need))
    lines.append("")
    lines.append("  original HUD reference: %s"
                 % "; ".join("%d items -> %.4f m" % (anchor["items"], anchor["diameter_m"])
                             for anchor in result["original_hud_anchors"]))
    lines.append("  = %s; overall %.2f %%/item"
                 % ("; ".join("+%.2f %%/item (items %d-%d)" % (row["rate"] * 100.0,
                                                              row["items_from"],
                                                              row["items_to"])
                              for row in result["original_step_rates"]),
                    result["original_overall_rate"] * 100.0))
    lines.append("  absorbs to reach a star: " + "  ".join(
        "%d star %.2f m: greedy %s / owner %s"
        % (greedy_entry["star"], greedy_entry["diameter_m"],
           "-" if greedy_entry["absorbs"] is None else greedy_entry["absorbs"],
           "-" if owner_entry["absorbs"] is None else owner_entry["absorbs"])
        for greedy_entry, owner_entry in zip(result["star_progress"]["greedy"],
                                             result["star_progress"]["owner"])))
    lines.append("  time budget %.0f s = %s absorbs at %s /s"
                 % (120.0, "/".join("%.0f" % (120.0 * rate) for rate in ABSORBS_PER_SECOND),
                    "/".join("%g" % rate for rate in ABSORBS_PER_SECOND)))
    lines.append("  target at %.2f %%/item: %s"
                 % (result["original_overall_rate"] * 100.0,
                    ", ".join("%d->%.2f m: %d items (%s absorbs/s -> %s s)"
                              % (entry["star"], entry["diameter_m"], entry["items"],
                                 "/".join("%g" % rate for rate in ABSORBS_PER_SECOND),
                                 "/".join("%.0f" % entry["seconds"][rate]
                                          for rate in ABSORBS_PER_SECOND))
                              for entry in result["star_schedule"])))
    lines.append("")
    if result["flags"]:
        lines.append("  FLAGS")
        for flag in result["flags"]:
            lines.append("   - " + flag)
    else:
        lines.append("  FLAGS: none")
    return "\n".join(lines)


def format_csv(result):
    """The ladder as CSV (one rung per line) -- the diffable form."""
    columns = ("index", "diameter_m", "ball_mm", "new", "cum", "need_m3",
               "mean_credit_m3", "needed_items", "runway", "supply",
               "owner_absorbs", "owner_median_step_pct")
    lines = [",".join(columns)]
    for row in result["ladder"]:
        lines.append(",".join("" if row[key] is None else str(row[key]) for key in columns))
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def build_parser():
    parser = argparse.ArgumentParser(
        description="Supply analysis of one placement.json: rungs, climb, supply ratio.")
    parser.add_argument("placement", help="a Docs/Design/Levels/<id>/placement.json")
    parser.add_argument("--additions", help="a proposed_additions.json dry-run patch "
                                            "(merged in memory, never written)")
    parser.add_argument("--stars", default=",".join("%g" % value for value in DEFAULT_STARS),
                        help="comma-separated star diameters (default %(default)s)")
    parser.add_argument("--step", type=float, default=DEFAULT_STEP_M,
                        help="ladder rung width in metres (default %(default)s)")
    parser.add_argument("--stop", type=float, default=None,
                        help="ladder top (default: the furthest star)")
    parser.add_argument("--band", type=float, default=0.10,
                        help="d_req band width of the tier table (default %(default)s)")
    parser.add_argument("--min-per-band", type=int, default=DEFAULT_MIN_PER_BAND,
                        help="the tier supply rule of section 4.4 (default %(default)s)")
    parser.add_argument("--min-per-rung", type=int, default=None,
                        help="new-props-per-rung floor (default: min-per-band * step/band)")
    parser.add_argument("--supply-target", type=float, default=DEFAULT_SUPPLY_TARGET,
                        help="the supply-ratio floor (default %(default)s)")
    parser.add_argument("--json", action="store_true", help="the full report as JSON")
    parser.add_argument("--csv", metavar="PATH", help="write the ladder as CSV to PATH")
    parser.add_argument("--merged-out", metavar="PATH",
                        help="write placement+additions to PATH (a temp copy for "
                             "`levelbuild.py check`; refuses to overwrite the placement)")
    parser.add_argument("--quiet", action="store_true", help="only the flags")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        stars = tuple(float(value) for value in args.stars.split(",") if value.strip())
    except ValueError:
        print("supply_plan: --stars must be comma-separated numbers", file=sys.stderr)
        return 2
    if not stars:
        print("supply_plan: --stars needs at least one diameter", file=sys.stderr)
        return 2
    placement = Path(args.placement)
    if not placement.is_file():
        print("supply_plan: cannot read %s" % placement, file=sys.stderr)
        return 2
    if args.additions and not Path(args.additions).is_file():
        print("supply_plan: cannot read %s" % args.additions, file=sys.stderr)
        return 2
    if args.merged_out and Path(args.merged_out).resolve() == placement.resolve():
        print("supply_plan: --merged-out may not be the placement itself (this tool is "
              "read-only on placement.json)", file=sys.stderr)
        return 2

    result = analyse(placement, additions=args.additions, stars=stars, step_m=args.step,
                     stop_m=args.stop, min_per_band=args.min_per_band,
                     band_m=args.band, supply_target=args.supply_target,
                     min_per_rung=args.min_per_rung)

    if args.merged_out:
        merged = merge_additions(load_placement(placement),
                                 load_additions(args.additions) if args.additions else
                                 {"props": []})
        Path(args.merged_out).write_text(core.dump_doc(merged), encoding="utf-8")
        print("wrote merged copy: %s" % args.merged_out)

    if args.csv:
        Path(args.csv).write_text(format_csv(result) + "\n", encoding="utf-8")
        print("wrote ladder csv: %s" % args.csv, flush=True)

    if args.json:
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
    elif args.quiet:
        for flag in result["flags"]:
            print(flag)
    else:
        print(format_report(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
