"""Take-name mapping of the skinned GLB -> FBX converter (no Blender needed).

    python3 -m unittest discover -s Docs/tests -p 'test_skinned_take_mapping.py'

The converter module keeps ``bpy`` imports inside its functions, so its pure mapping and
duration checks load here with fake action/clip data.
"""

import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location(
    "skinned_glb_to_fbx", ROOT / "Tools/levelbuild/blender/skinned_glb_to_fbx.py")
conv = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(conv)

# The out-of-repo exporter names clips its own way; the converter only trusts each clip's slot.
CLIPS = [
    {"slot": 0, "name": "c0", "seconds": 0.9667},
    {"slot": 1, "name": "c1", "seconds": 0.4833},
    {"slot": 2, "name": "c2", "seconds": 0.1},
    {"slot": 3, "name": "c3", "seconds": 0.9667},
    {"slot": 4, "name": "c4", "seconds": 0.9667},
]
# The glTF importer's order is alphabetical by Action name, which is NOT slot order.
ACTIONS = ["c3_root", "c2_root", "c4_root", "c1_root", "c0_root"]


class TakeNames(unittest.TestCase):
    def test_suffix_is_stripped(self):
        self.assertEqual(conv.clip_name_of("c1_root"), "c1")
        self.assertEqual(conv.clip_name_of("c3"), "c3")

    def test_alphabetical_actions_map_by_name_not_by_index(self):
        mapping = conv.take_names(ACTIONS, CLIPS)
        self.assertEqual(mapping, {"c0_root": "c0", "c1_root": "c1", "c2_root": "c2",
                                   "c3_root": "c3", "c4_root": "c4"})

    def test_unknown_action_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.take_names(ACTIONS + ["extra_root"], CLIPS)

    def test_clip_without_action_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.take_names(ACTIONS[:-1], CLIPS)

    def test_two_actions_for_one_clip_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.take_names(["c0_root", "c0"], CLIPS[:1])


class OriginalStates(unittest.TestCase):
    """PS2 slot -> REROLL `mononame.ANIM_NAME` state, matched on all 18 Cancer types."""

    def test_slot_order_names_the_original_states(self):
        self.assertEqual(conv.STATE_OF_SLOT, ("walk", "escape", "rampage", "idle", "chase", "special"))

    def test_clips_are_renamed_by_slot_not_by_their_exporter_name(self):
        clips = CLIPS + [{"slot": 5, "name": "c5", "seconds": 0.9667}]
        self.assertEqual(conv.state_names(clips), {"c0": "walk", "c1": "escape", "c2": "rampage",
                                                   "c3": "idle", "c4": "chase", "c5": "special"})

    def test_unknown_or_repeated_slot_is_an_error(self):
        for bad in ([{"slot": 6, "name": "x"}], [{"slot": None, "name": "x"}],
                    [{"slot": 1, "name": "x"}, {"slot": 1, "name": "y"}]):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                conv.state_names(bad)


class Durations(unittest.TestCase):
    def test_matching_spans_pass(self):
        spans = {"c0": (0.0, 23.2), "c1": (0.0, 11.6), "c2": (0.0, 2.4),
                 "c3": (0.0, 23.2), "c4": (0.0, 23.2)}
        conv.check_durations(spans, CLIPS, 24.0)

    def test_misattributed_span_fails(self):
        # Index-based renaming would put the 0.1 s clip's span under the 0.4833 s clip.
        with self.assertRaises(RuntimeError):
            conv.check_durations({"c1": (0.0, 2.4)}, CLIPS, 24.0)


class SceneFrameRate(unittest.TestCase):
    """The exporter's frame rate decides what Unity reads: it rounds each take to whole frames at the file's rate."""

    def test_reference_clips_use_60_fps(self):
        self.assertEqual(conv.scene_fps(CLIPS), 60)

    def test_every_reference_clip_is_a_whole_number_of_frames_at_the_scene_rate(self):
        fps = conv.scene_fps(CLIPS)
        frames = {c["name"]: c["seconds"] * fps for c in CLIPS}
        for name, count in frames.items():
            self.assertAlmostEqual(count, round(count), delta=conv.WHOLE_FRAME_TOLERANCE, msg=name)
        self.assertEqual([round(frames[n]) for n in ("c0", "c1", "c2", "c3", "c4")],
                         [58, 29, 6, 58, 58])

    def test_the_blender_default_rates_cut_clips_mid_frame(self):
        # 24 fps: 0.9667 s = 23.2 frames (Unity read 23 frames = 0.9583 s); 30 fps: 0.4833 s = 14.5 frames.
        for fps, clip in ((24, CLIPS[0]), (30, CLIPS[1])):
            frames = clip["seconds"] * fps
            self.assertGreater(abs(frames - round(frames)), conv.WHOLE_FRAME_TOLERANCE, (fps, clip["name"]))
        self.assertNotIn(24, [conv.scene_fps(CLIPS)])
        self.assertNotIn(30, [conv.scene_fps(CLIPS)])

    def test_spans_at_the_scene_rate_pass_the_duration_check(self):
        fps = conv.scene_fps(CLIPS)
        spans = {c["name"]: (0.0, round(c["seconds"] * fps)) for c in CLIPS}
        conv.check_durations(spans, CLIPS, fps)

    def test_a_clip_that_fits_no_candidate_rate_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.scene_fps([{"name": "too_short", "seconds": 0.0001}])

    def test_the_rate_is_the_lowest_that_fits(self):
        self.assertEqual(conv.scene_fps([{"name": "a", "seconds": 1.0}]), 24)
        self.assertEqual(conv.scene_fps([{"name": "a", "seconds": 0.9667}]), 30)


