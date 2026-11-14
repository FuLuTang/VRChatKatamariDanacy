"""Deterministic offline round-economy model for the VRC塊魂 prototype.

This is a *design* model: standard library only, no network, no Unity, no
physics. It answers "what does the 180 s economy look like under these
parameters" and nothing else. Read README.md in this directory for what it
does and does not claim, and for the parameter table.

Public API
----------
RoundParams                     scenario + tuning inputs (frozen)
PlayerProfile                   per-player speed/absorb skill, scenario handicap
ItemStack, ZoneSpec, MapSpec    the map resource snapshot handed to the sim
MapData, load_map               live map source (layout.json + prop_catalog.csv)
live_map_spec, map_fingerprint  live MapSpec + map identity for reports
Policy                          decision hooks (model never names a policy)
EatEverything / AvoidThreats / RushBank
simulate_round(params, policy, map_spec=None, seed=0) -> RoundResult

There is NO default map snapshot. When map_spec is None the simulation reads
Docs/Design/Maps/PicnicLawn/layout.json + prop_catalog.csv through
load_map()/live_map_spec(), so the default answer tracks the map files: spawn
order comes from player_count_plans, item counts/values from the catalog and
scatter regions, pickup gates from the per-tier band plus the size-gate
override (the collapsed bench is absorbable at 1.25 m). Callers that need a
frozen experiment or a hand-built arena pass an explicit MapSpec; that is the
only way to get anything other than the live map.

Loading the map is the only file I/O in this module; every simulation given an
explicit MapSpec is pure. units_to_max_diameter stays a design calibration
constant (1356 units for the 0.30 -> 2.20 m span), not the map total: the live
map is 1284 units, so clearing the whole map reaches ~2.16 m. Everything here
is PROVISIONAL_NOT_TESTED.
"""

from __future__ import annotations

import csv
import hashlib
import json
import math
import random
from dataclasses import dataclass, field
from pathlib import Path
from typing import Sequence

# A policy may ask for the bank instead of a map zone.
BANK = "bank"

# Tier upper edges from the PicnicLawn scale table (t0/t1/t2/t3).
TIER_EDGES_M = (0.55, 0.95, 1.45, 2.20)

# End-of-match bonus categories. The original game awards +50 pt to the
# first place of three categories (research README C11/C12); the exact
# category list is a project choice, not an original value.
BONUS_CATEGORIES = (
    "swallowed_opponents",
    "max_ball_size",
    "distance_travelled",
    "items_absorbed",
)
DEFAULT_BONUS_CATEGORIES = (
    "swallowed_opponents",
    "max_ball_size",
    "items_absorbed",
)

TIME_SINKS = (
    "absorb",
    "bank_service",
    "bank_travel",
    "bank_wait",
    "no_target",
    "powerup",
    "respawn",
    "travel",
)


# --------------------------------------------------------------------------
# Parameters
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class PlayerProfile:
    """A player's skill inputs.

    speed_factor / absorb_factor scale movement and pickup throughput. Both
    are *scenario* inputs for skill sweeps, not tuned game values.
    scenario_start_units is a handicap for tests/scenarios only (it is
    unbanked volume at t=0); it is never a game parameter.
    """

    name: str = "player"
    speed_factor: float = 1.0
    absorb_factor: float = 1.0
    scenario_start_units: float = 0.0


@dataclass(frozen=True)
class RoundParams:
    """One round's tuning inputs.

    Defaults are the prototype values from Docs/config/prototype_defaults.json
    and Docs/Design/Maps/PicnicLawn/README.md where those documents define a
    value. Values marked "(model)" are modeling assumptions that the design
    documents leave open; see README.md for evidence that would settle them.
    """

    duration_s: float = 180.0
    dt_s: float = 0.25

    start_diameter_m: float = 0.30          # PicnicLawn scale table
    max_diameter_m: float = 2.20            # PicnicLawn scale table
    # (model) growth calibration: units that take the ball 0.30 -> 2.20 m.
    # Deliberately a design knob, NOT the live map total (see module docstring).
    units_to_max_diameter: float = 1356.0
    radius_exponent: float = 1.0 / 3.0      # sphere law r = cbrt(3V/4pi)
    absorb_size_ratio: float = 1.0          # item pickup gate <= ball diameter * ratio

    bank_cycle_s: float = 60.0             # original: conversion area moves every 60 s
    bank_service_s: float = 0.5             # (model) dwell to complete one deposit
    # Converting trades size for points: the ball shrinks, it is not reset.
    # Diameter-space rule is OUR invention (the original formula is unknown):
    #   post = start + (pre - start) * bank_residual_fraction
    # The clamp is applied by post_bank_diameter(). 0.0 reproduces the old
    # reset-to-start rule for comparison.
    bank_residual_fraction: float = 0.25
    # A ball smaller than this cannot convert at all; the visit is wasted.
    # 0.55 m is the t1 band floor (project-provisional).
    min_bank_diameter_m: float = 0.55

    # End-of-match bonus layer (original: 3 categories, +50 pt to first place).
    # Ties award nothing, so only a strict first place scores.
    bonus_enabled: bool = True
    bonus_points: float = 50.0
    bonus_categories: tuple[str, ...] = DEFAULT_BONUS_CATEGORIES

    respawn_delay_s: float = 3.0            # prototype_defaults
    respawn_protection_s: float = 2.0       # prototype_defaults
    swallow_diameter_ratio: float = 1.35    # prototype_defaults
    contact_dwell_s: float = 0.5            # (model) co-location needed to swallow
    steal_fraction: float = 0.25            # (model) "limited share" of victim's unbanked

    magnet_duration_s: float = 4.0          # prototype_defaults
    magnet_absorb_multiplier: float = 2.0   # (model) magnet throughput
    rocket_duration_s: float = 2.5          # prototype_defaults
    rocket_speed_multiplier: float = 1.5    # prototype_defaults
    powerup_respawn_s: float = 30.0         # (model) station refresh, docs leave open
    powerup_pickup_s: float = 1.0           # (model) pickup detour/activation busy time

    absorb_items_per_s: float = 3.0         # (model) pickup throughput
    base_speed_mps: float = 3.0             # (model) rolling speed
    size_speed_penalty: float = 0.15        # (model) speed lost at max diameter
    path_detour_factor: float = 1.35        # (model) straight-line -> walkable distance
    restock_period_s: float = 0.0           # 0 = no restock (T22 still open)
    restock_fraction: float = 0.0           # refill this share of a zone's s-tier stock

    profiles: tuple[PlayerProfile, ...] = (
        PlayerProfile("P1"),
        PlayerProfile("P2"),
    )


