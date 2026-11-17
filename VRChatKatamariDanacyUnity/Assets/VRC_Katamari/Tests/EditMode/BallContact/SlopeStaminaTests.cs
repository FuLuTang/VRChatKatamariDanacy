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
            Assert.Greater(heights[1], heights[0] + 0.1f);
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
