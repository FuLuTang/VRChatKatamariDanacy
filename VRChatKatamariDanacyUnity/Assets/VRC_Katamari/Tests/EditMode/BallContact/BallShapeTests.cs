using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode rolling-shape tests: a pin can only protrude within
    /// the radial proxy's cap, only protruding pins enter the contact set (all 16 may), and
    /// a half-radius protrusion halves the climb gate. The visual core is not here: it is the
    /// level's fixed start diameter (ContactModelV2Tests).
    ///
    /// The needle directions are the radial proxy's own 16-direction Fibonacci set, not a
    /// second copy: the shape layer and the proxy must rotate the same pins.
    /// </summary>
    public class BallShapeTests
    {
        private const float Tolerance = 1e-5f;
        private const int NeedleCount = ContactTuning.NeedleDirectionCount;

        private static Vector3[] Directions()
        {
            Vector3[] directions = new Vector3[NeedleCount];
            Assert.IsTrue(RadialProxyMath.TryFillDirections(directions));
            return directions;
        }

        private static float[] Supports(float value)
        {
            float[] supports = new float[NeedleCount];
            for (int i = 0; i < supports.Length; i++) supports[i] = value;
            return supports;
        }

        [Test]
        public void TheSupportsAreClampedToTheProxyCap()
        {
            float clamped;
            Assert.IsTrue(BallShapeMath.TryClampNeedleSupport(5f, 0.30f, out clamped));
            // Cap = min(0.35 * 0.30, 0.30) = 0.105.
            Assert.AreEqual(0.405f, clamped, Tolerance);
            Assert.AreEqual(0.105f, BallShapeMath.NeedleProtrusionCap(0.30f), Tolerance);
            Assert.AreEqual(0.30f, BallShapeMath.NeedleProtrusionCap(2.20f), Tolerance);
            Assert.AreEqual(0.0525f, BallShapeMath.NeedleProtrusionCap(0.15f), Tolerance);
            // A support below the core is clamped up to the core.
            Assert.IsTrue(BallShapeMath.TryClampNeedleSupport(0.1f, 0.30f, out clamped));
            Assert.AreEqual(0.30f, clamped, Tolerance);
        }

        [Test]
        public void OnlyProtrudingNeedlesEnterTheContactSet()
        {
            float[] supports = Supports(0.30f);
            supports[2] = 0.40f;
            supports[5] = 0.42f;
            int[] compact = new int[ContactTuning.CompactNeedleCap];
            int count;
            Assert.IsTrue(BallShapeMath.TryCollectProtrudingNeedles(supports, NeedleCount, 0.30f,
                compact, out count));
            Assert.AreEqual(2, count);
            // Longest first.
            Assert.AreEqual(5, compact[0]);
            Assert.AreEqual(2, compact[1]);
        }

        [Test]
        public void EveryPinCanEnterTheContactSetWhenAllOfThemProtrude()
        {
            // Every attached prop registers into the pin table, so all 16 pins may protrude
            // (many tied at the cap); none of them may be dropped by a smaller compact cap.
            float[] supports = Supports(0.405f);
            int[] compact = new int[ContactTuning.CompactNeedleCap];
            int count;
            Assert.IsTrue(BallShapeMath.TryCollectProtrudingNeedles(supports, NeedleCount, 0.30f,
                compact, out count));
            Assert.AreEqual(NeedleCount, count);
            Assert.AreEqual(NeedleCount, ContactTuning.CompactNeedleCap);
            // Ties keep ascending index order, so the selection is deterministic.
            for (int k = 0; k < count; k++) Assert.AreEqual(k, compact[k]);
        }

        [Test]
        public void TheTipOffsetsRotateWithTheBall()
        {
            float[] supports = Supports(0.30f);
            supports[0] = 0.40f;
            Vector3[] directions = Directions();
            int[] compact = new int[ContactTuning.CompactNeedleCap];
            int count;
            Assert.IsTrue(BallShapeMath.TryCollectProtrudingNeedles(supports, NeedleCount, 0.30f,
                compact, out count));
            Assert.AreEqual(1, count);

            Vector3[] identity = new Vector3[1];
            Vector3[] rotated = new Vector3[1];
            Assert.IsTrue(BallShapeMath.TryTipOffsetsWorld(directions, supports, compact, count,
                Quaternion.identity, identity));
            Assert.IsTrue(BallShapeMath.TryTipOffsetsWorld(directions, supports, compact, count,
                Quaternion.AngleAxis(90f, new Vector3(0f, 0f, 1f)), rotated));
            // A 90 deg roll about +Z maps (x, y, z) to (-y, x, z).
            Assert.AreEqual(-identity[0].y, rotated[0].x, Tolerance);
            Assert.AreEqual(identity[0].x, rotated[0].y, Tolerance);
            Assert.AreEqual(identity[0].z, rotated[0].z, Tolerance);
        }

        [Test]
        public void TheNeedleTipContactReportsDepthAndNormal()
        {
            // A wide slab whose top face is y = 0; the tip is 5 mm above it.
            Vector3 normal;
            float depth;
            Vector3 point;
            Assert.IsTrue(BallShapeMath.TryNeedleTipContact(new Vector3(0f, -1f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(10f, 1f, 10f), new Vector3(0.2f, 0.005f, 0.3f), 0.01f,
                out normal, out depth, out point));
            Assert.AreEqual(1f, normal.y, Tolerance);
            Assert.AreEqual(0.005f, depth, Tolerance);
            Assert.AreEqual(0f, point.y, Tolerance);

            // A tip 25 mm above the same face does not touch.
            Assert.IsFalse(BallShapeMath.TryNeedleTipContact(new Vector3(0f, -1f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(10f, 1f, 10f), new Vector3(0.2f, 0.025f, 0.3f), 0.01f,
                out normal, out depth, out point));

            // A tip inside the box pushes out through the nearest face (the +y one).
            Assert.IsTrue(BallShapeMath.TryNeedleTipContact(new Vector3(0f, -1f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(10f, 1f, 10f), new Vector3(0.2f, -0.5f, 0.3f), 0.01f,
                out normal, out depth, out point));
            Assert.AreEqual(0.51f, depth, Tolerance);
            Assert.AreEqual(0f, point.y, Tolerance);
        }

        [Test]
        public void TheGripHalvesTheGateAtAHalfRadiusProtrusion()
        {
            Assert.AreEqual(0f, BallShapeMath.GripFromProtrusion(0f, 0.30f), Tolerance);
            Assert.AreEqual(0.5f, BallShapeMath.GripFromProtrusion(0.15f, 0.30f), Tolerance);
            Assert.AreEqual(0.5f, BallShapeMath.GripFromProtrusion(0.40f, 0.30f), Tolerance);
            Assert.AreEqual(0.75f, BallShapeMath.RequiredSpeedWithGrip(90f, 0.15f, 0.30f),
                Tolerance);
            Assert.AreEqual(1.5f, BallShapeMath.RequiredSpeedWithGrip(90f, 0f, 0.30f), Tolerance);
        }
    }
}
