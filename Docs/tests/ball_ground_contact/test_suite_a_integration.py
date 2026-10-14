"""Small deterministic bridge check for suite A's three independent layers."""
import unittest

from Docs.Design.BallLayers.GroundContact.ground_contact import solve_ground_contact
from Docs.Design.BallLayers.RadialProxy.radial_proxy import (
    AttachmentProxy,
    RadialProxy,
    direction_length_to_point,
)
from Docs.Design.BallLayers.Sweep.sweep import Plane, sweep_sphere

IDENTITY_QUATERNION = (0.0, 0.0, 0.0, 1.0)
IDENTITY_MATRIX = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
ORIGIN = (0.0, 0.0, 0.0)
GROUND = Plane((0.0, 1.0, 0.0), 0.0)


def snapshot_points(snapshot):
    return tuple(direction_length_to_point(direction, length)
                 for direction, length in zip(snapshot.directions, snapshot.support_lengths))


class SuiteAIntegrationTests(unittest.TestCase):
    def test_attach_support_sweep_detach_and_rebuild(self):
        proxy = RadialProxy(core_radius=0.5)
        proxy.attach(AttachmentProxy("confirmed-rod#gen3", ((0.0, -2.0, 0.0),)),
                     ORIGIN, IDENTITY_QUATERNION)

        attached_support = solve_ground_contact(
            0.5, IDENTITY_MATRIX, snapshot_points(proxy.snapshot()),
            (0.0, 1.0, 0.0), 0.0, current_center=ORIGIN,
            max_lift_per_step=10.0, max_drop_per_step=10.0,
            requested_normal_speed_perturbation=0.0,
            max_normal_speed_perturbation=1.0,
        )
        self.assertGreater(attached_support.support_height, 0.5)
        self.assertGreater(attached_support.center_position[1], 0.5)

        # Sweep remains the separate sphere-only collision layer; radial display
        # support does not silently enlarge the environment/PvP sphere radius.
        sweep = sweep_sphere(
            (0.0, attached_support.center_position[1] + 2.0, 0.0),
            (0.0, -4.0, 0.0), 0.5, (GROUND,),
        )
        self.assertTrue(sweep.contacts)
        self.assertGreaterEqual(sweep.position[1], 0.5)
        self.assertFalse(sweep.stuck)

        proxy.detach("confirmed-rod#gen3")
        proxy.rebuild(0.5, ())
        detached_support = solve_ground_contact(
            0.5, IDENTITY_MATRIX, snapshot_points(proxy.snapshot()),
            (0.0, 1.0, 0.0), 0.0,
            current_center=attached_support.center_position,
            max_lift_per_step=10.0, max_drop_per_step=10.0,
            requested_normal_speed_perturbation=0.0,
            max_normal_speed_perturbation=1.0,
        )
        self.assertEqual(detached_support.support_height, 0.5)
        self.assertAlmostEqual(detached_support.center_position[1], 0.5)

        settled = sweep_sphere(detached_support.center_position,
                               (0.0, -1.0, 0.0), 0.5, (GROUND,))
        self.assertTrue(settled.contacts)
        self.assertGreaterEqual(settled.position[1], 0.5)
        self.assertFalse(settled.stuck)
        self.assertEqual(proxy.candidate_ids, ())


if __name__ == "__main__":
    unittest.main()
