using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Core;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the follow-rider contract: the frame sits BEHIND and ABOVE the ball, both
    /// distances grow linearly with the ball's radius, the frame has position and yaw only,
    /// the caller owns the previous filtered state, and a teleport snap cannot be
    /// interpolated. Yaw inputs and outputs are degrees about +Y; positions are metres, +Y up.
    /// </summary>
    /// <remarks>
    /// Expected values are hand calculations from the documented model
    /// height = eyeBaseUpMeters + radius * eyeUpPerRadius and
    /// back = eyeBaseBackMeters + radius * eyeBackPerRadius, with the committed defaults
    /// written as literals below so a changed default fails a test instead of silently moving
    /// the expectation. The four diameters (0.10, 0.30, 0.72, 1.43 m) are the sizes the
    /// design is judged at: a ball can start small, and 1.43 m is where the old fixed eye
    /// height buried the view in absorbed props.
    /// </remarks>
    public class FollowRigTests
    {
        private const float Tolerance = 1e-5f;

        // The diameters the framing is judged at. The first is the "small ball" start the
        // design wants; the last is the measured worst case.
        private static readonly float[] TestDiameters = { 0.10f, 0.30f, 0.72f, 1.43f };

        // Test-only stand-in for the caller-owned state; the runtime API is stateless and
        // takes this state back as arguments.
        private sealed class RigHarness
        {
            // The motor's committed defaults. If these change, every expectation below must
            // be recalculated - that is the point of keeping them here.
            public const float BaseUp = 0.55f;
            public const float UpPerRadius = 2.4f;
            public const float UpMin = 0.1f;
            public const float UpMax = 8f;
            public const float BaseBack = 0.75f;
            public const float BackPerRadius = 2.2f;
            public const float MaxBack = 8f;

            public float HeightFilterHz;
            public float YawFilterHz;
            public bool HasFrame;
            public Vector3 Position;
            public float Yaw;
            public float Height;

            public bool Update(Vector3 ballCenter, float radius, float yawDegrees, float dtSeconds,
                bool reset = false)
            {
                return UpdateWith(ballCenter, radius, yawDegrees, dtSeconds,
                    BaseUp, UpPerRadius, UpMin, UpMax,
                    BaseBack, BackPerRadius, MaxBack, reset);
            }

            public bool UpdateWith(Vector3 ballCenter, float radius, float yawDegrees, float dtSeconds,
                float baseUp, float upPerRadius, float upMin, float upMax,
                float baseBack, float backPerRadius, float maxBack, bool reset = false)
            {
                Vector3 position;
                float yaw;
                float height;
                bool accepted = FollowRigMath.TryUpdate(
                    HeightFilterHz, YawFilterHz,
                    ballCenter, radius, yawDegrees, dtSeconds,
                    baseUp, upPerRadius, upMin, upMax,
                    baseBack, backPerRadius, maxBack,
                    reset, HasFrame, Yaw, Height,
                    out position, out yaw, out height);
                if (!accepted) return false;
                Position = position;
                Yaw = yaw;
                Height = height;
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
            Assert.IsTrue(forward.Update(Vector3.zero, 0.15f, 359f, 0.01f));
            float first = forward.Yaw;
            Assert.IsTrue(forward.Update(Vector3.zero, 0.15f, 1f, 0.05f));
            float step = ShortestDelta(forward.Yaw, first);
            Assert.Greater(step, 0f);
            Assert.Less(step, 2f);
            Assert.AreEqual(0.5392f, step, 1e-3f);

            RigHarness reverse = new RigHarness();
            reverse.HeightFilterHz = 0f;
            reverse.YawFilterHz = 1f;
            Assert.IsTrue(reverse.Update(Vector3.zero, 0.15f, 1f, 0.01f));
            first = reverse.Yaw;
            Assert.IsTrue(reverse.Update(Vector3.zero, 0.15f, 359f, 0.05f));
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
            // First sample snaps: 0.15 m radius, so 0.75 + 2.2 * 0.15 = 1.08 m back and
            // 0.55 + 2.4 * 0.15 = 0.91 m above the centre.
            Assert.IsTrue(rig.Update(Vector3.zero, 0.15f, 0f, 0.1f));
            AssertVector(new Vector3(0f, 0.91f, -1.08f), rig.Position);

            Assert.IsTrue(rig.Update(new Vector3(10f, 2f, -6f), 0.15f, 90f, 0.1f));
            Assert.Greater(rig.Position.x, 0f);
            Assert.Less(rig.Position.x, 10f);
            Assert.Less(rig.Position.z, -6f);
            Assert.Less(rig.Position.z, 0f);
            Assert.Greater(rig.Yaw, 0f);
            Assert.Less(rig.Yaw, 90f);
            Assert.GreaterOrEqual(rig.Position.y - 2f, RigHarness.UpMin);
            Assert.LessOrEqual(rig.Position.y - 2f, RigHarness.UpMax);

            for (int i = 0; i < 300; i++)
            {
                Assert.IsTrue(rig.Update(new Vector3(10f, 2f, -6f), 0.15f, 90f, 1f / 60f));
            }
            // yaw = 90 puts the back offset on -x: x = 10 - 1.08, z = -6.
            Assert.AreEqual(10f - 1.08f, rig.Position.x, Tolerance);
            Assert.AreEqual(-6f, rig.Position.z, Tolerance);
            Assert.AreEqual(0.91f, rig.Position.y - 2f, Tolerance);
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
            Assert.IsTrue(rig.Update(new Vector3(2f, 1f, 3f), 0.15f, 15f, 0.1f));

            Assert.IsTrue(rig.Update(new Vector3(20f, 4f, -10f), 0.15f, 30f, 0.01f));
            Assert.Less(rig.Position.x, 20f);
            Assert.Less(Mathf.Abs(ShortestDelta(30f, rig.Yaw)), 30f);

            // The 1.4 m rail bounds the CONSTANT part: base 2.0 -> 1.4, so the height is
            // 1.4 + 2.4 * 0.5 = 2.6 m, and 0.75 + 2.2 * 0.5 = 1.85 m of back offset at yaw
            // 270 (sin = -1, cos = 0) lands entirely on +x: x = -90 + 1.85, z = 120,
            // y = -4 + 2.6.
            Assert.IsTrue(rig.UpdateWith(new Vector3(-90f, -4f, 120f), 0.5f, 270f, 0f,
                2f, 2.4f, 0.6f, 1.4f, 0.75f, 2.2f, 8f, true));
            Assert.AreEqual(-88.15f, rig.Position.x, Tolerance);
            Assert.AreEqual(-1.4f, rig.Position.y, Tolerance);
            Assert.AreEqual(120f, rig.Position.z, Tolerance);
            Assert.AreEqual(270f, rig.Yaw, Tolerance);

            // The returned frame is the new baseline: feeding it back with dt = 0
            // must reproduce it and not fall back to the pre-teleport frame.
            Vector3 position;
            float yaw;
            float height;
            Assert.IsTrue(FollowRigMath.TryUpdate(0.5f, 0.5f,
                new Vector3(-90f, -4f, 120f), 0.5f, 270f, 0f,
                2f, 2.4f, 0.6f, 1.4f, 0.75f, 2.2f, 8f,
                false, true, rig.Yaw, rig.Height,
                out position, out yaw, out height));
            AssertVector(rig.Position, position);
            Assert.AreEqual(270f, yaw, 0f);
            Assert.AreEqual(2.6f, height, 0f);
        }

        [Test]
        public void HeightBoundsAreRailsNotTuning()
        {
            // 100 Hz over 0.2 s gives a full-weight step, so the height lands on the target
            // immediately. The max rail bounds the author-supplied CONSTANT part (the min
            // rail is a floor on the total height above the centre); the rails exist to
            // catch a mis-authored number, not to shape the normal range.
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 100f;
            rig.YawFilterHz = 0f;

            Assert.IsTrue(rig.UpdateWith(new Vector3(0f, 5f, 0f), 0.5f, 0f, 0.2f,
                100f, 0f, 0.5f, 1.5f, 0.75f, 2.2f, 8f));
            Assert.AreEqual(1.5f, rig.Position.y - 5f, Tolerance);

            Assert.IsTrue(rig.UpdateWith(new Vector3(0f, 5f, 0f), 0.5f, 0f, 0.2f,
                0.1f, 0f, 0.5f, 1.5f, 0.75f, 2.2f, 8f));
            Assert.AreEqual(0.5f, rig.Position.y - 5f, Tolerance);

            Assert.IsTrue(rig.UpdateWith(new Vector3(0f, 5f, 0f), 0.5f, 0f, 0.2f,
                1f, 0f, 0.5f, 1.5f, 0.75f, 2.2f, 8f));
            Assert.AreEqual(1f, rig.Position.y - 5f, Tolerance);
        }

        [Test]
        public void CameraIsBehindAndAboveAndBothGrowWithTheBall()
        {
            // The four judged diameters: height = 0.55 + 2.4 r, back = 0.75 + 2.2 r.
            float[] expectedHeight = { 0.67f, 0.91f, 1.414f, 2.266f };
            float[] expectedBack = { 0.86f, 1.08f, 1.542f, 2.323f };
            float previousHeight = -1f;
            float previousBack = -1f;
            float previousGap = -1f;

            for (int i = 0; i < TestDiameters.Length; i++)
            {
                float radius = TestDiameters[i] * 0.5f;
                RigHarness rig = new RigHarness();
                rig.HeightFilterHz = 0f;
                rig.YawFilterHz = 0f;
                // A snap keeps this a pure model check: no filter lag in the expectation.
                Assert.IsTrue(rig.Update(new Vector3(0f, radius, 0f), radius, 0f, 0f, true));

                Assert.AreEqual(radius + expectedHeight[i], rig.Position.y, Tolerance,
                    "height above the centre at d=" + TestDiameters[i]);
                Assert.AreEqual(-expectedBack[i], rig.Position.z, Tolerance,
                    "back offset at d=" + TestDiameters[i]);
                Assert.AreEqual(0f, rig.Position.x, Tolerance);

                // The property the old fixed height lost: the clearance above the SURFACE
                // (height - radius) must not shrink as the ball grows.
                float gap = rig.Position.y - radius - radius;
                Assert.Greater(gap, previousGap,
                    "surface clearance must grow with the ball at d=" + TestDiameters[i]);
                Assert.Greater(rig.Position.y - radius, previousHeight);
                Assert.Greater(expectedBack[i], previousBack);
                previousHeight = rig.Position.y - radius;
                previousBack = expectedBack[i];
                previousGap = gap;
            }
        }

        [Test]
        public void SmallBallIsPlayedFromJustAboveTheFloor()
        {
            // A 0.10 m ball must not lift the rider to a standing person's eye height, and the
            // eye must not hug or cross the floor. 0.55 + 2.4 * 0.05 = 0.67 m above the centre
            // is 0.72 m above the floor for a grounded ball, with 0.62 m of clearance above the
            // ball's top.
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 0f;
            rig.YawFilterHz = 0f;
            Assert.IsTrue(rig.Update(new Vector3(0f, 0.05f, 0f), 0.05f, 0f, 0f, true));

            Assert.AreEqual(0.67f, rig.Position.y - 0.05f, Tolerance);
            Assert.Less(rig.Position.y - 0.05f, 0.9f, "not a standing eye height");
            Assert.Greater(rig.Position.y, 0.5f, "eye must clear the floor");
            Assert.Greater(rig.Position.y - 0.05f - 0.05f, 0f, "eye must clear the ball's top");
        }

        [Test]
        public void LargeBallIsNotPinnedByTheHeightRails()
        {
            // The max rails bound the CONSTANT term, not radius * factor: with the stance
            // base 0.55 and per-radius 2.4, a 2.5 m rail cannot pin the radius term, so the
            // same 3.19 m height comes out at radius 1.1. A too-large BASE is what a rail
            // catches: base 3.0 with a 2.5 m rail becomes 2.5 + 2.4 * 1.1 = 5.14 m.
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 0f;
            rig.YawFilterHz = 0f;
            Assert.IsTrue(rig.Update(new Vector3(0f, 1.1f, 0f), 1.1f, 0f, 0f, true));
            Assert.AreEqual(3.19f, rig.Height, Tolerance);

            RigHarness railAtProportional = new RigHarness();
            railAtProportional.HeightFilterHz = 0f;
            railAtProportional.YawFilterHz = 0f;
            Assert.IsTrue(railAtProportional.UpdateWith(new Vector3(0f, 1.1f, 0f), 1.1f, 0f, 0f,
                0.55f, 2.4f, 0.1f, 2.5f, 0.75f, 2.2f, 8f, true));
            Assert.AreEqual(3.19f, railAtProportional.Height, Tolerance);

            RigHarness clampedBase = new RigHarness();
            clampedBase.HeightFilterHz = 0f;
            clampedBase.YawFilterHz = 0f;
            Assert.IsTrue(clampedBase.UpdateWith(new Vector3(0f, 1.1f, 0f), 1.1f, 0f, 0f,
                3f, 2.4f, 0.1f, 2.5f, 0.75f, 2.2f, 8f, true));
            Assert.AreEqual(5.14f, clampedBase.Height, Tolerance);
        }

        [Test]
        public void TheEyeStaysOutsideTheBallAtTheGlobalSizeCap()
        {
            // The push stance (1.900108 r up, 1.041795 r back, no constant) at the global
            // 400 m diameter cap, r = 200 m, with the scene's 128 m rails: a fixed metre
            // ceiling would pin the eye inside the ball, but the rails bound only the
            // constants, so the frame is 380.022 m up and 208.359 m back - outside.
            RigHarness stance = new RigHarness();
            stance.HeightFilterHz = 0f;
            stance.YawFilterHz = 0f;
            Assert.IsTrue(stance.UpdateWith(new Vector3(0f, 200f, 0f), 200f, 0f, 0f,
                0f, 1.900108f, 0.1f, 128f, 0f, 1.041795f, 128f, true));
            Assert.AreEqual(200f + 1.900108f * 200f, stance.Position.y, 1e-3f);
            Assert.AreEqual(-(1.041795f * 200f), stance.Position.z, 1e-3f);
            Assert.Greater(Mathf.Sqrt(
                (stance.Position.y - 200f) * (stance.Position.y - 200f) +
                stance.Position.z * stance.Position.z), 200f);

            // The motor's own defaults (0.55 up / 2.4 per radius, 0.75 back / 2.2 per
            // radius) are outside the ball at the same size.
            RigHarness motor = new RigHarness();
            motor.HeightFilterHz = 0f;
            motor.YawFilterHz = 0f;
            Assert.IsTrue(motor.Update(new Vector3(0f, 200f, 0f), 200f, 0f, 0f, true));
            Assert.AreEqual(0.55f + 2.4f * 200f, motor.Position.y - 200f, 1e-3f);
            Assert.AreEqual(-(0.75f + 2.2f * 200f), motor.Position.z, 1e-3f);
            Assert.Greater(Mathf.Sqrt((motor.Position.y - 200f) * (motor.Position.y - 200f) +
                motor.Position.z * motor.Position.z), 200f);
        }

        [Test]
        public void LevelViewKeepsTheBallInTheLowerPartOfTheFrame()
        {
            // The pitch is the player's mouse, so the framing has to work at pitch 0: the top
            // edge of the ball plus its prop shell must sit between 10 and 35 degrees BELOW the
            // horizon. Below 10 the props crowd the middle of a level view; above 35 the ball
            // would only be visible by looking down. The shell is the ball plus the deepest
            // prop the absorb gate allows (an item as big as the radius), capped at the scene's
            // 0.6 m largest prop.
            for (int i = 0; i < TestDiameters.Length; i++)
            {
                float radius = TestDiameters[i] * 0.5f;
                float height = RigHarness.BaseUp + radius * RigHarness.UpPerRadius;
                float back = RigHarness.BaseBack + radius * RigHarness.BackPerRadius;
                float distance = Mathf.Sqrt(height * height + back * back);
                float shell = radius + Mathf.Min(0.6f, radius);
                float centerPitch = Mathf.Atan2(height, back) * Mathf.Rad2Deg;
                float shellAngle = Mathf.Asin(shell / distance) * Mathf.Rad2Deg;
                float topEdge = centerPitch - shellAngle;

                Assert.GreaterOrEqual(topEdge, 10f, "ball+shell too high in a level view at d="
                    + TestDiameters[i]);
                Assert.LessOrEqual(topEdge, 35f, "ball+shell too low in a level view at d="
                    + TestDiameters[i]);
                // The ball itself has to stay visible, not just the shell.
                Assert.Greater(centerPitch - Mathf.Asin(radius / distance) * Mathf.Rad2Deg, 5f);
            }
        }

        [Test]
        public void InvalidInputsAreRejectedAtomically()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 5f;
            rig.YawFilterHz = 8f;
            Assert.IsTrue(rig.Update(new Vector3(1f, 2f, 3f), 0.15f, 45f, 0.1f));
            Vector3 oldPosition = rig.Position;
            float oldYaw = rig.Yaw;
            float oldHeight = rig.Height;

            Assert.IsFalse(rig.Update(new Vector3(float.NaN, 0f, 0f), 0.15f, 0f, 0.1f));
            Assert.IsFalse(rig.Update(Vector3.zero, 0.15f, float.PositiveInfinity, 0.1f));
            Assert.IsFalse(rig.Update(Vector3.zero, 0.15f, 0f, -0.1f));
            Assert.IsFalse(rig.Update(Vector3.zero, float.NaN, 0f, 0.1f));
            Assert.IsFalse(rig.Update(Vector3.zero, -0.15f, 0f, 0.1f));
            Assert.IsFalse(rig.Update(Vector3.zero, float.PositiveInfinity, 0f, 0.1f));
            Assert.IsFalse(rig.Update(Vector3.zero, 0.15f, float.NaN, 0.1f));
            Assert.IsFalse(rig.UpdateWith(Vector3.zero, 0.15f, 0f, 0.1f,
                float.NaN, 2.4f, 0.1f, 8f, 0.75f, 2.2f, 8f));
            Assert.IsFalse(rig.UpdateWith(Vector3.zero, 0.15f, 0f, 0.1f,
                -0.55f, 2.4f, 0.1f, 8f, 0.75f, 2.2f, 8f));
            Assert.IsFalse(rig.UpdateWith(Vector3.zero, 0.15f, 0f, 0.1f,
                0.55f, -2.4f, 0.1f, 8f, 0.75f, 2.2f, 8f));
            Assert.IsFalse(rig.UpdateWith(Vector3.zero, 0.15f, 0f, 0.1f,
                0.55f, 2.4f, 8f, 0.1f, 0.75f, 2.2f, 8f));
            Assert.IsFalse(rig.UpdateWith(Vector3.zero, 0.15f, 0f, 0.1f,
                0.55f, 2.4f, 0.1f, 8f, -0.75f, 2.2f, 8f));
            Assert.IsFalse(rig.UpdateWith(Vector3.zero, 0.15f, 0f, 0.1f,
                0.55f, 2.4f, 0.1f, 8f, 0.75f, 2.2f, -8f));

            AssertVector(oldPosition, rig.Position);
            Assert.AreEqual(oldYaw, rig.Yaw, 0f);
            Assert.AreEqual(oldHeight, rig.Height, 0f);
        }

        [Test]
        public void HugeFilterProductIsRejectedWithoutPartialCommit()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 3e38f;
            rig.YawFilterHz = 1f;
            Assert.IsTrue(rig.Update(Vector3.zero, 0.15f, 0f, 0f));
            Vector3 oldPosition = rig.Position;
            float oldYaw = rig.Yaw;
            float oldHeight = rig.Height;

            // 2*pi*3e38 overflows a float to infinity, so the call is refused
            // instead of committing a candidate frame.
            Assert.IsFalse(rig.Update(new Vector3(10f, 0f, 0f), 0.15f, 90f, 3e38f));
            AssertVector(oldPosition, rig.Position);
            Assert.AreEqual(oldYaw, rig.Yaw, 0f);
            Assert.AreEqual(oldHeight, rig.Height, 0f);
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

            // The filtered state lives in the caller, not in static fields. Compile-time
            // constants (the comfort tuning) are not state.
            FieldInfo[] fields = typeof(FollowRigMath).GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
                Assert.IsTrue(fields[i].IsLiteral, fields[i].Name + " is static state");
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
                float radius = 0.05f + (i % 7) * 0.1f;
                bool hadFrame = rig.HasFrame;
                float previousYaw = rig.Yaw;

                Assert.IsTrue(rig.Update(new Vector3(x, ballY, z), radius, yaw, 1f / 90f));
                Assert.IsTrue(KatamariMath.IsFiniteVector(rig.Position));
                Assert.IsTrue(KatamariMath.IsFiniteFloat(rig.Yaw));
                Assert.GreaterOrEqual(rig.Height, RigHarness.UpMin - Tolerance);
                Assert.LessOrEqual(rig.Height, RigHarness.UpMax + Tolerance);
                Assert.AreEqual(ballY + rig.Height, rig.Position.y, Tolerance);

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
            Assert.IsTrue(rig.Update(new Vector3(3f, 1f, -2f), 0.15f, 47f, 0.1f));
            Vector3 framePosition = rig.Position;
            float frameYaw = rig.Yaw;
            float frameHeight = rig.Height;

            Assert.IsTrue(rig.Update(new Vector3(3f, 1f, -2f), 0.15f, 47f, 0.1f));
            AssertVector(framePosition, rig.Position);
            Assert.AreEqual(frameYaw, rig.Yaw, 0f);
            Assert.AreEqual(frameHeight, rig.Height, 0f);
        }

        [Test]
        public void TeleportSnapStaysBoundedAndStable()
        {
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 2f;
            rig.YawFilterHz = 2f;
            Assert.IsTrue(rig.Update(Vector3.zero, 0.15f, 0f, 0.1f));

            // A 600 m jump with the reset marker lands exactly on the new frame: at radius
            // 0.5 the frame is 0.75 + 2.2 * 0.5 = 1.85 m back (yaw 270 -> +x) and
            // 0.55 + 2.4 * 0.5 = 1.75 m up, so y = 20 + 1.75.
            Assert.IsTrue(rig.Update(new Vector3(500f, 20f, -300f), 0.5f, 270f, 0f, true));
            AssertVector(new Vector3(501.85f, 21.75f, -300f), rig.Position);
            Assert.AreEqual(270f, rig.Yaw, Tolerance);

            // The next normal sample recomputes the back offset from the current centre.
            Assert.IsTrue(rig.Update(new Vector3(501f, 20f, -300f), 0.5f, 270f, 1f / 60f));
            Assert.AreEqual(502.85f, rig.Position.x, Tolerance);
            Assert.AreEqual(270f, rig.Yaw, Tolerance);
        }

        // ---- 减弱自动转向（ComfortTargetYaw）----

        [Test]
        public void ComfortYawWithinOneStepReachesTheHeading()
        {
            // 45 度/秒 × 0.1 秒 = 4.5 度；差 3 度直接到位。
            Assert.AreEqual(13f, FollowRigMath.ComfortTargetYaw(10f, 13f, 45f, 60f, 0.1f), Tolerance);
        }

        [Test]
        public void ComfortYawIsRateLimitedTowardTheHeading()
        {
            // 差 20 度，本步只走 4.5 度。
            Assert.AreEqual(14.5f, FollowRigMath.ComfortTargetYaw(10f, 30f, 45f, 60f, 0.1f), Tolerance);
            Assert.AreEqual(5.5f, FollowRigMath.ComfortTargetYaw(10f, 350f, 45f, 60f, 0.1f), Tolerance);
        }

        [Test]
        public void ComfortYawTakesTheShortArcAcrossZero()
        {
            // 350 -> 10 是 +20 度，不是 -340。
            Assert.AreEqual(354.5f, FollowRigMath.ComfortTargetYaw(350f, 10f, 45f, 60f, 0.1f), Tolerance);
        }

        [Test]
        public void ComfortYawNeverLagsMoreThanTheCap()
        {
            // 球一步转了 100 度：限速后还差 95.5 度，夹到 60 度落后。
            Assert.AreEqual(50f, FollowRigMath.ComfortTargetYaw(10f, 110f, 45f, 60f, 0.1f), Tolerance);
        }

        [Test]
        public void ComfortYawWithInvalidTuningFallsBackToTheHeading()
        {
            Assert.AreEqual(30f, FollowRigMath.ComfortTargetYaw(float.NaN, 30f, 45f, 60f, 0.1f), Tolerance);
            Assert.AreEqual(30f, FollowRigMath.ComfortTargetYaw(10f, 30f, -1f, 60f, 0.1f), Tolerance);
            Assert.AreEqual(30f, FollowRigMath.ComfortTargetYaw(10f, 390f, 45f, -1f, 0.1f), Tolerance);
            Assert.AreEqual(0f, FollowRigMath.ComfortTargetYaw(10f, float.PositiveInfinity, 45f, 60f, 0.1f));
        }

        [Test]
        public void ComfortYawAtZeroDtHoldsThePreviousTarget()
        {
            Assert.AreEqual(10f, FollowRigMath.ComfortTargetYaw(10f, 30f, 45f, 60f, 0f), Tolerance);
        }

        [Test]
        public void ComfortTuningIsSlowerThanTheBallTurnRate()
        {
            // 马达默认转向 110 度/秒；舒适速率必须更慢，否则开关没有可见差别。
            Assert.Less(FollowRigMath.ComfortYawRateDegreesPerSecond, 110f);
            Assert.Greater(FollowRigMath.ComfortMaxLagDegrees, 0f);
        }

        // Same shortest-arc helper as the Python test module, used only to state
        // the expected wrap direction.
        private static float ShortestDelta(float target, float current)
        {
            // C# % keeps the sign of its left operand, so a target behind the
            // current angle by more than 180 deg produced -358 deg instead of +1.2.
            float delta = (target - current + 180f) % 360f;
            if (delta < 0f) delta += 360f;
            return delta - 180f;
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }
    }
}
