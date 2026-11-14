"""Unit tests for the offline round model.

Run from the repository root:

    python3 -m unittest discover -s Docs/Design/Gameplay/tests -v

The tests use tiny hand-built maps so each invariant is isolated; they do not
assert any balance number from the PicnicLawn scenario sweeps.
"""

from __future__ import annotations

import math
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import round_model  # noqa: E402
from round_model import (  # noqa: E402
    BANK,
    AvoidThreats,
    EatEverything,
    ItemStack,
    MapSpec,
    PlayerProfile,
    Policy,
    RoundParams,
    RushBank,
    ZoneSpec,
    active_anchor_index,
    can_absorb,
    can_convert,
    diameter_for_units,
    live_map_spec,
    load_map,
    map_fingerprint,
    post_bank_diameter,
    simulate_round,
    units_for_diameter,
)


class _NoBank(Policy):
    """Stand still and never deposit; used to isolate absorption."""

    name = "no_bank"


class _AlwaysBank(Policy):
    """Deposit as soon as there is anything unbanked; used to isolate banking."""

    name = "always_bank"

    def wants_bank(self, ctx) -> bool:
        return ctx.unbanked_units > 0


def _map(zones: tuple[ZoneSpec, ...], bank_zone: str = "core") -> MapSpec:
    spawn = zones[0].zone_id
    return MapSpec(
        zones=zones,
        spawn_zones=(spawn, spawn, spawn, spawn),
        bank_anchors_m=((4.2, 4.2), (4.2, -4.2), (-4.2, -4.2), (-4.2, 4.2)),
        bank_zone_id=bank_zone,
    )


def _one_player(**kwargs) -> tuple[RoundParams, PlayerProfile]:
    profile = PlayerProfile("solo")
    params = RoundParams(profiles=(profile,), **kwargs)
    return params, profile


class GrowthMathTests(unittest.TestCase):
    def test_start_and_max_diameter(self):
        params = RoundParams()
        self.assertAlmostEqual(diameter_for_units(0.0, params), 0.30, places=9)
        self.assertAlmostEqual(
            diameter_for_units(params.units_to_max_diameter, params), 2.20, places=9)
        self.assertAlmostEqual(
            diameter_for_units(10.0 * params.units_to_max_diameter, params), 2.20, places=9)

    def test_growth_is_strictly_monotonic(self):
        params = RoundParams()
        previous = 0.0
        for units in (0, 1, 5, 20, 100, 400, 1000, 1356):
            diameter = diameter_for_units(float(units), params)
            self.assertGreaterEqual(diameter, previous)
            self.assertGreater(diameter, 0.0)
            previous = diameter

    def test_radius_is_finite_across_probes(self):
        for exponent in (0.2667, 1.0 / 3.0, 0.4):
            params = RoundParams(radius_exponent=exponent)
            for units in (0.0, 1e-6, 1.0, 1356.0, 1e7):
                diameter = diameter_for_units(units, params)
                self.assertTrue(math.isfinite(diameter))
                self.assertGreaterEqual(diameter, params.start_diameter_m - 1e-12)
                self.assertLessEqual(diameter, params.max_diameter_m + 1e-12)

    def test_post_bank_shrink_formula_and_clamp(self):
        params = RoundParams()
        pre = 1.40
        expected = params.start_diameter_m + (
            pre - params.start_diameter_m) * params.bank_residual_fraction
        self.assertAlmostEqual(post_bank_diameter(pre, params), expected, places=12)
        # Never below the start diameter, even for a nonsensical pre-bank size.
        self.assertAlmostEqual(
            post_bank_diameter(0.10, params), params.start_diameter_m, places=12)
        self.assertAlmostEqual(
            post_bank_diameter(params.start_diameter_m, params),
            params.start_diameter_m, places=12)
        # 0.0 reproduces the old reset rule; 1.0 keeps the whole ball.
        reset = RoundParams(bank_residual_fraction=0.0)
        keep = RoundParams(bank_residual_fraction=1.0)
        self.assertAlmostEqual(post_bank_diameter(pre, reset),
                               params.start_diameter_m, places=12)
        self.assertAlmostEqual(post_bank_diameter(pre, keep), pre, places=12)

    def test_units_for_diameter_round_trips(self):
        params = RoundParams()
        for diameter in (0.30, 0.3625, 0.55, 0.95, 1.45, 2.20):
            units = units_for_diameter(diameter, params)
            self.assertAlmostEqual(
                diameter_for_units(units, params), diameter, places=9)
        # A shrunken ball still carries real volume above the free core.
        self.assertGreater(units_for_diameter(0.3625, params), 0.0)
        self.assertAlmostEqual(
            units_for_diameter(0.20, params), 0.0, places=12)

    def test_absorb_threshold_is_inclusive_and_ratio_scaled(self):
        self.assertTrue(can_absorb(0.30, 0.30, 1.0))
        self.assertFalse(can_absorb(0.32, 0.30, 1.0))
        self.assertTrue(can_absorb(0.32, 0.30, 1.2))
        self.assertFalse(can_absorb(0.40, 0.30, 1.0))
        self.assertFalse(can_absorb(0.30, 0.30, 0.8))


