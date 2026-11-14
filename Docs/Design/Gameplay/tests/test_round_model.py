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

from round_model import (  # noqa: E402
    BANK,
    DEFAULT_MAP,
    AvoidThreats,
    EatEverything,
    ItemStack,
    MapSpec,
    PlayerProfile,
    Policy,
    RoundParams,
    RushBank,
    ZoneSpec,
    can_absorb,
    diameter_for_units,
    simulate_round,
)


class _NoBank(Policy):
    """Stand still and never deposit; used to isolate absorption."""

    name = "no_bank"


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

    def test_deposit_moves_unbanked_into_score_and_resets_size(self):
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
        self.assertAlmostEqual(player.final_diameter_m, params.start_diameter_m, places=9)
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

    def test_default_map_snapshot_is_consistent(self):
        total_items = sum(s.count for z in DEFAULT_MAP.zones for s in z.stacks)
        total_value = sum(s.count * s.value_units for z in DEFAULT_MAP.zones for s in z.stacks)
        self.assertEqual(total_items, 452)
        self.assertAlmostEqual(total_value, 1356.0, places=9)
        self.assertEqual(len(DEFAULT_MAP.zones), 21)
        bank = [z for z in DEFAULT_MAP.zones if z.is_bank]
        self.assertEqual(len(bank), 1)
        self.assertEqual(bank[0].zone_id, DEFAULT_MAP.bank_zone_id)


if __name__ == "__main__":
    unittest.main()
