"""Collider-proxy schema and fitter tests (no Blender, no Unity).

Run from the repository root:

    python3 -m unittest discover -s Docs/tests -p 'test_*.py'

What is pinned here:
* the optional ``defs[].collider`` block: schema, canonical bytes, convexity and
  "inside the visual AABB";
* the fitter's decision order on synthetic shapes (perfect sphere, cube, capsule,
  L shape);
* the compatibility promise: a level with no ``collider`` block canonicalises and
  validates byte-for-byte as it did at git HEAD.
"""

import copy
import json
import math
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import collider_proxy as proxies  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Docs.tests.levelbuild.test_placement_core import valid_doc  # noqa: E402

MANIFESTS = core.load_manifests()

# The two level families the compatibility test covers: the shipped story level and
# the nine physics-test cells (all authored before the ``collider`` field existed).
COMPATIBILITY_LEVELS = ["Docs/Design/Levels/story_01_bench_size/placement.json"] + [
    "Docs/Design/Levels/physics_test_field/%s/placement.json" % cell
    for cell in ("C01", "C03", "C05", "C06", "C08", "C09", "C10", "C13", "C14")
]


# --------------------------------------------------------------------------- #
# synthetic point clouds
# --------------------------------------------------------------------------- #
def sphere_points(radius, count=300):
    """Fibonacci points exactly on a sphere (a point cloud, so no facet sag)."""
    points = []
    golden = math.pi * (3.0 - math.sqrt(5.0))
    for index in range(count):
        y = 1.0 - 2.0 * (index + 0.5) / count
        ring = math.sqrt(max(0.0, 1.0 - y * y))
        angle = golden * index
        points.append((radius * ring * math.cos(angle), radius * y,
                       radius * ring * math.sin(angle)))
    return points


def cube_mesh(edge):
    half = 0.5 * edge
    vertices = [(x, y, z) for x in (-half, half) for y in (-half, half)
                for z in (-half, half)]
    index = {(x, y, z): i for i, (x, y, z) in enumerate(vertices)}

    def quad(a, b, c, d):
        return [(index[a], index[b], index[c]), (index[a], index[c], index[d])]

    faces = []
    faces += quad((-half, -half, -half), (-half, half, -half),
                  (-half, half, half), (-half, -half, half))       # -x
    faces += quad((half, -half, -half), (half, -half, half),
                  (half, half, half), (half, half, -half))          # +x
    faces += quad((-half, -half, -half), (-half, -half, half),
                  (half, -half, half), (half, -half, -half))        # -y
    faces += quad((-half, half, -half), (half, half, -half),
                  (half, half, half), (-half, half, half))          # +y
    faces += quad((-half, -half, -half), (half, -half, -half),
                  (half, half, -half), (-half, half, -half))        # -z
    faces += quad((-half, -half, half), (-half, half, half),
                  (half, half, half), (half, -half, half))          # +z
    return vertices, faces


def capsule_points(radius, half_height, rings=12, spokes=16):
    """Points on a capsule whose segment runs along X."""
    points = []
    for ring in range(rings):
        x = -half_height + 2.0 * half_height * ring / (rings - 1)
        for spoke in range(spokes):
            angle = 2.0 * math.pi * spoke / spokes
            points.append((x, radius * math.cos(angle), radius * math.sin(angle)))
    for sign in (-1, 1):
        for latitude in range(1, 9):
            phi = 0.5 * math.pi * latitude / 8.0
            ring_radius = radius * math.cos(phi)
            x = sign * (half_height + radius * math.sin(phi))
            for spoke in range(spokes):
                angle = 2.0 * math.pi * spoke / spokes
                points.append((x, ring_radius * math.cos(angle),
                               ring_radius * math.sin(angle)))
    return points


def l_shape_mesh():
    """An L prism (0.6 x 0.6 footprint, 0.2 thick) with a real closed mesh.

    The faces matter: without a mesh volume the "the concavity became excess volume"
    check would have no reference to compare against.
    """
    polygon = [(0.0, 0.0), (0.6, 0.0), (0.6, 0.2), (0.2, 0.2), (0.2, 0.6), (0.0, 0.6)]
    triangles = [(0, 1, 3), (1, 2, 3), (0, 3, 5), (3, 4, 5)]
    vertices = [(x, y, -0.1) for x, y in polygon] + [(x, y, 0.1) for x, y in polygon]
    n = len(polygon)
    faces = []
    for a, b, c in triangles:
        faces.append((a, b, c))                       # bottom, outward -Z
        faces.append((a + n, c + n, b + n))           # top, outward +Z
    for index in range(n):
        nxt = (index + 1) % n
        faces.append((index, nxt, nxt + n))
        faces.append((index, nxt + n, index + n))
    return vertices, faces


