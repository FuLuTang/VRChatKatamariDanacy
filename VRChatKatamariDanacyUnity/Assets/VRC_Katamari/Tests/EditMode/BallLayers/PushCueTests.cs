using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Hub;

namespace VRCKatamari.Tests
{
    public class PushCueTests
    {
        private const float Tolerance = 0.0001f;

        [TestCase(26f)]
        [TestCase(27f)]
        [TestCase(-27f)]
        public void StationaryAnchorDoesNotMoveInsideDeadZone(float headYaw)
        {
            bool following;
            float angle = PushCueMath.StepAnchorYaw(0f, headYaw,
                PushCueMath.DefaultDeadZoneDegrees, PushCueMath.DefaultCatchUpSeconds,
                1f, false, out following);
            Assert.AreEqual(0f, angle);
            Assert.IsFalse(following);
        }

        [Test]
        public void OnceTriggeredCatchUpContinuesInsideDeadZoneAtTheSameRate()
        {
            bool following;
            float first = PushCueMath.StepAnchorYaw(0f, 90f, 27f, 0.4f, 0.2f,
                false, out following);
            Assert.AreEqual(69.9183f, first, Tolerance);
            Assert.IsTrue(following);
            float second = PushCueMath.StepAnchorYaw(first, 90f, 27f, 0.4f, 0.2f,
                following, out following);
            float oneFrame = PushCueMath.StepAnchorYaw(0f, 90f, 27f, 0.4f, 0.4f,
                false, out following);
            Assert.AreEqual(85.5192f, second, Tolerance);
            Assert.AreEqual(second, oneFrame, Tolerance);
            Assert.IsTrue(following);
            Assert.AreEqual(90f, PushCueMath.StepAnchorYaw(second, 90f, 27f, 0.4f, 0.3f,
                true, out following));
            Assert.IsFalse(following);
        }

        [Test]
        public void CatchUpCrossesTheSeamByTheShortArc()
        {
            bool following;
            float angle = PushCueMath.StepAnchorYaw(350f, 20f, 27f, 0.4f, 0.1f,
                false, out following);
            Assert.IsTrue(following);
            Assert.AreEqual(5.829f, angle, Tolerance);
        }

        [TestCase(0f, 0f)]
        [TestCase(55f, -30f)]
        [TestCase(-65f, 40f)]
        public void HeadYawIgnoresPitchAndRoll(float pitch, float roll)
        {
            Assert.AreEqual(38f, PushCueMath.HeadYaw(HeadRotation(pitch, 38f, roll), 17f), Tolerance);
        }

        [TestCase(1f)]
        [TestCase(-1f)]
        public void VerticalHeadKeepsLastYaw(float vertical)
        {
            Assert.AreEqual(31f, PushCueMath.HeadYaw(HeadRotation(90f * vertical, 120f, 0f), 31f));
        }

        [Test]
        public void PoseIsAboveTheCurrentEyeAndPointsAlongTheSeparatePushYaw()
        {
            Vector3 highEye = new Vector3(7f, 2f, 9f);
            Vector3 lowEye = new Vector3(7f, 0.35f, 9f);
            float firstScale;
            Quaternion rotation;
            Vector3 first = Evaluate(highEye, 0f, 90f, PushCueMath.DefaultDistanceMeters,
                out rotation, out firstScale);
            Vector3 offset = first - highEye;
            Vector3 direction = rotation * Vector3.forward;
            Assert.AreEqual(1.6f, offset.magnitude, Tolerance);
            Assert.AreEqual(12f, Mathf.Atan2(offset.y, offset.z) * Mathf.Rad2Deg, Tolerance);
            Assert.AreEqual(0f, offset.x, Tolerance);
            Assert.AreEqual(1f, direction.x, Tolerance);
            Assert.AreEqual(0f, direction.y, Tolerance);
            Assert.AreEqual(0f, direction.z, Tolerance);

            float secondScale;
            Vector3 second = Evaluate(lowEye, 90f, 0f, PushCueMath.DefaultDistanceMeters,
                out rotation, out secondScale);
            Assert.AreEqual(offset.z, second.x - lowEye.x, Tolerance);
            Assert.AreEqual(offset.y, second.y - lowEye.y, Tolerance);
            Assert.AreEqual(0f, second.z - lowEye.z, Tolerance);
            Assert.AreEqual(firstScale, secondScale);
            Assert.AreEqual(Vector3.forward, rotation * Vector3.forward);
        }

        [TestCase(1.2f)]
        [TestCase(1.6f)]
        [TestCase(2f)]
        public void NominalAngularSizeIsConstantAcrossDistances(float distance)
        {
            float scale;
            Quaternion rotation;
            Vector3 center = Evaluate(Vector3.zero, 0f, 90f, distance, out rotation, out scale);
            Vector3 half = rotation * Vector3.forward * (0.5f * scale);
            float angle = Vector3.Angle(center - half, center + half);
            Assert.AreEqual(4f, angle, 0.001f);
        }

        [Test]
        public void InvalidPoseIsRejectedAtomically()
        {
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsFalse(PushCueMath.TryEvaluate(Vector3.zero, 0f, float.NaN,
                1.6f, 12f, 4f, out position, out rotation, out scale));
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

        // Unity 的 Z、X、Y 旋转顺序；用托管乘法构造输入，让本地 NUnit 也能跑。
        private static Quaternion HeadRotation(float pitch, float yaw, float roll)
        {
            const float halfRadians = Mathf.Deg2Rad * 0.5f;
            float x = pitch * halfRadians, y = yaw * halfRadians, z = roll * halfRadians;
            return new Quaternion(0f, Mathf.Sin(y), 0f, Mathf.Cos(y))
                * new Quaternion(Mathf.Sin(x), 0f, 0f, Mathf.Cos(x))
                * new Quaternion(0f, 0f, Mathf.Sin(z), Mathf.Cos(z));
        }

        private static Vector3 Evaluate(Vector3 eye, float anchorYaw, float pushYaw, float distance,
            out Quaternion rotation, out float scale)
        {
            Vector3 position;
            Assert.IsTrue(PushCueMath.TryEvaluate(eye, anchorYaw, pushYaw, distance,
                PushCueMath.DefaultElevationDegrees, PushCueMath.DefaultAngularSizeDegrees,
                out position, out rotation, out scale));
            return position;
        }
    }
}
