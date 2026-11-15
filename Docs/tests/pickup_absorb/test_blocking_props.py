import unittest

from Docs.Design.PickupAbsorb.blocking_props import (
    is_beyond_absorb_limit, select_nearest, try_box_from_centre_and_extents,
    try_distance_to_box,
)

BALL = (0.0, 0.0, 0.0)


def box(minimum, maximum):
    return minimum, maximum


class BlockingPropTests(unittest.TestCase):
    """S2 用例：与 Tests/EditMode/BlockingPropMathTests.cs 一一对应。"""

    def test_near_face_of_a_long_prop_beats_the_centre_of_a_small_one(self):
        # 3 m 长凳的中心在 2.0 m 外，近面在 1.5 m 外；小件的中心在 1.875 m 外，
        # 近面在 1.75 m 外。按中心距小件更近，按 AABB 距离长凳更近——选长凳。
        bench = try_box_from_centre_and_extents((0.0, 0.0, 2.0), (3.0, 0.5, 1.0))
        self.assertEqual(bench, ((-1.5, -0.25, 1.5), (1.5, 0.25, 2.5)))
        small = try_box_from_centre_and_extents((0.0, 0.0, 1.875), (0.25, 0.25, 0.25))
        self.assertEqual(small, ((-0.125, -0.125, 1.75), (0.125, 0.125, 2.0)))
        self.assertLess(1.875, 2.0)  # 中心距排序会选错，本用例才说明用面距
        result = select_nearest(BALL, 4.0, (bench[0], small[0]), (bench[1], small[1]),
                                (True, True), 2, (), 0, 0.0, 2)
        self.assertEqual(result, ((0, 1), (1.5, 1.75)))

    def test_ball_inside_a_box_is_distance_zero_and_ranks_first(self):
        inside = box((-1.0, -1.0, -1.0), (1.0, 1.0, 1.0))
        near = box((2.0, -1.0, -1.0), (3.0, 1.0, 1.0))
        ball = (0.0, 0.0, 0.5)
        self.assertEqual(try_distance_to_box(ball, inside[0], inside[1]), 0.0)
        self.assertEqual(try_distance_to_box(ball, near[0], near[1]), 2.0)
        result = select_nearest(ball, 3.0, (inside[0], near[0]), (inside[1], near[1]),
                                (True, True), 2, (), 0, 0.0, 2)
        self.assertEqual(result, ((0, 1), (0.0, 2.0)))

    def test_equal_distances_break_by_ascending_index_and_are_stable(self):
        front = box((-0.5, -0.5, 1.5), (0.5, 0.5, 2.5))
        back = box((-0.5, -0.5, -2.5), (0.5, 0.5, -1.5))
        first = select_nearest(BALL, 4.0, (front[0], back[0]), (front[1], back[1]),
                               (True, True), 2, (), 0, 0.0, 2)
        second = select_nearest(BALL, 4.0, (front[0], back[0]), (front[1], back[1]),
                                (True, True), 2, (), 0, 0.0, 2)
        self.assertEqual(first, ((0, 1), (1.5, 1.5)))
        self.assertEqual(first, second)
        clipped = select_nearest(BALL, 4.0, (front[0], back[0]), (front[1], back[1]),
                                 (True, True), 2, (), 0, 0.0, 1)
        self.assertEqual(clipped, ((0,), (1.5,)))

    def test_stickiness_keeps_a_previous_prop_until_the_new_one_leaves_the_sticky_band(self):
        # 旧件在 1.2 m（半径 1.0 外、粘性带 1.25 内），新件在 1.0 m。
        old = box((1.2, -0.5, -0.5), (1.6, 0.5, 0.5))
        new = box((1.0, -0.5, -0.5), (1.4, 0.5, 0.5))
        kept = select_nearest(BALL, 1.0, (old[0], new[0]), (old[1], new[1]),
                              (True, True), 2, (0,), 1, 0.25, 1)
        self.assertEqual(kept, ((0,), (1.2,)))
        # 旧件走到 1.26 m：超出 radius + stickiness，才让位给更近的新件。
        moved = box((1.26, -0.5, -0.5), (1.66, 0.5, 0.5))
        swapped = select_nearest(BALL, 1.0, (moved[0], new[0]), (moved[1], new[1]),
                                 (True, True), 2, (0,), 1, 0.25, 1)
        self.assertEqual(swapped, ((1,), (1.0,)))
        # 没有上一批时并不存在粘性，直接选近的。
        no_history = select_nearest(BALL, 1.0, (old[0], new[0]), (old[1], new[1]),
                                    (True, True), 2, (), 0, 0.25, 1)
        self.assertEqual(no_history, ((1,), (1.0,)))

    def test_capacity_clamp_and_empty_inputs(self):
        minima = ((3.0, -0.5, -0.5), (1.0, -0.5, -0.5), (2.0, -0.5, -0.5))
        maxima = ((3.6, 0.5, 0.5), (1.6, 0.5, 0.5), (2.6, 0.5, 0.5))
        clamped = select_nearest(BALL, 10.0, minima, maxima, (True, True, True), 3,
                                 (), 0, 0.0, 2)
        self.assertEqual(clamped, ((1, 2), (1.0, 2.0)))
        self.assertEqual(select_nearest(BALL, 10.0, minima, maxima,
                                        (True, True, True), 3, (), 0, 0.0, 0), ((), ()))
        self.assertEqual(select_nearest(BALL, 10.0, (), (), (), 0, (), 0, 0.0, 2),
                         ((), ()))
        # 候选标记为假的道具即使更近也不参与。
        skipped = select_nearest(BALL, 10.0, minima, maxima, (True, False, True), 3,
                                 (), 0, 0.0, 2)
        self.assertEqual(skipped, ((2, 0), (2.0, 3.0)))

    def test_non_finite_or_inverted_input_is_refused(self):
        good = box((-1.0, -1.0, -1.0), (1.0, 1.0, 1.0))
        self.assertIsNone(try_distance_to_box((float("nan"), 0.0, 0.0), good[0], good[1]))
        self.assertIsNone(try_distance_to_box(BALL, (1.0, -1.0, -1.0), (0.0, 1.0, 1.0)))
        self.assertIsNone(try_box_from_centre_and_extents(BALL, (-1.0, 1.0, 1.0)))
        self.assertIsNone(try_box_from_centre_and_extents((float("inf"), 0.0, 0.0),
                                                          (1.0, 1.0, 1.0)))
        bad = box((float("nan"), -0.5, -0.5), (0.5, 0.5, 0.5))
        self.assertIsNone(select_nearest(BALL, 1.0, (bad[0],), (bad[1],), (True,), 1,
                                         (), 0, 0.0, 1))
        self.assertIsNone(select_nearest((float("nan"), 0.0, 0.0), 1.0, good[0:1],
                                         good[1:2], (True,), 1, (), 0, 0.0, 1))
        self.assertIsNone(select_nearest(BALL, -1.0, good[0:1], good[1:2],
                                         (True,), 1, (), 0, 0.0, 1))
        self.assertIsNone(select_nearest(BALL, 1.0, good[0:1], good[1:2],
                                         (True,), 2, (), 0, 0.0, 1))
        self.assertIsNone(select_nearest(BALL, 1.0, good[0:1], good[1:2],
                                         (True,), 1, (0,), 2, 0.0, 1))

    def test_extent_equal_to_the_limit_is_absorbable(self):
        # 边界含等号：extent == limit 可吸、不挡；只有严格更大才挡。
        self.assertFalse(is_beyond_absorb_limit((0.5, 0.05, 0.05), 0.5))
        self.assertFalse(is_beyond_absorb_limit((0.4999, 0.05, 0.05), 0.5))
        self.assertTrue(is_beyond_absorb_limit((0.5001, 0.05, 0.05), 0.5))
        # 比较的是最大全长，不是第一个分量。
        self.assertFalse(is_beyond_absorb_limit((0.1, 0.5, 0.1), 0.5))
        self.assertTrue(is_beyond_absorb_limit((0.1, 0.5001, 0.1), 0.5))
        # 坏数据按"超出上限"处理，不能因为 NaN 比较恒假而放行。
        self.assertTrue(is_beyond_absorb_limit((float("nan"), 0.0, 0.0), 0.5))
        self.assertTrue(is_beyond_absorb_limit((-1.0, 0.05, 0.05), 0.5))
        self.assertTrue(is_beyond_absorb_limit((0.5, 0.05, 0.05), float("inf")))

    def test_growth_crossing_the_limit_flips_the_same_function(self):
        # D = 1.20 的球上限 1.20，1.25 m 的件挡；D = 1.25 的球上限 1.25，同一件可吸。
        item = (1.25, 0.05, 0.05)
        self.assertTrue(is_beyond_absorb_limit(item, 1.20))
        self.assertFalse(is_beyond_absorb_limit(item, 1.25))
        # 同一个函数、同一组输入，两处调用必须给出同一个答案（单一门槛）。
        self.assertEqual(is_beyond_absorb_limit(item, 1.20),
                         is_beyond_absorb_limit(item, 1.20))

    def test_shrinking_recomputes_with_no_residual_state(self):
        bench = (1.25, 0.1, 0.1)
        pebble = (0.3, 0.1, 0.1)

        def blocking_candidates(limit):
            return (is_beyond_absorb_limit(bench, limit),
                    is_beyond_absorb_limit(pebble, limit))

        self.assertEqual(blocking_candidates(1.25), (False, False))
        self.assertEqual(blocking_candidates(1.20), (True, False))
        # 缩小再长回来：纯重算，没有残留状态。
        self.assertEqual(blocking_candidates(1.25), (False, False))

        minima = ((1.0, -0.5, -0.5), (2.0, -0.5, -0.5))
        maxima = ((1.5, 0.5, 0.5), (2.5, 0.5, 0.5))
        grown = select_nearest(BALL, 10.0, minima, maxima, blocking_candidates(1.25),
                               2, (), 0, 0.0, 2)
        shrunk = select_nearest(BALL, 10.0, minima, maxima, blocking_candidates(1.20),
                                2, (), 0, 0.0, 2)
        self.assertEqual(grown, ((), ()))
        self.assertEqual(shrunk, ((0,), (1.0,)))
        self.assertEqual(select_nearest(BALL, 10.0, minima, maxima,
                                        blocking_candidates(1.25), 2, (), 0, 0.0, 2),
                         grown)

    def test_outside_the_radius_is_not_selected_and_the_boundary_is_inclusive(self):
        on_edge = box((1.0, -0.5, -0.5), (2.0, 0.5, 0.5))
        outside = box((1.0001, -0.5, -0.5), (2.0001, 0.5, 0.5))
        inside = box((0.9999, -0.5, -0.5), (1.9999, 0.5, 0.5))
        self.assertEqual(try_distance_to_box(BALL, on_edge[0], on_edge[1]), 1.0)
        self.assertEqual(select_nearest(BALL, 1.0, (on_edge[0],), (on_edge[1],),
                                        (True,), 1, (), 0, 0.0, 1),
                         ((0,), (1.0,)))
        self.assertEqual(select_nearest(BALL, 1.0, (outside[0],), (outside[1],),
                                        (True,), 1, (), 0, 0.0, 1), ((), ()))
        self.assertEqual(select_nearest(BALL, 1.0, (inside[0],), (inside[1],),
                                        (True,), 1, (), 0, 0.0, 1),
                         ((0,), (0.9999,)))


if __name__ == "__main__":
    unittest.main()
