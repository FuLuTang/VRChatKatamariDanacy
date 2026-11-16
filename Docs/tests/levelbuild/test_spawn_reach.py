"""The spawn reachability model on a tiny synthetic level (no reference data needed).

``Tools/levelbuild/spawn_reach.py`` answers "where can the ball roll, what does it touch, what
happens as it grows".  What would fail silently and is pinned here: the geometry helpers, the
step rule (1.35 D, both for rises and the old 0.25 D), walls and ledges, props as obstacles
only while their gate is closed, the contact test (``TryComputeReach``), the burying rule for
overlapping floor layers, and a greedy growth run that regrows the ball after each absorb.
"""

import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import spawn_reach as sr  # noqa: E402


class Builder:
    """Terrain triangles and props for a small level."""

    def __init__(self):
        self.vertices = []
        self.indices = []
        self.props = []

    def _tri(self, a, b, c):
        base = len(self.vertices)
        self.vertices.extend([a, b, c])
        self.indices.extend([base, base + 1, base + 2])

    def floor(self, x0, z0, x1, z1, y):
        """An up-facing rectangle at height ``y``."""
        self._tri((x0, y, z0), (x0, y, z1), (x1, y, z0))
        self._tri((x1, y, z1), (x1, y, z0), (x0, y, z1))

    def wall_x(self, x, z0, z1, y0, y1):
        """A vertical rectangle in the plane ``x`` (a riser or a wall)."""
        self._tri((x, y0, z0), (x, y1, z0), (x, y0, z1))
        self._tri((x, y1, z1), (x, y0, z1), (x, y1, z0))

    def prop(self, name, position, aabb, req_mm=100, growth=1_000_000, absorbable=True,
             quat=(0.0, 0.0, 0.0, 1.0)):
        self.props.append({"itemId": name, "def": name, "position": list(position),
                           "aabb": list(aabb), "absorbDiameterReqMm": req_mm,
                           "growthMm3": growth, "absorbable": absorbable,
                           "rotationQuat": list(quat)})

    def level(self, half=4.0):
        document = {"props": self.props,
                    "activityArea": {"walls": [{"points": [[-half, -half], [half, -half],
                                                           [half, half], [-half, half]]}]},
                    "structures": {"solids": []}}
        terrain = {"vertices": self.vertices, "indices": self.indices}
        return sr.Level(document, terrain)


def flat_level(prop_setup=None):
    b = Builder()
    b.floor(-4.0, -4.0, 4.0, 4.0, 0.0)
    if prop_setup:
        prop_setup(b)
    return b.level()


class GeometryTest(unittest.TestCase):
    def test_quaternion_matrix(self):
        ident = sr.quat_matrix((0, 0, 0, 1))
        self.assertEqual(ident[0], (1.0, 0.0, 0.0))
        half = math.sqrt(0.5)
        turned = sr.quat_matrix((0, half, 0, half))  # +90 degrees about y: x -> -z
        self.assertAlmostEqual(turned[2][0], -1.0)
        self.assertTrue(sr.is_unit_quaternion((0, 0, 0, -1)))
        self.assertFalse(sr.is_unit_quaternion((0, half, 0, half)))

    def test_triangle_distance(self):
        a, b, c = (0, 0, 0), (1, 0, 0), (0, 0, 1)
        self.assertAlmostEqual(sr.tri_dist((0.2, 0.5, 0.2), a, b, c), 0.5)
        self.assertAlmostEqual(sr.tri_dist((2.0, 0.0, 0.0), a, b, c), 1.0)
        self.assertAlmostEqual(sr.tri_dist((-1.0, 0.0, -1.0), a, b, c), math.sqrt(2.0))

    def test_convex_hull_of_a_box(self):
        cube = [(x, y, z) for x in (-0.5, 0.5) for y in (-0.5, 0.5) for z in (-0.5, 0.5)]
        tris, planes = sr.convex_hull_facets(cube)
        self.assertEqual(len(planes), 6)
        self.assertEqual(len(tris), 12)

    def test_solid_distance_and_vertical_hit(self):
        rot = sr.quat_matrix((0, 0, 0, 1))
        box = sr.Solid.from_box("b", (0, 1.0, 0), rot, (1.0, 2.0, 1.0))
        self.assertEqual(box.dist((0, 1.0, 0)), 0.0)
        self.assertAlmostEqual(box.dist((1.5, 1.0, 0)), 1.0)
        self.assertAlmostEqual(box.dist((0, 3.5, 0)), 1.5)
        self.assertAlmostEqual(box.first_hit_above(0, 0, -1.0, 5.0), 0.0)
        self.assertIsNone(box.first_hit_above(2.0, 0, -1.0, 5.0))

    def test_polyline_simplifies_a_straight_run(self):
        points = [(0.0, 0.0), (0.5, 0.01), (1.0, 0.0), (1.0, 1.0)]
        self.assertEqual(sr.simplify_polyline(points, 0.1), [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0)])


