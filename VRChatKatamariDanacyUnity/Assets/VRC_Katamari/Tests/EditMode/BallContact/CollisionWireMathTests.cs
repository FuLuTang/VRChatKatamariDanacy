using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    public class CollisionWireMathTests
    {
        [Test]
        public void WindowGrowsWithTheBallButNeverBelowFiveMetres()
        {
            Assert.AreEqual(5f, CollisionWireMath.WindowRange(0.3f), 1e-6f);
            Assert.AreEqual(5f, CollisionWireMath.WindowRange(float.NaN), 1e-6f);
            Assert.AreEqual(12f, CollisionWireMath.WindowRange(4f), 1e-5f);
        }

        [Test]
        public void BoxIsTwelveEdgesOnItsCorners()
        {
            var dst = new Vector3[CollisionWireMath.StaticVertexCapacity];
            int used = CollisionWireMath.AppendBox(dst, 0, Vector3.zero, Vector3.right, Vector3.up,
                Vector3.forward, new Vector3(1f, 2f, 3f));
            Assert.AreEqual(24, used);
            for (int i = 0; i < used; i++)
            {
                Assert.AreEqual(1f, Mathf.Abs(dst[i].x), 1e-5f, "x " + i);
                Assert.AreEqual(2f, Mathf.Abs(dst[i].y), 1e-5f, "y " + i);
                Assert.AreEqual(3f, Mathf.Abs(dst[i].z), 1e-5f, "z " + i);
            }
        }

        [Test]
        public void FullBufferDropsWholeShapesOnly()
        {
            var dst = new Vector3[30];
            int used = CollisionWireMath.AppendBox(dst, 0, Vector3.zero, Vector3.right, Vector3.up,
                Vector3.forward, Vector3.one);
            Assert.AreEqual(24, used);
            Assert.AreEqual(24, CollisionWireMath.AppendBox(dst, used, Vector3.zero, Vector3.right,
                Vector3.up, Vector3.forward, Vector3.one));
            Assert.AreEqual(30, CollisionWireMath.AppendTriangle(dst, used, Vector3.zero,
                Vector3.right, Vector3.up));
            Assert.AreEqual(30, CollisionWireMath.AppendTriangle(dst, 30, Vector3.zero,
                Vector3.right, Vector3.up));
        }

        [Test]
        public void SphereRingsStayOnTheRadius()
        {
            var dst = new Vector3[CollisionWireMath.PropVertexCapacity];
            Vector3 c = new Vector3(1f, 2f, 3f);
            int used = CollisionWireMath.AppendRound(dst, 0, c, Vector3.right, Vector3.up,
                Vector3.forward, 0.5f, 0f, 1);
            Assert.AreEqual(72, used);
            for (int i = 0; i < used; i++) Assert.AreEqual(0.5f, (dst[i] - c).magnitude, 1e-4f);
        }

        [Test]
        public void CapsuleStaysWithinItsHull()
        {
            var dst = new Vector3[CollisionWireMath.PropVertexCapacity];
            int used = CollisionWireMath.AppendRound(dst, 0, Vector3.zero, Vector3.right,
                Vector3.up, Vector3.forward, 0.5f, 1f, 1);
            Assert.AreEqual(104, used);
            for (int i = 0; i < used; i++)
            {
                Vector3 p = dst[i];
                float axial = Mathf.Clamp(p.y, -1f, 1f);
                Assert.AreEqual(0.5f, (p - new Vector3(0f, axial, 0f)).magnitude, 1e-4f, "point " + i);
            }
        }

        [Test]
        public void TriangleWindowUsesItsBounds()
        {
            Vector3 p = Vector3.zero;
            Assert.IsTrue(CollisionWireMath.TriangleInWindow(new Vector3(4f, 0f, 0f),
                new Vector3(9f, 0f, 0f), new Vector3(9f, 0f, 1f), p, 5f));
            Assert.IsFalse(CollisionWireMath.TriangleInWindow(new Vector3(6f, 0f, 0f),
                new Vector3(9f, 0f, 0f), new Vector3(9f, 0f, 1f), p, 5f));
        }
    }
}
