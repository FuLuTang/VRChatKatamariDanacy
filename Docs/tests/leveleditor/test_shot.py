#!/usr/bin/env python3
"""Unit tests for the shot renderer's camera/path/argument contract.

The browser end-to-end smoke is here too; it is skipped when no Chromium is available
(and it uses its own state dir/port/level copy, so it never touches a running editor or
a repository placement file).
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import math
import os
import shutil
import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.leveleditor import shot  # noqa: E402
from Tools.leveleditor import shot_cdp  # noqa: E402
from Tools.leveleditor import shot_png  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402

LEVELS = ROOT / "Docs" / "Design" / "Levels"


# --------------------------------------------------------------------------- #
# helpers
# --------------------------------------------------------------------------- #
def make_args(**overrides):
    defaults = dict(
        level="story_01_bench_size", view=None, views=None, frames=1, interval=0.5,
        no_diff=False, shadows=False, layers=None, w=1280, h=720,
        max_px=shot.DEFAULT_MAX_PX, out=None, save_bookmark=None,
        port=8793, state_dir=None, token=None, timeout=240.0, idle_timeout=900.0,
        levels_dir=None, chrome=None, status=False, stop=False, quiet=True)
    defaults.update(overrides)
    return argparse.Namespace(**defaults)


def project_corners(box, plan, aspect):
    """Project the 8 corners with the same pinhole model the page uses."""
    direction = tuple(plan["position"][i] - plan["target"][i] for i in range(3))
    length = math.sqrt(sum(value * value for value in direction))
    direction = tuple(value / length for value in direction)
    _, right, up = shot.camera_basis(direction)
    forward = tuple(-value for value in direction)
    fov = math.radians(plan["fov"])
    tan_v, tan_h = math.tan(fov / 2), math.tan(fov / 2) * aspect
    rows = []
    for x in (box[0], box[3]):
        for y in (box[1], box[4]):
            for z in (box[2], box[5]):
                relative = tuple((x, y, z)[i] - plan["position"][i] for i in range(3))
                depth = sum(relative[i] * forward[i] for i in range(3))
                px = sum(relative[i] * right[i] for i in range(3))
                py = sum(relative[i] * up[i] for i in range(3))
                rows.append((px / (depth * tan_h), py / (depth * tan_v)))
    return rows


def make_png(width=2, height=2):
    def chunk(kind, body):
        return (struct.pack(">I", len(body)) + kind + body
                + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF))

    raw = b"".join(b"\x00" + b"\x10\x20\x30" * width for _ in range(height))
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (shot_png.PNG_SIGNATURE + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


class TempLevel:
    def __init__(self, level="story_01_bench_size"):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.levels = self.root / "levels"
        shutil.copytree(LEVELS / level, self.levels / level)

    def close(self):
        self.temp.cleanup()


# --------------------------------------------------------------------------- #
# arguments / JSON contract
# --------------------------------------------------------------------------- #
class ArgumentTests(unittest.TestCase):
    def test_default_is_the_sheet(self):
        params = shot.normalize_request(make_args(state_dir=tempfile.mkdtemp()))
        self.assertEqual(params["views"], ["sheet"])
        self.assertEqual(params["frames"], 1)
        self.assertFalse(params["diff"])
        self.assertEqual(params["layers"]["props"], True)
        self.assertEqual(params["layers"]["structures"], True)
        self.assertFalse(params["layers"]["terrain"])
        self.assertEqual(params["meta"]["propCount"], 130)
        self.assertEqual(params["meta"]["level"], "story_01_bench_size")
        self.assertEqual(len(params["meta"]["sha256"]), 64)
        for key in ("level", "views", "frames", "interval", "diff", "layers", "w", "h",
                    "maxPx", "out", "outFile", "watermark", "saveBookmark", "levelsDir",
                    "meta", "timeout"):
            self.assertIn(key, params)

    def test_multi_frame_needs_one_view(self):
        with self.assertRaises(shot.ShotError) as caught:
            shot.normalize_request(make_args(frames=3, state_dir=tempfile.mkdtemp()))
        self.assertEqual(caught.exception.code, "bad_argument")
        params = shot.normalize_request(make_args(
            frames=3, view=["start"], state_dir=tempfile.mkdtemp()))
        self.assertTrue(params["diff"])
        params = shot.normalize_request(make_args(
            frames=3, view=["start"], no_diff=True, state_dir=tempfile.mkdtemp()))
        self.assertFalse(params["diff"])

    def test_layers(self):
        layers = shot.parse_layers(
            "props,terrain,collision,parents,grid,wire,labels:7,tier:0.222,"
            "ball:0.5,select:a,b", 0.3)
        self.assertTrue(layers["terrain"])
        self.assertTrue(layers["collision"])
        self.assertEqual(layers["labels"], 7)
        self.assertEqual(layers["tier"], 0.222)
        self.assertEqual(layers["ball"], 0.5)
        self.assertEqual(layers["select"], ["a", "b"])
        with self.assertRaises(shot.ShotError):
            shot.parse_layers("props,nonsense", 0.3)
        with self.assertRaises(shot.ShotError):
            shot.parse_layers("tier:0", 0.3)

    def test_bad_view_and_frames(self):
        with self.assertRaises(shot.ShotError):
            shot.normalize_request(make_args(view=["nonsense"]))
        with self.assertRaises(shot.ShotError):
            shot.normalize_request(make_args(frames=0))

    def test_out_file_only_for_one_image(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "one.png"
            params = shot.normalize_request(make_args(
                view=["top"], out=str(path), state_dir=directory))
            self.assertEqual(params["outFile"], "one.png")
            with self.assertRaises(shot.ShotError):
                shot.normalize_request(make_args(
                    view=["top", "iso"], out=str(path), state_dir=directory))

    def test_query_parameters(self):
        query = {"view": ["top", "iso"], "layers": ["props,tier:0.222"],
                 "w": ["640"], "h": ["360"], "frames": ["1"]}
        params = shot.request_from_query(query, "story_01_bench_size",
                                         levels_dir=None, state_dir=tempfile.mkdtemp())
        self.assertEqual(params["views"], ["top", "iso"])
        self.assertEqual(params["w"], 640)
        self.assertEqual(params["layers"]["tier"], 0.222)


# --------------------------------------------------------------------------- #
# camera math
# --------------------------------------------------------------------------- #
class CameraTests(unittest.TestCase):
    box = (0.0, 0.0, 0.0, 10.0, 4.0, 10.0)

    def assert_fits(self, box, plan, aspect, label):
        rows = project_corners(box, plan, aspect)
        for px, py in rows:
            self.assertLessEqual(abs(px), 1.000001, "%s horizontal overflow" % label)
            self.assertLessEqual(abs(py), 1.000001, "%s vertical overflow" % label)
        self.assertGreater(plan["distance"], 0.0)

    def test_top_fit(self):
        plan = shot.fit_view(self.box, (0.0, 1.0, 0.0001), 45.0, 16 / 9)
        self.assert_fits(self.box, plan, 16 / 9, "top")
        self.assertGreater(plan["position"][1], plan["target"][1])
        # The plan view frames the authored footprint, not a 9 m tree at the edge.
        doc = {"level": {"bounds": [[-20.0, -20.0], [-5.0, -5.0]], "groundY": 0.0},
               "props": [{"itemId": "tree", "def": "tree", "position": [-18.0, 4.5, -18.0],
                          "rotationQuat": [0, 0, 0, 1], "aabb": [0.5, 9.0, 0.5]}]}
        footprint = shot.level_bounds_box(doc)
        full = shot.level_box(doc)
        self.assertGreater(full[4], footprint[4])
        plan = shot.fit_view(footprint, (0.0, 1.0, 0.0001), 45.0, 16 / 9)
        self.assert_fits(footprint, plan, 16 / 9, "bounds")

    def test_iso_and_side_fit(self):
        for direction in ((1.0, 0.82, 1.0), (1.0, 0.02, 0.0001)):
            plan = shot.fit_view(self.box, direction, 45.0, 16 / 9)
            self.assert_fits(self.box, plan, 16 / 9, "direction %r" % (direction,))
            self.assertGreater(plan["position"][0], 0.0)

    def test_fit_distance_grows_with_box(self):
        small = shot.fit_distance((0, 0, 0, 1, 1, 1), (1, 1, 1), 45, 1.5)
        large = shot.fit_distance((0, 0, 0, 10, 1, 1), (1, 1, 1), 45, 1.5)
        self.assertGreater(large, small)

    def test_start_view_eye_height_and_direction(self):
        path = LEVELS / "story_01_bench_size" / "placement.json"
        doc = core.load_doc(path)
        plan = shot.start_view(doc, 16 / 9)
        level = doc["level"]
        radius = level["ballDiameterStart"] / 2
        self.assertAlmostEqual(plan["eyeHeight"],
                               level.get("groundY", 0) + 8.0 * radius, places=4)
        self.assertAlmostEqual(plan["position"][0], level["ballStart"][0], places=4)
        self.assertAlmostEqual(plan["position"][2], -level["ballStart"][2], places=4)
        self.assertEqual(plan["fov"], 60.0)
        self.assertIn("ball_eye", plan["directionSource"])
        # Looking roughly toward the authored ball_eye target, not backwards.
        dx = plan["target"][0] - plan["position"][0]
        self.assertGreater(dx, 0.0)

    def test_focus_union_and_fit(self):
        path = LEVELS / "story_01_bench_size" / "placement.json"
        doc = core.load_doc(path)
        box, focus = shot.focus_box(doc, "cup")
        self.assertEqual(focus["kind"], "def")
        self.assertGreater(focus["count"], 0)
        plan = shot.fit_view(box, (1.0, 0.7, 1.0), 45.0, 16 / 9, margin=1.38)
        self.assert_fits(box, plan, 16 / 9, "focus")
        with self.assertRaises(shot.ShotError):
            shot.focus_box(doc, "definitely-not-a-prop")

    def test_region_fit_from_sidecar(self):
        level = TempLevel()
        try:
            regions = {"schemaVersion": 1, "regions": [
                {"name": "pocket", "min": [-15, 0, -15], "max": [-12, 1, -12]}]}
            (level.levels / "story_01_bench_size" / "regions.json").write_text(
                json.dumps(regions), encoding="utf-8")
            box = shot.region_box("story_01_bench_size", "pocket", level.levels)
            self.assertEqual(box[0], -15.0)
            self.assertEqual(box[5], 15.0)
            plan = shot.fit_view(box, (1.0, 0.9, 1.0), 45.0, 16 / 9, margin=1.1)
            self.assert_fits(box, plan, 16 / 9, "region")
            with self.assertRaises(shot.ShotError):
                shot.region_box("story_01_bench_size", "missing", level.levels)
        finally:
            level.close()

    def test_world_view_rows_use_the_page_camera(self):
        plans = [{"name": "start", "kind": "start", "fov": 60.0,
                  "position": (-12.5, 1.2, -12.5), "target": (-10.5, 1.08, -9.9),
                  "box": None, "mark": None}]
        actual = [{"fov": 60.0, "position": [-12.5, 3.4, -12.5],
                   "target": [-10.5, 3.28, -9.9], "renderMs": 12,
                   "startSurfaceY": 2.51, "startLiftedM": 2.2}]
        rows = shot.world_view_rows(plans, actual)
        self.assertEqual(rows[0]["position"], [-12.5, 3.4, 12.5])
        self.assertEqual(rows[0]["target"], [-10.5, 3.28, 9.9])
        self.assertEqual(rows[0]["startSurfaceY"], 2.51)
        self.assertEqual(rows[0]["startLiftedM"], 2.2)
        self.assertEqual(rows[0]["fov"], 60.0)
        self.assertEqual(shot.world_view_rows(plans)[0]["position"], [-12.5, 1.2, 12.5])

    def test_lens_fov(self):
        self.assertAlmostEqual(shot.fov_from_lens(30), 43.60, places=2)
        self.assertAlmostEqual(shot.fov_from_lens(32), 41.11, places=2)
        self.assertEqual(shot.fov_from_lens(0), shot.FIT_FOV)

    def test_raw_camera(self):
        plan = shot.parse_raw_camera("cam=1,2,3>4,5,6,60")
        self.assertEqual(plan["position"], (1.0, 2.0, -3.0))
        self.assertEqual(plan["target"], (4.0, 5.0, -6.0))
        self.assertEqual(plan["fov"], 60.0)
        with self.assertRaises(shot.ShotError):
            shot.parse_raw_camera("1,2,3")

    def test_tile_size_cap(self):
        width, height, final_w, final_h = shot.tile_size(1280, 720, 4, 1_000_000)
        self.assertLessEqual(final_w * final_h, 1_000_000)
        self.assertEqual((final_w, final_h), (2 * width, 2 * height))
        self.assertEqual(shot.layout_for(4), {"cols": 2, "rows": 2})
        self.assertEqual(shot.layout_for(1), {"cols": 1, "rows": 1})


# --------------------------------------------------------------------------- #
# reference-level output guard
# --------------------------------------------------------------------------- #
class OutputGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.state = self.root / "state"
        self.state.mkdir()
        self.ref_root = self.root / "ref_shots"
        self.env = mock.patch.dict(os.environ,
                                   {"VRC_KATAMARI_REF_SHOTS": str(self.ref_root)})
        self.env.start()

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def test_ref_default_is_under_the_evidence_root(self):
        info = shot.output_target("_ref_cancer", None, state_dir=self.state)
        self.assertEqual(info["out"], (self.ref_root / "_ref_cancer").resolve())
        self.assertTrue(info["watermark"])

    def test_ref_outside_root_is_refused(self):
        with self.assertRaises(shot.ShotError) as caught:
            shot.output_target("_ref_cancer", str(self.root / "elsewhere"),
                               state_dir=self.state)
        self.assertEqual(caught.exception.code, "ref_output_forbidden")

    def test_ref_inside_repo_is_refused(self):
        with self.assertRaises(shot.ShotError) as caught:
            shot.output_target("_ref_cancer", str(ROOT / "Docs" / "Evidence"),
                               state_dir=self.state)
        self.assertEqual(caught.exception.code, "ref_output_forbidden")

    def test_ref_inside_its_own_repo_folder_is_refused(self):
        with self.assertRaises(shot.ShotError) as caught:
            shot.output_target("_ref_cancer", str(LEVELS / "_ref_cancer"), state_dir=self.state)
        self.assertEqual(caught.exception.code, "ref_output_forbidden")

    def test_normal_level_default_and_out(self):
        info = shot.output_target("story_01_bench_size", None, state_dir=self.state)
        self.assertEqual(info["out"], (self.state / "shots" / "story_01_bench_size").resolve())
        self.assertFalse(info["watermark"])
        info = shot.output_target("story_01_bench_size", str(self.state / "custom.png"),
                                  state_dir=self.state)
        self.assertEqual(info["out"], self.state.resolve())
        self.assertEqual(info["outFile"], "custom.png")


# --------------------------------------------------------------------------- #
# PNG provenance
# --------------------------------------------------------------------------- #
class PngTests(unittest.TestCase):
    def test_size_and_text(self):
        data = make_png(3, 2)
        self.assertEqual(shot_png.png_size(data), (3, 2))
        tagged = shot_png.add_text(data, {"Level": "story_01_bench_size",
                                          "Unlicensed": "UNLICENSED REFERENCE"})
        self.assertEqual(shot_png.png_size(tagged), (3, 2))
        kinds = [kind for kind, _body in shot_png.chunks(tagged)]
        self.assertIn("tEXt", kinds)
        self.assertEqual(kinds[-1], "IEND")
        self.assertGreater(len(tagged), len(data))
        self.assertIsNone(shot_png.png_size(b"not a png"))

    def test_refuses_to_tag_non_png(self):
        with self.assertRaises(ValueError):
            shot_png.add_text(b"nope", {"a": "b"})


# --------------------------------------------------------------------------- #
# end-to-end smoke (real Chromium + daemon)
# --------------------------------------------------------------------------- #
@unittest.skipUnless(shot_cdp.find_chrome() is not None,
                     "no Chromium/Chrome available")
class EndToEndTests(unittest.TestCase):
    """One real shot through the daemon on a private copy of a level.

    It uses a temporary state dir and port 8797, so it cannot collide with an editor
    the owner is using on 8777/8778 or with the normal daemon on 8793.
    """

    def test_top_shot(self):
        level = TempLevel()
        state = tempfile.TemporaryDirectory()
        try:
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = shot.main([
                    "story_01_bench_size", "--view", "top", "--w", "480", "--h", "270",
                    "--levels-dir", str(level.levels), "--state-dir", state.name,
                    "--port", "8797", "--timeout", "240", "--quiet"])
            self.assertEqual(code, 0)
            result = json.loads(buffer.getvalue().strip().splitlines()[-1])
            self.assertTrue(result["ok"], result.get("error"))
            self.assertEqual(result["level"], "story_01_bench_size")
            self.assertGreaterEqual(result["propCount"], 1)
            self.assertEqual(len(result["levelHash"]), 64)
            self.assertEqual(result["camera"]["name"], "top")
            self.assertIsNotNone(result["visibleBox"])
            self.assertIn("props", result["layers"]["applied"])
            self.assertEqual(result["images"][0]["kind"], "view")
            png = Path(result["images"][0]["path"])
            self.assertTrue(png.is_file())
            self.assertEqual(shot_png.png_size(png.read_bytes()),
                             (result["images"][0]["width"],
                              result["images"][0]["height"]))
            self.assertGreater(result["images"][0]["bytes"], 1000)
            self.assertLessEqual(len(result["consoleErrors"]), 30)
        finally:
            shot.stop_daemon(Path(state.name))
            state.cleanup()
            level.close()


if __name__ == "__main__":
    unittest.main()
