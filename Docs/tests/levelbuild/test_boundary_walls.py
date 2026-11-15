"""Boundary walls: all four sides are the same 1.2 m-high slab in world space.

The measured bug: ``_structure_from_shell`` gave every boundary
``size=[span, 0.04, 1.2]``.  ``align_up_to`` pins a wall's local +Y to its inward
normal, but for the +-X walls that rotation also turns local +X vertical, so the
``span`` value became the wall's *height*: the overview render showed two 13.4 m
grey pillars where the west and east walls should be, while the +-Z walls (whose
local +Z turns vertical) looked right.

The Unity importer does not read a boundary's size - it reads the authored local +Y
(``authored * Vector3.up``) and re-derives a ``LookRotation`` from it - so this file
pins both halves of the contract: every boundary's *world* AABB is span x 1.2 x 0.04,
and the inward-normal convention is untouched.
"""

import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild.levelbuild import (  # noqa: E402
    BOUNDARY_HEIGHT_M,
    BOUNDARY_THICKNESS_M,
    _boundary_size,
    _structure_from_shell,
)

LAYOUT = ROOT / "Docs/Design/StoryMode/picnic_corner_layout.json"
PLACEMENT = ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"

#: The structures' centre and size are stored at 4 decimals.
TOLERANCE_M = 1e-4

#: The four side walls, the inward normal each one has, and the side it runs along.
INWARD = {
    "boundary_x0": (1.0, 0.0, 0.0),
    "boundary_x1": (-1.0, 0.0, 0.0),
    "boundary_z0": (0.0, 0.0, 1.0),
    "boundary_z1": (0.0, 0.0, -1.0),
}


def layout_data():
    return json.loads(LAYOUT.read_text(encoding="utf-8"))


def generated_walls(shell=None, bounds=None):
    layout = layout_data()
    if shell is None:
        shell = layout["shell"]
    if bounds is None:
        bounds = layout["bounds"]
    structures = _structure_from_shell(shell, bounds)
    return {structure["id"]: structure for structure in structures
            if structure["role"] == "boundary"}


def world_size(structure):
    """The world-axis-aligned ``(width, height, depth)`` of a boundary's box."""
    (min_x, min_y, min_z), (max_x, max_y, max_z) = core.structure_world_aabb(structure)
    return (max_x - min_x, max_y - min_y, max_z - min_z)


