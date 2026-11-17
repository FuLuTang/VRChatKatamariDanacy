using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the three pure v2 decisions: D-5's scale layers, the parent graph, and the growth
    /// key priority (<c>growthMm3</c> authoritative, <c>volumeMm3</c> an alias).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection, like the other importer tests. Each case parses a small real v2 document and
    /// then calls the rule with the parsed prop/def objects, so the rule sees exactly what the
    /// importer will see.
    /// </para>
    /// <para>
    /// The scale cases are the reference level's own data: four houses at <c>0.9</c> with convex
    /// proxies, one <c>0.1</c> controller parented to a cubby, and one non-uniform sparrow with
    /// no collider at all. That last one is why "no proxy to distort" is its own allowed layer
    /// instead of being bent into the box/sphere clause.
    /// </para>
    /// </remarks>
    public class PlacementV2RulesTests
    {
        private const string ReaderTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Reader";
        private const string RulesTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Rules";

        private Type readerType;
        private Type rulesType;

        [SetUp]
        public void SetUp()
        {
            readerType = FindType(ReaderTypeName);
            rulesType = FindType(RulesTypeName);
            Assert.IsNotNull(readerType, ReaderTypeName + " not loaded");
            Assert.IsNotNull(rulesType, RulesTypeName + " not loaded");
        }

        // ------------------------------------------------------------------ scale ---

        [Test]
        public void UniformScaleIsAllowedAnywhere()
        {
            // Four houses (0.9, unparented) and the controller (0.1, parented): both legal
            // (D-5 layer 2 - the original's attached 0.1 is exactly this case).
            object doc = Parse(Props(
                Prop("house", "", @"""scale"":[0.9,0.9,0.9]"),
                Prop("cubby", ""),
                Prop("rc", "", @"""scale"":[0.1,0.1,0.1]","cubby")) + Defs(
                Def("d_house", "convex"), Def("d_rc", "convex"), Def("d_cubby", "box")),
                out object result);
            Assert.IsNull(FirstError(result));

            object house = PropOf(doc, "house");
            object rc = PropOf(doc, "rc");
            Assert.AreEqual("Uniform", LayerOf(house, DefOf(doc, "d_house")));
            Assert.IsNull(ScaleError(house, DefOf(doc, "d_house")));
            Assert.AreEqual("Uniform", LayerOf(rc, DefOf(doc, "d_rc")),
                "a UNIFORM scale on an attached prop is legal (D-5 layer 2)");
            Assert.IsNull(ScaleError(rc, DefOf(doc, "d_rc")));
        }

        [Test]
        public void NonUniformScaleOnAnAttachedPropIsRefused()
        {
            object doc = Parse(Props(
                Prop("p", "", @"""scale"":[0.5,0.25,1.0]","parent_prop"),
                Prop("parent_prop", "")) + Defs(Def("d_p", "box")), out object result);
            Assert.IsNull(FirstError(result));
            object prop = PropOf(doc, "p");
            Assert.AreEqual("Refused", LayerOf(prop, DefOf(doc, "d_p")));
            StringAssert.Contains("[prop_scale_attached]", ScaleError(prop, DefOf(doc, "d_p")));
        }

        [Test]
        public void NonUniformScaleNeedsStaticBehaviour()
        {
            object doc = Parse(Props(
                Prop("p", @"""behaviour"":""motion""", @"""scale"":[0.5,0.25,1.0]")) +
                Defs(Def("d_p", "box")), out object result);
            Assert.IsNull(FirstError(result));
            object prop = PropOf(doc, "p");
            StringAssert.Contains("[prop_scale_nonuniform]", ScaleError(prop, DefOf(doc, "d_p")));
        }

        [Test]
        public void NonUniformScaleOnlyFitsBoxAndSphereProxies()
        {
            string scale = @"""scale"":[0.5,0.25,1.0]";
            object doc = Parse(Props(Prop("boxed", "", scale), Prop("capped", "", scale),
                    Prop("hulled", "", scale), Prop("bare", "", scale)) +
                Defs(Def("d_boxed", "box"), Def("d_capped", "capsule"), Def("d_hulled", "convex"),
                    Def("d_bare", null)), out object result);
            Assert.IsNull(FirstError(result));

            Assert.AreEqual("NonUniformAllowed", LayerOf(PropOf(doc, "boxed"), DefOf(doc, "d_boxed")));
            Assert.IsNull(ScaleError(PropOf(doc, "boxed"), DefOf(doc, "d_boxed")));

            // A capsule or a convex hull is no longer the same shape per axis.
            StringAssert.Contains("[prop_scale_nonuniform]",
                ScaleError(PropOf(doc, "capped"), DefOf(doc, "d_capped")));
            StringAssert.Contains("[prop_scale_nonuniform]",
                ScaleError(PropOf(doc, "hulled"), DefOf(doc, "d_hulled")));

            // No proxy at all: nothing to distort. The reference level's sparrow is this case.
            Assert.AreEqual("NonUniformNoProxy", LayerOf(PropOf(doc, "bare"), DefOf(doc, "d_bare")));
            Assert.IsNull(ScaleError(PropOf(doc, "bare"), DefOf(doc, "d_bare")));
        }

        [Test]
        public void UnusableScaleIsInvalid()
        {
            object doc = Parse(Props(Prop("zero", "", @"""scale"":[1,0,1]"),
                    Prop("negative", "", @"""scale"":[-1,1,1]")) +
                Defs(Def("d_zero", "box"), Def("d_negative", "box")), out object result);
            Assert.IsNull(FirstError(result));
            StringAssert.Contains("[prop_scale]",
                ScaleError(PropOf(doc, "zero"), DefOf(doc, "d_zero")));
            StringAssert.Contains("[prop_scale]",
                ScaleError(PropOf(doc, "negative"), DefOf(doc, "d_negative")));
        }

        [Test]
        public void AbsentScaleIsUnitAndStaysQuiet()
        {
            object doc = Parse(Props(Prop("plain", "")) + Defs(Def("d_plain", "box")),
                out object result);
            Assert.IsNull(FirstError(result));
            object prop = PropOf(doc, "plain");
            Assert.AreEqual("Unit", LayerOf(prop, DefOf(doc, "d_plain")));
            Assert.IsNull(ScaleError(prop, DefOf(doc, "d_plain")));
        }

        // ----------------------------------------------------------------- parent ---

        [Test]
        public void ParentChainResolvesAndReportsItsDepth()
        {
            object doc = Parse(Props(
                Prop("c", "", "", "b"), Prop("b", "", "", "a"), Prop("a", "")),
                out object result);
            Assert.IsNull(FirstError(result));
            object graph = Invoke(rulesType, "BuildParentGraph",
                new object[] { Field(doc, "Props") });
            Assert.IsNull(Field(graph, "Error"));
            Assert.AreEqual(2, Field(graph, "EdgeCount"));
            Assert.AreEqual(1, Field(graph, "RootCount"));
            Assert.AreEqual(2, Field(graph, "MaxDepth"));

            var parentIndex = (int[])Field(graph, "ParentIndex");
            Assert.AreEqual(1, parentIndex[0], "c's parent is b (index 1)");
            Assert.AreEqual(2, parentIndex[1], "b's parent is a (index 2)");
            Assert.AreEqual(-1, parentIndex[2], "a is a root");
        }

        [Test]
        public void MissingParentIsRefused()
        {
            object doc = Parse(Props(Prop("orphan", "", "", "nobody")), out object result);
            object graph = Invoke(rulesType, "BuildParentGraph",
                new object[] { Field(doc, "Props") });
            StringAssert.Contains("[prop_parent]", (string)Field(graph, "Error"));
        }

        [Test]
        public void SelfParentIsRefused()
        {
            object doc = Parse(Props(Prop("loop", "", "", "loop")), out object result);
            object graph = Invoke(rulesType, "BuildParentGraph",
                new object[] { Field(doc, "Props") });
            StringAssert.Contains("[prop_parent]", (string)Field(graph, "Error"));
        }

        [Test]
        public void TwoPropCycleIsRefused()
        {
            object doc = Parse(Props(Prop("x", "", "", "y"), Prop("y", "", "", "x")),
                out object result);
            object graph = Invoke(rulesType, "BuildParentGraph",
                new object[] { Field(doc, "Props") });
            Assert.IsNotNull((string)Field(graph, "Error"),
                "a cycle must be reported, not walked forever");
            StringAssert.Contains("[prop_parent]", (string)Field(graph, "Error"));
        }

        // ----------------------------------------------------------------- growth ---

        [Test]
        public void GrowthMm3IsTheAuthority()
        {
            object doc = Parse(Props(Prop("p", "", @"""growthMm3"":1234" +
                    @",""volumePenalty"":0.8")), out object result);
            Assert.IsNull(FirstError(result));
            object[] values = Growth(PropOf(doc, "p"), 0.8f);
            Assert.IsNull((string)values[0]);
            Assert.AreEqual(1234L, (long)values[1]);
            Assert.AreEqual(false, (bool)values[2], "growthMm3 alone is not an alias read");
        }

        [Test]
        public void VolumeMm3IsReadOnlyWhenGrowthIsAbsent()
        {
            object doc = Parse(Props(Prop("p", "", @"""volumeMm3"":1000" +
                    @",""volumePenalty"":0.8")), out object result);
            Assert.IsNull(FirstError(result));
            object[] values = Growth(PropOf(doc, "p"), 0.8f);
            Assert.IsNull((string)values[0]);
            Assert.AreEqual(800L, (long)values[1], "round(1000 * 0.8)");
            Assert.AreEqual(true, (bool)values[2], "the alias was used");
        }

        [Test]
        public void BothKeysMustAgreeThroughThePenalty()
        {
            object consistent = Parse(Props(Prop("p", "",
                @"""growthMm3"":800,""volumeMm3"":1000,""volumePenalty"":0.8")), out _);
            Assert.IsNull((string)Growth(PropOf(consistent, "p"), 0.8f)[0],
                "growthMm3 == round(volumeMm3 * penalty) is the valid case");

            object stale = Parse(Props(Prop("p", "",
                @"""growthMm3"":900,""volumeMm3"":1000,""volumePenalty"":0.8")), out _);
            string error = (string)Growth(PropOf(stale, "p"), 0.8f)[0];
            StringAssert.Contains("[prop_volume_stale]", error);
        }

        [Test]
        public void NoGrowthKeyMeansZeroAndNoError()
        {
            object doc = Parse(Props(Prop("p", "")), out object result);
            object[] values = Growth(PropOf(doc, "p"), 0.8f);
            Assert.IsNull((string)values[0]);
            Assert.AreEqual(0L, (long)values[1]);
        }

        [Test]
        public void EffectivePenaltyFallsBackToTheDefThenTheDefault()
        {
            object doc = Parse(Props(Prop("own", "", @"""volumePenalty"":0.5"),
                    Prop("fromDef", ""), Prop("defaulted", "")) +
                Defs(Def("d_own", "box", 0.9f), Def("d_fromDef", "box", 0.6f),
                    Def("d_defaulted", "box")), out object result);
            Assert.IsNull(FirstError(result));

            Assert.AreEqual(0.5f, PenaltyOf(PropOf(doc, "own"), DefOf(doc, "d_own")));
            Assert.AreEqual(0.6f, PenaltyOf(PropOf(doc, "fromDef"), DefOf(doc, "d_fromDef")));
            Assert.AreEqual(0.8f, PenaltyOf(PropOf(doc, "defaulted"), DefOf(doc, "d_defaulted")));
        }

        // ------------------------------------------------------------ player spawn ---

        [Test]
        public void RespawnHeightIsTheKillPlaneMinusOneMetre()
        {
            Assert.AreEqual(1f, (float)Field(rulesType, "RespawnBelowKillPlaneM"));
            Assert.AreEqual(-5f, RespawnHeight(-4f), 1e-6f, "Cancer: kill -4 -> respawn -5");
            Assert.AreEqual(-7f, RespawnHeight(-6f), 1e-6f, "the format's default kill plane");
            Assert.AreEqual(-1f, RespawnHeight(0f), 1e-6f);
        }

        [Test]
        public void RespawnHeightSitsBelowTheKillPlaneAndEveryStandableSurface()
        {
            // The validator guarantees killPlane < min(groundY, lowest terrain point); Cancer's
            // numbers: groundY -3, lowest terrain -1.5918, kill -4.
            const float lowestTerrainY = -1.5918f;
            const float groundY = -3f;
            const float killY = -4f;
            float respawn = RespawnHeight(killY);
            Assert.Less(respawn, killY, "the ball is reset before the player is");
            Assert.Less(respawn, lowestTerrainY, "a player on the lowest terrain is never respawned");
            Assert.Less(respawn, groundY, "nor one standing on the fallback floor");
        }

        [Test]
        public void FacingZeroIsIdentityAndHeadingIsPlusZ()
        {
            float[] q = Facing(0f);
            Assert.AreEqual(0f, q[0], 1e-6f);
            Assert.AreEqual(0f, q[1], 1e-6f);
            Assert.AreEqual(0f, q[2], 1e-6f);
            Assert.AreEqual(1f, q[3], 1e-6f);
            AssertForward(q, 0f, 1f);
        }

        [Test]
        public void FacingIsAYawTowardsPlusXAndAUnitQuaternion()
        {
            // heading 0 = +Z, 90 = +X, 180 = -Z, -90 = -X; 15 is Cancer's player start.
            float[] degrees = { 15f, 90f, 180f, -90f, 30f, -30f, 360f };
            for (int i = 0; i < degrees.Length; i++)
            {
                float[] q = Facing(degrees[i]);
                Assert.AreEqual(1f, q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3], 1e-5f,
                    "unit at " + degrees[i]);
                Assert.AreEqual(0f, q[0], 1e-6f, "pure yaw (x) at " + degrees[i]);
                Assert.AreEqual(0f, q[2], 1e-6f, "pure yaw (z) at " + degrees[i]);
                double rad = degrees[i] * Math.PI / 180.0;
                AssertForward(q, (float)Math.Sin(rad), (float)Math.Cos(rad));
            }
        }

        [Test]
        public void FacingTowardsTheBallRecoversTheAuthoredBearing()
        {
            // Cancer: feet (-5.647, 0.0852), ball (-5.0, 2.5) -> bearing atan2(dx, dz) = 15 deg,
            // and the quaternion's forward points from the feet at the ball.
            double dx = -5.0 - -5.647;
            double dz = 2.5 - 0.0852;
            float bearing = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            Assert.AreEqual(15f, bearing, 0.05f);
            float[] q = Facing(15f);
            float forwardX = 2f * q[3] * q[1];
            float forwardZ = 1f - 2f * q[1] * q[1];
            double length = Math.Sqrt(dx * dx + dz * dz);
            Assert.AreEqual(dx / length, forwardX, 2e-3);
            Assert.AreEqual(dz / length, forwardZ, 2e-3);
        }

        [Test]
        public void FacingDegreesMustBeFiniteAndWithinOneTurn()
        {
            Assert.IsTrue((bool)Invoke(rulesType, "IsFacingDegValid", new object[] { 0f }));
            Assert.IsTrue((bool)Invoke(rulesType, "IsFacingDegValid", new object[] { -360f }));
            Assert.IsTrue((bool)Invoke(rulesType, "IsFacingDegValid", new object[] { 360f }));
            Assert.IsFalse((bool)Invoke(rulesType, "IsFacingDegValid", new object[] { 360.5f }));
            Assert.IsFalse((bool)Invoke(rulesType, "IsFacingDegValid",
                new object[] { float.NaN }));
            Assert.IsFalse((bool)Invoke(rulesType, "IsFacingDegValid",
                new object[] { float.PositiveInfinity }));
        }

        [Test]
        public void TerrainVisualMeshPathFollowsTheLevelKind()
        {
            Assert.AreEqual("Assets/_RefUnlicensed/Meshes/RefCancerTerrainVisual.asset",
                MeshPath("_ref_cancer", "cancer_ground", true));
            Assert.AreEqual("Assets/VRC_Katamari/Generated/Levels/story_01_bench_size/bench_ground.asset",
                MeshPath("story_01_bench_size", "bench_ground", false));
            Assert.AreEqual("Assets/VRC_Katamari/Generated/Levels/C21/tiny_pad.asset",
                MeshPath("physics_test_field/C21", "tiny_pad", false),
                "a nested level id uses its leaf, like the managed root");
            Assert.AreEqual("Assets/VRC_Katamari/Generated/Levels/odd/a_b_c.asset",
                MeshPath("odd", "a b/c", false), "no path separators or spaces leak into the file name");
        }

        [Test]
        public void TheSplitSurfaceAssetsFollowTheLevelKind()
        {
            // The contact mesh keeps its asset (the pre-split path is where the importer moves it
            // from) and the drawn surface gets its own names, all under _RefUnlicensed for a
            // reference level.
            Assert.AreEqual("Assets/_RefUnlicensed/Meshes/RefCancerTerrainCollision.asset",
                Text("TerrainCollisionMeshPath", "_ref_cancer", "cancer_ground", true));
            Assert.AreEqual("Assets/_RefUnlicensed/Meshes/RefCancerVisual_area2_sector0_model4.asset",
                Text("TerrainVisualPartMeshPath", "_ref_cancer", "area2_sector0_model4", true));
            Assert.AreEqual("Assets/_RefUnlicensed/Textures/RefCancer/11_area2_2f75_33ea.png",
                Text("TerrainVisualTexturePath", "_ref_cancer",
                    "terrain/textures/11_area2_2f75_33ea.png", true));
            Assert.AreEqual("Assets/_RefUnlicensed/Materials/RefCancerVisual_t012.mat",
                Text("TerrainVisualMaterialPath", "_ref_cancer", "t012", true));
            Assert.AreEqual("Assets/VRC_Katamari/Generated/Levels/C21/tiny_pad_collision.asset",
                Text("TerrainCollisionMeshPath", "physics_test_field/C21", "tiny_pad", false));
            Assert.AreEqual("Assets/VRC_Katamari/Generated/Levels/C21/visual_a_b.asset",
                Text("TerrainVisualPartMeshPath", "physics_test_field/C21", "a b", false));
            Assert.AreEqual("Assets/VRC_Katamari/Generated/Levels/C21/Textures/x.png",
                Text("TerrainVisualTexturePath", "physics_test_field/C21", "terrain/t/x.png", false));
            Assert.AreEqual("Assets/_RefUnlicensed/Meshes/RefCancerTerrainVisual.asset",
                MeshPath("_ref_cancer", "cancer_ground", true),
                "the pre-split name stays what a level WITHOUT terrain.visual uses");
        }

        [Test]
        public void VisualBlendMapsToTheLitLegacyShaders()
        {
            Assert.AreEqual("Legacy Shaders/Diffuse", Text("VisualShaderName", "opaque"));
            Assert.AreEqual("Legacy Shaders/Transparent/Cutout/Diffuse",
                Text("VisualShaderName", "cutout"));
            Assert.AreEqual("Legacy Shaders/Transparent/Diffuse", Text("VisualShaderName", "alpha"));
            Assert.IsNull(Text("VisualShaderName", "additive"), "an undefined blend has no shader");
            Assert.IsNull(Text("VisualShaderName", (object)null));
        }

        [Test]
        public void ThePartMeshNameCarriesTheSidecarHash()
        {
            string name = Text("VisualMeshName", "_ref_cancer", "area2_sector1_model0",
                "988C7ADC92047ED911339E46D60BD84512E9667A92595192456597D9F4A27FC1");
            Assert.AreEqual("_ref_cancer_area2_sector1_model0@988c7adc", name,
                "other sidecar bytes -> another name -> the importer rebuilds the asset in place");
            Assert.AreEqual("a_p@", Text("VisualMeshName", "a", "p", ""));
        }

        private string Text(string method, params object[] args)
        {
            return (string)Invoke(rulesType, method, args);
        }

        [Test]
        public void BallAlignmentIsNeededOnlyBeyondTheTolerance()
        {
            float[] target = { -5f, -0.999f, 2.5f };
            Assert.IsFalse(Aligns(new[] { -5f, -0.999f, 2.5f }, target), "identical: write nothing");
            Assert.IsFalse(Aligns(new[] { -5.0004f, -0.999f, 2.4996f }, target),
                "within 1 mm on every axis: the second import stays +0");
            Assert.IsTrue(Aligns(new[] { -5.002f, -0.999f, 2.5f }, target), "2 mm off in x");
            Assert.IsTrue(Aligns(new[] { 0f, 0.15f, 0f }, target), "the motor's default start");
            Assert.IsTrue(Aligns(new[] { -5f, -0.999f, float.NaN }, target), "a NaN is repaired");
            Assert.IsTrue(Aligns(null, target), "no current value");
            Assert.IsTrue(Aligns(new[] { 1f, 2f }, target), "wrong length");
            Assert.IsFalse(Aligns(new[] { 1f, 2f, 3f }, null), "no target: nothing to align to");
            Assert.IsFalse(Aligns(new[] { 1f, 2f, 3f }, new[] { 1f, 2f }), "malformed target");
        }

        private static bool Aligns(float[] current, float[] target)
        {
            return (bool)Invoke(FindType("VRCKatamari.Editor.LevelBuild.PlacementV2Rules"),
                "NeedsAlignment", new object[] { current, target, 0.001f });
        }

        private float RespawnHeight(float killPlaneY)
        {
            return (float)Invoke(rulesType, "RespawnHeightY", new object[] { killPlaneY });
        }

        private float[] Facing(float degrees)
        {
            return (float[])Invoke(rulesType, "FacingQuatXyzw", new object[] { degrees });
        }

        private string MeshPath(string levelId, string terrainId, bool isReference)
        {
            return (string)Invoke(rulesType, "TerrainVisualMeshPath",
                new object[] { levelId, terrainId, isReference });
        }

        /// <summary>The quaternion's local +Z axis (a pure yaw has no Y component).</summary>
        private static void AssertForward(float[] q, float expectedX, float expectedZ)
        {
            Assert.AreEqual(expectedX, 2f * q[3] * q[1], 1e-5f, "forward.x");
            Assert.AreEqual(expectedZ, 1f - 2f * q[1] * q[1], 1e-5f, "forward.z");
        }

        // --------------------------------------------------------------- fixtures ---

        /// <summary>
        /// One <c>props[]</c> entry as JSON. Every fragment after <paramref name="itemId"/> is
        /// optional and appended in order, so a test can write only the keys it is about; the
        /// four-argument form ends with the parent id.
        /// </summary>
        private static string Prop(string itemId, string extra = null, string extra2 = null,
            string parentId = null)
        {
            var body = new System.Text.StringBuilder();
            body.Append(@"{""itemId"":""").Append(itemId).Append(@""",""def"":""d_")
                .Append(itemId).Append(@""",""position"":[0,0,0],""rotationQuat"":[0,0,0,1],")
                .Append(@"""aabb"":[1,1,1]");
            if (!string.IsNullOrEmpty(extra)) body.Append(',').Append(extra);
            if (!string.IsNullOrEmpty(extra2)) body.Append(',').Append(extra2);
            if (parentId != null)
            {
                body.Append(@",""parent"":""").Append(parentId).Append('"');
            }

            body.Append('}');
            return body.ToString();
        }

        private static string Def(string id, string colliderType, float defPenalty = 0f)
        {
            var body = new System.Text.StringBuilder();
            body.Append(@"{""id"":""").Append(id).Append(@""",""kind"":""k""");
            if (defPenalty > 0f)
            {
                body.Append(@",""volumePenalty"":").Append(
                    defPenalty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }

            if (colliderType != null)
            {
                body.Append(@",""collider"":{""type"":""").Append(colliderType)
                    .Append(@""",""center"":[0,0,0]");
                if (string.Equals(colliderType, "box", StringComparison.Ordinal))
                {
                    body.Append(@",""size"":[1,1,1]");
                }
                else if (string.Equals(colliderType, "capsule", StringComparison.Ordinal))
                {
                    body.Append(@",""radius"":0.1,""halfHeight"":0.2,""axis"":""y""");
                }
                else if (string.Equals(colliderType, "convex", StringComparison.Ordinal))
                {
                    body.Append(@",""hull"":[0,0,0,1,0,0,0,1,0,0,0,1]");
                }

                body.Append('}');
            }

            body.Append('}');
            return body.ToString();
        }

        private static string Props(params string[] props)
        {
            return @"""props"":[" + string.Join(",", props) + "]";
        }

        private static string Defs(params string[] defs)
        {
            return @",""defs"":[" + string.Join(",", defs) + "]";
        }

        private object Parse(string blocks, out object result)
        {
            string json = @"{""schema"":""katamari.placement/2""," +
                          @"""meta"":{""id"":""probe"",""scene"":""S"",""units"":""m""," +
                          @"""convention"":""unity-y-up-left-handed-quat-xyzw""," +
                          @"""ball"":{""startDiameterM"":0.3,""maxDiameterRef"":""global""," +
                          @"""start"":[0,0,0],""spawnSource"":""authored""}}," + blocks + "}";
            AssertFixtureIsJson(json);
            result = Invoke(readerType, "Read", new object[] { json });
            object doc = Field(result, "Document");
            Assert.IsNotNull(doc, "the reader returned no document");
            return doc;
        }

        private object Parse(string blocks)
        {
            return Parse(blocks, out object ignored);
        }

        // The two-argument helpers below exist so a single-prop document does not need the
        // `out` dance at every call site; they parse through the same entry point.

        private object[] Growth(object prop, float penalty)
        {
            MethodInfo method = FindMethod(rulesType, "GrowthError", 4);
            Assert.IsNotNull(method, "GrowthError(prop, penalty, out long, out bool) not found");
            var args = new object[] { prop, penalty, 0L, false };
            object error = method.Invoke(null, args);
            return new[] { error, args[2], args[3] };
        }

        private string ScaleError(object prop, object def)
        {
            return (string)Invoke(rulesType, "ScaleError", new[] { prop, def });
        }

        private string LayerOf(object prop, object def)
        {
            object layer = Invoke(rulesType, "ScaleLayer", new[] { prop, def });
            return layer.ToString();
        }

        private float PenaltyOf(object prop, object def)
        {
            return (float)Invoke(rulesType, "EffectiveVolumePenalty", new[] { prop, def });
        }

        private static object PropOf(object doc, string itemId)
        {
            var props = (Array)Field(doc, "Props");
            for (int i = 0; i < props.Length; i++)
            {
                object prop = props.GetValue(i);
                if (prop != null && string.Equals((string)Field(prop, "ItemId"), itemId,
                    StringComparison.Ordinal))
                {
                    return prop;
                }
            }

            Assert.Fail("no prop '" + itemId + "' in the document");
            return null;
        }

        private static object DefOf(object doc, string defId)
        {
            var defs = (Array)Field(doc, "Defs");
            for (int i = 0; i < defs.Length; i++)
            {
                object def = defs.GetValue(i);
                if (def != null && string.Equals((string)Field(def, "Id"), defId,
                    StringComparison.Ordinal))
                {
                    return def;
                }
            }

            Assert.Fail("no def '" + defId + "' in the document");
            return null;
        }

        private static string FirstError(object result)
        {
            foreach (object error in (IEnumerable)Field(result, "Errors")) return (string)error;
            return null;
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

        private static object Field(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Static);
            Assert.IsNotNull(field, type.Name + " has no static field '" + name + "'");
            return field.GetValue(null);
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
            Assert.IsNotNull(method, type.Name + " has no method '" + name + "'/"+args.Length);
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
