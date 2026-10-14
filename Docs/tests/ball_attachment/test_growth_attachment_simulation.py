"""Cross-layer synthetic event sequence with independent ledger/geometry oracles."""
import math
import unittest

from Docs.Design.BallLayers.Attachment.attachment import (
    AttachmentLedger, ConfirmedAttachmentEvent, Pose, StaleOrRevokedError,
)
from Docs.Design.BallLayers.SizeModel.size_model import SizeParameters, derive_size


P = SizeParameters(2.0, 0.125, 2.0, 0.75, 1.25)


def pickup(round_id, seq, item, gain, x):
    # gain is deliberately external ledger data; T51 only consumes confirmed pose.
    ev = ConfirmedAttachmentEvent(round_id, "ball-1", item, 1, seq, f"c-{seq}-{item}",
        Pose((0, 0, 0), (0, 0, 0, 1)), Pose((x, 0, 1), (0, 0, 0, 1)))
    return ev, gain


class GrowthAttachmentSequenceTests(unittest.TestCase):
    def test_confirm_grow_attach_move_drop_bank_and_rebuild(self):
        records = AttachmentLedger()
        confirmed_volume = 0.0
        banked_score = 0.0
        # Confirmation arrives out of order; explicit item→gain mapping belongs to
        # the external confirmed ledger, not T51 attachment ordering.
        gain_by_instance = {"mug": 3.0, "toy": 5.0}
        events = [pickup("match-A", 71, "mug", gain_by_instance["mug"], 1.0),
                  pickup("match-A", 13, "toy", gain_by_instance["toy"], -2.0)]
        records.apply(events[1][0]); confirmed_volume += gain_by_instance[events[1][0].instance_id]
        records.apply(events[0][0]); confirmed_volume += gain_by_instance[events[0][0].instance_id]
        grown = derive_size(confirmed_volume, P)
        self.assertAlmostEqual(confirmed_volume, 8.0)
        # Independent geometric check with explicitly stated baseline + 8 units.
        expected_radius = ((3.0 * (2.0 + 8.0) * 0.125) / (4.0 * math.pi)) ** (1 / 3)
        self.assertAlmostEqual(grown.core_radius_world_m.value, expected_radius)
        self.assertEqual(len(records.records), 2)
        self.assertEqual({r.attach_sequence for r in records.records}, {13, 71})
        # Translation moves attachment with ball; independent object scale is unchanged.
        local = next(r.local_pose for r in records.records if r.instance_id == "toy")
        moved = Pose((5, 2, -1), (0, 0, 0, 1))
        self.assertEqual((moved.position[0] + local.position[0],
                          moved.position[1] + local.position[1],
                          moved.position[2] + local.position[2]), (3.0, 2.0, 0.0))
        self.assertEqual(local.object_scale, (1.0, 1.0, 1.0))
        # Drop mug by explicit instance identity; never infer gain from sorted record order.
        dropped = next(r for r in records.records if r.instance_id == "mug")
        self.assertTrue(records.revoke(dropped.identity))
        confirmed_volume -= gain_by_instance[dropped.instance_id]
        remaining_instances = {r.instance_id for r in records.records}
        remaining_authority_volume = sum(gain_by_instance[item] for item in remaining_instances)
        self.assertEqual(remaining_instances, {"toy"})
        self.assertEqual(len(records.records), 1)
        self.assertAlmostEqual(confirmed_volume, remaining_authority_volume)
        self.assertAlmostEqual(confirmed_volume, gain_by_instance["toy"])
        self.assertAlmostEqual(derive_size(confirmed_volume, P).core_radius_world_m.value,
                               ((3.0 * (2.0 + remaining_authority_volume) * .125)
                                / (4.0 * math.pi)) ** (1/3))
        # Bank is external transaction: reset size from post-bank confirmed ledger.
        banked_score += confirmed_volume
        confirmed_volume = 0.0
        self.assertEqual(banked_score, remaining_authority_volume)
        reset = derive_size(confirmed_volume, P)
        self.assertEqual(reset, derive_size(0.0, P))

    def test_revoked_replay_invalid_event_snapshot_rollback_and_new_round(self):
        ledger = AttachmentLedger()
        ev, _ = pickup("m1", 8, "cup", 4, 0)
        ledger.apply(ev); ledger.revoke(("m1", "ball-1", "cup", 1, 8, "c-8-cup"))
        before = ledger.records
        with self.assertRaises(StaleOrRevokedError): ledger.apply(ev)
        self.assertEqual(ledger.records, before)
        valid, _ = pickup("m1", 9, "jar", 2, 3)
        invalid = ConfirmedAttachmentEvent("m1", "ball-1", "bad", 1, 10, "bad",
            Pose((0, 0, 0), (0, 0, 0, 0)), Pose((1, 2, 3), (0, 0, 0, 1)))
        with self.assertRaises(ValueError): ledger.rebuild_snapshot("m1", [valid, invalid])
        self.assertEqual(ledger.records, before)
        ledger.rebuild_snapshot("m1", [valid], [("m1", "ball-1", "cup", 1, 8, "c-8-cup")])
        with self.assertRaises(StaleOrRevokedError): ledger.apply(ev)
        ledger.apply(pickup("m2", 8, "cup", 4, 0)[0])
        self.assertEqual({r.context_id for r in ledger.records}, {"m1", "m2"})

    def test_presentation_cap_does_not_truncate_authority_value(self):
        confirmed = 100_000.0
        banked = 17.0
        rendered = derive_size(confirmed, P)
        self.assertEqual(rendered.core_radius_world_m.value, P.max_core_radius_world_m)
        self.assertEqual((confirmed, banked), (100_000.0, 17.0))
        self.assertGreater(confirmed, rendered.effective_volume_units)


if __name__ == "__main__": unittest.main()
