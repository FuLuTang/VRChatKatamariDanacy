"""The web level editor's HTTP contract, over a real socket and a real file.

What a wrong implementation would get wrong *silently* is what these tests watch:

* every edit goes through ``editor_core`` (so the gate and the canonical bytes are the
  same as the CLI/Blender editor) - not a second writer;
* a refused edit leaves the file byte-identical;
* undo and redo restore the exact file bytes (the UI's Ctrl+Z is the same export path);
* a second writer (the Blender resident editor, a human, another tab) is detected
  before an edit instead of being overwritten;
* the one-writer lock really refuses a second editor on the same level;
* the write token is required, reads are not;
* the push endpoint is a stream, not a poll.

No Blender, no browser: a fake scene and CPython's own HTTP client.
"""

import json
import os
import shutil
import socket
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Docs.tests.levelbuild.test_placement_core import EXAMPLE  # noqa: E402
from Tools.levelbuild import editor_core  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.leveleditor import server as web  # noqa: E402

TOKEN = "test-token-for-the-web-editor"


def sha(path):
    import hashlib
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class WebServerCase(unittest.TestCase):
    """One server on a temp copy of the 16-prop example, serving on port 0."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="leveleditor-web-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.levels = self.tmp / "levels"
        self.level = "picnic_corner_demo"
        folder = self.levels / self.level
        folder.mkdir(parents=True)
        self.placement = folder / "placement.json"
        self.placement.write_bytes(EXAMPLE.read_bytes())
        self.state_dir = self.tmp / "state"
        self.app = web.WebEditor(self.level, levels_dir=self.levels,
                                 state_dir=self.state_dir, lock=False)
        self.httpd = web.make_http_server(self.app, "127.0.0.1", 0, token=TOKEN,
                                          token_path=self.state_dir / "token")
        self.thread = threading.Thread(target=self.httpd.serve_forever,
                                       kwargs={"poll_interval": 0.02}, daemon=True)
        self.thread.start()
        self.base = "http://127.0.0.1:%d" % self.httpd.server_address[1]
        self.addCleanup(self._stop)

    def _stop(self):
        self.httpd.stop_event.set()
        self.httpd.shutdown()
        self.app.close()
        self.httpd.server_close()
        self.thread.join(5)

    # -- helpers -------------------------------------------------------- #
    def call(self, path, payload=None, token=TOKEN, timeout=30):
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, headers={
            "Content-Type": "application/json"})
        if token:
            request.add_header(web.TOKEN_HEADER, token)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                body = response.read()
                try:
                    return response.status, json.loads(body)
                except (json.JSONDecodeError, UnicodeDecodeError):
                    return response.status, body
        except urllib.error.HTTPError as error:
            body = error.read()
            try:
                return error.code, json.loads(body)
            except (json.JSONDecodeError, UnicodeDecodeError):
                return error.code, body

    def op(self, name, token=TOKEN, **args):
        return self.call("/api/op", {"op": name, "args": args}, token=token)

    def state(self):
        status, payload = self.call("/api/level")
        self.assertEqual(status, 200)
        return payload["state"]

    def prop(self, item_id):
        return next(prop for prop in self.state()["doc"]["props"]
                    if prop["itemId"] == item_id)


class SurfaceTests(WebServerCase):
    def test_health_needs_no_token(self):
        status, payload = self.call("/api/health", token=None)
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["level"], self.level)
        self.assertEqual(payload["props"], 16)
        self.assertTrue(payload["gate"]["ok"])

    def test_index_js_models_and_favicon_are_served(self):
        status, body = self.call("/", token=None)
        self.assertEqual(status, 200)
        self.assertIn("VRC塊魂".encode("utf-8"), body)
        status, body = self.call("/js/main.js", token=None)
        self.assertEqual(status, 200)
        self.assertIn(b"EditorView", body)
        status, body = self.call("/api/models/acorn.glb", token=None)
        self.assertEqual(status, 200)
        self.assertTrue(body.startswith(b"glTF"))
        status, body = self.call("/favicon.svg", token=None)
        self.assertEqual(status, 200)

    def test_level_payload_has_doc_derived_and_manifests(self):
        state = self.state()
        self.assertEqual(state["counts"]["props"], 16)
        self.assertEqual(state["level"], self.level)
        self.assertEqual(state["check"]["errors"], [])
        self.assertIn("picnic_corner_demo", state["file"]["path"])
        self.assertGreaterEqual(len(state["manifests"]), 44)
        self.assertIn("dReq", state["manifests"]["apple"])
        self.assertAlmostEqual(state["manifests"]["apple"]["dReq"], 0.211, places=3)
        derived = state["derived"]["props"]["prop_acorn_000"]
        self.assertAlmostEqual(derived["dReq"], 0.068, places=3)
        self.assertEqual(derived["valueUnits"], 1)
        self.assertEqual(len(derived["worldAabb"]), 2)
        self.assertIn("tiers", state["check"]["tables"])
        self.assertEqual(state["check"]["tables"]["tiers"][1]["diameter"], 0.5)

    def test_every_level_is_listed(self):
        status, payload = self.call("/api/levels", token=None)
        self.assertEqual(status, 200)
        self.assertEqual([level["id"] for level in payload["levels"]], [self.level])


class WriteTests(WebServerCase):
    def test_write_without_the_token_is_403(self):
        before = sha(self.placement)
        status, payload = self.op("move", token=None, id="prop_acorn_000",
                                  to=[1.3, 0.015, 2.1])
        self.assertEqual(status, 403)
        self.assertFalse(payload["ok"])
        self.assertEqual(sha(self.placement), before)
        status, payload = self.op("move", token="not-the-token", id="prop_acorn_000",
                                  to=[1.3, 0.015, 2.1])
        self.assertEqual(status, 403)

    def test_a_move_exports_canonical_bytes_and_a_diff_summary(self):
        status, payload = self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"], payload["error"])
        saved = payload["saved"]
        self.assertTrue(saved["wrote"])
        self.assertEqual(saved["diff"]["counts"]["props"],
                         {"added": 0, "updated": 1, "removed": 0})
        text = self.placement.read_text(encoding="utf-8")
        self.assertEqual(text, core.dump_doc(self.app.editor.doc))
        self.assertEqual(sha(self.placement), saved["sha256"])
        self.assertEqual(json.loads(text)["props"][0]["position"], [1.3, 0.015, 2.1])

    def test_the_design_total_gate_refuses_an_add_until_it_is_explicit(self):
        before = sha(self.placement)
        status, payload = self.op("add_prop", **{"def": "apple"},
                                  position=[-3.5, 0.0394, 3.5], valueUnits=4)
        self.assertEqual(status, 200)
        self.assertFalse(payload["ok"])
        self.assertIn("design total", payload["error"])
        self.assertIn("level.expected", " ".join(payload["details"]))
        self.assertEqual(sha(self.placement), before)
        self.assertEqual(len(self.prop_ids()), 16)

        status, payload = self.op("add_prop", **{"def": "apple"},
                                  position=[-3.5, 0.0394, 3.5], valueUnits=4,
                                  updateExpected=True)
        self.assertTrue(payload["ok"], payload["error"])
        self.assertEqual(payload["result"]["itemId"], "apple_01")
        self.assertEqual(len(self.prop_ids()), 17)
        self.assertEqual(self.state()["doc"]["level"]["expected"]["propCount"], 17)

    def prop_ids(self):
        return [prop["itemId"] for prop in self.state()["doc"]["props"]]

    def test_a_batch_is_all_or_nothing(self):
        before = sha(self.placement)
        status, payload = self.call("/api/op", {"ops": [
            {"op": "move", "args": {"id": "prop_acorn_000", "to": [1.3, 0.015, 2.1]}},
            {"op": "set", "args": {"id": "prop_acorn_000", "valueUnits": 99999}},
        ]})
        self.assertEqual(status, 200)
        self.assertFalse(payload["ok"])
        self.assertEqual(sha(self.placement), before)
        self.assertEqual(self.prop("prop_acorn_000")["position"], [1.2, 0.015, 2.1])

    def test_delete_updates_expected_only_when_asked(self):
        status, payload = self.op("remove", id="prop_acorn_000")
        self.assertFalse(payload["ok"])
        self.assertIn("design total", payload["error"])
        status, payload = self.op("remove", id="prop_acorn_000", updateExpected=True)
        self.assertTrue(payload["ok"], payload["error"])
        self.assertNotIn("prop_acorn_000", self.prop_ids())
        self.assertEqual(self.state()["doc"]["level"]["expected"]["propCount"], 15)

    def test_the_file_is_the_same_document_after_a_rebuild(self):
        status, payload = self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertTrue(payload["ok"])
        rebuilt = editor_core.Editor(web.WebScene(), manifests=self.app.editor.manifests)
        opened = rebuilt.open(None, self.placement)
        self.assertTrue(opened["canonical"])
        exported = rebuilt.handle({"op": "export", "args": {}})
        self.assertTrue(exported["ok"], exported["error"])
        self.assertEqual(exported["result"]["sha256"], payload["saved"]["sha256"])


class HistoryTests(WebServerCase):
    def test_undo_and_redo_restore_the_exact_bytes(self):
        original = sha(self.placement)
        self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        moved = sha(self.placement)
        self.assertNotEqual(moved, original)

        status, payload = self.op("undo")
        self.assertTrue(payload["ok"], payload["error"])
        self.assertEqual(sha(self.placement), original)
        self.assertEqual(self.prop("prop_acorn_000")["position"], [1.2, 0.015, 2.1])
        self.assertEqual(payload["result"]["undone"], ["move prop_acorn_000"])

        status, payload = self.op("redo")
        self.assertTrue(payload["ok"], payload["error"])
        self.assertEqual(sha(self.placement), moved)
        self.assertEqual(self.prop("prop_acorn_000")["position"], [1.3, 0.015, 2.1])

    def test_a_new_edit_clears_redo(self):
        self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.op("undo")
        self.assertTrue(self.state()["history"]["redo"])
        self.op("move", id="prop_acorn_000", to=[1.9, 0.015, 2.1])
        self.assertEqual(self.state()["history"]["redo"], 0)
        status, payload = self.op("redo")
        self.assertFalse(payload["ok"])
        self.assertIn("nothing to redo", payload["error"])

    def test_undo_refuses_when_there_is_nothing(self):
        status, payload = self.op("undo")
        self.assertFalse(payload["ok"])
        self.assertIn("nothing to undo", payload["error"])


class SafetyTests(WebServerCase):
    def test_an_external_write_is_a_conflict_not_an_overwrite(self):
        external = json.loads(self.placement.read_text(encoding="utf-8"))
        external["props"][0]["position"] = [2.0, 0.015, 2.0]
        self.placement.write_text(core.dump_doc(external), encoding="utf-8")
        before = sha(self.placement)
        status, payload = self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertFalse(payload["ok"])
        self.assertIn("changed on disk", payload["error"])
        self.assertEqual(sha(self.placement), before)
        self.assertEqual(json.loads(self.placement.read_text())["props"][0]["position"],
                         [2.0, 0.015, 2.0])

    def test_a_failed_atomic_replace_keeps_the_old_file(self):
        before = sha(self.placement)
        with mock.patch("os.replace", side_effect=OSError("disk full")):
            status, payload = self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertFalse(payload["ok"])
        self.assertIn("disk full", payload["error"])
        self.assertEqual(sha(self.placement), before)
        for leftover in self.placement.parent.glob("*.tmp-*"):
            leftover.unlink()

    def test_a_second_editor_on_the_same_level_is_locked_out(self):
        state_dir = self.tmp / "lock-state"
        first = web.WebEditor(self.level, levels_dir=self.levels, state_dir=state_dir,
                              lock=True)
        try:
            with self.assertRaises(RuntimeError) as caught:
                web.WebEditor(self.level, levels_dir=self.levels, state_dir=state_dir,
                              lock=True)
            self.assertIn("one level, one writer", str(caught.exception))
        finally:
            first.close()

    def test_undo_refuses_after_an_external_write(self):
        external = json.loads(self.placement.read_text(encoding="utf-8"))
        external["props"][0]["position"] = [2.0, 0.015, 2.0]
        self.placement.write_text(core.dump_doc(external), encoding="utf-8")
        self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])  # conflict: refused
        before = sha(self.placement)
        status, payload = self.op("undo")
        self.assertFalse(payload["ok"])
        self.assertIn("changed on disk", payload["error"])
        self.assertEqual(sha(self.placement), before)

    def test_the_token_is_persisted_readable_only_by_the_owner(self):
        path = self.state_dir / "token"
        token = web.load_or_create_token(self.state_dir)
        self.assertTrue(path.is_file())
        self.assertEqual(path.read_text(encoding="utf-8").strip(), token)
        self.assertEqual(web.load_or_create_token(self.state_dir), token)
        if os.name != "nt":
            self.assertEqual(oct(path.stat().st_mode & 0o777), "0o600")


class PushTests(WebServerCase):
    def test_the_event_stream_opens_and_is_sse(self):
        host, port = self.httpd.server_address[:2]
        connection = socket.create_connection((host, port), timeout=5)
        try:
            connection.sendall(
                b"GET /api/events HTTP/1.1\r\nHost: localhost\r\n"
                b"Accept: text/event-stream\r\n\r\n")
            received = b""
            deadline = time.time() + 5
            while b"\n\n" not in received and time.time() < deadline:
                chunk = connection.recv(4096)
                if not chunk:
                    break
                received += chunk
            text = received.decode("latin-1")
            self.assertIn("200 OK", text)
            self.assertIn("text/event-stream", text)
            self.assertIn("retry:", text)
        finally:
            connection.close()

    def test_an_edit_publishes_a_new_version(self):
        before = self.app.version
        self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertGreater(self.app.version, before)


class HintsTests(WebServerCase):
    def test_hints_match_the_validator_tier_table(self):
        state = self.state()
        tier = next(row for row in state["check"]["tables"]["tiers"]
                    if row["diameter"] == 0.5)
        status, payload = self.call("/api/hints?d=0.5", token=None)
        self.assertEqual(status, 200)
        hints = payload["hints"]
        self.assertEqual(hints["counts"]["food"], tier["food"])
        self.assertEqual(hints["counts"]["terrain"], tier["terrain"])
        self.assertEqual(hints["counts"]["blocking"], tier["blocking"])
        self.assertEqual(hints["diameter"], 0.5)
        self.assertAlmostEqual(hints["stepLimit"], 0.125)


class OpenTests(WebServerCase):
    def test_open_another_level_through_the_token_endpoint(self):
        second_folder = self.levels / "second_level"
        second_folder.mkdir()
        document = json.loads(EXAMPLE.read_text(encoding="utf-8"))
        document["level"]["id"] = "second_level"
        (second_folder / "placement.json").write_text(
            core.dump_doc(document), encoding="utf-8")
        status, payload = self.call("/api/open", {"level": "second_level"})
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"], payload["error"])
        self.assertEqual(payload["state"]["level"], "second_level")
        self.assertEqual(payload["state"]["counts"]["props"], 16)

        status, payload = self.call("/api/open", {"level": "does_not_exist"})
        self.assertFalse(payload["ok"])
        self.assertIn("cannot read", payload["error"])
        self.assertEqual(self.state()["level"], "second_level")


if __name__ == "__main__":
    unittest.main()
