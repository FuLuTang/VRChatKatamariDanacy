"""The supply plan: the ladder, the climb, and the level-1 dead end it found.

These tests guard three different things:

* the arithmetic -- every number the analysis reports is the shared one in
  :mod:`Tools.levelbuild.placement_core` (the gate, the growth, the ball volume), so a
  change to the original's rule must show up here and not silently produce a second
  answer;
* the *finding* -- ``story_01_bench_size`` as committed cannot reach its first star
  (the ball stalls at ~0.51 m because nothing between 0.51 and 0.55 m is edible).  That
  is the whole reason this tool exists, so it is a regression test: if a future content
  pass fixes the level, this test is expected to fail and be rewritten, not deleted;
* the *patch* -- ``proposed_additions.json`` merged into a copy must pass the level
  gate with zero errors, and must carry the level's food chain to the declared cap.
"""

import copy
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import supply_plan as plan  # noqa: E402

LEVEL_DIR = ROOT / "Docs/Design/Levels/story_01_bench_size"
PLACEMENT = LEVEL_DIR / "placement.json"
ADDITIONS = LEVEL_DIR / "proposed_additions.json"


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def tiny_doc(sizes, start=0.30, ball_max=2.20):
    """A minimal placement document with one prop per entry in ``sizes``.

    An entry is either a number (a cube of that edge) or an ``(x, y, z)`` triple.
    """
    props = []
    for index, size in enumerate(sizes):
        aabb = list(size) if isinstance(size, (list, tuple)) else [size, size, size]
        props.append({
            "itemId": "prop_%02d" % index,
            "def": "toy_ball",
            "position": [-12.5 + 0.5 * index, aabb[1] / 2.0, -12.5],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0],
            "aabb": aabb,
        })
    return {
        "schema": core.SCHEMA,
        "level": {
            "id": "tiny",
            "convention": core.CONVENTION,
            "units": "m",
            "ballDiameterStart": start,
            "ballDiameterMax": ball_max,
            "groundY": 0.0,
            "bounds": [[-19.4, -19.4], [-6.0, -6.0]],
            "budget": {"planes": 8, "boxes": 8, "ramps": 4, "propSupports": 6,
                       "blocking": 6},
            "expected": {"propCount": len(props), "growthMm3": 0},
        },
        "defs": [{"id": "toy_ball", "kind": "toy"}],
        "props": props,
        "structures": [{"id": "ground", "kind": "lawn", "role": "ground", "shape": "plane",
                        "center": [-12.7, 0.0, -12.7], "size": [13.4, 0.04, 13.4],
                        "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "none",
                        "absorbable": False, "support": True}],
    }


class LadderTests(unittest.TestCase):
    def test_rungs_run_start_to_stop_without_duplicates(self):
        rungs = plan.rung_diameters(0.2997, 1.60, 0.05)
        self.assertEqual(rungs[0], 0.2997)
        self.assertEqual(rungs[-1], 1.60)          # snapped, not appended 0.3 mm later
        self.assertEqual(len(rungs), len(set(rungs)))
        for earlier, later in zip(rungs, rungs[1:]):
            self.assertGreater(later, earlier)

    def test_one_rung_row_per_rung_and_new_is_the_delta(self):
        doc = tiny_doc([0.10, 0.12, 0.16, 0.20, 0.30])
        items = plan.build_items(doc)
        rows = plan.ladder(items, 0.30, 0.60, 0.10)
        self.assertEqual([row["diameter_m"] for row in rows], [0.30, 0.40, 0.50, 0.60])
        self.assertEqual(rows[0]["new"], rows[0]["cum"])       # the start pool
        for previous, row in zip(rows, rows[1:]):
            self.assertEqual(row["new"], row["cum"] - previous["cum"])
            self.assertGreaterEqual(row["new"], 0)

    def test_cum_matches_the_gate_at_the_rung(self):
        items = plan.build_items(load(PLACEMENT))
        rows = plan.ladder(items, 0.2997, 1.60, 0.05)
        for row in rows:
            expected = sum(1 for item in items
                           if item["d_req_mm"] <= core.floor_mm(row["diameter_m"]))
            self.assertEqual(row["cum"], expected)

    def test_need_matches_the_ball_volume_difference(self):
        rows = plan.ladder([], 0.30, 0.50, 0.10)
        for row, earlier in zip(rows[1:], rows[:-1]):
            volume_now = math.pi / 6.0 * row["diameter_m"] ** 3
            volume_before = math.pi / 6.0 * earlier["diameter_m"] ** 3
            self.assertAlmostEqual(row["need_m3"], volume_now - volume_before, places=12)