def thirty_point_hull():
    """A valid 30-point sphere hull (for the >24 rejection test)."""
    return [list(point) for point in sphere_points(0.1, 30)]


# --------------------------------------------------------------------------- #
# fitter decisions
# --------------------------------------------------------------------------- #
class FitterSyntheticTests(unittest.TestCase):
    def test_perfect_sphere_fits_a_sphere(self):
        fit = proxies.fit_proxy(sphere_points(0.15))
        self.assertEqual(fit.kind, "sphere")
        self.assertTrue(fit.accepted)
        self.assertLess(fit.fit_error, 1e-6)
        self.assertAlmostEqual(fit.radius, 0.15, places=6)
        self.assertAlmostEqual(fit.center[0], 0.0, places=6)

    def test_visual_bounds_clamp_a_fitted_sphere(self):
        points = sphere_points(0.15)
        unbounded = proxies.fit_proxy(points)
        self.assertAlmostEqual(unbounded.radius, 0.15, places=6)
        bounds = ((-0.14, -0.14, -0.14), (0.14, 0.14, 0.14))
        bounded = proxies.fit_proxy(points, bounds=bounds)
        self.assertEqual(bounded.kind, "sphere")
        self.assertLessEqual(bounded.radius, 0.14 + 1e-9)
        low, high = proxies.collider_bounds(bounded.block("auto")["collider"])
        for axis in range(3):
            self.assertGreaterEqual(low[axis], bounds[0][axis] - 1e-6)
            self.assertLessEqual(high[axis], bounds[1][axis] + 1e-6)

    def test_cube_fits_a_box(self):
        vertices, faces = cube_mesh(0.3)
        fit = proxies.fit_proxy(vertices, faces)
        self.assertEqual(fit.kind, "box")
        self.assertTrue(fit.accepted)
        self.assertLess(fit.fit_error, 1e-9)
        for extent in fit.size:
            self.assertAlmostEqual(extent, 0.3, places=9)

    def test_capsule_fits_a_capsule(self):
        fit = proxies.fit_proxy(capsule_points(0.08, 0.2))
        self.assertEqual(fit.kind, "capsule")
        self.assertTrue(fit.accepted)
        self.assertEqual(fit.axis, "x")
        self.assertAlmostEqual(fit.radius, 0.08, delta=2e-3)
        self.assertAlmostEqual(fit.half_height, 0.2, delta=2e-3)
        self.assertLess(fit.max_error_m, 2e-3)

    def test_l_shape_is_a_convex_hull_or_box_and_the_concavity_is_measured(self):
        vertices, faces = l_shape_mesh()
        fit = proxies.fit_proxy(vertices, faces)
        self.assertIn(fit.kind, ("box", "convex"))
        self.assertGreater(fit.mesh_volume_ratio, 1.5,
                           "the L's notch must show up as excess volume even when the "
                           "outer envelope fits (the one-collider limit)")
        self.assertLessEqual(fit.fit_error, 0.2)

    def test_a_flat_disc_is_not_a_sphere_or_box(self):
        # A 0.24 x 0.025 x 0.24 plate: a huge sphere can graze the flat cap and a box
        # is flush with the top and bottom, so both need the volume guard to lose.
        points = []
        for index in range(120):
            angle = 2.0 * math.pi * index / 120
            for y in (-0.0125, 0.0125):
                points.append((0.12 * math.cos(angle), y, 0.12 * math.sin(angle)))
        points += [(0.0, -0.0125, 0.0), (0.0, 0.0125, 0.0)]
        fit = proxies.fit_proxy(points)
        self.assertEqual(fit.kind, "convex")
        self.assertLess(fit.volume_ratio, 1.1)

    def test_fit_is_deterministic(self):
        vertices, faces = cube_mesh(0.3)
        first = proxies.fit_proxy(vertices, faces).block("auto")
        second = proxies.fit_proxy(copy.deepcopy(vertices), copy.deepcopy(faces)).block("auto")
        self.assertEqual(json.dumps(first), json.dumps(second))

    def test_fit_error_is_in_range_and_the_block_validates(self):
        for points, faces in ((sphere_points(0.05, 120), None),
                              (capsule_points(0.02, 0.06, 6, 8), None),
                              l_shape_mesh()):
            fit = proxies.fit_proxy(points, faces)
            self.assertGreaterEqual(fit.fit_error, 0.0)
            self.assertLessEqual(fit.fit_error, 1.0)
            block = fit.block("auto")["collider"]
            self.assertEqual(proxies.collider_schema_errors(block), [], block)


