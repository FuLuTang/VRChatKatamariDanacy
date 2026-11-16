using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// A uniform XZ grid over one baked terrain mesh, plus the per-step candidate collection
    /// that turns "17 probes against every triangle" into "17 probes against a few dozen".
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY. A support query is 17 probes, and the measured 2026-11-16 courtyard run showed
    /// 4,080-8,160 per-triangle iterations per step (17 x ~240-480) with only 65-130 of them
    /// doing real work: the other 96-98% were per-triangle AABB rejects, each costing about a
    /// dozen <c>Mathf.Min/Max</c> extern calls. The grid replaces that per-triangle reject
    /// loop with a cell lookup, so all 17 probes share ONE candidate list per step.
    /// </para>
    /// <para>
    /// STRUCTURE. Built once by <see cref="TryBuild"/> (a load-time call, never in the
    /// frame): a CSR table (cell start offsets + a flat triangle-index array), a per-triangle
    /// XZ AABB stored as four float arrays (so the frame reads four floats instead of
    /// computing eight <c>Mathf.Min/Max</c>), and a per-triangle surface id (the chunk's zone
    /// id, the same <c>2000 + zone</c> the full scan mints). Nothing allocates per step:
    /// <see cref="CollectCandidates"/> writes into caller-owned buffers.
    /// </para>
    /// <para>
    /// CELL SIZE. <see cref="DefaultCellM"/> is 1 m, the unit the v2 document's
    /// <c>terrain.grid</c> describes. A smaller cell prunes more but makes each triangle span
    /// more cells; a larger cell visits fewer cells but keeps more triangles in each. The
    /// build REFUSES a grid that cannot fit the caps instead of silently dropping triangles
    /// (see <see cref="RefusalReason"/>); the caller keeps the mesh and falls back to the
    /// full scan, so collision never depends on the grid.
    /// </para>
    /// <para>
    /// UdonSharp: static methods, caller-owned arrays, no allocation, no static fields.
    /// </para>
    /// </remarks>
    public static class TerrainGridMath
    {
        /// <summary>Most cells one grid may have (cellsX * cellsZ).</summary>
        /// <remarks>
        /// 4,096 at the 1 m default covers a 64 x 64 m terrain; the reference Cancer mesh
        /// (3,456 triangles over roughly 40 x 40 m) is about 1,600. Past this the grid stops
        /// paying for itself and the build refuses.
        /// </remarks>
        public const int MaxGridCells = 4096;

        /// <summary>Most cell-to-triangle references one grid may hold.</summary>
        /// <remarks>
        /// A triangle is referenced once per cell its XZ AABB overlaps, so this bounds the
        /// flat index array (32,768 ints = 128 KB). A mesh that needs more has triangles far
        /// larger than the cell size; the fix is a larger cell size, not a bigger array.
        /// </remarks>
        public const int MaxGridReferences = 32768;

        /// <summary>Most cells a single triangle may span before the build refuses.</summary>
        /// <remarks>
        /// A huge triangle (one quad covering the whole level) would otherwise stamp itself
        /// into thousands of cells: 8,192 triangles x 1,600 cells is 13M references. 64 means
        /// "no bigger than 8 x 8 cells", which any reasonably tessellated terrain satisfies.
        /// </remarks>
        public const int MaxCellsPerTriangle = 64;

        /// <summary>Default cell size, metres (matches the v2 <c>terrain.grid</c> unit).</summary>
        public const float DefaultCellM = 1f;

        public const float MinCellM = 0.25f;
        public const float MaxCellM = 16f;

        // Refusal codes. The MESH is never refused: the caller keeps it and runs the full
        // scan, so a grid refusal is a performance answer, never a collision answer.
        public const int RefusalNone = 0;
        public const int RefusalInvalidInput = 1;
        public const int RefusalCellCount = 2;
        public const int RefusalCellsPerTriangle = 3;
        public const int RefusalReferences = 4;

        /// <summary>Outward slack when a triangle's cell range is derived from its bbox.</summary>
        private const float CellSlack = 1e-6f;

        /// <summary>The one literal per grid refusal code; "" when accepted.</summary>
        /// <remarks>
        /// Literals only (no float formatting, no allocation): a host may print this at setup
        /// cadence. The numeric detail travels separately as an int, which is why the text
        /// does not embed it.
        /// </remarks>
        public static string RefusalReason(int refusalCode)
        {
            if (refusalCode == RefusalNone) return "";
            if (refusalCode == RefusalInvalidInput) return "grid input is not finite or in range";
            if (refusalCode == RefusalCellCount)
                return "grid cells exceed the cap; raise the cell size (terrainGridCellM)";
            if (refusalCode == RefusalCellsPerTriangle)
                return "a triangle spans more cells than the cap; raise the cell size";
            if (refusalCode == RefusalReferences)
                return "grid references exceed the cap; raise the cell size";
            return "unknown grid refusal";
        }

        /// <summary>
        /// Builds the grid and the per-triangle lookup data from a validated mesh.
        /// </summary>
        /// <param name="minX">
        /// Mesh XZ bounds; the caller takes them from the chunk AABBs that
        /// <see cref="TerrainMeshMath.TryBuildMesh"/> already computed.
        /// </param>
        /// <param name="cellM">Requested cell size; refused outside [MinCellM, MaxCellM].</param>
        /// <param name="triangleMinX">
        /// Per-triangle XZ AABB, filled here and then read by every support/penetration query.
        /// Length must be at least <paramref name="triangleCount"/>.
        /// </param>
        /// <param name="cellStart">
        /// CSR offsets, length at least <c>cellCount + 1</c>: cell <c>k</c> owns
        /// <c>cellTriangles[cellStart[k] .. cellStart[k + 1])</c>.
        /// </param>
        /// <param name="cellCursor">
        /// Scratch of length at least <c>cellCount</c> (the fill pass's write cursor). Owned by
        /// the caller so the build allocates nothing.
        /// </param>
        /// <param name="refusalCode">
        /// <see cref="RefusalNone"/> when the grid is usable; otherwise the caller keeps the
        /// mesh, reports the code and uses the full scan.
        /// </param>
        /// <param name="refusalDetail">
        /// The number that broke the cap (cells needed, worst triangle span, references), so a
        /// log line can say "1600 > 4096" without building a string here.
        /// </param>
        public static bool TryBuild(
            float minX, float minZ, float maxX, float maxZ, float cellM,
            Vector3[] vertices, int[] indices, int triangleCount,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds, int chunkCount,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ, int[] triangleSurfaceId,
            int[] cellStart, int[] cellTriangles, int[] cellCursor,
            out float originX, out float originZ, out int cellsX, out int cellsZ,
            out int cellCount, out int referenceCount, out int refusalCode,
            out int refusalDetail)
        {
            originX = 0f;
            originZ = 0f;
            cellsX = 0;
            cellsZ = 0;
            cellCount = 0;
            referenceCount = 0;
            refusalCode = RefusalNone;
            refusalDetail = 0;

            if (!KatamariMath.IsFiniteFloat(minX) || !KatamariMath.IsFiniteFloat(minZ) ||
                !KatamariMath.IsFiniteFloat(maxX) || !KatamariMath.IsFiniteFloat(maxZ) ||
                !KatamariMath.IsFiniteFloat(cellM) || cellM < MinCellM || cellM > MaxCellM ||
                maxX < minX || maxZ < minZ)
            {
                refusalCode = RefusalInvalidInput;
                return false;
            }
            if (vertices == null || indices == null || chunkStarts == null ||
                chunkTriangleCounts == null || chunkZoneIds == null ||
                triangleMinX == null || triangleMaxX == null || triangleMinZ == null ||
                triangleMaxZ == null || triangleSurfaceId == null || cellStart == null ||
                cellTriangles == null || cellCursor == null || chunkCount < 1)
            {
                refusalCode = RefusalInvalidInput;
                return false;
            }
            if (triangleCount < 1 || triangleCount > triangleMinX.Length ||
                triangleCount > triangleMaxX.Length || triangleCount > triangleMinZ.Length ||
                triangleCount > triangleMaxZ.Length ||
                triangleCount > triangleSurfaceId.Length ||
                triangleCount > cellTriangles.Length)
            {
                refusalCode = RefusalInvalidInput;
                refusalDetail = triangleCount;
                return false;
            }

            originX = minX;
            originZ = minZ;
            float extentX = maxX - minX;
            float extentZ = maxZ - minZ;
            if (!KatamariMath.IsFiniteFloat(extentX) || !KatamariMath.IsFiniteFloat(extentZ))
            {
                refusalCode = RefusalInvalidInput;
                return false;
            }
            // +1 so a coordinate exactly on the far edge still has a cell.
            cellsX = Mathf.FloorToInt(extentX / cellM) + 1;
            cellsZ = Mathf.FloorToInt(extentZ / cellM) + 1;
            if (cellsX < 1) cellsX = 1;
            if (cellsZ < 1) cellsZ = 1;
            if (cellsX > MaxGridCells || cellsZ > MaxGridCells ||
                cellsX * cellsZ > MaxGridCells)
            {
                refusalCode = RefusalCellCount;
                refusalDetail = cellsX * cellsZ;
                return false;
            }
            cellCount = cellsX * cellsZ;
            if (cellStart.Length < cellCount + 1 || cellCursor.Length < cellCount)
            {
                refusalCode = RefusalInvalidInput;
                refusalDetail = cellCount;
                return false;
            }

            // Per-triangle XZ AABB + surface id. The surface id is the chunk's zone id, minted
            // exactly as the full scan mints it, so a candidate query can never report a
            // different surface for the same triangle.
            int expectedStart = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                int start = chunkStarts[c];
                int count = chunkTriangleCounts[c];
                int zone = chunkZoneIds[c];
                if (start != expectedStart || count < 1 || start + count > triangleCount ||
                    zone < 0)
                {
                    refusalCode = RefusalInvalidInput;
                    refusalDetail = c;
                    return false;
                }
                int surfaceId = TerrainMeshMath.FirstTerrainSurfaceId + zone;
                for (int t = start; t < start + count; t++)
                {
                    int at = t * 3;
                    Vector3 a = vertices[indices[at]];
                    Vector3 b = vertices[indices[at + 1]];
                    Vector3 d = vertices[indices[at + 2]];
                    if (!KatamariMath.IsFiniteVector(a) || !KatamariMath.IsFiniteVector(b) ||
                        !KatamariMath.IsFiniteVector(d))
                    {
                        refusalCode = RefusalInvalidInput;
                        refusalDetail = t;
                        return false;
                    }
                    triangleMinX[t] = Smallest(a.x, Smallest(b.x, d.x));
                    triangleMaxX[t] = Largest(a.x, Largest(b.x, d.x));
                    triangleMinZ[t] = Smallest(a.z, Smallest(b.z, d.z));
                    triangleMaxZ[t] = Largest(a.z, Largest(b.z, d.z));
                    triangleSurfaceId[t] = surfaceId;
                }
                expectedStart = start + count;
            }
            if (expectedStart != triangleCount)
            {
                refusalCode = RefusalInvalidInput;
                refusalDetail = expectedStart;
                return false;
            }

            // Count pass. cellStart[k + 1] holds cell k's count, then becomes its start.
            for (int k = 0; k <= cellCount; k++) cellStart[k] = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int i0;
                int i1;
                int j0;
                int j1;
                CellRange(triangleMinX[t], triangleMaxX[t], triangleMinZ[t], triangleMaxZ[t],
                    originX, originZ, cellsX, cellsZ, cellM, out i0, out i1, out j0, out j1);
                int span = (i1 - i0 + 1) * (j1 - j0 + 1);
                if (span > MaxCellsPerTriangle)
                {
                    refusalCode = RefusalCellsPerTriangle;
                    refusalDetail = span;
                    return false;
                }
                for (int j = j0; j <= j1; j++)
                {
                    int row = j * cellsX;
                    for (int i = i0; i <= i1; i++)
                    {
                        cellStart[row + i + 1] = cellStart[row + i + 1] + 1;
                    }
                }
            }
            for (int k = 0; k < cellCount; k++)
            {
                cellStart[k + 1] = cellStart[k + 1] + cellStart[k];
            }
            int total = cellStart[cellCount];
            // The caller's reference buffer is a hard bound too: writing past it would be the
            // silent triangle loss this cap exists to prevent.
            if (total > MaxGridReferences || total > cellTriangles.Length)
            {
                refusalCode = RefusalReferences;
                refusalDetail = total;
                return false;
            }

            // Fill pass.
            for (int k = 0; k < cellCount; k++) cellCursor[k] = cellStart[k];
            for (int t = 0; t < triangleCount; t++)
            {
                int i0;
                int i1;
                int j0;
                int j1;
                CellRange(triangleMinX[t], triangleMaxX[t], triangleMinZ[t], triangleMaxZ[t],
                    originX, originZ, cellsX, cellsZ, cellM, out i0, out i1, out j0, out j1);
                for (int j = j0; j <= j1; j++)
                {
                    int row = j * cellsX;
                    for (int i = i0; i <= i1; i++)
                    {
                        int k = row + i;
                        int at = cellCursor[k];
                        cellTriangles[at] = t;
                        cellCursor[k] = at + 1;
                    }
                }
            }

            referenceCount = total;
            return true;
        }

        /// <summary>
        /// The deduplicated triangles whose cells overlap the ball's footprint square.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The candidate set is a SUPERSET of every triangle any probe can be inside: a probe
        /// point inside a triangle puts both in the same cell, and the triangle is registered
        /// in every cell its XZ AABB overlaps, so a candidate query and the full scan see the
        /// same triangles. That is what makes the two paths interchangeable, and what the
        /// cross-check test pins.
        /// </para>
        /// <para>
        /// Dedupe is a caller-owned bool per triangle (cleared again by
        /// <see cref="ClearCandidates"/>, so no stamp wraparound exists to get wrong). The
        /// candidate buffer cannot overflow in practice: it is sized to the whole mesh and a
        /// triangle is written once, which is also why the caps live in <see cref="TryBuild"/>
        /// and not here.
        /// </para>
        /// </remarks>
        public static int CollectCandidates(
            float centerX, float centerZ, float halfExtent,
            float cellM, float originX, float originZ, int cellsX, int cellsZ, int cellCount,
            int[] cellStart, int[] cellTriangles,
            int[] candidates, bool[] candidateMark)
        {
            if (!KatamariMath.IsFiniteFloat(centerX) || !KatamariMath.IsFiniteFloat(centerZ) ||
                !KatamariMath.IsFiniteFloat(halfExtent) || !KatamariMath.IsFiniteFloat(cellM))
                return 0;
            if (cellM <= 0f || cellsX < 1 || cellsZ < 1 || cellCount < 1) return 0;
            if (cellStart == null || cellTriangles == null || candidates == null ||
                candidateMark == null) return 0;
            if (candidateMark.Length < candidates.Length) return 0;
            if (halfExtent < 0f) halfExtent = 0f;

            int i0 = CellIndex(centerX - halfExtent, originX, cellM, cellsX);
            int i1 = CellIndex(centerX + halfExtent, originX, cellM, cellsX);
            int j0 = CellIndex(centerZ - halfExtent, originZ, cellM, cellsZ);
            int j1 = CellIndex(centerZ + halfExtent, originZ, cellM, cellsZ);

            int count = 0;
            int capacity = candidates.Length;
            for (int j = j0; j <= j1; j++)
            {
                int row = j * cellsX;
                for (int i = i0; i <= i1; i++)
                {
                    int cell = row + i;
                    if (cell < 0 || cell >= cellCount) continue;
                    int start = cellStart[cell];
                    int end = cellStart[cell + 1];
                    for (int at = start; at < end; at++)
                    {
                        int triangle = cellTriangles[at];
                        if (triangle < 0 || triangle >= candidateMark.Length) continue;
                        if (candidateMark[triangle]) continue;
                        candidateMark[triangle] = true;
                        if (count >= capacity) return count;
                        candidates[count] = triangle;
                        count = count + 1;
                    }
                }
            }
            return count;
        }

        /// <summary>Clears the dedupe marks a collection set, so the next step starts clean.</summary>
        public static void ClearCandidates(int[] candidates, int candidateCount,
            bool[] candidateMark)
        {
            if (candidates == null || candidateMark == null || candidateCount <= 0) return;
            if (candidateCount > candidates.Length) candidateCount = candidates.Length;
            for (int i = 0; i < candidateCount; i++)
            {
                int triangle = candidates[i];
                if (triangle >= 0 && triangle < candidateMark.Length)
                    candidateMark[triangle] = false;
            }
        }

        /// <summary>Cell index of one coordinate, clamped into [0, cells - 1].</summary>
        public static int CellIndex(float coordinate, float origin, float cellM, int cells)
        {
            if (cells < 1) return 0;
            if (!KatamariMath.IsFiniteFloat(coordinate) || !KatamariMath.IsFiniteFloat(origin) ||
                !KatamariMath.IsFiniteFloat(cellM) || cellM <= 0f) return 0;
            int index = Mathf.FloorToInt((coordinate - origin) / cellM);
            if (index < 0) return 0;
            if (index >= cells) return cells - 1;
            return index;
        }

        private static void CellRange(float minX, float maxX, float minZ, float maxZ,
            float originX, float originZ, int cellsX, int cellsZ, float cellM,
            out int i0, out int i1, out int j0, out int j1)
        {
            // The outward slack matches the support test's bbox tolerance, so a probe that the
            // test accepts as "on the bbox edge" can never land in a cell the triangle was not
            // registered in. Registration and lookup use the same floor() rule otherwise.
            i0 = CellIndex(minX - CellSlack, originX, cellM, cellsX);
            i1 = CellIndex(maxX + CellSlack, originX, cellM, cellsX);
            j0 = CellIndex(minZ - CellSlack, originZ, cellM, cellsZ);
            j1 = CellIndex(maxZ + CellSlack, originZ, cellM, cellsZ);
        }

        private static float Smallest(float a, float b)
        {
            return a < b ? a : b;
        }

        private static float Largest(float a, float b)
        {
            return a > b ? a : b;
        }
    }
}
