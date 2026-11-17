using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    public class SlopeStaminaTests
    {
        [Test]
        public void SlopeStaminaBoundariesKeepRideAndWallSeparate()
        {
            Assert.IsFalse(SlopeClimbMath.IsSlope(45f));
            Assert.IsFalse(SlopeClimbMath.IsSlope(45.5f));
            Assert.IsTrue(SlopeClimbMath.IsSlope(50f));
            Assert.IsTrue(SlopeClimbMath.IsSlope(60f));
            Assert.IsFalse(SlopeClimbMath.IsSlope(60.01f));
            Assert.AreEqual(0.45f, ClimbMath.ClimbHeightCap(0.15f, 0f), 1e-6f);
        }

        [Test]
        public void SlopeStaminaDrainGrowsWithSteepnessAndTimeButFallsWithSizeAndDash()
        {
            float baseline = SlopeClimbMath.DrainRate(50f, 0.3f, 0f, 2f);
            Assert.Greater(SlopeClimbMath.DrainRate(58f, 0.3f, 0f, 2f), baseline);
            Assert.Greater(SlopeClimbMath.DrainRate(50f, 0.3f, 3f, 2f), baseline);
            Assert.Less(SlopeClimbMath.DrainRate(50f, 1.2f, 0f, 2f), baseline);
            Assert.AreEqual(baseline * 0.625f, SlopeClimbMath.DrainRate(50f, 0.3f, 0f, 3.2f), 1e-5f);
            Assert.Greater(SlopeClimbMath.UphillVerticalSpeed(1f, 3.2f, 50f, 0.3f, 0f),
                SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.3f, 0f));
            // Golden values shared with Python, independent of a world fixture.
            Assert.AreEqual(17.117117f, baseline, 1e-4f);
            Assert.AreEqual(22.834234f, SlopeClimbMath.DrainRate(58f, 0.3f, 1f, 2f), 1e-4f);
        }

        [Test]
        public void SlopeStaminaVerticalSpeedIsTheSlowClimbRateAtDiameterPointThree()
        {
            float v = SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.3f, 0f);
            Assert.GreaterOrEqual(v, 0.2f);
            Assert.LessOrEqual(v, 0.3f);
            // v_up0 = 0.917 * D, golden shared with Python.
            Assert.AreEqual(0.27488f, v, 1e-5f);
            // Fatigue only slows it: 1 / (1 + 0.15 t), floored at 0.35.
            Assert.AreEqual(v / 1.6f, SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.3f, 4f), 1e-5f);
            Assert.AreEqual(v * 0.35f, SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.3f, 30f), 1e-5f);
        }

        [Test]
        public void SlopeStaminaVerticalSpeedDoesNotGrowWithSteepness()
        {
            float reference = SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 46f, 0.3f, 0f);
            for (float theta = 46f; theta <= 60f; theta += 2f)
                Assert.AreEqual(reference, SlopeClimbMath.UphillVerticalSpeed(1f, 2f, theta, 0.3f, 0f),
                    1e-6f, "theta=" + theta);
            // The wall formula keeps its angle factor: 50 deg is still m(50)/m(90) of the 90 deg rate.
            float wall90 = ClimbMath.ClimbSpeed(1f, 2f, 90f, 0f, 0.45f);
            Assert.AreEqual(wall90, SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.3f, 0f), 1e-6f);
            Assert.AreEqual(wall90 * ClimbMath.ClimbShape(50f) / ClimbMath.ClimbShape(90f),
                ClimbMath.ClimbSpeed(1f, 2f, 50f, 0f, 0.45f), 1e-6f);
            Assert.Greater(ClimbMath.ClimbSpeed(1f, 2f, 50f, 0f, 0.45f), 4f * wall90);
        }

        [Test]
        public void SlopeStaminaVerticalSpeedScalesWithDiameterAndPushAndDashIsCappedAtOnePointFive()
        {
            float d030 = SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.3f, 0f);
            Assert.AreEqual(2f * d030, SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 0.6f, 0f), 1e-5f);
            Assert.AreEqual(4f * d030, SlopeClimbMath.UphillVerticalSpeed(1f, 2f, 50f, 1.2f, 0f), 1e-5f);
            Assert.AreEqual(0.5f * d030, SlopeClimbMath.UphillVerticalSpeed(0.5f, 2f, 50f, 0.3f, 0f), 1e-5f);
            // Dash: gate 3.2 (1.6x target) saturates at 1.5x; nothing goes faster.
            Assert.AreEqual(1.5f * d030, SlopeClimbMath.UphillVerticalSpeed(1f, 3.2f, 50f, 0.3f, 0f), 1e-5f);
            Assert.LessOrEqual(SlopeClimbMath.UphillVerticalSpeed(1f, 50f, 50f, 0.3f, 0f), 1.5f * d030 + 1e-5f);
        }

        [Test]
        public void SlopeStaminaFullPushTopsTheFiftyDegreeBoardInTheSlowClimbWindowWithStaminaToSpare()
        {
            // D=0.30, 1.5 m run-up, full push. Python model: top at 3.12 s, stamina floor 52.
            ContactWorld world = ContactScenes.SlopeWorld(50f, 0.6f, 4f);
            ClimbMotorLoop loop = new ClimbMotorLoop(world, new Vector3(0f, 0.15f, 5.5f), 0.15f, 0f);
            float topSeconds = -1f, minStamina = 100f;
            for (int i = 0; i < 400 && topSeconds < 0f; i++)
            {
                Assert.IsTrue(loop.Step());
                minStamina = Mathf.Min(minStamina, loop.Ball.SlopeStamina);
                if (loop.HighestCenterY - 0.15f >= 0.58f) topSeconds = (i + 1) * ClimbMotorLoop.Dt;
            }
            Assert.Greater(topSeconds, 0f, "must reach the board top");
            Assert.GreaterOrEqual(topSeconds, 2.5f, "a slow climb, not the 0.77 s dash-speed bug");
            Assert.LessOrEqual(topSeconds, 4f);
            Assert.Greater(minStamina, 30f, "stamina must be enough for the 0.6 m board");
        }

        [TestCase(50f)]
        [TestCase(58f)]
        public void SlopeStaminaExhaustionUsesTheExistingRestLatchBeforeSliding(float theta)
        {
            float stamina = 0f, time = 5f, rest = 0f, along = 0f;
            bool sliding = false;
            for (int i = 0; i < 14; i++)
                SlopeClimbMath.Step(theta, 0.3f, 1f, 2f, 0.02f, 0.3f, 0.05f,
                    ref stamina, ref time, ref rest, ref sliding, ref along);
            Assert.IsFalse(sliding);
            Assert.AreEqual(0f, along);
            for (int i = 0; i < 2; i++)
                SlopeClimbMath.Step(theta, 0.3f, 1f, 2f, 0.02f, 0.3f, 0.05f,
                    ref stamina, ref time, ref rest, ref sliding, ref along);
            Assert.IsTrue(sliding);
            Assert.Less(along, 0f);
            Assert.AreEqual(0f, stamina, "held push never refills stamina");
        }

        [Test]
        public void SlopeStaminaFullPushClearsTheFiftyDegreePointSixMetreBoard()
        {
            ContactWorld world = ContactScenes.SlopeWorld(50f, 0.6f, 4f);
            ClimbMotorLoop loop = new ClimbMotorLoop(world, new Vector3(0f, 0.15f, 6f), 0.15f, 0f);
            bool climbed = false;
            for (int i = 0; i < 200; i++)
            {
                Assert.IsTrue(loop.Step());
                climbed |= loop.Ball.ClimbActive;
                Assert.Less(loop.Ball.Residual, 0.02f);
            }
            Assert.IsTrue(climbed);
            Assert.Greater(loop.HighestCenterY - 0.15f, 0.59f);
            Assert.Greater(loop.Ball.Center.z, 7f + 0.6f / Mathf.Tan(50f * Mathf.Deg2Rad));
        }

        [TestCase(50f)]
        [TestCase(58f)]
        public void SlopeStaminaLongSlopeExhaustsAndReturnsToTheFoot(float theta)
        {
            ContactWorld world = ContactScenes.SlopeWorld(theta, 20f, 4f);
            ClimbMotorLoop loop = new ClimbMotorLoop(world, new Vector3(0f, 0.15f, 6.7f), 0.15f, 0f);
            bool exhausted = false, slid = false, reachedFoot = false;
            float peak = 0f;
            for (int i = 0; i < 1500; i++)
            {
                Assert.IsTrue(loop.Step());
                peak = Mathf.Max(peak, loop.Ball.Center.y);
                exhausted |= loop.Ball.SlopeStamina <= 0f;
                slid |= loop.Ball.SlopeSliding;
                Assert.Less(loop.Ball.Residual, 0.02f);
                if (exhausted && slid && loop.Ball.Center.y < 0.17f) { reachedFoot = true; break; }
            }
            Assert.IsTrue(exhausted);
            Assert.IsTrue(slid);
            Assert.IsTrue(reachedFoot, "must descend, not hover or ratchet uphill");
            Assert.Greater(peak - 0.15f, 0.45f, "no fixed 1.5D slope cap");
        }

        [Test]
        public void SlopeStaminaLevelSupportRefills()
        {
            ContactBall ball = new ContactBall { Radius = 0.15f, Center = new Vector3(0f, 0.15f, 0f),
                SlopeStamina = 7f, SlopeUphillSeconds = 4f };
            Assert.IsTrue(ball.Step(new ContactWorld(), Vector3.forward, 0f, 0.02f));
            Assert.AreEqual(100f, ball.SlopeStamina);
            Assert.AreEqual(0f, ball.SlopeUphillSeconds);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SlopeStaminaEnteringAWallStartsItsBudgetAtTheSlopeExit(bool continuousContact)
        {
            ContactWorld world = new ContactWorld { HasGround = false };
            world.Add(new Vector3(0f, 5f, 2.5f), Vector3.right, Vector3.up, Vector3.forward,
                new Vector3(2f, 5f, 0.5f), 1, ContactCodes.RoleBox);
            float angle = 50f * Mathf.Deg2Rad;
            ContactBall ball = new ContactBall { Radius = 0.15f,
                Center = new Vector3(0f, 3f, 1.85f), ClimbActive = continuousContact,
                ClimbNormal = new Vector3(0f, Mathf.Cos(angle), -Mathf.Sin(angle)),
                ClimbAnchorY = 0.15f, ClimbSurfaceId = 2,
                ClimbTopY = float.MaxValue, SlopeStamina = 17f };
            Assert.IsTrue(ball.Step(world, Vector3.forward, 4f, 0.02f));
            Assert.IsTrue(ball.ClimbActive, "the slope's old height is not spent wall budget");
            Assert.AreEqual(3f, ball.ClimbAnchorY, 1e-5f);
            Assert.AreEqual(17f, ball.SlopeStamina, "wall transition does not refill slope stamina");
            Assert.IsFalse(ball.ClimbLocked);
        }

        [TestCase(25f)]
        [TestCase(45f)]
        public void SlopeStaminaRideKeepsItsSpeedAndDoesNotRefillOnANonlevelSlope(float theta)
        {
            ContactWorld world = ContactScenes.SlopeWorld(theta, 20f, 4f);
            float angle = theta * Mathf.Deg2Rad;
            Vector3 normal = new Vector3(0f, Mathf.Cos(angle), -Mathf.Sin(angle));
            ContactBall ball = new ContactBall { Radius = 0.15f,
                Center = new Vector3(0f, 1f, 7f + 1f / Mathf.Tan(angle)) + normal * 0.15f,
                PlanarVelocity = Vector3.forward * 4f, SlopeStamina = 7f, SlopeUphillSeconds = 4f };
            for (int i = 0; i < 20; i++) Assert.IsTrue(ball.Step(world, Vector3.forward, 4f, 0.02f));
            Assert.AreEqual(4f, ball.PlanarVelocity.z, 1e-5f);
            Assert.AreEqual(7f, ball.SlopeStamina);
            Assert.AreEqual(4f, ball.SlopeUphillSeconds);
            Assert.IsFalse(ball.ClimbActive);
        }

        [Test]
        public void SlopeStaminaDashBurstGainsHeightAndSavesStamina()
        {
            float[] heights = new float[2];
            float[] energies = new float[2];
            for (int burst = 0; burst < 2; burst++)
            {
                float stamina = 100f, time = 0f, rest = 0f, along = 0f;
                bool sliding = false;
                for (int i = 0; i < 100; i++)
                {
                    float gate = burst == 1 && i >= 50 && i < 72 ? 3.2f : 2f;
                    SlopeClimbMath.Step(58f, 0.3f, 1f, gate, 0.02f, 0.3f, 0.05f,
                        ref stamina, ref time, ref rest, ref sliding, ref along);
                    heights[burst] += along * Mathf.Sin(58f * Mathf.Deg2Rad) * 0.02f;
                }
                energies[burst] = stamina;
            }
            // 22 dash ticks (0.44 s) at up to 1.5x the 0.275 m/s base rate: about +0.05 m
            // is the physical ceiling of this burst (+0.051 measured), so the bar is half of
            // the 0.09 m the old, 5x-too-fast formula allowed, not a hidden relaxation.
            Assert.Greater(heights[1], heights[0] + 0.04f);
            Assert.Greater(heights[1], heights[0] * 1.08f);
            Assert.Greater(energies[1], energies[0]);
        }

        [TestCase(70f)]
        [TestCase(90f)]
        public void SlopeStaminaWallChannelStillHasItsOnePointFiveDiameterCap(float theta)
        {
            ContactWorld world = theta == 90f ? ContactScenes.WallWorld() : ContactScenes.SlopeWorld(theta, 20f, 4f);
            Vector3 start = theta == 90f ? new Vector3(0f, 0.15f, 0.3f) : new Vector3(0f, 0.15f, 6.7f);
            ClimbMotorLoop loop = new ClimbMotorLoop(world, start, 0.15f, theta == 90f ? 180f : 0f);
            for (int i = 0; i < 500; i++) Assert.IsTrue(loop.Step());
            Assert.LessOrEqual(loop.HighestCenterY - 0.15f, 0.455f);
            Assert.AreEqual(100f, loop.Ball.SlopeStamina);
        }
    }
}
