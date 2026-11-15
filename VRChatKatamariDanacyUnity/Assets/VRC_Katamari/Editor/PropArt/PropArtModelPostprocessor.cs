using UnityEditor;
using UnityEngine;

namespace VRCKatamari.Editor.PropArt
{
    /// <summary>
    /// Import hook for the prop FBXes: applies the shared import settings, binds materials
    /// that already exist during the first pass, and runs the full manifest sync once the
    /// import has finished (where creating assets is allowed and the imported material
    /// sub-assets are readable).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split across the three callbacks on purpose:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>OnPreprocessModel</c> sets the material import mode and the
    /// axis/scale settings, and remaps any material that already exists. It cannot create
    /// assets: <c>AssetDatabase.CreateAsset</c> is restricted while an import is
    /// running.</description></item>
    /// <item><description><c>OnAssignMaterialModel</c> is the fast path that keeps an
    /// already-correct material bound even before the external object map is written; it
    /// only returns an existing asset and never creates one.</description></item>
    /// <item><description><c>OnPostprocessAllAssets</c> runs after the batch, so it can
    /// create the missing materials, write the remaps and reimport the FBX once. The sync
    /// is idempotent, so the reimport's own callback does nothing.</description></item>
    /// </list>
    /// <para>
    /// The definition lives in <c>Editor/LevelBuild/PlacementImporter.cs</c>, which
    /// instantiates <c>Assets/VRC_Katamari/Art/Props/&lt;def&gt;/&lt;def&gt;.fbx</c>
    /// directly; this hook therefore keys on the same path and never renames or moves the
    /// asset. Only FBX + manifest pairs under <c>Art/Props</c> are touched.
    /// </para>
    /// </remarks>
    public sealed class PropArtModelPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessModel()
        {
            var importer = assetImporter as ModelImporter;
            if (importer == null)
            {
                return;
            }

            string def;
            if (!PropArtMaterials.TryGetDefFromFbxPath(assetPath, out def))
            {
                return;
            }

            PropArtMaterials.ConfigureImporter(importer);

            PropArtManifest manifest;
            string error;
            if (!PropArtMaterials.TryLoadManifest(def, out manifest, out error))
            {
                Debug.LogError(PropArtMaterials.LogPrefix + " " + def + ": " + error);
                return;
            }

            PropArtMaterials.AddRemapsForExistingMaterials(importer, manifest);
        }

        public override Material OnAssignMaterialModel(Material material, Renderer renderer)
        {
            if (material == null)
            {
                return null;
            }

            string def;
            if (!PropArtMaterials.TryGetDefFromFbxPath(assetPath, out def))
            {
                return null;
            }

            PropArtManifest manifest;
            if (!PropArtMaterials.TryLoadManifest(def, out manifest, out _))
            {
                // Reported from OnPreprocessModel; returning null keeps Unity's default
                // behaviour for this pass and the post-import sync reports the failure.
                return null;
            }

            string declaredName;
            if (!PropArtMaterials.TryResolveSlot(material.name, def, manifest.Slots, out declaredName))
            {
                return null;
            }

            // Null on the first import: OnPostprocessAllAssets creates the material and
            // remaps the slot, then reimports this model.
            return AssetDatabase.LoadAssetAtPath<Material>(
                PropArtMaterials.MaterialAssetPath(def, declaredName));
        }

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            PropArtMaterials.SyncForImportedAssets(importedAssets, movedAssets);
        }
    }
}