# --------------------------------------------------------------------------
# Map snapshot
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class ItemStack:
    definition_id: str
    count: int
    pickup_diameter_m: float
    value_units: float


@dataclass(frozen=True)
class ZoneSpec:
    zone_id: str
    position_m: tuple[float, float]  # (x, z), metres, shared world space
    stacks: tuple[ItemStack, ...]
    powerup: str | None = None
    is_bank: bool = False


@dataclass(frozen=True)
class MapSpec:
    zones: tuple[ZoneSpec, ...]
    spawn_zones: tuple[str, ...]          # indexed by player slot
    bank_anchors_m: tuple[tuple[float, float], ...]
    bank_zone_id: str


# --------------------------------------------------------------------------
# Map source (the only file I/O in this module)
# --------------------------------------------------------------------------
#
# There is deliberately no default map constant. A hand-copied snapshot rots
# the moment the map changes, and every run that forgets to pass a map then
# silently reports numbers for a map that no longer exists. load_map() parses
# the same layout.json + prop_catalog.csv that the greybox generator and the
# level validator use, so counts, values, pickup gates and spawn plans always
# match the files on disk.

MAP_DIR = Path(__file__).resolve().parents[3] / "Docs/Design/Maps/PicnicLawn"
LAYOUT_NAME = "layout.json"
CATALOG_NAME = "prop_catalog.csv"


@dataclass(frozen=True)
class MapItem:
    """One item stack as the map data describes it (catalog value + pickup gate)."""

    definition_id: str
    count: int
    pickup_m: float
    value: int


class MapData:
    """A parsed map: zones, size gates, spawns, per-zone items and totals."""

    def __init__(self, layout, catalog_rows, map_dir=None):
        self.layout = layout
        self.map_dir = None if map_dir is None else Path(map_dir)
        self.map_id = layout["map_id"]
        self.catalog = {}
        for row in catalog_rows:
            self.catalog[row["id"]] = {
                "category": row["category"],
                "size_tier": row["size_tier"],
                "game_longest_dim_m": float(row["game_longest_dim_m"]),
                "value": int(row["absorb_value_placeholder"]),
                "count": int(row["count_in_map"]),
                "role": row["role"],
                "zone_ids": [z for z in row["zone_ids"].split(";") if z],
            }
        self.zone_shape = {z["id"]: z["shape"] for z in layout["zones"]}
        self.spawns = {s["id"]: s for s in layout["spawns"]}
        self.start_diameter_m = float(layout["scale"]["ball_start_collision_diameter_m"])
        self.max_diameter_m = float(layout["scale"]["ball_max_collision_diameter_m"])
        self.bands = layout["scale"]["item_tier_pickup_diameter_m"]
        self.gates = {g["id"]: g for g in layout["size_gates"]}
        self.absorb_gate = {
            g["definition_id"]: float(g["absorbable_at_ball_diameter_m"])
            for g in layout["size_gates"] if g["type"] == "absorb"
        }
        # A size gate is attached to the zone it opens: that zone's stock only
        # counts as reachable once the ball is big enough for the gate.
        self.zone_gate = {}
        for gate in layout["size_gates"]:
            zone = gate["opens_path"]
            if zone in self.zone_shape:
                self.zone_gate[zone] = max(
                    self.zone_gate.get(zone, 0.0),
                    float(gate["absorbable_at_ball_diameter_m"]))
        self.items = {zone_id: [] for zone_id in self.zone_shape}
        for region in layout["prop_scatter"]:
            stacks = []
            for entry in region["definitions"]:
                stacks.append(MapItem(entry["id"], int(entry["count_min"]),
                                      self.pickup_m(entry["id"]),
                                      self.catalog[entry["id"]]["value"]))
            self.items[region["zone_id"]] = stacks
        # Absorbable gate objects (the collapsed benches) are not in
        # prop_scatter; place them by their position on the map.
        for gate in layout["size_gates"]:
            if gate["type"] != "absorb":
                continue
            zone_id = self.zone_at(gate["position"][0], gate["position"][2])
            if zone_id is None:
                raise ValueError(f"size gate {gate['id']} is outside every zone")
            self.items[zone_id].append(
                MapItem(gate["definition_id"], 1, self.pickup_m(gate["definition_id"]),
                        self.catalog[gate["definition_id"]]["value"]))
        self.used_definitions = ({it.definition_id for stacks in self.items.values()
                                  for it in stacks}
                                 | {s["definition_id"] for s in layout["structures"]})
        self.absorbable_total = sum(m["count"] for m in self.catalog.values()
                                    if m["role"] == "absorbable")
        self.units_total = sum(m["count"] * m["value"] for m in self.catalog.values()
                               if m["role"] == "absorbable")
        plans = layout.get("player_count_plans", {})
        self.spawn_plan = {int(key): tuple(value) for key, value in plans.items()
                           if str(key).isdigit() and isinstance(value, list)}
        self.file_sha256 = {}

    def zone_at(self, x, z):
        for zone_id, shape in self.zone_shape.items():
            if (shape["min_x"] <= x <= shape["max_x"]
                    and shape["min_z"] <= z <= shape["max_z"]):
                return zone_id
        return None

    def pickup_m(self, definition_id):
        """Pickup gate: catalog game length clamped to its band; gates override."""
        meta = self.catalog[definition_id]
        low, high = self.bands[meta["size_tier"]]
        clamped = min(max(meta["game_longest_dim_m"], low), high)
        return self.absorb_gate.get(definition_id, clamped)

    def zone_units(self, zone_ids):
        return sum(it.count * it.value for z in zone_ids for it in self.items.get(z, ()))

    def zone_items(self, zone_ids):
        return sum(it.count for z in zone_ids for it in self.items.get(z, ()))

    def category_counts(self, zone_ids, definition_ids):
        wanted = set(definition_ids)
        return {zone_id: sum(it.count for it in self.items.get(zone_id, ())
                             if it.definition_id in wanted)
                for zone_id in zone_ids}

    def reach_field(self, zone_ids, start_m):
        """How big the ball can get inside a zone set, using only edible stock.

        Size gates apply: a zone whose gate threshold is above the current
        diameter contributes nothing yet. Returns (diameter, units, ladder),
        where each ladder step is (diameter, usable zones, units, items, next).
        """
        params = RoundParams(duration_s=120.0, profiles=(PlayerProfile("probe"),))
        diameter = start_m
        ladder = []
        for _ in range(40):
            usable = [z for z in zone_ids
                      if self.zone_gate.get(z) is None
                      or self.zone_gate[z] <= diameter + 1e-9]
            units = 0
            items = 0
            for zone_id in usable:
                for item in self.items.get(zone_id, ()):
                    if item.pickup_m <= diameter + 1e-9:
                        units += item.count * item.value
                        items += item.count
            grown = min(self.max_diameter_m,
                        diameter_for_units(float(units), params))
            ladder.append((diameter, len(usable), units, items, grown))
            if grown <= diameter + 1e-9:
                return diameter, units, ladder
            diameter = grown
        return diameter, units, ladder

    def spawn_ids(self, player_count=None):
        """Spawn order for a round: player_count_plans, else layout order."""
        if player_count is not None and player_count in self.spawn_plan:
            return self.spawn_plan[player_count]
        return tuple(self.spawns)

    def map_spec(self, spawn_ids=None):
        """Build the simulation MapSpec; spawn_ids default to the layout order."""
        if spawn_ids is None:
            spawn_ids = tuple(self.spawns)
        elif isinstance(spawn_ids, str):
            spawn_ids = (spawn_ids,)
        zones = []
        for zone_id, shape in self.zone_shape.items():
            position = ((shape["min_x"] + shape["max_x"]) / 2.0,
                        (shape["min_z"] + shape["max_z"]) / 2.0)
            stacks = tuple(ItemStack(it.definition_id, it.count, it.pickup_m,
                                     float(it.value))
                           for it in self.items.get(zone_id, ()))
            zones.append(ZoneSpec(zone_id, position, stacks,
                                  is_bank=zone_id == self.layout["bank"]["zone_id"]))
        anchors = tuple((a["position"][0], a["position"][2])
                        for a in self.layout["bank"]["anchors"])
        return MapSpec(zones=tuple(zones),
                       spawn_zones=tuple(spawn_ids),
                       bank_anchors_m=anchors,
                       bank_zone_id=self.layout["bank"]["zone_id"])


