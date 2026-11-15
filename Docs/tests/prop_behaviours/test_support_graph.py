"""支撑推导、移除传播与坠落的用例。

每个用例都对应 `PROP_BEHAVIOURS.md` §4 的一个具体几何，注释里写清「形状」，
这样改容差的人不用读代码就知道自己在改哪一条。
"""

import unittest

from Docs.Design.PickupAbsorb.prop_behaviours.support_graph import (
    ABSORBED, FALLING, MAX_SUPPORTERS, MIN_OVERLAP_FRACTION, RESTING,
    SETTLED, Body, SupportGraph, SupportRuntime, derive_supports,
    horizontal_overlap_fraction,
)

GROUND = 0.0


def cube(item_id, x, y, z, size=0.4, **kwargs):
    half = size / 2.0
    return Body(item_id, (x, y, z), (half, half, half), **kwargs)


def box(item_id, centre, half_extents, yaw_deg=0.0, **kwargs):
    return Body(item_id, centre, half_extents, yaw_deg=yaw_deg, **kwargs)


def build(bodies, structures=(), ground_y=GROUND):
    report = derive_supports(bodies, ground_y, structures)
    graph = SupportGraph(bodies, report, ground_y)
    return report, graph, SupportRuntime(graph)


def wall(x_min, x_max, height=2.0, depth=1.0):
    half_x = (x_max - x_min) / 2.0
    return Body("wall", (x_min + half_x, height / 2.0, 0.0),
                (half_x, height / 2.0, depth / 2.0), role="structure")


class StackOfStonesTests(unittest.TestCase):
    """五块石头叠成一摞：0.4 m 立方体，底面 0 / 0.4 / 0.8 / 1.2 / 1.6。"""

    def setUp(self):
        self.bodies = [cube("stone_%02d" % i, 0.0, 0.2 + 0.4 * i, 0.0)
                       for i in range(5)]

    def test_each_stone_rests_on_the_one_below(self):
        report, graph, _ = build(self.bodies)
        self.assertEqual(report.codes(), [])
        self.assertEqual(report.for_item("stone_00"), ())
        self.assertTrue(report.permanent["stone_00"])        # 贴地
        self.assertEqual(report.for_item("stone_01"), ("stone_00",))
        self.assertEqual(report.for_item("stone_04"), ("stone_03",))
        self.assertFalse(report.permanent["stone_04"])
        self.assertEqual(graph.live, [0, 1, 1, 1, 1])
        # CSR：支撑者 i 的承重者恰好是 i+1
        self.assertEqual(graph.dependent_count, [1, 1, 1, 1, 0])
        self.assertEqual(graph.dependent_list, [1, 2, 3, 4])

    def test_absorbing_the_bottom_two_at_once_drops_the_other_three(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["stone_00", "stone_01"])
        self.assertEqual(runtime.falls, ["stone_02", "stone_03", "stone_04"])
        runtime.settle_all()
        heights = runtime.heights()
        states = runtime.states()
        for name in ("stone_02", "stone_03", "stone_04"):
            self.assertEqual(states[name], SETTLED)
        for name in ("stone_00", "stone_01"):
            self.assertEqual(states[name], ABSORBED)
        # 整摞往下坐了两层：原来的第三块落到地面，第四第五块跟着坐低
        self.assertAlmostEqual(heights["stone_02"], 0.2, places=3)
        self.assertAlmostEqual(heights["stone_03"], 0.6, places=3)
        self.assertAlmostEqual(heights["stone_04"], 1.0, places=3)

    def test_batch_order_does_not_change_the_result(self):
        _, _, first = build(self.bodies)
        _, _, second = build(self.bodies)
        first.absorb(["stone_00", "stone_01"])
        second.absorb(["stone_01", "stone_00"])
        first.settle_all()
        second.settle_all()
        for name, height in first.heights().items():
            self.assertAlmostEqual(second.heights()[name], height, places=9)
        self.assertEqual(sorted(first.falls), sorted(second.falls))

    def test_absorbing_the_third_stone_drops_only_the_two_above(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["stone_02"])
        runtime.settle_all()
        heights = runtime.heights()
        self.assertAlmostEqual(heights["stone_00"], 0.2, places=3)
        self.assertAlmostEqual(heights["stone_01"], 0.6, places=3)
        self.assertAlmostEqual(heights["stone_03"], 1.0, places=3)
        self.assertAlmostEqual(heights["stone_04"], 1.4, places=3)

    def test_a_stone_absorbed_while_falling_is_removed_without_breaking_the_rest(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["stone_00"])
        runtime.step()
        runtime.step()
        self.assertEqual(runtime.states()["stone_04"], FALLING)
        runtime.absorb(["stone_04"])
        runtime.settle_all()
        self.assertEqual(runtime.states()["stone_04"], ABSORBED)
        self.assertEqual(runtime.states()["stone_03"], SETTLED)


