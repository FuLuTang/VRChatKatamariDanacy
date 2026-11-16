using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the <c>katamari.placement/2</c> reader: the five layers, the presence rules that
    /// <c>JsonUtility</c> cannot express, and the reference document's actual counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection, like every other importer test: <c>PlacementV2Reader</c> and
    /// <c>PlacementV2Document</c> live in <c>Assets/VRC_Katamari/Editor/LevelBuild/</c> and this
    /// assembly references only <c>VRCKatamari.Runtime</c>. Every test below feeds REAL JSON
    /// through the real reader, so it exercises the actual parse path rather than a hand-built
    /// DTO - which is the point: the defect class this guards against (nested objects are never
    /// null under <c>JsonUtility</c>) only appears through a real deserialization.
    /// </para>
    /// <para>
    /// <see cref="ReadsReferenceDocumentWhenPresent"/> is the acceptance gate for the reference
    /// level: a mismatch in ANY of the counts is a bug in the reader, never a reason to touch
    /// the data. It is skipped (not failed) when the git-ignored <c>_ref_cancer</c> tree is not
    /// on this machine.
    /// </para>
    /// </remarks>
    public class PlacementV2DocumentTests
    {
        private const string ReaderTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Reader";
        private const string PathsTypeName = "VRCKatamari.Editor.LevelBuild.LevelPaths";

        private static readonly string MetaJson =
            @"""meta"":{""id"":""probe"",""scene"":""ProbeScene"",""units"":""m""," +
            @"""convention"":""unity-y-up-left-handed-quat-xyzw""," +
            @"""ball"":{""startDiameterM"":0.3,""targetDiameterM"":2.2,""maxDiameterRef"":""global""," +
            @"""start"":[1,2,3],""spawnSource"":""authored""},""timeLimitS"":180," +
            @"""playerSpawn"":[4,5,6],""groundY"":-1.5,""bounds"":[[-10,-20],[10,20]]," +
            @"""spawnClearanceM"":0.5,""zones"":[{""id"":""z"",""code"":1,""kind"":""ground""}]," +
            @"""arena"":{""id"":""probe""}," +
            @"""caps"":{""props"":512,""staticSolids"":256}," +
            @"""budget"":{""planes"":8,""boxes"":64,""ramps"":4}," +
            // NOTE the TWO closing braces: the first closes `source`, the second closes `meta`.
            // Missing the second one made every DocWith(...) fixture an unterminated object.
            @"""expected"":{""propCount"":0},""source"":{""kind"":""authored""}}";

        private Type readerType;
        private Type pathsType;

        [SetUp]
        public void SetUp()
        {
            readerType = FindType(ReaderTypeName);
            pathsType = FindType(PathsTypeName);
            Assert.IsNotNull(readerType, ReaderTypeName + " not loaded");
            Assert.IsNotNull(pathsType, PathsTypeName + " not loaded");
        }

        // ------------------------------------------------------------- fixtures ---

        private static string DocWith(string blocks)
        {
            string json = @"{""schema"":""katamari.placement/2""," + MetaJson +
                          (string.IsNullOrEmpty(blocks) ? "" : "," + blocks) + "}";
            AssertFixtureIsJson(json);
            return json;
        }

        private static string PropJson(string body)
        {
            return @"""props"":[" + body + "]";
        }

        private static string OneProp(string body)
        {
            return PropJson(@"{""itemId"":""p"",""def"":""d"",""kind"":""k""," +
                            @"""position"":[0,0,0],""rotationQuat"":[0,0,0,1],""aabb"":[1,1,1]" +
                            (string.IsNullOrEmpty(body) ? "" : "," + body) + "}");
        }

        // --------------------------------------------------------------- parsing ---

        [Test]
        public void ReadsEveryLayerIntoTheModel()
        {
            string json = DocWith(
                PropJson(@"{""itemId"":""p1"",""def"":""d1"",""kind"":""k"",""behaviour"":""static""," +
                         @"""zone"":""z"",""position"":[1,2,3],""rotationQuat"":[0,0,0,1]," +
                         @"""scale"":[0.5,0.5,0.5],""aabb"":[2,2,2],""volumeK"":0.7," +
                         @"""growthMm3"":1000,""absorbDiameterReqMm"":12,""absorbable"":true," +
                         @"""valueUnits"":1,""parent"":null,""inherit"":""none""}") +
                @",""defs"":[{""id"":""d1"",""kind"":""k"",""model"":""Assets/Ref/d1.fbx""," +
                @"""volumeK"":0.7,""collider"":{""type"":""convex"",""center"":[0,0,0]," +
                @"""hull"":[0,0,0,1,0,0,0,1,0,0,0,1]},""scalePolicy"":""uniform""}]," +
                @"""structures"":{""solids"":[{""id"":""s1"",""kind"":""wall"",""role"":""box""," +
                @"""shape"":""box"",""center"":[0,0,0],""size"":[1,1,1],""rotationQuat"":[0,0,0,1]," +
                @"""collision"":""box"",""absorbable"":false,""support"":true}]}," +
                @"""activityArea"":{""walls"":[{""id"":""w"",""points"":[[0,0],[4,0],[4,4],[0,4]]," +
                @"""closed"":true,""heightM"":3.0,""thicknessM"":0.2,""kind"":""invisible""}]," +
                @"""killPlane"":{""y"":-6.0,""respawn"":""ballStart"",""zoneId"":8}," +
                @"""softMarginM"":0.0,""source"":""authored""}");

            object result = Read(json);
            Assert.IsNull(FirstError(result), "the reader refused a well-formed document");
            object doc = Field(result, "Document");

            Assert.AreEqual("katamari.placement/2", Field(doc, "Schema"));
            object meta = Field(doc, "Meta");
            Assert.AreEqual("probe", Field(meta, "Id"));
            Assert.AreEqual("ProbeScene", Field(meta, "Scene"));
            Assert.AreEqual(180f, Field(meta, "TimeLimitS"));
            Assert.AreEqual(-1.5f, Field(meta, "GroundY"));
            object ball = Field(meta, "Ball");
            Assert.AreEqual(0.3f, Field(ball, "StartDiameterM"));
            Assert.AreEqual(2.2f, Field(ball, "TargetDiameterM"));
            object bounds = Field(meta, "Bounds");
            Assert.AreEqual(2, ((Array)bounds).Length, "bounds must be two XZ pairs");
            Assert.AreEqual(-10f, ((float[])((Array)bounds).GetValue(0))[0]);

            object[] props = Items(Field(doc, "Props"));
            Assert.AreEqual(1, props.Length);
            Assert.AreEqual("p1", Field(props[0], "ItemId"));
            Assert.AreEqual("d1", Field(props[0], "Def"));
            Assert.AreEqual("static", Field(props[0], "Behaviour"));
            Assert.AreEqual(1000L, Field(props[0], "GrowthMm3"));
            Assert.AreEqual(true, Field(props[0], "HasGrowthMm3"));
            Assert.AreEqual(12, Field(props[0], "AbsorbDiameterReqMm"));

            object[] defs = Items(Field(doc, "Defs"));
            Assert.AreEqual(1, defs.Length);
            object collider = Field(defs[0], "Collider");
            Assert.IsNotNull(collider, "a declared collider block must be materialised");
            Assert.AreEqual("convex", Field(collider, "Type"));
            Assert.AreEqual(12, ((float[])Field(collider, "Hull")).Length);

            object[] solids = Items(Field(doc, "Solids"));
            Assert.AreEqual(1, solids.Length);
            Assert.AreEqual("box", Field(solids[0], "Role"));
            Assert.AreEqual(true, Field(solids[0], "Support"));

            object activity = Field(doc, "Activity");
            Assert.IsNotNull(activity);
            object[] walls = Items(Field(activity, "Walls"));
            Assert.AreEqual(1, walls.Length);
            Assert.AreEqual(true, Field(walls[0], "Closed"));
            Assert.AreEqual(3.0f, Field(walls[0], "HeightM"));
            object kill = Field(activity, "KillPlane");
            Assert.AreEqual(-6.0f, Field(kill, "Y"));
            Assert.AreEqual("ballStart", Field(kill, "Respawn"));
        }

        // ---------------------------------------------------- presence by content ---

        [Test]
        public void AbsentColliderBlockIsNotDeclared()
        {
            // The JsonUtility trap: a nested [Serializable] field is materialised empty, so
            // `!= null` says "declared". The v2 reader must answer by CONTENT.
            string json = DocWith(OneProp("") +
                @",""defs"":[{""id"":""d"",""kind"":""k""},{""id"":""e"",""kind"":""k""," +
                @"""collider"":{}}]");

            object result = Read(json);
            Assert.IsNull(FirstError(result));
            object[] defs = Items(Field(Field(result, "Document"), "Defs"));
            Assert.IsNull(Field(defs[0], "Collider"), "an absent collider block must stay null");
            Assert.IsNull(Field(defs[1], "Collider"),
                "an EMPTY collider block is not a declared proxy either");
        }

        [Test]
        public void WrittenNullParentIsNotAKey()
        {
            // `parent: null` and an absent `parent` are the same state; the presence flag is what
            // tells a rule "the author wrote a pointer" from "the author wrote no pointer".
            string json = DocWith(PropJson(
                @"{""itemId"":""a"",""def"":""d"",""position"":[0,0,0]," +
                @"""rotationQuat"":[0,0,0,1],""aabb"":[1,1,1],""parent"":null}," +
                @"{""itemId"":""b"",""def"":""d"",""position"":[0,0,0]," +
                @"""rotationQuat"":[0,0,0,1],""aabb"":[1,1,1]}," +
                @"{""itemId"":""c"",""def"":""d"",""position"":[0,0,0]," +
                @"""rotationQuat"":[0,0,0,1],""aabb"":[1,1,1],""parent"":""a""}"));

            object[] props = Items(Field(Field(Read(json), "Document"), "Props"));
            Assert.AreEqual(false, Field(props[0], "HasParent"), "parent:null is not a parent");
            Assert.AreEqual(false, Field(props[1], "HasParent"), "an absent key is not a parent");
            Assert.AreEqual(true, Field(props[2], "HasParent"));
            Assert.AreEqual("a", Field(props[2], "Parent"));
        }

        [Test]
        public void AbsentArraysAreEmptyAndAbsentBlocksAreNull()
        {
            string json = DocWith(PropJson(""));
            object result = Read(json);
            Assert.IsNull(FirstError(result));
            object doc = Field(result, "Document");
            Assert.AreEqual(0, ((Array)Field(doc, "Solids")).Length);
            Assert.AreEqual(0, ((Array)Field(doc, "Defs")).Length);
            Assert.AreEqual(0, ((Array)Field(doc, "Motions")).Length);
            Assert.AreEqual(0, ((Array)Field(doc, "Paths")).Length);
            Assert.IsNull(Field(doc, "Activity"), "an absent activityArea is null, not an empty block");
            Assert.IsNull(Field(doc, "Terrain"), "an absent terrain block is null");
        }

        // -------------------------------------------------------- structural rules ---

        [Test]
        public void UnknownKeyWarnsAndStillReads()
        {
            string json = DocWith(OneProp(@"""weight"":5"));
            object result = Read(json);
            Assert.IsNull(FirstError(result), "an unknown key is a warning, not a refusal");
            // Warnings is a List<string> on the result (one per unknown key); join it as a
            // sequence rather than casting to an array.
            string warnings = string.Join(" | ",
                (System.Collections.Generic.IEnumerable<string>)Field(result, "Warnings"));
            StringAssert.Contains("[v2_key]", warnings);
            StringAssert.Contains("props[0].weight", warnings);
        }

        [Test]
        public void WrongSchemaUnitsAndConventionAreErrors()
        {
            string noMeta = @"{""schema"":""katamari.placement/2"",""props"":[]}";
            StringAssert.Contains("[v2_source]", FirstError(Read(noMeta)));

            string wrongUnits = DocWith(PropJson("")).Replace(@"""units"":""m""",
                @"""units"":""cm""");
            StringAssert.Contains("[meta_units]", FirstError(Read(wrongUnits)));

            string wrongConvention = DocWith(PropJson("")).Replace(
                @"unity-y-up-left-handed-quat-xyzw", @"other-convention");
            StringAssert.Contains("[meta_convention]", FirstError(Read(wrongConvention)));

            string wrongSchema = DocWith(PropJson("")).Replace("katamari.placement/2",
                "katamari.placement/0");
            StringAssert.Contains("[v2_schema]", FirstError(Read(wrongSchema)));
        }

        [Test]
        public void MotionsAndPathsArePreservedWithoutRefusingTheDocument()
        {
            // agent-70 is filling these tables right now: a non-empty motion/path table must
            // never make a level fail to import, even though this round executes neither.
            string json = DocWith(
                PropJson("") +
                @",""motions"":[{""id"":""m1"",""class"":""sporadic_walk""," +
                @"""speed"":{""source"":""type"",""cmPerSec"":12.5},""params"":{""radiusM"":1.0}," +
                @"""updatesPerSecond"":15,""loop"":true," +
                @"""provenance"":{""moveType"":13,""action"":2}}]," +
                @"""paths"":[{""id"":""p1"",""points"":[[0,0,0],[1,0,0]],""loop"":true," +
                @"""speedCmPerSec"":40}]");

            object result = Read(json);
            Assert.IsNull(FirstError(result));
            object doc = Field(result, "Document");
            object[] motions = Items(Field(doc, "Motions"));
            object[] paths = Items(Field(doc, "Paths"));
            Assert.AreEqual(1, motions.Length);
            Assert.AreEqual(1, paths.Length);
            Assert.AreEqual("sporadic_walk", Field(motions[0], "Class"));
            Assert.AreEqual("type", Field(motions[0], "SpeedSource"));
            Assert.AreEqual(12.5f, Field(motions[0], "SpeedCmPerSec"));
            Assert.AreEqual(1, ((string[])Field(motions[0], "ParamKeys")).Length);
            Assert.AreEqual(2, ((float[][])Field(paths[0], "Points")).Length);
        }

        // ----------------------------------------------------- the reference level ---

        [Test]
        public void ReadsReferenceDocumentWhenPresent()
        {
            string repoRoot = (string)Invoke(pathsType, "RepoRoot", new object[0]);
            if (string.IsNullOrEmpty(repoRoot))
            {
                Assert.Ignore("no repository root above this Unity project; skipped");
            }

            string path = System.IO.Path.Combine(repoRoot,
                "Docs/Design/Levels/_ref_cancer/placement.v2.json");
            if (!System.IO.File.Exists(path))
            {
                Assert.Ignore("no _ref_cancer reference data on this machine; skipped");
            }

            object result = Invoke(readerType, "ReadFile", new object[] { path });
            Assert.IsNull(FirstError(result), "the reference document must parse clean");

            object summary = Invoke(FindType("VRCKatamari.Editor.LevelBuild.PlacementV2Rules"),
                "Summarize", new[] { Field(result, "Document") });

            // These are the numbers the document states, not numbers read back from the file:
            // a mismatch is a reader bug, never a reason to edit the data.
            Assert.AreEqual(511, Field(summary, "PropCount"), "props");
            Assert.AreEqual(145, Field(summary, "DefCount"), "defs");
            Assert.AreEqual(144, Field(summary, "DefWithColliderCount"), "defs with a collider");
            Assert.AreEqual(35, Field(summary, "SolidCount"), "structures.solids");
            Assert.AreEqual(83, Field(summary, "ParentCount"), "parent edges");
            // 511 props, 83 of them parented, so 428 have no parent (the graph's roots). The
            // 72 "chain heads" a reader might expect are props whose parent is itself a root -
            // a different number, and not what this counter means.
            Assert.AreEqual(428, Field(summary, "ParentRootCount"), "props without a parent");
            Assert.AreEqual(5, Field(summary, "ParentMaxDepth"), "deepest parent chain");
            Assert.AreEqual(5, Field(summary, "UniformScaleCount"), "uniform non-unit scales");
            Assert.AreEqual(1, Field(summary, "NonUniformScaleCount"), "non-uniform scales");
            Assert.AreEqual(1, Field(summary, "WallCount"), "activity walls");
            Assert.AreEqual(4, Field(summary, "WallSegmentCount"), "wall plane segments");
            Assert.AreEqual(2334698102952L, Field(summary, "GrowthMm3Total"),
                "sum(growthMm3) must equal meta.expected.growthMm3");
            Assert.IsNull(Field(summary, "ParentError"));

            double parseMs = (double)Field(result, "ParseMilliseconds");
            Debug.Log("[v2] reference document parsed in " +
                      parseMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                      " ms: props=" + Field(summary, "PropCount") +
                      " defs=" + Field(summary, "DefCount") +
                      " solids=" + Field(summary, "SolidCount") +
                      " parents=" + Field(summary, "ParentCount") +
                      " wallSegments=" + Field(summary, "WallSegmentCount"));
            Assert.Greater(parseMs, 0.0, "the reader must report its own parse time");
        }

        // --------------------------------------------------------------- helpers ---

        private object Read(string json)
        {
            return Invoke(readerType, "Read", new object[] { json });
        }

        private static string FirstError(object result)
        {
            var errors = (IEnumerable)Field(result, "Errors");
            foreach (object error in errors) return (string)error;
            return null;
        }

        private static object[] Items(object arrayField)
        {
            if (arrayField == null) return new object[0];
            var array = (Array)arrayField;
            var items = new object[array.Length];
            for (int i = 0; i < array.Length; i++) items[i] = array.GetValue(i);
            return items;
        }

        /// <summary>
        /// Fails with the fixture text when the fixture is not valid JSON.
        /// </summary>
        /// <remarks>
        /// This exists because a missing brace in a fixture helper surfaced as
        /// "[v2_source] not valid JSON: unterminated object" INSIDE the reader - i.e. the test
        /// looked like a reader bug. Parsing the fixture with the production parser first makes
        /// a broken fixture say so, and prints the text that is wrong.
        /// </remarks>
        private static void AssertFixtureIsJson(string json)
        {
            Type parserType = FindType("VRCKatamari.Editor.LevelBuild.LevelJsonParser");
            Assert.IsNotNull(parserType, "LevelJsonParser not loaded");
            MethodInfo parse = parserType.GetMethod("ParseText",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(parse, "LevelJsonParser.ParseText not found");
            try
            {
                parse.Invoke(null, new object[] { json });
            }
            catch (TargetInvocationException exception)
            {
                Assert.Fail("the test fixture is not valid JSON (" +
                            (exception.InnerException == null
                                ? "unknown parse error"
                                : exception.InnerException.Message) + "):\n" + json);
            }
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
            MethodInfo method = FindMethod(type, name, args == null ? 0 : args.Length);
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
