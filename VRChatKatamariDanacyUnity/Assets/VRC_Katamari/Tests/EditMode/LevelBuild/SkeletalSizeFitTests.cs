using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the skin/static size comparison of the skeletal-animation assembler: both sides are a
    /// mesh-local box carried through a transform, the ratio is per axis, and the tolerance is 0.25;
    /// and its source tracking: the stamp, the clip-length check and the stray-instance rules.
    /// </summary>
    public class SkeletalSizeFitTests
    {
        private const string AssemblerName = "VRCKatamari.Editor.LevelBuild.SkeletalAnimationAssembler";

        private static object Call(string name, params object[] args)
        {
            Type type = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(AssemblerName, false);
                if (type != null) break;
            }

            Assert.IsNotNull(type, AssemblerName + " not loaded");
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "no method " + name);
            return method.Invoke(null, args);
        }

        [Test]
        public void IdentityTransformKeepsTheMeshBounds()
        {
            var box = new Bounds(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(0.15f, 0.13f, 0.23f));
            var world = (Bounds)Call("MeshBoundsInWorld", box, Matrix4x4.identity);
            Assert.AreEqual(box.size.x, world.size.x, 1e-5f);
            Assert.AreEqual(box.size.y, world.size.y, 1e-5f);
            Assert.AreEqual(box.size.z, world.size.z, 1e-5f);
            Assert.AreEqual(box.center.x, world.center.x, 1e-5f);
        }

        [Test]
        public void ScaleAndRotationOfTheRendererAreApplied()
        {
            var box = new Bounds(Vector3.zero, new Vector3(1f, 2f, 3f));
            // 0.01 uniform scale, 90 degrees about Y (x -> -z, z -> x), translated 5 m along X.
            var m = new Matrix4x4();
            m.SetColumn(0, new Vector4(0f, 0f, -0.01f, 0f));
            m.SetColumn(1, new Vector4(0f, 0.01f, 0f, 0f));
            m.SetColumn(2, new Vector4(0.01f, 0f, 0f, 0f));
            m.SetColumn(3, new Vector4(5f, 0f, 0f, 1f));
            var world = (Bounds)Call("MeshBoundsInWorld", box, m);
            Assert.AreEqual(0.03f, world.size.x, 1e-5f); // the mesh's z extent (3) lands on X
            Assert.AreEqual(0.02f, world.size.y, 1e-5f);
            Assert.AreEqual(0.01f, world.size.z, 1e-5f);
            Assert.AreEqual(5f, world.center.x, 1e-5f);
        }

        [Test]
        public void MatchingSizesFit()
        {
            var ratio = (Vector3)Call("SizeRatio", new Vector3(0.1527f, 0.1348f, 0.2282f), new Vector3(0.1528f, 0.1348f, 0.2282f));
            Assert.AreEqual(1f, ratio.x, 0.01f);
            Assert.IsTrue((bool)Call("FitsTolerance", ratio));
        }

        [Test]
        public void SwappedAxesAndPoseCoveringGrowthAreOutOfTolerance()
        {
            var reference = new Vector3(0.15f, 0.13f, 0.23f);
            var swapped = (Vector3)Call("SizeRatio", reference, new Vector3(0.23f, 0.13f, 0.15f));
            Assert.IsFalse((bool)Call("FitsTolerance", swapped));
            // SkinnedMeshRenderer.bounds, which covers every clip pose, measured 1.17 / 1.27 / 1.17 times the model.
            var grown = (Vector3)Call("SizeRatio", reference, new Vector3(0.15f * 1.17f, 0.13f * 1.27f, 0.23f * 1.17f));
            Assert.IsFalse((bool)Call("FitsTolerance", grown));
        }

        [Test]
        public void ToleranceStaysAtTheCurrentEdge()
        {
            Assert.IsTrue((bool)Call("FitsTolerance", new Vector3(1.25f, 0.75f, 1f)));
            Assert.IsFalse((bool)Call("FitsTolerance", new Vector3(1.26f, 1f, 1f)));
            Assert.IsFalse((bool)Call("FitsTolerance", new Vector3(1f, 0.74f, 1f)));
        }

        [Test]
        public void DegenerateReferenceAxisReadsZeroAndFails()
        {
            var ratio = (Vector3)Call("SizeRatio", new Vector3(0f, 1f, 1f), new Vector3(1f, 1f, 1f));
            Assert.AreEqual(0f, ratio.x);
            Assert.IsFalse((bool)Call("FitsTolerance", ratio));
        }

        [Test]
        public void SourceStampChangesWithEitherFile()
        {
            byte[] fbx = { 1, 2, 3 };
            byte[] defs = { 9, 8 };
            string stamp = (string)Call("SourceStamp", fbx, defs);
            Assert.AreEqual(stamp, (string)Call("SourceStamp", new byte[] { 1, 2, 3 }, new byte[] { 9, 8 }));
            Assert.AreNotEqual(stamp, (string)Call("SourceStamp", new byte[] { 1, 2, 4 }, defs));
            Assert.AreNotEqual(stamp, (string)Call("SourceStamp", fbx, new byte[] { 9, 7 }));
            StringAssert.StartsWith("fbx=", stamp);
            StringAssert.Contains(";defs=", stamp);
        }

        [Test]
        public void ClipLengthIsCheckedAgainstTheSourceSeconds()
        {
            Assert.IsTrue((bool)Call("ClipLengthMatches", 0.96667f, 0.9667f));
            // At 24 fps Unity read 23 frames = 0.9583 s and 11 frames = 0.4583 s.
            Assert.IsFalse((bool)Call("ClipLengthMatches", 0.9583f, 0.9667f));
            Assert.IsFalse((bool)Call("ClipLengthMatches", 0.4583f, 0.4833f));
        }

        [TestCase("rootBone")]
        [TestCase("bones")]
        [TestCase("index 1")]
        public void StaleReasonRequestsRebuildWhenBonesAreMissing(string missing)
        {
            var prop = new GameObject("SkeletalStaleReasonTest");
            prop.hideFlags = HideFlags.DontSave;
            Mesh mesh = null;
            try
            {
                mesh = new Mesh();
                Transform skin = new GameObject("Skin").transform;
                skin.SetParent(prop.transform, false);
                var good = new GameObject("good", typeof(SkinnedMeshRenderer));
                good.transform.SetParent(skin, false);
                var goodRenderer = good.GetComponent<SkinnedMeshRenderer>();
                goodRenderer.sharedMesh = mesh;
                goodRenderer.rootBone = skin;
                goodRenderer.bones = new[] { skin };

                var broken = new GameObject("broken", typeof(SkinnedMeshRenderer));
                broken.transform.SetParent(skin, false);
                broken.SetActive(false);
                var renderer = broken.GetComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                renderer.rootBone = missing == "rootBone" ? null : skin;
                renderer.bones = missing == "bones" ? new Transform[0] : new[] { skin, (Transform)null };

                Type assembler = Array.Find(AppDomain.CurrentDomain.GetAssemblies(),
                    a => a.GetType(AssemblerName, false) != null)?.GetType(AssemblerName);
                Assert.IsNotNull(assembler);
                MethodInfo staleReason = assembler.GetMethod("StaleReason", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(staleReason);
                // Fail on the damaged SMR before checking the source asset or alignment.
                string reason = (string)staleReason.Invoke(null, new object[] { prop.transform, skin, null, null });
                StringAssert.StartsWith("bones-missing: broken", reason);
                StringAssert.Contains("has no " + (missing == "index 1" ? "bone at index 1" : missing), reason);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prop);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void StrayInstancesAreRecognisedByNameOrAssetFolder()
        {
            Assert.IsTrue((bool)Call("IsSkeletalInstanceName", "type_480"));
            Assert.IsFalse((bool)Call("IsSkeletalInstanceName", "type_"));
            Assert.IsFalse((bool)Call("IsSkeletalInstanceName", "type_480b"));
            Assert.IsFalse((bool)Call("IsSkeletalInstanceName", "Skin"));
            Assert.IsTrue((bool)Call("IsUnderAnimationRoot", "Assets/_RefUnlicensed/Animation/type_480/type_480.fbx"));
            Assert.IsFalse((bool)Call("IsUnderAnimationRoot", "Assets/_RefUnlicensed/Models/small_fiddler_crab/small_fiddler_crab.fbx"));
            Assert.IsFalse((bool)Call("IsUnderAnimationRoot", ""));
        }
    }
}