class ReachTest(unittest.TestCase):
    def test_flat_floor_area_is_the_activity_rectangle_less_the_wall_margin(self):
        level = flat_level()
        reach = sr.Reach(level, 0.222)
        node = reach.snap(0.0, 0.0)
        area = reach.reachable_area_m2(node)
        margin = 0.111 + sr.ACTIVITY_WALL_HALF_M
        expected = (8.0 - 2 * margin) ** 2
        self.assertAlmostEqual(area, expected, delta=expected * 0.03)

    def test_a_step_is_climbed_below_the_limit_and_a_wall_above_it(self):
        def setup(b):
            b.prop("filler", (0, 0, 0), (0.05, 0.05, 0.05), req_mm=9999, absorbable=False)

        for rise, climbs in ((0.2, True), (0.4, False)):
            b = Builder()
            b.floor(-4.0, -4.0, 1.0, 4.0, 0.0)
            b.wall_x(1.0, -4.0, 4.0, 0.0, rise)
            b.floor(1.0, -4.0, 4.0, 4.0, rise)
            level = b.level()
            reach = sr.Reach(level, 0.222)
            start = reach.snap(0.0, 0.0)
            region = reach.dijkstra({start: 0.0})
            top = reach.snap(2.0, 0.0)
            self.assertEqual(top in region, climbs, "rise %.2f" % rise)
        del setup

    def test_the_retired_step_rule_blocks_what_the_new_one_climbs(self):
        b = Builder()
        b.floor(-4.0, -4.0, 1.0, 4.0, 0.0)
        b.wall_x(1.0, -4.0, 4.0, 0.0, 0.08)
        b.floor(1.0, -4.0, 4.0, 4.0, 0.08)
        level = b.level()
        old = sr.Reach(level, 0.222, blockers="solids", step_diameters=sr.OLD_STEP_DIAMETERS,
                       max_drop=0.0555)
        new = sr.Reach(level, 0.222)
        top = new.snap(2.0, 0.0)
        self.assertNotIn(top, old.dijkstra({old.snap(0.0, 0.0): 0.0}))
        self.assertIn(top, new.dijkstra({new.snap(0.0, 0.0): 0.0}))

    def test_a_ledge_is_a_one_way_drop(self):
        b = Builder()
        b.floor(-4.0, -4.0, 1.0, 4.0, 1.0)
        b.wall_x(1.0, -4.0, 4.0, 0.0, 1.0)
        b.floor(1.0, -4.0, 4.0, 4.0, 0.0)
        reach = sr.Reach(b.level(), 0.222)
        high = reach.snap(0.0, 0.0)
        low = reach.snap(2.0, 0.0)
        self.assertIn(low, reach.dijkstra({high: 0.0}))
        self.assertNotIn(high, reach.dijkstra({low: 0.0}))

    def test_a_wall_keeps_the_ball_a_radius_away(self):
        b = Builder()
        b.floor(-4.0, -4.0, 4.0, 4.0, 0.0)
        b.wall_x(1.0, -4.0, 4.0, 0.0, 2.0)
        reach = sr.Reach(b.level(), 0.222)
        self.assertTrue(reach.valid(reach.snap(0.8, 0.0)))
        self.assertFalse(reach.valid(reach.snap(0.95, 0.0)))
        self.assertFalse(reach.valid(reach.snap(1.0, 0.0)))

    def test_a_prop_is_a_wall_until_its_gate_opens(self):
        def setup(b):
            b.prop("crate", (1.0, 0.25, 0.0), (0.5, 0.5, 0.5), req_mm=400, absorbable=True)

        level = flat_level(setup)
        small = sr.Reach(level, 0.222)
        big = sr.Reach(level, 0.45)
        blocked = small.snap(1.0, 0.0)
        self.assertFalse(small.valid(blocked))
        self.assertTrue(big.valid(big.snap(1.0, 0.0)))

    def test_a_low_prop_is_a_bump_not_a_wall(self):
        def setup(b):
            b.prop("pad", (1.0, 0.05, 0.0), (1.0, 0.1, 1.0), req_mm=9999, absorbable=False)

        reach = sr.Reach(flat_level(setup), 0.222)
        self.assertTrue(reach.valid(reach.snap(1.0, 0.0)))

    def test_a_slab_above_the_ball_buries_the_lower_layer(self):
        b = Builder()
        b.floor(-4.0, -4.0, 4.0, 4.0, 0.0)
        b.floor(-1.0, -1.0, 1.0, 1.0, 0.1)      # a floor patch 0.1 m above: less than one D
        reach = sr.Reach(b.level(), 0.222)
        cell = reach.level.layers[(0, 0)]
        self.assertEqual(len(cell), 2)
        self.assertFalse(reach.valid((0, 0, 0)))
        self.assertTrue(reach.valid((0, 0, 1)))

    def test_headroom_sees_a_ceiling(self):
        b = Builder()
        b.floor(-4.0, -4.0, 4.0, 4.0, 0.0)
        # a ceiling at 1.0 m: down-facing (reverse winding)
        b._tri((-4.0, 1.0, -4.0), (4.0, 1.0, -4.0), (-4.0, 1.0, 4.0))
        b._tri((4.0, 1.0, 4.0), (-4.0, 1.0, 4.0), (4.0, 1.0, -4.0))
        reach = sr.Reach(b.level(), 0.222)
        room = reach.headroom(reach.snap(0.0, 0.0), need=1.5)
        self.assertAlmostEqual(room, 1.0 - 0.222, delta=0.01)


