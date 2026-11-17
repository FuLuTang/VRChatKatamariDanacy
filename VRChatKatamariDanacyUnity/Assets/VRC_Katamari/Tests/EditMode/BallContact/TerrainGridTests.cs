using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The terrain grid: what it builds, what it refuses, and the invariant that every mesh
    /// triangle ends up reachable from at least one cell (no silent drops).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusals are DELIBERATE and must stay explicit: the grid is an acceleration
    /// structure, so a mesh that cannot fit the caps keeps its collision by falling back to
    /// the full scan (the motor does that, and reports the code), but the grid build itself
    /// must never quietly register a subset of the triangles - that would change which
    /// surface supports the ball.
    /// </para>
    /// <para>
    /// No scene object is created: the fixtures are plain arrays, so there is nothing to mark
    /// <c>HideFlags.DontSave</c> and nothing for a TearDown to destroy.
    /// </para>
    /// </remarks>
    public class TerrainGridTests
    {
        /// <summary>One axis-aligned quad in XZ at <paramref name="y"/>, wound +Y.</summary>
        private static void Quad(float y, float x0, float x1, float z0, float z1,
            Vector3[] vertices, int[] indices, int triangleBase, int indexBase)
        {
            int first = triangleBase * 3;
            vertices[first] = new Vector3(x0, y, z0);
            vertices[first + 1] = new Vector3(x0, y, z1);
            vertices[first + 2] = new Vector3(x1, y, z1);
            vertices[first + 3] = new Vector3(x1, y, z0);
            indices[indexBase] = first;
            indices[indexBase + 1] = first + 1;
            indices[indexBase + 2] = first + 2;
            indices[indexBase + 3] = first;
            indices[indexBase + 4] = first + 2;
            indices[indexBase + 5] = first + 3;
        }

        /// <summary>Builds a grid over a fixture of N quads, all in one chunk.</summary>
        private static bool TryBuildFixture(int quadCount, float cellM,
            out int cellsX, out int cellsZ, out int cellCount, out int references,
            out int oversized, out int refusalCode, out int refusalDetail)
        {
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            for (int q = 0; q < quadCount; q++)
            {
                // 2 x 2 m quads in a row; one quad is enough for the cap cases.
                float x0 = -1f + 2f * q;
                Quad(0f, x0, x0 + 2f, -1f, 1f, vertices, indices, q * 2, q * 6);
            }
            int triangles = quadCount * 2;
            int[] chunkStarts = { 0 };
            int[] chunkCounts = { triangles };
            int[] chunkZones = { 0 };
            float[] triMinX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMinZ = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxZ = new float[TerrainMeshMath.MaxTriangles];
            int[] triSurfaceId = new int[TerrainMeshMath.MaxTriangles];
            int[] cellStart = new int[TerrainGridMath.MaxGridCells + 1];
            int[] cellTriangles = new int[TerrainGridMath.MaxGridReferences];
            int[] cellCursor = new int[TerrainGridMath.MaxGridCells];
            int[] oversizedTriangles = new int[TerrainGridMath.MaxOversizedTriangles];
            return TerrainGridMath.TryBuild(-1f, -1f, 2f * quadCount - 1f, 1f, cellM,
                vertices, indices, triangles, chunkStarts, chunkCounts, chunkZones, 1,
                triMinX, triMaxX, triMinZ, triMaxZ, triSurfaceId, cellStart, cellTriangles,
                cellCursor, oversizedTriangles, out float originX, out float originZ,
                out cellsX, out cellsZ, out cellCount, out references, out oversized,
                out refusalCode, out refusalDetail);
        }

        [Test]
        public void ARealisticPadBuildsAndEveryTriangleIsReachable()
        {
            // 7 quads of 2 x 2 m: 14 triangles over 14 x 2 m at a 1 m cell -> 15 x 3 cells.
            Assert.IsTrue(TryBuildFixture(7, TerrainGridMath.DefaultCellM, out _, out _,
                out int cellCount, out int references, out int oversized, out int code,
                out int detail),
                "a pad-size fixture must build: code " + code + " detail " + detail);
            Assert.AreEqual(15 * 3, cellCount);
            Assert.That(references, Is.GreaterThan(14),
                "a triangle spanning a cell boundary is referenced in both cells");
            Assert.AreEqual(TerrainGridMath.RefusalNone, code);
        }

        [Test]
        public void TooManyCellsIsAnExplicitRefusal()
        {
            // 15 x 2 m at 0.25 m -> 61 x 9 = 549 cells (fine), so grow the fixture instead:
            // use the smallest legal cell on a very long row.
            Assert.IsFalse(TryBuildFixture(400, TerrainGridMath.MinCellM, out _, out _,
                out int cellCount, out int references, out int oversized, out int code,
                out int detail),
                "400 quads of 2 m at 0.25 m must exceed the cell cap");
            Assert.AreEqual(TerrainGridMath.RefusalCellCount, code);
            Assert.That(detail, Is.GreaterThan(TerrainGridMath.MaxGridCells),
                "the detail names the cell count that broke the cap");
            Assert.AreEqual(0, cellCount);
            Assert.AreEqual(0, references);
            Assert.AreEqual("grid cells exceed the cap; raise the cell size (terrainGridCellM)",
                TerrainGridMath.RefusalReason(code));
        }

        [Test]
        public void AHugeTriangleGoesToTheOversizedListInsteadOfRefusingTheGrid()
        {
            // One quad 20 x 20 m at 1 m: the grid itself fits (21 x 21 = 441 cells) but a
            // single triangle spans all 441 of them, past the per-triangle cap. The contract:
            // such a triangle is OFFERED TO EVERY QUERY from the oversized list instead of
            // being stamped into its cells or refusing the grid (which is what made the
            // reference level's 1 m grid impossible: code 3, detail 72).
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            Quad(0f, -10f, 10f, -10f, 10f, vertices, indices, 0, 0);
            int[] chunkStarts = { 0 };
            int[] chunkCounts = { 2 };
            int[] chunkZones = { 0 };
            float[] triMinX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMinZ = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxZ = new float[TerrainMeshMath.MaxTriangles];
            int[] triSurfaceId = new int[TerrainMeshMath.MaxTriangles];
            int[] cellStart = new int[TerrainGridMath.MaxGridCells + 1];
            int[] cellTriangles = new int[TerrainGridMath.MaxGridReferences];
            int[] cellCursor = new int[TerrainGridMath.MaxGridCells];
            int[] oversized = new int[TerrainGridMath.MaxOversizedTriangles];
            Assert.IsTrue(TerrainGridMath.TryBuild(-10f, -10f, 10f, 10f, 1f, vertices, indices,
                2, chunkStarts, chunkCounts, chunkZones, 1, triMinX, triMaxX, triMinZ, triMaxZ,
                triSurfaceId, cellStart, cellTriangles, cellCursor, oversized, out _, out _,
                out _, out _, out int cellCount, out int references, out int oversizedCount,
                out int code, out int detail),
                "a huge triangle must NOT refuse the grid: it goes to the oversized list");
            Assert.AreEqual(TerrainGridMath.RefusalNone, code);
            Assert.AreEqual(2, oversizedCount, "both triangles of the 20 x 20 m quad");
            Assert.That(references, Is.LessThan(2),
                "an oversized triangle is stamped into no cell, so the reference table stays tiny");
            Assert.That(cellCount, Is.GreaterThan(0));
            // No duplicate and no omission: the list holds each oversized triangle once.
            Assert.AreNotEqual(oversized[0], oversized[1]);

            // The list itself has a cap, and overflowing it is an explicit refusal that leaves
            // NO grid behind (all geometry outs zero).
            int[] tiny = new int[1];
            Assert.IsFalse(TerrainGridMath.TryBuild(-10f, -10f, 10f, 10f, 1f, vertices, indices,
                2, chunkStarts, chunkCounts, chunkZones, 1, triMinX, triMaxX, triMinZ, triMaxZ,
                triSurfaceId, cellStart, cellTriangles, cellCursor, tiny, out _, out _,
                out _, out _, out cellCount, out references, out oversizedCount, out code,
                out detail), "a one-slot oversized list cannot hold two huge triangles");
            Assert.AreEqual(TerrainGridMath.RefusalOversizedList, code);
            Assert.AreEqual(0, cellCount, "a refused build reports no grid");
            Assert.AreEqual(0, references);
            Assert.AreEqual(0, oversizedCount);
        }

        [Test]
        public void ATooSmallReferenceBufferIsARefusalNotAnOverwrite()
        {
            // The caller's reference array is a hard bound (the motor always passes the full
            // one; a short one must fail cleanly rather than write past its end).
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            Quad(0f, -1f, 1f, -1f, 1f, vertices, indices, 0, 0);
            int[] chunkStarts = { 0 };
            int[] chunkCounts = { 2 };
            int[] chunkZones = { 0 };
            float[] triMinX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMinZ = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxZ = new float[TerrainMeshMath.MaxTriangles];
            int[] triSurfaceId = new int[TerrainMeshMath.MaxTriangles];
            int[] cellStart = new int[TerrainGridMath.MaxGridCells + 1];
            int[] cellCursor = new int[TerrainGridMath.MaxGridCells];
            Assert.IsFalse(TerrainGridMath.TryBuild(-1f, -1f, 1f, 1f, 1f, vertices, indices, 2,
                chunkStarts, chunkCounts, chunkZones, 1, triMinX, triMaxX, triMinZ, triMaxZ,
                triSurfaceId, cellStart, new int[1], cellCursor,
                new int[TerrainGridMath.MaxOversizedTriangles], out _, out _, out _, out _,
                out _, out _, out _, out int code, out int detail),
                "a one-slot reference buffer cannot hold a 2-triangle mesh");
            Assert.AreEqual(TerrainGridMath.RefusalReferences, code);
            Assert.That(detail, Is.GreaterThan(1));
        }

        /// <summary>One built grid plus the per-triangle lookup data, for the size comparison.</summary>
        private sealed class Grid
        {
            public float[] TriMinX;
            public float[] TriMaxX;
            public float[] TriMinZ;
            public float[] TriMaxZ;
            public int[] TriSurfaceId;
            public int[] CellStart;
            public int[] CellTriangles;
            public int[] CellCursor;
            public int[] Oversized;
            public int OversizedCount;
            public float OriginX;
            public float OriginZ;
            public float CellM;
            public int CellsX;
            public int CellsZ;
            public int CellCount;
            public int[] Candidates;
            public bool[] Mark;
            public int TriangleCount;
        }

        private static Grid BuildGrid(Vector3[] vertices, int[] indices, int triangleCount,
            float cellM, float minX, float minZ, float maxX, float maxZ)
        {
            Grid grid = new Grid
            {
                TriMinX = new float[TerrainMeshMath.MaxTriangles],
                TriMaxX = new float[TerrainMeshMath.MaxTriangles],
                TriMinZ = new float[TerrainMeshMath.MaxTriangles],
                TriMaxZ = new float[TerrainMeshMath.MaxTriangles],
                TriSurfaceId = new int[TerrainMeshMath.MaxTriangles],
                CellStart = new int[TerrainGridMath.MaxGridCells + 1],
                CellTriangles = new int[TerrainGridMath.MaxGridReferences],
                CellCursor = new int[TerrainGridMath.MaxGridCells],
                Oversized = new int[TerrainGridMath.MaxOversizedTriangles],
                Candidates = new int[TerrainMeshMath.MaxTriangles],
                Mark = new bool[TerrainMeshMath.MaxTriangles],
                TriangleCount = triangleCount,
                CellM = cellM,
            };
            int[] chunkStarts = { 0 };
            int[] chunkCounts = { triangleCount };
            int[] chunkZones = { 0 };
            Assert.IsTrue(TerrainGridMath.TryBuild(minX, minZ, maxX, maxZ, cellM, vertices,
                indices, triangleCount, chunkStarts, chunkCounts, chunkZones, 1,
                grid.TriMinX, grid.TriMaxX, grid.TriMinZ, grid.TriMaxZ, grid.TriSurfaceId,
                grid.CellStart, grid.CellTriangles, grid.CellCursor, grid.Oversized,
                out grid.OriginX, out grid.OriginZ, out grid.CellsX, out grid.CellsZ,
                out grid.CellCount, out _, out grid.OversizedCount, out _, out _),
                "grid build at " + cellM + " m");
            return grid;
        }

        private static int CollectOn(Grid grid, float x, float z, float halfExtent)
        {
            return TerrainGridMath.CollectCandidates(x, z, halfExtent, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, grid.Oversized, grid.OversizedCount, grid.TriangleCount,
                grid.Candidates, grid.Mark);
        }

        [Test]
        public void TheWindowFilterMakesTheCandidateSetIndependentOfTheCellSize()
        {
            // The same pose must give the same candidates at different cell sizes (a reference
            // level once gave 13 vs 14 and 7 vs 12). The cause was NOT the grid: the collector added the oversized
            // triangles WITHOUT the window filter, so a 1 m grid (5 oversized faces) and a 2 m
            // grid (0 oversized) handed the queries different sets. This case pins the fix:
            // the window filter applies to oversized triangles too, so the candidate set is
            // exactly "triangles whose bbox overlaps the window" whatever the cells are.
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            // A 6 x 6 m patch of 2 x 2 m quads (9 quads = 18 triangles) ...
            int quads = 0;
            for (int zi = 0; zi < 3; zi++)
            {
                for (int xi = 0; xi < 3; xi++)
                {
                    float x0 = -3f + 2f * xi;
                    float z0 = -3f + 2f * zi;
                    Quad(0f, x0, x0 + 2f, z0, z0 + 2f, vertices, indices, quads * 2, quads * 6);
                    quads = quads + 1;
                }
            }
            // ... plus ONE 9 x 9 m quad: 100 cells at 1 m (oversized) but 25 cells at 2 m (in
            // the cells). That single difference is what used to change the candidate set.
            Quad(0f, -4.5f, 4.5f, -3.5f, 5.5f, vertices, indices, quads * 2, quads * 6);
            int triangleCount = (quads + 1) * 2;

            Grid one = BuildGrid(vertices, indices, triangleCount, 1f, -4.5f, -3.5f, 4.5f, 5.5f);
            Grid two = BuildGrid(vertices, indices, triangleCount, 2f, -4.5f, -3.5f, 4.5f, 5.5f);
            Assert.That(one.OversizedCount, Is.EqualTo(2), "the 9 x 9 quad is oversized at 1 m");
            Assert.AreEqual(0, two.OversizedCount, "and fits the cells at 2 m");

            float[] poses = { -3f, -2.5f, -1f, 0f, 0.5f, 2f, 4f };
            float[] windows = { 0.111f, 0.15f, 0.26f };
            int compared = 0;
            for (int pi = 0; pi < poses.Length; pi++)
            {
                for (int pj = 0; pj < poses.Length; pj++)
                {
                    for (int wi = 0; wi < windows.Length; wi++)
                    {
                        int a = CollectOn(one, poses[pi], poses[pj], windows[wi]);
                        int b = CollectOn(two, poses[pi], poses[pj], windows[wi]);
                        int[] setA = new int[a];
                        int[] setB = new int[b];
                        for (int k = 0; k < a; k++) setA[k] = one.Candidates[k];
                        for (int k = 0; k < b; k++) setB[k] = two.Candidates[k];
                        System.Array.Sort(setA);
                        System.Array.Sort(setB);
                        CollectionAssert.AreEqual(setA, setB,
                            "pose (" + poses[pi] + "," + poses[pj] + ") window " + windows[wi] +
                            ": the cell size must not change the candidate set");
                        TerrainGridMath.ClearCandidates(one.Candidates, a, one.Mark);
                        TerrainGridMath.ClearCandidates(two.Candidates, b, two.Mark);
                        compared = compared + 1;
                    }
                }
            }
            Assert.AreEqual(7 * 7 * 3, compared);
        }

        /// <summary>A 6 x 6 m patch of 2 m quads plus one oversized 9 x 9 m quad.</summary>
        private static Grid BuildRegressionGrid(out int triangleCount)
        {
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            int quads = 0;
            for (int zi = 0; zi < 3; zi++)
            {
                for (int xi = 0; xi < 3; xi++)
                {
                    float x0 = -3f + 2f * xi;
                    float z0 = -3f + 2f * zi;
                    Quad(0f, x0, x0 + 2f, z0, z0 + 2f, vertices, indices, quads * 2, quads * 6);
                    quads = quads + 1;
                }
            }
            Quad(0f, -4.5f, 4.5f, -3.5f, 5.5f, vertices, indices, quads * 2, quads * 6);
            triangleCount = (quads + 1) * 2;
            return BuildGrid(vertices, indices, triangleCount, 1f, -4.5f, -3.5f, 4.5f, 5.5f);
        }

        /// <summary>Brute-force cell membership of one triangle (mirrors the build's rule).</summary>
        private static bool TriangleTouchesCell(Grid grid, int triangle, int cell)
        {
            int cellsX = grid.CellsX;
            int i = cell % cellsX;
            int j = cell / cellsX;
            int i0 = TerrainGridMath.CellIndex(grid.TriMinX[triangle] - 1e-6f, grid.OriginX,
                grid.CellM, grid.CellsX);
            int i1 = TerrainGridMath.CellIndex(grid.TriMaxX[triangle] + 1e-6f, grid.OriginX,
                grid.CellM, grid.CellsX);
            int j0 = TerrainGridMath.CellIndex(grid.TriMinZ[triangle] - 1e-6f, grid.OriginZ,
                grid.CellM, grid.CellsZ);
            int j1 = TerrainGridMath.CellIndex(grid.TriMaxZ[triangle] + 1e-6f, grid.OriginZ,
                grid.CellM, grid.CellsZ);
            return i >= i0 && i <= i1 && j >= j0 && j <= j1;
        }

        [Test]
        public void AMixedMeshKeepsTheCsrIntactAndMatchesTheBruteForceCandidates()
        {
            // A MIXED mesh: 3 x 3 m of 1 m quads (18 triangles) plus ONE 12 x 12 m quad
            // (2 triangles) that is oversized at 1 m (13 x 13 = 169 cells > 64). The count
            // pass reserves no room for the oversized quad, so the fill pass MUST skip it
            // too: filling it would write it into every cell it touches, running
            // the cell cursor past its reservation and clobbering the following cells'
            // entries (and the reference table's tail).
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            int quads = 0;
            for (int zi = 0; zi < 3; zi++)
            {
                for (int xi = 0; xi < 3; xi++)
                {
                    float x0 = -1f + 1f * xi;
                    float z0 = -1f + 1f * zi;
                    Quad(0f, x0, x0 + 1f, z0, z0 + 1f, vertices, indices, quads * 2, quads * 6);
                    quads = quads + 1;
                }
            }
            Quad(0f, -6f, 6f, -6f, 6f, vertices, indices, quads * 2, quads * 6);
            int triangleCount = (quads + 1) * 2;

            Grid grid = BuildGrid(vertices, indices, triangleCount, 1f, -6f, -6f, 6f, 6f);
            Assert.AreEqual(2, grid.OversizedCount,
                "the 12 x 12 m quad is the only oversized geometry");
            Assert.IsTrue(grid.Oversized[0] == (quads * 2) && grid.Oversized[1] == (quads * 2 + 1),
                "and the oversized list holds exactly its two triangles, once each");

            // 1) CSR integrity, cell by cell, against a brute force of the same rule. This
            //    catches BOTH a clobbered cursor (wrong entries) and an omission.
            int expectedReferences = 0;
            int[] expectedCell = new int[triangleCount];
            int[] actualCell = new int[triangleCount];
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                int expected = 0;
                for (int t = 0; t < triangleCount; t++)
                {
                    bool oversized = (t == quads * 2 || t == quads * 2 + 1);
                    if (oversized) continue;
                    if (!TriangleTouchesCell(grid, t, cell)) continue;
                    expectedCell[expected] = t;
                    expected = expected + 1;
                }
                expectedReferences = expectedReferences + expected;
                int start = grid.CellStart[cell];
                int end = grid.CellStart[cell + 1];
                int actual = end - start;
                Assert.AreEqual(expected, actual,
                    "cell " + cell + " must hold exactly its non-oversized triangles");
                for (int k = 0; k < actual; k++)
                {
                    actualCell[k] = grid.CellTriangles[start + k];
                }
                System.Array.Sort(expectedCell, 0, expected);
                System.Array.Sort(actualCell, 0, actual);
                for (int k = 0; k < actual; k++)
                {
                    Assert.AreEqual(expectedCell[k], actualCell[k],
                        "cell " + cell + " entry " + k + " (no clobber, no duplicate)");
                }
            }
            // 2) The reference count is exactly what the count pass reserved: a clobbered fill
            //    pass would have written more than this.
            int assigned = 0;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                assigned = assigned + (grid.CellStart[cell + 1] - grid.CellStart[cell]);
            }
            Assert.AreEqual(assigned, expectedReferences);
            // 3) Candidate equivalence on the mixed mesh: the emitted set is EXACTLY the
            //    triangles (oversized included, since they are always offered) whose bbox
            //    overlaps the window.
            float[] centers = { 0f, 0.49f, -1.4f };
            float[] windows = { 0.15f, 0.6f };
            int compared = 0;
            for (int ci = 0; ci < centers.Length; ci++)
            {
                for (int cj = 0; cj < centers.Length; cj++)
                {
                    for (int wi = 0; wi < windows.Length; wi++)
                    {
                        int count = CollectOn(grid, centers[ci], centers[cj], windows[wi]);
                        Assert.That(count, Is.GreaterThanOrEqualTo(0));
                        int[] actualSet = new int[count];
                        for (int k = 0; k < count; k++) actualSet[k] = grid.Candidates[k];
                        System.Array.Sort(actualSet);
                        int expectedCount = 0;
                        int[] expectedSet = new int[triangleCount];
                        for (int t = 0; t < triangleCount; t++)
                        {
                            if (grid.TriMaxX[t] < centers[ci] - windows[wi] - 1e-6f) continue;
                            if (grid.TriMinX[t] > centers[ci] + windows[wi] + 1e-6f) continue;
                            if (grid.TriMaxZ[t] < centers[cj] - windows[wi] - 1e-6f) continue;
                            if (grid.TriMinZ[t] > centers[cj] + windows[wi] + 1e-6f) continue;
                            expectedSet[expectedCount] = t;
                            expectedCount = expectedCount + 1;
                        }
                        System.Array.Sort(expectedSet, 0, expectedCount);
                        Assert.AreEqual(expectedCount, count,
                            "window (" + centers[ci] + "," + centers[cj] + ") " + windows[wi]);
                        for (int k = 0; k < count; k++)
                        {
                            Assert.AreEqual(expectedSet[k], actualSet[k],
                                "candidate " + k + " of the mixed mesh");
                        }
                        TerrainGridMath.ClearCandidates(grid.Candidates, count, grid.Mark);
                        compared = compared + 1;
                    }
                }
            }
            Assert.AreEqual(3 * 3 * 2, compared);
        }

        [Test]
        public void ANullOversizedListIsALegalEmptyList()
        {
            // A caller with no oversized triangles passes null; that must be an EMPTY list,
            // not a NullReferenceException (the collector used to read .Length on it).
            Grid grid = BuildRegressionGrid(out int triangleCount);
            int withNull = TerrainGridMath.CollectCandidates(0.5f, 0.5f, 0.15f, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, null, 0, triangleCount, grid.Candidates, grid.Mark);
            Assert.That(withNull, Is.GreaterThan(0), "the cells still answer");
            int countWithNull = withNull;
            int[] nullSet = new int[countWithNull];
            for (int k = 0; k < countWithNull; k++) nullSet[k] = grid.Candidates[k];
            TerrainGridMath.ClearCandidates(grid.Candidates, countWithNull, grid.Mark);

            int withEmpty = TerrainGridMath.CollectCandidates(0.5f, 0.5f, 0.15f, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, new int[0], 0, triangleCount, grid.Candidates, grid.Mark);
            Assert.AreEqual(countWithNull, withEmpty, "null and empty are the same list");
            System.Array.Sort(nullSet);
            int[] emptySet = new int[withEmpty];
            for (int k = 0; k < withEmpty; k++) emptySet[k] = grid.Candidates[k];
            System.Array.Sort(emptySet);
            CollectionAssert.AreEqual(nullSet, emptySet);
            TerrainGridMath.ClearCandidates(grid.Candidates, withEmpty, grid.Mark);
        }

        [Test]
        public void AShortCandidateBufferFailsExplicitlyAndLeavesNoMarks()
        {
            // A buffer that cannot hold the mesh must be REFUSED (-1 = "use the full scan"),
            // never filled with a partial set: a partial set would change which surface
            // supports the ball. And because the refusal happens before anything is marked,
            // the marks are still clean, so the next collection is unaffected.
            Grid grid = BuildRegressionGrid(out int triangleCount);
            int full = CollectOn(grid, 0.5f, 0.5f, 0.15f);
            Assert.That(full, Is.GreaterThan(1), "the fixture's window must hold several triangles");
            TerrainGridMath.ClearCandidates(grid.Candidates, full, grid.Mark);

            int[] shortBuffer = new int[1];
            int refused = TerrainGridMath.CollectCandidates(0.5f, 0.5f, 0.15f, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, grid.Oversized, grid.OversizedCount, triangleCount, shortBuffer,
                grid.Mark);
            Assert.AreEqual(-1, refused,
                "a buffer smaller than the mesh is an explicit refusal, not a truncation");
            Assert.AreEqual(0, shortBuffer[0], "nothing was written into the short buffer");
            for (int i = 0; i < grid.Mark.Length; i++)
            {
                Assert.IsFalse(grid.Mark[i], "a refused call must leave every mark clear");
            }

            // The clean-state claim, checked the only way that matters: the next call returns
            // the same set as if the refused call had never happened.
            int again = CollectOn(grid, 0.5f, 0.5f, 0.15f);
            Assert.AreEqual(full, again, "the refused call left no stale marks behind");
            TerrainGridMath.ClearCandidates(grid.Candidates, again, grid.Mark);
        }

        [Test]
        public void AnInvalidCallIsRefusedInsteadOfReturningAPartialSet()
        {
            Grid grid = BuildRegressionGrid(out int triangleCount);
            // The oversized count past its own array is a caller bug: refuse, do not clamp.
            Assert.AreEqual(-1, TerrainGridMath.CollectCandidates(0f, 0f, 0.15f, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, new int[0], 3, triangleCount, grid.Candidates, grid.Mark));
            // A triangle count past the mark array cannot be served either.
            Assert.AreEqual(-1, TerrainGridMath.CollectCandidates(0f, 0f, 0.15f, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, grid.Oversized, grid.OversizedCount,
                grid.Mark.Length + 1, grid.Candidates, grid.Mark));
            // A negative oversized count is nonsense, not "none".
            Assert.AreEqual(-1, TerrainGridMath.CollectCandidates(0f, 0f, 0.15f, grid.CellM,
                grid.OriginX, grid.OriginZ, grid.CellsX, grid.CellsZ, grid.CellCount,
                grid.CellStart, grid.CellTriangles, grid.TriMinX, grid.TriMaxX, grid.TriMinZ,
                grid.TriMaxZ, grid.Oversized, -1, triangleCount, grid.Candidates, grid.Mark));
            // And none of those refusals left a mark behind.
            for (int i = 0; i < grid.Mark.Length; i++)
            {
                Assert.IsFalse(grid.Mark[i], "refusals never mark");
            }
        }

        [Test]
        public void CellIndexClampsAndRefusesBadInput()
        {
            Assert.AreEqual(0, TerrainGridMath.CellIndex(-100f, 0f, 1f, 10));
            Assert.AreEqual(9, TerrainGridMath.CellIndex(100f, 0f, 1f, 10));
            Assert.AreEqual(3, TerrainGridMath.CellIndex(3.99f, 0f, 1f, 10));
            Assert.AreEqual(2, TerrainGridMath.CellIndex(2.5f, 0.5f, 1f, 10));
            Assert.AreEqual(0, TerrainGridMath.CellIndex(float.NaN, 0f, 1f, 10));
            Assert.AreEqual(0, TerrainGridMath.CellIndex(1f, 0f, 0f, 10));
            Assert.AreEqual(0, TerrainGridMath.CellIndex(1f, 0f, 1f, 0));
        }

        [Test]
        public void AFootprintOutsideTheMeshHasNoCandidatesButIsNotAnError()
        {
            Assert.IsTrue(TryBuildFixture(2, TerrainGridMath.DefaultCellM, out _, out _,
                out int cellCount, out _, out _, out _, out _));
            Vector3[] vertices = new Vector3[TerrainMeshMath.MaxVertices];
            int[] indices = new int[TerrainMeshMath.MaxTriangles * 3];
            for (int q = 0; q < 2; q++)
            {
                float x0 = -1f + 2f * q;
                Quad(0f, x0, x0 + 2f, -1f, 1f, vertices, indices, q * 2, q * 6);
            }
            int[] chunkStarts = { 0 };
            int[] chunkCounts = { 4 };
            int[] chunkZones = { 0 };
            float[] triMinX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxX = new float[TerrainMeshMath.MaxTriangles];
            float[] triMinZ = new float[TerrainMeshMath.MaxTriangles];
            float[] triMaxZ = new float[TerrainMeshMath.MaxTriangles];
            int[] triSurfaceId = new int[TerrainMeshMath.MaxTriangles];
            int[] cellStart = new int[TerrainGridMath.MaxGridCells + 1];
            int[] cellTriangles = new int[TerrainGridMath.MaxGridReferences];
            int[] cellCursor = new int[TerrainGridMath.MaxGridCells];
            int[] oversizedTriangles = new int[TerrainGridMath.MaxOversizedTriangles];
            Assert.IsTrue(TerrainGridMath.TryBuild(-1f, -1f, 3f, 1f, 1f, vertices, indices, 4,
                chunkStarts, chunkCounts, chunkZones, 1, triMinX, triMaxX, triMinZ, triMaxZ,
                triSurfaceId, cellStart, cellTriangles, cellCursor, oversizedTriangles,
                out float originX, out float originZ, out int cellsX, out int cellsZ,
                out cellCount, out _, out int oversized, out _, out _),
                "the small pad's grid builds");
            int[] candidates = new int[TerrainMeshMath.MaxTriangles];
            bool[] mark = new bool[TerrainMeshMath.MaxTriangles];
            // 10 m away: the cell lookup clamps into the last cell, and the window filter drops
            // every triangle of that cell because none of them can reach a 0.15 m window 10 m
            // away. Exact zero, not "a few".
            int count = TerrainGridMath.CollectCandidates(10f, 10f, 0.15f, 1f, originX, originZ,
                cellsX, cellsZ, cellCount, cellStart, cellTriangles, triMinX, triMaxX,
                triMinZ, triMaxZ, oversizedTriangles, oversized, 4, candidates, mark);
            Assert.AreEqual(0, count, "nothing 10 m away can be a candidate");
            TerrainGridMath.ClearCandidates(candidates, count, mark);
            for (int i = 0; i < mark.Length; i++)
            {
                Assert.IsFalse(mark[i], "every mark is cleared again");
            }
            // A non-finite query is refused, not clamped into a cell.
            Assert.AreEqual(-1, TerrainGridMath.CollectCandidates(float.NaN, 0f, 0.15f, 1f,
                originX, originZ, cellsX, cellsZ, cellCount, cellStart, cellTriangles,
                triMinX, triMaxX, triMinZ, triMaxZ, oversizedTriangles, oversized, 4,
                candidates, mark), "a non-finite query is refused, not clamped");
        }
    }
}
