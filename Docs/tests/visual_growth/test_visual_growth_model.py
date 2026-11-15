"""Tests for the visual-growth model: current ledger, original curve, level 1.

The expectations below are computed with the sphere geometry identity
``V = 4/3*pi*r^3`` and the ledger identity ``V = (baseline + confirmed) * c``
directly in the test, not by calling the model's own mapping, so a change in the
model cannot silently change both sides.
"""

import math
import unittest

from Docs.Design.BallPresentation.visual_growth.visual_growth_model import (
    BASELINE_VOLUME_UNITS,
    CUBIC_METERS_PER_UNIT,
    MAX_CORE_RADIUS_M,
    ORIGINAL_MAS4,
    ORIGINAL_MAS5,
    apparent_relative_growth,
    delta_diameter,
    diameter_for_units,
    load_level,
    original_gate_relative,
    original_ratio,
    original_typical_relative,
    readable_until_diameter,
    relative_growth,
    simulate_greedy,
    volume_units_for_diameter,
)

SIZES = (0.30, 0.60, 1.10, 2.20)
UNITS = (1, 4, 100)


def oracle_diameter(confirmed_units):
    """Diameter straight from the two identities, cap included."""
    total = BASELINE_VOLUME_UNITS + confirmed_units
    radius = (3.0 * total * CUBIC_METERS_PER_UNIT / (4.0 * math.pi)) ** (1.0 / 3.0)
    return 2.0 * min(radius, MAX_CORE_RADIUS_M)


def oracle_relative(units_added, diameter_before):
    """Exact relative change of the diameter, computed from the volume ratio."""
    volume_before = math.pi / 6.0 * diameter_before**3
    confirmed_before = volume_before / CUBIC_METERS_PER_UNIT - BASELINE_VOLUME_UNITS
    after = oracle_diameter(confirmed_before + units_added)
    return after / diameter_before - 1.0


class CurrentLedgerTests(unittest.TestCase):
    def test_per_absorb_relative_growth_matches_independent_geometry(self):
        for diameter in SIZES:
            for units in UNITS:
                with self.subTest(diameter=diameter, units=units):
                    expected = oracle_relative(units, diameter)
                    self.assertAlmostEqual(
                        relative_growth(units, diameter), expected, places=9
                    )

    def test_documented_table_values_to_one_micrometre(self):
        # Values quoted in VISUAL_GROWTH_AUDIT.md: 1 / 4 / 100 units.
        expected_mm = {
            (0.30, 1): 26.5775,
            (0.30, 4): 87.8020,
            (0.30, 100): 632.1860,
            (0.60, 1): 7.1645,
            (0.60, 4): 27.7028,
            (0.60, 100): 399.6807,
            (1.10, 1): 2.1529,
            (1.10, 4): 8.5618,
            (1.10, 100): 183.4272,
        }
        for (diameter, units), millimetres in expected_mm.items():
            with self.subTest(diameter=diameter, units=units):
                self.assertAlmostEqual(
                    delta_diameter(units, diameter) * 1000.0, millimetres, places=4
                )

    def test_one_unit_at_the_cap_does_not_move_the_ball(self):
        self.assertEqual(delta_diameter(1, 2.20), 0.0)
        self.assertEqual(relative_growth(1, 2.20), 0.0)
        # Even with the cap removed the change is a quarter of a millimetre per
        # metre of ball: 2.45e-4 relative, i.e. far below the 1 % readability line.
        uncapped = relative_growth(1, 2.20, capped=False)
        self.assertAlmostEqual(uncapped, 2.4507e-4, places=7)
        self.assertLess(uncapped, 1e-3)

    def test_readability_line(self):
        self.assertGreater(relative_growth(1, 0.30), 1e-2)
        self.assertGreater(relative_growth(4, 0.30), 1e-2)
        self.assertGreater(relative_growth(1, 0.60), 1e-2)
        self.assertLess(relative_growth(1, 1.10), 1e-2)
        self.assertLess(relative_growth(4, 1.10), 1e-2)

    def test_readable_until_diameter_solves_the_one_percent_line(self):
        # 1.01^3 - 1 = 0.030301, and D(T) = (6*T*c/pi)^(1/3).
        self.assertAlmostEqual(readable_until_diameter(1), 0.6370, places=4)
        self.assertAlmostEqual(readable_until_diameter(4), 1.0111, places=4)
        self.assertGreater(readable_until_diameter(100), 2.20)

    def test_camera_pull_back_reduces_what_the_player_sees(self):
        for diameter in SIZES[:3]:
            for units in UNITS:
                with self.subTest(diameter=diameter, units=units):
                    raw = relative_growth(units, diameter)
                    apparent = apparent_relative_growth(units, diameter)
                    self.assertLess(apparent, raw)
        # At 1.10 m the follow rig keeps only about a third of the growth.
        ratio = apparent_relative_growth(1, 1.10) / relative_growth(1, 1.10)
        self.assertAlmostEqual(ratio, 0.3400, places=4)

    def test_round_trip_between_diameter_and_units(self):
        for diameter in (0.30, 0.60, 1.10, 2.20):
            with self.subTest(diameter=diameter):
                confirmed = volume_units_for_diameter(diameter) - BASELINE_VOLUME_UNITS
                self.assertAlmostEqual(
                    oracle_diameter(confirmed), diameter, places=6
                )
                self.assertAlmostEqual(diameter_for_units(confirmed), diameter, places=6)


