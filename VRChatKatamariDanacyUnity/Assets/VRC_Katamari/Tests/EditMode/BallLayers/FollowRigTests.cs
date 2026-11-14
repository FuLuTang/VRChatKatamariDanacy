using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Core;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_follow_rig/test_follow_rig.py plus the
    /// layer invariants: the frame has position and yaw only, the caller owns the
    /// previous filtered state, and a teleport snap cannot be interpolated. Yaw
    /// inputs and outputs are degrees about +Y; positions are metres, +Y up.
    /// Expected values are hand calculations (filter alpha = 1 - exp(-2*pi*hz*dt))
    /// or invariants, not re-runs of the implementation formula.
    /// </summary>
    public class FollowRigTests
    {
        private const float Tolerance = 1e-5f;

        // Test-only stand-in for the Python object's stored state; the runtime
        // API is stateless and takes this state back as arguments.
        private sealed class RigHarness
        {
            public float HeightFilterHz;
            public float YawFilterHz;
            public bool HasFrame;
            public Vector3 Position;
            public float Yaw;
            public float EyeOffset;

            public bool Update(Vector3 ballPosition, float yawDegrees, float dtSeconds,
                float eyeHeight, float eyeMin, float eyeMax, bool reset = false)
            {
                Vector3 position;
                float yaw;
                float offset;
                bool accepted = FollowRigMath.TryUpdate(
                    HeightFilterHz, YawFilterHz,
                    ballPosition, yawDegrees, dtSeconds, eyeHeight, eyeMin, eyeMax,
                    reset, HasFrame, Position, Yaw, EyeOffset,
                    out position, out yaw, out offset);
                if (!accepted) return false;
                Position = position;
                Yaw = yaw;
                EyeOffset = offset;
                HasFrame = true;
                return true;
            }
        }

        [Test]
        public void HeadingWrapUsesShortArcInBothDirections()
        {
            // 359 -> 1 is a +2 deg arc, not a -358 deg one. The first sample snaps,
            // so dt only matters for the second: alpha = 1 - exp(-2*pi*1*0.05) =
            // 0.2696, giving a 2 * 0.2696 = 0.539 deg step.
            RigHarness forward = new RigHarness();
            forward.HeightFilterHz = 0f;
            forward.YawFilterHz = 1f;
            Assert.IsTrue(forward.Update(Vector3.zero, 359f, 0.01f, 1f, 0.4f, 1.8f));
            float first = forward.Yaw;
            Assert.IsTrue(forward.Update(Vector3.zero, 1f, 0.05f, 1f, 0.4f, 1.8f));
            float step = ShortestDelta(forward.Yaw, first);
            Assert.Greater(step, 0f);
            Assert.Less(step, 2f);
            Assert.AreEqual(0.5392f, step, 1e-3f);

            RigHarness reverse = new RigHarness();
            reverse.HeightFilterHz = 0f;
            reverse.YawFilterHz = 1f;
            Assert.IsTrue(reverse.Update(Vector3.zero, 1f, 0.01f, 1f, 0.4f, 1.8f));
            first = reverse.Yaw;
            Assert.IsTrue(reverse.Update(Vector3.zero, 359f, 0.05f, 1f, 0.4f, 1.8f));
            step = ShortestDelta(reverse.Yaw, first);
            Assert.Less(step, 0f);
            Assert.Greater(step, -2f);
            Assert.AreEqual(-0.5392f, step, 1e-3f);
        }

        [Test]
        public void FiltersAreBoundedAndDoNotOvershoot()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 2f;
            rig.YawFilterHz = 3f;
            Assert.IsTrue(rig.Update(Vector3.zero, 0f, 0.1f, 1f, 0.4f, 1.8f));

            Assert.IsTrue(rig.Update(new Vector3(10f, 2f, -6f), 90f, 0.1f, 1.7f, 0.4f, 1.8f));
            Assert.Greater(rig.Position.x, 0f);
            Assert.Less(rig.Position.x, 10f);
            Assert.Greater(rig.Position.z, -6f);
            Assert.Less(rig.Position.z, 0f);
            Assert.Greater(rig.Yaw, 0f);
            Assert.Less(rig.Yaw, 90f);
            Assert.GreaterOrEqual(rig.Position.y - 2f, 0.4f);
            Assert.LessOrEqual(rig.Position.y - 2f, 1.8f);

            for (int i = 0; i < 300; i++)
            {
                Assert.IsTrue(rig.Update(new Vector3(10f, 2f, -6f), 90f, 1f / 60f,
                    1.7f, 0.4f, 1.8f));
            }
            Assert.AreEqual(10f, rig.Position.x, Tolerance);
            Assert.AreEqual(-6f, rig.Position.z, Tolerance);
            Assert.AreEqual(1.7f, rig.Position.y - 2f, Tolerance);
            // The yaw filter rounds out one float step below 90 (ulp(90) is
            // 7.6e-6), so the converged yaw is one ulp short at float precision.
            Assert.AreEqual(90f, rig.Yaw, 2f * Tolerance);
        }

        [Test]
        public void RespawnOrTeleportMarkerSnapsAndRestartsFilterBaseline()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 0.5f;
            rig.YawFilterHz = 0.5f;
            Assert.IsTrue(rig.Update(new Vector3(2f, 1f, 3f), 15f, 0.1f, 1f, 0.5f, 1.5f));

            Assert.IsTrue(rig.Update(new Vector3(20f, 4f, -10f), 30f, 0.01f, 1f, 0.5f, 1.5f));
            Assert.Less(rig.Position.x, 20f);
            Assert.Less(Mathf.Abs(ShortestDelta(30f, rig.Yaw)), 30f);

            // clamp(8, 0.6, 1.4) = 1.4, so the frame snaps to y = -4 + 1.4.
            Assert.IsTrue(rig.Update(new Vector3(-90f, -4f, 120f), 271f, 0f, 8f, 0.6f, 1.4f, true));
            Assert.AreEqual(-90f, rig.Position.x, Tolerance);
            Assert.AreEqual(-2.6f, rig.Position.y, Tolerance);
            Assert.AreEqual(120f, rig.Position.z, Tolerance);
            Assert.AreEqual(271f, rig.Yaw, Tolerance);

            // The returned frame is the new baseline: feeding it back with dt = 0
            // must reproduce it and not fall back to the pre-teleport frame.
            Vector3 position;
            float yaw;
            float offset;
            Assert.IsTrue(FollowRigMath.TryUpdate(0.5f, 0.5f,
                new Vector3(-90f, -4f, 120f), 271f, 0f, 8f, 0.6f, 1.4f,
                false, true, rig.Position, rig.Yaw, rig.EyeOffset,
                out position, out yaw, out offset));
            AssertVector(rig.Position, position);
            Assert.AreEqual(271f, yaw, 0f);
            Assert.AreEqual(1.4f, offset, 0f);
        }

        [Test]
        public void EyeHeightIsClampedToExplicitFiniteLimits()
        {
            // 100 Hz over 0.2 s gives a full-weight step, so the offset lands on
            // the clamped target immediately; the bounds are caller inputs.
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 100f;
            rig.YawFilterHz = 0f;

            Assert.IsTrue(rig.Update(new Vector3(0f, 5f, 0f), 0f, 0.2f, -100f, 0.75f, 1.25f));
            Assert.AreEqual(0.75f, rig.Position.y - 5f, Tolerance);
            Assert.IsTrue(rig.Update(new Vector3(0f, 5f, 0f), 0f, 0.2f, 100f, 0.75f, 1.25f));
            Assert.AreEqual(1.25f, rig.Position.y - 5f, Tolerance);
            Assert.IsTrue(rig.Update(new Vector3(0f, 5f, 0f), 0f, 0.2f, 1f, 0.75f, 1.25f));
            Assert.AreEqual(1f, rig.Position.y - 5f, Tolerance);
        }

        [Test]
        public void InvalidInputsAreRejectedAtomically()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 5f;
            rig.YawFilterHz = 8f;
            Assert.IsTrue(rig.Update(new Vector3(1f, 2f, 3f), 45f, 0.1f, 1f, 0.5f, 1.5f));
            Vector3 oldPosition = rig.Position;
            float oldYaw = rig.Yaw;
            float oldOffset = rig.EyeOffset;

            Assert.IsFalse(rig.Update(new Vector3(float.NaN, 0f, 0f), 0f, 0.1f, 1f, 0.5f, 1.5f));
            Assert.IsFalse(rig.Update(Vector3.zero, float.PositiveInfinity, 0.1f, 1f, 0.5f, 1.5f));
            Assert.IsFalse(rig.Update(Vector3.zero, 0f, -0.1f, 1f, 0.5f, 1.5f));
            Assert.IsFalse(rig.Update(Vector3.zero, 0f, 0.1f, float.NaN, 0.5f, 1.5f));
            Assert.IsFalse(rig.Update(Vector3.zero, 0f, 0.1f, 1f, 2f, 1f));
            Assert.IsFalse(rig.Update(Vector3.zero, float.NaN, 0.1f, 1f, 0.5f, 1.5f, true));

            AssertVector(oldPosition, rig.Position);
            Assert.AreEqual(oldYaw, rig.Yaw, 0f);
            Assert.AreEqual(oldOffset, rig.EyeOffset, 0f);
            // The Python case also passed reset=1, an int, and expected a
            // ValueError; a C# bool parameter makes that call unrepresentable.
        }

        [Test]
        public void HugeFilterProductIsRejectedWithoutPartialCommit()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 3e38f;
            rig.YawFilterHz = 1f;
            Assert.IsTrue(rig.Update(Vector3.zero, 0f, 0f, 1f, 0.5f, 1.5f));
            Vector3 oldPosition = rig.Position;
            float oldYaw = rig.Yaw;
            float oldOffset = rig.EyeOffset;

            // 2*pi*3e38 overflows a float to infinity, so the call is refused
            // instead of committing a candidate frame.
            Assert.IsFalse(rig.Update(new Vector3(10f, 0f, 0f), 90f, 3e38f, 1f, 0.5f, 1.5f));
            AssertVector(oldPosition, rig.Position);
            Assert.AreEqual(oldYaw, rig.Yaw, 0f);
            Assert.AreEqual(oldOffset, rig.EyeOffset, 0f);
        }

        [Test]
        public void ApiHasNoRotationStateOrHmdInput()
        {
            MethodInfo method = typeof(FollowRigMath).GetMethod("TryUpdate");
            Assert.IsNotNull(method);
            Assert.IsTrue(method.IsPublic && method.IsStatic);
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                Assert.AreNotEqual(typeof(Quaternion), parameters[i].ParameterType,
                    "no rotation input is allowed");
            }

            int outputCount = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut) outputCount++;
            }
            Assert.AreEqual(3, outputCount);
            ParameterInfo[] outputs = new ParameterInfo[3];
            int writeIndex = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut) outputs[writeIndex++] = parameters[i];
            }
            Assert.AreEqual(typeof(Vector3), outputs[0].ParameterType.GetElementType());
            Assert.AreEqual(typeof(float), outputs[1].ParameterType.GetElementType());
            Assert.AreEqual(typeof(float), outputs[2].ParameterType.GetElementType());

            // The filtered state lives in the caller, not in static fields.
            FieldInfo[] fields = typeof(FollowRigMath).GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.AreEqual(0, fields.Length);
        }

        [Test]
        public void DeterministicLongSequenceRemainsFiniteAndHeightLimited()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 4f;
            rig.YawFilterHz = 6f;
            for (int i = 0; i < 1000; i++)
            {
                float x = Mathf.Sin(i * 0.013f) * 50f;
                float z = Mathf.Cos(i * 0.017f) * 30f;
                float yaw = (359f + i * 0.7f) % 360f;
                float ballY = (float)(i % 9) - 4f;
                bool hadFrame = rig.HasFrame;
                float previousYaw = rig.Yaw;

                Assert.IsTrue(rig.Update(new Vector3(x, ballY, z), yaw, 1f / 90f,
                    2f, 0.7f, 1.4f));
                Assert.IsTrue(KatamariMath.IsFiniteVector(rig.Position));
                Assert.IsTrue(KatamariMath.IsFiniteFloat(rig.Yaw));
                Assert.GreaterOrEqual(rig.EyeOffset, 0.7f);
                Assert.LessOrEqual(rig.EyeOffset, 1.4f);
                Assert.AreEqual(ballY + rig.EyeOffset, rig.Position.y, Tolerance);

                if (hadFrame)
                {
                    // A filtered yaw approaches its target monotonically: the
                    // remaining short-arc distance cannot grow.
                    float before = Mathf.Abs(ShortestDelta(yaw, previousYaw));
                    float after = Mathf.Abs(ShortestDelta(yaw, rig.Yaw));
                    Assert.LessOrEqual(after, before + 1e-3f);
                }
            }
        }

        [Test]
        public void RotatingTheBallInPlaceDoesNotChangeTheFrame()
        {
            // The API has no ball-rotation input at all: "rotating in place" is
            // the same call with the same position and heading, and the frame
            // must not drift or acquire roll/pitch.
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 3f;
            rig.YawFilterHz = 4f;
            Assert.IsTrue(rig.Update(new Vector3(3f, 1f, -2f), 47f, 0.1f, 1.2f, 0.5f, 1.8f));
            Vector3 framePosition = rig.Position;
            float frameYaw = rig.Yaw;
            float frameOffset = rig.EyeOffset;

            Assert.IsTrue(rig.Update(new Vector3(3f, 1f, -2f), 47f, 0.1f, 1.2f, 0.5f, 1.8f));
            AssertVector(framePosition, rig.Position);
            Assert.AreEqual(frameYaw, rig.Yaw, 0f);
            Assert.AreEqual(frameOffset, rig.EyeOffset, 0f);
        }

        [Test]
        public void TeleportSnapStaysBoundedAndStable()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 2f;
            rig.YawFilterHz = 2f;
            Assert.IsTrue(rig.Update(Vector3.zero, 0f, 0.1f, 1f, 0.5f, 1.5f));

            // A 600 m jump with the reset marker lands exactly on the new frame;
            // clamp(1, 0.5, 1.5) = 1, so y = 20 + 1.
            Assert.IsTrue(rig.Update(new Vector3(500f, 20f, -300f), 300f, 0f, 1f, 0.5f, 1.5f, true));
            AssertVector(new Vector3(500f, 21f, -300f), rig.Position);
            Assert.AreEqual(300f, rig.Yaw, Tolerance);

            // The next normal sample starts from the new baseline and stays
            // bounded between it and the target instead of restarting the lag.
            Assert.IsTrue(rig.Update(new Vector3(501f, 20f, -300f), 300f, 1f / 60f, 1f, 0.5f, 1.5f));
            Assert.Greater(rig.Position.x, 500f);
            Assert.Less(rig.Position.x, 501f);
            Assert.AreEqual(300f, rig.Yaw, Tolerance);
        }

        // Same shortest-arc helper as the Python test module, used only to state
        // the expected wrap direction.
        private static float ShortestDelta(float target, float current)
        {
            return (target - current + 180f) % 360f - 180f;
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }
    }
}
