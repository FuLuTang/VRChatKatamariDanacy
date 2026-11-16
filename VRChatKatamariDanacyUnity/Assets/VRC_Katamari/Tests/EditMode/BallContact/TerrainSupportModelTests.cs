using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The terrain SUPPORT model, on real triangle geometry: which face holds the ball up,
    /// over a flat floor, a 1 m gully, its rim, a step at and beyond the step limit, a
    /// tunnel roof, a slope, a 10 m pit, and a box top competing with the mesh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MODEL (the sentence the design docs quote): <b>the support is the highest face at
    /// or below the ball's own bottom plus the step limit (0.25 * D), drawn from the terrain
    /// triangles, the static boxes, the ramps and the prop tops - and the arena ground plane
    /// is a candidate only where the mesh answers nothing under the ball.</b> Downward there
    /// is no bound at all; a real fall is the level's kill plane's decision.
    /// </para>
    /// <para>
    /// Why these cases are the regression set: the 2026-11-16 terrain run measured four
    /// facts in the real Editor. A copied arena plane at y=0 held a 0.30 m ball at y=0.15
    /// over C22's 1 m gully (the mesh lost because it was LOWER than the plane), and with
    /// the plane at -1000 the ball fell through. Both are selection rules, so both are
    /// pinned here without a scene: <see cref="ThereIsNoSupportPlaneWhereTheMeshAnswers"/>
    /// and <see cref="TheGullyFloorWinsEvenWhenThePlaneSitsAboveIt"/>.
    /// </para>
    /// <para>
    /// No scene object is created: every fixture is a plain triangle soup fed through
    /// <see cref="TerrainMeshMath.TryBuildMesh"/> into the same fixed arrays the motor uses.
    /// There is therefore nothing to mark <c>HideFlags.DontSave</c> and nothing for a
    /// TearDown to destroy - the rule is satisfied vacuously here, and any future case that
    /// adds a GameObject or Mesh must add both. The motor-level cases (the microsecond
    /// counter, the 64 static boxes) are in EditMode/BallMotor/, where a host object exists
    /// and is destroyed in TearDown.
    /// </para>
    /// </remarks>
    public class TerrainSupportModelTests
    {
        private const float Tolerance = 1e-4f;
        // The physics test field's starting ball: 0.30 m, so the step limit is 0.075 m.
        private const float Diameter = 0.3f;
        private const float Radius = Diameter * 0.5f;
        private const float StepLimit = 0.25f * Diameter;
        // The motor probes at 0.9 * r (TerrainMesh.BallMotor TryQueryTerrainSupport).
        private const float RingRadius = 0.9f * Radius;

        private static Vector3[] Directions()
        {
            Vector3[] directions = new Vector3[RadialProxyMath.DirectionCount];
            Assert.IsTrue(RadialProxyMath.TryFillDirections(directions),
                "the motor's own 16-direction set");
            return directions;
        }

        // ---------------------------------------------------------------- fixtures ---

        /// <summary>Triangle soup builder: every face is a quad, chunked as one chunk.</summary>
        private sealed class Soup
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int> indices = new List<int>();

            /// <summary>A floor quad whose winding normal is +Y (as a shipped floor is).</summary>
            public Soup Floor(float y, float minX, float maxX, float minZ, float maxZ)
            {
                return Quad(new Vector3(minX, y, minZ), new Vector3(minX, y, maxZ),
                    new Vector3(maxX, y, maxZ), new Vector3(maxX, y, minZ));
            }

            /// <summary>A roof quad whose winding normal is -Y (its underside faces the ball).</summary>
            /// <remarks>
            /// T1 is single-sided: a ball under a roof must be on the face's front side for
            /// the push-out to exist at all, so the winding here is load-bearing, not
            /// cosmetic.
            /// </remarks>
            public Soup Roof(float y, float minX, float maxX, float minZ, float maxZ)
            {
                return Quad(new Vector3(minX, y, minZ), new Vector3(maxX, y, minZ),
                    new Vector3(maxX, y, maxZ), new Vector3(minX, y, maxZ));
            }

            /// <summary>
            /// A vertical quad (a wall/riser). Its normal is horizontal, so it is never a
            /// support; <paramref name="facePlusZ"/> picks which side the winding faces.
            /// </summary>
            public Soup Wall(float y0, float y1, float minX, float maxX, float z,
                bool facePlusZ)
            {
                if (facePlusZ)
                {
                    return Quad(new Vector3(maxX, y0, z), new Vector3(minX, y0, z),
                        new Vector3(minX, y1, z), new Vector3(maxX, y1, z));
                }
                return Quad(new Vector3(minX, y0, z), new Vector3(maxX, y0, z),
                    new Vector3(maxX, y1, z), new Vector3(minX, y1, z));
            }

            public Soup Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int first = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                vertices.Add(d);
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 3);
                return this;
            }

            public Mesh Build()
            {
                Vector3[] soupVertices = vertices.ToArray();
                int[] soupIndices = indices.ToArray();
                int triangleCount = soupIndices.Length / 3;
                var meshVertices = new Vector3[TerrainMeshMath.MaxVertices];
                var meshIndices = new int[TerrainMeshMath.MaxTriangles * 3];
                var chunkStarts = new int[TerrainMeshMath.MaxChunks];
                var chunkCounts = new int[TerrainMeshMath.MaxChunks];
                var chunkZones = new int[TerrainMeshMath.MaxChunks];
                var chunkMin = new Vector3[TerrainMeshMath.MaxChunks];
                var chunkMax = new Vector3[TerrainMeshMath.MaxChunks];
                // One chunk for the whole soup (every fixture here is far below 512
                // triangles), which is also how C22's 14-triangle gully is shipped. The
                // writer's arrays are separate objects, never the destination buffers.
                int[] sourceStarts = { 0 };
                int[] sourceCounts = { triangleCount };
                int[] sourceZones = { 0 };
                int builtTriangles;
                int builtChunks;
                Assert.IsTrue(TerrainMeshMath.TryBuildMesh(soupVertices, soupVertices.Length,
                    soupIndices, soupIndices.Length, sourceStarts, sourceCounts, sourceZones,
                    1,
                    meshVertices, meshIndices, chunkStarts, chunkCounts, chunkZones,
                    chunkMin, chunkMax, out builtTriangles, out builtChunks),
                    "build mesh: " + triangleCount + " triangles");
                return new Mesh
                {
                    Vertices = meshVertices,
                    Indices = meshIndices,
                    ChunkStarts = chunkStarts,
                    ChunkTriangleCounts = chunkCounts,
                    ChunkZoneIds = chunkZones,
                    ChunkMin = chunkMin,
                    ChunkMax = chunkMax,
                    TriangleCount = builtTriangles,
                    ChunkCount = builtChunks,
                };
            }
        }

        private sealed class Mesh
        {
            public Vector3[] Vertices;
            public int[] Indices;
            public int[] ChunkStarts;
            public int[] ChunkTriangleCounts;
            public int[] ChunkZoneIds;
            public Vector3[] ChunkMin;
            public Vector3[] ChunkMax;
            public int TriangleCount;
            public int ChunkCount;
        }

        /// <summary>The mesh's answer for one ball pose (one candidate surface, or none).</summary>
        private static bool Select(Mesh mesh, Vector3 center, float ballBottom, float stepLimit,
            out float height, out Vector3 normal, out int surfaceId)
        {
            return TerrainMeshMath.TrySelectSupport(center, ballBottom, stepLimit, Directions(),
                Quaternion.identity, RingRadius, mesh.Vertices, mesh.Indices,
                mesh.TriangleCount, mesh.ChunkMin, mesh.ChunkMax, mesh.ChunkStarts,
                mesh.ChunkTriangleCounts, mesh.ChunkZoneIds, mesh.ChunkCount, out normal,
                out height, out surfaceId);
        }

        /// <summary>The one selection the motor runs, with no boxes, ramps or props.</summary>
        private static bool Resolve(Vector3 center, float ballBottom, float stepLimit,
            float groundOffset, float terrainHeight, Vector3 terrainNormal, int terrainId,
            out int surfaceId, out float height, out Vector3 normal)
        {
            Vector3 point;
            int candidates;
            return SupportSurfaceMath.TryResolveSupport(center, ballBottom, stepLimit,
                new Vector3(0f, 1f, 0f), groundOffset, new Vector3[0], new Vector3[0],
                new bool[0], 0, new Vector3[0], new Vector3[0], new Vector3[0], new float[0],
                new float[0], 0, new Vector3[0], new Vector3[0], 0, terrainId, terrainHeight,
                terrainNormal, out surfaceId, out point, out normal, out height,
                out candidates);
        }

        // ------------------------------------------------------------------ cases ---

        [Test]
        public void ThereIsNoSupportPlaneWhereTheMeshAnswers()
        {
            Mesh mesh = new Soup().Floor(0f, -2f, 2f, -2f, 2f).Build();
            float height;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(Select(mesh, new Vector3(0f, Radius, 0f), 0f, StepLimit, out height,
                out normal, out surfaceId), "the flat floor answers");
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(1f, normal.y, Tolerance);
            Assert.AreEqual(TerrainMeshMath.FirstTerrainSurfaceId, surfaceId);

            // The plane is at the same height, and the mesh wins anyway: the plane does not
            // compete where the mesh answers.
            int selected;
            float selectedHeight;
            Vector3 selectedNormal;
            Assert.IsTrue(Resolve(new Vector3(0f, Radius, 0f), 0f, StepLimit, 0f, height,
                normal, surfaceId, out selected, out selectedHeight, out selectedNormal));
            Assert.AreEqual(TerrainMeshMath.FirstTerrainSurfaceId, selected,
                "the mesh supports the ball, not the arena plane");
            Assert.AreEqual(0f, selectedHeight, Tolerance);
        }

        [Test]
        public void TheGullyFloorWinsEvenWhenThePlaneSitsAboveIt()
        {
            // C22's shape: a 1 m deep, 4 m wide gully through a floor at 0. The copied arena
            // plane sits at 0 as well, which is the configuration the operator measured.
            Mesh mesh = new Soup()
                .Floor(0f, -2f, 2f, -3f, -1f)   // south floor
                .Floor(0f, -2f, 2f, 1f, 3f)     // north floor
                .Floor(-1f, -2f, 2f, -1f, 1f)   // gully floor, 1 m down
                .Wall(0f, -1f, -2f, 2f, 1f, false)    // north face, faces -Z
                .Wall(0f, -1f, -2f, 2f, -1f, true)    // south face, faces +Z
                .Build();
            Vector3 center = new Vector3(0f, Radius, 0f);
            float terrainHeight;
            Vector3 terrainNormal;
            int terrainId;
            Assert.IsTrue(Select(mesh, center, 0f, StepLimit, out terrainHeight,
                out terrainNormal, out terrainId), "the gully floor answers");
            Assert.AreEqual(-1f, terrainHeight, Tolerance);
            Assert.That(terrainNormal.y, Is.GreaterThan(0.99f));

            int surfaceId;
            float height;
            Vector3 normal;
            // The plane at 0 is ABOVE the gully floor. It must not win: that is exactly the
            // bug that kept the ball at y=0.15 across the gully.
            Assert.IsTrue(Resolve(center, 0f, StepLimit, 0f, terrainHeight, terrainNormal,
                terrainId, out surfaceId, out height, out normal));
            Assert.AreEqual(-1f, height, Tolerance,
                "the ball's support is the gully floor, not the plane at 0");
            Assert.AreEqual(TerrainMeshMath.FirstTerrainSurfaceId, surfaceId);

            // And the plane is still the fallback where the mesh has nothing under the ball.
            Mesh floorOnly = new Soup().Floor(0f, -1f, 1f, -1f, 1f).Build();
            Assert.IsFalse(Select(floorOnly, new Vector3(3f, Radius, 0f), 0f, StepLimit,
                out terrainHeight, out terrainNormal, out terrainId),
                "outside the footprint the mesh answers nothing");
            Assert.IsTrue(Resolve(new Vector3(3f, Radius, 0f), 0f, StepLimit, -2f, 0f,
                Vector3.up, SupportSurfaceMath.NoSurfaceId, out surfaceId, out height,
                out normal));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, surfaceId);
            Assert.AreEqual(-2f, height, Tolerance, "the plane is the no-terrain answer");
        }

        [Test]
        public void TheGullyRimSupportsUntilTheFootprintLeavesIt()
        {
            Mesh mesh = new Soup()
                .Floor(0f, -2f, 2f, -3f, -1f)
                .Floor(0f, -2f, 2f, 1f, 3f)
                .Floor(-1f, -2f, 2f, -1f, 1f)
                .Build();
            // 0.05 m inside the rim: the -Z ring probe (0.135 m out) is still on the south
            // floor, so the rim holds the ball up.
            float height;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(Select(mesh, new Vector3(0f, Radius, -0.95f), 0f, StepLimit,
                out height, out normal, out surfaceId));
            Assert.AreEqual(0f, height, Tolerance, "the rim still supports the ball");

            // 0.20 m inside: every probe is inside the gully, so the ball belongs to the
            // floor 1 m down and starts to drop into it.
            Assert.IsTrue(Select(mesh, new Vector3(0f, Radius, -0.8f), 0f, StepLimit,
                out height, out normal, out surfaceId));
            Assert.AreEqual(-1f, height, Tolerance, "past the rim the ball is over the gully");
        }

        [Test]
        public void AStepAtTheStepLimitIsClimbableAndFourDiametersIsNot()
        {
            // 0.25 * D = 0.075 m: the inclusive edge of the step gate (C09's semantics).
            Mesh atLimit = new Soup()
                .Floor(0f, -2f, 2f, -2f, 0f)
                .Wall(0f, StepLimit, -2f, 2f, 0f, false)
                .Floor(StepLimit, -2f, 2f, 0f, 2f)
                .Build();
            float height;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(Select(atLimit, new Vector3(0f, Radius, -0.1f), 0f, StepLimit,
                out height, out normal, out surfaceId));
            Assert.AreEqual(StepLimit, height, 1e-5f,
                "a step at exactly 0.25 * D is a candidate (the bound is inclusive)");

            // 4 * D = 1.2 m: the top is inside the footprint (the ring reaches it) but above
            // the ceiling, so it is a wall and the floor underneath is still the support.
            Mesh tooTall = new Soup()
                .Floor(0f, -2f, 2f, -2f, 0f)
                .Wall(0f, 4f * Diameter, -2f, 2f, 0f, false)
                .Floor(4f * Diameter, -2f, 2f, 0f, 2f)
                .Build();
            Assert.IsTrue(Select(tooTall, new Vector3(0f, Radius, -0.1f), 0f, StepLimit,
                out height, out normal, out surfaceId));
            Assert.AreEqual(0f, height, Tolerance,
                "a 4 * D step is not a support; the ball keeps the floor");
        }

        [Test]
        public void ATunnelRoofIsNotASupportAndPushesTheBallDown()
        {
            Mesh mesh = new Soup()
                .Floor(0f, -2f, 2f, -2f, 2f)
                .Roof(1.2f, -2f, 2f, -2f, 2f)
                .Build();
            // Under the roof, 0.95 m above the floor: the floor is the support and the roof
            // (1.2 m) is above the ball's step ceiling.
            float height;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(Select(mesh, new Vector3(0f, 0.95f + Radius, 0f), 0.95f, StepLimit,
                out height, out normal, out surfaceId));
            Assert.AreEqual(0f, height, Tolerance, "a roof is never a floor");

            // Touching the roof from below: T1 pushes DOWN (the face normal), so the ball is
            // stopped instead of passing through.
            Vector3 contact;
            Vector3 pushNormal;
            float depth;
            Assert.IsTrue(TerrainMeshMath.TryDeepestPenetration(
                new Vector3(0f, 1.1f, 0f), Radius, mesh.Vertices, mesh.Indices,
                mesh.TriangleCount, mesh.ChunkMin, mesh.ChunkMax, mesh.ChunkStarts,
                mesh.ChunkTriangleCounts, mesh.ChunkCount, out contact, out pushNormal,
                out depth), "the roof is a contact, not a support");
            Assert.AreEqual(0.05f, depth, 1e-3f);
            Assert.That(pushNormal.y, Is.LessThan(-0.99f), "a roof pushes the ball down");
        }

        [Test]
        public void ASlopeSupportsAtItsHeightUnderTheBallCentre()
        {
            // A 10 degree slope rising along +X. The support height must be the plane's value
            // at the BALL's XZ, not at the probe 0.9 r away.
            float rise = Mathf.Tan(10f * Mathf.Deg2Rad);
            Mesh mesh = new Soup()
                .Quad(new Vector3(-2f, -2f * rise, -2f), new Vector3(-2f, -2f * rise, 2f),
                    new Vector3(2f, 2f * rise, 2f), new Vector3(2f, 2f * rise, -2f))
                .Build();
            float height;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(Select(mesh, new Vector3(0.5f, rise * 0.5f + Radius, 0.5f),
                rise * 0.5f, StepLimit, out height, out normal, out surfaceId));
            Assert.AreEqual(rise * 0.5f, height, 1e-3f);
            Assert.That(normal.y, Is.EqualTo(Mathf.Cos(10f * Mathf.Deg2Rad)).Within(1e-3f));
            Assert.That(normal.x, Is.EqualTo(-Mathf.Sin(10f * Mathf.Deg2Rad)).Within(1e-3f));
        }

        [Test]
        public void APitFloorTenMetresDownIsStillASupport()
        {
            // There is deliberately NO downward bound: the band only limits how far UP a
            // surface may be. A fall is the level's kill plane's decision.
            Mesh mesh = new Soup().Floor(-10f, -2f, 2f, -2f, 2f).Build();
            float height;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(Select(mesh, new Vector3(0f, Radius, 0f), 0f, StepLimit, out height,
                out normal, out surfaceId));
            Assert.AreEqual(-10f, height, Tolerance);
        }

        [Test]
        public void ABoxTopInsideTheBandBeatsTheMeshAndOneOutsideDoesNot()
        {
            Mesh mesh = new Soup().Floor(-1f, -2f, 2f, -2f, 2f).Build();
            Vector3 center = new Vector3(0f, Radius, 0f);
            float terrainHeight;
            Vector3 terrainNormal;
            int terrainId;
            Assert.IsTrue(Select(mesh, center, 0f, StepLimit, out terrainHeight, out terrainNormal,
                out terrainId));

            // A box top at -0.5 is inside the ball's band (its mask entry is true), so it is
            // a higher support than the mesh at -1.
            Vector3[] minima = { new Vector3(-1f, -2f, -1f) };
            Vector3[] maxima = { new Vector3(1f, -0.5f, 1f) };
            int surfaceId;
            float height;
            Vector3 normal;
            int candidates;
            Vector3 point;
            Assert.IsTrue(SupportSurfaceMath.TryResolveSupport(center, 0f, StepLimit,
                Vector3.up, 0f, minima, maxima, new[] { true }, 1, new Vector3[0],
                new Vector3[0], new Vector3[0], new float[0], new float[0], 0, new Vector3[0],
                new Vector3[0], 0, terrainId, terrainHeight, terrainNormal, out surfaceId,
                out point, out normal, out height, out candidates));
            Assert.AreEqual(1, surfaceId, "the in-band box top is the higher support");
            Assert.AreEqual(-0.5f, height, Tolerance);

            // The same box with a false mask entry is not a candidate (the motor's mask says
            // it is a wall), so the mesh wins instead.
            Assert.IsTrue(SupportSurfaceMath.TryResolveSupport(center, 0f, StepLimit,
                Vector3.up, 0f, minima, maxima, new[] { false }, 1, new Vector3[0],
                new Vector3[0], new Vector3[0], new float[0], new float[0], 0, new Vector3[0],
                new Vector3[0], 0, terrainId, terrainHeight, terrainNormal, out surfaceId,
                out point, out normal, out height, out candidates));
            Assert.AreEqual(TerrainMeshMath.FirstTerrainSurfaceId, surfaceId,
                "a box outside the band cannot support the ball");
            Assert.AreEqual(-1f, height, Tolerance);
        }
    }
}
