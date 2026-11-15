using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.Pickup
{
    /// <summary>
    /// The one authoring path that fills <see cref="PickupFieldHost"/>'s six index-aligned
    /// serialized arrays from the props already placed in the scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why data is derived from the props (option (a)) instead of carried by an authoring
    /// component (option (b)). <c>PickupFieldHost</c>'s arrays are serialized on the host and
    /// are the only runtime source of prop data, so the authoring layer must be able to
    /// write them. An editor-only MonoBehaviour cannot do that job: its script lives in an
    /// Editor assembly, which has no runtime type, so Unity cannot serialize such a
    /// component into a scene that is built. Option (b) would add a runtime-assembly
    /// component plus build-time stripping for data that never needs to exist at runtime.
    /// This sync therefore reads what the scenery already says and keeps the host as the
    /// single owner of the rows:
    /// </para>
    /// <list type="bullet">
    /// <item><description>item id = the prop GameObject's name (the greybox's
    /// <c>id#NN</c> naming is already unique);</description></item>
    /// <item><description>kind = the name of the prop's parent folder, one folder per kind
    /// directly under the field root;</description></item>
    /// <item><description>bounds = the full-size world AABB of every Renderer in the prop's
    /// subtree (metres, not half size), unless the GameObject name ends with
    /// <c>@size=&lt;metres&gt;</c>: that declared absorb size becomes a cube of that edge and
    /// the suffix is stripped from the item id (a gate bench 3.2 m long but 1.25 m thick is
    /// recorded as 1.25 m, not 3.2 m - see
    /// <see cref="PickupFieldAuthoringRules.TryParseDeclaredSize"/>);</description></item>
    /// <item><description>collider = the prop's own Collider when it has one, else the first
    /// child Collider in ordinal path order (the host disables at most one per
    /// prop);</description></item>
    /// <item><description>size tier = longest AABB extent, then the value units come from the
    /// explicit (kind, tier) table in
    /// <see cref="PickupFieldAuthoringRules"/>.</description></item>
    /// </list>
    /// <para>
    /// Ordering is deterministic: candidates are sorted by field-root-relative hierarchy
    /// path with an ordinal comparison, so the same scene always produces the same row
    /// order and a re-run writes the same arrays. Rows that cannot be authored are never
    /// repaired silently: they are left out and named in the returned
    /// <see cref="PickupFieldSyncReport"/>.
    /// </para>
    /// <para>
    /// UdonSharp sequence (the project's Labs README "必做序列"): the proxy's serialized
    /// fields are written through <see cref="SerializedObject"/>, the U# program assets are
    /// compiled (only when some source is dirty), and then
    /// <c>UdonSharpEditorUtility.CopyProxyToUdon</c> copies the proxy into the backing
    /// <c>UdonBehaviour</c>. Compiling before the copy is what prevents
    /// "outdated behaviour version". The component, its program asset and the backing
    /// <c>UdonBehaviour</c> must already exist; this tool creates none of them (the README
    /// sequence for a fresh behaviour is a separate step).
    /// </para>
    /// <para>
    /// Assembly placement: <c>Assets/VRC_Katamari/Editor/MapGreybox/VRCKatamari.Editor.asmdef</c>
    /// is the project's only editor assembly definition and it covers
    /// <c>Editor/MapGreybox</c> only, so files in this folder compile into the predefined
    /// <c>Assembly-CSharp-Editor</c> assembly (editor-only, and auto-referenced by nothing).
    /// That works because both U# editor assemblies are auto-referenced, but it is not the
    /// project's stated layout: moving that asmdef up to <c>Editor/</c> (it would then need
    /// <c>UdonSharp.Runtime</c> and <c>UdonSharp.Editor</c> in its references) is the Lead's
    /// call; no code change is required either way.
    /// </para>
    /// </remarks>
    public static class PickupFieldSync
    {
        public const string MenuPath = "VRC Katamari/Pickup/Sync Prop Arrays From Children";
        public const string ContextMenuPath = "CONTEXT/PickupFieldHost/Sync Prop Arrays From Children";
        public const string UndoLabel = "Sync Pickup Field From Children";

        // Field names are the host's serialized contract. They are looked up, not assumed,
        // so a rename in Runtime/Pickup/PickupFieldHost.cs fails loudly here.
        private const string PropTransformsField = "propTransforms";
        private const string ItemIdsField = "itemIds";
        private const string KindsField = "kinds";
        private const string PropBoundsField = "propBounds";
        private const string ValueUnitsField = "valueUnits";
        private const string PropCollidersField = "propColliders";

        // ------------------------------------------------------------------ menu ---

        [MenuItem(MenuPath, true)]
        private static bool ValidateSyncSelection()
        {
            return CollectSelectedHosts().Count > 0;
        }

        /// <summary>
        /// Syncs every selected <see cref="PickupFieldHost"/> from the children of its own
        /// GameObject. One report per host goes to the Console.
        /// </summary>
        [MenuItem(MenuPath, false, 100)]
        private static void SyncSelection()
        {
            List<PickupFieldHost> hosts = CollectSelectedHosts();
            if (hosts.Count == 0)
            {
                Debug.LogError("[PickupFieldSync] no PickupFieldHost in the selection. Select the host object " +
                               "(or a prop under it) and run the menu again. Props are read from " +
                               "<host object>/<kind>/<prop>; use the public SyncFromChildren(host, fieldRoot) " +
                               "entry point when the props live under a different root.");
                return;
            }

            for (int i = 0; i < hosts.Count; i++)
            {
                SyncFromChildren(hosts[i], hosts[i].transform).Log();
            }
        }

        [MenuItem(ContextMenuPath, false, 100)]
        private static void SyncFromContextMenu(MenuCommand command)
        {
            var host = command.context as PickupFieldHost;
            if (host == null)
            {
                Debug.LogError("[PickupFieldSync] the context menu command had no PickupFieldHost.");
                return;
            }

            SyncFromChildren(host, host.transform).Log();
        }

        // -------------------------------------------------------------- entry point ---

        /// <summary>
        /// Reads the props under <paramref name="fieldRoot"/> and writes the host's six
        /// index-aligned arrays.
        /// </summary>
        /// <param name="host">The field host to fill; its backing UdonBehaviour must already exist.</param>
        /// <param name="fieldRoot">
        /// The root whose immediate children are kind folders. The menu passes the host's own
        /// transform; a level that keeps its props elsewhere passes that transform instead.
        /// </param>
        /// <returns>What was written, and every row that was refused and why.</returns>
        public static PickupFieldSyncReport SyncFromChildren(PickupFieldHost host, Transform fieldRoot)
        {
            var report = new PickupFieldSyncReport();

            if (host == null)
            {
                report.Fail("the host is null");
                return report;
            }

            report.HostPath = HierarchyPath(host.transform);

            if (fieldRoot == null)
            {
                report.Fail("the field root is null");
                return report;
            }

            report.FieldRootPath = HierarchyPath(fieldRoot);

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.Fail("exit Play Mode first; the sync writes serialized scene data");
                return report;
            }

            if (!UdonSharpEditorUtility.IsProxyBehaviour(host))
            {
                // Checked before anything is written: a plain MonoBehaviour with the same
                // script and no backing UdonBehaviour would accept the SerializedObject write
                // and then never run in the world.
                report.Fail("the host is not an UdonSharp proxy: add the backing UdonBehaviour and its program " +
                            "asset first (VRC_Katamari/Labs/README.md, the U# scene-building sequence)");
                return report;
            }

            List<PropCandidate> candidates = CollectCandidates(fieldRoot);
            List<PropRow> rows = ValidateCandidates(candidates, report);

            if (rows.Count == 0 && report.Rejected.Count > 0)
            {
                // Every candidate was refused. Writing empty arrays here would erase a field
                // the author can still fix, so the existing rows are left untouched.
                report.Fail("every candidate row was rejected; the host's arrays were left untouched");
                return report;
            }

            if (candidates.Count == 0)
            {
                report.Notes.Add("no prop objects were found under the field root; the host's arrays are cleared");
            }

            if (!WriteRows(host, rows, report))
            {
                return report;
            }

            MarkDirty(host);
            CopyProxy(host, report);
            report.ReadBackVerified = VerifyReadBack(host, rows.Count);
            if (!report.ReadBackVerified && string.IsNullOrEmpty(report.Failure))
            {
                report.Fail("the six arrays did not read back at the expected length (" + rows.Count +
                            "); the write did not persist");
            }

            if (string.IsNullOrEmpty(report.Failure))
            {
                report.Success = true;
            }

            return report;
        }

        // ---------------------------------------------------------------- collection ---

        private sealed class PropCandidate
        {
            public Transform Root;
            public string Kind;
            public string Path;
            public string ItemId;
            public string SourceName;
            public float DeclaredSizeM;
            public string DeclaredSizeError;
            public bool HasRenderer;
            public Vector3 BoundsSize;
            public Collider PrimaryCollider;
            public int ColliderCount;
            public string Rejection;
        }

        private sealed class PropRow
        {
            public Transform Root;
            public string Kind;
            public string ItemId;
            public Vector3 BoundsSize;
            public int ValueUnits;
            public Collider PrimaryCollider;
        }

        private static List<PropCandidate> CollectCandidates(Transform fieldRoot)
        {
            var candidates = new List<PropCandidate>();
            for (int i = 0; i < fieldRoot.childCount; i++)
            {
                Transform kindFolder = fieldRoot.GetChild(i);
                string kind = kindFolder.name;

                if (kindFolder.GetComponent<Renderer>() != null)
                {
                    // A renderer on the kind folder itself would swallow the folder's subtree
                    // as one prop, which is never what "one folder per kind" means.
                    candidates.Add(RejectedCandidate(kindFolder, kind, fieldRoot,
                        "kind folder has its own Renderer; props belong inside the kind folder"));
                }

                CollectPropsUnder(kindFolder, kind, fieldRoot, candidates);
            }

            candidates.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
            return candidates;
        }

        private static void CollectPropsUnder(Transform parent, string kind, Transform fieldRoot,
            List<PropCandidate> candidates)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);

                if (child.GetComponent<Renderer>() != null)
                {
                    // The topmost renderer-bearing object is one prop; its whole subtree is
                    // that prop's visual and is not walked further.
                    candidates.Add(Candidate(child, kind, fieldRoot));
                    continue;
                }

                if (child.childCount > 0)
                {
                    CollectPropsUnder(child, kind, fieldRoot, candidates);
                    continue;
                }

                bool hasCollider = child.GetComponent<Collider>() != null;
                candidates.Add(RejectedCandidate(child, kind, fieldRoot, hasCollider
                    ? "no Renderer: bounds cannot be derived from a collider-only object"
                    : "no Renderer: a prop row needs renderer bounds"));
            }
        }

        private static PropCandidate Candidate(Transform root, string kind, Transform fieldRoot)
        {
            var candidate = new PropCandidate();
            candidate.Root = root;
            candidate.Kind = kind;
            candidate.Path = RelativePath(fieldRoot, root);
            candidate.SourceName = root.name;
            ReadDeclaredSize(candidate);
            candidate.HasRenderer = true;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                candidate.HasRenderer = false;
                candidate.Rejection = "no Renderer in the prop's subtree";
                return candidate;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            candidate.BoundsSize = bounds.size;

            var colliders = new List<Collider>(root.GetComponentsInChildren<Collider>(true));
            colliders.Sort((left, right) => string.CompareOrdinal(RelativePath(root, left.transform),
                RelativePath(root, right.transform)));
            candidate.ColliderCount = colliders.Count;
            if (colliders.Count > 0)
            {
                // One collider per prop: the prop's own component wins, otherwise the first
                // one in ordinal path order. Extra colliders are reported, not wired.
                Collider own = root.GetComponent<Collider>();
                candidate.PrimaryCollider = own != null ? own : colliders[0];
            }

            return candidate;
        }

        private static PropCandidate RejectedCandidate(Transform root, string kind, Transform fieldRoot,
            string reason)
        {
            var candidate = new PropCandidate();
            candidate.Root = root;
            candidate.Kind = kind;
            candidate.Path = RelativePath(fieldRoot, root);
            candidate.SourceName = root.name;
            ReadDeclaredSize(candidate);
            candidate.Rejection = reason;
            return candidate;
        }

        /// <summary>
        /// Reads the <c>@size=</c> suffix into the candidate: the stripped item id, the
        /// declared edge, and the reason when the marker is malformed.
        /// </summary>
        private static void ReadDeclaredSize(PropCandidate candidate)
        {
            string itemId;
            float declaredSizeM;
            string error;
            PickupFieldAuthoringRules.TryParseDeclaredSize(candidate.SourceName, out itemId,
                out declaredSizeM, out error);
            candidate.ItemId = itemId;
            candidate.DeclaredSizeM = declaredSizeM;
            candidate.DeclaredSizeError = error;
        }

        // -------------------------------------------------------------- validation ---

        private static List<PropRow> ValidateCandidates(List<PropCandidate> candidates,
            PickupFieldSyncReport report)
        {
            var ids = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                PropCandidate candidate = candidates[i];
                if (candidate.Rejection != null || !candidate.HasRenderer)
                {
                    continue;
                }

                ids.Add(candidate.ItemId);
            }

            string[] duplicates = PickupFieldAuthoringRules.FindDuplicateIds(ids.ToArray());

            var rows = new List<PropRow>();
            int multiColliderProps = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                PropCandidate candidate = candidates[i];

                if (candidate.Rejection != null)
                {
                    report.Reject(candidate.Path, candidate.ItemId, candidate.Rejection);
                    continue;
                }

                if (candidate.DeclaredSizeError != null)
                {
                    // A declared size that cannot be read is reported, never replaced by the
                    // AABB: the author asked for a specific pickup size and must learn that
                    // the request was refused.
                    report.Reject(candidate.Path, candidate.ItemId, candidate.DeclaredSizeError);
                    continue;
                }

                if (!PickupFieldAuthoringRules.IsValidItemId(candidate.ItemId))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "item id is empty or has leading/trailing whitespace");
                    continue;
                }

                if (IsDuplicate(duplicates, candidate.ItemId))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "duplicate itemId '" + candidate.ItemId + "' (every row sharing it is refused)");
                    continue;
                }

                if (!PickupFieldAuthoringRules.IsValidKind(candidate.Kind))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "kind folder name is empty or has leading/trailing whitespace");
                    continue;
                }

                string aabbTier = PickupFieldAuthoringRules.ClassifySizeTier(candidate.BoundsSize.x,
                    candidate.BoundsSize.y, candidate.BoundsSize.z);
                if (aabbTier.Length == 0)
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "zero or non-finite bounds " + FormatVector(candidate.BoundsSize) +
                        "; a prop needs a positive renderer AABB");
                    continue;
                }

                // A declared size replaces the AABB-derived size with a cube of that edge. The
                // AABB is still required above: a renderer whose AABB is degenerate is a data
                // error even when the pickup size is spelled out in the name.
                Vector3 pickSize = candidate.BoundsSize;
                string tier = aabbTier;
                string declaredSizeNote = null;
                if (candidate.DeclaredSizeM > 0f)
                {
                    pickSize = new Vector3(candidate.DeclaredSizeM, candidate.DeclaredSizeM,
                        candidate.DeclaredSizeM);
                    tier = PickupFieldAuthoringRules.ClassifySizeTier(pickSize.x, pickSize.y, pickSize.z);
                    declaredSizeNote = "'" + candidate.Path + "' declares " + candidate.SourceName +
                                       ": propBounds is a " + FormatMetres(candidate.DeclaredSizeM) +
                                       " m cube instead of the " + FormatVector(candidate.BoundsSize) +
                                       " m renderer AABB (size tier " + aabbTier + " -> " + tier + ")";
                }

                int declared = PickupFieldAuthoringRules.LookupValueUnits(candidate.Kind, tier);
                int valueUnits;
                if (!PickupFieldAuthoringRules.HasValueRow(candidate.Kind, tier))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "no value-table row for kind '" + candidate.Kind + "' + size tier '" + tier +
                        "' (bounds " + FormatVector(pickSize) + ")");
                    continue;
                }

                if (!PickupFieldAuthoringRules.TryValidateValueUnits(declared, out valueUnits))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "value " + declared + " is not a whole number >= 1 (Docs/OPEN_DECISIONS.md (j))");
                    continue;
                }

                var row = new PropRow();
                row.Root = candidate.Root;
                row.Kind = candidate.Kind;
                row.ItemId = candidate.ItemId;
                row.BoundsSize = pickSize;
                row.ValueUnits = valueUnits;
                row.PrimaryCollider = candidate.PrimaryCollider;
                rows.Add(row);

                report.CountRow(row.Kind, row.ValueUnits);
                report.TotalValueUnits += row.ValueUnits;

                if (declaredSizeNote != null)
                {
                    report.Notes.Add(declaredSizeNote);
                }

                if (candidate.ColliderCount > 1 && candidate.PrimaryCollider != null)
                {
                    multiColliderProps++;
                }
            }

            if (multiColliderProps > 0)
            {
                report.Notes.Add(multiColliderProps + " prop(s) have more than one collider; propColliders wires " +
                                 "only one per prop (the host disables one collider on attach)");
            }

            report.PropCount = rows.Count;
            return rows;
        }

        private static bool IsDuplicate(string[] duplicates, string itemId)
        {
            for (int i = 0; i < duplicates.Length; i++)
            {
                if (string.Equals(duplicates[i], itemId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------- write ---

        private static bool WriteRows(PickupFieldHost host, List<PropRow> rows, PickupFieldSyncReport report)
        {
            var serialized = new SerializedObject(host);
            var properties = new SerializedProperty[6];
            string[] fieldNames =
            {
                PropTransformsField, ItemIdsField, KindsField, PropBoundsField, ValueUnitsField,
                PropCollidersField,
            };

            for (int i = 0; i < fieldNames.Length; i++)
            {
                string failure;
                properties[i] = RequireArray(serialized, fieldNames[i], out failure);
                if (properties[i] == null)
                {
                    report.Fail(failure);
                    return false;
                }
            }

            SerializedProperty transforms = properties[0];
            SerializedProperty ids = properties[1];
            SerializedProperty kinds = properties[2];
            SerializedProperty bounds = properties[3];
            SerializedProperty values = properties[4];
            SerializedProperty colliders = properties[5];

            // Registered only after the contract fields were found, so a failed lookup leaves
            // no empty undo step behind.
            Undo.RegisterCompleteObjectUndo(host, UndoLabel);

            bool anyCollider = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].PrimaryCollider != null)
                {
                    anyCollider = true;
                    break;
                }
            }

            transforms.arraySize = rows.Count;
            ids.arraySize = rows.Count;
            kinds.arraySize = rows.Count;
            bounds.arraySize = rows.Count;
            values.arraySize = rows.Count;
            colliders.arraySize = anyCollider ? rows.Count : 0;

            for (int i = 0; i < rows.Count; i++)
            {
                PropRow row = rows[i];
                transforms.GetArrayElementAtIndex(i).objectReferenceValue = row.Root;
                ids.GetArrayElementAtIndex(i).stringValue = row.ItemId;
                kinds.GetArrayElementAtIndex(i).stringValue = row.Kind;
                bounds.GetArrayElementAtIndex(i).vector3Value = row.BoundsSize;
                values.GetArrayElementAtIndex(i).intValue = row.ValueUnits;
                if (anyCollider)
                {
                    colliders.GetArrayElementAtIndex(i).objectReferenceValue = row.PrimaryCollider;
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private static SerializedProperty RequireArray(SerializedObject serialized, string fieldName,
            out string failure)
        {
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                failure = "serialized field '" + fieldName + "' was not found on " +
                          serialized.targetObject.GetType().Name +
                          "; the host's field names are part of the authoring contract";
                return null;
            }

            if (!property.isArray)
            {
                failure = "serialized field '" + fieldName + "' is not an array";
                return null;
            }

            failure = null;
            return property;
        }

        // ---------------------------------------------------------------- Udon copy ---

        private static void MarkDirty(PickupFieldHost host)
        {
            EditorUtility.SetDirty(host);

            Scene scene = host.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        private static void CopyProxy(PickupFieldHost host, PickupFieldSyncReport report)
        {
            try
            {
                // Compile first, copy second: copying into an out-of-date program asset throws
                // "outdated behaviour version". forceCompile stays false so a sync with no
                // dirty script does not recompile every program in the project.
                UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
            }
            catch (Exception exception)
            {
                report.Fail("the UdonSharp compile step threw: " + exception.Message);
                return;
            }

            try
            {
                UdonSharpEditorUtility.CopyProxyToUdon(host);
                report.UdonCopyIssued = true;
            }
            catch (Exception exception)
            {
                report.Fail("CopyProxyToUdon threw: " + exception.Message +
                            ". If the message names an outdated behaviour version, the U# compile was still " +
                            "running; wait for it to finish and run the sync again");
            }
        }

        private static bool VerifyReadBack(PickupFieldHost host, int expectedRows)
        {
            var serialized = new SerializedObject(host);
            string[] fields =
            {
                PropTransformsField, ItemIdsField, KindsField, PropBoundsField, ValueUnitsField,
            };

            for (int i = 0; i < fields.Length; i++)
            {
                SerializedProperty property = serialized.FindProperty(fields[i]);
                if (property == null || !property.isArray || property.arraySize != expectedRows)
                {
                    return false;
                }
            }

            SerializedProperty colliders = serialized.FindProperty(PropCollidersField);
            if (colliders == null || !colliders.isArray)
            {
                return false;
            }

            // propColliders is allowed to be empty (no prop has a collider) but never longer
            // than the other rows.
            return colliders.arraySize == 0 || colliders.arraySize == expectedRows;
        }

        // ------------------------------------------------------------------ utilities ---

        private static List<PickupFieldHost> CollectSelectedHosts()
        {
            var hosts = new List<PickupFieldHost>();
            GameObject[] selected = Selection.gameObjects;
            if (selected == null)
            {
                return hosts;
            }

            for (int i = 0; i < selected.Length; i++)
            {
                GameObject gameObject = selected[i];
                if (gameObject == null)
                {
                    continue;
                }

                PickupFieldHost host = gameObject.GetComponent<PickupFieldHost>();
                if (host == null)
                {
                    host = gameObject.GetComponentInParent<PickupFieldHost>();
                }

                if (host != null && !hosts.Contains(host))
                {
                    hosts.Add(host);
                }
            }

            hosts.Sort((left, right) => string.CompareOrdinal(HierarchyPath(left.transform),
                HierarchyPath(right.transform)));
            return hosts;
        }

        private static string RelativePath(Transform root, Transform target)
        {
            var parts = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string HierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                   value.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                   value.z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        private static string FormatMetres(float metres)
        {
            return metres.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
