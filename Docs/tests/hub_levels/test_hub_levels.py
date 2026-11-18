"""The hub's level list (Docs/Design/StoryMode/hub_levels.json) is data the editor
assembler reads; these checks keep it well-formed without Unity."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
CATALOG = ROOT / "Docs/Design/StoryMode/hub_levels.json"
LEVELS = ROOT / "Docs/Design/Levels"


class HubLevelsTest(unittest.TestCase):
    def setUp(self):
        self.data = json.loads(CATALOG.read_text(encoding="utf-8"))

    def test_schema_and_room_block(self):
        self.assertEqual(self.data["schema"], "katamari.hub-levels/1")
        room = self.data["room"]
        for key in ("padSizeM", "padGapM", "padForwardM", "marginM"):
            self.assertIn(key, room)
        self.assertGreater(room["padSizeM"], 0)
        self.assertGreaterEqual(room["padGapM"], 0)

    def test_entries_are_complete_and_ids_unique(self):
        ids = []
        for lv in self.data["levels"]:
            required = ["id", "name", "scenes", "content"]
            if lv.get("kind", "round") == "round":
                required.append("entry")  # free-roam levels may omit it: the launcher falls back to the seat
            for key in required:
                self.assertTrue(lv.get(key), f"{lv.get('id')}: missing {key}")
            self.assertIsInstance(lv["scenes"], list)
            ids.append(lv["id"])
        self.assertEqual(len(ids), len(set(ids)))
        self.assertTrue(ids)

    def test_entry_lies_under_the_scene_roots_it_names(self):
        for lv in self.data["levels"]:
            self.assertFalse(lv["content"].startswith("/"))
            self.assertFalse(lv.get("entry", "").startswith("/"))

    def test_committed_levels_have_a_placement_document(self):
        # _ref_* levels are git-ignored and exist only on the operator's machine.
        for lv in self.data["levels"]:
            if lv["id"].startswith("_ref_"):
                continue
            if lv.get("kind") == "freeRoam":
                continue  # hand-built lab scenes have no placement document
            self.assertTrue(
                any((LEVELS / lv["id"] / n).exists()
                    for n in ("placement.json", "placement.v2.json")),
                lv["id"])

    def test_hub_document_has_no_level_pad(self):
        # Pads are generated from this list, one per offered level.
        doc = json.loads((LEVELS / "hub/placement.json").read_text(encoding="utf-8"))
        self.assertNotIn("select_picnic_corner", [s["id"] for s in doc["structures"]])
        self.assertEqual(doc["level"]["expected"]["structureCount"], len(doc["structures"]))

    def test_hub_room_is_wide_enough_for_the_pads_of_every_scene(self):
        # Same formula as HubSelectMath.RoomWidthNeeded (C#, Runtime/Hub): the assembler
        # measures the floor body's size x against it. The Python side cannot call C#, so
        # the formula is repeated here and HubSelectMathTests pins the C# values.
        room = self.data["room"]
        scenes = {}
        for lv in self.data["levels"]:
            for scene in lv["scenes"]:
                scenes[scene] = scenes.get(scene, 0) + 1
        doc = json.loads((LEVELS / "hub/placement.json").read_text(encoding="utf-8"))
        structures = {s["id"]: s for s in doc["structures"]}
        floor_width = structures["hub_floor"]["size"][0]
        wall_gap = (structures["boundary_x1"]["center"][0]
                    - structures["boundary_x0"]["center"][0])
        for scene, count in scenes.items():
            needed = (count * room["padSizeM"] + (count - 1) * room["padGapM"]
                      + 2 * room["marginM"])
            self.assertGreaterEqual(floor_width, needed, f"{scene}: floor")
            self.assertGreaterEqual(wall_gap, needed, f"{scene}: walls")

    # ---- schema /1 optional fields (all optional; absent = today's single-level behaviour)

    def test_optional_level_fields_are_well_typed(self):
        for lv in self.data["levels"]:
            lid = lv["id"]
            self.assertIn(lv.get("kind", "round"), ("round", "freeRoam"), lid)
            for key in ("allowAbort", "stars", "roundFromPlacement"):
                if key in lv:
                    self.assertIsInstance(lv[key], bool, f"{lid}: {key}")
            if "source" in lv:
                src = lv["source"]
                self.assertTrue(src.get("scene", "").endswith(".unity"), lid)
                self.assertTrue(src.get("takeRoots"), f"{lid}: takeRoots empty")
                for root in src["takeRoots"] + src.get("leaveBehind", []):
                    self.assertFalse(root.startswith("/"), lid)
            if "start" in lv:
                self.assertEqual(len(lv["start"]["center"]), 3, lid)
            if "island" in lv:
                off = lv["island"]["offset"]
                self.assertEqual(len(off), 3, lid)
                self.assertEqual(off[1], 0, f"{lid}: Y offset must be 0")
            if lv.get("kind") == "freeRoam":
                self.assertNotIn("roundFromPlacement", [k for k, v in lv.items() if v is True], lid)

    def test_hub_blocks_reference_known_levels_and_keep_islands_apart(self):
        ids = {lv["id"] for lv in self.data["levels"]}
        by_id = {lv["id"]: lv for lv in self.data["levels"]}
        for hub in self.data.get("hubs", []):
            self.assertTrue(hub["scene"].startswith("Assets/"), hub["id"])
            self.assertEqual(len(hub["levels"]), len(set(hub["levels"])), hub["id"])
            centres = [(1200.0, 0.0)]  # the hub room island
            for lid in hub["levels"]:
                self.assertIn(lid, ids)
                self.assertIn(hub["id"], by_id[lid]["scenes"], f"{lid} must list the hub scene")
                off = by_id[lid]["island"]["offset"]
                centres.append((off[0], off[2]))
            for i, a in enumerate(centres):
                for b in centres[i + 1:]:
                    dist = ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5
                    self.assertGreaterEqual(dist, 1000.0, f"{hub['id']}: islands {a} {b}")

    def test_unlicensed_sources_only_in_unlicensed_hub_scene(self):
        for hub in self.data.get("hubs", []):
            for lid in hub["levels"]:
                lv = next(x for x in self.data["levels"] if x["id"] == lid)
                if "_RefUnlicensed/" in lv.get("source", {}).get("scene", ""):
                    self.assertIn("_RefUnlicensed/", hub["scene"], hub["id"])

    def test_every_all_maps_level_in_a_hub_gets_a_pad(self):
        # AllMapsHub offers four levels, so the room must fit four pads (>= 14 m).
        for hub in self.data.get("hubs", []):
            self.assertGreaterEqual(len(hub["levels"]), 1)
        self.assertEqual(len(self.data["hubs"][0]["levels"]), 4)

    def test_every_level_declares_a_max_ball_diameter(self):
        # Owner 2026-11-18: the ball's size cap is a per-level parameter, as in the
        # original (MAS1 30 cm, Cancer 1.2 m). The hub writes one cap per level to the
        # motor before the round reset, so a level without an entry would silently
        # inherit the previous level's cap.
        for lv in self.data["levels"]:
            lid = lv["id"]
            self.assertIn("maxDiameterM", lv, lid)
            cap = lv["maxDiameterM"]
            self.assertIsInstance(cap, (int, float), lid)
            self.assertGreater(cap, 0, lid)
            start = lv.get("start", {}).get("diameterM", 0)
            if start:
                self.assertLess(start, cap, f"{lid}: the start ball must fit under its cap")

    def test_the_cancer_level_keeps_the_original_1_2_m_cap(self):
        # The original Cancer level's ball caps at about 1.2 m; eleven of its props need
        # a 1.27-6.45 m ball, so under the original cap they are never absorbable. That is
        # the owner's reason for restoring the cap (Docs/OPEN_DECISIONS.md (n)).
        by_id = {lv["id"]: lv for lv in self.data["levels"]}
        self.assertEqual(by_id["_ref_cancer"]["maxDiameterM"], 1.2)

    def test_our_own_levels_keep_the_authored_engineering_cap(self):
        # Our levels never had a gameplay cap of their own: they used the motor's single
        # authored 30 m envelope. The per-level decision must not silently tighten them.
        for lv in self.data["levels"]:
            if lv["id"].startswith("_ref_"):
                continue
            self.assertEqual(lv["maxDiameterM"], 30.0, lv["id"])


if __name__ == "__main__":
    unittest.main()
