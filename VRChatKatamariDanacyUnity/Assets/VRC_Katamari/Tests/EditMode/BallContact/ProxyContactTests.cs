using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The collider-proxy primitive's hand-calculated cases: sphere, capsule, box
    /// delegation and a tetrahedral convex hull, plus the "box proxy is the old OBB"
    /// equivalence that the flag-off path relies on.
    /// </summary>
    public class ProxyContactTests
    {
        private const float Tolerance = 1e-5f;
        private static readonly Vector3 UnitX = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 UnitY = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 UnitZ = new Vector3(0f, 0f, 1f);

        [Test]
        public void SphereClosestPointAndContactUseTheRadialNormal()
        {
            Vector3 point;
            float distance;
            Vector3 normal;
            int feature;
            Assert.IsTrue(ProxyContactMath.TryClosestPointOnProxy(
                ProxyContactMath.KindSphere, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, 1f, 0f, 0, null, 0, 0, null, null, null, null, null, 0, 0,
                new Vector3(3f, 0f, 0f), out point, out distance, out normal, out feature));
            Assert.AreEqual(2f, distance, Tolerance);
            Assert.AreEqual(1f, point.x, Tolerance);
            Assert.AreEqual(0f, point.y, Tolerance);
            Assert.AreEqual(1f, normal.x, Tolerance);

            Vector3 contactNormal;
            Vector3 contactPoint;
            float depth;
            int contactFeature;
            float angle;
            int contactClass;
            Assert.IsTrue(ProxyContactMath.TrySphereProxyContact(
                ProxyContactMath.KindSphere, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, 1f, 0f, 0, null, 0, 0, null, null, null, null, null, 0, 0,
                new Vector3(1.4f, 0f, 0f), 0.5f, out contactNormal, out contactPoint,
                out depth, out contactFeature, out angle, out contactClass));
            Assert.AreEqual(0.1f, depth, Tolerance);
            Assert.AreEqual(1f, contactNormal.x, Tolerance);
            Assert.AreEqual(ContactCodes.ClassClimb, contactClass);
        }

        [Test]
        public void CapsuleClosestPointClampsToTheSegment()
        {
            Vector3 point;
            float distance;
            Vector3 normal;
            int feature;
            Assert.IsTrue(ProxyContactMath.TryClosestPointOnProxy(
                ProxyContactMath.KindCapsule, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, 0.5f, 1f, ProxyContactMath.AxisX, null, 0, 0, null, null,
                null, null, null, 0, 0, new Vector3(0f, 1f, 0f), out point, out distance,
                out normal, out feature));
            Assert.AreEqual(0.5f, distance, Tolerance);
            Assert.AreEqual(0.5f, point.y, Tolerance);
            Assert.AreEqual(0f, point.x, Tolerance);
            Assert.AreEqual(1f, normal.y, Tolerance);

            // Beyond the end cap: the closest point is on the cap, not the cylinder.
            Assert.IsTrue(ProxyContactMath.TryClosestPointOnProxy(
                ProxyContactMath.KindCapsule, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, 0.5f, 1f, ProxyContactMath.AxisX, null, 0, 0, null, null,
                null, null, null, 0, 0, new Vector3(2f, 0f, 0f), out point, out distance,
                out normal, out feature));
            Assert.AreEqual(0.5f, distance, Tolerance);
            Assert.AreEqual(1.5f, point.x, Tolerance);
            Assert.AreEqual(1f, normal.x, Tolerance);
        }

        [Test]
        public void BoxProxyIsBitForBitTheObbClosestPoint()
        {
            Vector3[] probes =
            {
                new Vector3(2f, 0.5f, 0f),
                new Vector3(2f, 2f, 2f),
                new Vector3(0.2f, -0.3f, 0.4f),
                new Vector3(-3f, 0f, 0f),
            };
            for (int i = 0; i < probes.Length; i++)
            {
                Vector3 expectedPoint;
                float expectedDistance;
                Vector3 expectedNormal;
                int expectedFeature;
                Assert.IsTrue(ObbContactMath.TryClosestPointOnObb(Vector3.zero, UnitX, UnitY,
                    UnitZ, new Vector3(1f, 1f, 1f), probes[i], out expectedPoint,
                    out expectedDistance, out expectedNormal, out expectedFeature));

                Vector3 point;
                float distance;
                Vector3 normal;
                int feature;
                Assert.IsTrue(ProxyContactMath.TryClosestPointOnProxy(
                    ProxyContactMath.KindBox, Vector3.zero, UnitX, UnitY, UnitZ,
                    new Vector3(1f, 1f, 1f), 0f, 0f, 0, null, 0, 0, null, null, null,
                    null, null, 0, 0, probes[i], out point, out distance, out normal,
                    out feature));
                Assert.AreEqual(expectedPoint, point, "point " + i);
                Assert.That(distance, Is.EqualTo(expectedDistance).Within(Tolerance),
                    "distance " + i);
                Assert.AreEqual(expectedNormal, normal, "normal " + i);
                Assert.AreEqual(expectedFeature, feature, "feature " + i);
            }
        }

        [Test]
        public void ConvexTetraClosestPointAndContact()
        {
            Vector3[] hull =
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f),
            };
            // Outward faces of the tetra: (0,2,1), (0,1,3), (0,3,2), (1,2,3).
            int[] a = { 0, 0, 0, 1 };
            int[] b = { 2, 1, 3, 2 };
            int[] c = { 1, 3, 2, 3 };
            Vector3[] normals =
            {
                new Vector3(0f, 0f, -1f),
                new Vector3(0f, -1f, 0f),
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 1f, 1f).normalized,
            };
            float[] offsets = { 0f, 0f, 0f, 1f / Mathf.Sqrt(3f) };

            Vector3 point;
            float distance;
            Vector3 normal;
            int feature;
            Assert.IsTrue(ProxyContactMath.TryClosestPointOnProxy(
                ProxyContactMath.KindConvex, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, 0f, 0f, 0, hull, 0, 4, normals, offsets, a, b, c, 0, 4,
                new Vector3(2f, 2f, 2f), out point, out distance, out normal, out feature));
            Assert.Greater(distance, 0.5f);
            Assert.Greater(normal.x + normal.y + normal.z, 1f);

            // A point inside the tetra reports a negative distance (the box convention).
            Assert.IsTrue(ProxyContactMath.TryClosestPointOnProxy(
                ProxyContactMath.KindConvex, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, 0f, 0f, 0, hull, 0, 4, normals, offsets, a, b, c, 0, 4,
                new Vector3(0.1f, 0.1f, 0.1f), out point, out distance, out normal,
                out feature));
            Assert.Less(distance, 0f);
            Assert.AreEqual(ContactCodes.FeatureInside, feature);
        }

        [Test]
        public void WatermelonCapsuleRollsWithoutAnExtraLift()
        {
            // The fitted watermelon proxy: r = 0.11 m, halfHeight = 0.03 m along local X.
            // A ball of radius 0.15 m rolling on it should sit exactly r_proxy + r_ball
            // above the capsule axis: no invisible lift, no missing clearance.
            float proxyRadius = 0.11f;
            float ballRadius = 0.15f;
            Vector3 contactNormal;
            Vector3 contactPoint;
            float depth;
            int feature;
            float angle;
            int contactClass;
            Assert.IsTrue(ProxyContactMath.TrySphereProxyContact(
                ProxyContactMath.KindCapsule, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, proxyRadius, 0.03f, ProxyContactMath.AxisX, null, 0, 0,
                null, null, null, null, null, 0, 0,
                new Vector3(0f, proxyRadius + ballRadius, 0f), ballRadius, out contactNormal,
                out contactPoint, out depth, out feature, out angle, out contactClass));
            Assert.AreEqual(0f, depth, Tolerance);
            Assert.AreEqual(1f, contactNormal.y, Tolerance);
            Assert.AreEqual(proxyRadius, contactPoint.y, Tolerance);

            // One centimetre lower is a real 1 cm penetration, not a skin-only near miss.
            Assert.IsTrue(ProxyContactMath.TrySphereProxyContact(
                ProxyContactMath.KindCapsule, Vector3.zero, UnitX, UnitY, UnitZ,
                Vector3.zero, proxyRadius, 0.03f, ProxyContactMath.AxisX, null, 0, 0,
                null, null, null, null, null, 0, 0,
                new Vector3(0f, proxyRadius + ballRadius - 0.01f, 0f), ballRadius,
                out contactNormal, out contactPoint, out depth, out feature, out angle,
                out contactClass));
            Assert.AreEqual(0.01f, depth, Tolerance);
            Assert.AreEqual(1f, contactNormal.y, Tolerance);
        }

        [Test]
        public void BoxProxyContactCollectionMatchesTheOldObbCollection()
        {
            Vector3[] centers = { Vector3.zero };
            Vector3[] axisX = { UnitX };
            Vector3[] axisY = { UnitY };
            Vector3[] axisZ = { UnitZ };
            Vector3[] half = { new Vector3(1f, 1f, 1f) };
            int[] surfaceIds = { 100 };
            int[] roles = { ContactCodes.RoleBlocking };
            Vector3[] normals = new Vector3[1];
            Vector3[] points = new Vector3[1];
            float[] depths = new float[1];
            float[] angles = new float[1];
            int[] contactSurface = new int[1];
            int[] contactRoles = new int[1];
            int[] features = new int[1];
            int[] classes = new int[1];

            int oldCount;
            Assert.IsTrue(ContactSolveMath.TryCollectContacts(false, UnitY, 0f,
                new Vector3[0], new float[0], 0, centers, axisX, axisY, axisZ, half,
                surfaceIds, roles, 1, new Vector3(1.4f, 0f, 0f), 0.6f, normals, points,
                depths, angles, contactSurface, contactRoles, features, classes, 1,
                out oldCount));
            float oldDepth = depths[0];

            int newCount;
            Assert.IsTrue(ContactSolveMath.TryCollectContacts(false, UnitY, 0f,
                new Vector3[0], new float[0], 0, centers, axisX, axisY, axisZ, half,
                surfaceIds, roles, 1, new Vector3(1.4f, 0f, 0f), 0.6f, normals, points,
                depths, angles, contactSurface, contactRoles, features, classes, 1,
                out newCount, ContactTuning.ContactSkinM,
                new int[] { ProxyContactMath.KindBox }, new float[1], new float[1],
                new int[1], new int[1], new int[1], new int[1], new int[1],
                new Vector3[1], new Vector3[1], new float[1], new int[1], new int[1],
                new int[1]));

            Assert.AreEqual(oldCount, newCount);
            Assert.Greater(newCount, 0);
            Assert.AreEqual(oldDepth, depths[0], Tolerance);
        }

        [Test]
        public void BoxProxyWindowMatchesTheOldWindow()
        {
            Vector3[] centers = { Vector3.zero, new Vector3(3f, 0f, 0f) };
            Vector3[] axisX = { UnitX, UnitX };
            Vector3[] axisY = { UnitY, UnitY };
            Vector3[] axisZ = { UnitZ, UnitZ };
            Vector3[] half = { new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, 1f) };
            float[] bounding = { 1.7320508f, 1.7320508f };
            int[] surfaceIds = { 100, 101 };
            int[] previous = new int[2];
            int[] scratch = new int[2];
            float[] distances = new float[2];
            int[] selected = new int[2];

            int oldCount;
            int oldDropped;
            bool oldComplete;
            bool oldRebuilt;
            Assert.IsTrue(ContactWindowMath.TrySelectWindow(2, centers, axisX, axisY, axisZ,
                half, bounding, surfaceIds, Vector3.zero, 0.5f, 0.1f, previous, 0, 2,
                scratch, distances, selected, out oldCount, out oldDropped, out oldComplete,
                out oldRebuilt));

            int newCount;
            int newDropped;
            bool newComplete;
            bool newRebuilt;
            Assert.IsTrue(ContactWindowMath.TrySelectProxyWindow(2, centers, axisX, axisY,
                axisZ, half, bounding, surfaceIds,
                new int[] { ProxyContactMath.KindBox, ProxyContactMath.KindBox },
                new float[2], new float[2], new int[2], new int[2], new int[2],
                new int[2], new int[2], new Vector3[2], new Vector3[2], new float[2],
                new int[2], new int[2], new int[2], Vector3.zero, 0.5f, 0.1f, previous, 0,
                2, scratch, distances, selected, out newCount, out newDropped,
                out newComplete, out newRebuilt));

            Assert.AreEqual(oldCount, newCount);
            Assert.AreEqual(oldDropped, newDropped);
            Assert.AreEqual(oldComplete, newComplete);
            Assert.AreEqual(oldRebuilt, newRebuilt);
        }

        [Test]
        public void BoxBakeMatchesTheObbBake()
        {
            Vector3[] hull = new Vector3[24];
            Vector3[] normals = new Vector3[44];
            float[] offsets = new float[44];
            int[] a = new int[44];
            int[] b = new int[44];
            int[] c = new int[44];
            Vector3 center;
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 half;
            float radius;
            float halfHeight;
            int axis;
            int hullCount;
            int faceCount;
            float bounding;
            Assert.IsTrue(ProxyContactMath.TryBakeProxy(ProxyContactMath.KindBox,
                new Vector3(1f, 2f, 3f), Quaternion.identity, Vector3.zero,
                new Vector3(2f, 4f, 6f), 0f, 0f, 0, hull, 0, 0, new int[0], 0, 0,
                hull, 0, 24, normals, offsets, a, b, c, 0, 44, out center, out axisX,
                out axisY, out axisZ, out half, out radius, out halfHeight, out axis,
                out hullCount, out faceCount, out bounding));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), center);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), half);
            Assert.AreEqual(0f, radius, Tolerance);
            Assert.AreEqual(0, hullCount);
            Assert.AreEqual(0, faceCount);
            Assert.AreEqual(3.741657f, bounding, Tolerance);
        }

    }
}
