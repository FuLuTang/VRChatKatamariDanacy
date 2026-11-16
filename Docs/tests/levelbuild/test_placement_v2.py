"""The v2 validator: one case per ``[tag]``, plus the reference level's clean run.

``Tools/levelbuild/placement_v2.py`` implements LEVEL_FORMAT_V2 §9.  These tests pin the
contract that matters:

* every message starts with a stable ``[tag]`` and names the path, the expectation and what
  was actually found (so a failure is actionable without reading the code);
* a document that is *sound* passes with no errors — the reference level's own file is the
  proof (skipped when the extraction is not on this machine);
* the three things the format deliberately reports as notes/warnings instead of errors stay
  that way: a declared ``overlapBudget``, a prop inside a scenery solid (no §9.2 rule), and
  ``absorbable`` disagreeing with the gate (authorial in v2);
* a v1 document is refused with ``[schema_legacy]`` rather than half-validated.
"""

import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import placement_v2  # noqa: E402

LEVEL_DIR = ROOT / "Docs/Design/Levels/_ref_cancer"


def tiny_document():
    """The smallest *valid* v2 document (the §7.1 platform, one acorn)."""
    size = (0.024, 0.03, 0.0228)
    growth = core.item_growth_mm3(size, 0.7, 0.8)
    gate = core.absorb_diameter_req_mm(size, 0.7)
    return {
        "schema": placement_v2.SCHEMA,
        "meta": {
            "id": "tiny_pad",
            "scene": "PhysicsTestField",
            "units": "m",
            "convention": "unity-y-up-left-handed-quat-xyzw",
            "ball": {"startDiameterM": 0.30, "targetDiameterM": 0.60,
                     "maxDiameterRef": "level", "maxDiameterM": 2.20,
                     "start": [0.0, 0.15, 0.0], "spawnSource": "authored"},
            "timeLimitS": 0,
            "playerSpawn": [0.0, 0.5, -2.4],
            "groundY": -2.0,
            "bounds": [[-2.0, -2.0], [2.0, 2.0]],
            "spawnClearanceM": 0.2,
            "zones": [{"id": "pad", "code": 0, "kind": "ground"}],
            "arena": {"id": "tiny_pad"},
            "caps": {"props": 16, "terrainTriangles": 512, "terrainVertices": 1024,
                     "staticSolids": 8, "activityWalls": 8, "movingProps": 0, "animators": 0},
            "budget": {"planes": 8, "boxes": 8, "ramps": 4, "blocking": 6, "propSupports": 6},
            "expected": {"propCount": 1, "growthMm3": growth, "structureCount": 0,
                         "staticObbs": 0, "overlapBudget": 0, "terrainTriangles": 2},
            "source": {"kind": "authored"},
        },
        "terrain": {
            "id": "pad", "kind": "mesh", "winding": "ccw",
            "vertices": [[-2.0, 0.0, -2.0], [2.0, 0.0, -2.0], [2.0, 0.0, 2.0],
                         [-2.0, 0.0, 2.0]],
            "indices": [0, 2, 1, 0, 3, 2],
            "triangles": {"count": 2, "zoneIds": [0, 0], "classes": [1, 1]},
            "broadPhase": {"kind": "aabb", "boxes": [
                {"id": "pad_0", "min": [-2.0, -0.01, -2.0], "max": [2.0, 0.01, 2.0],
                 "triangleRange": [0, 2]}]},
            "chunks": [{"id": "c0", "boxRange": [0, 1], "triangleRange": [0, 2]}],
            "grid": {"cellM": 4.0, "origin": [-2.0, -2.0], "cellsX": 1, "cellsZ": 1},
            "materialSegments": [{"id": "ground", "firstIndex": 0, "indexCount": 6}],
            "source": {"kind": "authored"},
        },
        "structures": {"solids": []},
        "activityArea": {
            "walls": [{"id": "w_n", "points": [[-2.0, 2.0], [2.0, 2.0]], "closed": False,
                       "heightM": 3.0, "thicknessM": 0.2, "kind": "invisible"}],
            "killPlane": {"y": -3.0, "respawn": "ballStart", "zoneId": 0},
            "softMarginM": 0.0, "source": "authored"},
        "props": [{"itemId": "acorn_01", "def": "acorn", "kind": "nature",
                   "behaviour": "static", "inherit": "none", "zone": "pad",
                   "position": [-0.8, 0.015, -0.6], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
                   "aabb": [0.024, 0.03, 0.0228], "volumeK": 0.7, "volumePenalty": 0.8,
                   "absorbDiameterReqMm": gate, "growthMm3": growth, "absorbable": True}],
        "defs": [{"id": "acorn", "kind": "nature",
                  "model": "Assets/VRC_Katamari/Art/Props/acorn.fbx",
                  "volumeK": 0.7,
                  "collider": {"type": "sphere", "center": [0.0, 0.0, 0.0],
                               "radius": 0.012},
                  "scalePolicy": "uniform"}],
        "motions": [], "paths": [], "cameras": [], "renderPresets": [], "regions": [],
    }