def load_map(map_dir=None):
    """Parse the live map (layout.json + prop_catalog.csv). No snapshot fallback."""
    directory = Path(map_dir) if map_dir is not None else MAP_DIR
    layout_path = directory / LAYOUT_NAME
    catalog_path = directory / CATALOG_NAME
    if not layout_path.is_file() or not catalog_path.is_file():
        raise FileNotFoundError(
            f"map files not found under {directory}: expected {LAYOUT_NAME} and "
            f"{CATALOG_NAME}. round_model has no built-in snapshot; point map_dir "
            "at a real map or pass an explicit MapSpec.")
    layout = json.loads(layout_path.read_text(encoding="utf-8"))
    with catalog_path.open(encoding="utf-8", newline="") as stream:
        rows = list(csv.DictReader(stream))
    data = MapData(layout, rows, map_dir=directory)
    data.file_sha256 = {
        "layout_sha256": hashlib.sha256(layout_path.read_bytes()).hexdigest(),
        "catalog_sha256": hashlib.sha256(catalog_path.read_bytes()).hexdigest(),
    }
    return data


_LIVE_MAP_CACHE = {}


def live_map():
    """The repository map, parsed once per process."""
    if "data" not in _LIVE_MAP_CACHE:
        _LIVE_MAP_CACHE["data"] = load_map()
    return _LIVE_MAP_CACHE["data"]


def live_map_spec(player_count=None, map_dir=None):
    """MapSpec for the live map; spawn order follows player_count_plans."""
    data = load_map(map_dir) if map_dir is not None else live_map()
    return data.map_spec(data.spawn_ids(player_count))


def map_fingerprint(map_data=None):
    """Map identity + totals, for report headers and test assertions."""
    data = live_map() if map_data is None else map_data
    return {
        "map_id": data.map_id,
        "zones": len(data.zone_shape),
        "absorbable_instances_total": data.absorbable_total,
        "gain_units_total": data.units_total,
        **data.file_sha256,
    }


# --------------------------------------------------------------------------
# Pure math
# --------------------------------------------------------------------------


def _sphere_volume_m3(diameter_m: float) -> float:
    return math.pi / 6.0 * diameter_m ** 3


def diameter_for_units(units: float, params: RoundParams) -> float:
    """Sphere-equivalent diameter for accumulated volume units.

    volume(V) = V0 + units * cubic_metres_per_unit, so with the default
    exponent 1/3 this is exactly r = cbrt(3V / 4pi). The exponent is exposed
    only as a growth-shape sensitivity probe (README.md); 1/3 is the mapping
    the technical contract documents.
    """
    v0 = _sphere_volume_m3(params.start_diameter_m)
    v_max = _sphere_volume_m3(params.max_diameter_m)
    per_unit = (v_max - v0) / params.units_to_max_diameter
    volume = v0 + max(0.0, units) * per_unit
    diameter = params.start_diameter_m * (volume / v0) ** params.radius_exponent
    return min(diameter, params.max_diameter_m)


def units_for_diameter(diameter_m: float, params: RoundParams) -> float:
    """Inverse of diameter_for_units(), clamped to [start, max].

    Used to price the already-scored residual volume that survives a
    conversion, so that residual can never be scored twice.
    """
    clamped = min(max(diameter_m, params.start_diameter_m), params.max_diameter_m)
    v0 = _sphere_volume_m3(params.start_diameter_m)
    v_max = _sphere_volume_m3(params.max_diameter_m)
    per_unit = (v_max - v0) / params.units_to_max_diameter
    volume = v0 * (clamped / params.start_diameter_m) ** (1.0 / params.radius_exponent)
    return max(0.0, (volume - v0) / per_unit)


def post_bank_diameter(pre_bank_diameter_m: float, params: RoundParams) -> float:
    """Ball diameter after a conversion: shrink by a fraction of its growth.

    This exact diameter-space formula is a PROJECT INVENTION: the original
    only confirms that converting makes the ball smaller (research README
    C2), not by how much or in which space. 0.0 means "reset to start".
    Clamped to at least start_diameter_m.
    """
    residual = (params.start_diameter_m
                + (pre_bank_diameter_m - params.start_diameter_m)
                * params.bank_residual_fraction)
    return max(params.start_diameter_m, residual)


def can_convert(diameter_m: float, params: RoundParams) -> bool:
    """Whether a ball is big enough to convert at all (project rule)."""
    return diameter_m + 1e-9 >= params.min_bank_diameter_m


def can_absorb(pickup_diameter_m: float, ball_diameter_m: float,
               absorb_size_ratio: float) -> bool:
    """Prototype absorb gate: item pickup diameter <= ball diameter * ratio.

    The project documents a gameplay pickup diameter per item and gates the
    ball's diameter against it (PicnicLawn README sections 2 and 9); the
    boundary is inclusive. There is no "1.35x" style ratio for props -- that
    ratio is the PvP swallow rule.
    """
    return pickup_diameter_m <= ball_diameter_m * absorb_size_ratio + 1e-9


