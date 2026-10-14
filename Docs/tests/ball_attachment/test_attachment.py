import math
import unittest

from Docs.Design.BallLayers.Attachment.attachment import (
    AttachmentLedger, ConfirmedAttachmentEvent, IdentityConflictError, Pose,
    StaleOrRevokedError, event_identity_hash, local_to_world, remote_proxy_key_hash,
    world_to_local,
)


def close_tuple(actual, expected, tol=1e-7):
    return all(abs(a - b) <= tol for a, b in zip(actual, expected))


def event(context, item, seq, *, x=0.0, event_id=None, generation=1):
    return ConfirmedAttachmentEvent(
        context, "ball-A", item, generation, seq, event_id or f"evt-{seq}-{item}",
        Pose((0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)),
        Pose((x, 2.0, 3.0), (0.0, 0.0, 0.0, 1.0)), (0.4, 0.7, 1.2))


class AttachmentTests(unittest.TestCase):
    def test_world_local_roundtrip_against_independent_y_rotation_matrix(self):
        angle = math.pi / 3
        q = (0.0, math.sin(angle / 2), 0.0, math.cos(angle / 2))
        ball = Pose((2.0, -1.0, 4.0), q)
        obj = Pose((6.0, 2.0, 5.0), (0.0, 0.0, 0.0, 1.0))
        local = world_to_local(ball, obj, (0.4, 0.7, 1.2))
        # Independent inverse matrix for +Y rotation: x'=c*x-s*z, z'=s*x+c*z.
        dx, dy, dz = (4.0, 3.0, 1.0)
        expected = (math.cos(angle)*dx - math.sin(angle)*dz, dy,
                    math.sin(angle)*dx + math.cos(angle)*dz)
        self.assertTrue(close_tuple(local.position, expected))
        self.assertTrue(close_tuple(local_to_world(ball, local).position, obj.position))
        self.assertEqual(local.object_scale, (0.4, 0.7, 1.2))
        moved = Pose((8.0, 0.0, -2.0), q)
        rebuilt = local_to_world(moved, local)
        self.assertTrue(close_tuple(rebuilt.position,
                                    (moved.position[0] + dx, moved.position[1] + dy,
                                     moved.position[2] + dz)))

    def test_authoritative_identity_idempotency_and_arrival_order(self):
        events = [event("round-1", "i1", 90, x=1), event("round-1", "i2", 44, x=2)]
        a, b = AttachmentLedger(), AttachmentLedger()
        a.apply(events[0]); a.apply(events[1]); a.apply(events[0])
        b.apply(events[1]); b.apply(events[0])
        self.assertEqual(a.records, b.records)
        self.assertEqual([r.attach_sequence for r in a.records], [90, 44])
        self.assertEqual(len(a.records), 2)
        self.assertEqual(a.records, tuple(sorted(a.records, key=lambda r: r.identity)))
        # Remote proxy key is strictly the contract's four fields; event scope is separate.
        key = remote_proxy_key_hash("seed", "i", 1, 2)
        self.assertEqual(key, remote_proxy_key_hash("seed", "i", 1, 2))
        self.assertNotEqual(key, remote_proxy_key_hash("other-seed", "i", 1, 2))
        self.assertNotEqual(key, remote_proxy_key_hash("seed", "other", 1, 2))
        self.assertNotEqual(key, remote_proxy_key_hash("seed", "i", 2, 2))
        self.assertNotEqual(key, remote_proxy_key_hash("seed", "i", 1, 3))
        # Length prefixes distinguish fields whose naive concatenation is identical.
        self.assertNotEqual(remote_proxy_key_hash("ab", "c", 1, 2),
                            remote_proxy_key_hash("a", "bc", 1, 2))
        for invalid_key in (("", "i", 1, 2), ("seed", "", 1, 2),
                            ("seed", "i", True, 2), ("seed", "i", 1, 1 << 64)):
            with self.subTest(invalid_key=invalid_key), self.assertRaises(ValueError):
                remote_proxy_key_hash(*invalid_key)

        # Event identity has its own context/ball/item/event encoding, not the remote key.
        event_key = event_identity_hash("r", "b", "i", 1, 2, "e")
        self.assertEqual(event_key, event_identity_hash("r", "b", "i", 1, 2, "e"))
        self.assertNotEqual(event_key, event_identity_hash("r", "b", "i", 1, 2, "different"))
        self.assertNotEqual(event_identity_hash("ab", "c", "i", 1, 2, "e"),
                            event_identity_hash("a", "bc", "i", 1, 2, "e"))

    def test_conflict_revoke_tombstone_and_exact_generation_scope(self):
        ledger = AttachmentLedger()
        original = event("round-1", "same", 5)
        record = ledger.apply(original)
        self.assertEqual(ledger.apply(original), record)
        conflict = event("round-1", "same", 5, x=100)
        before = ledger.records
        with self.assertRaises(IdentityConflictError):
            ledger.apply(conflict)
        self.assertEqual(ledger.records, before)
        self.assertTrue(ledger.revoke(record.identity))
        self.assertFalse(ledger.records)
        with self.assertRaises(StaleOrRevokedError):
            ledger.apply(original)
        # New generation and new round remain separate identities.
        newer = event("round-1", "same", 5, generation=2)
        other_round = event("round-2", "same", 5)
        ledger.apply(newer); ledger.apply(other_round)
        self.assertEqual(len(ledger.records), 2)

    def test_invalid_inputs_are_atomic_including_identity_and_pose_failures(self):
        ledger = AttachmentLedger()
        ledger.apply(event("round-1", "valid", 2))
        before = (ledger.records, frozenset(ledger._revoked))
        invalids = [
            event("round-1", "bad", True),
            event("round-1", "bad", 3, x=float("inf")),
            ConfirmedAttachmentEvent("round-1", "ball-A", "bad", 1, 4, "bad-q",
                Pose((0, 0, 0), (0, 0, 0, 0)), Pose((1, 2, 3), (0, 0, 0, 1))),
            ConfirmedAttachmentEvent("round-1", "ball-A", "bad", 1, 4, "bad-scale",
                Pose((0, 0, 0), (0, 0, 0, 1)), Pose((1, 2, 3), (0, 0, 0, 1)), (1, 0, 1)),
        ]
        for bad in invalids:
            with self.subTest(bad=bad), self.assertRaises((ValueError, OverflowError)):
                ledger.apply(bad)
            self.assertEqual((ledger.records, frozenset(ledger._revoked)), before)

    def test_context_clear_and_atomic_snapshot_rebuild(self):
        ledger = AttachmentLedger()
        old = event("r1", "old", 1)
        ledger.apply(old); ledger.apply(event("r2", "keep", 2))
        ledger.rebuild_snapshot("r1", [event("r1", "new", 91), event("r1", "newer", 8)])
        self.assertEqual({r.instance_id for r in ledger.query(context_id="r1")}, {"new", "newer"})
        before = ledger.records
        with self.assertRaises((ValueError, OverflowError)):
            ledger.rebuild_snapshot("r1", [event("r1", "good", 7), event("r1", "bad", True)])
        self.assertEqual(ledger.records, before)
        ledger.clear_context("r1")
        self.assertEqual([r.context_id for r in ledger.records], ["r2"])

    def test_legacy_transform_and_scale_regression(self):
        ball = Pose((2.0, -3.0, 1.5), (0, 0, 0, 1))
        obj = Pose((4.0, 2.0, 3.0), (0, 0, 0, 1))
        local = world_to_local(ball, obj, (0.4, 0.7, 1.2))
        moved = Pose((-7.0, 5.0, 9.0), (0, 0, 0, 1))
        rebuilt = local_to_world(moved, local)
        self.assertEqual(rebuilt.position, (-5.0, 10.0, 10.5))
        self.assertEqual(local.object_scale, (0.4, 0.7, 1.2))


if __name__ == "__main__":
    unittest.main()
