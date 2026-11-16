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
    def test_read_apis_require_the_token_and_healthz_is_the_liveness_probe(self):
        status, payload = self.call("/api/health", token=None)
        self.assertEqual(status, 403)
        status, payload = self.call("/api/level", token=None)
        self.assertEqual(status, 403)
        status, payload = self.call("/api/levels")
        self.assertEqual(status, 200)
        self.assertEqual([level["id"] for level in payload["levels"]], [self.level])
        status, payload = self.call("/api/hints?d=0.5")
        self.assertEqual(status, 200)
        self.assertEqual(payload["hints"]["diameter"], 0.5)
        status, payload = self.call("/healthz", token=None)
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertNotIn("level", payload)
        status, payload = self.call("/api/health")
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
        self.assertEqual(status, 403)
        status, body = self.call("/api/models/acorn.glb")
        self.assertEqual(status, 200)
        self.assertTrue(body.startswith(b"glTF"))
        status, body = self.call("/api/models/acorn.glb?token=%s" % TOKEN, token=None)
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
        # k=0.70 (the original's default volume factor) floors apple's Req to 187 mm.
        self.assertAlmostEqual(state["manifests"]["apple"]["dReq"], 0.187, places=3)
        derived = state["derived"]["props"]["prop_acorn_000"]
        # k=0.70 -> 60 mm (was 68 mm under the k-less gate), still whole millimetres.
        self.assertAlmostEqual(derived["dReq"], 0.06, places=3)
        self.assertEqual(derived["valueUnits"], 1)
        self.assertEqual(len(derived["worldAabb"]), 2)
        self.assertIn("tiers", state["check"]["tables"])
        self.assertEqual(state["check"]["tables"]["tiers"][1]["diameter"], 0.5)

    def test_every_level_is_listed(self):
        status, payload = self.call("/api/levels")
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
                ("GET /api/events?token=%s HTTP/1.1\r\nHost: localhost\r\n"
                 "Accept: text/event-stream\r\n\r\n" % TOKEN).encode("ascii"))
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

    def test_a_multi_object_batch_is_one_export_and_one_undo_step(self):
        original = sha(self.placement)
        ids = ["prop_acorn_000", "prop_apple_000"]
        positions = {prop["itemId"]: prop["position"] for prop in self.state()["doc"]["props"]}
        operations = [
            {"op": "move", "args": {"id": item_id,
                                     "to": [positions[item_id][0] + 0.01,
                                            positions[item_id][1],
                                            positions[item_id][2]]}}
            for item_id in ids
        ]
        status, payload = self.call("/api/op", {"ops": operations})
        self.assertTrue(payload["ok"], payload["error"])
        self.assertEqual(payload["saved"]["diff"]["counts"]["props"]["updated"], 2)
        moved = sha(self.placement)
        self.assertNotEqual(moved, original)
        self.assertEqual(self.state()["history"]["undo"], 1)
        status, payload = self.op("undo")
        self.assertTrue(payload["ok"], payload["error"])
        self.assertEqual(sha(self.placement), original)
        for item_id in ids:
            self.assertEqual(self.prop(item_id)["position"], positions[item_id])

    def test_an_edit_publishes_a_new_version(self):
        before = self.app.version
        self.op("move", id="prop_acorn_000", to=[1.3, 0.015, 2.1])
        self.assertGreater(self.app.version, before)


class HintsTests(WebServerCase):
    def test_hints_match_the_validator_tier_table(self):
        state = self.state()
        tier = next(row for row in state["check"]["tables"]["tiers"]
                    if row["diameter"] == 0.5)
        status, payload = self.call("/api/hints?d=0.5")
        self.assertEqual(status, 200)
        hints = payload["hints"]
        self.assertEqual(hints["counts"]["food"], tier["food"])
        self.assertEqual(hints["counts"]["terrain"], tier["terrain"])
        self.assertEqual(hints["counts"]["blocking"], tier["blocking"])
        self.assertEqual(hints["diameter"], 0.5)
        self.assertAlmostEqual(hints["stepLimit"], 0.125)