class SharedRuleTests(unittest.TestCase):
    """The analysis must read the gate and the growth, not re-derive them."""

    def test_item_numbers_are_the_core_ones(self):
        doc = load(PLACEMENT)
        catalog = core.def_catalog(doc)
        by_id = {item["item_id"]: item for item in plan.build_items(doc)}
        for prop in doc["props"]:
            item = by_id[prop["itemId"]]
            size = tuple(prop["aabb"])
            volume_k = core.effective_volume_k(prop, catalog)
            penalty = core.effective_volume_penalty(prop, catalog)
            self.assertEqual(item["d_req_mm"], core.absorb_diameter_req_mm(size, volume_k))
            self.assertEqual(item["growth_mm3"],
                             core.item_growth_mm3(size, volume_k, penalty))
            self.assertAlmostEqual(item["credit_m3"], penalty * volume_k * math.prod(size),
                                   places=12)

    def test_every_absorb_respects_the_gate(self):
        items = plan.build_items(load(PLACEMENT))
        trace = plan.climb(items, 0.2997, 2.20, "greedy")
        self.assertTrue(trace["steps"])
        for step, before in zip(trace["steps"],
                                [0.2997] + [s["after_m"] for s in trace["steps"][:-1]]):
            self.assertGreaterEqual(core.floor_mm(before), step["d_req_mm"])
            self.assertGreater(step["after_m"], before)

    def test_reach_is_the_volume_sum(self):
        doc = tiny_doc([0.10, 0.12])
        level = plan.Level(doc)
        grown = level.diameter_after_eating_all()
        expected_volume = (core.ball_volume_mm3(level.start_m)
                           + sum(item["growth_mm3"] for item in level.items))
        self.assertAlmostEqual(grown, core.diameter_for_volume_mm3(expected_volume), places=12)


class ClimbTests(unittest.TestCase):
    def test_a_prop_that_is_too_big_is_never_eaten(self):
        doc = tiny_doc([0.10, 0.60])       # the 0.60 cube needs a ~0.87 m ball
        level = plan.Level(doc)
        trace = plan.climb(level.items, 0.30, 2.20)
        eaten = {step["item_id"] for step in trace["steps"]}
        self.assertIn("prop_00", eaten)
        self.assertNotIn("prop_01", eaten)
        self.assertTrue(trace["stalled"])

    def test_greedy_takes_the_largest_credit(self):
        # 0.04 and 0.12 cubes are both edible at 0.30 m; greedy takes the big one.
        level = plan.Level(tiny_doc([0.04, 0.12]))
        greedy = plan.climb(level.items, 0.30, 2.20, "greedy")
        self.assertEqual(greedy["steps"][0]["item_id"], "prop_01")

    def test_owner_never_starts_on_a_seed_while_a_cup_is_edible(self):
        # 0.02 seed and 0.10 cup are both edible at 0.30 m; a 0.35*D player takes the cup.
        level = plan.Level(tiny_doc([0.02, 0.10]))
        trace = plan.climb(level.items, 0.30, 2.20, "owner")
        self.assertEqual(trace["steps"][0]["item_id"], "prop_01")

    def test_owner_prefers_the_size_class_item_over_a_long_thin_one(self):
        # A 0.28 m long 3 cm twig and a 0.10 m cup: 0.35*0.30 = 0.105 m, so the cup is the
        # pick.  This is the "eat what is in your size class" rule, not "eat the biggest".
        level = plan.Level(tiny_doc([(0.28, 0.03, 0.03), 0.10]))
        trace = plan.climb(level.items, 0.30, 2.20, "owner")
        self.assertEqual(trace["steps"][0]["item_id"], "prop_01")

    def test_level_one_stalls_below_its_first_star(self):
        """The measured defect: the committed level cannot reach 1.25 m.

        Eating everything edible from 0.2997 m stops at ~0.514 m because the next prop
        available (the 1.40 m plank) needs a 0.547 m ball.  A content pass that closes
        the ladder is expected to break this test -- rewrite it then.
        """
        level = plan.Level(load(PLACEMENT))
        for policy in ("greedy", "owner"):
            trace = plan.climb(level.items, level.start_m, 1.25, policy)
            self.assertTrue(trace["stalled"], policy)
            self.assertLess(trace["reached_m"], 0.52, policy)
            self.assertGreater(trace["next_req_mm"], core.floor_mm(trace["reached_m"]))

    def test_upper_bound_is_above_the_reachable_one(self):
        """Eating all 130 props would give 1.398 m -- still short of the 3-star 1.60 m."""
        level = plan.Level(load(PLACEMENT))
        self.assertAlmostEqual(level.diameter_after_eating_all(), 1.398, places=3)
        self.assertLess(level.diameter_after_eating_all(), 1.60)

    def test_level_one_supply_ratios_match_the_audit(self):
        level = plan.Level(load(PLACEMENT))
        self.assertAlmostEqual(
            plan.supply_ratio(level.items, level.start_m, 1.60), 0.32, places=2)
        self.assertAlmostEqual(
            plan.supply_ratio(level.items, level.start_m, 1.25), 0.46, places=2)