# --------------------------------------------------------------------------- #
# schema: valid, canonical, rejected
# --------------------------------------------------------------------------- #
def doc_with_collider(block):
    document = valid_doc()
    document["defs"][0]["collider"] = copy.deepcopy(block)
    return document


class ColliderSchemaTests(unittest.TestCase):
    def test_optional_block_round_trips_through_canonical_bytes(self):
        block = {"type": "sphere", "center": [0.0, 0.0, 0.0], "radius": 0.04,
                 "source": "auto", "fit_error": 0.0123}
        document = doc_with_collider(block)
        self.assertTrue(core.validate(document).ok)
        first = core.dump_doc(document)
        second = core.dump_doc(json.loads(first))
        self.assertEqual(first, second)
        self.assertIn('"collider"', first)
        self.assertIn('"fit_error": 0.0123', first)

    def test_convex_hull_points_are_relative_to_center(self):
        # The C# consumer adds `center` exactly once; a hull point is an offset, not
        # an absolute local coordinate.  This pins the convention with a non-zero
        # centre, where "add it twice" would be visible immediately.
        block = {"type": "convex", "center": [0.1, 0.2, 0.3],
                 "hull": [[-0.05, -0.05, -0.05], [0.05, -0.05, -0.05],
                          [-0.05, 0.05, -0.05], [-0.05, -0.05, 0.05]]}
        self.assertEqual(proxies.collider_schema_errors(block), [])
        low, high = proxies.collider_bounds(block)
        for axis in range(3):
            self.assertAlmostEqual(low[axis], block["center"][axis] - 0.05, places=9)
            self.assertAlmostEqual(high[axis], block["center"][axis] + 0.05, places=9)

    def test_hull_canonicalises_to_flat_for_jsonutility(self):
        # Unity's JsonUtility cannot deserialize an array of arrays, so the on-disk
        # `hull` is flat; the readable nested form is accepted on input.
        block = {"type": "convex", "center": [0.1, 0.2, 0.3],
                 "hull": [[-0.05, -0.05, -0.05], [0.05, -0.05, -0.05],
                          [-0.05, 0.05, -0.05], [-0.05, -0.05, 0.05]]}
        canonical = proxies.canonical_collider(block)
        self.assertEqual(len(canonical["hull"]), 12)
        self.assertTrue(all(isinstance(value, float) for value in canonical["hull"]))
        self.assertEqual(proxies.collider_schema_errors(canonical), [])
        self.assertEqual(canonical, proxies.canonical_collider(canonical))

    def test_canonical_field_order_is_fixed(self):
        block = {"fit_error": 0.02, "source": "manual", "hull": [[1, 0, 0], [0, 1, 0],
                                                                 [0, 0, 1], [0, 0, 0]],
                 "center": [0, 0, 0], "type": "convex"}
        canonical = proxies.canonical_collider(block)
        self.assertEqual(list(canonical), ["type", "center", "hull", "source",
                                           "fit_error"])

    def test_a_prop_without_a_collider_block_still_validates(self):
        report = core.validate(valid_doc())
        self.assertTrue(report.ok, report.errors)
        self.assertFalse(any("[collider]" in error for error in report.errors))

    def test_convex_hull_rejects_an_interior_point(self):
        block = {"type": "convex", "center": [0, 0, 0],
                 "hull": [[0, 0, 0], [0.1, 0, 0], [0, 0.1, 0], [0, 0, 0.1],
                          [0.03, 0.03, 0.03]]}
        errors = proxies.collider_schema_errors(block)
        self.assertTrue(any("not a convex vertex" in error for error in errors), errors)

    def test_convex_hull_rejects_the_wrong_point_count(self):
        for hull in ([[0, 0, 0], [1, 0, 0], [0, 1, 0]],
                     thirty_point_hull()):
            errors = proxies.collider_schema_errors({"type": "convex", "center": [0, 0, 0],
                                                     "hull": hull})
            self.assertTrue(any("needs 4..24 points" in error for error in errors), errors)

    def test_convex_hull_rejects_nan_and_coplanar_points(self):
        nan_errors = proxies.collider_schema_errors(
            {"type": "convex", "center": [0, 0, 0],
             "hull": [[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, float("nan")]]})
        self.assertTrue(any("finite" in error for error in nan_errors), nan_errors)
        flat_errors = proxies.collider_schema_errors(
            {"type": "convex", "center": [0, 0, 0],
             "hull": [[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 1, 0]]})
        self.assertTrue(any("coplanar" in error or "zero volume" in error
                            for error in flat_errors), flat_errors)

    def test_each_type_rejects_missing_and_cross_type_fields(self):
        cases = [
            ({"type": "sphere", "center": [0, 0, 0]}, "radius"),
            ({"type": "box", "center": [0, 0, 0], "size": [0, 1, 1]}, "positive"),
            ({"type": "capsule", "center": [0, 0, 0], "radius": 0.1,
              "halfHeight": 0.1, "axis": "w"}, "axis"),
            ({"type": "box", "center": [0, 0, 0], "size": [1, 1, 1], "radius": 0.1},
             "not a box field"),
        ]
        for block, fragment in cases:
            errors = proxies.collider_schema_errors(block)
            self.assertTrue(any(fragment in error for error in errors), (block, errors))

    def test_fit_error_must_be_in_unit_interval(self):
        for value in (-0.1, 1.1, float("inf")):
            errors = proxies.collider_schema_errors(
                {"type": "sphere", "center": [0, 0, 0], "radius": 0.1,
                 "fit_error": value})
            self.assertTrue(any("fit_error" in error for error in errors), errors)

    def test_collider_must_stay_inside_the_visual_aabb(self):
        document = valid_doc()
        document["defs"][0]["collider"] = {"type": "sphere", "center": [0, 0, 0],
                                           "radius": 5.0}
        errors = core.validate(document).errors
        self.assertTrue(any("outside the visual AABB" in error for error in errors), errors)

    def test_a_collider_outside_the_aabb_is_still_detected_when_the_def_has_no_manifest(self):
        document = valid_doc()
        document["defs"][0]["collider"] = {"type": "box", "center": [0, 0, 0],
                                           "size": [100, 1, 1]}
        errors = core.validate(document).errors
        self.assertTrue(any("outside the visual AABB" in error for error in errors), errors)

    def test_a_convex_hull_outside_the_aabb_is_detected_through_its_center(self):
        # `hull` points are offsets; this block's real x extent is center.x + 0.5,
        # which is far outside the apple's half extent.  A validator that ignored
        # `center` (or treated the hull as absolute) would pass it.
        document = valid_doc()
        document["defs"][0]["collider"] = {
            "type": "convex", "center": [0.5, 0.0, 0.0],
            "hull": [[0.0, 0.0, 0.0], [0.01, 0.0, 0.0], [0.0, 0.01, 0.0],
                     [0.0, 0.0, 0.01]]}
        errors = core.validate(document).errors
        self.assertTrue(any("outside the visual AABB" in error for error in errors), errors)


