"""The original stage of a reference level: frame, resolution and transforms.

``_ref_*`` levels are drawn from two git-ignored sources: the placement files (this
project's frame, metres) and the byte-level stage extraction (the PS2 frame,
centimetres).  These tests pin the pieces that would fail silently if they regress:

* the measured frame mapping ``reroll = -0.01 * ps2`` is *detected* from the data and
  the mirrored alternative (``+0.01``) never wins;
* every prop is paired with the instance it came from, and a record whose type has no
  def in this level (a dummy attach anchor) is reported, not dropped silently;
* the rotation is the record's axis with the **negated** angle, the scale is +0x30, and
  the parent comes from the parent table, never from the placement's ``group``;
* collision vertices are group-local, so the flat ``verts`` list has to be walked with
  the group layout; the rendered frame is the project frame with z negated;
* the extractor's GLB is converted once (z mirrored, winding flipped, bounds updated),
  and a stage written in any other space is refused instead of drawn mirrored;
* the server only serves the stage while a ``_ref_*`` level with an extraction is open,
  and a reference level still opens when the extraction is missing.
"""

import json
import math
import struct
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

from Tools.levelbuild import ref_stage  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.leveleditor import server as web  # noqa: E402

TOKEN = "test-stage-token"


# --------------------------------------------------------------------------- #
# fixtures
# --------------------------------------------------------------------------- #
def write_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=1) + "\n", encoding="utf-8")


def tiny_glb(positions, normals, indices, name="probe"):
    """A one-primitive GLB, laid out like the extractor's writer."""
    document = {
        "asset": {"version": "2.0", "generator": "test"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": name}],
        "meshes": [{"name": name, "primitives": [{
            "attributes": {"POSITION": 0, "NORMAL": 1},
            "indices": 2, "material": 0, "mode": 4}]}],
        "materials": [{"name": "probe"}],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions),
             "type": "VEC3",
             "min": [min(p[axis] for p in positions) for axis in range(3)],
             "max": [max(p[axis] for p in positions) for axis in range(3)]},
            {"bufferView": 1, "componentType": 5126, "count": len(normals), "type": "VEC3"},
            {"bufferView": 2, "componentType": 5125, "count": len(indices), "type": "SCALAR"},
        ],
        "bufferViews": [],
        "buffers": [],
    }
    blob = bytearray()
    for index, kind in ((0, "POSITION"), (1, "NORMAL"), (2, "INDICES")):
        while len(blob) % 4:
            blob.append(0)
        offset = len(blob)
        if kind == "INDICES":
            data = struct.pack("<%dI" % len(indices), *indices)
        else:
            values = positions if kind == "POSITION" else normals
            data = struct.pack("<%df" % (3 * len(values)),
                               *[value for point in values for value in point])
        blob.extend(data)
        document["bufferViews"].append(
            {"buffer": 0, "byteOffset": offset, "byteLength": len(data)})
    document["buffers"] = [{"byteLength": len(blob)}]
    payload = json.dumps(document, separators=(",", ":")).encode("utf-8")
    while len(payload) % 4:
        payload += b" "
    while len(blob) % 4:
        blob.append(0)
    total = 12 + 8 + len(payload) + 8 + len(blob)
    out = bytearray(b"glTF")
    out += struct.pack("<II", 2, total)
    out += struct.pack("<I", len(payload)) + b"JSON" + payload
    out += struct.pack("<I", len(blob)) + b"BIN\0" + bytes(blob)
    return bytes(out)


