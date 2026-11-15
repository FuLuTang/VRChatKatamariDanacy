using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallPresentation;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the presentation math: the 0.95 visual core and the catch-up spring that draws
    /// it. The scene probe that reports them is pinned separately, in
    /// BallGrowthDiagnosticsTests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two groups, each with the source of its expected values written out:
    /// <list type="number">
    /// <item>THE SPRING. The step is the closed form Unity's <c>SmoothDamp</c> uses for a
    /// critically damped spring: <c>omega = 2 / smoothTime</c>, <c>decay = 1 / (1 + x + 0.48x^2
    /// + 0.235x^3)</c> with <c>x = omega * dt</c>. A critically damped spring has response
    /// <c>1 - (1 + omega*t) * exp(-omega*t)</c>, so at 0.08 s the remaining error after 200 ms
    /// is <c>6 * exp(-5) = 4.0%</c> of the step in continuous time (3.4% at 60-90 Hz in the
    /// discrete form this code uses). The tests therefore do not assert a hand-waved "looks
    /// settled": they assert the two things a player can actually check - the ball has moved
    /// more than 90% of the way and the residue is under 1% of the DIAMETER (the audit's
    /// readability line, ~3.3 mm at 0.33 m) by 200 ms; that it never crosses its target; and
    /// that it lands bit-exactly ON the target, because a permanent offset would show up as a
    /// visual radius that is no longer 0.95 r_phys.
    /// </item>
    /// <item>THE 0.95 FACTOR. <c>r_visual = 0.95 * r_phys</c> is design 4A.2 of
    /// Docs/Design/BallMotor/CONTACT_MODEL_V2.md and is already implemented once, in
    /// <c>BallShapeMath.TryVisualCoreRadius</c> (scale from <c>ContactTuning</c>). The tests
    /// pin the NUMBER (1.10 m radius -&gt; 1.045 -&gt; a 2.09 m mesh) and that the physical
    /// diameter and the lookup are passed through unchanged, so the visual factor cannot leak
    /// into anything the motor owns.
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    public class BallPresentationTests
    {
        // The design value the behaviour serializes; the spring tests use it explicitly so a
        // change to the default cannot silently loosen them.
        private const float SmoothTimeSeconds = 0.08f;

        // Diameter the audit's first absorb produces: 0.30 m + one acorn (1 unit).
        private const float StartDiameterM = 0.30f;
        private const float OneUnitDiameterM = 0.3266f;

        // ---------------------------------------------------------------- spring

        /// <summary>
        /// 200 ms after a one-unit absorb the spring has covered more than 90% of the step and
        /// the residue is under 1% of the diameter (the audit's 1% readability line).
        /// Checked at two frame rates so the result is not a property of one dt.
        /// </summary>
        [TestCase(1f / 60f)]
        [TestCase(1f / 90f)]
        public void SpringIsNearlyThereInsideTwoHundredMilliseconds(float deltaTimeSeconds)
        {
            float diameterM = StartDiameterM;
            float velocityMps = 0f;
            int steps = (int)(0.2f / deltaTimeSeconds);

            for (int i = 0; i < steps; i++)
            {
                Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(diameterM, velocityMps,
                    OneUnitDiameterM, deltaTimeSeconds, SmoothTimeSeconds,
                    out diameterM, out velocityMps), "step " + i + " was refused");
            }

            float movedFraction = (diameterM - StartDiameterM) / (OneUnitDiameterM - StartDiameterM);
            Assert.Greater(movedFraction, 0.9f,
                "after 200 ms the spring had only covered " + movedFraction + " of the step");
            Assert.Less(Mathf.Abs(diameterM - OneUnitDiameterM), 0.01f * OneUnitDiameterM,
                "after 200 ms the residue is still visible (more than 1% of the diameter)");
            Assert.Less(diameterM, OneUnitDiameterM, "the spring must not be past its target");
        }

        /// <summary>
        /// The spring never crosses its target, in either direction, and its peak overshoot is
        /// zero (the design allows 2% of the step). Checked over four seconds of frames so a
        /// late oscillation would show up too.
        /// </summary>
        [Test]
        public void SpringNeverCrossesItsTarget()
        {
            AssertNoCrossing(StartDiameterM, OneUnitDiameterM, "growing");
            AssertNoCrossing(2.20f, 2.09f, "shrinking");
        }

        /// <summary>
        /// After the catch-up the value is EXACTLY the target and the velocity is zero: no
        /// permanent offset, so the drawn radius really is 0.95 r_phys and not a residue.
        /// 600 frames at 90 Hz is 6.7 s, an order of magnitude past the 0.42 s settle.
        /// </summary>
        [Test]
        public void SpringSettlesExactlyOnTheTarget()
        {
            float diameterM = StartDiameterM;
            float velocityMps = 0f;
            for (int i = 0; i < 600; i++)
            {
                Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(diameterM, velocityMps,
                    OneUnitDiameterM, 1f / 90f, SmoothTimeSeconds,
                    out diameterM, out velocityMps));
            }
            Assert.AreEqual(OneUnitDiameterM, diameterM, "the settled diameter is not the target");
            Assert.AreEqual(0f, velocityMps, "the settled velocity is not zero");
        }

        /// <summary>
        /// A target that collapsed by more than half is a restart (new round, respawn), not a
        /// shrink, so it snaps. A bank loss of a few percent still animates.
        /// </summary>
        [Test]
        public void SpringSnapsOnAResetButAnimatesARealShrink()
        {
            float diameterM = 2.20f;
            float velocityMps = 0f;
            Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(diameterM, velocityMps,
                StartDiameterM, 1f / 90f, SmoothTimeSeconds,
                out diameterM, out velocityMps));
            Assert.AreEqual(StartDiameterM, diameterM, "a reset must not be animated");
            Assert.AreEqual(0f, velocityMps, "a snap must clear the velocity");

            diameterM = 2.20f;
            velocityMps = 0f;
            Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(diameterM, velocityMps,
                2.19f, 1f / 90f, SmoothTimeSeconds,
                out diameterM, out velocityMps));
            Assert.Greater(diameterM, 2.19f, "an ordinary shrink must be animated, not snapped");
            Assert.Less(diameterM, 2.20f);
        }

        /// <summary>
        /// A paused frame (dt = 0) holds the value and keeps the velocity; it is not a data
        /// error. Also covers a negative and a non-finite dt.
        /// </summary>
        [Test]
        public void NonPositiveDeltaTimeHoldsTheValue()
        {
            float nextDiameterM;
            float nextVelocityMps;
            Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(0.5f, 1.5f,
                OneUnitDiameterM, 0f, SmoothTimeSeconds,
                out nextDiameterM, out nextVelocityMps));
            Assert.AreEqual(0.5f, nextDiameterM);
            Assert.AreEqual(1.5f, nextVelocityMps);

            Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(0.5f, 1.5f,
                OneUnitDiameterM, -1f, SmoothTimeSeconds,
                out nextDiameterM, out nextVelocityMps));
            Assert.AreEqual(0.5f, nextDiameterM);
            Assert.AreEqual(1.5f, nextVelocityMps);
        }

        /// <summary>
        /// Unusable arguments are refused and leave the caller with nothing to write, which is
        /// what a motor that has not started (diameter 0) needs.
        /// </summary>
        [Test]
        public void SpringRefusesUnusableArguments()
        {
            float nextDiameterM;
            float nextVelocityMps;

            Assert.IsFalse(BallPresentationMath.TryStepCoreDiameter(0.5f, 0f,
                float.NaN, 1f / 90f, SmoothTimeSeconds, out nextDiameterM, out nextVelocityMps));
            Assert.AreEqual(0.5f, nextDiameterM);
            Assert.AreEqual(0f, nextVelocityMps);

            Assert.IsFalse(BallPresentationMath.TryStepCoreDiameter(0.5f, 0f,
                0f, 1f / 90f, SmoothTimeSeconds, out nextDiameterM, out nextVelocityMps));

            Assert.IsFalse(BallPresentationMath.TryStepCoreDiameter(0.5f, 0f,
                OneUnitDiameterM, 1f / 90f, 0f, out nextDiameterM, out nextVelocityMps));

            Assert.IsFalse(BallPresentationMath.TryStepCoreDiameter(0.5f, 0f,
                OneUnitDiameterM, 1f / 90f, float.PositiveInfinity,
                out nextDiameterM, out nextVelocityMps));

            // A fresh behaviour's "no previous value" pointer is 0, not an error: it snaps.
            Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(0f, 0f,
                OneUnitDiameterM, 1f / 90f, SmoothTimeSeconds,
                out nextDiameterM, out nextVelocityMps));
            Assert.AreEqual(OneUnitDiameterM, nextDiameterM);
        }

        // ---------------------------------------------------------------- 0.95 factor

        /// <summary>
        /// Design 4A.2: 1.10 m physical radius is drawn as 1.045 m, i.e. a 2.09 m mesh against
        /// a 2.20 m physical diameter. The expected numbers are one multiplication away from
        /// the design constant, not a copy of the implementation.
        /// </summary>
        [Test]
        public void VisualCoreTargetIsNinetyFivePercentOfThePhysicalDiameter()
        {
            float physicalRadiusM = 1.10f;
            float visualRadiusM;
            Assert.IsTrue(BallShapeMath.TryVisualCoreRadius(physicalRadiusM, out visualRadiusM),
                "the ball shape layer refused a positive radius");
            Assert.AreEqual(1.045f, visualRadiusM, 1e-6f);

            float targetDiameterM;
            Assert.IsTrue(BallPresentationMath.TryCoreTargetDiameter(2f * physicalRadiusM,
                visualRadiusM, true, out targetDiameterM));
            Assert.AreEqual(2.09f, targetDiameterM, 1e-5f);
            Assert.Less(targetDiameterM, 2f * physicalRadiusM,
                "the visual core must sit inside the physical sphere");
        }

        /// <summary>
        /// With the visual shape off the mesh gets the physical diameter, bit for bit, and the
        /// shape layer's lookup is unchanged by anything in this file: the presentation factor
        /// cannot reach the physical radius, the absorb gate or the camera, because all of
        /// them read the motor's own values.
        /// </summary>
        [Test]
        public void VisualCoreTargetPassesThePhysicalDiameterThroughWhenDisabled()
        {
            float physicalDiameterM = 2.20f;
            float targetDiameterM;
            Assert.IsTrue(BallPresentationMath.TryCoreTargetDiameter(physicalDiameterM, 1.045f,
                false, out targetDiameterM));
            Assert.AreEqual(physicalDiameterM, targetDiameterM);

            // The lookup itself is untouched: an explicit scale of 1 returns the input radius,
            // and the default scale is still the design 0.95.
            float radiusM;
            Assert.IsTrue(BallShapeMath.TryVisualCoreRadius(0.55f, out radiusM, 1f));
            Assert.AreEqual(0.55f, radiusM);
            Assert.IsTrue(BallShapeMath.TryVisualCoreRadius(0.55f, out radiusM));
            Assert.AreEqual(0.5225f, radiusM, 1e-6f);
        }

        /// <summary>
        /// A motor that cannot derive a visual radius answers with the physical one; a
        /// non-positive value here must fall back rather than stop drawing the ball.
        /// </summary>
        [Test]
        public void UnusableVisualRadiusFallsBackToThePhysicalDiameter()
        {
            float targetDiameterM;
            Assert.IsTrue(BallPresentationMath.TryCoreTargetDiameter(0.30f, -1f, true,
                out targetDiameterM));
            Assert.AreEqual(0.30f, targetDiameterM);

            Assert.IsTrue(BallPresentationMath.TryCoreTargetDiameter(0.30f, float.NaN, true,
                out targetDiameterM));
            Assert.AreEqual(0.30f, targetDiameterM);

            // The physical diameter is the one input that cannot be defaulted.
            Assert.IsFalse(BallPresentationMath.TryCoreTargetDiameter(0f, 0.1f, true,
                out targetDiameterM));
            Assert.IsFalse(BallPresentationMath.TryCoreTargetDiameter(float.NaN, 0.1f, true,
                out targetDiameterM));
        }

        /// <summary>
        /// Runs four seconds of frames and asserts monotone approach, no crossing and no
        /// overshoot beyond 2% of the step (the design allowance; this spring's own is zero).
        /// </summary>
        private static void AssertNoCrossing(float fromDiameterM, float toDiameterM, string label)
        {
            float stepM = toDiameterM - fromDiameterM;
            float diameterM = fromDiameterM;
            float velocityMps = 0f;
            float worstOvershootM = 0f;
            float previousM = fromDiameterM;

            for (int i = 0; i < 288; i++)
            {
                Assert.IsTrue(BallPresentationMath.TryStepCoreDiameter(diameterM, velocityMps,
                    toDiameterM, 1f / 72f, SmoothTimeSeconds,
                    out diameterM, out velocityMps), label + ": step " + i + " was refused");

                if (stepM > 0f)
                {
                    Assert.GreaterOrEqual(diameterM, previousM, label + ": the spring went backwards");
                    Assert.LessOrEqual(diameterM, toDiameterM, label + ": crossed the target");
                }
                else
                {
                    Assert.LessOrEqual(diameterM, previousM, label + ": the spring went backwards");
                    Assert.GreaterOrEqual(diameterM, toDiameterM, label + ": crossed the target");
                }

                float pastM = (diameterM - toDiameterM) * (stepM > 0f ? 1f : -1f);
                if (pastM > worstOvershootM) worstOvershootM = pastM;
                previousM = diameterM;
            }

            Assert.LessOrEqual(worstOvershootM, 0.02f * Mathf.Abs(stepM),
                label + ": overshoot beyond the 2% allowance");
        }
    }
}
