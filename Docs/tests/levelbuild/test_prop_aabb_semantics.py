"""``aabb`` is the un-rotated model size; ``rotationQuat`` turns it exactly once.

This pins the data end of the layout-to-collider chain (``PICNIC_CORNER_LAYOUT.md``
section 7):

  ``picnic_corner_layout.json`` (world footprint + ``yaw``)
    -> ``placement.json`` ``props[].aabb`` from the model manifest (un-rotated,
       Unity axes) + ``props[].rotationQuat``
    -> ``placement_core.world_aabb()`` here, and ``Renderer.bounds`` in Unity after
       ``PlacementImporter`` poses the instance
    -> ``PickupFieldHost.propBounds`` -> the axis-aligned blocking/support box.

The first level has one turned prop whose footprint moves, ``struct_lounge_chair_01``
(yaw 90); ``serving_tray_01`` is also turned, but only by a 180 deg flip that leaves
its box unchanged.  The chair's stored ``aabb`` is 1.60 x 0.35 x 0.60 (the model's own
axes) and its world footprint is 0.60 x 0.35 x 1.60.  Feeding the already-turned size
back through the same quat (a double rotation) would give 0.80 x 0.35 x 0.30, so each test below states one half of the rule
numerically.
"""

import json
import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402

STORY_LEVEL = ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"
MANIFESTS = core.load_manifests()

LOUNGE_CHAIR = "struct_lounge_chair_01"
FOLDING_TABLE = "struct_folding_table_01"

#: The stored quat is rounded to 6 decimals, so a quarter turn is 0.707107 rather
#: than sqrt(0.5)/2: a half extent that should be 0.3 comes out 0.3000007.  The
#: tolerance is tight enough to catch a swapped (1.6-long) footprint.
TOLERANCE_M = 1e-4


def load_level():
    return json.loads(STORY_LEVEL.read_text(encoding="utf-8"))


def find_prop(level, item_id):
    for prop in level["props"]:
        if prop["itemId"] == item_id:
            return prop
    raise AssertionError("%s is not in %s" % (item_id, STORY_LEVEL.name))


def world_box(prop):
    return core.world_aabb(tuple(prop["position"]), tuple(prop["aabb"]),
                           tuple(prop["rotationQuat"]))


def box_size(box):
    low, high = box
    return (high[0] - low[0], high[1] - low[1], high[2] - low[2])


def is_turned(quat_xyzw):
    return max(abs(quat_xyzw[0]), abs(quat_xyzw[1]), abs(quat_xyzw[2])) > 1e-6


class StoredAabbTests(unittest.TestCase):
    def test_every_prop_stores_the_un_rotated_manifest_size(self):
        # "aabb: 世界轴对齐尺寸（未旋转的模型 AABB），只作校验与预估"
        # (LEVEL_AUTHORING 4.2).  If a prop's aabb carried its rotation, the level
        # would disagree with its own model and world_aabb() would rotate it twice.
        level = load_level()
        checked = 0
        for prop in level["props"]:
            def_id = prop["def"]
            self.assertIn(def_id, MANIFESTS,
                          "%s has no model manifest, so its aabb has no truth to check "
                          "against" % prop["itemId"])
            expected = MANIFESTS[def_id]["aabb"]
            worst = max(abs(prop["aabb"][i] - expected[i]) for i in range(3))
            self.assertLessEqual(
                worst, core.MANIFEST_AABB_TOLERANCE_M,
                "%s def %r stores aabb %r but the model is %r; aabb is the un-rotated "
                "model size, never the turned world footprint"
                % (prop["itemId"], def_id, prop["aabb"], expected))
            checked += 1
        self.assertEqual(checked, len(level["props"]))

    def test_the_turned_prop_stores_its_long_axis_on_x(self):
        # The lounge chair is 1.60 m long in its own frame; the level's yaw puts
        # that length on world Z.  Storing [0.6, 0.35, 1.6] here would be the
        # "already rotated" reading that K7 flagged.
        chair = find_prop(load_level(), LOUNGE_CHAIR)
        self.assertEqual(chair["aabb"], [1.6, 0.35, 0.6])


