using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the push stance: where the eye sits relative to the ball so that a standing rider's
    /// hands meet the ball's back surface, where the panels hang, and the click target. Expected
    /// numbers are hand calculations of the stated geometry (push angle 45 degrees, shoulder
    /// 0.125 E below and 0.055 E behind the eye, push reach 0.8 * 0.41 E, feet on the ground),
    /// plus the real <see cref="FollowRigMath"/> fed with the derived coefficients, so the test
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
            // s = sin 45 = 0.707107, reach = 0.8 * 0.41 = 0.328.
            // e = (1 + s) / (1 - 0.125 - 0.328 s) = 1.707107 / 0.643069 = 2.654625.
            Assert.AreEqual(0.328f, RideViewMath.PushReachPerEyeHeight(), 1e-6f);
            Assert.AreEqual(2.654625f, RideViewMath.EyeHeightPerRadius(), 1e-4f);
            Assert.AreEqual(1.654625f, RideViewMath.EyeUpPerRadius(), 1e-4f);
            // back = -0.055 e + (1 + 0.328 e) cos 45 = -0.146004 + 1.870717 * 0.707107 = 1.176792.
            Assert.AreEqual(1.176792f, RideViewMath.EyeBackPerRadius(), 1e-4f);
            // No additive base: the picture is the same at every size.
            Assert.AreEqual(0f, RideViewMath.EyeBaseUpMeters);
            Assert.AreEqual(0f, RideViewMath.EyeBaseBackMeters);
        }

        // The stance's own closure: the eye is one eye height above the ground (feet on the
        // ground beside the ball), and the shoulder-to-centre distance is the push reach plus
        // the radius along a 45 degree line (the push goes through the centre).
        [TestCase(0.15f)]
        [TestCase(1f)]
        [TestCase(10f)]
        public void Stance_FeetOnTheGroundAndArmAlongTheRadius(float radius)
        {
            float up;
            float back;
            Assert.IsTrue(RideViewMath.TryEyeOffset(radius, out up, out back));
            float eye = RideViewMath.EyeHeightPerRadius() * radius;
            Assert.AreEqual(eye, up + radius, eye * 1e-4f);

            float shoulderForward = -RideViewMath.ShoulderBehindEyePerEyeHeight * eye;
            float shoulderDown = RideViewMath.ShoulderBelowEyePerEyeHeight * eye;
            float dx = back - shoulderForward;
            float dy = up - shoulderDown;
            float reach = RideViewMath.PushReachPerEyeHeight() * eye;
            Assert.AreEqual(reach + radius, Mathf.Sqrt(dx * dx + dy * dy), radius * 1e-3f);
            Assert.AreEqual(45f, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, 1e-2f);
        }

        // Ball centre 54.58 deg below the horizon, half angle asin(1 / 2.0305) = 29.51 deg: the
        // ball spans 25.07 to 84.08 deg below the horizon at every size.
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
            Assert.AreEqual(54.58f, depression, Tolerance);
            Assert.AreEqual(29.51f, halfAngle, Tolerance);
            Assert.AreEqual(25.07f, depression - halfAngle, Tolerance);
            if (radius == 1f)
            {
                Assert.AreEqual(1.6546f, up, 1e-3f);
                Assert.AreEqual(1.1768f, back, 1e-3f);
            }
        }

        [Test]
        public void HandContact_IsInFrontOfTheChestWithinReach()
        {
            // At r = 1: forward = 1.176792 - cos 45 = 0.469685, down = 1.654625 - sin 45 =
            // 0.947518, i.e. 0.177 E ahead of and 0.357 E below the eye (E = 2.654625).
            float forward;
            float down;
            Assert.IsTrue(RideViewMath.TryHandContactFromEye(1f, out forward, out down));
            Assert.AreEqual(0.469685f, forward, 1e-4f);
            Assert.AreEqual(0.947518f, down, 1e-4f);
            float eye = RideViewMath.EyeHeightPerRadius();
            Assert.AreEqual(0.1769f, forward / eye, 1e-3f);
            Assert.AreEqual(0.3569f, down / eye, 1e-3f);
            Assert.IsFalse(RideViewMath.TryHandContactFromEye(-1f, out forward, out down));
        }

        [Test]
        public void HandGap_IsTheDistanceToTheSurface()
        {
            Vector3 centre = new Vector3(1f, 2f, 3f);
            Assert.AreEqual(0.5f, RideViewMath.HandGapMeters(centre + new Vector3(0f, 0f, 1.5f),
                centre, 1f), 1e-5f);
            Assert.AreEqual(-0.25f, RideViewMath.HandGapMeters(centre + new Vector3(0.75f, 0f, 0f),
                centre, 1f), 1e-5f);
            Assert.IsTrue(float.IsPositiveInfinity(RideViewMath.HandGapMeters(
                new Vector3(float.NaN, 0f, 0f), centre, 1f)));
        }

        [Test]
        public void PanelScale_FollowsTheRider()
        {
            Assert.AreEqual(1f, RideViewMath.PanelScaleForEyeHeight(1.64f), 1e-6f);
            // D = 0.3 m: E = 2.654625 * 0.15 = 0.398194, scale 0.398194 / 1.64 = 0.242801.
            Assert.AreEqual(0.242801f, RideViewMath.PanelScaleForEyeHeight(0.398194f), 1e-5f);
            Assert.AreEqual(1f, RideViewMath.PanelScaleForEyeHeight(0f));
            Assert.AreEqual(1f, RideViewMath.PanelScaleForEyeHeight(float.NaN));
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
            // Hand value: 1.654625 * 0.25 = 0.413656.
            Assert.AreEqual(0.4137f, up, 1e-3f);
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
