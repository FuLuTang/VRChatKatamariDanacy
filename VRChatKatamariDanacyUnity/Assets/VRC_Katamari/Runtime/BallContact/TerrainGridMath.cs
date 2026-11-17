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
    /// THE CANDIDATE SET IS THE WINDOW, NOT THE CELL. <see cref="CollectCandidates"/> drops
    /// every triangle whose precomputed XZ AABB misses the caller's window (the ball's
    /// footprint, or the v2 sweep window). Because the cells only decide where a triangle is
    /// OFFERED, and the window decides whether it is KEPT, the resulting list is exactly
    /// "triangles whose bbox overlaps the window" - INDEPENDENT of the cell size, and a
    /// superset of everything any probe or sphere in that window can touch. That is the
    /// property the equivalence tests pin: the same pose must give the same support and the
    /// same penetration whether the cells are 1 m, 2 m, or absent (full scan).
    /// </para>
    /// <para>
    /// OVERSIZED TRIANGLES. A triangle bigger than <see cref="MaxCellsPerTriangle"/> cells is
    /// not stamped into its cells; it goes to a small always-offered list (still window
    /// filtered). A handful of huge terrain faces therefore cannot refuse the grid or explode
    /// the reference table, and a mesh with more such faces than the list holds refuses the
    /// grid explicitly instead of dropping one.
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

        /// <summary>
        /// Most cells a single triangle may span before it is treated as OVERSIZED.
        /// </summary>
        /// <remarks>
        /// A huge triangle (one quad covering the whole level) would otherwise stamp itself
        /// into thousands of cells: 8,192 triangles x 1,600 cells is 13M references. 64 means
        /// "no bigger than 8 x 8 cells", which any reasonably tessellated terrain satisfies.
        /// Oversized triangles are NOT dropped and do NOT refuse the grid: they go to the
        /// always-offered list (see <see cref="MaxOversizedTriangles"/>) and every query
        /// window-filters them like any other. Only overflowing THAT list refuses the grid.
        /// </remarks>
        public const int MaxCellsPerTriangle = 64;

        /// <summary>
        /// Most triangles that may be too big for the cells before the grid refuses.
        /// </summary>
        /// <remarks>
        /// A triangle spanning more than <see cref="MaxCellsPerTriangle"/> cells is not
        /// stamped into them: it goes to a small "always offered" list instead, so a handful
        /// of huge terrain faces (the reference level has 5 at 1 m) cannot blow the reference
        /// cap or force the whole grid to be refused. Overflowing THIS list is what refuses
        /// the grid; the mesh then keeps its collision through the full scan.
        /// </remarks>
        public const int MaxOversizedTriangles = 256;

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
        public const int RefusalOversizedList = 5;

        /// <summary>
        /// Boundary slack of the candidate window, metres. The same 1e-6 the geometry queries
        /// use on their per-triangle bbox, so a triangle that a query still accepts at the
        /// window edge can never be filtered out from under it.
        /// </summary>
        private const float WindowSlack = 1e-6f;

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
            if (refusalCode == RefusalOversizedList)
                return "more oversized triangles than the list holds; tessellate or raise the cell size";
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
            int[] oversizedTriangles,
            out float originX, out float originZ, out int cellsX, out int cellsZ,
            out int cellCount, out int referenceCount, out int oversizedCount,
            out int refusalCode, out int refusalDetail)
        {
            // GEOMETRY OUTS ARE WRITTEN ON SUCCESS ONLY. Every refusal leaves them zeroed, so a
            // caller that ignores the result can never read a half-built grid (a refusal must
            // not leak a cell count). The failure outs are the two
            // refusal values, which are always written.
            originX = 0f;
            originZ = 0f;
            cellsX = 0;
            cellsZ = 0;
            cellCount = 0;
            referenceCount = 0;
            oversizedCount = 0;
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
                cellTriangles == null || cellCursor == null || oversizedTriangles == null ||
                chunkCount < 1)
            {
                refusalCode = RefusalInvalidInput;
                return false;
            }
            // The caller's reference buffer is a hard bound and is checked with the count it
            // must hold, not with the triangle count: a mesh can need more references than
            // triangles (a triangle spanning a cell boundary is referenced twice).
            if (triangleCount < 1 || triangleCount > triangleMinX.Length ||
                triangleCount > triangleMaxX.Length || triangleCount > triangleMinZ.Length ||
                triangleCount > triangleMaxZ.Length ||
                triangleCount > triangleSurfaceId.Length)
            {
                refusalCode = RefusalInvalidInput;
                refusalDetail = triangleCount;
                return false;
            }

            float localOriginX = minX;
            float localOriginZ = minZ;
            float extentX = maxX - minX;
            float extentZ = maxZ - minZ;
            if (!KatamariMath.IsFiniteFloat(extentX) || !KatamariMath.IsFiniteFloat(extentZ))
            {
                refusalCode = RefusalInvalidInput;
                return false;
            }
            // +1 so a coordinate exactly on the far edge still has a cell.
            int localCellsX = Mathf.FloorToInt(extentX / cellM) + 1;
            int localCellsZ = Mathf.FloorToInt(extentZ / cellM) + 1;
            if (localCellsX < 1) localCellsX = 1;
            if (localCellsZ < 1) localCellsZ = 1;
            if (localCellsX > MaxGridCells || localCellsZ > MaxGridCells ||
                localCellsX * localCellsZ > MaxGridCells)
            {
                refusalCode = RefusalCellCount;
                refusalDetail = localCellsX * localCellsZ;
                return false;
            }
            int localCellCount = localCellsX * localCellsZ;
            if (cellStart.Length < localCellCount + 1 ||
                cellCursor.Length < localCellCount)
            {
                refusalCode = RefusalInvalidInput;
                refusalDetail = localCellCount;
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
            for (int k = 0; k <= localCellCount; k++) cellStart[k] = 0;
            int localOversized = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int i0;
                int i1;
                int j0;
                int j1;
                if (IsOversized(triangleMinX[t], triangleMaxX[t], triangleMinZ[t],
                    triangleMaxZ[t], localOriginX, localOriginZ, localCellsX, localCellsZ,
                    cellM, out i0, out i1, out j0, out j1))
                {
                    // A triangle bigger than the cell budget is NOT a reason to refuse the
                    // mesh: it goes to the oversized list, which every query offers and the
                    // window filter still narrows. Stamping it into its 72+ cells instead is
                    // what made the 1 m grid impossible on a dense reference level (refusal code 3,
                    // 72 cells for one triangle) and what would blow the reference cap.
                    if (localOversized >= MaxOversizedTriangles ||
                        localOversized >= oversizedTriangles.Length)
                    {
                        refusalCode = RefusalOversizedList;
                        refusalDetail = localOversized;
                        return false;
                    }
                    oversizedTriangles[localOversized] = t;
                    localOversized = localOversized + 1;
                    continue;
                }
                for (int j = j0; j <= j1; j++)
                {
                    int row = j * localCellsX;
                    for (int i = i0; i <= i1; i++)
                    {
                        cellStart[row + i + 1] = cellStart[row + i + 1] + 1;
                    }
                }
            }
            for (int k = 0; k < localCellCount; k++)
            {
                cellStart[k + 1] = cellStart[k + 1] + cellStart[k];
            }
            int localReferences = cellStart[localCellCount];
            // The caller's reference buffer is a hard bound too: writing past it would be the
            // silent triangle loss this cap exists to prevent.
            if (localReferences > MaxGridReferences ||
                localReferences > cellTriangles.Length)
            {
                refusalCode = RefusalReferences;
                refusalDetail = localReferences;
                return false;
            }

            // Fill pass. It MUST apply the same oversized decision as the count pass: the
            // counts only reserved room for the non-oversized triangles, so writing an
            // oversized one here would run the cell cursor past its reservation, overwrite the
            // next cell's entries and can write past the reference table. Both passes call
            // IsOversized, so they cannot disagree.
            for (int k = 0; k < localCellCount; k++) cellCursor[k] = cellStart[k];
            for (int t = 0; t < triangleCount; t++)
            {
                int i0;
                int i1;
                int j0;
                int j1;
                if (IsOversized(triangleMinX[t], triangleMaxX[t], triangleMinZ[t],
                    triangleMaxZ[t], localOriginX, localOriginZ, localCellsX, localCellsZ,
                    cellM, out i0, out i1, out j0, out j1)) continue;
                for (int j = j0; j <= j1; j++)
                {
                    int row = j * localCellsX;
                    for (int i = i0; i <= i1; i++)
                    {
                        int k = row + i;
                        int at = cellCursor[k];
                        cellTriangles[at] = t;
                        cellCursor[k] = at + 1;
                    }
                }
            }

            originX = localOriginX;
            originZ = localOriginZ;
            cellsX = localCellsX;
            cellsZ = localCellsZ;
            cellCount = localCellCount;
            referenceCount = localReferences;
            oversizedCount = localOversized;
            return true;
        }

        /// <summary>
        /// The deduplicated mesh triangles whose precomputed XZ bbox overlaps the caller's
        /// window (the ball's footprint, or the v2 sweep window).
        /// </summary>
        /// <param name="oversizedTriangles">
        /// The build's oversized list, or null for "none" (a legal empty list).
        /// </param>
        /// <param name="triangleCount">
        /// Triangles in the mesh. The candidate and mark buffers are checked against it
        /// up front, because a buffer that cannot hold the whole mesh cannot be allowed to
        /// return a partial set (see the return contract).
        /// </param>
        /// <returns>
        /// The candidate count (&gt;= 0), or <b>-1 when the call cannot be satisfied</b>:
        /// a required array is null, the candidate buffer or the mark array cannot hold
        /// <paramref name="triangleCount"/> triangles, the oversized list is longer than the
        /// mesh, or the buffer somehow runs out mid-walk. -1 means "no candidate list": the
        /// caller MUST fall back to the full scan (the queries already read a negative count
        /// that way) and must never treat a partial set as success. A failed call leaves
        /// EVERY mark clear, so the next call starts from a clean state.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The candidate set is a SUPERSET of every triangle any probe or sphere in the window
        /// can touch: a probe point inside a triangle puts both in the same cell, the triangle
        /// is registered in every cell its bbox overlaps, and the window filter only drops
        /// triangles whose bbox misses the window entirely. That is what makes the candidate
        /// path and the full scan interchangeable (and the set independent of the cell size).
        /// </para>
        /// <para>
        /// Dedupe is a caller-owned bool per triangle, cleared again by
        /// <see cref="ClearCandidates"/>. A mark is only ever set for a triangle that is
        /// written into <paramref name="candidates"/> in the same step, so the marks and the
        /// list can never disagree: that is why the capacity check comes BEFORE the mark.
        /// </para>
        /// <para>
        /// Production always passes the motor's fixed buffers (candidates and marks sized to
        /// the 8,192-triangle capacity), so the rejection paths are not reached in production. They are
        /// explicit, because "return a partial set" would silently change which surface
        /// supports the ball, and a stale mark would silently remove a triangle from every
        /// later query.
        /// </para>
        /// </remarks>
        public static int CollectCandidates(
            float centerX, float centerZ, float halfExtent,
            float cellM, float originX, float originZ, int cellsX, int cellsZ, int cellCount,
            int[] cellStart, int[] cellTriangles,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ,
            int[] oversizedTriangles, int oversizedCount,
            int triangleCount,
            int[] candidates, bool[] candidateMark)
        {
            if (!KatamariMath.IsFiniteFloat(centerX) || !KatamariMath.IsFiniteFloat(centerZ) ||
                !KatamariMath.IsFiniteFloat(halfExtent) || !KatamariMath.IsFiniteFloat(cellM))
                return -1;
            if (cellM <= 0f || cellsX < 1 || cellsZ < 1 || cellCount < 1) return -1;
            if (cellStart == null || cellTriangles == null || candidates == null ||
                candidateMark == null || triangleMinX == null || triangleMaxX == null ||
                triangleMinZ == null || triangleMaxZ == null) return -1;
            if (triangleCount < 1 || triangleCount > candidateMark.Length ||
                triangleCount > candidates.Length || triangleCount > triangleMinX.Length ||
                triangleCount > triangleMaxX.Length || triangleCount > triangleMinZ.Length ||
                triangleCount > triangleMaxZ.Length) return -1;
            if (oversizedCount < 0) return -1;
            // Null oversized is the legal "no oversized triangles" case; the length is only
            // read when the list exists. A count past its own array is a caller bug, not
            // something to clamp silently.
            int oversized = 0;
            if (oversizedTriangles != null)
            {
                if (oversizedCount > oversizedTriangles.Length) return -1;
                oversized = oversizedCount;
            }
            if (halfExtent < 0f) halfExtent = 0f;

            // THE FOOTPRINT FILTER. The cell lookup only says "these triangles are somewhere
            // in these 1-2 m cells"; a dense cell holds 100-400 of them (measured 114-443
            // candidates on a dense level, i.e. whole cells), and 17 probes then re-walked every one. A
            // triangle that cannot overlap the caller's window cannot contain any probe or
            // touch the sphere, so dropping it here is exact, not a heuristic - and it makes
            // the candidate set independent of the cell size (see the class remarks).
            //
            // The tolerance matches the geometry queries' boundary slack (TerrainMeshMath's
            // Epsilon): a triangle whose bbox touches the window edge to within 1e-6 stays in,
            // so a boundary or skin hit is never filtered out from under the query.
            float lowX = centerX - halfExtent - WindowSlack;
            float highX = centerX + halfExtent + WindowSlack;
            float lowZ = centerZ - halfExtent - WindowSlack;
            float highZ = centerZ + halfExtent + WindowSlack;

            int count = 0;
            int capacity = candidates.Length;

            // Oversized triangles are in no cell, so they are offered to every query; the
            // window filter still applies to them.
            for (int k = 0; k < oversized; k++)
            {
                int triangle = oversizedTriangles[k];
                if (triangle < 0 || triangle >= triangleCount) continue;
                if (candidateMark[triangle]) continue;
                if (!OverlapsWindow(triangle, triangleMinX, triangleMaxX, triangleMinZ,
                    triangleMaxZ, lowX, highX, lowZ, highZ)) continue;
                if (count >= capacity)
                {
                    // Not reached with a buffer sized to the mesh, and never a silent
                    // truncation: undo what this call marked and refuse.
                    ClearCandidates(candidates, count, candidateMark);
                    return -1;
                }
                candidateMark[triangle] = true;
                candidates[count] = triangle;
                count = count + 1;
            }

            int i0 = CellIndex(lowX, originX, cellM, cellsX);
            int i1 = CellIndex(highX, originX, cellM, cellsX);
            int j0 = CellIndex(lowZ, originZ, cellM, cellsZ);
            int j1 = CellIndex(highZ, originZ, cellM, cellsZ);
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
                        if (triangle < 0 || triangle >= triangleCount) continue;
                        if (candidateMark[triangle]) continue;
                        if (!OverlapsWindow(triangle, triangleMinX, triangleMaxX, triangleMinZ,
                            triangleMaxZ, lowX, highX, lowZ, highZ)) continue;
                        if (count >= capacity)
                        {
                            ClearCandidates(candidates, count, candidateMark);
                            return -1;
                        }
                        candidateMark[triangle] = true;
                        candidates[count] = triangle;
                        count = count + 1;
                    }
                }
            }
            return count;
        }

        /// <summary>Does this triangle's precomputed XZ bbox overlap the query window?</summary>
        private static bool OverlapsWindow(int triangle,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ,
            float lowX, float highX, float lowZ, float highZ)
        {
            if (triangleMaxX[triangle] < lowX || triangleMinX[triangle] > highX) return false;
            if (triangleMaxZ[triangle] < lowZ || triangleMinZ[triangle] > highZ) return false;
            return true;
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

        /// <summary>
        /// The ONE oversized decision: does this triangle's XZ bbox span more cells than
        /// <see cref="MaxCellsPerTriangle"/>? Both the count pass and the fill pass of
        /// <see cref="TryBuild"/> call it, so a triangle that the counts left out of the cells
        /// can never be written into them (otherwise the fill pass would run the cell cursor
        /// past its reservation and clobber neighbouring cells).
        /// </summary>
        private static bool IsOversized(float minX, float maxX, float minZ, float maxZ,
            float originX, float originZ, int cellsX, int cellsZ, float cellM,
            out int i0, out int i1, out int j0, out int j1)
        {
            CellRange(minX, maxX, minZ, maxZ, originX, originZ, cellsX, cellsZ, cellM,
                out i0, out i1, out j0, out j1);
            return (i1 - i0 + 1) * (j1 - j0 + 1) > MaxCellsPerTriangle;
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
