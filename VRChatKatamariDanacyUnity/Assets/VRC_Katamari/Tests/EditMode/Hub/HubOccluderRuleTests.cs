using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the hub's static-occlusion rule: closed versus open meshes, the
    /// material/opacity test, the occluder size and triangle-area limits, the background-shell
    /// limit, which hierarchy paths count as fixed structure geometry, the static-batching mark
    /// (terrain visual only, ORed onto the other flags), and the resulting occlusion mark.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other editor-code tests: <c>OccluderRule</c> lives in
    /// <c>Assets/VRC_Katamari/Editor/Hub/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>. The pure methods take flat arrays, so the test needs no
    /// engine object; expected numbers are hand-computed.
    /// </remarks>
    public class HubOccluderRuleTests
    {
        private const string RuleTypeName = "VRCKatamari.Editor.Hub.OccluderRule";

        private Type ruleType;

        [SetUp]
        public void SetUp()
        {
            ruleType = FindType(RuleTypeName);
            Assert.IsNotNull(ruleType, RuleTypeName + " not loaded");
        }

        // ---- closed versus open meshes ----

        [Test]
        public void ABoxIsAClosedMesh()
        {
            Assert.IsTrue(IsClosed(Cube(1f, 1f, 1f), CubeTriangles));
        }

        [Test]
        public void AScaledAndRotatedLocalBoxIsStillClosed()
        {
            // Local positions already carry the scale; a box is closed whatever its size.
            Assert.IsTrue(IsClosed(Cube(4f, 0.64f, 6.861f), CubeTriangles));
            Assert.IsTrue(IsClosed(Cube(13.4f, 0.04f, 13.4f), CubeTriangles));
        }

        [Test]
        public void AFaceSplitCubeIsClosedAfterTheSeamVerticesAreWelded()
        {
            // Unity's built-in primitives duplicate every face's vertices for normals and UVs;
            // the topology is only closed once equal positions are welded.
            Assert.IsTrue(IsClosed(FaceSplitCube(1f), FaceSplitCubeTriangles));
        }

        [Test]
        public void ASingleQuadIsOpen()
        {
            var quad = new float[]
            {
                0f, 0f, 0f,
                4f, 0f, 0f,
                4f, 0f, 4f,
                0f, 0f, 4f,
            };
            Assert.IsFalse(IsClosed(quad, new[] { 0, 1, 2, 0, 2, 3 }));
        }

        [Test]
        public void ABoxWithOneFaceMissingIsOpen()
        {
            var triangles = new int[CubeTriangles.Length - 6];
            Array.Copy(CubeTriangles, triangles, triangles.Length);
            Assert.IsFalse(IsClosed(Cube(1f, 1f, 1f), triangles));
        }

        [Test]
        public void TwoTrianglesOnOneEdgeAreOpen()
        {
            var vertices = new float[]
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f,
                0f, -1f, 0f,
            };
            Assert.IsFalse(IsClosed(vertices, new[] { 0, 1, 2, 1, 0, 3 }));
        }

        [Test]
        public void AnEmptyOrMalformedMeshIsNotClosed()
        {
            Assert.IsFalse(IsClosed(new float[0], new int[0]));
            Assert.IsFalse(IsClosed(null, CubeTriangles));
            Assert.IsFalse(IsClosed(Cube(1f, 1f, 1f), null));
            Assert.IsFalse(IsClosed(new float[] { 0f, 0f }, new[] { 0, 1, 2 }));
        }

        // ---- triangle area ----

        [Test]
        public void TheTriangleAreaIsHalfTheCrossProduct()
        {
            var vertices = new float[] { 0f, 0f, 0f, 2f, 0f, 0f, 0f, 2f, 0f };

            Assert.AreEqual(2f, (float)Invoke("MaxTriangleArea",
                new object[] { vertices, new[] { 0, 1, 2 } }), 1e-5f);
        }

        [Test]
        public void TheLargestTriangleWins()
        {
            // Two triangles in one mesh: area 0.5 and area 8 (a 4 x 4 right triangle).
            var vertices = new float[]
            {
                0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,
                0f, 0f, 5f, 4f, 0f, 5f, 0f, 4f, 5f,
            };

            Assert.AreEqual(8f, (float)Invoke("MaxTriangleArea",
                new object[] { vertices, new[] { 0, 1, 2, 3, 4, 5 } }), 1e-5f);
        }

        [Test]
        public void AMalformedMeshHasNoTriangleArea()
        {
            Assert.Less((float)Invoke("MaxTriangleArea",
                new object[] { null, new[] { 0, 1, 2 } }), 0f);
            Assert.Less((float)Invoke("MaxTriangleArea",
                new object[] { new float[] { 0f, 0f, 0f }, new[] { 0, 1, 2 } }), 0f);
            Assert.Less((float)Invoke("MaxTriangleArea",
                new object[] { new float[9], new[] { 0, 1, 9 } }), 0f);
        }

        // ---- size limits ----

        [Test]
        public void AnOccluderNeedsTwoSpansOfAtLeastOneMetre()
        {
            Assert.IsTrue(IsLargeEnough(new[] { 4f, 0.64f, 6.861f }));
            Assert.IsTrue(IsLargeEnough(new[] { 1f, 1f, 0.5f }));
            Assert.IsFalse(IsLargeEnough(new[] { 0.35f, 1.2f, 0.12f }));
            Assert.IsFalse(IsLargeEnough(new[] { 0.63f, 0.63f, 4f }));
            Assert.IsFalse(IsLargeEnough(new[] { 9.5f, 0.5f, 0.5f }));
            Assert.IsFalse(IsLargeEnough(null));
        }

        [Test]
        public void AnythingOverOneHundredAndFiftyMetresIsABackgroundShell()
        {
            // The reference island's area2_sector1_model0 is 242 m; the widest real island
            // mesh (area2_sector0_model0) is 105.9 m.
            Assert.IsTrue(IsBackgroundShell(new[] { 242.57f, 121.285f, 242.57f }));
            Assert.IsFalse(IsBackgroundShell(new[] { 105.927f, 10.988f, 104.262f }));
            Assert.IsFalse(IsBackgroundShell(new[] { 80f, 0.5f, 80f }));
            Assert.IsFalse(IsBackgroundShell(new[] { 150f, 1f, 1f }));
        }

        // ---- materials ----

        [Test]
        public void OpaqueMeansEverySlotBelowTheAlphaTestQueue()
        {
            Assert.IsTrue(IsOpaque(new[] { 2000 }, new[] { true }, new[] { "Legacy Shaders/Diffuse" }));
            Assert.IsFalse(IsOpaque(new[] { 2000, 2450 }, new[] { true, true },
                new[] { "Legacy Shaders/Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse" }));
            Assert.IsFalse(IsOpaque(new[] { 3000 }, new[] { true },
                new[] { "Legacy Shaders/Transparent/Diffuse" }));
        }

        [Test]
        public void ATransparentOrCutoutShaderNameIsNotOpaqueEvenBelowTheQueue()
        {
            Assert.IsFalse(IsOpaque(new[] { 2000 }, new[] { true },
                new[] { "Legacy Shaders/Transparent/Cutout/Diffuse" }));
            Assert.IsFalse(IsOpaque(new[] { 2000 }, new[] { true },
                new[] { "VRChat/Mobile/Toon Cutout" }));
        }

        [Test]
        public void AMissingMaterialOrAnEmptySlotListIsNotOpaque()
        {
            Assert.IsFalse(IsOpaque(new[] { 0 }, new[] { false }, new[] { "" }));
            Assert.IsFalse(IsOpaque(new int[0], new bool[0], new string[0]));
            Assert.IsFalse(IsOpaque(null, null, null));
        }

        // ---- fixed geometry ----

        [Test]
        public void LevelStructureRootsAreFixedGeometry()
        {
            Assert.IsTrue(IsFixedGeometry(
                "Island_story_01_bench_size/LevelContent/Structures/boundary_x0/Visual"));
            Assert.IsTrue(IsFixedGeometry(
                "Island_physics_test_field/LevelContent/C01/Structures/C01_ramp"));
            Assert.IsTrue(IsFixedGeometry("LevelContent/hub/Structures/hub_floor/Visual"));
        }

        [Test]
        public void TerrainVisualsAndRootGroundSlabsAreFixedGeometryToo()
        {
            Assert.IsTrue(IsFixedGeometry(
                "Island__ref_cancer/LevelContent/_ref_cancer/TerrainRoot/TerrainVisual/area2_sector0_model3"));
            Assert.IsTrue(IsFixedGeometry("Island_terrain_contact_lab/Ground"));
            Assert.IsTrue(IsFixedGeometry("HubReturnButton_story_01_bench_size"));
        }

        [Test]
        public void PropsTheBallAndTheOldHubPadsAreNotFixedStructure()
        {
            Assert.IsFalse(IsFixedGeometry("Island_story_01_bench_size/LevelContent/Props/basket"));
            Assert.IsFalse(IsFixedGeometry(
                "Island_physics_test_field/LevelContent/C01/Props/beach_ball"));
            Assert.IsFalse(IsFixedGeometry("PlayerBall/BallPhysicsRoot/BallVisualRollRoot"));
            Assert.IsFalse(IsFixedGeometry("HubPads/HubPad__ref_cancer"));
            Assert.IsFalse(IsFixedGeometry("LevelContent/hub/Structures"));
            Assert.IsFalse(IsFixedGeometry(""));
        }

        // ---- static batching ----

        [Test]
        public void TerrainVisualPartsAreFixedGeometryThatWantsBatching()
        {
            // The reference island's drawn terrain surface: fixed, never absorbable.
            Assert.IsTrue(IsTerrainVisual(
                "Island__ref_cancer/LevelContent/_ref_cancer/TerrainRoot/TerrainVisual/area2_sector0_model4"));
            Assert.IsTrue(WantsBatching(
                "Island__ref_cancer/LevelContent/_ref_cancer/TerrainRoot/TerrainVisual/area2_sector0_model4",
                new[] { 105.927f, 10.988f, 104.262f }));
            Assert.IsTrue(WantsBatching(
                "Island__ref_cancer/LevelContent/_ref_cancer/TerrainRoot/TerrainVisual/area2_sector2_model0",
                new[] { 44.2f, 6.1f, 41.9f }));
        }

        [Test]
        public void ABackgroundShellIsNotBatched()
        {
            // area2_sector1_model0 (242 m) is a unique backdrop: batching it saves no draw and
            // would only add combined-mesh memory, which is why the occlusion rule already
            // treats it as "not ours to mark".
            Assert.IsFalse(WantsBatching(
                "Island__ref_cancer/LevelContent/_ref_cancer/TerrainRoot/TerrainVisual/area2_sector1_model0",
                new[] { 242.57f, 121.285f, 242.57f }));
        }

        [Test]
        public void PropsGroundSlabsAndReturnButtonsAreNotBatched()
        {
            Assert.IsFalse(IsTerrainVisual("Island_story_01_bench_size/LevelContent/Props/basket"));
            Assert.IsFalse(WantsBatching(
                "Island_terrain_contact_lab/Ground", new[] { 80f, 0.5f, 80f }));
            Assert.IsFalse(WantsBatching(
                "LevelContent/hub/Structures/hub_floor/Visual", new[] { 16f, 0.04f, 8f }));
            Assert.IsFalse(WantsBatching("HubReturnButton_story_01_bench_size", new[] { 0.4f, 1.2f, 0.16f }));
            Assert.IsFalse(IsTerrainVisual(""));
            Assert.IsFalse(IsTerrainVisual(null));
        }

        [Test]
        public void BatchingIsOredOntoTheExistingFlags()
        {
            // StaticEditorFlags: OccluderStatic = 2, BatchingStatic = 4, OccludeeStatic = 16.
            // A terrain part that is only an occludee (16) becomes 20; an occluder+occludee
            // (18) becomes 22; the flag is never written alone.
            Assert.AreEqual(20, WithBatching(16));
            Assert.AreEqual(22, WithBatching(18));
            Assert.AreEqual(4, WithBatching(0));
            // Another pass may already have set BatchingStatic: it stays, nothing is dropped.
            Assert.AreEqual(4, WithBatching(4));
            Assert.AreEqual(22, WithBatching(22));
        }

        // ---- the mark ----

        [Test]
        public void AClosedOpaqueLargeBoxIsAnOccluder()
        {
            AssertMark("Occluder", "", new[] { 4f, 0.64f, 6.861f }, Opaque(),
                Cube(4f, 0.64f, 6.861f), CubeTriangles);
        }

        [Test]
        public void AnOpenOpaqueSurfaceIsOnlyAnOccludee()
        {
            // A 4 x 4 m single plane: large enough and opaque, but not a closed solid.
            var quad = new float[] { 0f, 0f, 0f, 4f, 0f, 0f, 4f, 0f, 4f, 0f, 0f, 4f };
            AssertMark("Occludee", "open-mesh", new[] { 4f, 0.04f, 4f }, Opaque(),
                quad, new[] { 0, 1, 2, 0, 2, 3 });
        }

        [Test]
        public void AClosedGroundSlabIsOnlyAnOccludeeBecauseItsTrianglesAreTooLarge()
        {
            // The picnic ground's 13.4 x 13.4 m top face: one 89.8 m^2 triangle, over the
            // 80 m^2 occluder limit; the hub floor's 16 x 8 m slab is 64 m^2 and stays in.
            AssertMark("Occludee", "large-triangle", new[] { 13.4f, 0.04f, 13.4f }, Opaque(),
                Cube(13.4f, 0.04f, 13.4f), CubeTriangles);
            AssertMark("Occluder", "", new[] { 16f, 0.04f, 8f }, Opaque(),
                Cube(16f, 0.04f, 8f), CubeTriangles);
        }

        [Test]
        public void ACutoutMeshIsOnlyAnOccludee()
        {
            AssertMark("Occludee", "non-opaque", new[] { 30f, 5f, 24f },
                new[] { 2000, 2450 }, new[] { true, true },
                new[] { "Legacy Shaders/Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse" },
                Cube(30f, 5f, 24f), CubeTriangles);
        }

        [Test]
        public void AThinPieceIsOnlyAnOccludee()
        {
            AssertMark("Occludee", "small", new[] { 0.35f, 1.2f, 0.12f }, Opaque(),
                Cube(0.35f, 1.2f, 0.12f), CubeTriangles);
        }

        [Test]
        public void ABackgroundShellIsNotMarkedAtAll()
        {
            AssertMark("None", "background-shell", new[] { 242.57f, 121.285f, 242.57f }, Opaque(),
                Cube(242.57f, 121.285f, 242.57f), CubeTriangles);
        }

        [Test]
        public void AMeshThatCannotBeReadIsOnlyAnOccludee()
        {
            AssertMark("Occludee", "no-mesh", new[] { 4f, 0.64f, 6.861f }, Opaque(), null, null);
        }

        // ------------------------------------------------------------------ helpers

        // A closed box centred on the origin; local positions carry the scale, as they do
        // after the transform is applied.
        private static float[] Cube(float x, float y, float z)
        {
            float hx = x * 0.5f, hy = y * 0.5f, hz = z * 0.5f;
            return new[]
            {
                -hx, -hy, -hz, hx, -hy, -hz, hx, hy, -hz, -hx, hy, -hz,
                -hx, -hy, hz, hx, -hy, hz, hx, hy, hz, -hx, hy, hz,
            };
        }

        private static readonly int[] CubeTriangles =
        {
            0, 3, 2, 0, 2, 1,   // -z
            4, 5, 6, 4, 6, 7,   // +z
            0, 1, 5, 0, 5, 4,   // -y
            3, 7, 6, 3, 6, 2,   // +y
            0, 4, 7, 0, 7, 3,   // -x
            1, 2, 6, 1, 6, 5,   // +x
        };

        // Six quads with their own four vertices, like Unity's built-in face-split meshes.
        private static float[] FaceSplitCube(float size)
        {
            float h = size * 0.5f;
            var faces = new[]
            {
                new[] { -h, -h, -h, -h, -h, h, -h, h, h, -h, h, -h },
                new[] { h, -h, -h, h, h, -h, h, h, h, h, -h, h },
                new[] { -h, -h, -h, h, -h, -h, h, -h, h, -h, -h, h },
                new[] { -h, h, -h, -h, h, h, h, h, h, h, h, -h },
                new[] { -h, -h, -h, -h, h, -h, h, h, -h, h, -h, -h },
                new[] { -h, -h, h, h, -h, h, h, h, h, -h, h, h },
            };
            var vertices = new float[6 * 12];
            for (int f = 0; f < 6; f++)
            {
                Array.Copy(faces[f], 0, vertices, f * 12, 12);
            }
            return vertices;
        }

        private static int[] BuildFaceSplitCubeTriangles()
        {
            var triangles = new int[6 * 6];
            for (int f = 0; f < 6; f++)
            {
                int b = f * 4;
                int o = f * 6;
                triangles[o] = b;
                triangles[o + 1] = b + 1;
                triangles[o + 2] = b + 2;
                triangles[o + 3] = b;
                triangles[o + 4] = b + 2;
                triangles[o + 5] = b + 3;
            }
            return triangles;
        }

        private static readonly int[] FaceSplitCubeTriangles = BuildFaceSplitCubeTriangles();

        private static int[] OpaqueQueues()
        {
            return new[] { 2000 };
        }

        private static bool[] OpaquePresent()
        {
            return new[] { true };
        }

        private static string[] OpaqueShaders()
        {
            return new[] { "Legacy Shaders/Diffuse" };
        }

        private static object[] Opaque()
        {
            return new object[] { OpaqueQueues(), OpaquePresent(), OpaqueShaders() };
        }

        private bool IsClosed(float[] positions, int[] triangles)
        {
            return (bool)Invoke("IsClosedMesh", new object[] { positions, triangles });
        }

        private bool IsLargeEnough(float[] boundsSize)
        {
            return (bool)Invoke("IsLargeEnoughAsOccluder", new object[] { boundsSize });
        }

        private bool IsBackgroundShell(float[] boundsSize)
        {
            return (bool)Invoke("IsBackgroundShell", new object[] { boundsSize });
        }

        private bool IsOpaque(int[] queues, bool[] present, string[] shaders)
        {
            return (bool)Invoke("IsOpaqueGeometry", new object[] { queues, present, shaders });
        }

        private bool IsFixedGeometry(string path)
        {
            return (bool)Invoke("IsFixedStructureGeometry", new object[] { path });
        }

        private bool IsTerrainVisual(string path)
        {
            return (bool)Invoke("IsTerrainVisualGeometry", new object[] { path });
        }

        private bool WantsBatching(string path, float[] boundsSize)
        {
            return (bool)Invoke("WantsBatchingStatic", new object[] { path, boundsSize });
        }

        private int WithBatching(int flags)
        {
            MethodInfo method = ruleType.GetMethod("WithBatchingStatic",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "OccluderRule.WithBatchingStatic not found");
            Type flagType = method.GetParameters()[0].ParameterType;
            object value = Enum.Parse(flagType, flags.ToString());
            return Convert.ToInt32(method.Invoke(null, new[] { value }));
        }

        // ChooseMark(boundsSize, queues, present, shaders, worldPositions, triangles, out reason)
        private void AssertMark(string expectedMark, string expectedReason, float[] boundsSize,
            object[] opaque, float[] positions, int[] triangles)
        {
            AssertMark(expectedMark, expectedReason, boundsSize, (int[])opaque[0],
                (bool[])opaque[1], (string[])opaque[2], positions, triangles);
        }

        private void AssertMark(string expectedMark, string expectedReason, float[] boundsSize,
            int[] queues, bool[] present, string[] shaders, float[] positions, int[] triangles)
        {
            var args = new object[] { boundsSize, queues, present, shaders, positions, triangles, null };
            object mark = Invoke("ChooseMark", args);

            Assert.AreEqual(expectedMark, mark.ToString(), "reason: " + args[6]);
            Assert.AreEqual(expectedReason, args[6]);
        }

        private object Invoke(string name, object[] args)
        {
            MethodInfo method = ruleType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "OccluderRule." + name + " not found");
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