class TargetCurveTests(unittest.TestCase):
    def test_the_hud_anchors_give_the_published_rates(self):
        rates, overall = plan.original_step_rates()
        self.assertEqual(len(rates), 2)                     # three anchors -> two blocks
        self.assertAlmostEqual(rates[0][2], 0.0137, places=4)
        self.assertAlmostEqual(rates[1][2], 0.0154, places=4)
        self.assertAlmostEqual(overall, 0.0147, places=4)

    def test_curve_and_its_inverse_agree_within_one_absorb(self):
        # items_for_diameter rounds up, so a round trip may come back one item high on an
        # exact boundary; it must never drift further.
        for count in (0, 1, 12, 60):
            diameter = plan.diameter_for_items(0.2997, count, 0.0147)
            self.assertLessEqual(abs(plan.items_for_diameter(0.2997, diameter, 0.0147)
                                     - count), 1)

    def test_star_schedule_is_monotone_and_in_the_time_budget_ballpark(self):
        schedule = plan.star_schedule(0.2997, plan.DEFAULT_STARS, 0.0147)
        diameters = [entry["diameter_m"] for entry in schedule]
        counts = [entry["items"] for entry in schedule]
        self.assertEqual(diameters, sorted(diameters))
        self.assertEqual(counts, sorted(counts))
        self.assertGreater(counts[0], 80)                   # 1.25 m at 1.47 %/item


class MergeTests(unittest.TestCase):
    def test_merge_adds_props_and_repins_the_totals(self):
        base = load(PLACEMENT)
        patch = {"props": [dict(base["props"][0], itemId="extra_01")]}
        merged = plan.merge_additions(base, patch)
        self.assertEqual(len(merged["props"]), len(base["props"]) + 1)
        self.assertEqual(merged["level"]["expected"]["propCount"], len(merged["props"]))
        self.assertEqual(merged["level"]["expected"]["growthMm3"],
                         core.growth_mm3_total(merged))
        # the input document is untouched
        self.assertEqual(len(base["props"]), 130)

    def test_duplicate_item_id_is_refused(self):
        base = load(PLACEMENT)
        patch = {"props": [dict(base["props"][0])]}
        with self.assertRaises(ValueError):
            plan.merge_additions(base, patch)

    def test_merged_expected_matches_the_derived_totals(self):
        merged = plan.merge_additions(load(PLACEMENT), load(ADDITIONS))
        self.assertEqual(merged["level"]["expected"]["propCount"], len(merged["props"]))
        self.assertEqual(merged["level"]["expected"]["growthMm3"],
                         core.growth_mm3_total(merged))