def level_document(level_id="_ref_demo"):
    return {
        "schema": core.SCHEMA,
        "level": {
            "id": level_id, "groundY": 0.0, "ballStart": [0.0, 0.1, 0.0],
            "ballDiameterStart": 0.30, "ballDiameterMax": 2.20,
            "bounds": [[-2.0, -2.0], [2.0, 2.0]],
            "expected": {"propCount": 2, "valueUnits": 2},
        },
        "defs": [{"id": "crate", "kind": "reference",
                  "model": "Assets/_RefUnlicensed/Models/crate/crate.fbx",
                  "valueUnits": 1}],
        "props": [
            {"itemId": "crate_001", "def": "crate", "kind": "reference",
             "position": [1.0, 0.1, 2.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": [0.2, 0.2, 0.2], "valueUnits": 1, "group": "crate_002",
             "groupId": "crate_002"},
            {"itemId": "crate_002", "def": "crate", "kind": "reference",
             "position": [-1.5, 0.1, 0.5], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": [0.2, 0.2, 0.2], "valueUnits": 1},
        ],
        "structures": [{
            "id": "ground", "kind": "reference", "role": "ground", "shape": "plane",
            "center": [0.0, 0.0, 0.0], "size": [4.0, 0.04, 4.0],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "support": True,
        }],
    }


def instance_record(table_index, name_idx, position, axis=(0.0, 1.0, 0.0),
                    angle=0.0, scale=(1.0, 1.0, 1.0), parent=None):
    """One 0x40-byte spawn record as ``instances.json`` writes it (raw centimetres)."""
    return {
        "nameIdx": name_idx, "locPosType": 0, "randomGroupNo": -1, "moveType": -1,
        "hitOnAreaNo": 2, "linkAction": 0, "twistNo": -1,
        "t": list(position), "w": 1.0,
        "axis": list(axis), "angleRaw": angle, "scale": list(scale),
        "q": [0.0, 0.0, 0.0, 1.0], "tableIndex": table_index, "area": 2,
        "parentTableIndex": parent, "parentExpected": parent is not None,
    }


def raw_position(world):
    """The PS2 centimetre triple for a REROLL/Unity metre position."""
    return [-value * 100.0 for value in world]


def stage_directory(root, space="reroll"):
    """A complete, tiny extraction output directory."""
    directory = Path(root) / "kse_out"
    directory.mkdir(parents=True, exist_ok=True)
    records = [
        instance_record(0, 7, raw_position((1.0, 0.1, 2.0)),
                        angle=-math.pi / 2.0, parent=1),
        instance_record(1, 7, raw_position((-1.5, 0.1, 0.5)),
                        scale=(2.0, 2.0, 2.0)),
        instance_record(2, 99, raw_position((0.0, 0.0, 0.0))),
    ]
    write_json(directory / "instances.json", {
        "level": 11, "firstArea": 2, "levelParams": {}, "areas": [],
        "instances": records,
    })
    write_json(directory / "report.json", {
        "level": 11, "space": space,
        "mesh": {"vertices": 3, "indices": 3, "materials": 1, "glbBytes": 120},
    })
    write_json(directory / "zones.json", [{
        "bbox": {"min": [-200.0, 0.0, -200.0], "max": [200.0, 0.0, 200.0]},
        "verts": [], "is_strip": [False],
    }])
    # One collision entry: a 4-vertex strip (2 triangles) and a 3-vertex polygon
    # (1 triangle), all at world y = 0, spanning the props' x/z.
    strip = [(2.0, 0.0, 3.0), (2.0, 0.0, 1.0), (-2.0, 0.0, 3.0), (-2.0, 0.0, 1.0)]
    polygon = [(-1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)]
    values = []
    for world in strip + polygon:
        values.extend(raw_position(world)[:3])
        values.append(0.0)
    write_json(directory / "collision_area2.json", [[{
        "bbox": {"min": [-200.0, 0.0, -300.0], "max": [200.0, 0.0, 300.0]},
        "groups": [{"is_strip": True, "vertex_count": 4, "zones": [0, 0, 0, 0]},
                   {"is_strip": False, "vertex_count": 3, "zones": [3, 3, 3]}],
        "triangles": [[0, 1, 2, 0], [1, 3, 2, 0], [0, 1, 2, 3]],
        "verts": values,
    }]])
    # A flat quad at y = 0 covering the props: the surface they are supposed to stand on.
    (directory / "stage.glb").write_bytes(tiny_glb(
        [(-3.0, 0.0, -3.0), (3.0, 0.0, -3.0), (3.0, 0.0, 3.0), (-3.0, 0.0, 3.0)],
        [(0.0, 1.0, 0.0)] * 4,
        [0, 1, 2, 0, 2, 3]))
    return directory


def level_directory(root, level_id="_ref_demo"):
    folder = Path(root) / "levels" / level_id
    document = level_document(level_id)
    write_json(folder / "placement.model.json", document)
    write_json(folder / "placement.json", document)
    write_json(folder / "manifests" / "crate.manifest.json", {
        "id": "crate", "category": "reference", "pivot": "center",
        "bounds_m": {"min": [-0.1, -0.1, -0.1], "max": [0.1, 0.1, 0.1]},
        "dimensions_m": {"x": 0.2, "y": 0.2, "z": 0.2, "longest": 0.2},
        "source": "0001_Crate.glb",
    })
    write_json(folder / "original_defs.json", {
        "defs": [{"id": "crate", "mono": 7, "modelFile": "0001_Crate.glb"}]
    })
    return folder


# --------------------------------------------------------------------------- #
# frames
# --------------------------------------------------------------------------- #
class FrameTests(unittest.TestCase):
    def test_reroll_is_minus_one_percent_of_the_ps2_centimetres(self):
        self.assertEqual(ref_stage.reroll_from_raw([100.0, -50.0, 25.0]),
                         [-1.0, 0.5, -0.25])
        self.assertEqual(ref_stage.REROLL_FROM_RAW, -0.01)

    def test_the_viewer_frame_is_the_project_frame_with_z_negated(self):
        self.assertEqual(ref_stage.reroll_to_viewer([1.0, 2.0, 3.0]), [1.0, 2.0, -3.0])
        self.assertEqual(ref_stage.raw_to_viewer([-100.0, -200.0, 300.0]),
                         [1.0, 2.0, 3.0])

    def test_a_raw_box_swaps_its_bounds_into_the_viewer_frame(self):
        low, high = ref_stage.raw_box_to_viewer(
            {"min": [-300.0, -200.0, -100.0], "max": [100.0, 0.0, 200.0]})
        self.assertEqual(low, [-1.0, 0.0, -1.0])
        self.assertEqual(high, [3.0, 2.0, 2.0])


# --------------------------------------------------------------------------- #
# locating the extraction
# --------------------------------------------------------------------------- #
class LocateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refstage-locate-"))
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)

    def test_a_missing_directory_is_reported_not_raised(self):
        directory, reason = ref_stage.resolve_stage_dir(self.tmp / "absent")
        self.assertIsNone(directory)
        self.assertIn("no stage extraction", reason)

    def test_a_directory_inside_the_repository_is_refused(self):
        with self.assertRaises(ref_stage.RefStageError):
            ref_stage.resolve_stage_dir(core.ROOT / "Docs")

    def test_a_complete_extraction_resolves_and_the_sibling_out_is_the_fallback(self):
        directory = stage_directory(self.tmp)
        resolved, reason = ref_stage.resolve_stage_dir(directory)
        self.assertEqual(resolved, directory.resolve())
        self.assertIsNone(reason)

    def test_the_older_sibling_directory_is_used_when_the_first_is_incomplete(self):
        older = self.tmp / "out"
        older.mkdir()
        for name in ("stage.glb", "instances.json", "report.json"):
            (older / name).write_bytes((stage_directory(self.tmp) / name).read_bytes())
        # The requested directory is the extractor's default name under `self.tmp`, so
        # its sibling `out/` is the candidate that actually holds a complete set.
        resolved, reason = ref_stage.resolve_stage_dir(self.tmp / "kse_absent")
        self.assertEqual(resolved, older.resolve())
        self.assertIsNone(reason)


# --------------------------------------------------------------------------- #
# instances -> props
# --------------------------------------------------------------------------- #
class InstanceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refstage-instances-"))
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)
        self.stage = stage_directory(self.tmp)
        self.doc = level_document()
        self.mono = ref_stage.mono_table(self.tmp)  # no original_defs next to it

    def test_the_def_table_reads_the_levels_own_conversion_file(self):
        folder = level_directory(self.tmp)
        self.assertEqual(ref_stage.mono_table(folder), {7: "crate"})

    def test_the_measured_frame_wins_over_the_mirrored_one(self):
        instances = ref_stage.load_instances(self.stage)
        frame = ref_stage.detect_frame(instances, self.doc, {7: "crate"})
        self.assertEqual(frame["scale"], -0.01)
        self.assertTrue(frame["confident"])
        self.assertEqual(frame["matched"], 2)
        self.assertLess(frame["medianError"], 1e-6)

    def test_a_rotation_is_the_axis_with_the_negated_stored_angle(self):
        # angleRaw = -pi/2 is the stored (negated) angle, so the applied rotation is
        # +90 degrees about +Y: quaternion (0, sin45, 0, cos45).
        record = instance_record(0, 7, [0, 0, 0], axis=(0.0, 1.0, 0.0),
                                 angle=-math.pi / 2.0)
        quat = ref_stage.instance_quaternion(record)
        self.assertAlmostEqual(quat[1], math.sqrt(0.5), places=6)
        self.assertAlmostEqual(quat[3], math.sqrt(0.5), places=6)
        self.assertAlmostEqual(ref_stage.angle_degrees(record), 90.0, places=6)
        # and it is the same rotation core.quat_axis_angle builds from the applied angle
        expected = core.quat_axis_angle((0.0, 1.0, 0.0), math.pi / 2.0)
        for got, want in zip(quat, expected):
            self.assertAlmostEqual(got, want, places=9)

    def test_an_empty_axis_is_the_identity(self):
        record = instance_record(0, 7, [0, 0, 0], axis=(0.0, 0.0, 0.0), angle=1.2)
        self.assertEqual(ref_stage.instance_quaternion(record), [0.0, 0.0, 0.0, 1.0])
        self.assertEqual(ref_stage.angle_degrees(record), 0.0)
        zero_with_identity_axis = instance_record(0, 7, [0, 0, 0],
                                                  axis=(0.0, 1.0, 0.0), angle=0.0)
        self.assertEqual(ref_stage.angle_degrees(zero_with_identity_axis), 0.0)

    def test_every_prop_is_paired_and_an_unknown_type_is_an_anchor(self):
        instances = ref_stage.load_instances(self.stage)
        matched = ref_stage.match_instances(self.doc, instances, -0.01, {7: "crate"})
        self.assertEqual(matched["counts"]["matched"], 2)
        self.assertEqual(matched["counts"]["anchors"], 1)
        self.assertEqual(matched["counts"]["unmatchedProps"], 0)
        pairs = {match["itemId"]: match["instance"]["tableIndex"]
                 for match in matched["matches"]}
        self.assertEqual(pairs, {"crate_001": 0, "crate_002": 1})
        self.assertAlmostEqual(matched["matches"][0]["distance"], 0.0, places=6)


