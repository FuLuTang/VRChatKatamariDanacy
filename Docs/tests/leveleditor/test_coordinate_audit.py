"""Coordinate audit against known world boxes (the web renderer's contract).

The browser renders placement.json through one conversion (Unity -> three).  These
tests pin the *authoritative* Python side of that conversion to numbers a human can
verify against the Unity scene: the lounge chair's world box, a prop seated on the
flower bed, the 30 degree ramp's walking plane, and the boundary normals.  The JS
smoke test (`Tools/leveleditor/test/smoke.mjs`) then compares the rendered Box3 of
the same objects against these server-computed AABBs.
"""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402

LEVELS = ROOT / "Docs/Design/Levels"
STORY = LEVELS / "story_01_bench_size/placement.json"
CELL = LEVELS / "physics_test_field/C01/placement.json"


def doc_at(path):
    return core.load_doc(path)


def prop(doc, item_id):
    return next(prop for prop in doc["props"] if prop["itemId"] == item_id)


def structure(doc, structure_id):
    return next(entry for entry in doc["structures"] if entry["id"] == structure_id)


class StoryLevelCoordinateAudit(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.doc = doc_at(STORY)
        cls.manifests = core.load_manifests()

    def test_lounge_chair_matches_the_known_world_box(self):
        chair = prop(self.doc, "struct_lounge_chair_01")
        self.assertEqual(chair["def"], "lounge_chair")
        self.assertEqual(chair["aabb"], [1.6, 0.35, 0.6])
        low, high = core.world_aabb(chair["position"], chair["aabb"],
                                    chair["rotationQuat"])
        # The number the design notes quote: x[-12.6,-12.0], z[-10.4,-8.8], top 0.35.
        self.assertAlmostEqual(low[0], -12.6, places=4)
        self.assertAlmostEqual(high[0], -12.0, places=4)
        self.assertAlmostEqual(low[2], -10.4, places=4)
        self.assertAlmostEqual(high[2], -8.8, places=4)
        self.assertAlmostEqual(low[1], 0.0, places=4)
        self.assertAlmostEqual(high[1], 0.35, places=4)

    def test_a_prop_on_the_flower_bed_sits_exactly_on_its_surface(self):
        bed = structure(self.doc, "flower_bed")
        normal, point = core.structure_top_plane(bed)
        self.assertEqual(normal, (0.0, 1.0, 0.0))
        self.assertAlmostEqual(point[1], 0.26, places=4)
        apple = prop(self.doc, "apple_04")
        self.assertEqual(apple["support"], "flower_bed")
        low, _high = core.world_aabb(apple["position"], apple["aabb"],
                                     apple["rotationQuat"])
        self.assertAlmostEqual(low[1], 0.26, places=4)

    def test_the_ground_is_a_plane_through_its_centre(self):
        ground = structure(self.doc, "ground")
        self.assertEqual(ground["role"], "ground")
        self.assertAlmostEqual(core.ground_level(self.doc), 0.0, places=6)
        self.assertAlmostEqual(ground["center"][1], 0.0, places=6)
        # The visible slab's top is the authored plane (size.y/2 was added by the
        # renderer, not by the data).
        self.assertAlmostEqual(ground["size"][1], 0.04, places=6)

    def test_the_30_degree_ramp_walking_surface(self):
        ramp = structure(self.doc, "ramp_to_table")
        self.assertEqual(ramp["role"], "ramp")
        self.assertAlmostEqual(core.structure_slope_deg(ramp), 30.0, delta=0.05)
        normal, point = core.structure_top_plane(ramp)
        self.assertAlmostEqual(normal[1], 0.866, places=3)
        self.assertAlmostEqual(normal[2], -0.5, places=3)
        self.assertAlmostEqual(point[0], -13.1, places=4)
        self.assertAlmostEqual(point[1], 0.35, places=3)
        # The plane through that point has the authored walking height at its centre.
        self.assertAlmostEqual(
            core.plane_height_at(normal, point, point[0], point[2]), 0.35, places=3)

    def test_boundary_walls_point_inward(self):
        west = structure(self.doc, "boundary_x0")
        east = structure(self.doc, "boundary_x1")
        north = structure(self.doc, "boundary_z1")
        self.assertAlmostEqual(core.structure_up(west)[0], 1.0, places=5)
        self.assertAlmostEqual(core.structure_up(east)[0], -1.0, places=5)
        self.assertAlmostEqual(core.structure_up(north)[2], -1.0, places=5)
        for wall in (west, east, north):
            self.assertEqual(wall["role"], "boundary")
            self.assertFalse(wall["support"])

    def test_the_design_total_and_the_derived_diameter_fields(self):
        # The totals are the live level's content, so derive them from the props
        # rather than pinning 127 / 1500 here; the design note's numbers must still
        # match what is actually placed.
        expected = self.doc["level"]["expected"]
        self.assertEqual(expected["propCount"], len(self.doc["props"]))
        # Steps C+D: the content total is DERIVED growth in mm^3; the legacy valueUnits
        # total is gone from the written file.
        self.assertEqual(expected["growthMm3"], core.growth_mm3_total(self.doc))
        for entry in self.doc["props"]:
            self.assertAlmostEqual(entry["absorbDiameterReq"],
                                   core.absorb_diameter_req(entry["aabb"]), places=3)
        report = core.validate(self.doc, manifests=self.manifests)
        self.assertTrue(report.ok, report.errors)


class PhysicsCellCoordinateAudit(unittest.TestCase):
    def test_c01_is_a_cell_with_its_own_ground_and_walls(self):
        doc = doc_at(CELL)
        self.assertEqual(doc["level"]["id"], "physics_test_field/C01")
        self.assertEqual(doc["level"]["arena"]["id"], "C01")
        self.assertEqual(len(doc["props"]), 0)
        self.assertEqual(core.ground_level(doc), 0.0)
        ground = structure(doc, "ground")
        self.assertAlmostEqual(ground["center"][0], -10.5, places=6)
        self.assertAlmostEqual(ground["center"][2], 11.0, places=6)
        ramp = structure(doc, "C01_ramp")
        self.assertAlmostEqual(core.structure_slope_deg(ramp), 5.0, delta=0.05)
        self.assertAlmostEqual(core.structure_top_plane(ramp)[0][1], 0.9962, places=3)
        self.assertEqual(len(doc["structures"]), 7)  # ground + 4 walls + ramp + landing


if __name__ == "__main__":
    unittest.main()
