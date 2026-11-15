"""Tests for the level-level/def-level fields added for external asset packs.

Covered here (all on a synthetic level - no reference-level data or names):

* ``check --manifests DIR``: a level can validate against its own manifest tree, and the
  default (repo manifests) is unchanged when the option is absent;
* ``defs[].model``: the canonical round trip keeps it byte-identically, malformed paths are
  red, a def with a model does not need a repo asset, and ``prop_fbx`` prefers it;
* ``level.expected.overlapBudget`` + ``props[].groupId``: the historic 5 %-of-smaller rule is
  untouched when the budget is absent, and only the pair *count* is gated when it is there;
* ``level.spawnClearanceM`` per level (0.15 m).

Run from the repository root: ``python3 -m unittest discover -s Docs/tests``.
"""

import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import editor_core  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Docs.tests.levelbuild.test_level_editor import FakeScene  # noqa: E402
from Docs.tests.levelbuild.test_placement_core import valid_doc  # noqa: E402

CLI = ROOT / "Tools/levelbuild/levelbuild.py"
REPO_EXPORTS = ROOT / "Docs/Art/OriginalModels/props/exports"

#: A Unity-relative model path from a (fictional) third-party pack. Not a repo prop.
EXTERNAL_MODEL = "Assets/ThirdParty/Models/synthetic_widget/synthetic_widget.fbx"


def run_cli(*arguments):
    return subprocess.run([sys.executable, str(CLI)] + [str(a) for a in arguments],
                          capture_output=True, text=True)


def manifest_document(def_id, aabb):
    """A repo-shaped manifest whose Unity AABB is ``aabb`` (written in Blender axes)."""
    x, y, z = aabb
    return {
        "id": def_id,
        "dimensions_m": {"x": x, "y": z, "z": y, "longest": max(aabb)},
        "bounds_m": [[-x / 2.0, -y / 2.0, -z / 2.0], [x / 2.0, y / 2.0, z / 2.0]],
        "category": "synthetic",
    }


def write_manifest(directory, def_id, aabb):
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (def_id + ".manifest.json")
    path.write_text(json.dumps(manifest_document(def_id, aabb), indent=2) + "\n",
                    encoding="utf-8")
    return path


def doc_with_external_def():
    """``valid_doc`` with its apple renamed to a def that has no repo manifest."""
    doc = valid_doc()
    doc["defs"][0]["id"] = "synthetic_widget"
    doc["props"][0]["def"] = "synthetic_widget"
    return doc


class ManifestsOptionTests(unittest.TestCase):
    """R1: ``check --manifests DIR`` (default behaviour must not move)."""

    def test_own_manifest_tree_turns_a_def_into_a_known_def(self):
        doc = doc_with_external_def()
        with tempfile.TemporaryDirectory(prefix="levelbuild-manifests-") as tmp_name:
            tmp = Path(tmp_name)
            path = tmp / "placement.json"
            core.write_doc(doc, path)

            without = run_cli("check", path)
            self.assertEqual(without.returncode, 1, without.stdout + without.stderr)
            self.assertIn("has no manifest", without.stdout)

            manifests = tmp / "manifests"
            # The temp tree is the *only* manifest source under --manifests, so it needs an
            # entry for every def the document uses.
            aabb_by_def = {}
            for prop in doc["props"]:
                aabb_by_def.setdefault(prop["def"], prop["aabb"])
            for def_id, aabb in aabb_by_def.items():
                write_manifest(manifests, def_id, aabb)
            with_option = run_cli("check", path, "--manifests", manifests)
            self.assertEqual(with_option.returncode, 0, with_option.stdout + with_option.stderr)
            self.assertIn("PASS", with_option.stdout)

    def test_repo_manifests_are_the_default(self):
        example = ROOT / "Tools/levelbuild/examples/placement.json"
        default = run_cli("check", example)
        explicit = run_cli("check", example, "--manifests", REPO_EXPORTS)
        self.assertEqual(default.returncode, explicit.returncode)
        self.assertEqual(default.stdout, explicit.stdout)

    def test_missing_manifests_directory_is_a_usage_error(self):
        example = ROOT / "Tools/levelbuild/examples/placement.json"
        result = run_cli("check", example, "--manifests", ROOT / "no/such/dir")
        self.assertEqual(result.returncode, 2)
        self.assertIn("not a directory", result.stderr)

    def test_level_option_resolves_the_level_directory(self):
        bad = run_cli("check", "--level", "../escape")
        self.assertEqual(bad.returncode, 2)
        self.assertIn("bad level id", bad.stderr)

    def test_probe_level_ids_may_start_with_an_underscore(self):
        self.assertTrue(core.is_valid_level_id("_external_probe"))
        self.assertFalse(core.is_valid_level_id("_../escape"))
        self.assertFalse(core.is_valid_level_id("/absolute"))


