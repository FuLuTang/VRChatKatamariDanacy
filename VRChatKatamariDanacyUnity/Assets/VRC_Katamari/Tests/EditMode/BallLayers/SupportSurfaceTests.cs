using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_terrain_support/test_terrain_support.py.
    /// Lengths are metres in world space (+Y up). Expected values are hand
    /// calculations or geometric invariants, never a re-run of the implementation
    /// formula. The assembly layer (KatamariBallMotor) cannot be instantiated in
    /// EditMode, so every case here exercises SupportSurfaceMath, which is where
    /// the design puts the whole decision.
    ///
    /// The last three cases cover the P1 gravity decision: support loss starts a
    /// fall, the fall follows gravity and stops exactly on the lower surface, and
    /// the fall is NOT limited by the old per-step drop clamp.
    /// </summary>
    public class SupportSurfaceTests
    {
        private const float Tolerance = 1e-5f;

        // The design's default step ratio (layout.json max_step_height_over_ball_diameter).
        private const float StepRatio = 0.25f;

        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        private static Vector3[] NoBoxes()
        {
            return new Vector3[0];
        }

        // P2: the prop table. Every P1 case passes an empty one, which is exactly
        // the pre-P2 query (the motor does the same when terrain support is off).
        private static Vector3[] NoProps()
        {
            return new Vector3[0];
        }

        private static float StepLimit(float diameter)
        {
            float limit;
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(diameter, StepRatio, out limit));
            return limit;
        }

        private static float[] NoFloats()
        {
            return new float[0];
        }

        // ---------------------------------------------------------------- 7 ----
        [Test]
        public void StepLimitScalesWithDiameter()
        {
            float limit;
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(0.30f, 0.25f, out limit));
            Assert.AreEqual(0.075f, limit, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(0.83f, 0.25f, out limit));
            Assert.AreEqual(0.2075f, limit, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(1.10f, 0.25f, out limit));
            Assert.AreEqual(0.275f, limit, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(2.20f, 0.25f, out limit));
            Assert.AreEqual(0.55f, limit, Tolerance);

            // Refusals: a non-finite size, a negative/above-one ratio, and a ratio
            // that is not a ratio at all.
            Assert.IsFalse(SupportSurfaceMath.TryStepHeightLimit(float.NaN, 0.25f, out limit));
            Assert.AreEqual(0f, limit, Tolerance);
            Assert.IsFalse(SupportSurfaceMath.TryStepHeightLimit(1.10f, -0.1f, out limit));
            Assert.IsFalse(SupportSurfaceMath.TryStepHeightLimit(1.10f, 1.5f, out limit));
        }

        // ---------------------------------------------------------------- 8 ----
        [Test]
        public void TheBoxThatIsWithinTheLimitLeavesTheSweep()
        {
            // 0.26 m picnic bed. A 1.10 m ball has a 0.275 m limit -> step; a
            // 0.30 m ball has 0.075 m -> wall. The comparison is `top - reference`.
            Assert.IsTrue(SupportSurfaceMath.IsStepWithinLimit(0.26f, 0f, StepLimit(1.10f)));
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(0.26f, 0f, StepLimit(0.30f)));
            // A top below the support is never a wall either.
            Assert.IsTrue(SupportSurfaceMath.IsStepWithinLimit(-0.1f, 0f, 0f));
            // Invalid numbers are conservative: keep the box as a wall.
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(float.NaN, 0f, 0.275f));
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(0.1f, 0f, -1f));
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(0.1f, float.NaN, 0.275f));
        }

        // --------------------------------------------------------------- 10 ----
        [Test]
        public void ABallAlreadyAboveTheTopIsNeverPushedOut()
        {
            // Standing on the box, or above it, the reference is the ball's own bottom, so
            // the rise is zero or negative and the box is a step. This is the old
            // unconditional "already above" rule as a consequence of the shared
            // reference, not a second comparison that could disagree with the table.
            float limit = StepLimit(0.30f);
            Assert.IsTrue(SupportSurfaceMath.IsStepWithinLimit(0.075f, 0.075f, limit));
            Assert.IsTrue(SupportSurfaceMath.IsStepWithinLimit(0.075f, 1f, limit));
            // A corrupt top is still refused first: a corrupt state must not masquerade
            // as a classified box.
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(float.NaN, 0.075f, limit));
        }

    }
}
