using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The v1 regression: a <c>katamari.placement/0</c> document must keep parsing and
    /// validating exactly as it did, and must never be sniffed as v2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "don't break level 1" gate for the v2 work. It runs the REAL
    /// <c>JsonUtility</c> deserialization into the REAL v1 DTO, then calls the importer's own
    /// <c>ValidateDocument</c> with the live <c>story_01_bench_size</c> document. If the v2
    /// branch ever takes over the v1 path (a sniff bug, a changed DTO, a new required field),
    /// this test goes red without needing a scene, a model or an asset.
    /// </para>
    /// <para>
    /// The two assertions that matter most: <c>props.Length == 130</c> (the level's authored
    /// count) and <c>ValidateDocument == true</c> (the v1 rules still accept their own data).
    /// The scene side of a v1 import (<c>+130 ~0 -0</c>, then <c>+0 ~0 -0</c>) still needs the
    /// operator's Unity run with the prop models present; it is not claimed here.
    /// </para>
    /// </remarks>
    public class PlacementImporterV1RegressionTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";
        private const string PathsTypeName = "VRCKatamari.Editor.LevelBuild.LevelPaths";
        private const string ReaderTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Reader";

        private Type importerType;
        private Type reportType;
        private Type pathsType;
        private Type readerType;

        [SetUp]
        public void SetUp()
        {
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            pathsType = FindType(PathsTypeName);
            readerType = FindType(ReaderTypeName);
            Assert.IsNotNull(importerType, ImporterTypeName + " not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " not loaded");
            Assert.IsNotNull(pathsType, PathsTypeName + " not loaded");
            Assert.IsNotNull(readerType, ReaderTypeName + " not loaded");
        }

        [Test]
        public void V1DocumentStillDeserializesIntoTheV1Shape()
        {
            string text = ReadStoryPlacement();
            if (text == null) Assert.Ignore("story_01_bench_size is not in this checkout");

            Assert.IsFalse((bool)Invoke(readerType, "LooksLikeV2", new object[] { text }),
                "a v1 document must not be sniffed as v2");

            object doc = ParseV1(text);
            Assert.IsNotNull(doc, "JsonUtility returned no document");
            Assert.AreEqual("katamari.placement/0", Field(doc, "schema"));

            var props = (Array)Field(doc, "props");
            var defs = (Array)Field(doc, "defs");
            var structures = (Array)Field(doc, "structures");
            Assert.AreEqual(130, props.Length, "the level's authored prop count");
            Assert.Greater(defs.Length, 0, "the level declares defs");
            Assert.AreEqual(11, structures.Length, "the level's authored structures");

            // The v2-only columns exist on the DTO but a v1 document leaves them empty: that is
            // what "do not add a required field to v1" means in practice.
            object firstProp = props.GetValue(0);
            Assert.IsNull(Field(firstProp, "scale"), "a v1 prop has no authored scale");
            Assert.IsNull(Field(firstProp, "parent"), "a v1 prop has no parent pointer");

            // And the v1 document still declares its own ground plane (the v2 branch has none).
            int grounds = 0;
            for (int i = 0; i < structures.Length; i++)
            {
                object structure = structures.GetValue(i);
                if (string.Equals((string)Field(structure, "role"), "ground", StringComparison.Ordinal))
                {
                    grounds++;
                }
            }

            Assert.AreEqual(1, grounds, "a v1 level has exactly one role=ground");
        }

        [Test]
        public void V1ValidationStillAcceptsTheLiveV1Document()
        {
            string text = ReadStoryPlacement();
            if (text == null) Assert.Ignore("story_01_bench_size is not in this checkout");
            object doc = ParseV1(text);
            object report = Activator.CreateInstance(reportType);

            bool ok = (bool)Invoke(importerType, "ValidateDocument", new[] { doc, report });
            if (!ok)
            {
                Assert.Fail("the v1 validator refused the live v1 document: " +
                            Join((IEnumerable)Field(report, "Errors")));
            }
        }

        [Test]
        public void V1DocumentCarriesNoV2KeyThatWouldChangeItsMeaning()
        {
            string text = ReadStoryPlacement();
            if (text == null) Assert.Ignore("story_01_bench_size is not in this checkout");
            StringAssert.Contains("katamari.placement/0", text);
            StringAssert.DoesNotContain("katamari.placement/2", text);
            // A v1 document has no v2-only layers; if it grows one, the two branches need a
            // written rule instead of a sniff.
            bool hasTerrain = text.IndexOf("\"terrain\"", StringComparison.Ordinal) >= 0;
            bool hasActivity = text.IndexOf("\"activityArea\"", StringComparison.Ordinal) >= 0;
            bool hasMeta = text.IndexOf("\"meta\"", StringComparison.Ordinal) >= 0;
            Assert.IsFalse(hasTerrain, "level 1 has no terrain block");
            Assert.IsFalse(hasActivity, "level 1 has no activityArea block");
            Assert.IsFalse(hasMeta, "level 1 keeps its `level` block, not `meta`");
        }

        // --------------------------------------------------------------- helpers ---

        private string ReadStoryPlacement()
        {
            object levels = Invoke(pathsType, "LevelsFolder", new object[0]);
            if (levels == null)
            {
                string root = (string)Invoke(pathsType, "RepoRoot", new object[0]);
                if (string.IsNullOrEmpty(root)) return null;
                string candidate = System.IO.Path.Combine(root, "Docs/Design/Levels",
                    "story_01_bench_size/placement.json");
                return System.IO.File.Exists(candidate) ? System.IO.File.ReadAllText(candidate) : null;
            }

            string path = System.IO.Path.Combine((string)levels,
                "story_01_bench_size/placement.json");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }

        private object ParseV1(string text)
        {
            Type docType = importerType.GetNestedType("PlacementDocument", BindingFlags.Public);
            Assert.IsNotNull(docType, "PlacementImporter.PlacementDocument not found");
            return JsonUtility.FromJson(text, docType);
        }

        private static string Join(IEnumerable values)
        {
            var text = new System.Text.StringBuilder();
            foreach (object value in values)
            {
                if (text.Length > 0) text.Append(" | ");
                text.Append(value);
            }

            return text.ToString();
        }

        private static object Field(object target, string name)
        {
            Assert.IsNotNull(target, "cannot read field '" + name + "' from a null object");
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, target.GetType().Name + " has no field '" + name + "'");
            return field.GetValue(target);
        }

        private static object Invoke(Type type, string name, object[] args)
        {
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