# --------------------------------------------------------------------------- #
# compatibility: a level without ``collider`` is today's level
# --------------------------------------------------------------------------- #
class NoColliderBlockCompatibilityTests(unittest.TestCase):
    def _head_text(self, relative):
        try:
            done = subprocess.run(["git", "-C", str(ROOT), "show", "HEAD:" + relative],
                                  capture_output=True, text=True)
        except OSError as error:
            self.skipTest("git unavailable: %s" % error)
        if done.returncode != 0 or not done.stdout:
            self.skipTest("cannot read %s from git HEAD" % relative)
        return done.stdout

    def test_head_levels_still_validate_and_migrate_cleanly(self):
        # The C+D format bump (valueUnits -> derived growthMm3) means HEAD's bytes are no
        # longer the writer's output, so byte equality with HEAD is retired. What must
        # still hold: every old file is VALID INPUT, and running it through the writer
        # gives a document the validator accepts with exactly the disk file's errors.
        for relative in COMPATIBILITY_LEVELS:
            if not (ROOT / relative).exists():
                continue
            head_text = self._head_text(relative)
            disk_text = (ROOT / relative).read_text(encoding="utf-8")
            disk_doc = json.loads(disk_text)
            self.assertEqual(core.dump_doc(disk_doc), disk_text, relative)

            head_doc = json.loads(head_text)
            self.assertFalse(any("collider" in entry for entry in head_doc.get("defs", [])),
                             relative)
            migrated_text = core.dump_doc(head_doc)
            self.assertEqual(core.dump_doc(json.loads(migrated_text)), migrated_text,
                             "the migration itself must be deterministic: " + relative)
            migrated = json.loads(migrated_text)
            self.assertNotIn("valueUnits", migrated.get("level", {}).get("expected", {}),
                             "the deprecated total is dropped: " + relative)
            self.assertFalse(any("valueUnits" in entry for entry in migrated["props"]),
                             "the deprecated per-prop key is dropped: " + relative)
            self.assertEqual(core.validate(migrated).errors,
                             core.validate(disk_doc).errors, relative)
            self.assertFalse(any("[collider]" in error for error in core.validate(migrated).errors),
                             relative)


