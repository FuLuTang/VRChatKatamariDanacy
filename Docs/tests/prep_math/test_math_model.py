import json
import math
import unittest
from pathlib import Path

from Docs.tools.prep_math import math_model as m


ROOT = Path(__file__).resolve().parents[3]


class SphereMathTests(unittest.TestCase):
    def test_volume_radius_inverse(self):
        for radius in (0.0, 0.1, 1.0, 2.5, 5.0):
            self.assertAlmostEqual(m.sphere_radius_from_volume(m.sphere_volume_from_radius(radius)), radius)

    def test_radius_is_not_diameter(self):
        radius = 2.0
        diameter = 2.0 * radius
        self.assertEqual(radius, 2.0)
        self.assertEqual(diameter, 4.0)
        self.assertNotEqual(radius, diameter)

    def test_cube_root_volume_scaling(self):
        self.assertAlmostEqual(m.sphere_radius_from_volume(8 * m.sphere_volume_from_radius(1)), 2)

    def test_zero_volume_has_zero_radius(self):
        self.assertEqual(m.sphere_radius_from_volume(0), 0)

    def test_negative_volume_rejected(self):
        with self.assertRaises(ValueError):
            m.sphere_radius_from_volume(-1)

    def test_nonfinite_volume_rejected(self):
        for value in (float("nan"), float("inf"), -float("inf")):
            with self.subTest(value=value), self.assertRaises(ValueError):
                m.sphere_radius_from_volume(value)

    def test_extreme_finite_volume_still_yields_finite_radius(self):
        result = m.sphere_radius_from_volume(1e308)
        self.assertTrue(math.isfinite(result))
        self.assertAlmostEqual(math.log10(result), 102.46, places=1)

    def test_radius_whose_volume_overflows_rejected_cleanly(self):
        with self.assertRaises(ValueError):
            m.sphere_volume_from_radius(1e200)


class LedgerAndSupportTests(unittest.TestCase):
    def test_add_and_total_contributions(self):
        ledger = m.add_contribution({}, "fruit", 3)
        ledger = m.add_contribution(ledger, "cup", 4)
        self.assertEqual(m.total_volume(ledger), 7)

    def test_remove_specific_contribution_and_recompute(self):
        ledger = {"long-rod": 100.0, "berry": 2.0}
        revised = m.remove_contribution(ledger, "long-rod")
        self.assertEqual(m.total_volume(revised), 2.0)
        self.assertEqual(m.total_volume(ledger), 102.0)  # immutable input

    def test_unknown_remove_is_idempotent(self):
        self.assertEqual(m.remove_contribution({"x": 1}, "missing"), {"x": 1})

    def test_duplicate_attach_same_instance_does_not_add_again(self):
        once = m.add_contribution({}, "instance-1", 5)
        twice = m.add_contribution(once, "instance-1", 5)
        self.assertEqual(m.total_volume(once), m.total_volume(twice))

    def test_duplicate_attach_conflicting_value_rejected(self):
        ledger = m.add_contribution({}, "instance-1", 5)
        with self.assertRaises(ValueError):
            m.add_contribution(ledger, "instance-1", 6)

    def test_bank_appearance_reset_does_not_mutate_ledger(self):
        confirmed = {"item-a": 3.0, "item-b": 4.0}
        before = m.total_volume(confirmed)
        _visual_core_radius_after_bank = 0.5
        after = m.total_volume(confirmed)
        self.assertEqual(before, after)
        self.assertEqual(sum(confirmed.values()), 7.0)

    def test_core_volume_is_game_units_not_mesh_volume(self):
        # Explicit ledger argument is authoritative; no mesh parameter exists in this model.
        self.assertEqual(m.total_volume({"item": 2}, core_volume=10), 12)

    def test_overflowing_ledger_sum_rejected_cleanly(self):
        with self.assertRaises(ValueError):
            m.total_volume({"a": 1e308, "b": 1e308})

    def test_long_rod_support_uses_current_extreme(self):
        self.assertEqual(m.support_distance((1, 0, 0), [(0, 0, 0), (5, 0, 0)], 1), 5)

    def test_removed_long_rod_no_longer_persists_as_historical_max(self):
        remaining_points = [(0.75, 0, 0)]
        self.assertEqual(m.support_distance((1, 0, 0), remaining_points, 1), 1)

    def test_covered_candidate_does_not_exceed_grown_core(self):
        self.assertEqual(m.support_distance((1, 0, 0), [(1.4, 0, 0)], 2), 2)

    def test_support_direction_normalized(self):
        self.assertEqual(m.support_distance((10, 0, 0), [(3, 0, 0)], 1), 3)

    def test_support_needs_finite_nonnegative_core(self):
        for bad in (-1, float("nan"), float("inf")):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                m.support_distance((1, 0, 0), [], bad)

    def test_contributions_must_be_finite_and_nonnegative(self):
        for bad in (-1, float("nan"), float("inf")):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                m.add_contribution({}, "x", bad)


