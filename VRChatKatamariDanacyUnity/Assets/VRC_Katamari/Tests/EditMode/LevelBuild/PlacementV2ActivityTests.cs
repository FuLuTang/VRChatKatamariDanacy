using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the <c>activityArea</c> layer: one plane segment per closed-polygon edge, the normal
    /// pointing INTO the play area, and the refusals (a concave polygon, a zero-height wall, a
    /// duplicated point).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plane channel is <see cref="VRCKatamari.Story.LevelArenaHost"/>'s: a wall Transform's
    /// local +Z is baked as the plane normal and free space is on the positive side, so "inward"
    /// is not a convention this test invents - it is the arena's contract, and a normal pointing
    /// the wrong way blocks the whole field instead of one edge.
    /// </para>
    /// <para>
    /// A plane cannot represent a concave corner (two half-planes cross outside the polygon), so
    /// a non-convex closed polygon is refused rather than silently degraded (owner ruling F).
    /// The reference level's rectangle is convex and this test uses its real coordinates.
    /// </para>
    /// </remarks>
    public class PlacementV2ActivityTests
    {
        private const string ReaderTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Reader";
        private const string RulesTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Rules";
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";

        // The reference level's authored rectangle (placement.v2.json activityArea.walls[0]).
        private const string ReferenceRectangle =
            "[[-11.8073,-7.5354],[8.5732,-7.5354],[8.5732,8.9912],[-11.8073,8.9912]]";

        private Type readerType;
        private Type rulesType;
        private Type importerType;
        private Type reportType;

        [SetUp]
        public void SetUp()
        {
            readerType = FindType(ReaderTypeName);
            rulesType = FindType(RulesTypeName);
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            Assert.IsNotNull(readerType, ReaderTypeName + " not loaded");
            Assert.IsNotNull(rulesType, RulesTypeName + " not loaded");
            Assert.IsNotNull(importerType, ImporterTypeName + " not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " not loaded");
        }

        // ---------------------------------------------------------- closed polygon ---

        [Test]
        public void ClosedRectangleBecomesOnePlanePerEdgePointingInward()
        {
            object wall = WallOf(Wall("rect", ReferenceRectangle, true, 3.0f, 0.2f));
            object[] segments = SegmentsOf(wall);

            Assert.AreEqual(4, segments.Length, "a closed 4-point wall is 4 plane segments");
            float[] centroid = new[] { -1.61705f, 0.7279f };
            float expected = 20.3805f + 16.5266f + 20.3805f + 16.5266f;
            float total = 0f;
            for (int i = 0; i < segments.Length; i++)
            {
                object segment = segments[i];
                float[] normal = (float[])Field(segment, "InwardNormal");
                float[] mid = (float[])Field(segment, "Mid");
                float length = (float)Field(segment, "LengthM");
                total += length;

                Assert.AreEqual(1.0f, Math.Abs(normal[0]) + Math.Abs(normal[1]), 1e-3f,
                    "the normal must be a unit XZ vector");
                Assert.Greater(normal[0] * (centroid[0] - mid[0]) + normal[1] * (centroid[1] - mid[1]),
                    0f, "segment " + i + " points away from the play area");
                Assert.AreEqual("rect_seg0" + i, (string)Field(segment, "Id"),
                    "ids must be stable for idempotence");
            }

            Assert.AreEqual(expected, total, 0.01f, "the wall's perimeter");
        }

        [Test]
        public void ReversedWindingStillPointsInward()
        {
            // The same rectangle written clockwise: a correct implementation must not depend on
            // the author's winding order.
            object wall = WallOf(Wall("cw",
                "[[-11.8073,8.9912],[8.5732,8.9912],[8.5732,-7.5354],[-11.8073,-7.5354]]", true,
                3.0f, 0.2f));
            object[] segments = SegmentsOf(wall);
            Assert.AreEqual(4, segments.Length);
            float[] centroid = new[] { -1.61705f, 0.7279f };
            for (int i = 0; i < segments.Length; i++)
            {
                float[] normal = (float[])Field(segments[i], "InwardNormal");
                float[] mid = (float[])Field(segments[i], "Mid");
                Assert.Greater(normal[0] * (centroid[0] - mid[0]) + normal[1] * (centroid[1] - mid[1]),
                    0f, "cw segment " + i + " points away from the play area");
            }
        }

        [Test]
        public void ClosedTriangleBecomesThreeSegments()
        {
            object wall = WallOf(Wall("tri", "[[0,0],[4,0],[0,4]]", true, 2.0f, 0.2f));
            Assert.AreEqual(3, SegmentsOf(wall).Length);
        }

        [Test]
        public void OpenPolylineUsesTheLeftOfTravelConvention()
        {
            object wall = WallOf(Wall("open", "[[0,0],[0,10]]", false, 3.0f, 0.2f));
            object[] segments = SegmentsOf(wall);
            Assert.AreEqual(1, segments.Length, "an open 2-point wall is one segment");
            float[] normal = (float[])Field(segments[0], "InwardNormal");
            // Travel is +z, so "the play area on the left" is -x.
            Assert.AreEqual(-1f, normal[0], 1e-4f);
            Assert.AreEqual(0f, normal[1], 1e-4f);
        }

        // -------------------------------------------------------------- refusals ---

        [Test]
        public void ConcaveClosedPolygonIsRefused()
        {
            // An L: the plane channel would block the notch's outside corner.
            object wall = WallOf(Wall("concave",
                "[[0,0],[4,0],[4,1],[1,1],[1,4],[0,4]]", true, 3.0f, 0.2f));
            string error = (string)Invoke(rulesType, "WallError", new[] { wall });
            Assert.IsNotNull(error, "a concave closed polygon must be refused");
            StringAssert.Contains("[activity_wall]", error);
            StringAssert.Contains("not convex", error);

            object[] segments;
            string buildError;
            bool built = BuildSegments(wall, out segments, out buildError);
            Assert.IsFalse(built, "a refused wall must not produce segments");
            StringAssert.Contains("[activity_wall]", buildError);
        }

        [Test]
        public void ZeroHeightWallIsRefused()
        {
            object wall = WallOf(Wall("flat", "[[0,0],[4,0],[4,4]]", true, 0f, 0.2f));
            StringAssert.Contains("[activity_wall]",
                (string)Invoke(rulesType, "WallError", new[] { wall }));
        }

        [Test]
        public void DuplicatedPointIsRefused()
        {
            object wall = WallOf(Wall("dup", "[[0,0],[0,0],[4,0],[4,4]]", true, 3.0f, 0.2f));
            StringAssert.Contains("[activity_wall]",
                (string)Invoke(rulesType, "WallError", new[] { wall }));
        }

        [Test]
        public void SinglePointIsRefused()
        {
            object wall = WallOf(Wall("dot", "[[0,0]]", false, 3.0f, 0.2f));
            StringAssert.Contains("[activity_wall]",
                (string)Invoke(rulesType, "WallError", new[] { wall }));
        }

        // ------------------------------------------------------------- kill plane ---

        [Test]
        public void KillPlaneIsReadWithItsRespawnMode()
        {
            object doc = Parse(@"""activityArea"":{""walls"":[],""killPlane"":{""y"":-4.0," +
                               @"""respawn"":""ballStart"",""zoneId"":0},""softMarginM"":0.0}");
            object activity = Field(doc, "Activity");
            object kill = Field(activity, "KillPlane");
            Assert.AreEqual(-4.0f, Field(kill, "Y"));
            Assert.AreEqual("ballStart", Field(kill, "Respawn"));
            Assert.IsNull(Field(kill, "RespawnPoint"));
            Assert.AreEqual(true, Field(kill, "HasZoneId"));
            Assert.AreEqual(0, Field(kill, "ZoneId"));
        }

        [Test]
        public void ExplicitRespawnPointIsRead()
        {
            object doc = Parse(@"""activityArea"":{""walls"":[],""killPlane"":{""y"":-2.5," +
                               @"""respawn"":[1,2,3]}}");
            object kill = Field(Field(doc, "Activity"), "KillPlane");
            float[] point = (float[])Field(kill, "RespawnPoint");
            Assert.IsNotNull(point);
            Assert.AreEqual(2f, point[1]);
            Assert.IsNull(Field(kill, "Respawn"));
        }

        [Test]
        public void SegmentsOfEveryWallAreFlattenedInDocumentOrder()
        {
            // Wall "a" must be OPEN: a closed 2-point polygon is degenerate and the wall rule
            // refuses it (fewer than 3 points cannot be convex), so it would contribute nothing
            // and the total would be 3 instead of 5 - which is exactly what a red run showed.
            object doc = Parse(@"""activityArea"":{""walls"":[" +
                               @"{""id"":""a"",""points"":[[0,0],[4,0],[4,4]],""closed"":false," +
                               @"""heightM"":3.0}," +
                               @"{""id"":""b"",""points"":[[0,10],[4,10],[4,14]],""closed"":true," +
                               @"""heightM"":2.0}]}");
            object[] all = (object[])Invoke(rulesType, "AllWallSegments", new object[] { doc, null });
            Assert.AreEqual(5, all.Length, "2 open edges from wall a + 3 closed edges from wall b");
            // Document order, wall by wall, edge by edge: the arena bakes this order.
            string[] expectedIds = { "a_seg00", "a_seg01", "b_seg00", "b_seg01", "b_seg02" };
            for (int i = 0; i < expectedIds.Length && i < all.Length; i++)
            {
                Assert.AreEqual(expectedIds[i], (string)Field(all[i], "Id"));
            }
        }

        // ------------------------------------------------------------ idempotence ---

        [Test]
        public void SecondRunOfTheWallLayerWritesNothing()
        {
            // The operator's gate for the whole importer is "+0 ~0 -0" on the second run; the
            // wall layer is the v2-only half of that, and it is the one the design calls out by
            // name (12.4). The objects carry no Renderer and no Collider, so they also cannot
            // leak into the pickup rows.
            object doc = Parse(@"""activityArea"":{""walls"":[" +
                               Wall("rect", ReferenceRectangle, true, 3.0f, 0.2f) + "]}");
            var root = new GameObject("PlacementV2ActivityTests");
            root.hideFlags = HideFlags.DontSave;
            try
            {
                object first = Activator.CreateInstance(reportType);
                object created = Invoke(importerType, "ApplyActivityWalls",
                    new[] { doc, root.transform, first });
                Assert.AreEqual(true, created);
                Assert.AreEqual(4, Field(first, "V2WallsCreated"), "four edges, four markers");
                Assert.AreEqual(0, Field(first, "V2WallsUpdated"));
                Assert.AreEqual(0, Field(first, "V2WallsDeleted"));
                Assert.IsNull(FirstError(first), JoinErrors(first));

                object second = Activator.CreateInstance(reportType);
                object again = Invoke(importerType, "ApplyActivityWalls",
                    new[] { doc, root.transform, second });
                Assert.AreEqual(true, again);
                Assert.AreEqual(0, Field(second, "V2WallsCreated"), "second run must create nothing");
                Assert.AreEqual(0, Field(second, "V2WallsUpdated"), "second run must move nothing");
                Assert.AreEqual(0, Field(second, "V2WallsDeleted"), "second run must delete nothing");
                Assert.AreEqual(4, Field(second, "V2WallsSkipped"));

                Transform walls = root.transform.Find("ActivityWalls");
                Assert.IsNotNull(walls, "the reserved walls container");
                Assert.AreEqual(4, walls.childCount, "no duplicated segment objects");
                for (int i = 0; i < walls.childCount; i++)
                {
                    Transform marker = walls.GetChild(i);
                    Assert.IsNull(marker.GetComponent<Renderer>(),
                        "a wall is a rule: no Renderer");
                    Assert.IsNull(marker.GetComponent<Collider>(),
                        "a wall is a rule: no Collider (the arena bakes the plane)");
                    Assert.AreEqual(Vector3.one, marker.localScale);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // --------------------------------------------------------------- fixtures ---

        private static string Wall(string id, string points, bool closed, float heightM,
            float thicknessM)
        {
            return @"{""id"":""" + id + @""",""points"":" + points +
                   @",""closed"":" + (closed ? "true" : "false") +
                   @",""heightM"":" + heightM.ToString("0.###",
                       System.Globalization.CultureInfo.InvariantCulture) +
                   @",""thicknessM"":" + thicknessM.ToString("0.###",
                       System.Globalization.CultureInfo.InvariantCulture) +
                   @",""kind"":""invisible""}";
        }

        private object WallOf(string wallJson)
        {
            object doc = Parse(@"""activityArea"":{""walls"":[" + wallJson + "]}");
            object activity = Field(doc, "Activity");
            var walls = (Array)Field(activity, "Walls");
            Assert.AreEqual(1, walls.Length);
            return walls.GetValue(0);
        }

        private object[] SegmentsOf(object wall)
        {
            object[] segments;
            string error;
            Assert.IsTrue(BuildSegments(wall, out segments, out error), "build refused: " + error);
            return segments;
        }

        private bool BuildSegments(object wall, out object[] segments, out string error)
        {
            MethodInfo method = null;
            MethodInfo[] methods = rulesType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                        BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (string.Equals(methods[i].Name, "TryBuildWallSegments", StringComparison.Ordinal))
                {
                    method = methods[i];
                    break;
                }
            }

            Assert.IsNotNull(method, "TryBuildWallSegments not found");
            var args = new object[] { wall, null, null };
            bool built = (bool)method.Invoke(null, args);
            segments = null;
            if (args[1] != null)
            {
                var array = (Array)args[1];
                segments = new object[array.Length];
                for (int i = 0; i < array.Length; i++) segments[i] = array.GetValue(i);
            }

            error = (string)args[2];
            return built;
        }

        private object Parse(string blocks)
        {
            string json = @"{""schema"":""katamari.placement/2""," +
                          @"""meta"":{""id"":""probe"",""scene"":""S"",""units"":""m""," +
                          @"""convention"":""unity-y-up-left-handed-quat-xyzw""," +
                          @"""ball"":{""startDiameterM"":0.3,""maxDiameterRef"":""global""," +
                          @"""start"":[0,0,0],""spawnSource"":""authored""}}," +
                          @"""props"":[]," + blocks + "}";
            AssertFixtureIsJson(json);
            object result = Invoke(readerType, "Read", new object[] { json });
            Assert.IsNull(FirstError(result), "the fixture must parse clean");
            return Field(result, "Document");
        }

        private static string FirstError(object result)
        {
            foreach (object error in (IEnumerable)Field(result, "Errors")) return (string)error;
            return null;
        }

        private static string JoinErrors(object report)
        {
            var text = new System.Text.StringBuilder();
            foreach (object error in (IEnumerable)Field(report, "Errors"))
            {
                if (text.Length > 0) text.Append(" | ");
                text.Append(error);
            }

            return text.ToString();
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
