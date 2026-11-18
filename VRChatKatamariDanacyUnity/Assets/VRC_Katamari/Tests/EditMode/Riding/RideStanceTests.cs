using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    public class RideStanceTests
    {
        [TestCase(0.75f)]
        [TestCase(1f)]
        [TestCase(1.25f)]
        public void MeasuredStanceClosesEyeAndGroundAtEverySize(float eta)
        {
            foreach (float diameter in new float[] { 0.222f, 0.3f, 2f, 20f })
            {
                float radius = diameter * 0.5f;
                float eyeHeight;
                bool clamped;
                Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(diameter, eta,
                    out eyeHeight, out clamped));
                Assert.IsFalse(clamped);
                Vector3 head = new Vector3(0.04f * eyeHeight, eta * eyeHeight, -0.02f * eyeHeight);
                float measured;
                int reason;
                Assert.IsTrue(RideStanceMath.TryMeasureEta(head, eyeHeight, out measured, out reason));
                Assert.AreEqual(eta, measured, 1e-5f);
                float rigY = radius + RideViewMath.EyeUpPerRadius() * radius;
                float feet = RideStanceMath.FeetTargetInRig(radius, radius, rigY);
                Vector3 seat = -RideStanceMath.EffectiveHeadInRig(head, true, feet);
                Assert.AreEqual(0f, rigY + seat.y, 1e-4f);
                Assert.Less((seat + head).magnitude, 1e-4f);
            }
        }

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1.26f)]
        [TestCase(float.NaN)]
        public void NonStandingDatumIsNotAStance(float headHeight)
        {
            float eta;
            int reason;
            Assert.IsFalse(RideStanceMath.TryMeasureEta(new Vector3(0f, headHeight, 0f),
                1f, out eta, out reason));
            Vector3 head = new Vector3(0.1f, 0.6f, -0.1f);
            Assert.AreEqual(head, RideStanceMath.EffectiveHeadInRig(head, false, -20f));
        }

        [Test]
        public void GroundWinsWhileEyeHeightLagsGrowth()
        {
            float feet = RideStanceMath.FeetTargetInRig(10f, 2f, 14f);
            Vector3 head = new Vector3(0.1f, 1f, -0.1f);
            Vector3 seat = -RideStanceMath.EffectiveHeadInRig(head, true, feet);
            Assert.AreEqual(8f, 14f + seat.y);
            Assert.AreEqual(-5f, (seat + head).y);
        }
    }
}
