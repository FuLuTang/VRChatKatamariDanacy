using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins <c>PlacementImporter</c>'s def-level <c>model</c> path resolution: a declared
    /// model wins over the repo prop, and a malformed path is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scene-free on purpose: these are the two pure functions the import calls before it
    /// loads any asset (<c>ResolvePropModelPath</c> and <c>IsValidModelPath</c>). The calls go
    /// through reflection, exactly like <see cref="PlacementImporterCurveRampTests"/>: the
    /// importer lives in <c>Assets/VRC_Katamari/Editor/LevelBuild/</c> and this assembly
    /// references only <c>VRCKatamari.Runtime</c>, so it cannot name editor types.
    /// </para>
    /// <para>
    /// What is <b>not</b> covered here: the asset existing and the scene-side create/update
    /// (<c>+N ~0 -0</c> then <c>+0 ~0 -0</c>). That needs the asset tree and a scene, so it is
    /// the Unity operator's check. The declared model is expected to be a def's own FBX, e.g.
    /// <c>Assets/ThirdParty/Models/mug/mug.fbx</c>.
    /// </para>
    /// </remarks>
    public class PlacementImporterModelPathTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string RepoProp = "Assets/VRC_Katamari/Art/Props/apple/apple.fbx";
        private const string DeclaredModel = "Assets/ThirdParty/Models/mug/mug.fbx";

        [Test]
        public void DeclaredModelWinsOverTheRepoProp()
        {
            Assert.AreEqual(DeclaredModel, Invoke("ResolvePropModelPath", "mug", DeclaredModel),
                "a def's model must be what the importer instantiates, not Art/Props/<def>");
        }

        [Test]
        public void WithoutAModelTheHistoricRepoPathIsUnchanged()
        {
            Assert.AreEqual(RepoProp, Invoke("ResolvePropModelPath", "apple", (string)null));
            Assert.AreEqual(RepoProp, Invoke("ResolvePropModelPath", "apple", ""));
        }

        [Test]
        public void ModelPathsMustBeUnityRelativeFbxUnderAssets()
        {
            Assert.IsTrue((bool)Invoke("IsValidModelPath", DeclaredModel));

            Assert.IsFalse((bool)Invoke("IsValidModelPath", (string)null));
            Assert.IsFalse((bool)Invoke("IsValidModelPath", ""));
            Assert.IsFalse((bool)Invoke("IsValidModelPath",
                "Assets/ThirdParty/Models/mug/mug.glb"), "only .fbx is a model");
            Assert.IsFalse((bool)Invoke("IsValidModelPath",
                "VRC_Katamari/Art/Props/apple/apple.fbx"), "the path is Unity-relative");
            Assert.IsFalse((bool)Invoke("IsValidModelPath", "Assets/../apple.fbx"),
                "'..' would leave the project");
            Assert.IsFalse((bool)Invoke("IsValidModelPath", "Assets/Models/../../x.fbx"),
                "'..' would leave the project");
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
            MethodInfo method = ImporterType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, ImporterTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }
    }
}
