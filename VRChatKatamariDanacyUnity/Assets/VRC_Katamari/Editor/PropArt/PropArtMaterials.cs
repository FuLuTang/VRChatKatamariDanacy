using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRCKatamari.Editor.LevelBuild;

namespace VRCKatamari.Editor.PropArt
{
    /// <summary>
    /// Builds and binds one Standard material per declared material slot of every prop FBX
    /// under <c>Assets/VRC_Katamari/Art/Props/&lt;def&gt;/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists. Blender's FBX exporter does not write the Principled Base Color into
    /// the FBX material, so a freshly imported prop would use Unity's default material.
    /// The authoritative colours live in the sibling <c>&lt;def&gt;.manifest.json</c>
    /// (<c>material_base_colors</c>, linear RGB). This tool creates one
    /// <c>&lt;def&gt;/Materials/&lt;slot&gt;.mat</c> per declared slot (the slot name is
    /// already <c>Prop_&lt;def&gt;_&lt;key&gt;</c>) with that colour and
    /// remaps each FBX material slot to that asset through the ModelImporter's external
    /// object map, so the binding survives reimports and is visible in the Materials tab.
    /// </para>
    /// <para>
    /// Shader: the existing hand-made prop materials all use Unity's built-in
    /// <c>Standard</c> shader (<c>m_Shader: {fileID: 46, guid:
    /// 0000000000000000f000000000000000}</c>, <c>_Color</c>, <c>_Glossiness</c>,
    /// <c>_Metallic</c>), which is also available in VRChat worlds. New materials use
    /// Standard; an existing material with a colour property is adopted as-is and only its
    /// colour is updated, so a deliberate shader choice is never overwritten.
    /// </para>
    /// <para>
    /// Import mode: material creation mode is <c>ImportViaMaterialDescription</c> and
    /// location is <c>InPrefab</c> (Unity's "Use Embedded Materials"). That is the mode in
    /// which the ModelImporter exposes the "Remapped Materials" list backed by
    /// <see cref="AssetImporter.GetExternalObjectMap"/>; the remap then replaces the
    /// embedded material for that slot. (Location <c>External</c> is the legacy
    /// extract-to-.mat mode and is not what this project's hand-made materials rely on.)
    /// </para>
    /// <para>
    /// Idempotence: the material path is derived only from the def and the slot name, the
    /// colour is compared before writing, and a reimport only happens when an import setting
    /// or a remap actually changed. Re-running the menu, or reimporting an FBX, therefore
    /// creates no new assets and leaves already-correct materials untouched.
    /// </para>
    /// <para>
    /// Slot mapping: <c>prop_lib.py</c> names every material <c>Prop_&lt;def&gt;_&lt;key&gt;</c>
    /// and writes those same strings into <c>materials</c> / <c>material_base_colors</c>;
    /// the FBX stores them verbatim and Unity's importer uses that name for the imported
    /// material. The expected mapping is the identity. <see cref="TryResolveSlot"/> adds
    /// tolerant fallbacks (case, Blender's <c>.001</c> duplicate suffix, bare key) only for
    /// hand-edited files, and refuses an ambiguous match instead of picking one.
    /// </para>
    /// <para>
    /// This is a long-lived pipeline, not a one-shot builder: the menu re-syncs every def,
    /// and <see cref="PropArtModelPostprocessor"/> applies the same code to a newly imported
    /// FBX. Failures (missing manifest, slot mismatch, missing shader) are reported with the
    /// reason and never repaired by guessing.
    /// </para>
    /// </remarks>
    public static class PropArtMaterials
    {
        public const string PropsRoot = "Assets/VRC_Katamari/Art/Props";
        public const string MaterialsFolderName = "Materials";
        public const string StandardShaderName = "Standard";
        public const string MenuPath = "VRC Katamari/Prop Art/Resync All Prop Materials";

        /// <summary>Console prefix shared by the menu and the import postprocessor.</summary>
        public const string LogPrefix = "[prop-art]";

        /// <summary>
        /// Colour comparison tolerance. The manifests round to four decimals, and setting a
        /// colour and reading it back from the asset is exact, so 1e-4 catches real edits
        /// without treating float noise as a change.
        /// </summary>
        public const float ColorTolerance = 1e-4f;

        private const string ManifestSuffix = ".manifest.json";

        // Runs the missing-material check once per editor session. A delayed import sync
        // can be lost to the domain reload that follows a script compile; this startup
        // hook is the recovery path, and it only does work when a material is actually
        // missing.
        private const string StartupCheckSessionKey = "VRCKatamari.PropArt.StartupCheckDone";