class SimulationTests(unittest.TestCase):
    def test_determinism_with_same_seed(self):
        params = RoundParams(
            profiles=(
                PlayerProfile("fast", 1.10, 1.05),
                PlayerProfile("slow", 0.85, 0.90),
            ),
            duration_s=60.0,
        )
        first = simulate_round(params, (EatEverything(), RushBank()), seed=11)
        second = simulate_round(params, (EatEverything(), RushBank()), seed=11)
        self.assertEqual(first, second)
        self.assertEqual(first.seed, 11)

    def test_absorption_grows_the_ball_monotonically(self):
        arena = ZoneSpec("arena", (0.0, 0.0), (
            ItemStack("seed", 60, 0.08, 1),
            ItemStack("flower", 20, 0.18, 1),
        ))
        params, _ = _one_player(duration_s=30.0)
        result = simulate_round(params, _NoBank(), _map((arena, ZoneSpec("core", (5.0, 0.0), (), is_bank=True))))
        player = result.players[0]
        self.assertGreater(player.absorbed_items, 0)
        self.assertGreater(player.peak_diameter_m, params.start_diameter_m)
        curve = player.growth_curve_m
        self.assertEqual(list(curve), sorted(curve))
        self.assertEqual(player.score_units, 0.0)

    def test_deposit_scores_unbanked_and_shrinks_by_fraction(self):
        zones = (
            ZoneSpec("spawn", (0.0, 0.0), ()),
            ZoneSpec("core", (2.0, 0.0), (), is_bank=True),
        )
        params = RoundParams(
            duration_s=10.0,
            profiles=(PlayerProfile("solo", scenario_start_units=100.0),),
        )
        stay = simulate_round(params, _NoBank(), _map(zones))
        self.assertEqual(stay.players[0].score_units, 0.0)
        self.assertAlmostEqual(stay.players[0].absorbed_units, 0.0, places=9)

        hop = simulate_round(params, RushBank(), _map(zones))
        player = hop.players[0]
        self.assertEqual(player.deposits, 1)
        self.assertAlmostEqual(player.score_units, 100.0, places=9)
        self.assertAlmostEqual(player.deposit_units, 100.0, places=9)
        self.assertAlmostEqual(player.bank_score_units, 100.0, places=9)
        # The ball now SHRINKS instead of resetting to the start diameter.
        pre_bank = diameter_for_units(100.0, params)
        expected = post_bank_diameter(pre_bank, params)
        self.assertAlmostEqual(player.final_diameter_m, expected, places=9)
        self.assertGreater(player.final_diameter_m, params.start_diameter_m)
        self.assertLess(player.final_diameter_m, pre_bank)
        self.assertGreater(player.peak_diameter_m, params.start_diameter_m)
        bank_events = [e for e in hop.events if e[1] == "bank"]
        self.assertEqual(len(bank_events), 1)
        self.assertAlmostEqual(float(bank_events[0][3]), 100.0, places=9)

    def test_swallow_transfers_share_and_respawns_small(self):
        arena = ZoneSpec("arena", (0.0, 0.0), ())
        bank = ZoneSpec("core", (10.0, 0.0), (), is_bank=True)
        params = RoundParams(
            duration_s=6.5,
            profiles=(
                PlayerProfile("big", scenario_start_units=300.0),
                PlayerProfile("small", scenario_start_units=100.0),
            ),
        )
        result = simulate_round(params, _NoBank(), _map((arena, bank)))
        big, small = result.players
        swallows = [e for e in result.events if e[1] == "swallow"]
        self.assertGreaterEqual(len(swallows), 1)
        self.assertEqual(small.swallowed, len(swallows))
        self.assertEqual(big.swallows, len(swallows))
        self.assertEqual(swallows[0][2], 0)  # attacker is player 0
        # floor(100 * 0.25) = 25 units move from victim to attacker.
        self.assertAlmostEqual(big.stolen_units, 25.0, places=9)
        self.assertAlmostEqual(small.score_units, 0.0, places=9)
        self.assertAlmostEqual(small.final_diameter_m, params.start_diameter_m, places=9)
        # The victim comes back at its spawn, small, after respawn_delay_s.
        respawns = [e for e in result.events if e[1] == "respawn"]
        self.assertEqual(len(respawns), 1)
        self.assertAlmostEqual(respawns[0][0] - swallows[0][0], 3.0, places=6)
        # Protection must keep the victim alive for respawn_protection_s.
        if len(swallows) > 1:
            self.assertGreaterEqual(
                swallows[1][0] - respawns[0][0],
                params.respawn_protection_s - params.dt_s - 1e-9)
        self.assertGreaterEqual(small.respawn_downtime_s, 3.0 - 1e-9)

    def test_single_item_slot_never_overlaps_effects(self):
        arena = ZoneSpec("arena", (0.0, 0.0), (), powerup="magnet")
        bank = ZoneSpec("core", (5.0, 0.0), (), is_bank=True)
        params, _ = _one_player(duration_s=12.0, powerup_respawn_s=0.5,
                                magnet_duration_s=4.0)
        result = simulate_round(params, _NoBank(), _map((arena, bank)))
        pickups = [e[0] for e in result.events if e[1] == "powerup"]
        self.assertGreaterEqual(len(pickups), 2)
        for earlier, later in zip(pickups, pickups[1:]):
            self.assertGreaterEqual(later - earlier, params.magnet_duration_s - params.dt_s - 1e-9)

    def test_threshold_blocks_items_above_the_start_ball(self):
        apple = ZoneSpec("arena", (0.0, 0.0), (ItemStack("apple", 5, 0.32, 4),))
        bank = ZoneSpec("core", (5.0, 0.0), (), is_bank=True)
        params, _ = _one_player(duration_s=5.0)
        blocked = simulate_round(params, _NoBank(), _map((apple, bank)))
        self.assertEqual(blocked.players[0].absorbed_items, 0)

        flower = ZoneSpec("arena", (0.0, 0.0), (ItemStack("flower", 5, 0.18, 1),))
        allowed = simulate_round(params, _NoBank(), _map((flower, bank)))
        self.assertEqual(allowed.players[0].absorbed_items, 5)

    def test_results_are_finite_and_non_negative(self):
        for exponent in (0.2667, 1.0 / 3.0, 0.4):
            params = RoundParams(duration_s=30.0, radius_exponent=exponent,
                                 profiles=(PlayerProfile("a"), PlayerProfile("b")))
            result = simulate_round(params, (EatEverything(), AvoidThreats()), seed=3)
            for player in result.players:
                for value in (player.score_units, player.peak_diameter_m,
                              player.final_diameter_m, player.respawn_downtime_s,
                              player.stolen_units):
                    self.assertTrue(math.isfinite(value))
                    self.assertGreaterEqual(value, 0.0)
                for _, seconds in player.time_sinks:
                    self.assertGreaterEqual(seconds, 0.0)

    def test_round_rejects_bad_rosters(self):
        with self.assertRaises(ValueError):
            simulate_round(RoundParams(profiles=()), EatEverything())
        with self.assertRaises(ValueError):
            simulate_round(
                RoundParams(profiles=tuple(PlayerProfile(str(i)) for i in range(5))),
                EatEverything())

    def test_default_map_is_the_live_map_not_a_snapshot(self):
        # No constant to drift: the module has no snapshot and the default
        # MapSpec must reproduce the files on disk exactly.
        self.assertFalse(hasattr(round_model, "DEFAULT_MAP"))
        data = load_map()
        spec = live_map_spec(4)
        total_items = sum(s.count for z in spec.zones for s in z.stacks)
        total_value = sum(s.count * s.value_units for z in spec.zones for s in z.stacks)
        self.assertEqual(total_items, data.absorbable_total)
        self.assertEqual(total_items, 452)
        self.assertAlmostEqual(total_value, float(data.units_total), places=9)
        self.assertAlmostEqual(total_value, 1284.0, places=9)
        self.assertEqual(len(spec.zones), len(data.zone_shape))
        self.assertEqual(len(spec.zones), 21)
        # 4 players use player_count_plans["4"], not an ad-hoc order.
        self.assertEqual(spec.spawn_zones,
                         ("spawn_nw", "spawn_ne", "spawn_sw", "spawn_se"))
        self.assertEqual(live_map_spec(2).spawn_zones, ("spawn_nw", "spawn_se"))
        bank = [z for z in spec.zones if z.is_bank]
        self.assertEqual(len(bank), 1)
        self.assertEqual(bank[0].zone_id, spec.bank_zone_id)

    def test_default_run_equals_an_explicit_live_map(self):
        params = RoundParams(duration_s=60.0, profiles=(PlayerProfile("solo"),))
        implicit = simulate_round(params, _NoBank(), seed=5)
        explicit = simulate_round(params, _NoBank(), live_map_spec(1), seed=5)
        self.assertEqual(
            [(p.absorbed_items, round(p.absorbed_units, 6), p.final_diameter_m)
             for p in implicit.players],
            [(p.absorbed_items, round(p.absorbed_units, 6), p.final_diameter_m)
             for p in explicit.players])
        self.assertEqual(implicit.players[0].spawn_zone, "spawn_nw")

    def test_map_fingerprint_reports_the_files(self):
        info = map_fingerprint()
        self.assertEqual(info["map_id"], "picnic_lawn")
        self.assertEqual(info["zones"], 21)
        self.assertEqual(info["absorbable_instances_total"], 452)
        self.assertEqual(info["gain_units_total"], 1284)
        self.assertRegex(info["layout_sha256"], r"^[0-9a-f]{64}$")
        self.assertRegex(info["catalog_sha256"], r"^[0-9a-f]{64}$")

    def test_load_map_refuses_to_invent_a_map(self):
        with self.assertRaises(FileNotFoundError):
            load_map("/nonexistent/picnic_lawn")


