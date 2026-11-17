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
                Mesh mesh = new Mesh
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
                mesh.TriMinX = new float[TerrainMeshMath.MaxTriangles];
                mesh.TriMaxX = new float[TerrainMeshMath.MaxTriangles];
                mesh.TriMinZ = new float[TerrainMeshMath.MaxTriangles];
                mesh.TriMaxZ = new float[TerrainMeshMath.MaxTriangles];
                mesh.TriSurfaceId = new int[TerrainMeshMath.MaxTriangles];
                mesh.CellStart = new int[TerrainGridMath.MaxGridCells + 1];
                mesh.CellTriangles = new int[TerrainGridMath.MaxGridReferences];
                mesh.CellCursor = new int[TerrainGridMath.MaxGridCells];
                mesh.Candidates = new int[TerrainMeshMath.MaxTriangles];
                mesh.CandidateMark = new bool[TerrainMeshMath.MaxTriangles];
                mesh.Oversized = new int[TerrainGridMath.MaxOversizedTriangles];
                float minX = float.MaxValue;
                float minZ = float.MaxValue;
                float maxX = -float.MaxValue;
                float maxZ = -float.MaxValue;
                for (int c = 0; c < builtChunks; c++)
                {
                    if (chunkMin[c].x < minX) minX = chunkMin[c].x;
                    if (chunkMin[c].z < minZ) minZ = chunkMin[c].z;
                    if (chunkMax[c].x > maxX) maxX = chunkMax[c].x;
                    if (chunkMax[c].z > maxZ) maxZ = chunkMax[c].z;
                }
                int cellsX;
                int cellsZ;
                int cellCount;
                int referenceCount;
                int oversizedCount;
                int refusalCode;
                int refusalDetail;
                mesh.GridReady = TerrainGridMath.TryBuild(minX, minZ, maxX, maxZ,
                    TerrainGridMath.DefaultCellM, meshVertices, meshIndices, builtTriangles,
                    chunkStarts, chunkCounts, chunkZones, builtChunks, mesh.TriMinX,
                    mesh.TriMaxX, mesh.TriMinZ, mesh.TriMaxZ, mesh.TriSurfaceId,
                    mesh.CellStart, mesh.CellTriangles, mesh.CellCursor, mesh.Oversized,
                    out mesh.GridOriginX, out mesh.GridOriginZ, out cellsX, out cellsZ,
                    out cellCount, out referenceCount, out oversizedCount, out refusalCode,
                    out refusalDetail);
                mesh.OversizedCount = oversizedCount;
                mesh.GridCellsX = cellsX;
                mesh.GridCellsZ = cellsZ;
                mesh.GridCellCount = cellCount;
                mesh.GridCellM = TerrainGridMath.DefaultCellM;
                mesh.GridRefusalDetail = refusalDetail;
                Assert.IsTrue(mesh.GridReady, "the fixture's grid built: code " + refusalCode);
                return mesh;
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

            // The grid the motor builds in SetTerrainMesh, for the equivalence case.
            public float[] TriMinX;
            public float[] TriMaxX;
            public float[] TriMinZ;
            public float[] TriMaxZ;
            public int[] TriSurfaceId;
            public int[] CellStart;
            public int[] CellTriangles;
            public int[] CellCursor;
            public float GridOriginX;
            public float GridOriginZ;
            public float GridCellM;
            public int GridCellsX;
            public int GridCellsZ;
            public int GridCellCount;
            public bool GridReady;
            public int GridRefusalDetail;
            public int[] Candidates;
            public bool[] CandidateMark;
            public int[] Oversized;
            public int OversizedCount;
        }

        /// <summary>The mesh's answer for one ball pose (one candidate surface, or none).</summary>
        private static bool Select(Mesh mesh, Vector3 center, float ballBottom, float stepLimit,
            out float height, out Vector3 normal, out int surfaceId)
        {
            // The work counters are the motor's cost diagnostic; these cases assert heights,
            // so they discard them (the counter cases live in EditMode/BallMotor).
            int discardedTriangles = 0;
            int discardedEvaluations = 0;
            return TerrainMeshMath.TrySelectSupport(center, ballBottom, stepLimit, Directions(),
                Quaternion.identity, RingRadius, mesh.Vertices, mesh.Indices,
                mesh.TriangleCount, mesh.ChunkMin, mesh.ChunkMax, mesh.ChunkStarts,
                mesh.ChunkTriangleCounts, mesh.ChunkZoneIds, mesh.ChunkCount,
                null, -1, null, null, null, null, null,
                ref discardedTriangles, ref discardedEvaluations, out normal, out height,
                out surfaceId);
        }


        /// <summary>The full chunk scan (the reference every candidate answer must match).</summary>
        private static bool SelectFull(Mesh mesh, Vector3 center, float ballBottom, float radius,
            float stepLimit, out float height, out Vector3 normal, out int surfaceId)
        {
            int tested = 0;
            int evals = 0;
            return TerrainMeshMath.TrySelectSupport(center, ballBottom, stepLimit, Directions(),
                Quaternion.identity, 0.9f * radius, mesh.Vertices, mesh.Indices,
                mesh.TriangleCount, mesh.ChunkMin, mesh.ChunkMax, mesh.ChunkStarts,
                mesh.ChunkTriangleCounts, mesh.ChunkZoneIds, mesh.ChunkCount, null, -1, null,
                null, null, null, null, ref tested, ref evals, out normal, out height,
                out surfaceId);
        }

        /// <summary>The same query, walking only the grid's candidates.</summary>
        private static bool SelectGrid(Mesh mesh, Vector3 center, float ballBottom, float radius,
            float stepLimit, int candidateCount, out float height, out Vector3 normal,
            out int surfaceId)
        {
            int tested = 0;
            int evals = 0;
            return TerrainMeshMath.TrySelectSupport(center, ballBottom, stepLimit, Directions(),
                Quaternion.identity, 0.9f * radius, mesh.Vertices, mesh.Indices,
                mesh.TriangleCount, mesh.ChunkMin, mesh.ChunkMax, mesh.ChunkStarts,
                mesh.ChunkTriangleCounts, mesh.ChunkZoneIds, mesh.ChunkCount,
                mesh.Candidates, candidateCount, mesh.TriMinX, mesh.TriMaxX, mesh.TriMinZ,
                mesh.TriMaxZ, mesh.TriSurfaceId, ref tested, ref evals, out normal, out height,
                out surfaceId);
        }

        /// <summary>Collect with the query's own window (or a wider v2-style sweep window).</summary>
        private static int Collect(Mesh mesh, Vector3 center, float halfExtent)
        {
            return TerrainGridMath.CollectCandidates(center.x, center.z, halfExtent,
                mesh.GridCellM, mesh.GridOriginX, mesh.GridOriginZ, mesh.GridCellsX,
                mesh.GridCellsZ, mesh.GridCellCount, mesh.CellStart, mesh.CellTriangles,
                mesh.TriMinX, mesh.TriMaxX, mesh.TriMinZ, mesh.TriMaxZ, mesh.Oversized,
                mesh.OversizedCount, mesh.TriangleCount, mesh.Candidates,
                mesh.CandidateMark);
        }

        /// <summary>Support selection at the fixture's default diameter (the simple cases).</summary>
        private static bool Select(Mesh mesh, Vector3 center, float ballBottom, out float height,
            out Vector3 normal, out int surfaceId)
        {
            return SelectFull(mesh, center, ballBottom, Radius, StepLimit, out height,
                out normal, out surfaceId);
        }

        private static bool SelectGridDefault(Mesh mesh, Vector3 center, float ballBottom,
            int candidateCount, out float height, out Vector3 normal, out int surfaceId)
        {
            return SelectGrid(mesh, center, ballBottom, Radius, StepLimit, candidateCount,
                out height, out normal, out surfaceId);
        }

        private static void Clear(Mesh mesh, int count)
        {
            TerrainGridMath.ClearCandidates(mesh.Candidates, count, mesh.CandidateMark);
        }

        // ------------------------------------------------------------------ cases ---



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
                mesh.ChunkTriangleCounts, mesh.ChunkCount, null, -1, null, null, null, null,
                out contact, out pushNormal, out depth),
                "the roof is a contact, not a support");
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
        public void TheGridCandidatesMatchTheFullScanAcrossDiametersWindowsAndCells()
        {
            // A REAL multi-level soup: flat floor, 1 m pit, a step at twice the step limit, a
            // 10 degree slope, and a roof. The floor/roof faces are 8 x 8 m quads, i.e. 72 cells
            // at 1 m, so this fixture also exercises the OVERSIZED list.
            Mesh mesh = new Soup()
                .Floor(0f, -4f, 4f, -4f, -1f)          // south floor
                .Floor(0f, -4f, 4f, 1f, 4f)            // north floor
                .Floor(-1f, -4f, 4f, -1f, 1f)          // pit floor at -1
                .Wall(0f, -1f, -4f, 4f, 1f, false)
                .Wall(0f, -1f, -4f, 4f, -1f, true)
                .Wall(0f, 2f * StepLimit, -4f, 4f, 3f, false)
                .Floor(2f * StepLimit, -4f, 4f, 3f, 4f) // 2 * step limit step
                .Roof(1.2f, -4f, 4f, -4f, 4f)          // roof over everything
                .Build();
            Assert.IsTrue(mesh.GridReady, "the fixture's grid must build");
            Assert.That(mesh.OversizedCount, Is.GreaterThan(0),
                "the 8 x 8 m faces must land in the oversized list at 1 m cells");

            // Diameters: the reference start ball (0.222), the PTF ball (0.30) and a big one.
            float[] diameters = { 0.222f, 0.3f, 1.2f };
            // Cell boundaries of a 1 m grid whose origin is -4: x/z = -3..3. Poses sit exactly
            // on the boundaries and 5 cm off them, so a boundary error cannot pass unnoticed.
            float[] poses = { -3f, -2.95f, -2f, -1f, -0.95f, 0f, 0.95f, 2f, 3f };
            float[] bottoms = { -1.5f, -0.6f, 0.2f };
            int[] first = new int[TerrainMeshMath.MaxTriangles];
            int compared = 0;
            int supported = 0;
            int penetrating = 0;
            int supersetChecked = 0;

            for (int d = 0; d < diameters.Length; d++)
            {
                float radius = diameters[d] * 0.5f;
                float stepLimit = 0.25f * diameters[d];
                for (int xi = 0; xi < poses.Length; xi++)
                {
                    for (int zi = 0; zi < poses.Length; zi++)
                    {
                        for (int bi = 0; bi < bottoms.Length; bi++)
                        {
                            Vector3 center = new Vector3(poses[xi], bottoms[bi] + radius,
                                poses[zi]);
                            // The query's own window, then a v2-style sweep window (radius
                            // plus half a step's travel plus skin): the wider list must be a
                            // superset, and BOTH must agree with the full scan.
                            int own = Collect(mesh, center, radius);
                            Assert.That(own, Is.GreaterThanOrEqualTo(0),
                                "a valid collection never refuses");
                            for (int k = 0; k < own; k++) first[k] = mesh.Candidates[k];
                            Clear(mesh, own);
                            int wide = Collect(mesh, center, radius + 0.11f);
                            Assert.That(wide, Is.GreaterThanOrEqualTo(own),
                                "a wider window cannot hold FEWER candidates than a narrow one");

                            int missing = 0;
                            for (int k = 0; k < own; k++)
                            {
                                bool found = false;
                                for (int w = 0; w < wide; w++)
                                {
                                    if (mesh.Candidates[w] == first[k]) { found = true; break; }
                                }
                                if (!found) missing = missing + 1;
                            }
                            Assert.AreEqual(0, missing,
                                "the wider window must keep every candidate of the narrower one");
                            supersetChecked = supersetChecked + 1;

                            float fullHeight;
                            Vector3 fullNormal;
                            int fullId;
                            bool full = SelectFull(mesh, center, bottoms[bi], radius, stepLimit,
                                out fullHeight, out fullNormal, out fullId);
                            float gridHeight;
                            Vector3 gridNormal;
                            int gridId;
                            bool grid = SelectGrid(mesh, center, bottoms[bi], radius, stepLimit,
                                wide, out gridHeight, out gridNormal, out gridId);
                            if (full)
                            {
                                Assert.IsTrue(grid, "pose " + d + "/" + xi + "/" + zi + "/" + bi);
                                Assert.AreEqual(fullId, gridId);
                                Assert.AreEqual(fullHeight, gridHeight, 1e-5f);
                                Assert.AreEqual(fullNormal.x, gridNormal.x, 1e-5f);
                                Assert.AreEqual(fullNormal.y, gridNormal.y, 1e-5f);
                                Assert.AreEqual(fullNormal.z, gridNormal.z, 1e-5f);
                                supported = supported + 1;
                            }
                            else
                            {
                                Assert.IsFalse(grid);
                            }

                            Vector3 fullPoint;
                            Vector3 fullPush;
                            float fullDepth;
                            bool fullHit = TerrainMeshMath.TryDeepestPenetration(center, radius,
                                mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts,
                                mesh.ChunkCount, null, -1, null, null, null, null, out fullPoint,
                                out fullPush, out fullDepth);
                            Vector3 gridPoint;
                            Vector3 gridPush;
                            float gridDepth;
                            bool gridHit = TerrainMeshMath.TryDeepestPenetration(center, radius,
                                mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts,
                                mesh.ChunkCount, mesh.Candidates, wide, mesh.TriMinX,
                                mesh.TriMaxX, mesh.TriMinZ, mesh.TriMaxZ, out gridPoint,
                                out gridPush, out gridDepth);
                            Assert.AreEqual(fullHit, gridHit, "penetration found");
                            if (fullHit)
                            {
                                Assert.AreEqual(fullDepth, gridDepth, 1e-5f);
                                Assert.AreEqual(fullPush.x, gridPush.x, 1e-5f);
                                Assert.AreEqual(fullPush.y, gridPush.y, 1e-5f);
                                Assert.AreEqual(fullPush.z, gridPush.z, 1e-5f);
                                penetrating = penetrating + 1;
                            }

                            Clear(mesh, wide);
                            compared = compared + 1;
                        }
                    }
                }
            }
            // Denominators, so "clean" cannot be read as "few poses": 3 diameters x 9 x 9 x 3.
            Assert.AreEqual(3 * 9 * 9 * 3, compared);
            Assert.That(supported, Is.GreaterThan(compared / 4),
                "enough poses actually stood on the mesh to make the comparison meaningful");
            Assert.That(penetrating, Is.GreaterThan(20),
                "and enough poses actually penetrated it");
            Assert.AreEqual(compared, supersetChecked);
        }

        [Test]
        public void TheCandidateListIsDedupedAndStaysSmall()
        {
            // A 8 x 8 m floor as one chunk: without the window filter a cell lookup would hand
            // the 17 probes a whole 1 m cell (dozens), with it only the triangles that can
            // touch a 0.3 m ball.
            Mesh mesh = new Soup().Floor(0f, -4f, 4f, -4f, 4f).Build();
            Vector3 center = new Vector3(0.49f, Radius, 0.49f); // near a 1 m cell corner
            int count = Collect(mesh, center, Radius);
            Assert.That(count, Is.GreaterThan(0), "a valid collection never refuses");
            Assert.That(count, Is.LessThan(20),
                "the window filter keeps a handful of triangles, not a cell");
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    Assert.AreNotEqual(mesh.Candidates[i], mesh.Candidates[j],
                        "candidate " + i + " and " + j + " are the same triangle");
                }
            }
            float fullHeight;
            Vector3 fullNormal;
            int fullId;
            float gridHeight;
            Vector3 gridNormal;
            int gridId;
            Assert.IsTrue(Select(mesh, center, 0f, out fullHeight, out fullNormal, out fullId));
            Assert.IsTrue(SelectGridDefault(mesh, center, 0f, count, out gridHeight,
                out gridNormal, out gridId));
            Assert.AreEqual(fullId, gridId);
            Assert.AreEqual(fullHeight, gridHeight, 1e-5f);
            Clear(mesh, count);
        }

    }
}
