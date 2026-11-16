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
    /// The one authoring path that fills <see cref="PickupFieldHost"/>'s seven index-aligned
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
        private const string ValueUnitsField = "valueUnits";
        private const string PropCollidersField = "propColliders";

        // A BoxCollider with a zero side never collides. A visual can legitimately have a
        // zero-thickness side (paper), so the created box is given a 1 mm floor instead of
        // being silently left out of the wiring.
        private const float MinCreatedColliderExtentM = 0.001f;

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
        /// Reads the props under <paramref name="fieldRoot"/> and writes the host's seven
        /// index-aligned arrays.
        /// </summary>
        /// <param name="host">The field host to fill; its backing UdonBehaviour must already exist.</param>
        /// <param name="fieldRoot">
        /// The root whose immediate children are kind folders. The menu passes the host's own
        /// transform; a level that keeps its props elsewhere passes that transform instead.
        /// </param>
        /// <param name="declaredValueUnits">
        /// Item id to value units, supplied by the level importer from
        /// <c>placement.json</c>; 64-bit, like the host column it is written into. When an id
        /// is absent, the value already serialized on the host is preserved; a row with
        /// neither is written as 0 and reported. Pass <c>null</c> when no declaration exists
        /// - that is the un-migrated scene path, and it must not change any existing value.
        /// </param>
        /// <returns>What was written, and every row that was refused and why.</returns>
        public static PickupFieldSyncReport SyncFromChildren(PickupFieldHost host, Transform fieldRoot,
            IDictionary<string, long> declaredValueUnits)
        {
            return SyncFromChildren(host, fieldRoot, declaredValueUnits, false);
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
            IDictionary<string, long> declaredValueUnits, bool createMissingColliders)
        {
            var report = new PickupFieldSyncReport();
            report.ValueUnitsRequested = declaredValueUnits != null && declaredValueUnits.Count > 0;

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
            Dictionary<string, long> existingValueUnits = ReadExistingValueUnits(host);

            List<PropCandidate> candidates = CollectCandidates(fieldRoot);
            List<PropRow> rows = ValidateCandidates(candidates, declaredValueUnits, existingValueUnits,
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

            if (!WriteRows(host, rows, report))
            {
                return report;
            }

            MarkDirty(host);
            CopyProxy(host, report);
            report.ReadBackVerified = VerifyReadBack(host, rows.Count, createMissingColliders);
            if (!report.ReadBackVerified)
            {
                if (string.IsNullOrEmpty(report.Failure))
                {
                    report.Fail("the seven arrays did not read back at the expected length (" + rows.Count +
                                ") or a propColliders slot was empty; the write did not persist");
                }
            }
            else
            {
                report.ValueUnitsVerified = VerifyDeclaredValueUnits(host, declaredValueUnits, report);
                if (!report.ValueUnitsVerified && string.IsNullOrEmpty(report.Failure))
                {
                    report.Fail(report.ValueUnitMismatches.Count + " declared valueUnits row(s) did not " +
                                "read back as declared; treat the host arrays as wrong until this is fixed");
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
            public long ValueUnits;
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
            IDictionary<string, long> declaredValueUnits, Dictionary<string, long> existingValueUnits,
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
                // Same row, measured along the prop's own axes, for the radial-proxy shell. A
                // row whose local measurement failed keeps the world AABB (the pre-fix
                // behaviour for that one row) instead of dropping the prop; it is reported
                // below so a rotated prop among them is not silently wrong.
                Vector3 pickLocalSize = candidate.LocalBoundsSize;

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
                    localBoundsFallbacks++;
                }

                // The value is content data, never a price. The declaration wins; without
                // one the host's existing row is preserved; with neither the row is still
                // written (0) and named in the notes, because dropping it would lose the
                // prop's geometry and pickup wiring over a missing number.
                long declaredValue = 0L;
                bool hasDeclaredValue = declaredValueUnits != null &&
                                        declaredValueUnits.TryGetValue(candidate.ItemId, out declaredValue);
                long existingValue = 0L;
                bool hasExistingValue = existingValueUnits != null &&
                                        existingValueUnits.TryGetValue(candidate.ItemId, out existingValue);

                long valueUnits;
                string valueSource;
                if (!PickupFieldAuthoringRules.TryResolveValueUnits(declaredValue, hasDeclaredValue,
                        existingValue, hasExistingValue, out valueUnits, out valueSource))
                {
                    report.Reject(candidate.Path, candidate.ItemId,
                        "declared valueUnits " + declaredValue + " is not a whole number >= 1 " +
                        "(Docs/OPEN_DECISIONS.md (j)); the row was left out rather than priced");
                    continue;
                }

                var row = new PropRow();
                row.Root = candidate.Root;
                row.Kind = candidate.Kind;
                row.ItemId = candidate.ItemId;
                row.BoundsSize = pickSize;
                row.LocalBoundsSize = pickLocalSize;
                row.ValueUnits = valueUnits;
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

                if (string.Equals(valueSource, PickupFieldAuthoringRules.ValueSourceDeclared,
                        StringComparison.Ordinal))
                {
                    report.ValueUnitsDeclared += valueUnits;
                }
                else if (string.Equals(valueSource, PickupFieldAuthoringRules.ValueSourceExisting,
                             StringComparison.Ordinal))
                {
                    report.ValueUnitsPreserved++;
                }
                else
                {
                    report.ValueUnitsUndeclared++;
                }

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

            if (report.ValueUnitsPreserved > 0)
            {
                report.Notes.Add(report.ValueUnitsPreserved + " row(s) kept the valueUnits already serialized " +
                                 "on the host (no declaration was supplied). This is the pre-U2 scene path and " +
                                 "is intentionally not repriced");
            }

            if (report.ValueUnitsUndeclared > 0)
            {
                report.Notes.Add(report.ValueUnitsUndeclared + " row(s) have no declared value and no " +
                                 "existing serialized value; valueUnits was written as 0 and the host will " +
                                 "ignore those rows at runtime until a value is declared " +
                                 "(Docs/OPEN_DECISIONS.md (q) U2)");
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
            var properties = new SerializedProperty[7];
            string[] fieldNames =
            {
                PropTransformsField, ItemIdsField, KindsField, PropBoundsField, PropLocalBoundsField,
                ValueUnitsField, PropCollidersField,
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
            SerializedProperty colliders = properties[6];

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
            localBounds.arraySize = rows.Count;
            values.arraySize = rows.Count;
            colliders.arraySize = anyCollider ? rows.Count : 0;

            for (int i = 0; i < rows.Count; i++)
            {
                PropRow row = rows[i];
                transforms.GetArrayElementAtIndex(i).objectReferenceValue = row.Root;
                ids.GetArrayElementAtIndex(i).stringValue = row.ItemId;
                kinds.GetArrayElementAtIndex(i).stringValue = row.Kind;
                bounds.GetArrayElementAtIndex(i).vector3Value = row.BoundsSize;
                localBounds.GetArrayElementAtIndex(i).vector3Value = row.LocalBoundsSize;
                values.GetArrayElementAtIndex(i).longValue = row.ValueUnits;
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

        private static bool VerifyReadBack(PickupFieldHost host, int expectedRows,
            bool requireEveryCollider)
        {
            var serialized = new SerializedObject(host);
            string[] fields =
            {
                PropTransformsField, ItemIdsField, KindsField, PropBoundsField,
                PropLocalBoundsField, ValueUnitsField,
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
        /// Reads the host's current <c>itemIds</c>/<c>valueUnits</c> into an id-keyed map
        /// before the sync resizes anything.
        /// </summary>
        /// <remarks>
        /// Keyed by item id rather than array index on purpose: the sync is allowed to
        /// reorder rows (props sort by hierarchy path), and moving a row must not move its
        /// value to a different prop. A host with no arrays yields an empty map; an unaligned
        /// pair contributes only its overlapping prefix, so the write never invents a value.
        /// </remarks>
        private static Dictionary<string, long> ReadExistingValueUnits(PickupFieldHost host)
        {
            var existing = new Dictionary<string, long>(StringComparer.Ordinal);
            var serialized = new SerializedObject(host);
            SerializedProperty ids = serialized.FindProperty(ItemIdsField);
            SerializedProperty values = serialized.FindProperty(ValueUnitsField);
            if (ids == null || values == null || !ids.isArray || !values.isArray)
            {
                return existing;
            }

            int rows = ids.arraySize < values.arraySize ? ids.arraySize : values.arraySize;
            for (int i = 0; i < rows; i++)
            {
                string itemId = ids.GetArrayElementAtIndex(i).stringValue;
                if (!string.IsNullOrEmpty(itemId) && !existing.ContainsKey(itemId))
                {
                    existing.Add(itemId, values.GetArrayElementAtIndex(i).longValue);
                }
            }

            return existing;
        }

        /// <summary>
        /// Re-reads the host after the write and reports every declared value that did not
        /// land, plus every declared id that has no row.
        /// </summary>
        /// <remarks>
        /// This is the U2 guard: the importer never touches the arrays itself, so this
        /// read-back is the only place a declaration can be proven to have reached the host.
        /// A mismatch is a ledger bug that nothing later in the pipeline would notice, so the
        /// run is failed with the details in <see cref="PickupFieldSyncReport.ValueUnitMismatches"/>.
        /// </remarks>
        private static bool VerifyDeclaredValueUnits(PickupFieldHost host,
            IDictionary<string, long> declaredValueUnits, PickupFieldSyncReport report)
        {
            if (declaredValueUnits == null || declaredValueUnits.Count == 0)
            {
                // Nothing was declared, so there is nothing to verify; preserved values are
                // the contract for this path.
                return true;
            }

            var serialized = new SerializedObject(host);
            SerializedProperty ids = serialized.FindProperty(ItemIdsField);
            SerializedProperty values = serialized.FindProperty(ValueUnitsField);
            if (ids == null || values == null || !ids.isArray || !values.isArray ||
                ids.arraySize != values.arraySize)
            {
                report.ValueUnitMismatches.Add("the host's " + ItemIdsField + "/" + ValueUnitsField +
                                               " arrays could not be read back index-aligned");
                return false;
            }

            var readIds = new string[ids.arraySize];
            var readValues = new long[values.arraySize];
            for (int i = 0; i < ids.arraySize; i++)
            {
                readIds[i] = ids.GetArrayElementAtIndex(i).stringValue;
                readValues[i] = values.GetArrayElementAtIndex(i).longValue;
            }

            string[] mismatches = PickupFieldAuthoringRules.FindValueUnitMismatches(readIds, readValues,
                declaredValueUnits);
            string[] missingRows = PickupFieldAuthoringRules.FindDeclaredIdsWithoutRows(readIds,
                declaredValueUnits);

            for (int i = 0; i < mismatches.Length; i++)
            {
                report.ValueUnitMismatches.Add("read-back mismatch: " + mismatches[i]);
            }

            for (int i = 0; i < missingRows.Length; i++)
            {
                report.ValueUnitMismatches.Add("declared itemId has no host row: " + missingRows[i]);
            }

            return mismatches.Length == 0 && missingRows.Length == 0;
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
