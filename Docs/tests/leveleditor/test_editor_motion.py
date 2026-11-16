"""The level editor's motion layer: ``motions[]`` / ``paths[]`` and the motion_sim tool.

What these tests protect (each one is a rule that fails silently otherwise):

* the v2 reader exposes ``motions[]``, ``paths[]``, ``props[].motion`` and the
  ``behaviour: "attached"`` relation, and it counts the reference level's real numbers:
  **14** motion programs, **4** paths, **113** props with a ``motion``, **15** attached
  props (LEVEL_FORMAT_V2 6.7 - the naive "129 props have motion" reading is wrong);
* every motion view toggle the GUI offers has an HTTP spelling (the parity rule of
  AGENTS.md), and the GUI's data and the endpoint are one Python call;
* the resolved numbers - speed in m/s, the D-12 phase seed, the WaitForPlayer trigger
  radius - come from ``MotionMath`` through ``Tools/motion_sim``, never from a Python
  re-implementation; the tests compare them against MotionMath's own documented constants
  and against hand calculations from the motion research;
* the trajectory samples reproduce MotionMath's measured values (Spin: one turn per
  4.19 s; the p23 path: 24.4 cm/s), and a whole-level 30 s trajectory stays fast enough to
  be requested on every page load.

The synthetic fixture is the format's own tiny pad plus three motions, so the parser and
endpoint tests run without the unlicensed reference extraction.  The reference-level tests
assert the real counts and are skipped when ``_ref_cancer`` is not present locally.
"""

import json
import math
import shutil
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Docs.tests.leveleditor.test_v2_editor import (  # noqa: E402
    v2_document_with_sidecar,
    write_json,
)
from Tools.levelbuild import editor_core  # noqa: E402
from Tools.leveleditor import server as web  # noqa: E402
from Tools.leveleditor import v2_document  # noqa: E402
from Tools.motion_sim import motion_cli  # noqa: E402

TOKEN = "test-motion-token"
REFERENCE = ROOT / "Docs/Design/Levels/_ref_cancer/placement.v2.json"


def reference_available():
    return REFERENCE.is_file()


def motion_sim_available():
    try:
        return motion_cli.build() is not None
    except motion_cli.MotionSimError:
        return False


