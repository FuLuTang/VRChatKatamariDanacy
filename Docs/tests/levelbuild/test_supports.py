"""Support derivation: geometry in, one parent per prop out.

The rule is the one calibrated on the retail dump (``STACKS_IN_ORIGINAL.md`` section 2):
a prop rests on the prop whose top surface is the highest one below it, with the
footprints overlapping and the vertical gap inside ``[-0.30, +0.50] * min height``.  These
tests pin the parts that would fail *silently*:

* the window scales with the smaller item and uses **world** (rotated) heights - a prop
  lying on its side is judged by the height it actually has, not by its model AABB;
* touching footprints count, side-by-side does not;
* the answer does not depend on the order of ``props[]``;
* ``apply`` writes only links that are missing, never a null, and a second run is a no-op;
* the validator agrees with the derivation (same window function), so a derived document
  cannot be red;
* on real data: the hand-authored ``stackedOn`` links in ``story_01_bench_size`` that are
  geometrically sound are reproduced, and the two that are not are reported, not invented.
"""

import copy
import json
import random
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Docs.tests.levelbuild.test_placement_core import EXAMPLE, MANIFESTS  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import supports  # noqa: E402

IDENTITY = [0.0, 0.0, 0.0, 1.0]
#: +90 degrees about X: a box that is 1 m tall in its model becomes 1 m long on the ground.
QUAT_X90 = [0.7071067811865476, 0.0, 0.0, 0.7071067811865476]


def prop(item_id, position, aabb, quat=IDENTITY, **extra):
    entry = {"itemId": item_id, "def": "box", "position": list(position),
             "aabb": list(aabb), "rotationQuat": list(quat)}
    entry.update(extra)
    return entry


def doc(*props):
    return {"schema": core.SCHEMA, "level": {"id": "supports_test", "ballDiameterMax": 2.0},
            "props": list(props)}


def box(item_id, bottom, size):
    """Shorthand: a box whose bottom sits at ``bottom``."""
    size = list(size)
    centre = [0.0, bottom + size[1] / 2.0, 0.0]
    return prop(item_id, centre, size)