class Base(unittest.TestCase):
    def setUp(self):
        self.doc = tiny_document()

    def run_validator(self, doc=None):
        return placement_v2.validate(doc if doc is not None else self.doc)

    def tags(self, report):
        return [message.split(" ", 1)[0] for message in report.errors]

    def assert_tag(self, tag, doc=None):
        report = self.run_validator(doc)
        self.assertIn(tag, self.tags(report), "expected %s in:\n%s"
                      % (tag, "\n".join(report.errors) or "(no errors)"))
        return report

    def test_the_tiny_document_is_clean(self):
        report = self.run_validator()
        self.assertEqual(report.errors, [], "\n".join(report.errors))

    def test_every_message_names_path_expectation_and_actual(self):
        doc = copy.deepcopy(self.doc)
        doc["props"][0]["aabb"] = [-1.0, 0.03, 0.0228]
        report = self.run_validator(doc)
        self.assertTrue(report.errors)
        for message in report.errors:
            self.assertTrue(message.startswith("["), message)
            self.assertIn("props['acorn_01']", message)
            self.assertIn("expected", message)
            self.assertIn("got", message)


class MetaTags(Base):
    def test_units_and_convention(self):
        self.doc["meta"]["units"] = "cm"
        self.assert_tag("[meta_units]")
        self.doc["meta"]["convention"] = "blender-z-up"
        self.assert_tag("[meta_convention]")

    def test_unknown_meta_key_is_an_error(self):
        self.doc["meta"]["maxDiameter"] = 2.2
        self.assert_tag("[meta_unknown_key]")

    def test_start_diameter_missing_or_above_the_cap(self):
        del self.doc["meta"]["ball"]["startDiameterM"]
        self.assert_tag("[meta_start_diameter]")
        self.doc = tiny_document()
        self.doc["meta"]["ball"]["startDiameterM"] = 3.0
        self.assert_tag("[meta_start_diameter]")

    def test_cap_override_is_a_warning_for_a_story_level(self):
        self.doc["meta"]["scene"] = "StoryMode"
        self.doc["meta"]["timeLimitS"] = 60
        report = self.run_validator()
        self.assertIn("[meta_cap_override]", [w.split(" ", 1)[0] for w in report.warnings])
        self.assertEqual(report.errors, [])

    def test_time_limit_is_required_for_a_story_level(self):
        self.doc["meta"]["scene"] = "StoryMode"
        self.assert_tag("[meta_time_limit]")

    def test_ground_y_must_sit_below_the_terrain(self):
        self.doc["meta"]["groundY"] = 0.5
        self.assert_tag("[meta_ground_y]")

    def test_zone_codes_are_unique_and_zero_appears_once(self):
        self.doc["meta"]["zones"] = [{"id": "a", "code": 0}, {"id": "b", "code": 0}]
        self.assert_tag("[meta_zone_code]")
        self.doc["meta"]["zones"] = [{"id": "a", "code": 300}]
        self.assert_tag("[meta_zone_code]")

    def test_caps_must_be_complete(self):
        del self.doc["meta"]["caps"]["animators"]
        self.assert_tag("[meta_caps]")


