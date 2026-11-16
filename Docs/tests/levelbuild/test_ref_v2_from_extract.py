"""The reference level's v2 builder: path guards, structure, and determinism.

``Tools/levelbuild/ref_v2_from_extract.py`` turns the extractor's output (outside the
repository) into ``Docs/Design/Levels/_ref_cancer/placement.v2.json`` plus a ``.katerrain``
sidecar.  What would fail silently, and is pinned here:

* it refuses to read outside ``~/KatamariRef`` and to write outside a ``_ref_*`` level
  directory (the data is unlicensed and must never leak into the repository);
* the derived rules are what the doc says they are (terrain from the collision lists with
  the winding flipped once, chunk AABBs that tile the triangle list, scenery = gate over the
  design ceiling and covered by collision space, activity polygon from the collision
  footprint);
* the sidecar round-trips (magic/version/flags/counts/index width);
* the document is canonical and deterministic (two builds → identical bytes);
* the built document is structurally valid ``katamari.placement/2`` (keys, ordering, unit
  quaternions, chunk/broad-phase bookkeeping, sidecar counts + hash), independent of the
  design numbers.
"""

import json
import math
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import ref_stage  # noqa: E402
from Tools.levelbuild import ref_v2_from_extract as build  # noqa: E402

LEVEL = "_ref_cancer"


def collision_list():
    """One collision entry: a strip (2 triangles) at y=0 and a polygon at y=1.

    Raw PS2 centimetres: ``world = -0.01 * raw``, so a vertex at world (x, 0, z) is stored
    as (-100x, 0, -100z).
    """
    # The PS2 order faces *down* in the Unity frame (that is what the real Cancer data does);
    # the builder's single flip is what turns these floors face-up.
    strip = [(1.0, 0.0, 1.0), (-1.0, 0.0, 1.0), (1.0, 0.0, -1.0), (-1.0, 0.0, -1.0)]
    polygon = [(-1.0, 1.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 1.0)]
    values = []
    for world in strip + polygon:
        values.extend([-value * 100.0 for value in world])
        values.append(0.0)
    return [[{
        "bbox": {"min": [-100.0, -100.0, -100.0], "max": [100.0, 100.0, 100.0]},
        "groups": [{"is_strip": True, "vertex_count": 4, "zones": [0, 0, 3, 3]},
                   {"is_strip": False, "vertex_count": 3, "zones": [24, 24, 24]}],
        "triangles": [[0, 1, 2, 0], [1, 3, 2, 0], [0, 1, 2, 24]],
        "verts": values,
    }]]


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refv2-guard-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.read_root = self.tmp / "KatamariRef"
        self.levels = self.tmp / "Levels"
        (self.read_root / "noclip_export").mkdir(parents=True)
        (self.levels / "_ref_demo").mkdir(parents=True)
        (self.levels / "story_01_bench_size").mkdir(parents=True)
        self._patches = [
            mock.patch.object(build, "READ_ROOT", self.read_root),
            mock.patch.object(build, "LEVELS_ROOT", self.levels),
        ]
        for patch in self._patches:
            patch.start()
            self.addCleanup(patch.stop)

    def test_reading_outside_the_reference_tree_is_refused(self):
        with self.assertRaises(build.RefV2Error):
            build.guard_read(ROOT / "Tools/levelbuild/placement_core.py")
        inside = self.read_root / "noclip_export" / "stage.glb"
        self.assertEqual(build.guard_read(inside), inside)

    def test_writing_outside_a_reference_level_is_refused(self):
        with self.assertRaises(build.RefV2Error):
            build.guard_write(self.tmp / "elsewhere" / "placement.v2.json")
        with self.assertRaises(build.RefV2Error):
            build.guard_write(self.levels / "story_01_bench_size" / "placement.v2.json")
        ok = self.levels / "_ref_demo" / "placement.v2.json"
        self.assertEqual(build.guard_write(ok), ok.resolve())
        with self.assertRaises(build.RefV2Error):
            build.guard_write(self.levels / "_ref_demo" / ".." / "escape.json")


