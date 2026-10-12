import math
import unittest

from Docs.tools.prep_protocol.model import (
    Item, Player, ProtocolError, ProtocolModel, Request, Snapshot, SnapshotPart,
)


def make_model(**kwargs):
    values = dict(
        round_id="round-1", epoch=2, cutoff_tick=100,
        players={"p1": Player(3, banked_score=10, unbanked=5),
                 "p2": Player(4, banked_score=7, unbanked=8, held_powerup="magnet")},
        items={"seed": Item(1, 3), "fruit": Item(2, 4), "old": Item(5, 2)},
    )
    values.update(kwargs)
    return ProtocolModel(**values)


def req(actor="p1", seq=1, operation="pickup", payload=None, *, epoch=2,
        session=3, tick=10, sender=None):
    return Request("round-1", epoch, actor, session, seq, operation,
                   payload if payload is not None else {"spawn_id": "seed", "generation": 1},
                   tick, sender or actor)


def valid_snapshot(**overrides):
    payload = {
        "round_id": "round-1", "commit_sequence": 9,
        "players": [
            {"player_id": "p1", "session_generation": 3, "banked_score": 11,
             "unbanked": 2, "life_state": "alive", "held_powerup": None},
            {"player_id": "p2", "session_generation": 4, "banked_score": 7,
             "unbanked": 8, "life_state": "ko", "held_powerup": None},
        ],
        "items": [{"spawn_id": "seed", "generation": 2, "gain": 3, "available": True}],
        "business_ids": [],
    }
    payload.update(overrides)
    part = SnapshotPart("snap-1", 5, 2, 0, 1, payload)
    return Snapshot("snap-1", 5, 2, True, [part])


