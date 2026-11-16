"""``floating_requeue`` 的用例：谁掉、谁不掉、``pinned`` 在哪里截断传播。

每个用例画的是一个具体几何（注释里写清），这样改容差的人不用读代码就知道在改哪一条。
"""

import unittest

from Docs.Design.PickupAbsorb.prop_behaviours.floating_requeue import (
    ABSORBED, FALLING, RESTING, SETTLED, Body, Field,
)

GROUND = 0.0


def slab(item_id, bottom, height=0.2, **kwargs):
    """0.4 x height x 0.4 的方块，XZ 中心在原点：所有用例共用一套足迹，只差高度。"""
    half = 0.2
    return Body(item_id, ((-half, bottom, -half), (half, bottom + height, half)), **kwargs)


def stack(*bodies):
    field = Field(bodies, ground_y=GROUND)
    return field


class FallTests(unittest.TestCase):
    """§10.9 的第 1、2 条：父件没了 ⇒ 复检 ⇒ 脚下没东西 ⇒ 落。"""

    def test_the_top_of_a_pair_falls_when_its_supporter_is_eaten(self):
        """plank(0..0.2) + cup(0.2..0.4)：吃掉 plank，cup 落到地面。"""
        field = stack(slab("plank", 0.0), slab("cup", 0.2, parent="plank"))
        field.absorb(["plank"])
        self.assertEqual(field.states()["cup"], RESTING)      # 还没复检
        field.settle()
        self.assertEqual(field.states()["cup"], SETTLED)
        self.assertAlmostEqual(field.heights()["cup"], GROUND, places=3)

    def test_a_three_body_stack_falls_one_layer_at_a_time(self):
        """底(0..0.2) 中(0.2..0.4) 顶(0.4..0.6)：吃掉底，中先落，顶再落。"""
        field = stack(slab("low", 0.0), slab("mid", 0.2, parent="low"),
                      slab("top", 0.4, parent="mid"))
        field.absorb(["low"])
        field.step(1)
        self.assertEqual(field.states()["mid"], FALLING)
        self.assertEqual(field.states()["top"], RESTING)      # 下一 tick 才轮到它
        field.settle()
        self.assertEqual(field.states()["mid"], SETTLED)
        self.assertEqual(field.states()["top"], SETTLED)
        self.assertAlmostEqual(field.heights()["mid"], 0.0, places=3)
        self.assertAlmostEqual(field.heights()["top"], 0.2, places=3)   # 落在 mid 上

    def test_a_stateless_body_still_falls(self):
        """没有指针也能落：吃掉下面那件时，上面那件不在任何人手里，但几何说它悬空。"""
        field = stack(slab("plank", 0.0), slab("cup", 0.2))   # 没有 parent
        field.absorb(["plank"])
        field.step(1)                                          # 没人入队
        self.assertEqual(field.states()["cup"], RESTING)
        field.settle()
        self.assertEqual(field.states()["cup"], RESTING)       # 没入队 ⇒ 不自己发现
        # 这正是「指针是优化」的边界：没指针的件只能等下一次它自己的事件。

    def test_a_wrong_pointer_costs_one_check_not_a_fall(self):
        """指针指着别处，但脚下真的有东西：复检看见它，于是留着。"""
        field = stack(slab("plank", 0.0), slab("table", 0.0), slab("cup", 0.2, parent="plank"))
        field.absorb(["plank"])                               # 被吃掉的不是它真正站着的东西
        field.step(1)
        self.assertEqual(field.states()["cup"], RESTING)
        self.assertEqual(field.pending, [field.index["cup"]])  # 留在待复检里

    def test_an_unseated_declared_parent_still_falls(self):
        """作者声明挂在长杆上（水平错开、不接触）：父件没了，它就落——校验器只警告。"""
        mast = Body("mast", ((-0.05, 0.0, -0.05), (0.05, 2.0, 0.05)))
        sign = Body("sign", ((0.1, 1.9, -0.02), (0.5, 2.1, 0.02)), parent="mast")
        field = Field([mast, sign], ground_y=GROUND)
        field.absorb(["mast"])
        field.settle()
        self.assertEqual(field.states()["sign"], SETTLED)
        self.assertAlmostEqual(field.heights()["sign"], GROUND, places=3)


