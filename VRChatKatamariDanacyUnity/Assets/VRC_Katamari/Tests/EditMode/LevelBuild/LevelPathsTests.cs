using System;
using System.Collections.Generic;
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
            string projectRoot = (string)Invoke("ProjectRoot", new object[0]);
            string levels = (string)Invoke("LevelsFolder", new object[0]);
            string where = "ProjectRoot=" + projectRoot + " LevelsFolder=" + levels +
                           " RepoRoot=" + root;
            Assert.IsNotNull(root, "no repository root found above the Unity project. " + where);
            // The root is the folder that CONTAINS Docs - not Docs itself, which is what an
            // off-by-one in the `..` walk produced.
            Assert.IsTrue(System.IO.Directory.Exists(System.IO.Path.Combine(root, "Docs")),
                "the repository root must contain Docs: " + where);
            Assert.IsTrue(System.IO.Directory.Exists(
                    System.IO.Path.Combine(root, "Docs/Design/Levels")),
                "the repository root must contain Docs/Design/Levels: " + where);
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
            // `tried` is an APPEND-ONLY list the caller owns (not an out parameter: it may
            // already hold the candidates of an earlier call), so the caller can read it even
            // when the resolution fails - which is the whole point of the diagnostics.
            var tried = new List<string>();
            var args = new object[] { "no_such_level_anywhere", null, tried };
            bool found = (bool)Invoke("TryResolveLevelDirectory", args);
            Assert.IsFalse(found);
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
            if (!System.IO.File.Exists(System.IO.Path.Combine(directory, "placement.v2.json")))
            {
                // The reference tree is git-ignored: a checkout may hold only the v1 file. The
                // ORDER itself is pinned by the temp-directory tests below, which need no data.
                Assert.Ignore("no placement.v2.json in " + directory + " on this machine");
            }

            var tried = new List<string>();
            var args = new object[] { directory, null, tried };
            bool found = (bool)Invoke("TryResolvePlacementIn", args);
            Assert.IsTrue(found, "no placement document in " + directory);
            StringAssert.EndsWith("placement.v2.json", (string)args[1]);
            CollectionAssert.Contains(tried, System.IO.Path.Combine(directory, "placement.json"),
                "both candidate names must be reported, not only the one that won");
        }

        [Test]
        public void V1LevelStaysOnItsCanonicalDocument()
        {
            if (!LevelExists("story_01_bench_size"))
            {
                Assert.Ignore("Docs/Design/Levels/story_01_bench_size is not in this checkout");
            }

            string directory = ResolveDirectory("story_01_bench_size");
            var tried = new List<string>();
            var args = new object[] { directory, null, tried };
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

        // ------------------------------------------------- placement-document order ---
        // These pin the rule without the git-ignored reference tree: build the two candidate
        // files in a temp directory and read which one the resolver picks.

        private const string V1Document =
            @"{""schema"":""katamari.placement/0"",""level"":{""id"":""probe"",""scene"":""S""}," +
            @"""props"":[]}";

        private const string V2Document =
            @"{""schema"":""katamari.placement/2"",""meta"":{""id"":""probe"",""scene"":""S""}," +
            @"""props"":[]}";

        [Test]
        public void AV1LevelAloneStaysOnItsCanonicalDocument()
        {
            string directory = NewTempLevel(V1Document, null);
            try
            {
                string chosen = ChoosePlacement(directory);
                StringAssert.EndsWith("placement.json", chosen);
            }
            finally
            {
                System.IO.Directory.Delete(directory, true);
            }
        }

        [Test]
        public void TheV2SideFileWinsOverAV1Canonical()
        {
            string directory = NewTempLevel(V1Document, V2Document);
            try
            {
                var tried = new List<string>();
                string chosen = ChoosePlacement(directory, tried);
                StringAssert.EndsWith("placement.v2.json", chosen,
                    "the reference levels keep their v2 document in the side file");
                CollectionAssert.Contains(tried,
                    System.IO.Path.Combine(directory, "placement.json"),
                    "the losing candidate is still reported in the diagnostics");
            }
            finally
            {
                System.IO.Directory.Delete(directory, true);
            }
        }

        [Test]
        public void AV2CanonicalWinsOverTheSideFile()
        {
            string directory = NewTempLevel(V2Document, V1Document);
            try
            {
                string chosen = ChoosePlacement(directory);
                StringAssert.EndsWith("placement.json", chosen,
                    "a level that upgraded in place is read from the canonical name");
            }
            finally
            {
                System.IO.Directory.Delete(directory, true);
            }
        }

        [Test]
        public void AV1FileThatOnlyMENTIONSV2LosesToTheSideFile()
        {
            // The trap a substring test falls into: a v1 document whose NOTES mention the v2
            // schema must not be mistaken for a v2 document.
            string v1MentioningV2 =
                @"{""schema"":""katamari.placement/0"",""level"":{""id"":""probe"",""scene"":""S""," +
                @"""notes"":""migrated from katamari.placement/2 on 2026-11-16""},""props"":[]}";
            string directory = NewTempLevel(v1MentioningV2, V2Document);
            try
            {
                string chosen = ChoosePlacement(directory);
                StringAssert.EndsWith("placement.v2.json", chosen,
                    "the preference is decided by the PARSED schema, not by a substring");
            }
            finally
            {
                System.IO.Directory.Delete(directory, true);
            }
        }

        private static string NewTempLevel(string canonical, string side)
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "katamari_levelpaths_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "placement.json"),
                canonical);
            if (side != null)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(directory, "placement.v2.json"), side);
            }

            return directory;
        }

        private string ChoosePlacement(string directory)
        {
            return ChoosePlacement(directory, new List<string>());
        }

        private string ChoosePlacement(string directory, List<string> tried)
        {
            var args = new object[] { directory, null, tried };
            bool found = (bool)Invoke("TryResolvePlacementIn", args);
            Assert.IsTrue(found, "no placement document resolved in " + directory +
                                 ". Tried: " + string.Join(", ", tried.ToArray()));
            return (string)args[1];
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
            var tried = new List<string>();
            var args = new object[] { levelId, null, tried };
            bool found = (bool)Invoke("TryResolveLevelDirectory", args);
            if (!found)
            {
                Assert.Fail("'" + levelId + "' did not resolve. Tried: " +
                            (tried.Count == 0 ? "(none)" : string.Join(", ", tried.ToArray())));
            }

            return (string)args[1];
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
