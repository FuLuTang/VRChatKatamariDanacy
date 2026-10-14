"""Deterministic cross-layer event trace using only narrow DTO-like inputs."""
import hashlib
import math
import unittest
from dataclasses import replace

from Docs.Design.BallLayers.FollowRig.follow_rig import FollowRig
from Docs.Design.BallLayers.RemoteProxy.remote_proxy import (
    RemoteProxyCache, SummaryError, SyntheticConfirmedSummary,
)
from Docs.Design.BallLayers.VisualRoll.visual_roll import VisualRollState


def _cross_for_oracle(a, b):
    # Independent contact-velocity expansion; never imports module math helpers.
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _len(v):
    return math.sqrt(sum(c * c for c in v))


def _source_event(context, generation, sequence, event_id, revision,
                  position, *, removed=False, priority=5):
    fields = (context, "ball-A", "toy-cup", generation, sequence, event_id, revision,
              "cup-proxy", 73, position, (0.0, 0.0, 0.0, 1.0), True, False,
              removed, priority)
    digest = hashlib.sha256(repr(fields).encode("utf-8")).hexdigest()
    return SyntheticConfirmedSummary(
        context_id=context, ball_id="ball-A", instance_id="toy-cup",
        generation=generation, attach_sequence=sequence, event_id=event_id,
        revision=revision, summary_digest=digest, variant_id="cup-proxy",
        variant_seed=73, position=position, rotation=(0.0, 0.0, 0.0, 1.0),
        visible=True, buried=False, removed=removed, presentation_priority=priority)


def _assert_no_slip(test, state, tangent_velocity, normal, radius):
    q = state.local_rotation
    qv = math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2])
    angle = 2.0 * math.atan2(qv, q[3])
    axis = (q[0] / qv, q[1] / qv, q[2] / qv)
    omega = tuple(component * angle for component in axis)  # dt=1 by fixture
    rho = tuple(-radius * component for component in normal)
    spin = _cross_for_oracle(omega, rho)
    residual = tuple(tangent_velocity[i] + spin[i] for i in range(3))
    test.assertLess(_len(residual), 1e-10)