class WorldFootprintTests(unittest.TestCase):
    def test_lounge_chair_footprint_matches_the_p2_chain(self):
        # E7: the chair's world footprint is x in [-12.6, -12.0], z in [-10.4, -8.8],
        # top at 0.35 - the surface the P2 chain "bed top -> chair -> table" stands on.
        chair = find_prop(load_level(), LOUNGE_CHAIR)
        low, high = world_box(chair)
        self.assertAlmostEqual(low[0], -12.6, delta=TOLERANCE_M)
        self.assertAlmostEqual(high[0], -12.0, delta=TOLERANCE_M)
        self.assertAlmostEqual(low[1], 0.0, delta=TOLERANCE_M)
        self.assertAlmostEqual(high[1], 0.35, delta=TOLERANCE_M)
        self.assertAlmostEqual(low[2], -10.4, delta=TOLERANCE_M)
        self.assertAlmostEqual(high[2], -8.8, delta=TOLERANCE_M)

    def test_a_quarter_turn_swaps_the_footprint_without_widening_it(self):
        size = (1.6, 0.35, 0.6)
        quarter = core.quat_axis_angle((0.0, 1.0, 0.0), math.radians(90.0))
        half = core.world_half_extents(size, quarter)
        self.assertAlmostEqual(half[0], 0.3, delta=TOLERANCE_M)
        self.assertAlmostEqual(half[1], 0.175, delta=TOLERANCE_M)
        self.assertAlmostEqual(half[2], 0.8, delta=TOLERANCE_M)

    def test_rotating_the_turned_size_again_is_the_double_rotation(self):
        # world_half_extents(world size, same quat) is what the runtime would build
        # if propBounds were the un-turned aabb - or if the AABB were turned and the
        # box were turned after it.  It is a 0.8 x 0.35 x 0.3 box: 60 % wider than
        # the chair is thin, on the wrong axis.
        quarter = core.quat_axis_angle((0.0, 1.0, 0.0), math.radians(90.0))
        twice = core.world_half_extents((0.6, 0.35, 1.6), quarter)
        self.assertAlmostEqual(twice[0], 0.8, delta=TOLERANCE_M)
        self.assertAlmostEqual(twice[1], 0.175, delta=TOLERANCE_M)
        self.assertAlmostEqual(twice[2], 0.3, delta=TOLERANCE_M)

    def test_the_gate_is_the_same_whichever_footprint_is_read(self):
        # A quarter turn keeps the volume, so the gate's D_req is consistent with both
        # the stored aabb and the world footprint.  With the original's k=0.70 the
        # requirement is 1.650 m (the k-less 1.858 m folded by 0.70^(1/3) and floored
        # to whole millimetres).  A non-90
        # yaw would make the world footprint fatter (45 deg: +41 %), which is why
        # the level keeps every other prop at yaw 0.
        chair = find_prop(load_level(), LOUNGE_CHAIR)
        world_size = box_size(world_box(chair))
        self.assertAlmostEqual(core.absorb_diameter_req(tuple(chair["aabb"])),
                               core.absorb_diameter_req(world_size), delta=1e-5)
        self.assertAlmostEqual(core.absorb_diameter_req(tuple(chair["aabb"])),
                               1.650, delta=1e-3)

    def test_the_p2_chain_props_touch_only_on_one_plane(self):
        # E7: chair x in [-12.6, -12.0] and table x in [-13.6, -12.6] meet exactly
        # at x = -12.6; the rounded quat leaves a sub-millimetre sliver, not an
        # overlap ("重叠面积 0").
        level = load_level()
        chair_low, chair_high = world_box(find_prop(level, LOUNGE_CHAIR))
        table_low, table_high = world_box(find_prop(level, FOLDING_TABLE))
        self.assertAlmostEqual(chair_low[0], table_high[0], delta=TOLERANCE_M)
        self.assertAlmostEqual(table_low[0], -13.6, delta=TOLERANCE_M)
        self.assertAlmostEqual(table_high[1], 0.7, delta=TOLERANCE_M)
        overlap = core.aabb_intersection((chair_low, chair_high), (table_low, table_high))
        # 1.4e-7 m3 of sliver from the 6-decimal quat: four orders below the chair's
        # 0.336 m3, i.e. the boxes touch rather than interpenetrate.
        self.assertLess(overlap, 1e-5)


class RotationInventoryTests(unittest.TestCase):
    def test_only_the_deliberate_props_are_turned(self):
        # PICNIC_CORNER_LAYOUT section 4.3/7: every prop is at yaw 0 except the
        # lounge chair at 90.  A new yaw is a data decision with a 41 % wider box at
        # 45 deg, so it must be made deliberately - update this list and the section
        # 7 note together.  The serving tray on the bench is set upside down (a 180
        # deg flip about x), which turns the box without moving its footprint.
        level = load_level()
        turned = [prop["itemId"] for prop in level["props"] if is_turned(prop["rotationQuat"])]
        self.assertEqual(turned, ["serving_tray_01", LOUNGE_CHAIR],
                         "the first level is authored with yaw 0 everywhere except the "
                         "lounge chair; a new yaw changes its world footprint, the "
                         "blocking box and the section 7 note")
        tray = find_prop(level, "serving_tray_01")
        self.assertAlmostEqual(tray["rotationQuat"][0], 1.0, delta=1e-6)
        self.assertEqual(tray["rotationQuat"][1:], [0.0, 0.0, 0.0])
        self.assertAlmostEqual(core.absorb_diameter_req(tuple(tray["aabb"])),
                               core.absorb_diameter_req(box_size(world_box(tray))),
                               delta=1e-5)
        world_size = box_size(world_box(tray))
        for axis, stored in enumerate(tray["aabb"]):
            self.assertAlmostEqual(world_size[axis], stored, delta=TOLERANCE_M)


if __name__ == "__main__":
    unittest.main()
