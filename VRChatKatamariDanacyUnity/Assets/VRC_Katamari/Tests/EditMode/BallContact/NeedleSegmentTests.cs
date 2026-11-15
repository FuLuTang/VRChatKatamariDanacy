using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the needle-tip SWEEP (CONTACT_MODEL_V2.md §4A.3) against the reference
    /// model in <c>Docs/Design/BallMotor/contact_v2/shapes.py</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Python side is tested by <c>Docs/tests/ball_contact_v2/test_segment_sweep.py</c>
    /// with the same fixtures; this file pins the C# port to the same geometry. Where a
    /// number is bisected (the edge/corner pass) the tolerance is 1e-3 of the interval,
    /// which is the 12-iteration resolution the implementation documents; the face,
    /// inside and plane cases are exact to float noise and use 1e-5.
    /// </para>
    /// <para>
    /// The point of the sweep is the thin-wall case: a 0.32 m sweep crosses a 0.02 m
    /// wall that neither endpoint's point test sees. That case is the reason the
    /// production solver may not use <see cref="BallShapeMath.TryNeedleTipContact"/>
    /// alone.
    /// </para>
    /// </remarks>
    public class NeedleSegmentTests
    {
        private static readonly Vector3 Origin = Vector3.zero;
        private static readonly Vector3 AxisX = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 AxisY = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 AxisZ = new Vector3(0f, 0f, 1f);
        private static readonly Vector3 Half = new Vector3(0.5f, 0.5f, 0.5f);
        private const float TipRadius = 0.01f;

        private static bool Sweep(Vector3 previous, Vector3 now, out Vector3 normal,
            out float depth, out Vector3 point, out float fraction)
        {
            return BallShapeMath.TryNeedleSegmentContact(Origin, AxisX, AxisY, AxisZ, Half,
                previous, now, TipRadius, out normal, out depth, out point, out fraction);
        }

        [Test]
        public void AFaceEntryIsFoundAtTheInflatedFace()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            // From x = 2 to x = 0 along -x: the tip (r = 0.01) first touches x = 0.51.
            Assert.IsTrue(Sweep(new Vector3(2f, 0f, 0f), Vector3.zero, out normal,
                out depth, out point, out fraction), "a head-on face sweep must hit");
            Assert.AreEqual(1f, normal.x, 1e-5f);
            Assert.AreEqual(0f, normal.y, 1e-5f);
            Assert.AreEqual(0f, depth, 1e-5f);
            Assert.AreEqual(0.5f, point.x, 1e-5f);
            Assert.AreEqual(0.745f, fraction, 1e-5f);
        }

        [Test]
        public void ATopFaceEntryUsesTheFaceNormal()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsTrue(Sweep(new Vector3(0.5f, 0.5f, 2f), new Vector3(0.5f, 0.5f, 0f),
                out normal, out depth, out point, out fraction), "a downward sweep must hit");
            Assert.AreEqual(1f, normal.z, 1e-5f);
            Assert.AreEqual(0.5f, point.z, 1e-5f);
            Assert.AreEqual(0.745f, fraction, 1e-5f);
            Assert.AreEqual(0f, depth, 1e-5f);
        }

        [Test]
        public void ACornerPassReportsTheCornerNormal()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            // The body diagonal: the entry sample sits at the inflated corner
            // (sqrt(3) r from the real corner, no contact), the next sample is inside,
            // so the sweep bisects to the first touch at k = 0.5 + r/sqrt(3).
            Assert.IsTrue(Sweep(new Vector3(1f, 1f, 1f), new Vector3(-1f, -1f, -1f),
                out normal, out depth, out point, out fraction), "a corner pass must hit");
            float expected = (1f - (0.5f + TipRadius / Mathf.Sqrt(3f))) / 2f;
            Assert.AreEqual(expected, fraction, 1e-3f);
            Assert.Less(depth, 1e-4f);
            float third = 1f / Mathf.Sqrt(3f);
            Assert.AreEqual(third, normal.x, 1e-4f);
            Assert.AreEqual(third, normal.y, 1e-4f);
            Assert.AreEqual(third, normal.z, 1e-4f);
            Assert.AreEqual(0.5f, point.x, 1e-4f);
            Assert.AreEqual(0.5f, point.y, 1e-4f);
            Assert.AreEqual(0.5f, point.z, 1e-4f);
        }

        [Test]
        public void APassOutsideTheBoxIsNotAContact()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsFalse(Sweep(new Vector3(2f, 2f, 2f), new Vector3(2f, 2f, 0f),
                out normal, out depth, out point, out fraction));
        }

        [Test]
        public void ATipThatStartsInsideIsPushedOutThroughTheNearestFace()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsTrue(Sweep(Vector3.zero, Vector3.zero, out normal, out depth,
                out point, out fraction), "a centre exactly inside is a contact");
            Assert.AreEqual(0f, fraction, 0f);
            Assert.AreEqual(1f, normal.x, 1e-6f);
            Assert.AreEqual(TipRadius + 0.5f, depth, 1e-5f);
            Assert.AreEqual(0.5f, point.x, 1e-5f);
        }

        [Test]
        public void AStillTipMatchesThePointTest()
        {
            Vector3[] tips =
            {
                new Vector3(0.505f, 0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.6f),
                Vector3.zero,
                new Vector3(0.51f, 0.51f, 0.5f),
                new Vector3(2f, 0f, 0f),
            };
            for (int i = 0; i < tips.Length; i++)
            {
                Vector3 sweptNormal;
                float sweptDepth;
                Vector3 sweptPoint;
                float fraction;
                bool swept = Sweep(tips[i], tips[i], out sweptNormal, out sweptDepth,
                    out sweptPoint, out fraction);
                Vector3 staticNormal;
                float staticDepth;
                Vector3 staticPoint;
                bool stat = BallShapeMath.TryNeedleTipContact(Origin, AxisX, AxisY, AxisZ,
                    Half, tips[i], TipRadius, out staticNormal, out staticDepth,
                    out staticPoint);
                Assert.AreEqual(stat, swept, "still tip at index " + i);
                if (!stat) continue;
                Assert.AreEqual(0f, fraction, 0f, "a still tip reports its own position");
                Assert.AreEqual(staticDepth, sweptDepth, 1e-5f, "depth at index " + i);
                Assert.AreEqual(staticNormal.x, sweptNormal.x, 1e-5f, "nx at index " + i);
                Assert.AreEqual(staticNormal.y, sweptNormal.y, 1e-5f, "ny at index " + i);
                Assert.AreEqual(staticNormal.z, sweptNormal.z, 1e-5f, "nz at index " + i);
                Assert.AreEqual(staticPoint.x, sweptPoint.x, 1e-5f, "px at index " + i);
                Assert.AreEqual(staticPoint.y, sweptPoint.y, 1e-5f, "py at index " + i);
                Assert.AreEqual(staticPoint.z, sweptPoint.z, 1e-5f, "pz at index " + i);
            }
        }

        [Test]
        public void AStillTipJustOutsideTheSkinIsNotAContact()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Vector3 far = new Vector3(0.52f, 0.5f, 0.5f);
            Assert.IsFalse(Sweep(far, far, out normal, out depth, out point, out fraction));
            Vector3 near = new Vector3(0.505f, 0.5f, 0.5f);
            Assert.IsTrue(Sweep(near, near, out normal, out depth, out point, out fraction));
            Assert.AreEqual(1f, normal.x, 1e-6f);
            Assert.AreEqual(0.005f, depth, 1e-5f);
        }

        [Test]
        public void ARotatedBoxIsSweptInItsOwnFrame()
        {
            float half = 1f / Mathf.Sqrt(2f);
            Vector3 axisX = new Vector3(half, 0f, -half);
            Vector3 axisZ = new Vector3(half, 0f, half);
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsTrue(BallShapeMath.TryNeedleSegmentContact(Origin, axisX, AxisY,
                axisZ, Half, new Vector3(1.5f, 0f, 0f), new Vector3(-1.5f, 0f, 0f),
                TipRadius, out normal, out depth, out point, out fraction),
                "the world +x extreme is the rotated corner, so the sweep must hit it");
            float expected = (1.5f - (half + TipRadius)) / 3f;
            Assert.AreEqual(expected, fraction, 1e-3f);
            Assert.AreEqual(1f, normal.x, 1e-4f);
            Assert.AreEqual(0f, depth, 1e-4f);
            Assert.AreEqual(half, point.x, 1e-4f);
        }

        [Test]
        public void ThePlaneSweepFindsTheEntryAtTipRadius()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsTrue(BallShapeMath.TryNeedleSegmentPlaneContact(new Vector3(0f, 1f, 0f),
                0f, new Vector3(0f, 2f, 0f), new Vector3(0f, -1f, 0f), TipRadius,
                out normal, out depth, out point, out fraction), "a downward plane sweep hits");
            Assert.AreEqual((2f - TipRadius) / 3f, fraction, 1e-5f);
            Assert.AreEqual(0f, depth, 1e-5f);
            Assert.AreEqual(1f, normal.y, 1e-6f);
            Assert.AreEqual(0f, point.y, 1e-5f);
        }

        [Test]
        public void ATipAlreadyBelowTheSurfaceReportsItsPenetration()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsTrue(BallShapeMath.TryNeedleSegmentPlaneContact(new Vector3(0f, 1f, 0f),
                0f, new Vector3(0f, 0.005f, 0f), new Vector3(0f, -0.5f, 0f), TipRadius,
                out normal, out depth, out point, out fraction));
            Assert.AreEqual(0f, fraction, 0f);
            Assert.AreEqual(0.005f, depth, 1e-6f);
            Assert.AreEqual(1f, normal.y, 1e-6f);
        }

        [Test]
        public void ATipThatStopsAboveTheSurfaceIsNotAContact()
        {
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsFalse(BallShapeMath.TryNeedleSegmentPlaneContact(new Vector3(0f, 1f, 0f),
                0f, new Vector3(0f, 2f, 0f), new Vector3(0f, 0.5f, 0f), TipRadius,
                out normal, out depth, out point, out fraction));
        }

        [Test]
        public void ASweepCrossesAWallThinnerThanTheStep()
        {
            // The reason the segment exists: 0.32 m swept through a 0.02 m wall. Neither
            // endpoint's point test sees the wall; the sweep reports the entrance.
            Vector3 thin = new Vector3(0.01f, 2f, 2f);
            Vector3 previous = new Vector3(0.16f, 0f, 0f);
            Vector3 now = new Vector3(-0.16f, 0f, 0f);
            Vector3 normal;
            float depth;
            Vector3 point;
            float fraction;
            Assert.IsTrue(BallShapeMath.TryNeedleSegmentContact(Origin, AxisX, AxisY, AxisZ,
                thin, previous, now, TipRadius, out normal, out depth, out point,
                out fraction), "the sweep must not tunnel through a thin wall");
            Assert.AreEqual((0.16f - (0.01f + TipRadius)) / 0.32f, fraction, 1e-5f);
            Assert.AreEqual(0f, depth, 1e-5f);
            Assert.AreEqual(1f, normal.x, 1e-5f);

            Vector3 ignoredNormal;
            float ignoredDepth;
            Vector3 ignoredPoint;
            Assert.IsFalse(BallShapeMath.TryNeedleTipContact(Origin, AxisX, AxisY, AxisZ,
                thin, previous, TipRadius, out ignoredNormal, out ignoredDepth,
                out ignoredPoint), "the old start-of-step point test sees nothing");
            Assert.IsFalse(BallShapeMath.TryNeedleTipContact(Origin, AxisX, AxisY, AxisZ,
                thin, now, TipRadius, out ignoredNormal, out ignoredDepth, out ignoredPoint),
                "the old end-of-step point test sees nothing either");
        }
    }
}
