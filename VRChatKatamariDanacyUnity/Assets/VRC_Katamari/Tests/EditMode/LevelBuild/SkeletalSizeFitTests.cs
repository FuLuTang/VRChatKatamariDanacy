using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the skin/static size comparison of the skeletal-animation assembler: both sides are a
    /// mesh-local box carried through a transform, the ratio is per axis, and the tolerance is 0.25.
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
    }
}
