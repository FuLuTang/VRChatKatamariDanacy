using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Diagnostics;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the DEV-ONLY ball camera's pure math: the behind-above offset, the
    /// framing it produces, the clip planes, and the refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expectations are hand calculations from the documented contract: 2.6 r behind the ball's
    /// heading, 1.4 r above its centre, aimed 0.4 r above the centre, at a 60 degree vertical
    /// field of view. Radii cover the real range, 0.15 m (the starting ball) to 30 m (a level
    /// whose ball has grown past the map).
    /// </para>
    /// <para>
    /// The framing assertions project with Unity's own perspective matrix rather than re-running
    /// the module: the point is that the documented aim lift lands the ball in the lower-middle
    /// third of the viewport whatever its size, including that the whole ball still fits.
    /// </para>
    /// <para>
    /// What EditMode cannot reach: the behaviour's own off-path (no camera or motor is touched
    /// while the switch is off) is a property of <c>BallFollowCamera</c>, which only runs as an
    /// Udon program; there is no scene, no Play mode and no Udon VM here. The module's side of it
    /// - that it keeps no state and takes every input as a parameter - is asserted below.
    /// </para>
    /// </remarks>
    public class DebugCameraTests
    {
        private const float FieldOfViewDegrees = 60f;
        private const float Aspect = 16f / 9f;

        // The checklist radii: the starting ball, a small ball, a mid-round ball, a big ball and
        // one past the largest map dimension this project plans for.
        private static readonly float[] Radii = { 0.15f, 1f, 5f, 15f, 30f };

        private static void Pose(Vector3 center, Vector3 forward, float radius,
            out Vector3 position, out Quaternion rotation, out float distance)
        {
            Assert.IsTrue(DebugCameraMath.TryComputePose(center, forward, radius, 2.6f, 1.4f, 0.4f,
                out position, out rotation, out distance), "radius " + radius);
        }

        // NDC -> viewport, with the perspective divide done here so the test does not depend on
        // which Unity multiply overload divides by w. For this view matrix clip.w is the
        // distance in front of the camera, so it is positive exactly when the point is visible.
        private static Vector2 Project(Matrix4x4 viewProjection, Vector3 world)
        {
            Vector4 clip = viewProjection * new Vector4(world.x, world.y, world.z, 1f);
            Assert.IsTrue(clip.w > 0f, "the point must be in front of the camera");
            return new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
        }

        private static Matrix4x4 ViewProjection(Vector3 position, Quaternion rotation,
            float nearM, float farM)
        {
            // Unity's camera space is OpenGL's: a camera looks down -Z, while a transform's
            // forward is +Z. Camera.worldToCameraMatrix is therefore the world-to-local matrix
            // with Z negated. A bare TRS(...).inverse is the object-space matrix, so every point
            // in front of the camera would get clip.w = -distance (negative) and the perspective
            // divide would mirror x and y as well.
            // Matrix4x4.Perspective takes the field of view in DEGREES (the same unit as
            // Camera.fieldOfView); passing FieldOfViewDegrees * Mathf.Deg2Rad, as this helper
            // once did, turns 60 degrees into a 1.047 degree cone - a projection 63x too narrow
            // (m11 109.4 instead of 1.732) that pushes every radius to viewport.y = -6.47.
            Matrix4x4 worldToLocal = Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * worldToLocal;
            Matrix4x4 projection = Matrix4x4.Perspective(FieldOfViewDegrees, Aspect, nearM, farM);
            // Pin the unit contract where it is cheapest to see: a 60 degree vertical field of
            // view has m11 = 1/tan(30 deg) = 1.7320508. A degree/radian mix-up makes it 109.4,
            // which otherwise only shows up as a ball projected far off screen.
            Assert.AreEqual(1.7320508f, projection.m11, 1e-4f);
            return projection * view;
        }

        [Test]
        public void PoseSitsBehindAndAboveAtTheDocumentedMultiples()
        {
            Vector3 center = new Vector3(3f, 2f, -1f);
            Vector3 forward = new Vector3(0f, 0f, 1f);

            for (int i = 0; i < Radii.Length; i++)
            {
                float radius = Radii[i];
                Vector3 position;
                Quaternion rotation;
                float distance;
                Pose(center, forward, radius, out position, out rotation, out distance);

                Vector3 offset = position - center;
                float tolerance = 1e-4f * radius;

                // Ahead is +Z, so "behind" is -Z; up is world +Y. A 2.6 r / 1.4 r rig therefore
                // puts the camera at (0, 1.4 r, -2.6 r) from the centre.
                Assert.AreEqual(0f, offset.x, tolerance, "radius " + radius);
                Assert.AreEqual(1.4f * radius, offset.y, tolerance, "radius " + radius);
                Assert.AreEqual(-2.6f * radius, offset.z, tolerance, "radius " + radius);

                // sqrt(2.6^2 + 1.4^2) = 2.95296..., the scale-invariant camera distance.
                Assert.AreEqual(2.9529645f * radius, distance, tolerance, "radius " + radius);
            }
        }

        [Test]
        public void PoseTurnsWithTheHeadingSoTheCameraStaysBehindIt()
        {
            Vector3 center = new Vector3(-2f, 0.5f, 4f);
            // Heading 90 degrees: the motor derives forward as (+sin, +cos), i.e. +X.
            Vector3 forward = new Vector3(1f, 0f, 0f);

            Vector3 position;
            Quaternion rotation;
            float distance;
            Pose(center, forward, 1f, out position, out rotation, out distance);

            Vector3 offset = position - center;
            Assert.AreEqual(-2.6f, offset.x, 1e-4f);
            Assert.AreEqual(1.4f, offset.y, 1e-4f);
            Assert.AreEqual(0f, offset.z, 1e-4f);
        }

        [Test]
        public void PoseAimsAboveTheBallCentreSoTheBallSitsLowInFrame()
        {
            Vector3 center = new Vector3(0f, 1f, 0f);
            Vector3 forward = new Vector3(0f, 0f, 1f);

            for (int i = 0; i < Radii.Length; i++)
            {
                float radius = Radii[i];
                Vector3 position;
                Quaternion rotation;
                float distance;
                Pose(center, forward, radius, out position, out rotation, out distance);

                Vector3 aim = center + Vector3.up * (0.4f * radius);
                Vector3 aimDirection = (aim - position).normalized;
                Vector3 cameraForward = rotation * Vector3.forward;
                Assert.AreEqual(1f, Vector3.Dot(cameraForward, aimDirection), 1e-4f,
                    "radius " + radius);
            }
        }

        [Test]
        public void FramingKeepsTheWholeBallInTheLowerMiddleThirdAtEverySize()
        {
            Vector3 center = new Vector3(0f, 2f, 0f);
            Vector3 forward = new Vector3(0f, 0f, 1f);

            for (int i = 0; i < Radii.Length; i++)
            {
                float radius = Radii[i];
                Vector3 position;
                Quaternion rotation;
                float distance;
                Pose(center, forward, radius, out position, out rotation, out distance);

                float nearM;
                float farM;
                Assert.IsTrue(DebugCameraMath.TryClipPlanes(distance, radius, out nearM, out farM));
                Matrix4x4 viewProjection = ViewProjection(position, rotation, nearM, farM);

                // Hand calculation for the centre: the aim lift is 0.4 r and the camera is
                // 2.6 r back / 1.4 r up, so the centre is 7.2 degrees below the axis and lands
                // at about 0.391 of the viewport height - inside the lower-middle third. The
                // ball's top lands at about 0.689 and its bottom at about 0.156, at every
                // radius, because the pose scales with r.
                Vector2 centerViewport = Project(viewProjection, center);
                Assert.AreEqual(0.5f, centerViewport.x, 1e-3f, "radius " + radius);
                Assert.GreaterOrEqual(centerViewport.y, 1f / 3f, "radius " + radius);
                Assert.LessOrEqual(centerViewport.y, 0.5f, "radius " + radius);

                // The whole ball fits: its top stays on screen and its bottom stays on screen,
                // which is what makes a screenshot usable at every radius.
                Vector2 top = Project(viewProjection, center + Vector3.up * radius);
                Vector2 bottom = Project(viewProjection, center - Vector3.up * radius);
                Assert.LessOrEqual(top.y, 1f, "radius " + radius);
                Assert.GreaterOrEqual(bottom.y, 0f, "radius " + radius);
                Assert.Greater(top.y, centerViewport.y, "radius " + radius);
                Assert.Less(bottom.y, centerViewport.y, "radius " + radius);
            }
        }

        [Test]
        public void ClipPlanesReachTheFarSideOfTheLargestBall()
        {
            Vector3 center = Vector3.zero;
            Vector3 forward = new Vector3(0f, 0f, 1f);

            for (int i = 0; i < Radii.Length; i++)
            {
                float radius = Radii[i];
                Vector3 position;
                Quaternion rotation;
                float distance;
                Pose(center, forward, radius, out position, out rotation, out distance);

                float nearM;
                float farM;
                Assert.IsTrue(DebugCameraMath.TryClipPlanes(distance, radius, out nearM, out farM),
                    "radius " + radius);

                // Near is in front of the ball, far is past its far surface.
                Assert.Greater(nearM, 0f, "radius " + radius);
                Assert.Less(nearM, distance - radius, "radius " + radius);
                Assert.GreaterOrEqual(farM, distance + radius, "radius " + radius);
                Assert.GreaterOrEqual(farM, DebugCameraMath.MinimumFarClipM, "radius " + radius);

                // Unity's own relation between the planes, and the map floor: a 30 m ball must
                // not force a far plane beyond what the engine accepts.
                Assert.LessOrEqual(farM, 100000f * nearM, "radius " + radius);
            }
        }

        [Test]
        public void ClipPlanesRefuseDistancesAndRadiiThatCannotDescribeACamera()
        {
            float nearM;
            float farM;

            Assert.IsFalse(DebugCameraMath.TryClipPlanes(0f, 1f, out nearM, out farM));
            Assert.AreEqual(0f, nearM, 0f);
            Assert.AreEqual(0f, farM, 0f);

            Assert.IsFalse(DebugCameraMath.TryClipPlanes(-1f, 1f, out nearM, out farM));
            Assert.IsFalse(DebugCameraMath.TryClipPlanes(float.NaN, 1f, out nearM, out farM));
            Assert.IsFalse(DebugCameraMath.TryClipPlanes(10f, 0f, out nearM, out farM));
            Assert.IsFalse(DebugCameraMath.TryClipPlanes(10f, -1f, out nearM, out farM));
            Assert.IsFalse(DebugCameraMath.TryClipPlanes(10f, float.NaN, out nearM, out farM));
            Assert.AreEqual(0f, nearM, 0f);
            Assert.AreEqual(0f, farM, 0f);
        }

        [Test]
        public void PoseRefusesUnusableInputsAndWritesNothing()
        {
            Vector3 position;
            Quaternion rotation;
            float distance;
            Vector3 center = new Vector3(0f, 1f, 0f);
            Vector3 forward = new Vector3(0f, 0f, 1f);

            // A non-finite centre, then a radius that is zero, negative or non-finite.
            Assert.IsFalse(DebugCameraMath.TryComputePose(new Vector3(float.NaN, 0f, 0f), forward,
                1f, 2.6f, 1.4f, 0.4f, out position, out rotation, out distance));
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, forward, 0f, 2.6f, 1.4f, 0.4f,
                out position, out rotation, out distance));
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, forward, -1f, 2.6f, 1.4f, 0.4f,
                out position, out rotation, out distance));
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, forward, float.NaN, 2.6f, 1.4f,
                0.4f, out position, out rotation, out distance));

            // Factors: negative, non-finite, or both offsets zero (a camera at the centre).
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, forward, 1f, -2.6f, 1.4f, 0.4f,
                out position, out rotation, out distance));
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, forward, 1f, 2.6f, float.NaN, 0.4f,
                out position, out rotation, out distance));
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, forward, 1f, 0f, 0f, 0.4f,
                out position, out rotation, out distance));

            // Heading: none at all, and one with no horizontal part, which nothing can sit behind.
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, Vector3.zero, 1f, 2.6f, 1.4f, 0.4f,
                out position, out rotation, out distance));
            Assert.IsFalse(DebugCameraMath.TryComputePose(center, Vector3.up, 1f, 2.6f, 1.4f, 0.4f,
                out position, out rotation, out distance));

            Assert.AreEqual(Vector3.zero, position);
            Assert.AreEqual(Quaternion.identity, rotation);
            Assert.AreEqual(0f, distance, 0f);
        }

        [Test]
        public void PoseUsesOnlyTheHorizontalPartOfTheHeading()
        {
            Vector3 center = new Vector3(0f, 1f, 0f);
            Vector3 positionFlat;
            Vector3 positionTilted;
            Quaternion rotationFlat;
            Quaternion rotationTilted;
            float distanceFlat;
            float distanceTilted;

            // A ball's roll and pitch must never tilt the debug view, so (0, 5, 2) and (0, 0, 2)
            // are the same heading.
            Assert.IsTrue(DebugCameraMath.TryComputePose(center, new Vector3(0f, 0f, 2f), 1f,
                2.6f, 1.4f, 0.4f, out positionFlat, out rotationFlat, out distanceFlat));
            Assert.IsTrue(DebugCameraMath.TryComputePose(center, new Vector3(0f, 5f, 2f), 1f,
                2.6f, 1.4f, 0.4f, out positionTilted, out rotationTilted, out distanceTilted));

            Assert.AreEqual(positionFlat, positionTilted);
            Assert.AreEqual(rotationFlat, rotationTilted);
            Assert.AreEqual(distanceFlat, distanceTilted, 0f);
        }

        [Test]
        public void TheModuleKeepsNoStateBetweenCalls()
        {
            // Only literals are allowed: a cached array or a static scratch vector would make
            // "off means no allocations" untrue and would let one camera's frame leak into the
            // next. This mirrors the rule the other math modules are held to.
            FieldInfo[] fields = typeof(DebugCameraMath).GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.Greater(fields.Length, 0, "the module should expose its clip-plane constants");
            for (int i = 0; i < fields.Length; i++)
            {
                Assert.IsTrue(fields[i].IsLiteral,
                    fields[i].Name + " would be mutable static state");
            }
        }
    }
}