        // Guards a sync that reimports an FBX: the reimport calls OnPostprocessAllAssets
        // again, which would otherwise try to sync the same def recursively.
        private static readonly HashSet<string> s_SyncInProgress =
            new HashSet<string>(StringComparer.Ordinal);

        // Imports are deferred to EditorApplication.delayCall: creating assets while the
        // AssetDatabase is still in its importing state logs a restriction warning (and
        // newer Unity versions refuse it outright). The menu path calls SyncAll directly.
        private static readonly SortedSet<string> s_PendingDefs =
            new SortedSet<string>(StringComparer.Ordinal);
        private static bool s_FlushScheduled;

        // ------------------------------------------------------------------ paths ---

        public static string DefFolderAssetPath(string def)
        {
            return PropsRoot + "/" + def;
        }

        public static string FbxAssetPath(string def)
        {
            return DefFolderAssetPath(def) + "/" + def + ".fbx";
        }

        public static string ManifestAssetPath(string def)
        {
            return DefFolderAssetPath(def) + "/" + def + ManifestSuffix;
        }

        public static string MaterialsFolderAssetPath(string def)
        {
            return DefFolderAssetPath(def) + "/" + MaterialsFolderName;
        }

        public static string MaterialAssetPath(string def, string slot)
        {
            return MaterialsFolderAssetPath(def) + "/" + slot + ".mat";
        }

        /// <summary>
        /// True when <paramref name="assetPath"/> is exactly
        /// <c>Assets/VRC_Katamari/Art/Props/&lt;def&gt;/&lt;def&gt;.fbx</c>.
        /// </summary>
        public static bool TryGetDefFromFbxPath(string assetPath, out string def)
        {
            def = null;
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            string normalized = NormalizeAssetPath(assetPath);
            if (!normalized.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string fileName = Path.GetFileNameWithoutExtension(normalized);
            string directory = NormalizeAssetPath(Path.GetDirectoryName(normalized));
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(directory))
            {
                return false;
            }

            if (!string.Equals(directory, PropsRoot + "/" + fileName, StringComparison.Ordinal))
            {
                return false;
            }

            def = fileName;
            return true;
        }

        /// <summary>
        /// True when <paramref name="assetPath"/> is exactly
        /// <c>Assets/VRC_Katamari/Art/Props/&lt;def&gt;/&lt;def&gt;.manifest.json</c>.
        /// </summary>
        public static bool TryGetDefFromManifestPath(string assetPath, out string def)
        {
            def = null;
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            string normalized = NormalizeAssetPath(assetPath);
            string fileName = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(fileName) ||
                !fileName.EndsWith(ManifestSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            string candidate = fileName.Substring(0, fileName.Length - ManifestSuffix.Length);
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            string directory = NormalizeAssetPath(Path.GetDirectoryName(normalized));
            if (!string.Equals(directory, PropsRoot + "/" + candidate, StringComparison.Ordinal))
            {
                return false;
            }

            def = candidate;
            return true;
        }

        // --------------------------------------------------------------- manifest ---

        /// <summary>
        /// Reads the copied <c>&lt;def&gt;.manifest.json</c> from disk. Reading the file
        /// directly (rather than through a TextAsset) keeps this callable during import,
        /// when the manifest asset may not have been imported yet.
        /// </summary>
        public static bool TryLoadManifest(string def, out PropArtManifest manifest, out string error)
        {
            manifest = null;
            error = null;
            string assetPath = ManifestAssetPath(def);
            string fullPath = ToAbsolutePath(assetPath);
            if (!File.Exists(fullPath))
            {
                error = "manifest not found at " + assetPath +
                        " (copy <def>.manifest.json next to <def>.fbx)";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                error = "could not read " + assetPath + ": " + exception.Message;
                return false;
            }

            return PropArtManifests.TryParse(json, def, out manifest, out error);
        }

        /// <summary>
        /// Maps one FBX/Unity material name to the declared slot in the manifest. The
        /// expected case is an exact match (both sides are <c>Prop_&lt;def&gt;_&lt;key&gt;</c>);
        /// the fallbacks only make hand-edited exports usable. An ambiguous match is refused,
        /// because assigning the wrong colour is worse than reporting the mismatch.
        /// </summary>
        public static bool TryResolveSlot(string importedName, string def, string[] declaredNames,
            out string declaredName)
        {
            declaredName = null;
            if (string.IsNullOrEmpty(importedName) || declaredNames == null || declaredNames.Length == 0)
            {
                return false;
            }

            string name = importedName.Trim();
            if (name.Length == 0)
            {
                return false;
            }

            if (TryMatchExact(name, declaredNames, out declaredName))
            {
                return true;
            }

            if (TryMatchCaseInsensitive(name, declaredNames, out declaredName))
            {
                return true;
            }

            string withoutDuplicateSuffix = StripBlenderDuplicateSuffix(name);
            if (!string.Equals(withoutDuplicateSuffix, name, StringComparison.Ordinal))
            {
                if (TryMatchExact(withoutDuplicateSuffix, declaredNames, out declaredName) ||
                    TryMatchCaseInsensitive(withoutDuplicateSuffix, declaredNames, out declaredName))
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(def))
            {
                string prefixed = "Prop_" + def + "_" + name;
                if (TryMatchExact(prefixed, declaredNames, out declaredName) ||
                    TryMatchCaseInsensitive(prefixed, declaredNames, out declaredName))
                {
                    return true;
                }
            }

            string suffixMatch = null;
            int suffixMatches = 0;
            for (int i = 0; i < declaredNames.Length; i++)
            {
                if (declaredNames[i] != null &&
                    declaredNames[i].EndsWith("_" + name, StringComparison.Ordinal))
                {
                    suffixMatch = declaredNames[i];
                    suffixMatches++;
                }
            }

            if (suffixMatches == 1)
            {
                declaredName = suffixMatch;
                return true;
            }

            declaredName = null;
            return false;
        }

        // ----------------------------------------------------------- startup hook ---

        [InitializeOnLoadMethod]
        private static void ScheduleStartupMaterialCheck()
        {
            EditorApplication.delayCall += CheckMaterialsOnStartup;
        }

        private static void CheckMaterialsOnStartup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                SessionState.GetBool(StartupCheckSessionKey, false))
            {
                return;
            }

