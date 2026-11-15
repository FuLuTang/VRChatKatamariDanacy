using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using VRCKatamari.Editor.Pickup;
using VRCKatamari.Pickup;
using VRCKatamari.Story;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The one-way importer: <c>Docs/Design/Levels/&lt;levelId&gt;/placement.json</c> into the
    /// managed subtree <c>LevelContent</c> of the open scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Boundary (design: <c>Docs/Design/Tools/LEVEL_AUTHORING.md</c> sections 7.1-7.2). This
    /// tool creates, updates and deletes objects <b>only</b> under
    /// <c>LevelContent/Props/...</c> and <c>LevelContent/Structures/...</c>. It never
    /// reparents, renames or deletes anything outside that root, never touches the ball,
    /// the HUD, the stations or the room wiring, and never saves the scene: it leaves the
    /// scene dirty on purpose so "import" and "save" stay separate, undoable actions.
    /// Hand edits inside <c>LevelContent</c> are overwritten by the next run - that is the
    /// design (single source of truth is the JSON), and the objects carry a tooltip saying
    /// so.
    /// </para>
    /// <para>
    /// Identity is the <c>itemId</c>, and there is deliberately <b>no marker component</b>.
    /// A <c>LevelItem</c> MonoBehaviour would have to live in a runtime assembly to be
    /// serialized into a scene (an Editor-assembly type cannot), and <c>Runtime/</c> is out
    /// of scope for this tool. The item id is therefore the GameObject's name, which is
    /// exactly what <see cref="PickupFieldSync"/> already uses as the row id - one key, two
    /// consumers, no second convention to keep in sync.
    /// </para>
    /// <para>
    /// Hierarchy: <c>LevelContent/Props/&lt;kind&gt;/[&lt;zone&gt;/][&lt;group&gt;/]&lt;itemId&gt;</c>.
    /// The first level <b>must</b> be the kind folder: <see cref="PickupFieldSync"/> reads the
    /// kind from the folder directly under the field root it is given, and the importer gives
    /// it <c>LevelContent/Props</c>. Everything below the kind folder is for human
    /// readability only; poses are world-space.
    /// </para>
    /// <para>
    /// value units are the level's data, not a pricing rule. The Lead's decision of
    /// 2026-11-15 moved the number onto the per-def declaration in the JSON
    /// (<c>defs[].valueUnits</c>, overridable per prop), so this importer builds one
    /// item-id-to-value map and hands it to <see cref="PickupFieldSync.SyncFromChildren"/>.
    /// The sync writes the declared value together with the other five arrays and reads it
    /// back; the importer never touches a host array itself (Docs/OPEN_DECISIONS.md (q) U2).
    /// A declaration the sync cannot apply (a dropped row) or one that does not read back is
    /// an error, not something this tool repairs by rewriting.
    /// </para>
    /// <para>
    /// The importer is not the path for an un-migrated scene: it only imports a
    /// <c>placement.json</c>. The menu sync called with no declaration map is that path, and
    /// it preserves every value already serialized on the host.
    /// </para>
    /// <para>
    /// Assembly placement: <c>Editor/</c> compiles into the predefined
    /// <c>Assembly-CSharp-Editor</c> (see the remark in <c>PickupFieldSync.cs</c>), which is
    /// why the UdonSharp editor usings below resolve without an asmdef.
    /// </para>
    /// </remarks>
    public static class PlacementImporter
    {
        public const string MenuPath = "VRC Katamari/Level/Import Placement (LevelContent)";

        public const string ManagedRootName = "LevelContent";
        public const string PropsFolderName = "Props";
        public const string StructuresFolderName = "Structures";

        // Repo-relative, like the greybox builder's payload: the JSON lives next to the
        // Unity project, not inside Assets/, so it can be diffed and validated by the CLI.
        public const string LevelsRepoRelativeFolder = "Docs/Design/Levels";
        public const string PlacementFileName = "placement.json";

        private const string UndoLabel = "Import Level Placement";
        private const float PositionToleranceM = 0.001f;
        private const float RotationDotTolerance = 0.99999f;

        private const string PropsArtFolder = "Assets/VRC_Katamari/Art/Props";

        // LevelArenaHost's serialized field names (Runtime/Story/LevelArenaHost.cs). They are
        // private, so they are looked up by name exactly like PickupFieldSync does with the
        // host's arrays; a rename in Runtime/ fails loudly here instead of silently wiring
        // nothing.
        private const string GroundNormalField = "groundNormal";
        private const string GroundOffsetField = "groundOffset";
        private const string BoundaryWallsField = "boundaryWalls";
        private const string StaticBoxesField = "staticBoxes";
        private const string AllowedKindsField = "allowedKinds";

        // ------------------------------------------------------------------ DTO ---

        [Serializable]
        public class PlacementDocument
        {
            public string schema;
            public LevelBlock level;
            public DefEntry[] defs;
            public PropEntry[] props;
            public StructureEntry[] structures;
        }

        [Serializable]
        public class LevelBlock
        {
            public string id;
            public string scene;
            public float ballDiameterMax = 2.2f;
            public BudgetBlock budget;
            public ArenaBlock arena;
        }

        /// <summary>
        /// Present when this document describes ONE arena cell instead of a whole level.
        /// </summary>
        /// <remarks>
        /// The motor's static tables hold 8 planes / 8 boxes / 4 ramps, so the physics test
        /// field registers one cell at a time (<c>PHYSICS_TEST_FIELD.md</c> section 3) and each
        /// cell needs its own <see cref="LevelArenaHost"/>. One document per cell is what makes
        /// that 1:1 with the importer: the cell id names a managed subtree
        /// (<c>LevelContent/&lt;id&gt;</c>) and the arena host that registers only that subtree.
        /// Without an arena block the importer keeps its old behaviour (one level per scene, one
        /// arena host found anywhere in it).
        /// </remarks>
        [Serializable]
        public class ArenaBlock
        {
            public string id;
        }

        [Serializable]
        public class BudgetBlock
        {
            public int planes = 8;
            public int boxes = 8;
            public int ramps = 4;
            public int blocking = 6;
        }

        [Serializable]
        public class DefEntry
        {
            public string id;
            public string kind;
            public int valueUnits;
        }

        [Serializable]
        public class PropEntry
        {
            public string itemId;
            public string def;
            public string kind;
            public string zone;
            public string group;
            public float[] position;
            public float[] rotationQuat;
            public float[] aabb;
            public string support;
            public string stackedOn;
            public int valueUnits;
            public string notes;
        }

        [Serializable]
        public class StructureEntry
        {
            public string id;
            public string kind;
            public string role;
            public string shape;
            public float[] center;
            public float[] size;
            public float[] rotationQuat;
            public string collision;
            public bool support;
            public string meets;
        }

        // ------------------------------------------------------------------ menu ---

        /// <summary>
        /// Imports every document whose <c>level.scene</c> matches the open scene's name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A menu item cannot prompt for text, so the file says which scene it belongs to. Two
        /// cases exist:
        /// </para>
        /// <list type="bullet">
        /// <item>one level names the scene: import it (the historic behaviour);</item>
        /// <item>several name it, and every one of them is an arena cell
        /// (<c>level.arena</c> is set): import each cell, because that is exactly what the test
        /// field is - one scene, one arena document per cell (see
        /// <see cref="ArenaBlock"/>).</item>
        /// </list>
        /// <para>
        /// A mix of cell and whole-level documents naming one scene is refused: importing both
        /// would register two arenas into the same motor. Programmatic callers use
        /// <see cref="Import(string)"/> instead.
        /// </para>
        /// </remarks>
        [MenuItem(MenuPath, false, 200)]
        public static void ImportActiveSceneLevel()
        {
            List<string> matches;
            List<string> seen;
            string failure;
            if (!TryResolveLevelsForActiveScene(out matches, out seen, out failure))
            {
                Debug.LogError("[level] " + failure);
                return;
            }

            if (matches.Count == 1)
            {
                Import(matches[0]);
                return;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                if (!IsArenaLevel(matches[i]))
                {
                    Debug.LogError("[level] several documents name the scene '" +
                                   EditorSceneManager.GetActiveScene().name + "': " +
                                   string.Join(", ", matches.ToArray()) + ", and '" + matches[i] +
                                   "' is a whole level, not an arena cell. Call " +
                                   "PlacementImporter.Import(levelId) with the one you mean");
                    return;
                }
            }

            int imported = 0;
            var failed = new List<string>();
            for (int i = 0; i < matches.Count; i++)
            {
                var cellReport = new PlacementImportReport();
                if (RunImport(matches[i], cellReport))
                {
                    imported++;
                }
                else
                {
                    // An attempt is not an import: a cell whose arena host is missing, or whose
                    // placement failed validation, is a failed run even though it logged a report.
                    failed.Add(matches[i]);
                }

                cellReport.Log();
            }

            string line = "[level] imported " + imported + "/" + matches.Count + " arena cell(s): " +
                          string.Join(", ", matches.ToArray()) + " (levels naming this scene: " +
                          string.Join(", ", seen.ToArray()) + ")";
            if (failed.Count > 0)
            {
                Debug.LogError(line + "  FAILED: " + string.Join(", ", failed.ToArray()));
            }
            else
            {
                Debug.Log(line);
            }
        }

        /// <summary>
        /// Imports one level by id and returns the report text (also logged).
        /// A static entry point so an MCP bridge, a test or <c>-executeMethod</c> can call it.
        /// </summary>
        public static string Import(string levelId)
        {
            var report = new PlacementImportReport();
            RunImport(levelId, report);
            report.Log();
            return report.BuildSummary();
        }

        /// <summary>
        /// Imports one level into a caller-owned report and returns whether the run was accepted.
        /// </summary>
        /// <remarks>
        /// Split from <see cref="Import(string)"/> so a caller that imports several documents (the
        /// menu does, one per arena cell) can count <b>accepted runs</b> instead of attempts. A
        /// failed run still logs its own summary - it just must not be added to the success count.
        /// </remarks>
        public static bool RunImport(string levelId, PlacementImportReport report)
        {
            report.LevelId = levelId;
            report.SceneName = EditorSceneManager.GetActiveScene().name;

            bool ok = false;
            try
            {
                ok = ImportInternal(levelId, report);
            }
            catch (Exception exception)
            {
                report.Fail("unexpected exception: " + exception);
                ok = false;
            }

            if (ok && report.Errors.Count == 0)
            {
                report.Success = true;
            }
            else
            {
                report.Success = false;
                if (string.IsNullOrEmpty(report.Failure))
                {
                    report.Fail(report.Errors.Count + " error(s); the scene was changed but the run is not accepted");
                }
            }

            return report.Success;
        }

        // -------------------------------------------------------------- import ---

        private static bool ImportInternal(string levelId, PlacementImportReport report)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.Fail("exit Play Mode first; the import writes serialized scene data");
                return false;
            }

            string path;
            List<string> tried;
            if (!TryResolvePlacementPath(levelId, out path, out tried))
            {
                report.Fail("placement.json not found for level '" + levelId + "'. Tried:\n  " +
                            string.Join("\n  ", tried.ToArray()));
                return false;
            }

            report.PlacementPath = path;

            PlacementDocument doc;
            try
            {
                doc = JsonUtility.FromJson<PlacementDocument>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                report.Fail("cannot read " + path + ": " + exception.Message);
                return false;
            }

            if (doc == null || doc.props == null)
            {
                report.Fail(path + " has no props[]; is it a placement.json?");
                return false;
            }

            if (!ValidateDocument(doc, report))
            {
                return false;
            }

            Transform managedRoot;
            if (!FindOrCreateManagedRoot(report, out managedRoot))
            {
                return false;
            }

            // A cell document owns one subtree and one arena host; a whole-level document keeps the
            // old shape (everything directly under LevelContent).
            Transform levelRoot = managedRoot;
            string arenaId = ArenaIdOf(doc);
            if (!string.IsNullOrEmpty(arenaId))
            {
                levelRoot = EnsureFolder(managedRoot, arenaId);
                report.ArenaId = arenaId;
            }

            report.ManagedRootPath = HierarchyPath(levelRoot);

            Transform propsRoot = EnsureFolder(levelRoot, PropsFolderName);
            Transform structuresRoot = EnsureFolder(levelRoot, StructuresFolderName);

            bool propsOk = ApplyProps(doc, propsRoot, report);
            bool structuresOk = ApplyStructures(doc, structuresRoot, report);

            // Only the prop tree owns empty folders (kind/zone/group). A structure child is a
            // deliberate object - a boundary marker has no renderer and no collider, so a
            // folder cleanup over Structures would delete every wall.
            DeleteEmptyFolders(propsRoot);

            MarkSceneDirty();

            // The pickup sync runs after the props exist, because it derives rows from them.
            // The declaration map is the only value input: the sync writes it in the same
            // pass as the other arrays and reads it back, so there is no second write here.
            Dictionary<string, int> declaredValueUnits = BuildDeclaredValueUnits(doc);
            report.ValueUnitsDeclared = SumDeclaredValueUnits(doc);
            if (propsOk)
            {
                TrySyncPickupField(levelRoot, report.ArenaId, propsRoot, declaredValueUnits, report);
            }

            BindArenaHost(doc, levelRoot, structuresRoot, report);

            return propsOk && structuresOk;
        }

        // ---------------------------------------------------------- document ---

        private static bool ValidateDocument(PlacementDocument doc, PlacementImportReport report)
        {
            bool ok = true;
            if (!string.Equals(doc.schema, "katamari.placement/0", StringComparison.Ordinal))
            {
                report.Error("schema is '" + doc.schema + "', expected 'katamari.placement/0'");
                ok = false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId))
                {
                    report.Error("props[" + i + "] has no itemId");
                    ok = false;
                    continue;
                }

                if (!ids.Add(prop.itemId))
                {
                    report.Error("props[" + i + "] itemId '" + prop.itemId + "' is duplicated");
                    ok = false;
                }

                if (string.IsNullOrEmpty(prop.def))
                {
                    report.Error(prop.itemId + " has no def");
                    ok = false;
                }

                if (!IsVector(prop.position, 3))
                {
                    report.Error(prop.itemId + " has no usable position[3]");
                    ok = false;
                }

                if (!IsVector(prop.rotationQuat, 4))
                {
                    report.Error(prop.itemId + " has no usable rotationQuat[4]");
                    ok = false;
                }

                if (DeclaredValueUnits(doc, prop) < 1)
                {
                    report.Error(prop.itemId + " has no valueUnits (neither props[].valueUnits nor " +
                                 "defs[].valueUnits for '" + prop.def + "')");
                    ok = false;
                }
            }

            var structureIds = new HashSet<string>(StringComparer.Ordinal);
            int planes = 0;
            int boxes = 0;
            int ramps = 0;
            int grounds = 0;
            if (doc.structures != null)
            {
                for (int i = 0; i < doc.structures.Length; i++)
                {
                    StructureEntry structure = doc.structures[i];
                    if (structure == null || string.IsNullOrEmpty(structure.id))
                    {
                        report.Error("structures[" + i + "] has no id");
                        ok = false;
                        continue;
                    }

                    if (!structureIds.Add(structure.id))
                    {
                        report.Error("structures[" + i + "] id '" + structure.id + "' is duplicated " +
                                     "(a ramp and a box with one id put an invisible wall at the ramp foot)");
                        ok = false;
                    }

                    if (!IsVector(structure.center, 3) || !IsVector(structure.size, 3) ||
                        !IsVector(structure.rotationQuat, 4))
                    {
                        report.Error("structure '" + structure.id + "' needs center[3], size[3] and " +
                                     "rotationQuat[4]");
                        ok = false;
                        continue;
                    }

                    switch (structure.role)
                    {
                        case "ground":
                            grounds++;
                            break;
                        case "boundary":
                            planes++;
                            break;
                        case "box":
                            boxes++;
                            break;
                        case "ramp":
                            ramps++;
                            break;
                        default:
                            report.Error("structure '" + structure.id + "' has role '" + structure.role +
                                         "' (expected ground/boundary/box/ramp)");
                            ok = false;
                            break;
                    }
                }
            }

            if (grounds != 1)
            {
                report.Error("structures must contain exactly one role=ground (found " + grounds + ")");
                ok = false;
            }

            BudgetBlock budget = (doc.level != null && doc.level.budget != null)
                ? doc.level.budget
                : new BudgetBlock();
            if (planes > budget.planes)
            {
                report.Error("planes=" + planes + " over the level budget of " + budget.planes);
                ok = false;
            }

            if (boxes > budget.boxes)
            {
                report.Error("boxes=" + boxes + " over the level budget of " + budget.boxes +
                             " (the motor silently drops the overflow)");
                ok = false;
            }

            if (ramps > budget.ramps)
            {
                report.Error("ramps=" + ramps + " over the level budget of " + budget.ramps);
                ok = false;
            }

            return ok;
        }

        private static int DeclaredValueUnits(PlacementDocument doc, PropEntry prop)
        {
            if (prop.valueUnits >= 1)
            {
                return prop.valueUnits;
            }

            if (doc.defs == null || prop.def == null)
            {
                return 0;
            }

            for (int i = 0; i < doc.defs.Length; i++)
            {
                DefEntry entry = doc.defs[i];
                if (entry != null && string.Equals(entry.id, prop.def, StringComparison.Ordinal))
                {
                    return entry.valueUnits;
                }
            }

            return 0;
        }

        /// <summary>
        /// Item id to declared value, ready for <see cref="PickupFieldSync.SyncFromChildren"/>.
        /// </summary>
        /// <remarks>
        /// One map, built once, passed once. The importer deliberately does not keep a
        /// second path that writes <c>valueUnits</c> after the sync: that was the U2
        /// double-write, and it made the sync's write and the declaration disagree whenever
        /// the read-back guard was skipped.
        /// </remarks>
        private static Dictionary<string, int> BuildDeclaredValueUnits(PlacementDocument doc)
        {
            var declared = new Dictionary<string, int>(StringComparer.Ordinal);
            if (doc.props == null)
            {
                return declared;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop != null && !string.IsNullOrEmpty(prop.itemId))
                {
                    declared[prop.itemId] = DeclaredValueUnits(doc, prop);
                }
            }

            return declared;
        }

        /// <summary>The level's declared total, for the one-line report.</summary>
        private static int SumDeclaredValueUnits(PlacementDocument doc)
        {
            int total = 0;
            if (doc.props == null)
            {
                return total;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop != null)
                {
                    total += DeclaredValueUnits(doc, prop);
                }
            }

            return total;
        }

        private static string DeclaredKind(PlacementDocument doc, PropEntry prop)
        {
            if (!string.IsNullOrEmpty(prop.kind))
            {
                return prop.kind;
            }

            if (doc.defs != null && prop.def != null)
            {
                for (int i = 0; i < doc.defs.Length; i++)
                {
                    DefEntry entry = doc.defs[i];
                    if (entry != null && string.Equals(entry.id, prop.def, StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(entry.kind))
                    {
                        return entry.kind;
                    }
                }
            }

            // No fallback on purpose: the kind folder is what PickupFieldSync reports as the
            // row kind, and guessing one (from the def name, say) would silently group the
            // prop as something nobody declared. The CLI fills defs[].kind from the def's
            // manifest category, so a missing kind is a data bug with a one-line fix.
            return null;
        }

        // -------------------------------------------------------------- props ---

        private static bool ApplyProps(PlacementDocument doc, Transform propsRoot, PlacementImportReport report)
        {
            bool ok = true;
            Dictionary<string, GameObject> existing = IndexProps(propsRoot);

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                wanted.Add(prop.itemId);

                string kind = DeclaredKind(doc, prop);
                if (string.IsNullOrEmpty(kind))
                {
                    report.Error(prop.itemId + " has no kind: set props[].kind or defs[].kind");
                    report.PropErrors++;
                    ok = false;
                    continue;
                }

                GameObject current;
                existing.TryGetValue(prop.itemId, out current);

                if (current != null && !string.Equals(DefOfInstance(current), prop.def, StringComparison.Ordinal))
                {
                    // A different def is a different mesh; keeping the object would leave the
                    // old model with the new id (and the old size in propBounds).
                    Undo.DestroyObjectImmediate(current);
                    existing.Remove(prop.itemId);
                    current = null;
                }

                Transform folder = EnsurePropFolder(propsRoot, kind, prop.zone, prop.group);
                if (current == null)
                {
                    current = InstantiateProp(prop, folder, report);
                    if (current == null)
                    {
                        report.PropErrors++;
                        ok = false;
                        continue;
                    }

                    existing[prop.itemId] = current;
                    report.PropsCreated++;
                    continue;
                }

                bool moved = false;
                if (current.transform.parent != folder)
                {
                    Undo.SetTransformParent(current.transform, folder, UndoLabel);
                    moved = true;
                }

                if (!PoseMatches(current, prop))
                {
                    ApplyPose(current, prop);
                    moved = true;
                }

                if (moved)
                {
                    report.PropsUpdated++;
                }
                else
                {
                    report.PropsSkipped++;
                }
            }

            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, GameObject> pair in existing)
            {
                if (!wanted.Contains(pair.Key) && pair.Value != null)
                {
                    stale.Add(pair.Key);
                }
            }

            stale.Sort(StringComparer.Ordinal);
            for (int i = 0; i < stale.Count; i++)
            {
                GameObject doomed = existing[stale[i]];
                if (doomed == null)
                {
                    continue;
                }

                // Only objects under the managed root are ever deleted; IndexProps walked
                // propsRoot, so this cannot reach outside it.
                Undo.DestroyObjectImmediate(doomed);
                report.PropsDeleted++;
            }

            return ok;
        }

        private static Transform EnsurePropFolder(Transform propsRoot, string kind, string zone, string group)
        {
            Transform folder = EnsureFolder(propsRoot, kind);
            if (!string.IsNullOrEmpty(zone))
            {
                folder = EnsureFolder(folder, zone);
            }

            if (!string.IsNullOrEmpty(group))
            {
                folder = EnsureFolder(folder, group);
            }

            return folder;
        }

        private static GameObject InstantiateProp(PropEntry prop, Transform folder, PlacementImportReport report)
        {
            string assetPath = PropsArtFolder + "/" + prop.def + "/" + prop.def + ".fbx";
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
            {
                report.Error(prop.itemId + ": no model at " + assetPath + " (the prefab library is P2; " +
                             "until then the importer uses the FBX)");
                return null;
            }

            GameObject instance = PrefabUtility.InstantiatePrefab(asset, folder) as GameObject;
            if (instance == null)
            {
                report.Error(prop.itemId + ": could not instantiate " + assetPath);
                return null;
            }

            instance.name = prop.itemId;

            // The sync reads the item id from the topmost object that carries a Renderer and
            // does not descend into it. A model whose root is an empty node would therefore
            // report its child's name as the id; refuse loudly instead of silently shifting
            // every row id.
            if (instance.GetComponent<Renderer>() == null)
            {
                report.Error(prop.itemId + ": " + assetPath + " has no Renderer on its root, so the " +
                             "pickup sync would take the row id from a child. Fix the model import " +
                             "settings or give the def a prefab (P2)");
                Undo.DestroyObjectImmediate(instance);
                return null;
            }

            ApplyPose(instance, prop);
            return instance;
        }

        private static void ApplyPose(GameObject instance, PropEntry prop)
        {
            Undo.RecordObject(instance.transform, UndoLabel);
            instance.transform.position = ToVector(prop.position);
            instance.transform.rotation = ToQuaternion(prop.rotationQuat);
        }

        private static bool PoseMatches(GameObject instance, PropEntry prop)
        {
            Vector3 position = ToVector(prop.position);
            Quaternion rotation = ToQuaternion(prop.rotationQuat);
            Transform transform = instance.transform;
            return Vector3.Distance(transform.position, position) <= PositionToleranceM &&
                   Mathf.Abs(Quaternion.Dot(transform.rotation, rotation)) >= RotationDotTolerance;
        }

        private static Dictionary<string, GameObject> IndexProps(Transform propsRoot)
        {
            var index = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            CollectProps(propsRoot, index);
            return index;
        }

        private static void CollectProps(Transform parent, Dictionary<string, GameObject> index)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.GetComponent<Renderer>() != null)
                {
                    string itemId;
                    float declaredSize;
                    string error;
                    PickupFieldAuthoringRules.TryParseDeclaredSize(child.name, out itemId,
                        out declaredSize, out error);
                    if (!string.IsNullOrEmpty(error))
                    {
                        itemId = child.name;
                    }

                    if (!index.ContainsKey(itemId))
                    {
                        index.Add(itemId, child.gameObject);
                    }

                    continue;
                }

                CollectProps(child, index);
            }
        }

        private static string DefOfInstance(GameObject instance)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
            if (source == null)
            {
                return null;
            }

            return source.name;
        }

        // --------------------------------------------------------- structures ---

        /// <summary>
        /// Creates or updates one object per <c>structures[]</c> entry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Role decides the object, because the consumer decides the shape. A
        /// <c>ground</c> or <c>boundary</c> entry becomes a marker Transform:
        /// <see cref="LevelArenaHost"/> only reads <c>groundNormal/groundOffset</c> and, for
        /// each wall, <b>local +Z as the plane normal</b> plus the wall's world position as the
        /// plane offset - it never looks at geometry or a collider. Those two roles are
        /// therefore <b>not</b> boxes: making them boxes would add a walkable ledge at the top
        /// of every wall and eat the box budget (design 4.3).
        /// </para>
        /// <para>
        /// A <c>box</c> entry becomes a real cube with a real <see cref="BoxCollider"/>: the
        /// collider's <c>bounds</c> are what the arena bakes into the motor's min/max once, so
        /// the ball needs no runtime physics for it. A <c>ramp</c> entry gets the same
        /// treatment but is not registered yet - <c>LevelArenaHost</c> has no
        /// <c>rampBoxes</c> in P1 (the Lead's note); it is deliberately kept out of
        /// <c>staticBoxes</c> as well, because a ramp registered as a box puts an invisible
        /// wall at its foot (TERRAIN_SUPPORT 7.7).
        /// </para>
        /// </remarks>
        private static bool ApplyStructures(PlacementDocument doc, Transform structuresRoot,
            PlacementImportReport report)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            int total = doc.structures == null ? 0 : doc.structures.Length;
            for (int i = 0; i < total; i++)
            {
                StructureEntry structure = doc.structures[i];
                wanted.Add(structure.id);

                Transform found = FindChild(structuresRoot, structure.id);
                GameObject current = found != null ? found.gameObject : null;
                if (current == null)
                {
                    current = CreateStructureObject(structure, structuresRoot);
                    if (current == null)
                    {
                        report.Error("structure '" + structure.id + "' could not be created");
                        return false;
                    }

                    report.StructuresCreated++;
                    ApplyStructure(structure, current, report);
                    continue;
                }

                if (!KindMatches(current, structure))
                {
                    // A role change between runs changes the object kind (marker vs cube), and
                    // leaving the old one would keep a collider the arena must not bake.
                    Undo.DestroyObjectImmediate(current);
                    current = CreateStructureObject(structure, structuresRoot);
                    if (current == null)
                    {
                        report.Error("structure '" + structure.id + "' could not be recreated");
                        return false;
                    }

                    report.StructuresUpdated++;
                    ApplyStructure(structure, current, report);
                    continue;
                }

                if (ApplyStructure(structure, current, report))
                {
                    report.StructuresUpdated++;
                }
            }

            List<string> stale = new List<string>();
            for (int i = 0; i < structuresRoot.childCount; i++)
            {
                Transform child = structuresRoot.GetChild(i);
                if (!wanted.Contains(child.name))
                {
                    stale.Add(child.name);
                }
            }

            stale.Sort(StringComparer.Ordinal);
            for (int i = 0; i < stale.Count; i++)
            {
                Transform doomed = FindChild(structuresRoot, stale[i]);
                if (doomed != null)
                {
                    Undo.DestroyObjectImmediate(doomed.gameObject);
                    report.StructuresDeleted++;
                }
            }

            return true;
        }

        private static bool IsMarkerRole(StructureEntry structure)
        {
            return string.Equals(structure.role, "ground", StringComparison.Ordinal) ||
                   string.Equals(structure.role, "boundary", StringComparison.Ordinal);
        }

        private static bool WantsBoxCollider(StructureEntry structure)
        {
            if (IsMarkerRole(structure))
            {
                return false;
            }

            return string.Equals(structure.collision, "box", StringComparison.Ordinal);
        }

        private static GameObject CreateStructureObject(StructureEntry structure, Transform structuresRoot)
        {
            GameObject created;
            if (IsMarkerRole(structure))
            {
                created = new GameObject(structure.id);
                Undo.RegisterCreatedObjectUndo(created, UndoLabel);
                Undo.SetTransformParent(created.transform, structuresRoot, UndoLabel);
                return created;
            }

            // A unit cube scaled to the authored size: a real collider for the player layer and
            // a visible body in the scene view. The mesh stays primitive on purpose - P2
            // structure art replaces the renderer, never the collider the arena baked.
            created = GameObject.CreatePrimitive(PrimitiveType.Cube);
            created.name = structure.id;
            Undo.RegisterCreatedObjectUndo(created, UndoLabel);
            Undo.SetTransformParent(created.transform, structuresRoot, UndoLabel);
            return created;
        }

        private static bool KindMatches(GameObject instance, StructureEntry structure)
        {
            bool isMarker = instance.GetComponent<Renderer>() == null;
            return isMarker == IsMarkerRole(structure);
        }

        /// <summary>
        /// Brings one structure object to its authored pose, scale and collider state.
        /// </summary>
        /// <returns>True when something had to change (so the idempotency report is honest).</returns>
        private static bool ApplyStructure(StructureEntry structure, GameObject instance,
            PlacementImportReport report)
        {
            float[] size = structure.size;
            Quaternion authored = ToQuaternion(structure.rotationQuat);
            Vector3 targetPosition = ToVector(structure.center);
            Vector3 targetScale = new Vector3(size[0], size[1], size[2]);
            Quaternion targetRotation = authored;

            if (string.Equals(structure.role, "boundary", StringComparison.Ordinal))
            {
                // The JSON states a boundary as a plane whose local +Y is the inward normal
                // (the same convention the validator uses). LevelArenaHost reads a wall's
                // local **+Z** as the normal, so the object is turned to look along it.
                Vector3 inward = authored * Vector3.up;
                if (Mathf.Abs(inward.y) > 0.99f)
                {
                    report.Error("boundary '" + structure.id + "' has a horizontal normal; a boundary " +
                                 "must be a vertical plane");
                    return false;
                }

                targetRotation = Quaternion.LookRotation(inward, Vector3.up);
                targetScale = Vector3.one;
            }
            else if (string.Equals(structure.role, "ground", StringComparison.Ordinal))
            {
                targetScale = Vector3.one;
            }

            bool wantsCollider = WantsBoxCollider(structure);
            BoxCollider collider = instance.GetComponent<BoxCollider>();
            bool poseMatches =
                Vector3.Distance(instance.transform.position, targetPosition) <= PositionToleranceM &&
                Mathf.Abs(Quaternion.Dot(instance.transform.rotation, targetRotation)) >=
                RotationDotTolerance &&
                Vector3.Distance(instance.transform.localScale, targetScale) <= PositionToleranceM;
            bool colliderMatches = wantsCollider ? collider != null : collider == null;

            if (poseMatches && colliderMatches)
            {
                return false;
            }

            if (!poseMatches)
            {
                Undo.RecordObject(instance.transform, UndoLabel);
                instance.transform.position = targetPosition;
                instance.transform.rotation = targetRotation;
                instance.transform.localScale = targetScale;
            }

            if (wantsCollider && collider == null)
            {
                // The collider sits on the scaled object, so a unit BoxCollider bakes exactly
                // the authored size; LevelArenaHost turns those bounds into a motor box.
                collider = Undo.AddComponent<BoxCollider>(instance);
            }

            if (!wantsCollider && collider != null)
            {
                Undo.DestroyObjectImmediate(collider);
                collider = null;
            }

            if (collider != null)
            {
                collider.center = Vector3.zero;
                collider.size = Vector3.one;
            }

            if (string.Equals(structure.role, "ramp", StringComparison.Ordinal))
            {
                report.Note("ramp '" + structure.id + "' has its BoxCollider, but LevelArenaHost has no " +
                            "rampBoxes in P1: it is not registered with the motor yet (P3)");
            }

            return true;
        }

        // ---------------------------------------------------------------- sync ---

        private static bool TrySyncPickupField(Transform levelRoot, string arenaId, Transform propsRoot,
            IDictionary<string, int> declaredValueUnits, PlacementImportReport report)
        {
            PickupFieldHost[] hosts;
            if (!string.IsNullOrEmpty(arenaId))
            {
                // Cell mode: only a host INSIDE this cell's subtree may be synced. Picking the first
                // host in the scene would make a field of cells write every cell's prop rows into
                // one host - and, worse, wipe that host's arrays when this cell has no props.
                hosts = levelRoot.GetComponentsInChildren<PickupFieldHost>(true);
                if (hosts == null || hosts.Length == 0)
                {
                    string message = "no PickupFieldHost under " + HierarchyPath(levelRoot) +
                                     ": nothing was synced. A cell that declares props must own its " +
                                     "host (LevelContent/" + arenaId + "/PickupField)";
                    if (propsRoot.childCount > 0)
                    {
                        // Silent failure mode: the props exist as geometry but no gameplay row, so
                        // nobody can absorb them. Loud on purpose when there is something to lose.
                        report.Error(message);
                    }
                    else
                    {
                        report.Note(message + " - this cell has no props, so there is nothing to sync");
                    }

                    return false;
                }
            }
            else
            {
                hosts = UnityEngine.Object.FindObjectsOfType<PickupFieldHost>();
                if (hosts == null || hosts.Length == 0)
                {
                    report.Note("no PickupFieldHost in the scene: the props were imported but the host " +
                                "arrays were not synced (add the host, then run VRC Katamari/Pickup/" +
                                "Sync Prop Arrays From Children)");
                    return false;
                }
            }

            Array.Sort(hosts, (left, right) => string.CompareOrdinal(HierarchyPath(left.transform),
                HierarchyPath(right.transform)));
            PickupFieldHost host = hosts[0];

            if (hosts.Length > 1)
            {
                report.Note("found " + hosts.Length + " PickupFieldHost" +
                            (string.IsNullOrEmpty(arenaId) ? " in the scene" : " under " +
                             HierarchyPath(levelRoot)) + "; syncing " + HierarchyPath(host.transform) +
                            " (the first in hierarchy order). A cell must own exactly one host");
            }

            if (!propsRoot.IsChildOf(host.transform) && host.transform != propsRoot)
            {
                report.Note("the host " + HierarchyPath(host.transform) + " is outside " +
                            HierarchyPath(propsRoot) + "; the sync is still pointed at the managed props " +
                            "root, but hosting the field under the level root is the wiring the design " +
                            "assumes (LEVEL_AUTHORING 7.4)");
            }

            PickupFieldSyncReport sync = PickupFieldSync.SyncFromChildren(host, propsRoot,
                declaredValueUnits);
            report.SyncRan = true;
            report.SyncPropCount = sync.PropCount;
            report.SyncRejected = sync.Rejected.Count;
            report.UdonCopyIssued = sync.UdonCopyIssued;
            report.ValueUnitsPreserved = sync.ValueUnitsPreserved;
            report.ValueUnitsUndeclared = sync.ValueUnitsUndeclared;
            report.ValueUnitsVerified = sync.ValueUnitsVerified;

            for (int i = 0; i < sync.ValueUnitMismatches.Count && i < 20; i++)
            {
                report.ValueUnitProblems.Add(sync.ValueUnitMismatches[i]);
            }

            if (!sync.Success)
            {
                report.Error("PickupFieldSync failed: " + sync.Failure);
                return false;
            }

            for (int i = 0; i < sync.Rejected.Count && i < 10; i++)
            {
                report.Error("sync rejected " + sync.Rejected[i].Path + ": " + sync.Rejected[i].Reason);
            }

            return true;
        }

        // --------------------------------------------------------------- arena ---

        /// <summary>
        /// Fills <see cref="LevelArenaHost"/>'s structure arrays from the objects just built.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The fields and their meanings are the component's, not this tool's:
        /// <c>groundNormal/groundOffset</c> for the floor, <c>boundaryWalls</c> (one Transform
        /// per wall, local +Z pointing into the field, origin on the collision plane),
        /// <c>staticBoxes</c> (BoxColliders whose bounds the host bakes once), and
        /// <c>allowedKinds</c> (the whitelist the motor receives). A ramp is deliberately not
        /// in <c>staticBoxes</c> - the component has no ramp slot yet.
        /// </para>
        /// <para>
        /// Writing an UdonSharp behaviour is a two-step contract: fill the proxy's serialized
        /// fields, then copy the proxy into the backing <c>UdonBehaviour</c>; the copy is what
        /// makes the values exist at runtime. The same sequence as
        /// <see cref="PickupFieldSync"/>.
        /// </para>
        /// </remarks>
        private static void BindArenaHost(PlacementDocument doc, Transform levelRoot,
            Transform structuresRoot, PlacementImportReport report)
        {
            string arenaId = ArenaIdOf(doc);
            LevelArenaHost host;
            if (!string.IsNullOrEmpty(arenaId))
            {
                // Cell mode: the arena lives on the cell's own root, so two cells can never
                // register each other's geometry.
                host = levelRoot.GetComponent<LevelArenaHost>();
                if (host == null)
                {
                    report.Note("no LevelArenaHost on " + HierarchyPath(levelRoot) + ": the cell's " +
                                "structures were imported, but its arena arrays were not filled. Add the " +
                                "component to that object, then import again");
                    return;
                }
            }
            else
            {
                host = UnityEngine.Object.FindObjectOfType<LevelArenaHost>();
                if (host == null)
                {
                    report.Note("no LevelArenaHost in the scene: the structures were imported but the " +
                                "arena arrays were not filled (add the component, then import again)");
                    return;
                }
            }

            var serialized = new SerializedObject(host);
            SerializedProperty groundNormal = serialized.FindProperty(GroundNormalField);
            SerializedProperty groundOffset = serialized.FindProperty(GroundOffsetField);
            SerializedProperty walls = serialized.FindProperty(BoundaryWallsField);
            SerializedProperty boxes = serialized.FindProperty(StaticBoxesField);
            SerializedProperty kinds = serialized.FindProperty(AllowedKindsField);

            if (groundNormal == null || groundOffset == null || walls == null || boxes == null ||
                kinds == null)
            {
                report.Error("LevelArenaHost is missing one of " + GroundNormalField + "/" +
                             GroundOffsetField + "/" + BoundaryWallsField + "/" + StaticBoxesField +
                             "/" + AllowedKindsField + "; the arena arrays were not filled");
                return;
            }

            var wallList = new List<Transform>();
            var boxList = new List<BoxCollider>();
            Vector3 floorNormal = Vector3.up;
            float floorOffset = 0f;
            bool hasGround = false;
            var kindList = new List<string>();
            var seenKinds = new HashSet<string>(StringComparer.Ordinal);

            if (doc.structures != null)
            {
                for (int i = 0; i < doc.structures.Length; i++)
                {
                    StructureEntry structure = doc.structures[i];
                    Transform found = FindChild(structuresRoot, structure.id);
                    if (found == null)
                    {
                        continue;
                    }

                    if (string.Equals(structure.role, "boundary", StringComparison.Ordinal))
                    {
                        wallList.Add(found);
                        continue;
                    }

                    if (string.Equals(structure.role, "ground", StringComparison.Ordinal))
                    {
                        Vector3 normal = ToQuaternion(structure.rotationQuat) * Vector3.up;
                        floorNormal = normal;
                        floorOffset = Vector3.Dot(normal, ToVector(structure.center));
                        hasGround = true;
                        continue;
                    }

                    if (string.Equals(structure.role, "ramp", StringComparison.Ordinal))
                    {
                        continue;   // no ramp slot in LevelArenaHost yet (P3)
                    }

                    BoxCollider collider = found.GetComponent<BoxCollider>();
                    if (collider != null)
                    {
                        boxList.Add(collider);
                    }
                }
            }

            if (doc.props != null)
            {
                for (int i = 0; i < doc.props.Length; i++)
                {
                    string kind = DeclaredKind(doc, doc.props[i]);
                    if (!string.IsNullOrEmpty(kind) && seenKinds.Add(kind))
                    {
                        kindList.Add(kind);
                    }
                }
            }

            kindList.Sort(StringComparer.Ordinal);
            wallList.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
            boxList.Sort((left, right) => string.CompareOrdinal(left.name, right.name));

            groundNormal.vector3Value = floorNormal;
            groundOffset.floatValue = floorOffset;

            walls.arraySize = wallList.Count;
            for (int i = 0; i < wallList.Count; i++)
            {
                walls.GetArrayElementAtIndex(i).objectReferenceValue = wallList[i];
            }

            boxes.arraySize = boxList.Count;
            for (int i = 0; i < boxList.Count; i++)
            {
                boxes.GetArrayElementAtIndex(i).objectReferenceValue = boxList[i];
            }

            kinds.arraySize = kindList.Count;
            for (int i = 0; i < kindList.Count; i++)
            {
                kinds.GetArrayElementAtIndex(i).stringValue = kindList[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(host);
            if (!hasGround)
            {
                report.Note("no role=ground structure was found; the arena keeps its own ground plane");
            }

            try
            {
                UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
                UdonSharpEditorUtility.CopyProxyToUdon(host);
            }
            catch (Exception exception)
            {
                report.Error("CopyProxyToUdon threw for LevelArenaHost: " + exception.Message +
                             ". The arena fields are set on the proxy but not on the backing " +
                             "UdonBehaviour; run the import again once the compile has finished");
            }

            report.ArenaWired = true;
            report.ArenaBoxes = boxList.Count;
            report.ArenaRamps = 0;
            report.ArenaPlanes = wallList.Count;
            report.ArenaKinds = kindList.Count;
        }

        /// <summary>The cell id when this document is one arena cell, else null.</summary>
        private static string ArenaIdOf(PlacementDocument doc)
        {
            if (doc == null || doc.level == null || doc.level.arena == null) return null;
            if (string.IsNullOrEmpty(doc.level.arena.id)) return null;
            return doc.level.arena.id;
        }

        // --------------------------------------------------------------- paths ---

        /// <summary>
        /// <c>&lt;repo&gt;/Docs/Design/Levels/&lt;levelId&gt;/placement.json</c>, found by walking
        /// up from the project folder so the checkout layout stays free.
        /// </summary>
        private static bool TryResolvePlacementPath(string levelId, out string path, out List<string> tried)
        {
            tried = new List<string>();
            path = null;
            if (string.IsNullOrEmpty(levelId))
            {
                return false;
            }

            string relative = LevelsRepoRelativeFolder + "/" + levelId + "/" + PlacementFileName;
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            var candidates = new List<string>
            {
                Path.GetFullPath(Path.Combine(projectRoot, "..", relative)),
                Path.GetFullPath(Path.Combine(projectRoot, "..", "..", relative)),
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative)),
            };

            string directory = projectRoot;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(directory); i++)
            {
                candidates.Add(Path.GetFullPath(Path.Combine(directory, "..", relative)));
                directory = Path.GetDirectoryName(directory);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (!tried.Contains(candidate))
                {
                    tried.Add(candidate);
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Every level whose <c>level.scene</c> (or, failing that, whose directory name) names the
        /// open scene. Searches one level of nesting, because a field keeps one arena document per
        /// cell (<c>Levels/physics_test_field/C14/placement.json</c>).
        /// </summary>
        private static bool TryResolveLevelsForActiveScene(out List<string> matches,
            out List<string> seen, out string failure)
        {
            matches = new List<string>();
            seen = new List<string>();
            failure = null;
            string active = EditorSceneManager.GetActiveScene().name;

            string folder = ResolveLevelsFolder();
            if (folder == null || !Directory.Exists(folder))
            {
                failure = "cannot find " + LevelsRepoRelativeFolder + " next to the Unity project; pass a " +
                          "level id to PlacementImporter.Import(string) instead";
                return false;
            }

            CollectLevels(folder, "", matches, seen, active);

            if (matches.Count > 0)
            {
                return true;
            }

            failure = "no level under " + LevelsRepoRelativeFolder + " names the scene '" + active +
                      "'; levels found: " + string.Join(", ", seen.ToArray());
            return false;
        }

        /// <summary>
        /// Walks one level of directories. A directory that has a placement.json is a level; a
        /// directory without one is a field folder and is walked once more.
        /// </summary>
        private static void CollectLevels(string folder, string prefix, List<string> matches,
            List<string> seen, string active)
        {
            string[] directories = Directory.GetDirectories(folder);
            Array.Sort(directories, StringComparer.Ordinal);
            for (int i = 0; i < directories.Length; i++)
            {
                string name = Path.GetFileName(directories[i]);
                string placement = Path.Combine(directories[i], PlacementFileName);
                string id = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                if (File.Exists(placement))
                {
                    seen.Add(id);
                    string scene = ReadSceneOf(placement);
                    if (string.Equals(scene, active, StringComparison.Ordinal) ||
                        (string.IsNullOrEmpty(scene) && string.Equals(name, active, StringComparison.Ordinal)))
                    {
                        matches.Add(id);
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(prefix))
                {
                    CollectLevels(directories[i], id, matches, seen, active);
                }
            }
        }

        /// <summary>True when that level's document declares one arena cell.</summary>
        private static bool IsArenaLevel(string levelId)
        {
            string path;
            List<string> tried;
            if (!TryResolvePlacementPath(levelId, out path, out tried))
            {
                return false;
            }

            try
            {
                var doc = JsonUtility.FromJson<PlacementDocument>(File.ReadAllText(path));
                return doc != null && doc.level != null && doc.level.arena != null &&
                       !string.IsNullOrEmpty(doc.level.arena.id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadSceneOf(string placementPath)
        {
            try
            {
                var doc = JsonUtility.FromJson<PlacementDocument>(File.ReadAllText(placementPath));
                if (doc != null && doc.level != null)
                {
                    return doc.level.scene;
                }
            }
            catch (Exception)
            {
                // A broken file is the CLI's problem; the menu only needs the level list.
            }

            return null;
        }

        private static string ResolveLevelsFolder()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string directory = projectRoot;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, "..", LevelsRepoRelativeFolder));
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                candidate = Path.GetFullPath(Path.Combine(directory, LevelsRepoRelativeFolder));
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        // ------------------------------------------------------------ managed ---

        private static bool FindOrCreateManagedRoot(PlacementImportReport report, out Transform root)
        {
            root = null;
            Scene scene = EditorSceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();
            GameObject existing = null;
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, ManagedRootName, StringComparison.Ordinal))
                {
                    existing = roots[i];
                    break;
                }
            }

            if (existing == null)
            {
                existing = new GameObject(ManagedRootName);
                Undo.RegisterCreatedObjectUndo(existing, UndoLabel);
                Undo.SetTransformParent(existing.transform, null, UndoLabel);
                report.Note("created the managed root " + ManagedRootName + " at the scene root");
            }

            root = existing.transform;
            if (root.parent != null)
            {
                report.Fail(ManagedRootName + " exists but is not a scene root (" + HierarchyPath(root) +
                            "); the managed subtree must not be nested under someone else's object");
                return false;
            }

            return true;
        }

        private static Transform EnsureFolder(Transform parent, string name)
        {
            Transform existing = FindChild(parent, name);
            if (existing != null)
            {
                return existing;
            }

            var folder = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(folder, UndoLabel);
            Undo.SetTransformParent(folder.transform, parent, UndoLabel);
            return folder.transform;
        }

        private static Transform FindManagedChild(string name)
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, ManagedRootName, StringComparison.Ordinal))
                {
                    Transform child = FindChild(roots[i].transform, name);
                    return child;
                }
            }

            return null;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private static void DeleteEmptyFolders(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                DeleteEmptyFolders(child);
                if (child.childCount == 0 && child.GetComponent<Renderer>() == null &&
                    child.GetComponent<Collider>() == null)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                }
            }
        }

        private static void MarkSceneDirty()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // ----------------------------------------------------------- utilities ---

        private static bool IsVector(float[] values, int length)
        {
            if (values == null || values.Length < length)
            {
                return false;
            }

            for (int i = 0; i < length; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static Vector3 ToVector(float[] values)
        {
            return new Vector3(values[0], values[1], values[2]);
        }

        private static Quaternion ToQuaternion(float[] values)
        {
            // The JSON is Unity order (x, y, z, w). Unity accepts either sign; normalising the
            // sign here keeps the pose comparison stable (q and -q are one rotation).
            float x = values[0];
            float y = values[1];
            float z = values[2];
            float w = values[3];
            if (w < 0f)
            {
                x = -x;
                y = -y;
                z = -z;
                w = -w;
            }

            return new Quaternion(x, y, z, w);
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
    }
}
