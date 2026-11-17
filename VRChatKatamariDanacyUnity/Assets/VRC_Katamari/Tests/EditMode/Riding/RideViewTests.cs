using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins what the rider sees by default and where the click target and the landing spot
    /// are. Expected numbers are hand calculations of the stated geometry (a ball centre 18
    /// degrees below the horizon, a half angle of 10 degrees, no additive base), plus the real
    /// <see cref="FollowRigMath"/> fed with the coefficients the rule derives, so the test
    /// closes the loop through the code that actually moves the eye.
    /// </summary>
    public class RideViewTests
    {
        private const float Tolerance = 0.02f;

        // The scene's follow-frame bounds the assembly writes next to the derived numbers.
        private const float EyeUpMin = 0.1f;
        private const float EyeUpMax = 128f;
        private const float EyeBackMax = 128f;

        // Steady-state rig for a ball at the origin heading +Z: no filtering history, so the
        // frame is exactly the target. Returns up above and back behind the centre.
        private static void RigFor(float radius, out float up, out float back)
        {
            Vector3 rig;
            float yaw;
            float height;
            bool ok = FollowRigMath.TryUpdate(5f, 8f, Vector3.zero, radius, 0f, 0.02f,
                RideViewMath.EyeBaseUpMeters, RideViewMath.EyeUpPerRadius(), EyeUpMin, EyeUpMax,
                RideViewMath.EyeBaseBackMeters, RideViewMath.EyeBackPerRadius(), EyeBackMax,
                true, false, Vector3.zero, 0f, 0f, out rig, out yaw, out height);
            Assert.IsTrue(ok);
            up = rig.y;
            back = -rig.z;
        }

        [Test]
        public void DerivedCoefficients_MatchTheirClosedForm()
        {
            // sin 18 / sin 10 = 0.309017 / 0.173648; back factor divides that by tan 18 = 0.324920.
            Assert.AreEqual(1.779558f, RideViewMath.EyeUpPerRadius(), 1e-4f);
            Assert.AreEqual(5.476916f, RideViewMath.EyeBackPerRadius(), 1e-3f);
            // No additive base: the picture is the same at every size.
            Assert.AreEqual(0f, RideViewMath.EyeBaseUpMeters);
            Assert.AreEqual(0f, RideViewMath.EyeBaseBackMeters);
        }

        // Diameters 0.222 / 0.5 / 0.8 / 2 / 20 m. up = 1.779558 r and back = up / tan 18, so the
        // centre is 18 degrees below the horizon and the half angle asin(sin 18 / 1.779558) =
        // 10 degrees at every one of them: the lower edge is at 28 degrees, inside the 30 degree
        // half field with the 2 degree margin.
        [TestCase(0.111f)]
        [TestCase(0.25f)]
        [TestCase(0.4f)]
        [TestCase(1f)]
        [TestCase(10f)]
        public void BallView_IsTheSameAtEveryPlayedSize(float radius)
        {
            float up;
            float back;
            RigFor(radius, out up, out back);
            float depression;
            float halfAngle;
            Assert.IsTrue(RideViewMath.TryBallView(radius, up, back, out depression,
                out halfAngle));
            Assert.AreEqual(18f, depression, Tolerance);
            Assert.AreEqual(10f, halfAngle, Tolerance);
            Assert.AreEqual(28f, depression + halfAngle, Tolerance);
            Assert.IsTrue(RideViewMath.IsBallInView(depression, halfAngle,
                RideViewMath.HalfFovDegrees, 1.9f));
            // Hand values at r = 1: up 1.7796, back 5.4769.
            if (radius == 1f)
            {
                Assert.AreEqual(1.7796f, up, 1e-3f);
                Assert.AreEqual(5.4769f, back, 1e-3f);
            }
        }

        [Test]
        public void ShippedLinearFrame_PutsTheSameBallsOutsideTheView()
        {
            // The linear frame's coefficients, as literals: 0.55 + 2.4 r up, 0.75 + 2.2 r back.
            // At r = 0.111: up 0.8164, back 0.9942, atan2 = 39.4 deg; the lower edge
            // (39.4 + 4.95) is past the 30 degree half field.
            float depression;
            float halfAngle;
            Assert.IsTrue(RideViewMath.TryBallView(0.111f, 0.55f + 2.4f * 0.111f,
                0.75f + 2.2f * 0.111f, out depression, out halfAngle));
            Assert.AreEqual(39.4f, depression, 0.1f);
            Assert.IsFalse(RideViewMath.IsBallInView(depression, halfAngle,
                RideViewMath.HalfFovDegrees, RideViewMath.EdgeMarginDegrees));
        }

        [Test]
        public void EyeOffset_MatchesTheFollowFrame()
        {
            float expectedUp;
            float expectedBack;
            Assert.IsTrue(RideViewMath.TryEyeOffset(0.25f, out expectedUp, out expectedBack));
            float up;
            float back;
            RigFor(0.25f, out up, out back);
            Assert.AreEqual(expectedUp, up, 1e-4f);
            Assert.AreEqual(expectedBack, back, 1e-4f);
            // Hand value: 1.779558 * 0.25 = 0.44489.
            Assert.AreEqual(0.4449f, up, 1e-3f);
        }

        [Test]
        public void EyeOffset_RefusesNegativeAndNonFiniteRadius()
        {
            float up;
            float back;
            Assert.IsFalse(RideViewMath.TryEyeOffset(-0.1f, out up, out back));
            Assert.AreEqual(0f, up);
            Assert.IsFalse(RideViewMath.TryEyeOffset(float.NaN, out up, out back));
            Assert.IsFalse(RideViewMath.TryEyeOffset(float.PositiveInfinity, out up, out back));
        }

        [Test]
        public void BallView_RefusesAnEyeInsideTheBall()
        {
            float depression;
            float halfAngle;
            Assert.IsFalse(RideViewMath.TryBallView(1f, 0.3f, 0.3f, out depression,
                out halfAngle));
            Assert.IsFalse(RideViewMath.TryBallView(0f, 1f, 1f, out depression, out halfAngle));
        }

        [Test]
        public void HudPlacement_RoundTripsDistanceAzimuthAndElevation()
        {
            // 1.6 m straight ahead and level: (0, 0, 1.6).
            Vector3 ahead = RideViewMath.HudLocalPosition(1.6f, 0f, 0f);
            Assert.AreEqual(0f, ahead.x, 1e-5f);
            Assert.AreEqual(0f, ahead.y, 1e-5f);
            Assert.AreEqual(1.6f, ahead.z, 1e-5f);

            // 90 degrees right is +X; 30 degrees up at 2 m is y = 1.0, horizontal 1.732.
            Vector3 right = RideViewMath.HudLocalPosition(2f, 90f, 0f);
            Assert.AreEqual(2f, right.x, 1e-5f);
            Assert.AreEqual(0f, right.z, 1e-5f);
            Vector3 up = RideViewMath.HudLocalPosition(2f, 0f, 30f);
            Assert.AreEqual(1f, up.y, 1e-5f);
            Assert.AreEqual(1.7320508f, up.z, 1e-5f);

            // The panel's forward points away from the eye, along the placement direction.
            Vector3 place = RideViewMath.HudLocalPosition(1.6f, -20f, 15f);
            Quaternion facing = RideViewMath.HudLocalRotation(-20f, 15f);
            Vector3 normal = facing * Vector3.forward;
            Vector3 away = place.normalized;
            Assert.AreEqual(away.x, normal.x, 1e-4f);
            Assert.AreEqual(away.y, normal.y, 1e-4f);
            Assert.AreEqual(away.z, normal.z, 1e-4f);
        }

        // Hand values: 1.25 * 0.111 = 0.139 -> floor 0.25; 1.25 * 0.4 = 0.5; 1.25 * 15 = 18.75.
        [TestCase(0.111f, 0.25f)]
        [TestCase(0.2f, 0.25f)]
        [TestCase(0.4f, 0.5f)]
        [TestCase(15f, 18.75f)]
        public void SeatTrigger_CoversTheBallAndScalesWithIt(float collisionRadius,
            float expected)
        {
            float radius = RideViewMath.SeatTriggerRadius(collisionRadius);
            Assert.AreEqual(expected, radius, 1e-4f);
            Assert.GreaterOrEqual(radius, collisionRadius);
        }

        [Test]
        public void SeatTrigger_FallsBackToTheFloorOnBadInput()
        {
            Assert.AreEqual(0.25f, RideViewMath.SeatTriggerRadius(float.NaN), 1e-6f);
            Assert.AreEqual(0.25f, RideViewMath.SeatTriggerRadius(-1f), 1e-6f);
        }
    }
}
