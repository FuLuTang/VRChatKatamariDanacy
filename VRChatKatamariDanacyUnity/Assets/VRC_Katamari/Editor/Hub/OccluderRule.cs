using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The static marks the all-maps hub puts on the geometry it assembles: fixed structure
    /// geometry becomes an Umbra occludee, the closed, opaque, structure-sized part of it is an
    /// occluder too, and the reference island's terrain visual is additionally marked for
    /// static batching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Umbra assumes every occluder is a closed solid. An open mesh (a terrain surface, a
    /// single-plane ground) is still treated as solid, so it wrongly hides what stands behind
    /// it: measured on this project, the Cancer island's single-surface terrain visual
    /// <c>area2_sector0_model3</c> made 9.56% of the courtyard view disappear (49544 of
    /// 518400 px at 960x540; turning off only that mesh brought the difference to 0). A
    /// cutout/transparent mesh has holes Umbra cannot see, so it is not an occluder either
    /// (<c>area2_sector0_model4</c>: 8 cutout and 2 transparent submeshes out of 127; as an
    /// occluder it culled 22 props that are not OccludeeStatic, so the draw saving was zero).
    /// </para>
    /// <para>
    /// The rule is stated by what the geometry is, not by a list of paths. The hub's fixed
    /// structure geometry is marked <see cref="StaticEditorFlags.OccludeeStatic"/>: every
    /// level document's structures (the <c>Structures</c> root below a level content root),
    /// the reference island's <c>TerrainVisual</c> terrain, the terrain lab's island-root
    /// <c>Ground</c> slab, and the hub's own return buttons. The part of it that is also a
    /// closed mesh, opaque, at least 1 m on two axes and with no triangle over 80 m² is marked
    /// <see cref="StaticEditorFlags.OccluderStatic"/> as well. The operator's measured set
    /// (30 occluders, 48 occludees) is what this rule reproduces on the scene it was measured
    /// on; <c>ALL_MAPS_HUB.md</c> section 6.1.1 states why a regenerated scene now has 4 fewer
    /// of each (the wall panel replaced the hub's select pads).
    /// </para>
    /// <para>
    /// The numbers behind the limits. The measured 30 occluders are 12-triangle boxes; the
    /// largest triangle among them is <c>hub_floor</c>'s 16 x 8 m slab at 64 m², and the
    /// smallest triangle of the three excluded ground slabs is the picnic ground's
    /// 13.4 x 13.4 m top at 89.8 m², so 80 m² separates the two groups (the sandbox ground's
    /// is 976 m² and the terrain lab ground's 3200 m²). Thin pieces (C05/C06 ramps at
    /// 0.63 m square, the shell tree trunk, the signpost, the ramps to the bed and table) are
    /// skipped by the two-axes-at-1-m rule, which is why the operator's conservative pass
    /// dropped them as well. The one mesh that must not be touched is the reference island's
    /// <c>area2_sector1_model0</c> background shell: its bounds are 242 m across, so anything
    /// over <see cref="MaxStructureExtentM"/> is treated as background, not structure (the
    /// largest real island mesh, <c>area2_sector0_model0</c>, is 105.9 m).
    /// </para>
    /// <para>
    /// Static batching. The same pass marks the reference island's <c>TerrainVisual</c> pieces
    /// (all but the background shell) <see cref="StaticEditorFlags.BatchingStatic"/>: they are
    /// fixed, no prop can absorb them, and nothing reads their meshes at runtime (the
    /// <c>TerrainFieldHost</c> holds its own baked world-space arrays, and the collision mesh is
    /// the separate, unrendered <c>TerrainCollision</c> object). Of the 7 parts, the 6 marked
    /// ones carry 204 of the 205 material slots but only 157 distinct materials, because the
    /// same terrain material is used by several parts: static batching merges those into one
    /// draw per material, so its draw count is ~157 instead of 205 (the saving comes from the
    /// repeated materials, not from splitting a part's 127 submeshes). The flag is ORed onto
    /// whatever <see cref="StaticEditorFlags"/> the object already has; this class never writes
    /// a whole flag set that would drop another mark.
    /// </para>
    /// <para>
    /// This class only sets the flags. The bake is the operator's step: <c>Occlusion Culling</c>
    /// window, bake with smallestOccluder=1, smallestHole=0.05, backfaceThreshold=100, then
    /// save (ALL_MAPS_HUB.md section 7).
    /// </para>
    /// </remarks>
    public static class OccluderRule
    {
        /// <summary>An occluder has to span at least this much on two axes.</summary>
        public const float LargeExtentM = 1f;

        /// <summary>An occluder's largest triangle, in square metres (see the class remarks).</summary>
        public const float MaxOccluderTriangleAreaM2 = 80f;

        /// <summary>Anything longer than this on an axis is a background shell, not structure.</summary>
        public const float MaxStructureExtentM = 150f;

        /// <summary>Unity's first non-opaque render queue (AlphaTest); cutout and transparent start here.</summary>
        public const int FirstNonOpaqueQueue = 2450;

        /// <summary>Vertices closer than this are the same point when checking closedness.</summary>
        public const float WeldMetres = 0.0001f;

        private const string StructuresRootName = "Structures";
        private const string TerrainVisualRootName = "TerrainVisual";
        private const string RootGroundName = "Ground";
        private const string IslandShellPrefix = "Island_";
        private const string ReturnButtonPrefix = HubAssembler.ReturnButtonName + "_";

        private const string ReasonBackground = "background-shell";
        private const string ReasonSmall = "small";
        private const string ReasonNonOpaque = "non-opaque";
        private const string ReasonUnreadable = "no-mesh";
        private const string ReasonLargeTriangle = "large-triangle";
        private const string ReasonOpenMesh = "open-mesh";

        /// <summary>What a fixed-geometry renderer gets, and why.</summary>
        public enum OcclusionMark
        {
            /// <summary>Not ours to mark: a background shell, or geometry that is not fixed structure.</summary>
            None = 0,

            /// <summary>The geometry is fixed but is not a usable occluder.</summary>
            Occludee = 1,

            /// <summary>The geometry is a closed, opaque, structure-sized solid: use it as an occluder.</summary>
            Occluder = 2,
        }

        /// <summary>What <see cref="Mark"/> did: counts, the marked occluders and every skipped piece.</summary>
        public sealed class MarkResult
        {
            public int Occluders;
            public int Occludees;

            /// <summary>Fixed structure renderers this pass wants <see cref="StaticEditorFlags.BatchingStatic"/> on.</summary>
            public int BatchingStatic;

            public int Changed;

            /// <summary>The marked occluder paths, ordinal-sorted.</summary>
            public readonly List<string> OccluderPaths = new List<string>();

            /// <summary>The fixed-geometry pieces that are not occluders, as "reason path", ordinal-sorted.</summary>
            public readonly List<string> Skips = new List<string>();

            public readonly List<string> Problems = new List<string>();
        }

        // ------------------------------------------------------------ fixed geometry ---

        /// <summary>
        /// True for the hub's own fixed structure geometry: a renderer below a level's
        /// <c>Structures</c> root or the reference island's <c>TerrainVisual</c> root, an
        /// island's root <c>Ground</c> slab (the terrain lab), or one of the hub's return
        /// buttons. Absorbable props, the ball and infrastructure are not fixed structure.
        /// </summary>
        public static bool IsFixedStructureGeometry(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] segments = path.Split('/');

            // Ancestors, not the object itself: a structure's renderer can be the object.
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], StructuresRootName, StringComparison.Ordinal)
                    || string.Equals(segments[i], TerrainVisualRootName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // A lab island keeps its ground as an island-root Ground mesh, with no level
            // document behind it (the terrain contact lab's 80 x 80 m slab).
            if (segments.Length == 2
                && string.Equals(segments[1], RootGroundName, StringComparison.Ordinal)
                && segments[0].StartsWith(IslandShellPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            // The hub generator's return buttons are scene roots, not island children.
            return segments.Length == 1
                && segments[0].StartsWith(ReturnButtonPrefix, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------ pure decisions ---

        /// <summary>True when two of the three extents reach <see cref="LargeExtentM"/>.</summary>
        public static bool IsLargeEnoughAsOccluder(float[] boundsSize)
        {
            if (boundsSize == null || boundsSize.Length != 3) return false;
            int large = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!IsFinite(boundsSize[i])) return false;
                if (boundsSize[i] >= LargeExtentM - 0.0001f) large++;
            }
            return large >= 2;
        }

        /// <summary>True when one extent exceeds <see cref="MaxStructureExtentM"/>: a backdrop shell.</summary>
        public static bool IsBackgroundShell(float[] boundsSize)
        {
            if (boundsSize == null || boundsSize.Length != 3) return true;
            for (int i = 0; i < 3; i++)
            {
                if (!IsFinite(boundsSize[i])) return true;
                if (boundsSize[i] > MaxStructureExtentM) return true;
            }
            return false;
        }

        /// <summary>
        /// True when every material is present, below Unity's AlphaTest queue, and not a
        /// shader named Transparent/Cutout. A missing material or a mesh with no material is
        /// not opaque.
        /// </summary>
        public static bool IsOpaqueGeometry(int[] renderQueues, bool[] materialPresent,
            string[] shaderNames)
        {
            if (renderQueues == null || materialPresent == null || shaderNames == null) return false;
            if (renderQueues.Length == 0) return false;
            if (materialPresent.Length != renderQueues.Length
                || shaderNames.Length != renderQueues.Length)
            {
                return false;
            }
            for (int i = 0; i < renderQueues.Length; i++)
            {
                if (!materialPresent[i]) return false;
                if (renderQueues[i] >= FirstNonOpaqueQueue) return false;
                string shader = shaderNames[i];
                if (shader == null) continue;
                if (shader.IndexOf("Transparent", StringComparison.Ordinal) >= 0
                    || shader.IndexOf("Cutout", StringComparison.Ordinal) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when the triangles form a closed surface: every edge, after welding vertices
        /// that sit on the same point, is used by exactly two triangles. Duplicate vertices are
        /// welded because Unity's built-in primitives (and many imported meshes) duplicate them
        /// per face for normals and UVs; a single-plane ground, a terrain surface or a box with
        /// a missing face is open.
        /// </summary>
        public static bool IsClosedMesh(float[] worldPositions, int[] triangles)
        {
            if (worldPositions == null || triangles == null) return false;
            if (worldPositions.Length % 3 != 0 || triangles.Length == 0
                || triangles.Length % 3 != 0)
            {
                return false;
            }
            int vertexCount = worldPositions.Length / 3;
            if (vertexCount < 4) return false;

            var welded = new Dictionary<VertexKey, int>(vertexCount);
            var vertexOf = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var key = new VertexKey(
                    Quantize(worldPositions[i * 3]),
                    Quantize(worldPositions[i * 3 + 1]),
                    Quantize(worldPositions[i * 3 + 2]));
                int id;
                if (!welded.TryGetValue(key, out id))
                {
                    id = welded.Count;
                    welded.Add(key, id);
                }
                vertexOf[i] = id;
            }

            var edges = new Dictionary<long, int>(triangles.Length);
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = IndexOf(triangles[t], vertexOf);
                int b = IndexOf(triangles[t + 1], vertexOf);
                int c = IndexOf(triangles[t + 2], vertexOf);
                if (a < 0 || b < 0 || c < 0) return false;
                if (a == b || b == c || a == c) return false;   // a degenerate face is not a solid
                AddEdge(edges, a, b);
                AddEdge(edges, b, c);
                AddEdge(edges, c, a);
            }

            if (edges.Count == 0) return false;
            foreach (KeyValuePair<long, int> edge in edges)
            {
                if (edge.Value != 2) return false;
            }
            return true;
        }

        /// <summary>
        /// The largest triangle area in square metres, from world-space positions. -1 when the
        /// positions and triangles do not describe a mesh.
        /// </summary>
        public static float MaxTriangleArea(float[] worldPositions, int[] triangles)
        {
            if (worldPositions == null || triangles == null) return -1f;
            if (worldPositions.Length % 3 != 0 || triangles.Length == 0
                || triangles.Length % 3 != 0)
            {
                return -1f;
            }
            int vertexCount = worldPositions.Length / 3;
            float largest = 0f;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t];
                int b = triangles[t + 1];
                int c = triangles[t + 2];
                if (a < 0 || b < 0 || c < 0
                    || a >= vertexCount || b >= vertexCount || c >= vertexCount)
                {
                    return -1f;
                }
                float ax = worldPositions[a * 3], ay = worldPositions[a * 3 + 1],
                    az = worldPositions[a * 3 + 2];
                float abx = worldPositions[b * 3] - ax, aby = worldPositions[b * 3 + 1] - ay,
                    abz = worldPositions[b * 3 + 2] - az;
                float acx = worldPositions[c * 3] - ax, acy = worldPositions[c * 3 + 1] - ay,
                    acz = worldPositions[c * 3 + 2] - az;
                float cx = aby * acz - abz * acy;
                float cy = abz * acx - abx * acz;
                float cz = abx * acy - aby * acx;
                float area = 0.5f * (float)Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (area > largest) largest = area;
            }
            return largest;
        }

        /// <summary>
        /// The mark for one piece of fixed structure geometry, with why when it is not an
        /// occluder. <paramref name="worldPositions"/> and <paramref name="triangles"/> may be
        /// null when the mesh could not be read: the result is then an occludee only.
        /// </summary>
        public static OcclusionMark ChooseMark(float[] boundsSize, int[] renderQueues,
            bool[] materialPresent, string[] shaderNames, float[] worldPositions,
            int[] triangles, out string reason)
        {
            reason = "";
            if (IsBackgroundShell(boundsSize))
            {
                reason = ReasonBackground;
                return OcclusionMark.None;
            }
            if (!IsLargeEnoughAsOccluder(boundsSize))
            {
                reason = ReasonSmall;
                return OcclusionMark.Occludee;
            }
            if (!IsOpaqueGeometry(renderQueues, materialPresent, shaderNames))
            {
                reason = ReasonNonOpaque;
                return OcclusionMark.Occludee;
            }
            float largest = MaxTriangleArea(worldPositions, triangles);
            if (largest < 0f)
            {
                reason = ReasonUnreadable;
                return OcclusionMark.Occludee;
            }
            if (largest > MaxOccluderTriangleAreaM2)
            {
                reason = ReasonLargeTriangle;
                return OcclusionMark.Occludee;
            }
            if (!IsClosedMesh(worldPositions, triangles))
            {
                reason = ReasonOpenMesh;
                return OcclusionMark.Occludee;
            }
            return OcclusionMark.Occluder;
        }

        // ------------------------------------------------------------ static batching ---

        /// <summary>
        /// True for a renderer below a <c>TerrainVisual</c> root: the reference island's drawn
        /// terrain surface. It is fixed (no prop can absorb it, nothing moves it), so static
        /// batching may merge its renderers; the collision mesh is a different object
        /// (<c>TerrainCollision</c>) and is not a renderer at all.
        /// </summary>
        public static bool IsTerrainVisualGeometry(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] segments = path.Split('/');
            // Ancestors, not the object itself: a part renderer sits below TerrainVisual.
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], TerrainVisualRootName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when a piece of fixed structure geometry should be batched statically: the
        /// reference island's terrain visual, except a background shell (it is a single unique
        /// backdrop piece, so batching it saves no draw and would only add combined-mesh
        /// memory; the same limit already keeps it out of the occlusion marks).
        /// </summary>
        public static bool WantsBatchingStatic(string path, float[] boundsSize)
        {
            return IsTerrainVisualGeometry(path) && !IsBackgroundShell(boundsSize);
        }

        /// <summary>The flags with <see cref="StaticEditorFlags.BatchingStatic"/> ORed in; other flags are kept.</summary>
        public static StaticEditorFlags WithBatchingStatic(StaticEditorFlags flags)
        {
            return flags | StaticEditorFlags.BatchingStatic;
        }

        // ------------------------------------------------------------ scene marking ---

        /// <summary>
        /// Marks every fixed-structure renderer of an open scene: OccludeeStatic on all of it,
        /// OccluderStatic on the closed opaque part, BatchingStatic on the reference island's
        /// terrain visual. Only the flags are written (the bake is the operator's step); the
        /// scene is marked dirty when something changed.
        /// </summary>
        public static MarkResult Mark(Scene scene)
        {
            var result = new MarkResult();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                result.Problems.Add("static occlusion: the scene to mark is not open");
                return result;
            }

            var occluders = new List<string>();
            var skips = new List<string>();
            bool dirty = false;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                Renderer[] renderers = roots[r].GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || !(renderer is MeshRenderer)) continue;
                    // A renderer that is not drawn has nothing to cull, and is not ours to
                    // mark: the reference island keeps its original props' renderers off.
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;

                    string path = AnimationUtility.CalculateTransformPath(renderer.transform, null);
                    if (!IsFixedStructureGeometry(path)) continue;

                    Vector3 extent = renderer.bounds.size;
                    float[] boundsSize = { extent.x, extent.y, extent.z };
                    Material[] materials = renderer.sharedMaterials;
                    var queues = new int[materials.Length];
                    var present = new bool[materials.Length];
                    var shaders = new string[materials.Length];
                    for (int m = 0; m < materials.Length; m++)
                    {
                        present[m] = materials[m] != null;
                        queues[m] = present[m] ? materials[m].renderQueue : 0;
                        shaders[m] = present[m] ? materials[m].shader.name : "";
                    }

                    // Reading the mesh is the only expensive step, so only when the cheap
                    // checks leave this piece as a possible occluder.
                    float[] positions = null;
                    int[] triangles = null;
                    if (!IsBackgroundShell(boundsSize) && IsLargeEnoughAsOccluder(boundsSize)
                        && IsOpaqueGeometry(queues, present, shaders))
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        TryReadWorldMesh(filter == null ? null : filter.sharedMesh,
                            renderer.transform.localToWorldMatrix, out positions, out triangles);
                    }

                    string reason;
                    OcclusionMark mark = ChooseMark(boundsSize, queues, present, shaders,
                        positions, triangles, out reason);

                    StaticEditorFlags flags =
                        GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                    StaticEditorFlags wanted = flags;
                    if (mark != OcclusionMark.None)
                    {
                        wanted |= StaticEditorFlags.OccludeeStatic;
                        if (mark == OcclusionMark.Occluder)
                        {
                            wanted |= StaticEditorFlags.OccluderStatic;
                        }
                        else
                        {
                            wanted &= ~StaticEditorFlags.OccluderStatic;
                        }
                    }
                    // Static batching is a different question from occlusion: the reference
                    // island's terrain visual is fixed, non-absorbable geometry whatever Umbra
                    // makes of it, so it is batched too. The flag is ORed onto whatever is
                    // already there (another pass may have set other StaticEditorFlags), never
                    // written as a replacement.
                    if (WantsBatchingStatic(path, boundsSize))
                    {
                        wanted = WithBatchingStatic(wanted);
                        result.BatchingStatic++;
                    }
                    if (wanted != flags)
                    {
                        Undo.RecordObject(renderer.gameObject, "Mark static occlusion");
                        GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, wanted);
                        EditorUtility.SetDirty(renderer.gameObject);
                        result.Changed++;
                        dirty = true;
                    }

                    if (mark == OcclusionMark.Occluder)
                    {
                        result.Occluders++;
                        result.Occludees++;
                        occluders.Add(path);
                    }
                    else
                    {
                        if (mark == OcclusionMark.Occludee) result.Occludees++;
                        skips.Add(reason + " " + path);
                    }
                }
            }

            if (dirty) EditorSceneManager.MarkSceneDirty(scene);
            occluders.Sort(StringComparer.Ordinal);
            skips.Sort(StringComparer.Ordinal);
            result.OccluderPaths.AddRange(occluders);
            result.Skips.AddRange(skips);
            return result;
        }

        // ------------------------------------------------------------ helpers ---

        /// <summary>Reads a mesh's world-space vertices and triangles; false when it is not readable.</summary>
        private static bool TryReadWorldMesh(Mesh mesh, Matrix4x4 localToWorld,
            out float[] worldPositions, out int[] triangles)
        {
            worldPositions = null;
            triangles = null;
            if (mesh == null) return false;
            try
            {
                Vector3[] vertices = mesh.vertices;
                int[] indices = mesh.triangles;
                if (vertices == null || indices == null || indices.Length == 0) return false;
                var world = new float[vertices.Length * 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 point = localToWorld.MultiplyPoint3x4(vertices[i]);
                    world[i * 3] = point.x;
                    world[i * 3 + 1] = point.y;
                    world[i * 3 + 2] = point.z;
                }
                worldPositions = world;
                triangles = indices;
                return true;
            }
            catch (UnityException)
            {
                // Read/Write off: the mesh cannot be judged, and the rule treats it as not
                // closed rather than guessing (OccludeeStatic only).
                return false;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static int IndexOf(int index, int[] vertexOf)
        {
            return index < 0 || index >= vertexOf.Length ? -1 : vertexOf[index];
        }

        private static long Quantize(float value)
        {
            return (long)Math.Round(value / WeldMetres);
        }

        private static void AddEdge(Dictionary<long, int> edges, int a, int b)
        {
            int low = Math.Min(a, b);
            int high = Math.Max(a, b);
            long key = ((long)low << 32) | (uint)high;
            int count;
            edges.TryGetValue(key, out count);
            edges[key] = count + 1;
        }

        // Three quantised metres; a struct key keeps the weld exact with no hash collisions.
        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private readonly long x;
            private readonly long y;
            private readonly long z;

            public VertexKey(long x, long y, long z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public bool Equals(VertexKey other)
            {
                return x == other.x && y == other.y && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + x.GetHashCode();
                    hash = hash * 31 + y.GetHashCode();
                    hash = hash * 31 + z.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