# --------------------------------------------------------------------------- #
# collision + zones
# --------------------------------------------------------------------------- #
class CollisionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refstage-collision-"))
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)
        self.stage = stage_directory(self.tmp)
        self.lists = ref_stage.read_json(self.stage / "collision_area2.json")

    def test_group_local_indices_are_placed_in_the_flat_vertex_list(self):
        entry = ref_stage.collision_entries(self.lists)[0]
        triangles = ref_stage.entry_triangles(entry)
        self.assertEqual(len(triangles), 3)
        strip = triangles[0]
        self.assertEqual([list(point) for point in strip[:3]],
                         [[2.0, 0.0, -3.0], [2.0, 0.0, -1.0], [-2.0, 0.0, -3.0]])
        self.assertEqual(strip[3], 0)
        # The polygon group's indices start again at 0: its first vertex is (-1, 0, 0).
        polygon = triangles[2]
        self.assertEqual(list(polygon[0]), [-1.0, 0.0, 0.0])
        self.assertEqual(polygon[3], 3)

    def test_the_payload_has_aabbs_and_a_viewer_frame_triangle_soup(self):
        payload = ref_stage.collision_payload(self.lists)
        self.assertEqual(payload["counts"], {"entries": 1, "aabbs": 1, "triangles": 3})
        self.assertEqual(payload["aabbs"][0]["min"], [-2.0, 0.0, -3.0])
        self.assertEqual(payload["aabbs"][0]["max"], [2.0, 0.0, 3.0])
        self.assertEqual(len(payload["triangles"]), 27)
        self.assertEqual(payload["triangleZones"], [0, 0, 3])

    def test_a_zone_is_the_surface_the_prop_stands_on(self):
        lookup = ref_stage.zone_lookup(self.lists)
        self.assertEqual(lookup(1.0, 0.6, -2.0), (0, 0.0))
        self.assertEqual(lookup(0.0, 0.6, 0.0), (3, 0.0))
        self.assertIsNone(lookup(9.0, 0.6, 9.0))

    def test_zone_boxes_convert_into_the_viewer_frame(self):
        boxes = ref_stage.zone_boxes([{
            "bbox": {"min": [-200.0, 0.0, -200.0], "max": [200.0, 0.0, 200.0]}}])
        self.assertEqual(boxes, [{"id": 0, "min": [-2.0, 0.0, -2.0],
                                  "max": [2.0, 0.0, 2.0]}])