class CoordinateMathTests(unittest.TestCase):
    def test_normalizes_direction(self):
        result = m.normalize((3, 0, 4))
        self.assertAlmostEqual(math.sqrt(m.dot(result, result)), 1)
        self.assertEqual(result, (0.6, 0.0, 0.8))

    def test_zero_direction_rejected(self):
        with self.assertRaises(ValueError):
            m.normalize((0, 0, 0))

    def test_near_zero_direction_cutoff_is_explicit(self):
        with self.assertRaises(ValueError):
            m.normalize((1e-15, 0, 0))
        just_above = math.nextafter(1e-15, math.inf)
        self.assertEqual(m.normalize((just_above, 0, 0)), (1.0, 0.0, 0.0))

    def test_nonfinite_direction_rejected(self):
        with self.assertRaises(ValueError):
            m.normalize((1, float("nan"), 0))

    def test_large_finite_direction_normalizes_safely(self):
        result = m.normalize((1e308, 1e308, 0))
        self.assertAlmostEqual(result[0], math.sqrt(0.5))
        self.assertAlmostEqual(result[1], math.sqrt(0.5))
        self.assertEqual(result[2], 0)

    def test_scale_must_be_positive_and_finite(self):
        self.assertEqual(m.positive_scale((1, 2, 3)), (1.0, 2.0, 3.0))
        for scale in ((1, 0, 1), (1, -1, 1), (1, float("inf"), 1), (1, float("nan"), 1)):
            with self.subTest(scale=scale), self.assertRaises(ValueError):
                m.positive_scale(scale)

    def test_world_to_local_to_world_with_parent_and_rotation(self):
        parent = m.translation(10, -2, 3)
        visual_local = m.rotation_z(math.pi / 2)
        visual_to_world = m.transform_chain(parent, visual_local)
        world = (8.0, 5.0, 4.0)
        local = m.attachment_local_pose(world, visual_to_world)
        actual = m.attachment_world_pose(local, visual_to_world)
        for a, b in zip(actual, world):
            self.assertAlmostEqual(a, b, places=10)

    def test_nested_parent_chain_roundtrip_nonuniform_positive_scale(self):
        grandparent = m.translation(4, 1, -3)
        parent = m.scale_matrix((2, 3, 4))
        child = m.translation(1, -2, 0.5)
        world_matrix = m.transform_chain(grandparent, parent, child)
        p = (0.25, 0.5, -0.75)
        actual = m.transform_point(m.inverse4(world_matrix), m.transform_point(world_matrix, p))
        for a, b in zip(actual, p):
            self.assertAlmostEqual(a, b, places=10)

    def test_attachment_root_local_pose_invariance_is_a_model_assumption(self):
        attachment_local = (0.25, 1.5, 0.0)
        parent_world = m.translation(2, -1, 0)
        attachment_root_local = m.translation(0, 0.5, 0)
        attachment_root_world = m.transform_chain(parent_world, attachment_root_local)
        # CoreMesh is a sibling child: it changes scale, but is not in AttachmentRoot's path.
        core_before = m.transform_chain(parent_world, m.scale_matrix((1, 1, 1)))
        core_after = m.transform_chain(parent_world, m.scale_matrix((5, 5, 5)))
        self.assertNotEqual(core_before, core_after)
        world_before = m.attachment_world_pose(attachment_local, attachment_root_world)
        world_after = m.attachment_world_pose(attachment_local, attachment_root_world)
        self.assertEqual(world_before, world_after)

    def test_rotation_transforms_direction_without_translation(self):
        matrix = m.matmul(m.translation(99, -20, 3), m.rotation_z(math.pi / 2))
        rotated = m.transform_direction(matrix, (1, 0, 0))
        self.assertAlmostEqual(rotated[0], 0, places=12)
        self.assertAlmostEqual(rotated[1], 1, places=12)
        self.assertEqual(rotated[2], 0)

    def test_singular_parent_inverse_rejected(self):
        singular = ((1.,0.,0.,0.), (0.,0.,0.,0.), (0.,0.,1.,0.), (0.,0.,0.,1.))
        with self.assertRaises(ValueError):
            m.inverse4(singular)

    def test_malformed_and_nonfinite_matrices_rejected(self):
        with self.assertRaises(ValueError):
            m.transform_point(((1., 0., 0.),), (1, 2, 3))
        invalid = ((1.,0.,0.,0.), (0.,float("inf"),0.,0.), (0.,0.,1.,0.), (0.,0.,0.,1.))
        with self.assertRaises(ValueError):
            m.transform_direction(invalid, (1, 0, 0))

    def test_nonfinite_transform_output_rejected(self):
        huge = ((1e308,0.,0.,0.), (0.,1.,0.,0.), (0.,0.,1.,0.), (0.,0.,0.,1.))
        with self.assertRaises(ValueError):
            m.transform_point(huge, (2, 0, 0))
        with self.assertRaises(ValueError):
            m.transform_direction(huge, (2, 0, 0))


