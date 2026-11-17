"""The web editor's read-only v2 path (``katamari.placement/2``).

The rules that fail silently if they regress:

* ``_ref_*`` opens ``placement.v2.json`` first, and a loaded v1 file reports itself as
  legacy instead of showing the v1 validator's 544 errors as if they were v2's;
* v2 loads through ``placement_v2.read_sidecar`` (the one codec) and
  ``ref_v2_from_extract.structural_errors`` (the one structural checker), and its
  ``check`` is labelled structure-only, never the v1 validator;
* the seven view layers have an HTTP spelling each and the GUI toggles use the same
  Python data; rotation/scale off is a display transform only;
* v2 is read-only: mutating ops do not write and do not enter the v1 editor.

The fixture is the format's own tiny valid document (the §7.1 pad), converted to the
sidecar form the reference level uses, so this runs without the unlicensed extraction.
"""

import copy
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

from Docs.tests.levelbuild.test_placement_v2 import tiny_document  # noqa: E402
from Docs.tests.levelbuild.test_ref_stage_visual import synthetic_glb  # noqa: E402
from Tools.levelbuild import ref_stage  # noqa: E402
from Tools.levelbuild import ref_stage_visual  # noqa: E402
from Tools.levelbuild import placement_v2  # noqa: E402
from Tools.leveleditor import server as web  # noqa: E402
from Tools.leveleditor import v2_document  # noqa: E402

TOKEN = "test-v2-token"


def v2_document_with_sidecar():
    """The tiny valid v2 document in the same sidecar form as the real level."""
    document = tiny_document()
    terrain = document["terrain"]
    payload, width = placement_v2.sidecar_bytes({
        "vertices": terrain["vertices"],
        "indices": terrain["indices"],
        "zoneIds": terrain["triangles"]["zoneIds"],
        "classes": terrain["triangles"]["classes"],
    })
    digest = __import__("hashlib").sha256(payload).hexdigest()
    document["terrain"].pop("vertices")
    document["terrain"].pop("indices")
    document["terrain"].pop("triangles")
    document["terrain"]["triangles"] = {"count": 2, "zoneCounts": {"0": 2}}
    document["terrain"]["meshRef"] = {
        "path": "terrain/tiny.katerrain",
        "sha256": digest,
        "vertexCount": 4,
        "indexCount": 6,
        "triangleCount": 2,
        "indexWidth": width,
        "zoneIds": True,
        "classes": True,
    }
    return document, payload


def write_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


class V2Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="leveleditor-v2-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.levels = self.tmp / "levels"
        self.models = self.tmp / "KatamariRef" / "models"
        self.level_id = "_ref_demo"
        folder = self.levels / self.level_id
        self.document, sidecar = v2_document_with_sidecar()
        self.document["meta"]["id"] = self.level_id
        self.document["meta"]["arena"] = {"id": self.level_id}
        write_json(folder / "placement.v2.json", self.document)
        (folder / "terrain").mkdir(parents=True)
        (folder / "terrain" / "tiny.katerrain").write_bytes(sidecar)
        # A reference level has its own manifests and a conversion table; both are
        # intentionally small here.
        write_json(folder / "manifests" / "acorn.manifest.json", {
            "id": "acorn", "category": "reference", "pivot": "center",
            "source": "tiny.glb",
            "bounds_m": {"min": [-0.012, -0.015, -0.0114],
                         "max": [0.012, 0.015, 0.0114]},
            "dimensions_m": {"x": 0.024, "y": 0.03, "z": 0.0228, "longest": 0.03},
        })
        write_json(folder / "original_defs.json", {
            "defs": [{"id": "acorn", "modelFile": "tiny.glb"}]
        })
        (self.models / "glb-pack").mkdir(parents=True)
        (self.models / "glb-pack" / "tiny.glb").write_bytes(b"glTF-fake-acorn")
        self.state_dir = self.tmp / "state"

    def open_v2(self):
        return web.WebEditor(self.level_id, levels_dir=self.levels,
                             state_dir=self.state_dir, lock=False,
                             ref_models_dir=self.models)


