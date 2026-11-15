import unittest

from Docs.Design.PickupAbsorb.prop_behaviours.budget import (
    DEFAULT_COST_MS_PER_UPDATE, fall_cost_ms, game_budget_ms, max_movers,
    movers_per_frame, steady_state_cost_ms, stride_for, updates_per_frame,
)


class BudgetTests(unittest.TestCase):
    """预算算术：只验证数量级与单调性，不声称任何实测帧时间。"""

    def test_one_ninetieth_of_a_frame_is_eleven_point_one_ms(self):
        self.assertAlmostEqual(game_budget_ms(share=1.0), 11.1111, places=3)
        self.assertAlmostEqual(game_budget_ms(), 2.7778, places=3)
        with self.assertRaises(ValueError):
            game_budget_ms(frame_ms=0.0)
        with self.assertRaises(ValueError):
            game_budget_ms(share=0.0)

    def test_a_quarter_frame_buys_about_a_hundred_and_forty_single_writes(self):
        budget = game_budget_ms()
        self.assertEqual(updates_per_frame(budget), 138)
        self.assertEqual(updates_per_frame(0.001), 1)   # 至少一次，不返回 0
        with self.assertRaises(ValueError):
            updates_per_frame(budget, cost_ms_per_update=0.0)

    def test_stride_is_the_frame_rate_over_the_update_rate(self):
        self.assertEqual(stride_for(120, 10.0, 90.0), 9)
        self.assertEqual(stride_for(1, 90.0, 90.0), 1)
        self.assertEqual(stride_for(5, 60.0, 90.0), 2)     # round(1.5) = 2
        with self.assertRaises(ValueError):
            stride_for(0, 10.0, 90.0)
        with self.assertRaises(ValueError):
            stride_for(10, 90.0, 60.0)   # 不能比帧还快

    def test_halving_the_update_rate_halves_the_cost(self):
        full = steady_state_cost_ms(120, 30.0, 90.0)
        half = steady_state_cost_ms(120, 15.0, 90.0)
        self.assertAlmostEqual(full, 120 / 3 * DEFAULT_COST_MS_PER_UPDATE)
        self.assertAlmostEqual(half, full / 2.0, places=9)

    def test_cost_is_linear_in_movers_at_a_fixed_stride(self):
        a = steady_state_cost_ms(90, 10.0, 90.0)
        b = steady_state_cost_ms(900, 10.0, 90.0)
        self.assertAlmostEqual(b, a * 10.0, places=9)
        self.assertAlmostEqual(movers_per_frame(90, 10.0, 90.0), 10.0)

    def test_max_movers_is_the_largest_count_that_still_fits(self):
        budget = game_budget_ms()
        fitted = max_movers(budget, 10.0, 90.0)
        self.assertLessEqual(steady_state_cost_ms(fitted, 10.0, 90.0), budget)
        self.assertGreater(steady_state_cost_ms(fitted + 1, 10.0, 90.0),
                           budget)
        self.assertEqual(max_movers(0.0, 10.0, 90.0), 0)

    def test_a_short_fall_costs_a_fraction_of_a_millisecond(self):
        # 0.8 m 自由落体 ≈ 0.404 s ≈ 13 步（30 Hz 固定步长）
        self.assertAlmostEqual(fall_cost_ms(0.8), 13 * DEFAULT_COST_MS_PER_UPDATE)
        self.assertEqual(fall_cost_ms(0.0), 0.0)
        with self.assertRaises(ValueError):
            fall_cost_ms(-1.0)


if __name__ == '__main__':   # pragma: no cover
    unittest.main()
