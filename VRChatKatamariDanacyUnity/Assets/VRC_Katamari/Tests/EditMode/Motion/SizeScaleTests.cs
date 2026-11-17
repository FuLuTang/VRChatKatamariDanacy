using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.Motion;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Scale invariance of the ball's feel (BALL_INERTIA.md, owner 2026-11-17): with
    /// s = D / D0 multiplying every length-type value, start-up times, glide in
    /// diameters and the climb gate read the same at any ball size, and the downhill
    /// slide starts only after the ball has been still. Expected values are the
    /// invariants themselves (equal across sizes) plus hand-solved ODE numbers.
    /// D0 = 0.222 m (the Cancer start ball); D = 0.222 / 0.8 / 5 m.
    /// </summary>
    public class SizeScaleTests
    {
        private const float Dt = 0.02f;
        private const float D0 = 0.222f;
        private static readonly float[] Diameters = { 0.222f, 0.8f, 5f };

        // Inertia values at D0 (the motor defaults).
        private const float MaxSpeed = 4f;
        private const float Acceleration = 7.5f;
        private const float Braking = 6f;
        private const float Friction = 0.9f;
        private const float Rolling = 1.3f;
        private const float Grip = 2f;
        private const float TurnRate = 110f;
        private const float MaxLateral = 1.2f;

        private static bool Step(float forward, float scale, ref Vector3 velocity, ref float heading,
            out Vector3 displacement)
        {
            float nextHeading;
            Vector3 nextVelocity;
            bool ok = BallMotionMath.TryStep(forward, 0f, 0f, heading, velocity, Dt, scale,
                MaxSpeed, Acceleration, Braking, Friction, Rolling, Grip, TurnRate, MaxLateral,
                out nextHeading, out nextVelocity, out displacement);
            if (ok)
            {
                heading = nextHeading;
                velocity = nextVelocity;
            }
            return ok;
        }

        [Test]
        public void StartUpTimesInDiametersPerSecondAreTheSameAtEverySize()
        {
            float first50 = 0f;
            float first95 = 0f;
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                float cap = MaxSpeed * scale;
                Vector3 velocity = Vector3.zero;
                float heading = 0f;
                float t50 = -1f;
                float t95 = -1f;
                Vector3 displacement;
                for (int i = 1; i <= 200; i++)
                {
                    Assert.IsTrue(Step(1f, scale, ref velocity, ref heading, out displacement));
                    float time = i * Dt;
                    if (t50 < 0f && velocity.z >= 0.5f * cap) t50 = time;
                    if (t95 < 0f && velocity.z >= 0.95f * cap) t95 = time;
                }
                Assert.Greater(t50, 0f);
                Assert.Greater(t95, 0f);
                if (k == 0) { first50 = t50; first95 = t95; }
                Assert.AreEqual(first50, t50, 1e-4f, "D=" + Diameters[k]);
                Assert.AreEqual(first95, t95, 1e-4f, "D=" + Diameters[k]);
            }
            // Owner 2026-11-17: >= 95 % of the cap in about 0.7 s.
            Assert.AreEqual(0.70f, first95, 0.05f);
            Assert.AreEqual(0.32f, first50, 0.05f);
        }

        [Test]
        public void ReleaseGlideInDiametersIsTheSameAtEverySize()
        {
            float firstGlide = 0f;
            float firstTime = 0f;
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                Vector3 velocity = new Vector3(0f, 0f, MaxSpeed * scale);
                float heading = 0f;
                float distance = 0f;
                float time = 0f;
                Vector3 displacement;
                while (velocity.z >= 0.1f * scale && time < 10f)
                {
                    Assert.IsTrue(Step(0f, scale, ref velocity, ref heading, out displacement));
                    distance += displacement.z;
                    time += Dt;
                }
                float glideInDiameters = distance / Diameters[k];
                if (k == 0) { firstGlide = glideInDiameters; firstTime = time; }
                Assert.AreEqual(firstGlide, glideInDiameters, 1e-3f, "D=" + Diameters[k]);
                Assert.AreEqual(firstTime, time, 1e-4f, "D=" + Diameters[k]);
            }
            // 2.26 m of glide for the 0.222 m ball at 4 m/s is 10.2 diameters.
            Assert.AreEqual(10.2f, firstGlide, 0.5f);
            Assert.AreEqual(1.4f, firstTime, 0.2f);
        }

        [Test]
        public void ClimbGateReadsTheSameFullPushAndHalfPushAtEverySize()
        {
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                float required = ClimbMath.RequiredSpeed(90f);
                float full = ClimbMath.GateSpeed(0f, MaxSpeed * scale, scale);
                float half = ClimbMath.GateSpeed(0f, 0.5f * MaxSpeed * scale, scale);
                Assert.GreaterOrEqual(full, required, "full push climbs, D=" + Diameters[k]);
                Assert.Less(half, required, "half push does not, D=" + Diameters[k]);
                Assert.IsTrue(ClimbMath.IsClimbEligible(1f, full, 90f, 0f, 1f, 0f));
                Assert.IsFalse(ClimbMath.IsClimbEligible(1f, half, 90f, 0f, 1f, 0f));
                // The actual speed counts too, in the same units.
                Assert.AreEqual(4f, ClimbMath.GateSpeed(MaxSpeed * scale, 0f, scale), 1e-4f);
            }
            // A bad scale is read as 1.
            Assert.AreEqual(2f, ClimbMath.GateSpeed(0f, 4f, 0f), 1e-6f);
            Assert.AreEqual(2f, ClimbMath.GateSpeed(0f, 4f, float.NaN), 1e-6f);
        }

        [Test]
        public void RestLatchOpensOnlyAfterTheHoldAndShutsOnAnyPush()
        {
            float rest = 0f;
            bool sliding = false;
            // 14 still steps of 0.02 s = 0.28 s: not yet.
            for (int i = 0; i < 14; i++)
            {
                Assert.IsTrue(RampSlideMath.TryStepRestLatch(rest, sliding, 0f, 0.05f, 0f,
                    true, Dt, 0.3f, out rest, out sliding));
                Assert.IsFalse(sliding);
            }
            for (int i = 0; i < 2; i++)
            {
                Assert.IsTrue(RampSlideMath.TryStepRestLatch(rest, sliding, 0f, 0.05f, 0f,
                    true, Dt, 0.3f, out rest, out sliding));
            }
            Assert.IsTrue(sliding, "0.32 s of rest on a slope opens the latch");

            // The slide is motion; the latch stays on while there is no push.
            Assert.IsTrue(RampSlideMath.TryStepRestLatch(rest, sliding, 1f, 0.05f, 0f,
                true, Dt, 0.3f, out rest, out sliding));
            Assert.IsTrue(sliding);
            Assert.AreEqual(0f, rest, 0f);

            // Any push, or leaving the slope, shuts it and resets the rest.
            Assert.IsTrue(RampSlideMath.TryStepRestLatch(rest, sliding, 1f, 0.05f, 1f,
                true, Dt, 0.3f, out rest, out sliding));
            Assert.IsFalse(sliding);
            Assert.IsTrue(RampSlideMath.TryStepRestLatch(0.5f, true, 0f, 0.05f, 0f,
                false, Dt, 0.3f, out rest, out sliding));
            Assert.IsFalse(sliding);

            // A moving ball never accumulates rest, so it never opens the latch.
            rest = 0f;
            sliding = false;
            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(RampSlideMath.TryStepRestLatch(rest, sliding, 0.5f, 0.05f, 0f,
                    true, Dt, 0.3f, out rest, out sliding));
                Assert.IsFalse(sliding);
            }
            Assert.IsFalse(RampSlideMath.TryStepRestLatch(0f, false, float.NaN, 0.05f, 0f,
                true, Dt, 0.3f, out rest, out sliding));
        }

        // The motor's order: BallMotionMath step, then the latch, then (latched) the
        // downhill component with gravity and the cap scaled by s. The slope drops
        // towards +X (the support normal tilts to +X), the ball faces +Z, so the slide
        // is a sideways drift that the lateral grip limits.
        private static void RunSlope(float scale, float slopeDegrees, float initialAlongZ,
            float seconds, out float slideStartSeconds, out float distanceX,
            out float maxDownhillSpeedWhileMoving)
        {
            float radians = slopeDegrees * Mathf.Deg2Rad;
            Vector3 normal = new Vector3(Mathf.Sin(radians), Mathf.Cos(radians), 0f);
            Vector3 velocity = new Vector3(0f, 0f, initialAlongZ * scale);
            float heading = 0f;
            float rest = 0f;
            bool sliding = false;
            slideStartSeconds = -1f;
            distanceX = 0f;
            maxDownhillSpeedWhileMoving = 0f;
            int steps = (int)(seconds / Dt);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 displacement;
                Assert.IsTrue(Step(0f, scale, ref velocity, ref heading, out displacement));
                distanceX += displacement.x;
                float speed = Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z);
                Assert.IsTrue(RampSlideMath.TryStepRestLatch(rest, sliding, speed, 0.05f * scale,
                    0f, true, Dt, 0.3f, out rest, out sliding));
                if (sliding && slideStartSeconds < 0f) slideStartSeconds = i * Dt;
                if (!sliding) maxDownhillSpeedWhileMoving =
                    Mathf.Max(maxDownhillSpeedWhileMoving, Mathf.Abs(velocity.x));
                if (sliding)
                {
                    Vector3 slid;
                    Assert.IsTrue(SupportSurfaceMath.TryApplyRampTangentialGravity(velocity,
                        normal, 9.81f * scale, 1f, Dt, MaxSpeed * scale, out slid));
                    velocity = slid;
                }
            }
        }

        [Test]
        public void ABallAtRestStartsSlidingDownASlopeAfterTheHoldAndNotBefore()
        {
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                float start;
                float distanceX;
                float movingDownhill;
                RunSlope(scale, 10f, 0f, 0.28f, out start, out distanceX, out movingDownhill);
                Assert.Less(start, 0f, "no slide inside the 0.3 s hold, D=" + Diameters[k]);
                Assert.AreEqual(0f, distanceX, 0f);

                RunSlope(scale, 10f, 0f, 3f, out start, out distanceX, out movingDownhill);
                Assert.AreEqual(0.30f, start, 0.021f, "D=" + Diameters[k]);
                Assert.Greater(distanceX / Diameters[k], 0.5f,
                    "slid downhill, D=" + Diameters[k]);
            }
        }

        [Test]
        public void DownhillSlideDistanceInDiametersIsTheSameAtEverySize()
        {
            float first = 0f;
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                float start;
                float distanceX;
                float movingDownhill;
                RunSlope(scale, 10f, 0f, 3f, out start, out distanceX, out movingDownhill);
                float inDiameters = distanceX / Diameters[k];
                if (k == 0) first = inDiameters;
                Assert.AreEqual(first, inDiameters, 1e-3f * Mathf.Max(1f, first), "D=" + Diameters[k]);
            }
        }

        [Test]
        public void AMovingBallGetsNoExtraDownhillAcceleration()
        {
            // Rolling along the contour (+Z) at 1 m/s per D0 on a 10 deg slope: while it
            // moves, nothing pushes it downhill (velocity.x stays exactly 0). It only
            // starts to drift after it has come to rest and held still for 0.3 s.
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                float start;
                float distanceX;
                float movingDownhill;
                RunSlope(scale, 10f, 1f, 4f, out start, out distanceX, out movingDownhill);
                Assert.AreEqual(0f, movingDownhill, 0f, "D=" + Diameters[k]);
                // It needs >= 0.3 s after the speed fell under 0.05 m/s per D0, and
                // that takes longer than the 0.3 s hold alone.
                Assert.Greater(start, 0.3f, "D=" + Diameters[k]);
            }
        }

        [Test]
        public void FlatGroundNeverSlides()
        {
            for (int k = 0; k < Diameters.Length; k++)
            {
                float scale = Diameters[k] / D0;
                float start;
                float distanceX;
                float movingDownhill;
                // Gravity on a flat normal is a no-op, so even a latched ball stays put.
                Vector3 slid;
                Assert.IsTrue(SupportSurfaceMath.TryApplyRampTangentialGravity(Vector3.zero,
                    Vector3.up, 9.81f * scale, 1f, Dt, MaxSpeed * scale, out slid));
                Assert.AreEqual(0f, slid.magnitude, 0f);
                RunSlope(scale, 0f, 0f, 2f, out start, out distanceX, out movingDownhill);
                Assert.AreEqual(0f, distanceX, 1e-6f);
            }
        }
    }
}
