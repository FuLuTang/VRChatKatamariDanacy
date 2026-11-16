using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Static, chunked terrain mesh queries for the kinematic motor: support under a
    /// probe and sphere-vs-mesh penetration.
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
    /// band. The normal is the triangle's face normal, flipped to +Y for a double-sided
    /// floor. Push-out is single-sided: only the front side of a triangle (the side its
    /// winding normal points to) pushes the sphere out, so standing under a roof does not
    /// lift the ball. Degenerate/zero-area triangles are skipped.
    /// </para>
    /// <para>
    /// The support probe list is the motor's 16 radial directions; each is projected to
    /// XZ and scaled to the ball's footprint, so a footprint edge on a terrace still finds
    /// the upper surface. The selected support plane is handed to the existing
    /// <c>GroundContactMath</c> as a regular <c>(normal, offset)</c> pair, exactly like a
    /// plane/box/ramp support.
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
        /// Terrain surface ids start here, clear of planes (1000 + index), boxes (1 + i),
        /// ramps (10 + i) and props (100 + i). The id is the chunk's zone id, so a seam
        /// between two chunks in the same zone keeps one support id.
        /// </summary>
        public const int FirstTerrainSurfaceId = 2000;

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
                for (int t = start; t < end; t++)
                {
                    int at = t * 3;
                    Vector3 a = vertices[indices[at]];
                    Vector3 b = vertices[indices[at + 1]];
                    Vector3 d = vertices[indices[at + 2]];
                    if (probe.x < Mathf.Min(a.x, Mathf.Min(b.x, d.x)) - Epsilon ||
                        probe.x > Mathf.Max(a.x, Mathf.Max(b.x, d.x)) + Epsilon) continue;
                    if (probe.z < Mathf.Min(a.z, Mathf.Min(b.z, d.z)) - Epsilon ||
                        probe.z > Mathf.Max(a.z, Mathf.Max(b.z, d.z)) + Epsilon) continue;

                    Vector3 raw = Cross(b - a, d - a);
                    float length = raw.magnitude;
                    if (!KatamariMath.IsFiniteFloat(length) || length <= Epsilon) continue;
                    Vector3 face = raw * (1f / length);
                    if (face.y < 0f) face = face * -1f;
                    if (face.y <= MinUpwardNormalY) continue;

                    float planeOffset = Dot(face, a);
                    float height = (planeOffset - face.x * probe.x - face.z * probe.z) / face.y;
                    if (!KatamariMath.IsFiniteFloat(height) || height > maxHeight + Epsilon)
                        continue;
                    if (!PointInTriangleXZ(probe, a, b, d)) continue;
                    if (height <= bestHeight) continue;

                    bestHeight = height;
                    point = new Vector3(probe.x, height, probe.z);
                    normal = face;
                    int zone = chunkZoneIds[c];
                    surfaceId = FirstTerrainSurfaceId + (zone >= 0 ? zone : c);
                    found = true;
                }
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
            out Vector3 point, out Vector3 normal, out float depth)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            depth = 0f;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
            if (vertices == null || indices == null || chunkMin == null || chunkMax == null ||
                chunkStarts == null || chunkTriangleCounts == null) return false;
            if (triangleCount <= 0 || chunkCount <= 0) return false;

            float bestDepth = 0f;
            bool found = false;
            Vector3 minimum = new Vector3(center.x - radius, center.y - radius,
                center.z - radius);
            Vector3 maximum = new Vector3(center.x + radius, center.y + radius,
                center.z + radius);

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

                    Vector3 raw = Cross(b - a, d - a);
                    float length = raw.magnitude;
                    if (!KatamariMath.IsFiniteFloat(length) || length <= Epsilon) continue;
                    Vector3 face = raw * (1f / length);
                    // Single-sided: a query behind the winding normal is outside the solid.
                    if (Dot(face, center - a) < -Epsilon) continue;

                    Vector3 closest;
                    if (!TryClosestPointOnTriangle(center, a, b, d, out closest)) continue;
                    Vector3 delta = center - closest;
                    float distance = delta.magnitude;
                    if (!KatamariMath.IsFiniteFloat(distance) || distance >= radius) continue;
                    Vector3 outward;
                    if (distance > Epsilon) outward = delta * (1f / distance);
                    else outward = face;
                    if (Dot(outward, face) < 0f) outward = face;
                    float candidateDepth = radius - distance;
                    if (!found || candidateDepth > bestDepth)
                    {
                        found = true;
                        bestDepth = candidateDepth;
                        point = closest;
                        normal = outward;
                    }
                }
            }

            if (!found) return false;
            depth = bestDepth;
            return true;
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
