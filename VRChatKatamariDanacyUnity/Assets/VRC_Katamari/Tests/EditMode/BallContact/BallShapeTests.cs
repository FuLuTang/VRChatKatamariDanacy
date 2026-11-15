using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_contact_v2/test_shapes.py: the visual core is inside
    /// the physical sphere, the pickup reach is the patent's box support function unioned
    /// with the pins, a pin can only protrude within the radial proxy's cap, only protruding
    /// pins enter the contact set, and a half-radius protrusion halves the climb gate.
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
        public void TheVisualCoreIsInsideThePhysicalSphere()
        {
            float radius;
            Assert.IsTrue(BallShapeMath.TryVisualCoreRadius(0.30f, out radius));
            Assert.AreEqual(0.285f, radius, Tolerance);
            Assert.IsTrue(BallShapeMath.TryVisualCoreRadius(1.10f, out radius));
            Assert.AreEqual(1.045f, radius, Tolerance);
            Assert.Less(radius, 1.10f);
            Assert.IsFalse(BallShapeMath.TryVisualCoreRadius(0f, out radius));
        }

        [Test]
        public void ThePickupBoxReachIsDirectionDependent()
        {
            float reach;
            // Axis 1.0 r, face diagonal sqrt(2) r, corner sqrt(3) r.
            Assert.IsTrue(BallShapeMath.TryPickupBoxReach(new Vector3(1f, 0f, 0f), 0.30f,
                out reach));
            Assert.AreEqual(0.30f, reach, Tolerance);
            Assert.IsTrue(BallShapeMath.TryPickupBoxReach(
                new Vector3(0.70710678f, 0.70710678f, 0f), 0.30f, out reach));
            Assert.AreEqual(0.42426407f, reach, Tolerance);
            Assert.IsTrue(BallShapeMath.TryPickupBoxReach(
                new Vector3(0.57735027f, 0.57735027f, 0.57735027f), 0.30f, out reach));
            Assert.AreEqual(0.51961524f, reach, Tolerance);
        }

        [Test]
        public void ThePickupIsNeverSmallerThanTheRollingShape()
        {
            float[] supports = Supports(0.30f);
            // One needle protrudes through the cap (clamped to 0.405 for r = 0.30).
            supports[0] = 1f;
            Vector3[] directions = Directions();
            float clamped;
            Assert.IsTrue(BallShapeMath.TryClampNeedleSupport(supports[0], 0.30f, out clamped));
            supports[0] = clamped;
            float rolling;
            Assert.IsTrue(BallShapeMath.TryRollingReach(directions[0], 0.30f, directions,
                supports, NeedleCount, out rolling));
            float pickup;
            Assert.IsTrue(BallShapeMath.TryPickupReach(directions[0], 0.30f, directions,
                supports, NeedleCount, out pickup));
            Assert.GreaterOrEqual(pickup, rolling - 1e-6f);
            Assert.LessOrEqual(rolling, 0.405f + 1e-6f);
            // With no pins the pickup reach is the box alone.
            float boxOnly;
            Assert.IsTrue(BallShapeMath.TryPickupReach(directions[0], 0.30f, directions,
                supports, 0, out boxOnly));
            Assert.AreEqual(0.30f * Mathf.Abs(directions[0].x) + 0.30f * Mathf.Abs(directions[0].y)
                + 0.30f * Mathf.Abs(directions[0].z), boxOnly, Tolerance);
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
