"""The editor's local transport: real HTTP, fake scene, no Blender.

The transport is small but it is the only thing between a Bash-invoked agent and the
level file, so it gets a real socket test: bind, endpoint file, token, one op, the
``/health`` probe without a token, and a clean shutdown that leaves no endpoint behind.

A stale endpoint (the process died) must produce a readable "no editor answered" error,
not a traceback in the agent's face.
"""

import json
import os
import stat
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Docs.tests.levelbuild.test_level_editor import FakeScene  # noqa: E402
from Docs.tests.levelbuild.test_placement_core import EXAMPLE, MANIFESTS  # noqa: E402
from Tools.levelbuild import editor_core, editor_http  # noqa: E402


class TransportTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="levelbuild-http-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.tmp, True))
        self.placement = self.tmp / "placement.json"
        self.placement.write_bytes(EXAMPLE.read_bytes())
        self.endpoint = self.tmp / "run" / "editor.json"
        scene = FakeScene()
        self.editor = editor_core.Editor(scene, manifests=MANIFESTS)
        self.editor.open(None, self.placement)
        self.server = editor_http.Server(self.editor, self.endpoint, extra={
            "blender": "fake-4.0.2"}, verbose=False)
        self.server.start()
        self.thread = threading.Thread(target=self.server.serve, daemon=True)
        self.thread.start()

    def tearDown(self):
        if self.thread.is_alive():
            self.editor.stop_requested = True
            self.thread.join(5)
        editor_http.clear_endpoint(self.endpoint)

    def url(self, path):
        host, port = self.server._httpd.server_address[:2]
        return "http://%s:%d%s" % (host, port, path)

    def test_health_needs_no_token_and_reports_the_process(self):
        health = editor_http.health(endpoint=self.endpoint)
        self.assertTrue(health["ok"])
        self.assertEqual(health["pid"], os.getpid())
        self.assertEqual(health["level"], (self.editor.doc["level"]["id"]))
        self.assertEqual(health["props"], 16)
        self.assertEqual(health["blender"], "fake-4.0.2")

    def test_endpoint_file_is_private_and_describes_the_server(self):
        info = editor_http.read_endpoint(self.endpoint)
        self.assertEqual(info["protocol"], editor_http.PROTOCOL)
        self.assertTrue(info["token"])
        self.assertEqual(info["host"], "127.0.0.1")
        self.assertGreater(info["port"], 0)
        if os.name != "nt":
            mode = stat.S_IMODE(self.endpoint.stat().st_mode)
            self.assertEqual(oct(mode), "0o600")
            self.assertEqual(oct(stat.S_IMODE(self.endpoint.parent.stat().st_mode)), "0o700")

    def test_one_op_over_http(self):
        response = editor_http.call({"op": "status"}, endpoint=self.endpoint)
        self.assertTrue(response["ok"], response["error"])
        self.assertEqual(response["result"]["counts"]["props"], 16)
        self.assertIn("state", response)

    def test_mutation_over_http_exports_the_file(self):
        response = editor_http.call({"op": "move", "args": {
            "id": "prop_acorn_000", "to": [1.3, 0.015, 2.1]}}, endpoint=self.endpoint)
        self.assertTrue(response["ok"], response["error"])
        document = json.loads(self.placement.read_text(encoding="utf-8"))
        self.assertEqual(document["props"][0]["position"], [1.3, 0.015, 2.1])

    def test_a_bad_token_is_403_not_a_crash(self):
        body = json.dumps({"op": "status", "token": "not-the-token"}).encode()
        request = urllib.request.Request(self.url("/op"), data=body,
                                         headers={"Content-Type": "application/json"})
        with self.assertRaises(urllib.error.HTTPError) as caught:
            urllib.request.urlopen(request, timeout=5)
        self.assertEqual(caught.exception.code, 403)
        payload = json.loads(caught.exception.read().decode())
        self.assertFalse(payload["ok"])
        self.assertIn("token", payload["error"])

    def test_malformed_json_is_400(self):
        request = urllib.request.Request(self.url("/op"), data=b"{not json",
                                         headers={"Content-Type": "application/json"})
        with self.assertRaises(urllib.error.HTTPError) as caught:
            urllib.request.urlopen(request, timeout=5)
        self.assertEqual(caught.exception.code, 400)

    def test_a_refused_op_is_http_200_with_ok_false(self):
        response = editor_http.call({"op": "nonsense"}, endpoint=self.endpoint)
        self.assertFalse(response["ok"])
        self.assertIn("unknown op", response["error"])

    def test_shutdown_stops_the_loop_and_removes_the_endpoint(self):
        response = editor_http.call({"op": "shutdown"}, endpoint=self.endpoint)
        self.assertTrue(response["ok"], response["error"])
        self.thread.join(10)
        self.assertFalse(self.thread.is_alive(), "the serve loop did not stop")
        self.assertFalse(self.endpoint.exists())
        with self.assertRaises(editor_http.EditorUnavailable):
            editor_http.call({"op": "status"}, endpoint=self.endpoint)

    def test_a_stale_endpoint_says_so(self):
        editor_http.clear_endpoint(self.endpoint)
        with self.assertRaises(editor_http.EditorUnavailable) as caught:
            editor_http.call({"op": "status"}, endpoint=self.endpoint)
        self.assertIn("no editor endpoint", str(caught.exception))


class EndpointFileTests(unittest.TestCase):
    def test_read_refuses_a_foreign_protocol(self):
        directory = Path(tempfile.mkdtemp(prefix="levelbuild-endpoint-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(directory, True))
        path = directory / "editor.json"
        path.write_text(json.dumps({"protocol": 99, "host": "127.0.0.1", "port": 1}))
        with self.assertRaises(editor_http.EditorUnavailable) as caught:
            editor_http.read_endpoint(path)
        self.assertIn("protocol", str(caught.exception))

    def test_clear_only_removes_the_matching_pid(self):
        directory = Path(tempfile.mkdtemp(prefix="levelbuild-endpoint-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(directory, True))
        path = directory / "editor.json"
        editor_http.write_endpoint(path, {"pid": 1234, "host": "127.0.0.1", "port": 1})
        self.assertFalse(editor_http.clear_endpoint(path, only_pid=999))
        self.assertTrue(path.exists())
        self.assertTrue(editor_http.clear_endpoint(path, only_pid=1234))
        self.assertFalse(path.exists())


if __name__ == "__main__":
    unittest.main()
