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
                    reset, HasFrame, Position, Yaw, Height,
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
            Assert.Greater(rig.Position.z, -6f);
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

            // A 0.55 + 2.4 * 0.5 = 1.75 m height clamps to the caller's 1.4 m rail, and
            // 0.75 + 2.2 * 0.5 = 1.85 m of back offset at yaw 270 (sin = -1, cos = 0) lands
            // entirely on +x: x = -90 + 1.85, z = 120, y = -4 + 1.4.
            Assert.IsTrue(rig.UpdateWith(new Vector3(-90f, -4f, 120f), 0.5f, 270f, 0f,
                0.55f, 2.4f, 0.6f, 1.4f, 0.75f, 2.2f, 8f, true));
            Assert.AreEqual(-88.15f, rig.Position.x, Tolerance);
            Assert.AreEqual(-2.6f, rig.Position.y, Tolerance);
            Assert.AreEqual(120f, rig.Position.z, Tolerance);
            Assert.AreEqual(270f, rig.Yaw, Tolerance);

            // The returned frame is the new baseline: feeding it back with dt = 0
            // must reproduce it and not fall back to the pre-teleport frame.
            Vector3 position;
            float yaw;
            float height;
            Assert.IsTrue(FollowRigMath.TryUpdate(0.5f, 0.5f,
                new Vector3(-90f, -4f, 120f), 0.5f, 270f, 0f,
                0.55f, 2.4f, 0.6f, 1.4f, 0.75f, 2.2f, 8f,
                false, true, rig.Position, rig.Yaw, rig.Height,
                out position, out yaw, out height));
            AssertVector(rig.Position, position);
            Assert.AreEqual(270f, yaw, 0f);
            Assert.AreEqual(1.4f, height, 0f);
        }

        [Test]
        public void HeightBoundsAreRailsNotTuning()
        {
            // 100 Hz over 0.2 s gives a full-weight step, so the height lands on the clamped
            // target immediately. The bounds bound the height above the ball's CENTRE; they
            // exist to catch a mis-authored factor, not to shape the normal range.
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
            // The full size range ends at 2.2 m, i.e. radius 1.1: 0.55 + 2.4 * 1.1 = 3.19 m
            // above the centre, well inside the 8 m rail. A rail set at 2.5 must clamp it,
            // which is the rail doing its job rather than the model failing.
            RigHarness rig = new RigHarness();
            rig.HeightFilterHz = 0f;
            rig.YawFilterHz = 0f;
            Assert.IsTrue(rig.Update(new Vector3(0f, 1.1f, 0f), 1.1f, 0f, 0f, true));
            Assert.AreEqual(3.19f, rig.Height, Tolerance);

            RigHarness clamped = new RigHarness();
            clamped.HeightFilterHz = 0f;
            clamped.YawFilterHz = 0f;
            Assert.IsTrue(clamped.UpdateWith(new Vector3(0f, 1.1f, 0f), 1.1f, 0f, 0f,
                0.55f, 2.4f, 0.1f, 2.5f, 0.75f, 2.2f, 8f, true));
            Assert.AreEqual(2.5f, clamped.Height, Tolerance);
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

            // The next normal sample starts from the new baseline and stays
            // bounded between it and the target instead of restarting the lag.
            Assert.IsTrue(rig.Update(new Vector3(501f, 20f, -300f), 0.5f, 270f, 1f / 60f));
            Assert.Greater(rig.Position.x, 501.85f);
            Assert.Less(rig.Position.x, 502.85f);
            Assert.AreEqual(270f, rig.Yaw, Tolerance);
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