class FixtureIntegrityTests(unittest.TestCase):
    def load_cases(self):
        payload = json.loads((ROOT / "Docs/Preparation/MathCases/vectors.json").read_text(encoding="utf-8"))
        self.assertEqual(payload["schema"], "offline-math-cases-v1")
        ids = [case["id"] for case in payload["vectors"]]
        self.assertGreaterEqual(len(ids), 20)
        self.assertEqual(len(ids), len(set(ids)))
        return payload["vectors"]

    def assert_nested_close(self, actual, expected):
        if isinstance(expected, dict):
            self.assertEqual(set(actual), set(expected))
            for key in expected:
                self.assert_nested_close(actual[key], expected[key])
        elif isinstance(expected, list):
            self.assertEqual(len(actual), len(expected))
            for actual_item, expected_item in zip(actual, expected):
                self.assert_nested_close(actual_item, expected_item)
        elif isinstance(expected, (int, float)):
            self.assertAlmostEqual(actual, expected, places=9)
        else:
            self.assertEqual(actual, expected)

    def run_case(self, case):
        op, data = case["operation"], case["input"]
        if op == "sphere_radius": return m.sphere_radius_from_volume(data["volume"])
        if op == "sphere_volume": return m.sphere_volume_from_radius(data["radius"])
        if op == "radius_diameter": return {"radius": data["radius"], "diameter": 2 * data["radius"]}
        if op == "ledger_total": return m.total_volume(data["ledger"], data.get("core_volume", 0))
        if op == "ledger_remove": return m.remove_contribution(data["ledger"], data["item_id"])
        if op == "support_distance": return m.support_distance(data["direction"], data["points"], data["core_radius"])
        if op == "duplicate_attach": return m.add_contribution(data["initial"], data["item_id"], data["amount"])
        if op == "normalize": return m.normalize(data["vector"])
        if op == "roundtrip":
            matrix = m.transform_chain(m.translation(*data["parent_translation"]), m.rotation_z(data["visual_rotation_z"]))
            local = m.attachment_local_pose(data["world_point"], matrix)
            return m.attachment_world_pose(local, matrix)
        if op == "parent_roundtrip":
            matrix = m.transform_chain(m.translation(*data["parent_translation"]), m.scale_matrix(data["parent_scale"]), m.translation(*data["local_translation"]))
            point = data["point"]
            return m.transform_point(m.inverse4(matrix), m.transform_point(matrix, point))
        if op == "local_attachment":
            return m.attachment_world_pose(data["local_attachment"], m.scale_matrix(data["root_scale"]))
        if op == "rotate_z":
            return m.transform_direction(m.rotation_z(data["radians"]), data["direction"])
        if op == "invalid_scale": return m.positive_scale(data["scale"])
        if op == "invalid_finite": return m.finite(float("nan") if data["value"] == "NaN" else float("inf"))
        if op == "invalid_direction": return m.normalize(data["vector"])
        if op == "core_visual_only":
            parent = m.translation(*data["parent_world_translation"])
            attachment_root_world = m.transform_chain(parent, m.translation(*data["attachment_root_local"]))
            core_before = m.transform_chain(parent, m.scale_matrix((data["core_radius_before"],) * 3))
            core_after = m.transform_chain(parent, m.scale_matrix((data["core_radius_after"],) * 3))
            before = m.attachment_world_pose(data["attachment_local"], attachment_root_world)
            after = m.attachment_world_pose(data["attachment_local"], attachment_root_world)
            return {"core_mesh_scale_changed": core_before != core_after,
                    "attachment_world_before": before, "attachment_world_after": after}
        self.fail(f"unsupported fixture operation: {op}")

    def test_every_json_vector_executes_and_matches_expected_result(self):
        for case in self.load_cases():
            with self.subTest(case=case["id"]):
                if case["expected"] == "ValueError":
                    with self.assertRaises(ValueError):
                        self.run_case(case)
                else:
                    self.assert_nested_close(self.run_case(case), case["expected"])


if __name__ == "__main__":
    unittest.main()