class SkeletalManifest(unittest.TestCase):
    @staticmethod
    def instance(object_id, in_level=True, animated=True):
        return {"nameIdx": object_id, "inLevelProps": in_level, "animated": animated}

    @staticmethod
    def definition(object_id, name, count=1):
        return {"mono": object_id, "id": name, "instances": count}

    def test_shared_english_names_are_joined_by_original_id(self):
        rows = [self.instance(519), self.instance(141), self.instance(141), self.instance(1695, False),
                self.instance(143, animated=False)]
        defs = [self.definition(141, "pigeon", 2), self.definition(519, "pigeon_519")]
        self.assertEqual(conv.skeletal_defs(rows, defs), {141: defs[0], 519: defs[1]})

    def test_incomplete_duplicate_or_wrong_instance_counts_fail(self):
        rows = [self.instance(141), self.instance(519)]
        a, b = self.definition(141, "pigeon"), self.definition(519, "pigeon_519")
        for bad in [[a], [a, a, b], [a, dict(b, id="pigeon")], [a, dict(b, instances=2)]]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                conv.skeletal_defs(rows, bad)

    def test_missing_glb_is_not_silently_ignored(self):
        with tempfile.TemporaryDirectory() as folder:
            expected = {141: self.definition(141, "pigeon"), 519: self.definition(519, "pigeon_519")}
            report = [{"objectId": 141}, {"objectId": 519}]
            (Path(folder) / "type_141.glb").touch()
            with self.assertRaisesRegex(ValueError, "type_519"):
                conv.export_inputs(expected, report, folder, set())
            with self.assertRaisesRegex(ValueError, "duplicate"):
                conv.export_inputs(expected, report + report[:1], folder, {"type_141"})
            with self.assertRaisesRegex(ValueError, "unknown selected"):
                conv.export_inputs(expected, report, folder, {"type_999"})

    def test_blender_36_armature_suffix_and_frozen_take(self):
        names = ["c3_type_141_skin", "c1_root"]
        clips = [{"name": "c3", "seconds": 0.3}, {"name": "c1", "seconds": None}]
        self.assertEqual(conv.take_names(names, clips, ("type_141_skin",)), dict(zip(names, ["c3", "c1"])))
        self.assertEqual(conv.scene_fps(clips), 30)
        self.assertEqual(conv.take_frames(None, 30), 1)
        conv.check_durations({"c1": (0, 1)}, clips, 30)
        with self.assertRaises(RuntimeError):
            conv.check_durations({"c1": (0, 2)}, clips, 30)

    def test_q8_dog_speed_is_rounded_by_less_than_one_millisecond(self):
        clips = [{"name": str(i), "seconds": s} for i, s in enumerate([0.4209, 0.3222, 0.2417, 0.9667, 0.2762])]
        fps = conv.scene_fps(clips)
        self.assertEqual(fps, 480)
        for clip in clips:
            seconds = clip["seconds"]
            self.assertLessEqual(abs(conv.take_frames(seconds, fps) / fps - seconds), 0.001)


class ReferenceJointRoots(unittest.TestCase):
    def test_detached_root_joins_existing_coordinate_root_without_reparenting_its_children(self):
        doc = {"nodes": [{"children": [2]}, {}, {}, {"children": [0, 4], "matrix": [1]}, {}],
               "scenes": [{"nodes": [3]}], "skins": [{"joints": [0, 1, 2], "skeleton": 0}]}
        self.assertTrue(conv.attach_detached_joints(doc))
        self.assertEqual(doc["nodes"][3]["children"], [0, 4, 1])
        self.assertEqual(doc["nodes"][0]["children"], [2])
        self.assertEqual(doc["skins"][0]["skeleton"], 3)
        self.assertFalse(conv.attach_detached_joints(doc))

    def test_detached_non_joint_ancestor_is_not_moved(self):
        doc = {"nodes": [{"children": [1]}, {}, {"matrix": [1]}],
               "scenes": [{"nodes": [2]}], "skins": [{"joints": [1], "skeleton": 1}]}
        with self.assertRaisesRegex(ValueError, "unexpected disconnected root 0"):
            conv.attach_detached_joints(doc)


if __name__ == "__main__":
    unittest.main()