class GeometryTests(unittest.TestCase):
    def test_a_pair_that_touches_is_linked(self):
        links = supports.derive_supports(doc(box("plate", 0.0, [1.0, 0.2, 1.0]),
                                             box("cup", 0.2, [0.1, 0.1, 0.1])))
        self.assertEqual(links, {"cup": "plate"})

    def test_the_window_scales_with_the_smaller_item(self):
        # Both 0.2 m tall => min height 0.2 => window [-0.06, +0.10].
        for gap, linked in ((0.08, True), (0.12, False), (-0.04, True), (-0.09, False)):
            with self.subTest(gap=gap):
                links = supports.derive_supports(
                    doc(box("plate", 0.0, [1.0, 0.2, 1.0]),
                        box("cup", 0.2 + gap, [0.1, 0.2, 0.1])))
                self.assertEqual("cup" in links, linked, links)
        # A 2 mm leaf on a 1 m crate has a 0.6 mm lower bound, not 6 cm.
        links = supports.derive_supports(
            doc(box("crate", 0.0, [1.0, 1.0, 1.0]), box("leaf", 0.98, [0.1, 0.002, 0.1])))
        self.assertNotIn("leaf", links)

    def test_touching_footprints_count_and_side_by_side_does_not(self):
        touching = supports.derive_supports(
            doc(prop("plate", [0.0, 0.1, 0.0], [1.0, 0.2, 1.0]),
                prop("cup", [0.55, 0.25, 0.0], [0.1, 0.1, 0.1])))   # edges meet at x = 0.5
        self.assertEqual(touching, {"cup": "plate"})
        apart = supports.derive_supports(
            doc(prop("plate", [0.0, 0.1, 0.0], [1.0, 0.2, 1.0]),
                prop("cup", [0.56, 0.25, 0.0], [0.1, 0.1, 0.1])))
        self.assertEqual(apart, {})

    def test_rotation_decides_the_measured_height(self):
        plate = prop("plate", [0.0, 0.1, 0.0], [1.0, 0.2, 1.0])
        # Model box 0.2 x 1.0 x 0.2; rotated 90 deg about X its world height is 0.2, so it
        # stands on the plate at centre y = 0.3.  The same box unrotated hangs through it.
        lying = prop("plank", [0.0, 0.3, 0.0], [0.2, 1.0, 0.2], QUAT_X90)
        standing = prop("plank", [0.0, 0.3, 0.0], [0.2, 1.0, 0.2])
        self.assertAlmostEqual(supports.prop_box(lying)[1][1], 0.4, places=6)
        self.assertEqual(supports.derive_supports(doc(plate, lying)), {"plank": "plate"})
        self.assertEqual(supports.derive_supports(doc(plate, standing)), {})

    def test_the_highest_top_surface_below_wins(self):
        links = supports.derive_supports(doc(box("plate", 0.0, [1.0, 0.2, 1.0]),
                                             box("middle", 0.2, [0.5, 0.1, 0.5]),
                                             box("top", 0.3, [0.1, 0.1, 0.1])))
        self.assertEqual(links, {"middle": "plate", "top": "middle"})

    def test_ground_and_structures_are_not_parents(self):
        self.assertEqual(supports.derive_supports(doc(box("cup", 0.0, [0.1, 0.1, 0.1]))), {})
        on_a_shelf = box("cup", 0.5, [0.1, 0.1, 0.1])
        on_a_shelf["support"] = "shelf"
        self.assertEqual(supports.derive_supports(doc(on_a_shelf)), {})

    def test_the_order_of_props_does_not_matter(self):
        props = [box("plate", 0.0, [1.0, 0.2, 1.0]), box("middle", 0.2, [0.5, 0.1, 0.5]),
                 box("top", 0.3, [0.1, 0.1, 0.1]), box("side", 0.0, [0.2, 0.2, 0.2])]
        expected = supports.derive_supports(doc(*props))
        for seed in range(5):
            shuffled = copy.deepcopy(props)
            random.Random(seed).shuffle(shuffled)
            self.assertEqual(supports.derive_supports(doc(*shuffled)), expected)

    def test_an_unmeasurable_prop_is_never_linked(self):
        broken = box("cup", 0.2, [0.1, 0.1, 0.1])
        broken["aabb"] = None
        self.assertEqual(supports.derive_supports(doc(box("plate", 0.0, [1.0, 0.2, 1.0]), broken)), {})

    def test_cycle_break_leaves_a_forest(self):
        links = supports._break_cycles({"a": "b", "b": "a", "c": "a"})
        self.assertEqual(sorted(links), ["b", "c"])          # the lowest id of the ring lost
        self.assertEqual(supports._break_cycles(dict(links)), links)


