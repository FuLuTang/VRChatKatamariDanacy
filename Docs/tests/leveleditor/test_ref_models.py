"""Reference-level resolution + refusal rules (no Blender, no browser).

``_ref_*`` levels are git-ignored reference data: positions from a community data
mine, meshes ripped from a commercial game.  These tests pin the safety rules that
fail silently if they regress:

* only a ``_ref_*`` id may see the local reference meshes, and only a models root
  outside the repository is accepted;
* a ref level opens its converted ``placement.model.json`` and its own manifests;
* the def's Unity ``model`` is resolved through the level's conversion table to a
  local GLB, never through ``Docs/Art/OriginalModels/props/exports``;
* every edit/export is refused, and the file stays byte-identical;
* a normal level can never serve a reference mesh through ``/api/models``;
* ``group``/``groupId`` parent links are surfaced for the viewer.
"""

import json
import shutil
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

from Tools.levelbuild import ref_models  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.leveleditor import server as web  # noqa: E402

TOKEN = "test-ref-token"


def write_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


def level_doc(level_id="_ref_demo"):
    return {
        "schema": core.SCHEMA,
        "level": {
            "id": level_id,
            "groundY": 0.0,
            "ballStart": [0.0, 0.1, 0.0],
            "ballDiameterStart": 0.30,
            "ballDiameterMax": 2.20,
            "bounds": [[-2.0, -2.0], [2.0, 2.0]],
            "expected": {"propCount": 2, "valueUnits": 2},
        },
        "defs": [{
            "id": "crate",
            "kind": "reference",
            "model": "Assets/_RefUnlicensed/Models/crate/crate.fbx",
            "valueUnits": 1,
        }],
        "props": [
            {"itemId": "crate_001", "def": "crate", "kind": "reference",
             "position": [0.5, 0.1, 0.5], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": [0.2, 0.2, 0.2], "valueUnits": 1,
             "group": "crate_002", "groupId": "crate_002"},
            {"itemId": "crate_002", "def": "crate", "kind": "reference",
             "position": [-0.5, 0.1, 0.5], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": [0.2, 0.2, 0.2], "valueUnits": 1},
        ],
        "structures": [{
            "id": "ground", "kind": "reference", "role": "ground", "shape": "plane",
            "center": [0.0, 0.0, 0.0], "size": [4.0, 0.04, 4.0],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "support": True,
        }],
    }


def crate_manifest():
    return {
        "id": "crate",
        "category": "reference",
        "pivot": "center",
        "bounds_m": {"min": [-0.1, -0.1, -0.1], "max": [0.1, 0.1, 0.1]},
        "dimensions_m": {"x": 0.2, "y": 0.2, "z": 0.2, "longest": 0.2},
        "source": "0001_Crate.glb",
    }


class ReferenceFixture(unittest.TestCase):
    """A tiny git-ignored reference level plus a fake local mesh tree."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="leveleditor-ref-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.levels = self.tmp / "levels"
        self.models = self.tmp / "KatamariRef" / "models"
        self.level_id = "_ref_demo"
        folder = self.levels / self.level_id
        self.doc = level_doc(self.level_id)
        write_json(folder / "placement.model.json", self.doc)
        write_json(folder / "placement.json",
                   dict(self.doc, level=dict(self.doc["level"], id=self.level_id)))
        write_json(folder / "manifests" / "crate.manifest.json", crate_manifest())
        write_json(folder / "original_defs.json", {
            "defs": [{"id": "crate", "modelFile": "0001_Crate.glb",
                      "convertedModel": "Assets/_RefUnlicensed/Models/crate/crate.fbx"}]
        })
        (self.models / "glb-pack").mkdir(parents=True)
        self.glb = self.models / "glb-pack" / "0001_Crate.glb"
        self.glb.write_bytes(b"glTF-fake-crate")
        self.state_dir = self.tmp / "state"

    def open_editor(self, **kwargs):
        return web.WebEditor(self.level_id, levels_dir=self.levels,
                             state_dir=self.state_dir, lock=False,
                             ref_models_dir=self.models, **kwargs)


class ResolveModelTests(ReferenceFixture):
    def test_ref_level_detection_is_the_prefix_only(self):
        self.assertTrue(ref_models.is_ref_level("_ref_cancer"))
        self.assertTrue(ref_models.is_ref_level("_ref_mas1"))
        self.assertFalse(ref_models.is_ref_level("story_01_bench_size"))
        self.assertFalse(ref_models.is_ref_level("ref_cancer"))
        self.assertFalse(ref_models.is_ref_level(None))

    def test_models_root_must_exist_and_stay_outside_the_repository(self):
        self.assertEqual(ref_models.normalized_models_dir(self.models),
                         self.models.resolve())
        with self.assertRaises(ref_models.RefAssetError):
            ref_models.normalized_models_dir(self.tmp / "missing")
        with self.assertRaises(ref_models.RefAssetError):
            ref_models.normalized_models_dir(core.ROOT / "Docs")
        with self.assertRaises(ref_models.RefAssetError):
            ref_models.normalized_models_dir(core.ROOT)

    def test_the_level_conversion_table_maps_a_def_to_its_source_glb(self):
        table = ref_models.load_model_table(self.levels / self.level_id)
        self.assertEqual(table, {"crate": "0001_Crate.glb"})
        resolved = ref_models.resolve_model(
            "crate", "Assets/_RefUnlicensed/Models/crate/crate.fbx", table, self.models)
        self.assertEqual(resolved["kind"], "glb")
        self.assertEqual(resolved["source"], "glb-pack")
        self.assertEqual(Path(resolved["path"]), self.glb.resolve())
        self.assertIsNone(ref_models.resolve_model("unknown", None, table, self.models))

    def test_viewer_cache_and_convertible_fbx_are_the_fallbacks(self):
        cache = self.models / "viewer_glb" / "crate.glb"
        cache.parent.mkdir()
        cache.write_bytes(b"converted")
        (self.models / "glb-pack" / "0001_Crate.glb").unlink()
        resolved = ref_models.resolve_model("crate", None, {}, self.models)
        self.assertEqual(resolved["source"], "viewer_glb")
        cache.unlink()
        fbx = self.models / "converted_local" / "crate" / "crate.fbx"
        fbx.parent.mkdir(parents=True)
        fbx.write_bytes(b"fbx")
        resolved = ref_models.resolve_model("crate", None, {}, self.models)
        self.assertEqual(resolved["kind"], "fbx")
        self.assertEqual(resolved["source"], "converted_local")

    def test_parent_links_resolve_only_existing_parents(self):
        links = ref_models.parent_links(self.doc)
        self.assertEqual(links, [{"child": "crate_001", "parent": "crate_002",
                                  "def": "crate"}])
        orphan = json.loads(json.dumps(self.doc))
        orphan["props"][0]["group"] = "absent"
        self.assertEqual(ref_models.parent_links(orphan), [])


class RefLevelOpenTests(ReferenceFixture):
    def test_ref_level_opens_the_model_placement_with_its_own_manifests(self):
        app = self.open_editor()
        self.addCleanup(app.close)
        self.assertTrue(app.ref_mode)
        state = app.state()
        self.assertIn("placement.model.json", state["file"]["path"])
        self.assertEqual(state["counts"]["props"], 2)
        self.assertEqual(state["ref"]["modelCounts"],
                         {"total": 1, "resolved": 1, "convertible": 0, "missing": 0,
                          "bySource": {"glb-pack": 1}})
        self.assertEqual(state["ref"]["parentLinkCount"], 1)
        self.assertTrue(state["ref"]["readOnly"])
        self.assertIn("UNLICENSED", state["ref"]["banner"])
        self.assertEqual(state["check"]["warningCount"], 0)
        self.assertTrue(state["manifests"]["crate"]["refModel"])
        self.assertEqual(state["manifests"]["crate"]["viewerPivot"], "bottom")

    def test_normal_level_never_gets_the_reference_mode(self):
        folder = self.levels / "normal_demo"
        write_json(folder / "placement.json", level_doc("normal_demo"))
        app = web.WebEditor("normal_demo", levels_dir=self.levels,
                            state_dir=self.state_dir, lock=False,
                            ref_models_dir=self.models)
        self.addCleanup(app.close)
        self.assertFalse(app.ref_mode)
        self.assertIsNone(app.state()["ref"])
        self.assertEqual(app.ref_resolution, {})

    def test_every_edit_and_export_is_refused_and_the_file_is_unchanged(self):
        app = self.open_editor()
        self.addCleanup(app.close)
        path = Path(self.levels / self.level_id / "placement.model.json")
        before = path.read_bytes()
        for payload in (
            {"op": "move", "args": {"id": "crate_001", "to": [0.0, 0.1, 0.0]}},
            {"op": "set", "args": {"id": "crate_001", "valueUnits": 9}},
            {"op": "remove", "args": {"id": "crate_001"}},
            {"op": "export", "args": {"to": "Docs/Art/OriginalModels/props/exports/x.glb"}},
            {"op": "save"},
            {"op": "add_def", "args": {"id": "crate", "kind": "reference"}},
            {"ops": [{"op": "move", "args": {"id": "crate_001", "to": [0.0, 0.1, 0.0]}}]},
        ):
            response = app.handle(payload)
            self.assertFalse(response["ok"], payload)
            self.assertIn("UNLICENSED REFERENCE level is read-only", response["error"])
        self.assertEqual(path.read_bytes(), before)
        self.assertFalse((core.ROOT / "Docs/Art/OriginalModels/props/exports/x.glb").exists())


class ModelServingTests(ReferenceFixture):
    """Over a real socket: a ref mesh is reachable only while a ref level is open."""

    def start(self, app):
        httpd = web.make_http_server(app, "127.0.0.1", 0, token=TOKEN,
                                     token_path=self.state_dir / "token")
        thread = threading.Thread(target=httpd.serve_forever,
                                  kwargs={"poll_interval": 0.02}, daemon=True)
        thread.start()

        def stop():
            httpd.stop_event.set()
            httpd.shutdown()
            app.close()
            httpd.server_close()
            thread.join(5)

        self.addCleanup(stop)
        return "http://127.0.0.1:%d" % httpd.server_address[1]

    def get(self, base, path):
        request = urllib.request.Request(base + path)
        request.add_header(web.TOKEN_HEADER, TOKEN)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(), error.headers

    def test_the_ref_level_serves_the_local_glb_with_its_warning_header(self):
        app = self.open_editor()
        base = self.start(app)
        status, body, headers = self.get(base, "/api/models/crate.glb")
        self.assertEqual(status, 200)
        self.assertEqual(body, self.glb.read_bytes())
        self.assertEqual(headers.get("X-Unlicensed-Reference"), "1")
        status, _body, _headers = self.get(base, "/api/models/absent.glb")
        self.assertEqual(status, 404)
        status, _body, _headers = self.get(base, "/api/models/..%2F..%2Fetc%2Fpasswd.glb")
        self.assertEqual(status, 404)

    def test_a_normal_level_cannot_serve_the_reference_mesh(self):
        folder = self.levels / "normal_demo"
        write_json(folder / "placement.json", level_doc("normal_demo"))
        app = web.WebEditor("normal_demo", levels_dir=self.levels,
                            state_dir=self.state_dir, lock=False,
                            ref_models_dir=self.models)
        base = self.start(app)
        status, body, _headers = self.get(base, "/api/models/crate.glb")
        self.assertEqual(status, 404)
        self.assertNotIn(b"glTF-fake-crate", body)


if __name__ == "__main__":
    unittest.main()
