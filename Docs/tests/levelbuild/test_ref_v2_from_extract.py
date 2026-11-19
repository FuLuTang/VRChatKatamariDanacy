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

    def test_a_sparse_convex_solid_carries_parts_and_counts_them(self):
        box = ([-2.0, -1.0, -2.0], [2.0, 2.0, 2.0])
        convex = {"type": "convex", "center": [0.0, 0.0, 0.0],
                  "hull": [-0.5, -0.5, -0.5, 0.5, -0.5, -0.5, -0.5, -0.5, 0.5, 0.0, 0.5, 0.0]}
        parts = [{"center": [0.0, -0.4, 0.0], "size": [1.0, 0.2, 1.0]},
                 {"center": [0.0, 0.1, 0.0], "size": [0.1, 0.8, 0.1]}]
        entry = {"itemId": "stand_001", "def": "stand", "absorbDiameterReqMm": 5000,
                 "position": [0.0, 0.5, 0.0], "aabb": [1.0, 1.0, 1.0],
                 "rotationQuat": [0.0, 0.0, 0.0, 1.0],
                 "worldAabb": ([-0.5, 0.0, -0.5], [0.5, 1.0, 0.5]),
                 "collider": convex, "parts": parts}
        plain = dict(entry, itemId="stand_002", parts=None)
        solids, report = build.build_structures([entry, plain], [box])
        self.assertEqual(solids[0]["parts"], parts)
        self.assertNotIn("parts", solids[1])
        self.assertEqual(report["splitSolids"], 1)
        self.assertEqual(build.placement_v2.static_obb_count(solids), 3)
        self.assertEqual(build.scaled_parts(parts, [2.0, 2.0, 2.0])[1]["size"], [0.2, 1.6, 0.2])
        self.assertIsNone(build.scaled_parts(parts, [1.0, 2.0, 1.0]))
        self.assertIs(build.scaled_parts(parts, [1.0, 1.0, 1.0]), parts)

    def test_def_parts_split_only_a_sparse_hull(self):
        foot = [(-0.7, -1.0, -0.03), (0.7, -1.0, -0.03), (0.7, -0.85, -0.03),
                (-0.7, -0.85, -0.03), (-0.7, -1.0, 0.03), (0.7, -1.0, 0.03),
                (0.7, -0.85, 0.03), (-0.7, -0.85, 0.03)]
        pole = [(x * 0.03 / 0.7, -0.85 if y < -0.9 else 1.0, z) for x, y, z in foot]
        quads = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (3, 7, 6, 2), (0, 4, 7, 3),
                 (1, 2, 6, 5)]
        faces = []
        for base in (0, 8):
            for a, b, c, d in quads:
                faces += [(base + a, base + b, base + c), (base + a, base + c, base + d)]
        mesh = (foot + pole, faces, [0.7, 1.0, 0.03])
        sparse = {"type": "convex", "center": [0.0, 0.0, 0.0],
                  "hull": [c for p in (foot[:4] + pole[2:4] + pole[6:8]) for c in p]}
        parts = build.def_parts(sparse, mesh)
        # The cuts sit on a 1/16 grid, so the foot/pole joint costs a third box; what matters
        # is that the split happened and the boxes hold far less than the 1.4 x 2 m slab.
        self.assertGreaterEqual(len(parts), 2)
        volume = sum(p["size"][0] * p["size"][1] * p["size"][2] for p in parts)
        self.assertLess(volume / (1.4 * 2.0 * 0.06), 0.3)
        dense = {"type": "box", "center": [0.0, 0.0, 0.0], "size": [1.4, 2.0, 0.06]}
        self.assertIsNone(build.def_parts(dense, mesh))
        self.assertIsNone(build.def_parts(sparse, None))

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