# --------------------------------------------------------------------------- #
# the GLB conversion
# --------------------------------------------------------------------------- #
class GlbTests(unittest.TestCase):
    def setUp(self):
        self.positions = [(1.0, 2.0, 3.0), (4.0, 5.0, 6.0), (7.0, 8.0, 9.0)]
        self.normals = [(0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)]
        self.data = tiny_glb(self.positions, self.normals, [0, 1, 2])

    def test_z_is_negated_and_the_winding_is_kept(self):
        converted = ref_stage.convert_stage_glb(self.data)
        document, blob = ref_stage.read_glb(converted)
        positions = ref_stage.accessor_values(document, blob, 0)
        self.assertEqual(positions, [(1.0, 2.0, -3.0), (4.0, 5.0, -6.0), (7.0, 8.0, -9.0)])
        normals = ref_stage.accessor_values(document, blob, 1)
        self.assertEqual(normals, [(0.0, 1.0, -0.0), (0.0, 0.0, -1.0), (1.0, 0.0, -0.0)])
        # The index order is kept: the viewer's z mirror is the one reflection owed to
        # the extractor's own determinant -1 map, so the two compose into a rotation.
        indices = [item[0] for item in ref_stage.accessor_values(document, blob, 2)]
        self.assertEqual(indices, [0, 1, 2])

    def test_the_position_bounds_are_recomputed(self):
        converted = ref_stage.convert_stage_glb(self.data)
        document, _blob = ref_stage.read_glb(converted)
        accessor = document["accessors"][0]
        self.assertEqual(accessor["min"], [1.0, 2.0, -9.0])
        self.assertEqual(accessor["max"], [7.0, 8.0, -3.0])

    def test_the_rest_of_the_document_survives(self):
        converted = ref_stage.convert_stage_glb(self.data)
        document, _blob = ref_stage.read_glb(converted)
        self.assertEqual(document["meshes"][0]["primitives"][0]["mode"], 4)
        self.assertEqual(document["materials"][0]["name"], "probe")
        original, _ = ref_stage.read_glb(self.data)
        self.assertEqual(len(document["bufferViews"]), len(original["bufferViews"]))

    def test_a_glb_with_a_bad_magic_is_refused(self):
        with self.assertRaises(ref_stage.RefStageError):
            ref_stage.convert_stage_glb(b"not a glb at all")


