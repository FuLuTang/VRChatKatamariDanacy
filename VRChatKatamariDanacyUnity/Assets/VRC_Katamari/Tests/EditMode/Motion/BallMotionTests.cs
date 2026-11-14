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
    /// (damping exp(-friction*dt), braking*dt per step, cap derating
    /// 1 - factor*growth) or invariants, never re-runs of the implementation.
    /// The parameter values are the reference model's DRAFT placeholders and
    /// are passed explicitly; none of them is a project constant.
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
            public float GrowthFraction;
            public float MaxSpeedMps = 4f;
            public float AccelerationMps2 = 5f;
            public float BrakingMps2 = 8f;
            public float FrictionPerSecond = 1.2f;
            public float TurnRateDegreesPerSecond = 110f;
            public float MaxLateralMps = 1.2f;
            public float GrowthSpeedFactor = 0.25f;
            public float GrowthTurnFactor = 0.35f;

            public bool Step(float forward, float turn, float lateral, float deltaTime)
            {
                float heading;
                Vector3 velocity;
                Vector3 displacement;
                bool accepted = BallMotionMath.TryStep(
                    forward, turn, lateral, HeadingDegrees, Velocity, deltaTime, GrowthFraction,
                    MaxSpeedMps, AccelerationMps2, BrakingMps2, FrictionPerSecond,
                    TurnRateDegreesPerSecond, MaxLateralMps, GrowthSpeedFactor, GrowthTurnFactor,
                    out heading, out velocity, out displacement);
                if (!accepted) return false;
                HeadingDegrees = heading;
                Velocity = velocity;
                Displacement = displacement;
                return true;
            }

            public float Speed()
            {
                return Mathf.Sqrt(Velocity.x * Velocity.x + Velocity.z * Velocity.z);
            }
        }

        [Test]
        public void SteadyForwardInputReachesTheGrowthDeratedSpeedCap()
        {
            // One step from rest adds 5 * (1/120) along +Z and then damps it by
            // exp(-1.2/120): 0.0416667 * 0.9900498 = 0.0412521 m/s.
            BallHarness ball = new BallHarness();
            Assert.IsTrue(ball.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.0412521f, ball.Velocity.z, 1e-6f);
            Assert.AreEqual(0f, ball.Velocity.x, 0f);

            // The clamped map's fixed point is the cap itself: (4 + 5/120) *
            // exp(-0.01) = 4.00145 > 4, so 4 m/s is terminal and not the
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
            // is a*dt*damp/(1-damp) = 0.0412521 / 0.0099502 = 4.14587 m/s (float
            // accumulation lands one rounding below), which is one step of
            // discretisation away from the continuous a/k = 5/1.2 = 4.16667.
            BallHarness uncapped = new BallHarness();
            uncapped.MaxSpeedMps = 10f;
            for (int i = 0; i < 1200; i++) Assert.IsTrue(uncapped.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(4.14583f, uncapped.Speed(), 2e-4f);

            // Full growth derates the cap by growthSpeedFactor: 4 * (1 - 0.25)
            // = 3 m/s, and the derated cap is still positive.
            BallHarness grown = new BallHarness();
            grown.GrowthFraction = 1f;
            for (int i = 0; i < 1200; i++) Assert.IsTrue(grown.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(3f, grown.Velocity.z, Tolerance);
        }

        [Test]
        public void ZeroInputDecaysToRestWithoutOscillation()
        {
            // Friction first, then the braking bound: 4 * exp(-0.01) - 8/120 =
            // 3.960200 - 0.066667 = 3.893533 m/s after one coasting step.
            BallHarness ball = new BallHarness();
            ball.Velocity = new Vector3(0f, 0f, 4f);
            Assert.IsTrue(ball.Step(0f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(3.893533f, ball.Velocity.z, Tolerance);

            // 8 m/s^2 removes 4 m/s in 0.5 s, so one second of coasting is more
            // than enough; the speed never rises and the velocity never turns
            // around, which is the "no oscillation" claim.
            float previous = ball.Speed();
            bool reachedZero = false;
            for (int i = 0; i < 120; i++)
            {
                Assert.IsTrue(ball.Step(0f, 0f, 0f, FixedStepSeconds));
                float current = ball.Speed();
                Assert.LessOrEqual(current, previous + 1e-6f);
                Assert.GreaterOrEqual(current, 0f);
                Assert.GreaterOrEqual(ball.Velocity.z, 0f);
                if (current == 0f) reachedZero = true;
                previous = current;
            }
            Assert.IsTrue(reachedZero, "braking must reach an exact stop");
            Assert.AreEqual(0f, ball.Speed(), 0f);

            // Rest is absorbing: more coasting keeps it at exactly zero.
            for (int i = 0; i < 30; i++) Assert.IsTrue(ball.Step(0f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(0f, ball.Speed(), 0f);
            Assert.AreEqual(0f, ball.Velocity.z, 0f);

            // A backward velocity decays the same way and never crosses into
            // forward motion.
            BallHarness back = new BallHarness();
            back.Velocity = new Vector3(0f, 0f, -4f);
            for (int i = 0; i < 120; i++)
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
        public void TurnFollowsTheGrowthScaledRateAndWraps()
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

            // Full growth derates the turn rate: 110 * (1 - 0.35) = 71.5 deg/s,
            // i.e. 0.5958333 deg per step.
            BallHarness grown = new BallHarness();
            grown.GrowthFraction = 1f;
            Assert.IsTrue(grown.Step(0f, 1f, 0f, FixedStepSeconds));
            Assert.AreEqual(0.5958333f, grown.HeadingDegrees, 1e-4f);

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
            // turn rate over a 0.5 s step is a full 90 deg turn, so the 2.5 m/s
            // this step adds must land on +X, not on the +Z it started facing.
            BallHarness turned = new BallHarness();
            turned.TurnRateDegreesPerSecond = 180f;
            Assert.IsTrue(turned.Step(1f, 1f, 0f, 0.5f));
            Assert.AreEqual(90f, turned.HeadingDegrees, 1e-4f);
            Assert.AreEqual(1.37203f, turned.Velocity.x, 1e-4f);   // 2.5 * exp(-0.6)
            Assert.AreEqual(0f, turned.Velocity.z, 1e-4f);
            Assert.AreEqual(0.686015f, turned.Displacement.x, 1e-4f);
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
            Assert.AreEqual(fast.Velocity.z * FixedStepSeconds, fast.Displacement.z, 0f);
            Assert.Greater(fast.Displacement.z, 0.6f);
            Assert.AreEqual(0f, fast.Velocity.y, 0f);

            // One 2.5 s step is not 2.5 s of stepping: (5 * 2.5) * exp(-3) =
            // 0.622338 m/s, far from the 4 m/s the same input reaches in 300
            // fixed steps. Sub-stepping belongs to the caller's clock.
            BallHarness coarse = new BallHarness();
            Assert.IsTrue(coarse.Step(1f, 0f, 0f, 2.5f));
            Assert.AreEqual(0.6223384f, coarse.Velocity.z, 1e-4f);
            Assert.Greater(Mathf.Abs(coarse.Velocity.z - first.Speed()), 1f);
        }

        [Test]
        public void InputsAndGrowthAreClampedToTheModelBounds()
        {
            // The growth term is clamped to [0, 1], so 2 behaves like 1 and -3
            // like 0 (the model reaches this term through a clamped radius).
            BallHarness atOne = new BallHarness();
            atOne.GrowthFraction = 1f;
            BallHarness above = new BallHarness();
            above.GrowthFraction = 2f;
            BallHarness atZero = new BallHarness();
            BallHarness below = new BallHarness();
            below.GrowthFraction = -3f;
            for (int i = 0; i < 60; i++)
            {
                Assert.IsTrue(atOne.Step(1f, 1f, 1f, FixedStepSeconds));
                Assert.IsTrue(above.Step(1f, 1f, 1f, FixedStepSeconds));
                Assert.IsTrue(atZero.Step(1f, 1f, 1f, FixedStepSeconds));
                Assert.IsTrue(below.Step(1f, 1f, 1f, FixedStepSeconds));
                Assert.AreEqual(atOne.Velocity.x, above.Velocity.x, 0f);
                Assert.AreEqual(atOne.Velocity.z, above.Velocity.z, 0f);
                Assert.AreEqual(atOne.HeadingDegrees, above.HeadingDegrees, 0f);
                Assert.AreEqual(atZero.Velocity.x, below.Velocity.x, 0f);
                Assert.AreEqual(atZero.Velocity.z, below.Velocity.z, 0f);
                Assert.AreEqual(atZero.HeadingDegrees, below.HeadingDegrees, 0f);
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

            // The lateral speed bound is an absolute clamp: a 3 m/s sideways
            // entry is cut to maxLateral = 1.2 m/s in one step.
            BallHarness sideways = new BallHarness();
            sideways.MaxSpeedMps = 20f;
            sideways.Velocity = new Vector3(3f, 0f, 0f);
            Assert.IsTrue(sideways.Step(1f, 0f, 0f, FixedStepSeconds));
            Assert.AreEqual(1.2f, sideways.Velocity.x, 1e-6f);

            // Held lateral assist settles at L*dt/(1-damp), i.e.
            // 1.2 * (1/120) / (1 - exp(-0.01)) = 1.00501 m/s, below the clamp.
            BallHarness assisted = new BallHarness();
            assisted.MaxSpeedMps = 20f;
            for (int i = 0; i < 1200; i++)
            {
                Assert.IsTrue(assisted.Step(1f, 0f, 1f, FixedStepSeconds));
                Assert.LessOrEqual(LateralSpeedOf(assisted.Velocity, 0f),
                    1.2f + Tolerance);
            }
            Assert.AreEqual(1.00501f, LateralSpeedOf(assisted.Velocity, 0f), 2e-3f);
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
                float.NaN, 0f, 0f, 0f, Vector3.zero, FixedStepSeconds, 0f,
                4f, 5f, 8f, 1.2f, 110f, 1.2f, 0.25f, 0.35f,
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

            // Configuration is validated: negative rates, a derating factor
            // outside [0, 1] and a non-finite tuning value are all refused.
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
            bad.TurnRateDegreesPerSecond = float.PositiveInfinity;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.MaxLateralMps = -1f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.GrowthSpeedFactor = 1.5f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.GrowthTurnFactor = -0.5f;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));
            bad = new BallHarness();
            bad.GrowthFraction = float.NaN;
            Assert.IsFalse(bad.Step(1f, 0f, 0f, FixedStepSeconds));

            // Finite inputs can still overflow an intermediate, and that is
            // refused rather than clamped. Accel * dt = 9e76 is infinite, and a
            // 3e38 m/s velocity over 100 s has no finite displacement.
            BallHarness accelerating = new BallHarness();
            accelerating.FrictionPerSecond = 0f;
            accelerating.AccelerationMps2 = 3e38f;
            Assert.IsFalse(accelerating.Step(1f, 0f, 0f, 3e38f));
            BallHarness longStep = new BallHarness();
            longStep.FrictionPerSecond = 0f;
            longStep.MaxSpeedMps = 3e38f;
            longStep.Velocity = new Vector3(0f, 0f, 3e38f);
            Assert.IsFalse(longStep.Step(0f, 0f, 0f, 100f));
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
                1f, 0f, 0f, 0f, new Vector3(0f, 9f, 0f), FixedStepSeconds, 0f,
                4f, 5f, 8f, 1.2f, 110f, 1.2f, 0.25f, 0.35f,
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