# --------------------------------------------------------------------------
# Policies
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class ZoneView:
    zone_id: str
    remaining_items: int
    absorbable_items: int
    remaining_value_units: float
    absorbable_value_units: float
    distance_m: float
    travel_s: float
    is_bank: bool


@dataclass(frozen=True)
class ThreatView:
    """A living, unprotected player whose ball could swallow mine."""

    zone_id: str
    diameter_m: float


@dataclass(frozen=True)
class PolicyContext:
    time_s: float
    duration_s: float
    player_index: int
    zone_id: str
    diameter_m: float
    unbanked_units: float
    banked_score: float
    protected: bool
    powerup: str | None
    speed_mps: float
    can_bank: bool
    zones: tuple[ZoneView, ...]
    threats: tuple[ThreatView, ...]


class Policy:
    """Decision hooks. The model owns no policy names or thresholds."""

    name = "policy"

    def wants_bank(self, ctx: PolicyContext) -> bool:
        return False

    def must_flee(self, ctx: PolicyContext) -> str | None:
        return None

    def choose(self, ctx: PolicyContext) -> str | None:
        return None


@dataclass(frozen=True)
class EatEverything(Policy):
    """Greedily clear the richest reachable zone; bank late."""

    name: str = "eat_everything"
    bank_at_units: float = 400.0
    bank_before_end_s: float = 30.0

    def wants_bank(self, ctx: PolicyContext) -> bool:
        if ctx.unbanked_units <= 0 or not ctx.can_bank:
            return False
        return (ctx.unbanked_units >= self.bank_at_units
                or ctx.time_s >= ctx.duration_s - self.bank_before_end_s)

    def choose(self, ctx: PolicyContext) -> str | None:
        options = [z for z in ctx.zones
                   if z.absorbable_items > 0 and z.zone_id != ctx.zone_id]
        if options:
            return max(options, key=lambda z: (z.absorbable_value_units / (z.travel_s + 1.0),
                                               z.zone_id)).zone_id
        if ctx.unbanked_units > 0 and ctx.can_bank:
            return BANK
        # Nothing edible anywhere: walk to the nearest zone that still has
        # props, so the result shows the stranded/roaming time instead of
        # hiding it as a stationary player.
        stocked = [z for z in ctx.zones
                   if z.remaining_items > 0 and z.zone_id != ctx.zone_id]
        if stocked:
            return min(stocked, key=lambda z: (z.distance_m, z.zone_id)).zone_id
        return None


@dataclass(frozen=True)
class AvoidThreats(Policy):
    """Prefer safe zones, flee a bigger ball, bank before it is worth losing."""

    name: str = "avoid_threats"
    bank_at_units: float = 250.0
    bank_before_end_s: float = 25.0

    def wants_bank(self, ctx: PolicyContext) -> bool:
        if ctx.unbanked_units <= 0 or not ctx.can_bank:
            return False
        if ctx.time_s >= ctx.duration_s - self.bank_before_end_s:
            return True
        # Bank camping: do not walk into a bank a bigger ball is sitting on;
        # keep farming until it leaves or the round enters its last seconds.
        bank = next((z for z in ctx.zones if z.is_bank), None)
        if bank is not None and any(t.zone_id == bank.zone_id for t in ctx.threats):
            return False
        return ctx.unbanked_units >= self.bank_at_units

    def must_flee(self, ctx: PolicyContext) -> str | None:
        here = [t for t in ctx.threats if t.zone_id == ctx.zone_id]
        if not here:
            return None
        options = [z for z in ctx.zones
                   if z.absorbable_items > 0 and z.zone_id != ctx.zone_id]
        if not options:
            return None
        return max(options, key=lambda z: (z.distance_m, z.zone_id)).zone_id

    def choose(self, ctx: PolicyContext) -> str | None:
        danger = {t.zone_id for t in ctx.threats}
        options = [z for z in ctx.zones
                   if z.absorbable_items > 0
                   and z.zone_id != ctx.zone_id
                   and z.zone_id not in danger]
        if options:
            return max(options, key=lambda z: (z.absorbable_value_units / (z.travel_s + 1.0),
                                               z.zone_id)).zone_id
        safe_stock = [z for z in ctx.zones
                      if z.remaining_items > 0
                      and z.zone_id != ctx.zone_id
                      and z.zone_id not in danger]
        if safe_stock:
            return min(safe_stock, key=lambda z: (z.distance_m, z.zone_id)).zone_id
        if ctx.unbanked_units > 0 and ctx.can_bank:
            return BANK
        return None


@dataclass(frozen=True)
class RushBank(Policy):
    """Bank in small batches so a steal can never wipe the whole lap."""

    name: str = "rush_bank"
    bank_at_units: float = 100.0
    bank_before_end_s: float = 20.0
    proximity_weight: float = 2.0

    def wants_bank(self, ctx: PolicyContext) -> bool:
        if ctx.unbanked_units <= 0 or not ctx.can_bank:
            return False
        return (ctx.unbanked_units >= self.bank_at_units
                or ctx.time_s >= ctx.duration_s - self.bank_before_end_s)

    def choose(self, ctx: PolicyContext) -> str | None:
        options = [z for z in ctx.zones
                   if z.absorbable_items > 0 and z.zone_id != ctx.zone_id]
        if options:
            # Prefer value close to the bank: shorter laps, more deposits.
            return max(options, key=lambda z: (
                z.absorbable_value_units / (self.proximity_weight * z.travel_s + 1.0),
                z.zone_id)).zone_id
        if ctx.unbanked_units > 0 and ctx.can_bank:
            return BANK
        stocked = [z for z in ctx.zones
                   if z.remaining_items > 0 and z.zone_id != ctx.zone_id]
        if stocked:
            return min(stocked, key=lambda z: (z.distance_m, z.zone_id)).zone_id
        return None


# --------------------------------------------------------------------------
# Round state and result
# --------------------------------------------------------------------------


