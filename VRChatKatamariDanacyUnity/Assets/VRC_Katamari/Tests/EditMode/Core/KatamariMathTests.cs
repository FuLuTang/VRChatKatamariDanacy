using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the shared finite-input helpers. Values are hand
    /// calculations; the scale-robust case is chosen so a naive squared-length
    /// implementation would overflow to infinity.
    /// </summary>
    public class KatamariMathTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void IsFiniteFloatAcceptsOrdinaryValues()
        {
            Assert.IsTrue(KatamariMath.IsFiniteFloat(0f));
            Assert.IsTrue(KatamariMath.IsFiniteFloat(-1.5f));
            Assert.IsTrue(KatamariMath.IsFiniteFloat(float.MaxValue));
            Assert.IsTrue(KatamariMath.IsFiniteFloat(float.MinValue));
        }

        [Test]
        public void IsFiniteFloatRejectsNaNAndInfinities()
        {
            Assert.IsFalse(KatamariMath.IsFiniteFloat(float.NaN));
            Assert.IsFalse(KatamariMath.IsFiniteFloat(float.PositiveInfinity));
            Assert.IsFalse(KatamariMath.IsFiniteFloat(float.NegativeInfinity));
        }

        [Test]
        public void IsFiniteVectorAndQuaternionRejectOneBadComponent()
        {
            Assert.IsTrue(KatamariMath.IsFiniteVector(new Vector3(1f, -2f, 3f)));
            Assert.IsFalse(KatamariMath.IsFiniteVector(new Vector3(1f, float.NaN, 3f)));
            Assert.IsFalse(KatamariMath.IsFiniteVector(new Vector3(1f, 2f, float.NegativeInfinity)));

            Assert.IsTrue(KatamariMath.IsFiniteQuaternion(new Quaternion(0f, 1f, 0f, 0f)));
            Assert.IsFalse(KatamariMath.IsFiniteQuaternion(new Quaternion(0f, 0f, 0f, float.NaN)));
            Assert.IsFalse(KatamariMath.IsFiniteQuaternion(new Quaternion(float.PositiveInfinity, 0f, 0f, 1f)));
        }

        [Test]
        public void TryNormalizeReturnsUnitDirectionAndMetreLength()
        {
            // 3-4-5 triangle: unit (0,0.6,0.8), length 5 m.
            Vector3 unit;
            float length;
            Assert.IsTrue(KatamariMath.TryNormalize(new Vector3(0f, 3f, 4f), out unit, out length));
            Assert.AreEqual(5f, length, Tolerance);
            Assert.AreEqual(0f, unit.x, Tolerance);
            Assert.AreEqual(0.6f, unit.y, Tolerance);
            Assert.AreEqual(0.8f, unit.z, Tolerance);

            // Sign is preserved: (-3,0,-4) keeps pointing into the same octant.
            Assert.IsTrue(KatamariMath.TryNormalize(new Vector3(-3f, 0f, -4f), out unit, out length));
            Assert.AreEqual(5f, length, Tolerance);
            Assert.AreEqual(-0.6f, unit.x, Tolerance);
            Assert.AreEqual(0f, unit.y, Tolerance);
            Assert.AreEqual(-0.8f, unit.z, Tolerance);
        }

        [Test]
        public void TryNormalizeIsScaleRobustForHugeVectors()
        {
            // Same 3-4-5 direction at 1e30 scale: squaring the components directly
            // overflows to infinity, while the scaled path returns length 5e30.
            Vector3 unit;
            float length;
            Assert.IsTrue(KatamariMath.TryNormalize(new Vector3(0f, 3e30f, 4e30f), out unit, out length));
            Assert.AreEqual(5e30f, length, 1e24f);
            Assert.AreEqual(0f, unit.x, Tolerance);
            Assert.AreEqual(0.6f, unit.y, Tolerance);
            Assert.AreEqual(0.8f, unit.z, Tolerance);
        }

        [Test]
        public void TryNormalizeRejectsDegenerateAndUnrepresentableInput()
        {
            Vector3 unit;
            float length;
            Assert.IsFalse(KatamariMath.TryNormalize(Vector3.zero, out unit, out length));
            Assert.AreEqual(Vector3.zero, unit);
            Assert.AreEqual(0f, length, Tolerance);

            // ~zero in metres (1e-20 m) is treated as degenerate.
            Assert.IsFalse(KatamariMath.TryNormalize(new Vector3(1e-20f, 0f, 0f), out unit, out length));
            Assert.IsFalse(KatamariMath.TryNormalize(new Vector3(float.NaN, 0f, 0f), out unit, out length));

            // (0,3e38,3e38) has length 3e38*sqrt(2) ~ 4.24e38, beyond
            // float.MaxValue, so no finite length can be reported and the call
            // fails instead of returning infinity.
            Assert.IsFalse(KatamariMath.TryNormalize(new Vector3(0f, 3e38f, 3e38f), out unit, out length));
        }
    }
}
