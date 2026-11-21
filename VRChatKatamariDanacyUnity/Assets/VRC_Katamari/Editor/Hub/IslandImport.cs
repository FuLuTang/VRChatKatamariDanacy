using System;
using System.Collections.Generic;
using System.IO;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;
using VRCKatamari.Editor.LevelBuild;
using VRCKatamari.Labs.PhysicsTestField;
using VRCKatamari.Pickup;
using VRCKatamari.Story;
using Object = UnityEngine.Object;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>What a source scene says about its own level, read before anything is moved.</summary>
    public sealed class IslandSourceFacts
    {
        // The source scene's ball motor.
        public bool HasMotor;
        public Vector3 MotorStartCenter;
        public float MotorStartDiameterM;
        public float MotorGroundOffset;

        // The source scene's round controller (paths are source-scene hierarchy paths).
        public bool HasRound;
        public string RoundLevelId;
        public int RoundGoalType;
        public float RoundTargetDiameterM;
        public int RoundTargetCount;
        public float RoundTimeLimitSeconds;
        public bool RoundKillEnabled;
        public float RoundKillY;
        public string RoundCountKind;
        public string RoundPickupHostPath;

        // The physics test field's driver: the order its sandbox registers arenas in.
        public bool HasDriver;
        public string DriverFrameArenaPath;
        public readonly List<string> DriverCellArenaPaths = new List<string>();
        public string[] DriverKinds;
        public Vector3 DriverSpawn;
        public float DriverSpawnHeadingDeg;
    }

    /// <summary>One serialized object reference from a taken object to something outside the taken set.</summary>
    public sealed class IslandRef
    {
        public Component Owner;
        public string OwnerPath;
        public string PropertyPath;
        public GameObject TargetObject;
        public Type TargetType;
        public int TargetOrdinal;
        public string TargetPath;
    }

    /// <summary>What a level's take list resolves to in its source scene (read-only analysis).</summary>
    public sealed class IslandAnalysis
    {
        public HubLevelCatalog.LevelEntry Level;
        public Scene Source;
        public bool SourceIsOutput;
        public readonly List<Transform> TakeRoots = new List<Transform>();
        public readonly List<string> TakePaths = new List<string>();
        public readonly List<Transform> LeaveBehind = new List<Transform>();
        public readonly List<string> LeavePaths = new List<string>();
        public readonly HashSet<GameObject> MovedObjects = new HashSet<GameObject>();
        public int ComponentCount;
        public readonly List<IslandRef> External = new List<IslandRef>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> ArenaHosts = new List<string>();
        public readonly List<string> PickupHosts = new List<string>();
        public readonly List<string> TerrainHosts = new List<string>();
        public readonly List<string> MotionHosts = new List<string>();
        public readonly List<string> InfrastructureHits = new List<string>();
        public IslandSourceFacts Facts;
    }

    /// <summary>What one executed island import did.</summary>
    public sealed class IslandResult
    {
        public Transform Shell;
        public IslandSourceFacts Facts;
        public string Mode;
        public int RootsMoved;
        public int ObjectsMoved;
        public int ComponentsScanned;
        public int Remapped;
        public int TerrainVerticesShifted;
        public int PathFloatsShifted;
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> RemapSummary = new List<string>();
        public readonly List<UdonSharpBehaviour> ModifiedProxies = new List<UdonSharpBehaviour>();

        public bool Ok
        {
            get { return Problems.Count == 0 && Shell != null; }
        }
    }

    /// <summary>
    /// Moves a level's content out of its source scene into an island shell
    /// (<c>Island_&lt;id&gt;</c>) of the open hub scene, at the level's offset, and fixes what
    /// the move breaks. The source scene on disk is never saved or changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it does for one level, in order: (1) open the source scene additively (or use the
    /// hub scene itself when the source is the base scene), refusing if it is open with unsaved
    /// changes; (2) read what the source scene says about its own level (ball start, round
    /// settings, the test field's driver order) and list every object reference that leaves
    /// the taken set; (3) create the shell and reproduce each taken object's source path under
    /// it, so a path means the same object in the source and in the hub; (4) move each taken
    /// root in, leaving the <c>leaveBehind</c> subtrees where they were; (5) move the shell to
    /// the offset; (6) rewrite the references that left the taken set by source hierarchy
    /// path onto the same-path object of the hub scene (a miss is a PROBLEM naming property
    /// path and target path, never a guess by type); (7) add the offset to the serialized
    /// world coordinates (<see cref="IslandWorldFields"/>); (8) set every terrain host to
    /// defer its registration; (9) <c>CopyProxyToUdon</c> every proxy that was written; (10)
    /// close the source without saving.
    /// </para>
    /// <para>
    /// Move versus instantiate. The default takes the objects with
    /// <c>SceneManager.MoveGameObjectToScene</c>: the same objects (and the same UdonSharp
    /// proxy + backing <c>UdonBehaviour</c> pairs) travel, references between taken objects
    /// stay valid, and only the references that point back into the source scene need
    /// rewriting. That is the intended behaviour, but it has not been run against UdonSharp
    /// backings here: if a moved behaviour comes out with empty variables, switch
    /// <see cref="UseInstantiateFallback"/> (menu "VRC Katamari/Hub/All-Maps: Use Instantiate
    /// Fallback"). That path clones each taken root with <c>Object.Instantiate</c> under the
    /// shell, removes the cloned leave-behind subtrees, maps original to clone by walking both
    /// hierarchies in step, and rewrites every reference that still points at an original
    /// (to its clone) or at the source scene (by path), then the same steps 5 to 10 run.
    /// Instantiate is not available when the source is the hub scene itself (there the
    /// objects are already in the right scene and are moved).
    /// </para>
    /// <para>
    /// Not touched: any reference from outside the taken set into it (the hub's own objects
    /// reference nothing in an island), the source scene's lighting (all four sources have
    /// none baked and the same sky), and Y scalars (a zero Y offset is required).
    /// </para>
    /// </remarks>
    public static class IslandImport
    {
        public const string FallbackPrefsKey = "VRCKatamari.Hub.IslandInstantiateFallback";

        /// <summary>Simple type names of the single-per-scene infrastructure; a taken subtree must not contain one.</summary>
        public static readonly string[] InfrastructureTypeNames =
        {
            "KatamariBallMotor", "DesktopMoveInput", "BallRideStation", "BallView",
            "StoryRoundController", "StoryResultPresenter", "HubLevelLauncher", "HubRoomHost",
            "LevelSwitchHost", "PhysicsTestFieldDriver", "LabBallSizeSetter",
            "VRCSceneDescriptor", "PipelineManager",
        };

        /// <summary>When true the roots are cloned instead of moved (see the class remarks).</summary>
        public static bool UseInstantiateFallback
        {
            get { return EditorPrefs.GetBool(FallbackPrefsKey, false); }
            set { EditorPrefs.SetBool(FallbackPrefsKey, value); }
        }

        // ------------------------------------------------------------ scene access ---

        /// <summary>
        /// Opens a source scene additively. Refuses when the scene is already open with
        /// unsaved changes. <paramref name="openedByUs"/> says whether the caller must close it.
        /// </summary>
        public static bool TryOpenSource(string assetPath, out Scene scene, out bool openedByUs,
            List<string> problems)
        {
            scene = default(Scene);
            openedByUs = false;
            string full = Path.Combine(LevelPaths.ProjectRoot(), assetPath);
            if (!File.Exists(full))
            {
                problems.Add("source scene '" + assetPath + "' does not exist on this machine"
                    + " (a '_RefUnlicensed' scene is git-ignored and only exists where it was made)");
                return false;
            }

            Scene existing = SceneManager.GetSceneByPath(assetPath);
            if (existing.IsValid() && existing.isLoaded)
            {
                if (existing.isDirty)
                {
                    problems.Add("source scene '" + assetPath + "' is open with unsaved changes;"
                        + " save or discard them first (this tool never saves or discards for you)");
                    return false;
                }
                // Already open and clean: use it, and let the caller close it again.
                scene = existing;
                openedByUs = true;
                return true;
            }

            try
            {
                scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            }
            catch (Exception exception)
            {
                problems.Add("cannot open source scene '" + assetPath + "': " + exception.Message);
                return false;
            }
            openedByUs = true;
            return scene.IsValid() && scene.isLoaded;
        }

        /// <summary>Closes a source scene opened by <see cref="TryOpenSource"/> without saving.</summary>
        public static void CloseSource(Scene source, Scene output)
        {
            if (source.IsValid() && source.isLoaded && source != output)
            {
                EditorSceneManager.CloseScene(source, true);
            }
            if (output.IsValid() && output.isLoaded) SceneManager.SetActiveScene(output);
        }

        /// <summary>The hierarchy path of a transform (root name first, '/' separated).</summary>
        public static string PathOf(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // ------------------------------------------------------------ analysis ---

        /// <summary>
        /// Resolves the level's take list in <paramref name="source"/> and gathers everything a
        /// move needs to know, without changing anything: the taken set, the references that
        /// leave it, hosts, infrastructure hits and the source's own facts.
        /// </summary>
        public static IslandAnalysis Analyze(HubLevelCatalog.LevelEntry level, Scene source,
            bool sourceIsOutput)
        {
            var a = new IslandAnalysis();
            a.Level = level;
            a.Source = source;
            a.SourceIsOutput = sourceIsOutput;
            string label = "level '" + level.id + "'";

            if (level.source == null)
            {
                a.Problems.Add(label + " has no source block");
                return a;
            }

            ResolveList(level.source.takeRoots, source, label, "takeRoots", a.TakeRoots,
                a.TakePaths, a.Problems);
            ResolveList(level.source.leaveBehind, source, label, "leaveBehind", a.LeaveBehind,
                a.LeavePaths, a.Problems);
            if (a.Problems.Count > 0) return a;

            // No take root may sit inside another one; a leave-behind must sit inside a take root.
            for (int i = 0; i < a.TakeRoots.Count; i++)
            {
                for (int j = 0; j < a.TakeRoots.Count; j++)
                {
                    if (i != j && a.TakeRoots[j].IsChildOf(a.TakeRoots[i]))
                    {
                        a.Problems.Add(label + ": takeRoots '" + a.TakePaths[j] + "' lies inside '"
                            + a.TakePaths[i] + "'; list one of them");
                    }
                }
            }
            for (int i = 0; i < a.LeaveBehind.Count; i++)
            {
                bool inside = false;
                for (int j = 0; j < a.TakeRoots.Count; j++)
                {
                    if (a.LeaveBehind[i] != a.TakeRoots[j] && a.LeaveBehind[i].IsChildOf(a.TakeRoots[j]))
                    {
                        inside = true;
                    }
                }
                if (!inside)
                {
                    a.Problems.Add(label + ": leaveBehind '" + a.LeavePaths[i]
                        + "' is not inside any takeRoot (or is one)");
                }
            }
            if (a.Problems.Count > 0) return a;

            a.Facts = ReadFacts(source);

            var leaveSet = new HashSet<Transform>();
            for (int i = 0; i < a.LeaveBehind.Count; i++)
            {
                Transform[] below = a.LeaveBehind[i].GetComponentsInChildren<Transform>(true);
                for (int k = 0; k < below.Length; k++) leaveSet.Add(below[k]);
            }

            var movedComponents = new List<Component>();
            for (int i = 0; i < a.TakeRoots.Count; i++)
            {
                Transform[] all = a.TakeRoots[i].GetComponentsInChildren<Transform>(true);
                for (int k = 0; k < all.Length; k++)
                {
                    if (leaveSet.Contains(all[k])) continue;
                    a.MovedObjects.Add(all[k].gameObject);
                    Component[] components = all[k].GetComponents<Component>();
                    for (int c = 0; c < components.Length; c++)
                    {
                        if (components[c] == null) continue;
                        movedComponents.Add(components[c]);
                        Classify(components[c], all[k], a);
                    }
                }
            }
            a.ComponentCount = movedComponents.Count;
            if (a.InfrastructureHits.Count > 0)
            {
                a.Problems.Add(label + ": the taken subtrees contain infrastructure that the hub"
                    + " already has one of (" + a.InfrastructureHits.Count + " hit(s), first: "
                    + a.InfrastructureHits[0] + "); take only the level's own content");
            }

            if (!sourceIsOutput)
            {
                CollectRefs(movedComponents, source, a.MovedObjects, null, a.External);
            }
            return a;
        }

        private static void ResolveList(string[] paths, Scene source, string label, string what,
            List<Transform> transforms, List<string> keptPaths, List<string> problems)
        {
            if (paths == null) return;
            for (int i = 0; i < paths.Length; i++)
            {
                Transform t = HubLevelCatalog.FindPath(source, paths[i]);
                if (t == null)
                {
                    problems.Add(label + ": " + what + " '" + paths[i] + "' was not found in scene '"
                        + source.name + "'");
                    continue;
                }
                if (transforms.Contains(t))
                {
                    problems.Add(label + ": " + what + " lists '" + paths[i] + "' twice");
                    continue;
                }
                transforms.Add(t);
                keptPaths.Add(paths[i]);
            }
        }

        private static void Classify(Component component, Transform owner, IslandAnalysis a)
        {
            string typeName = component.GetType().Name;
            for (int i = 0; i < InfrastructureTypeNames.Length; i++)
            {
                if (string.Equals(InfrastructureTypeNames[i], typeName, StringComparison.Ordinal))
                {
                    a.InfrastructureHits.Add(typeName + " at " + PathOf(owner));
                    return;
                }
            }
            if (IslandWorldFields.IsNotImportedType(typeName))
            {
                a.InfrastructureHits.Add(typeName + " at " + PathOf(owner)
                    + " (holds world positions the importer does not shift)");
                return;
            }

            if (component is LevelArenaHost) a.ArenaHosts.Add(PathOf(owner));
            else if (component is PickupFieldHost)
            {
                a.PickupHosts.Add(PathOf(owner) + " (" + ArrayLength(component, "itemIds") + " items)");
            }
            else if (component is TerrainFieldHost)
            {
                a.TerrainHosts.Add(PathOf(owner) + " (" + ArrayLength(component, "vertices")
                    + " vertices)");
            }
            else if (component is MotionHost)
            {
                a.MotionHosts.Add(PathOf(owner) + " (" + ArrayLength(component, "pathPoints")
                    + " path floats)");
            }
        }

        private static int ArrayLength(Component component, string field)
        {
            SerializedProperty property = new SerializedObject(component).FindProperty(field);
            return property != null && property.isArray ? property.arraySize : 0;
        }

        /// <summary>
        /// Lists every object reference of <paramref name="components"/> that points at an
        /// object of <paramref name="sourceScene"/> which is not in <paramref name="movedSet"/>
        /// (or, when <paramref name="mapped"/> is given, that is not a key of it).
        /// </summary>
        private static void CollectRefs(List<Component> components, Scene sourceScene,
            HashSet<GameObject> movedSet, Dictionary<GameObject, GameObject> mapped,
            List<IslandRef> into)
        {
            for (int i = 0; i < components.Count; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                // A Transform's parent pointer is the structure, not data; the UdonBehaviour's
                // own table is rewritten from its proxy.
                if (component is Transform || component is UdonBehaviour) continue;

                var so = new SerializedObject(component);
                SerializedProperty it = so.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    enter = true;
                    if (it.propertyType == SerializedPropertyType.Generic && it.isArray
                        && !ArrayMayHoldObjects(it))
                    {
                        enter = false;
                        continue;
                    }
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                    Object target = it.objectReferenceValue;
                    if (target == null) continue;
                    GameObject targetObject = target as GameObject;
                    if (targetObject == null)
                    {
                        Component targetComponent = target as Component;
                        if (targetComponent != null) targetObject = targetComponent.gameObject;
                    }
                    if (targetObject == null || !targetObject.scene.IsValid()) continue;
                    if (targetObject.scene != sourceScene) continue;
                    if (movedSet != null && movedSet.Contains(targetObject)) continue;

                    var r = new IslandRef();
                    r.Owner = component;
                    r.OwnerPath = PathOf(component.transform);
                    r.PropertyPath = it.propertyPath;
                    r.TargetObject = targetObject;
                    r.TargetType = target.GetType();
                    r.TargetOrdinal = OrdinalOf(targetObject, target);
                    r.TargetPath = PathOf(targetObject.transform);
                    into.Add(r);
                }
            }
        }

        private static bool ArrayMayHoldObjects(SerializedProperty array)
        {
            if (array.arraySize == 0) return false;
            SerializedPropertyType elementType = array.GetArrayElementAtIndex(0).propertyType;
            return elementType == SerializedPropertyType.ObjectReference
                || elementType == SerializedPropertyType.Generic;
        }

        private static int OrdinalOf(GameObject owner, Object target)
        {
            if (target is GameObject) return 0;
            Component[] same = owner.GetComponents(target.GetType());
            for (int i = 0; i < same.Length; i++)
            {
                if (same[i] == target) return i;
            }
            return 0;
        }

        // ------------------------------------------------------------ facts ---

        /// <summary>Reads what the source scene says about its own level.</summary>
        public static IslandSourceFacts ReadFacts(Scene source)
        {
            var facts = new IslandSourceFacts();

            KatamariBallMotor motor = FirstIn<KatamariBallMotor>(source);
            if (motor != null)
            {
                var so = new SerializedObject(motor);
                facts.HasMotor = true;
                SerializedProperty center = so.FindProperty("startCenterWorld");
                if (center != null) facts.MotorStartCenter = center.vector3Value;
                SerializedProperty diameter = so.FindProperty("startDiameterWorldM");
                if (diameter != null) facts.MotorStartDiameterM = diameter.floatValue;
                SerializedProperty ground = so.FindProperty("groundPlaneOffset");
                if (ground != null) facts.MotorGroundOffset = ground.floatValue;
            }

            StoryRoundController round = FirstIn<StoryRoundController>(source);
            if (round != null)
            {
                var so = new SerializedObject(round);
                facts.HasRound = true;
                facts.RoundLevelId = StringOf(so, "levelId");
                facts.RoundGoalType = IntOf(so, "goalType", 0);
                facts.RoundTargetDiameterM = FloatOf(so, "targetDiameterM", 0f);
                facts.RoundTargetCount = IntOf(so, "targetCount", 0);
                facts.RoundTimeLimitSeconds = FloatOf(so, "timeLimitSeconds", 0f);
                facts.RoundKillEnabled = IntOf(so, "killPlaneEnabled", 0) != 0;
                facts.RoundKillY = FloatOf(so, "killPlaneY", 0f);
                facts.RoundCountKind = StringOf(so, "countGoalKind");
                SerializedProperty host = so.FindProperty("pickupHost");
                Component hostComponent = host == null ? null : host.objectReferenceValue as Component;
                if (hostComponent != null) facts.RoundPickupHostPath = PathOf(hostComponent.transform);
            }

            PhysicsTestFieldDriver driver = FirstIn<PhysicsTestFieldDriver>(source);
            if (driver != null)
            {
                var so = new SerializedObject(driver);
                facts.HasDriver = true;
                SerializedProperty frame = so.FindProperty("sandboxFrame");
                Component frameComponent = frame == null ? null : frame.objectReferenceValue as Component;
                if (frameComponent != null) facts.DriverFrameArenaPath = PathOf(frameComponent.transform);

                SerializedProperty cells = so.FindProperty("cells");
                if (cells != null && cells.isArray)
                {
                    for (int i = 0; i < cells.arraySize; i++)
                    {
                        PhysicsTestCell cell = cells.GetArrayElementAtIndex(i).objectReferenceValue
                            as PhysicsTestCell;
                        if (cell == null) continue;
                        SerializedProperty arena = new SerializedObject(cell).FindProperty("arena");
                        Component arenaComponent = arena == null
                            ? null : arena.objectReferenceValue as Component;
                        if (arenaComponent != null)
                        {
                            facts.DriverCellArenaPaths.Add(PathOf(arenaComponent.transform));
                        }
                    }
                }

                SerializedProperty kinds = so.FindProperty("kindWhitelist");
                if (kinds != null && kinds.isArray)
                {
                    facts.DriverKinds = new string[kinds.arraySize];
                    for (int i = 0; i < kinds.arraySize; i++)
                    {
                        facts.DriverKinds[i] = kinds.GetArrayElementAtIndex(i).stringValue;
                    }
                }
                SerializedProperty spawn = so.FindProperty("sandboxSpawn");
                if (spawn != null) facts.DriverSpawn = spawn.vector3Value;
                facts.DriverSpawnHeadingDeg = FloatOf(so, "sandboxSpawnHeadingDegrees", 0f);
            }
            return facts;
        }

        /// <summary>The first component of type T in a scene (hierarchy order, inactive included).</summary>
        public static T FirstIn<T>(Scene scene) where T : Component
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                T found = roots[i].GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

        private static string StringOf(SerializedObject so, string field)
        {
            SerializedProperty p = so.FindProperty(field);
            return p == null ? null : p.stringValue;
        }

        private static float FloatOf(SerializedObject so, string field, float fallback)
        {
            SerializedProperty p = so.FindProperty(field);
            return p == null ? fallback : p.floatValue;
        }

        // int, enum and bool properties all read as an int here (a bool is 0 or 1).
        private static int IntOf(SerializedObject so, string field, int fallback)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) return fallback;
            if (p.propertyType == SerializedPropertyType.Boolean) return p.boolValue ? 1 : 0;
            if (p.propertyType == SerializedPropertyType.Enum) return p.intValue;
            return p.intValue;
        }

        // ------------------------------------------------------------ bounds ---

        /// <summary>
        /// The world bounds of the island's play area: the active, enabled renderers and
        /// colliders below <paramref name="root"/> that are NOT inside a <c>TerrainVisual</c>
        /// folder; false when there are none. The drawn backdrop is measured separately
        /// (<see cref="TryBackdropBounds"/>) and does not count toward island spacing or the
        /// coordinate range (<see cref="IslandMath"/>).
        /// </summary>
        public static bool TryBounds(Transform root, out float[] min, out float[] max)
        {
            return TryBoundsOf(root, false, out min, out max);
        }

        /// <summary>
        /// The world bounds of the drawn backdrop below <paramref name="root"/>: the active,
        /// enabled renderers inside a <c>TerrainVisual</c> folder (a level's ground surface,
        /// distant buildings, mountains, sea and sky shell). False when there are none.
        /// </summary>
        public static bool TryBackdropBounds(Transform root, out float[] min, out float[] max)
        {
            return TryBoundsOf(root, true, out min, out max);
        }

        private static bool TryBoundsOf(Transform root, bool backdrop, out float[] min,
            out float[] max)
        {
            min = null;
            max = null;
            if (root == null) return false;
            string folder = TerrainMeshImporter.TerrainVisualName;
            bool any = false;
            Bounds total = new Bounds();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (!renderers[i].enabled) continue;
                if (IslandMath.IsBackdropPath(PathOf(renderers[i].transform), folder) != backdrop) continue;
                Encapsulate(ref total, ref any, renderers[i].bounds);
            }
            if (!backdrop)
            {
                Collider[] colliders = root.GetComponentsInChildren<Collider>(false);
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (!colliders[i].enabled) continue;
                    if (IslandMath.IsBackdropPath(PathOf(colliders[i].transform), folder)) continue;
                    Encapsulate(ref total, ref any, colliders[i].bounds);
                }
            }
            if (!any) return false;
            min = new[] { total.min.x, total.min.y, total.min.z };
            max = new[] { total.max.x, total.max.y, total.max.z };
            return true;
        }

        private static void Encapsulate(ref Bounds total, ref bool any, Bounds next)
        {
            if (!any)
            {
                total = next;
                any = true;
            }
            else
            {
                total.Encapsulate(next);
            }
        }

        // ------------------------------------------------------------ import ---

        /// <summary>
        /// Opens the level's source (or uses the hub scene for the base scene), analyses it,
        /// and when the analysis has no problem moves the content into
        /// <c>Island_&lt;id&gt;</c>. <paramref name="output"/> must be the active scene.
        /// </summary>
        public static IslandResult Import(HubLevelCatalog.LevelEntry level, Scene output,
            string baseScenePath)
        {
            var result = new IslandResult();
            bool sourceIsOutput = string.Equals(level.source.scene, baseScenePath,
                StringComparison.OrdinalIgnoreCase);

            Scene source;
            bool openedByUs = false;
            if (sourceIsOutput)
            {
                source = output;
            }
            else if (!TryOpenSource(level.source.scene, out source, out openedByUs, result.Problems))
            {
                return result;
            }

            try
            {
                SceneManager.SetActiveScene(output);
                IslandAnalysis analysis = Analyze(level, source, sourceIsOutput);
                result.Facts = analysis.Facts;
                result.ObjectsMoved = analysis.MovedObjects.Count;
                result.ComponentsScanned = analysis.ComponentCount;
                for (int i = 0; i < analysis.Problems.Count; i++) result.Problems.Add(analysis.Problems[i]);
                if (analysis.Problems.Count == 0) Execute(analysis, output, result);
            }
            catch (Exception exception)
            {
                result.Problems.Add("level '" + level.id + "': the island import threw "
                    + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (openedByUs) CloseSource(source, output);
            }
            return result;
        }

        private static void Execute(IslandAnalysis a, Scene output, IslandResult result)
        {
            HubLevelCatalog.LevelEntry level = a.Level;
            string shellName = IslandMath.ShellName(level.id);
            bool instantiate = UseInstantiateFallback && !a.SourceIsOutput;
            result.Mode = instantiate ? "instantiate" : "move";

            if (HubLevelCatalog.FindPath(output, shellName) != null)
            {
                result.Problems.Add("the scene already has '" + shellName + "'");
                return;
            }

            string offsetReason;
            float[] offset = level.island == null ? null : level.island.offset;
            if (!IslandMath.IsOffsetValid(offset, out offsetReason))
            {
                result.Problems.Add("level '" + level.id + "': " + offsetReason);
                return;
            }

            // The shell sits at the origin while objects are parented with their world pose
            // kept, and is moved to the offset afterwards: every taken object then ends at its
            // authored position plus the offset.
            var shellObject = new GameObject(shellName);
            SceneManager.MoveGameObjectToScene(shellObject, output);
            Transform shell = shellObject.transform;
            result.Shell = shell;

            // Leave-behind subtrees come off their parents first, remembering where they were.
            var leftBehind = new List<KeyValuePair<Transform, string>>();
            if (!instantiate)
            {
                for (int i = 0; i < a.LeaveBehind.Count; i++)
                {
                    leftBehind.Add(new KeyValuePair<Transform, string>(a.LeaveBehind[i], a.LeavePaths[i]));
                    a.LeaveBehind[i].SetParent(null, true);
                }
            }

            var cloneOf = new Dictionary<GameObject, GameObject>();
            var placed = new List<Transform>();
            for (int i = 0; i < a.TakeRoots.Count; i++)
            {
                string[] parts = a.TakePaths[i].Split('/');
                Transform parent = shell;
                for (int k = 0; k < parts.Length - 1; k++) parent = EnsureStub(parent, parts[k]);

                Transform taken = a.TakeRoots[i];
                if (instantiate)
                {
                    GameObject clone = Object.Instantiate(taken.gameObject, parent, true);
                    clone.name = taken.name;
                    RemoveLeaveBehindClones(taken, clone.transform, a);
                    if (!MapHierarchies(taken, clone.transform, a, cloneOf, result))
                    {
                        return;
                    }
                    placed.Add(clone.transform);
                }
                else
                {
                    if (taken.parent != null) taken.SetParent(null, true);
                    if (!a.SourceIsOutput) SceneManager.MoveGameObjectToScene(taken.gameObject, output);
                    taken.SetParent(parent, true);
                    placed.Add(taken);
                }
                result.RootsMoved++;
            }

            // Put the leave-behind subtrees back under (recreated) copies of their ancestors,
            // in the hub scene itself; in another scene they go away with the source.
            if (a.SourceIsOutput)
            {
                for (int i = 0; i < leftBehind.Count; i++) ReattachLeftBehind(leftBehind[i], output);
            }

            shell.position = new Vector3(offset[0], offset[1], offset[2]);

            // Components that now live under the shell.
            var components = new List<Component>();
            Transform[] all = shell.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Component[] here = all[i].GetComponents<Component>();
                for (int k = 0; k < here.Length; k++) if (here[k] != null) components.Add(here[k]);
            }

            if (!a.SourceIsOutput) RemapReferences(a, components, cloneOf, output, instantiate, result);
            ShiftWorldData(shell, offset, result);
            DeferTerrainRegistration(shell, result);

            for (int i = 0; i < result.ModifiedProxies.Count; i++)
            {
                try
                {
                    UdonSharpEditorUtility.CopyProxyToUdon(result.ModifiedProxies[i]);
                }
                catch (Exception exception)
                {
                    result.Problems.Add("CopyProxyToUdon threw for "
                        + result.ModifiedProxies[i].GetType().Name + " on '"
                        + PathOf(result.ModifiedProxies[i].transform) + "': " + exception.Message
                        + ". Compile, save the scene, reopen it and run the assembly again");
                }
            }

            // CopyProxyToUdon reports nothing when it does not move a value, so the one value the
            // hub cannot work without is read back from the table.
            TerrainFieldHost[] terrains = shell.GetComponentsInChildren<TerrainFieldHost>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                if (!UdonVariableAccess.CheckBool(terrains[i], "deferRegistration", true,
                    PathOf(terrains[i].transform), result.Lines))
                {
                    result.Problems.Add("TerrainFieldHost on '" + PathOf(terrains[i].transform)
                        + "' does not have deferRegistration=true in its Udon variables after the"
                        + " proxy copy (see the diag line)");
                }
            }

            // The drawn backdrop (TerrainVisual) is left out of the island's play-area bounds;
            // it is reported here so the numbers stay findable.
            float[] backdropMin;
            float[] backdropMax;
            if (TryBackdropBounds(shell, out backdropMin, out backdropMax))
            {
                result.Lines.Add("backdrop bounds " + IslandMath.Format(backdropMin) + ".."
                    + IslandMath.Format(backdropMax) + " x " + IslandMath.Num(backdropMin[0]) + ".."
                    + IslandMath.Num(backdropMax[0]) + " z " + IslandMath.Num(backdropMin[2]) + ".."
                    + IslandMath.Num(backdropMax[2]) + " (drawn only; not counted toward island"
                    + " spacing or the coordinate range)");
            }

            EditorSceneManager.MarkSceneDirty(output);
        }

        private static Transform EnsureStub(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        // The ancestors of a leave-behind subtree (for example LevelContent above
        // LevelContent/hub) were taken away with the rest; put empty ones back at the scene
        // root and put the subtree under them with its world pose kept.
        private static void ReattachLeftBehind(KeyValuePair<Transform, string> item, Scene output)
        {
            string[] parts = item.Value.Split('/');
            Transform parent = null;
            for (int k = 0; k < parts.Length - 1; k++)
            {
                Transform next = null;
                if (k == 0)
                {
                    GameObject[] roots = output.GetRootGameObjects();
                    for (int r = 0; r < roots.Length; r++)
                    {
                        if (string.Equals(roots[r].name, parts[0]) && roots[r].transform != item.Key)
                        {
                            next = roots[r].transform;
                            break;
                        }
                    }
                    if (next == null)
                    {
                        var go = new GameObject(parts[0]);
                        SceneManager.MoveGameObjectToScene(go, output);
                        next = go.transform;
                    }
                }
                else
                {
                    next = EnsureStub(parent, parts[k]);
                }
                parent = next;
            }
            item.Key.SetParent(parent, true);
        }

        // ---- instantiate fallback helpers ----

        private static void RemoveLeaveBehindClones(Transform original, Transform clone,
            IslandAnalysis a)
        {
            for (int i = 0; i < a.LeaveBehind.Count; i++)
            {
                Transform leave = a.LeaveBehind[i];
                if (!leave.IsChildOf(original)) continue;
                string relative = RelativePath(original, leave);
                Transform copy = relative.Length == 0 ? null : clone.Find(relative);
                if (copy != null) Object.DestroyImmediate(copy.gameObject);
            }
        }

        private static string RelativePath(Transform ancestor, Transform descendant)
        {
            string path = "";
            for (Transform t = descendant; t != null && t != ancestor; t = t.parent)
            {
                path = path.Length == 0 ? t.name : t.name + "/" + path;
            }
            return path;
        }

        private static bool MapHierarchies(Transform original, Transform clone, IslandAnalysis a,
            Dictionary<GameObject, GameObject> cloneOf, IslandResult result)
        {
            var originals = new List<Transform>();
            Transform[] all = original.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (a.MovedObjects.Contains(all[i].gameObject)) originals.Add(all[i]);
            }
            Transform[] clones = clone.GetComponentsInChildren<Transform>(true);
            if (originals.Count != clones.Length)
            {
                result.Problems.Add("instantiate fallback: '" + PathOf(original) + "' has "
                    + originals.Count + " taken objects but its clone has " + clones.Length);
                return false;
            }
            for (int i = 0; i < clones.Length; i++)
            {
                cloneOf[originals[i].gameObject] = clones[i].gameObject;
            }
            return true;
        }

        // ------------------------------------------------------------ remap ---

        private static void RemapReferences(IslandAnalysis a, List<Component> components,
            Dictionary<GameObject, GameObject> cloneOf, Scene output, bool instantiate,
            IslandResult result)
        {
            List<IslandRef> refs = a.External;
            if (instantiate)
            {
                // Clones still point at the originals (other cloned roots) and at the source
                // scene; collect from the clones with "mapped" originals counting as taken.
                refs = new List<IslandRef>();
                var originalsNotMapped = new HashSet<GameObject>();
                CollectRefs(components, a.Source, originalsNotMapped, null, refs);
            }

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < refs.Count; i++)
            {
                IslandRef r = refs[i];
                if (r.Owner == null) continue;

                Object replacement = null;
                GameObject cloneObject;
                if (instantiate && cloneOf.TryGetValue(r.TargetObject, out cloneObject))
                {
                    replacement = PickTarget(cloneObject, r);
                }
                else
                {
                    Transform same = HubLevelCatalog.FindPath(output, r.TargetPath);
                    if (same != null) replacement = PickTarget(same.gameObject, r);
                }

                if (replacement == null)
                {
                    result.Problems.Add("level '" + a.Level.id + "': " + r.OwnerPath + " ("
                        + r.Owner.GetType().Name + ")." + r.PropertyPath + " points at '"
                        + r.TargetPath + "' (" + r.TargetType.Name + ") in '" + a.Source.name
                        + "', and the hub scene has no object of that type at that path");
                    continue;
                }

                var so = new SerializedObject(r.Owner);
                SerializedProperty property = so.FindProperty(r.PropertyPath);
                if (property == null)
                {
                    result.Problems.Add("level '" + a.Level.id + "': cannot find property '"
                        + r.PropertyPath + "' on " + r.OwnerPath);
                    continue;
                }
                property.objectReferenceValue = replacement;
                so.ApplyModifiedPropertiesWithoutUndo();
                result.Remapped++;
                NoteProxy(r.Owner, result);

                string key = r.TargetPath + " (" + r.TargetType.Name + ")";
                int n;
                counts.TryGetValue(key, out n);
                counts[key] = n + 1;
            }
            foreach (KeyValuePair<string, int> pair in counts)
            {
                result.RemapSummary.Add(pair.Value + " x " + pair.Key);
            }

            // Nothing may still point into a scene other than the hub.
            var residual = new List<IslandRef>();
            CollectRefs(components, a.Source, new HashSet<GameObject>(), null, residual);
            for (int i = 0; i < residual.Count; i++)
            {
                result.Problems.Add("level '" + a.Level.id + "': " + residual[i].OwnerPath + " ("
                    + residual[i].Owner.GetType().Name + ")." + residual[i].PropertyPath
                    + " still points into source scene '" + a.Source.name + "' at '"
                    + residual[i].TargetPath + "'");
            }
        }

        private static Object PickTarget(GameObject owner, IslandRef r)
        {
            if (r.TargetType == typeof(GameObject)) return owner;
            if (r.TargetType == typeof(Transform) || r.TargetType == typeof(RectTransform))
            {
                return owner.transform;
            }
            Component[] same = owner.GetComponents(r.TargetType);
            if (same.Length == 0) return null;
            return r.TargetOrdinal < same.Length ? same[r.TargetOrdinal] : same[0];
        }

        private static void NoteProxy(Component owner, IslandResult result)
        {
            var proxy = owner as UdonSharpBehaviour;
            if (proxy != null && !result.ModifiedProxies.Contains(proxy)) result.ModifiedProxies.Add(proxy);
        }

        // ------------------------------------------------------------ world data ---

        // Adds the island offset to the serialized world coordinates (IslandWorldFields).
        private static void ShiftWorldData(Transform shell, float[] offset, IslandResult result)
        {
            if (offset[0] == 0f && offset[1] == 0f && offset[2] == 0f) return;

            TerrainFieldHost[] terrains = shell.GetComponentsInChildren<TerrainFieldHost>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                var so = new SerializedObject(terrains[i]);
                SerializedProperty vertices = so.FindProperty("vertices");
                if (vertices == null || !vertices.isArray)
                {
                    result.Problems.Add("TerrainFieldHost.vertices is not a serialized array on '"
                        + PathOf(terrains[i].transform) + "' (renamed? update IslandWorldFields)");
                    continue;
                }
                Vector3 add = new Vector3(offset[0], offset[1], offset[2]);
                for (int v = 0; v < vertices.arraySize; v++)
                {
                    SerializedProperty element = vertices.GetArrayElementAtIndex(v);
                    element.vector3Value = element.vector3Value + add;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                result.TerrainVerticesShifted += vertices.arraySize;
                NoteProxy(terrains[i], result);
            }

            MotionHost[] motions = shell.GetComponentsInChildren<MotionHost>(true);
            for (int i = 0; i < motions.Length; i++)
            {
                var so = new SerializedObject(motions[i]);
                SerializedProperty points = so.FindProperty("pathPoints");
                if (points == null || !points.isArray)
                {
                    result.Problems.Add("MotionHost.pathPoints is not a serialized array on '"
                        + PathOf(motions[i].transform) + "' (renamed? update IslandWorldFields)");
                    continue;
                }
                var flat = new float[points.arraySize];
                for (int f = 0; f < flat.Length; f++) flat[f] = points.GetArrayElementAtIndex(f).floatValue;
                if (IslandMath.TranslateTriples(flat, offset) < 0)
                {
                    result.Problems.Add("MotionHost.pathPoints on '" + PathOf(motions[i].transform)
                        + "' has " + flat.Length + " numbers, not a multiple of 3");
                    continue;
                }
                for (int f = 0; f < flat.Length; f++) points.GetArrayElementAtIndex(f).floatValue = flat[f];
                so.ApplyModifiedPropertiesWithoutUndo();
                result.PathFloatsShifted += flat.Length;
                NoteProxy(motions[i], result);
            }
        }

        // In a hub the level switch registers the terrain; the host must not do it in Start.
        private static void DeferTerrainRegistration(Transform shell, IslandResult result)
        {
            TerrainFieldHost[] terrains = shell.GetComponentsInChildren<TerrainFieldHost>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                var so = new SerializedObject(terrains[i]);
                SerializedProperty defer = so.FindProperty("deferRegistration");
                if (defer == null)
                {
                    result.Problems.Add("TerrainFieldHost.deferRegistration is not a serialized"
                        + " field on '" + PathOf(terrains[i].transform) + "'");
                    continue;
                }
                if (!defer.boolValue)
                {
                    defer.boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                NoteProxy(terrains[i], result);
            }
        }
    }
}