class TerrainTests(unittest.TestCase):
    def test_the_collision_list_becomes_a_ccw_mesh_with_the_zone_majority(self):
        terrain = build.terrain_from_collision(collision_list())
        self.assertEqual(terrain["stats"]["vertices"], 7)
        self.assertEqual(terrain["stats"]["triangles"], 3)
        self.assertEqual(terrain["stats"]["droppedDegenerate"], 0)
        # A floor triangle written counter-clockwise in Unity's frame: normal +Y.
        first = terrain["indices"][:3]
        a, b, c = (terrain["vertices"][index] for index in first)
        normal = build._triangle_normal(a, b, c)
        self.assertGreater(normal[1], 0.0)
        self.assertEqual(terrain["classes"][0], 1)      # ride
        self.assertEqual(terrain["zoneIds"][0], 0)      # strip group: 0,0,3,3 -> majority 0
        self.assertEqual(terrain["zoneIds"][2], 24)     # polygon group: 24,24,24

    def test_a_degenerate_triangle_is_dropped(self):
        lists = collision_list()
        entry = lists[0][0]
        entry["verts"][4:8] = entry["verts"][0:4]       # vertex 1 := vertex 0
        terrain = build.terrain_from_collision(lists)
        self.assertEqual(terrain["stats"]["droppedDegenerate"], 1)
        self.assertEqual(terrain["stats"]["triangles"], 2)

    def test_chunks_tile_the_triangle_list_and_carry_their_own_box(self):
        terrain = build.terrain_from_collision(collision_list())
        boxes, chunks = build.chunk_layout(terrain["vertices"], terrain["indices"], limit=2)
        self.assertEqual([box["triangleRange"] for box in boxes], [[0, 2], [2, 1]])
        self.assertEqual([chunk["boxRange"] for chunk in chunks], [[0, 1], [1, 1]])
        for box in boxes:
            start, count = box["triangleRange"]
            for index in range(start, start + count):
                for offset in range(3):
                    point = terrain["vertices"][terrain["indices"][index * 3 + offset]]
                    for axis in range(3):
                        self.assertLessEqual(box["min"][axis], point[axis])
                        self.assertGreaterEqual(box["max"][axis], point[axis])

    def test_the_sidecar_round_trips(self):
        terrain = build.terrain_from_collision(collision_list())
        data, width = build.sidecar_bytes(terrain)
        self.assertEqual(width, 2)
        parsed = build.read_sidecar(data)
        self.assertEqual(parsed["counts"]["vertices"], 7)
        self.assertEqual(parsed["counts"]["indices"], 9)
        self.assertEqual(parsed["counts"]["triangles"], 3)
        self.assertEqual(parsed["indexWidth"], 2)
        self.assertEqual(parsed["flags"], 3)
        self.assertEqual(list(parsed["zoneIds"]), terrain["zoneIds"])
        self.assertEqual(list(parsed["classes"]), terrain["classes"])
        self.assertEqual(len(parsed["indices"]), 9)

    def test_a_bad_sidecar_is_refused(self):
        # The codec lives in placement_v2 (the format module) and raises its own error type,
        # re-exported by the builder so callers do not need to know where it lives.
        with self.assertRaises(build.SidecarError):
            build.read_sidecar(b"JUNK" + b"\0" * 32)


class RuleTests(unittest.TestCase):
    def test_volume_k_is_the_originals_fill_ratio(self):
        self.assertAlmostEqual(build.def_volume_k({"bboxOverSolidVolume": 1.43}), 0.6993,
                               places=4)
        self.assertEqual(build.def_volume_k({"bboxOverSolidVolume": 0.0}), 0.70)
        self.assertEqual(build.def_volume_k({}), 0.70)
        self.assertLessEqual(build.def_volume_k({"bboxOverSolidVolume": 0.001}), 32.0)

    def test_coverage_is_the_fraction_inside_one_box(self):
        box = ([-1.0, -1.0, -1.0], [1.0, 1.0, 1.0])
        self.assertAlmostEqual(build.coverage(([0.0, 0.0, 0.0], [1.0, 1.0, 1.0]), [box]), 1.0)
        self.assertAlmostEqual(build.coverage(([0.5, 0.0, 0.0], [1.5, 1.0, 1.0]), [box]), 0.5)
        self.assertEqual(build.coverage(([5.0, 5.0, 5.0], [6.0, 6.0, 6.0]), [box]), 0.0)

    def test_scenery_needs_a_gate_over_the_ceiling_and_collision_cover(self):
        box = ([-2.0, -1.0, -2.0], [2.0, 2.0, 2.0])
        inside = {"itemId": "big_001", "def": "big", "absorbDiameterReqMm": 5000,
                  "position": [0.0, 0.5, 0.0], "aabb": [1.0, 1.0, 1.0],
                  "rotationQuat": [0.0, 0.0, 0.0, 1.0],
                  "worldAabb": ([-0.5, 0.0, -0.5], [0.5, 1.0, 0.5])}
        outside = dict(inside, itemId="big_002", worldAabb=([9.0, 0.0, 9.0], [10.0, 1.0, 10.0]))
        small = dict(inside, itemId="small_001", absorbDiameterReqMm=300)
        solids, report = build.build_structures([inside, outside, small], [box])
        self.assertEqual([solid["id"] for solid in solids], ["big_001"])
        self.assertEqual(report["neverAbsorbable"], 2)
        self.assertEqual(report["withSolid"], 1)
        self.assertEqual(report["background"], 1)
        self.assertEqual(solids[0]["collision"], "box")
        self.assertFalse(solids[0]["absorbable"])

    def test_the_activity_polygon_is_the_collision_footprint_plus_margin(self):
        terrain = build.terrain_from_collision(collision_list())
        points, info = build.activity_polygon(terrain, [([-2.0, 0.0, -3.0], [2.0, 1.0, 3.0])],
                                             margin=1.0)
        self.assertEqual(points[0], [-3.0, -4.0])
        self.assertEqual(points[2], [3.0, 4.0])
        self.assertEqual(info["margin"], 1.0)

    def test_the_canonical_text_is_ordered_and_clean(self):
        document = {"schema": build.SCHEMA_V2, "meta": {"id": "x", "value": -0.0},
                    "terrain": {}, "structures": {}, "activityArea": {}, "props": [],
                    "defs": [], "motions": [], "paths": [], "cameras": [],
                    "renderPresets": [], "regions": []}
        text = build.canonical_text(document)
        self.assertTrue(text.endswith("\n"))
        self.assertLess(text.index('"schema"'), text.index('"meta"'))
        self.assertIn('"value": 0.0', text)
        self.assertNotIn("-0.0", text)