class ProposalTests(unittest.TestCase):
    def setUp(self):
        # A plate with a cup and a bowl on it, the bowl clear of the cup.
        self.document = doc(box("plate", 0.0, [1.0, 0.2, 1.0]),
                            box("cup", 0.2, [0.1, 0.1, 0.1]))
        bowl = box("bowl", 0.2, [0.2, 0.1, 0.2])
        bowl["position"] = [0.35, 0.25, 0.0]
        self.document["props"].append(bowl)

    def test_propose_separates_new_agreed_and_orphaned(self):
        declared = copy.deepcopy(self.document)
        declared["props"][1]["stackedOn"] = "plate"                # cup: agrees
        declared["props"][2]["stackedOn"] = "plate"                # bowl: agrees
        diff = supports.propose(declared)
        self.assertEqual(diff["counts"]["new"], 0)
        self.assertEqual([entry["itemId"] for entry in diff["agreed"]], ["bowl", "cup"])

        plain = supports.propose(self.document)                    # nothing declared
        self.assertEqual([entry["itemId"] for entry in plain["new"]], ["bowl", "cup"])
        self.assertEqual(plain["counts"]["agreed"], 0)
        self.assertEqual(plain["counts"]["orphaned"], 0)

        # Declared on something the bowl does not touch at all: reported, never rewritten.
        orphan = copy.deepcopy(self.document)
        orphan["props"][2]["position"][0] = 3.0
        orphan["props"][2]["stackedOn"] = "cup"
        diff = supports.propose(orphan)
        self.assertEqual([entry["itemId"] for entry in diff["orphaned"]], ["bowl"])
        self.assertEqual(diff["counts"]["new"], 1)

    def test_apply_writes_only_missing_links_and_never_a_null(self):
        declared = copy.deepcopy(self.document)
        declared["props"][1]["stackedOn"] = "plate"
        written = supports.apply_supports(declared)
        self.assertEqual(written["written"], ["bowl"])
        self.assertEqual(declared["props"][1]["stackedOn"], "plate")
        self.assertNotIn("parent", declared["props"][1])
        self.assertEqual(declared["props"][2]["parent"], "plate")
        self.assertIs(declared["props"][2]["parentDerived"], True)
        self.assertEqual(supports.count_derived(declared), 1)

    def test_apply_is_idempotent(self):
        working = copy.deepcopy(self.document)
        once = supports.apply_supports(working)
        self.assertEqual(sorted(once["written"]), ["bowl", "cup"])
        text = core.dump_doc(working)
        self.assertEqual(text.count('"parent": "plate"'), 2)
        self.assertNotIn('"parent": null', text)
        self.assertNotIn("null", text)
        twice = supports.apply_supports(working)
        self.assertEqual(twice["written"], [])
        self.assertEqual(core.dump_doc(working), text)

    def test_a_declared_parent_does_not_lose_to_a_conflicting_one(self):
        declared = copy.deepcopy(self.document)
        declared["props"][1]["parent"] = "bowl"
        declared["props"][1]["parentDerived"] = True
        diff = supports.propose(declared)
        self.assertEqual([entry["itemId"] for entry in diff["conflict"]], ["cup"])