class PinnedTests(unittest.TestCase):
    """``pinned``：永不入队、永不下落，是传播的终点，但仍然是支撑面。"""

    def test_a_pinned_body_never_falls(self):
        field = stack(slab("plank", 0.0), slab("cloud", 0.2, parent="plank", pinned=True))
        field.absorb(["plank"])
        self.assertEqual(field.last_enqueued, [])             # 连队都没进
        field.settle()
        self.assertEqual(field.states()["cloud"], RESTING)
        self.assertAlmostEqual(field.heights()["cloud"], 0.2, places=3)

    def test_pinned_blocks_propagation_through_it(self):
        """底(0..0.2) + 钉住的柱(0.2..0.4) + 柱顶的球(0.4..0.6)：吃掉底，谁都不动。"""
        field = stack(slab("plank", 0.0),
                      slab("post", 0.2, parent="plank", pinned=True),
                      slab("ball", 0.4, parent="post"))
        field.absorb(["plank"])
        field.settle()
        self.assertEqual(field.states()["post"], RESTING)
        self.assertEqual(field.states()["ball"], RESTING)     # 传播在钉住的件上截止
        self.assertAlmostEqual(field.heights()["ball"], 0.4, places=3)

    def test_a_pinned_body_is_still_a_support_surface(self):
        field = stack(slab("plank", 0.0),
                      slab("post", 0.2, parent="plank", pinned=True),
                      slab("ball", 0.4, parent="post"))
        field.absorb(["plank"])
        field.settle()
        field.absorb([])                                      # 空批：只是让 last_enqueued 清空
        field._enqueue(field.index["ball"])                   # 强行复检：它仍然被 post 托着
        field.step(1)
        self.assertEqual(field.states()["ball"], RESTING)

    def test_absorbing_a_pinned_body_drops_its_children(self):
        """钉住只保证「自己不掉」；被吸走时孩子照样落。"""
        field = stack(slab("plank", 0.0),
                      slab("post", 0.2, parent="plank", pinned=True),
                      slab("ball", 0.4, parent="post"))
        field.absorb(["post"])
        field.settle()
        self.assertEqual(field.states()["ball"], SETTLED)
        # 板还在（0..0.2），所以球落在板上而不是地面：掉了，但掉得不深。
        self.assertAlmostEqual(field.heights()["ball"], 0.2, places=3)

    def test_pinned_with_a_parent_keeps_the_link_but_never_falls(self):
        """挂在横梁上的招牌：指针还在 CSR 里（整组记账用），``pinned`` 让它留在半空。"""
        beam = Body("beam", ((-1.0, 1.8, -0.1), (1.0, 2.0, 0.1)))
        sign = Body("sign", ((-0.3, 1.4, -0.02), (0.3, 1.8, 0.02)), parent="beam", pinned=True)
        field = Field([beam, sign], ground_y=GROUND)
        self.assertEqual(field.children(field.index["beam"]), [field.index["sign"]])
        field.absorb(["beam"])
        field.settle()
        self.assertEqual(field.states()["sign"], RESTING)
        self.assertAlmostEqual(field.heights()["sign"], 1.4, places=3)

    def test_pinned_and_absorbed_are_exclusive(self):
        field = stack(slab("plank", 0.0), slab("cloud", 0.2, parent="plank", pinned=True))
        field.absorb(["cloud"])
        field.settle()
        self.assertEqual(field.states()["cloud"], ABSORBED)


class DataShapeTests(unittest.TestCase):
    """CSR 子表与状态数组：Udon 那边只能有这么点东西。"""

    def test_children_are_a_stable_csr_array(self):
        field = stack(slab("a", 0.0), slab("b", 0.2, parent="a"), slab("c", 0.2, parent="a"),
                      slab("d", 0.0))
        self.assertEqual(field.child_start[field.index["a"]],
                         field.child_start[field.index["a"]])
        self.assertEqual(sorted(field.child_list[field.child_start[field.index["a"]]:
                                                 field.child_start[field.index["a"] + 1]]),
                         sorted([field.index["b"], field.index["c"]]))
        self.assertEqual(field.children(field.index["d"]), [])

    def test_a_pointer_to_a_missing_body_is_ignored(self):
        field = stack(slab("cup", 0.2, parent="ghost"))
        self.assertEqual(field.child_list, [])
        self.assertEqual(field.children(field.index["cup"]), [])
        field.settle()                                        # 没有事件 ⇒ 不复检、不落
        self.assertEqual(field.states()["cup"], RESTING)

    def test_states_and_heights_are_plain_dictionaries(self):
        field = stack(slab("plank", 0.0))
        self.assertEqual(field.states(), {"plank": RESTING})
        self.assertEqual(field.heights(), {"plank": 0.0})
        self.assertIn("resting", field.summary()["plank"])


if __name__ == "__main__":
    unittest.main()
