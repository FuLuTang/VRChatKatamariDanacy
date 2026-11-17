using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_terrain_support/test_ramp_support.py.
    /// Lengths are metres in world space (+Y up). Expected values are hand
    /// calculations or geometric invariants, never a re-run of the implementation
    /// formula. The assembly layer (KatamariBallMotor) cannot be instantiated in
    /// EditMode, so every case here exercises SupportSurfaceMath, which is where
    /// the design puts the whole ramp decision (P3 of
    /// Docs/Design/BallMotor/TERRAIN_SUPPORT.md).
    ///
    /// Conventions used below: a ramp's normal is (0, cos(slope), -sin(slope)),
    /// so the downhill direction is +Z and the plane height falls as z grows;
    /// <c>center</c> is a point on the walking plane (TryBakeRamp's output), not
    /// the box centre.
    /// </summary>
    public class RampSupportTests
    {
        private const float Tolerance = 1e-5f;
        private const float MaxSlopeDegrees = 45f;

        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        // The same empty tables the P1/P2 tests pass.
        private static Vector3[] NoVectors()
        {
            return new Vector3[0];
        }

        private static float[] NoFloats()
        {
            return new float[0];
        }

        private static Vector3 RampNormal(float slopeDegrees)
        {
            float radians = slopeDegrees * Mathf.Deg2Rad;
            return new Vector3(0f, Mathf.Cos(radians), -Mathf.Sin(radians));
        }

        private sealed class Surface
        {
            public bool Ok;
            public int Id;
            public Vector3 Point;
            public Vector3 Normal;
            public float Height;
            public int Candidates;
        }

        // ---------------------------------------------------------------- 3 ----
        [Test]
        public void ARampSteeperThanTheLimitIsRefusedWithAReason()
        {
            Vector3 steep = RampNormal(58f);
            Vector3 unitNormal;
            Vector3 unitRight;
            float slope;
            int code;

            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, steep,
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalSlopeTooSteep, code);
            Assert.AreEqual(58f, slope, 0.01f, "the measured slope is reported with the refusal");
            Assert.AreEqual(0f, unitNormal.y, Tolerance, "a refusal writes no surface");
            Assert.AreEqual("", SupportSurfaceMath.RampRefusalReason(SupportSurfaceMath.RampRefusalNone));
            Assert.IsTrue(SupportSurfaceMath.RampRefusalReason(code).Length > 0);

            // 45.1 is over the rail; 44.9 is inside it. The equality case is avoided
            // because cos/sin round-trip through float would make a 45.0000x reading
            // ambiguous, and the rule is "> limit", not "== limit".
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(45.1f),
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalSlopeTooSteep, code);
            Assert.IsTrue(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(44.9f),
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalNone, code);
            Assert.AreEqual(44.9f, slope, 0.01f);

            // A different configured rail re-tunes the same predicate, and capacity is
            // the one ramp predicate the motor uses before it validates anything.
            Assert.IsTrue(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(58f),
                new Vector3(1f, 0f, 0f), 1f, 1f, 60f,
                out unitNormal, out unitRight, out slope, out code));
            Assert.IsTrue(SupportSurfaceMath.IsRampCountAllowed(4));
            Assert.IsFalse(SupportSurfaceMath.IsRampCountAllowed(5));
            Assert.IsFalse(SupportSurfaceMath.IsRampCountAllowed(-1));
        }

        // ---------------------------------------------------------------- 4 ----
        [Test]
        public void ARampWithANonUpwardNormalIsRefused()
        {
            Vector3 unitNormal;
            Vector3 unitRight;
            float slope;
            int code;

            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero,
                new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalNormalNotUpward, code);

            // The design's boundary is inclusive: n.y <= 0.05 is refused.
            Vector3 nearlyUp = new Vector3(0f, 0.05f, 0.99875f);
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, nearlyUp,
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalNormalNotUpward, code);

            // A flat plane has no downhill direction: it is a box top, not a ramp.
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, Up,
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalDegenerateAxis, code);

            // A right axis that projects to nothing horizontally is degenerate too.
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(30f),
                Up, 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalDegenerateAxis, code);
        }

        // ---------------------------------------------------------------- 5 ----
        [Test]
        public void BakeFromATransformedBoxYieldsTheExpectedPlane()
        {
            // The shipped board shape: 0.35 m wide, 0.04 m thick, 1.3984 m along the run.
            Vector3 size = new Vector3(0.35f, 0.04f, 1.3984f);
            Vector3 boxCenter = new Vector3(2f, 1f, 3f);
            Vector3 boxNormal = RampNormal(30f);
            Vector3 boxRight = new Vector3(1f, 0f, 0f);
            Vector3 walk;
            Vector3 normal;
            Vector3 right;
            float halfWidth;
            float halfRun;
            float slope;
            int code;

            Assert.IsTrue(SupportSurfaceMath.TryBakeRamp(boxCenter, boxNormal, boxRight,
                size, MaxSlopeDegrees, out walk, out normal, out right, out halfWidth,
                out halfRun, out slope, out code));

            // The walking face is the +Y face: half a thickness along the normal.
            Assert.AreEqual(boxCenter.x, walk.x, Tolerance);
            Assert.AreEqual(boxCenter.y + boxNormal.y * size.y * 0.5f, walk.y, Tolerance);
            Assert.AreEqual(boxCenter.z + boxNormal.z * size.y * 0.5f, walk.z, Tolerance);
            Assert.AreEqual(boxNormal.x, normal.x, Tolerance);
            Assert.AreEqual(boxNormal.y, normal.y, Tolerance);
            Assert.AreEqual(boxNormal.z, normal.z, Tolerance);
            Assert.AreEqual(0f, right.y, Tolerance, "the stored right is horizontal");
            Assert.AreEqual(1f, right.x, Tolerance);
            Assert.AreEqual(0.175f, halfWidth, Tolerance);
            Assert.AreEqual(size.z * 0.5f * Mathf.Cos(30f * Mathf.Deg2Rad), halfRun, Tolerance);
            Assert.AreEqual(30f, slope, 0.01f);

            // 45 degrees at a different non-unit scale.
            Vector3 steepSize = new Vector3(2f, 0.1f, 2f);
            Vector3 steepNormal = RampNormal(45f);
            Assert.IsTrue(SupportSurfaceMath.TryBakeRamp(new Vector3(-1f, 0.5f, 0.25f),
                steepNormal, boxRight, steepSize, MaxSlopeDegrees,
                out walk, out normal, out right, out halfWidth, out halfRun, out slope, out code));
            Assert.AreEqual(1f, halfWidth, Tolerance);
            Assert.AreEqual(1f * Mathf.Cos(45f * Mathf.Deg2Rad), halfRun, Tolerance);
            Assert.AreEqual(45f, slope, 0.01f);
            Assert.AreEqual(steepNormal.y, normal.y, Tolerance);

            // A box whose walking face is below the mid-plane would sink the ball by
            // half the thickness; the bake must never report the box centre.
            Assert.AreNotEqual(boxCenter.y, walk.y);
        }

        // ---------------------------------------------------------------- 6 ----
        [Test]
        public void HalfRunUsesTheHorizontalProjection()
        {
            // The shipped ramp_to_bed board: 10.7 degrees, 1.3984 m along the slope.
            float slopeDegrees = 10.7f;
            Vector3 size = new Vector3(0.35f, 0.04f, 1.3984f);
            Vector3 walk;
            Vector3 normal;
            Vector3 right;
            float halfWidth;
            float halfRun;
            float slope;
            int code;

            Assert.IsTrue(SupportSurfaceMath.TryBakeRamp(Vector3.zero, RampNormal(slopeDegrees),
                new Vector3(1f, 0f, 0f), size, MaxSlopeDegrees, out walk, out normal,
                out right, out halfWidth, out halfRun, out slope, out code));

            float tiltedHalfLength = size.z * 0.5f;
            float expected = tiltedHalfLength * Mathf.Cos(slopeDegrees * Mathf.Deg2Rad);
            Assert.AreEqual(expected, halfRun, Tolerance);
            Assert.Less(halfRun, tiltedHalfLength, "the horizontal run is shorter than the board");
            Assert.AreEqual(0.687f, halfRun, 0.001f);
        }

        // ---------------------------------------------------------------- 7 ----
        [Test]
        public void CapacityRefusesTheFifthRampAndKeepsThePreviousFour()
        {
            Assert.IsTrue(SupportSurfaceMath.IsRampCountAllowed(4));
            Assert.IsFalse(SupportSurfaceMath.IsRampCountAllowed(5));
        }

        // --------------------------------------------------------------- 12 ----
        [Test]
        public void RampSurfaceIdsNeverCollideWithBoxOrPropIds()
        {
            Assert.IsTrue(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId));
            Assert.IsTrue(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId + SupportSurfaceMath.MaxRampCount - 1));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId - 1));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId + SupportSurfaceMath.MaxRampCount));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(SupportSurfaceMath.GroundSurfaceId));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(1));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstPropSurfaceId));
            Assert.AreEqual(0, SupportSurfaceMath.RampSurfaceIndex(
                SupportSurfaceMath.FirstRampSurfaceId));
            Assert.AreEqual(3, SupportSurfaceMath.RampSurfaceIndex(
                SupportSurfaceMath.FirstRampSurfaceId + 3));
            Assert.AreEqual(-1, SupportSurfaceMath.RampSurfaceIndex(1));
        }

        // --------------------------------------------------------------- 13 ----
        [Test]
        public void NonFiniteRampInputIsRefusedAndNothingIsWritten()
        {
            Vector3 unitNormal;
            Vector3 unitRight;
            float slope;
            int code;

            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(
                new Vector3(float.NaN, 0f, 0f), RampNormal(30f), new Vector3(1f, 0f, 0f),
                1f, 1f, MaxSlopeDegrees, out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalInvalidInput, code);
            Assert.AreEqual(0f, unitNormal.y, Tolerance);
            Assert.AreEqual(0f, unitRight.x, Tolerance);

            Assert.IsFalse(SupportSurfaceMath.TryBakeRamp(Vector3.zero, RampNormal(30f),
                new Vector3(1f, 0f, 0f), new Vector3(0.35f, float.NaN, 1.4f),
                MaxSlopeDegrees, out Vector3 walk, out Vector3 normal, out Vector3 right,
                out float halfWidth, out float halfRun, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalInvalidInput, code);
            Assert.AreEqual(Vector3.zero, walk);
            Assert.AreEqual(0f, halfWidth, Tolerance);
            Assert.AreEqual(0f, halfRun, Tolerance);
        }

    }
}