def _box_mesh(low, high):
    """A closed axis-aligned box as (vertices, triangle faces)."""
    x0, y0, z0 = low
    x1, y1, z1 = high
    vertices = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
                (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    quads = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (3, 7, 6, 2), (0, 4, 7, 3),
             (1, 2, 6, 5)]
    faces = []
    for a, b, c, d in quads:
        faces.append((a, b, c))
        faces.append((a, c, d))
    return vertices, faces


def _merge(*meshes):
    vertices, faces = [], []
    for mesh_vertices, mesh_faces in meshes:
        base = len(vertices)
        vertices.extend(mesh_vertices)
        faces.extend(tuple(base + index for index in face) for face in mesh_faces)
    return vertices, faces


def _deck():
    """A laundry-deck-like stand: a full-width foot, a thin pole, a tray on top (metres)."""
    return _merge(_box_mesh((-0.7, -1.0, -0.03), (0.7, -0.85, 0.03)),
                  _box_mesh((-0.03, -0.85, -0.03), (0.03, 0.8, 0.03)),
                  _box_mesh((-0.15, 0.8, -0.15), (0.15, 1.0, 0.15)))


def _inside_any(point, boxes, margin=1e-9):
    return any(all(low[k] - margin <= point[k] <= high[k] + margin for k in range(3))
               for low, high in boxes)