@dataclass
class PlayerState:
    index: int
    profile: PlayerProfile
    policy: Policy
    spawn_zone: str
    zone_id: str
    units: float
    diameter_m: float
    carried_units: float = 0.0
    bonus_score: float = 0.0
    bonus_wins: tuple[str, ...] = ()
    distance_travelled_m: float = 0.0
    target: str | None = None
    travelling: bool = False
    leg_origin: str | None = None
    leg_length_m: float = 0.0
    distance_left_m: float = 0.0
    leg_anchor_index: int = 0
    banking: bool = False
    bank_progress_s: float = 0.0
    dead_until_s: float = 0.0
    protection_until_s: float = 0.0
    powerup: str | None = None
    powerup_ends_at_s: float = 0.0
    busy_until_s: float = 0.0
    absorb_credit: float = 0.0
    score: float = 0.0
    absorbed_items: int = 0
    absorbed_units: float = 0.0
    deposits: int = 0
    deposit_units: float = 0.0
    swallows: int = 0
    swallowed: int = 0
    stolen_units: float = 0.0
    peak_diameter_m: float = 0.0
    reach_times_s: dict[float, float] = field(default_factory=dict)
    time_sinks: dict[str, float] = field(default_factory=dict)

    def protected(self, now_s: float) -> bool:
        return now_s < self.protection_until_s

    def dead(self, now_s: float) -> bool:
        return now_s < self.dead_until_s

    def speed_mps(self, params: RoundParams, now_s: float) -> float:
        span = max(1e-9, params.max_diameter_m - params.start_diameter_m)
        grown = (self.diameter_m - params.start_diameter_m) / span
        grown = min(1.0, max(0.0, grown))
        speed = params.base_speed_mps * self.profile.speed_factor
        speed *= 1.0 - params.size_speed_penalty * grown
        if self.powerup == "rocket" and now_s < self.powerup_ends_at_s:
            speed *= params.rocket_speed_multiplier
        return speed


@dataclass
class _World:
    params: RoundParams
    map: MapSpec
    positions: dict[str, tuple[float, float]]
    pools: dict[str, dict[str, int]]
    defs: dict[str, dict[str, ItemStack]]
    initial: dict[str, dict[str, int]]
    stations: dict[str, float]
    contacts: dict[tuple[int, int], float]
    events: list[tuple[float, str, int, str]]
    next_restock_s: float = 0.0
    time_s: float = 0.0
    players: list["PlayerState"] = field(default_factory=list)


@dataclass(frozen=True)
class PlayerResult:
    index: int
    name: str
    policy: str
    spawn_zone: str
    score_units: float
    bank_score_units: float
    bonus_points: float
    bonus_wins: tuple[str, ...]
    distance_travelled_m: float
    absorbed_items: int
    absorbed_units: float
    deposits: int
    deposit_units: float
    swallows: int
    swallowed: int
    stolen_units: float
    respawn_downtime_s: float
    peak_diameter_m: float
    final_diameter_m: float
    growth_curve_m: tuple[float, ...]
    reach_times_s: tuple[tuple[float, float | None], ...]
    time_sinks: tuple[tuple[str, float], ...]


@dataclass(frozen=True)
class RoundResult:
    seed: int
    duration_s: float
    bucket_seconds: tuple[float, ...]
    players: tuple[PlayerResult, ...]
    events: tuple[tuple[float, str, int, str], ...]
    winners: tuple[int, ...]

    @property
    def winner(self) -> str:
        if len(self.winners) != 1:
            return "tie"
        return self.players[self.winners[0]].name

    @property
    def total_score(self) -> float:
        return sum(p.score_units for p in self.players)

    @property
    def total_bonus(self) -> float:
        return sum(p.bonus_points for p in self.players)


# --------------------------------------------------------------------------
# Simulation
# --------------------------------------------------------------------------