class DefModelTests(unittest.TestCase):
    """R2: a def can name its own .fbx instead of being defined by its id."""

    def test_model_survives_the_canonical_round_trip_byte_identically(self):
        doc = valid_doc()
        doc["defs"][0]["model"] = EXTERNAL_MODEL
        first = core.dump_doc(doc)
        second = core.dump_doc(json.loads(first))
        self.assertEqual(first, second)
        reloaded = json.loads(first)
        entry = next(item for item in reloaded["defs"] if item["id"] == "apple")
        self.assertEqual(entry["model"], EXTERNAL_MODEL)

    def test_malformed_model_paths_are_red(self):
        for bad in (123, "", "Props/apple.fbx", "Assets/apple.glb", "Assets/../apple.fbx",
                    "Assets/Models/../../apple.fbx"):
            doc = valid_doc()
            doc["defs"][0]["model"] = bad
            errors = core.validate(doc).errors
            self.assertTrue(any("[def_model]" in error for error in errors),
                            "model %r must be rejected, got %r" % (bad, errors))

    def test_a_def_with_a_model_does_not_need_a_repo_asset(self):
        doc = doc_with_external_def()
        doc["defs"][0]["model"] = EXTERNAL_MODEL
        report = core.validate(doc)
        self.assertEqual([], report.errors)
        # No manifest exists for it yet; the aabb is then unverified, which is a warning.
        self.assertTrue(any("no manifest was found" in warning for warning in report.warnings),
                        report.warnings)

    def test_model_def_ignores_the_repo_manifest_of_a_colliding_id(self):
        # `apple` is also a repo prop: with a model, our manifest describes a *different*
        # object, so its aabb must not be imposed on the level's prop.
        doc = valid_doc()
        doc["defs"][0]["model"] = EXTERNAL_MODEL
        doc["props"][0]["aabb"] = [0.5, 0.5, 0.5]
        doc["props"][0]["position"] = [0.6, 0.25, 0.6]
        errors = core.validate(doc).errors
        self.assertFalse(any("disagrees with manifest" in error for error in errors), errors)

    def test_an_explicit_manifest_wins_over_the_one_beside_the_model(self):
        doc = doc_with_external_def()
        doc["defs"][0]["model"] = EXTERNAL_MODEL
        explicit = {"aabb": (1.0, 1.0, 1.0), "category": "synthetic", "longest_m": 1.0,
                    "manifest": {}, "source": None}
        merged = core.manifests_with_models(doc, {"synthetic_widget": explicit})
        self.assertIs(merged["synthetic_widget"], explicit)

    def test_prop_fbx_prefers_the_declared_model(self):
        self.assertEqual(core.prop_fbx("synthetic_widget", model=EXTERNAL_MODEL),
                         core.UNITY_ROOT / EXTERNAL_MODEL)
        self.assertEqual(core.prop_fbx("apple"),
                         core.UNITY_ROOT / "Assets/VRC_Katamari/Art/Props/apple/apple.fbx")

    def test_manifest_beside_the_model_is_used_when_the_caller_has_none(self):
        doc = doc_with_external_def()
        doc["defs"][0]["model"] = EXTERNAL_MODEL
        aabb = doc["props"][0]["aabb"]
        expected = (core.UNITY_ROOT / "Assets/ThirdParty/Models/exports/"
                    "synthetic_widget.manifest.json")
        self.assertEqual(core.model_manifest_path(EXTERNAL_MODEL), expected)

        with tempfile.TemporaryDirectory(prefix="levelbuild-model-manifests-") as tmp_name:
            old_root = core.UNITY_ROOT
            core.UNITY_ROOT = Path(tmp_name)
            try:
                target = core.model_manifest_path(EXTERNAL_MODEL)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(json.dumps(manifest_document("synthetic_widget", aabb)) + "\n",
                                  encoding="utf-8")
                loaded = core.load_model_manifest(EXTERNAL_MODEL)
                self.assertIsNotNone(loaded)
                self.assertEqual(loaded["aabb"], tuple(aabb))
                merged = core.manifests_with_models(doc, {})
                self.assertIn("synthetic_widget", merged)
                self.assertEqual(merged["synthetic_widget"]["aabb"], tuple(aabb))
            finally:
                core.UNITY_ROOT = old_root