class PresentationCrossLayerSimulationTests(unittest.TestCase):
    def test_normal_undo_rebuild_teleport_and_invalid_event_trace(self):
        # Independent layer states are connected only by explicit input values.
        roll = VisualRollState.identity()
        rig = FollowRig(height_filter_hz=4.0, yaw_filter_hz=5.0)
        proxy = RemoteProxyCache(max_records=8, renderer_budget=2)
        frame = rig.update((0.0, 0.0, 0.0), 359.0, 0.0, 1.0, 0.7, 1.3)

        # Normal motion: no-slip oracle independently checks the chosen axis.
        radius = 0.8
        displacement = (0.0, 0.0, 0.16)
        roll = roll.advance(displacement, (0.0, 1.0, 0.0), radius)
        _assert_no_slip(self, roll, displacement, (0.0, 1.0, 0.0), radius)
        frame = rig.update((0.0, 0.0, 0.16), 1.0, 0.05, 1.0, 0.7, 1.3)
        short_delta = (frame.yaw_deg - 359.0 + 180.0) % 360.0 - 180.0
        self.assertGreater(short_delta, 0.0)
        self.assertLess(short_delta, 2.0)

        # Synthetic confirmation arrives twice; only the decoration consumer changes.
        old_event = _source_event("round-C", 2, 7001, "confirm-A", 0, (2.0, 0.0, 1.0))
        self.assertEqual(proxy.apply(old_event), "accepted")
        roll_before_proxy = roll.local_rotation
        frame_before_proxy = frame
        self.assertEqual(proxy.apply(old_event), "duplicate")
        self.assertEqual(roll.local_rotation, roll_before_proxy)
        self.assertEqual(rig.frame, frame_before_proxy)
        self.assertEqual(proxy.renderer_count, 1)

        # Undo is a newer authoritative synthetic tombstone; no ledger/collider path exists.
        revoked_event = _source_event("round-C", 2, 7001, "confirm-A", 1,
                                      (2.0, 0.0, 1.0), removed=True)
        self.assertEqual(proxy.apply(revoked_event), "updated")
        self.assertEqual(proxy.renderer_count, 0)
        self.assertEqual(proxy.apply(old_event), "stale_revision")

        # Complete snapshot rebuild on a fresh cache is order independent.
        current_generation = _source_event("round-C", 3, 7002, "confirm-B", 0,
                                           (3.0, 0.0, 2.0), priority=9)
        reordered = RemoteProxyCache(max_records=8, renderer_budget=2)
        reordered.rebuild_context("round-C", (old_event, current_generation))
        self.assertEqual(reordered.tracked_count, 1)
        self.assertEqual(reordered.summaries[0].generation, 3)
        self.assertEqual(reordered.rendered_summaries[0].event_id, "confirm-B")
        self.assertEqual(reordered.apply(old_event), "stale_generation")

        forward_order = RemoteProxyCache(max_records=8, renderer_budget=2)
        reverse_order = RemoteProxyCache(max_records=8, renderer_budget=2)
        for event in (old_event, current_generation):
            forward_order.apply(event)
        for event in (current_generation, old_event):
            reverse_order.apply(event)
        self.assertEqual(forward_order.summaries, reverse_order.summaries)
        self.assertEqual(forward_order.rendered_summaries, reverse_order.rendered_summaries)

        # Explicit teleport/respawn skips the travel chord and snaps the independent rig.
        before_teleport = roll
        roll_after_teleport = roll.advance((100.0, 0.0, -50.0), (0.0, 1.0, 0.0),
                                           radius, discontinuous=True)
        self.assertIs(roll_after_teleport, before_teleport)
        frame = rig.update((-20.0, 4.0, 8.0), 271.0, 0.0, 9.0,
                           0.6, 1.4, reset=True)
        self.assertEqual(frame.position, (-20.0, 5.4, 8.0))
        self.assertEqual(frame.yaw_deg, 271.0)
        next_roll = roll_after_teleport.advance((0.0, 0.0, 0.08),
                                                (0.0, 1.0, 0.0), radius)
        self.assertNotEqual(next_roll.local_rotation, roll.local_rotation)

        # Degenerate but valid zero-tangent input preserves pose; invalid inputs
        # for each layer are rejected without cross-layer or partial state changes.
        self.assertIs(next_roll.advance((0.0, 0.3, 0.0), (0.0, 1.0, 0.0), radius), next_roll)
        old_frame = rig.frame
        old_roll = next_roll.local_rotation
        old_proxy = proxy.summaries
        with self.assertRaises(ValueError):
            next_roll.advance((float("nan"), 0.0, 0.0), (0.0, 1.0, 0.0), radius)
        with self.assertRaises(ValueError):
            rig.update((0.0, 0.0, 0.0), 0.0, -1.0, 1.0, 0.5, 1.5)
        with self.assertRaises(SummaryError):
            proxy.apply(replace(old_event, position=(float("inf"), 0.0, 0.0)))
        self.assertEqual(next_roll.local_rotation, old_roll)
        self.assertEqual(rig.frame, old_frame)
        self.assertEqual(proxy.summaries, old_proxy)

        # Clearing a closed match context blocks delayed replay; a new match uses a new key.
        proxy.clear_context("round-C")
        self.assertEqual(proxy.apply(old_event), "cleared_context")
        new_round = _source_event("round-D", 1, 1, "confirm-round-D", 0, (0.0, 0.0, 0.0))
        self.assertEqual(proxy.apply(new_round), "accepted")
        self.assertLessEqual(proxy.renderer_count, 2)

    def test_invalid_complete_snapshot_does_not_partially_replace_cache(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=2)
        stable = _source_event("round-X", 1, 2, "confirmed-X", 0, (0.0, 0.0, 0.0))
        cache.apply(stable)
        before = cache.summaries
        malformed = replace(stable, revision=1, position=(float("nan"), 0.0, 0.0))
        with self.assertRaises(SummaryError):
            cache.rebuild_context("round-X", (malformed,))
        self.assertEqual(cache.summaries, before)


if __name__ == "__main__":
    unittest.main()
