"""Take-name mapping of the skinned GLB -> FBX converter (no Blender needed).

    python3 -m unittest discover -s Docs/tests -p 'test_skinned_take_mapping.py'

The converter module keeps ``bpy`` imports inside its functions, so its pure mapping and
duration checks load here with fake action/clip data.
"""

import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location(
    "skinned_glb_to_fbx", ROOT / "Tools/levelbuild/blender/skinned_glb_to_fbx.py")
conv = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(conv)

CLIPS = [
    {"slot": 0, "name": "walk", "seconds": 0.9667},
    {"slot": 1, "name": "walk_fast", "seconds": 0.4833},
    {"slot": 2, "name": "slot2", "seconds": 0.1},
    {"slot": 3, "name": "idle", "seconds": 0.9667},
    {"slot": 4, "name": "slot4", "seconds": 0.9667},
]
# Blender 4.0's glTF importer order: alphabetical, which is NOT slot order.
ACTIONS = ["idle_root", "slot2_root", "slot4_root", "walk_fast_root", "walk_root"]


class TakeNames(unittest.TestCase):
    def test_suffix_is_stripped(self):
        self.assertEqual(conv.clip_name_of("walk_fast_root"), "walk_fast")
        self.assertEqual(conv.clip_name_of("idle"), "idle")

    def test_alphabetical_actions_map_by_name_not_by_index(self):
        mapping = conv.take_names(ACTIONS, CLIPS)
        self.assertEqual(mapping["idle_root"], "idle")
        self.assertEqual(mapping["walk_root"], "walk")
        self.assertEqual(mapping["walk_fast_root"], "walk_fast")
        self.assertEqual(mapping["slot2_root"], "slot2")
        self.assertEqual(mapping["slot4_root"], "slot4")

    def test_unknown_action_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.take_names(ACTIONS + ["extra_root"], CLIPS)

    def test_clip_without_action_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.take_names(ACTIONS[:-1], CLIPS)

    def test_two_actions_for_one_clip_is_an_error(self):
        with self.assertRaises(RuntimeError):
            conv.take_names(["walk_root", "walk"], CLIPS[:1])


class Durations(unittest.TestCase):
    def test_matching_spans_pass(self):
        spans = {"walk": (0.0, 23.2), "walk_fast": (0.0, 11.6), "slot2": (0.0, 2.4),
                 "idle": (0.0, 23.2), "slot4": (0.0, 23.2)}
        conv.check_durations(spans, CLIPS, 24.0)

    def test_misattributed_span_fails(self):
        # The old index-based renaming put the 0.1 s clip's span under "walk_fast".
        with self.assertRaises(RuntimeError):
            conv.check_durations({"walk_fast": (0.0, 2.4)}, CLIPS, 24.0)


class SceneFrameRate(unittest.TestCase):
    """The exporter's frame rate decides what Unity reads: it rounds each take to whole frames at the file's rate."""

    def test_reference_clips_use_60_fps(self):
        self.assertEqual(conv.scene_fps(CLIPS), 60)

    def test_every_reference_clip_is_a_whole_number_of_frames_at_the_scene_rate(self):
        fps = conv.scene_fps(CLIPS)
        frames = {c["name"]: c["seconds"] * fps for c in CLIPS}
        for name, count in frames.items():
            self.assertAlmostEqual(count, round(count), delta=conv.WHOLE_FRAME_TOLERANCE, msg=name)
        self.assertEqual([round(frames[n]) for n in ("walk", "walk_fast", "slot2", "idle", "slot4")],
                         [58, 29, 6, 58, 58])

    def test_the_blender_default_rates_cut_clips_mid_frame(self):
        # 24 fps: walk = 23.2 frames (Unity read 23 frames = 0.9583 s); 30 fps: walk_fast = 14.5 frames.
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
            conv.scene_fps([{"name": "odd", "seconds": 0.0123}])

    def test_the_rate_is_the_lowest_that_fits(self):
        self.assertEqual(conv.scene_fps([{"name": "a", "seconds": 1.0}]), 24)
        self.assertEqual(conv.scene_fps([{"name": "a", "seconds": 0.9667}]), 30)


if __name__ == "__main__":
    unittest.main()
