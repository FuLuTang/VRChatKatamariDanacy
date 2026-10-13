import unittest

from Docs.Design.PickupAbsorb.pickup_absorb import (
    AuthorityLedger, Ball, Item, PickupRules, SpatialRegistry, eligible,
)


def item(name="rock", value=2.0):
    return Item(name, 1, (0.5, 0, 0), (0.2, 0.2, 0.2), "stone", value)


class PickupAbsorbTests(unittest.TestCase):
    def setUp(self):
        self.registry = SpatialRegistry(1.0)
        self.rock = item()
        self.registry.register(self.rock)

    def test_uniform_grid_nearby_and_bounds(self):
        self.assertEqual(self.registry.nearby((0, 0, 0), 1), (self.rock,))
        self.registry.remove(self.rock.key)
        self.assertEqual(self.registry.nearby((0, 0, 0), 1), ())

    def test_absorb_gates(self):
        rules = PickupRules(0.1, 0.5, frozenset({"stone"}), 5.0)
        self.assertTrue(eligible(self.rock, (0, 0, 0), 0.3, rules))
        self.assertFalse(eligible(Item("x", 1, (0, 0, 0), (1, 1, 1), "stone", 1), (0, 0, 0), 1, rules))
        self.assertFalse(eligible(Item("y", 1, (0, 0, 0), (0, 0, 0), "wood", 1), (0, 0, 0), 1, rules))
        self.assertFalse(eligible(self.rock, (0, 0, 0), 5.0, rules))

    def test_idempotent_single_count_and_replay(self):
        authority = AuthorityLedger()
        ball = Ball(1.0, 0.1, 2.0)
        first = authority.claim("req-1", "p1", self.rock)
        replay = authority.claim("req-1", "p1", self.rock)
        self.assertEqual(first, replay)
        self.assertEqual(len(authority.snapshot()), 1)
        ball.rebuild_from_snapshot(authority.snapshot(), self.registry)
        self.assertEqual(ball.confirmed_value, 2.0)
        self.assertEqual(ball.radius, 1.2)
        self.assertIsNone(authority.claim("req-2", "p1", self.rock))
        ball.rebuild_from_snapshot(authority.snapshot(), self.registry)
        self.assertEqual(ball.confirmed_value, 2.0)

    def test_two_balls_race_authority_wins_prediction_rolls_back(self):
        authority = AuthorityLedger()
        local_winner = Ball(1, 0.1, 3)
        local_loser = Ball(1, 0.1, 3)
        local_winner.predict(self.rock)
        local_loser.predict(self.rock)
        self.assertEqual(local_loser.confirmed_value, 0)
        self.assertEqual(authority.claim("a", "p1", self.rock).player_id, "p1")
        self.assertIsNone(authority.claim("b", "p2", self.rock))
        local_loser.reconcile(authority, self.registry)
        self.assertEqual(local_loser.predicted, {})
        self.assertEqual(local_loser.confirmed_value, 2)
        # A rejected local claimant has no confirmed contribution after rebuilding from empty authority.
        empty = AuthorityLedger()
        local_loser.predict(self.rock)
        local_loser.rebuild_from_snapshot(empty.snapshot(), self.registry)
        self.assertEqual(local_loser.confirmed_value, 0)
        self.assertEqual(local_loser.predicted, {})

    def test_radius_monotonic_and_capped(self):
        ball = Ball(1, 0.5, 2)
        radii = [ball.radius]
        authority = AuthorityLedger()
        for n in range(1, 5):
            obj = Item(str(n), 1, (0, 0, 0), (0, 0, 0), "stone", 1)
            self.registry.register(obj)
            authority.claim(f"r{n}", "p", obj)
            ball.rebuild_from_snapshot(authority.snapshot(), self.registry)
            radii.append(ball.radius)
        self.assertEqual(radii, sorted(radii))
        self.assertEqual(radii[-1], 2)


if __name__ == "__main__":
    unittest.main()
