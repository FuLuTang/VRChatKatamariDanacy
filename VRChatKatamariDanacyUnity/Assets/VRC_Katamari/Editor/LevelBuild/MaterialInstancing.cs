using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Which materials may be GPU-instanced, and the hub assembly's pass that turns it on for
    /// the materials a scene's repeated props share.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why. The assembled hub drew 1192 draw calls from 505 renderers, and 372 of those draws
    /// were single-material props (none of the 317 materials had <c>enableInstancing</c>). A
    /// material and mesh shared by n renderers is n draws without instancing and one with it;
    /// measured group counts on the Cancer island put the saving at about 240 draws.
    /// </para>
    /// <para>
    /// Which materials. Only an opaque material of a shader that has an instancing variant:
    /// this project's props use the built-in <c>Standard</c> shader (or its Specular setup).
    /// Unity's <c>Legacy Shaders/...</c> family has no <c>#pragma multi_compile_instancing</c>
    /// (the terrain visual's <c>Legacy Shaders/Diffuse</c> is exactly that case), so enabling
    /// the flag there would only make it look enabled. Cutout/transparent materials are left
    /// alone: the brief scopes instancing to opaque slots, and instanced transparency would
    /// need per-renderer sort care.
    /// </para>
    /// <para>
    /// Where the flag can live. A material asset under <c>_RefUnlicensed</c> (the terrain
    /// visual materials, the hub's instanced copies) is ours to write. A material under
    /// <c>Assets/VRC_Katamari/</c> is a committed asset owned by the code that creates it
    /// (<see cref="VRCKatamari.Editor.PropArt.PropArtMaterials"/>,
    /// <see cref="TerrainMeshImporter"/>), so this pass never edits those; the creation code
    /// sets the flag instead. A reference prop's material is a <b>read-only sub-asset</b> of
    /// its imported FBX ("Use Embedded Materials": Unity's own documentation says sub-assets
    /// can only be modified after they are extracted), so the flag cannot be written on it.
    /// The pass therefore copies such a material to a <c>.mat</c> asset under
    /// <c>_RefUnlicensed/Materials/Instanced/</c>, copies its shader/keywords/properties, turns
    /// instancing on, and assigns the copy to the renderer. That is the same thing Unity's
    /// "Extract Materials" button does, done only for the slots that need it; the copied
    /// assets are git-ignored reference material, never committed or uploaded.
    /// </para>
    /// <para>
    /// Idempotence: copy paths are derived from the owning model and the embedded material's
    /// name, an existing copy is reused (never recreated), a material that already has the flag
    /// is counted and left alone, and a second run therefore writes no asset and changes no
    /// renderer. A copy that cannot be named (an embedded material with no locatable owning
    /// model) is reported and skipped, never guessed by name.
    /// </para>
    /// </remarks>
    public static class MaterialInstancing
    {
        /// <summary>Unity's first non-opaque render queue (AlphaTest); cutout and transparent start here.</summary>
        public const int FirstNonOpaqueQueue = 2450;

        /// <summary>Where the copies of read-only reference materials live (git-ignored).</summary>
        public const string InstancedFolder = "Assets/_RefUnlicensed/Materials/Instanced";

        /// <summary>The built-in shaders this project uses that have instancing variants.</summary>
        private static readonly string[] InstancingShaders =
        {
            "Standard",
            "Standard (Specular setup)",
        };

        /// <summary>True when the shader name is one whose instancing variant exists.</summary>
        public static bool ShaderSupportsInstancing(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            for (int i = 0; i < InstancingShaders.Length; i++)
            {
                if (string.Equals(InstancingShaders[i], shaderName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when a material with this shader and queue may be instanced: the shader has an
        /// instancing variant and the material is opaque (below Unity's AlphaTest queue and not
        /// a shader named Transparent/Cutout, the same test the occlusion rule uses).
        /// </summary>
        public static bool IsEligible(string shaderName, int renderQueue)
        {
            if (renderQueue >= FirstNonOpaqueQueue) return false;
            if (shaderName == null) return false;
            if (shaderName.IndexOf("Transparent", StringComparison.Ordinal) >= 0
                || shaderName.IndexOf("Cutout", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            return ShaderSupportsInstancing(shaderName);
        }

        /// <summary>True when the material itself is eligible (see <see cref="IsEligible"/>).</summary>
        public static bool IsEligible(Material material)
        {
            return material != null && material.shader != null
                && IsEligible(material.shader.name, material.renderQueue);
        }

        /// <summary>
        /// Turns instancing on for one material asset when it is eligible, and reports whether
        /// it changed anything. The creation code's entry point for a material it owns (a new
        /// asset still in memory or an existing one it may write).
        /// </summary>
        public static bool EnableIfEligible(Material material)
        {
            if (!IsEligible(material) || material.enableInstancing) return false;
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return true;
        }

        /// <summary>What one <see cref="EnableInScene"/> pass did.</summary>
        public sealed class Result
        {
            /// <summary>Distinct materials that had the flag turned on by this pass.</summary>
            public int Enabled;

            /// <summary>Read-only reference materials copied out to <see cref="InstancedFolder"/>.</summary>
            public int Duplicated;

            /// <summary>Eligible materials that already had the flag on.</summary>
            public int AlreadyEnabled;

            /// <summary>Materials whose shader has no instancing variant (e.g. the terrain visual).</summary>
            public int SkippedUnsupported;

            /// <summary>Eligible-shader materials above the opaque queue or named cutout/transparent.</summary>
            public int SkippedNonOpaque;

            /// <summary>Committed <c>Assets/VRC_Katamari/</c> materials: the creation code owns the flag.</summary>
            public int SkippedCommitted;

            /// <summary>Renderers whose material list was rewritten to the instanced copies.</summary>
            public int RenderersRewritten;

            /// <summary>Read-only materials that could not be copied (reported, not guessed).</summary>
            public readonly List<string> Problems = new List<string>();
        }

        /// <summary>
        /// The hub assembly's instancing pass over an open scene: every enabled MeshRenderer's
        /// eligible materials get instancing, and a read-only reference material is first copied
        /// to an asset under <see cref="InstancedFolder"/>. <paramref name="mayWriteReferenceAssets"/>
        /// is false for an output scene that is not under <c>_RefUnlicensed/</c> (a committed
        /// scene must not reference reference assets), in which case nothing is written.
        /// </summary>
        public static Result EnableInScene(Scene scene, bool mayWriteReferenceAssets)
        {
            var result = new Result();
            if (!mayWriteReferenceAssets) return result;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                result.Problems.Add("instancing: the scene to process is not open");
                return result;
            }

            var seen = new Dictionary<Material, Material>();
            bool dirty = false;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                MeshRenderer[] renderers = roots[r].GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    MeshRenderer renderer = renderers[i];
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Material[] materials = renderer.sharedMaterials;
                    Material[] replaced = null;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        Material source = materials[slot];
                        if (source == null) continue;

                        // One decision per material, not per slot or per renderer: the counts
                        // in the report are distinct materials (the same material appears on
                        // many renderers and in several slots).
                        Material instanced;
                        if (seen.TryGetValue(source, out instanced))
                        {
                            if (instanced != source)
                            {
                                if (replaced == null) replaced = (Material[])materials.Clone();
                                replaced[slot] = instanced;
                            }
                            continue;
                        }

                        if (!IsEligible(source))
                        {
                            if (!ShaderSupportsInstancing(
                                    source.shader == null ? null : source.shader.name))
                            {
                                result.SkippedUnsupported++;
                            }
                            else
                            {
                                result.SkippedNonOpaque++;
                            }
                            seen[source] = source;
                            continue;
                        }

                        if (source.enableInstancing)
                        {
                            result.AlreadyEnabled++;
                            seen[source] = source;
                            continue;
                        }

                        string path = AssetDatabase.GetAssetPath(source);
                        if (!string.IsNullOrEmpty(path) && !IsModelAssetPath(path))
                        {
                            if (path.StartsWith(LevelPaths.ReferenceSceneRoot, StringComparison.Ordinal))
                            {
                                Undo.RecordObject(source, "Enable GPU instancing");
                                source.enableInstancing = true;
                                EditorUtility.SetDirty(source);
                                result.Enabled++;
                            }
                            else
                            {
                                // A committed asset: the importer/creation code owns its flag.
                                result.SkippedCommitted++;
                            }
                            seen[source] = source;
                            continue;
                        }

                        // A read-only sub-asset of an imported model. Copy it to an asset we own.
                        string owner = OwnerAssetPath(source, renderer);
                        if (owner == null)
                        {
                            result.Problems.Add("instancing: cannot name the model that owns the"
                                + " embedded material '" + source.name + "' on '"
                                + HierarchyPath(renderer.transform) + "'; left unchanged");
                            seen[source] = source;
                            continue;
                        }

                        string destination = InstancedAssetPath(owner, source);
                        Material copy = AssetDatabase.LoadAssetAtPath<Material>(destination);
                        if (copy != null && copy.shader != source.shader)
                        {
                            // Two different embedded materials would share one copy: never
                            // silently draw the wrong shader. Report and leave it alone.
                            result.Problems.Add("instancing: " + destination + " exists with"
                                + " shader '" + (copy.shader == null ? "null" : copy.shader.name)
                                + "', not the embedded material's '"
                                + (source.shader == null ? "null" : source.shader.name) + "';"
                                + " delete the copy or rename the material, left unchanged");
                            seen[source] = source;
                            continue;
                        }
                        if (copy == null)
                        {
                            if (!EnsureFolder(FolderOf(destination)))
                            {
                                result.Problems.Add("instancing: cannot create the folder for "
                                    + destination);
                                seen[source] = source;
                                continue;
                            }
                            copy = CopyMaterial(source, destination);
                            if (copy == null)
                            {
                                result.Problems.Add("instancing: could not copy '" + source.name
                                    + "' to " + destination);
                                seen[source] = source;
                                continue;
                            }
                            result.Duplicated++;
                        }
                        else if (!SameRenderState(copy, source))
                        {
                            // The embedded material changed since the copy was made (a reimport
                            // that set its render mode from the source glTF, a new texture). The
                            // copy is ours, so it follows the source instead of drawing the old
                            // state; counted with the copies this pass wrote.
                            copy.CopyPropertiesFromMaterial(source);
                            copy.shaderKeywords = source.shaderKeywords;
                            copy.renderQueue = source.renderQueue;
                            EditorUtility.SetDirty(copy);
                            result.Duplicated++;
                        }
                        if (!copy.enableInstancing)
                        {
                            copy.enableInstancing = true;
                            EditorUtility.SetDirty(copy);
                            result.Enabled++;
                        }
                        else
                        {
                            result.AlreadyEnabled++;
                        }
                        seen[source] = copy;
                        if (replaced == null) replaced = (Material[])materials.Clone();
                        replaced[slot] = copy;
                    }

                    if (replaced != null)
                    {
                        Undo.RecordObject(renderer, "Assign instanced materials");
                        renderer.sharedMaterials = replaced;
                        EditorUtility.SetDirty(renderer);
                        result.RenderersRewritten++;
                        dirty = true;
                    }
                }
            }

            AssetDatabase.SaveAssets();
            if (dirty) EditorSceneManager.MarkSceneDirty(scene);
            return result;
        }

        // ------------------------------------------------------------ naming ---

        /// <summary>
        /// The model asset that owns <paramref name="material"/>: the imported model whose
        /// sub-asset it is, or the prefab/mesh the renderer instances when the asset path does
        /// not name it. Null when no model can be named (never guessed from the material name).
        /// </summary>
        private static string OwnerAssetPath(Material material, MeshRenderer renderer)
        {
            string path = AssetDatabase.GetAssetPath(material);
            if (IsModelAssetPath(path)) return path;

            GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(renderer.gameObject);
            if (root != null)
            {
                UnityEngine.Object prefab = PrefabUtility.GetCorrespondingObjectFromSource(root);
                string prefabPath = prefab == null ? null : AssetDatabase.GetAssetPath(prefab);
                if (IsModelAssetPath(prefabPath)) return prefabPath;
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter == null ? null : filter.sharedMesh;
            string meshPath = mesh == null ? null : AssetDatabase.GetAssetPath(mesh);
            if (IsModelAssetPath(meshPath)) return meshPath;
            return null;
        }

        /// <summary>A model asset (.fbx/.obj) path; an embedded material's asset path is its model's.</summary>
        public static bool IsModelAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The copy's asset path: model file stem + embedded material name.</summary>
        public static string InstancedAssetPath(string ownerAssetPath, Material material)
        {
            string name = SafeFileName(material == null ? null : material.name);
            return InstancedFolder + "/" + SafeFileName(FileStem(ownerAssetPath)) + "_" + name + ".mat";
        }

        // The file name without its folder and extension, with either separator: the project
        // runs on Windows (where '\' appears in hand-typed paths), the tests on Linux.
        private static string FileStem(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            int backslash = path.LastIndexOf('\\');
            int start = Math.Max(slash, backslash) + 1;
            string file = start <= 0 ? path : path.Substring(start);
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        /// <summary>A name that is safe as an asset file name; an empty one becomes "material".</summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "material";
            char[] invalid = Path.GetInvalidFileNameChars();
            var text = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool bad = c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"'
                    || c == '<' || c == '>' || c == '|';
                for (int k = 0; k < invalid.Length && !bad; k++) bad = invalid[k] == c;
                text.Append(bad ? '_' : c);
            }
            string result = text.ToString().Trim();
            return result.Length == 0 ? "material" : result;
        }

        private static string FolderOf(string assetPath)
        {
            int slash = assetPath.LastIndexOf('/');
            return slash < 0 ? "" : assetPath.Substring(0, slash);
        }

        // Copies the material (shader, keywords, properties) and creates the asset. False when
        // Unity refused the copy; the caller reports it and leaves the renderer alone.
        private static Material CopyMaterial(Material source, string destination)
        {
            Material copy;
            try
            {
                copy = new Material(source);
            }
            catch (Exception)
            {
                return null;
            }
            copy.name = Path.GetFileNameWithoutExtension(destination);
            copy.shaderKeywords = source.shaderKeywords;
            copy.renderQueue = source.renderQueue;
            AssetDatabase.CreateAsset(copy, destination);
            return AssetDatabase.LoadAssetAtPath<Material>(destination);
        }

        // True when the copy still draws like the source: same render queue, keywords, main
        // texture and colour. The Standard blend state (_Mode, blend factors, depth write) only
        // changes together with the queue and the alpha keywords, so those two catch a mode
        // change; texture and colour catch a re-converted model.
        private static bool SameRenderState(Material copy, Material source)
        {
            if (copy.renderQueue != source.renderQueue) return false;
            if (copy.mainTexture != source.mainTexture) return false;
            if (copy.HasProperty("_Color") && source.HasProperty("_Color") && copy.color != source.color)
            {
                return false;
            }

            var keywords = new HashSet<string>(copy.shaderKeywords ?? new string[0], StringComparer.Ordinal);
            return keywords.SetEquals(source.shaderKeywords ?? new string[0]);
        }

        private static bool EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            if (AssetDatabase.IsValidFolder(folder)) return true;
            string parent = FolderOf(folder);
            if (!EnsureFolder(parent)) return false;
            string name = folder.Substring(folder.LastIndexOf('/') + 1);
            string guid = AssetDatabase.CreateFolder(parent, name);
            return !string.IsNullOrEmpty(guid) && AssetDatabase.IsValidFolder(folder);
        }

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            for (Transform p = transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