class GeneratedWallTests(unittest.TestCase):
    def test_every_wall_is_span_long_and_1_2_m_high(self):
        bounds = layout_data()["bounds"]
        span = max(float(bounds["x1"]) - float(bounds["x0"]),
                   float(bounds["z1"]) - float(bounds["z0"]))
        walls = generated_walls()
        self.assertEqual(sorted(walls), sorted(INWARD))
        for wall_id, structure in walls.items():
            width, height, depth = world_size(structure)
            self.assertAlmostEqual(height, BOUNDARY_HEIGHT_M, delta=TOLERANCE_M,
                                   msg="%s: world height is %.4f m" % (wall_id, height))
            self.assertAlmostEqual(max(width, depth), span, delta=TOLERANCE_M,
                                   msg="%s: long horizontal edge is %.4f m"
                                       % (wall_id, max(width, depth)))
            self.assertAlmostEqual(min(width, depth), BOUNDARY_THICKNESS_M,
                                   delta=TOLERANCE_M,
                                   msg="%s: wall thickness is %.4f m"
                                       % (wall_id, min(width, depth)))

    def test_a_rectangular_arena_uses_each_side_length(self):
        """A wall runs along its own side, not along the longer of the two arena sides."""
        bounds = {"x0": -10.0, "x1": 10.0, "z0": -5.0, "z1": 5.0, "ground_y": 0.0}
        shell = [{"id": "ground"}] + [{"id": wall_id} for wall_id in INWARD]
        walls = generated_walls(shell=shell, bounds=bounds)
        for wall_id, expected_length in (("boundary_x0", 10.0), ("boundary_x1", 10.0),
                                         ("boundary_z0", 20.0), ("boundary_z1", 20.0)):
            width, height, depth = world_size(walls[wall_id])
            self.assertAlmostEqual(height, BOUNDARY_HEIGHT_M, delta=TOLERANCE_M)
            self.assertAlmostEqual(max(width, depth), expected_length, delta=TOLERANCE_M,
                                   msg="%s: expected a %.1f m edge" % (wall_id, expected_length))

    def test_the_inward_normal_is_still_local_plus_y(self):
        """``align_up_to`` may only be re-used, never re-interpreted: the importer reads +Y."""
        for wall_id, structure in generated_walls().items():
            up = core.structure_up(structure)
            expected = INWARD[wall_id]
            for actual, wanted in zip(up, expected):
                self.assertAlmostEqual(actual, wanted, delta=1e-6,
                                       msg="%s: local +Y is %r, not the inward normal %r"
                                           % (wall_id, up, expected))
            self.assertEqual(tuple(structure["rotationQuat"]),
                             tuple(core.rounded_seq(core.align_up_to(expected),
                                                    core.QUAT_DECIMALS)))

    def test_the_local_size_is_chosen_per_normal(self):
        length = 13.4
        for wall_id, inward in INWARD.items():
            size = _boundary_size(inward, length)
            self.assertAlmostEqual(size[1], BOUNDARY_THICKNESS_M, delta=1e-9)
            # Local +Y is the normal; whichever of local X/Z turns vertical carries
            # the 1.2 m height, the other one carries the wall's length.
            quat = core.align_up_to(inward)
            local_x_y = abs(core.quat_rotate(quat, (1.0, 0.0, 0.0))[1])
            local_z_y = abs(core.quat_rotate(quat, (0.0, 0.0, 1.0))[1])
            vertical_axis = 0 if local_x_y > local_z_y else 2
            self.assertAlmostEqual(size[vertical_axis], BOUNDARY_HEIGHT_M, delta=1e-9,
                                   msg="%s: local axis %d should carry the height"
                                       % (wall_id, vertical_axis))
            self.assertAlmostEqual(size[2 if vertical_axis == 0 else 0], length, delta=1e-9)

    def test_a_diagonal_normal_is_refused_rather_than_drawn_wrong(self):
        """A 45 degree wall cannot be 1.2 m high without also changing its orientation."""
        with self.assertRaises(ValueError):
            _boundary_size((0.5, 0.0, 0.5), 13.4)

    def test_wall_centres_stand_on_the_authored_planes(self):
        bounds = layout_data()["bounds"]
        floor = float(bounds.get("ground_y", 0.0))
        walls = generated_walls()
        self.assertAlmostEqual(walls["boundary_x0"]["center"][0], float(bounds["x0"]))
        self.assertAlmostEqual(walls["boundary_x1"]["center"][0], float(bounds["x1"]))
        self.assertAlmostEqual(walls["boundary_z0"]["center"][2], float(bounds["z0"]))
        self.assertAlmostEqual(walls["boundary_z1"]["center"][2], float(bounds["z1"]))
        for structure in walls.values():
            self.assertAlmostEqual(structure["center"][1], floor + BOUNDARY_HEIGHT_M / 2.0,
                                   delta=TOLERANCE_M)


class ShippedLevelTests(unittest.TestCase):
    def test_the_shipped_first_level_has_four_short_walls(self):
        structures = {structure["id"]: structure
                      for structure in core.load_doc(PLACEMENT)["structures"]
                      if structure.get("role") == "boundary"}
        self.assertEqual(sorted(structures), sorted(INWARD))
        for wall_id, structure in structures.items():
            width, height, depth = world_size(structure)
            self.assertAlmostEqual(height, BOUNDARY_HEIGHT_M, delta=TOLERANCE_M,
                                   msg="%s: world height is %.4f m" % (wall_id, height))
            self.assertAlmostEqual(max(width, depth), 13.4, delta=TOLERANCE_M)
            self.assertAlmostEqual(min(width, depth), BOUNDARY_THICKNESS_M,
                                   delta=TOLERANCE_M)

    def test_the_shipped_walls_match_a_fresh_rebuild(self):
        shipped = {structure["id"]: structure
                   for structure in core.load_doc(PLACEMENT)["structures"]
                   if structure.get("role") == "boundary"}
        for wall_id, structure in generated_walls().items():
            self.assertEqual(shipped[wall_id], structure,
                             "%s is stale; re-run from-layout" % wall_id)


if __name__ == "__main__":
    unittest.main()
