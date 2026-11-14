"""Deterministic offline round-economy model for the VRC塊魂 prototype.

This is a *design* model: standard library only, no file/network I/O, no
Unity, no physics. It answers "what does the 180 s economy look like under
these parameters" and nothing else. Read README.md in this directory for
what it does and does not claim, and for the parameter table.

Public API
----------
RoundParams                     scenario + tuning inputs (frozen)
PlayerProfile                   per-player speed/absorb skill, scenario handicap
ItemStack, ZoneSpec, MapSpec    shared map resource snapshot
DEFAULT_MAP                     PicnicLawn v0.1 snapshot (452 rollable props)
Policy                          decision hooks (model never names a policy)
EatEverything / AvoidThreats / RushBank
simulate_round(params, policy, map_spec=DEFAULT_MAP, seed=0) -> RoundResult

The map constant below is a hand-derived snapshot of
Docs/Design/Maps/PicnicLawn/{layout.json,prop_catalog.csv} at design v0.3:
item counts and tier values follow the catalog, pickup diameters follow the
layout's per-tier pickup gate with long/thin overrides (collapsed_bench is
absorbable at ball diameter 1.25 m per PicnicLawn README section 9).
Everything in it is PROVISIONAL_NOT_TESTED.
"""

from __future__ import annotations

import math
import random
from dataclasses import dataclass, field
from typing import Sequence

# A policy may ask for the bank instead of a map zone.
BANK = "bank"

# Tier upper edges from the PicnicLawn scale table (t0/t1/t2/t3).
TIER_EDGES_M = (0.55, 0.95, 1.45, 2.20)

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
    units_to_max_diameter: float = 1356.0   # (model) whole-map placeholder value
    radius_exponent: float = 1.0 / 3.0      # sphere law r = cbrt(3V/4pi)
    absorb_size_ratio: float = 1.0          # item pickup gate <= ball diameter * ratio

    bank_cycle_s: float = 45.0              # PicnicLawn rotation period (provisional)
    bank_service_s: float = 0.5             # (model) dwell to complete one deposit

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


_SPAWN_STACKS = (
    ItemStack("seed", 4, 0.08, 1),
    ItemStack("bottle_cap", 2, 0.08, 1),
    ItemStack("paper_scrap", 2, 0.10, 1),
    ItemStack("pebble", 2, 0.08, 1),
    ItemStack("fallen_leaf", 2, 0.10, 1),
)

_FRINGE_STACKS = (
    ItemStack("seed", 6, 0.08, 1),
    ItemStack("acorn", 2, 0.08, 1),
    ItemStack("bottle_cap", 3, 0.08, 1),
    ItemStack("paper_scrap", 3, 0.10, 1),
    ItemStack("fallen_leaf", 3, 0.10, 1),
    ItemStack("soda_can", 2, 0.32, 4),
    ItemStack("pebble", 2, 0.08, 1),
)

_ROOTS_STACKS = (
    ItemStack("acorn", 4, 0.08, 1),
    ItemStack("pebble", 4, 0.08, 1),
    ItemStack("fallen_leaf", 3, 0.10, 1),
    ItemStack("mushroom", 3, 0.12, 1),
)

_FLOWER_STACKS = (
    ItemStack("flower", 5, 0.18, 1),
    ItemStack("mushroom", 2, 0.12, 1),
    ItemStack("pebble", 2, 0.08, 1),
    ItemStack("small_potted_plant", 2, 0.55, 6),
    ItemStack("watering_can", 1, 0.58, 6),
)

_BLANKET_STACKS = (
    ItemStack("plate", 6, 0.50, 4),
    ItemStack("cup", 6, 0.32, 4),
    ItemStack("bowl", 5, 0.42, 4),
    ItemStack("spoon", 5, 0.34, 4),
    ItemStack("apple", 5, 0.32, 4),
    ItemStack("orange", 4, 0.32, 4),
    ItemStack("croissant", 4, 0.34, 4),
    ItemStack("bread_slice", 4, 0.32, 4),
    ItemStack("sandwich", 3, 0.38, 4),
    ItemStack("cookie", 5, 0.09, 1),
    ItemStack("grapes", 4, 0.08, 1),
    ItemStack("cheese_slice", 4, 0.08, 1),
    ItemStack("strawberry", 4, 0.08, 1),
    ItemStack("banana", 3, 0.45, 4),
    ItemStack("pear", 3, 0.32, 4),
    ItemStack("glass_bottle", 3, 0.52, 5),
    ItemStack("juice_carton", 3, 0.34, 4),
    ItemStack("soda_can", 3, 0.32, 4),
    ItemStack("napkin", 3, 0.32, 3),
    ItemStack("pebble", 1, 0.08, 1),
    ItemStack("toy_ball", 2, 0.40, 4),
)

_BASKET_STACKS = (
    ItemStack("picnic_basket", 2, 1.20, 16),
    ItemStack("thermos", 2, 1.00, 16),
    ItemStack("watermelon", 2, 0.72, 6),
    ItemStack("cooler_box", 1, 1.60, 20),
    ItemStack("folding_stool", 2, 1.40, 16),
    ItemStack("flower_pot_large", 2, 1.00, 16),
    ItemStack("banana", 2, 0.45, 4),
    ItemStack("apple", 2, 0.32, 4),
    ItemStack("bread_slice", 2, 0.32, 4),
    ItemStack("sandwich", 2, 0.38, 4),
    ItemStack("juice_carton", 1, 0.34, 4),
    ItemStack("plate", 2, 0.50, 4),
    ItemStack("bowl", 2, 0.42, 4),
    ItemStack("cup", 2, 0.32, 4),
)

