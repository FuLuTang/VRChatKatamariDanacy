using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of the curved-ramp cases of
    /// Docs/tests/ball_contact_v2/test_contact_v2.py: the chord budget, the orthonormal
    /// segment axes with an overlapping chain, and a ball crossing a shallow curve.
    ///
    /// The budget is the design's: sagitta at most 5% of the smallest ball radius
    /// (0.05 * 0.15 = 7.5 mm). Expected numbers come from the arc geometry, not from the
    /// implementation: delta = arc/segments, sagitta = R(1 - cos(delta/2)).
    /// </summary>
    public class CurveRampTests
    {
        private const float Tolerance = 1e-5f;

        private static readonly Vector3 Origin = new Vector3(0f, 0f, 7f);
        private static readonly Vector3 Forward = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        private static bool Tessellate(float radiusM, float arcDeg, int segments, float widthM,
            float thicknessM, ContactWorld world)
        {
            float deltaDeg;
            float sagittaM;
            float normalJumpDeg;
            float segmentLengthM;
            float overlap;
            bool ok = CurveRampMath.TryTessellateVerticalCurve(Origin, Forward, Up, radiusM,
                arcDeg, segments, widthM, thicknessM, world.Centers, world.AxisX, world.AxisY,
                world.AxisZ, world.HalfExtents, world.SurfaceIds, world.Roles, out deltaDeg,
                out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap);
            if (ok) world.Count = segments;
            return ok;
        }

        private static float Sagitta(float radiusM, float arcDeg, int segments)
        {
            float delta = arcDeg * Mathf.Deg2Rad / segments;
            return radiusM * (1f - Mathf.Cos(delta * 0.5f));
        }

        [Test]
        public void TwelveSegmentsPer90DegreesMeetsTheChordBudget()
        {
            const int Count = 12;
            Vector3[] centers = new Vector3[Count];
            Vector3[] axisX = new Vector3[Count];
            Vector3[] axisY = new Vector3[Count];
            Vector3[] axisZ = new Vector3[Count];
            Vector3[] halfExtents = new Vector3[Count];
            int[] surfaceIds = new int[Count];
            int[] roles = new int[Count];
            float deltaDeg;
            float sagittaM;
            float normalJumpDeg;
            float segmentLengthM;
            float overlap;
            Assert.IsTrue(CurveRampMath.TryTessellateVerticalCurve(Origin, Forward, Up, 3f, 90f,
                Count, 4f, 0.1f, centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles,
                out deltaDeg, out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));

            Assert.AreEqual(7.5f, deltaDeg, Tolerance);
            Assert.AreEqual(3.75f, normalJumpDeg, Tolerance);
            Assert.AreEqual(Sagitta(3f, 90f, Count), sagittaM, Tolerance);
            Assert.LessOrEqual(sagittaM, 0.05f * 0.15f);  // 5% of the smallest r
            Assert.IsTrue(CurveRampMath.IsCurveWithinChordBudget(3f, 12, 90f, 0.15f));
            Assert.IsFalse(CurveRampMath.IsCurveWithinChordBudget(3f, 4, 90f, 0.15f));
            // No crack on the convex side: each segment is longer than its chord.
            Assert.Greater(overlap, 1f);
            Assert.AreEqual(200, surfaceIds[0]);
            Assert.AreEqual(211, surfaceIds[11]);
            Assert.AreEqual(ContactCodes.RoleSlab, roles[0]);
        }

        [Test]
        public void CurveAxesAreOrthonormalAndTheChainIsContinuous()
        {
            const int Count = 8;
            Vector3[] centers = new Vector3[Count];
            Vector3[] axisX = new Vector3[Count];
            Vector3[] axisY = new Vector3[Count];
            Vector3[] axisZ = new Vector3[Count];
            Vector3[] halfExtents = new Vector3[Count];
            int[] surfaceIds = new int[Count];
            int[] roles = new int[Count];
            float deltaDeg;
            float sagittaM;
            float normalJumpDeg;
            float segmentLengthM;
            float overlap;
            Assert.IsTrue(CurveRampMath.TryTessellateVerticalCurve(Origin, Forward, Up, 5f, 45f,
                Count, 3f, 0.2f, centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles,
                out deltaDeg, out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));

            for (int i = 0; i < Count; i++)
            {
                Assert.AreEqual(0f, Vector3.Dot(axisX[i], axisY[i]), 1e-5f);
                Assert.AreEqual(0f, Vector3.Dot(axisX[i], axisZ[i]), 1e-5f);
                Assert.AreEqual(0f, Vector3.Dot(axisY[i], axisZ[i]), 1e-5f);
                Assert.AreEqual(1f, axisY[i].magnitude, 1e-5f);
            }
            for (int i = 1; i < Count; i++)
            {
                float gap = (centers[i] - centers[i - 1]).magnitude;
                // Segment centres are one chord apart; the overlap must cover the joint.
                Assert.Less(gap, 2f * Mathf.Max(halfExtents[i].z, halfExtents[i - 1].z));
            }
        }

        [Test]
        public void AShortBufferOrADegenerateCurveIsRefused()
        {
            // 12 segments into 4 slots: refused, never truncated.
            Vector3[] centers = new Vector3[4];
            Vector3[] axisX = new Vector3[4];
            Vector3[] axisY = new Vector3[4];
            Vector3[] axisZ = new Vector3[4];
            Vector3[] halfExtents = new Vector3[4];
            int[] surfaceIds = new int[4];
            int[] roles = new int[4];
            float deltaDeg;
            float sagittaM;
            float normalJumpDeg;
            float segmentLengthM;
            float overlap;
            Assert.IsFalse(CurveRampMath.TryTessellateVerticalCurve(Origin, Forward, Up, 3f, 90f,
                12, 4f, 0.1f, centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles,
                out deltaDeg, out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));
            // forward parallel to up has no side axis.
            Assert.IsFalse(CurveRampMath.TryTessellateVerticalCurve(Origin, Up, Up, 3f, 90f, 4,
                4f, 0.1f, centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles,
                out deltaDeg, out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));
            // Zero segments and a non-positive radius are not curves.
            Assert.IsFalse(CurveRampMath.TryTessellateVerticalCurve(Origin, Forward, Up, 3f, 90f,
                0, 4f, 0.1f, centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles,
                out deltaDeg, out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));
            Assert.IsFalse(CurveRampMath.TryTessellateVerticalCurve(Origin, Forward, Up, 0f, 90f,
                4, 4f, 0.1f, centers, axisX, axisY, axisZ, halfExtents, surfaceIds, roles,
                out deltaDeg, out sagittaM, out normalJumpDeg, out segmentLengthM, out overlap));
        }

        [Test]
        public void ABallTraversesAShallowCurveWithoutStalling()
        {
            ContactWorld world = new ContactWorld();
            Assert.IsTrue(Tessellate(10f, 30f, 8, 3f, 0.2f, world));
            ContactBall ball = new ContactBall();
            ball.Center = new Vector3(0f, 0.15f, 7f);
            ball.Radius = 0.15f;
            ball.PlanarVelocity = new Vector3(4f, 0f, 0f);
            float highest = 0f;
            for (int i = 0; i < 90; i++)
            {
                Assert.IsTrue(ball.Step(world, new Vector3(1f, 0f, 0f), 4f, 0.02f, 5f));
                if (ball.Center.y > highest) highest = ball.Center.y;
            }
            // 30 deg over R = 10 rises 1.34 m; the centre rides r * cos 30 above it.
            Assert.Greater(highest, 1.40f);
            Assert.Greater(ball.Center.x, 4f);
        }
    }
}