def with_motions(document):
    """The tiny pad plus three motion programs, one path, a walker and an attached rider."""
    document["motions"] = [
        {"id": "mt_spin", "class": "spin", "speed": {"source": "none"},
         "updatesPerSecond": 5, "loop": True, "provenance": {"motionID": 21}},
        {"id": "mt_wait", "class": "wait_for_player", "speed": {"source": "none"},
         "updatesPerSecond": 5, "loop": True, "provenance": {"motionID": 0}},
        {"id": "mt_path", "class": "path_collision", "speed": {"source": "path"},
         "path": "p_loop", "updatesPerSecond": 15, "loop": True,
         "provenance": {"motionID": 2}},
    ]
    document["paths"] = [
        {"id": "p_loop",
         "points": [[-1.0, 0.0, -1.0], [1.0, 0.0, -1.0], [1.0, 0.0, 1.0], [-1.0, 0.0, 1.0]],
         "loop": "loop", "speedCmPerSec": 24.4},
    ]
    props = document["props"]
    props.append({
        "itemId": "spinner_01", "def": "acorn", "kind": "nature",
        "behaviour": "motion", "inherit": "none", "zone": "pad",
        "position": [0.0, 0.015, 0.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
        "aabb": [0.024, 0.03, 0.0228], "volumeK": 0.7, "volumePenalty": 0.8,
        "absorbDiameterReqMm": 60, "growthMm3": 9193, "absorbable": True,
        "motion": "mt_spin",
    })
    props.append({
        "itemId": "rider_01", "def": "acorn", "kind": "nature",
        "behaviour": "attached", "inherit": "full", "zone": "pad",
        "position": [0.05, 0.015, 0.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
        "aabb": [0.024, 0.03, 0.0228], "volumeK": 0.7, "volumePenalty": 0.8,
        "absorbDiameterReqMm": 60, "growthMm3": 9193, "absorbable": True,
        "parent": "spinner_01",
    })
    props.append({
        "itemId": "walker_01", "def": "acorn", "kind": "nature",
        "behaviour": "motion", "inherit": "none", "zone": "pad",
        "position": [0.0, 0.015, -1.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
        "aabb": [0.024, 0.03, 0.0228], "volumeK": 0.7, "volumePenalty": 0.8,
        "absorbDiameterReqMm": 60, "growthMm3": 9193, "absorbable": True,
        "motion": "mt_path",
    })
    props.append({
        "itemId": "waiter_01", "def": "acorn", "kind": "nature",
        "behaviour": "motion", "inherit": "none", "zone": "pad",
        "position": [0.5, 0.015, 0.5], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
        "aabb": [0.024, 0.03, 0.0228], "volumeK": 0.7, "volumePenalty": 0.8,
        "absorbDiameterReqMm": 60, "growthMm3": 9193, "absorbable": True,
        "motion": "mt_wait",
    })
    document["meta"]["expected"]["propCount"] = len(props)
    return document


class MotionFixture(unittest.TestCase):
    """A temporary v2 level with three motion programs (no reference assets)."""

    level_id = "_ref_motion_demo"

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="leveleditor-motion-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.levels = self.tmp / "levels"
        self.models = self.tmp / "KatamariRef" / "models"
        self.state_dir = self.tmp / "state"
        folder = self.levels / self.level_id
        document, sidecar = v2_document_with_sidecar()
        with_motions(document)
        document["meta"]["id"] = self.level_id
        document["meta"]["arena"] = {"id": self.level_id}
        write_json(folder / "placement.v2.json", document)
        # A v1 sibling as well: `shot.py` plans its cameras with the v1 loader (it predates
        # the v2 viewer), and a level may legitimately carry both files.
        example = ROOT / "Tools/levelbuild/examples/placement.json"
        legacy = json.loads(example.read_text(encoding="utf-8"))
        legacy["level"]["id"] = self.level_id
        write_json(folder / "placement.json", legacy)
        (folder / "terrain").mkdir(parents=True, exist_ok=True)
        (folder / "terrain" / "tiny.katerrain").write_bytes(sidecar)
        write_json(folder / "manifests" / "acorn.manifest.json", {
            "id": "acorn", "category": "reference", "pivot": "center",
            "source": "tiny.glb",
            "bounds_m": {"min": [-0.012, -0.015, -0.0114],
                         "max": [0.012, 0.015, 0.0114]},
            "dimensions_m": {"x": 0.024, "y": 0.03, "z": 0.0228, "longest": 0.03},
        })
        write_json(folder / "original_defs.json",
                   {"defs": [{"id": "acorn", "modelFile": "tiny.glb"}]})
        (self.models / "glb-pack").mkdir(parents=True, exist_ok=True)
        (self.models / "glb-pack" / "tiny.glb").write_bytes(b"glTF-fake-acorn")
        self.document = document

    def open_level(self):
        return web.WebEditor(self.level_id, levels_dir=self.levels,
                             state_dir=self.state_dir, lock=False,
                             ref_models_dir=self.models)


class DocumentMotionTests(MotionFixture):
    def test_the_reader_counts_declared_motions_paths_and_attached_props(self):
        app = self.open_level()
        self.addCleanup(app.close)
        counts = app.v2_document.summary["counts"]
        self.assertEqual(counts["motions"], 3)
        self.assertEqual(counts["paths"], 1)
        self.assertEqual(counts["motionProps"], 3)
        self.assertEqual(counts["attachedProps"], 1)

    def test_motion_rows_carry_class_parent_and_path_from_the_document(self):
        app = self.open_level()
        self.addCleanup(app.close)
        rows = app.v2_document.document_motion_rows()
        self.assertEqual(sorted(rows),
                         ["rider_01", "spinner_01", "waiter_01", "walker_01"])
        self.assertEqual(rows["spinner_01"]["motionClass"], "spin")
        self.assertEqual(rows["walker_01"]["motionClass"], "path_collision")
        self.assertEqual(rows["walker_01"]["path"], "p_loop")
        self.assertEqual(rows["walker_01"]["speedCmPerSec"], 24.4)
        # 6.7.3: dragged_along exists only as behaviour "attached" + parent.
        self.assertEqual(rows["rider_01"]["motionClass"], "dragged_along")
        self.assertEqual(rows["rider_01"]["parent"], "spinner_01")

    def test_a_document_without_motions_reads_as_unavailable_not_as_an_error(self):
        folder = self.levels / "plain_demo"
        document, sidecar = v2_document_with_sidecar()
        document["meta"]["id"] = "plain_demo"
        write_json(folder / "placement.v2.json", document)
        (folder / "terrain").mkdir(parents=True, exist_ok=True)
        (folder / "terrain" / "tiny.katerrain").write_bytes(sidecar)
        app = web.WebEditor("plain_demo", levels_dir=self.levels,
                            state_dir=self.state_dir, lock=False,
                            placement="placement.v2.json")
        self.addCleanup(app.close)
        payload = app.v2_motion_payload()
        self.assertTrue(payload["ok"])
        self.assertFalse(payload["available"])
        self.assertEqual(payload["counts"]["motions"], 0)
        self.assertEqual(payload["props"], [])
        self.assertIn("no motions", payload["reason"])


@unittest.skipUnless(motion_sim_available(), "the .NET SDK / motion_sim is not available")
class ResolvedMotionTests(MotionFixture):
    def test_resolved_numbers_come_from_motion_math(self):
        app = self.open_level()
        self.addCleanup(app.close)
        payload = app.v2_motion_payload()
        self.assertTrue(payload["available"], payload["reason"])
        rows = {row["itemId"]: row for row in payload["props"]}
        # The path record's speed wins over any type speed (6.7.4): 24.4 cm/s = 0.244 m/s.
        self.assertAlmostEqual(rows["walker_01"]["speedMps"], 0.244, places=6)
        # WaitForPlayer's gate is MotionMath.PanicRadiusM(0) = 2.0 m, i.e. the reference
        # constant table's 200 cm for size group 0 (motion program 12.11).
        self.assertAlmostEqual(rows["waiter_01"]["triggerRadiusM"], 2.0, places=6)
        # The D-12 phase seed is the hash of the level id and the item id, and the phase is
        # MotionMath.Phase01 of it - not "random".
        self.assertIsInstance(rows["spinner_01"]["phaseSeed"], int)
        self.assertGreaterEqual(rows["spinner_01"]["phase"], 0.0)
        self.assertLess(rows["spinner_01"]["phase"], 1.0)
        self.assertNotEqual(rows["spinner_01"]["phaseSeed"],
                            rows["walker_01"]["phaseSeed"])
        # halfHeightM is the AABB half height the classes move about (6.7.3's h).
        self.assertAlmostEqual(rows["spinner_01"]["halfHeightM"], 0.015, places=6)
        # The trigger ring's ground height is a terrain-sidecar query, not a guess.
        self.assertIsNotNone(rows["waiter_01"]["groundY"])

    def test_one_prop_lookup_returns_the_panel_data(self):
        app = self.open_level()
        self.addCleanup(app.close)
        payload = app.v2_motion_prop_payload("rider_01")
        prop = payload["prop"]
        self.assertEqual(prop["motionClass"], "dragged_along")
        self.assertEqual([entry["itemId"] for entry in prop["parentChain"]],
                         ["spinner_01"])
        walker = app.v2_motion_prop_payload("walker_01")["prop"]
        self.assertEqual(walker["pathDetail"]["id"], "p_loop")
        self.assertEqual(len(walker["pathDetail"]["points"]), 4)
        with self.assertRaises(v2_document.V2LoadError):
            app.v2_motion_prop_payload("no_such_prop")

    def test_every_motion_toggle_has_an_http_spelling(self):
        app = self.open_level()
        self.addCleanup(app.close)
        layers = v2_document.motion_layer_map(app.v2_document.motion_counts())
        self.assertEqual(set(layers), {"motionColors", "motionPaths", "motionRadius",
                                       "motionParents", "motionPlayback"})
        for name, entry in layers.items():
            self.assertTrue(entry["http"].startswith("/api/v2/"), name)
            self.assertTrue(entry["id"], name)
            self.assertTrue(entry["label"], name)


class MotionHTTPTests(MotionFixture):
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
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read())

    def test_motion_endpoints_serve_the_same_python_as_the_gui(self):
        app = self.open_level()
        base = self.start(app)
        status, motion = self.get(base, "/api/v2/motion")
        self.assertEqual(status, 200)
        self.assertTrue(motion["ok"])
        self.assertEqual(motion["counts"]["motions"], 3)
        self.assertEqual(sorted(entry["class"] for entry in motion["classes"]),
                         ["dragged_along", "path_collision", "spin", "wait_for_player"])
        status, paths = self.get(base, "/api/v2/motion/paths")
        self.assertEqual(status, 200)
        self.assertEqual(paths["count"], 1)
        self.assertEqual(paths["paths"][0]["users"], ["walker_01"])
        status, prop = self.get(base, "/api/v2/motion/prop/walker_01")
        self.assertEqual(status, 200)
        self.assertEqual(prop["prop"]["path"], "p_loop")
        status, missing = self.get(base, "/api/v2/motion/prop/nope")
        self.assertEqual(status, 404)
        self.assertFalse(missing["ok"])
        status, layers = self.get(base, "/api/v2/layers")
        self.assertEqual(status, 200)
        self.assertEqual(set(layers["motionLayers"]),
                         {"motionColors", "motionPaths", "motionRadius",
                          "motionParents", "motionPlayback"})
        self.assertEqual(layers["counts"]["motionProps"], 3)

    def test_trajectory_endpoint_is_cached_and_narrows_to_the_asked_props(self):
        if not motion_sim_available():
            self.skipTest("motion_sim is not available")
        app = self.open_level()
        base = self.start(app)
        status, payload = self.get(
            base, "/api/v2/motion/trajectory?items=spinner_01&seconds=1&rate=30")
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"], payload.get("error"))
        self.assertEqual([row["itemId"] for row in payload["props"]], ["spinner_01"])
        self.assertEqual(payload["sampleCount"], 31)
        self.assertFalse(payload["cached"])
        status, again = self.get(
            base, "/api/v2/motion/trajectory?items=spinner_01&seconds=1&rate=30")
        self.assertTrue(again["cached"])
        status, refusal = self.get(
            base, "/api/v2/motion/trajectory?items=no_such_prop&seconds=1")
        self.assertEqual(status, 200)
        self.assertFalse(refusal["ok"])
        self.assertIn("no such prop", refusal["error"])

    def test_trajectory_refuses_a_bad_rate(self):
        app = self.open_level()
        base = self.start(app)
        status, payload = self.get(base, "/api/v2/motion/trajectory?rate=7")
        self.assertEqual(status, 200)
        self.assertFalse(payload["ok"])
        self.assertIn("rate", payload["error"])


@unittest.skipUnless(reference_available(), "the reference level is not on this machine")
class ReferenceMotionTests(unittest.TestCase):
    """The counts and speeds the reference level's own data pins down (6.7)."""

    @classmethod
    def setUpClass(cls):
        cls.document = v2_document.load(REFERENCE)

    def test_the_reference_counts_match_the_format(self):
        counts = self.document.summary["counts"]
        self.assertEqual(counts["motions"], 14)
        self.assertEqual(counts["paths"], 4)
        self.assertEqual(counts["motionProps"], 113)
        self.assertEqual(counts["attachedProps"], 15)
        self.assertEqual(counts["props"], 511)

    def test_the_class_distribution_is_the_measured_one(self):
        counts = self.document.motion_counts()
        by_class = counts["byClass"]
        self.assertEqual(by_class["sporadic_walk"], 31)
        self.assertEqual(by_class["hop"], 18)
        self.assertEqual(by_class["sway"], 15)
        self.assertEqual(by_class["wait_for_player"], 15)
        self.assertEqual(by_class["whack_a_mole"], 8)
        self.assertEqual(by_class["path_collision"], 7)
        self.assertEqual(by_class["spin"], 7)
        self.assertEqual(by_class["clouds"], 5)
        self.assertEqual(by_class["flip"], 2)
        self.assertEqual(by_class["bob"], 2)
        self.assertEqual(by_class["sloping_path"], 2)
        self.assertEqual(by_class["back_and_forth"], 1)
        # The 15 attached props are the format's dragged_along class (6.7.3).
        self.assertEqual(by_class["dragged_along"], 15)
        self.assertEqual(counts["waitForPlayerProps"], 15)

    def test_the_four_paths_keep_their_measured_speeds(self):
        payload = self.document.motion_paths_payload()
        self.assertEqual(payload["count"], 4)
        speeds = {path["id"]: path["speedCmPerSec"] for path in payload["paths"]}
        self.assertEqual(speeds["p15"], 36.6)      # sloping_path
        self.assertEqual(speeds["p22"], 18.3)      # back_and_forth
        self.assertEqual(speeds["p23"], 24.4)      # path_collision
        users = {path["id"]: len(path["users"]) for path in payload["paths"]}
        self.assertEqual(users["p15"], 2)
        self.assertEqual(users["p22"], 1)
        self.assertEqual(users["p23"], 7)

    def test_a_hop_prop_carries_the_size_group_speed_and_a_radius_prop_its_ring(self):
        if not motion_sim_available():
            self.skipTest("motion_sim is not available")
        payload = self.document.motion_payload()
        self.assertTrue(payload["available"], payload["reason"])
        rows = {row["itemId"]: row for row in payload["props"]}
        hop = next(row for row in rows.values() if row["motionClass"] == "hop")
        # MotionMath.HopSpeedCmPerFrame(0) = 4 cm/game-frame; the measured 6.05/6.60 cm dip
        # is exactly that speed's recurrence (motion program 12.6).
        self.assertEqual(hop["hopSpeedCmPerFrame"], 4)
        ring = next(row for row in rows.values()
                    if row["motionClass"] == "wait_for_player")
        self.assertEqual(ring["triggerRadiusM"], 2.0)
        self.assertIsNotNone(ring["groundY"])
        # 7 of the 15 wait-for-player props have the walk-up gate (motionID 0x00); the other
        # 8 need inZone && parentRolled (0x04) and never fire in a preview (12.11).
        gates = [row["triggerMotionId"] for row in rows.values()
                 if row["motionClass"] == "wait_for_player"]
        self.assertEqual(gates.count(0), 7)
        self.assertEqual(gates.count(4), 8)


@unittest.skipUnless(motion_sim_available(), "the .NET SDK / motion_sim is not available")
@unittest.skipUnless(reference_available(), "the reference level is not on this machine")
class ReferenceTrajectoryTests(unittest.TestCase):
    """The samples the browser plays back, checked against MotionMath's hand values.

    Every expectation here is a number the *research* states (a period, a swing amplitude, a
    path speed), measured back out of the sampled trajectory.  None of them is a re-run of
    the implementation: the trajectory comes from MotionMath through motion_sim, and the
    expectation comes from the documents.
    """

    @classmethod
    def setUpClass(cls):
        cls.params = motion_cli.params(REFERENCE)
        cls.rows = {row["itemId"]: row for row in cls.params["props"]}

    def example(self, motion_class):
        """A prop of that class with no parent, so the measured channel is the class's own."""
        for item_id in sorted(self.rows):
            row = self.rows[item_id]
            if row.get("motionClass") == motion_class and not row.get("parent"):
                return item_id, row
        self.fail("no %s prop in the reference level" % motion_class)

    def samples(self, item_id, seconds):
        payload = motion_cli.trajectory(REFERENCE, seconds=seconds, items=[item_id])
        row = payload["props"][0]
        count = len(row["pos"]) // 3
        positions = [(row["pos"][index * 3], row["pos"][index * 3 + 1],
                      row["pos"][index * 3 + 2]) for index in range(count)]
        rotations = [tuple(row["rot"][index * 4:index * 4 + 4]) for index in range(count)]
        return payload, positions, rotations

    def test_spin_turns_once_every_419_seconds(self):
        # Motion program 12.1: Spin advances 0.05 rad per game frame, so one turn is
        # 2*pi/0.05 = 125.66 frames = 4.18879 s (4.19 s).  Measure the rate from the samples
        # and derive the period from it.  The rate is the rotation *about the spawn frame's
        # up axis*: the world quaternion is spawn * R(euler), so the step-to-step relative
        # rotation cancels the spawn orientation (a prop need not stand upright).
        item_id, row = self.example("spin")
        self.assertEqual(row["motionClass"], "spin")
        payload, _positions, rotations = self.samples(item_id, 0.5)
        self.assertEqual(payload["rate"], 30)
        rate = self.relative_yaw(rotations[0], rotations[1])
        self.assertAlmostEqual(rate, 0.05, delta=1e-4)          # rad per game frame
        period_frames = 2.0 * math.pi / rate
        self.assertAlmostEqual(period_frames, 125.6637, delta=0.05)
        self.assertAlmostEqual(period_frames / 30.0, 4.18879, delta=1e-3)
        self.assertAlmostEqual(period_frames / 30.0, 4.19, delta=0.01)

    def test_sway_moves_on_the_measured_ellipse_of_the_full_model_height(self):
        # 6.7.3 / 5.7: the horizontal peak-to-peak is 0.342 of the full height and the
        # vertical peak-to-peak 0.030 of it (sunflower: 82.2 / 7.25 cm against the measured
        # 82.0 / 7.19).  The period is 3.0 s, so a 7 s window covers two full swings.
        item_id, row = self.example("sway")
        full_height = 2.0 * row["halfHeightM"]
        _payload, positions, _rotations = self.samples(item_id, 7.0)
        xs = [point[0] for point in positions]
        ys = [point[1] for point in positions]
        zs = [point[2] for point in positions]
        horizontal = [max(xs) - min(xs), max(zs) - min(zs)]
        swung = max(horizontal)
        flat = min(horizontal)
        self.assertAlmostEqual(swung, 0.342 * full_height, delta=0.03 * full_height)
        # The swing is one horizontal axis: the other axis barely moves (the spawn rotation
        # is a yaw, so the ellipse stays horizontal).
        self.assertLess(flat, 0.02 * full_height)
        self.assertAlmostEqual(max(ys) - min(ys), 0.030 * full_height,
                               delta=0.01 * full_height)

    def test_hop_dips_the_measured_six_centimetres_and_comes_back(self):
        # 12.6 / 15.1 item 2: at speed 4 the dt = 1 recurrence is -3, -5, -6, -6, -5, -3, 0 cm,
        # i.e. 6.0 cm deep, and the measured dips are 6.05 / 6.60 cm.  The prop waits 0-8.5 s
        # between hops, so a 20 s window is guaranteed to contain one.
        item_id, _row = self.example("hop")
        _payload, positions, _rotations = self.samples(item_id, 20.0)
        ys = [point[1] for point in positions]
        depth = max(ys) - min(ys)
        self.assertGreater(depth, 0.055)
        self.assertLess(depth, 0.075)

    def test_a_path_prop_walks_at_the_recorded_24_4_centimetres_per_second(self):
        item_id, row = self.example("path_collision")
        self.assertEqual(row["path"], "p23")
        self.assertAlmostEqual(row["speedMps"], 0.244, places=6)
        payload, positions, _rotations = self.samples(item_id, 3.0)
        self.assertEqual(payload["rate"], 30)
        # Sample 0 is the resolved start point (12.9 teleports onto the nearest path point),
        # so compare one second of travel with the next: 0.244 m/s * 1 s = 0.244 m.
        travelled = math.dist(positions[30], positions[60])
        self.assertAlmostEqual(travelled, 0.244, delta=0.01)

    def test_the_whole_level_trajectory_is_fast_enough_for_a_page_load(self):
        started = time.time()
        payload = motion_cli.trajectory(REFERENCE, seconds=30.0)
        elapsed = time.time() - started
        self.assertEqual(payload["rate"], 30)
        self.assertEqual(payload["sampleCount"], 901)
        self.assertGreaterEqual(len(payload["props"]), 113)
        for row in payload["props"]:
            self.assertEqual(len(row["pos"]), 901 * 3, row["itemId"])
            self.assertEqual(len(row["rot"]), 901 * 4, row["itemId"])
        # A cold request includes the tool's own start-up; the bar is "a person does not
        # wait", not a benchmark number (measured ~1.0 s for 155 props x 901 samples).
        self.assertLess(elapsed, 20.0, "30 s of the whole level took %.2f s" % elapsed)

    def test_an_attached_prop_only_moves_when_its_parent_does(self):
        payload = motion_cli.trajectory(REFERENCE, seconds=2.0)
        rows = {row["itemId"]: row for row in payload["props"]}
        # coconut_crab_003 is attached with no parent (the original's "joint part" flag,
        # parent = -1): there is nothing to follow, so it is not in the moving set at all.
        self.assertNotIn("coconut_crab_003", rows)
        # small_fiddler_crab_036 rides a spinning parent, so its pose does change.
        rider = rows["small_fiddler_crab_036"]
        self.assertTrue(rider["followsParent"])
        self.assertNotEqual(rider["pos"][:3], rider["pos"][-3:])

    @staticmethod
    def yaw(quaternion):
        x, y, z, w = quaternion
        return math.atan2(2.0 * (w * y + x * z), 1.0 - 2.0 * (y * y + x * x))

    @classmethod
    def relative_yaw(cls, first, second):
        """The rotation from ``first`` to ``second``, read about the spawn frame's up axis.

        ``world = spawn * R(euler)`` for a spawn-local class, so ``conj(first) * second`` is
        ``conj(R0) * R1``: a pure rotation about the spawn frame's +Y, which is exactly the
        angle the class advanced.
        """
        ax, ay, az, aw = first
        bx, by, bz, bw = second
        # conj(a) * b
        x = aw * bx - ax * bw - ay * bz + az * by
        y = aw * by + ax * bz - ay * bw - az * bx
        z = aw * bz - ax * by + ay * bx + az * bw
        w = aw * bw + ax * bx + ay * by + az * bz
        return 2.0 * math.atan2(y, w)


class ShotTimeRequestTests(MotionFixture):
    """``shot.py --time`` reaches the page through the same request the GUI uses."""

    def request(self, *argv):
        from Tools.leveleditor import shot
        parser = shot.build_parser()
        args = parser.parse_args([self.level_id, "--levels-dir", str(self.levels),
                                 "--state-dir", str(self.state_dir), *argv])
        return shot.normalize_request(args)

    def test_time_is_carried_and_implies_the_motion_layers(self):
        request = self.request("--view", "top", "--time", "12.5")
        self.assertEqual(request["time"], 12.5)
        self.assertTrue(request["layers"]["motions"])
        self.assertIn("motions", request["layers"]["requested"])

    def test_no_time_means_no_motion_layer(self):
        request = self.request("--view", "top")
        self.assertIsNone(request["time"])
        self.assertFalse(request["layers"]["motions"])

    def test_a_negative_time_is_refused(self):
        from Tools.leveleditor import shot
        with self.assertRaises(shot.ShotError):
            self.request("--view", "top", "--time", "-1")


if __name__ == "__main__":
    unittest.main()