class VolumeKApiTests(WebServerCase):
    """The web surface of the per-item volume factor: derived rows, stats, and set_def."""

    def test_derived_rows_carry_whole_mm_req_and_the_factor(self):
        status, payload = self.call("/api/level")
        state = payload["state"]
        derived = state["derived"]["props"]["prop_acorn_000"]
        self.assertEqual(derived["dReqMm"], 60)
        self.assertAlmostEqual(derived["dReq"], 0.06, places=3)
        self.assertEqual(derived["volumeK"], 0.70)
        manifest = state["manifests"]["acorn"]
        self.assertEqual(manifest["volumeK"], 0.70)
        self.assertEqual(manifest["dReqMm"], 60)

    def test_set_def_changes_the_derived_gate_and_stats(self):
        status, payload = self.op("add_def", id="apple", valueUnits=1, kind="fruit")
        self.assertTrue(payload["ok"], payload["error"])
        before = self.state()["derived"]["props"]["prop_apple_000"]
        status, payload = self.op("set_def", id="apple", volumeK=2.0)
        self.assertTrue(payload["ok"], payload["error"])
        after = self.state()["derived"]["props"]["prop_apple_000"]
        self.assertEqual(after["volumeK"], 2.0)
        self.assertGreater(after["dReqMm"], before["dReqMm"])
        status, payload = self.call("/api/stats?scope=all&metric=count&bin=0.05&diameter=0.5")
        self.assertEqual(status, 200)
        stats = payload["stats"]
        self.assertEqual(stats["ballMm"], 500)
        self.assertEqual(sum(tier["count"] for tier in stats["tiers"].values()),
                         stats["totals"]["count"])

    def test_stats_green_tier_is_the_validator_food_count(self):
        status, payload = self.call("/api/stats?scope=all&metric=count&bin=0.05&diameter=0.3")
        stats = payload["stats"]
        tier = next(row for row in self.state()["check"]["tables"]["tiers"]
                    if row["diameter"] == 0.30)
        self.assertEqual(stats["tiers"]["green"]["count"], tier["food"])
        self.assertEqual(stats["dReqMedianMm"] is not None, True)


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