class V2LoadTests(V2Fixture):
    def test_default_open_prefers_v2_and_structure_check_is_not_v1(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        self.assertTrue(app.v2_mode)
        state = app.state()
        self.assertEqual(state["v2"]["schema"], v2_document.SCHEMA_V2)
        self.assertTrue(state["file"]["path"].endswith("placement.v2.json"))
        self.assertEqual(state["check"]["kind"], "structural")
        self.assertEqual(state["check"]["label"], "结构检查（v2）")
        self.assertEqual(state["check"]["errorCount"], 0)
        # The v1 path would have said "stale absorbDiameterReq"; v2 must not.
        self.assertFalse(any("absorbDiameterReq" in error
                             for error in state["check"]["errors"]))
        self.assertIsNone(state["legacy"])

    def test_v2_state_carries_the_layers_the_viewer_needs(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        state = app.state()
        v2 = state["v2"]
        self.assertEqual(v2["counts"]["props"], 1)
        self.assertEqual(v2["counts"]["solids"], 0)
        self.assertEqual(v2["counts"]["walls"], 1)
        self.assertEqual(v2["counts"]["terrainTriangles"], 2)
        self.assertEqual(v2["counts"]["terrainVertices"], 4)
        self.assertEqual(v2["activity"]["killPlane"]["y"], -3.0)
        self.assertEqual(v2["parents"], [])
        self.assertEqual(set(v2["layers"]), {
            "v2Terrain", "v2Collision", "v2Solids", "v2Walls", "v2KillPlane",
            "v2Parents", "v2Rotation", "v2Scale"})
        # The view document keeps the existing client's list shape, but it is not a
        # v1 document: no v1 schema, and no v1 validator ever runs on it.
        self.assertEqual(state["doc"]["schema"], v2_document.SCHEMA_V2)
        self.assertEqual(state["doc"]["props"][0]["aabb"], [0.024, 0.03, 0.0228])

    def test_every_v2_view_toggle_has_an_http_spelling(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        layers = v2_document.layer_map(app.v2_document)
        self.assertTrue(layers["v2Terrain"]["http"].startswith("/api/v2/"))
        self.assertTrue(layers["v2Solids"]["http"].startswith("/api/v2/"))
        self.assertTrue(layers["v2Walls"]["http"].startswith("/api/v2/"))
        self.assertTrue(layers["v2KillPlane"]["http"].startswith("/api/v2/"))
        self.assertTrue(layers["v2Parents"]["http"].startswith("/api/v2/"))
        self.assertIn("rotation=0", layers["v2Rotation"]["http"])
        self.assertIn("scale=0", layers["v2Scale"]["http"])
        payload = app.v2_payload(terrain=False, solids=False, walls=False, kill=False,
                                 parents=False, rotation=False, scale=False, props=True)
        prop = payload["props"][0]
        self.assertEqual(prop["rotationQuat"], [0.0, 0.0, 0.0, 1.0])
        self.assertEqual(prop["scale"], [1.0, 1.0, 1.0])
        self.assertIsNone(payload["terrain"])
        self.assertIsNone(payload["solids"])
        self.assertIsNone(payload["parents"])

    def test_v2_is_read_only_and_never_runs_the_v1_editor(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        path = self.levels / self.level_id / "placement.v2.json"
        before = path.read_bytes()
        for payload in (
            {"op": "move", "args": {"id": "acorn_01", "to": [0, 0, 0]}},
            {"op": "export", "args": {}},
            {"op": "save"},
            {"ops": [{"op": "remove", "args": {"id": "acorn_01"}}]},
        ):
            response = app.handle(payload)
            self.assertFalse(response["ok"], payload)
            self.assertIn("read-only", response["error"])
        self.assertEqual(path.read_bytes(), before)

    def test_opening_another_level_after_v2_does_not_reuse_the_v2_filename(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        folder = self.levels / "normal_demo"
        example = ROOT / "Tools/levelbuild/examples/placement.json"
        legacy = json.loads(example.read_text(encoding="utf-8"))
        legacy["level"]["id"] = "normal_demo"
        write_json(folder / "placement.json", legacy)
        app.open_level("normal_demo")
        self.assertFalse(app.v2_mode)
        self.assertTrue(Path(app.editor.path).name == "placement.json")
        self.assertIsNone(app.state()["v2"])

    def test_v2_read_ops_bypass_the_v1_editor(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        listed = app.handle({"op": "list", "args": {"limit": 1}})
        self.assertTrue(listed["ok"], listed)
        self.assertEqual(listed["result"]["total"], 1)
        got = app.handle({"op": "get", "args": {"id": "acorn_01"}})
        self.assertTrue(got["ok"], got)
        self.assertEqual(got["result"]["type"], "prop")
        unknown = app.handle({"op": "frobnicate", "args": {}})
        self.assertFalse(unknown["ok"])
        self.assertIn("read-only", unknown["error"])

    def test_a_v1_file_with_a_v2_sibling_is_flagged_as_legacy(self):
        # Copy the repository's v1 example beside the v2 fixture and open it explicitly.
        example = ROOT / "Tools/levelbuild/examples/placement.json"
        legacy = json.loads(example.read_text(encoding="utf-8"))
        legacy["level"]["id"] = self.level_id
        write_json(self.levels / self.level_id / "placement.model.json", legacy)
        app = web.WebEditor(self.level_id, levels_dir=self.levels,
                            state_dir=self.state_dir, lock=False,
                            ref_models_dir=self.models,
                            placement="placement.model.json")
        self.addCleanup(app.close)
        state = app.state()
        self.assertIsNone(state["v2"])
        legacy_state = state["legacy"]
        self.assertIsNotNone(legacy_state)
        self.assertEqual(legacy_state["replacedBy"], "placement.v2.json")
        self.assertTrue(legacy_state["canSwitch"])
        self.assertIn("v1 旧文档", legacy_state["message"])
        # The switch the GUI button performs is the same open path.
        app.open_level(self.level_id, placement="placement.v2.json")
        self.assertTrue(app.v2_mode)
        self.assertIsNone(app.legacy_state())


class V2Server(V2Fixture):
    """A fixture with a running editor HTTP server and a JSON GET helper."""

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
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read())


class V2HTTPTests(V2Server):
    def test_v2_endpoints_return_each_layer(self):
        app = self.open_v2()
        base = self.start(app)
        status, layers = self.get(base, "/api/v2/layers")
        self.assertEqual(status, 200)
        self.assertEqual(layers["layers"]["v2Terrain"]["http"], "/api/v2/terrain")
        status, terrain = self.get(base, "/api/v2/terrain")
        self.assertEqual(status, 200)
        self.assertEqual(terrain["terrain"]["counts"]["triangles"], 2)
        status, solids = self.get(base, "/api/v2/solids")
        self.assertEqual(status, 200)
        self.assertEqual(solids["count"], 0)
        status, activity = self.get(base, "/api/v2/activity")
        self.assertEqual(status, 200)
        self.assertEqual(len(activity["activity"]["walls"]), 1)
        status, parents = self.get(base, "/api/v2/parents")
        self.assertEqual(status, 200)
        self.assertEqual(parents["count"], 0)
        status, props = self.get(base, "/api/v2/props?rotation=0&scale=0")
        self.assertEqual(status, 200)
        self.assertEqual(props["props"][0]["rotationQuat"], [0.0, 0.0, 0.0, 1.0])
        status, view = self.get(base, "/api/v2/view?terrain=0&solids=0&walls=0&kill=0&parents=0&props=0")
        self.assertEqual(status, 200)
        self.assertIsNone(view["terrain"])
        self.assertIsNone(view["solids"])
        self.assertIsNone(view["activity"])

    def test_check_endpoint_is_structural_only(self):
        app = self.open_v2()
        base = self.start(app)
        status, payload = self.get(base, "/api/check")
        self.assertEqual(status, 200)
        self.assertEqual(payload["result"]["kind"], "structural")
        self.assertEqual(payload["result"]["label"], "结构检查（v2）")
        self.assertEqual(payload["result"]["errorCount"], 0)

    def test_a_normal_level_cannot_get_v2_layers(self):
        folder = self.levels / "normal_demo"
        example = ROOT / "Tools/levelbuild/examples/placement.json"
        legacy = json.loads(example.read_text(encoding="utf-8"))
        legacy["level"]["id"] = "normal_demo"
        write_json(folder / "placement.json", legacy)
        app = web.WebEditor("normal_demo", levels_dir=self.levels,
                            state_dir=self.state_dir, lock=False)
        base = self.start(app)
        status, _payload = self.get(base, "/api/v2/terrain")
        self.assertEqual(status, 404)



class V2VisualTests(V2Server):
    """``terrain.visual``: the page gets the textured stage as a GLB, the collision as an overlay."""

    def setUp(self):
        super().setUp()
        folder = self.levels / self.level_id
        self.built = ref_stage_visual.convert(synthetic_glb())
        terrain = {}
        for key, value in self.document["terrain"].items():
            if key == "source":
                terrain["visual"] = self.built["block"]
            terrain[key] = value
        self.document["terrain"] = terrain
        write_json(folder / "placement.v2.json", self.document)
        (folder / "terrain").mkdir(parents=True, exist_ok=True)
        (folder / self.built["block"]["meshRef"]["path"]).write_bytes(self.built["sidecar"])
        for relative, blob in self.built["textures"].items():
            target = folder / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(blob)

    def test_the_visual_is_loaded_summarised_and_valid(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        v2 = app.state()["v2"]
        self.assertEqual([e for e in app.v2_document.errors if "visual" in e], [])
        self.assertTrue(v2["visual"]["available"])
        self.assertEqual([part["role"] for part in v2["visual"]["parts"]],
                         ["stage", "stage", "sky"])
        self.assertEqual(v2["counts"]["visualParts"], 3)
        layers = v2["layers"]
        self.assertEqual(layers["v2Terrain"]["http"], "/api/v2/visual.glb")
        self.assertEqual(layers["v2Collision"]["http"], "/api/v2/terrain")
        self.assertFalse(layers["v2Collision"]["default"])

    def test_the_glb_is_mirrored_with_the_winding_reversed(self):
        app = self.open_v2()
        self.addCleanup(app.close)
        glb = app.v2_document.visual_glb()
        document, binary = ref_stage.read_glb(glb)
        self.assertEqual([node["name"] for node in document["nodes"]],
                         ["area2_sector0_model0", "area2_sector0_model1",
                          "area2_sector1_model0"])
        self.assertEqual(document["nodes"][2]["extras"]["role"], "sky")
        self.assertEqual(document["buffers"], [{"byteLength": len(binary)}],
                         "a GLB loader needs the buffer entry in the JSON chunk")
        self.assertEqual(len(document["textures"]), 3)
        modes = {m["name"]: m.get("alphaMode", "OPAQUE") for m in document["materials"]}
        self.assertEqual(modes, {"t000": "OPAQUE", "t001": "MASK", "t002": "BLEND"})
        self.assertFalse(any(m["doubleSided"] for m in document["materials"]))
        primitive = document["meshes"][0]["primitives"][0]
        positions = ref_stage.accessor_values(document, binary,
                                              primitive["attributes"]["POSITION"])
        indices = [v[0] for v in ref_stage.accessor_values(document, binary,
                                                           primitive["indices"])]
        # the file's z is negated for three.js ...
        file_vertices = app.v2_document.visual_sidecar["vertices"]
        self.assertAlmostEqual(positions[2][2], -file_vertices[2][2], places=6)
        # ... so the winding is reversed and a floor that faces up in the file still faces up
        a, b, c = (positions[i] for i in indices[:3])
        cross_y = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2])
        self.assertGreater(cross_y, 0.0)

    def test_the_endpoint_serves_the_glb_and_a_level_without_visual_has_none(self):
        app = self.open_v2()
        base = self.start(app)
        request = urllib.request.Request(base + "/api/v2/visual.glb")
        request.add_header(web.TOKEN_HEADER, TOKEN)
        with urllib.request.urlopen(request, timeout=10) as response:
            self.assertEqual(response.headers["Content-Type"], "model/gltf-binary")
            self.assertEqual(response.read()[:4], b"glTF")
        self.assertEqual(self.get(base, "/api/v2/layers")[1]["layers"]["v2Terrain"]["http"],
                         "/api/v2/visual.glb")

    def test_a_visual_whose_sidecar_hash_is_wrong_is_reported(self):
        folder = self.levels / self.level_id
        document = json.loads((folder / "placement.v2.json").read_text(encoding="utf-8"))
        document["terrain"]["visual"]["meshRef"]["sha256"] = "0" * 64
        write_json(folder / "placement.v2.json", document)
        app = self.open_v2()
        self.addCleanup(app.close)
        self.assertTrue(any("[terrain_visual_sidecar]" in e for e in app.v2_document.errors))


class V2NoVisualTests(V2Server):
    def test_a_document_without_visual_has_no_glb(self):
        app = self.open_v2()
        base = self.start(app)
        request = urllib.request.Request(base + "/api/v2/visual.glb")
        request.add_header(web.TOKEN_HEADER, TOKEN)
        with self.assertRaises(urllib.error.HTTPError) as caught:
            urllib.request.urlopen(request, timeout=10)
        self.assertEqual(caught.exception.code, 404)
        self.assertFalse(app.state()["v2"]["visual"]["available"])


if __name__ == "__main__":
    unittest.main()
