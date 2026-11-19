using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Hub;

namespace VRCKatamari.Tests
{
    public class PushCueTests
    {
        private const float Tolerance = 0.0001f;

        [TestCase(0f, 0f, 1f)]
        [TestCase(90f, 1f, 0f)]
        [TestCase(-90f, -1f, 0f)]
        [TestCase(450f, 1f, 0f)]
        public void CueSitsAheadOfTheBallAndPointsAlongTheSameDrivingYaw(float yaw, float x, float z)
        {
            Vector3 center = new Vector3(7f, 2f, 9f);
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsTrue(PushCueMath.TryEvaluate(center, 2f, yaw, out position, out rotation, out scale));
            Assert.AreEqual(1.2f * x, position.x - center.x, Tolerance);
            Assert.AreEqual(2.5f, position.y - center.y, Tolerance);
            Assert.AreEqual(1.2f * z, position.z - center.z, Tolerance);
            Assert.AreEqual(1.8f, scale, Tolerance);
            Vector3 heading = rotation * Vector3.forward;
            Assert.AreEqual(x, heading.x, Tolerance);
            Assert.AreEqual(0f, heading.y, Tolerance);
            Assert.AreEqual(z, heading.z, Tolerance);
        }

        [TestCase(0.111f)]
        [TestCase(20f)]
        [TestCase(200f)]
        public void CueScalesWithSemanticRadiusAndItsTailClearsTheBallTopSightLine(float radius)
        {
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsTrue(PushCueMath.TryEvaluate(Vector3.zero, radius, 0f, out position, out rotation, out scale));
            Assert.AreEqual(1.25f, position.y / radius, Tolerance);
            Assert.AreEqual(0.6f, position.z / radius, Tolerance);
            Assert.AreEqual(0.9f, scale / radius, Tolerance);

            // 52° 站姿名义眼点：(前向 -1.041795r，高度 1.900108r)，语义球顶：(0, r)。
            float sightZ = -1.041795f * (position.y / radius - 1f) / (1.900108f - 1f) * radius;
            float tailZ = position.z - 0.5f * scale;
            Assert.Greater(tailZ, 0f);
            Assert.Greater(tailZ, sightZ);
        }

        [TestCase(0f, 0f, 0f)]
        [TestCase(0f, -1f, 0f)]
        [TestCase(0f, float.NaN, 0f)]
        [TestCase(0f, float.PositiveInfinity, 0f)]
        [TestCase(float.NaN, 1f, 0f)]
        [TestCase(0f, 1f, float.NaN)]
        [TestCase(0f, float.MaxValue, 0f)]
        public void InvalidPoseIsRejectedAtomically(float centerX, float radius, float yaw)
        {
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsFalse(PushCueMath.TryEvaluate(new Vector3(centerX, 0f, 0f), radius, yaw,
                out position, out rotation, out scale));
            Assert.AreEqual(Vector3.zero, position);
            Assert.AreEqual(Quaternion.identity, rotation);
            Assert.AreEqual(0f, scale);
        }

        [Test]
        public void PushCueSettingDefaultsOnAndStepsBothWays()
        {
            int id = HubSettingsMath.PushCue;
            Assert.AreEqual("vrck.set.pushCue", HubSettingsMath.StorageKey(id));
            Assert.AreEqual(1, HubSettingsMath.Default(id));
            Assert.AreEqual(0, HubSettingsMath.Stepped(id, 1, -1));
            Assert.AreEqual(1, HubSettingsMath.Stepped(id, 0, -1));
            Assert.IsTrue(HubSettingsMath.HasConsumer(id));
        }
    }
}
