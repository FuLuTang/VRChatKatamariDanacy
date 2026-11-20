using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Model import hook for the reference props under <c>Assets/_RefUnlicensed/Models/</c>:
    /// gives each imported material the render mode its source glTF material declares
    /// (MASK -> Standard Cutout with <c>_Cutoff</c> = <c>alphaCutoff</c>, BLEND -> Standard
    /// Fade, OPAQUE unchanged). The rules are <see cref="ReferenceMaterialAlphaMath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the source comes from. The FBX's own material description has lost the alpha mode
    /// (see <see cref="ReferenceMaterialAlphaMath"/>), so the hook finds the GLB the FBX was
    /// converted from: the folder's <c>&lt;def&gt;.manifest.json</c> names it in
    /// <c>source_glb</c>, and the GLB is read from the local reference tree
    /// <c>%USERPROFILE%/KatamariRef/models/glb-pack/</c> (outside the project and the
    /// repository, like <see cref="ReferenceCatchNames"/>' table). The FBX material is matched
    /// to the glTF material by name (the conversion keeps the names: 228/228 models checked).
    /// </para>
    /// <para>
    /// When. <c>OnPreprocessMaterialDescription</c> runs while the model imports, after Unity's
    /// own FBX material setup (order -990), and only in the
    /// <c>ImportViaMaterialDescription</c> material mode these models use. The embedded
    /// materials are therefore created with the right mode; nothing is extracted, renamed or
    /// written outside the import. A GLB that is missing or unreadable is an import warning and
    /// the material keeps Unity's default (Opaque) - the reference assets are optional local
    /// data, so the import never fails on them.
    /// </para>
    /// <para>
    /// Reimport. The GLB is outside <c>Assets/</c>, so Unity cannot track it as a dependency:
    /// after placing or changing the GLBs, reimport <c>Assets/_RefUnlicensed/Models</c>
    /// (right-click > Reimport). Changing <see cref="Version"/> makes Unity reimport every
    /// model once.
    /// </para>
    /// </remarks>
    public sealed class ReferenceModelMaterialPostprocessor : AssetPostprocessor
    {
        /// <summary>Bump when the mapping changes so Unity reimports the models.</summary>
        private const uint Version = 1;

        /// <summary>The reference model root this hook handles (nothing else is touched).</summary>
        public const string ModelsRoot = "Assets/_RefUnlicensed/Models/";

        /// <summary>The local GLB pack, relative to the user profile.</summary>
        public const string GlbPackRelativePath = "KatamariRef/models/glb-pack";

        private const string ManifestSuffix = ".manifest.json";
        private const string StandardShaderName = "Standard";

        // One parse per model import: every material of the model asks for the same file. Keyed
        // by the asset path so a reused postprocessor instance never answers for another model.
        private ReferenceMaterialAlphaMath.SourceMaterial[] cachedMaterials;
        private string cachedError;
        private string cachedFor;

        public override uint GetVersion()
        {
            return Version;
        }

        /// <summary>True for an FBX under <see cref="ModelsRoot"/>.</summary>
        public static bool IsReferenceModelPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   assetPath.StartsWith(ModelsRoot, StringComparison.Ordinal) &&
                   assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The absolute folder the source GLBs are read from.</summary>
        public static string GlbPackFolder()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(root, GlbPackRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// The source glTF materials of a reference model: manifest -> GLB -> <c>materials[]</c>.
        /// Usable outside an import (e.g. an audit comparing the scene against the source).
        /// </summary>
        public static bool TryReadSourceMaterials(string modelAssetPath,
            out ReferenceMaterialAlphaMath.SourceMaterial[] materials, out string error)
        {
            materials = null;
            string folder = Path.GetDirectoryName(modelAssetPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(modelAssetPath);
            string[] manifestPaths;
            try
            {
                manifestPaths = Directory.GetFiles(folder, "*" + ManifestSuffix);
            }
            catch (Exception exception)
            {
                error = "cannot list " + folder + ": " + exception.Message;
                return false;
            }

            Array.Sort(manifestPaths, StringComparer.Ordinal);
            var names = new string[manifestPaths.Length];
            var sources = new string[manifestPaths.Length];
            for (int i = 0; i < manifestPaths.Length; i++)
            {
                names[i] = Path.GetFileName(manifestPaths[i]);
                try
                {
                    LevelJsonNode manifest = LevelJsonParser.ParseText(File.ReadAllText(manifestPaths[i]));
                    sources[i] = manifest == null ? null :
                        manifest.GetString("source_glb", manifest.GetString("source", null));
                }
                catch (Exception exception)
                {
                    error = names[i] + " does not parse: " + exception.Message;
                    return false;
                }
            }

            string glb;
            if (!ReferenceMaterialAlphaMath.TryResolveSourceGlb(stem, names, sources, out glb, out error))
            {
                return false;
            }

            string glbPath = Path.Combine(GlbPackFolder(), glb);
            if (!File.Exists(glbPath))
            {
                error = "source GLB " + glb + " is not in " + GlbPackFolder();
                return false;
            }

            string json;
            if (!ReferenceMaterialAlphaMath.TryReadGlbJson(File.ReadAllBytes(glbPath), out json, out error))
            {
                error = glb + ": " + error;
                return false;
            }

            if (!ReferenceMaterialAlphaMath.TryReadMaterials(json, out materials, out error))
            {
                error = glb + ": " + error;
                return false;
            }

            return true;
        }

        private void OnPreprocessModel()
        {
            if (!IsReferenceModelPath(assetPath)) return;
            var importer = assetImporter as ModelImporter;
            if (importer != null &&
                importer.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription)
            {
                context.LogImportWarning("[ref-material] " + assetPath + " imports materials as " +
                    importer.materialImportMode + "; the source alpha mode is only applied in " +
                    "ImportViaMaterialDescription");
            }
        }

        public void OnPreprocessMaterialDescription(MaterialDescription description, Material material,
            AnimationClip[] clips)
        {
            if (!IsReferenceModelPath(assetPath) || material == null) return;

            if (!string.Equals(cachedFor, assetPath, StringComparison.Ordinal))
            {
                cachedFor = assetPath;
                TryReadSourceMaterials(assetPath, out cachedMaterials, out cachedError);
            }

            string name = description.materialName;
            if (cachedMaterials == null)
            {
                context.LogImportWarning("[ref-material] " + assetPath + " '" + name +
                    "' keeps the default opaque mode: " + cachedError);
                return;
            }

            ReferenceMaterialAlphaMath.SourceMaterial source =
                ReferenceMaterialAlphaMath.Find(cachedMaterials, name);
            if (source == null)
            {
                context.LogImportWarning("[ref-material] " + assetPath + " material '" + name +
                    "' has no glTF material of that name; left opaque");
                return;
            }

            ReferenceMaterialAlphaMath.RenderMode mode = source.Mode;
            if (mode == ReferenceMaterialAlphaMath.RenderMode.Unknown)
            {
                context.LogImportWarning("[ref-material] " + assetPath + " '" + name +
                    "' has alphaMode '" + source.AlphaMode + "'; left opaque");
                return;
            }

            if (source.DoubleSided)
            {
                // Standard culls back faces; no reference prop is double-sided today (0/238).
                context.LogImportWarning("[ref-material] " + assetPath + " '" + name +
                    "' is doubleSided in the source; Standard draws only its front faces");
            }

            if (mode == ReferenceMaterialAlphaMath.RenderMode.Opaque) return;

            if (material.shader == null || material.shader.name != StandardShaderName)
            {
                context.LogImportWarning("[ref-material] " + assetPath + " '" + name + "' uses shader '" +
                    (material.shader == null ? "null" : material.shader.name) +
                    "', not Standard; its " + source.AlphaMode + " mode was not applied");
                return;
            }

            Apply(material, mode, source.CutoffValue);
        }

        /// <summary>Sets a Standard material to the mode, as Unity's Standard inspector does.</summary>
        public static void Apply(Material material, ReferenceMaterialAlphaMath.RenderMode mode, float cutoff)
        {
            material.SetFloat("_Mode", ReferenceMaterialAlphaMath.StandardMode(mode));
            material.SetOverrideTag("RenderType", ReferenceMaterialAlphaMath.RenderTypeTag(mode));
            material.SetFloat("_SrcBlend", ReferenceMaterialAlphaMath.SourceBlend(mode));
            material.SetFloat("_DstBlend", ReferenceMaterialAlphaMath.DestinationBlend(mode));
            material.SetFloat("_ZWrite", ReferenceMaterialAlphaMath.ZWrite(mode));
            string keyword = ReferenceMaterialAlphaMath.Keyword(mode);
            for (int i = 0; i < ReferenceMaterialAlphaMath.BlendKeywords.Length; i++)
            {
                string each = ReferenceMaterialAlphaMath.BlendKeywords[i];
                if (each == keyword) material.EnableKeyword(each);
                else material.DisableKeyword(each);
            }

            if (mode == ReferenceMaterialAlphaMath.RenderMode.Cutout) material.SetFloat("_Cutoff", cutoff);
            material.renderQueue = ReferenceMaterialAlphaMath.RenderQueue(mode);
        }
    }
}