class ContactTest(unittest.TestCase):
    def test_reach_is_radius_plus_half_diagonal(self):
        def setup(b):
            b.prop("cup", (1.0, 0.04, 0.0), (0.08, 0.08, 0.08), req_mm=100)

        level = flat_level(setup)
        reach = sr.Reach(level, 0.222)
        prop = level.by_id["cup"]
        self.assertAlmostEqual(prop.half_diagonal, 0.5 * math.sqrt(3) * 0.08)
        limit = 0.111 + prop.half_diagonal
        near = reach.snap(1.0 - (limit - 0.05), 0.0)
        far = reach.snap(1.0 - (limit + 0.15), 0.0)
        self.assertTrue(reach.touching(near, [prop]))
        self.assertFalse(reach.touching(far, [prop]))
        nodes = reach.contact_nodes(prop)
        self.assertIn(near, nodes)
        self.assertNotIn(far, nodes)

    def test_the_gate_floors_whole_millimetres(self):
        def setup(b):
            b.prop("edge", (1.0, 0.04, 0.0), (0.08, 0.08, 0.08), req_mm=222)
            b.prop("over", (1.0, 0.04, 1.0), (0.08, 0.08, 0.08), req_mm=223)

        level = flat_level(setup)
        self.assertTrue(level.by_id["edge"].gate_open(0.222))
        self.assertFalse(level.by_id["over"].gate_open(0.222))
        self.assertTrue(level.by_id["over"].gate_open(0.2235))