            if (EditorApplication.isUpdating || EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += CheckMaterialsOnStartup;
                return;
            }

            SessionState.SetBool(StartupCheckSessionKey, true);
            if (!HasMissingMaterials())
            {
                return;
            }

            // The menu path also works; this is only so the first open after copying the
            // FBXes does not need a manual step when the import callback was interrupted.
            PropArtSyncReport report = SyncAll();
            report.Log(LogPrefix, false);
        }

        private static bool HasMissingMaterials()
        {
            string root = ToAbsolutePath(PropsRoot);
            if (!Directory.Exists(root))
            {
                return false;
            }

            string[] folders = Directory.GetDirectories(root);
            for (int i = 0; i < folders.Length; i++)
            {
                string def = Path.GetFileName(folders[i]);
                if (string.IsNullOrEmpty(def) ||
                    !File.Exists(Path.Combine(folders[i], def + ".fbx")))
                {
                    continue;
                }

                PropArtManifest manifest;
                if (!TryLoadManifest(def, out manifest, out _))
                {
                    return true;
                }

                for (int slot = 0; slot < manifest.Slots.Length; slot++)
                {
                    if (AssetDatabase.LoadAssetAtPath<Material>(
                            MaterialAssetPath(def, manifest.Slots[slot])) == null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ------------------------------------------------------------- menu entry ---

        [MenuItem(MenuPath, false, 300)]
        public static void ResyncAllMaterialsFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError(LogPrefix + " run the material sync outside Play Mode: it rewrites " +
                               ".mat assets and reimports the FBX files.");
                return;
            }

            PropArtSyncReport report = SyncAll();
            report.Log(LogPrefix, true);
            if (report.Errors.Count > 0)
            {
                Debug.LogError(LogPrefix + " finished with " + report.Errors.Count +
                               " error(s); the props above keep their previous materials.");
            }
        }

        /// <summary>
        /// Re-syncs every imported prop FBX under <see cref="PropsRoot"/>. This is the
        /// recovery path after changing a manifest, a shader or an import setting by hand.
        /// </summary>
        public static PropArtSyncReport SyncAll()
        {
            var report = new PropArtSyncReport();
            var defs = new SortedSet<string>(StringComparer.Ordinal);

            string root = ToAbsolutePath(PropsRoot);
            if (!Directory.Exists(root))
            {
                report.Errors.Add("props folder not found: " + PropsRoot);
                return report;
            }

            string[] folders = Directory.GetDirectories(root);
            for (int i = 0; i < folders.Length; i++)
            {
                string def = Path.GetFileName(folders[i]);
                if (!string.IsNullOrEmpty(def) &&
                    File.Exists(Path.Combine(folders[i], def + ".fbx")))
                {
                    defs.Add(def);
                }
            }

            if (defs.Count == 0)
            {
                report.Errors.Add("no FBX found under " + PropsRoot +
                                  " (copy <def>/<def>.fbx before running the sync)");
                return report;
            }

            foreach (string def in defs)
            {
                SyncDef(def, report);
            }

            return report;
        }

        /// <summary>
        /// Called by the import postprocessor. Only defs whose FBX or manifest was just
        /// imported are considered; a sync that changes something reimports the FBX once,
        /// and the nested callback is suppressed by <see cref="s_SyncInProgress"/>.
        /// </summary>
        public static void SyncForImportedAssets(string[] importedAssets, string[] movedAssets)
        {
            var defs = new SortedSet<string>(StringComparer.Ordinal);
            CollectDefs(importedAssets, defs);
            CollectDefs(movedAssets, defs);
            if (defs.Count == 0)
            {
                return;
            }

            foreach (string def in defs)
            {
                // A def whose own reimport caused this callback is already being synced;
                // queueing it again would only produce a redundant second pass.
                if (!s_SyncInProgress.Contains(def))
                {
                    s_PendingDefs.Add(def);
                }
            }

            if (s_PendingDefs.Count == 0 || s_FlushScheduled)
            {
                return;
            }

            s_FlushScheduled = true;
            EditorApplication.delayCall += FlushPendingSync;
        }

        private static void FlushPendingSync()
        {
            s_FlushScheduled = false;
            if (s_PendingDefs.Count == 0)
            {
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // Do not rewrite assets while a world is running; the menu path reports
                // this explicitly and can be run after leaving Play Mode.
                s_PendingDefs.Clear();
                return;
            }

            if (EditorApplication.isUpdating || EditorApplication.isCompiling)
            {
                if (!s_FlushScheduled)
                {
                    s_FlushScheduled = true;
                    EditorApplication.delayCall += FlushPendingSync;
                }

                return;
            }

            var defs = new List<string>(s_PendingDefs);
            s_PendingDefs.Clear();

            var report = new PropArtSyncReport();
            for (int i = 0; i < defs.Count; i++)
            {
                SyncDef(defs[i], report);
            }

            // Silent when an ordinary reimport changed nothing; loud on first import,
            // colour change, remap change, warning or error.
            report.Log(LogPrefix, false);
        }

        // --------------------------------------------------------------- importer ---

        /// <summary>
        /// Applies the import settings the axis/scale contract relies on: one FBX unit is
        /// one metre (Blender bakes metres), no file-scale multiplier, no axis conversion
        /// bake, and the embedded-material mode that exposes external remaps.
        /// </summary>
        internal static bool ConfigureImporter(ModelImporter importer)
        {
            bool changed = false;

            if (importer.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                changed = true;
            }

            if (importer.materialLocation != ModelImporterMaterialLocation.InPrefab)
            {
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                changed = true;
            }

            if (Mathf.Abs(importer.globalScale - 1f) > 1e-6f)
            {
                importer.globalScale = 1f;
                changed = true;
            }

            if (importer.useFileScale)
            {
                importer.useFileScale = false;
                changed = true;
            }

            if (importer.bakeAxisConversion)
            {
                importer.bakeAxisConversion = false;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Preprocess-time fast path: bind the slots whose materials already exist, using
        /// the manifest names (the identity mapping). The postprocessor's post-import sync
        /// repairs this with the actual imported source names if they ever differ.
        /// </summary>
        internal static void AddRemapsForExistingMaterials(ModelImporter importer, PropArtManifest manifest)
        {
            for (int i = 0; i < manifest.Slots.Length; i++)
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(
                    MaterialAssetPath(manifest.Id, manifest.Slots[i]));
                if (material == null)
                {
                    continue;
                }

                var identifier = new AssetImporter.SourceAssetIdentifier(typeof(Material), manifest.Slots[i]);
                UnityEngine.Object current;
                if (importer.GetExternalObjectMap().TryGetValue(identifier, out current) &&
                    current == material)
                {
                    continue;
                }

                importer.AddRemap(identifier, material);
            }
        }

        // ------------------------------------------------------------------ sync ---

        private static void SyncDef(string def, PropArtSyncReport report)
        {
            if (!s_SyncInProgress.Add(def))
            {
                return;
            }

            try
            {
                PropArtManifest manifest;
                string error;
                if (!TryLoadManifest(def, out manifest, out error))
                {
                    report.Errors.Add(def + ": " + error);
                    return;
                }

                string fbxPath = FbxAssetPath(def);
                var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (importer == null)
                {
                    report.Errors.Add(def + ": no ModelImporter at " + fbxPath +
                                      " (has Unity imported the FBX?)");
                    return;
                }

                bool importerChanged = ConfigureImporter(importer);

                List<string> sourceMaterialNames = CollectSourceMaterialNames(def, importer, manifest.Slots);
                if (sourceMaterialNames.Count == 0)
                {
                    // Embedded materials are not necessarily sub-assets once every slot is
                    // remapped, and an FBX reimported with an empty map may not have finished
                    // writing them. The identity mapping is the documented contract, so use
                    // the manifest names rather than refusing; verification below reports a
                    // binding that did not take.
                    for (int i = 0; i < manifest.Slots.Length; i++)
                    {
                        sourceMaterialNames.Add(manifest.Slots[i]);
                    }

                    report.Warnings.Add(def + ": no imported material sub-assets or remaps were readable; " +
                                        "using the manifest material names");
                }

                int errorsBefore = report.Errors.Count;
                string[] sourceNames = new string[manifest.Slots.Length];
                int[] bindingIndex = new int[manifest.Slots.Length];
                for (int i = 0; i < bindingIndex.Length; i++)
                {
                    bindingIndex[i] = -1;
                }

                for (int sourceIndex = 0; sourceIndex < sourceMaterialNames.Count; sourceIndex++)
                {
                    string sourceName = sourceMaterialNames[sourceIndex];
                    if (string.IsNullOrEmpty(sourceName))
                    {
                        report.Errors.Add(def + ": an imported FBX material slot has no name");
                        continue;
                    }

                    string declaredName;
                    if (!TryResolveSlot(sourceName, def, manifest.Slots, out declaredName))
                    {
                        report.Errors.Add(def + ": FBX material '" + sourceName +
                                          "' does not match any manifest material");
                        continue;
                    }

                    int slotIndex = Array.IndexOf(manifest.Slots, declaredName);
                    if (bindingIndex[slotIndex] >= 0)
                    {
                        report.Errors.Add(def + ": two FBX materials ('" + sourceNames[slotIndex] + "' and '" +
                                          sourceName + "') map to manifest material '" + declaredName + "'");
                        continue;
                    }

                    bindingIndex[slotIndex] = sourceIndex;
                    sourceNames[slotIndex] = sourceName;
                }

                var missing = new List<string>();
                for (int i = 0; i < bindingIndex.Length; i++)
                {
                    if (bindingIndex[i] < 0)
                    {
                        missing.Add(manifest.Slots[i]);
                    }
                }

                if (missing.Count > 0)
                {
                    report.Errors.Add(def + ": manifest material(s) missing from the FBX: " +
                                      string.Join(", ", missing.ToArray()));
                }

                // Never create a partial material set when the slot map is wrong: a half
                // applied colour table is harder to diagnose than a refused import.
                if (report.Errors.Count != errorsBefore)
                {
                    return;
                }

                var materials = new Material[manifest.Slots.Length];
                for (int i = 0; i < manifest.Slots.Length; i++)
                {
                    bool created;
                    bool updated;
                    string warning;
                    if (!TryEnsureMaterial(def, manifest.Slots[i], manifest.BaseColors[i],
                            out materials[i], out created, out updated, out warning, out error))
                    {
                        report.Errors.Add(def + ": " + error);
                        return;
                    }

                    if (warning != null)
                    {
                        report.Warnings.Add(def + ": " + warning);
                    }

                    if (created)
                    {
                        report.MaterialsCreated++;
                    }
                    else if (updated)
                    {
                        report.MaterialsUpdated++;
                    }
                    else
                    {
                        report.MaterialsUnchanged++;
                    }
                }

                if (ApplyRemaps(importer, def, sourceNames, materials))
                {
                    importerChanged = true;
                }

                if (importerChanged)
                {
                    // Persist the importer settings/remaps and reimport this one model.
                    // The nested OnPostprocessAllAssets callback returns immediately because
                    // this def is still in s_SyncInProgress.
                    importer.SaveAndReimport();
                    report.ModelsReimported++;
                }

                if (VerifyModelUsesMaterials(def, manifest, report))
                {
                    report.ModelsVerified++;
                }

                report.DefsSynced++;
            }
            catch (Exception exception)
            {
                // One broken asset must not stop the batch; name the def and keep going so
                // the Console shows every problem in one run.
                report.Errors.Add(def + ": unexpected " + exception.GetType().Name + ": " +
                                  exception.Message);
            }
            finally
            {
                s_SyncInProgress.Remove(def);
            }
        }

        // ------------------------------------------------------------- materials ---

        /// <summary>
        /// Names of the FBX's source materials, as Unity currently knows them. Two public
        /// sources are combined because neither is complete on its own: the embedded
        /// material sub-assets (present on a first import, but not necessarily created for a
        /// slot that is already remapped) and the existing external object map (present once
        /// this tool has run, but empty on a first import). The manifest names are the
        /// fallback in the caller, not here, so a genuinely missing slot still shows up as a
        /// mismatch.
        /// </summary>
        private static List<string> CollectSourceMaterialNames(string def, ModelImporter importer,
            string[] declaredNames)
        {
            var names = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var coveredSlots = new HashSet<string>(StringComparer.Ordinal);

            // Embedded sub-assets are the actual imported source names, so they win over the
            // external object map. If a remap key was written with a stale/renamed name,
            // preferring the sub-asset lets ApplyRemaps remove that key and rewrite it with
            // the real name instead of reporting a duplicate slot.
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(FbxAssetPath(def));
            for (int i = 0; i < assets.Length; i++)
            {
                var material = assets[i] as Material;
                if (material == null || string.IsNullOrEmpty(material.name))
                {
                    continue;
                }

                string declared;
                if (TryResolveSlot(material.name, def, declaredNames, out declared))
                {
                    coveredSlots.Add(declared);
                }

                if (seenNames.Add(material.name))
                {
                    names.Add(material.name);
                }
            }

            string ownedPrefix = "Prop_" + def + "_";
            foreach (KeyValuePair<AssetImporter.SourceAssetIdentifier, UnityEngine.Object> pair
                     in importer.GetExternalObjectMap())
            {
                if (pair.Key.type != typeof(Material) || pair.Key.name == null ||
                    !pair.Key.name.StartsWith(ownedPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string declared;
                if (!TryResolveSlot(pair.Key.name, def, declaredNames, out declared))
                {
                    // A stale remap from a renamed slot is removed by ApplyRemaps; do not
                    // treat it as a live source name and turn the cleanup into an error.
                    continue;
                }

                if (coveredSlots.Contains(declared))
                {
                    // The embedded material already names this slot; the map key is only a
                    // fallback for slots Unity did not materialize as sub-assets.
                    continue;
                }

                if (seenNames.Add(pair.Key.name))
                {
                    names.Add(pair.Key.name);
                }
            }

            return names;
        }

        private static bool TryEnsureMaterial(string def, string slot, Color desired,
            out Material material, out bool created, out bool updated, out string warning, out string error)
        {
            material = null;
            created = false;
            updated = false;
            warning = null;
            error = null;

            if (!IsSimpleAssetName(slot))
            {
                error = "manifest material name '" + slot + "' is not a safe asset file name";
                return false;
            }

            string assetPath = MaterialAssetPath(def, slot);
            material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                string folderError;
                if (!EnsureMaterialsFolder(def, out folderError))
                {
                    error = folderError;
                    return false;
                }

                Shader shader = Shader.Find(StandardShaderName);
                if (shader == null)
                {
                    error = "shader '" + StandardShaderName + "' was not found";
                    return false;
                }

                material = new Material(shader);
                material.name = slot;

                string colorProperty;
                if (!TryGetColorProperty(material, out colorProperty))
                {
                    UnityEngine.Object.DestroyImmediate(material);
                    material = null;
                    error = "new '" + StandardShaderName + "' material has no _Color/_BaseColor property";
                    return false;
                }

                material.SetColor(colorProperty, desired);

                // Match the existing hand-made prop materials: fully rough, non-metallic
                // flat colours. The manifest's roughness is not applied yet; the art tier is
                // deliberately flat-colour until the layout freeze (README section 9).
                if (material.HasProperty("_Glossiness"))
                {
                    material.SetFloat("_Glossiness", 0f);
                }

                if (material.HasProperty("_Metallic"))
                {
                    material.SetFloat("_Metallic", 0f);
                }

                // A flat Standard prop material: repeats of this prop share mesh and material,
                // so GPU instancing turns n draws into one. The hub assembly never edits a
                // committed asset; this sync is the material's owner (MaterialInstancing).
                material.enableInstancing = MaterialInstancing.IsEligible(material);

                AssetDatabase.CreateAsset(material, assetPath);
                AssetDatabase.SaveAssetIfDirty(material);
                created = true;
                return true;
            }

            if (material.shader == null)
            {
                error = "material " + assetPath + " has no shader";
                return false;
            }

            if (!string.Equals(material.shader.name, StandardShaderName, StringComparison.Ordinal))
            {
                warning = "adopted existing material with shader '" + material.shader.name + "'";
            }

            string property;
            if (!TryGetColorProperty(material, out property))
            {
                error = "material " + assetPath + " uses shader '" + material.shader.name +
                        "', which has no _Color/_BaseColor property";
                return false;
            }

            Color current = material.GetColor(property);
            if (!ColorsClose(current, desired))
            {
                material.SetColor(property, desired);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
                updated = true;
            }

            // Existing committed material: turn instancing on once. This rewrites the .mat on
            // the machine that runs the sync (the same run already writes the colour).
            if (!material.enableInstancing && MaterialInstancing.IsEligible(material))
            {
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
                updated = true;
            }

            return true;
        }

        private static bool EnsureMaterialsFolder(string def, out string error)
        {
            error = null;
            string folder = MaterialsFolderAssetPath(def);
            if (AssetDatabase.IsValidFolder(folder))
            {
                return true;
            }

            string parent = DefFolderAssetPath(def);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                error = "def folder " + parent + " is not in the AssetDatabase";
                return false;
            }

            string guid = AssetDatabase.CreateFolder(parent, MaterialsFolderName);
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
            {
                error = "could not create folder " + folder;
                return false;
            }

            return true;
        }

        private static bool TryGetColorProperty(Material material, out string property)
        {
            if (material.HasProperty("_Color"))
            {
                property = "_Color";
                return true;
            }

            if (material.HasProperty("_BaseColor"))
            {
                property = "_BaseColor";
                return true;
            }

            property = null;
            return false;
        }

        private static bool ColorsClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) <= ColorTolerance &&
                   Mathf.Abs(a.g - b.g) <= ColorTolerance &&
                   Mathf.Abs(a.b - b.b) <= ColorTolerance;
        }

        /// <summary>
        /// Writes the external object map: one entry per actual FBX material name pointing at
        /// the material asset for its declared slot, and removes stale
        /// <c>Prop_&lt;def&gt;_*</c> entries left by a renamed slot. Returns true when the map
        /// changed, which is what decides whether the FBX has to be reimported.
        /// </summary>
        private static bool ApplyRemaps(ModelImporter importer, string def, string[] sourceNames,
            Material[] materials)
        {
            bool changed = false;
            Dictionary<AssetImporter.SourceAssetIdentifier, UnityEngine.Object> map =
                importer.GetExternalObjectMap();

            for (int slot = 0; slot < sourceNames.Length; slot++)
            {
                var identifier = new AssetImporter.SourceAssetIdentifier(typeof(Material), sourceNames[slot]);
                UnityEngine.Object current;
                if (map.TryGetValue(identifier, out current) && current == materials[slot])
                {
                    continue;
                }

                importer.AddRemap(identifier, materials[slot]);
                changed = true;
            }

            string ownedPrefix = "Prop_" + def + "_";
            var stale = new List<AssetImporter.SourceAssetIdentifier>();
            foreach (KeyValuePair<AssetImporter.SourceAssetIdentifier, UnityEngine.Object> pair in map)
            {
                if (pair.Key.type != typeof(Material) || pair.Key.name == null ||
                    !pair.Key.name.StartsWith(ownedPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                bool stillDeclared = false;
                for (int slot = 0; slot < sourceNames.Length; slot++)
                {
                    if (string.Equals(sourceNames[slot], pair.Key.name, StringComparison.Ordinal))
                    {
                        stillDeclared = true;
                        break;
                    }
                }

                if (!stillDeclared)
                {
                    stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                importer.RemoveRemap(stale[i]);
                changed = true;
            }

            return changed;
        }

        // ----------------------------------------------------------------- verify ---

        /// <summary>
        /// Checks the imported prefab after the sync: every renderer material must be one of
        /// the generated assets, not an embedded/sub-asset material. An unused declared slot
        /// is a warning because it can legitimately have no faces; an embedded or unexpected
        /// material is an error because the prop would render with the wrong colour.
        /// </summary>
        private static bool VerifyModelUsesMaterials(string def, PropArtManifest manifest,
            PropArtSyncReport report)
        {
            string fbxPath = FbxAssetPath(def);
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null)
            {
                report.Errors.Add(def + ": imported model is missing at " + fbxPath);
                return false;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                report.Errors.Add(def + ": imported model has no Renderer");
                return false;
            }

            var expected = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < manifest.Slots.Length; i++)
            {
                expected.Add(MaterialAssetPath(def, manifest.Slots[i]));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool ok = true;
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    report.Errors.Add(def + ": renderer '" + renderer.gameObject.name + "' has no materials");
                    ok = false;
                    continue;
                }

                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material material = materials[materialIndex];
                    if (material == null)
                    {
                        report.Errors.Add(def + ": renderer '" + renderer.gameObject.name +
                                          "' has a null material in slot " + materialIndex);
                        ok = false;
                        continue;
                    }

                    string path = AssetDatabase.GetAssetPath(material);
                    if (string.Equals(path, fbxPath, StringComparison.Ordinal))
                    {
                        report.Errors.Add(def + ": renderer '" + renderer.gameObject.name +
                                          "' still uses the embedded material '" + material.name + "'");
                        ok = false;
                        continue;
                    }

                    if (!expected.Contains(path))
                    {
                        report.Errors.Add(def + ": renderer '" + renderer.gameObject.name +
                                          "' uses unexpected material '" + material.name + "' at " + path);
                        ok = false;
                        continue;
                    }

                    seen.Add(path);
                }
            }

            for (int i = 0; i < manifest.Slots.Length; i++)
            {
                string path = MaterialAssetPath(def, manifest.Slots[i]);
                if (!seen.Contains(path))
                {
                    report.Warnings.Add(def + ": material '" + manifest.Slots[i] +
                                        "' is not referenced by any Renderer (unused slot?)");
                }
            }

            return ok;
        }