_CORE_STACKS = (ItemStack("collapsed_bench", 4, 1.25, 16),)

DEFAULT_MAP = MapSpec(
    zones=(
        ZoneSpec("spawn_nw", (-14.5, 14.5), _SPAWN_STACKS),
        ZoneSpec("spawn_ne", (14.5, 14.5), _SPAWN_STACKS),
        ZoneSpec("spawn_sw", (-14.5, -14.5), _SPAWN_STACKS),
        ZoneSpec("spawn_se", (14.5, -14.5), _SPAWN_STACKS),
        ZoneSpec("fringe_north", (0.0, 15.5), _FRINGE_STACKS, powerup="rocket"),
        ZoneSpec("fringe_south", (0.0, -15.5), _FRINGE_STACKS, powerup="rocket"),
        ZoneSpec("fringe_east", (15.5, 0.0), _FRINGE_STACKS, powerup="rocket"),
        ZoneSpec("fringe_west", (-15.5, 0.0), _FRINGE_STACKS, powerup="rocket"),
        ZoneSpec("tree_roots_nw", (-18.75, 18.75), _ROOTS_STACKS),
        ZoneSpec("tree_roots_ne", (18.75, 18.75), _ROOTS_STACKS),
        ZoneSpec("tree_roots_sw", (-18.75, -18.75), _ROOTS_STACKS),
        ZoneSpec("tree_roots_se", (18.75, -18.75), _ROOTS_STACKS),
        ZoneSpec("flower_bed_nw", (-10.0, 8.55), _FLOWER_STACKS),
        ZoneSpec("flower_bed_ne", (10.0, 8.55), _FLOWER_STACKS),
        ZoneSpec("flower_bed_sw", (-10.0, -8.55), _FLOWER_STACKS),
        ZoneSpec("flower_bed_se", (10.0, -8.55), _FLOWER_STACKS),
        ZoneSpec("blanket_west", (-10.25, 0.0), _BLANKET_STACKS, powerup="magnet"),
        ZoneSpec("blanket_east", (10.25, 0.0), _BLANKET_STACKS, powerup="magnet"),
        ZoneSpec("basket_north", (0.0, 10.5), _BASKET_STACKS),
        ZoneSpec("basket_south", (0.0, -10.5), _BASKET_STACKS),
        ZoneSpec("core", (0.0, 0.0), _CORE_STACKS, is_bank=True),
    ),
    spawn_zones=("spawn_nw", "spawn_se", "spawn_ne", "spawn_sw"),
    # Rotation order bank_a -> bank_d -> bank_c -> bank_b (PicnicLawn README 6).
    bank_anchors_m=((4.2, 4.2), (4.2, -4.2), (-4.2, -4.2), (-4.2, 4.2)),
    bank_zone_id="core",
)


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
        if ctx.unbanked_units <= 0:
            return False
        return (ctx.unbanked_units >= self.bank_at_units
                or ctx.time_s >= ctx.duration_s - self.bank_before_end_s)

    def choose(self, ctx: PolicyContext) -> str | None:
        options = [z for z in ctx.zones
                   if z.absorbable_items > 0 and z.zone_id != ctx.zone_id]
        if options:
            return max(options, key=lambda z: (z.absorbable_value_units / (z.travel_s + 1.0),
                                               z.zone_id)).zone_id
        if ctx.unbanked_units > 0:
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
        if ctx.unbanked_units <= 0:
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
        if ctx.unbanked_units > 0:
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
        if ctx.unbanked_units <= 0:
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
        if ctx.unbanked_units > 0:
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
    map_spec = map_spec or DEFAULT_MAP
    _validate_params(params)
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

    results = []
    for p in players:
        sink = {k: p.time_sinks.get(k, 0.0) for k in TIME_SINKS}
        results.append(PlayerResult(
            index=p.index, name=p.profile.name, policy=p.policy.name,
            spawn_zone=p.spawn_zone, score_units=p.score,
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

    if p.policy.wants_bank(ctx) and p.units > 0.0:
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
        p.diameter_m = diameter_for_units(p.units, params)
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
    p.leg_anchor_index = _active_anchor_index(world.time_s, world.params)
    p.leg_length_m = _distance(world, p.zone_id, target)
    p.distance_left_m = p.leg_length_m
    if p.distance_left_m <= 1e-9:
        _arrive(world, p)
    return True


def _advance_travel(world: _World, p: PlayerState, dt: float) -> None:
    params = world.params
    if p.target == BANK:
        index = _active_anchor_index(world.time_s, params)
        if index != p.leg_anchor_index and p.leg_origin is not None:
            travelled = max(0.0, p.leg_length_m - p.distance_left_m)
            origin = world.positions[p.leg_origin]
            anchor = world.map.bank_anchors_m[index]
            new_length = _euclid(origin, anchor) * params.path_detour_factor
            p.leg_anchor_index = index
            p.leg_length_m = new_length
            p.distance_left_m = max(0.0, new_length - travelled)

    speed = p.speed_mps(params, world.time_s)
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
        index = _active_anchor_index(world.time_s, params)
        return _euclid(origin, world.map.bank_anchors_m[index]) * params.path_detour_factor
    target_pos = world.positions[target]
    return _euclid(origin, target_pos) * params.path_detour_factor


def _euclid(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _active_anchor_index(now_s: float, params: RoundParams) -> int:
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
    p.score += amount
    p.deposit_units += amount
    p.deposits += 1
    p.units = 0.0
    p.diameter_m = world.params.start_diameter_m
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
    attacker.diameter_m = diameter_for_units(attacker.units, params)
    attacker.swallows += 1

    victim.units = 0.0
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