class TerrainTags(Base):
    def test_zero_area_triangle(self):
        self.doc["terrain"]["vertices"][2] = [2.0, 0.0, -2.0]   # collides with vertex 1
        self.assert_tag("[terrain_mesh]")

    def test_index_out_of_range(self):
        self.doc["terrain"]["indices"] = [0, 2, 9, 0, 3, 2]
        self.assert_tag("[terrain_mesh]")

    def test_winding_is_checked_by_shared_edges(self):
        self.doc["terrain"]["indices"] = [0, 2, 1, 0, 2, 3]     # second triangle reversed
        self.assert_tag("[terrain_winding]")

    def test_broad_phase_must_tile_the_triangles(self):
        self.doc["terrain"]["broadPhase"]["boxes"][0]["triangleRange"] = [1, 2]
        self.assert_tag("[terrain_broadphase]")
        self.doc = tiny_document()
        self.doc["terrain"]["broadPhase"]["boxes"][0]["min"] = [0.5, -0.01, 0.5]
        self.assert_tag("[terrain_broadphase]")

    def test_zone_codes_must_be_declared(self):
        self.doc["terrain"]["triangles"]["zoneIds"] = [0, 7]
        self.assert_tag("[terrain_zone]")

    def test_grid_must_cover_the_broad_phase(self):
        self.doc["terrain"]["grid"]["origin"] = [1.0, 1.0]
        self.assert_tag("[terrain_grid]")

    def test_chunk_budget(self):
        self.doc["meta"]["caps"]["terrainTriangles"] = 1
        self.assert_tag("[terrain_budget]")

    def test_sidecar_hash_and_malformed_payload(self):
        terrain = tiny_document()["terrain"]
        self.doc["terrain"].pop("vertices")
        self.doc["terrain"].pop("indices")
        self.doc["terrain"].pop("triangles")
        self.doc["terrain"]["meshRef"] = {"path": "terrain/x.katerrain", "sha256": "0" * 64,
                                         "vertexCount": 4, "indexCount": 6,
                                         "triangleCount": 2, "indexWidth": 2,
                                         "zoneIds": True, "classes": True}
        report = placement_v2.validate(self.doc, sidecar_bytes_=b"not a sidecar")
        self.assertIn("[terrain_sidecar]", self.tags(report))
        sidecar, _width = placement_v2.sidecar_bytes(
            {"vertices": terrain["vertices"], "indices": terrain["indices"],
             "zoneIds": [0, 0], "classes": [1, 1]})
        report = placement_v2.validate(self.doc, sidecar_bytes_=sidecar)
        self.assertIn("[terrain_sidecar]", self.tags(report))   # sha256 is still zeros

    def test_the_sidecar_round_trips(self):
        terrain = tiny_document()["terrain"]
        data, width = placement_v2.sidecar_bytes(
            {"vertices": terrain["vertices"], "indices": terrain["indices"],
             "zoneIds": [0, 0], "classes": [1, 1]})
        parsed = placement_v2.read_sidecar(data)
        self.assertEqual(width, 2)
        self.assertEqual(parsed["counts"], {"vertices": 4, "indices": 6, "triangles": 2})
        self.assertEqual(parsed["zoneIds"], [0, 0])
        self.assertEqual(parsed["classes"], [1, 1])
        self.assertEqual(parsed["trailing"], 0)