class ProtocolModelTests(unittest.TestCase):
    def test_enqueue_is_pending_not_success(self):
        model = make_model()
        result = model.enqueue(req())
        self.assertEqual(("pending", "awaiting_host_commit"), (result["status"], result["reason"]))
        self.assertEqual(5, model.players["p1"].unbanked)
        self.assertEqual("committed", model.commit_queued()[0]["status"])

    def test_duplicate_queued_request_is_idempotent(self):
        model = make_model()
        first = model.enqueue(req())
        second = model.enqueue(req())
        self.assertEqual("pending", first["status"])
        self.assertEqual("duplicate_queued", second["reason"])
        self.assertEqual(1, len(model.queue))

    def test_reused_request_key_with_changed_payload_rejected(self):
        model = make_model()
        model.enqueue(req())
        changed = req(payload={"spawn_id": "fruit", "generation": 2})
        self.assertEqual("idempotency_key_conflict", model.enqueue(changed)["reason"])

    def test_race_same_spawn_only_one_gain(self):
        model = make_model()
        model.enqueue(req("p1", 1))
        model.enqueue(req("p2", 1, session=4))
        results = model.commit_queued()
        self.assertEqual(1, sum(r["reason"] == "pickup_committed" for r in results))
        self.assertEqual(16, model.players["p1"].unbanked + model.players["p2"].unbanked)

    def test_stale_spawn_generation_rejected(self):
        model = make_model()
        model.enqueue(req(payload={"spawn_id": "seed", "generation": 9}))
        self.assertEqual("stale_spawn_generation", model.commit_queued()[0]["reason"])

    def test_unknown_spawn_rejected(self):
        model = make_model()
        model.enqueue(req(payload={"spawn_id": "missing", "generation": 1}))
        self.assertEqual("unknown_spawn", model.commit_queued()[0]["reason"])

    def test_old_authority_epoch_rejected(self):
        model = make_model()
        result = model.enqueue(req(epoch=1))
        self.assertEqual("stale_epoch", result["reason"])
        self.assertFalse(model.queue)

    def test_stale_session_generation_rejected(self):
        model = make_model()
        with self.assertRaisesRegex(ProtocolError, "stale_session_generation"):
            model.enqueue(req(session=2))

    def test_sender_is_only_harness_fixture_and_mismatch_rejected(self):
        model = make_model()
        with self.assertRaisesRegex(ProtocolError, "sender_fixture_mismatch"):
            model.enqueue(req(actor="p1", sender="p2"))

    def test_duplicate_pickup_with_new_sequence_cannot_award_twice(self):
        model = make_model()
        model.enqueue(req(seq=1))
        model.enqueue(req(seq=2))
        results = model.commit_queued()
        self.assertEqual(["pickup_committed", "spawn_unavailable"], [r["reason"] for r in results])
        self.assertEqual(8, model.players["p1"].unbanked)

    def test_magnet_commits_some_candidates_and_rejects_others(self):
        model = make_model()
        payload = {"candidates": [
            {"spawn_id": "seed", "generation": 1},
            {"spawn_id": "fruit", "generation": 99},
            {"spawn_id": "missing", "generation": 1},
            {"spawn_id": "seed", "generation": 1},
        ]}
        model.enqueue(req(operation="magnet", payload=payload))
        result = model.commit_queued()[0]
        self.assertEqual("committed", result["status"])
        self.assertEqual(["seed"], result["accepted"])
        self.assertEqual(["committed", "stale_spawn_generation", "unknown_spawn", "duplicate_candidate"],
                         [c["status"] if c["status"] == "committed" else c["reason"] for c in result["candidates"]])

    def test_magnet_candidate_limit_rejected_without_mutation(self):
        model = make_model()
        model.enqueue(req(operation="magnet", payload={"candidates": [{}] * 65}))
        result = model.commit_queued()[0]
        self.assertEqual("candidate_limit", result["reason"])
        self.assertTrue(model.items["seed"].available)

    def test_bank_request_idempotent_across_request_sequences(self):
        model = make_model()
        model.enqueue(req(seq=1, operation="bank", payload={"bank_id": "b-1"}))
        model.commit_queued()
        # A second collection followed by same business ID must not bank it again.
        model.phase = "playing"
        model.enqueue(req(seq=2, operation="pickup", payload={"spawn_id": "fruit", "generation": 2}))
        model.commit_queued()
        model.phase = "playing"
        model.enqueue(req(seq=3, operation="bank", payload={"bank_id": "b-1"}))
        result = model.commit_queued()[0]
        self.assertEqual("duplicate_business_id", result["reason"])
        self.assertEqual(15, model.players["p1"].banked_score)
        self.assertEqual(4, model.players["p1"].unbanked)

    def test_bank_retry_does_not_double_score(self):
        model = make_model()
        model.enqueue(req(operation="bank", payload={"bank_id": "b-1"}))
        first = model.commit_queued()[0]
        total = model.players["p1"].banked_score
        second = model.enqueue(req(operation="bank", payload={"bank_id": "b-1"}))
        self.assertEqual("banked", first["reason"])
        self.assertEqual("banked", second["reason"])
        self.assertEqual(total, model.players["p1"].banked_score)

    def test_recovery_snapshot_preserves_bank_idempotency(self):
        model = make_model()
        model.enqueue(req(operation="bank", payload={"bank_id": "persisted-bank"}))
        committed = model.commit_queued()[0]
        snap = valid_snapshot()
        header = snap.parts[0].payload
        header["commit_sequence"] = model.commit_sequence
        header["players"][0]["banked_score"] = 15
        header["players"][0]["unbanked"] = 2
        header["business_ids"] = [{
            "actor_id": "p1", "session_generation": 3, "operation": "bank",
            "business_id": "persisted-bank", "result": committed,
        }]
        self.assertEqual("recovered_fixture", model.recover(snap)["status"])
        model.enqueue(req(seq=2, epoch=3, operation="bank", payload={"bank_id": "persisted-bank"}))
        result = model.commit_queued()[0]
        self.assertEqual("duplicate_business_id", result["reason"])
        self.assertEqual(15, model.players["p1"].banked_score)
        self.assertEqual(2, model.players["p1"].unbanked)

    def test_ko_preserves_banked_score_and_clears_unbanked_and_powerup_once(self):
        model = make_model()
        model.enqueue(req(operation="ko", payload={"ko_id": "ko-1", "victim_id": "p2", "attacker_gain": 3}))
        result = model.commit_queued()[0]
        victim = model.players["p2"]
        self.assertEqual("ko_committed", result["reason"])
        self.assertEqual(7, victim.banked_score)
        self.assertEqual(0, victim.unbanked)
        self.assertIsNone(victim.held_powerup)
        self.assertEqual(8, model.players["p1"].unbanked)

    def test_ko_reward_cannot_exceed_lost_unbanked(self):
        model = make_model()
        model.enqueue(req(operation="ko", payload={"ko_id": "ko-1", "victim_id": "p2", "attacker_gain": 9}))
        self.assertEqual("reward_exceeds_unbanked_loss", model.commit_queued()[0]["reason"])
        self.assertEqual(8, model.players["p2"].unbanked)

    def test_ko_duplicate_business_id_does_not_apply_twice(self):
        model = make_model()
        model.enqueue(req(seq=1, operation="ko", payload={"ko_id": "ko-x", "victim_id": "p2", "attacker_gain": 2}))
        model.commit_queued()
        model.phase = "playing"
        model.players["p2"].life = "alive"  # fixture simulates later respawn
        model.players["p2"].unbanked = 4
        model.enqueue(req(seq=2, operation="ko", payload={"ko_id": "ko-x", "victim_id": "p2", "attacker_gain": 2}))
        result = model.commit_queued()[0]
        self.assertEqual("duplicate_business_id", result["reason"])
        self.assertEqual(7, model.players["p1"].unbanked)
        self.assertEqual(4, model.players["p2"].unbanked)

    def test_request_after_cutoff_rejected(self):
        model = make_model()
        model.enqueue(req(tick=100))
        self.assertEqual("after_cutoff", model.commit_queued()[0]["reason"])
        self.assertEqual(5, model.players["p1"].unbanked)

    def test_bank_ko_same_tick_ordering_is_selectable_not_frozen(self):
        payload_bank = {"bank_id": "b-race"}
        payload_ko = {"ko_id": "ko-race", "victim_id": "p1", "attacker_gain": 5}
        outcomes = {}
        for ordering in ("bank_before_ko", "ko_before_bank"):
            model = make_model(ordering=ordering)
            model.enqueue(req(actor="p1", seq=1, operation="bank", payload=payload_bank, tick=10))
            model.enqueue(req(actor="p2", seq=1, session=4, operation="ko", payload=payload_ko, tick=10))
            model.commit_queued()
            outcomes[ordering] = (model.players["p1"].banked_score, model.players["p1"].unbanked)
        self.assertEqual((15, 0), outcomes["bank_before_ko"])
        self.assertEqual((10, 0), outcomes["ko_before_bank"])
        self.assertNotEqual(outcomes["bank_before_ko"], outcomes["ko_before_bank"])

    def test_cutoff_equality_policy_is_explicit_and_strict(self):
        model = make_model(cutoff_tick=20)
        model.enqueue(req(tick=19))
        self.assertEqual("pickup_committed", model.commit_queued()[0]["reason"])
        model = make_model(cutoff_tick=20)
        model.enqueue(req(tick=20))
        self.assertEqual("after_cutoff", model.commit_queued()[0]["reason"])

    def test_complete_snapshot_restores_and_increments_epoch(self):
        model = make_model()
        result = model.recover(valid_snapshot())
        self.assertEqual("recovered_fixture", result["status"])
        self.assertEqual(3, model.epoch)
        self.assertEqual(11, model.players["p1"].banked_score)
        self.assertEqual(2, model.players["p1"].unbanked)
        self.assertEqual(2, model.items["seed"].generation)

    def test_missing_snapshot_cancels_match(self):
        model = make_model()
        snap = valid_snapshot()
        snap.parts = []
        result = model.recover(snap)
        self.assertEqual(("cancelled", "snapshot_missing_parts"), (result["status"], result["reason"]))
        self.assertEqual("cancelled", model.phase)

    def test_incomplete_snapshot_cancels_match(self):
        model = make_model()
        snap = valid_snapshot()
        snap.complete = False
        self.assertEqual("cancelled", model.recover(snap)["status"])

    def test_mixed_snapshot_revision_cancels_match(self):
        model = make_model()
        snap = valid_snapshot()
        snap.parts = [snap.parts[0], SnapshotPart("snap-1", 4, 2, 1, 2, {})]
        snap.parts[0].total = 2
        self.assertEqual("mixed_snapshot_version", model.recover(snap)["reason"])

    def test_complete_multipart_snapshot_is_rejected_not_partially_consumed(self):
        model = make_model()
        snap = valid_snapshot()
        header = snap.parts[0].payload
        snap.parts[0].total = 2
        extra = SnapshotPart("snap-1", 5, 2, 1, 2,
                             {"players": [{"player_id": "p3"}], "items": []})
        snap.parts.append(extra)
        result = model.recover(snap)
        self.assertEqual(("cancelled", "unsupported_snapshot_parts"),
                         (result["status"], result["reason"]))
        self.assertEqual(2, model.epoch)
        self.assertNotIn("p3", model.players)

    def test_request_response_cache_is_scoped_to_authority_epoch(self):
        model = make_model()
        model.enqueue(req(seq=1, operation="pickup"))
        model.commit_queued()
        snap = valid_snapshot()
        header = snap.parts[0].payload
        header["commit_sequence"] = model.commit_sequence
        header["players"][0]["unbanked"] = 8
        header["items"][0]["available"] = False
        model.recover(snap)
        queued = model.enqueue(req(seq=1, epoch=3, operation="bank", payload={"bank_id": "bank-new"}))
        self.assertEqual("pending", queued["status"])
        outcome = model.commit_queued()[0]
        self.assertEqual("banked", outcome["reason"])
        self.assertEqual(19, model.players["p1"].banked_score)
        self.assertEqual(0, model.players["p1"].unbanked)

    def test_duplicate_snapshot_item_id_cancels_match(self):
        model = make_model()
        snap = valid_snapshot()
        snap.parts[0].payload["items"].append(dict(snap.parts[0].payload["items"][0]))
        self.assertEqual("duplicate_snapshot_item", model.recover(snap)["reason"])

    def test_snapshot_non_finite_number_cancels_match(self):
        model = make_model()
        snap = valid_snapshot()
        snap.parts[0].payload["unused"] = math.inf
        self.assertEqual("non_finite_number", model.recover(snap)["reason"])

    def test_recovery_rejects_old_epoch_after_increment(self):
        model = make_model()
        model.recover(valid_snapshot())
        result = model.enqueue(req(epoch=2, seq=8))
        self.assertEqual("stale_epoch", result["reason"])

    def test_malformed_non_json_payload_rejected(self):
        model = make_model()
        with self.assertRaisesRegex(ProtocolError, "non_json_value"):
            model.enqueue(req(payload={"spawn_id": object()}))

    def test_boolean_cannot_masquerade_as_integer(self):
        model = make_model()
        with self.assertRaisesRegex(ProtocolError, "invalid_sequence"):
            model.enqueue(req(seq=True))

    def test_integer_range_enforced_before_state_change(self):
        model = make_model()
        model.enqueue(req(payload={"spawn_id": "seed", "generation": 1}))
        # Replace gain only after model setup to probe malformed/extreme runtime state.
        model.items["seed"].gain = 2**40
        with self.assertRaisesRegex(ProtocolError, "invalid_gain"):
            model.commit_queued()
        self.assertTrue(model.items["seed"].available)

    def test_bank_score_overflow_is_rejected_atomically(self):
        model = make_model()
        model.players["p1"].banked_score = 2_147_483_646
        model.enqueue(req(operation="bank", payload={"bank_id": "b-max"}))
        result = model.commit_queued()[0]
        self.assertEqual("score_limit", result["reason"])
        self.assertEqual(2_147_483_646, model.players["p1"].banked_score)
        self.assertEqual(5, model.players["p1"].unbanked)


if __name__ == "__main__":
    unittest.main()
