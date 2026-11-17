using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The split surface of a level that declares <c>terrain.visual</c> (LEVEL_FORMAT_V2 §3.8,
    /// §11.2): what is DRAWN (the original's graphic sectors, with textures) and what the player
    /// STANDS ON (the original's collision mesh) are different meshes in different objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hierarchy under <c>TerrainRoot</c>: <c>TerrainVisual/&lt;part id&gt;</c> (one
    /// <c>MeshFilter</c> + <c>MeshRenderer</c> per <c>terrain.visual.parts[]</c>; no collider) and
    /// <c>TerrainCollision</c> (a non-convex Default-layer <c>MeshCollider</c> on the contact
    /// mesh, no renderer; only for a level with <c>meta.playerStart</c>). The collision object is
    /// never drawn: the contact mesh carries the original's invisible 15 m wall ring.
    /// </para>
    /// <para>
    /// Assets. The contact mesh keeps its asset (and GUID): the pre-split file
    /// (<see cref="PlacementV2Rules.TerrainVisualMeshPath"/>) is MOVED to
    /// <see cref="PlacementV2Rules.TerrainCollisionMeshPath"/> with <c>AssetDatabase.MoveAsset</c>.
    /// Each part's mesh, each texture (imported from the converter's PNG, point/repeat/no mips)
    /// and each material are created when missing and corrected in place when stale, never
    /// deleted and recreated; the two mesh kinds are compared separately (the contact mesh by
    /// triangle count, a part mesh by name = part id + sidecar hash, vertex, triangle and
    /// submesh count). Everything lands under <c>Assets/_RefUnlicensed/</c> for a reference level.
    /// </para>
    /// </remarks>
    public static partial class TerrainMeshImporter
    {
        /// <summary>The collider child of <c>TerrainRoot</c> (contact mesh, never rendered).</summary>
        public const string TerrainCollisionName = "TerrainCollision";

        private sealed class VisualCounters
        {
            public int Created;
            public int Updated;
        }

        private static void EnsureSplitSurface(string levelId, string terrainId,
            TerrainGeometry geometry, bool isReference, bool withCollider, string levelDir,
            TerrainVisualDocument visual, TerrainVisualSidecar sidecar, TerrainImportReport report)
        {
            Transform levelRoot = FindLevelRoot(levelId);
            Transform terrainRoot = levelRoot == null ? null : levelRoot.Find(TerrainRootName);
            if (terrainRoot == null)
            {
                report.Error("[terrain_visual] no " + TerrainRootName + " under the level root; " +
                             "the visual surface was not built");
                return;
            }

            var counters = new VisualCounters();
            Transform visualRoot = EnsureChildObject(terrainRoot, TerrainVisualName, counters);
            if (!EnsureWorldIdentity(visualRoot, counters, report)) return;
            RemoveLegacySingleMesh(visualRoot, counters, report);

            // textures and materials first: the parts only reference them
            var materials = new Dictionary<string, Material>();
            if (!EnsureVisualMaterials(levelId, isReference, levelDir, visual, materials, counters,
                report)) return;

            string sidecarSha = visual.SidecarSha256;
            for (int i = 0; i < visual.Parts.Length; i++)
            {
                TerrainVisualPart part = visual.Parts[i];
                Transform partTransform = EnsureChildObject(visualRoot, part.Id, counters);
                if (!EnsureWorldIdentity(partTransform, counters, report)) return;
                Mesh mesh = EnsurePartMesh(levelId, isReference, part, sidecar, sidecarSha,
                    visual, counters, report);
                if (mesh == null) return;
                EnsurePartRenderer(partTransform, part, mesh, visual, materials, counters);
                RemoveColliders(partTransform, counters);
            }

            if (withCollider)
            {
                EnsureCollisionObject(levelId, terrainId, geometry, isReference, terrainRoot,
                    counters, report);
            }

            report.StandingCreated += counters.Created;
            report.StandingUpdated += counters.Updated;
            if (counters.Created > 0 || counters.Updated > 0)
            {
                EditorUtility.SetDirty(visualRoot.gameObject);
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
            }

            int triangles = 0;
            for (int i = 0; i < visual.Parts.Length; i++) triangles += visual.Parts[i].IndexCount / 3;
            report.Notes.Add("visual surface " + HierarchyPath(visualRoot) + ": " +
                             visual.Parts.Length + " parts (" + triangles + " tris), " +
                             visual.Materials.Length + " materials, filter " + visual.Filter +
                             "; +" + counters.Created + " ~" + counters.Updated +
                             (withCollider
                                 ? "; the contact mesh is a MeshCollider on " +
                                   TerrainCollisionName + " and is not drawn"
                                 : "; no meta.playerStart, so no player collider"));
        }

        // ------------------------------------------------------------------ objects ---

        private static Transform EnsureChildObject(Transform parent, string name,
            VisualCounters counters)
        {
            Transform child = parent.Find(name);
            if (child != null) return child;
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            counters.Created++;
            return go.transform;
        }

        /// <summary>Unit pose, Default layer, active: the vertices are world metres.</summary>
        private static bool EnsureWorldIdentity(Transform transform, VisualCounters counters,
            TerrainImportReport report)
        {
            if (transform.localPosition != Vector3.zero ||
                transform.localRotation != Quaternion.identity ||
                transform.localScale != Vector3.one)
            {
                Undo.RecordObject(transform, "Reset terrain pose");
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
                counters.Updated++;
            }

            if (Vector3.Distance(transform.position, Vector3.zero) > 1e-4f ||
                Vector3.Distance(transform.lossyScale, Vector3.one) > 1e-4f)
            {
                report.Error("[terrain_visual] " + HierarchyPath(transform) + " is not at the " +
                             "world origin with unit scale (a parent moves it); the terrain " +
                             "vertices are world metres, so fix the parent chain");
                return false;
            }

            if (transform.gameObject.layer != 0)
            {
                Undo.RecordObject(transform.gameObject, "Terrain layer");
                transform.gameObject.layer = 0;
                counters.Updated++;
            }

            if (!transform.gameObject.activeSelf)
            {
                Undo.RecordObject(transform.gameObject, "Terrain active");
                transform.gameObject.SetActive(true);
                counters.Updated++;
            }

            return true;
        }

        /// <summary>
        /// A scene imported before the split has the renderer and the collider on
        /// <c>TerrainVisual</c> itself; with parts below it those components are removed (the
        /// collider moves to <see cref="TerrainCollisionName"/>), so nothing draws the contact mesh.
        /// </summary>
        private static void RemoveLegacySingleMesh(Transform visualRoot, VisualCounters counters,
            TerrainImportReport report)
        {
            bool removed = false;
            foreach (Collider collider in visualRoot.GetComponents<Collider>())
            {
                Undo.DestroyObjectImmediate(collider);
                counters.Updated++;
                removed = true;
            }

            MeshRenderer renderer = visualRoot.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Undo.DestroyObjectImmediate(renderer);
                counters.Updated++;
                removed = true;
            }

            MeshFilter filter = visualRoot.GetComponent<MeshFilter>();
            if (filter != null)
            {
                Undo.DestroyObjectImmediate(filter);
                counters.Updated++;
                removed = true;
            }

            if (removed)
            {
                report.Notes.Add("removed the pre-split renderer/collider from " +
                                 HierarchyPath(visualRoot) + " (the contact mesh is no longer drawn)");
            }
        }

        private static void RemoveColliders(Transform transform, VisualCounters counters)
        {
            foreach (Collider collider in transform.GetComponents<Collider>())
            {
                Undo.DestroyObjectImmediate(collider);
                counters.Updated++;
            }
        }

        private static void EnsurePartRenderer(Transform partTransform, TerrainVisualPart part,
            Mesh mesh, TerrainVisualDocument visual, Dictionary<string, Material> materials,
            VisualCounters counters)
        {
            MeshFilter filter = partTransform.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = Undo.AddComponent<MeshFilter>(partTransform.gameObject);
                counters.Created++;
            }

            if (filter.sharedMesh != mesh)
            {
                Undo.RecordObject(filter, "Assign visual mesh");
                filter.sharedMesh = mesh;
                counters.Updated++;
            }

            MeshRenderer renderer = partTransform.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<MeshRenderer>(partTransform.gameObject);
                counters.Created++;
            }

            var wanted = new Material[part.Submeshes.Length];
            for (int i = 0; i < wanted.Length; i++) wanted[i] = materials[part.Submeshes[i].Material];
            if (!SameMaterials(renderer.sharedMaterials, wanted))
            {
                Undo.RecordObject(renderer, "Assign visual materials");
                renderer.sharedMaterials = wanted;
                counters.Updated++;
            }

            // The sky shell is a backdrop: it must neither throw nor catch a shadow.
            bool sky = part.Role == TerrainVisualDocument.RoleSky;
            ShadowCastingMode casting = sky ? ShadowCastingMode.Off : ShadowCastingMode.On;
            if (renderer.shadowCastingMode != casting || renderer.receiveShadows == sky)
            {
                Undo.RecordObject(renderer, "Visual shadows");
                renderer.shadowCastingMode = casting;
                renderer.receiveShadows = !sky;
                counters.Updated++;
            }
        }

        private static bool SameMaterials(Material[] current, Material[] wanted)
        {
            if (current == null || current.Length != wanted.Length) return false;
            for (int i = 0; i < wanted.Length; i++)
            {
                if (current[i] != wanted[i]) return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- collision ---

        private static void EnsureCollisionObject(string levelId, string terrainId,
            TerrainGeometry geometry, bool isReference, Transform terrainRoot,
            VisualCounters counters, TerrainImportReport report)
        {
            string collisionPath = PlacementV2Rules.TerrainCollisionMeshPath(levelId, terrainId,
                isReference);
            string legacyPath = PlacementV2Rules.TerrainVisualMeshPath(levelId, terrainId,
                isReference);
            if (AssetDatabase.LoadAssetAtPath<Mesh>(collisionPath) == null &&
                AssetDatabase.LoadAssetAtPath<Mesh>(legacyPath) != null)
            {
                string moveError = AssetDatabase.MoveAsset(legacyPath, collisionPath);
                if (!string.IsNullOrEmpty(moveError))
                {
                    report.Error("[terrain_visual] cannot move the contact mesh " + legacyPath +
                                 " to " + collisionPath + ": " + moveError);
                    return;
                }

                counters.Updated++;
                report.Notes.Add("contact mesh moved " + legacyPath + " -> " + collisionPath +
                                 " (GUID kept)");
            }

            int created = 0;
            int updated = 0;
            Mesh collisionMesh = EnsureVisualMeshAsset(collisionPath, geometry, report,
                ref created, ref updated);
            counters.Created += created;
            counters.Updated += updated;
            if (collisionMesh == null) return;

            Transform collisionTransform = EnsureChildObject(terrainRoot, TerrainCollisionName,
                counters);
            if (!EnsureWorldIdentity(collisionTransform, counters, report)) return;
            MeshCollider collider = collisionTransform.GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = Undo.AddComponent<MeshCollider>(collisionTransform.gameObject);
                counters.Created++;
            }

            if (collider.sharedMesh != collisionMesh || collider.convex || collider.isTrigger ||
                !collider.enabled)
            {
                Undo.RecordObject(collider, "Terrain collision");
                collider.sharedMesh = collisionMesh;
                collider.convex = false;
                collider.isTrigger = false;
                collider.enabled = true;
                counters.Updated++;
            }

            // never drawn: a renderer on the contact mesh is how the 15 m wall ring got on screen
            MeshRenderer renderer = collisionTransform.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Undo.DestroyObjectImmediate(renderer);
                counters.Updated++;
            }

            MeshFilter filter = collisionTransform.GetComponent<MeshFilter>();
            if (filter != null)
            {
                Undo.DestroyObjectImmediate(filter);
                counters.Updated++;
            }

            report.Notes.Add("collision " + HierarchyPath(collisionTransform) + ": MeshCollider " +
                             "(non-convex, Default layer) on '" + collisionMesh.name + "' (" +
                             MeshTriangleCount(collisionMesh) + " tris); for the player only");
        }

        // -------------------------------------------------------------------- meshes ---

        private static Mesh EnsurePartMesh(string levelId, bool isReference, TerrainVisualPart part,
            TerrainVisualSidecar sidecar, string sidecarSha, TerrainVisualDocument visual,
            VisualCounters counters, TerrainImportReport report)
        {
            string assetPath = PlacementV2Rules.TerrainVisualPartMeshPath(levelId, part.Id,
                isReference);
            string meshName = PlacementV2Rules.VisualMeshName(levelId, part.Id, sidecarSha);
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null && existing.name == meshName &&
                existing.vertexCount == part.VertexCount &&
                MeshTriangleCount(existing) == part.IndexCount / 3 &&
                existing.subMeshCount == part.Submeshes.Length)
            {
                return existing;
            }

            Mesh built = BuildPartMesh(part, sidecar, meshName);
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                counters.Updated++;
                report.Notes.Add("visual mesh " + assetPath + " rebuilt in place (name, vertex, " +
                                 "triangle or submesh count differed)");
                return existing;
            }

            if (!EnsureAssetFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/')))
            {
                UnityEngine.Object.DestroyImmediate(built);
                report.Error("[terrain_visual] cannot create the folder for " + assetPath);
                return null;
            }

            AssetDatabase.CreateAsset(built, assetPath);
            AssetDatabase.SaveAssets();
            counters.Created++;
            return built;
        }

        /// <summary>
        /// One part's <see cref="Mesh"/>: the sidecar's vertices as they are (shared, with the
        /// original's normals), one submesh per <c>submeshes[]</c> entry. The sidecar's winding
        /// is already Unity's (front face = counter-clockwise numeric cross product, i.e. the
        /// +Y face of a floor), so the indices are copied as they are; only the texture
        /// coordinate's <c>v</c> flips, because the file's origin is the PNG's top-left and
        /// Unity's is bottom-left.
        /// </summary>
        private static Mesh BuildPartMesh(TerrainVisualPart part, TerrainVisualSidecar sidecar,
            string name)
        {
            int count = part.VertexCount;
            var vertices = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int at = 3 * (part.VertexStart + i);
                vertices[i] = new Vector3(sidecar.Positions[at], sidecar.Positions[at + 1],
                    sidecar.Positions[at + 2]);
            }

            var mesh = new Mesh { name = name };
            if (count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            if (sidecar.Normals != null)
            {
                var normals = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    int at = 3 * (part.VertexStart + i);
                    normals[i] = new Vector3(sidecar.Normals[at], sidecar.Normals[at + 1],
                        sidecar.Normals[at + 2]);
                }

                mesh.normals = normals;
            }

            if (sidecar.Uvs != null)
            {
                var uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    int at = 2 * (part.VertexStart + i);
                    uvs[i] = new Vector2(sidecar.Uvs[at], 1f - sidecar.Uvs[at + 1]);
                }

                mesh.uv = uvs;
            }

            mesh.subMeshCount = part.Submeshes.Length;
            for (int s = 0; s < part.Submeshes.Length; s++)
            {
                TerrainVisualSubmesh submesh = part.Submeshes[s];
                var triangles = new int[submesh.IndexCount];
                int start = part.IndexStart + submesh.FirstIndex;
                for (int i = 0; i < triangles.Length; i++) triangles[i] = sidecar.Indices[start + i];
                mesh.SetTriangles(triangles, s, false);
            }

            if (sidecar.Normals == null) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------------------------------------------------------------- materials ---

        private static bool EnsureVisualMaterials(string levelId, bool isReference,
            string levelDir, TerrainVisualDocument visual, Dictionary<string, Material> materials,
            VisualCounters counters, TerrainImportReport report)
        {
            var texturePaths = new Dictionary<string, string>();
            var written = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < visual.Materials.Length; i++)
                {
                    TerrainVisualMaterial material = visual.Materials[i];
                    string source;
                    string error;
                    if (!visual.TryResolveTexture(levelDir, material, out source, out error))
                    {
                        report.Error(error);
                        return false;
                    }

                    string assetPath = PlacementV2Rules.TerrainVisualTexturePath(levelId,
                        material.Texture, isReference);
                    if (isReference && !assetPath.StartsWith(ReferenceSceneRoot, StringComparison.Ordinal))
                    {
                        report.Error("[terrain_visual_texture] a reference texture may only be " +
                                     "written under " + ReferenceSceneRoot + " (got " + assetPath + ")");
                        return false;
                    }

                    texturePaths[material.Id] = assetPath;
                    string destination = ProjectFilePath(assetPath);
                    byte[] bytes = File.ReadAllBytes(source);
                    if (!File.Exists(destination) || !SameBytes(File.ReadAllBytes(destination), bytes))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.WriteAllBytes(destination, bytes);
                        written.Add(assetPath);
                        if (AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath) == null) counters.Created++;
                        else counters.Updated++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            for (int i = 0; i < written.Count; i++)
            {
                AssetDatabase.ImportAsset(written[i], ImportAssetOptions.ForceUpdate);
            }

            // importer settings (a texture that was already imported may carry other ones)
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < visual.Materials.Length; i++)
                {
                    TerrainVisualMaterial material = visual.Materials[i];
                    if (ApplyTextureSettings(texturePaths[material.Id], material, visual.Filter))
                    {
                        counters.Updated++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            for (int i = 0; i < visual.Materials.Length; i++)
            {
                TerrainVisualMaterial material = visual.Materials[i];
                Material result = EnsureMaterial(levelId, isReference, material,
                    texturePaths[material.Id], counters, report);
                if (result == null) return false;
                materials[material.Id] = result;
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        private static string ProjectFilePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        /// <summary>
        /// Pixel art, tiled: Point or Bilinear as the document says, Repeat, no mipmaps (the
        /// original has none), sRGB, uncompressed (64x64 textures; compression would smear them),
        /// alpha kept as transparency for cutout/alpha. Returns true when it changed anything.
        /// </summary>
        private static bool ApplyTextureSettings(string assetPath, TerrainVisualMaterial material,
            string filter)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return false;
            bool transparent = material.Blend != "opaque";
            FilterMode filterMode = filter == "point" ? FilterMode.Point : FilterMode.Bilinear;
            bool dirty = importer.textureType != TextureImporterType.Default ||
                         importer.mipmapEnabled || importer.filterMode != filterMode ||
                         importer.wrapMode != TextureWrapMode.Repeat || !importer.sRGBTexture ||
                         importer.alphaSource != TextureImporterAlphaSource.FromInput ||
                         importer.alphaIsTransparency != transparent ||
                         importer.textureCompression != TextureImporterCompression.Uncompressed ||
                         importer.npotScale != TextureImporterNPOTScale.None ||
                         importer.isReadable;
            if (!dirty) return false;
            importer.textureType = TextureImporterType.Default;
            importer.mipmapEnabled = false;
            importer.filterMode = filterMode;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = transparent;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = false;
            importer.SaveAndReimport();
            return true;
        }

        private static Material EnsureMaterial(string levelId, bool isReference,
            TerrainVisualMaterial material, string texturePath, VisualCounters counters,
            TerrainImportReport report)
        {
            string shaderName = PlacementV2Rules.VisualShaderName(material.Blend);
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                report.Error("[terrain_visual_material] shader '" + shaderName + "' (blend " +
                             material.Blend + ") is not available in this Editor");
                return null;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                report.Error("[terrain_visual_texture] " + texturePath + " did not import as a " +
                             "Texture2D");
                return null;
            }

            string path = PlacementV2Rules.TerrainVisualMaterialPath(levelId, material.Id,
                isReference);
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                string folder = Path.GetDirectoryName(path).Replace('\\', '/');
                if (!EnsureAssetFolder(folder))
                {
                    report.Error("[terrain_visual] cannot create the folder for " + path);
                    return null;
                }

                var created = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                ConfigureMaterial(created, material, texture);
                AssetDatabase.CreateAsset(created, path);
                counters.Created++;
                return created;
            }

            if (existing.shader != shader || existing.mainTexture != texture ||
                existing.color != Color.white ||
                (material.Blend == "cutout" && !Mathf.Approximately(existing.GetFloat("_Cutoff"), 0.5f)))
            {
                Undo.RecordObject(existing, "Terrain visual material");
                existing.shader = shader;
                ConfigureMaterial(existing, material, texture);
                EditorUtility.SetDirty(existing);
                counters.Updated++;
            }

            return existing;
        }

        private static void ConfigureMaterial(Material target, TerrainVisualMaterial material,
            Texture2D texture)
        {
            target.mainTexture = texture;
            target.color = Color.white;
            if (material.Blend == "cutout") target.SetFloat("_Cutoff", 0.5f);
        }
    }
}