class StructureTags(Base):
    def test_boundary_and_ground_move_out_of_structures(self):
        self.doc["structures"]["solids"] = [
            {"id": "g", "role": "ground", "shape": "plane", "center": [0, 0, 0],
             "size": [1, 1, 1], "rotationQuat": [0, 0, 0, 1]}]
        self.assert_tag("[structure_role]")

    def test_a_proxy_solid_needs_a_valid_collider_and_a_source(self):
        solid = {"id": "tree", "kind": "tree", "role": "box", "shape": "proxy",
                 "center": [0.0, 1.0, 0.0], "size": [0.6, 2.0, 0.6],
                 "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
                 "absorbable": False, "support": True,
                 "collider": {"type": "sphere", "center": [0.0, 0.0, 0.0], "radius": 0.3},
                 "proxySource": "def"}
        self.doc["structures"]["solids"] = [solid]
        self.doc["meta"]["expected"]["structureCount"] = 1
        self.doc["meta"]["expected"]["staticObbs"] = 1
        report = self.run_validator()
        self.assertEqual(report.errors, [], "\n".join(report.errors))
        broken = copy.deepcopy(self.doc)
        del broken["structures"]["solids"][0]["collider"]
        self.assertIn("[structure_proxy]", self.tags(self.run_validator(broken)))
        broken = copy.deepcopy(self.doc)
        broken["structures"]["solids"][0]["proxySource"] = "guessed"
        self.assertIn("[structure_proxy]", self.tags(self.run_validator(broken)))
        broken = copy.deepcopy(self.doc)
        broken["structures"]["solids"][0]["shape"] = "box"      # box + collider is a mistake
        self.assertIn("[structure_proxy]", self.tags(self.run_validator(broken)))

    def test_capacity(self):
        solid = {"id": "s0", "role": "box", "shape": "box", "center": [0, 0, 0],
                 "size": [1, 1, 1], "rotationQuat": [0, 0, 0, 1], "collision": "box"}
        self.doc["structures"]["solids"] = [dict(solid, id="s%d" % index) for index in range(9)]
        self.assert_tag("[structure_capacity]")


class ActivityTags(Base):
    def test_a_wall_needs_two_points_and_a_height(self):
        self.doc["activityArea"]["walls"][0]["points"] = [[0.0, 0.0]]
        self.assert_tag("[activity_wall]")
        self.doc = tiny_document()
        self.doc["activityArea"]["walls"][0]["heightM"] = 0.0
        self.assert_tag("[activity_wall]")

    def test_a_gate_open_from_the_start_is_a_warning(self):
        self.doc["activityArea"]["walls"][0]["minBallDiameterM"] = 0.2
        report = self.run_validator()
        self.assertIn("[activity_wall_gate]", [w.split(" ", 1)[0] for w in report.warnings])

    def test_kill_plane_must_be_below_the_floor(self):
        self.doc["activityArea"]["killPlane"]["y"] = 0.0
        self.assert_tag("[activity_kill_plane]")
        self.doc["activityArea"]["killPlane"] = {"y": -3.0, "respawn": "somewhere"}
        self.assert_tag("[activity_kill_plane]")