class PyramidTests(unittest.TestCase):
    """三块底座 + 一块横跨两块底座的上层件（并列支撑）。"""

    def setUp(self):
        self.bodies = [
            cube("pyr_0", -0.4, 0.2, 0.0),
            cube("pyr_1", 0.0, 0.2, 0.0),
            cube("pyr_2", 0.4, 0.2, 0.0),
            cube("pyr_top", -0.2, 0.6, 0.0),
        ]

    def test_the_top_piece_reports_both_supports_at_equal_height(self):
        report, graph, _ = build(self.bodies)
        self.assertEqual(report.codes(), [])
        self.assertEqual(report.for_item("pyr_top"), ("pyr_0", "pyr_1"))
        self.assertEqual(graph.live[3], 2)
        self.assertLessEqual(len(report.for_item("pyr_top")), MAX_SUPPORTERS)

    def test_one_of_two_supports_is_enough_to_keep_the_top_piece_up(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["pyr_0"])
        self.assertEqual(runtime.falls, [])
        self.assertEqual(runtime.states()["pyr_top"], RESTING)

    def test_the_second_support_starts_the_fall(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["pyr_0"])
        runtime.absorb(["pyr_1"])
        self.assertEqual(runtime.falls, ["pyr_top"])
        runtime.settle_all()
        # 右底座不接住它（水平投影不重叠），落回地面
        self.assertAlmostEqual(runtime.heights()["pyr_top"], 0.2, places=3)

    def test_both_supports_in_one_batch_also_drops_it_once(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["pyr_0", "pyr_1"])
        self.assertEqual(runtime.falls, ["pyr_top"])


class TableOnTwoLegsTests(unittest.TestCase):
    """一条桌面搭在两条细腿上：验证「压住比例」在细支撑上也算得对。"""

    def setUp(self):
        self.bodies = [
            box("leg_l", (-0.5, 0.25, 0.0), (0.05, 0.25, 0.05)),
            box("leg_r", (0.5, 0.25, 0.0), (0.05, 0.25, 0.05)),
            box("tabletop", (0.0, 0.55, 0.0), (0.7, 0.05, 0.4)),
        ]

    def test_two_thin_legs_still_count_as_supports(self):
        report, _, _ = build(self.bodies)
        self.assertEqual(report.codes(), [])
        self.assertEqual(report.for_item("tabletop"), ("leg_l", "leg_r"))

    def test_tabletop_falls_only_when_both_legs_are_gone(self):
        _, _, runtime = build(self.bodies)
        runtime.absorb(["leg_l"])
        self.assertEqual(runtime.falls, [])
        runtime.absorb(["leg_r"])
        self.assertEqual(runtime.falls, ["tabletop"])
        runtime.settle_all()
        self.assertAlmostEqual(runtime.heights()["tabletop"], 0.05, places=3)


class RotatedBoxTests(unittest.TestCase):
    """绕 Y 转过的盒子：世界 AABB 用闭式解，不枚举角点。"""

    def test_yaw_45_world_half_extent_matches_the_closed_form(self):
        rotated = box("plank", (0.0, 0.5, 0.0), (0.3, 0.1, 0.1), yaw_deg=45.0)
        self.assertAlmostEqual(rotated.half_world[0], 0.4 * 0.70710678, places=6)
        self.assertAlmostEqual(rotated.half_world[1], 0.1)
        self.assertAlmostEqual(rotated.half_world[2], 0.4 * 0.70710678, places=6)

    def test_a_yawed_box_on_a_level_box_is_supported(self):
        lower = cube("crate", 0.0, 0.2, 0.0)
        upper = box("plank", (0.0, 0.5, 0.0), (0.3, 0.1, 0.1), yaw_deg=37.0)
        report, _, _ = build([lower, upper])
        self.assertEqual(report.codes(), [])
        self.assertEqual(report.for_item("plank"), ("crate",))
        self.assertGreaterEqual(horizontal_overlap_fraction(upper, lower),
                                MIN_OVERLAP_FRACTION)

    def test_a_yawed_box_hanging_off_a_corner_is_reported_as_floating(self):
        lower = cube("crate", 0.0, 0.2, 0.0)
        # 只压住 12.5% 的投影面积（< 20%），不是支撑
        upper = cube("edge_case", 0.35, 0.6, 0.0)
        report, _, _ = build([lower, upper])
        self.assertEqual(report.for_item("edge_case"), ())
        self.assertIn("FLOATING", report.codes())
        self.assertLess(horizontal_overlap_fraction(upper, lower),
                        MIN_OVERLAP_FRACTION)


class ToleranceTests(unittest.TestCase):
    """容差：允许陷进去 1 cm，允许浮 2 cm，不允许浮 3 cm。"""

    def test_a_box_sunk_one_centimetre_still_counts_as_supported(self):
        lower = cube("crate", 0.0, 0.2, 0.0)
        upper = cube("lid", 0.0, 0.6 - 0.008, 0.0)   # 底面 0.392，陷进 8 mm
        report, _, _ = build([lower, upper])
        self.assertEqual(report.for_item("lid"), ("crate",))

    def test_a_box_hovering_three_centimetres_is_floating(self):
        lower = cube("crate", 0.0, 0.2, 0.0)
        upper = cube("lid", 0.0, 0.6 + 0.03, 0.0)    # 底面 0.43
        report, _, _ = build([lower, upper])
        self.assertEqual(report.for_item("lid"), ())
        self.assertIn("FLOATING", report.codes())

    def test_declared_floating_decor_is_not_a_violation(self):
        balloon = cube("balloon", 0.0, 3.0, 0.0, floating_ok=True)
        report, _, _ = build([balloon])
        self.assertEqual(report.codes(), [])
        self.assertFalse(report.permanent["balloon"])


