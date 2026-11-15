using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pure geometry behind <c>PlacementImporter</c>'s structure bodies: the
    /// <c>Visual</c> child's pose under a <c>ground</c>/<c>boundary</c> marker, and the footprint
    /// growth that makes a floor reach its boundary planes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These calls go through reflection on purpose. The importer lives in
    /// <c>Assets/VRC_Katamari/Editor/LevelBuild/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c> (see <c>Tests/EditMode/VRCKatamari.Tests.EditMode.asmdef</c>),
    /// so it cannot name editor types; the same constraint and the same remedy are documented
    /// in <c>Pickup/PickupFieldAuthoringRulesTests</c>.
    /// </para>
    /// <para>
    /// The expected numbers are hand calculations from the placement convention, not copies of
    /// the implementation: a plane role declares its normal as the authored local <b>+Y</b>
    /// (design 4.3 / the validator's rule), so <c>size</c> reads "in-plane X by thickness by
    /// in-plane Z" and the plane is the authored local <c>y = 0</c> plane through
    /// <c>center</c>. The body must therefore have its visible face ON that plane: a floor's top
    /// face for <c>ground</c>, a wall's inner face for <c>boundary</c>.
    /// </para>
    /// <para>
    /// The subtle part is which authored extent ends up vertical. <c>ApplyStructure</c> turns a
    /// boundary marker to <c>LookRotation(inward, worldUp)</c>, which throws the authored roll
    /// away, so the picnic level's west/east walls have the height in <c>size[0]</c> and its
    /// south/north walls have it in <c>size[2]</c>. These tests pin the world-space height of
    /// the body from the real documents, so a "just use size[0] as the height" simplification
    /// fails here instead of in the scene.
    /// </para>
    /// </remarks>
    public class PlacementImporterVisualTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";

        // The importer's own pose tolerances (PlacementImporter.PositionToleranceM and
        // RotationDotTolerance). Only float noise is expected here: identical input, identical
        // output, so 1e-4 m is generous.
        private const float PoseToleranceM = 0.0001f;

        // ---- pose: the visible face lies on the marker's plane -----------------

        [Test]
        public void GroundBodySitsBelowItsPlaneWithTheAuthoredFootprint()
        {
            // The picnic corner's floor: 13.4 x 0.04 x 13.4 at (-12.7, 0, -12.7), no rotation.
            // A ground body extends DOWN from the plane, so the top face is the walkable face.
            BodyPose pose = Pose(Quaternion.identity, Quaternion.identity,
                new Vector3(13.4f, 0.04f, 13.4f));

            Assert.IsTrue(pose.Accepted);
            Assert.AreEqual(0f, pose.LocalPosition.x, PoseToleranceM);
            Assert.AreEqual(-0.02f, pose.LocalPosition.y, PoseToleranceM, "half a thickness below the plane");
            Assert.AreEqual(0f, pose.LocalPosition.z, PoseToleranceM);
            Assert.AreEqual(13.4f, pose.LocalScale.x, PoseToleranceM);
            Assert.AreEqual(0.04f, pose.LocalScale.y, PoseToleranceM);
            Assert.AreEqual(13.4f, pose.LocalScale.z, PoseToleranceM, "the footprint is not swapped");
            Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(pose.LocalRotation, Quaternion.identity)),
                0.0001f, "a ground marker already has the authored frame, so the body is unrotated");

            // The top face is exactly the ground plane the arena registers: a ground marker's
            // plane normal is its local +Y, not the +Z a wall uses.
            Assert.AreEqual(0f, FaceOffsetFromPlane(pose, Quaternion.identity, Vector3.up),
                PoseToleranceM);
        }

        [Test]
        public void BoundaryBodySitsBehindItsPlaneSoTheInnerFaceIsTheWall()
        {
            // Wall x0 of the picnic corner: center (-19.4, 0.6, -12.7), size 1.2 x 0.04 x 13.4,
            // quat (0, 0, -0.707107, 0.707107) = -90 deg about Z, which makes the authored +Y
            // the inward normal (+X). The importer re-rolls the marker to LookRotation(+X, up).
            Quaternion authored = new Quaternion(0f, 0f, -0.707107f, 0.707107f);
            Quaternion marker = MarkerRotation(authored);

            BodyPose pose = Pose(authored, marker, new Vector3(1.2f, 0.04f, 13.4f));

            Assert.IsTrue(pose.Accepted);
            Assert.AreEqual(1.2f, pose.LocalScale.x, PoseToleranceM);
            Assert.AreEqual(0.04f, pose.LocalScale.y, PoseToleranceM);
            Assert.AreEqual(13.4f, pose.LocalScale.z, PoseToleranceM);

            // Outward is the marker's -Z (the inward normal is +Z), so the body centre sits half
            // a thickness behind the plane and its inner face lands ON the plane.
            Assert.AreEqual(0f, pose.LocalPosition.x, PoseToleranceM);
            Assert.AreEqual(0f, pose.LocalPosition.y, PoseToleranceM);
            Assert.AreEqual(-0.02f, pose.LocalPosition.z, PoseToleranceM);

            Assert.AreEqual(0f, FaceOffsetFromPlane(pose, marker, Vector3.forward), PoseToleranceM,
                "the wall's inner face must lie on the boundary plane");

            // The 1.2 authored extent is the HEIGHT: the authored X lands on world -Y, which is
            // exactly the roll the marker rewrite would hide.
            Assert.AreEqual(1.2f, WorldHeight(pose, marker), 0.001f);
        }

        [Test]
        public void BoundaryBodyHeightFollowsTheAuthoredAxisThatEndsUpVertical()
        {
            // Wall z0 of the picnic corner: size 13.4 x 0.04 x 1.2, quat (0.707107, 0, 0,
            // 0.707107) = +90 deg about X, authored +Y -> inward +Z. Here the 1.2 extent is
            // size[2], not size[0]: the same wall, written with the other in-plane axis first.
            Quaternion authoredZ0 = new Quaternion(0.707107f, 0f, 0f, 0.707107f);
            BodyPose poseZ0 = Pose(authoredZ0, MarkerRotation(authoredZ0), new Vector3(13.4f, 0.04f, 1.2f));
            Assert.AreEqual(1.2f, WorldHeight(poseZ0, MarkerRotation(authoredZ0)), 0.001f);

            // The physics test field's wall_x_max: size 88 x 0.04 x 3, quat (-0.5, 0.5, 0.5,
            // 0.5), authored +Y -> inward -X. The 3 m extent is the height and the 88 m one runs
            // along the wall (world Z).
            Quaternion authoredXMax = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f);
            Quaternion markerXMax = MarkerRotation(authoredXMax);
            BodyPose poseXMax = Pose(authoredXMax, markerXMax, new Vector3(88f, 0.04f, 3f));
            Assert.AreEqual(3f, WorldHeight(poseXMax, markerXMax), 0.001f);
            Assert.AreEqual(88f, WorldLengthAlong(poseXMax, markerXMax, Vector3.forward), 0.001f);
        }

        [Test]
        public void DegenerateSizesAreRefusedInsteadOfBuildingAnInvisibleBody()
        {
            // size is positive on every axis in a valid document (the CLI's check rule), so a
            // zero, negative or non-finite axis here is a hand-edited file, and a body made from
            // it would be invisible or a paper collider. Refused, with the defaults returned.
            AssertRefused(new Vector3(0f, 0.04f, 10f));
            AssertRefused(new Vector3(10f, 0f, 10f));
            AssertRefused(new Vector3(10f, -0.04f, 10f));
            AssertRefused(new Vector3(10f, 0.04f, 0f));
            AssertRefused(new Vector3(float.NaN, 0.04f, 10f));
            AssertRefused(new Vector3(10f, float.PositiveInfinity, 10f));
            AssertRefused(new Vector3(10f, 0.04f, float.NegativeInfinity));
        }

        // ---- footprint: the floor reaches the boundary planes ------------------

        [Test]
        public void CoverageKeepsTheAuthoredFootprintWhenTheWallsAreInside()
        {
            // The picnic corner: a 13.4 m floor at (-12.7, 0, -12.7) and four walls whose
            // origins are exactly on its rim (x and z at -19.4 and -6.0). Nothing to grow, so
            // the authored size stays - the shipped documents must not be resized by the
            // importer.
            Vector3[] walls =
            {
                new Vector3(-19.4f, 0.6f, -12.7f),
                new Vector3(-6.0f, 0.6f, -12.7f),
                new Vector3(-12.7f, 0.6f, -19.4f),
                new Vector3(-12.7f, 0.6f, -6.0f),
            };

            Vector2 half = Coverage(new Vector3(-12.7f, 0f, -12.7f), Quaternion.identity,
                new Vector3(13.4f, 0.04f, 13.4f), walls);

            Assert.AreEqual(6.7f, half.x, PoseToleranceM);
            Assert.AreEqual(6.7f, half.y, PoseToleranceM);

            // The physics test field is the same story one size up: 88 m floor, walls at +-44.
            Vector3[] fieldWalls =
            {
                new Vector3(44f, 1.5f, 0f),
                new Vector3(-44f, 1.5f, 0f),
                new Vector3(0f, 1.5f, 44f),
                new Vector3(0f, 1.5f, -44f),
            };

            Vector2 field = Coverage(Vector3.zero, Quaternion.identity, new Vector3(88f, 0.04f, 88f),
                fieldWalls);
            Assert.AreEqual(44f, field.x, PoseToleranceM);
            Assert.AreEqual(44f, field.y, PoseToleranceM);
        }

        [Test]
        public void CoverageGrowsAFloorThatIsSmallerThanTheField()
        {
            // A 10 m floor in an arena walled at +-44 must cover the walls, not the other way
            // round: without this the walls (and the player's whole walkable area) stand over
            // the void.
            Vector3[] walls =
            {
                new Vector3(44f, 1.5f, 0f),
                new Vector3(-44f, 1.5f, 0f),
                new Vector3(0f, 1.5f, 44f),
                new Vector3(0f, 1.5f, -44f),
            };

            Vector2 half = Coverage(Vector3.zero, Quaternion.identity, new Vector3(10f, 0.04f, 10f),
                walls);
            Assert.AreEqual(44f, half.x, PoseToleranceM);
            Assert.AreEqual(44f, half.y, PoseToleranceM);
        }

        [Test]
        public void CoverageGrowsPerAxisNotByTheLargestPoint()
        {
            // One wall on world +X only: the floor grows along its local X and keeps its
            // authored depth along local Z. Growing a square around the farthest point would
            // also work for a square floor, which is exactly why the asymmetric case is pinned.
            Vector3[] walls = { new Vector3(30f, 1.5f, 0f) };

            Vector2 half = Coverage(Vector3.zero, Quaternion.identity, new Vector3(10f, 0.04f, 10f),
                walls);
            Assert.AreEqual(30f, half.x, PoseToleranceM, "local X reaches the wall on world +X");
            Assert.AreEqual(5f, half.y, PoseToleranceM, "local Z is untouched");
        }

        [Test]
        public void CoverageFollowsARotatedPlane()
        {
            // The same floor turned 90 degrees about Y: local X becomes world -Z and local Z
            // becomes world +X, so the extents must be attributed the other way round.
            Vector3[] walls = { new Vector3(0f, 1.5f, 30f) };

            Vector2 half = Coverage(Vector3.zero, Quaternion.Euler(0f, 90f, 0f),
                new Vector3(20f, 0.04f, 10f), walls);
            Assert.AreEqual(30f, half.x, 0.001f, "local X runs along world -Z after the rotation");
            Assert.AreEqual(5f, half.y, 0.001f, "local Z runs along world X and stays at half the size");
        }

        [Test]
        public void CoverageWithoutWallsKeepsTheAuthoredSize()
        {
            Vector3 size = new Vector3(12f, 0.04f, 8f);

            Vector2 none = Coverage(new Vector3(1f, 0f, 2f), Quaternion.identity, size, null);
            Assert.AreEqual(6f, none.x, PoseToleranceM);
            Assert.AreEqual(4f, none.y, PoseToleranceM);

            Vector2 empty = Coverage(new Vector3(1f, 0f, 2f), Quaternion.identity, size,
                new Vector3[0]);
            Assert.AreEqual(6f, empty.x, PoseToleranceM);
            Assert.AreEqual(4f, empty.y, PoseToleranceM);
        }

        [Test]
        public void CoverageIsIdempotent()
        {
            // Re-importing must not creep: feeding the grown size back in must return it
            // unchanged, which is what keeps the second run at +0 ~0 -0.
            Vector3[] walls = { new Vector3(44f, 1.5f, 0f) };
            Vector2 grown = Coverage(Vector3.zero, Quaternion.identity, new Vector3(10f, 0.04f, 10f),
                walls);

            Vector2 again = Coverage(Vector3.zero, Quaternion.identity,
                new Vector3(grown.x * 2f, 0.04f, grown.y * 2f), walls);
            Assert.AreEqual(grown.x, again.x, PoseToleranceM);
            Assert.AreEqual(grown.y, again.y, PoseToleranceM);
        }

        // ------------------------------------------------------------- helpers ---

        private struct BodyPose
        {
            public bool Accepted;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
        }

        private static BodyPose Pose(Quaternion authoredRotation, Quaternion markerRotation, Vector3 size)
        {
            object[] arguments = { authoredRotation, markerRotation, size, null, null, null };
            bool accepted = (bool)Invoke("TryComputePlaneBodyPose", arguments);

            var pose = new BodyPose();
            pose.Accepted = accepted;
            pose.LocalPosition = arguments[3] is Vector3 position ? position : Vector3.zero;
            pose.LocalRotation = arguments[4] is Quaternion rotation ? rotation : Quaternion.identity;
            pose.LocalScale = arguments[5] is Vector3 scale ? scale : Vector3.one;
            return pose;
        }

        /// <summary>
        /// Half extents in the plane's own frame: <c>x</c> along local X, <c>y</c> along local Z
        /// (the two in-plane axes; local Y is the thickness).
        /// </summary>
        private static Vector2 Coverage(Vector3 planeOrigin, Quaternion planeRotation, Vector3 size,
            Vector3[] mustCoverWorld)
        {
            object result = Invoke("CoverHalfExtentsInPlane",
                planeOrigin, planeRotation, size, mustCoverWorld);
            Assert.IsNotNull(result, "CoverHalfExtentsInPlane must return half extents");
            return (Vector2)result;
        }

        private static void AssertRefused(Vector3 size)
        {
            BodyPose pose = Pose(Quaternion.identity, Quaternion.identity, size);
            Assert.IsFalse(pose.Accepted, "size " + size + " must not produce a body");
            Assert.AreEqual(Vector3.zero, pose.LocalPosition);
            Assert.AreEqual(Vector3.one, pose.LocalScale);
            Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(pose.LocalRotation, Quaternion.identity)),
                0.0001f);
        }

        /// <summary>
        /// The marker rotation the importer writes for a boundary: the authored +Y (the inward
        /// normal) becomes the marker's +Z, with world up as the roll hint (ApplyStructure).
        /// </summary>
        private static Quaternion MarkerRotation(Quaternion authored)
        {
            Vector3 inward = authored * Vector3.up;
            Assert.Less(Mathf.Abs(inward.y), 0.99f, "a boundary must be a vertical plane");
            return Quaternion.LookRotation(inward, Vector3.up);
        }

        /// <summary>
        /// The visible face's signed distance from the marker's plane (the plane runs through
        /// the marker origin): 0 means the face lies exactly on it.
        /// </summary>
        /// <param name="localNormalAxis">
        /// The marker-local axis that is the plane normal: <c>Vector3.up</c> for a ground
        /// (authored frame == marker frame) and <c>Vector3.forward</c> for a boundary (the
        /// importer writes <c>LookRotation(inward, up)</c>, so local +Z points into the field).
        /// </param>
        private static float FaceOffsetFromPlane(BodyPose pose, Quaternion markerRotation,
            Vector3 localNormalAxis)
        {
            // The body extends one half thickness from the plane along the normal's opposite,
            // so the face is half a thickness towards the normal from the centre. The extent
            // along the normal is the authored thickness (localScale.y).
            Vector3 center = markerRotation * pose.LocalPosition;
            Vector3 normal = markerRotation * localNormalAxis;
            return Vector3.Dot(center + normal * (pose.LocalScale.y * 0.5f), normal);
        }

        /// <summary>
        /// The body's world-space height: the sum over its own axes of the vertical part of each
        /// full extent, which is exact for a box that is axis-aligned in its own frame.
        /// </summary>
        private static float WorldHeight(BodyPose pose, Quaternion markerRotation)
        {
            return WorldExtentAlong(pose, markerRotation, Vector3.up);
        }

        private static float WorldLengthAlong(BodyPose pose, Quaternion markerRotation, Vector3 worldAxis)
        {
            return WorldExtentAlong(pose, markerRotation, worldAxis);
        }

        private static float WorldExtentAlong(BodyPose pose, Quaternion markerRotation, Vector3 worldAxis)
        {
            Vector3 x = markerRotation * (pose.LocalRotation * Vector3.right);
            Vector3 y = markerRotation * (pose.LocalRotation * Vector3.up);
            Vector3 z = markerRotation * (pose.LocalRotation * Vector3.forward);
            return Mathf.Abs(Vector3.Dot(x, worldAxis)) * pose.LocalScale.x +
                   Mathf.Abs(Vector3.Dot(y, worldAxis)) * pose.LocalScale.y +
                   Mathf.Abs(Vector3.Dot(z, worldAxis)) * pose.LocalScale.z;
        }

        // ----------------------------------------------------------- reflection ---

        private static Type ImporterType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(ImporterTypeName, false);
                if (found != null)
                {
                    return found;
                }
            }

            Assert.Fail("type '" + ImporterTypeName + "' is not loaded. It lives in " +
                        "Assets/VRC_Katamari/Editor/LevelBuild and is expected to compile into the " +
                        "editor assembly.");
            return null;
        }

        private static object Invoke(string methodName, params object[] arguments)
        {
            MethodInfo method = ImporterType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, ImporterTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }
    }
}