class PropTags(Base):
    def test_duplicate_and_blank_ids(self):
        self.doc["props"].append(copy.deepcopy(self.doc["props"][0]))
        self.assert_tag("[prop_id]")

    def test_unknown_prop_key(self):
        self.doc["props"][0]["valueUnits"] = 1        # allowed (deprecated)
        self.doc["props"][0]["group"] = "other"       # v1 key, not v2
        self.assert_tag("[prop_key]")

    def test_parent_must_exist_and_be_acyclic(self):
        self.doc["props"][0]["parent"] = "ghost_01"
        self.assert_tag("[prop_parent]")
        self.doc["props"][0]["parent"] = "acorn_01"
        self.assert_tag("[prop_parent]")
        self.doc["props"].append({"itemId": "acorn_02", "def": "acorn", "kind": "nature",
                                  "behaviour": "static", "parent": "acorn_03",
                                  "position": [1.0, 0.02, 1.0],
                                  "rotationQuat": [0, 0, 0, 1],
                                  "aabb": [0.02, 0.02, 0.02], "absorbDiameterReqMm": 100,
                                  "growthMm3": 1000, "absorbable": True})
        self.doc["props"].append(dict(self.doc["props"][1], itemId="acorn_03",
                                      parent="acorn_02"))
        self.assert_tag("[prop_parent]")

    def test_attached_props_may_be_uniformly_scaled_but_not_non_uniformly(self):
        self.doc["props"].append({"itemId": "acorn_02", "def": "acorn", "kind": "nature",
                                  "behaviour": "static", "parent": "acorn_01",
                                  "scale": [0.1, 0.1, 0.1],
                                  "position": [0.4, 0.02, 0.4],
                                  "rotationQuat": [0, 0, 0, 1],
                                  "aabb": [0.0024, 0.003, 0.00228],
                                  "absorbDiameterReqMm": 10, "growthMm3": 10,
                                  "absorbable": True})
        report = self.run_validator()
        self.assertNotIn("[prop_scale_attached]", self.tags(report))
        self.doc["props"][-1]["scale"] = [0.1, 0.2, 0.1]
        self.assert_tag("[prop_scale_attached]")

    def test_non_uniform_needs_static_no_parent_and_no_capsule_proxy(self):
        doc = tiny_document()
        doc["props"][0]["scale"] = [1.0, 0.5, 1.0]
        doc["props"][0]["aabb"] = [0.024, 0.015, 0.0228]
        doc["props"][0]["absorbDiameterReqMm"] = core.absorb_diameter_req_mm(
            (0.024, 0.015, 0.0228), 0.7)
        doc["props"][0]["growthMm3"] = core.item_growth_mm3((0.024, 0.015, 0.0228), 0.7, 0.8)
        doc["meta"]["expected"]["growthMm3"] = doc["props"][0]["growthMm3"]
        report = placement_v2.validate(doc)
        self.assertNotIn("[prop_scale_nonuniform]", self.tags(report))   # sphere proxy
        doc["defs"][0]["collider"] = {"type": "capsule", "center": [0, 0, 0], "radius": 0.01,
                                      "halfHeight": 0.0, "axis": "y"}
        self.assert_tag("[prop_scale_nonuniform]", doc)

    def test_derived_columns_are_recomputed(self):
        self.doc["props"][0]["growthMm3"] += 1
        self.assert_tag("[prop_growth_stale]")
        self.doc = tiny_document()
        self.doc["props"][0]["absorbDiameterReqMm"] += 1
        self.assert_tag("[prop_absorb_req_stale]")

    def test_behaviour_and_motion(self):
        self.doc["props"][0]["behaviour"] = "moving"
        self.assert_tag("[prop_behaviour]")
        self.doc = tiny_document()
        self.doc["props"][0]["behaviour"] = "motion"
        self.assert_tag("[prop_behaviour]")              # no motion id
        self.doc["props"][0]["motion"] = "ghost_motion"
        self.assert_tag("[prop_motion]")

    def test_inherit_requires_a_parent(self):
        self.doc["props"][0]["inherit"] = "full"
        self.assert_tag("[prop_inherit]")

    def test_rotation_quaternion_must_be_unit_and_canonical(self):
        self.doc["props"][0]["rotationQuat"] = [0.0, 0.0, 0.0, 2.0]
        self.assert_tag("[prop_rotation]")
        self.doc["props"][0]["rotationQuat"] = [0.0, 0.0, 0.0, -1.0]
        self.assert_tag("[prop_rotation]")

    def test_instance_index_is_a_distinct_non_negative_integer(self):
        self.doc["props"][0]["instanceIndex"] = 7
        report = self.run_validator()
        self.assertNotIn("[prop_instance_index]", self.tags(report))
        for value in (-1, 1.5, True, "7"):
            broken = copy.deepcopy(self.doc)
            broken["props"][0]["instanceIndex"] = value
            self.assertIn("[prop_instance_index]",
                          self.tags(self.run_validator(broken)), value)
        # duplicates are an error (two props cannot be the same spawn record)
        duplicate = copy.deepcopy(self.doc)
        duplicate["props"].append(dict(copy.deepcopy(duplicate["props"][0]),
                                       itemId="acorn_02", instanceIndex=7,
                                       position=[1.5, 0.02, 1.5]))
        duplicate["meta"]["expected"]["propCount"] = 2
        duplicate["meta"]["expected"]["growthMm3"] = 2 * duplicate["props"][0]["growthMm3"]
        self.assertIn("[prop_instance_index]", self.tags(self.run_validator(duplicate)))
        # no upper bound: an index past the extractor's record count is provenance, not an error
        far = copy.deepcopy(self.doc)
        far["props"][0]["instanceIndex"] = 4000
        self.assertNotIn("[prop_instance_index]", self.tags(self.run_validator(far)))

    def test_phase_range(self):
        self.doc["props"][0]["phase"] = 1.5
        self.assert_tag("[prop_phase]")

    def test_clip_start_needs_clips(self):
        self.doc["props"][0]["clipStart"] = 0
        self.assert_tag("[prop_clip]")

    def test_bounds(self):
        self.doc["props"][0]["position"] = [9.0, 0.015, 0.0]
        self.assert_tag("[prop_bounds]")

    def test_overlap_is_an_error_without_a_budget(self):
        self.doc["meta"]["expected"].pop("overlapBudget")
        self.doc["props"].append(dict(copy.deepcopy(self.doc["props"][0]),
                                      itemId="acorn_02", position=[-0.79, 0.015, -0.6]))
        self.assert_tag("[prop_overlap]")

    def test_a_declared_budget_is_a_warning_until_it_is_exceeded(self):
        second = dict(copy.deepcopy(self.doc["props"][0]), itemId="acorn_02",
                      position=[-0.79, 0.015, -0.6])
        self.doc["props"].append(second)
        self.doc["meta"]["expected"]["propCount"] = 2
        self.doc["meta"]["expected"]["growthMm3"] = 2 * second["growthMm3"]
        self.doc["meta"]["expected"]["overlapBudget"] = 1
        report = self.run_validator()
        self.assertEqual(report.errors, [], "\n".join(report.errors))
        self.assertIn("[prop_overlap]", [w.split(" ", 1)[0] for w in report.warnings])
        third = dict(copy.deepcopy(second), itemId="acorn_03", position=[-0.78, 0.015, -0.6])
        self.doc["props"].append(third)
        self.doc["meta"]["expected"]["propCount"] = 3
        self.doc["meta"]["expected"]["growthMm3"] = 3 * second["growthMm3"]
        self.assert_tag("[prop_overlap]")

    def test_absorbable_disagreeing_with_the_gate_is_a_note_not_an_error(self):
        self.doc["props"][0]["absorbable"] = False
        report = self.run_validator()
        self.assertEqual(report.errors, [])
        self.assertTrue(any("absorbable" in note for note in report.notes))