class LeanTests(unittest.TestCase):
    """靠着：靠箱子会掉，靠墙不会——差别只在支撑者会不会被吸收。"""

    def test_a_plank_leaning_on_a_crate_falls_when_the_crate_is_absorbed(self):
        crate = box("crate", (0.52, 0.3, 0.0), (0.2, 0.3, 0.2))     # 顶面 0.6
        plank = box("plank", (0.0, 0.55, 0.0), (0.3, 0.05, 0.1))
        report, graph, runtime = build([crate, plank])
        self.assertEqual(report.for_item("plank"), ("crate",))
        self.assertFalse(report.permanent["plank"])
        self.assertEqual(graph.live[1], 1)
        runtime.absorb(["crate"])
        self.assertEqual(runtime.falls, ["plank"])
        runtime.settle_all()
        self.assertAlmostEqual(runtime.heights()["plank"], 0.05, places=3)

    def test_a_plank_leaning_on_a_wall_is_permanent(self):
        plank = box("plank", (0.0, 0.55, 0.0), (0.3, 0.05, 0.1))
        report, graph, runtime = build([plank], structures=[wall(0.32, 1.32)])
        self.assertEqual(report.codes(), [])
        self.assertEqual(report.for_item("plank"), ("wall",))
        self.assertTrue(report.permanent["plank"])
        self.assertEqual(graph.live, [0])
        # 没有任何东西能减掉它的支撑计数
        self.assertEqual(runtime.falls, [])

    def test_a_leaning_pair_with_no_external_support_is_a_cycle(self):
        left = cube("brace_l", 0.0, 1.0, 0.0)
        right = cube("brace_r", 0.42, 1.0, 0.0)      # 水平间隙 2 cm
        report, _, runtime = build([left, right])
        self.assertEqual(report.for_item("brace_l"), ("brace_r",))
        self.assertEqual(report.for_item("brace_r"), ("brace_l",))
        self.assertEqual(report.codes().count("CYCLE"), 2)
        # 成环的按「钉住」处理：不传播坠落，避免自相矛盾
        self.assertTrue(report.permanent["brace_l"])
        runtime.absorb(["brace_l"])
        self.assertEqual(runtime.falls, [])


class BookkeepingTests(unittest.TestCase):
    """数据结构上的硬约束：定长、无字典、队列不溢出。"""

    def test_a_twelve_high_chain_fits_the_fixed_worklist(self):
        bodies = [cube("c_%02d" % i, 0.0, 0.2 + 0.4 * i, 0.0) for i in range(12)]
        _, _, runtime = build(bodies)
        runtime.absorb(["c_00"])
        self.assertEqual(len(runtime.falls), 11)
        runtime.settle_all()
        self.assertAlmostEqual(runtime.heights()["c_11"],
                               0.2 + 0.4 * 10, places=2)

    def test_support_derivation_is_order_independent_in_the_input_list(self):
        bodies = [cube("stone_%02d" % i, 0.0, 0.2 + 0.4 * i, 0.0)
                  for i in range(3)]
        forward = derive_supports(bodies, GROUND)
        backward = derive_supports(list(reversed(bodies)), GROUND)
        self.assertEqual(forward.supporters, backward.supporters)

    def test_a_falling_body_never_lands_through_a_settled_one(self):
        bodies = [cube("a", 0.0, 0.2, 0.0), cube("b", 0.0, 0.6, 0.0),
                  cube("c", 0.0, 1.0, 0.0)]
        _, _, runtime = build(bodies)
        runtime.absorb(["b"])
        runtime.settle_all()
        heights = runtime.heights()
        self.assertAlmostEqual(heights["a"], 0.2, places=3)
        # c 落到 a 上面，不是地面
        self.assertAlmostEqual(heights["c"], 0.6, places=3)

    def test_graph_can_be_built_for_a_prop_resting_on_a_structure(self):
        shelf = Body("shelf", (0.0, 1.0, 0.0), (0.6, 0.05, 0.3),
                     role="structure")
        vase = cube("vase", 0.0, 1.15, 0.0, size=0.2)
        report, graph, runtime = build([vase], structures=[shelf])
        self.assertEqual(report.for_item("vase"), ("shelf",))
        self.assertTrue(report.permanent["vase"])
        self.assertEqual(graph.live, [0])
        runtime.absorb(["vase"])
        self.assertEqual(runtime.state[0], ABSORBED)


if __name__ == '__main__':   # pragma: no cover
    unittest.main()