class ValidatorTests(unittest.TestCase):
    """The codes the gate raises for a bad pointer, and silence for a good one."""

    def report(self, document):
        report = core.Report()
        boxes = {}
        for entry in document["props"]:
            world = supports.prop_box(entry)
            if world is not None:
                boxes[entry["itemId"]] = world
        core._check_parents(document, report, boxes)
        return report

    def test_a_derived_link_in_contact_is_silent(self):
        cup = box("cup", 0.2, [0.1, 0.1, 0.1])
        cup.update(parent="plate", parentDerived=True)
        report = self.report(doc(box("plate", 0.0, [1.0, 0.2, 1.0]), cup))
        self.assertEqual(report.errors, [])

    def test_self_and_missing_parents(self):
        document = doc(box("plate", 0.0, [1.0, 0.2, 1.0]),
                       dict(box("cup", 0.2, [0.1, 0.1, 0.1]), parent="cup"),
                       dict(box("bowl", 0.2, [0.1, 0.1, 0.1]), parent="ghost"))
        errors = self.report(document).errors
        self.assertTrue(any("[parent_self]" in line for line in errors), errors)
        self.assertTrue(any("[parent_missing]" in line for line in errors), errors)

    def test_a_cycle_is_reported_once(self):
        document = doc(dict(box("a", 0.0, [1.0, 0.2, 1.0]), parent="b"),
                       dict(box("b", 0.0, [1.0, 0.2, 1.0]), parent="a"),
                       dict(box("c", 0.0, [1.0, 0.2, 1.0]), parent="a"))
        errors = [line for line in self.report(document).errors if "[parent_cycle]" in line]
        self.assertEqual(len(errors), 1, errors)
        self.assertIn("a -> b -> a", errors[0])

    def test_a_derived_link_out_of_the_window_is_reported(self):
        document = doc(box("plate", 0.0, [1.0, 0.2, 1.0]),
                       dict(box("cup", 0.9, [0.1, 0.1, 0.1]),
                            parent="plate", parentDerived=True))
        errors = self.report(document).errors
        self.assertTrue(any("[parent_seat]" in line for line in errors), errors)

    def test_an_authored_attachment_is_not_held_to_the_window(self):
        document = doc(box("mast", 0.0, [0.1, 2.0, 0.1]),
                       dict(box("flag", 1.9, [0.4, 0.2, 0.02]), parent="mast"))
        self.assertEqual(self.report(document).errors, [])

    def test_parent_and_stacked_on_must_agree(self):
        document = doc(box("plate", 0.0, [1.0, 0.2, 1.0]), box("other", 0.0, [1.0, 0.2, 1.0]),
                       dict(box("cup", 0.2, [0.1, 0.1, 0.1]),
                            parent="plate", stackedOn="other", parentDerived=True))
        errors = self.report(document).errors
        self.assertTrue(any("[parent_conflict]" in line for line in errors), errors)

    def test_parent_derived_without_a_parent_is_reported(self):
        document = doc(dict(box("cup", 0.0, [0.1, 0.1, 0.1]), parentDerived=True))
        errors = self.report(document).errors
        self.assertTrue(any("[parent_derived_orphan]" in line for line in errors), errors)

    def test_the_code_shows_up_in_a_full_validate_run(self):
        document = core.load_doc(EXAMPLE)
        entry = next(prop for prop in document["props"] if prop["itemId"] == "prop_acorn_000")
        entry["parent"] = "nonexistent_thing"
        report = core.validate(document, manifests=MANIFESTS)
        self.assertTrue(any("[parent_missing]" in line for line in report.errors), report.errors)

    def test_a_derived_parent_satisfies_the_floating_check(self):
        """The gate must accept what derivation writes, or `apply` cannot green a level."""
        document = core.load_doc(EXAMPLE)
        acorn = next(prop for prop in document["props"] if prop["itemId"] == "prop_acorn_000")
        acorn["position"] = [-1.70, 0.485, -1.10]          # on the plate, clear of the cookie
        self.assertTrue(any("floats" in line or "sits" in line
                            for line in core.validate(document, manifests=MANIFESTS).errors),
                        "the moved acorn should be floating before derivation")
        supports.apply_supports(document)
        self.assertEqual(acorn["parent"], "prop_plate_000")
        report = core.validate(document, manifests=MANIFESTS)
        self.assertEqual(report.errors, [])


class BenchSizeTests(unittest.TestCase):
    """``story_01_bench_size``: 130 props, 8 hand-authored ``stackedOn`` links."""

    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"
        cls.document = core.load_doc(cls.path)

    def test_the_sounds_declarations_are_reproduced_and_the_others_are_reported(self):
        diff = supports.propose(self.document)
        self.assertEqual(diff["counts"]["props"], 130)
        self.assertEqual(diff["counts"]["declared"], 8)
        self.assertEqual(sorted(entry["itemId"] for entry in diff["agreed"]),
                         ["bowl_02", "croissant_01", "cup_02", "cup_03", "sandwich_01",
                          "soda_can_01"])
        # Both of these hang in the air beside the thing they claim to rest on: the tray is
        # 7.5 cm left of the bench, the plant 88 cm from the cooler.
        self.assertEqual(sorted(entry["itemId"] for entry in diff["orphaned"]),
                         ["serving_tray_01", "small_potted_plant_03"])
        self.assertEqual(diff["counts"]["new"], 0)
        self.assertEqual(diff["counts"]["conflict"], 0)

    def test_structures_are_never_parents(self):
        links = supports.derive_supports(self.document)
        planted = [prop for prop in self.document["props"]
                   if prop.get("support") == "flower_bed"]
        self.assertEqual(len(planted), 28)
        self.assertFalse([prop["itemId"] for prop in planted if prop["itemId"] in links])

    def test_the_stored_pointers_are_the_eight_hand_authored_ones(self):
        self.assertEqual(supports.count_derived(self.document), 0)
        declared = supports.parents_from_document(self.document)
        self.assertEqual(len(declared), 8)
        self.assertEqual(declared["bowl_02"], "struct_folding_table_01")
        self.assertEqual(core.validate(self.document).errors, [])

    def test_deriving_the_whole_level_is_stable(self):
        """No hand edit: the derived set is the 6 agreements, and apply is a no-op."""
        before = core.dump_doc(self.document)
        result = supports.apply_supports(self.document)
        self.assertEqual(result["written"], [])
        self.assertEqual(core.dump_doc(self.document), before)