class RegionStatsTests(WebServerCase):
    def region(self, name, low, high, **extra):
        entry = {"name": name, "min": list(low), "max": list(high), "visible": True}
        entry.update(extra)
        return entry

    def save_regions(self, regions, token=TOKEN):
        return self.call("/api/regions", {"regions": regions}, token=token)

    def stats(self, **query):
        from urllib.parse import urlencode
        return self.call("/api/stats?" + urlencode(query))

    def test_regions_start_empty_and_round_trip_atomically(self):
        status, payload = self.call("/api/regions")
        self.assertEqual(status, 200)
        self.assertEqual(payload["schemaVersion"], 1)
        self.assertEqual(payload["regions"], [])
        region = self.region("small", [0, 0, 0], [1, 1, 1])
        status, payload = self.save_regions([region])
        self.assertEqual(status, 200, payload)
        self.assertTrue(payload["ok"])
        path = self.tmp / "levels" / self.level / "regions.json"
        self.assertTrue(path.is_file())
        self.assertFalse(list(path.parent.glob("*.tmp-*")))
        status, payload = self.call("/api/regions")
        self.assertEqual(payload["regions"], [region])
        self.assertEqual(payload["path"], core.display_path(path))

    def test_regions_validate_names_and_boxes(self):
        status, payload = self.save_regions([self.region("x", [0, 0, 0], [0, 1, 1])])
        self.assertEqual(status, 400)
        self.assertIn("min >= max", payload["error"])
        status, payload = self.save_regions([
            self.region("x", [0, 0, 0], [1, 1, 1]),
            self.region("x", [0, 0, 0], [1, 1, 1])])
        self.assertEqual(status, 400)
        self.assertIn("duplicate", payload["error"])
        status, payload = self.save_regions([self.region("", [0, 0, 0], [1, 1, 1])])
        self.assertEqual(status, 400)
        status, payload = self.save_regions([self.region("x", [0, 0, 0], [1, 1, 1])],
                                            token=None)
        self.assertEqual(status, 403)

    def test_stats_all_count_volume_units_and_bins(self):
        status, payload = self.stats(scope="all", metric="volume", bin=0.05, diameter=0.5)
        self.assertEqual(status, 200, payload)
        stats = payload["stats"]
        document = self.state()["doc"]
        self.assertEqual(stats["totals"]["count"], len(document["props"]))
        volumes = [prop["aabb"][0] * prop["aabb"][1] * prop["aabb"][2]
                   for prop in document["props"]]
        self.assertAlmostEqual(stats["totals"]["volume"], sum(volumes), places=6)
        self.assertEqual(stats["totals"]["units"], core.value_units_total(document))
        self.assertEqual(sum(bin_["count"] for bin_ in stats["bins"]),
                         stats["totals"]["count"])
        self.assertAlmostEqual(sum(bin_["volume"] for bin_ in stats["bins"]),
                               stats["totals"]["volume"], places=5)
        self.assertAlmostEqual(sum(bin_["volumePct"] for bin_ in stats["bins"]), 100.0,
                               places=1)
        self.assertEqual(len(stats["includedIds"]), len(document["props"]))
        self.assertTrue(stats["cumulative"])
        self.assertIsNotNone(stats["dReqMedian"])

    def test_stats_center_vs_bbox_and_boundaries(self):
        prop = self.prop("prop_acorn_000")
        position = prop["position"]
        aabb = prop["aabb"]
        center_box = self.region("center", [position[0] - 0.001, 0, position[2] - 0.001],
                                 [position[0] + 0.001, 0.1, position[2] + 0.001])
        self.save_regions([center_box])
        status, payload = self.stats(scope="region", region="center", mode="center")
        self.assertEqual(payload["stats"]["totals"]["count"], 1)
        self.assertEqual(payload["stats"]["includedIds"], ["prop_acorn_000"])

        edge = position[0] + aabb[0] / 2
        touch = self.region("touch", [edge, 0, position[2] - 0.01],
                            [edge + 0.5, 0.1, position[2] + 0.01])
        self.save_regions([touch])
        status, payload = self.stats(scope="region", region="touch", mode="center")
        self.assertEqual(payload["stats"]["totals"]["count"], 0,
                         "center mode excludes a box that only touches the face")
        status, payload = self.stats(scope="region", region="touch", mode="bbox")
        self.assertEqual(payload["stats"]["totals"]["count"], 1,
                         "bbox mode counts a touching face")

    def test_empty_region_and_ids_scope(self):
        empty = self.region("empty", [100, 100, 100], [101, 101, 101])
        self.save_regions([empty])
        status, payload = self.stats(scope="region", region="empty", mode="center")
        stats = payload["stats"]
        self.assertEqual(stats["totals"], {"count": 0, "volume": 0.0, "units": 0})
        self.assertEqual(stats["dReqMedian"], None)
        self.assertTrue(all(bin_["count"] == 0 for bin_ in stats["bins"]))
        ids = ["prop_acorn_000", "prop_plate_000"]
        status, payload = self.stats(scope="ids", ids=",".join(ids))
        self.assertEqual(sorted(payload["stats"]["includedIds"]), sorted(ids))

    def test_tiers_and_compare(self):
        prop = self.prop("prop_acorn_000")
        region = self.region("pocket", [0, 0, 0], [4.5, 1, 4.5])
        self.save_regions([region])
        status, payload = self.stats(scope="region", region="pocket", mode="center",
                                     diameter=0.5)
        tiers = payload["stats"]["tiers"]
        self.assertEqual(tiers["green"]["count"] + tiers["yellow"]["count"]
                         + tiers["red"]["count"] + tiers["grey"]["count"],
                         payload["stats"]["totals"]["count"])
        status, payload = self.stats(compare=1, diameter=0.5)
        self.assertEqual(len(payload["stats"]["regions"]), 1)
        row = payload["stats"]["regions"][0]
        self.assertEqual(row["name"], "pocket")
        self.assertEqual(row["count"], payload["stats"]["regions"][0]["count"])
        self.assertIn("absorbableRatio", row)
        self.assertTrue(prop)