def simulate_round(params: RoundParams,
                   policy: Policy | Sequence[Policy],
                   map_spec: MapSpec | None = None,
                   seed: int = 0) -> RoundResult:
    """Simulate one round. Deterministic for a given seed.

    The only stochastic element is the per-step player processing order:
    contested pickups in the same zone are resolved in a seeded shuffle order
    instead of by player index. No other randomness is used.
    """
    _validate_params(params)
    if map_spec is None:
        map_spec = live_map_spec(len(params.profiles))
    if len(map_spec.spawn_zones) < len(params.profiles):
        raise ValueError("map has fewer spawn zones than player profiles")
    _validate_map(map_spec)
    policies = _broadcast_policies(policy, len(params.profiles))
    rng = random.Random(seed)

    positions = {z.zone_id: z.position_m for z in map_spec.zones}
    pools = {z.zone_id: {s.definition_id: s.count for s in z.stacks}
             for z in map_spec.zones}
    defs = {z.zone_id: {s.definition_id: s for s in z.stacks}
            for z in map_spec.zones}
    initial = {zone: dict(counts) for zone, counts in pools.items()}
    stations = {z.zone_id: 0.0 for z in map_spec.zones if z.powerup}
    world = _World(params=params, map=map_spec, positions=positions,
                   pools=pools, defs=defs, initial=initial, stations=stations,
                   contacts={}, events=[])

    players: list[PlayerState] = []
    for i, profile in enumerate(params.profiles):
        spawn = map_spec.spawn_zones[i]
        start_units = max(0.0, profile.scenario_start_units)
        diameter = diameter_for_units(start_units, params)
        players.append(PlayerState(
            index=i, profile=profile, policy=policies[i], spawn_zone=spawn,
            zone_id=spawn, units=start_units, diameter_m=diameter,
            peak_diameter_m=diameter,
            protection_until_s=0.0,
            time_sinks={key: 0.0 for key in TIME_SINKS},
        ))
    world.players = players

    steps = max(1, int(round(params.duration_s / params.dt_s)))
    dt = params.duration_s / steps
    bucket_count = max(1, int(math.ceil(params.duration_s / 15.0)))
    curves = [[p.diameter_m for _ in range(bucket_count)] for p in players]

    for step in range(steps):
        now = step * dt
        world.time_s = now
        _restock(world)
        order = list(range(len(players)))
        rng.shuffle(order)
        for index in order:
            _step_player(world, players[index], dt)
        _resolve_contacts(world, players, dt)
        bucket = min(int(now // 15.0), bucket_count - 1)
        for p in players:
            curves[p.index][bucket] = p.diameter_m
            p.peak_diameter_m = max(p.peak_diameter_m, p.diameter_m)
            for edge in TIER_EDGES_M:
                if p.diameter_m >= edge and edge not in p.reach_times_s:
                    p.reach_times_s[edge] = now + dt

    _award_bonuses(world, players)

    results = []
    for p in players:
        sink = {k: p.time_sinks.get(k, 0.0) for k in TIME_SINKS}
        results.append(PlayerResult(
            index=p.index, name=p.profile.name, policy=p.policy.name,
            spawn_zone=p.spawn_zone, score_units=p.score,
            bank_score_units=p.deposit_units,
            bonus_points=p.bonus_score, bonus_wins=p.bonus_wins,
            distance_travelled_m=p.distance_travelled_m,
            absorbed_items=p.absorbed_items, absorbed_units=p.absorbed_units,
            deposits=p.deposits, deposit_units=p.deposit_units,
            swallows=p.swallows, swallowed=p.swallowed,
            stolen_units=p.stolen_units,
            respawn_downtime_s=sink["respawn"],
            peak_diameter_m=p.peak_diameter_m,
            final_diameter_m=p.diameter_m,
            growth_curve_m=tuple(curves[p.index]),
            reach_times_s=tuple((edge, p.reach_times_s.get(edge))
                                for edge in TIER_EDGES_M),
            time_sinks=tuple(sorted(sink.items())),
        ))

    if results:
        best = max(r.score_units for r in results)
        winners = tuple(r.index for r in results if r.score_units == best)
    else:  # pragma: no cover - validation already rejects an empty roster
        winners = ()
    buckets = tuple(15.0 * (i + 1) for i in range(bucket_count))
    return RoundResult(seed=seed, duration_s=params.duration_s,
                       bucket_seconds=buckets, players=tuple(results),
                       events=tuple(world.events), winners=winners)


def _step_player(world: _World, p: PlayerState, dt: float) -> None:
    params = world.params
    now = world.time_s

    if p.dead(now):
        _sink(p, "respawn", dt)
        return
    if p.dead_until_s > 0.0 and not p.dead(now):
        _revive(p, world)

    if p.powerup and now >= p.powerup_ends_at_s:
        p.powerup = None

    if p.busy_until_s > now:
        _sink(p, "powerup", dt)
        return

    if p.banking:
        p.bank_progress_s += dt
        _sink(p, "bank_service", dt)
        if p.bank_progress_s + 1e-9 >= params.bank_service_s:
            _deposit(p, world)
        return

    if p.travelling:
        _advance_travel(world, p, dt)
        return

    if _pickup_powerup(world, p):
        _sink(p, "powerup", dt)
        return

    ctx = _build_context(world, p)

    flee = p.policy.must_flee(ctx)
    if flee is not None:
        if _start_trip(world, p, flee):
            _sink(p, "travel", dt)
            return

    if p.policy.wants_bank(ctx) and p.units > 0.0 and ctx.can_bank:
        if p.zone_id == world.map.bank_zone_id:
            if p.protected(now):
                _sink(p, "bank_wait", dt)
            else:
                p.banking = True
                p.bank_progress_s = 0.0
                _sink(p, "bank_service", dt)
                if p.bank_progress_s + 1e-9 >= params.bank_service_s:
                    _deposit(p, world)
            return
        if _start_trip(world, p, BANK):
            _sink(p, "bank_travel", dt)
            return

    if _absorb_step(world, p, dt):
        return

    target = p.policy.choose(ctx)
    if target is not None and target != p.zone_id:
        if _start_trip(world, p, target):
            _sink(p, "travel", dt)
            return
    _sink(p, "no_target", dt)


def _sink(p: PlayerState, key: str, seconds: float) -> None:
    p.time_sinks[key] = p.time_sinks.get(key, 0.0) + seconds


def _absorb_step(world: _World, p: PlayerState, dt: float) -> bool:
    """Absorb while the current zone still has something edible.

    Fractional credit means a step can end without taking a whole item; that
    must not be read as "this zone is finished" or the player would leave
    mid-sweep. Returns True while the player is absorbing (or has just
    cleared the zone this step).
    """
    params = world.params
    rate = params.absorb_items_per_s * p.profile.absorb_factor
    if p.powerup == "magnet" and world.time_s < p.powerup_ends_at_s:
        rate *= params.magnet_absorb_multiplier
    p.absorb_credit += rate * dt

    taken = 0
    while p.absorb_credit >= 1.0:
        item = _take_item(world, p)
        if item is None:
            p.absorb_credit = 0.0
            break
        p.absorb_credit -= 1.0
        p.units += item.value_units
        p.absorbed_units += item.value_units
        p.absorbed_items += 1
        p.diameter_m = diameter_for_units(p.units + p.carried_units, params)
        taken += 1

    if taken or _zone_has_edible(world, p):
        _sink(p, "absorb", dt)
        return True
    p.absorb_credit = 0.0
    return False


def _zone_has_edible(world: _World, p: PlayerState) -> bool:
    counts = world.pools[p.zone_id]
    defs = world.defs[p.zone_id]
    for definition_id, count in counts.items():
        if count <= 0:
            continue
        stack = defs[definition_id]
        if can_absorb(stack.pickup_diameter_m, p.diameter_m,
                      world.params.absorb_size_ratio):
            return True
    return False


def _take_item(world: _World, p: PlayerState) -> ItemStack | None:
    counts = world.pools[p.zone_id]
    defs = world.defs[p.zone_id]
    best: ItemStack | None = None
    for definition_id, count in counts.items():
        if count <= 0:
            continue
        stack = defs[definition_id]
        if not can_absorb(stack.pickup_diameter_m, p.diameter_m,
                          world.params.absorb_size_ratio):
            continue
        if best is None or stack.value_units > best.value_units:
            best = stack
    if best is None:
        return None
    counts[best.definition_id] -= 1
    return best


def _build_context(world: _World, p: PlayerState) -> PolicyContext:
    params = world.params
    threats = tuple(
        ThreatView(zone_id=q.zone_id, diameter_m=q.diameter_m)
        for q in world.players
        if q.index != p.index
        and not q.dead(world.time_s)
        and not q.protected(world.time_s)
        and not p.protected(world.time_s)
        and q.diameter_m >= p.diameter_m * params.swallow_diameter_ratio
    )
    views = []
    for zone in world.map.zones:
        remaining_items, remaining_value, edible_items, edible_value = \
            _zone_stock(world, p, zone)
        distance = _distance(world, p.zone_id, zone.zone_id)
        views.append(ZoneView(
            zone_id=zone.zone_id,
            remaining_items=remaining_items,
            absorbable_items=edible_items,
            remaining_value_units=remaining_value,
            absorbable_value_units=edible_value,
            distance_m=distance,
            travel_s=distance / max(1e-9, p.speed_mps(params, world.time_s)),
            is_bank=zone.is_bank,
        ))
    return PolicyContext(
        time_s=world.time_s, duration_s=params.duration_s,
        player_index=p.index, zone_id=p.zone_id,
        diameter_m=p.diameter_m, unbanked_units=p.units,
        banked_score=p.score, protected=p.protected(world.time_s),
        powerup=p.powerup, speed_mps=p.speed_mps(params, world.time_s),
        can_bank=can_convert(p.diameter_m, params),
        zones=tuple(views), threats=threats,
    )


def _zone_stock(world: _World, p: PlayerState, zone: ZoneSpec) -> tuple[int, float, int, float]:
    counts = world.pools[zone.zone_id]
    defs = world.defs[zone.zone_id]
    remaining_items = 0
    remaining_value = 0.0
    edible_items = 0
    edible_value = 0.0
    limit = p.diameter_m * world.params.absorb_size_ratio
    for definition_id, count in counts.items():
        if count <= 0:
            continue
        stack = defs[definition_id]
        remaining_items += count
        remaining_value += count * stack.value_units
        if stack.pickup_diameter_m <= limit + 1e-9:
            edible_items += count
            edible_value += count * stack.value_units
    return remaining_items, remaining_value, edible_items, edible_value


def _start_trip(world: _World, p: PlayerState, target: str) -> bool:
    if target == p.zone_id:
        return False
    p.travelling = True
    p.target = target
    p.leg_origin = p.zone_id
    p.banking = False
    p.bank_progress_s = 0.0
    p.leg_anchor_index = active_anchor_index(world.time_s, world.params)
    p.leg_length_m = _distance(world, p.zone_id, target)
    p.distance_left_m = p.leg_length_m
    if p.distance_left_m <= 1e-9:
        _arrive(world, p)
    return True


def _advance_travel(world: _World, p: PlayerState, dt: float) -> None:
    params = world.params
    if p.target == BANK:
        index = active_anchor_index(world.time_s, params)
        if index != p.leg_anchor_index and p.leg_origin is not None:
            travelled = max(0.0, p.leg_length_m - p.distance_left_m)
            origin = world.positions[p.leg_origin]
            anchor = world.map.bank_anchors_m[index]
            new_length = _euclid(origin, anchor) * params.path_detour_factor
            p.leg_anchor_index = index
            p.leg_length_m = new_length
            p.distance_left_m = max(0.0, new_length - travelled)

    speed = p.speed_mps(params, world.time_s)
    step_m = min(speed * dt, max(0.0, p.distance_left_m))
    p.distance_travelled_m += step_m
    p.distance_left_m -= speed * dt
    _sink(p, "bank_travel" if p.target == BANK else "travel", dt)
    if p.distance_left_m <= 1e-9:
        _arrive(world, p)


def _arrive(world: _World, p: PlayerState) -> None:
    if p.target == BANK:
        p.zone_id = world.map.bank_zone_id
    elif p.target is not None:
        p.zone_id = p.target
    p.travelling = False
    p.target = None
    p.leg_origin = None
    p.distance_left_m = 0.0


def _distance(world: _World, from_zone: str, target: str) -> float:
    """Walkable distance estimate between a zone and a zone or the bank."""
    origin = world.positions[from_zone]
    params = world.params
    if target == BANK or target == world.map.bank_zone_id:
        if from_zone == world.map.bank_zone_id:
            return 0.0
        index = active_anchor_index(world.time_s, params)
        return _euclid(origin, world.map.bank_anchors_m[index]) * params.path_detour_factor
    target_pos = world.positions[target]
    return _euclid(origin, target_pos) * params.path_detour_factor


def _euclid(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def active_anchor_index(now_s: float, params: RoundParams) -> int:
    """Which bank anchor is active at now_s (deterministic rotation)."""
    if params.bank_cycle_s <= 0:
        return 0
    return int(max(0.0, now_s) // params.bank_cycle_s) % 4


def _pickup_powerup(world: _World, p: PlayerState) -> bool:
    zone = _zone(world, p.zone_id)
    if zone is None or zone.powerup is None:
        return False
    now = world.time_s
    if p.powerup is not None or p.busy_until_s > now:
        return False
    if p.protected(now):
        return False
    if world.stations.get(zone.zone_id, 0.0) > now:
        return False
    params = world.params
    world.stations[zone.zone_id] = now + params.powerup_respawn_s
    p.powerup = zone.powerup
    duration = (params.magnet_duration_s if zone.powerup == "magnet"
                else params.rocket_duration_s)
    p.powerup_ends_at_s = now + duration
    p.busy_until_s = now + params.powerup_pickup_s
    world.events.append((round(now, 3), "powerup", p.index, zone.powerup))
    return True


def _deposit(p: PlayerState, world: _World) -> None:
    amount = p.units
    if amount <= 0.0:
        p.banking = False
        p.bank_progress_s = 0.0
        return
    params = world.params
    pre_bank_diameter = p.diameter_m
    p.score += amount
    p.deposit_units += amount
    p.deposits += 1
    # The ball keeps a fraction of its growth as a "carried" residual. That
    # residual is already scored, so it is NOT part of p.units and can never
    # be converted again; only newly absorbed volume is.
    p.carried_units = units_for_diameter(
        post_bank_diameter(pre_bank_diameter, params), params)
    p.units = 0.0
    p.diameter_m = post_bank_diameter(pre_bank_diameter, params)
    p.banking = False
    p.bank_progress_s = 0.0
    p.powerup = None
    p.busy_until_s = 0.0
    p.target = None
    p.absorb_credit = 0.0
    world.events.append((round(world.time_s, 3), "bank", p.index, f"{amount:.1f}"))


def _revive(p: PlayerState, world: _World) -> None:
    params = world.params
    p.units = 0.0
    p.carried_units = 0.0
    p.diameter_m = params.start_diameter_m
    p.dead_until_s = 0.0
    p.zone_id = p.spawn_zone
    p.target = None
    p.travelling = False
    p.banking = False
    p.bank_progress_s = 0.0
    p.distance_left_m = 0.0
    p.powerup = None
    p.busy_until_s = 0.0
    p.protection_until_s = world.time_s + params.respawn_protection_s
    p.absorb_credit = 0.0
    world.events.append((round(world.time_s, 3), "respawn", p.index, ""))


def _resolve_contacts(world: _World, players: Sequence[PlayerState], dt: float) -> None:
    params = world.params
    now = world.time_s
    alive = [p for p in players if not p.dead(now)]
    seen: set[tuple[int, int]] = set()
    for i, a in enumerate(alive):
        for b in alive[i + 1:]:
            if a.zone_id != b.zone_id:
                continue
            if a.protected(now) or b.protected(now):
                continue
            attacker = victim = None
            if a.diameter_m >= b.diameter_m * params.swallow_diameter_ratio:
                attacker, victim = a, b
            elif b.diameter_m >= a.diameter_m * params.swallow_diameter_ratio:
                attacker, victim = b, a
            if attacker is None or victim is None:
                continue
            key = (attacker.index, victim.index)
            seen.add(key)
            world.contacts[key] = world.contacts.get(key, 0.0) + dt
            if world.contacts[key] + 1e-9 >= params.contact_dwell_s:
                _swallow(world, attacker, victim)
                world.contacts.pop(key, None)
    for key in list(world.contacts):
        if key not in seen:
            world.contacts.pop(key, None)


def _swallow(world: _World, attacker: PlayerState, victim: PlayerState) -> None:
    params = world.params
    stolen = math.floor(max(0.0, victim.units) * params.steal_fraction)
    attacker.units += stolen
    attacker.stolen_units += stolen
    attacker.diameter_m = diameter_for_units(attacker.units + attacker.carried_units,
                                              params)
    attacker.swallows += 1

    victim.units = 0.0
    victim.carried_units = 0.0
    victim.diameter_m = params.start_diameter_m
    victim.dead_until_s = world.time_s + params.respawn_delay_s
    victim.swallowed += 1
    victim.target = None
    victim.travelling = False
    victim.banking = False
    victim.bank_progress_s = 0.0
    victim.powerup = None
    victim.busy_until_s = 0.0
    victim.absorb_credit = 0.0

    world.events.append((round(world.time_s, 3), "swallow", attacker.index,
                         f"victim={victim.index} stolen={stolen}"))


def _restock(world: _World) -> None:
    params = world.params
    if params.restock_period_s <= 0 or params.restock_fraction <= 0:
        return
    if world.time_s + 1e-9 < world.next_restock_s:
        return
    world.next_restock_s = world.time_s + params.restock_period_s
    for zone in world.map.zones:
        counts = world.pools[zone.zone_id]
        for definition_id, count in list(counts.items()):
            stack = world.defs[zone.zone_id][definition_id]
            if stack.pickup_diameter_m > params.start_diameter_m:
                continue
            target = world.initial[zone.zone_id][definition_id]
            add = int(math.floor(world.initial[zone.zone_id][definition_id]
                                 * params.restock_fraction))
            if add <= 0:
                continue
            counts[definition_id] = min(target, count + add)


def _bonus_metric(p: PlayerState, category: str) -> float:
    """The single number that ranks a player in one bonus category."""
    if category == "swallowed_opponents":
        return float(p.swallows)
    if category == "max_ball_size":
        return p.peak_diameter_m
    if category == "distance_travelled":
        return p.distance_travelled_m
    if category == "items_absorbed":
        return float(p.absorbed_items)
    raise ValueError(f"unknown bonus category: {category}")


def _award_bonuses(world: _World, players: Sequence[PlayerState]) -> None:
    """Award each enabled category's strict first place at match end.

    Ties award nothing: the original gives +50 pt to the category's first
    place, and "everyone tied at zero gets +50" would silently inflate every
    score. Which tie rule the original uses is unknown, so this is a project
    choice. A one-player round is a test artifact and never gets bonuses.
    """
    params = world.params
    if not params.bonus_enabled or len(players) < 2:
        return
    for category in params.bonus_categories:
        values = [_bonus_metric(p, category) for p in players]
        best = max(values)
        leaders = [i for i, value in enumerate(values)
                   if value + 1e-9 >= best]
        if len(leaders) != 1:
            continue
        winner = players[leaders[0]]
        winner.score += params.bonus_points
        winner.bonus_score += params.bonus_points
        winner.bonus_wins = winner.bonus_wins + (category,)
        world.events.append((round(params.duration_s, 3), "bonus",
                             winner.index, category))


def _zone(world: _World, zone_id: str) -> ZoneSpec | None:
    for zone in world.map.zones:
        if zone.zone_id == zone_id:
            return zone
    return None


# --------------------------------------------------------------------------
# Validation
# --------------------------------------------------------------------------


def _broadcast_policies(policy: Policy | Sequence[Policy], count: int) -> list[Policy]:
    if isinstance(policy, Policy):
        return [policy] * count
    policies = list(policy)
    if len(policies) != count:
        raise ValueError(f"expected {count} policies, got {len(policies)}")
    for item in policies:
        if not isinstance(item, Policy):
            raise TypeError("policy must be a Policy instance")
    return policies


def _validate_params(params: RoundParams) -> None:
    if not 1 <= len(params.profiles) <= 4:
        raise ValueError("a round needs 1-4 player profiles")
    if not params.duration_s > 0 or not params.dt_s > 0:
        raise ValueError("duration and dt must be positive")
    if params.dt_s > params.duration_s:
        raise ValueError("dt must not exceed the duration")
    if not 0 < params.start_diameter_m < params.max_diameter_m:
        raise ValueError("start diameter must be positive and below max diameter")
    if params.units_to_max_diameter <= 0:
        raise ValueError("units_to_max_diameter must be positive")
    if params.radius_exponent <= 0:
        raise ValueError("radius_exponent must be positive")
    if not 0.0 <= params.bank_residual_fraction <= 1.0:
        raise ValueError("bank_residual_fraction must be in [0, 1]")
    if not 0.0 < params.min_bank_diameter_m <= params.max_diameter_m:
        raise ValueError("min_bank_diameter_m must be positive and at most max_diameter_m")
    if params.bonus_points < 0:
        raise ValueError("bonus_points must be non-negative")
    unknown = [c for c in params.bonus_categories if c not in BONUS_CATEGORIES]
    if unknown:
        raise ValueError(f"unknown bonus categories: {unknown}")
    if len(set(params.bonus_categories)) != len(params.bonus_categories):
        raise ValueError("bonus_categories must not repeat")
    for name in ("absorb_size_ratio", "swallow_diameter_ratio",
                 "magnet_absorb_multiplier", "rocket_speed_multiplier",
                 "base_speed_mps", "absorb_items_per_s", "path_detour_factor"):
        if getattr(params, name) <= 0:
            raise ValueError(f"{name} must be positive")
    for name in ("bank_service_s", "respawn_delay_s", "respawn_protection_s",
                 "contact_dwell_s", "magnet_duration_s", "rocket_duration_s",
                 "powerup_respawn_s", "powerup_pickup_s", "restock_period_s",
                 "restock_fraction", "steal_fraction"):
        if getattr(params, name) < 0:
            raise ValueError(f"{name} must be non-negative")
    if not 0.0 <= params.size_speed_penalty < 1.0:
        raise ValueError("size_speed_penalty must be in [0, 1)")
    for profile in params.profiles:
        for name in ("speed_factor", "absorb_factor"):
            if getattr(profile, name) <= 0:
                raise ValueError(f"profile {name} must be positive")
        if profile.scenario_start_units < 0:
            raise ValueError("scenario_start_units must be non-negative")


def _validate_map(map_spec: MapSpec) -> None:
    ids = {z.zone_id for z in map_spec.zones}
    if len(ids) != len(map_spec.zones):
        raise ValueError("duplicate zone ids in map")
    if map_spec.bank_zone_id not in ids:
        raise ValueError("bank zone missing from map")
    if any(zone_id not in ids for zone_id in map_spec.spawn_zones):
        raise ValueError("spawn zone missing from map")
    if map_spec.bank_zone_id not in ids:
        raise ValueError("bank zone missing from map")
