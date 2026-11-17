using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins which props get a MotionHost row and with which numbers, by feeding real JSON through
    /// the real reader and <c>PlacementMotionRows.Build</c> (reflection, like every importer test:
    /// the Editor code is not referenced by this assembly). No scene, no GameObject. The writer
    /// that puts the columns into the component (<c>PlacementImporter.Motion.cs</c>) and the
    /// component itself need Unity; see Docs/Design/PickupAbsorb/MOTION_HOST.md.
    /// </summary>
    public class PlacementMotionRowsTests
    {
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
            @"""expected"":{""propCount"":0},""source"":{""kind"":""authored""}}";

        private static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name);
                if (type != null) return type;
            }
            return null;
        }

        private static object Field(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name);
            Assert.IsNotNull(field, name);
            return field.GetValue(target);
        }

        private static object Build(string blocks, ISet<string> animated)
        {
            Type reader = FindType("VRCKatamari.Editor.LevelBuild.PlacementV2Reader");
            Type rows = FindType("VRCKatamari.Editor.LevelBuild.PlacementMotionRows");
            Assert.IsNotNull(reader);
            Assert.IsNotNull(rows);
            string json = @"{""schema"":""katamari.placement/2""," + MetaJson + "," + blocks + "}";
            object result = reader.GetMethod("Read", new[] { typeof(string) }).Invoke(null, new object[] { json });
            object doc = Field(result, "Document");
            Assert.IsNotNull(doc);
            return rows.GetMethod("Build").Invoke(null, new object[] { doc, animated });
        }

        private static string Prop(string id, string body)
        {
            return @"{""itemId"":""" + id + @""",""def"":""crab"",""kind"":""k"",""position"":[0,0,0]," +
                   @"""rotationQuat"":[0,0,0,1],""aabb"":[1,0.4,1]" + (body == "" ? "" : "," + body) + "}";
        }

        private const string Defs = @"""defs"":[{""id"":""crab"",""kind"":""k"",""model"":""x.fbx"",""speedIndex"":1}]";

        private const string Motions =
            @"""motions"":[" +
            @"{""id"":""walk"",""class"":""sporadic_walk"",""speed"":{""source"":""type""},""updatesPerSecond"":15,""loop"":true}," +
            @"{""id"":""spin"",""class"":""spin"",""speed"":{""source"":""none""},""updatesPerSecond"":5,""loop"":true}," +
            @"{""id"":""wait"",""class"":""wait_for_player"",""speed"":{""source"":""none""},""params"":{""triggerRadiusM"":3.0},""updatesPerSecond"":5,""loop"":true,""provenance"":{""moveType"":7,""motionID"":0}}," +
            @"{""id"":""lap"",""class"":""path_collision"",""speed"":{""source"":""path""},""path"":""p1"",""updatesPerSecond"":15,""loop"":true}" +
            @"],""paths"":[{""id"":""p1"",""points"":[[0,0,0],[1,0,0],[1,0,1]],""speedCmPerSec"":24.4}]";

        [Test]
        public void OwnProgramsRidersAndStaticParentsAreSeparated()
        {
            // Order in the document: the rider comes BEFORE its parent on purpose.
            string props = @"""props"":[" +
                Prop("child", @"""behaviour"":""attached"",""parent"":""table"",""inherit"":""full""") + "," +
                Prop("table", @"""behaviour"":""motion"",""motion"":""spin""") + "," +
                Prop("onStatic", @"""behaviour"":""attached"",""parent"":""rock"",""inherit"":""full""") + "," +
                Prop("rock", "") + "," +
                Prop("crab", @"""behaviour"":""motion"",""motion"":""walk""") + "]";
            object table = Build(props + "," + Defs + "," + Motions, null);

            string[] ids = (string[])Field(table, "ItemIds");
            Assert.AreEqual(3, ids.Length, "table, child and crab move; onStatic rides a static rock");
            CollectionAssert.DoesNotContain(ids, "onStatic");
            CollectionAssert.DoesNotContain(ids, "rock");
            int tableRow = Array.IndexOf(ids, "table");
            int childRow = Array.IndexOf(ids, "child");
            Assert.Less(tableRow, childRow, "the parent row precedes its rider");
            int[] parent = (int[])Field(table, "ParentRow");
            Assert.AreEqual(tableRow, parent[childRow]);
            Assert.AreEqual(-1, parent[tableRow]);
            Assert.AreEqual(MotionHostMath.InheritFull, ((int[])Field(table, "Inherit"))[childRow]);
            Assert.AreEqual(2, Field(table, "OwnProgramRows"));
            Assert.AreEqual(1, Field(table, "RiderRows"));
            Assert.AreEqual(MotionHostMath.ClassNone, ((int[])Field(table, "MotionClass"))[childRow]);
        }

        [Test]
        public void PhaseIsTheHashUnlessTheAuthorOverridesIt()
        {
            string props = @"""props"":[" +
                Prop("a", @"""behaviour"":""motion"",""motion"":""spin""") + "," +
                Prop("b", @"""behaviour"":""motion"",""motion"":""spin"",""phase"":0.25") + "]";
            object table = Build(props + "," + Defs + "," + Motions, null);
            string[] ids = (string[])Field(table, "ItemIds");
            int[] seeds = (int[])Field(table, "PhaseSeed");
            Assert.AreEqual(PhaseSeed.From("probe", "a"), seeds[Array.IndexOf(ids, "a")]);
            Assert.AreEqual(0.25f, MotionMath.Phase01(seeds[Array.IndexOf(ids, "b")]), 2e-6f);
            // Deterministic: a second build gives the same seeds.
            object again = Build(props + "," + Defs + "," + Motions, null);
            CollectionAssert.AreEqual(seeds, (int[])Field(again, "PhaseSeed"));
        }

        [Test]
        public void SpeedAndTriggerGateAreResolvedOnce()
        {
            string props = @"""props"":[" +
                Prop("w", @"""behaviour"":""motion"",""motion"":""walk""") + "," +
                Prop("t", @"""behaviour"":""motion"",""motion"":""wait""") + "," +
                Prop("r", @"""behaviour"":""motion"",""motion"":""lap""") + "]";
            object table = Build(props + "," + Defs + "," + Motions, null);
            string[] ids = (string[])Field(table, "ItemIds");
            float[] speed = (float[])Field(table, "SpeedMps");
            Assert.AreEqual(0.3f, speed[Array.IndexOf(ids, "w")], 1e-5f, "speedIndex 1 = 1 cm/frame = 0.3 m/s");
            Assert.AreEqual(0.244f, speed[Array.IndexOf(ids, "r")], 1e-5f, "the path record's speed wins");
            float[] gate = (float[])Field(table, "TriggerRadiusM");
            Assert.AreEqual(3f, gate[Array.IndexOf(ids, "t")], 0f, "params.triggerRadiusM, never the 12 m wake radius");
            Assert.AreEqual(0, ((int[])Field(table, "MotionIdCode"))[Array.IndexOf(ids, "t")]);
            Assert.AreEqual(0.2f, ((float[])Field(table, "HalfHeightM"))[0], 1e-6f, "aabb[1] / 2");
        }

        [Test]
        public void PathsAreFlattenedAndReferencedByRow()
        {
            string props = @"""props"":[" + Prop("r", @"""behaviour"":""motion"",""motion"":""lap""") + "]";
            object table = Build(props + "," + Defs + "," + Motions, null);
            Assert.AreEqual(1, ((int[])Field(table, "PathPointCount")).Length);
            Assert.AreEqual(3, ((int[])Field(table, "PathPointCount"))[0]);
            float[] points = (float[])Field(table, "PathPoints");
            Assert.AreEqual(9, points.Length);
            Assert.AreEqual(1f, points[8], 0f);
            Assert.AreEqual(0, ((int[])Field(table, "PathIndex"))[0]);
            Assert.IsTrue(((bool[])Field(table, "PathClosed"))[0], "loop absent: the class wraps");
        }

        [Test]
        public void AnAnimatedPropGetsAnAnimationOnlyRow()
        {
            string props = @"""props"":[" + Prop("idle", "") + "," + Prop("plain", "") + "]";
            object table = Build(props + "," + Defs + "," + Motions, new HashSet<string> { "idle" });
            CollectionAssert.AreEqual(new[] { "idle" }, (string[])Field(table, "ItemIds"));
            Assert.AreEqual(1, Field(table, "AnimationOnlyRows"));
            Assert.AreEqual(MotionHostMath.ClassNone, ((int[])Field(table, "MotionClass"))[0]);
        }

        [Test]
        public void AMissingMotionIsReportedAndTheRowStaysStatic()
        {
            string props = @"""props"":[" + Prop("ghost", @"""behaviour"":""motion"",""motion"":""nope""") + "]";
            object table = Build(props + "," + Defs + "," + Motions, null);
            Assert.AreEqual(0, ((string[])Field(table, "ItemIds")).Length);
            var errors = (List<string>)Field(table, "Errors");
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("[prop_motion]", errors[0]);
        }

        [Test]
        public void ReferenceLevelRowCountsWhenPresent()
        {
            Type paths = FindType("VRCKatamari.Editor.LevelBuild.LevelPaths");
            string repoRoot = (string)paths.GetMethod("RepoRoot").Invoke(null, new object[0]);
            string file = string.IsNullOrEmpty(repoRoot) ? null : System.IO.Path.Combine(repoRoot,
                "Docs/Design/Levels/_ref_cancer/placement.v2.json");
            if (file == null || !System.IO.File.Exists(file))
            {
                Assert.Ignore("no _ref_cancer reference data on this machine; skipped");
            }
            Type reader = FindType("VRCKatamari.Editor.LevelBuild.PlacementV2Reader");
            object result = reader.GetMethod("ReadFile").Invoke(null, new object[] { file });
            object doc = Field(result, "Document");
            object table = FindType("VRCKatamari.Editor.LevelBuild.PlacementMotionRows")
                .GetMethod("Build").Invoke(null, new object[] { doc, null });
            // 113 props with a motion (LEVEL_FORMAT_V2 6.7), 42 more ride a moving parent chain
            // (the 13 attached props on static parents do not). The table carries only paths a
            // motion references: p15, p22 and p23; p00 is in the document but no motion uses it.
            Assert.AreEqual(113, Field(table, "OwnProgramRows"));
            Assert.AreEqual(42, Field(table, "RiderRows"));
            Assert.AreEqual(3, ((int[])Field(table, "PathPointCount")).Length);
            Assert.AreEqual(0, ((List<string>)Field(table, "Errors")).Count);
            int[] parent = (int[])Field(table, "ParentRow");
            for (int i = 0; i < parent.Length; i++) Assert.Less(parent[i], i, "parents first");
        }

        [Test]
        public void SkeletalAssemblerReadsTheDefOutOfAnItemName()
        {
            MethodInfo method = FindType("VRCKatamari.Editor.LevelBuild.SkeletalAnimationAssembler")
                .GetMethod("DefOfItem");
            Assert.AreEqual("small_fiddler_crab", method.Invoke(null, new object[] { "small_fiddler_crab_011" }));
            Assert.AreEqual("blue_crab", method.Invoke(null, new object[] { "blue_crab_002@size=3" }));
            Assert.AreEqual("barber_s_pole", method.Invoke(null, new object[] { "barber_s_pole_001" }));
            Assert.AreEqual("plain", method.Invoke(null, new object[] { "plain" }));
        }
    }
}