# --------------------------------------------------------------------------- #
# the whole payload + the server contract
# --------------------------------------------------------------------------- #
class PrepareTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refstage-prepare-"))
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)
        self.folder = level_directory(self.tmp)
        self.stage = stage_directory(self.tmp)

    def test_the_payload_carries_the_transforms_the_viewer_needs(self):
        payload = ref_stage.prepare(self.stage, self.folder, level_document())
        self.assertTrue(payload["available"])
        self.assertEqual(payload["counts"]["instances"], 3)
        self.assertEqual(payload["counts"]["props"], 2)
        self.assertEqual(payload["counts"]["matched"], 2)
        self.assertEqual(payload["counts"]["anchors"], 1)
        self.assertEqual(payload["counts"]["rotated"], 1)
        self.assertEqual(payload["counts"]["scaled"], 1)
        self.assertEqual(payload["counts"]["parentsResolved"], 1)
        self.assertEqual(payload["counts"]["aabbs"], 1)
        self.assertEqual(payload["counts"]["triangles"], 3)
        first = payload["transforms"]["byItemId"]["crate_001"]
        self.assertEqual(first["instance"], 0)
        self.assertAlmostEqual(first["angleDeg"], 90.0, places=3)
        self.assertEqual(first["parent"], "crate_002")
        self.assertEqual(first["parentInstance"], 1)
        self.assertEqual(first["zone"], 0)
        second = payload["transforms"]["byItemId"]["crate_002"]
        self.assertEqual(second["scale"], [2.0, 2.0, 2.0])
        self.assertIsNone(second["parent"])
        self.assertEqual(payload["transforms"]["byInstance"]["0"], "crate_001")

    def test_the_contact_report_measures_the_props_feet(self):
        # crate_001 sits at y = 0.1 with a 0.2 m AABB, so its bottom is y = 0 and the
        # collision/terrain surface below it is y = 0: contact within 1 cm.
        doc = level_document()
        payload = ref_stage.prepare(self.stage, self.folder, doc)
        report = ref_stage.contact_report(self.stage, payload, doc, 0.1)
        self.assertEqual(report["withTerrain"], 2)
        self.assertEqual(report["current"]["within"], 2)
        self.assertAlmostEqual(report["current"]["median"], 0.0, places=4)
        # Lift the file's own y by 1 m: the props now float, and the instance height is
        # what still puts their feet on the surface.
        raised = json.loads(json.dumps(doc))
        for prop in raised["props"]:
            prop["position"][1] += 1.0
        shifted = ref_stage.contact_report(self.stage, payload, raised, 0.1)
        self.assertEqual(shifted["fileHeight"]["within"], 0)
        self.assertAlmostEqual(shifted["fileHeight"]["median"], 1.0, places=4)
        self.assertEqual(shifted["current"]["within"], 2)

    def test_a_missing_extraction_is_not_an_error(self):
        payload = ref_stage.prepare(None, self.folder, level_document())
        self.assertFalse(payload["available"])
        self.assertIn("no stage extraction", payload["reason"])

    def test_a_stage_written_in_another_space_is_refused(self):
        folder = stage_directory(self.tmp / "noclip", space="noclip")
        with self.assertRaises(ref_stage.RefStageError) as caught:
            ref_stage.prepare(folder, self.folder, level_document())
        self.assertIn("reroll", str(caught.exception))