class V1ViewTests(unittest.TestCase):
    def test_the_view_maps_the_layers_the_validator_reads(self):
        document = {
            "schema": build.SCHEMA_V2,
            "meta": {"id": "_ref_x", "scene": "StoryMode", "units": "m",
                     "convention": "unity-y-up-left-handed-quat-xyzw",
                     "ball": {"startDiameterM": 0.222, "targetDiameterM": 0.8,
                              "maxDiameterRef": "global", "start": [1.0, 0.5, 2.0],
                              "spawnSource": "fieldCentreGuess"},
                     "timeLimitS": 420, "playerSpawn": [1.0, 0.9, 2.0], "groundY": -3.0,
                     "bounds": [[-1.0, -1.0], [1.0, 1.0]], "spawnClearanceM": 0.5,
                     "zones": [{"id": "default", "code": 0, "kind": "ground"}],
                     "arena": {"id": "_ref_x"}, "budget": {"planes": 8, "boxes": 64},
                     "expected": {"propCount": 1, "growthMm3": 10, "structureCount": 0,
                                  "staticObbs": 0, "overlapBudget": 0,
                                  "terrainTriangles": 3},
                     "caps": {}, "migration": {"maxDiameterM": 1.2}},
            "terrain": {"id": "t", "kind": "mesh", "winding": "ccw",
                        "meshRef": {"path": "terrain/x.katerrain", "sha256": "0" * 64,
                                    "vertexCount": 3, "indexCount": 3, "triangleCount": 1,
                                    "indexWidth": 2, "zoneIds": True, "classes": True},
                        "triangles": {"count": 1}},
            "structures": {"solids": []},
            "activityArea": {"walls": [{"id": "w", "points": [[-1.0, -1.0], [1.0, -1.0],
                                                             [1.0, 1.0], [-1.0, 1.0]],
                                        "closed": True, "heightM": 3.0, "thicknessM": 0.2,
                                        "kind": "invisible"}],
                             "killPlane": {"y": -4.0, "respawn": "ballStart", "zoneId": 0},
                             "softMarginM": 0.0, "source": "authored"},
            "props": [{"itemId": "a_001", "def": "a", "kind": "reference",
                       "behaviour": "static", "zone": "default",
                       "position": [0.0, 0.0, 0.0], "rotationQuat": [0, 0, 0, 1],
                       "aabb": [1.0, 1.0, 1.0], "volumeK": 0.7, "volumePenalty": 0.8,
                       "absorbDiameterReqMm": 500, "growthMm3": 560000000,
                       "absorbable": True, "parent": "a_002", "inherit": "full"},
                      {"itemId": "a_002", "def": "a", "kind": "reference",
                       "behaviour": "static", "zone": "default",
                       "position": [2.0, 0.0, 0.0], "rotationQuat": [0, 0, 0, 1],
                       "aabb": [1.0, 1.0, 1.0], "volumeK": 0.7, "volumePenalty": 0.8,
                       "absorbDiameterReqMm": 500, "growthMm3": 560000000,
                       "absorbable": True, "inherit": "none"}],
            "defs": [{"id": "a", "kind": "reference", "model": "Assets/x/a.fbx",
                      "volumeK": 0.7}],
            "motions": [], "paths": [], "cameras": [], "renderPresets": [], "regions": [],
        }
        view = build.to_v1_view(document)
        self.assertEqual(view["schema"], "katamari.placement/0")
        self.assertEqual(view["level"]["ballStart"], [1.0, 0.5, 2.0])
        self.assertEqual(view["level"]["ballDiameterStart"], 0.222)
        self.assertAlmostEqual(view["level"]["ballDiameterMax"], 1.2)
        self.assertEqual(view["level"]["expected"], {"propCount": 1, "growthMm3": 10,
                                                     "overlapBudget": 0})
        roles = [structure["role"] for structure in view["structures"]]
        self.assertEqual(roles.count("ground"), 1)
        self.assertEqual(roles.count("boundary"), 4)
        props = {prop["itemId"]: prop for prop in view["props"]}
        self.assertEqual(props["a_001"]["parent"], "a_002")
        self.assertAlmostEqual(props["a_001"]["absorbDiameterReq"], 0.5)
        # the boundary planes must be real unit quaternions (v1 validates them)
        for structure in view["structures"]:
            quat = structure["rotationQuat"]
            self.assertAlmostEqual(sum(value * value for value in quat), 1.0, places=4)


