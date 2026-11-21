using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the display fields of a structure solid (<c>def</c> and <c>visible</c>, LEVEL_FORMAT_V2
    /// 4): they decide what is DRAWN and never what the ball touches. A solid with neither keeps
    /// its placeholder cube; <c>visible:false</c> draws nothing; <c>def</c> draws that def's model.
    /// </summary>
    public class PlacementStructureDisplayTests
    {
        private const string ReaderTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Reader";
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";

        private Type readerType;
        private Type importerType;
        private Type reportType;

        [SetUp]
        public void SetUp()
        {
            readerType = FindType(ReaderTypeName);
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            Assert.IsNotNull(readerType, ReaderTypeName + " not loaded");
            Assert.IsNotNull(importerType, ImporterTypeName + " not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " not loaded");
        }

        // ------------------------------------------------------------ parsing ---

        [Test]
        public void TheReaderCarriesDefAndVisibleAndDefaultsToVisibleWithNoDef()
        {
            object doc = Parse(Solid("plain", "") + "," + Solid("shown", @",""def"":""d""") + "," +
                               Solid("hidden", @",""visible"":false"));
            object[] solids = (object[])(Array)Field(doc, "Solids");

            Assert.IsNull(Field(solids[0], "Def"));
            Assert.AreEqual(true, Field(solids[0], "Visible"), "visible defaults to true");
            Assert.AreEqual("d", Field(solids[1], "Def"));
            Assert.AreEqual(false, Field(solids[2], "Visible"));
        }

        [Test]
        public void ADefThatIsNotDeclaredOrAHiddenSolidWithADefIsRefused()
        {
            AssertFirstErrorContains(Solid("a", @",""def"":""nope"""), "[structure_display]");
            AssertFirstErrorContains(Solid("b", @",""def"":""d"",""visible"":false"), "[structure_display]");
            AssertFirstErrorContains(Solid("c", @",""def"":"""""), "[structure_display]");
        }

        // ----------------------------------------------------- the placeholder rule ---

        [Test]
        public void TheCubeIsShownOnlyWhenNothingElseShowsTheSolid()
        {
            object doc = Parse(Solid("plain", "") + "," + Solid("shown", @",""def"":""d""") + "," +
                               Solid("hidden", @",""visible"":false"));
            object view = Invoke(importerType, "BuildV1View", new[] { doc });

            Assert.IsTrue(CubeShown(view, "plain"), "no def, visible: the placeholder stays (vs_00's 35 solids)");
            Assert.IsFalse(CubeShown(view, "shown"), "a def is drawn instead of the cube");
            Assert.IsFalse(CubeShown(view, "hidden"), "visible:false draws nothing");
        }

        [Test]
        public void AHiddenSolidKeepsItsColliderAndAReimportWritesNothing()
        {
            object doc = Parse(Solid("wall", @",""visible"":false"));
            object view = Invoke(importerType, "BuildV1View", new[] { doc });
            var root = new GameObject("PlacementStructureDisplayTests");
            root.hideFlags = HideFlags.DontSave;
            try
            {
                object first = Activator.CreateInstance(reportType);
                Assert.IsTrue((bool)Invoke(importerType, "ApplyStructures",
                    new[] { view, root.transform, first }), Errors(first));
                Transform wall = root.transform.Find("wall");
                Assert.IsNotNull(wall);
                Assert.IsNotNull(wall.GetComponent<BoxCollider>(), "the invisible wall still collides");
                Assert.IsFalse(wall.GetComponent<MeshRenderer>().enabled, "nothing is drawn");

                object second = Activator.CreateInstance(reportType);
                Assert.IsTrue((bool)Invoke(importerType, "ApplyStructures",
                    new[] { view, root.transform, second }), Errors(second));
                Assert.AreEqual(0, Field(second, "StructuresCreated"));
                Assert.AreEqual(0, Field(second, "StructuresUpdated"), "settled: nothing written");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ADisplayModelThatIsMissingIsAnErrorAndTheCubeStaysVisible()
        {
            object doc = Parse(Solid("post", @",""def"":""d"""));
            object view = Invoke(importerType, "BuildV1View", new[] { doc });
            var root = new GameObject("PlacementStructureDisplayTests");
            root.hideFlags = HideFlags.DontSave;
            try
            {
                object report = Activator.CreateInstance(reportType);
                bool ok = (bool)Invoke(importerType, "ApplyStructures",
                    new[] { view, root.transform, report });

                Assert.IsFalse(ok, "a missing model fails the structure pass");
                StringAssert.Contains("[model_missing]", Errors(report));
                Transform post = root.transform.Find("post");
                Assert.IsTrue(post.GetComponent<MeshRenderer>().enabled,
                    "never invisible by accident: the placeholder is drawn again");
                Assert.IsNotNull(post.GetComponent<BoxCollider>());
                Assert.IsNull(post.Find("Model"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------- model fitting ---

        [Test]
        public void AModelIsScaledPerAxisToFillTheSolidsCubeAndCentred()
        {
            // The reference level's 72 m pillar: model 0.13 x 0.13 x 3.388, solid size 8.9 x 72 x 19.7
            // (the original's instance scale x68 y554 z5.8 is already inside the size).
            object[] args =
            {
                new Vector3(0.13f, 0.13f, 3.388f), new Vector3(0.01f, 0f, -0.02f), null, null,
            };
            Invoke(importerType, "FitModelToUnitCube", args);
            var scale = (Vector3)args[2];
            var position = (Vector3)args[3];

            Assert.AreEqual(1f / 0.13f, scale.x, 1e-4f);
            Assert.AreEqual(1f / 0.13f, scale.y, 1e-4f);
            Assert.AreEqual(1f / 3.388f, scale.z, 1e-6f);
            // the parent cube then multiplies by size: the world extent is size, the mesh centre is the cube's centre
            Assert.AreEqual(8.9f, 0.13f * scale.x * 8.9f, 1e-3f);
            Assert.AreEqual(-0.01f * scale.x, position.x, 1e-4f);
            Assert.AreEqual(0.02f * scale.z, position.z, 1e-5f);
        }

        // --------------------------------------------------------------- helpers ---

        private bool CubeShown(object view, string id)
        {
            foreach (object structure in (Array)Field(view, "structures"))
            {
                if ((string)Field(structure, "id") == id)
                {
                    return (bool)Invoke(importerType, "PlaceholderCubeShown", new[] { structure });
                }
            }

            Assert.Fail("no structure '" + id + "' in the view");
            return false;
        }

        private static string Solid(string id, string extra)
        {
            return @"{""id"":""" + id + @""",""kind"":""k"",""role"":""box"",""shape"":""box""," +
                   @"""center"":[0,0,0],""size"":[2,4,2],""rotationQuat"":[0,0,0,1],""collision"":""box""," +
                   @"""absorbable"":false,""support"":false,""proxySource"":""manual""" + extra + "}";
        }

        private static string Document(string solids)
        {
            return @"{""schema"":""katamari.placement/2""," +
                   @"""meta"":{""id"":""probe"",""scene"":""S"",""units"":""m""," +
                   @"""convention"":""unity-y-up-left-handed-quat-xyzw""," +
                   @"""ball"":{""startDiameterM"":0.3,""maxDiameterRef"":""global""," +
                   @"""start"":[0,0,0],""spawnSource"":""authored""}}," +
                   @"""defs"":[{""id"":""d"",""kind"":""k"",""model"":""Assets/_RefUnlicensed/Models/none/none.fbx""}]," +
                   @"""props"":[],""structures"":{""solids"":[" + solids + "]}}";
        }

        private object Parse(string solids)
        {
            object result = Invoke(readerType, "Read", new object[] { Document(solids) });
            Assert.IsNull(FirstError(result), "the fixture must parse clean");
            return Field(result, "Document");
        }

        private void AssertFirstErrorContains(string solids, string fragment)
        {
            object result = Invoke(readerType, "Read", new object[] { Document(solids) });
            string error = FirstError(result);
            Assert.IsNotNull(error, "expected the reader to refuse: " + solids);
            StringAssert.Contains(fragment, error);
        }

        private static string FirstError(object result)
        {
            var errors = Field(result, "Errors") as System.Collections.IEnumerable;
            if (errors == null) return null;
            foreach (object error in errors) return (string)error;
            return null;
        }

        private static string Errors(object report)
        {
            var errors = Field(report, "Errors") as System.Collections.IEnumerable;
            if (errors == null) return "";
            var text = new System.Text.StringBuilder();
            foreach (object error in errors) text.Append(error).Append(" | ");
            return text.ToString();
        }

        private static object Field(object target, string name)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(target);
            PropertyInfo property = type.GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(property, type.Name + "." + name + " not found");
            return property.GetValue(target, null);
        }

        private static object Invoke(Type type, string name, object[] args)
        {
            MethodInfo method = type.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, type.Name + "." + name + " not found");
            return method.Invoke(null, args);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }

            return null;
        }
    }
}