class StageServingTests(unittest.TestCase):
    """Over a real socket: the stage bytes exist only while a reference level is open."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refstage-server-"))
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)
        self.levels = self.tmp / "levels"
        self.models = self.tmp / "KatamariRef" / "models"
        (self.models / "glb-pack").mkdir(parents=True)
        (self.models / "glb-pack" / "0001_Crate.glb").write_bytes(b"glTF-fake-crate")
        self.state_dir = self.tmp / "state"
        self.folder = level_directory(self.tmp)
        self.stage = stage_directory(self.tmp)

    def open_editor(self, level="_ref_demo", stage_dir=None):
        return web.WebEditor(level, levels_dir=self.levels, state_dir=self.state_dir,
                             lock=False, ref_models_dir=self.models,
                             ref_stage_dir=self.stage if stage_dir is None else stage_dir)

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

    def test_the_ref_level_reports_and_serves_the_stage(self):
        app = self.open_editor()
        self.addCleanup(app.close)
        stage = app.state()["ref"]["stage"]
        self.assertTrue(stage["available"])
        self.assertEqual(stage["instances"]["matched"], 2)
        self.assertEqual(stage["collision"], {"aabbs": 1, "triangles": 3})
        base = self.start(app)
        status, body, headers = self.get(base, "/api/ref-stage/stage.glb")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get("X-Unlicensed-Reference"), "1")
        self.assertEqual(headers.get("Cache-Control"), "no-store")
        document, blob = ref_stage.read_glb(body)
        positions = ref_stage.accessor_values(document, blob, 0)
        self.assertEqual(positions[0], (-3.0, 0.0, 3.0))
        status, body, _headers = self.get(base, "/api/ref-stage/transforms.json")
        self.assertEqual(status, 200)
        payload = json.loads(body)
        self.assertAlmostEqual(payload["byItemId"]["crate_001"]["angleDeg"], 90.0, 3)
        status, body, _headers = self.get(base, "/api/ref-stage/collision.json")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body)["counts"]["triangles"], 3)
        status, _body, _headers = self.get(base, "/api/ref-stage/secrets.json")
        self.assertEqual(status, 404)
        status, body, _headers = self.get(base, "/api/ref-stage/contact?t=0.1")
        self.assertEqual(status, 200)
        report = json.loads(body)
        self.assertEqual(report["withTerrain"], 2)
        self.assertEqual(report["current"]["within"], 2)

    def test_a_normal_level_never_gets_the_stage(self):
        folder = self.levels / "normal_demo"
        write_json(folder / "placement.json", level_document("normal_demo"))
        app = web.WebEditor("normal_demo", levels_dir=self.levels,
                            state_dir=self.state_dir, lock=False,
                            ref_models_dir=self.models, ref_stage_dir=self.stage)
        self.addCleanup(app.close)
        self.assertIsNone(app.state()["ref"])
        base = self.start(app)
        status, _body, _headers = self.get(base, "/api/ref-stage/stage.glb")
        self.assertEqual(status, 404)

    def test_a_reference_level_without_an_extraction_still_opens(self):
        app = self.open_editor(stage_dir=self.tmp / "absent")
        self.addCleanup(app.close)
        self.assertTrue(app.ref_mode)
        stage = app.state()["ref"]["stage"]
        self.assertFalse(stage["available"])
        self.assertIn("no stage extraction", stage["reason"])
        base = self.start(app)
        status, _body, _headers = self.get(base, "/api/ref-stage/stage.glb")
        self.assertEqual(status, 404)
        status, _body, _headers = self.get(base, "/api/level")
        self.assertEqual(status, 200)

    def test_the_stage_needs_the_token_like_every_other_api_read(self):
        app = self.open_editor()
        self.addCleanup(app.close)
        base = self.start(app)
        try:
            with urllib.request.urlopen(base + "/api/ref-stage/stage.glb", timeout=10):
                self.fail("the stage bytes must not be public")
        except urllib.error.HTTPError as error:
            self.assertIn(error.code, (401, 403))


if __name__ == "__main__":
    unittest.main()