@unittest.skipUnless((Path.home() / "KatamariRef/noclip_export/kse_out/stage.glb").is_file(),
                     "the reference extraction is not on this machine")
class RealExtractionTests(unittest.TestCase):
    """The built document against the real Cancer extraction (structure, not numbers)."""

    @classmethod
    def setUpClass(cls):
        cls.document, cls.sidecar, cls.report = build.build_document(
            LEVEL, ROOT / "Docs/Design/Levels" / LEVEL, with_colliders=False)
        cls.loose, cls.loose_sidecar, _report = build.build_document(
            LEVEL, ROOT / "Docs/Design/Levels" / LEVEL, with_colliders=False)

    def test_two_builds_are_byte_identical(self):
        self.assertEqual(build.canonical_text(self.document),
                         build.canonical_text(self.loose))
        self.assertEqual(self.sidecar, self.loose_sidecar)

    def test_the_document_is_structurally_valid(self):
        problems = build.structural_errors(json.loads(build.canonical_text(self.document)),
                                           self.sidecar)
        self.assertEqual(problems, [])

    def test_the_shape_of_the_level(self):
        document = self.document
        terrain = document["terrain"]
        self.assertEqual(terrain["meshRef"]["triangleCount"], 3455)
        self.assertEqual(terrain["meshRef"]["vertexCount"], 7154)
        self.assertEqual(terrain["meshRef"]["indexWidth"], 2)
        self.assertEqual(terrain["triangles"]["count"], 3455)
        self.assertEqual(len(document["props"]), 511)
        self.assertEqual(len(document["defs"]), 145)
        self.assertEqual(len(document["structures"]["solids"]), 35)
        parents = [prop for prop in document["props"] if prop.get("parent")]
        self.assertEqual(len(parents), 83)
        self.assertTrue(all(prop["behaviour"] == "static" for prop in document["props"]))
        self.assertTrue(all("rotationQuat" in prop for prop in document["props"]))
        chunks = terrain["chunks"]
        self.assertEqual(len(chunks), math.ceil(3455 / build.CHUNK_TRIANGLES))
        self.assertTrue(all(chunk["triangleRange"][1] <= build.CHUNK_TRIANGLES
                            for chunk in chunks))
        # every prop's zone is declared, and the terrain's block is one terrain
        zones = {zone["id"] for zone in document["meta"]["zones"]}
        self.assertTrue({prop["zone"] for prop in document["props"]} <= zones)
        # the kill plane sits below the fallback floor and the terrain's lowest point
        lowest = min(point[1] for point in build.read_sidecar(self.sidecar)["vertices"])
        self.assertLess(document["activityArea"]["killPlane"]["y"], document["meta"]["groundY"])
        self.assertLess(document["meta"]["groundY"], lowest)

    def test_the_reported_numbers_match_the_document(self):
        report = self.report
        self.assertEqual(report["terrain"]["triangles"],
                         self.document["terrain"]["triangles"]["count"])
        self.assertEqual(report["sidecar"]["sha256"],
                         self.document["terrain"]["meshRef"]["sha256"])
        self.assertEqual(report["props"]["total"], len(self.document["props"]))
        self.assertEqual(report["expected"]["propCount"], len(self.document["props"]))

    def test_the_sidecar_matches_its_declared_hash(self):
        import hashlib
        self.assertEqual(hashlib.sha256(self.sidecar).hexdigest(),
                         self.document["terrain"]["meshRef"]["sha256"])


if __name__ == "__main__":
    unittest.main()