        // --------------------------------------------------------------- helpers ---

        private static void CollectDefs(string[] assetPaths, SortedSet<string> defs)
        {
            if (assetPaths == null)
            {
                return;
            }

            for (int i = 0; i < assetPaths.Length; i++)
            {
                string def;
                if (TryGetDefFromFbxPath(assetPaths[i], out def) ||
                    TryGetDefFromManifestPath(assetPaths[i], out def))
                {
                    defs.Add(def);
                }
            }
        }

        private static bool TryMatchExact(string candidate, string[] declaredNames, out string match)
        {
            for (int i = 0; i < declaredNames.Length; i++)
            {
                if (string.Equals(declaredNames[i], candidate, StringComparison.Ordinal))
                {
                    match = declaredNames[i];
                    return true;
                }
            }

            match = null;
            return false;
        }

        private static bool TryMatchCaseInsensitive(string candidate, string[] declaredNames, out string match)
        {
            match = null;
            int matches = 0;
            for (int i = 0; i < declaredNames.Length; i++)
            {
                if (string.Equals(declaredNames[i], candidate, StringComparison.OrdinalIgnoreCase))
                {
                    match = declaredNames[i];
                    matches++;
                }
            }

            if (matches == 1)
            {
                return true;
            }

            match = null;
            return false;
        }

