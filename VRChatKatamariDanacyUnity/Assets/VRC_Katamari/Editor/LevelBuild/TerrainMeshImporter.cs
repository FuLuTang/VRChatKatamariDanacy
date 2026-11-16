using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Reads one v2 level's <c>terrain</c> block (inline or <c>.katerrain</c> sidecar),
    /// builds the chunk arrays the motor consumes, and writes them onto a
    /// <see cref="TerrainFieldHost"/> under the level's managed root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the editor-only bridge between <c>LEVEL_FORMAT_V2</c> §7/§8.3 and the
    /// Udon runtime: no disk access ever reaches the runtime, only the serialized arrays on
    /// <see cref="TerrainFieldHost"/>. It follows the §7 schema exactly (inline
    /// <c>vertices</c>/<c>indices</c> at &lt;=512 triangles; <c>meshRef</c> + sidecar above
    /// that) and does not introduce a second terrain format.
    /// </para>
    /// <para>
    /// Idempotent by source sha256 + counts: a second import writes nothing and logs
    /// <c>+0 ~0 -0</c> - but only after the UDON side is confirmed too (see
    /// <see cref="EnsureUdonSide"/>; the arrays live twice and a host added in this session
    /// starts with an empty U# variable table). It never saves a scene. A reference level
    /// (<c>meta.source.kind == "reference"</c>) is only accepted when the active scene is
    /// under <c>Assets/_RefUnlicensed/</c> and the level folder is <c>_ref_*</c>; this keeps
    /// terrain arrays out of <c>StoryMode.unity</c> and out of git (D-8).
    /// </para>
    /// </remarks>
    public static class TerrainMeshImporter
    {
        public const string MenuPath = "VRC Katamari/Level/Import Terrain Mesh (v2)";
        public const string ManagedRootName = "LevelContent";
        private const string TerrainRootName = "TerrainRoot";
        /// <summary>The visual child of <c>TerrainRoot</c>; it also carries the player's collider.</summary>
        public const string TerrainVisualName = "TerrainVisual";
        /// <summary>
        /// Placeholder for a terrain material segment that names none (the reference level's
        /// one segment has no <c>material</c>): the greybox ground the structures already use.
        /// </summary>
        private const string FallbackVisualMaterialPath =
            "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundGround.mat";
        private const string ReferenceSceneRoot = "Assets/_RefUnlicensed/";
        private const int MaxConsoleRows = 25;

        // ------------------------------------------------------------------ report ---

        public sealed class TerrainImportReport
        {
            public string LevelId;
            public string TerrainId;
            public string ScenePath;
            public int Created;
            public int Updated;
            public int Skipped;
            /// <summary>
            /// The standing surface (<c>TerrainVisual</c> + its collider, mesh asset): objects,
            /// components or assets this run created / repaired. Zero on the idempotent second
            /// import, and always zero for a level that declares no <c>meta.playerStart</c>.
            /// </summary>
            public int StandingCreated;
            public int StandingUpdated;
            public int Triangles;
            public int Vertices;
            public int Chunks;
            /// <summary>
            /// True only when <c>CopyProxyToUdon</c> succeeded on this run. The terrain arrays
            /// live in TWO places (the proxy's serialized fields and the backing UdonBehaviour),
            /// and "already imported" is only honest when both are written.
            /// </summary>
            public bool UdonCopyIssued;
            /// <summary>Why the Udon copy failed, when it did (for the error line).</summary>
            public string UdonCopyError;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Notes = new List<string>();

            public bool Ok { get { return Errors.Count == 0; } }

            public string Headline()
            {
                return "[terrain] +" + (Created + StandingCreated) + " ~" +
                       (Updated + StandingUpdated) + " -0 terrain=" + TerrainId +
                       " tris=" + Triangles + " verts=" + Vertices + " chunks=" + Chunks +
                       " errors=" + Errors.Count;
            }

            public void Log()
            {
                string text = BuildSummary();
                if (Ok) Debug.Log(text); else Debug.LogError(text);
            }

            public string BuildSummary()
            {
                var text = new StringBuilder();
                text.Append(Headline());
                if (!string.IsNullOrEmpty(LevelId))
                    text.Append("  level=").Append(LevelId);
                text.AppendLine();
                if (!string.IsNullOrEmpty(ScenePath))
                    text.Append("  scene: ").Append(ScenePath).AppendLine();
                for (int i = 0; i < Notes.Count && i < MaxConsoleRows; i++)
                    text.Append("  note: ").Append(Notes[i]).AppendLine();
                for (int i = 0; i < Errors.Count && i < MaxConsoleRows; i++)
                    text.Append("  error: ").Append(Errors[i]).AppendLine();
                if (Errors.Count > MaxConsoleRows)
                    text.Append("  ... ").Append(Errors.Count - MaxConsoleRows).AppendLine();
                return text.ToString();
            }

            public void Error(string message) { Errors.Add(message); }
        }

        // --------------------------------------------------------------------- entry ---

        [MenuItem(MenuPath)]
        public static void ImportActiveLevel()
        {
            string levelId = ActiveLevelId();
            if (string.IsNullOrEmpty(levelId))
            {
                Debug.LogError("[terrain] no LevelContent/<levelId> root in the active scene; " +
                               "run the placement import first or call TerrainMeshImporter.Import(levelId)");
                return;
            }
            Import(levelId).Log();
        }

        public static TerrainImportReport Import(string levelId)
        {
            var report = new TerrainImportReport { LevelId = levelId };
            // The repository root, a nested level id ("physics_test_field/C21") and the
            // canonical-vs-side filename order all come from LevelPaths: the importer and this
            // tool must not answer "where is Docs/Design/Levels" in two different ways (that
            // was the measured defect: ProjectRoot() is the UNITY project directory).
            string levelDir;
            var tried = new List<string>();
            if (!LevelPaths.TryResolveLevelDirectory(levelId, out levelDir, tried))
            {
                report.Error("[terrain_source] no level directory for '" + levelId +
                             "'. Searched:\n  " + string.Join("\n  ", tried.ToArray()));
                return report;
            }

            string path;
            var placementTried = new List<string>();
            if (!LevelPaths.TryResolvePlacementIn(levelDir, out path, placementTried))
            {
                report.Error("[terrain_source] no placement.json / placement.v2.json in " +
                             levelDir + ". Searched:\n  " +
                             string.Join("\n  ", placementTried.ToArray()));
                return report;
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                LevelJsonNode root = new LevelJsonParser(json).Parse();
                return ImportDocument(levelId, levelDir, path, json, root, report);
            }
            catch (Exception exception)
            {
                report.Error("[terrain_source] " + exception.Message);
                return report;
            }
        }

        private static TerrainImportReport ImportDocument(string levelId, string levelDir,
            string path, string json, LevelJsonNode root, TerrainImportReport report)
        {
            if (root == null || root.Kind != LevelJsonKind.Object)
            {
                report.Error("[terrain_source] the document is not a JSON object");
                return report;
            }
            string schema = root.GetString("schema");
            if (!string.Equals(schema, "katamari.placement/2", StringComparison.Ordinal))
            {
                report.Error("[terrain_source] schema is '" + schema +
                             "', expected katamari.placement/2");
                return report;
            }

            LevelJsonNode meta = root.Get("meta");
            LevelJsonNode terrain = root.Get("terrain");
            if (terrain == null || terrain.Kind != LevelJsonKind.Object)
            {
                report.Error("[terrain_source] no terrain block");
                return report;
            }
            string terrainId = terrain.GetString("id");
            report.TerrainId = terrainId;
            string kind = terrain.GetString("kind");
            if (!string.Equals(kind, "mesh", StringComparison.Ordinal))
            {
                report.Error("[terrain_mesh] terrain.kind must be 'mesh' (got '" + kind + "')");
                return report;
            }
            string winding = terrain.GetString("winding");
            if (!string.Equals(winding, "ccw", StringComparison.Ordinal))
            {
                report.Error("[terrain_winding] terrain.winding must be 'ccw' (got '" +
                             winding + "')");
                return report;
            }

            string sourceKind = meta != null && meta.Get("source") != null
                ? meta.Get("source").GetString("kind") : null;
            bool isReference = levelId.StartsWith("_ref_", StringComparison.Ordinal) ||
                string.Equals(sourceKind, "reference", StringComparison.Ordinal);
            if (isReference)
            {
                if (!levelId.StartsWith("_ref_", StringComparison.Ordinal))
                {
                    report.Error("[terrain_scene_guard] reference terrain must be in a _ref_* level");
                    return report;
                }
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                report.ScenePath = scene.path;
                if (string.IsNullOrEmpty(scene.path) ||
                    !scene.path.StartsWith(ReferenceSceneRoot, StringComparison.Ordinal))
                {
                    report.Error("[terrain_scene_guard] reference terrain may only be written " +
                                 "into " + ReferenceSceneRoot + " (active scene is '" +
                                 scene.path + "'); open RefCancer.unity under _RefUnlicensed");
                    return report;
                }
            }

            TerrainGeometry geometry;
            string geometryError;
            if (!TryReadGeometry(levelDir, terrain, out geometry, out geometryError))
            {
                report.Error(geometryError);
                return report;
            }
            report.Triangles = geometry.TriangleCount;
            report.Vertices = geometry.VertexCount;
            report.Chunks = geometry.ChunkCount;

            if (geometry.TriangleCount > TerrainMeshMath.MaxTriangles ||
                geometry.VertexCount > TerrainMeshMath.MaxVertices ||
                geometry.ChunkCount > TerrainMeshMath.MaxChunks)
            {
                report.Error("[terrain_capacity] " + geometry.TriangleCount + " triangles / " +
                             geometry.VertexCount + " vertices / " + geometry.ChunkCount +
                             " chunks exceed the motor capacity (" + TerrainMeshMath.MaxTriangles +
                             "/" + TerrainMeshMath.MaxVertices + "/" +
                             TerrainMeshMath.MaxChunks + ")");
                return report;
            }

            int expected = meta != null && meta.Get("expected") != null
                ? meta.Get("expected").GetInt("terrainTriangles", -1) : -1;
            if (expected >= 0 && expected != geometry.TriangleCount)
            {
                report.Error("[terrain_mesh] meta.expected.terrainTriangles=" + expected +
                             " but the geometry has " + geometry.TriangleCount);
                return report;
            }

            // Inline hash vs the sidecar hash: one stable idempotence key for both modes.
            string sourceSha = geometry.SidecarSha;
            if (string.IsNullOrEmpty(sourceSha)) sourceSha = Sha256Hex(Encoding.UTF8.GetBytes(json));
            if (string.IsNullOrEmpty(geometry.SidecarSha))
            {
                report.Notes.Add("inline geometry (" + geometry.TriangleCount +
                                 " triangles); sha256 is the document bytes");
            }
            else
            {
                report.Notes.Add("sidecar '" + geometry.SidecarPath + "' sha256 verified");
            }

            geometry.Segments = ReadVisualSegments(terrain.Get("materialSegments"),
                geometry.IndexCount, report);

            TerrainImportReport written = WriteHost(levelId, terrainId, sourceSha, geometry, report);
            // The player's standing surface only exists for a level that declares where the
            // player starts (meta.playerStart): a lab cell with a terrain keeps today's scene.
            if (written.Ok && meta != null && meta.Get("playerStart") != null)
            {
                EnsureStandingSurface(levelId, terrainId, geometry, isReference, written);
            }

            return written;
        }

        private static VisualSegment[] ReadVisualSegments(LevelJsonNode node, int indexCount,
            TerrainImportReport report)
        {
            var segments = new List<VisualSegment>();
            if (node != null && node.Kind == LevelJsonKind.Array && node.Count > 0)
            {
                int coverage = 0;
                for (int i = 0; i < node.Count; i++)
                {
                    LevelJsonNode entry = node.At(i);
                    int first = entry.GetInt("firstIndex", -1);
                    int count = entry.GetInt("indexCount", -1);
                    if (first != coverage || count <= 0 || count % 3 != 0)
                    {
                        segments.Clear();
                        break;
                    }

                    segments.Add(new VisualSegment
                    {
                        FirstIndex = first,
                        IndexCount = count,
                        Material = entry.GetString("material")
                    });
                    coverage += count;
                }

                if (segments.Count > 0 && coverage != indexCount) segments.Clear();
                if (segments.Count == 0)
                {
                    report.Notes.Add("terrain.materialSegments do not tile the " + indexCount +
                                     " indices; the visual uses one submesh");
                }
            }

            if (segments.Count == 0)
            {
                segments.Add(new VisualSegment { FirstIndex = 0, IndexCount = indexCount });
            }

            return segments.ToArray();
        }

        // ------------------------------------------------------------------ geometry ---

        private sealed class TerrainGeometry
        {
            public Vector3[] Vertices;
            public int[] Indices;
            public int[] ChunkStarts;
            public int[] ChunkTriangleCounts;
            public int[] ChunkZoneIds;
            public int VertexCount;
            public int IndexCount;
            public int TriangleCount;
            public int ChunkCount;
            public string SidecarPath;
            public string SidecarSha;
            /// <summary>Visual material segments (first index, index count, material path).</summary>
            public VisualSegment[] Segments;
        }

        private struct VisualSegment
        {
            public int FirstIndex;
            public int IndexCount;
            public string Material;
        }

        private static bool TryReadGeometry(string levelDir, LevelJsonNode terrain,
            out TerrainGeometry geometry, out string error)
        {
            geometry = null;
            error = null;
            LevelJsonNode meshRef = terrain.Get("meshRef");
            Vector3[] vertices;
            int[] indices;
            int[] zoneIds;
            string sidecarPath = null;
            string sidecarSha = null;
            if (meshRef != null)
            {
                if (terrain.Get("vertices") != null || terrain.Get("indices") != null)
                {
                    error = "[terrain_mesh] meshRef and inline vertices/indices are mutually exclusive";
                    return false;
                }
                string relative = meshRef.GetString("path");
                sidecarSha = meshRef.GetString("sha256");
                int vertexCount = meshRef.GetInt("vertexCount", -1);
                int indexCount = meshRef.GetInt("indexCount", -1);
                int triangleCount = meshRef.GetInt("triangleCount", -1);
                if (!TryReadSidecar(levelDir, relative, sidecarSha, vertexCount, indexCount,
                    triangleCount, out vertices, out indices, out zoneIds, out sidecarPath,
                    out error)) return false;
            }
            else
            {
                if (!TryReadVertices(terrain.Get("vertices"), out vertices, out error)) return false;
                if (!TryReadInts(terrain.Get("indices"), out indices, out error)) return false;
                LevelJsonNode triangles = terrain.Get("triangles");
                if (triangles == null)
                {
                    error = "[terrain_mesh] inline terrain needs a triangles block";
                    return false;
                }
                int declared = triangles.GetInt("count", -1);
                if (declared >= 0 && declared != indices.Length / 3)
                {
                    error = "[terrain_mesh] triangles.count=" + declared +
                            " but indices length is " + indices.Length + " (3 per triangle)";
                    return false;
                }
                zoneIds = null;
                LevelJsonNode zoneNode = triangles.Get("zoneIds");
                if (zoneNode != null && !TryReadInts(zoneNode, out zoneIds, out error)) return false;
            }

            int triangleCount2 = indices.Length / 3;
            if (indices.Length != triangleCount2 * 3 || triangleCount2 <= 0)
            {
                error = "[terrain_mesh] indices length must be 3 × triangle count";
                return false;
            }
            if (vertices.Length == 0) { error = "[terrain_mesh] no vertices"; return false; }
            LevelJsonNode trianglesBlock = terrain.Get("triangles");
            int declaredCount = trianglesBlock != null
                ? trianglesBlock.GetInt("count", -1) : -1;
            if (declaredCount >= 0 && declaredCount != triangleCount2)
            {
                error = "[terrain_mesh] triangles.count=" + declaredCount +
                        " but the geometry has " + triangleCount2 + " triangles";
                return false;
            }
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] < 0 || indices[i] >= vertices.Length)
                {
                    error = "[terrain_mesh] index " + i + " = " + indices[i] +
                            " is outside 0.." + (vertices.Length - 1);
                    return false;
                }
            }
            if (zoneIds != null && zoneIds.Length != triangleCount2)
            {
                error = "[terrain_zone] zoneIds length " + zoneIds.Length +
                        " != triangle count " + triangleCount2;
                return false;
            }

            int[] chunkStarts;
            int[] chunkCounts;
            int[] chunkZones;
            if (!TryReadChunks(terrain.Get("chunks"), triangleCount2, zoneIds, out chunkStarts,
                out chunkCounts, out chunkZones, out error)) return false;

            geometry = new TerrainGeometry
            {
                Vertices = vertices,
                Indices = indices,
                ChunkStarts = chunkStarts,
                ChunkTriangleCounts = chunkCounts,
                ChunkZoneIds = chunkZones,
                VertexCount = vertices.Length,
                IndexCount = indices.Length,
                TriangleCount = triangleCount2,
                ChunkCount = chunkCounts.Length,
                SidecarPath = sidecarPath,
                SidecarSha = sidecarSha,
            };
            return true;
        }

        private static bool TryReadChunks(LevelJsonNode chunks, int triangleCount, int[] zoneIds,
            out int[] starts, out int[] counts, out int[] zones, out string error)
        {
            error = null;
            var startList = new List<int>();
            var countList = new List<int>();
            var zoneList = new List<int>();
            if (chunks != null && chunks.Kind == LevelJsonKind.Array && chunks.Count > 0)
            {
                int expected = 0;
                for (int i = 0; i < chunks.Count; i++)
                {
                    LevelJsonNode range = chunks.At(i).Get("triangleRange");
                    if (range == null || range.Count != 2)
                    {
                        error = "[terrain_broadphase] chunks[" + i + "].triangleRange must be [start,count]";
                        starts = counts = zones = null;
                        return false;
                    }
                    int start = range.At(0).AsInt();
                    int count = range.At(1).AsInt();
                    if (start != expected || count <= 0 || start + count > triangleCount)
                    {
                        error = "[terrain_broadphase] chunks must be contiguous, non-empty and " +
                                "cover every triangle once (chunk " + i + ")";
                        starts = counts = zones = null;
                        return false;
                    }
                    // A provided chunk may be larger than the motor's per-chunk ceiling
                    // (the §7 example itself has a 966-triangle chunk). Split it instead of
                    // rejecting a level the motor can still represent.
                    for (int offset = 0; offset < count; offset += TerrainMeshMath.MaxTrianglesPerChunk)
                    {
                        int part = Mathf.Min(TerrainMeshMath.MaxTrianglesPerChunk, count - offset);
                        int partStart = start + offset;
                        startList.Add(partStart);
                        countList.Add(part);
                        zoneList.Add(zoneIds != null && partStart < zoneIds.Length
                            ? zoneIds[partStart] : 0);
                    }
                    expected += count;
                }
                if (expected != triangleCount)
                {
                    error = "[terrain_broadphase] chunks cover " + expected + " of " +
                            triangleCount + " triangles";
                    starts = counts = zones = null;
                    return false;
                }
            }
            else
            {
                // §3.3: no chunks array means the importer groups by <=512 triangles.
                for (int start = 0; start < triangleCount; start += TerrainMeshMath.MaxTrianglesPerChunk)
                {
                    int count = Mathf.Min(TerrainMeshMath.MaxTrianglesPerChunk,
                        triangleCount - start);
                    startList.Add(start);
                    countList.Add(count);
                    zoneList.Add(zoneIds != null && start < zoneIds.Length ? zoneIds[start] : 0);
                }
            }
            if (startList.Count > TerrainMeshMath.MaxChunks)
            {
                // 68 broad-phase AABBs can legitimately be finer than the 64-slot motor
                // table; regroup into 512-triangle chunks, which is always <=16 for 8192.
                startList.Clear();
                countList.Clear();
                zoneList.Clear();
                for (int start = 0; start < triangleCount; start += TerrainMeshMath.MaxTrianglesPerChunk)
                {
                    int count = Mathf.Min(TerrainMeshMath.MaxTrianglesPerChunk,
                        triangleCount - start);
                    startList.Add(start);
                    countList.Add(count);
                    zoneList.Add(zoneIds != null && start < zoneIds.Length ? zoneIds[start] : 0);
                }
            }
            starts = startList.ToArray();
            counts = countList.ToArray();
            zones = zoneList.ToArray();
            return true;
        }

        // ------------------------------------------------------------------- sidecar ---

        private static bool TryReadSidecar(string levelDir, string relative, string expectedSha,
            int expectedVertices, int expectedIndices, int expectedTriangles,
            out Vector3[] vertices, out int[] indices, out int[] zoneIds,
            out string sidecarPath, out string error)
        {
            vertices = null;
            indices = null;
            zoneIds = null;
            sidecarPath = null;
            error = null;
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
                relative.Contains(".."))
            {
                error = "[terrain_sidecar] meshRef.path must be relative, non-empty and outside no '..'";
                return false;
            }
            string full = Path.GetFullPath(Path.Combine(levelDir, relative));
            string fullDir = Path.GetFullPath(levelDir) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(fullDir, StringComparison.Ordinal))
            {
                error = "[terrain_sidecar] meshRef.path escapes the level directory";
                return false;
            }
            if (!File.Exists(full))
            {
                error = "[terrain_sidecar] missing " + full;
                return false;
            }
            byte[] bytes = File.ReadAllBytes(full);
            string actual = Sha256Hex(bytes);
            if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                error = "[terrain_sidecar] sha256 " + actual + " != meshRef.sha256 " + expectedSha;
                return false;
            }
            sidecarPath = full;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Encoding.ASCII))
                {
                    if ((char)reader.ReadByte() != 'K' || (char)reader.ReadByte() != 'T' ||
                        (char)reader.ReadByte() != 'R' || (char)reader.ReadByte() != 'N')
                    {
                        error = "[terrain_sidecar] magic is not KTRN";
                        return false;
                    }
                    int version = reader.ReadUInt16();
                    if (version != 1) { error = "[terrain_sidecar] version " + version + " != 1"; return false; }
                    int indexWidth = reader.ReadByte();
                    int flags = reader.ReadByte();
                    int vertexCount = (int)reader.ReadUInt32();
                    int indexCount = (int)reader.ReadUInt32();
                    int triangleCount = (int)reader.ReadUInt32();
                    if (indexCount != triangleCount * 3 ||
                        (expectedVertices >= 0 && expectedVertices != vertexCount) ||
                        (expectedIndices >= 0 && expectedIndices != indexCount) ||
                        (expectedTriangles >= 0 && expectedTriangles != triangleCount))
                    {
                        error = "[terrain_sidecar] counts disagree with meshRef/JSON";
                        return false;
                    }
                    if (indexWidth != 2 && indexWidth != 4)
                    {
                        error = "[terrain_sidecar] indexWidth must be 2 or 4";
                        return false;
                    }
                    vertices = new Vector3[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                        vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(),
                            reader.ReadSingle());
                    indices = new int[indexCount];
                    for (int i = 0; i < indexCount; i++)
                        indices[i] = indexWidth == 2 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
                    if ((flags & 1) != 0)
                    {
                        zoneIds = new int[triangleCount];
                        for (int i = 0; i < triangleCount; i++) zoneIds[i] = reader.ReadByte();
                    }
                    if ((flags & 2) != 0)
                    {
                        // classes are derived from normals; the motor does not consume them
                        // in T0/T1, but they are part of the sidecar and are skipped here.
                        for (int i = 0; i < triangleCount; i++) reader.ReadByte();
                    }
                    if (stream.Position != stream.Length)
                    {
                        error = "[terrain_sidecar] trailing bytes after the declared sections";
                        return false;
                    }
                }
            }
            catch (Exception exception)
            {
                error = "[terrain_sidecar] " + exception.Message;
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------------- host ---

        private static TerrainImportReport WriteHost(string levelId, string terrainId,
            string sourceSha, TerrainGeometry geometry, TerrainImportReport report)
        {
            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            report.ScenePath = scene.path;
            Transform levelRoot = FindLevelRoot(levelId);
            if (levelRoot == null)
            {
                report.Error("[terrain_root] no " + ManagedRootName + "/" + levelId +
                             " root in the active scene; run the placement import first");
                return report;
            }
            KatamariBallMotor motor = UnityEngine.Object.FindObjectOfType<KatamariBallMotor>();
            if (motor == null)
            {
                report.Error("[terrain_motor] no KatamariBallMotor in the active scene");
                return report;
            }

            Transform terrainRoot = levelRoot.Find(TerrainRootName);
            bool created = false;
            if (terrainRoot == null)
            {
                var go = new GameObject(TerrainRootName);
                Undo.RegisterCreatedObjectUndo(go, "Create terrain root");
                terrainRoot = go.transform;
                terrainRoot.SetParent(levelRoot, false);
                created = true;
            }
            TerrainFieldHost host = terrainRoot.GetComponent<TerrainFieldHost>();
            if (host == null)
            {
                host = Undo.AddComponent<TerrainFieldHost>(terrainRoot.gameObject);
                created = true;
            }
            if (host == null)
            {
                report.Error("[terrain_udon] could not add TerrainFieldHost; create the " +
                             "component on " + HierarchyPath(terrainRoot) + " and re-import");
                return report;
            }

            var serialized = new SerializedObject(host);
            SerializedProperty sha = serialized.FindProperty("sourceSha256");
            SerializedProperty tri = serialized.FindProperty("triangleCount");
            SerializedProperty verts = serialized.FindProperty("vertexCount");
            SerializedProperty chunks = serialized.FindProperty("chunkCount");
            bool unchanged = sha != null && tri != null && verts != null && chunks != null &&
                sha.stringValue == sourceSha && tri.intValue == geometry.TriangleCount &&
                verts.intValue == geometry.VertexCount && chunks.intValue == geometry.ChunkCount &&
                !created;
            if (unchanged)
            {
                // "Already imported" means BOTH sides are written: the proxy's arrays AND the
                // backing UdonBehaviour's copy. Verifying only the proxy is what let a failed
                // copy look like a no-op and never get repaired (see EnsureUdonSide).
                if (!EnsureUdonSide(host, created, report)) return report;
                report.Skipped = 1;
                report.Notes.Add("already imported (sha256 and counts unchanged): +0 ~0 -0; " +
                                 "the Udon side was re-copied and verified");
                return report;
            }

            Undo.RegisterCompleteObjectUndo(host, "Import terrain mesh");
            SetObject(serialized, "motor", motor);
            SetBool(serialized, "terrainMeshContactEnabled", true);
            SetString(serialized, "terrainId", terrainId);
            SetString(serialized, "sourceSha256", sourceSha);
            SetInt(serialized, "vertexCount", geometry.VertexCount);
            SetInt(serialized, "indexCount", geometry.IndexCount);
            SetInt(serialized, "triangleCount", geometry.TriangleCount);
            SetInt(serialized, "chunkCount", geometry.ChunkCount);
            SetVector3Array(serialized, "vertices", geometry.Vertices);
            SetIntArray(serialized, "indices", geometry.Indices);
            SetIntArray(serialized, "chunkStarts", geometry.ChunkStarts);
            SetIntArray(serialized, "chunkTriangleCounts", geometry.ChunkTriangleCounts);
            SetIntArray(serialized, "chunkZoneIds", geometry.ChunkZoneIds);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(host);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
            if (created) report.Created = 1; else report.Updated = 1;

            if (!EnsureUdonSide(host, created, report)) return report;
            report.Notes.Add("terrain host at " + HierarchyPath(terrainRoot) +
                             "; scene is dirty but NOT saved");
            return report;
        }

        // ---------------------------------------------------------- standing surface ---

        /// <summary>
        /// Makes sure <c>TerrainRoot/TerrainVisual</c> exists with a mesh, a renderer and a
        /// non-convex <see cref="MeshCollider"/>, so a player (VRChat's PhysX capsule) can stand
        /// on the terrain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Who uses this collider: ONLY the player. The ball never touches PhysX - its contact
        /// model reads <see cref="TerrainFieldHost"/>'s arrays - and nothing in Runtime issues a
        /// Physics query (no Raycast/Overlap/Cast/Check anywhere; no layer or LayerMask is read),
        /// so a collider on the Default layer cannot disturb the ball, the pickup scan or the
        /// riding station. It stays on layer 0 (Default) and is not a trigger: the layers the
        /// player collides with are VRChat's own, and a custom layer would silently remove the
        /// floor. It is never convex: a convex hull would turn the lawn into a lid.
        /// </para>
        /// <para>
        /// The mesh. The visual mesh is the same triangles as the contact data (unwelded, so each
        /// face is flat-shaded), one submesh per <c>terrain.materialSegments</c> entry, in an
        /// asset whose path is <see cref="PlacementV2Rules.TerrainVisualMeshPath"/>. An asset or a
        /// mesh already on the <c>MeshFilter</c> whose triangle count matches is kept as it is (so
        /// a hand-dressed visual and its GUID survive); a missing one is created, a stale one is
        /// overwritten in place. The standing surface follows the terrain's triangle winding as
        /// is: the sidecar's faces already point up in Unity's frame (checked: every flat lawn
        /// face has a +Y cross-product normal), which is also PhysX's front side.
        /// </para>
        /// </remarks>
        private static void EnsureStandingSurface(string levelId, string terrainId,
            TerrainGeometry geometry, bool isReference, TerrainImportReport report)
        {
            Transform levelRoot = FindLevelRoot(levelId);
            Transform terrainRoot = levelRoot == null ? null : levelRoot.Find(TerrainRootName);
            if (terrainRoot == null)
            {
                report.Error("[terrain_visual] no " + TerrainRootName + " under the level root; " +
                             "the standing surface was not built");
                return;
            }

            int created = 0;
            int updated = 0;

            Transform visual = terrainRoot.Find(TerrainVisualName);
            if (visual == null)
            {
                var go = new GameObject(TerrainVisualName);
                Undo.RegisterCreatedObjectUndo(go, "Create terrain visual");
                go.transform.SetParent(terrainRoot, false);
                visual = go.transform;
                created++;
            }

            // World pose identity: the vertices are world metres, so the visual and its collider
            // must not be moved, turned or scaled by anything above them.
            if (visual.localPosition != Vector3.zero || visual.localRotation != Quaternion.identity ||
                visual.localScale != Vector3.one)
            {
                Undo.RecordObject(visual, "Reset terrain visual pose");
                visual.localPosition = Vector3.zero;
                visual.localRotation = Quaternion.identity;
                visual.localScale = Vector3.one;
                updated++;
            }

            if (Vector3.Distance(visual.position, Vector3.zero) > 1e-4f ||
                Vector3.Distance(visual.lossyScale, Vector3.one) > 1e-4f)
            {
                report.Error("[terrain_visual] " + HierarchyPath(visual) + " is not at the world " +
                             "origin with unit scale (a parent moves it); the terrain vertices are " +
                             "world metres, so fix the parent chain");
                return;
            }

            if (visual.gameObject.layer != 0)
            {
                Undo.RecordObject(visual.gameObject, "Terrain visual layer");
                visual.gameObject.layer = 0;
                updated++;
            }

            if (!visual.gameObject.activeSelf)
            {
                Undo.RecordObject(visual.gameObject, "Terrain visual active");
                visual.gameObject.SetActive(true);
                updated++;
            }

            MeshFilter filter = visual.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = Undo.AddComponent<MeshFilter>(visual.gameObject);
                created++;
            }

            MeshRenderer renderer = visual.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<MeshRenderer>(visual.gameObject);
                created++;
            }

            Mesh mesh = filter.sharedMesh;
            if (mesh == null || MeshTriangleCount(mesh) != geometry.TriangleCount)
            {
                string assetPath = PlacementV2Rules.TerrainVisualMeshPath(levelId, terrainId,
                    isReference);
                mesh = EnsureVisualMeshAsset(assetPath, geometry, report, ref created, ref updated);
                if (mesh == null) return;
                Undo.RecordObject(filter, "Assign terrain visual mesh");
                filter.sharedMesh = mesh;
                updated++;
            }

            if (!MaterialsFit(renderer, mesh))
            {
                Undo.RecordObject(renderer, "Assign terrain visual materials");
                renderer.sharedMaterials = LoadSegmentMaterials(geometry, mesh.subMeshCount, report);
                updated++;
            }

            MeshCollider collider = visual.GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = Undo.AddComponent<MeshCollider>(visual.gameObject);
                created++;
            }

            if (collider.sharedMesh != mesh || collider.convex || collider.isTrigger ||
                !collider.enabled)
            {
                Undo.RecordObject(collider, "Terrain visual collider");
                collider.sharedMesh = mesh;
                collider.convex = false;
                collider.isTrigger = false;
                collider.enabled = true;
                updated++;
            }

            report.StandingCreated += created;
            report.StandingUpdated += updated;
            if (created > 0 || updated > 0)
            {
                EditorUtility.SetDirty(visual.gameObject);
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
            }

            report.Notes.Add("standing surface " + HierarchyPath(visual) + ": MeshCollider " +
                             "(non-convex, Default layer) on '" + mesh.name + "' (" +
                             MeshTriangleCount(mesh) + " tris, " + mesh.subMeshCount +
                             " submesh); +" + created + " ~" + updated +
                             "; for the player only - the ball never queries PhysX");
        }

        private static int MeshTriangleCount(Mesh mesh)
        {
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
            return (int)(indices / 3);
        }

        private static bool MaterialsFit(MeshRenderer renderer, Mesh mesh)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length != mesh.subMeshCount) return false;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) return false;
            }

            return true;
        }

        private static Material[] LoadSegmentMaterials(TerrainGeometry geometry, int count,
            TerrainImportReport report)
        {
            var materials = new Material[count];
            for (int i = 0; i < count; i++)
            {
                string path = geometry.Segments != null && i < geometry.Segments.Length &&
                              !string.IsNullOrEmpty(geometry.Segments[i].Material)
                    ? geometry.Segments[i].Material
                    : FallbackVisualMaterialPath;
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null && path != FallbackVisualMaterialPath)
                {
                    report.Notes.Add("terrain material '" + path + "' not found; the greybox ground " +
                                     "material is used for submesh " + i);
                    material = AssetDatabase.LoadAssetAtPath<Material>(FallbackVisualMaterialPath);
                }

                if (material == null)
                {
                    report.Notes.Add("no material at " + FallbackVisualMaterialPath +
                                     "; submesh " + i + " has none (pink)");
                }

                materials[i] = material;
            }

            return materials;
        }

        /// <summary>
        /// The visual <see cref="Mesh"/> asset at <paramref name="assetPath"/>: kept when its
        /// triangle count matches, overwritten in place (GUID kept) when stale, created when
        /// missing. Unwelded vertices (three per face) so each face is flat-shaded.
        /// </summary>
        private static Mesh EnsureVisualMeshAsset(string assetPath, TerrainGeometry geometry,
            TerrainImportReport report, ref int created, ref int updated)
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null && MeshTriangleCount(existing) == geometry.TriangleCount)
            {
                return existing;
            }

            Mesh built = BuildVisualMesh(geometry, System.IO.Path.GetFileNameWithoutExtension(assetPath));
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                updated++;
                report.Notes.Add("terrain visual mesh " + assetPath + " rebuilt in place (its " +
                                 "triangle count no longer matched the terrain)");
                return existing;
            }

            if (!EnsureAssetFolder(System.IO.Path.GetDirectoryName(assetPath).Replace('\\', '/')))
            {
                UnityEngine.Object.DestroyImmediate(built);
                report.Error("[terrain_visual] cannot create the folder for " + assetPath);
                return null;
            }

            AssetDatabase.CreateAsset(built, assetPath);
            AssetDatabase.SaveAssets();
            created++;
            report.Notes.Add("terrain visual mesh created at " + assetPath + " (" +
                             geometry.TriangleCount + " tris)");
            return built;
        }

        private static Mesh BuildVisualMesh(TerrainGeometry geometry, string name)
        {
            int indexCount = geometry.IndexCount;
            var vertices = new Vector3[indexCount];
            for (int i = 0; i < indexCount; i++) vertices[i] = geometry.Vertices[geometry.Indices[i]];

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            VisualSegment[] segments = geometry.Segments;
            mesh.subMeshCount = segments.Length;
            for (int s = 0; s < segments.Length; s++)
            {
                var triangles = new int[segments[s].IndexCount];
                for (int i = 0; i < triangles.Length; i++) triangles[i] = segments[s].FirstIndex + i;
                mesh.SetTriangles(triangles, s, true);
            }

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return true;
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return false;
            if (!EnsureAssetFolder(parent)) return false;
            return !string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, leaf));
        }

        // -------------------------------------------------------------------- udon ---

        /// <summary>
        /// Writes the proxy's serialized fields into the backing UdonBehaviour, retrying once
        /// after an asset refresh.
        /// </summary>
        /// <remarks>
        /// THE TRAP THIS EXISTS FOR (measured 2026-11-16): a <see cref="TerrainFieldHost"/> this
        /// importer ADDS has no U# program asset yet in that session, so <c>CopyProxyToUdon</c>
        /// throws <c>Value cannot be null. Parameter name: key</c> and the arrays land on the
        /// PROXY only. The next run then answered "already imported (+0 ~0 -0)" from the
        /// sha256/counts and never repaired the Udon side. A newly added U# behaviour has an EMPTY
        /// variable table for the rest of that session; a forced recompile (or save + reopen) is
        /// what fills it. This method never claims success without a successful copy.
        /// </remarks>
        private static bool TryCopyProxyToUdon(TerrainFieldHost host, bool forceCompile,
            TerrainImportReport report)
        {
            try
            {
                UdonSharpProgramAsset.CompileAllCsPrograms(forceCompile, false);
                UdonSharpEditorUtility.CopyProxyToUdon(host);
                report.UdonCopyIssued = true;
                report.UdonCopyError = null;
                return true;
            }
            catch (Exception exception)
            {
                report.UdonCopyIssued = false;
                report.UdonCopyError = exception.Message;
                return false;
            }
        }

        private static bool EnsureUdonSide(TerrainFieldHost host, bool createdHost,
            TerrainImportReport report)
        {
            if (TryCopyProxyToUdon(host, false, report))
            {
                if (createdHost)
                {
                    report.Notes.Add("U# program compiled and copied on the first attempt");
                }

                return true;
            }

            // FORCE the compile on the second attempt: CompileAllCsPrograms' first parameter is
            // forceCompile, and the non-forced call above is what CREATES the program asset for a
            // component added in this session; the forced one is what makes the copy able to land.
            if (TryCopyProxyToUdon(host, true, report))
            {
                report.Notes.Add("the Udon copy needed a second, force-compiled attempt (a host " +
                                 "added in this session starts with an empty U# variable table); " +
                                 "it succeeded now");
                return true;
            }

            report.Error("[terrain_udon] CopyProxyToUdon failed twice (" + report.UdonCopyError +
                         "). The arrays are on the proxy but NOT on the UdonBehaviour, so this run " +
                         "is deliberately NOT counted as imported. A TerrainFieldHost added in this " +
                         "session has an empty U# variable table until its program exists: save the " +
                         "scene, reopen it, then run the terrain import again");
            return false;
        }

        // -------------------------------------------------------------------- helpers ---

        private static string ActiveLevelId()
        {
            GameObject root = GameObject.Find(ManagedRootName);
            if (root == null || root.transform.childCount != 1) return null;
            return root.transform.GetChild(0).name;
        }

        private static Transform FindLevelRoot(string levelId)
        {
            GameObject root = GameObject.Find(ManagedRootName);
            if (root == null) return null;
            // A nested id ("physics_test_field/C21") names a DIRECTORY, not a hierarchy: the
            // managed root is always LevelContent/<meta.id>, so only the leaf can match.
            Transform child = root.transform.Find(LevelPaths.LeafOf(levelId));
            return child;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        private static string HierarchyPath(Transform transform)
        {
            if (transform == null) return "";
            var text = new StringBuilder(transform.name);
            Transform parent = transform.parent;
            while (parent != null)
            {
                text.Insert(0, parent.name + "/");
                parent = parent.parent;
            }
            return text.ToString();
        }

        private static void SetObject(SerializedObject serialized, string name, UnityEngine.Object value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.objectReferenceValue = value;
        }

        private static void SetBool(SerializedObject serialized, string name, bool value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.boolValue = value;
        }

        private static void SetInt(SerializedObject serialized, string name, int value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.intValue = value;
        }

        private static void SetString(SerializedObject serialized, string name, string value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.stringValue = value;
        }

        private static void SetVector3Array(SerializedObject serialized, string name, Vector3[] values)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null) return;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).vector3Value = values[i];
        }

        private static void SetIntArray(SerializedObject serialized, string name, int[] values)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null) return;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).intValue = values[i];
        }

        private static bool TryReadVertices(LevelJsonNode node, out Vector3[] values, out string error)
        {
            values = null;
            error = null;
            if (node == null || node.Kind != LevelJsonKind.Array)
            {
                error = "[terrain_mesh] vertices must be an array";
                return false;
            }
            values = new Vector3[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode point = node.At(i);
                if (point == null || point.Kind != LevelJsonKind.Array || point.Count != 3)
                {
                    error = "[terrain_mesh] vertices[" + i + "] must be [x,y,z]";
                    return false;
                }
                values[i] = new Vector3(point.At(0).AsFloat(), point.At(1).AsFloat(),
                    point.At(2).AsFloat());
            }
            return true;
        }

        private static bool TryReadInts(LevelJsonNode node, out int[] values, out string error)
        {
            values = null;
            error = null;
            if (node == null || node.Kind != LevelJsonKind.Array)
            {
                error = "[terrain_mesh] expected an integer array";
                return false;
            }
            values = new int[node.Count];
            for (int i = 0; i < node.Count; i++) values[i] = node.At(i).AsInt();
            return true;
        }

        // ----------------------------------------------------------------------- JSON ---
        //
        // The reader itself lives in LevelJson.cs: one implementation for every v2 document,
        // including the jagged arrays (meta.bounds, walls[].points) that JsonUtility cannot
        // read. This file used to carry a private copy; it does not any more.
    }
}
