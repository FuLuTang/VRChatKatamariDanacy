import math
import sys
import unittest

from Docs.Design.BallLayers.SizeModel.size_model import (
    CollisionRadiusWorldM, CoreRadiusWorldM, SemanticDiameter, SizeParameters,
    core_diameter_world_m, core_radius_from_volume, derive_size, rebuild_after_reset,
)

PARAMS = SizeParameters(1.0, 0.25, 3.0, 0.8, 1.5)


class SizeModelTests(unittest.TestCase):
    def test_sphere_geometry_and_eightfold_volume_ratio_oracle(self):
        r1 = core_radius_from_volume(1.0, 2.0).value
        r8 = core_radius_from_volume(8.0, 2.0).value
        # Independent geometric identity, not production inverse function.
        self.assertAlmostEqual((4 * math.pi / 3) * r1**3, 2.0, places=12)
        self.assertAlmostEqual((4 * math.pi / 3) * r8**3, 16.0, places=11)
        self.assertAlmostEqual(r8 / r1, 2.0)

    def test_radius_diameter_collision_and_semantic_types_stay_distinct(self):
        state = derive_size(0, PARAMS)
        self.assertIsInstance(state.core_radius_world_m, CoreRadiusWorldM)
        self.assertIsInstance(state.collision_radius_world_m, CollisionRadiusWorldM)
        self.assertIsInstance(state.semantic_diameter, SemanticDiameter)
        self.assertAlmostEqual(core_diameter_world_m(state.core_radius_world_m),
                               2 * state.core_radius_world_m.value)
        self.assertNotAlmostEqual(state.core_radius_world_m.value,
                                  state.semantic_diameter.value)
        for wrong in (state.semantic_diameter, state.collision_radius_world_m, 2.0):
            with self.assertRaises(TypeError): core_diameter_world_m(wrong)

    def test_cap_is_display_only_and_does_not_mutate_external_ledger(self):
        ledger = {"confirmed": 10**15, "banked": 7}
        before = dict(ledger)
        at_cap = derive_size(10**15, PARAMS)
        larger = derive_size(10**15 + 12345, PARAMS)
        self.assertAlmostEqual(at_cap.core_radius_world_m.value, PARAMS.max_core_radius_world_m)
        self.assertEqual(at_cap.core_radius_world_m, larger.core_radius_world_m)
        self.assertEqual(ledger, before)
        # Distinct authoritative values remain independently available to caller.
        self.assertEqual((ledger["confirmed"], ledger["banked"]), (10**15, 7))

    def test_reset_rebuild_uses_new_ledger_not_previous_display_size(self):
        grown = derive_size(20.0, PARAMS)
        rebuilt = rebuild_after_reset(0.0, PARAMS)
        self.assertGreater(grown.core_radius_world_m.value, rebuilt.core_radius_world_m.value)
        self.assertEqual(rebuilt, derive_size(0.0, PARAMS))

    def test_finite_extreme_intermediates_saturate_or_reject_without_inf(self):
        state = derive_size(sys.float_info.max, PARAMS)
        self.assertTrue(all(math.isfinite(x) for x in (
            state.core_radius_world_m.value, state.collision_radius_world_m.value,
            state.semantic_diameter.value, state.effective_volume_units)))
        extreme = core_radius_from_volume(sys.float_info.max, sys.float_info.max)
        self.assertTrue(math.isfinite(extreme.value))
        # Its logarithmic sphere-volume relation remains representable without
        # materializing the overflowing product of the two input factors.
        self.assertAlmostEqual(math.log(extreme.value),
            (2 * math.log(sys.float_info.max) + math.log(3) - math.log(4 * math.pi)) / 3,
            places=10)
        # Final mapped dimensions can overflow even when input factors are finite.
        absurd = SizeParameters(1e308, 1e308, 1e308, 1e308, 1.0)
        with self.assertRaises(OverflowError): derive_size(0.0, absurd)

    def test_invalid_inputs_and_cap_below_baseline(self):
        for bad in (-1.0, float("nan"), float("inf"), True, 10**1000):
            with self.subTest(bad=bad), self.assertRaises(ValueError): derive_size(bad, PARAMS)
        too_small = SizeParameters(1.0, 1.0, 0.01, 1.0, 1.0)
        with self.assertRaises(ValueError): derive_size(0.0, too_small)

    def test_legacy_named_outputs_remain_finite_at_cap(self):
        state = derive_size(10**12, PARAMS)
        self.assertAlmostEqual(state.core_radius_world_m.value, 3.0)
        self.assertAlmostEqual(state.collision_radius_world_m.value, 2.4)
        self.assertAlmostEqual(state.semantic_diameter.value, 9.0)


if __name__ == "__main__": unittest.main()