        /// <summary>Blender's duplicate-name suffix (<c>.001</c>) is not part of the slot.</summary>
        private static string StripBlenderDuplicateSuffix(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || name.Length - dot - 1 != 3)
            {
                return name;
            }

            for (int i = dot + 1; i < name.Length; i++)
            {
                if (name[i] < '0' || name[i] > '9')
                {
                    return name;
                }
            }

            return name.Substring(0, dot);
        }

        private static bool IsSimpleAssetName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
            {
                return false;
            }

            return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name.IndexOf(':') < 0;
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            return assetPath == null ? null : assetPath.Replace('\\', '/');
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>Counters and messages for one menu run or one import batch.</summary>
    public sealed class PropArtSyncReport
    {
        public int DefsSynced;
        public int MaterialsCreated;
        public int MaterialsUpdated;
        public int MaterialsUnchanged;
        public int ModelsReimported;
        public int ModelsVerified;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public bool HasChanges
        {
            get
            {
                return MaterialsCreated > 0 || MaterialsUpdated > 0 || ModelsReimported > 0;
            }
        }

        public void Log(string prefix, bool always)
        {
            for (int i = 0; i < Errors.Count; i++)
            {
                Debug.LogError(prefix + " " + Errors[i]);
            }

            for (int i = 0; i < Warnings.Count; i++)
            {
                Debug.LogWarning(prefix + " " + Warnings[i]);
            }

            if (!always && !HasChanges && Errors.Count == 0 && Warnings.Count == 0)
            {
                return;
            }

            Debug.Log(prefix + " synced " + DefsSynced + " def(s): materials created " +
                      MaterialsCreated + ", updated " + MaterialsUpdated + ", unchanged " +
                      MaterialsUnchanged + "; models reimported " + ModelsReimported +
                      ", verified " + ModelsVerified + "; errors " + Errors.Count +
                      ", warnings " + Warnings.Count + ".");
        }
    }
}