REF_LEVEL_DIR = ROOT / "Docs/Design/Levels/_ref_cancer"
REF_STAGE = Path.home() / "KatamariRef/noclip_export/kse_out"


def ref_available():
    return REF_LEVEL_DIR.is_dir() and (REF_STAGE / "instances.json").is_file()


@unittest.skipUnless(ref_available(), "the _ref_cancer level or its stage extraction is absent")
class ReferenceLevelTests(unittest.TestCase):
    """What derivation does on a real imported level, against the extractor's own table.

    Two facts are pinned here, and neither is a success story:

    * the import **flattened** the level (every prop's bottom is on the floor), so the
      geometry has no stacks left to find;
    * the extractor's parent records are **not** support links - of 90, only 5 put the
      child more than 5 cm above the parent and 50 put it below.  ``parentProp`` means
      "moves with", so reproducing it with a support rule would be wrong.
    """

    @classmethod
    def setUpClass(cls):
        cls.document = core.load_doc(REF_LEVEL_DIR / "placement.json")
        cls.instances = json.loads((REF_STAGE / "instances.json").read_text(encoding="utf-8"))
        from Tools.levelbuild import ref_stage
        stage_dir, why = ref_stage.resolve_stage_dir(None)
        assert stage_dir is not None, why
        prepared = ref_stage.prepare(stage_dir, REF_LEVEL_DIR, cls.document)
        cls.truth = {item_id: entry["parent"]
                     for item_id, entry in prepared["transforms"]["byItemId"].items()
                     if entry.get("parent")}

    def test_every_prop_was_seated_on_the_floor(self):
        bottoms = [supports.prop_box(prop)[0][1] for prop in self.document["props"]]
        self.assertEqual(len(bottoms), 511)
        self.assertLess(max(abs(value) for value in bottoms), 0.01)

    def test_derivation_finds_nothing_because_nothing_is_stacked(self):
        self.assertEqual(supports.derive_supports(self.document), {})

    def test_the_extractor_pointers_are_not_support_links(self):
        links = [record for record in self.instances["instances"]
                 if record.get("parentTableIndex") is not None]
        by_index = {record["tableIndex"]: record for record in self.instances["instances"]}
        rises = [record["t"][1] - by_index[record["parentTableIndex"]]["t"][1]
                 for record in links if record.get("parentTableIndex") in by_index]
        self.assertEqual(len(links), 90)
        self.assertEqual(len(rises), 90)
        self.assertEqual(sum(1 for rise in rises if rise > 5.0), 5)      # child above, cm
        self.assertEqual(sum(1 for rise in rises if rise < -5.0), 50)    # child below
        self.assertEqual(sum(1 for rise in rises if abs(rise) <= 5.0), 35)

    def test_recall_against_the_extractor_is_zero_and_that_is_correct(self):
        derived = supports.derive_supports(self.document)
        reproduced = [item_id for item_id, parent in derived.items()
                      if self.truth.get(item_id) == parent]
        self.assertEqual(len(derived), 0)
        self.assertEqual(len(reproduced), 0)
        self.assertGreaterEqual(len(self.truth), 80)


if __name__ == "__main__":
    unittest.main()
