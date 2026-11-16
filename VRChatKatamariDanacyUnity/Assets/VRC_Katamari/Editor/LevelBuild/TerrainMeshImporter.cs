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
    /// Idempotent by source sha256 + counts: a second import of the same file writes
    /// nothing and logs <c>+0 ~0 -0</c>. It never saves a scene. A reference level
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
            public int Triangles;
            public int Vertices;
            public int Chunks;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Notes = new List<string>();

            public bool Ok { get { return Errors.Count == 0; } }

            public string Headline()
            {
                return "[terrain] +" + Created + " ~" + Updated + " -0 terrain=" + TerrainId +
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
            string levelDir = Path.Combine(ProjectRoot(), "Docs/Design/Levels", levelId);
            string path = FindPlacementV2Path(levelDir);
            if (path == null)
            {
                report.Error("[terrain_source] no placement.v2.json (or v2 placement.json) under " +
                             levelDir);
                return report;
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                Node root = new JsonParser(json).Parse();
                return ImportDocument(levelId, levelDir, path, json, root, report);
            }
            catch (Exception exception)
            {
                report.Error("[terrain_source] " + exception.Message);
                return report;
            }
        }

        private static TerrainImportReport ImportDocument(string levelId, string levelDir,
            string path, string json, Node root, TerrainImportReport report)
        {
            if (root == null || root.Kind != NodeKind.Object)
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

            Node meta = root.Get("meta");
            Node terrain = root.Get("terrain");
            if (terrain == null || terrain.Kind != NodeKind.Object)
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

            return WriteHost(levelId, terrainId, sourceSha, geometry, report);
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
        }

        private static bool TryReadGeometry(string levelDir, Node terrain,
            out TerrainGeometry geometry, out string error)
        {
            geometry = null;
            error = null;
            Node meshRef = terrain.Get("meshRef");
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
                Node triangles = terrain.Get("triangles");
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
                Node zoneNode = triangles.Get("zoneIds");
                if (zoneNode != null && !TryReadInts(zoneNode, out zoneIds, out error)) return false;
            }

            int triangleCount2 = indices.Length / 3;
            if (indices.Length != triangleCount2 * 3 || triangleCount2 <= 0)
            {
                error = "[terrain_mesh] indices length must be 3 × triangle count";
                return false;
            }
            if (vertices.Length == 0) { error = "[terrain_mesh] no vertices"; return false; }
            Node trianglesBlock = terrain.Get("triangles");
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

        private static bool TryReadChunks(Node chunks, int triangleCount, int[] zoneIds,
            out int[] starts, out int[] counts, out int[] zones, out string error)
        {
            error = null;
            var startList = new List<int>();
            var countList = new List<int>();
            var zoneList = new List<int>();
            if (chunks != null && chunks.Kind == NodeKind.Array && chunks.Count > 0)
            {
                int expected = 0;
                for (int i = 0; i < chunks.Count; i++)
                {
                    Node range = chunks.At(i).Get("triangleRange");
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
            if (sha != null && tri != null && verts != null && chunks != null &&
                sha.stringValue == sourceSha && tri.intValue == geometry.TriangleCount &&
                verts.intValue == geometry.VertexCount && chunks.intValue == geometry.ChunkCount &&
                !created)
            {
                report.Skipped = 1;
                report.Notes.Add("already imported (sha256 and counts unchanged): +0 ~0 -0");
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

            try
            {
                UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
                UdonSharpEditorUtility.CopyProxyToUdon(host);
            }
            catch (Exception exception)
            {
                report.Error("[terrain_udon] CopyProxyToUdon threw: " + exception.Message);
            }
            report.Notes.Add("terrain host at " + HierarchyPath(terrainRoot) +
                             "; scene is dirty but NOT saved");
            return report;
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
            Transform child = root.transform.Find(levelId);
            return child;
        }

        private static string FindPlacementV2Path(string levelDir)
        {
            string v2 = Path.Combine(levelDir, "placement.v2.json");
            if (File.Exists(v2)) return v2;
            string fallback = Path.Combine(levelDir, "placement.json");
            if (File.Exists(fallback))
            {
                string text = File.ReadAllText(fallback, Encoding.UTF8);
                if (text.Contains("\"katamari.placement/2\"")) return fallback;
            }
            return null;
        }

        private static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
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

        private static bool TryReadVertices(Node node, out Vector3[] values, out string error)
        {
            values = null;
            error = null;
            if (node == null || node.Kind != NodeKind.Array)
            {
                error = "[terrain_mesh] vertices must be an array";
                return false;
            }
            values = new Vector3[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                Node point = node.At(i);
                if (point == null || point.Kind != NodeKind.Array || point.Count != 3)
                {
                    error = "[terrain_mesh] vertices[" + i + "] must be [x,y,z]";
                    return false;
                }
                values[i] = new Vector3(point.At(0).AsFloat(), point.At(1).AsFloat(),
                    point.At(2).AsFloat());
            }
            return true;
        }

        private static bool TryReadInts(Node node, out int[] values, out string error)
        {
            values = null;
            error = null;
            if (node == null || node.Kind != NodeKind.Array)
            {
                error = "[terrain_mesh] expected an integer array";
                return false;
            }
            values = new int[node.Count];
            for (int i = 0; i < node.Count; i++) values[i] = node.At(i).AsInt();
            return true;
        }

        // ----------------------------------------------------------------------- JSON ---

        private enum NodeKind { Null, Bool, Number, String, Array, Object }

        private sealed class Node
        {
            public NodeKind Kind;
            public bool BoolValue;
            public double NumberValue;
            public string StringValue;
            public List<Node> Items;
            public Dictionary<string, Node> Fields;

            public int Count { get { return Items == null ? 0 : Items.Count; } }
            public Node At(int index)
            {
                return Items != null && index >= 0 && index < Items.Count ? Items[index] : null;
            }
            public Node Get(string name)
            {
                return Fields != null && Fields.ContainsKey(name) ? Fields[name] : null;
            }
            public string GetString(string name)
            {
                Node node = Get(name);
                return node == null ? null : node.StringValue;
            }
            public int GetInt(string name, int fallback)
            {
                Node node = Get(name);
                return node == null ? fallback : node.AsInt();
            }
            public int AsInt()
            {
                return Kind == NodeKind.Number ? (int)Math.Round(NumberValue) : 0;
            }
            public float AsFloat()
            {
                return Kind == NodeKind.Number ? (float)NumberValue : 0f;
            }
        }

        private sealed class JsonParser
        {
            private readonly string text;
            private int index;

            public JsonParser(string text) { this.text = text; }

            public Node Parse()
            {
                Node node = ParseValue();
                SkipWhitespace();
                if (index != text.Length) throw new FormatException("trailing JSON at " + index);
                return node;
            }

            private Node ParseValue()
            {
                SkipWhitespace();
                if (index >= text.Length) throw new FormatException("unexpected end of JSON");
                char c = text[index];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return new Node { Kind = NodeKind.String, StringValue = ParseString() };
                if (c == 't' || c == 'f')
                {
                    bool value = c == 't';
                    Expect(value ? "true" : "false");
                    return new Node { Kind = NodeKind.Bool, BoolValue = value };
                }
                if (c == 'n') { Expect("null"); return new Node { Kind = NodeKind.Null }; }
                return new Node { Kind = NodeKind.Number, NumberValue = ParseNumber() };
            }

            private Node ParseObject()
            {
                index++; // {
                var fields = new Dictionary<string, Node>(StringComparer.Ordinal);
                SkipWhitespace();
                if (index < text.Length && text[index] == '}') { index++; return new Node { Kind = NodeKind.Object, Fields = fields }; }
                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    if (index >= text.Length || text[index] != ':')
                        throw new FormatException("expected ':' at " + index);
                    index++;
                    fields[key] = ParseValue();
                    SkipWhitespace();
                    if (index >= text.Length) throw new FormatException("unterminated object");
                    if (text[index] == ',') { index++; continue; }
                    if (text[index] == '}') { index++; break; }
                    throw new FormatException("expected ',' or '}' at " + index);
                }
                return new Node { Kind = NodeKind.Object, Fields = fields };
            }

            private Node ParseArray()
            {
                index++; // [
                var items = new List<Node>();
                SkipWhitespace();
                if (index < text.Length && text[index] == ']') { index++; return new Node { Kind = NodeKind.Array, Items = items }; }
                while (true)
                {
                    items.Add(ParseValue());
                    SkipWhitespace();
                    if (index >= text.Length) throw new FormatException("unterminated array");
                    if (text[index] == ',') { index++; continue; }
                    if (text[index] == ']') { index++; break; }
                    throw new FormatException("expected ',' or ']' at " + index);
                }
                return new Node { Kind = NodeKind.Array, Items = items };
            }

            private string ParseString()
            {
                if (index >= text.Length || text[index] != '"')
                    throw new FormatException("expected string at " + index);
                index++;
                var value = new StringBuilder();
                while (index < text.Length)
                {
                    char c = text[index++];
                    if (c == '"') return value.ToString();
                    if (c != '\\') { value.Append(c); continue; }
                    if (index >= text.Length) break;
                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length) throw new FormatException("bad \\u escape");
                            value.Append((char)int.Parse(text.Substring(index, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default: throw new FormatException("bad escape \\" + escape);
                    }
                }
                throw new FormatException("unterminated string");
            }

            private double ParseNumber()
            {
                int start = index;
                while (index < text.Length && "-+.eE0123456789".IndexOf(text[index]) >= 0) index++;
                if (start == index) throw new FormatException("expected number at " + index);
                return double.Parse(text.Substring(start, index - start),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void Expect(string literal)
            {
                if (index + literal.Length > text.Length ||
                    string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                    throw new FormatException("expected " + literal + " at " + index);
                index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            }
        }
    }
}
