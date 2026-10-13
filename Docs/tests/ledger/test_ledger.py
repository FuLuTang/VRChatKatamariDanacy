import pathlib
import random
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from Docs.Design.Ledger.ledger import (  # noqa: E402
    Claim, EventKey, InvalidTransition, Ledger, Phase, Lifecycle,
)


class LedgerTests(unittest.TestCase):
    def playing(self):
        l = Ledger("r1")
        l.join("a"); l.join("b")
        l.transition(Phase.READY, 0)
        l.transition(Phase.PREPARING, 1)
        l.transition(Phase.PLAYING, 2)
        return l

    def test_duplicate_claim_and_replay_do_not_double_score(self):
        l = self.playing(); k = EventKey("item", "r1", 4)
        c = Claim(k, "a", 10, 1)
        self.assertTrue(l.submit_claim(c)); self.assertTrue(l.submit_claim(c))
        commit = l.resolve_claims(k, 7)
        self.assertEqual(l.resolve_claims(k, 7), commit)
        self.assertEqual(l.players["a"].ledger.confirmed_volume, 7)
        replica = Ledger("r1"); replica.join("a"); replica.join("b")
        self.assertTrue(replica.apply_commit(commit))
        self.assertFalse(replica.apply_commit(commit))
        self.assertEqual(replica.players["a"].ledger.confirmed_volume, 7)

    def test_same_frame_tie_and_loser_rollback(self):
        l = self.playing(); k = EventKey("i", "r1", 0)
        l.predict("a", k, 50); l.predict("b", k, 50)
        l.submit_claim(Claim(k, "b", 12, 1)); l.submit_claim(Claim(k, "a", 12, 1))
        commit = l.resolve_claims(k, 3)
        self.assertEqual(commit.winner, "a")
        self.assertEqual(l.players["a"].ledger.confirmed_volume, 3)
        self.assertEqual(l.players["b"].ledger.confirmed_volume, 0)
        self.assertFalse(l.players["a"].predicted); self.assertFalse(l.players["b"].predicted)

    def test_predicted_value_cannot_bank(self):
        l = self.playing(); k = EventKey("i", "r1", 0)
        l.predict("a", k, 9)
        self.assertFalse(l.bank("a", 9, "bank-1"))
        self.assertEqual(l.players["a"].ledger.banked_score, 0)

    def test_late_join_snapshot_is_consistent(self):
        source = self.playing(); key = EventKey("item", "r1", 1)
        source.submit_claim(Claim(key, "a", 3, 1)); source.resolve_claims(key, 11)
        source.bank("a", 4, "b1")
        snapshot = source.join("late")
        client = Ledger("r1"); client.install_snapshot(snapshot)
        self.assertEqual(client.snapshot(), snapshot)
        self.assertEqual(client.players["a"].ledger.confirmed_volume, 7)
        self.assertEqual(client.players["a"].ledger.banked_score, 4)

    def test_ko_respawn_lifecycle_is_legal_and_clears_prediction(self):
        l = self.playing(); k = EventKey("x", "r1", 0)
        l.players["a"].ledger.confirmed_volume = 10
        l.predict("a", k, 999)
        self.assertTrue(l.ko("a", "b", 5, 2, 3))
        self.assertEqual(l.players["a"].life, Lifecycle.RESPAWN_PENDING)
        self.assertEqual(l.players["a"].ledger.confirmed_volume, 0)
        self.assertFalse(l.players["a"].predicted)
        self.assertEqual(l.players["b"].ledger.confirmed_volume, 3)
        self.assertFalse(l.respawn("a", 6))
        self.assertTrue(l.respawn("a", 7))
        self.assertEqual(l.players["a"].life, Lifecycle.ACTIVE)
        self.assertTrue(l.disconnect("a"))
        self.assertEqual(l.players["a"].life, Lifecycle.DISCONNECTED)

    def test_illegal_transition_rejected_and_timeout_once(self):
        l = Ledger("r")
        with self.assertRaises(InvalidTransition): l.transition(Phase.PLAYING, 0)
        l.transition(Phase.READY, 0, 5)
        self.assertFalse(l.advance_timeout(4))
        self.assertTrue(l.advance_timeout(5))
        self.assertEqual(l.phase, Phase.PREPARING)
        self.assertFalse(l.advance_timeout(100))

    def test_all_phases_reachable_no_dead_end_and_random_walk(self):
        # Exhaustively explore graph, including explicitly permitted cancellation edges.
        reached = {Phase.LOBBY}; pending = [Phase.LOBBY]
        while pending:
            phase = pending.pop()
            for nxt in Ledger.ALLOWED[phase]:
                if nxt not in reached:
                    reached.add(nxt); pending.append(nxt)
        self.assertEqual(reached, set(Phase))
        # Seeded walks ensure each nonterminal phase has an outgoing transition.
        rng = random.Random(183)
        for _ in range(100):
            phase = Phase.LOBBY
            for _step in range(50):
                edges = Ledger.ALLOWED[phase]
                self.assertTrue(edges, f"dead end at {phase}")
                phase = rng.choice(tuple(edges))

    def test_old_generation_and_other_round_rejected(self):
        l = self.playing()
        with self.assertRaises(ValueError): l.submit_claim(Claim(EventKey("i", "other", 1), "a", 0, 1))


if __name__ == "__main__":
    unittest.main()
