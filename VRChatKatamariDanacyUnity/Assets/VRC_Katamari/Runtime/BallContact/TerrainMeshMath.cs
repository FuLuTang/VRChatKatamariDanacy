using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Static, chunked terrain mesh queries for the kinematic motor: support under the
    /// ball's footprint and sphere-vs-mesh penetration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the T0/T1 terrain source of <c>LEVEL_FORMAT_V2</c> §13. The mesh is a
    /// caller-owned triangle soup (no statics here, UdonSharp has no static fields); the
    /// motor owns the fixed arrays and <see cref="TryBuildMesh"/> copies and validates the
    /// writer's arrays once. Triangles are grouped into chunks of at most
    /// <see cref="MaxTrianglesPerChunk"/>; a per-chunk AABB is the broad phase and a
    /// per-triangle AABB the fast reject.
    /// </para>
    /// <para>
    /// Support is a vertical ray at a probe: the highest upward-facing triangle whose XZ
    /// projection contains the probe and whose plane height is at or below the caller's
    /// ceiling. The normal is the triangle's face normal, flipped to +Y for a double-sided
    /// floor. Push-out is single-sided: only the front side of a triangle (the side its
    /// winding normal points to) pushes the sphere out, so standing under a roof does not
    /// lift the ball. Degenerate/zero-area triangles are skipped.
    /// </para>
    /// <para>
    /// <see cref="TrySelectSupport"/> is the whole T0 answer for one ball: the probe set is
    /// the motor's radial directions projected to XZ and scaled to the footprint, plus the
    /// ball's own XZ, and the selected plane is a regular <c>(normal, offset)</c> pair,
    /// exactly like a plane/box/ramp support. The band
    /// it applies is anchored on the BALL'S BOTTOM (upward only, <c>0.25 * D</c>); see that
    /// method for why there is no downward bound.
    /// </para>
    /// <para>
    /// Capacities: 8,192 triangles / 16,384 vertices / 64 chunks / 512 triangles per chunk.
    /// A 3,456-triangle Cancer mesh is 7 full chunks; a query near one chunk tests one
    /// chunk's triangles after the chunk AABB reject. No allocation happens in any query.
    /// </para>
    /// </remarks>
    public static class TerrainMeshMath
    {
        public const int MaxTriangles = 8192;
        public const int MaxVertices = 16384;
        public const int MaxChunks = 64;
        public const int MaxTrianglesPerChunk = 512;
        public const int MaxProbes = 16;

        /// <summary>
        /// Terrain surface ids start here, clear of the support table's other ranges: ground
        /// 0, static box (1 + i, 1..64), prop (100 + i, 100..105) and ramp
        /// (<c>SupportSurfaceMath.FirstRampSurfaceId</c> + i, 500..503). The id is the
        /// chunk's zone id, so a seam between two chunks in the same zone keeps one support
        /// id.
        /// </summary>
        public const int FirstTerrainSurfaceId = 2000;

        /// <summary>True when <paramref name="surfaceId"/> names a terrain chunk/zone.</summary>
        /// <remarks>
        /// The one range test, so a reader that classifies a selected surface (the motor's
        /// support-kind getter) cannot drift from where the ids are minted.
        /// </remarks>
        public static bool IsTerrainSurfaceId(int surfaceId)
        {
            return surfaceId >= FirstTerrainSurfaceId;
        }

        /// <summary>A support triangle must face up by at least this much.</summary>
        public const float MinUpwardNormalY = 0.05f;

        private const float Epsilon = 1e-6f;

        /// <summary>
        /// Copies and validates the writer's arrays, then computes every chunk AABB.
        /// Chunks must be contiguous and cover the whole index buffer once; each chunk is
        /// 1..512 triangles. The output arrays are the motor's fixed buffers.
        /// </summary>
        public static bool TryBuildMesh(
            Vector3[] sourceVertices, int vertexCount,
            int[] sourceIndices, int indexCount,
            int[] sourceChunkStarts, int[] sourceChunkTriangleCounts,
            int[] sourceChunkZoneIds, int chunkCount,
            Vector3[] vertices, int[] indices,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds,
            Vector3[] chunkMin, Vector3[] chunkMax,
            out int triangleCount, out int chunkOut)
        {
            triangleCount = 0;
            chunkOut = 0;
            if (sourceVertices == null || sourceIndices == null ||
                sourceChunkStarts == null || sourceChunkTriangleCounts == null ||
                sourceChunkZoneIds == null) return false;
            if (vertices == null || indices == null || chunkStarts == null ||
                chunkTriangleCounts == null || chunkZoneIds == null || chunkMin == null ||
                chunkMax == null) return false;

            if (vertexCount < 1 || vertexCount > MaxVertices) return false;
            if (indexCount < 3 || indexCount > MaxTriangles * 3 || indexCount % 3 != 0)
                return false;
            int triangles = indexCount / 3;
            if (chunkCount < 1 || chunkCount > MaxChunks) return false;
            if (sourceChunkStarts.Length < chunkCount ||
                sourceChunkTriangleCounts.Length < chunkCount ||
                sourceChunkZoneIds.Length < chunkCount) return false;
            if (vertices.Length < vertexCount || indices.Length < indexCount ||
                chunkStarts.Length < chunkCount || chunkTriangleCounts.Length < chunkCount ||
                chunkZoneIds.Length < chunkCount || chunkMin.Length < chunkCount ||
                chunkMax.Length < chunkCount) return false;

            int expectedStart = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                int start = sourceChunkStarts[c];
                int count = sourceChunkTriangleCounts[c];
                int zone = sourceChunkZoneIds[c];
                if (start != expectedStart) return false;
                if (count < 1 || count > MaxTrianglesPerChunk) return false;
                if (start < 0 || start + count > triangles) return false;
                if (zone < 0) return false;
                chunkStarts[c] = start;
                chunkTriangleCounts[c] = count;
                chunkZoneIds[c] = zone;
                chunkMin[c] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                chunkMax[c] = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
                expectedStart += count;
            }
            if (expectedStart != triangles) return false;

            for (int v = 0; v < vertexCount; v++)
            {
                if (!KatamariMath.IsFiniteVector(sourceVertices[v])) return false;
                vertices[v] = sourceVertices[v];
            }
            for (int i = 0; i < indexCount; i++)
            {
                int index = sourceIndices[i];
                if (index < 0 || index >= vertexCount) return false;
                indices[i] = index;
            }

            for (int c = 0; c < chunkCount; c++)
            {
                int start = chunkStarts[c];
                int end = start + chunkTriangleCounts[c];
                Vector3 minimum = chunkMin[c];
                Vector3 maximum = chunkMax[c];
                for (int t = start; t < end; t++)
                {
                    int at = t * 3;
                    Vector3 a = vertices[indices[at]];
                    Vector3 b = vertices[indices[at + 1]];
                    Vector3 d = vertices[indices[at + 2]];
                    IncludePoint(a, ref minimum, ref maximum);
                    IncludePoint(b, ref minimum, ref maximum);
                    IncludePoint(d, ref minimum, ref maximum);
                }
                if (minimum.x == float.MaxValue) return false;
                chunkMin[c] = minimum;
                chunkMax[c] = maximum;
            }

            triangleCount = triangles;
            chunkOut = chunkCount;
            return true;
        }

        /// <summary>
        /// Highest upward-facing triangle under <paramref name="probe"/> in XZ, with its
        /// plane height at or below <paramref name="maxHeight"/>. The returned point is
        /// on the triangle's plane at the probe's XZ; the normal is unit and +Y.
        /// </summary>
        public static bool TrySupportProbe(
            Vector3 probe,
            float maxHeight,
            Vector3[] vertices, int[] indices, int triangleCount,
            Vector3[] chunkMin, Vector3[] chunkMax,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds,
            int chunkCount,
            out Vector3 point, out Vector3 normal, out int surfaceId)
        {
            int discardedTriangles = 0;
            int discardedEvaluations = 0;
            return TrySupportProbeCounted(probe, maxHeight, vertices, indices, triangleCount,
                chunkMin, chunkMax, chunkStarts, chunkTriangleCounts, chunkZoneIds, chunkCount,
                ref discardedTriangles, ref discardedEvaluations, out point, out normal,
                out surfaceId);
        }

        /// <summary>
        /// <see cref="TrySupportProbe"/> plus the two work counters the motor's cost
        /// diagnostic needs.
        /// </summary>
        /// <remarks>
        /// ONE body: <see cref="TrySupportProbe"/> delegates here with two throw-away
        /// counters, so the query and the count can never drift. The counters are
        /// instructions, not measurements:
        /// <paramref name="trianglesTested"/> counts every triangle that survives the chunk
        /// AABB filter (i.e. every per-triangle iteration), and
        /// <paramref name="fullEvals"/> counts the ones that also survive the per-triangle
        /// XZ AABB and therefore pay the cross product, the plane height and the barycentric
        /// test. That split is what tells a reader whether a slow step is "too many
        /// triangles" or "too much work per triangle".
        /// </remarks>
        public static bool TrySupportProbeCounted(
            Vector3 probe,
            float maxHeight,
            Vector3[] vertices, int[] indices, int triangleCount,
            Vector3[] chunkMin, Vector3[] chunkMax,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds,
            int chunkCount,
            ref int trianglesTested, ref int fullEvals,
            out Vector3 point, out Vector3 normal, out int surfaceId)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            surfaceId = -1;
            if (!KatamariMath.IsFiniteVector(probe)) return false;
            if (!KatamariMath.IsFiniteFloat(maxHeight)) return false;
            if (vertices == null || indices == null || chunkMin == null || chunkMax == null ||
                chunkStarts == null || chunkTriangleCounts == null || chunkZoneIds == null)
                return false;
            if (triangleCount <= 0 || chunkCount <= 0) return false;

            bool found = false;
            float bestHeight = -float.MaxValue;
            for (int c = 0; c < chunkCount; c++)
            {
                Vector3 minimum = chunkMin[c];
                Vector3 maximum = chunkMax[c];
                if (probe.x < minimum.x - Epsilon || probe.x > maximum.x + Epsilon) continue;
                if (probe.z < minimum.z - Epsilon || probe.z > maximum.z + Epsilon) continue;
                if (minimum.y > maxHeight + Epsilon) continue;

                int start = chunkStarts[c];
                int end = start + chunkTriangleCounts[c];
                int zone = chunkZoneIds[c];
                int chunkSurfaceId = FirstTerrainSurfaceId + (zone >= 0 ? zone : c);
                for (int t = start; t < end; t++)
                {
                    trianglesTested = trianglesTested + 1;
                    int at = t * 3;
                    Vector3 a = vertices[indices[at]];
                    Vector3 b = vertices[indices[at + 1]];
                    Vector3 d = vertices[indices[at + 2]];
                    if (probe.x < Mathf.Min(a.x, Mathf.Min(b.x, d.x)) - Epsilon ||
                        probe.x > Mathf.Max(a.x, Mathf.Max(b.x, d.x)) + Epsilon) continue;
                    if (probe.z < Mathf.Min(a.z, Mathf.Min(b.z, d.z)) - Epsilon ||
                        probe.z > Mathf.Max(a.z, Mathf.Max(b.z, d.z)) + Epsilon) continue;
                    fullEvals = fullEvals + 1;

                    float height;
                    Vector3 face;
                    if (!TryTrianglePlane(t, probe, maxHeight, vertices, indices, out height,
                        out face)) continue;
                    if (height <= bestHeight) continue;

                    bestHeight = height;
                    point = new Vector3(probe.x, height, probe.z);
                    normal = face;
                    surfaceId = chunkSurfaceId;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// <see cref="TrySupportProbe"/> against ONE pre-collected candidate list, using the
        /// per-triangle XZ AABB the grid precomputed.
        /// </summary>
        /// <remarks>
        /// This is the per-step fast path: the caller collects the triangles of the cells the
        /// ball's footprint overlaps (TerrainGridMath.CollectCandidates) once, then all 17
        /// probes walk that same short list. The geometry is
        /// <see cref="TryTrianglePlane"/> - the SAME body the full scan calls - so a candidate
        /// query and a full scan cannot disagree about whether a triangle supports the ball;
        /// only the set of triangles each one looks at differs, and the grid is built so that
        /// the candidate set is a superset of every triangle a probe can be inside.
        /// </remarks>
        public static bool TrySupportProbeInCandidates(
            Vector3 probe,
            float maxHeight,
            int[] candidates, int candidateCount,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ, int[] triangleSurfaceId,
            Vector3[] vertices, int[] indices,
            ref int trianglesTested, ref int fullEvals,
            out Vector3 point, out Vector3 normal, out int surfaceId)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            surfaceId = -1;
            if (!KatamariMath.IsFiniteVector(probe)) return false;
            if (!KatamariMath.IsFiniteFloat(maxHeight)) return false;
            if (candidates == null || triangleMinX == null || triangleMaxX == null ||
                triangleMinZ == null || triangleMaxZ == null || triangleSurfaceId == null ||
                vertices == null || indices == null) return false;
            if (candidateCount <= 0 || candidateCount > candidates.Length) return false;
            if (candidateCount > triangleMinX.Length || candidateCount > triangleMaxX.Length ||
                candidateCount > triangleMinZ.Length || candidateCount > triangleMaxZ.Length ||
                candidateCount > triangleSurfaceId.Length) return false;

            bool found = false;
            float bestHeight = -float.MaxValue;
            for (int k = 0; k < candidateCount; k++)
            {
                int t = candidates[k];
                if (t < 0 || t >= triangleMinX.Length) continue;
                trianglesTested = trianglesTested + 1;
                if (probe.x < triangleMinX[t] - Epsilon ||
                    probe.x > triangleMaxX[t] + Epsilon) continue;
                if (probe.z < triangleMinZ[t] - Epsilon ||
                    probe.z > triangleMaxZ[t] + Epsilon) continue;
                fullEvals = fullEvals + 1;

                float height;
                Vector3 face;
                if (!TryTrianglePlane(t, probe, maxHeight, vertices, indices, out height,
                    out face)) continue;
                if (height <= bestHeight) continue;

                bestHeight = height;
                point = new Vector3(probe.x, height, probe.z);
                normal = face;
                surfaceId = triangleSurfaceId[t];
                found = true;
            }
            return found;
        }

        /// <summary>
        /// The ONE support test for one triangle: upward-facing, plane height under the probe
        /// inside the ceiling, and the probe inside the XZ projection.
        /// </summary>
        /// <remarks>
        /// Both probe paths call this, so the support rule exists once. The bounding-box
        /// reject is deliberately the CALLER's: the full scan computes it from the vertices
        /// (12 <c>Mathf.Min/Max</c> calls), the candidate path reads the four floats the grid
        /// precomputed. That difference is the whole optimisation; nothing in here changes.
        /// </remarks>
        private static bool TryTrianglePlane(int triangle, Vector3 probe, float maxHeight,
            Vector3[] vertices, int[] indices, out float height, out Vector3 normal)
        {
            height = 0f;
            normal = Vector3.zero;
            int at = triangle * 3;
            Vector3 a = vertices[indices[at]];
            Vector3 b = vertices[indices[at + 1]];
            Vector3 d = vertices[indices[at + 2]];
            Vector3 raw = Cross(b - a, d - a);
            float length = raw.magnitude;
            if (!KatamariMath.IsFiniteFloat(length) || length <= Epsilon) return false;
            Vector3 face = raw * (1f / length);
            if (face.y < 0f) face = face * -1f;
            if (face.y <= MinUpwardNormalY) return false;

            float planeOffset = Dot(face, a);
            float candidateHeight = (planeOffset - face.x * probe.x - face.z * probe.z) /
                face.y;
            if (!KatamariMath.IsFiniteFloat(candidateHeight) ||
                candidateHeight > maxHeight + Epsilon) return false;
            if (!PointInTriangleXZ(probe, a, b, d)) return false;
            height = candidateHeight;
            normal = face;
            return true;
        }

        /// <summary>
        /// The T0 support answer for one ball: the highest upward triangle under the ball's
        /// footprint that is at or below the ball's step ceiling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// GEOMETRY. Every direction in <paramref name="localDirections"/> is rotated to
        /// world (the motor passes its radial-proxy set and the visual roll rotation),
        /// projected to XZ and scaled to <paramref name="ringRadius"/>, and the ball's own XZ
        /// is probed last, so a flat floor under a stationary ball works even when every ring
        /// direction misses. Each probe asks <see cref="TrySupportProbe"/> for its highest
        /// triangle at or below the ceiling; the answer is then re-measured on that
        /// triangle's PLANE at the ball's XZ, because a slope's height 0.9 r away is not its
        /// height under the centre. The highest of those centre heights wins.
        /// </para>
        /// <para>
        /// THE BAND IS ANCHORED ON THE BALL'S BOTTOM (<paramref name="ballBottomY"/>), never
        /// on the previous support and never on the arena ground plane
        /// (<c>LEVEL_FORMAT_V2</c> §1.3 rule 3, §3.6). Upward it is exactly
        /// <paramref name="stepLimitM"/>: that is the step gate, and it is deliberately the
        /// shared <see cref="SupportSurfaceMath.IsStepWithinLimit"/>.
        /// Downward there is NO bound at all: a gully, a pit or a tunnel floor any distance
        /// below the ball is a support, and a real fall is the level's kill plane's decision
        /// rather than a band's. A triangle above the ceiling is inside the footprint but too
        /// tall to be a step, so it is a wall (T1 pushes the ball out of it), not a support.
        /// </para>
        /// <para>
        /// The ball's bottom can be below the mesh (a spawn inside geometry, a ball that fell
        /// through a seam): then every triangle is above the ceiling and this returns false.
        /// The caller must NOT read that as "no support anywhere" - the arena ground plane is
        /// still the fallback, which is what <c>ThereIsNoSupportPlaneWhereTheMeshAnswers</c>
        /// in the support model requires.
        /// </para>
        /// <para>
        /// No allocation: the probe list is caller-owned and the loop reads only the fixed
        /// buffers. Cost is at most <c>(probeCount) * trianglesInHitChunks</c> triangle tests,
        /// one more probe than the old ring-only query (the ball's own XZ is no longer a
        /// fallback that runs only on a miss), PLUS a linear scan of every chunk per probe:
        /// <see cref="TrySupportProbeCounted"/>'s chunk filter is an O(chunkCount) XZ-AABB
        /// scan, not a spatial index. That is why chunking a mesh into more, smaller chunks
        /// helps only while the triangle work it removes exceeds the chunk scans it adds, and
        /// why the optimum is tens of triangles per chunk rather than one.
        /// </para>
        /// <para>
        /// <paramref name="trianglesTested"/>/<paramref name="fullEvals"/> are work counters
        /// for the motor's cost diagnostic (see
        /// <see cref="TrySupportProbeCounted"/>); nothing here branches on them.
        /// </para>
        /// </remarks>
        public static bool TrySelectSupport(
            Vector3 center, float ballBottomY, float stepLimitM,
            Vector3[] localDirections, Quaternion localToWorld, float ringRadius,
            Vector3[] vertices, int[] indices, int triangleCount,
            Vector3[] chunkMin, Vector3[] chunkMax,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds, int chunkCount,
            int[] candidates, int candidateCount,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ, int[] triangleSurfaceId,
            ref int trianglesTested, ref int fullEvals,
            out Vector3 normal, out float height, out int surfaceId)
        {
            normal = Vector3.zero;
            height = 0f;
            surfaceId = -1;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteFloat(ballBottomY)) return false;
            if (!KatamariMath.IsFiniteFloat(stepLimitM) || stepLimitM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(ringRadius) || ringRadius < 0f) return false;
            if (triangleCount <= 0) return false;
            float ceiling = ballBottomY + stepLimitM;
            if (!KatamariMath.IsFiniteFloat(ceiling)) return false;

            int directionCount = localDirections == null ? 0 : localDirections.Length;
            if (directionCount > MaxProbes) directionCount = MaxProbes;
            int probeCount = directionCount + 1;
            bool found = false;
            float bestHeight = -float.MaxValue;
            for (int i = 0; i < probeCount; i++)
            {
                Vector3 probe = center;
                if (i < directionCount)
                {
                    Vector3 direction = localToWorld * localDirections[i];
                    float horizontal = Mathf.Sqrt(direction.x * direction.x +
                        direction.z * direction.z);
                    if (!KatamariMath.IsFiniteFloat(horizontal) || horizontal <= 1e-4f) continue;
                    probe = new Vector3(center.x + direction.x / horizontal * ringRadius,
                        center.y, center.z + direction.z / horizontal * ringRadius);
                }
                Vector3 candidatePoint;
                Vector3 candidateNormal;
                int candidateId;
                bool probeFound;
                if (candidateCount >= 0)
                {
                    probeFound = TrySupportProbeInCandidates(probe, ceiling, candidates,
                        candidateCount, triangleMinX, triangleMaxX, triangleMinZ,
                        triangleMaxZ, triangleSurfaceId, vertices, indices,
                        ref trianglesTested, ref fullEvals, out candidatePoint,
                        out candidateNormal, out candidateId);
                }
                else
                {
                    probeFound = TrySupportProbeCounted(probe, ceiling, vertices, indices,
                        triangleCount, chunkMin, chunkMax, chunkStarts, chunkTriangleCounts,
                        chunkZoneIds, chunkCount, ref trianglesTested, ref fullEvals,
                        out candidatePoint, out candidateNormal, out candidateId);
                }
                if (!probeFound) continue;
                // The triangle's plane at the BALL's XZ, not at the probe's: a slope tilts
                // under the centre, and the contact solve works on the centre.
                float centreHeight = (Dot(candidateNormal, candidatePoint) -
                    candidateNormal.x * center.x - candidateNormal.z * center.z) /
                    candidateNormal.y;
                if (!KatamariMath.IsFiniteFloat(centreHeight)) continue;
                if (!SupportSurfaceMath.IsStepWithinLimit(centreHeight, ballBottomY,
                    stepLimitM)) continue;
                if (found && centreHeight <= bestHeight) continue;
                found = true;
                bestHeight = centreHeight;
                height = centreHeight;
                normal = candidateNormal;
                surfaceId = candidateId;
            }
            return found;
        }

        /// <summary>
        /// Deepest sphere-vs-triangle penetration, single-sided for push-out. Returns the
        /// closest point on the triangle, the outward normal and a positive depth.
        /// </summary>
        public static bool TryDeepestPenetration(
            Vector3 center, float radius,
            Vector3[] vertices, int[] indices, int triangleCount,
            Vector3[] chunkMin, Vector3[] chunkMax,
            int[] chunkStarts, int[] chunkTriangleCounts,
            int chunkCount,
            int[] candidates, int candidateCount,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ,
            out Vector3 point, out Vector3 normal, out float depth)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            depth = 0f;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
            if (vertices == null || indices == null) return false;

            float bestDepth = 0f;
            bool found = false;
            Vector3 minimum = new Vector3(center.x - radius, center.y - radius,
                center.z - radius);
            Vector3 maximum = new Vector3(center.x + radius, center.y + radius,
                center.z + radius);

            if (candidateCount >= 0)
            {
                // The per-step fast path: the same candidate list the support query used, so
                // T1 and T0 look at one set of triangles instead of two scans.
                if (candidates == null || triangleMinX == null || triangleMaxX == null ||
                    triangleMinZ == null || triangleMaxZ == null) return false;
                if (candidateCount > candidates.Length ||
                    candidateCount > triangleMinX.Length ||
                    candidateCount > triangleMaxX.Length ||
                    candidateCount > triangleMinZ.Length ||
                    candidateCount > triangleMaxZ.Length) return false;
                for (int k = 0; k < candidateCount; k++)
                {
                    int t = candidates[k];
                    if (t < 0 || t >= triangleMinX.Length) continue;
                    if (maximum.x < triangleMinX[t] || minimum.x > triangleMaxX[t]) continue;
                    if (maximum.z < triangleMinZ[t] || minimum.z > triangleMaxZ[t]) continue;
                    Vector3 closest;
                    Vector3 outward;
                    float candidateDepth;
                    if (!TryTrianglePenetration(t, center, radius, vertices, indices, 0f,
                        out closest, out outward, out candidateDepth)) continue;
                    if (!found || candidateDepth > bestDepth)
                    {
                        found = true;
                        bestDepth = candidateDepth;
                        point = closest;
                        normal = outward;
                    }
                }
            }
            else
            {
                if (chunkMin == null || chunkMax == null || chunkStarts == null ||
                    chunkTriangleCounts == null) return false;
                if (triangleCount <= 0 || chunkCount <= 0) return false;
                for (int c = 0; c < chunkCount; c++)
                {
                    if (chunkMax[c].x < minimum.x || chunkMin[c].x > maximum.x) continue;
                    if (chunkMax[c].y < minimum.y || chunkMin[c].y > maximum.y) continue;
                    if (chunkMax[c].z < minimum.z || chunkMin[c].z > maximum.z) continue;

                    int start = chunkStarts[c];
                    int end = start + chunkTriangleCounts[c];
                    for (int t = start; t < end; t++)
                    {
                        int at = t * 3;
                        Vector3 a = vertices[indices[at]];
                        Vector3 b = vertices[indices[at + 1]];
                        Vector3 d = vertices[indices[at + 2]];
                        if (maximum.x < Mathf.Min(a.x, Mathf.Min(b.x, d.x)) ||
                            minimum.x > Mathf.Max(a.x, Mathf.Max(b.x, d.x))) continue;
                        if (maximum.y < Mathf.Min(a.y, Mathf.Min(b.y, d.y)) ||
                            minimum.y > Mathf.Max(a.y, Mathf.Max(b.y, d.y))) continue;
                        if (maximum.z < Mathf.Min(a.z, Mathf.Min(b.z, d.z)) ||
                            minimum.z > Mathf.Max(a.z, Mathf.Max(b.z, d.z))) continue;
                        Vector3 closest;
                        Vector3 outward;
                        float candidateDepth;
                        if (!TryTrianglePenetration(t, center, radius, vertices, indices, 0f,
                            out closest, out outward, out candidateDepth)) continue;
                        if (!found || candidateDepth > bestDepth)
                        {
                            found = true;
                            bestDepth = candidateDepth;
                            point = closest;
                            normal = outward;
                        }
                    }
                }
            }

            if (!found) return false;
            depth = bestDepth;
            return true;
        }

        /// <summary>
        /// The ONE sphere-vs-triangle push-out test for one triangle: single-sided (a query
        /// behind the winding normal is outside the solid), nearest point on the triangle, and
        /// a positive depth when the sphere reaches it.
        /// </summary>
        /// <remarks>
        /// Both the chunk scan and the candidate path call this, so the push-out rule exists
        /// once. The bounding-box rejects stay with the CALLER: the chunk scan computes them
        /// from the vertices, the candidate path reads the grid's precomputed XZ floats, and
        /// the candidate path relies on this body's distance test for the Y axis.
        /// </remarks>
        private static bool TryTrianglePenetration(int triangle, Vector3 center, float radius,
            Vector3[] vertices, int[] indices, float skin,
            out Vector3 point, out Vector3 normal, out float depth)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            depth = 0f;
            if (!KatamariMath.IsFiniteFloat(skin) || skin < 0f) return false;
            int at = triangle * 3;
            Vector3 a = vertices[indices[at]];
            Vector3 b = vertices[indices[at + 1]];
            Vector3 d = vertices[indices[at + 2]];
            Vector3 raw = Cross(b - a, d - a);
            float length = raw.magnitude;
            if (!KatamariMath.IsFiniteFloat(length) || length <= Epsilon) return false;
            Vector3 face = raw * (1f / length);
            // Single-sided: a query behind the winding normal is outside the solid.
            if (Dot(face, center - a) < -Epsilon) return false;

            Vector3 closest;
            if (!TryClosestPointOnTriangle(center, a, b, d, out closest)) return false;
            Vector3 delta = center - closest;
            float distance = delta.magnitude;
            // skin = 0 is the push-out-only test (T1). The v2 collector passes the solver's
            // contact skin, so a resting surface registers with a NEGATIVE depth instead of
            // vanishing - "near" is a contact for support, never for push-out.
            if (!KatamariMath.IsFiniteFloat(distance) || distance >= radius + skin) return false;
            Vector3 outward;
            if (distance > Epsilon) outward = delta * (1f / distance);
            else outward = face;
            if (Dot(outward, face) < 0f) outward = face;
            point = closest;
            normal = outward;
            depth = radius - distance;
            return true;
        }

        /// <summary>
        /// v2 contact source: appends one contact per terrain triangle the sphere touches
        /// or is within <paramref name="skin"/> of, to the solver's parallel contact arrays.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is <see cref="TryDeepestPenetration"/>'s body, not a second one: every
        /// triangle goes through <see cref="TryTrianglePenetration"/> (the same nearest-point
        /// and single-sided rule), the per-triangle XZ reject reuses the grid's precomputed
        /// floats (or the vertices when the grid is not built), and the surface id comes from
        /// the grid's per-triangle table (<c>2000+zone</c>). "Deepest" and "all of them" are
        /// two readings of one query, so the solver and T1 can never disagree about whether
        /// the ball touches a triangle.
        /// </para>
        /// <para>
        /// <paramref name="contactTopY"/> gets the triangle's highest vertex: that is the
        /// ledge the climb's exit compares the ball's bottom against (TERRAIN_CLIMB 6.3).
        /// Contacts are appended in candidate order; the solver's capacity is the bound, and
        /// running past it returns false instead of dropping geometry, the same fail-closed
        /// shape the OBB collection uses.
        /// </para>
        /// </remarks>
        public static bool TryAppendContacts(
            Vector3 center, float radius, float skin,
            int[] candidates, int candidateCount,
            float[] triangleMinX, float[] triangleMaxX, float[] triangleMinZ,
            float[] triangleMaxZ, int[] triangleSurfaceId,
            Vector3[] vertices, int[] indices, int triangleCount,
            Vector3[] chunkMin, Vector3[] chunkMax,
            int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds, int chunkCount,
            Vector3[] contactNormals, Vector3[] contactPoints, float[] contactDepths,
            float[] contactAngles, int[] contactSurfaceIds, int[] contactRoles,
            int[] contactFeatures, int[] contactClasses, float[] contactTopY,
            int capacity, int startCount, out int newCount)
        {
            newCount = startCount;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(skin) || skin < 0f) return false;
            if (vertices == null || indices == null) return false;
            if (contactNormals == null || contactPoints == null || contactDepths == null ||
                contactAngles == null || contactSurfaceIds == null || contactRoles == null ||
                contactFeatures == null || contactClasses == null || contactTopY == null)
                return false;
            if (capacity < 0 || startCount < 0 || startCount > capacity) return false;
            if (capacity > contactNormals.Length || capacity > contactPoints.Length ||
                capacity > contactDepths.Length || capacity > contactAngles.Length ||
                capacity > contactSurfaceIds.Length || capacity > contactRoles.Length ||
                capacity > contactFeatures.Length || capacity > contactClasses.Length ||
                capacity > contactTopY.Length) return false;

            Vector3 minimum = new Vector3(center.x - radius - skin, center.y - radius - skin,
                center.z - radius - skin);
            Vector3 maximum = new Vector3(center.x + radius + skin, center.y + radius + skin,
                center.z + radius + skin);

            if (candidateCount >= 0)
            {
                if (candidates == null || triangleMinX == null || triangleMaxX == null ||
                    triangleMinZ == null || triangleMaxZ == null ||
                    triangleSurfaceId == null) return false;
                if (candidateCount > candidates.Length ||
                    candidateCount > triangleMinX.Length ||
                    candidateCount > triangleMaxX.Length ||
                    candidateCount > triangleMinZ.Length ||
                    candidateCount > triangleMaxZ.Length) return false;
                for (int k = 0; k < candidateCount; k++)
                {
                    int t = candidates[k];
                    if (t < 0 || t >= triangleMinX.Length) continue;
                    if (maximum.x < triangleMinX[t] || minimum.x > triangleMaxX[t]) continue;
                    if (maximum.z < triangleMinZ[t] || minimum.z > triangleMaxZ[t]) continue;
                    Vector3 closest;
                    Vector3 outward;
                    float depth;
                    if (!TryTrianglePenetration(t, center, radius, vertices, indices, skin,
                        out closest, out outward, out depth)) continue;
                    if (newCount >= capacity) return false;
                    WriteContact(t, triangleSurfaceId[t], closest, outward, depth, vertices,
                        indices, contactNormals, contactPoints, contactDepths, contactAngles,
                        contactSurfaceIds, contactRoles, contactFeatures, contactClasses,
                        contactTopY, newCount);
                    newCount = newCount + 1;
                }
                return true;
            }

            if (chunkMin == null || chunkMax == null || chunkStarts == null ||
                chunkTriangleCounts == null || chunkZoneIds == null) return false;
            if (triangleCount <= 0 || chunkCount <= 0) return false;
            for (int c = 0; c < chunkCount; c++)
            {
                if (chunkMax[c].x < minimum.x || chunkMin[c].x > maximum.x) continue;
                if (chunkMax[c].y < minimum.y || chunkMin[c].y > maximum.y) continue;
                if (chunkMax[c].z < minimum.z || chunkMin[c].z > maximum.z) continue;
                int start = chunkStarts[c];
                int end = start + chunkTriangleCounts[c];
                int zone = chunkZoneIds[c];
                int chunkSurfaceId = FirstTerrainSurfaceId + (zone >= 0 ? zone : c);
                for (int t = start; t < end; t++)
                {
                    int at = t * 3;
                    Vector3 a = vertices[indices[at]];
                    Vector3 b = vertices[indices[at + 1]];
                    Vector3 d = vertices[indices[at + 2]];
                    if (maximum.x < Mathf.Min(a.x, Mathf.Min(b.x, d.x)) ||
                        minimum.x > Mathf.Max(a.x, Mathf.Max(b.x, d.x))) continue;
                    if (maximum.y < Mathf.Min(a.y, Mathf.Min(b.y, d.y)) ||
                        minimum.y > Mathf.Max(a.y, Mathf.Max(b.y, d.y))) continue;
                    if (maximum.z < Mathf.Min(a.z, Mathf.Min(b.z, d.z)) ||
                        minimum.z > Mathf.Max(a.z, Mathf.Max(b.z, d.z))) continue;
                    Vector3 closest;
                    Vector3 outward;
                    float depth;
                    if (!TryTrianglePenetration(t, center, radius, vertices, indices, skin,
                        out closest, out outward, out depth)) continue;
                    if (newCount >= capacity) return false;
                    WriteContact(t, chunkSurfaceId, closest, outward, depth, vertices,
                        indices, contactNormals, contactPoints, contactDepths, contactAngles,
                        contactSurfaceIds, contactRoles, contactFeatures, contactClasses,
                        contactTopY, newCount);
                    newCount = newCount + 1;
                }
            }
            return true;
        }

        /// <summary>
        /// One contact record at <paramref name="index"/>: angle and class from the same
        /// <see cref="ContactClassificationMath"/> every other source uses, and the ledge
        /// height from the triangle's highest vertex.
        /// </summary>
        private static void WriteContact(int triangle, int surfaceId, Vector3 point,
            Vector3 normal, float depth, Vector3[] vertices, int[] indices,
            Vector3[] contactNormals, Vector3[] contactPoints, float[] contactDepths,
            float[] contactAngles, int[] contactSurfaceIds, int[] contactRoles,
            int[] contactFeatures, int[] contactClasses, float[] contactTopY, int index)
        {
            int at = triangle * 3;
            Vector3 a = vertices[indices[at]];
            Vector3 b = vertices[indices[at + 1]];
            Vector3 d = vertices[indices[at + 2]];
            float top = Mathf.Max(a.y, Mathf.Max(b.y, d.y));
            contactNormals[index] = normal;
            contactPoints[index] = point;
            contactDepths[index] = depth;
            contactAngles[index] = ObbContactMath.ContactAngleDeg(normal);
            contactSurfaceIds[index] = surfaceId;
            contactRoles[index] = ContactCodes.RoleBlocking;
            contactFeatures[index] = ContactCodes.FeatureFace;
            contactClasses[index] = ContactClassificationMath.ClassifyContact(normal);
            contactTopY[index] = top;
        }

        /// <summary>Closest point on one triangle; false for a degenerate triangle.</summary>
        public static bool TryClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b,
            Vector3 c, out Vector3 closest)
        {
            closest = Vector3.zero;
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 normal = Cross(ab, ac);
            if (!KatamariMath.IsFiniteFloat(normal.magnitude) || normal.magnitude <= Epsilon)
                return false;

            Vector3 ap = point - a;
            float d1 = Dot(ab, ap);
            float d2 = Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                closest = a;
                return true;
            }

            Vector3 bp = point - b;
            float d3 = Dot(ab, bp);
            float d4 = Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                closest = b;
                return true;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                closest = a + ab * v;
                return true;
            }

            Vector3 cp = point - c;
            float d5 = Dot(ab, cp);
            float d6 = Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                closest = c;
                return true;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                closest = a + ac * w;
                return true;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                closest = b + (c - b) * w;
                return true;
            }

            float denominator = va + vb + vc;
            if (Mathf.Abs(denominator) <= Epsilon) return false;
            float vWeight = vb / denominator;
            float wWeight = vc / denominator;
            closest = a + ab * vWeight + ac * wWeight;
            return true;
        }

        private static bool PointInTriangleXZ(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(denominator) <= Epsilon) return false;
            float u = ((b.z - c.z) * (point.x - c.x) +
                (c.x - b.x) * (point.z - c.z)) / denominator;
            float v = ((c.z - a.z) * (point.x - c.x) +
                (a.x - c.x) * (point.z - c.z)) / denominator;
            float w = 1f - u - v;
            return u >= -1e-4f && v >= -1e-4f && w >= -1e-4f;
        }

        private static void IncludePoint(Vector3 point, ref Vector3 minimum, ref Vector3 maximum)
        {
            if (point.x < minimum.x) minimum.x = point.x;
            if (point.y < minimum.y) minimum.y = point.y;
            if (point.z < minimum.z) minimum.z = point.z;
            if (point.x > maximum.x) maximum.x = point.x;
            if (point.y > maximum.y) maximum.y = point.y;
            if (point.z > maximum.z) maximum.z = point.z;
        }

        private static float Dot(Vector3 left, Vector3 right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z;
        }

        private static Vector3 Cross(Vector3 left, Vector3 right)
        {
            return new Vector3(left.y * right.z - left.z * right.y,
                left.z * right.x - left.x * right.z,
                left.x * right.y - left.y * right.x);
        }
    }
}
