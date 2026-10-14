import math
import unittest

from Docs.Design.BallLayers.RadialProxy.radial_proxy import (
    AttachmentProxy,
    RadialConfig,
    RadialProxy,
    direction_length_to_point,
    world_to_ball_local,
)

IDENTITY = (0.0, 0.0, 0.0, 1.0)
ORIGIN = (0.0, 0.0, 0.0)


class RadialProxyTests(unittest.TestCase):
    def test_attach_updates_near_direction_but_not_remote_direction(self):
        proxy = RadialProxy(core_radius=1.0)
        before = proxy.support_lengths
        direction = proxy.directions[0]
        endpoint = direction_length_to_point(direction, 3.0)
        affected = proxy.attach(AttachmentProxy("rod#gen1", (endpoint,)), ORIGIN, IDENTITY)

        self.assertIn(0, affected)
        self.assertGreater(proxy.support_length(0), before[0])
        remote = next(i for i, d in enumerate(proxy.directions)
                      if sum(a * b for a, b in zip(d, direction)) < -0.5)
        self.assertEqual(proxy.support_length(remote), before[remote])
        self.assertAlmostEqual(proxy.protrusion(0), proxy.support_length(0) - 1.0)

    def test_same_direction_contributions_survive_largest_removal(self):
        proxy = RadialProxy(core_radius=1.0)
        direction = proxy.directions[7]
        short_point = direction_length_to_point(direction, 2.0)
        long_point = direction_length_to_point(direction, 4.0)
        proxy.attach(AttachmentProxy("short#1", (short_point,)), ORIGIN, IDENTITY)
        proxy.attach(AttachmentProxy("long#1", (long_point,)), ORIGIN, IDENTITY)
        self.assertAlmostEqual(proxy.support_length(7), 4.0)

        proxy.detach("long#1")
        self.assertAlmostEqual(proxy.support_length(7), 2.0)
        proxy.detach("short#1")
        self.assertAlmostEqual(proxy.support_length(7), 1.0)

    def test_detach_recomputes_and_candidate_slot_is_released(self):
        proxy = RadialProxy(core_radius=0.75)
        endpoint = direction_length_to_point(proxy.directions[3], 4.0)
        proxy.attach(AttachmentProxy("long-rod#1", (endpoint,), True), ORIGIN, IDENTITY)
        self.assertGreater(proxy.support_length(3), 0.75)
        self.assertEqual(proxy.candidate_ids, ("long-rod#1",))
        affected = proxy.detach("long-rod#1")
        self.assertIn(3, affected)
        self.assertAlmostEqual(proxy.support_length(3), 0.75)
        self.assertEqual(proxy.candidate_ids, ())

        proxy.attach(AttachmentProxy("replacement#1", ((3.0, 0.0, 0.0),), True),
                     ORIGIN, IDENTITY)
        self.assertEqual(proxy.candidate_ids, ("replacement#1",))

    def test_full_rebuild_matches_incremental_ledger(self):
        entries = [
            (AttachmentProxy("a#1", ((2.0, 0.0, 0.0),)), ORIGIN, IDENTITY),
            (AttachmentProxy("b#1", ((0.0, 0.0, -3.0),), True), ORIGIN, IDENTITY),
            (AttachmentProxy("c#1", ((0.0, 2.5, 0.0),)), ORIGIN, IDENTITY),
        ]
        incremental = RadialProxy(1.0)
        for entry in entries:
            incremental.attach(*entry)
        rebuilt = RadialProxy(0.25)
        rebuilt.rebuild(1.0, entries)
        reverse_rebuilt = RadialProxy(0.25)
        reverse_rebuilt.rebuild(1.0, tuple(reversed(entries)))
        self.assertEqual(incremental.snapshot(), rebuilt.snapshot())
        self.assertEqual(rebuilt.snapshot(), reverse_rebuilt.snapshot())

    def test_candidate_ids_are_sorted_and_limits_reject_without_eviction(self):
        with self.assertRaises(ValueError):
            RadialProxy(1.0, RadialConfig(direction_count=31))
        with self.assertRaises(ValueError):
            RadialProxy(1.0, RadialConfig(direction_count=33))
        with self.assertRaises(ValueError):
            RadialProxy(1.0, RadialConfig(max_extreme_candidates=5))
        proxy = RadialProxy(1.0)
        self.assertEqual(len(proxy.directions), 32)
        proxy.attach(AttachmentProxy("zeta#1", ((2.0, 0.0, 0.0),), True),
                     ORIGIN, IDENTITY)
        proxy.attach(AttachmentProxy("alpha#1", ((0.0, 2.0, 0.0),), True),
                     ORIGIN, IDENTITY)
        self.assertEqual(proxy.candidate_ids, ("alpha#1", "zeta#1"))
        for n in range(2):
            proxy.attach(AttachmentProxy(f"fill{n}#1", ((2.0 + n, 0.0, 0.0),), True),
                         ORIGIN, IDENTITY)
        before = proxy.snapshot()
        with self.assertRaises(ValueError):
            proxy.attach(AttachmentProxy("overflow#1", ((8.0, 0.0, 0.0),), True),
                         ORIGIN, IDENTITY)
        self.assertEqual(proxy.snapshot(), before)

    def test_direction_and_length_form_endpoint_in_all_components(self):
        point = direction_length_to_point((0.0, 0.0, 1.0), 2.5)
        self.assertEqual(point, (0.0, 0.0, 2.5))
        with self.assertRaises(ValueError):
            direction_length_to_point((0.0, 0.0, 2.0), 2.5)
        with self.assertRaises(ValueError):
            direction_length_to_point((0.0, 0.0, 1.0), -1.0)
        with self.assertRaises(ValueError):
            direction_length_to_point((0.0, 0.0, 1.0), float("inf"))

    def test_host_quaternion_direction_probe_and_world_to_local(self):
        half = math.pi / 4.0
        q = (0.0, math.sin(half), 0.0, math.cos(half))
        local = world_to_ball_local((0.0, 0.0, 1.0), ORIGIN, q)
        # Inverse host-Hamilton +Y/90° maps +Z to -X. Unity parity is not asserted.
        self.assertAlmostEqual(local[0], -1.0)
        self.assertAlmostEqual(local[1], 0.0)
        self.assertAlmostEqual(local[2], 0.0)

    def test_world_point_is_transformed_into_ball_local_frame(self):
        proxy = RadialProxy(0.5)
        q = (0.0, math.sin(math.pi / 4), 0.0, math.cos(math.pi / 4))
        endpoint_world = (10.0, 0.0, -2.0)
        proxy.attach(AttachmentProxy("transformed#1", (endpoint_world,)),
                     (10.0, 0.0, 0.0), q)
        best_index = max(range(len(proxy.directions)),
                         key=lambda i: proxy.directions[i][0])
        self.assertAlmostEqual(proxy.support_length(best_index),
                               2.0 * proxy.directions[best_index][0], places=6)

    def test_invalid_attach_and_rebuild_are_atomic(self):
        proxy = RadialProxy(1.0)
        proxy.attach(AttachmentProxy("valid#1", ((0.0, 0.0, 2.0),)), ORIGIN, IDENTITY)
        before = proxy.snapshot()
        with self.assertRaises(ValueError):
            proxy.attach(AttachmentProxy("bad#1", ((float("inf"), 0.0, 0.0),)),
                         ORIGIN, IDENTITY)
        self.assertEqual(proxy.snapshot(), before)

        invalid_ledger = (
            (AttachmentProxy("temporary#1", ((0.0, 1.0, 0.0),)), ORIGIN, IDENTITY),
            (AttachmentProxy("bad#1", ((1.0, 0.0, 0.0),)), ORIGIN,
             (0.0, 0.0, 0.0, 0.0)),
        )
        with self.assertRaises(ValueError):
            proxy.rebuild(0.25, invalid_ledger)
        self.assertEqual(proxy.snapshot(), before)

    def test_non_finite_transforms_and_bad_indices_are_rejected(self):
        with self.assertRaises(ValueError):
            world_to_ball_local((1e308, 0.0, 0.0), (-1e308, 0.0, 0.0), IDENTITY)
        with self.assertRaises(ValueError):
            world_to_ball_local((0.0, 0.0, 1.0), ORIGIN, (0.0, 0.0, 0.0, 0.0))
        proxy = RadialProxy(1.0)
        for index in (-1, 32, 1.0):
            with self.subTest(index=index), self.assertRaises(IndexError):
                proxy.support_length(index)


if __name__ == "__main__":
    unittest.main()
