using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// T0/T1 terrain mesh math: flat/slope support, step and gully bands, overhang
    /// single-sided push-out, degenerate triangles, chunk seams, and the no-mesh reject.
    /// The motor integration is a thin consumer of these answers; the flag-off path calls
    /// none of them.
    /// </summary>
    public class TerrainMeshContactTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly Vector3[] QuadVertices =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 0f, 1f),
            new Vector3(0f, 0f, 1f),
        };

        private static readonly int[] QuadIndices = { 0, 1, 2, 0, 2, 3 };

        [Test]
        public void FlatMeshHasTheSameSupportAsAPlane()
        {
            Mesh mesh = Build(QuadVertices, QuadIndices, new[] { 0 }, new[] { 0 });
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(TerrainMeshMath.TrySupportProbe(new Vector3(0.5f, 0.5f, 0.5f),
                10f, mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts, mesh.ChunkZoneIds,
                mesh.ChunkCount, out point, out normal, out surfaceId));
            Assert.AreEqual(0f, point.y, Tolerance);
            Assert.AreEqual(1f, normal.y, Tolerance);
            Assert.AreEqual(TerrainMeshMath.FirstTerrainSurfaceId, surfaceId);
        }

        [Test]
        public void TenDegreeSlopeReturnsItsPlaneHeightAndNormal()
        {
            float rise = Mathf.Tan(10f * Mathf.Deg2Rad);
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, rise, 0f),
                new Vector3(1f, rise, 1f),
                new Vector3(0f, 0f, 1f),
            };
            Mesh mesh = Build(vertices, QuadIndices, new[] { 0 }, new[] { 0 });
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(TerrainMeshMath.TrySupportProbe(new Vector3(0.5f, 0.5f, 0.5f),
                10f, mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts, mesh.ChunkZoneIds,
                mesh.ChunkCount, out point, out normal, out surfaceId));
            Assert.AreEqual(rise * 0.5f, point.y, 1e-3f);
            Assert.That(normal.y, Is.EqualTo(Mathf.Cos(10f * Mathf.Deg2Rad)).Within(1e-3f));
            Assert.That(normal.x, Is.EqualTo(-Mathf.Sin(10f * Mathf.Deg2Rad)).Within(1e-3f));
        }

        [Test]
        public void AStepIsOutsideTheSmallBallBandAndInsideTheLargeBallBand()
        {
            // The existing rule is h <= 0.25 * D; a 0.5 m bank blocks 0.30 m and is
            // climbable by 2.20 m. A 0.6 m step is outside even 2.20's 0.55 band.
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(0.5f, 0f, 0.25f * 0.30f));
            Assert.IsTrue(SupportSurfaceMath.IsStepWithinLimit(0.5f, 0f, 0.25f * 2.20f));
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(0.6f, 0f, 0.25f * 2.20f));
        }

        [Test]
        public void AFlatGullyFloorSupportsAtMinusOne()
        {
            Vector3[] vertices =
            {
                new Vector3(-2f, 0f, -2f),
                new Vector3(2f, 0f, -2f),
                new Vector3(2f, 0f, 2f),
                new Vector3(-2f, 0f, 2f),
                new Vector3(-1f, -1f, -1f),
                new Vector3(1f, -1f, -1f),
                new Vector3(1f, -1f, 1f),
                new Vector3(-1f, -1f, 1f),
            };
            int[] indices =
            {
                4, 5, 6, 4, 6, 7,       // floor at -1
                0, 1, 5, 0, 5, 4,       // north slope
                1, 2, 6, 1, 6, 5,       // east slope
                2, 3, 7, 2, 7, 6,       // south slope
                3, 0, 4, 3, 4, 7,       // west slope
            };
            // 10 triangles (5 quads), one chunk per quad: the starts are triangle indices
            // 0,2,4,6,8. Chunks must be contiguous and cover every triangle exactly once, so a
            // table that describes more triangles than the index buffer has (e.g. 0,5,10,15)
            // is refused by TryBuildMesh, not silently trimmed.
            Mesh mesh = Build(vertices, indices, new[] { 0, 2, 4, 6, 8 }, new[] { 0 });
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(TerrainMeshMath.TrySupportProbe(new Vector3(0f, 0f, 0f), 10f,
                mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts, mesh.ChunkZoneIds,
                mesh.ChunkCount, out point, out normal, out surfaceId));
            Assert.AreEqual(-1f, point.y, Tolerance);
            Assert.That(normal.y, Is.GreaterThan(0.9f));
            Assert.IsFalse(SupportSurfaceMath.IsStepWithinLimit(-0.5f, -1f, 0.075f),
                "a 0.5 m bank is outside the 0.30 m ball's step band");
            Assert.IsTrue(SupportSurfaceMath.IsStepWithinLimit(-0.5f, -1f, 0.55f),
                "a 0.5 m bank is inside the 2.20 m ball's step band");
        }

        [Test]
        public void OverhangDoesNotSupportFromAboveButPushesDownWhenTouching()
        {
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f), new Vector3(0f, 0f, 1f),
                // Roof, wound so its face normal points down (front side is below it).
                new Vector3(0f, 0.8f, 0f), new Vector3(0f, 0.8f, 1f),
                new Vector3(1f, 0.8f, 1f), new Vector3(1f, 0.8f, 0f),
            };
            int[] indices =
            {
                0, 2, 1, 0, 3, 2,       // floor normal +Y
                4, 6, 5, 4, 7, 6,       // roof normal -Y
            };
            Mesh mesh = Build(vertices, indices, new[] { 0, 2 }, new[] { 0 });
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(TerrainMeshMath.TrySupportProbe(new Vector3(0.5f, 0f, 0.5f), 0.275f,
                mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts, mesh.ChunkZoneIds,
                mesh.ChunkCount, out point, out normal, out surfaceId));
            Assert.That(point.y, Is.EqualTo(0f).Within(Tolerance),
                "the roof is above the support band");

            Vector3 contact;
            Vector3 pushNormal;
            float depth;
            Assert.IsTrue(TerrainMeshMath.TryDeepestPenetration(
                new Vector3(0.5f, 0.6f, 0.5f), 0.3f, mesh.Vertices, mesh.Indices,
                mesh.TriangleCount, mesh.ChunkMin, mesh.ChunkMax, mesh.ChunkStarts,
                mesh.ChunkTriangleCounts, mesh.ChunkCount, null, -1, null, null, null, null,
                out contact, out pushNormal, out depth));
            Assert.That(depth, Is.GreaterThan(0f));
            Assert.That(pushNormal.y, Is.LessThan(-0.5f), "a roof pushes the sphere down");
        }

        [Test]
        public void DegenerateTriangleIsSkipped()
        {
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f), new Vector3(0f, 0f, 1f),
                new Vector3(2f, 0f, 2f), new Vector3(2f, 0f, 2f),
                new Vector3(2f, 0f, 2f),
            };
            int[] indices = { 0, 1, 2, 0, 2, 3, 4, 5, 6 };
            Mesh mesh = Build(vertices, indices, new[] { 0, 2 }, new[] { 0 });
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            Assert.IsTrue(TerrainMeshMath.TrySupportProbe(new Vector3(0.5f, 0f, 0.5f), 1f,
                mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts, mesh.ChunkZoneIds,
                mesh.ChunkCount, out point, out normal, out surfaceId));
            Assert.AreEqual(0f, point.y, Tolerance);
            Vector3 closest;
            Assert.IsFalse(TerrainMeshMath.TryClosestPointOnTriangle(Vector3.zero,
                vertices[4], vertices[5], vertices[6], out closest));
        }

        [Test]
        public void ChunkSeamHasNoGap()
        {
            // One quad split across two chunks; probe along the shared diagonal.
            Mesh mesh = Build(QuadVertices, QuadIndices, new[] { 0, 1 }, new[] { 0, 0 });
            Assert.AreEqual(2, mesh.ChunkCount);
            for (int i = 0; i <= 10; i++)
            {
                float x = i / 10f;
                Vector3 point;
                Vector3 normal;
                int surfaceId;
                Assert.IsTrue(TerrainMeshMath.TrySupportProbe(new Vector3(x, 0f, 1f - x), 1f,
                    mesh.Vertices, mesh.Indices, mesh.TriangleCount, mesh.ChunkMin,
                    mesh.ChunkMax, mesh.ChunkStarts, mesh.ChunkTriangleCounts,
                    mesh.ChunkZoneIds, mesh.ChunkCount, out point, out normal,
                    out surfaceId), "seam probe " + i);
                Assert.That(point.y, Is.EqualTo(0f).Within(Tolerance), "seam probe " + i);
            }
        }

        [Test]
        public void NoMeshAndMalformedChunksAreRejected()
        {
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            Assert.IsFalse(TerrainMeshMath.TrySupportProbe(Vector3.zero, 1f,
                new Vector3[8], new int[24], 0, new Vector3[4], new Vector3[4],
                new int[4], new int[4], new int[4], 0, out point, out normal, out surfaceId));
            Assert.IsFalse(TerrainMeshMath.TryDeepestPenetration(Vector3.zero, 1f,
                new Vector3[8], new int[24], 0, new Vector3[4], new Vector3[4],
                new int[4], new int[4], 0, null, -1, null, null, null, null,
                out point, out normal, out float depth));

            // A chunk gap (0..1 then 3..4) must be refused, not silently split.
            Vector3[] vertices = new Vector3[8];
            int[] indices = new int[24];
            var chunkStarts = new[] { 0, 3 };
            var chunkCounts = new[] { 1, 1 };
            var zones = new[] { 0, 0 };
            int triangles;
            int chunks;
            Assert.IsFalse(TerrainMeshMath.TryBuildMesh(vertices, 4, indices, 6, chunkStarts,
                chunkCounts, zones, 2, vertices, indices, new int[64], new int[64],
                new int[64], new Vector3[64], new Vector3[64], out triangles, out chunks));
        }

        private static Mesh Build(Vector3[] vertices, int[] indices, int[] starts, int[] zones)
        {
            if (zones.Length < starts.Length)
            {
                int[] expanded = new int[starts.Length];
                for (int i = 0; i < starts.Length; i++)
                {
                    expanded[i] = zones.Length == 0 ? 0 : zones[Mathf.Min(i, zones.Length - 1)];
                }
                zones = expanded;
            }
            int[] counts = new int[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                int end = i + 1 < starts.Length ? starts[i + 1] : indices.Length / 3;
                counts[i] = end - starts[i];
            }
            var meshVertices = new Vector3[TerrainMeshMath.MaxVertices];
            var meshIndices = new int[TerrainMeshMath.MaxTriangles * 3];
            var chunkStarts = new int[TerrainMeshMath.MaxChunks];
            var chunkCounts = new int[TerrainMeshMath.MaxChunks];
            var chunkZones = new int[TerrainMeshMath.MaxChunks];
            var chunkMin = new Vector3[TerrainMeshMath.MaxChunks];
            var chunkMax = new Vector3[TerrainMeshMath.MaxChunks];
            int triangleCount;
            int chunkCount;
            // The message carries the shape the chunk table has to match, because "build mesh"
            // alone does not say whether the fixture or the mesh validator is wrong.
            Assert.IsTrue(TerrainMeshMath.TryBuildMesh(vertices, vertices.Length, indices,
                indices.Length, starts, counts, zones, starts.Length, meshVertices,
                meshIndices, chunkStarts, chunkCounts, chunkZones, chunkMin, chunkMax,
                out triangleCount, out chunkCount),
                "build mesh: " + (indices.Length / 3) + " triangles, " + starts.Length +
                " chunks");
            return new Mesh
            {
                Vertices = meshVertices,
                Indices = meshIndices,
                ChunkStarts = chunkStarts,
                ChunkTriangleCounts = chunkCounts,
                ChunkZoneIds = chunkZones,
                ChunkMin = chunkMin,
                ChunkMax = chunkMax,
                TriangleCount = triangleCount,
                ChunkCount = chunkCount,
            };
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
    }
}