class GrowthTest(unittest.TestCase):
    def test_volume_to_diameter_round_trips(self):
        volume = math.pi / 6.0 * 222.0 ** 3
        self.assertAlmostEqual(sr.diameter_from_volume_mm3(volume), 0.222, places=6)

    def test_greedy_run_eats_nearest_first_and_regrows(self):
        def setup(b):
            b.prop("near", (0.6, 0.04, 0.0), (0.08, 0.08, 0.08), req_mm=100, growth=2_000_000)
            b.prop("mid", (0.6, 0.04, 1.5), (0.08, 0.08, 0.08), req_mm=100, growth=2_000_000)
            b.prop("big", (-1.5, 0.15, 0.0), (0.3, 0.3, 0.3), req_mm=240, growth=9_000_000)

        level = flat_level(setup)
        result = sr.simulate_growth(level, (0.0, 0.0), target_m=5.0)
        order = [event["id"] for event in result["events"]]
        self.assertEqual(order, ["near", "mid", "big"])
        diameters = [event["diameterAfterM"] for event in result["events"]]
        self.assertEqual(diameters, sorted(diameters))
        # "big" needs 240 mm: it was out of the gate at the start diameter, in after two cups
        self.assertGreaterEqual(math.floor(diameters[1] * 1000 + 1e-6), 223)
        # nothing absorbable is left, and nothing is left out of reach either
        self.assertEqual(result["stuck"]["absorbableButUnreachable"], 0)

    def test_a_prop_behind_a_tall_wall_leaves_the_ball_stuck(self):
        b = Builder()
        b.floor(-4.0, -4.0, 4.0, 4.0, 0.0)
        b.wall_x(1.0, -4.0, 4.0, 0.0, 2.0)
        b.prop("here", (-1.0, 0.04, 0.0), (0.08, 0.08, 0.08), req_mm=100)
        b.prop("there", (2.0, 0.04, 0.0), (0.08, 0.08, 0.08), req_mm=100)
        result = sr.simulate_growth(b.level(), (0.0, 0.0), target_m=5.0)
        self.assertEqual([event["id"] for event in result["events"]], ["here"])
        self.assertIsNotNone(result["stuck"])
        self.assertEqual(result["stuck"]["absorbableButUnreachable"], 1)

    def test_route_waypoints_merge_props_taken_at_one_stop(self):
        events = [{"id": "a", "ballXZ": (1.0, 2.0), "node": (10, 20, 0)},
                  {"id": "b", "ballXZ": (1.0, 2.0), "node": (10, 20, 0)},
                  {"id": "c", "ballXZ": (3.0, 2.0), "node": (30, 20, 0)}]
        stops = sr.route_waypoints(None, None, None, events)
        self.assertEqual(stops, [{"x": 1.0, "z": 2.0, "items": ["a", "b"]},
                                 {"x": 3.0, "z": 2.0, "items": ["c"]}])


class ComponentTest(unittest.TestCase):
    def test_a_ledge_splits_the_graph_into_two_components(self):
        b = Builder()
        b.floor(-4.0, -4.0, 1.0, 4.0, 1.0)
        b.wall_x(1.0, -4.0, 4.0, 0.0, 1.0)
        b.floor(1.0, -4.0, 4.0, 4.0, 0.0)
        reach = sr.Reach(b.level(), 0.222)
        comp = reach.components()
        self.assertNotEqual(comp[reach.snap(0.0, 0.0)], comp[reach.snap(2.0, 0.0)])
        self.assertEqual(comp[reach.snap(2.0, 0.0)], comp[reach.snap(3.0, 1.0)])


if __name__ == "__main__":
    unittest.main()
