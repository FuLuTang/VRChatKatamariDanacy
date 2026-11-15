using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode cases for the eight-corner OBB bake (CONTACT_MODEL_V2.md section 1.4).
    ///
    /// The contract under test is the migration checkpoint: for an axis-aligned box the
    /// corner bake must reproduce the bounds numbers exactly, and a sheared transform must
    /// be refused so the caller can fall back to the bounds AABB with a warning. Expected
    /// values are the box the test built, not the bake's arithmetic.
    /// </summary>
    public class ObbBakeTests
    {
        private const float Tolerance = 1e-5f;

        // Corner index bits: 1 = +local X, 2 = +local Y, 4 = +local Z.
        private static Vector3 Corner(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 half, int index)
        {
            float signX = (index & 1) != 0 ? 1f : -1f;
            float signY = (index & 2) != 0 ? 1f : -1f;
            float signZ = (index & 4) != 0 ? 1f : -1f;
            return new Vector3(
                center.x + signX * half.x * axisX.x + signY * half.y * axisY.x +
                    signZ * half.z * axisZ.x,
                center.y + signX * half.x * axisX.y + signY * half.y * axisY.y +
                    signZ * half.z * axisZ.y,
                center.z + signX * half.x * axisX.z + signY * half.y * axisY.z +
                    signZ * half.z * axisZ.z);
        }

        private static Vector3[] Corners(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 half)
        {
            Vector3[] corners = new Vector3[ObbBakeMath.CornerCount];
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i] = Corner(center, axisX, axisY, axisZ, half, i);
            }
            return corners;
        }

        [Test]
        public void AnAxisAlignedBoxReproducesItsBounds()
        {
            Vector3 center = new Vector3(1f, 2f, 3f);
            Vector3 half = new Vector3(1f, 2f, 3f);
            Vector3[] corners = Corners(center, Vector3.right, Vector3.up, Vector3.forward,
                half);
            Vector3 bakedCenter;
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 bakedHalf;
            Assert.IsTrue(ObbBakeMath.TryBakeFromCorners(corners, out bakedCenter, out axisX,
                out axisY, out axisZ, out bakedHalf));
            Assert.AreEqual(center.x, bakedCenter.x, Tolerance);
            Assert.AreEqual(center.y, bakedCenter.y, Tolerance);
            Assert.AreEqual(center.z, bakedCenter.z, Tolerance);
            Assert.AreEqual(1f, axisX.x, Tolerance);
            Assert.AreEqual(1f, axisY.y, Tolerance);
            Assert.AreEqual(1f, axisZ.z, Tolerance);
            Assert.AreEqual(half.x, bakedHalf.x, Tolerance);
            Assert.AreEqual(half.y, bakedHalf.y, Tolerance);
            Assert.AreEqual(half.z, bakedHalf.z, Tolerance);
            Assert.IsTrue(ObbContactMath.IsValidObb(bakedCenter, axisX, axisY, axisZ, bakedHalf));
        }

        [Test]
        public void ARotatedBoxKeepsItsAxesAndHalfExtents()
        {
            Quaternion rotation = Quaternion.Euler(0f, 30f, 0f);
            Vector3 axisX = rotation * Vector3.right;
            Vector3 axisY = rotation * Vector3.up;
            Vector3 axisZ = rotation * Vector3.forward;
            Vector3 center = new Vector3(-2f, 0.5f, 4f);
            Vector3 half = new Vector3(2f, 0.05f, 0.4f);
            Vector3[] corners = Corners(center, axisX, axisY, axisZ, half);
            Vector3 bakedCenter;
            Vector3 bakedX;
            Vector3 bakedY;
            Vector3 bakedZ;
            Vector3 bakedHalf;
            Assert.IsTrue(ObbBakeMath.TryBakeFromCorners(corners, out bakedCenter, out bakedX,
                out bakedY, out bakedZ, out bakedHalf));
            Assert.AreEqual(center.x, bakedCenter.x, Tolerance);
            Assert.AreEqual(center.y, bakedCenter.y, Tolerance);
            Assert.AreEqual(center.z, bakedCenter.z, Tolerance);
            Assert.AreEqual(axisX.x, bakedX.x, Tolerance);
            Assert.AreEqual(axisX.z, bakedX.z, Tolerance);
            Assert.AreEqual(0.05f, bakedHalf.y, Tolerance);
            Assert.AreEqual(0.4f, bakedHalf.z, Tolerance);
            Assert.IsTrue(ObbContactMath.IsValidObb(bakedCenter, bakedX, bakedY, bakedZ,
                bakedHalf));
        }

        [Test]
        public void AShearedCornerSetIsRefused()
        {
            // A non-uniformly scaled rotated parent: the "Y edge" is not perpendicular to X.
            Vector3[] corners = new Vector3[ObbBakeMath.CornerCount];
            Vector3 axisY = new Vector3(0.3f, 1f, 0f).normalized;
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i] = Corner(Vector3.zero, Vector3.right, axisY, Vector3.forward,
                    new Vector3(1f, 1f, 1f), i);
            }
            Vector3 bakedCenter;
            Vector3 axisX;
            Vector3 bakedY;
            Vector3 axisZ;
            Vector3 bakedHalf;
            Assert.IsFalse(ObbBakeMath.TryBakeFromCorners(corners, out bakedCenter, out axisX,
                out bakedY, out axisZ, out bakedHalf));
        }

        [Test]
        public void ShortOrNonFiniteInputIsRefused()
        {
            Vector3[] shortCorners = new Vector3[4];
            Vector3 bakedCenter;
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 bakedHalf;
            Assert.IsFalse(ObbBakeMath.TryBakeFromCorners(shortCorners, out bakedCenter,
                out axisX, out axisY, out axisZ, out bakedHalf));
            Vector3[] corners = Corners(Vector3.zero, Vector3.right, Vector3.up,
                Vector3.forward, new Vector3(1f, 1f, 1f));
            corners[7] = new Vector3(float.NaN, 0f, 0f);
            Assert.IsFalse(ObbBakeMath.TryBakeFromCorners(corners, out bakedCenter, out axisX,
                out axisY, out axisZ, out bakedHalf));
        }

        [Test]
        public void TheBoundsFallbackIsTheWorldAxisBox()
        {
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 half;
            Assert.IsTrue(ObbBakeMath.TryBakeFromBounds(new Vector3(1f, 2f, 3f),
                new Vector3(2f, 4f, 6f), out axisX, out axisY, out axisZ, out half));
            Assert.AreEqual(1f, axisX.x, Tolerance);
            Assert.AreEqual(1f, axisY.y, Tolerance);
            Assert.AreEqual(1f, axisZ.z, Tolerance);
            Assert.AreEqual(1f, half.x, Tolerance);
            Assert.AreEqual(2f, half.y, Tolerance);
            Assert.AreEqual(3f, half.z, Tolerance);
            Assert.IsFalse(ObbBakeMath.TryBakeFromBounds(Vector3.zero,
                new Vector3(-1f, 1f, 1f), out axisX, out axisY, out axisZ, out half));
        }
    }
}
