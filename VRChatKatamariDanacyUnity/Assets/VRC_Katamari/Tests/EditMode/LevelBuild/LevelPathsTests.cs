using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the ONE level-path resolution both importers use: repository root, nested level ids
    /// and the canonical-vs-side placement filename order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two defects these tests were written for (measured 2026-11-16):
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>TerrainMeshImporter.ProjectRoot()</c> returned the UNITY project
    /// directory, so it looked for <c>VRChatKatamariDanacyUnity/Docs/Design/Levels/…</c> and
    /// reported <c>[terrain_source] no placement.v2.json …</c> for a level that was right
    /// there.</description></item>
    /// <item><description><c>Import("C21")</c> built <c>Levels/C21</c> by concatenation, while
    /// the physics test field's cells live in <c>Levels/physics_test_field/C21</c>.</description></item>
    /// </list>
    /// <para>
    /// A test that needs the git-ignored reference tree or a specific test-field directory
    /// skips (never fails) when this checkout does not have it.
    /// </para>
    /// </remarks>
    public class LevelPathsTests
    {
        private const string PathsTypeName = "VRCKatamari.Editor.LevelBuild.LevelPaths";

        private Type pathsType;

        [SetUp]
        public void SetUp()
        {
            pathsType = FindType(PathsTypeName);
            Assert.IsNotNull(pathsType, PathsTypeName + " not loaded");
        }

        [Test]
        public void RepositoryRootIsFoundAboveTheUnityProject()
        {
            string root = (string)Invoke("RepoRoot", new object[0]);
            Assert.IsNotNull(root, "no repository root found above the Unity project");
            Assert.IsTrue(System.IO.Directory.Exists(
                    System.IO.Path.Combine(root, "Docs/Design/Levels")),
                "the repository root must contain Docs/Design/Levels: " + root);
        }

        [Test]
        public void NestedLevelIdResolvesItsDirectory()
        {
            if (!LevelExists("physics_test_field/C21"))
            {
                Assert.Ignore("Docs/Design/Levels/physics_test_field/C21 is not in this checkout");
            }

            AssertResolvesTo("physics_test_field/C21", "physics_test_field/C21");
        }

        [Test]
        public void BareCellIdResolvesByTheIdTheDocumentDeclares()
        {
            // Import("C21") is how an operator names a cell; the folder is one level deeper, and
            // the document's own meta.id is what must win - not a guess.
            if (!LevelExists("physics_test_field/C21"))
            {
                Assert.Ignore("Docs/Design/Levels/physics_test_field/C21 is not in this checkout");
            }

            AssertResolvesTo("C21", "physics_test_field/C21");
        }

        [Test]
        public void MissingLevelListsEveryPathItTried()
        {
            var args = new object[] { "no_such_level_anywhere", null, null };
            bool found = (bool)Invoke("TryResolveLevelDirectory", args);
            Assert.IsFalse(found);
            var tried = (IList)args[2];
            Assert.IsNotNull(tried, "the resolution must report the paths it tried");
            Assert.Greater(tried.Count, 0, "a failure that names no path is the defect again");
        }

        [Test]
        public void ReferenceLevelPrefersItsV2Document()
        {
            // _ref_cancer has BOTH a v1 placement.json and a placement.v2.json: the v2 sidecar
            // name is the one the reference writer uses, and it must win.
            if (!LevelExists("_ref_cancer"))
            {
                Assert.Ignore("the git-ignored _ref_cancer tree is not on this machine");
            }

            string directory = ResolveDirectory("_ref_cancer");
            var args = new object[] { directory, null, null };
            bool found = (bool)Invoke("TryResolvePlacementIn", args);
            Assert.IsTrue(found, "no placement document in " + directory);
            StringAssert.EndsWith("placement.v2.json", (string)args[1]);
        }

        [Test]
        public void V1LevelStaysOnItsCanonicalDocument()
        {
            if (!LevelExists("story_01_bench_size"))
            {
                Assert.Ignore("Docs/Design/Levels/story_01_bench_size is not in this checkout");
            }

            string directory = ResolveDirectory("story_01_bench_size");
            var args = new object[] { directory, null, null };
            Assert.IsTrue((bool)Invoke("TryResolvePlacementIn", args));
            StringAssert.EndsWith("placement.json", (string)args[1]);
            string text = System.IO.File.ReadAllText((string)args[1]);
            StringAssert.Contains("katamari.placement/0", text,
                "the v1 level must still be the v1 document");
            Assert.IsFalse(LooksLikeV2(text),
                "a v1 document must not be sniffed as v2 (the v1 import path stays untouched)");
        }

        [Test]
        public void ReferenceIdIsRecognisedFromItsLeaf()
        {
            Assert.IsTrue((bool)Invoke("IsReferenceLevelId", new object[] { "_ref_cancer" }));
            Assert.IsTrue((bool)Invoke("IsReferenceLevelId", new object[] { "_ref_cancer/x" }));
            Assert.IsFalse((bool)Invoke("IsReferenceLevelId", new object[] { "story_01_bench_size" }));
        }

        // --------------------------------------------------------------- helpers ---

        private void AssertResolvesTo(string levelId, string expectedSuffix)
        {
            string directory = ResolveDirectory(levelId);
            Assert.IsNotNull(directory, "'" + levelId + "' did not resolve");
            StringAssert.EndsWith(expectedSuffix.Replace('/', System.IO.Path.DirectorySeparatorChar),
                directory);
        }

        private string ResolveDirectory(string levelId)
        {
            var args = new object[] { levelId, null, null };
            bool found = (bool)Invoke("TryResolveLevelDirectory", args);
            if (!found)
            {
                var tried = args[2] as IList;
                Assert.Fail("'" + levelId + "' did not resolve. Tried: " +
                            (tried == null ? "(none)" : string.Join(", ", ToStrings(tried))));
            }

            return (string)args[1];
        }

        private static string[] ToStrings(IList list)
        {
            var values = new string[list.Count];
            for (int i = 0; i < list.Count; i++) values[i] = list[i] == null ? "" : list[i].ToString();
            return values;
        }

        private bool LevelExists(string levelId)
        {
            string root = (string)Invoke("LevelsFolder", new object[0]);
            if (string.IsNullOrEmpty(root)) return false;
            string directory = System.IO.Path.Combine(root,
                levelId.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return System.IO.Directory.Exists(directory);
        }

        private bool LooksLikeV2(string text)
        {
            return (bool)Invoke(FindType("VRCKatamari.Editor.LevelBuild.PlacementV2Reader"),
                "LooksLikeV2", new object[] { text });
        }

        private object Invoke(string name, object[] args)
        {
            return Invoke(pathsType, name, args);
        }

        private static object Invoke(Type type, string name, object[] args)
        {
            Assert.IsNotNull(type, "type for '" + name + "' not loaded");
            MethodInfo method = FindMethod(type, name, args.Length);
            Assert.IsNotNull(method, type.Name + " has no method '" + name + "'");
            return method.Invoke(null, args);
        }

        private static MethodInfo FindMethod(Type type, string name, int argumentCount)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
                if (methods[i].GetParameters().Length == argumentCount) return methods[i];
            }

            return null;
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
