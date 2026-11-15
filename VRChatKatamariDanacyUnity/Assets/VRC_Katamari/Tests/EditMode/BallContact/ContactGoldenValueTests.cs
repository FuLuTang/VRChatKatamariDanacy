using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Golden values printed by the Python reference model, hardcoded so the C# port cannot
    /// drift from it silently.
    ///
    /// HOW THESE NUMBERS WERE GENERATED (repo root, no committed generator):
    /// <code>
    /// python3 - &lt;&lt;'EOF'
    /// import math
    /// from Docs.Design.BallMotor.contact_v2.contact_v2 import *
    /// from Docs.Design.BallMotor.contact_v2.shapes import *
    /// ... build the same slab / slope / wall / seam worlds the tests build, call
    /// required_speed, climb_shape, climb_speed, climb_fade, tessellate_vertical_curve,
    /// select_window and step(...) and print the values with repr-like precision ...
    /// EOF
    /// </code>
    /// The same script prints the reference scenarios of CONTACT_MODEL_V2.md section 9.3
    /// (45 deg seam reaching 0.750 m, the 90 deg wall capped at 0.581 m of rise, the 70 deg
    /// slope reaching the 0.39 m deck), so these goldens also check the document's numbers.
    ///
    /// The Python model is double and this port is float, so a value is compared with a
    /// tolerance rather than for bit equality: <see cref="PureTolerance"/> for closed-form
    /// values, <see cref="IntegratorTolerance"/> for the numbers that come out of 200-500
    /// accumulated steps (float rounding accumulates, double does not), and
    /// <see cref="TrajectoryTolerance"/> for a peak that is reached by overshooting the
    /// climb cap, where float rounding shifts the overshoot itself.
    /// </summary>
    public class ContactGoldenValueTests
    {
        private const float PureTolerance = 1e-4f;
        private const float IntegratorTolerance = 1e-3f;
        private const float TrajectoryTolerance = 5e-3f;

        [Test]
        public void TheGateAndTheClimbShapeMatchTheModel()
        {
            Assert.AreEqual(1.0606601718f, ClimbMath.RequiredSpeed(45f), PureTolerance);
            Assert.AreEqual(1.0698756737f, ClimbMath.RequiredSpeed(45.5f), PureTolerance);
            Assert.AreEqual(1.0790097005f, ClimbMath.RequiredSpeed(46f), PureTolerance);
            Assert.AreEqual(1.1490666647f, ClimbMath.RequiredSpeed(50f), PureTolerance);
            Assert.AreEqual(1.4095389312f, ClimbMath.RequiredSpeed(70f), PureTolerance);
            Assert.AreEqual(1.5f, ClimbMath.RequiredSpeed(90f), PureTolerance);

            Assert.AreEqual(0.7576942762f, ClimbMath.ClimbShape(50f), PureTolerance);
            Assert.AreEqual(0.4829740364f, ClimbMath.ClimbShape(70f), PureTolerance);
            Assert.AreEqual(0.15f, ClimbMath.ClimbShape(90f), PureTolerance);

            Assert.AreEqual(0.3162277660f, ClimbMath.ClimbFade(0.54f, 0.6f), PureTolerance);
            Assert.AreEqual(0f, ClimbMath.ClimbFade(0.6f, 0.6f), PureTolerance);
        }

        [Test]
        public void TheClimbSpeedTableMatchesTheDesignsSection36()
        {
            // Full push (v_gate = 2 m/s), h = 0, so rate = 1.0. D = 0.30 (H_cap 0.30):
            // the design's own table prints 0.579 / 0.532 / 0.481 / 0.369 / 0.245 / 0.115.
            Assert.AreEqual(0.5785416740f, ClimbMath.ClimbSpeed(1f, 2f, 50f, 0f, 0.30f),
                PureTolerance);
            Assert.AreEqual(0.5317776887f, ClimbMath.ClimbSpeed(1f, 2f, 55f, 0f, 0.30f),
                PureTolerance);
            Assert.AreEqual(0.4809665540f, ClimbMath.ClimbSpeed(1f, 2f, 60f, 0f, 0.30f),
                PureTolerance);
            Assert.AreEqual(0.3687775087f, ClimbMath.ClimbSpeed(1f, 2f, 70f, 0f, 0.30f),
                PureTolerance);
            Assert.AreEqual(0.2453833454f, ClimbMath.ClimbSpeed(1f, 2f, 80f, 0f, 0.30f),
                PureTolerance);
            Assert.AreEqual(0.1145333333f, ClimbMath.ClimbSpeed(1f, 2f, 90f, 0f, 0.30f),
                PureTolerance);
            // D = 0.60 (H_cap 0.60): 1.157 / 1.064 / 0.962 / 0.738 / 0.491 / 0.229.
            Assert.AreEqual(1.1570833479f, ClimbMath.ClimbSpeed(1f, 2f, 50f, 0f, 0.60f),
                PureTolerance);
            Assert.AreEqual(1.0635553773f, ClimbMath.ClimbSpeed(1f, 2f, 55f, 0f, 0.60f),
                PureTolerance);
            Assert.AreEqual(0.9619331080f, ClimbMath.ClimbSpeed(1f, 2f, 60f, 0f, 0.60f),
                PureTolerance);
            Assert.AreEqual(0.7375550174f, ClimbMath.ClimbSpeed(1f, 2f, 70f, 0f, 0.60f),
                PureTolerance);
            Assert.AreEqual(0.4907666908f, ClimbMath.ClimbSpeed(1f, 2f, 80f, 0f, 0.60f),
                PureTolerance);
            Assert.AreEqual(0.2290666667f, ClimbMath.ClimbSpeed(1f, 2f, 90f, 0f, 0.60f),
                PureTolerance);
            // The dash gate (3.2 m/s) hits the 1.5 rate ceiling.
            Assert.AreEqual(0.3436f, ClimbMath.ClimbSpeed(1f, 3.2f, 90f, 0f, 0.60f),
                PureTolerance);
            // The grip discount: a 0.075 m protrusion on a 0.30 m core halves v_req.
            Assert.AreEqual(0.7954951288f,
                BallShapeMath.RequiredSpeedWithGrip(45f, 0.075f, 0.30f), PureTolerance);
        }

        [Test]
        public void TheCurvedRampMetricsMatchTheModel()
        {
            const int Count = 12;
            Vector3[] centers = new Vector3[Count];
            Vector3[] axisX = new Vector3[Count];
            Vector3[] axisY = new Vector3[Count];
            Vector3[] axisZ = new Vector3[Count];
            Vector3[] halfExtents = new Vector3[Count];
            int[] surfaceIds = new int[Count];
            int[] roles = new int[Count];
            float deltaDeg;
            float sagittaM;
            float normalJumpDeg;
            float segmentLengthM;
            float overlap;
            Assert.IsTrue(CurveRampMath.TryTessellateVerticalCurve(new Vector3(0f, 0f, 7f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), 3f, 90f, Count, 4f, 0.1f,
                centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles, out deltaDeg,
                out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));

            Assert.AreEqual(0.0064232300f, sagittaM, PureTolerance);
            Assert.AreEqual(0.3924187750f, segmentLengthM, PureTolerance);
            Assert.AreEqual(1.0327366100f, overlap, PureTolerance);
            // First and last segment placement: the chain starts at the origin and ends at
            // the top of the arc (R*sin 90 = 3 in x, R*(1 - cos 90) = 3 in y).
            Assert.AreEqual(0.1962093880f, centers[0].x, PureTolerance);
            Assert.AreEqual(0.0064232300f, centers[0].y, PureTolerance);
            Assert.AreEqual(7f, centers[0].z, PureTolerance);
            Assert.AreEqual(-0.0654031290f, axisY[0].x, PureTolerance);
            Assert.AreEqual(0.9978589230f, axisY[0].y, PureTolerance);
            Assert.AreEqual(0.9978589230f, axisZ[0].x, PureTolerance);
            Assert.AreEqual(0.0654031290f, axisZ[0].y, PureTolerance);
            Assert.AreEqual(2f, halfExtents[0].x, PureTolerance);
            Assert.AreEqual(0.05f, halfExtents[0].y, PureTolerance);
            Assert.AreEqual(0.2026326180f, halfExtents[0].z, PureTolerance);
            Assert.AreEqual(2.9935767700f, centers[Count - 1].x, PureTolerance);
            Assert.AreEqual(2.8037906120f, centers[Count - 1].y, PureTolerance);
            Assert.AreEqual(-0.9978589230f, axisY[Count - 1].x, PureTolerance);
            Assert.AreEqual(0.0654031290f, axisY[Count - 1].y, PureTolerance);

            // An 8-segment R = 5, 45 deg arc.
            Assert.IsTrue(CurveRampMath.TryTessellateVerticalCurve(new Vector3(0f, 0f, 7f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), 5f, 45f, 8, 3f, 0.2f,
                centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles, out deltaDeg,
                out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));
            Assert.AreEqual(0.4906767430f, segmentLengthM, PureTolerance);
            Assert.AreEqual(0.0060227190f, sagittaM, PureTolerance);
            Assert.AreEqual(1.0245486220f, overlap, PureTolerance);
        }

        [Test]
        public void The45DegreeSeamLandsWhereTheModelSays()
        {
            ContactWorld world = ContactScenes.SeamWorld();
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.1f + 0.15f * ContactScenes.Sin45,
                7.7f - 0.15f * ContactScenes.Sin45);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(0f, 0f, 4f);
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 4f, 0.02f));
            }
            // Python after 5 steps: y = 0.4060660172, z = 7.7939339828.
            Assert.AreEqual(0.4060660172f, ball.Center.y, PureTolerance);
            Assert.AreEqual(7.7939339828f, ball.Center.z, PureTolerance);
            for (int i = 0; i < 15; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(0f, 0f, 1f), 4f, 0.02f));
            }
            // Python after 20 steps: y = 0.7500917653, z = 8.6754220031, v_planar = 4.0,
            // 2 substeps, residual 0, one contact on the deck (surface 1, RIDE, n = +Y).
            // The design's section 2.6 prints the same landing: y = 0.750, z = 8.675.
            Assert.AreEqual(0.7500917653f, ball.Center.y, PureTolerance);
            Assert.AreEqual(8.6754220031f, ball.Center.z, PureTolerance);
            Assert.AreEqual(4f, ball.PlanarVelocity.z, PureTolerance);
            Assert.IsTrue(Mathf.Abs(ball.PlanarVelocity.x) < PureTolerance);
            Assert.AreEqual(2, ball.Substeps);
            Assert.AreEqual(0f, ball.Residual, PureTolerance);
            Assert.AreEqual(1, ball.ContactCount);
            Assert.AreEqual(1, ball.DominantSurfaceId);
            Assert.AreEqual(ContactCodes.ClassRide, ball.DominantClass);
            Assert.IsFalse(ball.Airborne);
            Assert.AreEqual(1f, ball.ReportNormal.y, PureTolerance);
        }

        [Test]
        public void TheReferenceScenariosMatchTheModel()
        {
            ContactWorld wallWorld = ContactScenes.WallWorld();
            ContactBall wallBall = new ContactBall();
            wallBall.Center = new Vector3(0f, 0.30f, 0.5f);
            wallBall.Radius = 0.30f;
            wallBall.PlanarVelocity = new Vector3(0f, 0f, -4f);
            float wallHighest = 0f;
            for (int i = 0; i < 500; i++)
            {
                Assert.IsTrue(wallBall.Step(wallWorld, new Vector3(0f, 0f, -1f), 4f, 0.02f));
                if (wallBall.Center.y > wallHighest) wallHighest = wallBall.Center.y;
            }
            // Python: highest centre y = 0.881094802 (0.581 m of rise against H_cap 0.60),
            // final centre (0, 0.30, 0.30), climb locked.
            // The peak overshoots the cap by about 2 mm and is therefore trajectory-shaped.
            Assert.AreEqual(0.881094802f, wallHighest, TrajectoryTolerance);
            Assert.AreEqual(0.30f, wallBall.Center.y, IntegratorTolerance);
            Assert.AreEqual(0.30f, wallBall.Center.z, IntegratorTolerance);
            Assert.IsTrue(wallBall.ClimbLocked);

            ContactWorld slope70 = ContactScenes.SlopeWorld(70f, 0.24f, 2f);
            ContactBall slope70Ball = new ContactBall();
            slope70Ball.Center = new Vector3(0f, 0.15f, 6.65f);
            slope70Ball.Radius = 0.15f;
            slope70Ball.PlanarVelocity = new Vector3(0f, 0f, 4f);
            float slope70Highest = 0f;
            for (int i = 0; i < 200; i++)
            {
                Assert.IsTrue(slope70Ball.Step(slope70, new Vector3(0f, 0f, 1f), 4f, 0.02f,
                    5f));
                if (slope70Ball.Center.y > slope70Highest) slope70Highest = slope70Ball.Center.y;
            }
            // Python: y = 0.389952512 (the 0.24 m deck plus r).
            Assert.AreEqual(0.389952512f, slope70Highest, IntegratorTolerance);

            ContactWorld slope50 = ContactScenes.SlopeWorld(50f, 0.6f, 3f);
            ContactBall smallBall = new ContactBall();
            smallBall.Center = new Vector3(0f, 0.15f, 6.4f);
            smallBall.Radius = 0.15f;
            smallBall.PlanarVelocity = new Vector3(0f, 0f, 4f);
            float smallHighest = 0f;
            for (int i = 0; i < 250; i++)
            {
                Assert.IsTrue(smallBall.Step(slope50, new Vector3(0f, 0f, 1f), 4f, 0.02f, 5f));
                if (smallBall.Center.y > smallHighest) smallHighest = smallBall.Center.y;
            }
            // Python: highest y = 0.448744607, climb locked. At step 250 the settled centre
            // is NOT an equilibrium: it is a slide/settle limit cycle (y alternates
            // 0.15298 .. 0.15724 with v_y 0 / -0.392), and a 1e-6 change of the start moves
            // the step-250 y by 6.5e-4 (650x amplification), a 1e-9 change by 9.8e-4. The
            // model's own case therefore asserts a band (rise < 0.05 m), not this value; the
            // float run's 0.15571 lies inside the model's cycle band, which is the real
            // cross-check.
            Assert.AreEqual(0.448744607f, smallHighest, TrajectoryTolerance);
            Assert.Less(smallBall.Center.y - 0.15f, 0.05f);  // slid back down (model's claim)
            Assert.Less(smallBall.Center.z, 7f);             // back outside the foot at z = 7
            Assert.IsTrue(smallBall.ClimbLocked);

            ContactWorld lowWall = ContactScenes.LowWallWorld();
            ContactBall lowWallBall = new ContactBall();
            lowWallBall.Center = new Vector3(0f, 0.15f, 0.8f);
            lowWallBall.Radius = 0.15f;
            lowWallBall.PlanarVelocity = new Vector3(0f, 0f, -4f);
            float lowWallHighest = 0f;
            for (int i = 0; i < 150; i++)
            {
                Assert.IsTrue(lowWallBall.Step(lowWall, new Vector3(0f, 0f, -1f), 4f, 0.02f,
                    5f));
                if (lowWallBall.Center.y > lowWallHighest) lowWallHighest = lowWallBall.Center.y;
            }
            // Python: highest y = 0.30 (r above the 0.15 m wall top), final z = -8.287761259.
            Assert.AreEqual(0.30f, lowWallHighest, IntegratorTolerance);
            Assert.AreEqual(-8.287761259f, lowWallBall.Center.z, IntegratorTolerance);
        }
    }
}
