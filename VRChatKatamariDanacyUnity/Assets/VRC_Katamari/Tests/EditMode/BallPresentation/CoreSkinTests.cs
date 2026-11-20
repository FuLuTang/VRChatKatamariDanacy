using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRCKatamari.Tests
{
    public class CoreSkinTests
    {
        // The optional assembler belongs to Unity's predefined editor assembly; asmdef
        // tests cannot reference that assembly directly. Reflection calls the real code.
        private static Type Assembler
        {
            get
            {
                Type type = Type.GetType("VRCKatamari.Editor.BallPresentation.CoreSkinAssembler, Assembly-CSharp-Editor");
                Assert.IsNotNull(type);
                return type;
            }
        }

        private static readonly int[] Tetra = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        private static Vector3[] Vertices(float factor = 1f, float offset = 0f)
        {
            return new[] { new Vector3(offset, offset, offset),
                new Vector3(offset + factor, offset, offset),
                new Vector3(offset, offset + factor, offset),
                new Vector3(offset, offset, offset + factor) };
        }

        private static bool Normalize(Vector3[] vertices, int[] indices, out double diameter, out float scale)
        {
            object[] args = { vertices, indices, 0d, 0f };
            bool result = (bool)Assembler.GetMethod("TryNormalization").Invoke(null, args);
            diameter = (double)args[2];
            scale = (float)args[3];
            return result;
        }

        [TestCase(1f, 0f)]
        [TestCase(0.01f, 0f)]
        [TestCase(100f, 0f)]
        [TestCase(1f, 100f)]
        public void EquivalentDiameterAndChildScaleCancelSourceUnits(float factor, float offset)
        {
            double diameter;
            float scale;
            Assert.IsTrue(Normalize(Vertices(factor, offset), Tetra, out diameter, out scale));
            // A right tetrahedron has volume factor^3/6, not its bounding-box volume.
            Assert.AreEqual(factor * Math.Pow(1d / Math.PI, 1d / 3d), diameter, factor * 1e-6);
            Assert.AreEqual(1d, diameter * scale, 1e-6);
            Assert.AreEqual(0.222d, diameter * scale * 0.222d, 1e-7);
        }

        [Test]
        public void ReversedWindingHasTheSameVolume()
        {
            int[] reversed = (int[])Tetra.Clone();
            Array.Reverse(reversed);
            double diameter;
            float scale;
            Assert.IsTrue(Normalize(Vertices(), reversed, out diameter, out scale));
            Assert.AreEqual(Math.Pow(1d / Math.PI, 1d / 3d), diameter, 1e-9);
        }

        [Test]
        public void InvalidOrDegenerateInputDoesNotReturnAPartialScale()
        {
            double diameter;
            float scale;
            Assert.IsFalse(Normalize(null, Tetra, out diameter, out scale));
            Assert.IsFalse(Normalize(Vertices(), new[] { 0, 1, 2 }, out diameter, out scale));
            int[] bad = (int[])Tetra.Clone();
            bad[0] = 100;
            Assert.IsFalse(Normalize(Vertices(), bad, out diameter, out scale));
            bad[0] = -1;
            Assert.IsFalse(Normalize(Vertices(), bad, out diameter, out scale));
            Assert.IsFalse(Normalize(new Vector3[4], Tetra, out diameter, out scale));
            Vector3[] nonFinite = Vertices();
            nonFinite[2].x = float.NaN;
            Assert.IsFalse(Normalize(nonFinite, Tetra, out diameter, out scale));
            nonFinite[2].x = float.PositiveInfinity;
            Assert.IsFalse(Normalize(nonFinite, Tetra, out diameter, out scale));
            Assert.AreEqual(0d, diameter);
            Assert.AreEqual(0f, scale);
        }

        // A List<CoreSkinAssembler.SkinSource> built through reflection (editor assembly).
        private static IList Skins(params object[][] rows)
        {
            Type source = Assembler.GetNestedType("SkinSource");
            Assert.IsNotNull(source);
            var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(source));
            foreach (object[] row in rows)
            {
                object skin = Activator.CreateInstance(source);
                source.GetField("Number").SetValue(skin, row[0]);
                source.GetField("Mesh").SetValue(skin, row[1]);
                source.GetField("Material").SetValue(skin, row[2]);
                source.GetField("Scale").SetValue(skin, row[3]);
                list.Add(skin);
            }
            return list;
        }

        [TestCase("core_01", 1)]
        [TestCase("core_29", 29)]
        [TestCase("core_26", -1)]
        [TestCase("core_1", -1)]
        [TestCase("core_30", -1)]
        [TestCase("OriginalCoreSkin", -1)]
        public void OnlyOriginalCoreNamesAreSkinChildren(string name, int expected)
        {
            object[] args = { name, 0 };
            bool ok = (bool)Assembler.GetMethod("TryParseSkinChild").Invoke(null, args);
            Assert.AreEqual(expected > 0, ok);
            if (ok) Assert.AreEqual(expected, args[1]);
        }

        [Test]
        public void AssemblyIsIdempotentAndNeverWritesRuntimeScaleOrRoll()
        {
            Scene preview = EditorSceneManager.NewPreviewScene();
            var core = new GameObject("CoreMesh");
            SceneManager.MoveGameObjectToScene(core, preview);
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Mesh mesh = new Mesh();
            Material material = null;
            try
            {
                core.AddComponent<MeshRenderer>();
                core.transform.localScale = Vector3.one * 0.222f;
                core.transform.localRotation = Quaternion.Euler(10, 20, 30);
                Quaternion rotation = core.transform.localRotation;
                mesh.vertices = Vertices(0.01f);
                mesh.triangles = Tetra;
                mesh.RecalculateBounds();
                material = new Material(Shader.Find("Standard"));
                double diameter;
                float scale;
                Assert.IsTrue(Normalize(mesh.vertices, mesh.triangles, out diameter, out scale));
                MethodInfo apply = Assembler.GetMethod("ApplyToCore");
                // Two skins sharing one mesh: core_01 (default, shown) and core_02 (hidden).
                IList skins = Skins(new object[] { 1, mesh, material, scale },
                    new object[] { 2, mesh, material, scale });
                object[] first = { core.transform, skins, 0, 0 };
                var renderers = (MeshRenderer[])apply.Invoke(null, first);
                Assert.AreEqual(3, first[2], "container + two skins");
                Assert.Greater((int)first[3], 0);
                object[] second = { core.transform, skins, 0, 0 };
                apply.Invoke(null, second);
                Assert.AreEqual(0, second[2]);
                Assert.AreEqual(0, second[3]);
                Assert.AreEqual(Vector3.one * 0.222f, core.transform.localScale);
                Assert.AreEqual(rotation, core.transform.localRotation);
                Assert.AreEqual(1, core.transform.childCount);
                Transform container = core.transform.GetChild(0);
                Assert.AreEqual("OriginalCoreSkin", container.name);
                Assert.AreEqual(Vector3.one, container.localScale);
                Assert.IsNull(container.GetComponent<MeshRenderer>());
                Assert.AreEqual(2, container.childCount);
                Transform skin = container.Find("core_01");
                Assert.AreEqual(mesh, skin.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreEqual(material, skin.GetComponent<MeshRenderer>().sharedMaterial);
                Assert.AreEqual(2, renderers.Length);
                Assert.IsTrue(renderers[0].enabled, "default skin shown");
                Assert.IsFalse(renderers[1].enabled, "other skins hidden");
                Assert.AreSame(renderers[1], container.Find("core_02").GetComponent<MeshRenderer>());
                Assert.IsFalse(core.GetComponent<MeshRenderer>().enabled);
                Assert.AreEqual(-mesh.bounds.center * scale, skin.localPosition);
                Assert.AreEqual(0.222d, diameter * skin.localScale.x * core.transform.localScale.x, 1e-7);
                Assert.AreEqual(0, skin.GetComponents<Collider>().Length);

                // core_02's files gone: its child is removed, core_01 stays.
                object[] third = { core.transform, Skins(new object[] { 1, mesh, material, scale }), 0, 0 };
                apply.Invoke(null, third);
                Assert.AreEqual(1, container.childCount);
                Assert.IsNotNull(container.Find("core_01"));
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
                UnityEngine.Object.DestroyImmediate(core);
                UnityEngine.Object.DestroyImmediate(mesh);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }
    }
}