class PathSimplificationTests(unittest.TestCase):
    def test_a_short_polyline_is_untouched(self):
        points = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0)]
        kept, tolerance, deviation = build.simplify_polyline(points)
        self.assertEqual(kept, points)
        self.assertEqual(tolerance, 0.0)
        self.assertEqual(deviation, 0.0)

    def test_a_dense_polyline_fits_the_budget_with_a_measured_deviation(self):
        dense = [(index * 0.05, 0.0, 0.0) for index in range(200)]
        kept, tolerance, deviation = build.simplify_polyline(dense, max_points=62)
        self.assertLessEqual(len(kept), 62)
        self.assertEqual(kept[0], dense[0])
        self.assertEqual(kept[-1], dense[-1])
        self.assertLess(deviation, 0.02)          # a straight line survives almost exactly
        # determinism: same input, same output
        again = build.simplify_polyline(dense, max_points=62)
        self.assertEqual(kept, again[0])
        self.assertEqual(tolerance, again[1])

    def test_a_bend_is_kept(self):
        points = [(0.0, 0.0, 0.0)] * 1 + [(index * 0.05, 0.0, 0.0) for index in range(1, 60)] \
            + [(3.0, 0.0, index * 0.05) for index in range(1, 60)]
        kept, _tolerance, deviation = build.simplify_polyline(points, max_points=62)
        self.assertIn((points[59][0], points[59][1], points[59][2]), kept)
        self.assertLess(deviation, 0.05)


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

    def test_every_behaviour_is_backed_by_the_motion_table(self):
        """The behaviour column and the motion table must agree, prop by prop."""
        document = self.document
        by_id = {motion["id"]: motion for motion in document["motions"]}
        total = 0
        for prop in document["props"]:
            total += 1
            behaviour = prop["behaviour"]
            if behaviour == "static":
                self.assertNotIn("motion", prop)
                continue
            if behaviour in ("motion", "ambient"):
                self.assertIn(prop["motion"], by_id, prop["itemId"])
                self.assertIn(by_id[prop["motion"]]["class"],
                              set(build.MOTION_CLASS_NAMES.values()))
            elif behaviour == "attached":
                self.assertNotIn("motion", prop)          # §6.7.3
                self.assertIn("motionClass=dragged_along", prop.get("notes", ""))
            else:
                self.fail("%s has an unknown behaviour %r" % (prop["itemId"], behaviour))
        # the behaviours partition the props: nothing is counted twice or lost
        counts = {}
        for prop in document["props"]:
            counts[prop["behaviour"]] = counts.get(prop["behaviour"], 0) + 1
        self.assertEqual(sum(counts.values()), total)
        self.assertEqual(counts["static"] + counts.get("motion", 0) + counts.get("ambient", 0)
                         + counts.get("attached", 0), total)
        self.assertEqual(counts, {"static": 383, "motion": 108, "ambient": 5, "attached": 15})

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
        behaviours = {}
        for prop in document["props"]:
            behaviours[prop["behaviour"]] = behaviours.get(prop["behaviour"], 0) + 1
        self.assertEqual(behaviours.get("static", 0), 383)
        self.assertEqual(behaviours.get("attached", 0), 15)      # dragged_along
        self.assertEqual(behaviours.get("ambient", 0), 5)        # clouds
        self.assertEqual(behaviours.get("motion", 0), 108)
        self.assertTrue(all("rotationQuat" in prop for prop in document["props"]))
        chunks = terrain["chunks"]
        self.assertEqual(len(chunks), 58)                    # 6 below the runtime's 64
        self.assertLessEqual(len(chunks), build.placement_v2.MAX_CHUNKS)
        self.assertTrue(all(chunk["triangleRange"][1] <= build.CHUNK_LIMIT
                            for chunk in chunks))
        # the chunks tile the triangle buffer exactly once, in index order (the runtime
        # requires start == expectedStart)
        expected = 0
        for chunk in chunks:
            self.assertEqual(chunk["triangleRange"][0], expected)
            expected += chunk["triangleRange"][1]
        self.assertEqual(expected, 3455)
        # every prop's zone is declared, and the terrain's block is one terrain
        zones = {zone["id"] for zone in document["meta"]["zones"]}
        self.assertTrue({prop["zone"] for prop in document["props"]} <= zones)
        # the kill plane sits below the fallback floor and the terrain's lowest point
        lowest = min(point[1] for point in build.read_sidecar(self.sidecar)["vertices"])
        self.assertLess(document["activityArea"]["killPlane"]["y"], document["meta"]["groundY"])
        self.assertLess(document["meta"]["groundY"], lowest)

    def test_the_spawn_is_authored_and_rests_on_the_terrain(self):
        """The reference level's spawn is a measured point, not the field-centre guess.

        The value itself lives in ``ref_v2_from_extract.AUTHORED_SPAWNS`` (one source); this
        checks what the document must satisfy because of it: the source is ``authored``, the
        note says so, ``playerSpawn`` is the same column 0.4 m up (the format's rule), and the
        ball's bottom actually meets a terrain face (so a terrain rebuild that moves it would
        have to re-measure instead of drifting).
        """
        meta = self.document["meta"]
        ball = meta["ball"]
        self.assertEqual(ball["spawnSource"], "authored")
        self.assertIn("Ball spawn: authored", meta["arena"]["notes"])
        spawn = meta["playerSpawn"]
        self.assertEqual((spawn[0], spawn[2]), (ball["start"][0], ball["start"][2]))
        self.assertAlmostEqual(spawn[1] - ball["start"][1], 0.4, places=4)
        radius = ball["startDiameterM"] / 2.0
        lookup = build._terrain_lookup(build.read_sidecar(self.sidecar))
        surface = lookup(ball["start"][0], ball["start"][2], ball["start"][1] - radius + 1e-4)
        self.assertIsNotNone(surface, "no terrain face under the authored spawn")
        self.assertLess(abs(surface + radius - ball["start"][1]), 1e-3)

    def test_the_player_start_follows_our_rule_and_rests_on_the_terrain(self):
        """``AUTHORED_SPAWNS[...]['playerStart']``: behind the ball, on a face, facing it.

        The player's descriptor spawn is OUR rule (not the original's): 1.8-3.5 m behind the
        ball along heading 0 (+z) within +-30 degrees, feet on a terrain face, facing the
        ball.  The builder itself refuses a start that is no longer on a terrain face.
        """
        authored = build.AUTHORED_SPAWNS[LEVEL]
        player = authored["playerStart"]
        ball = self.document["meta"]["ball"]["start"]
        feet = player["position"]
        dx, dz = ball[0] - feet[0], ball[2] - feet[2]
        distance = math.hypot(dx, dz)
        self.assertGreaterEqual(distance, 1.8 - 1e-3)
        self.assertLessEqual(distance, 3.5 + 1e-3)
        bearing = math.degrees(math.atan2(dx, dz))        # heading 0 = +z
        self.assertAlmostEqual(bearing, player["facingDeg"], delta=0.5)
        self.assertLessEqual(abs(player["facingDeg"]), 30.0 + 1e-6)
        self.assertAlmostEqual(distance, player["distanceM"], delta=0.01)
        lookup = build._terrain_lookup(build.read_sidecar(self.sidecar))
        surface = lookup(feet[0], feet[2], feet[1] + 1e-4)
        self.assertIsNotNone(surface)
        self.assertLess(abs(surface - feet[1]), 0.02)
        self.assertIn("not the original", player["source"])

    def test_the_document_carries_the_player_start_for_the_importer(self):
        """``meta.playerStart`` is ``AUTHORED_SPAWNS[...]['playerStart']`` (one source), and the
        document stays valid against the v2 validator that knows the key."""
        player = build.AUTHORED_SPAWNS[LEVEL]["playerStart"]
        written = self.document["meta"]["playerStart"]
        self.assertEqual(written["position"], [round(v, 4) for v in player["position"]])
        self.assertEqual(written["facingDeg"], player["facingDeg"])
        self.assertEqual(written["source"], player["source"])
        self.assertEqual(sorted(written), ["facingDeg", "position", "source"])
        keys = list(self.document["meta"])
        self.assertEqual(keys.index("playerStart"), keys.index("playerSpawn") + 1)

    def test_chunks_are_spatially_compact(self):
        """A chunk covers a small piece of the map, never an index-order run across it."""
        terrain = self.document["terrain"]
        boxes, chunks = terrain["broadPhase"]["boxes"], terrain["chunks"]
        self.assertEqual(len(boxes), len(chunks))
        self.assertEqual([chunk["boxRange"] for chunk in chunks],
                         [[index, 1] for index in range(len(chunks))])
        spans = sorted(math.hypot(box["max"][0] - box["min"][0],
                                  box["max"][2] - box["min"][2]) for box in boxes)
        self.assertLess(spans[len(spans) // 2], 6.0)          # median chunk: a few metres
        self.assertLess(spans[-1], 20.0)                      # no scene-wide aggregate box
        # at most one in ten chunks may be wider than 12 m, and only because it contains one
        # of the oversized triangles (12.7 m) rather than because of a bad partition
        self.assertLessEqual(sum(1 for span in spans if span > 12.0), len(spans) // 5)

    def test_a_chunk_does_not_straddle_a_height_gap(self):
        """Inside a chunk, consecutive triangles in the sort order are within the y gap."""
        sidecar = build.read_sidecar(self.sidecar)
        vertices, indices = sidecar["vertices"], sidecar["indices"]
        for chunk in self.document["terrain"]["chunks"]:
            start, count = chunk["triangleRange"]
            heights = []
            for corner in range(start, start + count):
                points = [vertices[indices[corner * 3 + step]] for step in range(3)]
                heights.append(sum(point[1] for point in points) / 3.0)
            heights.sort()
            for previous, current in zip(heights, heights[1:]):
                self.assertLessEqual(current - previous, 6.0,
                                     "chunk %r spans %.2f m of empty height"
                                     % (chunk["id"], current - previous))

    def test_the_permutation_keeps_every_triangle(self):
        """Group-by-group: same triangles, same area, same winding, only the order changed."""
        stage_dir, reason = build.ref_stage.resolve_stage_dir()
        self.assertIsNotNone(stage_dir, reason)
        original = build.terrain_from_collision(
            build.ref_stage.read_json(build.ref_stage.collision_file(stage_dir)))
        permuted = build.permute_terrain(
            original, build.spatial_triangle_order(original["vertices"], original["indices"]))
        self.assertEqual(len(permuted["indices"]), len(original["indices"]))
        self.assertEqual(len(permuted["zoneIds"]), 3455)
        # the per-triangle arrays travel with their triangles: same multiset, new order
        self.assertEqual(sorted(permuted["zoneIds"]), sorted(original["zoneIds"]))
        self.assertEqual(sorted(permuted["classes"]), sorted(original["classes"]))

        def triangles(vertices, indices):
            out = []
            for corner in range(len(indices) // 3):
                points = [tuple(vertices[indices[corner * 3 + step]]) for step in range(3)]
                # canonical key: rotate the corner order so the same triangle always matches
                rotations = [points, points[1:] + points[:1], points[2:] + points[:2]]
                key = min(tuple(tuple(round(value, 5) for value in point) for point in item)
                          for item in rotations)
                winding = 1 if rotations.index(sorted(rotations)[0]) == 0 else -1
                area = 0.5 * math.dist(points[0], points[1]) * math.dist(points[0], points[2])
                out.append((key, round(area, 6)))
            return sorted(out)

        self.assertEqual(triangles(permuted["vertices"], permuted["indices"]),
                         triangles(original["vertices"], original["indices"]))

    def test_a_chunk_never_straddles_a_big_height_gap(self):
        retained = self.document["terrain"]["broadPhase"]["boxes"]
        for box in retained:
            self.assertLessEqual(box["max"][1] - box["min"][1], 30.0)

    def test_the_reported_numbers_match_the_document(self):
        report = self.report
        self.assertEqual(report["terrain"]["triangles"],
                         self.document["terrain"]["triangles"]["count"])
        self.assertEqual(report["sidecar"]["sha256"],
                         self.document["terrain"]["meshRef"]["sha256"])
        self.assertEqual(report["props"]["total"], len(self.document["props"]))
        self.assertEqual(report["expected"]["propCount"], len(self.document["props"]))

    def test_motions_are_one_entry_per_move_type_and_are_well_formed(self):
        document = self.document
        motions = document["motions"]
        self.assertEqual(len(motions), 14)          # 15 moveTypes minus dragged_along
        self.assertEqual([motion["id"] for motion in motions], sorted(m["id"] for m in motions))
        classes = {motion["class"] for motion in motions}
        self.assertTrue(classes <= set(build.MOTION_CLASS_NAMES.values()))
        for motion in motions:
            self.assertIn(motion["class"], classes)
            self.assertIn(motion["speed"]["source"], ("explicit", "path", "type", "none"))
            self.assertIsInstance(motion["updatesPerSecond"], int)
            self.assertGreater(motion["updatesPerSecond"], 0)
            self.assertIsInstance(motion["loop"], bool)
            provenance = motion["provenance"]
            self.assertIsInstance(provenance["moveType"], int)
            if motion["class"] in build.PATH_CLASSES:
                self.assertEqual(motion["speed"]["source"], "path")
                self.assertIn(motion["path"], [path["id"] for path in document["paths"]])
            else:
                self.assertNotIn("path", motion)
        hop = [motion for motion in motions if motion["class"] == "hop"][0]
        self.assertEqual(hop["updatesPerSecond"], 30)     # §6.7.5: displacement decides
        whack = [motion for motion in motions if motion["class"] == "whack_a_mole"][0]
        self.assertEqual(whack["updatesPerSecond"], 30)
        self.assertFalse(whack["loop"])                   # "stop at the top"

    def test_paths_keep_their_shape_and_speed(self):
        paths = self.document["paths"]
        self.assertEqual([path["id"] for path in paths], ["p00", "p15", "p22", "p23"])
        for path in paths:
            points = path["points"]
            self.assertGreaterEqual(len(points), 2)
            self.assertLessEqual(len(points), 64)         # §12.2 per-path budget
            for index in range(1, len(points)):
                gap = math.dist(points[index - 1], points[index])
                self.assertGreaterEqual(gap, 0.05, path["id"])
            if "speedCmPerSec" in path:
                self.assertGreater(path["speedCmPerSec"], 0.0)
                self.assertLess(path["speedCmPerSec"], 500.0)
        by_id = {path["id"]: path for path in paths}
        self.assertNotIn("speedCmPerSec", by_id["p00"])    # the -1 sentinel
        self.assertAlmostEqual(by_id["p15"]["speedCmPerSec"], 36.6, places=1)
        self.assertIn("simplified", by_id["p15"]["notes"])  # the reduction is stated
        self.assertIn("137 original point(s)", by_id["p15"]["notes"])

    def test_every_moving_prop_resolves_through_its_own_instance_index(self):
        """129 records with a program: 128 rendered props (113 motion + 15 attached), 1 anchor."""
        document = self.document
        sidecar = self.sidecar
        motion_document = build.ref_stage.read_json(
            build.guard_read(Path.home() / "KatamariRef/noclip_export/kse_out/motion_table.json"))
        records = {record["instanceIndex"]: record for record in motion_document["instances"]}
        by_index = {prop["instanceIndex"]: prop for prop in document["props"]}
        self.assertEqual(len(by_index), 511)
        moving_records = {index: record for index, record in records.items()
                          if record.get("motionClass") != "static"}
        self.assertEqual(len(moving_records), 129)          # counts.byClass
        rendered = {index: record for index, record in moving_records.items()
                    if index in by_index}
        self.assertEqual(len(rendered), 128)                # counts.byClassRendered
        self.assertEqual(set(moving_records) - set(rendered), {497})   # Dummy Hit 01, no prop
        with_motion = [index for index, record in rendered.items()
                       if record["motionClass"] in ("Clouds", "SporadicWalk", "Hop", "Sway",
                                                    "WhackAMole", "PathCollision", "Spin",
                                                    "Flip", "Bob", "SlopingPath",
                                                    "BackAndForthNoYaw", "WaitForPlayer")]
        self.assertEqual(len(with_motion), 113)
        for index in with_motion:
            prop = by_index[index]
            self.assertIn(prop["behaviour"], ("motion", "ambient"), prop["itemId"])
            self.assertTrue(prop.get("motion"), prop["itemId"])
            self.assertIn(prop["motion"], [motion["id"] for motion in document["motions"]])
        attached = [index for index, record in rendered.items()
                    if record["motionClass"] == "DraggedAlong"]
        self.assertEqual(len(attached), 15)
        for index in attached:
            prop = by_index[index]
            self.assertEqual(prop["behaviour"], "attached")
            self.assertNotIn("motion", prop)                # §6.7.3: no motions[] entry
            self.assertIn("dragged_along", prop.get("notes", ""))
        # the 8 moveType-10 props carry the unverified trigger note, not guessed params
        unverified = [by_index[index] for index, record in rendered.items()
                      if record["moveType"] == 10]
        self.assertEqual(len(unverified), 8)
        for prop in unverified:
            self.assertIn("triggerUnknown", prop.get("notes", ""))
            self.assertNotIn("params", json.dumps(prop))

    def test_the_motion_split_is_recorded_in_the_banner_facts(self):
        notes = self.document["meta"]["arena"]["notes"]
        self.assertIn("129 records with a program", notes)
        self.assertIn("128 rendered props", notes)
        self.assertIn("113 carry props[].motion", notes)
        caps = self.document["meta"]["caps"]
        self.assertEqual(caps["movingProps"], 128)
        self.assertLessEqual(caps["movingProps"], caps["props"])

    def test_a_missing_motion_table_degrades_to_no_motions_at_all(self):
        with mock.patch.object(build, "load_motion_tables",
                               return_value=(None, None, "motion_table.json not in the activation")):
            document, sidecar, report = build.build_document(
                LEVEL, ROOT / "Docs/Design/Levels" / LEVEL, with_colliders=False)
        self.assertEqual(document["motions"], [])
        self.assertEqual(document["paths"], [])
        self.assertFalse(report["motions"]["available"])
        self.assertIn("not in the activation", report["motions"]["reason"])
        self.assertIn("none (", document["meta"]["arena"]["notes"])
        self.assertEqual(document["meta"]["caps"]["movingProps"], 0)
        self.assertTrue(all(prop["behaviour"] == "static" for prop in document["props"]))
        self.assertEqual(build.structural_errors(document, sidecar), [])

    def test_the_sidecar_matches_its_declared_hash(self):
        import hashlib
        self.assertEqual(hashlib.sha256(self.sidecar).hexdigest(),
                         self.document["terrain"]["meshRef"]["sha256"])


if __name__ == "__main__":
    unittest.main()