class DefAndMotionTags(Base):
    def test_def_model_and_speed_index(self):
        self.doc["defs"][0]["model"] = "../outside.fbx"
        self.assert_tag("[def_model]")
        self.doc = tiny_document()
        self.doc["defs"][0]["speedIndex"] = 12
        self.assert_tag("[def_speed_index]")

    def test_collider_schema(self):
        self.doc["defs"][0]["collider"] = {"type": "sphere", "center": [0, 0, 0],
                                           "radius": -1.0}
        self.assert_tag("[collider]")

    def test_clips(self):
        self.doc["defs"][0]["clips"] = [{"id": "a", "fps": 0}] * 7
        report = self.run_validator()
        self.assertIn("[def_clips]", self.tags(report))

    def test_motions_need_ids_paths_and_speeds(self):
        self.doc["motions"] = [{"id": "m0", "class": "teleport"}]
        self.assert_tag("[motion_class]")
        self.doc["motions"] = [{"id": "m0", "class": "path_collision"}]
        self.assert_tag("[motion_path]")
        self.doc["motions"] = [{"id": "m0", "class": "sway",
                                "speed": {"source": "explicit"}}]
        self.assert_tag("[motion_speed]")
        self.doc["motions"] = [{"id": "m0", "class": "sway"}, {"id": "m0", "class": "sway"}]
        self.assert_tag("[motion_id]")

    def test_paths_points_and_speed(self):
        self.doc["paths"] = [{"id": "p0", "points": [[0, 0, 0], [0.01, 0, 0]]}]
        self.assert_tag("[path_points]")
        self.doc["paths"] = [{"id": "p0", "points": [[0, 0, 0], [1, 0, 0]],
                              "speedCmPerSec": 900}]
        self.assert_tag("[path_speed]")


