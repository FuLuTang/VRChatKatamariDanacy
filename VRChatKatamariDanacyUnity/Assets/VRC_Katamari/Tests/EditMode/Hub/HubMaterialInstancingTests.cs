using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the GPU-instancing rule: which shaders have an instancing variant,
    /// which materials are eligible (opaque only), how a copy of a read-only reference
    /// material is named, and that the copies land under <c>_RefUnlicensed</c>.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other editor-code tests: <c>MaterialInstancing</c> lives in
    /// <c>Assets/VRC_Katamari/Editor/LevelBuild/</c>,
    /// which this assembly does not reference. The tested methods take names and numbers, so
    /// no engine object is needed; expected numbers/names are hand-written from the rule.
    /// </remarks>
    public class HubMaterialInstancingTests
    {
        private const string RuleTypeName = "VRCKatamari.Editor.LevelBuild.MaterialInstancing";

        private Type ruleType;

        [SetUp]
        public void SetUp()
        {
            ruleType = FindType(RuleTypeName);
            Assert.IsNotNull(ruleType, RuleTypeName + " not loaded");
        }

        // ---- which shaders ----

        [Test]
        public void StandardAndItsSpecularSetupHaveAnInstancingVariant()
        {
            Assert.IsTrue(ShaderSupportsInstancing("Standard"));
            Assert.IsTrue(ShaderSupportsInstancing("Standard (Specular setup)"));
        }

        [Test]
        public void TheLegacyFamilyAndOtherPipelinesDoNotHaveOne()
        {
            // This is why the reference island's 158 terrain visual materials stay off: the
            // visual shader is Legacy Shaders/Diffuse, which has no instancing variant.
            Assert.IsFalse(ShaderSupportsInstancing("Legacy Shaders/Diffuse"));
            Assert.IsFalse(ShaderSupportsInstancing("Legacy Shaders/Transparent/Cutout/Diffuse"));
            Assert.IsFalse(ShaderSupportsInstancing("Universal Render Pipeline/Lit"));
            Assert.IsFalse(ShaderSupportsInstancing(""));
            Assert.IsFalse(ShaderSupportsInstancing(null));
        }

        // ---- which materials ----

        [Test]
        public void AnOpaqueStandardMaterialIsEligible()
        {
            Assert.IsTrue(IsEligible("Standard", 2000));
            Assert.IsTrue(IsEligible("Standard (Specular setup)", 2000));
        }

        [Test]
        public void CutoutAndTransparentMaterialsAreNotEligible()
        {
            // Queue 2450 is Unity's first non-opaque queue (AlphaTest).
            Assert.IsFalse(IsEligible("Standard", 2450));
            Assert.IsFalse(IsEligible("Standard", 3000));
            Assert.IsFalse(IsEligible("Custom/Transparent", 2000));
            Assert.IsFalse(IsEligible("Custom/Cutout", 2000));
        }

        [Test]
        public void ASupportedShaderIsStillRefusedWhenTheLegacyFamilyOwnsIt()
        {
            // The queue/name checks pass, but there is no instancing variant: off.
            Assert.IsFalse(IsEligible("Legacy Shaders/Diffuse", 2000));
            Assert.IsFalse(IsEligible(null, 2000));
        }

        // ---- the copy's name and home ----

        [Test]
        public void ACopiedMaterialIsNamedAfterItsModelAndItself()
        {
            // The embedded material object is not available without a model import, so the
            // null material here is the documented "material" fallback name.
            Assert.AreEqual(
                "Assets/_RefUnlicensed/Materials/Instanced/blue_crab_material.mat",
                InstancedAssetPath("Assets/_RefUnlicensed/Models/blue_crab/blue_crab.fbx", null));
            Assert.AreEqual(
                "Assets/_RefUnlicensed/Materials/Instanced/fiddler_crab_material.mat",
                InstancedAssetPath(@"Assets\_RefUnlicensed\Models\fiddler_crab\fiddler_crab.fbx", null));
        }

        [Test]
        public void CopiesStayUnderTheGitIgnoredReferenceRoot()
        {
            string folder = (string)GetConst("InstancedFolder");
            StringAssert.StartsWith("Assets/_RefUnlicensed/", folder);
            StringAssert.Contains("/Materials/", folder);
        }

        [Test]
        public void FileNamesAreMadeSafeForAnAssetPath()
        {
            Assert.AreEqual("Prop_crab_shell", SafeFileName("Prop_crab_shell"));
            Assert.AreEqual("Prop_a_b_c_d", SafeFileName("Prop_a/b\\c:d"));
            Assert.AreEqual("material", SafeFileName(""));
            Assert.AreEqual("material", SafeFileName(null));
        }

        // ---- helpers ----

        private bool ShaderSupportsInstancing(string shaderName)
        {
            return (bool)Invoke("ShaderSupportsInstancing", new[] { typeof(string) },
                new object[] { shaderName });
        }

        private bool IsEligible(string shaderName, int renderQueue)
        {
            return (bool)Invoke("IsEligible", new[] { typeof(string), typeof(int) },
                new object[] { shaderName, renderQueue });
        }

        private string InstancedAssetPath(string ownerAssetPath, object material)
        {
            MethodInfo method = ruleType.GetMethod("InstancedAssetPath",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "MaterialInstancing.InstancedAssetPath not found");
            Type[] types = new Type[method.GetParameters().Length];
            for (int i = 0; i < types.Length; i++) types[i] = method.GetParameters()[i].ParameterType;
            return (string)method.Invoke(null, new object[] { ownerAssetPath, material });
        }

        private string SafeFileName(string name)
        {
            return (string)Invoke("SafeFileName", new[] { typeof(string) }, new object[] { name });
        }

        private object GetConst(string name)
        {
            FieldInfo field = ruleType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, "MaterialInstancing." + name + " not found");
            return field.GetRawConstantValue();
        }

        private object Invoke(string name, Type[] parameterTypes, object[] args)
        {
            MethodInfo method = ruleType.GetMethod(name, BindingFlags.Public | BindingFlags.Static,
                null, parameterTypes, null);
            Assert.IsNotNull(method, "MaterialInstancing." + name + " not found");
            return method.Invoke(null, args);
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
