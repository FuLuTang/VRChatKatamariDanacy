"""The resident editor's state machine, without Blender.

What is tested here is what a wrong implementation would get wrong *silently*:

* an op changes the document exactly the way it says (and nothing else);
* a successful op exports canonical bytes, and an export of an untouched file is
  byte-identical to it (the file is the truth, so this is the "does not lose work"
  property);
* an op that makes the gate worse is refused and leaves both the document and the
  file alone - the failure mode this design exists to avoid;
* ``undo`` restores the document and the file, and the scene sync it triggers is the
  same diff run backwards;
* a ``batch`` is all-or-nothing with one export and one scene sync;
* export -> open -> export is lossless (the round trip the Unity importer and the
  one-shot scripts depend on).

The scene adapter is a fake: this file must run with plain CPython.
"""

import hashlib
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Docs.tests.levelbuild.test_placement_core import EXAMPLE, MANIFESTS  # noqa: E402
from Tools.levelbuild import editor_core  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402


class FakeScene:
    """Records the diffs it is asked to apply; writes tiny "PNGs"."""

    def __init__(self):
        self.resets = 0
        self.syncs = []
        self.renders = []
        self.fail_sync = False
        self.props = 0

    def reset(self):
        self.resets += 1
        self.props = 0

    def sync(self, doc, previous=None):
        if self.fail_sync:
            raise RuntimeError("fake scene refuses")
        self.syncs.append((doc, previous))
        self.props = len(doc.get("props") or [])
        counts = {kind: {"added": 0, "updated": 0, "removed": 0}
                  for kind in ("props", "structures", "cameras")}
        if previous is None:
            counts["props"]["added"] = self.props
            counts["structures"]["added"] = len(doc.get("structures") or [])
            counts["cameras"]["added"] = len(doc.get("cameras") or [])
        else:
            before = {prop.get("itemId") for prop in previous.get("props") or []}
            after = {prop.get("itemId") for prop in doc.get("props") or []}
            counts["props"]["added"] = len(after - before)
            counts["props"]["removed"] = len(before - after)
            counts["props"]["updated"] = len(after & before)
        return counts

    def render(self, spec, doc):
        self.renders.append(spec)
        path = Path(spec["path"])
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"\x89PNG\r\n\x1a\nfake")
        return {"path": str(path), "engine_id": "FAKE_" + str(spec.get("engine")),
                "seconds": 0.01, "res": spec.get("res")}

    def status(self):
        return {"blender": "fake", "objects": {"props": self.props}}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class EditorCase(unittest.TestCase):
    """A private copy of the 16-prop example level, opened in a fresh editor."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="levelbuild-editor-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.path = self.tmp / "placement.json"
        self.path.write_bytes(EXAMPLE.read_bytes())
        self.scene = FakeScene()
        self.editor = editor_core.Editor(self.scene, manifests=MANIFESTS)
        self.opened = self.editor.open(None, self.path)

    def handle(self, op, **args):
        return self.editor.handle({"op": op, "args": args})

    def ok(self, op, **args):
        response = self.handle(op, **args)
        self.assertTrue(response["ok"], "%s was refused: %s %s"
                        % (op, response["error"], response["details"]))
        return response["result"]

    def refused(self, op, **args):
        response = self.handle(op, **args)
        self.assertFalse(response["ok"], "%s was accepted but should be refused" % op)
        return response

    def prop(self, item_id):
        return editor_core._prop_by_id(self.editor.doc, item_id)


class OpenAndReadTests(EditorCase):
    def test_open_builds_the_scene_and_reports_a_green_gate(self):
        self.assertEqual(self.opened["counts"]["props"], 16)
        self.assertTrue(self.opened["gate"]["ok"])
        self.assertTrue(self.opened["canonical"])
        self.assertEqual(self.scene.resets, 1)
        self.assertIsNone(self.scene.syncs[0][1])

    def test_status_is_clean_and_in_sync_right_after_open(self):
        status = self.ok("status")
        self.assertFalse(status["dirty"])
        self.assertTrue(status["gate"]["ok"])
        self.assertEqual(status["counts"]["props"], 16)
        self.assertEqual(status["file"]["bytes"], len(self.path.read_bytes()))
        self.assertTrue(status["file"]["canonical"])

    def test_list_filters(self):
        table = self.ok("list", zone="table")
        self.assertTrue(table["props"])
        self.assertTrue(all(prop["zone"] == "table" for prop in table["props"]))
        self.assertLess(table["total"], self.ok("list")["total"])
        near = self.ok("list", near=[1.2, 2.1], radius=0.3)
        self.assertIn("prop_acorn_000", [prop["itemId"] for prop in near["props"]])
        self.assertEqual(self.ok("list", text="cookie")["total"], 1)
        self.assertEqual(self.ok("list", limit=3)["count"], 3)

    def test_get_prop_structure_and_camera(self):
        self.assertEqual(self.ok("get", id="prop_plate_000")["type"], "prop")
        self.assertEqual(self.ok("get", id="picnic_table")["type"], "structure")
        self.assertEqual(self.ok("get", id="top")["type"], "camera")
        self.assertEqual(self.ok("get", id="plan")["type"], "preset")
        self.assertIn("not a prop", self.refused("get", id="nope")["error"])


class MutationTests(EditorCase):
    def test_move_prop_writes_canonical_json_and_syncs_once(self):
        before_syncs = len(self.scene.syncs)
        result = self.ok("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertTrue(result["changed"])
        self.assertEqual(len(self.scene.syncs), before_syncs + 1)
        new_doc, previous = self.scene.syncs[-1]
        self.assertIs(new_doc, self.editor.doc)
        self.assertEqual(self.prop("prop_acorn_000")["position"][0], 1.3)
        self.assertEqual(previous, core.load_doc(EXAMPLE))
        self.assertEqual(self.path.read_text(encoding="utf-8"),
                         core.dump_doc(self.editor.doc, MANIFESTS))
        self.assertEqual(sha(self.path), hashlib.sha256(
            self.path.read_bytes()).hexdigest())          # canonical write, stable
        self.assertFalse(self.ok("status")["dirty"])

    def test_move_delta_is_relative(self):
        start = self.prop("prop_acorn_000")["position"][:]
        self.ok("move", id="prop_acorn_000", delta=[0.1, 0.0, -0.1])
        self.assertAlmostEqual(self.prop("prop_acorn_000")["position"][0], start[0] + 0.1)
        self.assertAlmostEqual(self.prop("prop_acorn_000")["position"][2], start[2] - 0.1)

    def test_set_fields_and_unknown_field_refused(self):
        self.ok("set", id="prop_acorn_000", zone="lawn")
        self.assertEqual(self.prop("prop_acorn_000")["zone"], "lawn")
        self.assertIn("unknown argument", self.refused("set", id="prop_acorn_000",
                                                       postion=[0, 0, 0])["error"])

    def test_changing_the_volume_k_is_refused_by_the_design_total(self):
        # Growth is derived from aabb/k/penalty (steps C+D), so volumeK -- not the retired
        # valueUnits -- is what moves the design total.
        response = self.refused("set", id="prop_acorn_000", volumeK=2.0)
        self.assertTrue(any("level.expected" in error for error in response["details"]))
        self.assertEqual(self.prop("prop_acorn_000").get("volumeK"), None)
        # The deprecated key is accepted from old callers but is dropped by the canonical
        # writer, so it cannot become a second growth currency.
        self.ok("set", id="prop_acorn_000", valueUnits=2)
        self.assertNotIn("valueUnits", self.prop("prop_acorn_000"))

    def test_rotate_yaw_sets_a_canonical_unit_quaternion(self):
        self.ok("rotate", id="prop_acorn_000", yaw=90)
        quat = self.prop("prop_acorn_000")["rotationQuat"]
        self.assertAlmostEqual(abs(quat[1]), 0.707107, places=5)
        self.assertAlmostEqual(abs(quat[3]), 0.707107, places=5)

    def test_add_prop_uses_the_manifest_aabb_and_seats_on_the_ground(self):
        result = self.ok("add_prop", **{"def": "apple", "position": [3.2, 0.0, 3.2],
                                        "zone": "lawn", "seat": True,
                                        "updateExpected": True})
        item_id = result["itemId"]
        prop = self.prop(item_id)
        self.assertEqual(prop["aabb"], list(MANIFESTS["apple"]["aabb"]))
        self.assertAlmostEqual(prop["position"][1],
                               round(MANIFESTS["apple"]["aabb"][1] / 2.0, 4), places=4)
        self.assertEqual(self.ok("check")["errorCount"], 0)

    def test_add_prop_needs_no_value_because_growth_is_derived(self):
        # No valueUnits / volumeK / volumePenalty argument: growth still comes out of
        # aabb x 0.70 x 0.8, so the op needs only the design-total acknowledgement.
        response = self.refused("add_prop", **{"def": "apple", "position": [3.2, 0.0, 3.2],
                                               "seat": True})
        self.assertIn("updateExpected", response["error"])
        result = self.ok("add_prop", **{"def": "apple", "position": [3.2, 0.0, 3.2],
                                        "seat": True, "updateExpected": True})
        prop = self.prop(result["itemId"])
        self.assertNotIn("valueUnits", prop)
        self.assertGreater(core.growth_mm3(prop), 0)

    def test_add_prop_point_out_the_design_total(self):
        response = self.refused("add_prop", **{"def": "apple", "position": [3.2, 0.0, 3.2],
                                               "seat": True})
        self.assertIn("updateExpected", response["error"])
        self.assertTrue(any("level.expected" in error for error in response["details"]))

    def test_add_prop_refuses_an_unknown_def(self):
        self.assertIn("manifest", self.refused("add_prop", **{"def": "unicorn"})["error"])

    def test_add_def_needs_no_value_and_the_prop_derives_growth(self):
        self.ok("add_def", id="apple", kind="fruit")
        result = self.ok("add_prop", **{"def": "apple", "position": [3.2, 0.0, 3.2],
                                        "seat": True, "updateExpected": True})
        prop = self.prop(result["itemId"])
        self.assertNotIn("valueUnits", prop)
        self.assertEqual(core.growth_mm3(prop), core.item_growth_mm3(prop["aabb"]))

    def test_remove_prop_and_structure(self):
        self.ok("remove", id="prop_acorn_000", updateExpected=True)
        self.assertIsNone(self.prop("prop_acorn_000"))
        self.assertEqual(self.ok("status")["counts"]["growthMm3"],
                         core.growth_mm3_total(self.editor.doc))
        self.ok("remove", id="crate")
        self.assertIsNone(editor_core._structure_by_id(self.editor.doc, "crate"))
        self.assertEqual(self.ok("list")["total"], 15)
        self.assertEqual(self.ok("check")["errorCount"], 0)

    def test_expected_op_recomputes_the_design_total(self):
        result = self.editor.handle({"ops": [
            {"op": "remove", "args": {"id": "prop_acorn_000"}},
            {"op": "expected", "args": {}},
        ]})
        self.assertTrue(result["ok"], result["error"])
        self.assertEqual(self.editor.doc["level"]["expected"]["propCount"], 15)
        self.assertEqual(self.editor.doc["level"]["expected"]["growthMm3"],
                         core.growth_mm3_total(self.editor.doc))
        self.assertEqual(self.ok("check")["errorCount"], 0)

    def test_add_structure_and_resize_it(self):
        self.ok("add_structure", role="box", center=[3.5, 0.25, -3.5],
                size=[0.3, 0.5, 0.3], support=True)
        self.assertIsNotNone(editor_core._structure_by_id(self.editor.doc, "structure_01"))
        self.ok("set", id="structure_01", size=[0.4, 0.5, 0.4])
        self.assertEqual(editor_core._structure_by_id(self.editor.doc, "structure_01")["size"][0],
                         0.4)
        self.assertEqual(self.ok("check")["errorCount"], 0)

    def test_seat_repairs_a_displaced_stacked_prop(self):
        # A prop 5 cm off its plate is a *warning* since the owner's 2026-11-16 ruling (an
        # authored pointer is never a geometric claim that must hold), so the file opens
        # green - and `seat` is still what repairs it.
        document = json.loads(self.path.read_text(encoding="utf-8"))
        cookie = next(prop for prop in document["props"]
                      if prop["itemId"] == "prop_cookie_000")
        cookie["position"][1] += 0.05
        self.path.write_text(core.dump_doc(document, MANIFESTS), encoding="utf-8")
        editor = editor_core.Editor(FakeScene(), manifests=MANIFESTS)
        gate = editor.open(None, self.path)["gate"]
        self.assertTrue(gate["ok"], gate["errors"])
        self.assertEqual(gate["errorCount"], 0)
        self.assertTrue(any("[parent_not_seated]" in warning for warning in gate["warnings"]),
                        gate["warnings"])

        result = editor.handle({"op": "seat", "args": {}})
        self.assertTrue(result["ok"], result["error"])
        self.assertEqual(result["result"]["moved"], ["prop_cookie_000"])
        self.assertEqual(editor.handle({"op": "check"})["result"]["errorCount"], 0)
        # The level is now seated, so a second seat is a no-op that writes nothing.
        before = sha(self.path)
        again = editor.handle({"op": "seat", "args": {}})
        self.assertEqual(again["result"]["moved"], [])
        self.assertEqual(sha(self.path), before)

    def test_noop_op_does_not_export_or_sync(self):
        before_syncs = len(self.scene.syncs)
        before = sha(self.path)
        result = self.ok("move", id="prop_acorn_000",
                         to=self.prop("prop_acorn_000")["position"])
        self.assertFalse(result["changed"])
        self.assertEqual(len(self.scene.syncs), before_syncs)
        self.assertEqual(sha(self.path), before)


class GateTests(EditorCase):
    def test_an_op_that_breaks_a_rule_is_refused_and_changes_nothing(self):
        before_doc = json.dumps(self.editor.doc, sort_keys=True)
        before_file = sha(self.path)
        before_syncs = len(self.scene.syncs)
        response = self.refused("move", id="prop_apple_000", to=[-2.7, -3.0, -1.2])
        self.assertTrue(response["details"], "the refusal must name the broken rule")
        self.assertEqual(json.dumps(self.editor.doc, sort_keys=True), before_doc)
        self.assertEqual(sha(self.path), before_file)
        self.assertEqual(len(self.scene.syncs), before_syncs)
        self.assertIn("nothing was written", response["error"])

    def test_a_red_file_stays_editable_but_may_not_get_worse(self):
        doc = json.loads(self.path.read_text(encoding="utf-8"))
        doc["level"]["expected"]["propCount"] = 999
        self.path.write_text(core.dump_doc(doc, MANIFESTS), encoding="utf-8")

        editor = editor_core.Editor(FakeScene(), manifests=MANIFESTS)
        opened = editor.open(None, self.path)
        self.assertFalse(opened["gate"]["ok"], "the fixture must open red")

        # Repair mode: a change that does not add an error is accepted and exported.
        moved = editor.handle({"op": "move",
                               "args": {"id": "prop_acorn_000", "delta": [0.1, 0, 0]}})
        self.assertTrue(moved["ok"], moved["error"])
        self.assertFalse(editor.state_summary()["dirty"])
        self.assertFalse(editor.handle({"op": "check"})["result"]["ok"])

        # A *new* error is still refused, even though the file is already red.
        worse = editor.handle({"op": "move",
                               "args": {"id": "prop_apple_000", "to": [-2.7, -3.0, -1.2]}})
        self.assertFalse(worse["ok"])

    def test_a_scene_failure_rolls_the_export_back(self):
        before = sha(self.path)
        self.scene.fail_sync = True
        response = self.refused("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertIn("rolled back", response["error"])
        self.assertEqual(sha(self.path), before)
        self.assertIsNotNone(self.prop("prop_acorn_000"))
        self.assertEqual(self.prop("prop_acorn_000")["position"][0], 1.2)

    def test_no_auto_export_leaves_the_file_alone_and_reports_dirty(self):
        scene = FakeScene()
        editor = editor_core.Editor(scene, manifests=MANIFESTS, auto_export=False)
        editor.open(None, self.path)
        before = sha(self.path)
        response = editor.handle({"op": "move",
                                  "args": {"id": "prop_acorn_000", "to": [1.3, 0.015, 2.1]}})
        self.assertTrue(response["ok"], response["error"])
        self.assertEqual(sha(self.path), before)
        self.assertTrue(editor.state_summary()["dirty"])
        self.assertTrue(editor.state_summary()["unexported"])
        self.assertIsNone(editor.handle({"op": "export"})["result"].get("to"))
        self.assertFalse(editor.state_summary()["dirty"])
        self.assertFalse(editor.state_summary()["unexported"])

    def test_reload_flushes_unexported_work_instead_of_dropping_it(self):
        scene = FakeScene()
        editor = editor_core.Editor(scene, manifests=MANIFESTS, auto_export=False)
        editor.open(None, self.path)
        self.assertTrue(editor.handle({"op": "move", "args": {
            "id": "prop_acorn_000", "to": [1.3, 0.015, 2.1]}})["ok"])
        self.assertEqual(json.loads(self.path.read_text(encoding="utf-8"))
                         ["props"][0]["position"], [1.2, 0.015, 2.1])
        response = editor.handle({"op": "reload"})
        self.assertTrue(response["ok"], response["error"])
        self.assertTrue(response["result"]["flushed"]["wrote"])
        # The pending move reached the file before the re-read, and survived it.
        self.assertEqual(json.loads(self.path.read_text(encoding="utf-8"))
                         ["props"][0]["position"], [1.3, 0.015, 2.1])
        self.assertFalse(editor.state_summary()["unexported"])


class VolumeKTests(EditorCase):
    """The per-item volume factor k (the original's `Volume = k * a*b*c`, default 0.70).

    The rule is one number with three consequences: it prices the gate (Req in whole mm),
    it is written only when authored (so an existing file stays byte-identical), and it is
    undoable like any other authored field.
    """

    def def_entry(self, def_id):
        return next((entry for entry in self.editor.doc.get("defs") or []
                     if entry.get("id") == def_id), None)

    def test_set_def_volume_k_reprices_the_gate_and_undo_restores_the_bytes(self):
        before = self.path.read_bytes()
        self.ok("add_def", id="apple", valueUnits=1, kind="fruit")
        # A def with no volumeK must not gain the key: the file keeps the 0.70 default.
        self.assertNotIn("volumeK", self.path.read_text(encoding="utf-8"))
        self.ok("set_def", id="apple", volumeK=32, updateExpected=True)
        apple = self.prop("prop_apple_000")
        self.assertEqual(apple["absorbDiameterReq"],
                         core.absorb_diameter_req(tuple(apple["aabb"]), 32.0))
        # k multiplies the volume, so Req grows by k^(1/3): the apple is food at the
        # 0.30 m start ball with k=0.70 and an obstacle once k=32.
        self.assertTrue(core.is_absorbable(tuple(apple["aabb"]), 0.30, 0.70))
        self.assertFalse(core.is_absorbable(tuple(apple["aabb"]), 0.30, 32.0))
        self.assertEqual(self.def_entry("apple")["volumeK"], 32)
        self.assertIn('"volumeK": 32', self.path.read_text(encoding="utf-8"))
        self.assertEqual(self.ok("check")["errorCount"], 0)
        self.ok("undo", steps=2)
        self.assertEqual(self.path.read_bytes(), before)

    def test_a_small_k_makes_a_large_prop_absorbable(self):
        self.ok("add_def", id="watermelon", valueUnits=1, kind="fruit")
        watermelon = self.prop("prop_watermelon_000")
        self.assertFalse(core.is_absorbable(tuple(watermelon["aabb"]), 0.30, 0.70))
        self.ok("set_def", id="watermelon", volumeK=0.05, updateExpected=True)
        body = tuple(self.prop("prop_watermelon_000")["aabb"])
        self.assertTrue(core.is_absorbable(body, 0.30, 0.05))
        self.assertEqual(self.prop("prop_watermelon_000")["absorbDiameterReq"],
                         core.absorb_diameter_req(body, 0.05))

    def test_clearing_volume_k_restores_the_default_without_writing_a_key(self):
        self.ok("add_def", id="apple", valueUnits=1, kind="fruit")
        with_def = self.path.read_bytes()
        self.ok("set_def", id="apple", volumeK=2.0, updateExpected=True)
        self.assertNotEqual(self.path.read_bytes(), with_def)
        self.ok("set_def", id="apple", volumeK=None, updateExpected=True)
        self.assertNotIn("volumeK", self.path.read_text(encoding="utf-8"))
        self.assertEqual(self.path.read_bytes(), with_def)

    def test_volume_k_range_is_enforced_by_the_op(self):
        self.ok("add_def", id="apple", valueUnits=1, kind="fruit")
        for bad in (0, -1, 33, 70, float("nan"), float("inf"), "0.7", True):
            response = self.refused("set_def", id="apple", volumeK=bad)
            self.assertIn("volumeK", response["error"], bad)
        self.assertIsNone(self.def_entry("apple").get("volumeK"))

    def test_set_def_refuses_an_undeclared_def(self):
        self.assertIn("not declared", self.refused("set_def", id="unicorn",
                                                   volumeK=0.7)["error"])

    def test_a_prop_may_override_its_def_factor(self):
        self.ok("add_def", id="apple", valueUnits=1, kind="fruit")
        self.ok("set", id="prop_apple_000", volumeK=1.0, updateExpected=True)
        self.assertEqual(self.prop("prop_apple_000")["volumeK"], 1.0)
        self.assertEqual(self.prop("prop_apple_000")["absorbDiameterReq"],
                         core.absorb_diameter_req(
                             tuple(self.prop("prop_apple_000")["aabb"]), 1.0))


class UndoTests(EditorCase):
    def test_undo_restores_document_and_file_byte_for_byte(self):
        before = self.path.read_bytes()
        self.ok("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertNotEqual(self.path.read_bytes(), before)
        result = self.ok("undo")
        self.assertEqual(result["undone"], ["move prop_acorn_000"])
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.scene.syncs[-1][0], core.load_doc(EXAMPLE))

    def test_undo_twice_and_undo_without_history(self):
        self.ok("move", id="prop_acorn_000", delta=[0.1, 0, 0])
        self.ok("move", id="prop_acorn_000", delta=[0.1, 0, 0])
        self.ok("undo", steps=2)
        self.assertEqual(self.prop("prop_acorn_000")["position"], [1.2, 0.015, 2.1])
        self.assertIn("nothing to undo", self.refused("undo")["error"])

    def test_undo_steps_beyond_the_stack_is_refused_without_side_effects(self):
        self.ok("move", id="prop_acorn_000", delta=[0.1, 0, 0])
        self.assertIn("only 1 step", self.refused("undo", steps=5)["error"])


class ExportTests(EditorCase):
    def test_export_is_byte_identical_when_nothing_changed(self):
        before = self.path.read_bytes()
        result = self.ok("export")
        self.assertTrue(result["wrote"])
        self.assertEqual(result["bytes"], len(before))
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(result["sha256"], hashlib.sha256(before).hexdigest())

    def test_export_keeps_every_section(self):
        result = self.ok("export", to=str(self.tmp / "copy.json"))
        text = Path(self.tmp / "copy.json").read_text(encoding="utf-8")
        document = json.loads(text)
        self.assertEqual(set(document),
                         {"schema", "level", "props", "structures", "cameras",
                          "renderPresets"})
        self.assertEqual(len(document["props"]), 16)
        self.assertEqual(len(document["structures"]), 8)
        self.assertEqual(len(document["cameras"]), 3)
        self.assertEqual(len(document["renderPresets"]), 3)
        self.assertEqual(core.value_units_total(document),
                         core.value_units_total(core.load_doc(EXAMPLE)))
        self.assertEqual(Path(result["path"]).name, "copy.json")

    def test_export_to_a_copy_does_not_move_the_truth(self):
        result = self.ok("export", to=str(self.tmp / "handoff.json"))
        self.assertFalse(result["truth"])
        self.assertTrue(self.ok("status")["lastExport"] is None)
        # The next edit still exports to the opened file, not to the copy.
        self.ok("move", id="prop_acorn_000", delta=[0.1, 0, 0])
        self.assertEqual(json.loads(self.path.read_text(encoding="utf-8"))
                         ["props"][0]["position"],
                         [1.3, 0.015, 2.1])

    def test_round_trip_export_open_export_is_lossless(self):
        first = self.tmp / "first.json"
        self.ok("export", to=str(first))
        second_editor = editor_core.Editor(FakeScene(), manifests=MANIFESTS)
        second_editor.open(None, first)
        second = self.tmp / "second.json"
        second_editor.handle({"op": "export", "args": {"to": str(second)}})
        self.assertEqual(first.read_bytes(), second.read_bytes())
        # And a rebuild from the export produces the same document as the original.
        self.assertEqual(json.loads(first.read_text(encoding="utf-8")),
                         core.canonical_doc(core.load_doc(EXAMPLE), MANIFESTS))

    def test_export_refuses_a_red_document_without_force(self):
        self.editor.doc["level"]["expected"]["propCount"] = 999
        response = self.ok("export")
        self.assertFalse(response["wrote"])
        self.assertTrue(response["gate"]["errors"])
        forced = self.ok("export", force=True)
        self.assertTrue(forced["wrote"])


class RealLevelExportTests(unittest.TestCase):
    """The export must not lose anything on the real first level (read-only)."""

    LEVEL = ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"

    def test_export_of_the_first_level_is_complete_and_byte_identical(self):
        self.assertTrue(self.LEVEL.is_file(), "the first level is missing")
        tmp = Path(tempfile.mkdtemp(prefix="levelbuild-export-"))
        self.addCleanup(shutil.rmtree, tmp, True)
        editor = editor_core.Editor(FakeScene(), manifests=MANIFESTS)
        opened = editor.open(None, self.LEVEL)
        self.assertTrue(opened["gate"]["ok"], opened["gate"]["errors"])
        target = tmp / "export.json"
        result = editor.handle({"op": "export", "args": {"to": str(target)}})["result"]
        self.assertTrue(result["wrote"])
        document = json.loads(target.read_text(encoding="utf-8"))
        # The counts are the live level's, so read them off the document instead of
        # pinning them here: the export must agree with its source, and
        # ``level.expected`` must agree with the props it describes.
        source = core.load_doc(self.LEVEL)
        self.assertTrue(document["props"])
        self.assertEqual(len(document["props"]), len(source["props"]))
        self.assertEqual(len(document["defs"]), len(source["defs"]),
                         "defs[] must survive the export")
        self.assertEqual(core.value_units_total(document), 0,
                         "the canonical writer drops the deprecated valueUnits keys")
        self.assertEqual(document["cameras"], source["cameras"])
        self.assertEqual(document["level"]["expected"],
                         {"propCount": len(document["props"]),
                          "growthMm3": core.growth_mm3_total(document)})
        self.assertEqual(target.read_bytes(), self.LEVEL.read_bytes(),
                         "export -> bytes must equal the committed file")


class DeriveSupportsTests(EditorCase):
    """The reviewable half of support derivation: dry run, then an explicit write.

    The fixture is a level as an import leaves it - the acorn sits on the plate with no
    pointer, so the gate reports it as floating.  ``derive_supports`` is what turns that
    into a green level, and the dry run is what a human reviews before that happens.
    """

    def setUp(self):
        super().setUp()
        document = core.load_doc(EXAMPLE)
        acorn = next(prop for prop in document["props"] if prop["itemId"] == "prop_acorn_000")
        acorn["position"] = [-1.70, 0.485, -1.10]
        self.path.write_text(json.dumps(document, indent=1) + "\n", encoding="utf-8")
        self.opened = self.editor.open(None, self.path)
        self.assertFalse(self.opened["gate"]["ok"], "the acorn should be floating on open")

    def test_the_dry_run_reports_the_link_and_writes_nothing(self):
        before = self.path.read_bytes()
        syncs = len(self.scene.syncs)
        result = self.ok("derive_supports")
        self.assertFalse(result["applied"])
        self.assertEqual([entry["itemId"] for entry in result["new"]], ["prop_acorn_000"])
        self.assertEqual(result["new"][0]["parent"], "prop_plate_000")
        self.assertEqual(result["new"][0]["gap"], 0.0)
        self.assertEqual(self.path.read_bytes(), before)
        self.assertNotIn("parent", self.prop("prop_acorn_000"))
        self.assertEqual(len(self.scene.syncs), syncs, "a dry run must not touch the scene")
        self.assertFalse(self.ok("status")["gate"]["ok"])

    def test_apply_writes_the_pointer_and_greens_the_gate(self):
        result = self.ok("derive_supports", apply=True)
        self.assertTrue(result["applied"])
        self.assertEqual(result["written"], ["prop_acorn_000"])
        prop = self.prop("prop_acorn_000")
        self.assertEqual(prop["parent"], "prop_plate_000")
        self.assertIs(prop["parentDerived"], True)
        self.assertEqual(self.path.read_text(encoding="utf-8"),
                         core.dump_doc(self.editor.doc, self.editor.manifests))
        status = self.ok("status")
        self.assertTrue(status["gate"]["ok"])
        self.assertFalse(status["dirty"])
        self.assertEqual(status["counts"]["supportsDerived"], 1)

    def test_a_hand_authored_pointer_survives(self):
        self.ok("derive_supports", apply=True)
        cookie = self.prop("prop_cookie_000")
        self.assertEqual(cookie["stackedOn"], "prop_plate_000")
        self.assertNotIn("parent", cookie)

    def test_a_stale_derived_link_only_fails_under_strict_parents(self):
        # Apply, then move the prop off its supporter: the pointer is now machine-stale.
        self.ok("derive_supports", apply=True)
        self.ok("move", id="prop_acorn_000", delta=[0.0, 0.5, 0.0])
        self.ok("set", id="prop_acorn_000", pinned=True)      # stands on its own: no float error
        plain = self.ok("check")
        self.assertEqual(plain["errorCount"], 0)
        self.assertTrue(any("[parent_not_seated]" in warning
                            for warning in plain["warnings"]), plain["warnings"])
        strict = self.ok("check", strict_parents=True)
        self.assertFalse(strict["ok"])
        self.assertTrue(any("[parent_not_seated]" in error for error in strict["errors"]),
                        strict["errors"])

    def test_pinned_is_settable_and_clears_cleanly(self):
        self.ok("set", id="prop_plate_000", pinned=True)
        self.assertIs(self.prop("prop_plate_000")["pinned"], True)
        self.assertEqual(self.ok("status")["counts"]["pinned"], 1)
        self.ok("set", id="prop_plate_000", pinned=False)
        self.assertNotIn("pinned", self.prop("prop_plate_000"), "false must not be written")
        self.assertEqual(self.ok("status")["counts"]["pinned"], 0)

    def test_pinning_is_what_makes_a_floating_prop_legal(self):
        # The acorn floats in this fixture; `pinned` is the author's "it stands on its own".
        self.ok("set", id="prop_acorn_000", pinned=True)
        self.assertEqual(self.ok("status")["gate"]["errorCount"], 0)
        # Unpinning re-arms the floating rule, so the same document is refused again.
        refused = self.refused("set", id="prop_acorn_000", pinned=False)
        self.assertTrue(any("floats" in detail for detail in refused["details"]),
                        refused["details"])
        self.assertIs(self.prop("prop_acorn_000")["pinned"], True)

    def test_a_second_apply_is_nothing_to_do(self):
        self.ok("derive_supports", apply=True)
        result = self.ok("derive_supports", apply=True)
        self.assertEqual(result["written"], [])
        self.assertFalse(result["changed"])
        self.assertFalse(self.ok("status")["dirty"])

    def test_scope_limits_which_props_are_linked(self):
        result = self.ok("derive_supports", ids=["prop_apple_000"])
        self.assertEqual(result["new"], [])

    def test_unknown_argument_is_refused(self):
        self.assertIn("unknown argument", self.refused("derive_supports", bogus=1)["error"])


class BatchTests(EditorCase):
    def test_batch_applies_every_op_with_one_export_and_one_sync(self):
        before_syncs = len(self.scene.syncs)
        result = self.editor.handle({"ops": [
            {"op": "move", "args": {"id": "prop_acorn_000", "to": [1.3, 0.015, 2.1]}},
            {"op": "move", "args": {"id": "prop_banana_000", "to": [0.7, 0.016, 1.6]}},
            {"op": "set", "args": {"id": "prop_acorn_000", "zone": "pocket"}},
        ]})
        self.assertTrue(result["ok"], result["error"])
        self.assertEqual(len(self.scene.syncs), before_syncs + 1)
        ops = result["result"]["ops"]
        self.assertEqual(len(ops), 3)
        self.assertTrue(all(item["changed"] for item in ops),
                        "each deferred op must report whether it changed the document")
        self.assertEqual(len(self.editor.undo_stack), 1)
        self.assertAlmostEqual(self.prop("prop_acorn_000")["position"][0], 1.3)
        self.assertAlmostEqual(self.prop("prop_banana_000")["position"][0], 0.7)
        self.assertEqual(self.prop("prop_acorn_000")["zone"], "pocket")

    def test_a_noop_inside_a_batch_is_reported_as_unchanged(self):
        result = self.editor.handle({"ops": [
            {"op": "set", "args": {"id": "prop_acorn_000", "zone": "lawn"}},
            {"op": "move", "args": {"id": "prop_acorn_000", "delta": [0.1, 0, 0]}},
        ]})
        self.assertTrue(result["ok"], result["error"])
        self.assertEqual([item["changed"] for item in result["result"]["ops"]],
                         [False, True])

    def test_batch_is_atomic(self):
        before = sha(self.path)
        before_syncs = len(self.scene.syncs)
        result = self.editor.handle({"ops": [
            {"op": "move", "args": {"id": "prop_acorn_000", "to": [1.3, 0.015, 2.1]}},
            {"op": "move", "args": {"id": "prop_apple_000", "to": [-2.7, -3.0, -1.2]}},
        ]})
        self.assertFalse(result["ok"])
        self.assertIn("gate rule", result["error"])
        self.assertTrue(any("sunk" in detail or "below" in detail
                            for detail in result["details"]))
        self.assertEqual(sha(self.path), before)
        self.assertEqual(len(self.scene.syncs), before_syncs)
        self.assertEqual(self.prop("prop_acorn_000")["position"][0], 1.2)
        self.assertEqual(self.editor.undo_stack, [])

    def test_batch_rejects_a_read_only_op(self):
        result = self.editor.handle({"ops": [{"op": "list", "args": {}}]})
        self.assertFalse(result["ok"])
        self.assertIn("not a mutating op", result["error"])


class CameraAndRenderTests(EditorCase):
    def test_set_camera_merges_and_renders_a_preset(self):
        self.ok("camera", action="set", id="overview_3q", lens=40)
        camera = next(item for item in self.editor.doc["cameras"]
                      if item["id"] == "overview_3q")
        self.assertEqual(camera["lens"], 40)
        self.assertIn("location", camera)
        self.assertIn("target", camera)

    def test_add_camera_and_a_preset_that_uses_it(self):
        self.ok("camera", action="set", id="side", type="perspective",
                location=[0, 2, -6], target=[0, 0.3, 0], lens=50)
        self.ok("preset", action="set", id="side_view", camera="side",
                engine="WORKBENCH", res=[640, 480])
        result = self.ok("render", preset="side_view", out=str(self.tmp / "render"))
        self.assertTrue(Path(result["path"]).is_file())
        self.assertEqual(result["engine"], "WORKBENCH")
        self.assertEqual(result["res"], [640, 480])
        self.assertEqual(Path(result["path"]).name, "picnic_corner_demo_side_view_workbench.png")
        camera_doc = self.scene.renders[-1]["camera"]
        self.assertEqual(camera_doc["id"], "side")

    def test_render_with_an_inline_camera_is_temporary(self):
        result = self.ok("render", camera={"type": "ortho_top",
                                           "location": [0, 9, 0], "target": [0, 0, 0],
                                           "coverage_m": 9.0},
                         out=str(self.tmp / "render"), res=[320, 240])
        self.assertTrue(Path(result["path"]).is_file())
        self.assertIsNone(self.scene.renders[-1]["preset"])
        self.assertIn("adhoc", Path(result["path"]).name)
        # A render with an inline camera must not add a camera to the document.
        self.assertIn("not a prop", self.refused("get", id="adhoc")["error"])

    def test_unknown_preset_is_refused_with_the_known_ids(self):
        response = self.refused("render", preset="nope")
        self.assertIn("plan", response["error"])

    def test_remove_camera_reports_the_presets_that_used_it(self):
        result = self.ok("camera", action="remove", id="top")
        self.assertEqual(result["presetsUsingIt"], ["plan"])
        self.assertIn("not a prop", self.refused("get", id="top")["error"])


class ProtocolTests(EditorCase):
    def test_unknown_op_lists_the_known_ones(self):
        response = self.refused("teleport")
        self.assertIn("unknown op", response["error"])
        self.assertIn("add_prop", response["error"])

    def test_bad_args_are_refused_before_anything_changes(self):
        self.assertIn("must be a list of 3 numbers",
                      self.refused("move", id="prop_acorn_000", to="left")["error"])
        self.assertIn("exactly one of to",
                      self.refused("move", id="prop_acorn_000",
                                   to=[0, 0, 0], delta=[1, 0, 0])["error"])
        self.assertIn("unknown argument",
                      self.refused("list", zon="table")["error"])

    def test_ops_need_an_open_level(self):
        editor = editor_core.Editor(FakeScene(), manifests=MANIFESTS)
        response = editor.handle({"op": "list"})
        self.assertFalse(response["ok"])
        self.assertIn("no level is open", response["error"])
        self.assertIn("open", editor.handle({"op": "status"})["error"])

    def test_response_always_carries_state_and_seconds(self):
        response = self.handle("status")
        self.assertIn("state", response)
        self.assertIn("seconds", response)
        self.assertEqual(response["state"]["props"], 16)

    def test_open_missing_file_is_refused(self):
        editor = editor_core.Editor(FakeScene(), manifests=MANIFESTS)
        response = editor.handle({"op": "open", "args": {"level": "../nope"}})
        self.assertFalse(response["ok"])

    def test_reload_picks_up_a_hand_edit(self):
        document = json.loads(self.path.read_text(encoding="utf-8"))
        document["props"][0]["position"][0] = 1.25
        self.path.write_text(core.dump_doc(document, MANIFESTS), encoding="utf-8")
        self.ok("reload")
        self.assertEqual(self.prop("prop_acorn_000")["position"][0], 1.25)
        self.assertEqual(self.editor.undo_stack, [])


if __name__ == "__main__":
    unittest.main()