class ProposedAdditionsTests(unittest.TestCase):
    """The patch is a deliverable: it has to be valid content, not just a list."""

    @classmethod
    def setUpClass(cls):
        cls.patch = load(ADDITIONS)
        cls.merged = plan.merge_additions(load(PLACEMENT), cls.patch)

    def test_patch_shape(self):
        self.assertEqual(self.patch["schema"], "katamari.additions/0")
        self.assertEqual(self.patch["level"], "story_01_bench_size")
        self.assertTrue(self.patch["props"])
        for prop in self.patch["props"]:
            for key in ("itemId", "def", "position", "rotationQuat", "aabb"):
                self.assertIn(key, prop)

    def test_every_def_has_a_model_manifest(self):
        manifests = core.load_manifests()
        for prop in self.patch["props"]:
            self.assertIn(prop["def"], manifests,
                          "%s: def %r has no manifest" % (prop["itemId"], prop["def"]))

    def test_declared_derived_columns_are_not_stale(self):
        for prop in self.patch["props"]:
            size = tuple(prop["aabb"])
            if "absorbDiameterReq" in prop:
                self.assertEqual(prop["absorbDiameterReq"],
                                 core.absorb_diameter_req(size))
            if "growthMm3" in prop:
                self.assertEqual(prop["growthMm3"],
                                 core.growth_mm3({"aabb": prop["aabb"]}))
            if "absorbable" in prop:
                self.assertEqual(prop["absorbable"],
                                 core.is_absorbable(size, 2.20))

    def test_merged_copy_passes_the_level_gate(self):
        report = core.validate(self.merged, path=None)
        self.assertEqual(report.errors, [])

    def test_additions_are_unique_and_do_not_collide_with_the_level(self):
        ids = [prop["itemId"] for prop in self.patch["props"]]
        self.assertEqual(len(ids), len(set(ids)))
        base_ids = {prop["itemId"] for prop in load(PLACEMENT)["props"]}
        self.assertEqual(base_ids & set(ids), set())

    def test_merged_level_reaches_the_cap_and_holds_the_supply_floor(self):
        level = plan.Level(self.merged)
        greedy = plan.climb(level.items, level.start_m, level.max_m, "greedy")
        self.assertFalse(greedy["stalled"])
        self.assertGreaterEqual(greedy["reached_m"], 2.20)
        for star in plan.DEFAULT_STARS:
            self.assertGreaterEqual(plan.supply_ratio(level.items, level.start_m, star),
                                    3.0, "star %.2f" % star)
        self.assertAlmostEqual(level.diameter_after_eating_all(), 3.104, places=3)

    def test_merged_level_has_no_new_early_trap(self):
        """Nothing new may sit in the first ring while needing a much bigger ball.

        A box collider the ball cannot absorb is a physical obstacle; the O6-O8 runs
        observed exactly that with watermelon / apple / gate_basket / toy_ball / stool.
        The patch keeps every new prop's gate at or below the ball that reaches it, so no
        added prop may need more than 2x the start diameter within 3 m of the spawn.
        """
        start = (-12.5, -12.5)
        for prop in self.patch["props"]:
            size = tuple(prop["aabb"])
            radius = math.hypot(prop["position"][0] - start[0],
                                prop["position"][2] - start[1])
            if radius < 3.0:
                self.assertLessEqual(core.absorb_diameter_req_mm(size), 600,
                                     "%s at r=%.2f needs %d mm"
                                     % (prop["itemId"], radius,
                                        core.absorb_diameter_req_mm(size)))


class AnalyseTests(unittest.TestCase):
    def test_report_flags_the_dead_end_and_the_supply_shortfall(self):
        result = plan.analyse(PLACEMENT, stars=(1.25, 1.45, 1.60), stop_m=2.20)
        joined = "\n".join(result["flags"])
        self.assertIn("DEAD END", joined)
        self.assertIn("SUPPLY RATIO", joined)
        self.assertLess(result["greedy"]["reached_m"], 0.52)
        self.assertEqual(result["props"], 130)
        self.assertEqual(result["ladder"][0]["cum"], 96)

    def test_report_on_the_patch_overlay_is_clean_of_dead_end(self):
        result = plan.analyse(PLACEMENT, additions=ADDITIONS, stars=(1.25, 1.45, 1.60),
                              stop_m=2.20)
        joined = "\n".join(result["flags"])
        self.assertNotIn("DEAD END", joined)
        self.assertNotIn("SUPPLY RATIO", joined)

    def test_json_is_serialisable(self):
        result = plan.analyse(PLACEMENT, stars=(1.25,), stop_m=0.60)
        json.dumps(result)

    def test_csv_has_one_line_per_rung(self):
        result = plan.analyse(PLACEMENT, stars=(1.25,), stop_m=0.60, step_m=0.05)
        lines = plan.format_csv(result).splitlines()
        self.assertEqual(len(lines), len(result["ladder"]) + 1)


class CommandLineTests(unittest.TestCase):
    def test_merged_out_refuses_to_overwrite_the_placement(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "placement.json"
            target.write_text(PLACEMENT.read_text(encoding="utf-8"), encoding="utf-8")
            with contextlib.redirect_stderr(io.StringIO()):
                code = plan.main([str(target), "--merged-out", str(target)])
            self.assertEqual(code, 2)

    def test_merged_out_writes_a_file_that_passes_the_gate(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as folder:
            out = Path(folder) / "merged.json"
            with contextlib.redirect_stdout(io.StringIO()):
                code = plan.main([str(PLACEMENT), "--additions", str(ADDITIONS),
                                  "--merged-out", str(out), "--quiet"])
            self.assertEqual(code, 0)
            merged = load(out)
            self.assertEqual(core.validate(merged, path=None).errors, [])


if __name__ == "__main__":
    unittest.main()