class CapsAndSchemaTags(Base):
    def test_caps_props_and_motion(self):
        self.doc["meta"]["caps"]["props"] = 0
        self.assert_tag("[caps_props]")
        self.doc = tiny_document()
        self.doc["meta"]["caps"]["animators"] = 9
        self.assert_tag("[caps_motion]")
        self.doc["meta"]["caps"]["movingProps"] = 9
        self.assert_tag("[caps_motion]")

    def test_a_v1_document_is_refused_with_schema_legacy(self):
        self.doc["schema"] = core.SCHEMA
        self.assert_tag("[schema_legacy]")

    def test_an_unknown_schema_is_refused(self):
        self.doc["schema"] = "katamari.placement/9"
        self.assert_tag("[schema]")

    def test_canonical_text_is_stable_and_ordered(self):
        text = placement_v2.canonical_text(self.doc)
        again = placement_v2.canonical_text(json.loads(text))
        self.assertEqual(text, again)
        self.assertTrue(text.endswith("\n"))
        self.assertLess(text.index('"schema"'), text.index('"meta"'))


class SharedRuleTests(unittest.TestCase):
    def test_the_overlap_scan_is_the_one_v1_uses(self):
        boxes = {"a": ((-1.0, 0.0, -1.0), (1.0, 1.0, 1.0)),
                 "b": ((0.5, 0.0, -1.0), (2.5, 1.0, 1.0)),
                 "c": ((10.0, 0.0, 10.0), (11.0, 1.0, 11.0))}
        pairs = core.overlap_scan(boxes)
        self.assertEqual([(left, right) for left, right, _f in pairs], [("a", "b")])
        self.assertEqual(core.overlap_scan(boxes, lambda left, right: True), [])

    def test_proxy_penetration_matches_the_shape_not_the_box(self):
        center = (0.0, 1.0, 0.0)
        quat = (0.0, 0.0, 0.0, 1.0)
        sphere = {"type": "sphere", "center": [0.0, 0.0, 0.0], "radius": 0.5}
        self.assertGreater(core.proxy_penetration((0.0, 1.0, 0.0), sphere, center, quat), 0.4)
        self.assertLess(core.proxy_penetration((0.0, 1.0, 0.6), sphere, center, quat), 0.0)
        box = {"type": "box", "center": [0.0, 0.0, 0.0], "size": [1.0, 2.0, 1.0]}
        # a box-corner point that the AABB would call "inside" is outside a sphere proxy
        self.assertLess(core.proxy_penetration((0.45, 1.0, 0.45), sphere, center, quat), 0.0)
        self.assertGreater(core.proxy_penetration((0.45, 1.0, 0.45), box, center, quat), 0.0)
        capsule = {"type": "capsule", "center": [0.0, 0.0, 0.0], "radius": 0.1,
                   "halfHeight": 0.5, "axis": "y"}
        self.assertGreater(core.proxy_penetration((0.0, 1.4, 0.0), capsule, center, quat), 0.0)
        self.assertLess(core.proxy_penetration((0.0, 1.7, 0.0), capsule, center, quat), 0.0)
        hull = {"type": "convex", "center": [0.0, 0.0, 0.0],
                "hull": [-0.5, -0.5, -0.5, 0.5, -0.5, -0.5, 0.0, 0.5, -0.5,
                         -0.5, -0.5, 0.5, 0.5, -0.5, 0.5, 0.0, 0.5, 0.5]}
        self.assertGreater(core.proxy_penetration((0.0, 1.0, 0.0), hull, center, quat), 0.0)
        self.assertLess(core.proxy_penetration((1.0, 1.0, 0.0), hull, center, quat), 0.0)

    def test_the_proxy_mode_drops_the_coarse_box_hits(self):
        props = [{"itemId": "crab", "position": [0.6, 0.1, 0.6], "aabb": [0.1, 0.1, 0.1],
                  "rotationQuat": [0.0, 0.0, 0.0, 1.0]}]
        boxes = {"crab": core.world_aabb((0.6, 0.1, 0.6), (0.1, 0.1, 0.1), (0, 0, 0, 1))}
        base = {"id": "tree", "role": "box", "shape": "box", "center": [0.0, 1.0, 0.0],
                "size": [2.0, 2.0, 2.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
                "collision": "box"}
        # the AABB box reaches the crab; the tree's actual trunk (a thin sphere) does not
        as_box = {"tree": dict(base)}
        as_proxy = {"tree": dict(base, shape="proxy",
                                 collider={"type": "sphere", "center": [0.0, 0.0, 0.0],
                                           "radius": 0.2})}
        self.assertEqual(len(core.structure_penetrations(props, boxes, as_box, 0.6)), 1)
        self.assertEqual(len(core.structure_penetrations(props, boxes, as_proxy, 0.6,
                                                         proxy="prefer")), 0)

    def test_structure_penetrations_are_shared(self):
        props = [{"itemId": "p", "position": [0.0, 0.05, 0.0], "aabb": [0.2, 0.1, 0.2],
                  "rotationQuat": [0.0, 0.0, 0.0, 1.0]}]
        boxes = {"p": core.world_aabb((0.0, 0.05, 0.0), (0.2, 0.1, 0.2), (0, 0, 0, 1))}
        solids = {"s": {"id": "s", "role": "box", "shape": "box", "center": [0.0, 0.1, 0.0],
                        "size": [1.0, 1.0, 1.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
                        "collision": "box"}}
        hits = core.structure_penetrations(props, boxes, solids, 0.6)
        self.assertEqual(len(hits), 1)
        self.assertGreater(hits[0][2], 0.0)


@unittest.skipUnless((LEVEL_DIR / "placement.v2.json").is_file(),
                     "the reference level's v2 document is not on this machine")
class ReferenceLevelTests(unittest.TestCase):
    """The shipped reference document must be *sound*: no structural error at all."""

    @classmethod
    def setUpClass(cls):
        cls.document = json.loads((LEVEL_DIR / "placement.v2.json").read_text("utf-8"))
        sidecar = LEVEL_DIR / cls.document["terrain"]["meshRef"]["path"]
        cls.sidecar = sidecar.read_bytes() if sidecar.is_file() else None

    def test_zero_errors(self):
        report = placement_v2.validate(self.document, path=LEVEL_DIR / "placement.v2.json",
                                       sidecar_bytes_=self.sidecar)
        self.assertIsNotNone(self.sidecar, "the sidecar is missing")
        self.assertEqual(report.errors, [], "\n".join(report.errors))

    def test_only_the_approved_soft_items_remain(self):
        report = placement_v2.validate(self.document, sidecar_bytes_=self.sidecar)
        tags = {message.split(" ", 1)[0] for message in report.warnings}
        self.assertEqual(tags, {"[prop_overlap]"}, "unexpected warning tags: %s" % tags)
        notes = " ".join(report.notes)
        self.assertIn("24 prop(s) sink into a scenery solid's fitted proxy", notes)
        self.assertIn("raw-AABB criterion would count 47", notes)
        self.assertIn("sparrow_001", notes)            # D-5 declaration + non-uniform notice
        self.assertEqual(len(report.warnings), 1)      # the declared overlap budget
        self.assertEqual(len(report.notes), 3)

    def test_the_budget_equals_the_measured_pairs(self):
        report = placement_v2.validate(self.document, sidecar_bytes_=self.sidecar)
        expected = self.document["meta"]["expected"]["overlapBudget"]
        message = report.warnings[0]
        self.assertIn("%d pair(s) declared" % expected, message)

    def test_animators_match_the_original_load(self):
        caps = self.document["meta"]["caps"]
        self.assertEqual(caps["animators"], 153)
        self.assertIn("153", self.document["meta"]["arena"]["notes"])
        self.assertLessEqual(caps["animators"], caps["props"])


if __name__ == "__main__":
    unittest.main()