class BankingRuleTests(unittest.TestCase):
    """Conversion = shrink + minimum size, not a reset."""

    @staticmethod
    def _bank_spawn_map() -> MapSpec:
        # The player spawns already standing on the conversion area.
        return _map((ZoneSpec("core", (0.0, 0.0), (), is_bank=True),))

    def test_sub_threshold_ball_cannot_convert(self):
        params = RoundParams(
            duration_s=8.0,
            profiles=(PlayerProfile("solo", scenario_start_units=5.0),),
        )
        start_diameter = diameter_for_units(5.0, params)
        self.assertFalse(can_convert(start_diameter, params))
        result = simulate_round(params, _AlwaysBank(), self._bank_spawn_map())
        player = result.players[0]
        # The visit is wasted: no score, no deposit, no size or volume change.
        self.assertEqual(player.deposits, 0)
        self.assertEqual(player.score_units, 0.0)
        self.assertEqual(player.absorbed_items, 0)
        self.assertAlmostEqual(player.final_diameter_m, start_diameter, places=9)
        self.assertAlmostEqual(player.peak_diameter_m, start_diameter, places=9)
        self.assertFalse([e for e in result.events if e[1] == "bank"])
        # The same ball one step above the threshold does convert.
        allowed = RoundParams(
            duration_s=8.0,
            profiles=(PlayerProfile("solo", scenario_start_units=60.0),),
        )
        converted = simulate_round(allowed, _AlwaysBank(), self._bank_spawn_map())
        self.assertEqual(converted.players[0].deposits, 1)
        self.assertAlmostEqual(converted.players[0].score_units, 60.0, places=9)

    def test_bank_rotation_schedule_is_60_seconds(self):
        params = RoundParams()
        self.assertEqual(params.bank_cycle_s, 60.0)
        schedule = ((0.0, 0), (59.75, 0), (60.0, 1), (119.75, 1),
                    (120.0, 2), (180.0, 3), (240.0, 0), (300.0, 1))
        for now_s, expected in schedule:
            self.assertEqual(active_anchor_index(now_s, params), expected)

    def test_repeated_banking_cannot_farm_the_initial_core(self):
        params = RoundParams(
            duration_s=60.0,
            profiles=(PlayerProfile("solo", scenario_start_units=100.0),),
        )
        result = simulate_round(params, _AlwaysBank(), self._bank_spawn_map())
        player = result.players[0]
        # 100 unbanked units are scored exactly once, in one deposit.
        self.assertEqual(player.deposits, 1)
        self.assertAlmostEqual(player.score_units, 100.0, places=9)
        self.assertAlmostEqual(player.bank_score_units, 100.0, places=9)
        # The residual is kept as ball size but is already scored: it never
        # turns into units that a second conversion could pay for again.
        self.assertGreater(player.final_diameter_m, params.start_diameter_m)
        residual_units = units_for_diameter(player.final_diameter_m, params)
        self.assertGreater(residual_units, 0.0)
        self.assertLess(residual_units, 100.0)