class SolidPartsTests(unittest.TestCase):
    """`split_mesh_into_boxes` / `solid_parts`: the static-solid split (LEVEL_FORMAT_V2 §4)."""

    def test_a_sparse_stand_becomes_its_bars_and_leaves_the_gap_open(self):
        vertices, faces = _deck()
        boxes = proxies.split_mesh_into_boxes(vertices, faces)
        self.assertEqual(len(boxes), 3)
        total = sum((h[0] - l[0]) * (h[1] - l[1]) * (h[2] - l[2]) for l, h in boxes)
        whole = 1.4 * 2.0 * 0.3
        self.assertLess(total / whole, 0.15, boxes)
        # Every surface point is covered ...
        for point in vertices:
            self.assertTrue(_inside_any(point, boxes), point)
        # ... and the space beside the pole, above the foot, is empty (the old one box
        # around the whole stand was a wall there).
        for point in ((0.4, -0.5, 0.0), (-0.4, 0.0, 0.0), (0.4, 0.5, 0.1)):
            self.assertFalse(_inside_any(point, boxes), point)

    def test_a_solid_box_does_not_split(self):
        vertices, faces = _box_mesh((-0.5, -0.5, -0.5), (0.5, 0.5, 0.5))
        self.assertEqual(len(proxies.split_mesh_into_boxes(vertices, faces)), 1)
        self.assertEqual(proxies.solid_parts(vertices, faces), [])

    def test_the_part_count_is_capped(self):
        rungs = [_box_mesh((-0.5, y, -0.02), (0.5, y + 0.04, 0.02))
                 for y in (0.0, 0.3, 0.6, 0.9, 1.2, 1.5)]
        vertices, faces = _merge(*rungs)
        self.assertLessEqual(len(proxies.split_mesh_into_boxes(vertices, faces)),
                             proxies.PART_MAX_COUNT)
        self.assertEqual(len(proxies.split_mesh_into_boxes(vertices, faces, max_parts=2)), 2)

    def test_parts_are_deterministic_rounded_outward_and_sorted(self):
        vertices, faces = _deck()
        first = proxies.solid_parts(vertices, faces)
        self.assertEqual(first, proxies.solid_parts(list(vertices), list(faces)))
        self.assertEqual(json.dumps(first), json.dumps(proxies.solid_parts(vertices, faces)))
        lows = [[part["center"][k] - part["size"][k] / 2.0 for k in range(3)] for part in first]
        self.assertEqual([low[1] for low in lows], sorted(low[1] for low in lows))
        boxes = [([p["center"][k] - p["size"][k] / 2.0 for k in range(3)],
                  [p["center"][k] + p["size"][k] / 2.0 for k in range(3)]) for p in first]
        for point in vertices:
            self.assertTrue(_inside_any(point, boxes, margin=1e-6), point)
        for part in first:
            self.assertTrue(all(value >= proxies.PART_MIN_SIZE_M - 1e-9
                                for value in part["size"]), part)
            self.assertEqual(sorted(part), sorted(proxies.PART_KEYS))

    def test_a_thin_board_gets_the_minimum_thickness(self):
        # Two 2 mm boards 2 m apart: two parts, each grown to the 1 cm floor (a degenerate
        # box would be refused by the arena).
        vertices, faces = _merge(_box_mesh((0.0, 0.0, 0.0), (1.0, 1.0, 0.002)),
                                 _box_mesh((3.0, 0.0, 0.0), (4.0, 1.0, 0.002)))
        parts = proxies.solid_parts(vertices, faces)
        self.assertEqual(len(parts), 2)
        for part in parts:
            self.assertAlmostEqual(part["size"][2], proxies.PART_MIN_SIZE_M, places=3)

    def test_a_flat_mesh_is_not_cut(self):
        vertices = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 0.0),
                    (3.0, 0.0, 0.0), (4.0, 0.0, 0.0), (4.0, 1.0, 0.0), (3.0, 1.0, 0.0)]
        faces = [(0, 1, 2), (0, 2, 3), (4, 5, 6), (4, 6, 7)]
        self.assertEqual(proxies.solid_parts(vertices, faces), [])

    def test_fill_ratio_selects_only_sparse_hulls(self):
        vertices, faces = _deck()
        hull = proxies.convex_hull(vertices)[0]
        sparse = {"type": "convex", "center": [0.0, 0.0, 0.0],
                  "hull": [c for point in hull for c in point]}
        self.assertLess(proxies.hull_fill_ratio(sparse), proxies.PART_SPLIT_FILL_MAX)
        cube = {"type": "convex", "center": [0.0, 0.0, 0.0],
                "hull": [c for point in _box_mesh((0, 0, 0), (1, 1, 1))[0] for c in point]}
        self.assertAlmostEqual(proxies.hull_fill_ratio(cube), 1.0, places=6)
        self.assertIsNone(proxies.hull_fill_ratio({"type": "box", "center": [0, 0, 0],
                                                   "size": [1, 1, 1]}))

    def test_parts_schema(self):
        good = [{"center": [0.0, -0.9, 0.0], "size": [1.4, 0.2, 0.1]},
                {"center": [0.0, 0.1, 0.0], "size": [0.1, 1.8, 0.1]}]
        self.assertEqual(proxies.solid_parts_errors(good, [1.4, 2.0, 0.3]), [])
        self.assertTrue(proxies.solid_parts_errors(good[:1], [1.4, 2.0, 0.3]))
        self.assertTrue(proxies.solid_parts_errors(good * 3, [1.4, 2.0, 0.3]))
        self.assertTrue(proxies.solid_parts_errors(good, [1.0, 2.0, 0.3]))
        bad = copy.deepcopy(good)
        bad[0]["size"] = [1.4, 0.0, 0.1]
        self.assertTrue(proxies.solid_parts_errors(bad, [1.4, 2.0, 0.3]))
        bad = copy.deepcopy(good)
        bad[0]["axis"] = "y"
        self.assertTrue(proxies.solid_parts_errors(bad, [1.4, 2.0, 0.3]))


if __name__ == "__main__":
    unittest.main()
