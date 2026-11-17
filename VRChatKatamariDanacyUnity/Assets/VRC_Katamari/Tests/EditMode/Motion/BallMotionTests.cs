using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Motion;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of the planar motion half of
    /// Docs/tests/ball_motion/test_ball_motion.py, plus the layer invariants:
    /// one fixed step is solved from caller-owned heading and velocity, the
    /// heading and velocity are handed back, and no vertical motion exists in
    /// this API. Motion is XZ only, +Y up, metres and seconds; headings are
    /// degrees about +Y in [0, 360), where 0 faces +Z.
    ///
    /// Expected values are hand calculations from the model's rules
    /// (forward damping exp(-friction*dt), lateral damping
    /// exp(-(friction+grip)*dt), rolling resistance per step, every length-type
    /// value times sizeScale) or invariants, never re-runs of the implementation.
    /// The harness defaults are the inertia values at D0 (BALL_INERTIA.md,
    /// owner 2026-11-17, all PROVISIONAL), sizeScale 1 and are passed explicitly; none of them is a project
    /// constant. LegacyHarness() holds the pre-inertia comparison values.
    /// </summary>
    public class BallMotionTests
    {
        private const float FixedStepSeconds = 1f / 120f;
        private const float Tolerance = 1e-5f;

        // Test-only stand-in for the Python object's stored fields: the runtime
        // API is stateless, so the harness owns the heading, velocity, growth
        // term and tuning values and passes them back on every call.
        private sealed class BallHarness
        {
            public float HeadingDegrees;
            public Vector3 Velocity;
            public Vector3 Displacement;
            public float SizeScale = 1f;
            public float MaxSpeedMps = 4f;
            public float AccelerationMps2 = 7.5f;
            public float BrakingMps2 = 6f;
            public float FrictionPerSecond = 0.9f;
            public float RollingResistanceMps2 = 1.3f;
            public float LateralGripPerSecond = 2f;
            public float TurnRateDegreesPerSecond = 110f;
            public float MaxLateralMps = 1.2f;

            public bool Step(float forward, float turn, float lateral, float deltaTime)
            {
                float heading;
                Vector3 velocity;
                Vector3 displacement;
                bool accepted = BallMotionMath.TryStep(
                    forward, turn, lateral, HeadingDegrees, Velocity, deltaTime, SizeScale,
                    MaxSpeedMps, AccelerationMps2, BrakingMps2, FrictionPerSecond,
                    RollingResistanceMps2, LateralGripPerSecond, TurnRateDegreesPerSecond, MaxLateralMps,
                    out heading, out velocity, out displacement);
                if (!accepted) return false;
                HeadingDegrees = heading;
                Velocity = velocity;
                Displacement = displacement;
                return true;
            }

            // Pre-inertia comparison values (approximate; see BallMotionMath remarks).
            public static BallHarness LegacyHarness()
            {
                BallHarness legacy = new BallHarness();
                legacy.AccelerationMps2 = 5f;
                legacy.BrakingMps2 = 8f;
                legacy.FrictionPerSecond = 1.2f;
                legacy.RollingResistanceMps2 = 8f;
                legacy.LateralGripPerSecond = 1000f;
                return legacy;
            }

            public float Speed()
            {
                return Mathf.Sqrt(Velocity.x * Velocity.x + Velocity.z * Velocity.z);
            }
        }

        [Test]
        public void SteadyForwardInputReachesTheSizeScaledSpeedCap()
        {
            // One step from rest adds 7.5 * (1/120) along +Z and then damps it by
            // exp(-0.9/120): 0.0625 * 0.9925280 = 0.0620330 m/s.
            BallHarness ball = new BallHarness();
            Assert.IsTrue(ball.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.0620330f, ball.Velocity.z, 1e-6f);
            Assert.AreEqual(0f, ball.Velocity.x, 0f);

            // The clamped map's fixed point is the cap itself: (4 + 7.5/120) *
            // exp(-0.0075) = 4.0321 > 4, so 4 m/s is terminal and not the
            // friction equilibrium. Nothing in the ramp may exceed the cap.
            float previous = ball.Speed();
            for (int i = 0; i < 1200; i++)
            {
                Assert.IsTrue(ball.Step(1f, 0f, 0f, FixedStepSeconds));
                float current = ball.Speed();
                Assert.GreaterOrEqual(current, previous - 1e-6f);
                Assert.LessOrEqual(current, 4f + Tolerance);
                previous = current;
            }
            Assert.AreEqual(4f, ball.Velocity.z, Tolerance);

            // With a cap the friction law cannot reach, the discrete equilibrium
            // is a*dt*damp/(1-damp) = 0.0620330 / 0.0074719 = 8.302 m/s, one
            // step of discretisation below the continuous a/k = 7.5/0.9 = 8.333.
            BallHarness uncapped = new BallHarness();
            uncapped.MaxSpeedMps = 10f;
            for (int i = 0; i < 1200; i++) Assert.IsTrue(uncapped.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(8.302f, uncapped.Speed(), 5e-3f);

            // sizeScale multiplies the cap: at 3x the terminal speed is 12 m/s.
            BallHarness grown = new BallHarness();
            grown.SizeScale = 3f;
            for (int i = 0; i < 1200; i++) Assert.IsTrue(grown.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(12f, grown.Velocity.z, 1e-4f);
        }

        [Test]
        public void ZeroInputDecaysToRestWithoutOscillation()
        {
            // Friction first, then rolling resistance: 4 * exp(-0.0075) - 1.3/120 =
            // 3.970112 - 0.010833 = 3.959279 m/s after one coasting step.
            BallHarness ball = new BallHarness();
            ball.Velocity = new Vector3(0f, 0f, 4f);
            Assert.IsTrue(ball.Step(0f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(3.959279f, ball.Velocity.z, Tolerance);

            // dv/dt = -0.9 v - 1.3 reaches zero at ln(1 + 0.9*4/1.3)/0.9 = 1.47 s,
            // so 2 s of coasting is enough; the speed never rises and the velocity
            // never turns around, which is the "no oscillation" claim.
            float previous = ball.Speed();
            bool reachedZero = false;
            for (int i = 0; i < 240; i++)
            {
                Assert.IsTrue(ball.Step(0f, 0f, 0f, FixedStepSeconds));
                float current = ball.Speed();
                Assert.LessOrEqual(current, previous + 1e-6f);
                Assert.GreaterOrEqual(current, 0f);
                Assert.GreaterOrEqual(ball.Velocity.z, 0f);
                if (current == 0f) reachedZero = true;
                previous = current;
            }
            Assert.IsTrue(reachedZero, "rolling resistance must reach an exact stop");
            Assert.AreEqual(0f, ball.Speed(), 0f);

            // Rest is absorbing: more coasting keeps it at exactly zero.
            for (int i = 0; i < 30; i++) Assert.IsTrue(ball.Step(0f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(0f, ball.Speed(), 0f);
            Assert.AreEqual(0f, ball.Velocity.z, 0f);

            // A backward velocity decays the same way and never crosses into
            // forward motion.
            BallHarness back = new BallHarness();
            back.Velocity = new Vector3(0f, 0f, -4f);
            for (int i = 0; i < 240; i++)
            {
                Assert.IsTrue(back.Step(0f, 0f, 0f, FixedStepSeconds));
                Assert.LessOrEqual(back.Velocity.z, 0f);
            }
            Assert.AreEqual(0f, back.Speed(), 0f);
        }

        [Test]
        public void AccelerationIsSignSymmetricUnderIntentFlip()
        {
            // The step is odd in (velocity, forward): no turn, so the whole solve
            // is antisymmetric and flipping both inputs must negate velocity and
            // displacement exactly, not merely to within a tolerance.
            BallHarness positive = new BallHarness();
            positive.HeadingDegrees = 37f;
            positive.Velocity = new Vector3(0.3f, 0f, -0.2f);
            BallHarness negative = new BallHarness();
            negative.HeadingDegrees = 37f;
            negative.Velocity = new Vector3(-0.3f, 0f, 0.2f);

            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(positive.Step(1f, 0f, 0f, FixedStepSeconds));
                Assert.IsTrue(negative.Step(-1f, 0f, 0f, FixedStepSeconds));
                Assert.AreEqual(-positive.Velocity.x, negative.Velocity.x, 0f);
                Assert.AreEqual(-positive.Velocity.z, negative.Velocity.z, 0f);
                Assert.AreEqual(-positive.Displacement.x, negative.Displacement.x, 0f);
                Assert.AreEqual(-positive.Displacement.z, negative.Displacement.z, 0f);
                Assert.AreEqual(positive.HeadingDegrees, negative.HeadingDegrees, 0f);
            }

            // Lateral intent is the other axis: flipping it must negate the
            // lateral speed while leaving the forward speed untouched. The
            // projections are measurements, so float rounding keeps them equal
            // only to a tolerance, unlike the exact negation above.
            BallHarness left = new BallHarness();
            left.HeadingDegrees = 20f;
            BallHarness right = new BallHarness();
            right.HeadingDegrees = 20f;
            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(left.Step(1f, 0f, 1f, FixedStepSeconds));
                Assert.IsTrue(right.Step(1f, 0f, -1f, FixedStepSeconds));
                Assert.AreEqual(-LateralSpeedOf(left.Velocity, 20f),
                    LateralSpeedOf(right.Velocity, 20f), Tolerance);
                Assert.AreEqual(ForwardSpeedOf(left.Velocity, 20f),
                    ForwardSpeedOf(right.Velocity, 20f), Tolerance);
            }
        }

        [Test]
        public void TurnFollowsTheUnscaledRateAndWraps()
        {
            // 110 deg/s over 1/120 s is 0.916667 deg. A negative turn is not a
            // negative heading: the stored heading stays in [0, 360), so it wraps
            // to 360 - 0.916667 = 359.08333.
            BallHarness ball = new BallHarness();
            Assert.IsTrue(ball.Step(0f, 1f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.9166667f, ball.HeadingDegrees, 1e-4f);
            BallHarness backward = new BallHarness();
            Assert.IsTrue(backward.Step(0f, -1f, 0f, FixedStepSeconds));
            Assert.AreEqual(359.08334f, backward.HeadingDegrees, 1e-4f);

            // The turn axis is clamped, so 5 is the same intent as 1.
            BallHarness strong = new BallHarness();
            Assert.IsTrue(strong.Step(0f, 5f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.9166667f, strong.HeadingDegrees, 1e-4f);

            // The turn rate is a time constant: it does not scale with the ball.
            BallHarness grown = new BallHarness();
            grown.SizeScale = 5f;
            Assert.IsTrue(grown.Step(0f, 1f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.9166667f, grown.HeadingDegrees, 1e-4f);

            // 120 steps at the full rate is 110 deg, and a step that would pass
            // 360 wraps back to the start of the range.
            BallHarness full = new BallHarness();
            for (int i = 0; i < 120; i++) Assert.IsTrue(full.Step(0f, 1f, 0f, FixedStepSeconds));
            Assert.AreEqual(110f, full.HeadingDegrees, 1e-3f);
            BallHarness wrapped = new BallHarness();
            wrapped.HeadingDegrees = 359.5f;
            Assert.IsTrue(wrapped.Step(0f, 1f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.41666f, wrapped.HeadingDegrees, 1e-4f);

            // Acceleration follows the heading the step ENDED with: a 180 deg/s
            // turn rate over a 0.5 s step is a full 90 deg turn, so the speed
            // this step adds must land on +X, not on the +Z it started facing.
            // 7.5 * 0.5 = 3.75 m/s, damped by exp(-0.45) = 0.637628.
            BallHarness turned = new BallHarness();
            turned.TurnRateDegreesPerSecond = 180f;
            Assert.IsTrue(turned.Step(1f, 1f, 0f, 0.5f));
            Assert.AreEqual(90f, turned.HeadingDegrees, 1e-4f);
            Assert.AreEqual(2.391105f, turned.Velocity.x, 1e-4f);
            Assert.AreEqual(0f, turned.Velocity.z, 1e-4f);
            Assert.AreEqual(1.195553f, turned.Displacement.x, 1e-4f);
        }

        [Test]
        public void StepTravelIsUnclampedAndReproducible()
        {
            // Port of test_delay_and_fixed_step_are_bounded_and_reproducible. The
            // model claims fixed-step reproducibility, not continuous delta-time
            // independence, so the only claim to check is that the same step
            // sequence always produces the same state.
            BallHarness first = new BallHarness();
            BallHarness second = new BallHarness();
            for (int i = 0; i < 300; i++) Assert.IsTrue(first.Step(1f, 0.2f, 0f, FixedStepSeconds));
            for (int i = 0; i < 300; i++) Assert.IsTrue(second.Step(1f, 0.2f, 0f, FixedStepSeconds));
            Assert.AreEqual(first.HeadingDegrees, second.HeadingDegrees, 0f);
            Assert.AreEqual(first.Velocity.x, second.Velocity.x, 0f);
            Assert.AreEqual(first.Velocity.z, second.Velocity.z, 0f);
            // 300 * 0.2 * 110 / 120 = 55 deg of turn.
            Assert.AreEqual(55f, first.HeadingDegrees, 1e-3f);
            Assert.LessOrEqual(first.Speed(), 4f + Tolerance);

            // Interleaving two independent balls must not change either run: the
            // layer has no static state to leak between callers.
            BallHarness interleavedFirst = new BallHarness();
            BallHarness interleavedOther = new BallHarness();
            for (int i = 0; i < 300; i++)
            {
                Assert.IsTrue(interleavedFirst.Step(1f, 0.2f, 0f, FixedStepSeconds));
                Assert.IsTrue(interleavedOther.Step(-1f, 0.5f, 0.3f, FixedStepSeconds));
            }
            Assert.AreEqual(first.Velocity.x, interleavedFirst.Velocity.x, 0f);
            Assert.AreEqual(first.Velocity.z, interleavedFirst.Velocity.z, 0f);
            Assert.AreEqual(first.HeadingDegrees, interleavedFirst.HeadingDegrees, 0f);

            // Travel is exactly velocity * dt with no sub-stepping and no
            // clipping: keeping this 80 m/s segment out of a thin wall is the
            // sweep layer's job (the Python fast-sweep case), and this layer must
            // hand it the whole segment.
            BallHarness fast = new BallHarness();
            fast.MaxSpeedMps = 200f;
            fast.Velocity = new Vector3(0f, 0f, 80f);
            Assert.IsTrue(fast.Step(0f, 0f, 0f, FixedStepSeconds));
            // The expected travel is materialised as a float local first: the values are
            // bit-identical, but NUnit's zero-delta overload still reports the inline
            // product at full double precision and rejects the pair. The tolerance is
            // 1e-6 m, far below the 0.66 m a clipped or sub-stepped segment would lose.
            float expectedTravel = fast.Velocity.z * FixedStepSeconds;
            Assert.AreEqual(expectedTravel, fast.Displacement.z, 1e-6f);
            Assert.Greater(fast.Displacement.z, 0.6f);
            Assert.AreEqual(0f, fast.Velocity.y, 0f);

            // One 2.5 s step is not 2.5 s of stepping: (7.5 * 2.5) * exp(-2.25) =
            // 1.976236 m/s, far from the ~3.9 m/s the same input reaches in 300
            // fixed steps. Sub-stepping belongs to the caller's clock.
            BallHarness coarse = new BallHarness();
            Assert.IsTrue(coarse.Step(1f, 0f, 0f, 2.5f));
            Assert.AreEqual(1.976236f, coarse.Velocity.z, 1e-4f);
            Assert.Greater(Mathf.Abs(coarse.Velocity.z - first.Speed()), 1f);
        }

        [Test]
        public void InputsAreClampedAndSizeScalesEveryLength()
        {
            // Scale invariance: a ball at sizeScale 3 under the same inputs has
            // exactly 3x the velocity and the same heading (every length-type value
            // scales, every time constant does not).
            BallHarness unitBall = new BallHarness();
            BallHarness tripled = new BallHarness();
            tripled.SizeScale = 3f;
            for (int i = 0; i < 200; i++)
            {
                Assert.IsTrue(unitBall.Step(1f, (i % 50) < 25 ? 1f : -0.5f, 0.5f, FixedStepSeconds));
                Assert.IsTrue(tripled.Step(1f, (i % 50) < 25 ? 1f : -0.5f, 0.5f, FixedStepSeconds));
                Assert.AreEqual(3f * unitBall.Velocity.x, tripled.Velocity.x, 1e-4f);
                Assert.AreEqual(3f * unitBall.Velocity.z, tripled.Velocity.z, 1e-4f);
                Assert.AreEqual(unitBall.HeadingDegrees, tripled.HeadingDegrees, 1e-4f);
            }

            // The three intent axes are clamped to [-1, 1] as well.
            BallHarness clamped = new BallHarness();
            BallHarness unit = new BallHarness();
            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(clamped.Step(5f, 5f, 5f, FixedStepSeconds));
                Assert.IsTrue(unit.Step(1f, 1f, 1f, FixedStepSeconds));
                Assert.AreEqual(unit.Velocity.x, clamped.Velocity.x, 0f);
                Assert.AreEqual(unit.Velocity.z, clamped.Velocity.z, 0f);
                Assert.AreEqual(unit.HeadingDegrees, clamped.HeadingDegrees, 0f);
            }

            // Sideways speed is no longer hard-clamped: a 3 m/s sideways entry
            // decays by the grip, exp(-(0.9 + 2.0)/120) = 0.976123, so one step
            // leaves 2.928369 m/s (the drift that replaced the old 1.2 m/s clamp).
            BallHarness sideways = new BallHarness();
            sideways.MaxSpeedMps = 20f;
            sideways.Velocity = new Vector3(3f, 0f, 0f);
            Assert.IsTrue(sideways.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(2.928369f, sideways.Velocity.x, 1e-4f);

            // Strafe input never pushes sideways speed past maxLateralMps: the
            // same drift entry with full strafe input only decays, it does not grow.
            BallHarness strafing = new BallHarness();
            strafing.MaxSpeedMps = 20f;
            strafing.Velocity = new Vector3(3f, 0f, 0f);
            Assert.IsTrue(strafing.Step(1f, 0f, 1f, FixedStepSeconds));
            Assert.AreEqual(2.928369f, strafing.Velocity.x, 1e-4f);

            // Held lateral assist settles at L*dt/(1-damp) with the lateral
            // damping exp(-2.9/120): 0.01 / 0.023877 = 0.4188 m/s, below the clamp.
            BallHarness assisted = new BallHarness();
            assisted.MaxSpeedMps = 20f;
            for (int i = 0; i < 1200; i++)
            {
                Assert.IsTrue(assisted.Step(1f, 0f, 1f, FixedStepSeconds));
                Assert.LessOrEqual(LateralSpeedOf(assisted.Velocity, 0f),
                    1.2f + Tolerance);
            }
            Assert.AreEqual(0.4188f, LateralSpeedOf(assisted.Velocity, 0f), 2e-3f);
            Assert.LessOrEqual(LateralSpeedOf(assisted.Velocity, 0f), 1.2f);
        }

        [Test]
        public void NonFiniteInputAndOverflowAreRejectedAtomically()
        {
            // A refused call writes nothing: the harness still holds the state it
            // passed in, and every documented out value is zero.
            BallHarness ball = new BallHarness();
            Assert.IsTrue(ball.Step(1f, 0f, 0f, FixedStepSeconds));
            Vector3 keptVelocity = ball.Velocity;
            float keptHeading = ball.HeadingDegrees;

            Assert.IsFalse(ball.Step(1f, 0f, 0f, float.NaN));
            Assert.IsFalse(ball.Step(1f, 0f, 0f, 0f));            // zero step
            Assert.IsFalse(ball.Step(1f, 0f, 0f, -FixedStepSeconds));
            Assert.IsFalse(ball.Step(float.NaN, 0f, 0f, FixedStepSeconds));
            Assert.IsFalse(ball.Step(1f, float.PositiveInfinity, 0f, FixedStepSeconds));
            Assert.IsFalse(ball.Step(1f, 0f, float.NaN, FixedStepSeconds));
            Assert.AreEqual(keptVelocity.x, ball.Velocity.x, 0f);
            Assert.AreEqual(keptVelocity.z, ball.Velocity.z, 0f);
            Assert.AreEqual(keptHeading, ball.HeadingDegrees, 0f);

            float heading;
            Vector3 velocity;
            Vector3 displacement;
            Assert.IsFalse(BallMotionMath.TryStep(
                float.NaN, 0f, 0f, 0f, Vector3.zero, FixedStepSeconds, 1f,
                4f, 7.5f, 6f, 0.9f, 1.3f, 2f, 110f, 1.2f,
                out heading, out velocity, out displacement));
            Assert.AreEqual(0f, heading, 0f);
            Assert.AreEqual(0f, velocity.x, 0f);
            Assert.AreEqual(0f, velocity.y, 0f);
            Assert.AreEqual(0f, velocity.z, 0f);
            Assert.AreEqual(0f, displacement.x, 0f);
            Assert.AreEqual(0f, displacement.y, 0f);
            Assert.AreEqual(0f, displacement.z, 0f);

            // A corrupt stored velocity or heading is refused rather than
            // propagated; y is unused by the solve but must still be finite.
            BallHarness corrupt = new BallHarness();
            corrupt.Velocity = new Vector3(float.NaN, 0f, 0f);
            Assert.IsFalse(corrupt.Step(1f, 0f, 0f, FixedStepSeconds));
            corrupt.Velocity = new Vector3(0f, float.PositiveInfinity, 0f);
            Assert.IsFalse(corrupt.Step(1f, 0f, 0f, FixedStepSeconds));
            corrupt.Velocity = Vector3.zero;
            corrupt.HeadingDegrees = float.NaN;
            Assert.IsFalse(corrupt.Step(1f, 0f, 0f, FixedStepSeconds));

            // Configuration is validated: negative rates, a non-positive size
            // scale and a non-finite tuning value are all refused.
            BallHarness bad = new BallHarness();
            bad.MaxSpeedMps = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.AccelerationMps2 = float.NaN;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.BrakingMps2 = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.FrictionPerSecond = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.RollingResistanceMps2 = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.LateralGripPerSecond = float.NaN;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.TurnRateDegreesPerSecond = float.PositiveInfinity;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.MaxLateralMps = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.SizeScale = float.NaN;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.SizeScale = 0f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.SizeScale = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));

            // Finite inputs can still overflow an intermediate, and that is
            // refused rather than clamped. Accel * dt = 9e76 is infinite, and a
            // 3e38 m/s velocity over 100 s has no finite displacement.
            BallHarness accelerating = new BallHarness();
            accelerating.FrictionPerSecond = 0f;
            accelerating.LateralGripPerSecond = 0f;
            accelerating.AccelerationMps2 = 3e38f;
            Assert.IsFalse(accelerating.Step(1f, 0f, 0f, 3e38f));
            BallHarness longStep = new BallHarness();
            longStep.FrictionPerSecond = 0f;
            longStep.LateralGripPerSecond = 0f;
            longStep.MaxSpeedMps = 3e38f;
            longStep.Velocity = new Vector3(0f, 0f, 3e38f);
            Assert.IsFalse(longStep.Step(0f, 0f, 0f, 100f));
        }

        [Test]
        public void ReleaseGlideIsAVisibleCoastNotAnInstantStop()
        {
            // Preset B at the motor's 0.02 s step, from the 4 m/s cap. The
            // continuous solution v(t) = (4 + c) exp(-0.9 t) - c with c = 1.3/0.9 =
            // 1.444 reaches 0.1 m/s at 1.40 s, and the distance there is
            // (4 + c)/0.9 * (1 - exp(-0.9 * 1.40)) - c * 1.40 = 2.31 m; the old
            // model stopped in 0.40 s over 0.68 m.
            BallHarness ball = new BallHarness();
            ball.Velocity = new Vector3(0f, 0f, 4f);
            float time = 0f;
            float distance = 0f;
            while (ball.Speed() >= 0.1f && time < 5f)
            {
                Assert.IsTrue(ball.Step(0f, 0f, 0f, 0.02f));
                time += 0.02f;
                distance += ball.Displacement.z;
            }
            Assert.AreEqual(1.4f, time, 0.2f);
            Assert.AreEqual(2.3f, distance, 0.3f);
            Assert.Greater(time, 1.0f);
        }

        [Test]
        public void OpposingInputUsesTheBrakeRateAndSkipsRollingResistance()
        {
            // From 4 m/s, reverse input: 4 - 6/120 = 3.95, damped by 0.992528 =
            // 3.920487. Had it used acceleration the step would be 3.9337.
            BallHarness brake = new BallHarness();
            brake.Velocity = new Vector3(0f, 0f, 4f);
            Assert.IsTrue(brake.Step(-1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(3.920487f, brake.Velocity.z, 1e-5f);

            // The brake rate is the BrakingMps2 field: zeroing it leaves only drag.
            BallHarness weak = new BallHarness();
            weak.BrakingMps2 = 0f;
            weak.Velocity = new Vector3(0f, 0f, 4f);
            Assert.IsTrue(weak.Step(-1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(4f * 0.9925280f, weak.Velocity.z, 1e-5f);

            // Same-sign input is plain acceleration: (1 + 7.5/120) * 0.992528.
            BallHarness push = new BallHarness();
            push.Velocity = new Vector3(0f, 0f, 1f);
            Assert.IsTrue(push.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(1.054561f, push.Velocity.z, 1e-5f);

            // Rolling resistance exists only without forward input: a huge value
            // changes nothing while a forward key is held, and does with none.
            BallHarness held = new BallHarness();
            held.RollingResistanceMps2 = 1000f;
            held.Velocity = new Vector3(0f, 0f, 1f);
            Assert.IsTrue(held.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(push.Velocity.z, held.Velocity.z, 0f);
            BallHarness released = new BallHarness();
            released.RollingResistanceMps2 = 1000f;
            released.Velocity = new Vector3(0f, 0f, 1f);
            Assert.IsTrue(released.Step(0f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(0f, released.Velocity.z, 0f);

            // Braking from the cap crosses zero much faster than coasting, and a
            // held reverse key then keeps accelerating backwards (no stop).
            BallHarness reversing = new BallHarness();
            reversing.Velocity = new Vector3(0f, 0f, 4f);
            float crossTime = 0f;
            while (reversing.Velocity.z > 0f && crossTime < 3f)
            {
                Assert.IsTrue(reversing.Step(-1f, 0f, 0f, 0.02f));
                crossTime += 0.02f;
            }
            Assert.AreEqual(0.54f, crossTime, 0.1f);
            Assert.IsTrue(reversing.Step(-1f, 0f, 0f, 0.02f));
            Assert.Less(reversing.Velocity.z, 0f);
        }

        [Test]
        public void ReverseSpeedIsCappedAtHalfTheSizeScaledCap()
        {
            BallHarness ball = new BallHarness();
            for (int i = 0; i < 1200; i++)
            {
                Assert.IsTrue(ball.Step(-1f, 0f, 0f, FixedStepSeconds));
                Assert.GreaterOrEqual(ball.Velocity.z, -2f - Tolerance);
            }
            Assert.AreEqual(-2f, ball.Velocity.z, Tolerance);

            // The cap scales with the ball: 0.5 * 4 * 3 = 6 m/s at sizeScale 3.
            BallHarness grown = new BallHarness();
            grown.SizeScale = 3f;
            for (int i = 0; i < 1200; i++) Assert.IsTrue(grown.Step(-1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(-6f, grown.Velocity.z, 1e-4f);
        }

        [Test]
        public void TurningLeavesTheVelocityDirectionBehindTheHeading()
        {
            // Full speed, then full turn at the motor's 0.02 s step until the
            // heading reaches 90 deg. The defaults leave the velocity near 61 deg
            // (a ~29 deg lag) at the 4 m/s cap (the thrust refills the speed); a stronger grip lags less, and a
            // huge grip (the old hard-clamp behaviour) follows the heading.
            float lag12 = TurnLagDegrees(1.2f);
            float lag20 = TurnLagDegrees(2.0f);
            float lag30 = TurnLagDegrees(3.0f);
            float lagHuge = TurnLagDegrees(1000f);
            Assert.AreEqual(29f, lag20, 8f);
            Assert.Greater(lag12, lag20);
            Assert.Greater(lag20, lag30);
            Assert.Greater(lag30, lagHuge);
            Assert.Less(lagHuge, 1f);

            BallHarness ball = new BallHarness();
            ball.Velocity = new Vector3(0f, 0f, 4f);
            while (ball.HeadingDegrees < 90f) Assert.IsTrue(ball.Step(1f, 1f, 0f, 0.02f));
            Assert.AreEqual(4f, ball.Speed(), 0.05f);
        }

        [Test]
        public void LegacyComparisonValuesStopQuicklyAndFollowTheHeading()
        {
            // The documented old-behaviour set: braking = rolling resistance = 8,
            // grip huge. Release from 4 m/s stops in about half a second.
            BallHarness legacy = BallHarness.LegacyHarness();
            legacy.Velocity = new Vector3(0f, 0f, 4f);
            float time = 0f;
            while (legacy.Speed() > 0f && time < 3f)
            {
                Assert.IsTrue(legacy.Step(0f, 0f, 0f, 0.02f));
                time += 0.02f;
            }
            Assert.AreEqual(0.45f, time, 0.15f);
            Assert.AreEqual(0f, legacy.Speed(), 0f);
        }

        // Heading minus velocity direction (degrees) at the step the heading
        // first reaches 90, after reaching the 4 m/s cap going straight.
        private static float TurnLagDegrees(float lateralGripPerSecond)
        {
            BallHarness ball = new BallHarness();
            ball.LateralGripPerSecond = lateralGripPerSecond;
            ball.Velocity = new Vector3(0f, 0f, 4f);
            for (int i = 0; i < 400; i++) Assert.IsTrue(ball.Step(1f, 0f, 0f, 0.02f));
            while (ball.HeadingDegrees < 90f) Assert.IsTrue(ball.Step(1f, 1f, 0f, 0.02f));
            float velocityDegrees = Mathf.Atan2(ball.Velocity.x, ball.Velocity.z) * (180f / Mathf.PI);
            return ball.HeadingDegrees - velocityDegrees;
        }

        [Test]
        public void ApiIsStatelessPlanarAndSingleEntryPoint()
        {
            // One entry point, no overloads, scalars and Vector3 only: no config
            // object, no rotation, no arrays and nothing to allocate.
            MethodInfo[] methods = typeof(BallMotionMath).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.AreEqual(1, methods.Length);
            Assert.AreEqual("TryStep", methods[0].Name);

            ParameterInfo[] parameters = methods[0].GetParameters();
            int outputIndex = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                if (parameter.IsOut)
                {
                    Type outType = parameter.ParameterType.GetElementType();
                    if (outputIndex == 0) Assert.AreEqual(typeof(float), outType);
                    else Assert.AreEqual(typeof(Vector3), outType);
                    outputIndex++;
                    continue;
                }
                Type type = parameter.ParameterType;
                Assert.IsTrue(type == typeof(float) || type == typeof(Vector3),
                    "unexpected parameter type " + type.Name);
            }
            Assert.AreEqual(3, outputIndex);

            // No mutable static state: the only fields allowed are literals.
            FieldInfo[] fields = typeof(BallMotionMath).GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                Assert.IsTrue(fields[i].IsLiteral,
                    fields[i].Name + " would be mutable static state");
            }

            // Planar by construction: a vertical velocity is not solved and
            // never appears in either output, while the planar part still moves.
            float heading;
            Vector3 velocity;
            Vector3 displacement;
            Assert.IsTrue(BallMotionMath.TryStep(
                1f, 0f, 0f, 0f, new Vector3(0f, 9f, 0f), FixedStepSeconds, 1f,
                4f, 7.5f, 6f, 0.9f, 1.3f, 2f, 110f, 1.2f,
                out heading, out velocity, out displacement));
            Assert.AreEqual(0f, velocity.y, 0f);
            Assert.AreEqual(0f, displacement.y, 0f);
            Assert.Greater(velocity.z, 0f);
            Assert.Greater(displacement.z, 0f);
        }

        // Projection of a velocity onto the stored heading axes. Used only to
        // state "the lateral speed flipped and the forward speed did not"; the
        // expected values themselves come from the hand calculations above.
        private static float ForwardSpeedOf(Vector3 velocity, float headingDegrees)
        {
            float radians = headingDegrees * (Mathf.PI / 180f);
            return velocity.x * Mathf.Sin(radians) + velocity.z * Mathf.Cos(radians);
        }

        private static float LateralSpeedOf(Vector3 velocity, float headingDegrees)
        {
            float radians = headingDegrees * (Mathf.PI / 180f);
            return velocity.x * Mathf.Cos(radians) - velocity.z * Mathf.Sin(radians);
        }
    }
}