class OverlapBudgetTests(unittest.TestCase):
    """R3: the budget gates the pair count; absent = the historic rule."""

    def overlapping(self):
        doc = valid_doc()
        doc["props"][1]["position"] = list(doc["props"][0]["position"])
        return doc

    def test_default_rule_is_unchanged(self):
        report = core.validate(self.overlapping())
        self.assertEqual(1, sum("overlap" in error for error in report.errors), report.errors)
        self.assertFalse(any("overlapBudget" in warning for warning in report.warnings))

    def test_budget_makes_the_count_the_gate(self):
        doc = self.overlapping()
        doc["level"]["expected"]["overlapBudget"] = 5
        report = core.validate(doc)
        self.assertEqual([], report.errors)
        self.assertTrue(any("1 prop/prop overlap pair(s)" in warning
                            for warning in report.warnings), report.warnings)

    def test_over_budget_is_red(self):
        doc = self.overlapping()
        doc["level"]["expected"]["overlapBudget"] = 0
        report = core.validate(doc)
        self.assertTrue(any("exceed level.expected.overlapBudget" in error
                            for error in report.errors), report.errors)

    def test_bad_budget_is_red(self):
        for bad in ("5", -1, 1.5, True):
            doc = self.overlapping()
            doc["level"]["expected"]["overlapBudget"] = bad
            errors = core.validate(doc).errors
            self.assertTrue(any("overlapBudget must be a whole number" in error
                                for error in errors), (bad, errors))

    def test_one_group_id_is_exempt(self):
        doc = self.overlapping()
        doc["props"][0]["groupId"] = "biscuit_pile"
        doc["props"][1]["groupId"] = "biscuit_pile"
        report = core.validate(doc)
        self.assertFalse(any("overlap" in error for error in report.errors), report.errors)

    def test_different_group_ids_still_overlap(self):
        doc = self.overlapping()
        doc["props"][0]["groupId"] = "biscuit_pile"
        doc["props"][1]["groupId"] = "other_pile"
        report = core.validate(doc)
        self.assertTrue(any("overlap" in error for error in report.errors), report.errors)

    def test_group_id_must_be_a_non_empty_string(self):
        doc = self.overlapping()
        doc["props"][0]["groupId"] = ""
        errors = core.validate(doc).errors
        self.assertTrue(any("groupId must be a non-empty string" in error
                            for error in errors), errors)


class CanonicalRoundTripTests(unittest.TestCase):
    def test_level_and_prop_fields_survive_the_rewrite(self):
        doc = valid_doc()
        doc["level"]["spawnClearanceM"] = 0.15
        doc["level"]["expected"]["overlapBudget"] = 0
        doc["props"][0]["groupId"] = "biscuit_pile"
        text = core.dump_doc(doc)
        back = json.loads(text)
        self.assertEqual(back["level"]["spawnClearanceM"], 0.15)
        self.assertEqual(back["level"]["expected"]["overlapBudget"], 0)
        self.assertEqual(back["props"][0]["groupId"], "biscuit_pile")
        self.assertEqual(core.dump_doc(back), text)


class EditorFieldTests(unittest.TestCase):
    """The resident editor's ops must carry the new fields through to the exported file."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="levelbuild-editor-fields-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.path = self.tmp / "placement.json"
        core.write_doc(valid_doc(), self.path)
        self.editor = editor_core.Editor(FakeScene())
        opened = self.editor.open(None, self.path)
        self.assertTrue(opened["gate"]["ok"], opened["gate"]["errors"])

    def test_add_def_model_set_group_and_expected_budget(self):
        for request in (
            {"op": "add_def", "args": {"id": "synthetic_widget", "valueUnits": 1,
                                       "model": EXTERNAL_MODEL}},
            {"op": "set", "args": {"id": "prop_apple_000", "groupId": "biscuit_pile"}},
            {"op": "expected", "args": {"overlapBudget": 3}},
        ):
            response = self.editor.handle(request)
            self.assertTrue(response["ok"], (request, response["error"], response["details"]))

        exported = json.loads(self.path.read_text(encoding="utf-8"))
        entry = next(item for item in exported["defs"] if item["id"] == "synthetic_widget")
        self.assertEqual(entry["model"], EXTERNAL_MODEL)
        prop = next(item for item in exported["props"]
                    if item["itemId"] == "prop_apple_000")
        self.assertEqual(prop["groupId"], "biscuit_pile")
        self.assertEqual(exported["level"]["expected"]["overlapBudget"], 3)


class MalformedDocumentTests(unittest.TestCase):
    def test_non_object_document_still_gets_the_shape_error(self):
        self.assertEqual(["the document must be a JSON object"], core.validate([]).errors)


class SpawnClearanceTests(unittest.TestCase):
    """R4: ``level.spawnClearanceM`` is per level; 0.15 m is enough for a small room."""

    def test_per_level_clearance_is_honoured(self):
        doc = valid_doc()
        # 0.71 m from ballStart (-2.5, 0.15, -2.5): inside the 1.0 m default, outside 0.15.
        doc["props"][0]["position"] = [-2.0, 0.0387, -2.0]
        default = core.validate(doc)
        self.assertTrue(any("within 1.00 m of ballStart" in error
                            for error in default.errors), default.errors)

        doc["level"]["spawnClearanceM"] = 0.15
        tight = core.validate(doc)
        self.assertFalse(any("of ballStart" in error for error in tight.errors), tight.errors)
        self.assertEqual([], tight.errors)


if __name__ == "__main__":
    unittest.main()
