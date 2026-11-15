"""The level authoring gate: the five negative fixtures, plus the CLI exit codes.

The design (``LEVEL_AUTHORING`` section 11, P0 acceptance) requires that five
deliberately broken files each go **red**: sunk, floating, out of bounds, over
budget, and a value total that does not match ``level.expected``. A validator
that only accepts good data is not a gate, so every one of them is a test here.
"""

import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Docs.tests.levelbuild.test_placement_core import EXAMPLE, valid_doc  # noqa: E402

CLI = ROOT / "Tools/levelbuild/levelbuild.py"


def errors_of(doc):
    return core.validate(doc).errors


class NegativeFixtureTests(unittest.TestCase):
    def assert_red(self, fragment, mutate):
        doc = valid_doc()
        mutate(doc)
        errors = errors_of(doc)
        self.assertTrue(errors, "the fixture was accepted but must be rejected")
        self.assertTrue(any(fragment in error for error in errors),
                        "expected %r in %r" % (fragment, errors))
        return errors

    # 1 -- a prop sunk into the floor
    def test_sunk_prop_is_red(self):
        def mutate(doc):
            doc["props"][0]["position"][1] = 0.005
        self.assert_red("sunk", mutate)

    # 2 -- a prop hanging in the air with no support
    def test_floating_prop_is_red(self):
        def mutate(doc):
            doc["props"][1]["position"][1] = 0.5
        self.assert_red("floats", mutate)

    # 3 -- a prop outside level.bounds
    def test_out_of_bounds_prop_is_red(self):
        def mutate(doc):
            doc["props"][1]["position"][0] = 5.5
        self.assert_red("leaves level.bounds", mutate)

    # 4 -- one box structure too many
    def test_budget_overrun_is_red(self):
        def mutate(doc):
            for index in range(8):
                doc["structures"].append({
                    "id": "crate_%02d" % index, "role": "box", "shape": "box",
                    "center": [3.0, 0.2, -3.0 + index * 0.5], "size": [0.2, 0.4, 0.2],
                    "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
                })
        errors = self.assert_red("boxes=", mutate)
        self.assertTrue(any("over the budget" in error for error in errors))

    # 5 -- the value total disagrees with the level's expected block
    def test_expected_total_mismatch_is_red(self):
        def mutate(doc):
            doc["level"]["expected"] = {"propCount": 3, "valueUnits": 412}
        self.assert_red("does not match level.expected.valueUnits", mutate)

    def test_missing_prop_count_is_red(self):
        def mutate(doc):
            doc["props"].pop()
        self.assert_red("does not match level.expected.propCount", mutate)

    # extra guard rails that the first level depends on
    def test_unknown_def_is_red(self):
        def mutate(doc):
            doc["props"][0]["def"] = "not_a_real_prop"
        self.assert_red("has no manifest", mutate)

    def test_duplicate_item_id_is_red(self):
        def mutate(doc):
            doc["props"][1]["itemId"] = doc["props"][0]["itemId"]
        self.assert_red("duplicated", mutate)

    def test_overlapping_props_are_red(self):
        def mutate(doc):
            doc["props"][1]["position"] = [0.61, 0.0387, 0.6]
        self.assert_red("overlap", mutate)

    def test_ramp_and_box_with_one_id_is_red(self):
        def mutate(doc):
            doc["structures"].append({
                "id": "picnic_table", "role": "ramp", "shape": "box",
                "center": [1.5, 0.4, -1.5], "size": [1.2, 0.05, 0.8],
                "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box"})
        self.assert_red("both a box and a ramp", mutate)

    def test_missing_ground_is_red(self):
        def mutate(doc):
            doc["structures"] = [s for s in doc["structures"] if s["role"] != "ground"]
        self.assert_red("exactly one role=ground", mutate)

    def test_stale_absorb_gate_is_red(self):
        def mutate(doc):
            doc["props"][0]["absorbDiameterReq"] = 9.9
        self.assert_red("is stale", mutate)

    def test_good_file_is_green(self):
        self.assertEqual(errors_of(valid_doc()), [])

    def test_shipped_example_is_green(self):
        doc = core.load_doc(EXAMPLE)
        report = core.validate(doc, path=EXAMPLE)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])


class CliTests(unittest.TestCase):
    def run_cli(self, *arguments):
        return subprocess.run([sys.executable, str(CLI)] + list(arguments),
                              cwd=str(ROOT), capture_output=True, text=True)

    def test_exit_code_is_zero_for_the_example(self):
        result = self.run_cli("check", str(EXAMPLE))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("quatOrder=xyzw", result.stdout)
        self.assertIn("PASS", result.stdout)

    def test_exit_code_is_one_for_a_bad_file(self):
        doc = valid_doc()
        doc["props"][0]["position"][1] = 0.005
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "placement.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            result = self.run_cli("check", str(path))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("FAIL", result.stdout)

    def test_exit_code_is_two_for_a_missing_file(self):
        result = self.run_cli("check", "no/such/placement.json")
        self.assertEqual(result.returncode, 2)

    def test_format_rewrites_and_check_reports(self):
        doc = valid_doc()
        doc["props"].reverse()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "placement.json"
            path.write_text(json.dumps(doc, indent=4), encoding="utf-8")
            self.assertEqual(self.run_cli("format", str(path), "--check").returncode, 1)
            self.assertEqual(self.run_cli("format", str(path)).returncode, 0)
            self.assertEqual(self.run_cli("format", str(path), "--check").returncode, 0)

    def test_seat_fixes_a_half_buried_prop(self):
        """The measured bug: an upright prop lowered onto a tilted plank is buried."""
        doc = valid_doc()
        doc["structures"].append({
            "id": "plank", "role": "ramp", "shape": "box",
            "center": [-0.5, 0.2, 0.0], "size": [1.1, 0.04, 0.42],
            "rotationQuat": list(core.quat_axis_angle((0.0, 0.0, 1.0), 0.34906585)),
            "collision": "box", "absorbable": False, "support": True})
        doc["props"].append({
            "itemId": "prop_apple_001", "def": "apple", "zone": "ramp",
            "position": [0.0, 0.2, 0.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
            "aabb": list(core.load_manifests()["apple"]["aabb"]),
            "support": "plank", "valueUnits": 1})
        doc["level"]["expected"] = {"propCount": 4, "valueUnits": 7}
        self.assertTrue(errors_of(doc))

        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "placement.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            dry = self.run_cli("seat", str(path))
            self.assertEqual(dry.returncode, 0, dry.stdout + dry.stderr)
            self.assertIn("dry run", dry.stdout)
            self.assertTrue(errors_of(core.load_doc(path)))  # nothing written yet

            written = self.run_cli("seat", str(path), "--write")
            self.assertEqual(written.returncode, 0, written.stdout + written.stderr)
            self.assertEqual(errors_of(core.load_doc(path)), [])

            again = self.run_cli("seat", str(path), "--write")
            self.assertIn("0 prop(s) re-seated", again.stdout)


if __name__ == "__main__":
    unittest.main()