class BonusLayerTests(unittest.TestCase):
    @staticmethod
    def _two_zone_map() -> MapSpec:
        zones = (
            ZoneSpec("rich", (0.0, 0.0), (ItemStack("seed", 40, 0.08, 1),)),
            ZoneSpec("empty", (10.0, 0.0), ()),
            ZoneSpec("core", (20.0, 0.0), (), is_bank=True),
        )
        return MapSpec(
            zones=zones,
            spawn_zones=("rich", "empty", "empty", "empty"),
            bank_anchors_m=((20.0, 0.0),) * 4,
            bank_zone_id="core",
        )

    def test_bonus_is_deterministic_and_only_first_place_scores(self):
        params = RoundParams(
            duration_s=5.0,
            profiles=(PlayerProfile("eater"), PlayerProfile("idler")),
            bonus_enabled=True,
            bonus_points=50.0,
            bonus_categories=("items_absorbed",),
        )
        first = simulate_round(params, (_NoBank(), _NoBank()),
                               self._two_zone_map(), seed=4)
        second = simulate_round(params, (_NoBank(), _NoBank()),
                                self._two_zone_map(), seed=4)
        self.assertEqual(first, second)
        eater, idler = first.players
        self.assertGreater(eater.absorbed_items, 0)
        self.assertEqual(eater.bonus_points, 50.0)
        self.assertEqual(idler.bonus_points, 0.0)
        self.assertEqual(eater.bonus_wins, ("items_absorbed",))
        self.assertAlmostEqual(eater.score_units, 50.0, places=9)
        self.assertAlmostEqual(idler.score_units, 0.0, places=9)
        self.assertEqual(first.winners, (0,))
        self.assertAlmostEqual(first.total_bonus, 50.0, places=9)

    def test_bonus_tie_awards_nothing(self):
        zones = (
            ZoneSpec("arena", (0.0, 0.0), ()),
            ZoneSpec("core", (5.0, 0.0), (), is_bank=True),
        )
        params = RoundParams(
            duration_s=1.0,
            profiles=(PlayerProfile("a"), PlayerProfile("b")),
            bonus_points=50.0,
            bonus_categories=("max_ball_size",),
        )
        result = simulate_round(params, (_NoBank(), _NoBank()), _map(zones))
        self.assertAlmostEqual(result.total_bonus, 0.0, places=12)
        self.assertEqual(result.players[0].score_units, 0.0)
        self.assertEqual(result.players[1].score_units, 0.0)

    def test_bonus_can_decide_the_ranking(self):
        # Both banked scores are zero; only the bonus separates them.
        params = RoundParams(
            duration_s=5.0,
            profiles=(PlayerProfile("eater"), PlayerProfile("idler")),
            bonus_points=50.0,
            bonus_categories=("items_absorbed",),
        )
        result = simulate_round(params, (_NoBank(), _NoBank()),
                                self._two_zone_map(), seed=4)
        self.assertAlmostEqual(result.players[0].bank_score_units, 0.0, places=9)
        self.assertAlmostEqual(result.players[1].bank_score_units, 0.0, places=9)
        self.assertEqual(result.winners, (0,))

if __name__ == "__main__":
    unittest.main()
