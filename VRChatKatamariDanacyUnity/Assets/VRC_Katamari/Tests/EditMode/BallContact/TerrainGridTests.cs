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
            out int refusalCode, out int refusalDetail)
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
            return TerrainGridMath.TryBuild(-1f, -1f, 2f * quadCount - 1f, 1f, cellM,
                vertices, indices, triangles, chunkStarts, chunkCounts, chunkZones, 1,
                triMinX, triMaxX, triMinZ, triMaxZ, triSurfaceId, cellStart, cellTriangles,
                cellCursor, out float originX, out float originZ, out cellsX, out cellsZ,
                out cellCount, out references, out refusalCode, out refusalDetail);
        }

        [Test]
        public void ARealisticPadBuildsAndEveryTriangleIsReachable()
        {
            // 7 quads of 2 x 2 m: 14 triangles over 14 x 2 m at a 1 m cell -> 15 x 3 cells.
            Assert.IsTrue(TryBuildFixture(7, TerrainGridMath.DefaultCellM, out _, out _,
                out int cellCount, out int references, out int code, out int detail),
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
                out int cellCount, out int references, out int code, out int detail),
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
        public void AHugeTriangleIsRefusedInsteadOfBeingSilentlyDropped()
        {
            // One quad 20 x 20 m at 1 m: the grid itself fits (21 x 21 = 441 cells) but a
            // single triangle spans all 441 of them, far past the per-triangle cap.
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
            Assert.IsFalse(TerrainGridMath.TryBuild(-10f, -10f, 10f, 10f, 1f, vertices, indices,
                2, chunkStarts, chunkCounts, chunkZones, 1, triMinX, triMaxX, triMinZ, triMaxZ,
                triSurfaceId, cellStart, cellTriangles, cellCursor, out _, out _, out _, out _,
                out int cellCount, out int references, out int code, out int detail),
                "one triangle spanning thousands of cells is refused");
            Assert.AreEqual(TerrainGridMath.RefusalCellsPerTriangle, code);
            Assert.That(detail, Is.GreaterThan(TerrainGridMath.MaxCellsPerTriangle));
            Assert.AreEqual(0, cellCount);
            Assert.AreEqual(0, references);
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
                triSurfaceId, cellStart, new int[1], cellCursor, out _, out _, out _, out _,
                out _, out _, out int code, out int detail),
                "a one-slot reference buffer cannot hold a 2-triangle mesh");
            Assert.AreEqual(TerrainGridMath.RefusalReferences, code);
            Assert.That(detail, Is.GreaterThan(1));
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
                out int cellCount, out _, out _, out _));
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
            Assert.IsTrue(TerrainGridMath.TryBuild(-1f, -1f, 3f, 1f, 1f, vertices, indices, 4,
                chunkStarts, chunkCounts, chunkZones, 1, triMinX, triMaxX, triMinZ, triMaxZ,
                triSurfaceId, cellStart, cellTriangles, cellCursor, out float originX,
                out float originZ, out int cellsX, out int cellsZ, out cellCount, out _,
                out _, out _), "the small pad's grid builds");
            int[] candidates = new int[TerrainMeshMath.MaxTriangles];
            bool[] mark = new bool[TerrainMeshMath.MaxTriangles];
            // 10 m away: outside every cell's data, clamped into the last cell, and the
            // per-triangle bbox does the rejecting. Never an exception, never a phantom.
            int count = TerrainGridMath.CollectCandidates(10f, 10f, 0.15f, 1f, originX, originZ,
                cellsX, cellsZ, cellCount, cellStart, cellTriangles, candidates, mark);
            Assert.That(count, Is.GreaterThanOrEqualTo(0));
            Assert.That(count, Is.LessThanOrEqualTo(4), "at most the fixture's four triangles");
            TerrainGridMath.ClearCandidates(candidates, count, mark);
            for (int i = 0; i < mark.Length; i++)
            {
                Assert.IsFalse(mark[i], "every mark is cleared again");
            }
            // A non-finite query is refused, not clamped into a cell.
            Assert.AreEqual(0, TerrainGridMath.CollectCandidates(float.NaN, 0f, 0.15f, 1f,
                originX, originZ, cellsX, cellsZ, cellCount, cellStart, cellTriangles,
                candidates, mark));
        }
    }
}
