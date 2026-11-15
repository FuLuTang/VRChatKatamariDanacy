using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the two bounds interpretations the radial-proxy shell depends on: the
    /// <c>propLocalBounds</c> column is measured along the prop's own axes (the root
    /// rotation is applied once, at runtime), while the legacy <c>propBounds</c> column is
    /// the world AABB. Also pins the fallback reason codes an un-synced scene produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this file guards: <c>RadialProxyShellMath.TryBuildAabbCorners</c> rotates
    /// its full extents by the prop's ball-local rotation, and
    /// <c>AttachmentMath.TryWorldPoseToLocal</c> already put the prop's own rotation into
    /// that quaternion. Feeding the world AABB (which was measured after the prop was
    /// turned) rotates the box a second time, so a yaw-90 lounge chair's long axis ends
    /// up on the wrong ball axis. The first test states the fixed geometry; the fallback
    /// test states the old geometry on purpose.
    /// </para>
    /// <para>
    /// The editor-side measurement lives in
    /// <c>Assets/VRC_Katamari/Editor/Pickup/PickupFieldSync.cs</c>. This test assembly
    /// references only <c>VRCKatamari.Runtime</c> (see the EditMode asmdef), so the
    /// measurement and the fallback resolver are reached by reflection exactly like
    /// <c>PickupFieldAuthoringRulesTests</c> does. Expected numbers are hand calculations
    /// for a 1.6 x 0.35 x 0.6 box yawed 90 degrees, not re-runs of the implementation.
    /// </para>
    /// </remarks>
    public class RadialProxyLocalBoundsTests
    {
        private const string SyncTypeName = "VRCKatamari.Editor.Pickup.PickupFieldSync";
        private const float Tolerance = 1e-4f;

        [Test]
        public void TurnedPropUsesLocalExtentsOnItsOwnLongAxis()
        {
            // The lounge chair's model is 1.6 m long on its local X. At yaw 90 that length
            // lies on world Z, so the world AABB is 0.6 x 0.35 x 1.6. The shell must get
            // 1.6 x 0.35 x 0.6 and let localRotation (which contains the yaw) place it.
            GameObject root = null;
            try
            {
                root = CreateProp(new Vector3(1.6f, 0.35f, 0.6f),
                    Quaternion.AngleAxis(90f, Vector3.up));
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                Assert.AreEqual(1, renderers.Length, "one visual renderer");

                Vector3 local = MeasureLocalBounds(root.transform, renderers);
                AssertVector(new Vector3(1.6f, 0.35f, 0.6f), local,
                    "propLocalBounds is measured along the prop's own axes");
                AssertVector(new Vector3(0.6f, 0.35f, 1.6f), renderers[0].bounds.size,
                    "propBounds stays the world AABB");

                Vector3 localPosition;
                Quaternion localRotation;
                Assert.IsTrue(AttachmentMath.TryWorldPoseToLocal(Vector3.zero,
                        Quaternion.identity, root.transform.position, root.transform.rotation,
                        root.transform.lossyScale, out localPosition, out localRotation),
                    "the runtime pose conversion accepts the prop");

                Vector3[] corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
                float farthest;
                Assert.IsTrue(RadialProxyShellMath.TryBuildAabbCorners(Vector3.zero,
                    localRotation, local, corners, out farthest));

                // local +X turned +90 about +Y points along -Z in the ball frame, so the
                // 0.8 m half-length lands on Z and the 0.3 m half-width on X.
                Assert.AreEqual(0.3f, MaxAbs(corners, 0), 1e-3f, "thin axis after the turn");
                Assert.AreEqual(0.8f, MaxAbs(corners, 2), 1e-3f, "long axis follows the prop");
                Assert.Greater(farthest, 0.8f, "the far corner is a long-object tip");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        [Test]
        public void MissingLocalColumnFallsBackToTheWorldAabbAndKeepsTheOldRotation()
        {
            // An un-synced scene has no propLocalBounds. The resolver must hand the shell
            // the legacy world AABB and say why; that reproduces the pre-fix geometry, in
            // which the same yaw is applied to the already-turned box.
            Vector3 world = new Vector3(0.6f, 0.35f, 1.6f);
            Vector3[] worldRows = { world };
            Vector3 extents;
            int source;
            Assert.IsTrue(RadialProxyShellMath.TryResolvePropExtents(null, worldRows, 0, 1,
                out extents, out source));
            Assert.AreEqual(RadialProxyShellMath.ExtentsSourceWorldMissing, source,
                "a missing column is reported, not refused");
            AssertVector(world, extents, "the fallback value is propBounds");

            Vector3[] corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            float farthest;
            Assert.IsTrue(RadialProxyShellMath.TryBuildAabbCorners(Vector3.zero,
                Quaternion.AngleAxis(90f, Vector3.up), extents, corners, out farthest));
            // The 1.6 m world size is turned again: the old long axis (Z of the size) goes
            // to ball X. This is the wrong box the fix removes; the prop's real long axis
            // is ball Z (previous test).
            Assert.AreEqual(0.8f, MaxAbs(corners, 0), 1e-3f, "double-rotated long axis on X");
            Assert.AreEqual(0.3f, MaxAbs(corners, 2), 1e-3f, "true long axis is only half-width");
        }

        [Test]
        public void ResolvePropExtentsReportsEveryFallbackReason()
        {
            Vector3 world = new Vector3(0.6f, 0.35f, 1.6f);
            Vector3 local = new Vector3(1.6f, 0.35f, 0.6f);
            Vector3[] worldRows = { world };

            Vector3 extents;
            int source;

            Assert.IsTrue(RadialProxyShellMath.TryResolvePropExtents(new[] { local }, worldRows,
                0, 1, out extents, out source));
            Assert.AreEqual(RadialProxyShellMath.ExtentsSourceLocal, source);
            AssertVector(local, extents, "a usable local row wins");

            // The length is checked against the row count the caller derives from itemIds,
            // not silently zipped: an extra local row makes the whole column untrusted.
            Assert.IsTrue(RadialProxyShellMath.TryResolvePropExtents(new[] { local, local },
                worldRows, 0, 1, out extents, out source));
            Assert.AreEqual(RadialProxyShellMath.ExtentsSourceWorldLengthMismatch, source);
            AssertVector(world, extents, "a length mismatch falls back to propBounds");

            Assert.IsTrue(RadialProxyShellMath.TryResolvePropExtents(
                new[] { new Vector3(-1f, 0.35f, 0.6f) }, worldRows, 0, 1, out extents,
                out source));
            Assert.AreEqual(RadialProxyShellMath.ExtentsSourceWorldRowInvalid, source);
            AssertVector(world, extents, "a negative local row falls back per row");

            Assert.IsTrue(RadialProxyShellMath.TryResolvePropExtents(
                new[] { new Vector3(float.NaN, 0.35f, 0.6f) }, worldRows, 0, 1, out extents,
                out source));
            Assert.AreEqual(RadialProxyShellMath.ExtentsSourceWorldRowInvalid, source);
            AssertVector(world, extents, "a non-finite local row falls back per row");

            Assert.IsFalse(RadialProxyShellMath.TryResolvePropExtents(new[] { local }, worldRows,
                1, 1, out extents, out source), "an index outside the rows is refused");
            Assert.IsFalse(RadialProxyShellMath.TryResolvePropExtents(new[] { local }, null,
                0, 1, out extents, out source), "a null world column is refused");
            Assert.IsFalse(RadialProxyShellMath.TryResolvePropExtents(new[] { local }, worldRows,
                0, -1, out extents, out source), "a negative row count is refused");
        }

        [Test]
        public void LocalBoundsIgnoreTheRootRotation()
        {
            // The column is authored once. Re-posing the prop in the scene must not change
            // it: the root rotation is the runtime's job, so the measurement cancels it.
            GameObject root = null;
            try
            {
                root = CreateProp(new Vector3(0.9f, 0.2f, 1.4f), Quaternion.identity);
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                Vector3 atRest = MeasureLocalBounds(root.transform, renderers);

                root.transform.rotation = Quaternion.AngleAxis(37f,
                    new Vector3(1f, 2f, 3f).normalized);
                Vector3 turned = MeasureLocalBounds(root.transform, renderers);

                AssertVector(new Vector3(0.9f, 0.2f, 1.4f), atRest, "authored local size");
                AssertVector(atRest, turned, "the root rotation is not baked into the column");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        [Test]
        public void ChildRotationIsBakedIntoTheLocalColumn()
        {
            // A child turned 45 degrees inside the prop is part of the prop's own axes: the
            // local column must enclose it, not read the child's un-rotated mesh size.
            GameObject root = null;
            try
            {
                root = CreateProp(new Vector3(1f, 0.1f, 0.1f), Quaternion.identity);
                Transform visual = root.transform.GetChild(0);
                visual.localRotation = Quaternion.AngleAxis(45f, Vector3.up);
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

                Vector3 local = MeasureLocalBounds(root.transform, renderers);
                // Half extents: 0.5*cos45 + 0.05*sin45 = 0.38909 m on X and Z; Y unchanged.
                float expectedWidth = 2f * (0.5f * Mathf.Cos(45f * Mathf.Deg2Rad) +
                                            0.05f * Mathf.Sin(45f * Mathf.Deg2Rad));
                Assert.AreEqual(expectedWidth, local.x, 1e-3f, "rotated child width");
                Assert.AreEqual(expectedWidth, local.z, 1e-3f, "rotated child depth");
                Assert.AreEqual(0.1f, local.y, 1e-3f, "thin axis unchanged");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        [Test]
        public void MissingColliderGetsOneBoxOnThePropRootAndIsReused()
        {
            // The level importer creates its props from FBX models whose import settings
            // have addColliders: 0. Without this step propColliders would be empty, the host
            // would warn, and an absorbed prop would keep its (here: missing) collider.
            GameObject root = null;
            try
            {
                root = CreateProp(new Vector3(1.6f, 0.35f, 0.6f),
                    Quaternion.AngleAxis(90f, Vector3.up));
                Assert.IsNull(root.GetComponentInChildren<Collider>(true),
                    "the fixture starts with no collider, like an imported FBX prop");

                Collider first = EnsureCollider(root);
                Assert.IsNotNull(first, "a prop with no collider must get one");
                Assert.IsTrue(first is BoxCollider, "the created collider is a BoxCollider");
                Assert.AreSame(root, first.gameObject, "the box lives on the prop root");

                var box = (BoxCollider)first;
                AssertVector(new Vector3(1.6f, 0.35f, 0.6f), box.size,
                    "the box follows the prop's own axes, not the world AABB");
                AssertVector(Vector3.zero, box.center, "the fixture's mesh is centred on the root");

                Collider second = EnsureCollider(root);
                Assert.AreSame(first, second, "a second sync must reuse the created collider");
                Assert.AreEqual(1, root.GetComponentsInChildren<Collider>(true).Length,
                    "one collider per prop is the host's contract");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        [Test]
        public void ExistingChildColliderIsWiredInsteadOfDuplicated()
        {
            // A hand-wired prop (the old scenes) keeps its authored collider; the importer's
            // create-missing path must not add a second one to the root.
            GameObject root = null;
            try
            {
                root = CreateProp(new Vector3(0.8f, 0.4f, 0.6f), Quaternion.identity);
                Collider authored = root.transform.GetChild(0).gameObject.AddComponent<BoxCollider>();

                Collider wired = EnsureCollider(root);

                Assert.AreSame(authored, wired, "the existing child collider is the row's slot");
                Assert.IsNull(root.GetComponent<Collider>(),
                    "no second collider is added when one already exists");
                Assert.AreEqual(1, root.GetComponentsInChildren<Collider>(true).Length,
                    "still one collider per prop");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        // ------------------------------------------------------------- fixtures ---

        private static GameObject CreateProp(Vector3 localSize, Quaternion rootRotation)
        {
            var root = new GameObject("test_prop");
            root.transform.rotation = rootRotation;
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "visual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = localSize;
            // A primitive cube carries a BoxCollider; this fixture is geometry only.
            Collider collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }

            return root;
        }

        private static Vector3 MeasureLocalBounds(Transform root, Renderer[] renderers)
        {
            MethodInfo method = SyncType().GetMethod("MeasureLocalBounds",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, SyncTypeName + ".MeasureLocalBounds is missing");
            return (Vector3)method.Invoke(null, new object[] { root, renderers });
        }

        private static Collider EnsureCollider(GameObject root)
        {
            MethodInfo method = SyncType().GetMethod("EnsurePropCollider",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, SyncTypeName + ".EnsurePropCollider is missing");
            return (Collider)method.Invoke(null, new object[]
            {
                root.transform,
                new Vector3(0.1f, 0.1f, 0.1f),
            });
        }

        private static Type SyncType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(SyncTypeName, false);
                if (found != null)
                {
                    return found;
                }
            }

            Assert.Fail("type '" + SyncTypeName + "' is not loaded. It lives in " +
                        "Assets/VRC_Katamari/Editor/Pickup and is expected to compile into the " +
                        "editor assembly.");
            return null;
        }

        private static float MaxAbs(Vector3[] points, int axis)
        {
            float maximum = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                float value = axis == 0 ? points[i].x : axis == 1 ? points[i].y : points[i].z;
                if (value < 0f)
                {
                    value = -value;
                }

                if (value > maximum)
                {
                    maximum = value;
                }
            }

            return maximum;
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, message + " (x)");
            Assert.AreEqual(expected.y, actual.y, Tolerance, message + " (y)");
            Assert.AreEqual(expected.z, actual.z, Tolerance, message + " (z)");
        }
    }
}