class OriginalCurveTests(unittest.TestCase):
    def test_mas5_knots_and_interpolation(self):
        self.assertAlmostEqual(original_ratio(0.05, ORIGINAL_MAS5), 0.80)
        self.assertAlmostEqual(original_ratio(0.10, ORIGINAL_MAS5), 0.80)
        self.assertAlmostEqual(original_ratio(1.50, ORIGINAL_MAS5), 0.80)
        self.assertAlmostEqual(original_ratio(2.00, ORIGINAL_MAS5), 0.40)
        self.assertAlmostEqual(original_ratio(2.50, ORIGINAL_MAS5), 0.16)
        self.assertAlmostEqual(original_ratio(3.00, ORIGINAL_MAS5), 0.08)
        # 2.20 m sits between the 2.00 and 2.50 knots.
        self.assertAlmostEqual(original_ratio(2.20, ORIGINAL_MAS5), 0.304, places=12)
        self.assertAlmostEqual(original_ratio(9.00, ORIGINAL_MAS5), 0.08)

    def test_mas4_decays_to_zero(self):
        self.assertAlmostEqual(original_ratio(1.10, ORIGINAL_MAS4), 0.32, places=12)
        self.assertAlmostEqual(original_ratio(1.35, ORIGINAL_MAS4), 0.0, places=12)

    def test_per_item_change_at_the_ten_times_gate(self):
        # f/30 in the linear limit; the exact cube root is what the model uses.
        for diameter, expected in ((0.30, 0.025986), (0.60, 0.025986), (1.10, 0.025986)):
            with self.subTest(diameter=diameter):
                self.assertAlmostEqual(
                    original_gate_relative(diameter), expected, places=6
                )
        self.assertAlmostEqual(
            original_gate_relative(2.20, ORIGINAL_MAS5), 0.010032, places=6
        )
        # The largest documented mission ratio (0.88) is still under 3 % per item.
        # f/30 is the linear limit; the exact cube root is 2.6 %, within 1e-3.
        self.assertAlmostEqual(original_gate_relative(0.30), 0.8 / 30.0, delta=1e-3)

    def test_typical_item_is_two_orders_below_our_one_unit_absorb(self):
        typical = original_typical_relative(0.30)
        self.assertAlmostEqual(typical, 0.001142, places=6)
        self.assertLess(typical, relative_growth(1, 0.30) / 50.0)


class LevelOneTraceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.props = load_level()
        cls.rows = simulate_greedy(cls.props)

    def test_every_prop_is_absorbed_and_the_ledger_total_matches_the_level(self):
        self.assertEqual(len(self.props), 130)
        self.assertEqual(len(self.rows), 130)
        self.assertEqual(int(self.rows[-1].confirmed_units), 1522)
        self.assertAlmostEqual(self.rows[-1].diameter_physical_m, 2.20, places=9)

    def test_diameter_never_decreases_and_stops_at_the_cap(self):
        previous = 0.2997
        for row in self.rows:
            self.assertGreaterEqual(row.diameter_physical_m, previous - 1e-12)
            previous = row.diameter_physical_m
        at_cap = [
            index
            for index, row in enumerate(self.rows)
            if row.diameter_physical_m >= 2.20 - 1e-12
        ]
        self.assertTrue(at_cap)
        # The absorb that reaches the cap still moves the ball; every absorb after
        # it moves the ball by exactly zero while the ledger keeps counting.
        for row in self.rows[at_cap[0] + 1:]:
            self.assertEqual(row.delta_diameter_m, 0.0)
            self.assertEqual(row.relative_growth, 0.0)

    def test_cap_is_reached_at_the_documented_unit_count(self):
        confirmed_at_cap = volume_units_for_diameter(2.2) - BASELINE_VOLUME_UNITS
        self.assertAlmostEqual(confirmed_at_cap, 1356.4, places=1)

    def test_first_bucket_grows_the_ball_by_more_than_half(self):
        start = 0.2997
        after_ten = self.rows[9].diameter_physical_m
        self.assertGreater(after_ten / start - 1.0, 0.5)
        # ... and the same ten absorbs leave most props buried inside the core.
        self.assertGreaterEqual(self.rows[9].swallowed_fraction, 0.3)

    def test_silhouette_metrics_are_fractions(self):
        for row in self.rows:
            self.assertGreater(row.visible_core_fraction, 0.0)
            self.assertLessEqual(row.visible_core_fraction, 1.0)
            self.assertGreaterEqual(row.shell_radius_m, row.core_radius_m)
            self.assertLessEqual(row.swallowed_fraction, 1.0)
            self.assertAlmostEqual(
                row.diameter_visual_m, 0.95 * row.diameter_physical_m, places=12
            )

    def test_most_attached_props_end_up_inside_the_core(self):
        self.assertGreater(self.rows[-1].swallowed_fraction, 0.8)


if __name__ == "__main__":
    unittest.main()
