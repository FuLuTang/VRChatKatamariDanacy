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
        self.assertEqual(self.data["schema"], "katamari.hub-levels/0")
        room = self.data["room"]
        for key in ("padSizeM", "padGapM", "padForwardM", "marginM"):
            self.assertIn(key, room)
        self.assertGreater(room["padSizeM"], 0)
        self.assertGreaterEqual(room["padGapM"], 0)

    def test_entries_are_complete_and_ids_unique(self):
        ids = []
        for lv in self.data["levels"]:
            for key in ("id", "name", "scenes", "content", "entry"):
                self.assertTrue(lv.get(key), f"{lv.get('id')}: missing {key}")
            self.assertIsInstance(lv["scenes"], list)
            ids.append(lv["id"])
        self.assertEqual(len(ids), len(set(ids)))
        self.assertTrue(ids)

    def test_entry_lies_under_the_scene_roots_it_names(self):
        for lv in self.data["levels"]:
            self.assertFalse(lv["content"].startswith("/"))
            self.assertFalse(lv["entry"].startswith("/"))

    def test_committed_levels_have_a_placement_document(self):
        # _ref_* levels are git-ignored and exist only on the operator's machine.
        for lv in self.data["levels"]:
            if lv["id"].startswith("_ref_"):
                continue
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


if __name__ == "__main__":
    unittest.main()
