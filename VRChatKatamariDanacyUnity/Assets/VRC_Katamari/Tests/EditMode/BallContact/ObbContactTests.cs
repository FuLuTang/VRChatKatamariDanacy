using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of the closest-point cases of
    /// Docs/tests/ball_contact_v2/test_contact_v2.py, plus the skin band and the input
    /// validation the C# side adds.
    ///
    /// The box is the unit box (half extents 1 on the world axes), so every expected
    /// distance is a hand calculation: a face is 1, an edge sqrt(2), a corner sqrt(3).
    /// The point of these cases is that edges and corners carry their rounded normal, not a
    /// face normal - the reason the 45 deg seam no longer deadlocks.
    /// </summary>
    public class ObbContactTests
    {
        private const float Tolerance = 1e-5f;
        private const float Sqrt2 = 1.41421356f;
        private const float Sqrt3 = 1.73205081f;

        private static readonly Vector3 Unit = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 UnitY = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 UnitZ = new Vector3(0f, 0f, 1f);
        private static readonly Vector3 Origin = Vector3.zero;
        private static readonly Vector3 Half = new Vector3(1f, 1f, 1f);

        [Test]
        public void FaceEdgeAndCornerUseTheTrueNormal()
        {
            Vector3 point;
            float distance;
            Vector3 normal;
            int feature;

            Assert.IsTrue(ObbContactMath.TryClosestPointOnObb(Origin, Unit, UnitY, UnitZ, Half,
                new Vector3(2f, 0.5f, 0f), out point, out distance, out normal, out feature));
            Assert.AreEqual(ContactCodes.FeatureFace, feature);
            Assert.AreEqual(1f, distance, Tolerance);
            Assert.AreEqual(1f, point.x, Tolerance);
            Assert.AreEqual(0.5f, point.y, Tolerance);
            Assert.Greater(normal.x, 0.9999f);

            Assert.IsTrue(ObbContactMath.TryClosestPointOnObb(Origin, Unit, UnitY, UnitZ, Half,
                new Vector3(2f, 2f, 0f), out point, out distance, out normal, out feature));
            Assert.AreEqual(ContactCodes.FeatureEdge, feature);
            Assert.AreEqual(Sqrt2, distance, Tolerance);
            Assert.AreEqual(1f, point.x, Tolerance);
            Assert.AreEqual(1f, point.y, Tolerance);
            Assert.AreEqual(0.70710678f, normal.x, Tolerance);
            Assert.AreEqual(0.70710678f, normal.y, Tolerance);

            Assert.IsTrue(ObbContactMath.TryClosestPointOnObb(Origin, Unit, UnitY, UnitZ, Half,
                new Vector3(2f, 2f, 2f), out point, out distance, out normal, out feature));
            Assert.AreEqual(ContactCodes.FeatureCorner, feature);
            Assert.AreEqual(Sqrt3, distance, Tolerance);
            Assert.AreEqual(1f, point.x, Tolerance);
            Assert.AreEqual(1f, point.y, Tolerance);
            Assert.AreEqual(1f, point.z, Tolerance);
            Assert.AreEqual(0.57735027f, normal.x, Tolerance);
            Assert.AreEqual(0.57735027f, normal.y, Tolerance);
            Assert.AreEqual(0.57735027f, normal.z, Tolerance);
        }

        [Test]
        public void AnInsidePointPushesOutThroughTheNearestFace()
        {
            Vector3 normal;
            Vector3 point;
            float depth;
            int feature;
            float angleDeg;
            int contactClass;
            // The centre is 0.1 inside the +x face, so the depth is radius + 0.1 and the
            // distance is negative.
            Assert.IsTrue(ObbContactMath.TrySphereObbContact(Origin, Unit, UnitY, UnitZ, Half,
                new Vector3(0.9f, 0f, 0f), 0.5f, out normal, out point, out depth, out feature,
                out angleDeg, out contactClass));
            Assert.Greater(depth, 0.5f);
            Assert.Greater(normal.x, 0.9999f);
            Assert.AreEqual(ContactCodes.FeatureInside, feature);
            Assert.AreEqual(ContactCodes.ClassClimb, contactClass);
        }

        [Test]
        public void ABoxCornerIsRoundedNotASquareWall()
        {
            // A corner contact exists while the centre is still outside the AABB.
            Vector3 normal;
            Vector3 point;
            float depth;
            int feature;
            float angleDeg;
            int contactClass;
            Assert.IsTrue(ObbContactMath.TrySphereObbContact(Origin, Unit, UnitY, UnitZ, Half,
                new Vector3(1.2f, 1.2f, 0f), 0.3f, out normal, out point, out depth,
                out feature, out angleDeg, out contactClass));
            Assert.Greater(normal.x, 0.5f);
            Assert.Greater(normal.y, 0.5f);
            Assert.Less(angleDeg, 50f);  // a rounded edge, not a wall
            Assert.AreEqual(ContactCodes.FeatureEdge, feature);
            Assert.AreEqual(0.3f - 0.28284271f, depth, Tolerance);
        }

        [Test]
        public void APlaneRegistersInsideTheSkinBandWithoutPushing()
        {
            Vector3 normal;
            Vector3 point;
            float depth;
            float angleDeg;
            int contactClass;

            // 50 mm above the floor with r = 0.15: outside radius + skin, no contact.
            Assert.IsFalse(ObbContactMath.TryPlaneContact(UnitY, 0f, new Vector3(0f, 0.2f, 0f),
                0.15f, out normal, out point, out depth, out angleDeg, out contactClass));

            // 2 mm above the floor: registered, depth negative (near, not touching).
            Assert.IsTrue(ObbContactMath.TryPlaneContact(UnitY, 0f, new Vector3(0f, 0.152f, 0f),
                0.15f, out normal, out point, out depth, out angleDeg, out contactClass));
            Assert.AreEqual(-0.002f, depth, Tolerance);
            Assert.AreEqual(0f, angleDeg, Tolerance);
            Assert.AreEqual(ContactCodes.ClassRide, contactClass);
            Assert.AreEqual(0f, point.y, Tolerance);

            // 50 mm into the floor: depth positive and the point is the foot of the normal.
            Assert.IsTrue(ObbContactMath.TryPlaneContact(UnitY, 0f, new Vector3(0f, 0.10f, 0f),
                0.15f, out normal, out point, out depth, out angleDeg, out contactClass));
            Assert.AreEqual(0.05f, depth, Tolerance);
        }

        [Test]
        public void AScaledOrShearedBoxIsRefused()
        {
            Vector3 point;
            float distance;
            Vector3 normal;
            int feature;
            Assert.IsFalse(ObbContactMath.TryClosestPointOnObb(Origin, new Vector3(2f, 0f, 0f),
                UnitY, UnitZ, Half, new Vector3(0f, 0f, 3f), out point, out distance,
                out normal, out feature));
            // Two axes 45 deg apart: the parallelogram a sheared transform would produce.
            Assert.IsFalse(ObbContactMath.TryClosestPointOnObb(Origin, Unit,
                new Vector3(0.70710678f, 0.70710678f, 0f), UnitZ, Half,
                new Vector3(0f, 0f, 3f), out point, out distance, out normal, out feature));
            // A negative half extent is not a box.
            Assert.IsFalse(ObbContactMath.TryClosestPointOnObb(Origin, Unit, UnitY, UnitZ,
                new Vector3(1f, -1f, 1f), new Vector3(0f, 0f, 3f), out point, out distance,
                out normal, out feature));
        }

        [Test]
        public void ANonFiniteQueryIsRefused()
        {
            Vector3 point;
            float distance;
            Vector3 normal;
            int feature;
            Assert.IsFalse(ObbContactMath.TryClosestPointOnObb(Origin, Unit, UnitY, UnitZ, Half,
                new Vector3(float.NaN, 0f, 0f), out point, out distance, out normal,
                out feature));
            float angleDeg;
            int contactClass;
            // A zero plane normal is degenerate and refuses instead of dividing by zero.
            Assert.IsFalse(ObbContactMath.TryPlaneContact(new Vector3(0f, 0f, 0f), 0f,
                new Vector3(0f, 0.1f, 0f), 0.15f, out normal, out point, out distance,
                out angleDeg, out contactClass));
        }

        [Test]
        public void TheBoundingRadiusIsTheHalfExtentLength()
        {
            Assert.AreEqual(5f, ObbContactMath.BoundingRadius(new Vector3(3f, 4f, 0f)),
                Tolerance);
        }
    }
}
