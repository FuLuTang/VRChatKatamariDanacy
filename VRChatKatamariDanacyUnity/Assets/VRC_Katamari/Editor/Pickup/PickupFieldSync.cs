using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// The one authoring path that fills <see cref="PickupFieldHost"/>'s eight index-aligned
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
    /// <see cref="PickupFieldAuthoringRules.TryParseDeclaredSize"/>). This column is the
    /// world AABB on purpose: the absorb gate, the blocking boxes and the support
    /// classification all read it as an axis-aligned world box;</description></item>
    /// <item><description>prop-local bounds = the same prop measured along its own root-local
    /// axes, with the root's rotation removed and child rotations/scales baked in. The
    /// radial-proxy shell rotates this size by the prop's ball-local rotation (which already
    /// contains the root rotation), so it must NOT receive the world AABB - that would turn a
    /// yaw-90 prop's box twice. The declared <c>@size=</c> edge is applied here too;</description></item>
    /// <item><description>collider = the prop's own Collider when it has one, else the first
    /// child Collider in ordinal path order (the host disables at most one per prop). The
    /// array is index-aligned with <c>propTransforms</c>: one slot per row, and a null slot
    /// means that prop stays collidable when it is absorbed. When the caller opts in with
    /// <c>createMissingColliders</c> (the level importer does), a prop with no Collider
    /// anywhere in its subtree gets a <see cref="BoxCollider"/> on its own root, sized and
    /// centred from its visual local AABB, and that box becomes the row's slot. The created
    /// box is deliberately measured from the visual mesh, not from the declared pickup size:
    /// <c>@size=</c> changes the gameplay volume, never the physical shape;</description></item>
    /// <item><description>value units = the caller's declaration for that item id (the level
    /// importer passes <c>placement.json</c>'s <c>defs[].valueUnits</c>/<c>props[].valueUnits</c>),
    /// else the value already serialized on the host for that id, else 0 with a report note.
    /// A missing declaration or an old value-table row never drops a prop
    /// (Docs/OPEN_DECISIONS.md (q) U2). The column and every number on this path are 64-bit
    /// (Docs/OPEN_DECISIONS.md (r) step B), so the write and the read-back use
    /// <see cref="SerializedProperty.longValue"/> and a declared value past
    /// <c>int.MaxValue</c> survives both.</description></item>
    /// <item><description>volume factor k = the caller's declaration for that item id (the
    /// level importer passes the def's <c>volumeK</c>, default 0.70; a prop's own
    /// <c>volumeK</c> wins), else the k already serialized on the host, else the original's
    /// default 0.70. The column is <c>propVolumeK</c> and it is what the absorb gate prices
    /// (<c>Req = floor(1000*cbrt(60*k*a*b*c/pi))</c>), so a row outside
    /// <c>0 &lt; k &lt;= 32</c> is refused rather than clamped.</description></item>
    /// </list>
    /// <para>
    /// The value-units rule is intentionally not a price. Before U2 this class derived
    /// <c>valueUnits</c> from the longest AABB edge through a (kind, tier) table; that made
    /// a level's declared content value unreachable and dropped any prop whose (kind, tier)
    /// had no row. Now the declaration is the only source, and a host that is re-synced
    /// without one keeps its serialized values bit-for-bit - the path un-migrated scenes
    /// (<c>StoryLevel</c>'s 110 props, <c>CpuSkirmish</c>) take.
    /// </para>
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
        private const string PropLocalBoundsField = "propLocalBounds";
        private const string GrowthMm3Field = "propGrowthMm3";
        private const string VolumeKField = "propVolumeK";
        private const string ParentIndexField = "propParentIndex";
        private const string PropCollidersField = "propColliders";
        private const string PropProxyKindsField = "propProxyKinds";
        private const string PropProxyCentersField = "propProxyCenters";
        private const string PropProxySizesField = "propProxySizes";
        private const string PropProxyRadiiField = "propProxyRadii";
        private const string PropProxyHalfHeightsField = "propProxyHalfHeights";
        private const string PropProxyAxesField = "propProxyAxes";
        private const string PropProxyHullStartsField = "propProxyHullStarts";
        private const string PropProxyHullCountsField = "propProxyHullCounts";
        private const string PropProxyHullPointsField = "propProxyHullPoints";
        private const string PropProxyFaceStartsField = "propProxyFaceStarts";
        private const string PropProxyFaceCountsField = "propProxyFaceCounts";
        private const string PropProxyHullFacesField = "propProxyHullFaces";

        // A BoxCollider with a zero side never collides. A visual can legitimately have a
        // zero-thickness side (paper), so the created box is given a 1 mm floor instead of
        // being silently left out of the wiring.
        private const float MinCreatedColliderExtentM = 0.001f;

        // The authored growth is the authority; the measured bounds are a VERIFICATION. They
        // cannot agree to the millimetre because Unity's renderer bounds and the authored aabb
        // round differently (O7 samples: 9203 vs 9193 mm^3), so a disagreement is a WARNING
        // with the item id when it exceeds the budget in
        // PickupFieldAuthoringRules.GrowthAgrees (max(2%, 1 cm^3)). The verification is made
        // in the prop's OWN axes against the authored (unrotated) aabb, never against the
        // world AABB of a turned prop: see PickupFieldAuthoringRules.CheckDeclaredGrowth.
        // Only this many mismatches are written out; the total is always counted.
        private const int GrowthMismatchNoteLimit = 20;

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
        /// Syncs with no declaration map: every value already serialized on the host is
        /// preserved. This is the menu path and the un-migrated scene path (StoryLevel's 110
        /// props, CpuSkirmish).
        /// </summary>
        public static PickupFieldSyncReport SyncFromChildren(PickupFieldHost host, Transform fieldRoot)
        {
            return SyncFromChildren(host, fieldRoot, null);
        }

        /// <summary>
        /// Reads the props under <paramref name="fieldRoot"/> and writes the host's eight
        /// index-aligned arrays.
        /// </summary>
        /// <param name="host">The field host to fill; its backing UdonBehaviour must already exist.</param>
        /// <param name="fieldRoot">
        /// The root whose immediate children are kind folders. The menu passes the host's own
        /// transform; a level that keeps its props elsewhere passes that transform instead.
        /// </param>
        /// <param name="declaredGrowthMm3">
        /// Item id to the AUTHORED growth in mm^3, supplied by the level importer from
        /// <c>placement.json</c>'s derived <c>props[].growthMm3</c> column. This is the
        /// authority: it is written into the host verbatim, so the host sum, the report's
        /// total and the file's total are one number. An id with no authored value (an old
        /// file) is priced from the measured bounds and counted as such; pass <c>null</c>
        /// for the menu/un-migrated path (every row then uses the bounds pricing).
        /// </param>
        /// <returns>What was written, and every row that was refused and why.</returns>
        public static PickupFieldSyncReport SyncFromChildren(PickupFieldHost host, Transform fieldRoot,
            IDictionary<string, long> declaredGrowthMm3)
        {
            return SyncFromChildren(host, fieldRoot, declaredGrowthMm3, false);
        }

        /// <summary>
        /// Same as the three-argument overload, and additionally creates a
        /// <see cref="BoxCollider"/> on every prop row that has no Collider at all when
        /// <paramref name="createMissingColliders"/> is true.
        /// </summary>
        /// <remarks>
        /// The menu path leaves the flag false so an existing scene is never edited beyond
        /// the host's arrays. The level importer passes true for a whole-level document,
        /// because its props are fresh FBX instances and the FBX import has
        /// <c>addColliders: 0</c>: without this step every imported prop would leave a null
        /// <c>propColliders</c> slot, the host would warn, and an absorbed prop would keep
        /// whatever collider it had. Cell documents keep the old behaviour; their props are
        /// hand-authored scenery and the importer must not add physics to them.
        /// </remarks>
        public static PickupFieldSyncReport SyncFromChildren(PickupFieldHost host, Transform fieldRoot,
            IDictionary<string, long> declaredGrowthMm3, bool createMissingColliders)
        {
            return SyncFromChildren(host, fieldRoot, declaredGrowthMm3, null, null, null,
                createMissingColliders);
        }

        /// <summary>
        /// The full entry point with declared collider proxies: a level's
        /// <c>defs[].collider</c> mapped to item ids. A null map is the menu path and
        /// preserves every row already serialized on the host.
        /// </summary>
        /// <param name="exemptFromField">
        /// Item ids that must NOT get a pickup row even though the level declares them: D-5's
        /// fourth layer (§18.3 ③), a non-uniformly scaled, collider-less, non-absorbable decal.
        /// Exempt ids are removed before validation (no row, no rejection) and dropped from the
        /// declaration maps the read-back compares against, so "declared k, no row" cannot fire
        /// for them. Null means "nothing is exempt" - the v1 and menu path.
        /// </param>
        /// <param name="declaredParents">
        /// Child item id to parent item id (the level's <c>props[].parent</c>), written as the
        /// host's <c>propParentIndex</c> column by id lookup. Null means "no declaration": the
        /// parents already serialized on the host are preserved by id, like k.
        /// </param>
        public static PickupFieldSyncReport SyncFromChildren(PickupFieldHost host, Transform fieldRoot,
            IDictionary<string, long> declaredGrowthMm3, IDictionary<string, float> declaredVolumeK,
            IDictionary<string, float> declaredVolumePenalty,
            IDictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> declaredProxies,
            bool createMissingColliders, ISet<string> exemptFromField = null,
            IDictionary<string, string> declaredParents = null)
        {
            var report = new PickupFieldSyncReport();
            // The growth column is written from the authored file values when present, and
            // verified against bounds x k x penalty (a warning, not a rewrite).
            report.GrowthPriced = true;

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

            // Read the host's current values before any array is resized: this is the whole
            // un-migrated-scene contract. A row whose item id is already present keeps its
            // serialized value unless the caller declared a different one.
            // Same rule for k: a re-sync without a declaration must not reprice a field.
            Dictionary<string, float> existingVolumeK = ReadExistingVolumeK(host);
            Dictionary<string, string> existingParents = ReadExistingParents(host);
            Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> existingProxies =
                ReadExistingProxies(host);

            List<PropCandidate> candidates = CollectCandidates(fieldRoot);
            if (exemptFromField != null && exemptFromField.Count > 0)
            {
                int removed = 0;
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    if (candidates[i].ItemId != null && exemptFromField.Contains(candidates[i].ItemId))
                    {
                        candidates.RemoveAt(i);
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    report.Notes.Add(removed + " prop(s) are declared as NOT participating in the " +
                                     "pickup field (D-5 decal: non-uniform scale, no collider, not " +
                                     "absorbable), so no row is written for them by design");
                }

                // The read-back checks compare against the declaration maps, so an exempt id has
                // to leave those maps too - otherwise "declared k, no row" reports a prop that
                // was deliberately left out.
                declaredVolumeK = DropExempt(declaredVolumeK, exemptFromField);
                declaredProxies = DropExemptProxies(declaredProxies, exemptFromField);
            }
            List<PropRow> rows = ValidateCandidates(candidates, declaredGrowthMm3,
                declaredVolumeK, existingVolumeK, declaredVolumePenalty,
                declaredProxies, existingProxies,
                createMissingColliders, report);

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

            var rowIds = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++) rowIds[i] = rows[i].ItemId;
            int parentsUnresolved;
            int parentsCut;
            int[] parentColumn = PickupFieldAuthoringRules.ResolveParentColumn(rowIds,
                declaredParents ?? (IDictionary<string, string>)existingParents,
                out parentsUnresolved, out parentsCut);
            if (parentsUnresolved > 0 || parentsCut > 0)
            {
                report.Notes.Add("parent column: " + parentsUnresolved + " edge(s) name a parent with no " +
                                 "pickup row (exempt or rejected) and " + parentsCut + " edge(s) were cut " +
                                 "to break a cycle; those rows have no parent (-1)");
            }

            if (!WriteRows(host, rows, parentColumn, report))
            {
                return report;
            }

            MarkDirty(host);
            CopyProxy(host, report);
            report.ReadBackVerified = VerifyReadBack(host, rows.Count, createMissingColliders) &&
                                      VerifyParentColumn(host, parentColumn);
            if (!report.ReadBackVerified)
            {
                if (string.IsNullOrEmpty(report.Failure))
                {
                    report.Fail("the eight arrays did not read back at the expected length (" + rows.Count +
                                ") or a propColliders slot was empty; the write did not persist");
                }
            }
            else
            {
                report.GrowthVerified = VerifyGrowthMm3(host, rows, report);
                if (!report.GrowthVerified && string.IsNullOrEmpty(report.Failure))
                {
                    report.Fail(report.GrowthMismatches.Count + " declared valueUnits row(s) did not " +
                                "read back as declared; treat the host arrays as wrong until this is fixed");
                }

                if (string.IsNullOrEmpty(report.Failure))
                {
                    string[] hostIds;
                    float[] hostVolumeK;
                    ReadHostVolumeKColumns(host, out hostIds, out hostVolumeK);
                    string[] kMismatches = PickupFieldAuthoringRules.FindVolumeKMismatches(
                        hostIds ?? new string[0], hostVolumeK ?? new float[0], declaredVolumeK);
                    if (kMismatches.Length > 0)
                    {
                        report.Fail(kMismatches.Length + " declared volumeK row(s) did not read back as " +
                                    "declared (" + string.Join("; ", kMismatches) + "); treat the host " +
                                    "arrays as wrong until this is fixed");
                    }
                }

                if (string.IsNullOrEmpty(report.Failure))
                {
                    report.ProxyVerified = VerifyDeclaredProxies(host, declaredProxies);
                    if (!report.ProxyVerified)
                    {
                        report.Fail("one or more declared collider proxy row(s) did not read back as " +
                                    "declared; treat the host proxy columns as wrong until this is fixed");
                    }
                }
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
            public Vector3 LocalBoundsSize;
            public Vector3 LocalBoundsCenter;
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
            public Vector3 LocalBoundsSize;
            public long GrowthMm3;
            /// <summary>The per-item volume factor k the gate will use (0.70 default).</summary>
            public float VolumeK;
            /// <summary>The def's collider proxy; null means the row keeps its OBB shape.</summary>
            public PickupFieldAuthoringRules.PickupProxyDefinition Proxy;
            public Collider PrimaryCollider;
        }

        private static IDictionary<string, float> DropExempt(IDictionary<string, float> declared,
            ISet<string> exempt)
        {
            if (declared == null || exempt == null || exempt.Count == 0) return declared;
            var kept = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, float> pair in declared)
            {
                if (!exempt.Contains(pair.Key)) kept[pair.Key] = pair.Value;
            }

            return kept;
        }

        private static IDictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition>
            DropExemptProxies(
                IDictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> declared,
                ISet<string> exempt)
        {
            if (declared == null || exempt == null || exempt.Count == 0) return declared;
            var kept = new Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition>(
                StringComparer.Ordinal);
            foreach (KeyValuePair<string, PickupFieldAuthoringRules.PickupProxyDefinition> pair in
                     declared)
            {
                if (!exempt.Contains(pair.Key)) kept[pair.Key] = pair.Value;
            }

            return kept;
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
            candidate.LocalBoundsSize = MeasureLocalBoundsAndCenter(root, renderers,
                out candidate.LocalBoundsCenter);

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

        /// <summary>
        /// Measures a prop's full extents along its OWN root-local axes, in world metres.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the second bounds interpretation: the radial-proxy shell multiplies
        /// these sizes by the prop's ball-local rotation, which already contains the prop
        /// root's rotation (<c>AttachmentMath.TryWorldPoseToLocal</c>). Feeding it the
        /// world AABB from <c>renderer.bounds</c> would therefore apply the prop's rotation
        /// twice and put a yaw-90 long prop's long axis on the wrong ball axis. The absorb
        /// gate, the blocking boxes and the support classification keep using the world
        /// AABB and are not affected by this column.
        /// </para>
        /// <para>
        /// Measurement: every mesh AABB is moved into root-local space corner by corner
        /// (so a rotated child is baked in), then each local extent is multiplied by the
        /// world length of that root axis - the localToWorld column norms - which keeps the
        /// result in metres when the prop or a parent is scaled. A renderer without a
        /// readable mesh contributes its world-AABB corners instead, which is conservative
        /// but still axis-correct in the root frame. The root rotation is deliberately not
        /// part of the result: the runtime applies it. Returns zero when nothing could be
        /// measured, and the caller then falls back to the world size for that row.
        /// </para>
        /// </remarks>
        private static Vector3 MeasureLocalBounds(Transform root, Renderer[] renderers)
        {
            Vector3 localCenter;
            return MeasureLocalBoundsAndCenter(root, renderers, out localCenter);
        }

        /// <summary>
        /// Measures the same local extents and, in addition, the centre of the local AABB in
        /// root-local units. The centre is what a created <see cref="BoxCollider"/> needs;
        /// the extents are still returned in world metres for <c>propLocalBounds</c>.
        /// </summary>
        private static Vector3 MeasureLocalBoundsAndCenter(Transform root, Renderer[] renderers,
            out Vector3 localCenter)
        {
            localCenter = Vector3.zero;
            if (root == null || renderers == null || renderers.Length == 0)
            {
                return Vector3.zero;
            }

            bool hasPoint = false;
            Vector3 minimum = Vector3.zero;
            Vector3 maximum = Vector3.zero;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    IncludeBoundsCorners(root, renderer.transform, filter.sharedMesh.bounds,
                        ref hasPoint, ref minimum, ref maximum);
                }
                else
                {
                    IncludeWorldBoundsCorners(root, renderer.bounds, ref hasPoint, ref minimum,
                        ref maximum);
                }
            }

            if (!hasPoint)
            {
                return Vector3.zero;
            }

            Vector3 localSize = maximum - minimum;
            localCenter = (minimum + maximum) * 0.5f;
            Matrix4x4 localToWorld = root.localToWorldMatrix;
            float axisX = localToWorld.MultiplyVector(Vector3.right).magnitude;
            float axisY = localToWorld.MultiplyVector(Vector3.up).magnitude;
            float axisZ = localToWorld.MultiplyVector(Vector3.forward).magnitude;
            return new Vector3(localSize.x * axisX, localSize.y * axisY, localSize.z * axisZ);
        }

        /// <summary>
        /// Returns the prop's wireable Collider, adding one on the prop root when the whole
        /// subtree has none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One collider per prop is the host's contract (<c>propColliders</c> is index-aligned
        /// with <c>propTransforms</c> and the host disables exactly one collider on attach or
        /// hide). The own-component rule and the ordinal child order match
        /// <see cref="Candidate"/>, so a prop that already had a collider is never given a
        /// second one.
        /// </para>
        /// <para>
        /// The created <see cref="BoxCollider"/> sits on the prop root, which the importer
        /// keeps at scale 1 with its origin at the model centre; the centre and size are
        /// still measured rather than assumed, so a child whose mesh is offset still gets a
        /// box around the real visual. Being an added component on a prefab instance, it is
        /// scene data: the next sync finds it and reuses it, which is what makes a second
        /// import report <c>+0 ~0 -0</c>.
        /// </para>
        /// </remarks>
        public static Collider EnsurePropCollider(Transform root, Vector3 fallbackSize)
        {
            if (root == null)
            {
                return null;
            }

            Collider existing = PrimaryColliderOf(root);
            if (existing != null)
            {
                return existing;
            }

            // Never edit a prefab asset from a scene sync. The importer instantiates
            // prefab instances, so this guard only protects a mistaken direct call.
            if (PrefabUtility.IsPartOfPrefabAsset(root))
            {
                return null;
            }

            Vector3 localCenter = Vector3.zero;
            Vector3 localSize = fallbackSize;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Vector3 measured = MeasureLocalBoundsAndCenter(root, renderers, out localCenter);
            if (PickupFieldAuthoringRules.HasUsableBounds(measured.x, measured.y, measured.z))
            {
                localSize = measured;
            }
            else
            {
                localCenter = Vector3.zero;
            }

            if (!PickupFieldAuthoringRules.HasUsableBounds(localSize.x, localSize.y, localSize.z))
            {
                return null;
            }

            // MeasureLocalBoundsAndCenter returns world metres; BoxCollider.size is in the
            // root's local units. An imported prop is scale 1, but a prefab with a scale
            // override still gets a correct box instead of one scaled twice.
            Vector3 axis = AxisLengths(root);
            localSize = new Vector3(DivideByAxis(localSize.x, axis.x),
                DivideByAxis(localSize.y, axis.y), DivideByAxis(localSize.z, axis.z));

            var box = Undo.AddComponent<BoxCollider>(root.gameObject);
            box.center = localCenter;
            box.size = new Vector3(
                Mathf.Max(localSize.x, MinCreatedColliderExtentM),
                Mathf.Max(localSize.y, MinCreatedColliderExtentM),
                Mathf.Max(localSize.z, MinCreatedColliderExtentM));
            return box;
        }

        private static Vector3 AxisLengths(Transform root)
        {
            Matrix4x4 localToWorld = root.localToWorldMatrix;
            return new Vector3(localToWorld.MultiplyVector(Vector3.right).magnitude,
                localToWorld.MultiplyVector(Vector3.up).magnitude,
                localToWorld.MultiplyVector(Vector3.forward).magnitude);
        }

        private static float DivideByAxis(float value, float axisLength)
        {
            if (axisLength <= 1e-6f || !IsFinite(axisLength))
            {
                return value;
            }

            return value / axisLength;
        }

        /// <summary>The prop's own Collider, else the first child Collider in path order.</summary>
        private static Collider PrimaryColliderOf(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            Collider own = root.GetComponent<Collider>();
            if (own != null)
            {
                return own;
            }

            var colliders = new List<Collider>(root.GetComponentsInChildren<Collider>(true));
            if (colliders.Count == 0)
            {
                return null;
            }

            colliders.Sort((left, right) => string.CompareOrdinal(RelativePath(root, left.transform),
                RelativePath(root, right.transform)));
            return colliders[0];
        }

        private static void IncludeBoundsCorners(Transform root, Transform source, Bounds bounds,
            ref bool hasPoint, ref Vector3 minimum, ref Vector3 maximum)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3(
                    (i & 1) == 0 ? -1f : 1f,
                    (i & 2) == 0 ? -1f : 1f,
                    (i & 4) == 0 ? -1f : 1f);
                Vector3 localPoint = bounds.center + Vector3.Scale(bounds.extents, sign);
                Vector3 rootLocal = root.InverseTransformPoint(source.TransformPoint(localPoint));
                IncludePoint(rootLocal, ref hasPoint, ref minimum, ref maximum);
            }
        }

        private static void IncludeWorldBoundsCorners(Transform root, Bounds bounds,
            ref bool hasPoint, ref Vector3 minimum, ref Vector3 maximum)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3(
                    (i & 1) == 0 ? -1f : 1f,
                    (i & 2) == 0 ? -1f : 1f,
                    (i & 4) == 0 ? -1f : 1f);
                Vector3 worldPoint = bounds.center + Vector3.Scale(bounds.extents, sign);
                Vector3 rootLocal = root.InverseTransformPoint(worldPoint);
                IncludePoint(rootLocal, ref hasPoint, ref minimum, ref maximum);
            }
        }

        private static void IncludePoint(Vector3 point, ref bool hasPoint, ref Vector3 minimum,
            ref Vector3 maximum)
        {
            if (!IsFinite(point.x) || !IsFinite(point.y) || !IsFinite(point.z))
            {
                return;
            }

            if (!hasPoint)
            {
                minimum = point;
                maximum = point;
                hasPoint = true;
                return;
            }

            if (point.x < minimum.x) minimum.x = point.x;
            if (point.y < minimum.y) minimum.y = point.y;
            if (point.z < minimum.z) minimum.z = point.z;
            if (point.x > maximum.x) maximum.x = point.x;
            if (point.y > maximum.y) maximum.y = point.y;
            if (point.z > maximum.z) maximum.z = point.z;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
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
            IDictionary<string, long> declaredGrowthMm3,
            IDictionary<string, float> declaredVolumeK, Dictionary<string, float> existingVolumeK,
            IDictionary<string, float> declaredVolumePenalty,
            IDictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> declaredProxies,
            Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> existingProxies,
            bool createMissingColliders, PickupFieldSyncReport report)
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
            int localBoundsFallbacks = 0;
            int collidersCreated = 0;
            int volumeKDefaulted = 0;
            int growthFromFile = 0;
            int growthFromBounds = 0;
            int growthBoundsMismatches = 0;
            int growthNotComparable = 0;
            int proxyDeclared = 0;
            int proxyPreserved = 0;
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

                if (!PickupFieldAuthoringRules.HasUsableBounds(candidate.BoundsSize.x,
                        candidate.BoundsSize.y, candidate.BoundsSize.z))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "no usable size from bounds " + FormatVector(candidate.BoundsSize) +
                        ": every side must be finite and >= 0, and the longest side must be at least " +
                        FormatMetres(PickupFieldAuthoringRules.MinBoundsExtentM) +
                        " m (a zero side is allowed)");
                    continue;
                }

                // A declared size replaces the AABB-derived size with a cube of that edge. The
                // AABB is still required above: a renderer whose AABB is degenerate is a data
                // error even when the pickup size is spelled out in the name.
                Vector3 pickSize = candidate.BoundsSize;
                // Same row, measured along the prop's own axes: the prop's own size, which the
                // absorb gate, the growth pricing below and the radial-proxy shell read. A
                // row whose local measurement failed keeps the world AABB (the pre-fix
                // behaviour for that one row) instead of dropping the prop; it is reported
                // below so a rotated prop among them is not silently wrong.
                Vector3 pickLocalSize = candidate.LocalBoundsSize;
                // True while pickLocalSize really is the prop's own-axes size (measured, or a
                // declared cube, which has no orientation). False only for the world-AABB
                // fallback below, which the growth verification must not compare.
                bool pickSizeIsPropLocal = true;

                string declaredSizeNote = null;
                if (candidate.DeclaredSizeM > 0f)
                {
                    pickSize = new Vector3(candidate.DeclaredSizeM, candidate.DeclaredSizeM,
                        candidate.DeclaredSizeM);
                    // The declared edge is the gameplay volume, not the model AABB, so it
                    // replaces both columns with the same cube. A cube is rotation-invariant,
                    // which also keeps a declared gate prop out of the long-object admission.
                    pickLocalSize = pickSize;
                    declaredSizeNote = "'" + candidate.Path + "' declares " + candidate.SourceName +
                                       ": propBounds and propLocalBounds are a " +
                                       FormatMetres(candidate.DeclaredSizeM) +
                                       " m cube instead of the renderer AABBs (world " +
                                       FormatVector(candidate.BoundsSize) + ", local " +
                                       FormatVector(candidate.LocalBoundsSize) + ")";
                }
                else if (!PickupFieldAuthoringRules.HasUsableBounds(pickLocalSize.x,
                             pickLocalSize.y, pickLocalSize.z))
                {
                    pickLocalSize = pickSize;
                    pickSizeIsPropLocal = false;
                    localBoundsFallbacks++;
                }

                // The per-item volume factor k: the declaration wins, else the row already
                // serialized on the host, else the original's 0.70. A declaration outside
                // 0 < k <= 32 refuses the row (it would silently change the gate).
                float declaredK = 0f;
                bool hasDeclaredK = declaredVolumeK != null &&
                                    declaredVolumeK.TryGetValue(candidate.ItemId, out declaredK);
                float existingK = 0f;
                bool hasExistingK = existingVolumeK != null &&
                                    existingVolumeK.TryGetValue(candidate.ItemId, out existingK);
                float volumeK;
                string volumeKSource;
                if (!PickupFieldAuthoringRules.TryResolveVolumeK(declaredK, hasDeclaredK,
                        existingK, hasExistingK, out volumeK, out volumeKSource))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "declared volumeK " + declaredK + " is outside the legal range " +
                        "(0 < k <= 32; 0.70 is the original's default)");
                    continue;
                }
                if (string.Equals(volumeKSource, PickupFieldAuthoringRules.ValueSourceNone,
                        StringComparison.Ordinal))
                {
                    volumeKDefaulted++;
                }

                // GROWTH: one absorb credits round(penalty * k * a*b*c * 1e9) mm^3
                // (Docs/OPEN_DECISIONS.md (r) C+D). k comes from the declaration/host/default
                // above; the penalty from the same three-step rule, defaulting to 0.80. A row
                // that cannot be priced is refused here, never written as a silent zero.
                float volumePenalty;
                string volumePenaltySource;
                if (!PickupFieldAuthoringRules.TryResolveVolumePenalty(candidate.ItemId,
                        declaredVolumePenalty, out volumePenalty, out volumePenaltySource))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "declared volumePenalty is outside the legal range (0 < p <= 1; 0.80 is " +
                        "the original's default)");
                    continue;
                }
                // AUTHORITY: the placement file's authored growthMm3 (the canonical writer's
                // double-precision number). The bounds-priced value is computed only to verify
                // it; see GrowthAgreementFraction for why they cannot match exactly.
                long declaredGrowth = 0L;
                bool hasDeclaredGrowth = declaredGrowthMm3 != null &&
                    declaredGrowthMm3.TryGetValue(candidate.ItemId, out declaredGrowth) &&
                    declaredGrowth > 0L;
                long boundsGrowth;
                bool boundsPriced = PickupAbsorbMath.TryComputeGrowthMm3(pickLocalSize, volumeK,
                    volumePenalty, out boundsGrowth);
                long growthMm3;
                if (hasDeclaredGrowth)
                {
                    growthMm3 = declaredGrowth;
                    growthFromFile++;
                    long checkedBoundsGrowth;
                    long difference;
                    PickupFieldAuthoringRules.GrowthCheck check =
                        PickupFieldAuthoringRules.CheckDeclaredGrowth(declaredGrowth,
                            pickLocalSize.x, pickLocalSize.y, pickLocalSize.z, pickSizeIsPropLocal,
                            volumeK, volumePenalty, out checkedBoundsGrowth, out difference);
                    if (check == PickupFieldAuthoringRules.GrowthCheck.Disagrees)
                    {
                        growthBoundsMismatches++;
                        if (growthBoundsMismatches <= GrowthMismatchNoteLimit)
                        {
                            report.GrowthMismatches.Add("prop-local bounds " +
                                FormatVector(pickLocalSize) + " disagree with the file for '" +
                                candidate.ItemId + "': file=" + declaredGrowth + " mm^3, bounds "
                                + checkedBoundsGrowth + " mm^3 (diff " + difference + "); the FILE wins,"
                                + " re-check volumeK/penalty/aabb if this is unexpected");
                        }
                    }
                    else if (check == PickupFieldAuthoringRules.GrowthCheck.NotComparable)
                    {
                        growthNotComparable++;
                    }
                }
                else if (boundsPriced)
                {
                    growthMm3 = boundsGrowth;
                    growthFromBounds++;
                }
                else
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "growth cannot be priced: no authored growthMm3 in the placement file " +
                        "and the bounds " + FormatVector(pickSize) +
                        ", k=" + volumeK.ToString("0.###", CultureInfo.InvariantCulture) +
                        ", penalty=" +
                        volumePenalty.ToString("0.###", CultureInfo.InvariantCulture) +
                        " cannot be priced; re-run the writer so the column exists");
                    continue;
                }

                // Collider proxy: declaration wins, else the row already on the host, else
                // null (kind -1 = keep the OBB shape). A declared block that cannot be
                // validated refuses the row instead of silently falling back to a box.
                PickupFieldAuthoringRules.PickupProxyDefinition proxy = null;
                if (declaredProxies != null)
                {
                    // The level declaration is authoritative: an id with no block means
                    // "box default", not "keep whatever the host had" (removing a block must
                    // remove the proxy). A null map is the menu/un-migrated path and preserves
                    // the existing row.
                    PickupFieldAuthoringRules.PickupProxyDefinition declaredProxy;
                    if (declaredProxies.TryGetValue(candidate.ItemId, out declaredProxy))
                    {
                        proxy = declaredProxy;
                        proxyDeclared++;
                    }
                }
                else
                {
                    PickupFieldAuthoringRules.PickupProxyDefinition existingProxy;
                    if (existingProxies != null &&
                        existingProxies.TryGetValue(candidate.ItemId, out existingProxy))
                    {
                        proxy = existingProxy;
                        proxyPreserved++;
                    }
                }
                if (proxy != null)
                {
                    string proxyProblem = PickupFieldAuthoringRules.ProxyError(proxy);
                    if (proxyProblem != null)
                    {
                        report.Reject(candidate.Path, candidate.ItemId,
                            "declared collider " + proxyProblem);
                        continue;
                    }
                }

                var row = new PropRow();
                row.Root = candidate.Root;
                row.Kind = candidate.Kind;
                row.ItemId = candidate.ItemId;
                row.BoundsSize = pickSize;
                row.LocalBoundsSize = pickLocalSize;
                row.GrowthMm3 = growthMm3;
                row.VolumeK = volumeK;
                row.Proxy = proxy;
                row.PrimaryCollider = candidate.PrimaryCollider;
                if (createMissingColliders && row.PrimaryCollider == null)
                {
                    // The visual local size is the physical shape. A row whose local
                    // measurement failed falls back to the gameplay size, which is still
                    // better than a null slot the host silently leaves collidable.
                    Vector3 colliderFallback = pickLocalSize;
                    if (!PickupFieldAuthoringRules.HasUsableBounds(colliderFallback.x,
                            colliderFallback.y, colliderFallback.z))
                    {
                        colliderFallback = pickSize;
                    }

                    row.PrimaryCollider = EnsurePropCollider(candidate.Root, colliderFallback);
                    if (row.PrimaryCollider != null)
                    {
                        collidersCreated++;
                    }
                    else
                    {
                        report.Notes.Add("'" + candidate.Path + "' has no Collider and none could be " +
                                    "created; its propColliders slot stays empty and the prop will stay " +
                                    "collidable after it is absorbed");
                    }
                }

                rows.Add(row);

                report.GrowthMm3Priced += growthMm3;
                report.CountRow(row.Kind, row.GrowthMm3);
                report.TotalGrowthMm3 += row.GrowthMm3;

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

            if (collidersCreated > 0)
            {
                report.CollidersCreated = collidersCreated;
                report.Notes.Add(collidersCreated + " prop(s) had no Collider; a BoxCollider was added on the " +
                                 "prop root from its visual local AABB and wired into propColliders " +
                                 "(idempotent: a later sync reuses it)");
            }

            if (localBoundsFallbacks > 0)
            {
                report.Notes.Add(localBoundsFallbacks + " prop(s) have no usable prop-local bounds; " +
                                 "propLocalBounds copied propBounds for them, so a rotated prop among them " +
                                 "keeps the pre-fix double-rotated radial shell");
            }

            report.GrowthFileRows = growthFromFile;
            report.GrowthBoundsRows = growthFromBounds;
            if (growthNotComparable > 0)
            {
                report.Notes.Add(growthNotComparable + " row(s) with an authored growthMm3 could not " +
                                 "be verified against the scene (no prop-local measurement or the " +
                                 "measured size cannot be priced); they were not counted as " +
                                 "mismatches and the file still wins for them");
            }

            if (growthBoundsMismatches > GrowthMismatchNoteLimit)
            {
                report.GrowthMismatches.Add("... " +
                    (growthBoundsMismatches - GrowthMismatchNoteLimit) +
                    " more bounds/file growth disagreement(s); the file still wins for every row");
            }

            if (volumeKDefaulted > 0)
            {
                report.Notes.Add(volumeKDefaulted + " row(s) got the default volume factor k=0.70 " +
                                 "(PickupAbsorbMath.DefaultVolumeK): neither the level nor the host " +
                                 "declared one for them");
            }

            if (proxyDeclared > 0 || proxyPreserved > 0)
            {
                report.Notes.Add("collider proxies: " + proxyDeclared + " row(s) from the level's " +
                                 "defs[].collider, " + proxyPreserved + " preserved from the host. " +
                                 "Rows without a proxy keep their OBB shape.");
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

        private static bool WriteRows(PickupFieldHost host, List<PropRow> rows, int[] parentColumn,
            PickupFieldSyncReport report)
        {
            var serialized = new SerializedObject(host);
            var properties = new SerializedProperty[20];
            string[] fieldNames =
            {
                PropTransformsField, ItemIdsField, KindsField, PropBoundsField, PropLocalBoundsField,
                GrowthMm3Field, VolumeKField, PropCollidersField,
                PropProxyKindsField, PropProxyCentersField, PropProxySizesField,
                PropProxyRadiiField, PropProxyHalfHeightsField, PropProxyAxesField,
                PropProxyHullStartsField, PropProxyHullCountsField, PropProxyHullPointsField,
                PropProxyFaceStartsField, PropProxyFaceCountsField, PropProxyHullFacesField,
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
            SerializedProperty localBounds = properties[4];
            SerializedProperty values = properties[5];
            SerializedProperty volumeKs = properties[6];
            SerializedProperty colliders = properties[7];
            SerializedProperty proxyKinds = properties[8];
            SerializedProperty proxyCenters = properties[9];
            SerializedProperty proxySizes = properties[10];
            SerializedProperty proxyRadii = properties[11];
            SerializedProperty proxyHalfHeights = properties[12];
            SerializedProperty proxyAxes = properties[13];
            SerializedProperty proxyHullStarts = properties[14];
            SerializedProperty proxyHullCounts = properties[15];
            SerializedProperty proxyHullPoints = properties[16];
            SerializedProperty proxyFaceStarts = properties[17];
            SerializedProperty proxyFaceCounts = properties[18];
            SerializedProperty proxyHullFaces = properties[19];

            // Registered only after the contract fields were found, so a failed lookup leaves
            // no empty undo step behind.
            string parentFailure;
            SerializedProperty parentIndices = RequireArray(serialized, ParentIndexField,
                out parentFailure);
            if (parentIndices == null)
            {
                report.Fail(parentFailure);
                return false;
            }

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

            int totalHullPoints = 0;
            int totalFaces = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                PickupFieldAuthoringRules.PickupProxyDefinition proxy = rows[i].Proxy;
                if (proxy == null || proxy.Kind != PickupFieldAuthoringRules.ProxyKindConvex)
                    continue;
                totalHullPoints += proxy.HullCount;
                totalFaces += proxy.FaceCount;
            }

            transforms.arraySize = rows.Count;
            ids.arraySize = rows.Count;
            kinds.arraySize = rows.Count;
            bounds.arraySize = rows.Count;
            localBounds.arraySize = rows.Count;
            values.arraySize = rows.Count;
            volumeKs.arraySize = rows.Count;
            parentIndices.arraySize = rows.Count;
            colliders.arraySize = anyCollider ? rows.Count : 0;
            proxyKinds.arraySize = rows.Count;
            proxyCenters.arraySize = rows.Count;
            proxySizes.arraySize = rows.Count;
            proxyRadii.arraySize = rows.Count;
            proxyHalfHeights.arraySize = rows.Count;
            proxyAxes.arraySize = rows.Count;
            proxyHullStarts.arraySize = rows.Count;
            proxyHullCounts.arraySize = rows.Count;
            proxyHullPoints.arraySize = totalHullPoints;
            proxyFaceStarts.arraySize = rows.Count;
            proxyFaceCounts.arraySize = rows.Count;
            proxyHullFaces.arraySize = totalFaces * 3;

            int hullCursor = 0;
            int faceCursor = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                PropRow row = rows[i];
                transforms.GetArrayElementAtIndex(i).objectReferenceValue = row.Root;
                ids.GetArrayElementAtIndex(i).stringValue = row.ItemId;
                kinds.GetArrayElementAtIndex(i).stringValue = row.Kind;
                bounds.GetArrayElementAtIndex(i).vector3Value = row.BoundsSize;
                localBounds.GetArrayElementAtIndex(i).vector3Value = row.LocalBoundsSize;
                values.GetArrayElementAtIndex(i).longValue = row.GrowthMm3;
                volumeKs.GetArrayElementAtIndex(i).floatValue = row.VolumeK;
                parentIndices.GetArrayElementAtIndex(i).intValue = parentColumn[i];
                if (anyCollider)
                {
                    colliders.GetArrayElementAtIndex(i).objectReferenceValue = row.PrimaryCollider;
                }

                PickupFieldAuthoringRules.PickupProxyDefinition proxy = row.Proxy;
                // A row with no declared block is still a full row: kind 0 (box) with the
                // prop's local edge lengths, which is exactly the old OBB shape. Keeping the
                // columns row-aligned is the writer-allocates contract; an empty/short array
                // would make the host fall back per row and look like "0 rows" to the importer.
                Vector3 defaultSize = row.LocalBoundsSize;
                if (!PickupFieldAuthoringRules.HasUsableBounds(defaultSize.x, defaultSize.y,
                        defaultSize.z))
                {
                    defaultSize = row.BoundsSize;
                }
                bool hasDefaultBox = PickupFieldAuthoringRules.HasUsableBounds(defaultSize.x,
                    defaultSize.y, defaultSize.z);
                int kind = proxy != null ? proxy.Kind
                    : (hasDefaultBox ? PickupFieldAuthoringRules.ProxyKindBox : -1);
                proxyKinds.GetArrayElementAtIndex(i).intValue = kind;
                proxyCenters.GetArrayElementAtIndex(i).vector3Value =
                    proxy == null ? Vector3.zero : proxy.Center;
                proxySizes.GetArrayElementAtIndex(i).vector3Value =
                    proxy == null ? defaultSize : proxy.Size;
                proxyRadii.GetArrayElementAtIndex(i).floatValue =
                    proxy == null ? 0f : proxy.Radius;
                proxyHalfHeights.GetArrayElementAtIndex(i).floatValue =
                    proxy == null ? 0f : proxy.HalfHeight;
                proxyAxes.GetArrayElementAtIndex(i).intValue =
                    proxy == null ? 0 : proxy.Axis;

                int hullStart = 0;
                int hullCount = 0;
                int faceStart = 0;
                int faceCount = 0;
                if (proxy != null && proxy.Kind == PickupFieldAuthoringRules.ProxyKindConvex)
                {
                    hullStart = hullCursor;
                    hullCount = proxy.HullCount;
                    faceStart = faceCursor * 3;
                    faceCount = proxy.FaceCount;
                    for (int p = 0; p < hullCount; p++)
                    {
                        proxyHullPoints.GetArrayElementAtIndex(hullCursor + p).vector3Value =
                            proxy.Hull[p];
                    }
                    for (int f = 0; f < faceCount * 3; f++)
                    {
                        proxyHullFaces.GetArrayElementAtIndex(faceStart + f).intValue =
                            proxy.Faces[f];
                    }
                    hullCursor += hullCount;
                    faceCursor += faceCount;
                }

                proxyHullStarts.GetArrayElementAtIndex(i).intValue = hullStart;
                proxyHullCounts.GetArrayElementAtIndex(i).intValue = hullCount;
                proxyFaceStarts.GetArrayElementAtIndex(i).intValue = faceStart;
                proxyFaceCounts.GetArrayElementAtIndex(i).intValue = faceCount;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        /// <summary>Reads <c>propParentIndex</c> back and compares it with what was written.</summary>
        private static bool VerifyParentColumn(PickupFieldHost host, int[] expected)
        {
            SerializedProperty property = new SerializedObject(host).FindProperty(ParentIndexField);
            if (property == null || !property.isArray || property.arraySize != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (property.GetArrayElementAtIndex(i).intValue != expected[i]) return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the host's current parent column into child id to parent id. A host without
        /// the column (or with a short one) contributes nothing, so a scene synced before the
        /// column existed simply has no parents.
        /// </summary>
        private static Dictionary<string, string> ReadExistingParents(PickupFieldHost host)
        {
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            var serialized = new SerializedObject(host);
            SerializedProperty ids = serialized.FindProperty(ItemIdsField);
            SerializedProperty parents = serialized.FindProperty(ParentIndexField);
            if (ids == null || parents == null || !ids.isArray || !parents.isArray)
            {
                return existing;
            }

            int rows = Math.Min(ids.arraySize, parents.arraySize);
            for (int i = 0; i < rows; i++)
            {
                int parentRow = parents.GetArrayElementAtIndex(i).intValue;
                string childId = ids.GetArrayElementAtIndex(i).stringValue;
                if (parentRow < 0 || parentRow >= ids.arraySize || string.IsNullOrEmpty(childId)) continue;
                string parentId = ids.GetArrayElementAtIndex(parentRow).stringValue;
                if (!string.IsNullOrEmpty(parentId) && !existing.ContainsKey(childId))
                {
                    existing.Add(childId, parentId);
                }
            }

            return existing;
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

        private static bool VerifyReadBack(PickupFieldHost host, int expectedRows,
            bool requireEveryCollider)
        {
            var serialized = new SerializedObject(host);
            string[] fields =
            {
                PropTransformsField, ItemIdsField, KindsField, PropBoundsField,
                PropLocalBoundsField, GrowthMm3Field, VolumeKField,
                PropProxyKindsField, PropProxyCentersField, PropProxySizesField,
                PropProxyRadiiField, PropProxyHalfHeightsField, PropProxyAxesField,
                PropProxyHullStartsField, PropProxyHullCountsField,
                PropProxyFaceStartsField, PropProxyFaceCountsField,
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
            SerializedProperty hullPoints = serialized.FindProperty(PropProxyHullPointsField);
            SerializedProperty hullFaces = serialized.FindProperty(PropProxyHullFacesField);
            if (hullPoints == null || !hullPoints.isArray ||
                hullFaces == null || !hullFaces.isArray)
            {
                return false;
            }

            // propColliders is allowed to be empty (no prop has a collider) but never longer
            // than the other rows. A run that asked for every prop to be wired additionally
            // proves that no slot came back null, so the host's length-only warning can
            // never be the sole evidence for a half-wired field.
            if (colliders.arraySize == 0)
            {
                return !requireEveryCollider || expectedRows == 0;
            }

            if (colliders.arraySize != expectedRows)
            {
                return false;
            }

            if (!requireEveryCollider)
            {
                return true;
            }

            for (int i = 0; i < colliders.arraySize; i++)
            {
                if (colliders.GetArrayElementAtIndex(i).objectReferenceValue == null)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads the host's current <c>itemIds</c>/<c>propVolumeK</c> into an id-keyed map.
        /// </summary>
        /// <remarks>
        /// Same contract as the old value-units map was: keyed by item id, because a
        /// sync may reorder rows, and a short/absent column simply contributes nothing (a
        /// scene synced before the k column existed therefore keeps the 0.70 default).
        /// </remarks>
        private static Dictionary<string, float> ReadExistingVolumeK(PickupFieldHost host)
        {
            var existing = new Dictionary<string, float>(StringComparer.Ordinal);
            string[] ids;
            float[] values;
            ReadHostVolumeKColumns(host, out ids, out values);
            if (ids == null || values == null)
            {
                return existing;
            }

            int rows = ids.Length < values.Length ? ids.Length : values.Length;
            for (int i = 0; i < rows; i++)
            {
                string itemId = ids[i];
                if (!string.IsNullOrEmpty(itemId) && !existing.ContainsKey(itemId))
                {
                    existing.Add(itemId, values[i]);
                }
            }

            return existing;
        }

        private static void ReadHostVolumeKColumns(PickupFieldHost host, out string[] ids,
            out float[] values)
        {
            var serialized = new SerializedObject(host);
            SerializedProperty idsProperty = serialized.FindProperty(ItemIdsField);
            SerializedProperty kProperty = serialized.FindProperty(VolumeKField);
            if (idsProperty == null || kProperty == null || !idsProperty.isArray || !kProperty.isArray)
            {
                ids = null;
                values = null;
                return;
            }

            int count = idsProperty.arraySize;
            ids = new string[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = idsProperty.GetArrayElementAtIndex(i).stringValue;
            }

            int kCount = kProperty.arraySize;
            values = new float[kCount];
            for (int i = 0; i < kCount; i++)
            {
                values[i] = kProperty.GetArrayElementAtIndex(i).floatValue;
            }
        }

        /// <summary>
        /// Reads the host's current proxy columns into id-keyed definitions, the same
        /// preserve-by-id rule as <c>valueUnits</c>/<c>propVolumeK</c>. A host with no
        /// columns yields an empty map.
        /// </summary>
        private static Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition>
            ReadExistingProxies(PickupFieldHost host)
        {
            var existing = new Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition>(
                StringComparer.Ordinal);
            var serialized = new SerializedObject(host);
            SerializedProperty ids = serialized.FindProperty(ItemIdsField);
            SerializedProperty kinds = serialized.FindProperty(PropProxyKindsField);
            SerializedProperty centers = serialized.FindProperty(PropProxyCentersField);
            SerializedProperty sizes = serialized.FindProperty(PropProxySizesField);
            SerializedProperty radii = serialized.FindProperty(PropProxyRadiiField);
            SerializedProperty halfHeights = serialized.FindProperty(PropProxyHalfHeightsField);
            SerializedProperty axes = serialized.FindProperty(PropProxyAxesField);
            SerializedProperty hullStarts = serialized.FindProperty(PropProxyHullStartsField);
            SerializedProperty hullCounts = serialized.FindProperty(PropProxyHullCountsField);
            SerializedProperty hullPoints = serialized.FindProperty(PropProxyHullPointsField);
            SerializedProperty faceStarts = serialized.FindProperty(PropProxyFaceStartsField);
            SerializedProperty faceCounts = serialized.FindProperty(PropProxyFaceCountsField);
            SerializedProperty faces = serialized.FindProperty(PropProxyHullFacesField);
            if (ids == null || kinds == null || centers == null || sizes == null ||
                radii == null || halfHeights == null || axes == null || hullStarts == null ||
                hullCounts == null || hullPoints == null || faceStarts == null ||
                faceCounts == null || faces == null)
            {
                return existing;
            }

            int count = ids.arraySize;
            if (kinds.arraySize < count) count = kinds.arraySize;
            if (centers.arraySize < count) count = centers.arraySize;
            if (sizes.arraySize < count) count = sizes.arraySize;
            if (radii.arraySize < count) count = radii.arraySize;
            if (halfHeights.arraySize < count) count = halfHeights.arraySize;
            if (axes.arraySize < count) count = axes.arraySize;
            if (hullStarts.arraySize < count) count = hullStarts.arraySize;
            if (hullCounts.arraySize < count) count = hullCounts.arraySize;
            if (faceStarts.arraySize < count) count = faceStarts.arraySize;
            if (faceCounts.arraySize < count) count = faceCounts.arraySize;

            for (int i = 0; i < count; i++)
            {
                string id = ids.GetArrayElementAtIndex(i).stringValue;
                int kind = kinds.GetArrayElementAtIndex(i).intValue;
                if (string.IsNullOrEmpty(id) || kind < 0 ||
                    kind > PickupFieldAuthoringRules.ProxyKindConvex) continue;

                var proxy = new PickupFieldAuthoringRules.PickupProxyDefinition();
                proxy.TypeName = kind == PickupFieldAuthoringRules.ProxyKindBox ? "box"
                    : kind == PickupFieldAuthoringRules.ProxyKindSphere ? "sphere"
                    : kind == PickupFieldAuthoringRules.ProxyKindCapsule ? "capsule" : "convex";
                proxy.Kind = kind;
                proxy.Center = centers.GetArrayElementAtIndex(i).vector3Value;
                proxy.Size = sizes.GetArrayElementAtIndex(i).vector3Value;
                proxy.Radius = radii.GetArrayElementAtIndex(i).floatValue;
                proxy.HalfHeight = halfHeights.GetArrayElementAtIndex(i).floatValue;
                proxy.Axis = axes.GetArrayElementAtIndex(i).intValue;

                if (kind == PickupFieldAuthoringRules.ProxyKindConvex)
                {
                    int hullStart = hullStarts.GetArrayElementAtIndex(i).intValue;
                    int hullCount = hullCounts.GetArrayElementAtIndex(i).intValue;
                    int faceStart = faceStarts.GetArrayElementAtIndex(i).intValue;
                    int faceCount = faceCounts.GetArrayElementAtIndex(i).intValue;
                    if (hullStart < 0 || hullCount < 0 ||
                        hullCount > PickupFieldAuthoringRules.MaxProxyHullPoints ||
                        hullStart + hullCount > hullPoints.arraySize ||
                        faceStart < 0 || faceCount < 0 ||
                        faceCount > PickupFieldAuthoringRules.MaxProxyHullFaces ||
                        faceStart + faceCount * 3 > faces.arraySize) continue;
                    proxy.Hull = new Vector3[hullCount];
                    for (int p = 0; p < hullCount; p++)
                    {
                        proxy.Hull[p] = hullPoints.GetArrayElementAtIndex(hullStart + p).vector3Value;
                    }
                    proxy.HullCount = hullCount;
                    proxy.Faces = new int[faceCount * 3];
                    for (int f = 0; f < faceCount * 3; f++)
                    {
                        proxy.Faces[f] = faces.GetArrayElementAtIndex(faceStart + f).intValue;
                    }
                    proxy.FaceCount = faceCount;
                }

                existing[id] = proxy;
            }
            return existing;
        }

        /// <summary>
        /// Compares every declared collider block with the host's read-back row. Bit equality
        /// is the contract: the sync just wrote these numbers, so a difference means the
        /// SerializedObject write did not land or was overwritten.
        /// </summary>
        private static bool VerifyDeclaredProxies(PickupFieldHost host,
            IDictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> declared)
        {
            if (declared == null || declared.Count == 0) return true;
            Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> actual =
                ReadExistingProxies(host);
            foreach (KeyValuePair<string, PickupFieldAuthoringRules.PickupProxyDefinition> pair in declared)
            {
                PickupFieldAuthoringRules.PickupProxyDefinition got;
                if (!actual.TryGetValue(pair.Key, out got)) return false;
                if (!ProxyEquals(pair.Value, got)) return false;
            }
            return true;
        }

        private static bool ProxyEquals(PickupFieldAuthoringRules.PickupProxyDefinition left,
            PickupFieldAuthoringRules.PickupProxyDefinition right)
        {
            if (left == null || right == null) return left == right;
            if (left.Kind != right.Kind) return false;
            if (left.Center != right.Center || left.Size != right.Size) return false;
            if (left.Radius != right.Radius || left.HalfHeight != right.HalfHeight) return false;
            if (left.Axis != right.Axis) return false;
            if (left.Kind != PickupFieldAuthoringRules.ProxyKindConvex) return true;
            if (left.HullCount != right.HullCount || left.FaceCount != right.FaceCount)
                return false;
            if (left.HullCount > 0)
            {
                for (int i = 0; i < left.HullCount; i++)
                {
                    if (left.Hull[i] != right.Hull[i]) return false;
                }
            }
            if (left.FaceCount > 0)
            {
                for (int i = 0; i < left.FaceCount * 3; i++)
                {
                    if (left.Faces[i] != right.Faces[i]) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Re-reads the written growth column and compares it with the rows this run wrote.
        /// </summary>
        /// <remarks>
        /// The growth is derived at sync time, so the declaration cannot be compared against
        /// it directly (there is no authored growth to compare with). What must hold is that
        /// the number this run priced is the number serialized on the host: a truncated
        /// <c>longValue</c> write, a wrong column or a resized array is a ledger bug that
        /// nothing later would notice, so the run fails and names the first rows that differ.
        /// </remarks>
        private static bool VerifyGrowthMm3(PickupFieldHost host, List<PropRow> rows,
            PickupFieldSyncReport report)
        {
            var serialized = new SerializedObject(host);
            SerializedProperty ids = serialized.FindProperty(ItemIdsField);
            SerializedProperty values = serialized.FindProperty(GrowthMm3Field);
            if (ids == null || values == null || !ids.isArray || !values.isArray ||
                ids.arraySize != values.arraySize || ids.arraySize != rows.Count)
            {
                report.GrowthMismatches.Add("the host's " + ItemIdsField + "/" + GrowthMm3Field +
                                            " arrays could not be read back at the written length (" +
                                            rows.Count + ")");
                return false;
            }

            int reported = 0;
            bool ok = true;
            for (int i = 0; i < rows.Count; i++)
            {
                string hostId = ids.GetArrayElementAtIndex(i).stringValue;
                long hostGrowth = values.GetArrayElementAtIndex(i).longValue;
                if (string.Equals(hostId, rows[i].ItemId, StringComparison.Ordinal) &&
                    hostGrowth == rows[i].GrowthMm3)
                {
                    continue;
                }
                ok = false;
                if (reported < 20)
                {
                    report.GrowthMismatches.Add("read-back mismatch at row " + i + ": written " +
                        rows[i].ItemId + "=" + rows[i].GrowthMm3 + " mm^3, host says " +
                        hostId + "=" + hostGrowth + " mm^3");
                    reported++;
                }
            }

            if (!ok && reported == 20)
            {
                report.GrowthMismatches.Add("... more growthMm3 read-back mismatch(es)");
            }
            return ok;
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
