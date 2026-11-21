using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using VRCKatamari.BallContact;
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
    /// Hierarchy: <c>LevelContent/Props/&lt;kind&gt;/[&lt;zone&gt;/][Groups/&lt;group&gt;/]&lt;itemId&gt;</c>.
    /// The first level <b>must</b> be the kind folder: <see cref="PickupFieldSync"/> reads the
    /// kind from the folder directly under the field root it is given, and the importer gives
    /// it <c>LevelContent/Props</c>. Everything below the kind folder is for human
    /// readability only; poses are world-space.
    /// </para>
    /// <para>
    /// Containers are a separate namespace from props, and that is a correctness rule, not
    /// tidiness. A <c>groupId</c> may equal another prop's <c>itemId</c> (the reference level
    /// has eight such cases), and the previous layout resolved a container with
    /// "the child called <c>&lt;group&gt;</c>", which found that PROP and parented the group's
    /// children inside it. The pickup sync's rule is "the topmost object with a Renderer is
    /// one prop and its subtree is that prop's visual", so the swallowed children never
    /// became rows (648 props imported, 640 rows), the declared value total did not read
    /// back, and every re-import re-created them because they were invisible to the
    /// importer's own walk too (<c>+8</c> each run). Two rules remove the class:
    /// <list type="number">
    /// <item><description>a group container is <c>Groups/&lt;group&gt;</c>, so its name is
    /// resolved under a folder no prop is ever a child of;</description></item>
    /// <item><description><see cref="EnsureFolder"/> never returns a child that carries a
    /// <see cref="Renderer"/> (that child is a prop, not a container): a name collision is
    /// reported as an error instead of silently re-parenting into a prop, and the props of
    /// that collision are left alone for the operator to fix.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Identity: one stable key per instance, the <c>itemId</c> under the level content
    /// root. That is why the walk (<see cref="IndexProps"/>) looks for wanted ids even
    /// inside an object that carries a Renderer: a scene imported before the fix has props
    /// nested in a prop, and those are found, moved to their owning folder and counted as
    /// updates instead of being duplicated (<c>+0</c> on the run after one repair).
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
    /// Structure bodies (2026-11-15). A <c>ground</c> or <c>boundary</c> entry stays a bare
    /// marker Transform, because <see cref="LevelArenaHost"/> reads the marker's world position
    /// and (for a wall) its local +Z, and nothing else - a Renderer or a
    /// <see cref="BoxCollider"/> on the marker itself would change what the arena bakes and how
    /// a marker is recognized as a marker. The body the player stands on and bumps into is a
    /// <c>Visual</c> child carrying a cube <see cref="MeshRenderer"/> and a
    /// <see cref="BoxCollider"/>, with its visible face on the marker's plane. See
    /// <see cref="ApplyStructureVisuals"/>.
    /// </para>
    /// <para>
    /// Curved ramps (CONTACT_MODEL_V2 5.7). A <c>role=curve_ramp</c> entry is ONE structure with a
    /// <c>curve</c> block (radius, arc, width, thickness) and no authored box: it is expanded at
    /// import time into <c>segments</c> slab cubes named <c>&lt;id&gt;_&lt;nn&gt;</c> under a
    /// collider-less parent, each with a <see cref="BoxCollider"/>, and every segment collider is
    /// wired into <c>LevelArenaHost.rampBoxes</c> exactly like a straight ramp. The runtime
    /// therefore sees only OBBs (its window, capacity and solver are untouched), the operator can
    /// select and inspect each segment, and the JSON keeps the one authored arc. The expansion
    /// maths is <see cref="CurveRampMath"/> - the same tessellator as the Python reference - so
    /// the importer never re-derives an arc.
    /// </para>
    /// <para>
    /// Assembly placement: <c>Editor/</c> compiles into the predefined
    /// <c>Assembly-CSharp-Editor</c> (see the remark in <c>PickupFieldSync.cs</c>), which is
    /// why the UdonSharp editor usings below resolve without an asmdef.
    /// </para>
    /// <para>
    /// Two files, two schemas. <c>PlacementImporter.V2.cs</c> is the other half of this
    /// partial class: v1's document shape, scene builder and identity rules stay here, and the
    /// v2 branch (schema sniffing, the v2-to-v1 view adapter, the activity-wall layer, the
    /// meta wiring and the terrain hand-off) lives there. Both halves call the SAME build
    /// steps - props, structures, colliders, pickup sync, arena binding - so no layer has a
    /// second implementation.
    /// </para>
    /// <para>
    /// 删除道具与结构按导入身份；空容器清理只认导入器在创建时留下的标记，
    /// 不按节点外观猜测，也不进入 prefab 实例内部。
    /// </para>
    /// </remarks>
    public static partial class PlacementImporter
    {
        public const string MenuPath = "VRC Katamari/Level/Import Placement (LevelContent)";

        public const string ManagedRootName = "LevelContent";
        public const string PropsFolderName = "Props";
        public const string StructuresFolderName = "Structures";
        // The reserved parent of every group container (see the class remarks). A prop is
        // never a child of this folder, so a group id can never resolve to a prop, even when
        // the id equals another prop's itemId.
        public const string GroupsFolderName = "Groups";
        // v2's parent pointer fills this reserved container (owner ruling A): a folder per
        // parent id, so the hierarchy is visible in the scene while the prop itself stays a
        // top-level row for PickupFieldSync. Never a prop name, for the same reason Groups is
        // reserved.
        public const string HierarchyFolderName = "Hierarchy";
        // The folder that holds one parent's children is NOT called the parent's id: a
        // by-name lookup ("find coconut_crab_009") would hit the identity, Renderer-less folder
        // before - or instead of - the prop, and read its (0,0,0) pose as the prop's.
        public const string HierarchyChildrenSuffix = "_children";

        // Repo-relative, like the greybox builder's payload: the JSON lives next to the
        // Unity project, not inside Assets/, so it can be diffed and validated by the CLI.
        public const string LevelsRepoRelativeFolder = LevelPaths.LevelsRepoRelativeFolder;
        public const string PlacementFileName = LevelPaths.PlacementFileName;

        private const string PropFolderMarkerName = "__PlacementImporterFolder";
        private const string PropFolderMarkerTag = "EditorOnly";

        private const string UndoLabel = "Import Level Placement";
        private const float PositionToleranceM = 0.001f;
        private const float RotationDotTolerance = 0.99999f;

        // A curve_ramp expands into `segments` slab cubes named "<id>_<nn>" under its own
        // parent, so one authored arc is one structure object plus N colliders
        // (CONTACT_MODEL_V2 5.7). CurveMinBallRadiusM is the design's r_min (the 0.30 m
        // starter ball), used when the level does not declare ballDiameterStart.
        private const float CurveMinBallRadiusM = 0.15f;
        private const float CurveMaxArcDeg = 360f;
        private const string CurveSegmentSeparator = "_";
        /// <summary>CONTACT_MODEL_V2 5.4/7.1: the motor's static OBB table holds this many.</summary>
        private const int StaticObbCapacity = 256;

        private const string PropsArtFolder = "Assets/VRC_Katamari/Art/Props";

        // One placeholder material per structure role, from the greybox palette the preview
        // already uses. Paths, not GUIDs: the path is readable here, and a move is a one-line
        // edit whose failure mode is a named report error instead of a silent white body.
        private const string GroundMaterialPath =
            "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundGround.mat";
        private const string WallMaterialPath =
            "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundWall.mat";
        private const string PropMaterialPath =
            "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundProp.mat";

        // The visible child under a ground/boundary marker. The marker itself stays bare (see
        // the class remark), so this name is the only handle the tool has on the body.
        private const string StructureVisualName = "Visual";

        // Unity names a primitive cube's built-in material "Default-Material" (older projects
        // also ship "Default-Diffuse"). A body still carrying one has no material decision of
        // its own, so the role default may replace it; any other material is hand art.
        private const string BuiltinDefaultMaterialName = "Default-Material";
        private const string BuiltinDefaultDiffuseMaterialName = "Default-Diffuse";

        // LevelArenaHost's serialized field names (Runtime/Story/LevelArenaHost.cs). They are
        // private, so they are looked up by name exactly like PickupFieldSync does with the
        // host's arrays; a rename in Runtime/ fails loudly here instead of silently wiring
        // nothing.
        private const string GroundNormalField = "groundNormal";
        private const string GroundOffsetField = "groundOffset";
        private const string BoundaryWallsField = "boundaryWalls";
        private const string StaticBoxesField = "staticBoxes";
        private const string RampBoxesField = "rampBoxes";
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
            /// <summary>
            /// The smallest ball the level plays with. A curved ramp's chord budget is a
            /// fraction of its radius (CONTACT_MODEL_V2 5.7), so the auto segment count
            /// depends on it; missing means the 0.30 m starter ball.
            /// </summary>
            public float ballDiameterStart = 0.3f;
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
            /// <summary>
            /// "own" (or absent): the document carries its own floor, exactly one role=ground.
            /// "shared": the cell stands on a shared frame (the physics test field's sandbox) and
            /// carries no role=ground and no role=boundary; see <see cref="FrameRuleViolation"/>.
            /// </summary>
            public string frame;
        }

        public const string FrameOwn = "own";
        public const string FrameShared = "shared";

        /// <summary>
        /// The ground/boundary rule for a document's <c>level.arena.frame</c>, or null when it
        /// holds. Pure (no scene, no asset), so the EditMode test can pin it, and the same rule
        /// as <c>placement_core._check_structures</c>.
        /// </summary>
        public static string FrameRuleViolation(string frame, int grounds, int boundaries)
        {
            if (string.Equals(frame, FrameShared, StringComparison.Ordinal))
            {
                if (grounds == 0 && boundaries == 0) return null;
                return "level.arena.frame is 'shared': this cell must not carry role=ground or " +
                       "role=boundary (found ground=" + grounds + " boundary=" + boundaries +
                       "); the shared frame owns them";
            }

            if (!string.IsNullOrEmpty(frame) && !string.Equals(frame, FrameOwn, StringComparison.Ordinal))
            {
                return "level.arena.frame '" + frame + "' must be 'own' or 'shared'";
            }

            if (grounds != 1)
            {
                return "structures must contain exactly one role=ground (found " + grounds + ")";
            }

            return null;
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
            /// <summary>
            /// Optional Unity-relative <c>.fbx</c> path under <c>Assets/</c>, e.g.
            /// <c>Assets/ExternalPack/Models/mug/mug.fbx</c>. When present it wins over the
            /// repo prop at <c>Art/Props/&lt;def&gt;/&lt;def&gt;.fbx</c>, so a def is no longer
            /// defined by its id (an external asset pack, or an id that collides with one of
            /// ours).
            /// </summary>
            public string model;
            /// <summary>
            /// This def's declared value units (Docs/OPEN_DECISIONS.md (q) U2). 64-bit: the host
            /// column is (r) step B, so a level may declare a value past <c>int.MaxValue</c>.
            /// </summary>
            public long valueUnits;
            /// <summary>
            /// The original's per-item volume factor k (<c>Volume = k * bbox</c>), absent
            /// (0) means the default 0.70. JSON <c>defs[].volumeK</c>; the gate prices the
            /// item with it (<c>Req = floor(1000*cbrt(60*k*a*b*c/pi))</c>). Absent and a
            /// declared 0 are the same thing here because 0 is outside the legal range, so
            /// the importer fills <c>PickupFieldAuthoringRules.DefaultVolumeK</c> for both
            /// and never writes a default back to the JSON.
            /// </summary>
            public float volumeK;
            /// <summary>
            /// The per-def growth penalty (absent/0 = 0.80). Original per-item values exist;
            /// only the default is used this round (FIXED_POINT_STEP_CD section 8).
            /// </summary>
            public float volumePenalty;
            /// <summary>
            /// Optional collider proxy (COLLIDER_PROXY.md). JsonUtility has no polymorphic
            /// object support and no jagged arrays, so the hull is a FLAT list of 3N numbers
            /// (`[x0,y0,z0, x1,y1,z1, ...]`), not an array of [x,y,z] points.
            /// </summary>
            public ColliderEntry collider;
        }

        [Serializable]
        public class ColliderEntry
        {
            public string type;
            public float[] center;
            public float[] size;
            public float radius;
            public float halfHeight;
            public string axis;
            public float[] hull;
            public string source;
            public float fit_error;
        }

        [Serializable]
        public class PropEntry
        {
            [NonSerialized] public bool noBallCollision;
            public string itemId;
            public string def;
            public string kind;
            public string zone;
            /// <summary>
            /// v1's visual attachment group (<c>Props/&lt;kind&gt;/&lt;zone&gt;/Groups/&lt;group&gt;/</c>).
            /// v2 has no such key: its <c>rigidGroup</c> is mapped onto this slot, and its
            /// <c>parent</c> gets the reserved <c>Hierarchy/</c> folder instead (see
            /// <see cref="EnsurePropFolder"/>).
            /// </summary>
            public string group;
            /// <summary>
            /// v2's single parent pointer (owner ruling A). The importer builds a
            /// <c>Hierarchy/&lt;parent&gt;_children/</c> FOLDER and keeps the prop a top-level row:
            /// parenting one prop's GameObject under another prop's GameObject would hide the
            /// child from the pickup sync's "topmost Renderer is one row" rule and lose its row.
            /// The edge itself is preserved for the future <c>propParent</c> column.
            /// </summary>
            public string parent;
            /// <summary>v2's <c>behaviour</c> (null for a v1 document, i.e. static).</summary>
            public string behaviour;
            /// <summary>v2's <c>inherit</c> (how a parent's motion reaches this prop).</summary>
            public string inherit;
            /// <summary>
            /// v2's authored <c>scale</c> (null = 1,1,1). It is <b>already</b> multiplied into
            /// <see cref="aabb"/> by the writer (§6.2/§18), so the importer applies it to the
            /// Transform (rendering) and to the collider proxy only - never to the box again.
            /// </summary>
            public float[] scale;
            /// <summary>
            /// v2's <c>absorbable</c> declaration. Stored and reported, not enforced: the
            /// pickup host has no absorbable column and the gate decides absorbability from
            /// k * aabb, so honouring it would need a runtime column (deferred, owner ruling D).
            /// </summary>
            public bool absorbable = true;
            public float[] position;
            public float[] rotationQuat;
            public float[] aabb;
            public string support;
            public string stackedOn;
            /// <summary>
            /// DEPRECATED per-prop value units (accepted, ignored for growth).
            /// </summary>
            public long valueUnits;
            /// <summary>
            /// The prop's DERIVED growth in mm^3, written by the canonical writer
            /// (<c>round(penalty*k*a*b*c*1e9)</c>) and never hand-typed. This is the
            /// AUTHORITY the sync writes into <c>PickupFieldHost.propGrowthMm3</c>: the
            /// Python writer computes it in double from the authored aabb, and the scene's
            /// measured bounds are only a VERIFICATION (they cannot reproduce the authored
            /// number bit-for-bit). 0 = absent (an old file): the importer prices it from the
            /// aabb and reports the fallback.
            /// </summary>
            public long growthMm3;
            /// <summary>
            /// Per-prop override of the def's <c>volumeK</c> (0 = not declared). Rare by
            /// design: the factor is a property of the TYPE, so the def carries it; the
            /// override exists for the one instance whose authored body differs.
            /// </summary>
            public float volumeK;
            /// <summary>Per-prop growth penalty override (0 = not declared, use 0.80).</summary>
            public float volumePenalty;
            public string notes;
        }

        [Serializable]
        public class StructureEntry
        {
            [NonSerialized] public bool noBallCollision;
            public string id;
            public string kind;
            public string role;
            public string shape;
            public float[] center;
            public float[] size;
            /// <summary>Present only on <c>role=curve_ramp</c> (a straight box has no curve).</summary>
            public CurveEntry curve;
            public float[] rotationQuat;
            public string collision;
            public bool support;
            public string meets;
            /// <summary>
            /// v2's contact proxy for a <c>shape:"proxy"</c> solid (same encoding as
            /// <c>defs[].collider</c>). Null for every other shape; <see cref="contactIsProxy"/>
            /// is what the scene builder branches on.
            /// </summary>
            public ColliderEntry collider;
            /// <summary>v2's <c>proxySource</c> (def/aabb-fallback/manual); null for v1.</summary>
            public string proxySource;
            /// <summary>
            /// True when this solid's contact shape comes from the proxy rather than from
            /// <see cref="size"/>. The two derived numbers below are already the metres-space
            /// contact box (§4), computed from the v2 document by
            /// <see cref="PlacementV2Rules.TrySolidContactBox"/> - the view carries the answer so
            /// the scene builder never re-derives geometry.
            /// </summary>
            public bool contactIsProxy;
            public float[] contactCenter;
            public float[] contactSize;
            /// <summary>
            /// A split proxy solid's part boxes (§4 <c>parts</c>), metres in the solid's frame,
            /// as flat <c>3N</c> centres and sizes; null for a one-box solid. When present the
            /// object carries one <see cref="BoxCollider"/> per part, in this order, instead of
            /// the one contact box, and every one of them goes into the arena's static table.
            /// </summary>
            public float[] contactPartCenters;
            public float[] contactPartSizes;
            /// <summary>
            /// True when a prop with this solid's own id carries the visible model, so the solid
            /// is only collision: the cube's <see cref="Renderer"/> is switched off (the
            /// <see cref="BoxCollider"/> stays). Set by the v2 view for a <c>shape:"proxy"</c>
            /// solid whose id is also a <c>props[].itemId</c> (the reference level's 35 solids
            /// are all such pairs); a solid without a prop keeps its placeholder body.
            /// </summary>
            public bool bodyIsProp;
        }

        /// <summary>
        /// The <c>curve</c> block: one vertical arc, expanded at import time into slabs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>CONTACT_MODEL_V2</c> 5.7: a curved ramp is authored once - radius, arc angle,
        /// width, thickness - and the importer expands it into <c>segments</c> slab cubes
        /// under a named parent, so the runtime sees only OBBs. The maths is
        /// <see cref="CurveRampMath.TryTessellateVerticalCurve"/>, the same tessellator as
        /// the Python reference (<c>Docs/Design/BallMotor/contact_v2</c>); this class only
        /// carries the six authored numbers.
        /// </para>
        /// <para>
        /// <c>center</c>/<c>rotationQuat</c> on the structure are the arc's <b>start pose</b>:
        /// the centre of the first slab, with local +Z the direction the arc leaves in and
        /// local +Y the start surface normal (half a thickness under the walking surface,
        /// exactly like a straight ramp's box centre).
        /// </para>
        /// </remarks>
        [Serializable]
        public class CurveEntry
        {
            public float radius_m;
            public float arc_deg;
            /// <summary>0 (or absent) = auto: the smallest count that fits the chord budget.</summary>
            public int segments;
            /// <summary>"up" (default) or "down".</summary>
            public string bend;
            public float width_m;
            public float thickness_m;
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
                // The list of paths actually tried is the difference between a one-line fix
                // and a round trip: the nested-id and repository-root defects both presented
                // as "not found" with a single, plausible-looking path.
                report.Fail("no placement document for level '" + levelId + "'. Searched:\n  " +
                            string.Join("\n  ", tried.ToArray()));
                return false;
            }

            report.PlacementPath = path;

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                report.Fail("cannot read " + path + ": " + exception.Message);
                return false;
            }

            // Schema sniff on the text we already have: a v2 document goes through the v2
            // reader and its own branch, a v1 document keeps the JsonUtility path it always
            // had (so the v1 import stays byte-for-byte what it was).
            if (PlacementV2Reader.LooksLikeV2(text))
            {
                PlacementV2ReadResult v2 = PlacementV2Reader.Read(text);
                if (v2.Document != null &&
                    string.Equals(v2.Document.Schema, PlacementV2Reader.SchemaName,
                        StringComparison.Ordinal))
                {
                    return ImportV2(levelId, path, v2, report);
                }
            }

            PlacementDocument doc;
            try
            {
                doc = JsonUtility.FromJson<PlacementDocument>(text);
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
                levelRoot = EnsureFolder(managedRoot, arenaId, report);
                if (levelRoot == null)
                {
                    report.Fail("the cell folder '" + arenaId + "' collides with an object that is " +
                                "not a folder; rename the cell in the level data");
                    return false;
                }

                report.ArenaId = arenaId;
            }

            report.ManagedRootPath = HierarchyPath(levelRoot);

            Transform propsRoot = EnsureFolder(levelRoot, PropsFolderName, report);
            Transform structuresRoot = EnsureFolder(levelRoot, StructuresFolderName, report);
            if (propsRoot == null || structuresRoot == null)
            {
                report.Fail("the managed props/structures folder collides with an object that is " +
                            "not a folder; the managed subtree was not imported");
                return false;
            }

            bool propsOk = ApplyProps(doc, propsRoot, report);
            if (propsOk)
            {
                // The sync's row rule is "topmost Renderer wins", so a prop that owns another
                // prop is a lost row. The importer must never report a run that loses one; the
                // check names the ids instead of letting 640-of-648 reach the sync.
                propsOk = VerifyPropRows(doc, propsRoot, report);
            }
            bool structuresOk = ApplyStructures(doc, structuresRoot, report);

            if (report.PoseScaleCompensated > 0)
            {
                report.Note(report.PoseScaleCompensated + " prop(s) sit under a container whose scale " +
                            "is not 1: their local scale was compensated so every prop's WORLD size " +
                            "is the document's aabb. The containers are named in the notes above; a " +
                            "clean managed tree has none");
            }

            DeleteEmptyFolders(propsRoot, report);

            MarkSceneDirty();

            // The pickup sync runs after the props exist, because it derives rows from them.
            // The declaration map is the only value input: the sync writes it in the same
            // pass as the other arrays and reads it back, so there is no second write here.
            if (propsOk)
            {
                // v1 has no declaration for "this prop is a decal", so nothing is exempt.
                bool reference = LevelPaths.IsReferenceLevelId(levelId) && LevelPaths.IsReferenceScene(out _);
                TrySyncDeclaredFields(doc, levelRoot, propsRoot, report, null, reference ? path : null);
            }

            BindArenaHost(doc, levelRoot, structuresRoot, report, null, false, 0f);
            ReportStartSize(doc, report);

            return propsOk && structuresOk;
        }

        /// <summary>
        /// Tells the operator what START BALL the level asks for, because this tool does not
        /// write the ball.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The start diameter is a per-level parameter (parity D6: 5 cm for MAS1, 22.2 cm for
        /// Cancer, 30 cm for our first level) and the runtime field that carries it is
        /// <c>KatamariBallMotor.startDiameterWorldM</c>. The importer owns the props and the
        /// arena arrays, not the motor's wiring, so it prints the value the JSON declares and
        /// leaves the component alone. 0 on the motor means "use the calibration baseline",
        /// which is the 0.2997 m start ball every level authored before the field existed
        /// keeps, so nothing moves until an operator sets the field.
        /// </para>
        /// <para>
        /// This is a note, never an error: a level without in-scene wiring is a normal state
        /// for a document that a lab imports into a scratch scene.
        /// </para>
        /// </remarks>
        private static void ReportStartSize(PlacementDocument doc, PlacementImportReport report)
        {
            float diameter = doc.level != null ? doc.level.ballDiameterStart : 0f;
            if (diameter <= 0f) return;
            report.Note("level.ballDiameterStart=" +
                        diameter.ToString("0.####", CultureInfo.InvariantCulture) +
                        " m: set KatamariBallMotor.startDiameterWorldM to that value to start the" +
                        " ball at this level's size (leave it at 0 for the calibration's 0.2997 m)");
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

                // `valueUnits` is DEPRECATED and no longer required: growth is derived from
                // aabb x k x penalty (Docs/OPEN_DECISIONS.md (r) C+D), so a prop with no value
                // is a normal prop. A declared penalty, if any, must be inside 0 < p <= 1.
                if (prop.volumePenalty > 0f)
                {
                    string penaltyProblem =
                        PickupFieldAuthoringRules.VolumePenaltyError(prop.volumePenalty);
                    if (penaltyProblem != null)
                    {
                        report.Error(prop.itemId + " volumePenalty " + prop.volumePenalty + " " +
                                     penaltyProblem);
                        ok = false;
                    }
                }

                // k is optional (default 0.70), but a DECLARED one must be inside the one
                // legal range. Same rule the sync applies when it writes the column.
                if (prop.volumeK != 0f)
                {
                    string volumeKProblem =
                        PickupFieldAuthoringRules.VolumeKError(prop.volumeK);
                    if (volumeKProblem != null)
                    {
                        report.Error(prop.itemId + " volumeK " + prop.volumeK + " " + volumeKProblem);
                        ok = false;
                    }
                }
            }

            if (doc.defs != null)
            {
                for (int i = 0; i < doc.defs.Length; i++)
                {
                    DefEntry def = doc.defs[i];
                    if (def == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(def.model) && !IsValidModelPath(def.model))
                    {
                        report.Error("defs[" + i + "] def '" + def.id + "' [def_model]: model '" +
                                     def.model + "' must be a Unity-relative .fbx path under " +
                                     "Assets/ and must not contain '..'");
                        ok = false;
                    }

                    // k is optional (0 = not declared, default 0.70), but a declared one
                    // must be inside the one legal range; the sync applies the same rule.
                    if (def.volumeK != 0f)
                    {
                        string defKProblem =
                            PickupFieldAuthoringRules.VolumeKError(def.volumeK);
                        if (defKProblem != null)
                        {
                            report.Error("defs[" + i + "] def '" + def.id + "' volumeK " +
                                         def.volumeK + " " + defKProblem);
                            ok = false;
                        }
                    }

                    if (def.volumePenalty != 0f)
                    {
                        string defPenaltyProblem =
                            PickupFieldAuthoringRules.VolumePenaltyError(def.volumePenalty);
                        if (defPenaltyProblem != null)
                        {
                            report.Error("defs[" + i + "] def '" + def.id + "' volumePenalty " +
                                         def.volumePenalty + " " + defPenaltyProblem);
                            ok = false;
                        }
                    }

                    if (def.collider != null &&
                        PickupFieldAuthoringRules.IsColliderDeclared(def.collider.type))
                    {
                        PickupFieldAuthoringRules.PickupProxyDefinition proxy;
                        string proxyError;
                        if (!TryBuildProxy(def.collider, out proxy, out proxyError))
                        {
                            report.Error("defs[" + i + "] def '" + def.id +
                                         "' [collider]: " + proxyError);
                            ok = false;
                        }
                    }
                }
            }

            var structureIds = new HashSet<string>(StringComparer.Ordinal);
            int planes = 0;
            int boxes = 0;
            int ramps = 0;
            int grounds = 0;
            int curves = 0;
            int curveSegments = 0;
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

                    // A curve_ramp has no authored box (`size` is derived from the arc), so only
                    // its pose is required here; ValidateCurve checks the curve block itself.
                    bool curve = IsCurveRole(structure);
                    if (!IsVector(structure.center, 3) || !IsVector(structure.rotationQuat, 4) ||
                        (!curve && !IsVector(structure.size, 3)))
                    {
                        report.Error("structure '" + structure.id + "' needs center[3], size[3] and " +
                                     "rotationQuat[4]" +
                                     (curve ? " (a curve_ramp's size is derived from its arc)" : ""));
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
                        case "curve_ramp":
                        {
                            int segmentCount;
                            if (!ValidateCurve(structure, BallRadiusOf(doc), report, out segmentCount))
                            {
                                ok = false;
                            }
                            else
                            {
                                curveSegments += segmentCount;
                            }

                            curves++;
                            break;
                        }
                        default:
                            report.Error("structure '" + structure.id + "' has role '" + structure.role +
                                         "' (expected ground/boundary/box/ramp/curve_ramp)");
                            ok = false;
                            break;
                    }
                }
            }

            string frameProblem = FrameRuleViolation(
                doc.level != null && doc.level.arena != null ? doc.level.arena.frame : null,
                grounds, planes);
            if (frameProblem != null)
            {
                report.Error(frameProblem);
                ok = false;
            }

            BudgetBlock budget = (doc.level != null && HasBudgetContent(doc.level.budget))
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

            // A curve_ramp is ONE logical ramp against the budget; its segments are static OBBs
            // and are checked against the motor's 256-solid table below instead.
            if (ramps + curves > budget.ramps)
            {
                report.Error("ramps=" + (ramps + curves) + " over the level budget of " +
                             budget.ramps + " (" + ramps + " straight + " + curves +
                             " curve_ramp; a curve counts once)");
                ok = false;
            }

            int staticObbs = boxes + ramps + curveSegments;
            if (staticObbs > StaticObbCapacity)
            {
                report.Error("static OBBs=" + staticObbs + " over the motor's " + StaticObbCapacity +
                             " (boxes + ramp slabs + curve segments; CONTACT_MODEL_V2 5.4)");
                ok = false;
            }

            return ok;
        }

        /// <summary>The smallest ball radius this level plays with (5.7's <c>r_min</c>).</summary>
        private static float BallRadiusOf(PlacementDocument doc)
        {
            float diameter = (doc != null && doc.level != null) ? doc.level.ballDiameterStart : 0.3f;
            if (!IsPositiveFinite(diameter))
            {
                diameter = 0.3f;
            }

            return Mathf.Max(CurveMinBallRadiusM, diameter * 0.5f);
        }

        private static bool IsCurveRole(StructureEntry structure)
        {
            return structure != null && string.Equals(structure.role, "curve_ramp",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Checks one <c>role=curve_ramp</c> entry and resolves its segment count.
        /// </summary>
        /// <remarks>
        /// The bracketed codes are the same ones <c>Tools/levelbuild/placement_core.py</c>
        /// prints, so the CLI and the importer name a broken arc the same way. Only values a
        /// solid arc cannot have are refused (a non-positive or non-finite radius/width/
        /// thickness, an arc outside 0..360, an unknown bend); a chain that is too coarse for
        /// the chord budget is refused too, because the ball would feel the bump.
        /// </remarks>
        private static bool ValidateCurve(StructureEntry structure, float ballRadiusM,
            PlacementImportReport report, out int segments)
        {
            segments = 0;
            CurveEntry curve = structure.curve;
            if (!HasCurveContent(curve))
            {
                report.Error("structure '" + structure.id + "' [curve_missing]: a role=curve_ramp " +
                             "needs a curve block (radius_m, arc_deg, width_m, thickness_m)");
                return false;
            }

            bool ok = true;
            string prefix = "structure '" + structure.id + "' ";
            if (!IsPositiveFinite(curve.radius_m))
            {
                report.Error(prefix + "[curve_radius]: radius_m must be finite and > 0 (got " +
                             curve.radius_m + ")");
                ok = false;
            }

            if (!IsPositiveFinite(curve.arc_deg) || curve.arc_deg > CurveMaxArcDeg)
            {
                report.Error(prefix + "[curve_arc]: arc_deg must be finite, > 0 and <= " +
                             CurveMaxArcDeg + " (got " + curve.arc_deg + ")");
                ok = false;
            }

            if (!IsPositiveFinite(curve.width_m))
            {
                report.Error(prefix + "[curve_width]: width_m must be finite and > 0 (got " +
                             curve.width_m + ")");
                ok = false;
            }

            if (!IsPositiveFinite(curve.thickness_m))
            {
                report.Error(prefix + "[curve_thickness]: thickness_m must be finite and > 0 (got " +
                             curve.thickness_m + ")");
                ok = false;
            }

            string bend = string.IsNullOrEmpty(curve.bend) ? "up" : curve.bend;
            if (!string.Equals(bend, "up", StringComparison.Ordinal) &&
                !string.Equals(bend, "down", StringComparison.Ordinal))
            {
                bool named = string.Equals(bend, "left", StringComparison.Ordinal) ||
                             string.Equals(bend, "right", StringComparison.Ordinal) ||
                             string.Equals(bend, "helix", StringComparison.Ordinal);
                report.Error(prefix + (named ? "[curve_bend_unsupported]" : "[curve_bend]") + ": bend '" +
                             bend + "' cannot be expanded; the tessellator handles a vertical arc " +
                             "only (\"up\" or \"down\")");
                ok = false;
            }

            if (curve.segments < 0 || curve.segments > StaticObbCapacity)
            {
                report.Error(prefix + "[curve_segments]: segments " + curve.segments +
                             " is outside 0.." + StaticObbCapacity + " (0 = auto by the chord budget)");
                ok = false;
            }

            // The same role constraints the CLI enforces (placement_core._check_curve): a
            // curve is a box role, and it has no straight top edge for the step rule to check.
            if (!string.IsNullOrEmpty(structure.shape) &&
                !string.Equals(structure.shape, "box", StringComparison.Ordinal))
            {
                report.Error(prefix + "[curve_shape]: a curve_ramp must be shape=box (got '" +
                             structure.shape + "')");
                ok = false;
            }

            if (!string.IsNullOrEmpty(structure.collision) &&
                !string.Equals(structure.collision, "box", StringComparison.Ordinal))
            {
                report.Error(prefix + "[curve_collision]: a curve_ramp must be collision=box (got '" +
                             structure.collision + "')");
                ok = false;
            }

            if (!string.IsNullOrEmpty(structure.meets))
            {
                report.Error(prefix + "[curve_meets]: a curved ramp has no straight top edge, so " +
                             "'meets' cannot be checked; drop it");
                ok = false;
            }

            if (structure.size != null && structure.size.Length == 3)
            {
                bool derived = Mathf.Abs(structure.size[0] - curve.width_m) <= 0.001f &&
                               Mathf.Abs(structure.size[1] - curve.thickness_m) <= 0.001f &&
                               Mathf.Abs(structure.size[2]) <= 0.001f;
                if (!derived)
                {
                    report.Error(prefix + "[curve_size]: size is derived as [width_m, thickness_m, 0]");
                    ok = false;
                }
            }

            if (!ok)
            {
                return false;
            }

            segments = ResolveCurveSegments(curve.radius_m, curve.arc_deg, curve.segments,
                ballRadiusM * 2f);
            if (!CurveRampMath.IsCurveWithinChordBudget(curve.radius_m, segments, curve.arc_deg,
                    ballRadiusM))
            {
                int needed = ResolveCurveSegments(curve.radius_m, curve.arc_deg, 0, ballRadiusM * 2f);
                report.Error(prefix + "[curve_segments_coarse]: segments=" + segments +
                             " puts the chord error over 5% of the " +
                             ballRadiusM.ToString("0.###", CultureInfo.InvariantCulture) +
                             " m smallest ball radius; use at least " + needed);
                return false;
            }

            return true;
        }

        /// <summary>
        /// The segment count of an arc: the authored one, else the smallest that fits the
        /// chord budget. The predicate is <see cref="CurveRampMath.IsCurveWithinChordBudget"/>,
        /// the same test <c>placement_core.auto_curve_segments</c> loops over, so both sides
        /// answer the same number.
        /// </summary>
        private static int ResolveCurveSegments(float radiusM, float arcDeg, int declared,
            float ballDiameterStart)
        {
            if (declared > 0)
            {
                return declared;
            }

            float ballRadius = Mathf.Max(CurveMinBallRadiusM, ballDiameterStart * 0.5f);
            for (int count = 1; count < StaticObbCapacity; count++)
            {
                if (CurveRampMath.IsCurveWithinChordBudget(radiusM, count, arcDeg, ballRadius))
                {
                    return count;
                }
            }

            return StaticObbCapacity;
        }

        /// <summary>
        /// Converts the flat JSON letter into the shared editor shape and triangulates a
        /// convex hull. The importer validates every def once, then builds the per-item map
        /// the sync consumes; an invalid block is an import error, not a silent box fallback.
        /// </summary>
        private static bool TryBuildProxy(ColliderEntry entry,
            out PickupFieldAuthoringRules.PickupProxyDefinition proxy, out string error)
        {
            proxy = new PickupFieldAuthoringRules.PickupProxyDefinition();
            error = null;
            if (entry == null)
            {
                error = "collider block is missing";
                return false;
            }

            string type = entry.type == null ? "" : entry.type.Trim().ToLowerInvariant();
            if (!PickupFieldAuthoringRules.IsColliderDeclared(type))
            {
                error = "collider.type is empty; the block is not declared";
                return false;
            }
            proxy.TypeName = type;
            if (type == "box") proxy.Kind = PickupFieldAuthoringRules.ProxyKindBox;
            else if (type == "sphere") proxy.Kind = PickupFieldAuthoringRules.ProxyKindSphere;
            else if (type == "capsule") proxy.Kind = PickupFieldAuthoringRules.ProxyKindCapsule;
            else if (type == "convex") proxy.Kind = PickupFieldAuthoringRules.ProxyKindConvex;
            else
            {
                error = "collider.type must be box/sphere/capsule/convex (got '" + entry.type + "')";
                return false;
            }

            Vector3 center;
            if (!TryVec3(entry.center, "collider.center", out center, out error)) return false;
            proxy.Center = center;
            proxy.Radius = entry.radius;
            proxy.HalfHeight = entry.halfHeight;
            proxy.FitError = entry.fit_error;

            if (proxy.Kind == PickupFieldAuthoringRules.ProxyKindBox)
            {
                Vector3 size;
                if (!TryVec3(entry.size, "collider.size", out size, out error)) return false;
                proxy.Size = size;
            }
            else if (proxy.Kind == PickupFieldAuthoringRules.ProxyKindSphere)
            {
                // radius is checked by ProxyError.
            }
            else if (proxy.Kind == PickupFieldAuthoringRules.ProxyKindCapsule)
            {
                string axis = entry.axis == null ? "" : entry.axis.Trim().ToLowerInvariant();
                if (axis == "x") proxy.Axis = 0;
                else if (axis == "y") proxy.Axis = 1;
                else if (axis == "z") proxy.Axis = 2;
                else
                {
                    error = "collider.axis must be x/y/z (got '" + entry.axis + "')";
                    return false;
                }
            }
            else
            {
                if (entry.hull == null || entry.hull.Length < 12 ||
                    entry.hull.Length > PickupFieldAuthoringRules.MaxProxyHullPoints * 3 ||
                    entry.hull.Length % 3 != 0)
                {
                    error = "collider.hull must be a flat list of 3N numbers (4..24 points)";
                    return false;
                }
                int count = entry.hull.Length / 3;
                Vector3[] points = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    Vector3 point = new Vector3(entry.hull[i * 3], entry.hull[i * 3 + 1],
                        entry.hull[i * 3 + 2]);
                    if (!IsFinite(point))
                    {
                        error = "collider.hull[" + i + "] must be finite";
                        return false;
                    }
                    points[i] = point;
                }
                int[] faces;
                int faceCount;
                string faceError;
                if (!PickupFieldAuthoringRules.TryBuildHullFaces(points, count, out faces,
                    out faceCount, out faceError))
                {
                    error = faceError;
                    return false;
                }
                proxy.Hull = points;
                proxy.HullCount = count;
                proxy.Faces = faces;
                proxy.FaceCount = faceCount;
            }

            string problem = PickupFieldAuthoringRules.ProxyError(proxy);
            if (problem != null)
            {
                error = problem;
                return false;
            }
            return true;
        }

        private static bool TryVec3(float[] values, string what, out Vector3 result,
            out string error)
        {
            result = Vector3.zero;
            error = null;
            if (values == null || values.Length != 3)
            {
                error = what + " must be a list of 3 numbers";
                return false;
            }
            result = new Vector3(values[0], values[1], values[2]);
            if (!IsFinite(result))
            {
                error = what + " must be finite";
                return false;
            }
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static PickupFieldAuthoringRules.PickupProxyDefinition DeclaredProxy(
            PlacementDocument doc, PropEntry prop)
        {
            if (doc == null || prop == null || doc.defs == null || prop.def == null)
                return null;
            for (int i = 0; i < doc.defs.Length; i++)
            {
                DefEntry entry = doc.defs[i];
                if (entry == null || entry.collider == null) continue;
                if (!PickupFieldAuthoringRules.IsColliderDeclared(entry.collider.type)) continue;
                if (!string.Equals(entry.id, prop.def, StringComparison.Ordinal)) continue;
                PickupFieldAuthoringRules.PickupProxyDefinition proxy;
                string error;
                if (TryBuildProxy(entry.collider, out proxy, out error)) return proxy;
                return null;
            }
            return null;
        }

        /// <summary>
        /// Multiplies a prop's authored <c>scale</c> into its def's collider proxy parameters
        /// (§18.4's outstanding importer requirement).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why this is needed: the proxy numbers are MODEL space and
        /// <c>PickupFieldHost</c> bakes them with the prop's position and rotation but NOT its
        /// lossy scale (the attach path preserves the scale, the bake does not apply it), so a
        /// prop rendered at 0.1 would otherwise carry a 10x capsule. The scale is applied here,
        /// once, at import - the runtime still never scales anything by the authored value.
        /// </para>
        /// <para>
        /// Uniform scale multiplies every parameter by the one factor, including a convex
        /// hull's points (the five 0.9/0.1 reference props all have convex proxies). Non-uniform
        /// scale is per-axis, and only meaningful for a box or a sphere (D-5 layer 3); a sphere
        /// cannot become an ellipsoid, so it takes the LARGEST axis - conservative, never
        /// smaller than the visual. Anything else is refused rather than scaled into a shape
        /// the gate did not intend.
        /// </para>
        /// </remarks>
        private static bool TryApplyAuthorScale(
            PickupFieldAuthoringRules.PickupProxyDefinition proxy, PropEntry prop,
            PlacementImportReport report)
        {
            if (proxy == null || prop == null || prop.scale == null || prop.scale.Length != 3)
            {
                return true;
            }

            float sx = prop.scale[0];
            float sy = prop.scale[1];
            float sz = prop.scale[2];
            bool uniform = Mathf.Abs(sx - sy) <= 1e-4f && Mathf.Abs(sy - sz) <= 1e-4f;
            bool unit = Mathf.Abs(sx - 1f) <= 1e-4f && Mathf.Abs(sy - 1f) <= 1e-4f &&
                        Mathf.Abs(sz - 1f) <= 1e-4f;
            if (unit) return true;

            if (!uniform)
            {
                if (proxy.Kind == PickupFieldAuthoringRules.ProxyKindBox)
                {
                    proxy.Center = Vector3.Scale(proxy.Center, new Vector3(sx, sy, sz));
                    proxy.Size = Vector3.Scale(proxy.Size, new Vector3(sx, sy, sz));
                }
                else if (proxy.Kind == PickupFieldAuthoringRules.ProxyKindSphere)
                {
                    float largest = Mathf.Max(sx, Mathf.Max(sy, sz));
                    proxy.Center = Vector3.Scale(proxy.Center, new Vector3(sx, sy, sz));
                    proxy.Radius *= largest;
                }
                else
                {
                    report.Error(prop.itemId + " [prop_scale_nonuniform] a non-uniform scale on a '" +
                                 proxy.TypeName + "' proxy cannot be applied; scale is a box/sphere " +
                                 "authoring feature (D-5 layer 3)");
                    return false;
                }
            }
            else
            {
                proxy.Center = Vector3.Scale(proxy.Center, new Vector3(sx, sy, sz));
                proxy.Size = Vector3.Scale(proxy.Size, new Vector3(sx, sy, sz));
                proxy.Radius *= sx;
                proxy.HalfHeight *= sx;
                if (proxy.Hull != null)
                {
                    for (int i = 0; i < proxy.Hull.Length; i++)
                    {
                        proxy.Hull[i] = Vector3.Scale(proxy.Hull[i], new Vector3(sx, sy, sz));
                    }
                }
            }

            string problem = PickupFieldAuthoringRules.ProxyError(proxy);
            if (problem != null)
            {
                report.Error(prop.itemId + " [collider] the proxy scaled by " + Format(new Vector3(sx, sy, sz)) +
                             " is no longer legal: " + problem);
                return false;
            }

            return true;
        }

        private static Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition>
            BuildDeclaredProxies(PlacementDocument doc, PlacementImportReport report)
        {
            var declared = new Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition>(
                StringComparer.Ordinal);
            if (doc == null || doc.props == null) return declared;
            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId)) continue;
                PickupFieldAuthoringRules.PickupProxyDefinition proxy = DeclaredProxy(doc, prop);
                if (proxy == null) continue;
                if (!TryApplyAuthorScale(proxy, prop, report)) continue;
                declared[prop.itemId] = proxy;
            }
            return declared;
        }

        /// <summary>
        /// Item id to the per-item volume factor k the level declares.
        /// </summary>
        /// <remarks>
        /// The prop's own <c>volumeK</c> wins over its def's, exactly like valueUnits; a def
        /// that declares neither contributes nothing, and the sync then falls back to the
        /// row already serialized on the host or to
        /// <c>PickupFieldAuthoringRules.DefaultVolumeK</c> (0.70). A declared value outside
        /// <c>0 &lt; k &lt;= 32</c> is NOT filtered here: the sync refuses that row with a
        /// report line naming the item, because silently replacing it would play a different
        /// gate than the file states.
        /// </remarks>
        private static Dictionary<string, float> BuildDeclaredVolumeK(PlacementDocument doc)
        {
            var declared = new Dictionary<string, float>(StringComparer.Ordinal);
            if (doc.props == null)
            {
                return declared;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId))
                {
                    continue;
                }

                float volumeK = DeclaredVolumeK(doc, prop);
                if (volumeK > 0f)
                {
                    declared[prop.itemId] = volumeK;
                }
            }

            return declared;
        }

        /// <summary>
        /// One prop's declared k: its own <c>volumeK</c>, else its def's, else 0 (not
        /// declared). 0 is a sentinel here, never a legal factor.
        /// </summary>
        private static float DeclaredVolumeK(PlacementDocument doc, PropEntry prop)
        {
            if (prop.volumeK > 0f) return prop.volumeK;
            if (doc.defs == null || prop.def == null) return 0f;
            for (int i = 0; i < doc.defs.Length; i++)
            {
                DefEntry entry = doc.defs[i];
                if (entry != null && string.Equals(entry.id, prop.def, StringComparison.Ordinal))
                {
                    return entry.volumeK;
                }
            }

            return 0f;
        }

        /// <summary>
        /// Item id to the growth penalty the level declares (absent = 0.80 default).
        /// </summary>
        private static Dictionary<string, float> BuildDeclaredVolumePenalty(PlacementDocument doc)
        {
            var declared = new Dictionary<string, float>(StringComparer.Ordinal);
            if (doc.props == null)
            {
                return declared;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId))
                {
                    continue;
                }

                float penalty = DeclaredVolumePenalty(doc, prop);
                if (penalty > 0f)
                {
                    declared[prop.itemId] = penalty;
                }
            }

            return declared;
        }

        /// <summary>One prop's declared penalty: its own, else its def's, else 0 (not declared).</summary>
        private static float DeclaredVolumePenalty(PlacementDocument doc, PropEntry prop)
        {
            if (prop.volumePenalty > 0f) return prop.volumePenalty;
            if (doc.defs == null || prop.def == null) return 0f;
            for (int i = 0; i < doc.defs.Length; i++)
            {
                DefEntry entry = doc.defs[i];
                if (entry != null && string.Equals(entry.id, prop.def, StringComparison.Ordinal))
                {
                    return entry.volumePenalty;
                }
            }

            return 0f;
        }

        /// <summary>
        /// Item id to the prop's AUTHORED derived growth (the canonical writer's number).
        /// </summary>
        /// <remarks>
        /// ONE pricer, and it is the data: <c>canonical_doc</c> computes
        /// <c>round(penalty*k*a*b*c*1e9)</c> in double from the authored aabb and writes
        /// <c>props[].growthMm3</c>; the sync copies that number into the host column. The
        /// scene's measured bounds are only a verification, because Unity's renderer bounds
        /// and the authored aabb differ by rounding - pricing from them is what made the host
        /// sum disagree with the file (O7: 1,417,080,788 vs 1,417,085,102).
        /// </remarks>
        private static Dictionary<string, long> BuildDeclaredGrowthMm3(PlacementDocument doc)
        {
            var declared = new Dictionary<string, long>(StringComparer.Ordinal);
            if (doc.props == null)
            {
                return declared;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId))
                {
                    continue;
                }

                long growth = DeclaredGrowthMm3(doc, prop);
                if (growth > 0L)
                {
                    declared[prop.itemId] = growth;
                }
            }

            return declared;
        }

        /// <summary>
        /// One prop's authored growth: <c>props[].growthMm3</c> when present. An old file with
        /// no column is priced from the aabb by the SAME formula, so it still imports; that
        /// fallback is counted and reported, never silent.
        /// </summary>
        private static long DeclaredGrowthMm3(PlacementDocument doc, PropEntry prop)
        {
            if (prop.growthMm3 > 0L)
            {
                return prop.growthMm3;
            }

            if (prop.aabb == null || prop.aabb.Length != 3) return 0L;
            float volumeK = DeclaredVolumeK(doc, prop);
            if (volumeK <= 0f) volumeK = PickupAbsorbMath.DefaultVolumeK;
            float penalty = DeclaredVolumePenalty(doc, prop);
            if (penalty <= 0f) penalty = PickupAbsorbMath.DefaultVolumePenalty;
            Vector3 bounds = new Vector3(prop.aabb[0], prop.aabb[1], prop.aabb[2]);
            long computed;
            return PickupAbsorbMath.TryComputeGrowthMm3(bounds, volumeK, penalty, out computed)
                ? computed
                : 0L;
        }

        /// <summary>
        /// The level's total growth in mm^3, for the one-line report.
        /// </summary>
        /// <remarks>
        /// The sum of the same authored numbers the sync writes, so the report's
        /// <c>declared=</c>, the host column's sum and <c>placement.json</c>'s total are one
        /// number by construction. A row with neither column nor usable aabb contributes 0
        /// and is named by the sync's refusal.
        /// </remarks>
        private static long SumDeclaredGrowthMm3(PlacementDocument doc)
        {
            long total = 0L;
            if (doc.props == null)
            {
                return total;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop != null)
                {
                    total += DeclaredGrowthMm3(doc, prop);
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

        /// <summary>The def's declared <c>model</c> (a Unity-relative .fbx path), or null.</summary>
        /// <remarks>
        /// <c>defs[].model</c> is what lets a def stop being defined by its id: a level built
        /// from an external asset pack (or a def id that collides with one of our props) points
        /// at its own FBX instead of <c>Art/Props/&lt;def&gt;/&lt;def&gt;.fbx</c>. The value is
        /// validated by <see cref="IsValidModelPath"/> before any asset is loaded.
        /// </remarks>
        public static string DeclaredModelOf(PlacementDocument doc, string defId)
        {
            if (doc == null || doc.defs == null || string.IsNullOrEmpty(defId))
            {
                return null;
            }

            for (int i = 0; i < doc.defs.Length; i++)
            {
                DefEntry entry = doc.defs[i];
                if (entry != null && string.Equals(entry.id, defId, StringComparison.Ordinal))
                {
                    return string.IsNullOrEmpty(entry.model) ? null : entry.model;
                }
            }

            return null;
        }

        /// <summary>The FBX this prop instantiates: a declared model wins, else the repo prop.</summary>
        /// <remarks>
        /// The fallback is the historic path and is unchanged. A declared model is <b>never</b>
        /// silently replaced by the repo prop when its asset is missing: the import fails with
        /// <c>[model_missing]</c> so a typo cannot quietly draw a different object.
        /// </remarks>
        public static string ResolvePropModelPath(string defId, string declaredModel)
        {
            if (!string.IsNullOrEmpty(declaredModel))
            {
                return declaredModel;
            }

            return PropsArtFolder + "/" + defId + "/" + defId + ".fbx";
        }

        /// <summary>A declared model path must be a Unity-relative .fbx under Assets/.</summary>
        /// <remarks>
        /// Same rule as <c>placement_core._check_defs</c>: starts with <c>Assets/</c>, ends in
        /// <c>.fbx</c>, no <c>..</c> segment. The Python validator is the gate; this second
        /// check exists so a hand-edited <c>placement.json</c> cannot point the importer at a
        /// folder outside the project.
        /// </remarks>
        public static bool IsValidModelPath(string model)
        {
            if (string.IsNullOrEmpty(model) ||
                !model.StartsWith("Assets/", StringComparison.Ordinal) ||
                !model.EndsWith(".fbx", StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = model.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "..")
                {
                    return false;
                }
            }

            return true;
        }

        // -------------------------------------------------------------- props ---

        private static bool ApplyProps(PlacementDocument doc, Transform propsRoot, PlacementImportReport report)
        {
            bool ok = true;

            // The declaration set is built first: it is both the reconcile's wanted list and
            // the walk's filter, so the walk can look for a wanted id inside an object that
            // carries a Renderer (a pre-fix scene's nested props) without ever taking a
            // model's internal node for a prop.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doc.props.Length; i++)
            {
                if (doc.props[i] != null && !string.IsNullOrEmpty(doc.props[i].itemId))
                {
                    wanted.Add(doc.props[i].itemId);
                }
            }

            Dictionary<string, GameObject> existing = IndexProps(propsRoot, wanted);

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];

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

                if (current != null &&
                    !InstanceMatchesDef(current, prop.def, DeclaredModelOf(doc, prop.def)))
                {
                    // A different def (or a different declared model) is a different mesh;
                    // keeping the object would leave the old model with the new id (and the old
                    // size in propBounds).
                    Undo.DestroyObjectImmediate(current);
                    existing.Remove(prop.itemId);
                    current = null;
                }

                Transform folder = EnsurePropFolder(propsRoot, kind, prop.zone, prop.group,
                    prop.parent, report);
                if (folder == null)
                {
                    // EnsurePropFolder already named the colliding container. Nothing is
                    // created or moved for this prop, so the run fails instead of losing it.
                    report.PropErrors++;
                    ok = false;
                    continue;
                }
                if (current == null)
                {
                    current = InstantiateProp(doc, prop, folder, report);
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
                    if (ApplyPose(current, prop))
                    {
                        report.PoseScaleCompensated++;
                        report.Note(prop.itemId + ": its container \"" +
                                    HierarchyPath(current.transform.parent) + "\" has scale " +
                                    Format(current.transform.parent.lossyScale) + ", so the authored " +
                                    "scale " + Format(ScaleOf(prop)) + " was written as the LOCAL " +
                                    "scale that keeps the WORLD size at the document's aabb");
                    }

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

        /// <summary>
        /// The name of the folder that holds the children of the prop <paramref name="parentId"/>:
        /// the id plus <see cref="HierarchyChildrenSuffix"/>, so no container ever shares a name
        /// with a prop.
        /// </summary>
        public static string HierarchyContainerName(string parentId)
        {
            return parentId + HierarchyChildrenSuffix;
        }

        /// <summary>
        /// Returns the folder a prop with this kind/zone/group belongs in, creating the
        /// containers it needs, or null when a container name is already taken by a prop.
        /// </summary>
        /// <remarks>
        /// The group goes under <see cref="GroupsFolderName"/>, which is the whole point: a
        /// group id may equal another prop's item id, and under a reserved parent the lookup
        /// can never find that prop. Zones stay directly under the kind folder (they were
        /// never read by anything but a human), so an import of an existing level only moves
        /// the grouped props once.
        /// </remarks>
        private static Transform EnsurePropFolder(Transform propsRoot, string kind, string zone,
                                                 string group, string parentId,
                                                 PlacementImportReport report)
        {
            Transform folder = EnsureOwnedPropFolder(propsRoot, kind, report);
            if (folder == null) return null;
            if (!string.IsNullOrEmpty(zone))
            {
                folder = EnsureOwnedPropFolder(folder, zone, report);
                if (folder == null) return null;
            }

            // v2's parent: a FOLDER for the parent prop's children, not the prop itself (owner
            // ruling A). Parenting the child's GameObject under the parent's GameObject would
            // put it below a Renderer, and PickupFieldSync stops at the topmost Renderer, so
            // the child would never become a row (the F2/F3 defect class). The world pose is
            // untouched either way: the folder is at identity and the child keeps its authored
            // world position, so no blend of two transforms can double it. The folder carries
            // the parent's id plus a suffix, never the bare id: two objects with one name made
            // a by-name probe read the folder's identity pose as the prop's.
            if (!string.IsNullOrEmpty(parentId))
            {
                folder = EnsureOwnedPropFolder(folder, HierarchyFolderName, report);
                if (folder == null) return null;
                folder = EnsureOwnedPropFolder(folder, HierarchyContainerName(parentId), report);
                if (folder == null) return null;
            }

            if (!string.IsNullOrEmpty(group))
            {
                folder = EnsureOwnedPropFolder(folder, GroupsFolderName, report);
                if (folder == null) return null;
                folder = EnsureOwnedPropFolder(folder, group, report);
                if (folder == null) return null;
            }

            return folder;
        }

        private static GameObject InstantiateProp(PlacementDocument doc, PropEntry prop, Transform folder,
                                                 PlacementImportReport report)
        {
            string declaredModel = DeclaredModelOf(doc, prop.def);
            string assetPath = ResolvePropModelPath(prop.def, declaredModel);
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
            {
                if (!string.IsNullOrEmpty(declaredModel))
                {
                    // The def names its own mesh, so the repo prop is a *different object*;
                    // falling back would draw the wrong thing and hide the broken path.
                    report.Error(prop.itemId + " [model_missing]: def '" + prop.def +
                                 "' declares model '" + assetPath + "' but no asset is there; the " +
                                 "repo prop at " + ResolvePropModelPath(prop.def, null) +
                                 " is NOT used as a fallback");
                }
                else
                {
                    report.Error(prop.itemId + ": no model at " + assetPath + " (the prefab library is P2; " +
                                 "until then the importer uses the FBX)");
                }
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

            if (ApplyPose(instance, prop))
            {
                report.PoseScaleCompensated++;
                report.Note(prop.itemId + ": its container \"" +
                            HierarchyPath(instance.transform.parent) + "\" has scale " +
                            Format(instance.transform.parent.lossyScale) + ", so the authored scale " +
                            Format(ScaleOf(prop)) + " was compensated to keep the world size");
            }

            return instance;
        }

        /// <summary>
        /// Writes the authored WORLD pose: position, rotation and the WORLD scale.
        /// </summary>
        /// <remarks>
        /// A prop's authored <c>position</c>/<c>rotationQuat</c>/<c>scale</c> are world quantities
        /// (<c>aabb</c> is in metres and already contains the scale), so the pose is written and
        /// compared in WORLD terms. The scale is the one value that a parent can multiply behind
        /// our back: writing the authored number into <c>localScale</c> under a container whose
        /// <c>lossyScale</c> is not 1 makes the object the wrong SIZE while its local numbers look
        /// perfect - and a `PoseMatches` that compares local scale then reports "already correct"
        /// forever. The reference level's 18 shrunken child props are exactly that shape of bug
        /// (their local scale was 1 while their world bounds were 1/100th of the document's aabb).
        /// The parent is compensated here, once, so the world size is always the authored one.
        /// </remarks>
        /// <returns>
        /// True when the parent container forced a non-authored LOCAL scale, i.e. when the
        /// scene's container tree is not identity for this prop (the importer compensates the
        /// world size and the caller reports it, so the cause is visible instead of silent).
        /// </returns>
        private static bool ApplyPose(GameObject instance, PropEntry prop)
        {
            Vector3 authored = ScaleOf(prop);
            Vector3 local = LocalScaleFor(instance.transform.parent, authored);
            Undo.RecordObject(instance.transform, UndoLabel);
            instance.transform.position = ToVector(prop.position);
            instance.transform.rotation = ToQuaternion(prop.rotationQuat);
            instance.transform.localScale = local;
            return Vector3.Distance(local, authored) > PositionToleranceM;
        }

        private static bool PoseMatches(GameObject instance, PropEntry prop)
        {
            Vector3 position = ToVector(prop.position);
            Quaternion rotation = ToQuaternion(prop.rotationQuat);
            Transform transform = instance.transform;
            // WORLD scale, not local: see ApplyPose. A parent that scales must not make a wrong
            // prop look settled.
            return Vector3.Distance(transform.position, position) <= PositionToleranceM &&
                   Mathf.Abs(Quaternion.Dot(transform.rotation, rotation)) >= RotationDotTolerance &&
                   Vector3.Distance(transform.lossyScale, ScaleOf(prop)) <= PositionToleranceM;
        }

        /// <summary>
        /// The local scale that makes an object's WORLD scale the authored one under
        /// <paramref name="parent"/>.
        /// </summary>
        /// <remarks>
        /// Under the managed folder tree (all containers at identity) this is the authored scale
        /// unchanged, so nothing moves for a clean scene. A zero-scale parent axis is left
        /// uncompensated instead of producing an infinite local scale; such a container is a data
        /// error the arena reports with its own reason code.
        /// </remarks>
        private static Vector3 LocalScaleFor(Transform parent, Vector3 authored)
        {
            if (parent == null) return authored;
            Vector3 parentScale = parent.lossyScale;
            return new Vector3(
                DivideByParentScale(authored.x, parentScale.x),
                DivideByParentScale(authored.y, parentScale.y),
                DivideByParentScale(authored.z, parentScale.z));
        }

        private static float DivideByParentScale(float value, float parentScale)
        {
            if (Mathf.Abs(parentScale) <= 1e-6f) return value;
            return value / parentScale;
        }

        /// <summary>The authored scale, or one when the document did not write one.</summary>
        private static Vector3 ScaleOf(PropEntry prop)
        {
            if (prop == null || prop.scale == null || prop.scale.Length != 3) return Vector3.one;
            return new Vector3(prop.scale[0], prop.scale[1], prop.scale[2]);
        }

        /// <summary>
        /// The importer's identity index: item id to the one instance under the managed props
        /// root. The id is parsed from the object's name (the <c>@size=</c> suffix is
        /// stripped), i.e. the same key <see cref="PickupFieldSync"/> uses for a row, so an
        /// imported object and a synced row cannot disagree about which prop they are.
        /// </summary>
        private static Dictionary<string, GameObject> IndexProps(Transform propsRoot,
                                                                HashSet<string> wanted)
        {
            var index = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            CollectProps(propsRoot, index, wanted, false);
            return index;
        }

        /// <summary>
        /// Collects the manageable props under <paramref name="parent"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The walk descends INTO a renderer-bearing object, which the sync's rule
        /// deliberately does not. That is the immunity F2 asks for: a scene imported before
        /// the group-container fix has real props parented inside a prop, and only a walk that
        /// looks past the first Renderer can find them, move them to their own folder and stop
        /// re-creating them on every import.
        /// </para>
        /// <para>
        /// Two filters keep that safe, and they are different at the two depths. At the top
        /// level every renderer-bearing object is a prop exactly as before - the stale sweep
        /// must still find an object the document no longer declares, or removal would stop
        /// working. Inside a prop only another prop counts: a prefab instance root whose id
        /// the level declares (the F2 nesting is a nested prefab, so it is one). The nodes of
        /// a prop's own model are not instance roots, so they can never become props.
        /// </para>
        /// </remarks>
        private static void CollectProps(Transform parent, Dictionary<string, GameObject> index,
                                        HashSet<string> wanted, bool insideProp)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.GetComponent<Renderer>() != null)
                {
                    string itemId = PropIdOf(child.name);
                    bool isProp = !insideProp ||
                                  (IsPrefabInstanceRoot(child) &&
                                   (wanted == null || wanted.Contains(itemId)));
                    if (isProp && !index.ContainsKey(itemId))
                    {
                        index.Add(itemId, child.gameObject);
                    }

                    if (child.childCount > 0 && IsPrefabInstanceRoot(child))
                    {
                        CollectProps(child, index, wanted, true);
                    }

                    continue;
                }

                CollectProps(child, index, wanted, insideProp);
            }
        }

        /// <summary>
        /// The item id a GameObject name carries: the registered name-parsing rule
        /// (<see cref="PickupFieldAuthoringRules.TryParseDeclaredSize"/>) with the name itself
        /// as the fallback when the optional <c>@size=</c> suffix is malformed. One spelling,
        /// used by both walks.
        /// </summary>
        private static string PropIdOf(string objectName)
        {
            string itemId;
            float declaredSize;
            string error;
            if (!PickupFieldAuthoringRules.TryParseDeclaredSize(objectName, out itemId,
                    out declaredSize, out error) || !string.IsNullOrEmpty(error))
            {
                return objectName;
            }

            return itemId;
        }

        /// <summary>
        /// True when this transform is the root of a prefab instance (a plain one, a nested
        /// one or a variant): every imported prop is one, while the nodes inside a prop's
        /// model are not. It is how <see cref="CollectProps"/> tells "a prop was parented
        /// into this prop" (the F2 state, which Unity models as a nested prefab) from the
        /// prop's own model internals.
        /// </summary>
        private static bool IsPrefabInstanceRoot(Transform candidate)
        {
            return PrefabUtility.IsAnyPrefabInstanceRoot(candidate.gameObject);
        }

        /// <summary>
        /// The property the pickup sync depends on: every declared itemId is the TOPMOST
        /// renderer-bearing object under Props, i.e. one row. A prop that owns another prop is
        /// not a row for the one inside it, so a run that leaves one nested must fail here
        /// with the ids named rather than reach the sync as "640 of 648".
        /// </summary>
        private static bool VerifyPropRows(PlacementDocument doc, Transform propsRoot,
                                          PlacementImportReport report)
        {
            var rows = new HashSet<string>(StringComparer.Ordinal);
            CollectTopmostRows(propsRoot, rows);

            bool ok = true;
            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId)) continue;
                if (rows.Contains(prop.itemId)) continue;
                report.Error(prop.itemId + ": the props tree has no topmost renderer for it, so the " +
                             "pickup sync would not make a row (a prop object is owning it as a " +
                             "container); move it out of that object or rename the container");
                ok = false;
            }

            return ok;
        }

        /// <summary>The pickup sync's collection rule, used only to predict its row set.</summary>
        private static void CollectTopmostRows(Transform parent, HashSet<string> rows)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.GetComponent<Renderer>() != null)
                {
                    rows.Add(PropIdOf(child.name));
                    continue;
                }

                CollectTopmostRows(child, rows);
            }
        }

        /// <summary>
        /// Is this instance already the mesh the document asks for (so it can be posed in
        /// place)? A declared model is compared by asset path - a model file may be named
        /// differently from its def id - while the historic no-model case keeps comparing the
        /// asset name to the def id.
        /// </summary>
        private static bool InstanceMatchesDef(GameObject instance, string defId, string declaredModel)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
            if (source == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(declaredModel))
            {
                string sourcePath = AssetDatabase.GetAssetPath(source);
                return !string.IsNullOrEmpty(sourcePath) &&
                       string.Equals(sourcePath, declaredModel, StringComparison.Ordinal);
            }

            return string.Equals(source.name, defId, StringComparison.Ordinal);
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
        /// therefore <b>not</b> boxes: making the marker itself a box would add a walkable ledge
        /// at the top of every wall and eat the box budget (design 4.3). Their <b>visible
        /// body</b> is a separate <c>Visual</c> child (<see cref="ApplyStructureVisuals"/>), so
        /// the player has a floor and walls without the arena ever seeing a box for them.
        /// </para>
        /// <para>
        /// A <c>box</c> entry becomes a real cube with a real <see cref="BoxCollider"/>: the
        /// collider's <c>bounds</c> are what the arena bakes into the motor's min/max once, so
        /// the ball needs no runtime physics for it. A <c>ramp</c> entry gets the same
        /// collider but is wired into <c>LevelArenaHost.rampBoxes</c>, never
        /// <c>staticBoxes</c>: a ramp registered as a box puts the radius-expanded AABB of
        /// the tilted board at its foot, an invisible wall (TERRAIN_SUPPORT 5.5/7.7).
        /// </para>
        /// </remarks>
        private static bool ApplyStructures(PlacementDocument doc, Transform structuresRoot,
            PlacementImportReport report)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);

            // Which ids already existed before this run. A body added to a marker that predates
            // the run is an UPDATE (the migration's "~N"); a body on an object this run created
            // is part of the creation and must not be counted a second time.
            var existedBefore = SnapshotStructureIds(structuresRoot);

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
                    ApplyCurveSegments(structure, current.transform, doc, report);
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
                    ApplyCurveSegments(structure, current.transform, doc, report);
                    continue;
                }

                if (ApplyStructure(structure, current, report))
                {
                    report.StructuresUpdated++;
                }

                ApplyCurveSegments(structure, current.transform, doc, report);
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

            // Bodies last, from the same document, so a size or role change can never leave a
            // stale one behind: the marker pose above is what the body is derived from.
            return ApplyStructureVisuals(doc, structuresRoot, existedBefore, report);
        }

        private static bool IsMarkerRole(StructureEntry structure)
        {
            return structure != null && IsMarkerRole(structure.role);
        }

        /// <summary>
        /// True for the two roles that are a plane to the arena (<c>ground</c>, <c>boundary</c>).
        /// </summary>
        /// <remarks>
        /// One spelling of the rule: the marker decision, the collider decision and the body
        /// decision all ask this, so a role cannot be a marker to one of them and a box to
        /// another.
        /// </remarks>
        private static bool IsMarkerRole(string role)
        {
            return string.Equals(role, "ground", StringComparison.Ordinal) ||
                   string.Equals(role, "boundary", StringComparison.Ordinal);
        }

        private static bool WantsBoxCollider(StructureEntry structure)
        {
            if (IsMarkerRole(structure) || IsCurveRole(structure))
            {
                // A curve's colliders are its segment children, never the parent.
                return false;
            }

            return string.Equals(structure.collision, "box", StringComparison.Ordinal);
        }

        private static GameObject CreateStructureObject(StructureEntry structure, Transform structuresRoot)
        {
            GameObject created;
            if (IsMarkerRole(structure) || IsCurveRole(structure))
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
            return isMarker == (IsMarkerRole(structure) || IsCurveRole(structure));
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
            // A curve_ramp's parent is a marker for its segment children: no authored box, so
            // scale stays 1 (a size of [width, thickness, 0] would flatten it) and the pose is
            // the arc's start frame.
            Vector3 targetScale = IsCurveRole(structure)
                ? Vector3.one
                : new Vector3(size[0], size[1], size[2]);
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
            BoxCollider[] colliders = instance.GetComponents<BoxCollider>();

            // A proxy solid's collider is its contact box in METRES, while the object itself is
            // scaled to the authored AABB (the visual and the support box). A BoxCollider's
            // numbers are local, so the contact box is divided by that scale once - the same
            // "one conversion, at import" rule the prop proxies follow. A split solid (§4
            // parts) gets one box per part, in the document's order.
            Vector3[] targetCenters;
            Vector3[] targetSizes;
            ContactBoxesOf(structure, targetScale, out targetCenters, out targetSizes);

            bool poseMatches =
                Vector3.Distance(instance.transform.position, targetPosition) <= PositionToleranceM &&
                Mathf.Abs(Quaternion.Dot(instance.transform.rotation, targetRotation)) >=
                RotationDotTolerance &&
                Vector3.Distance(instance.transform.localScale, targetScale) <= PositionToleranceM;
            bool colliderMatches = wantsCollider
                ? CollidersMatch(colliders, targetCenters, targetSizes)
                : colliders.Length == 0;

            // A solid whose model is a prop is collision only: the cube must not be drawn over
            // the model (an ochre box 3.4 x 4.1 x 2.8 m around a tree). A marker or curve parent
            // has no Renderer, so there is nothing to compare.
            Renderer body = instance.GetComponent<Renderer>();
            bool bodyMatches = body == null || body.enabled == !structure.bodyIsProp;

            if (poseMatches && colliderMatches && bodyMatches)
            {
                return false;
            }

            if (!bodyMatches)
            {
                Undo.RecordObject(body, UndoLabel);
                body.enabled = !structure.bodyIsProp;
            }

            if (!poseMatches)
            {
                Undo.RecordObject(instance.transform, UndoLabel);
                instance.transform.position = targetPosition;
                instance.transform.rotation = targetRotation;
                instance.transform.localScale = targetScale;
            }

            if (!colliderMatches)
            {
                // The collider sits on the scaled object, so a unit BoxCollider bakes exactly
                // the authored size; LevelArenaHost turns those bounds into a motor box. Extra
                // boxes (a solid that lost parts) are removed from the end, missing ones added
                // at the end, so the surviving boxes keep their order and a re-import of the
                // same document finds every box already in place.
                int wanted = wantsCollider ? targetCenters.Length : 0;
                for (int i = colliders.Length - 1; i >= wanted; i--)
                {
                    Undo.DestroyObjectImmediate(colliders[i]);
                }

                for (int i = 0; i < wanted; i++)
                {
                    BoxCollider box = i < colliders.Length
                        ? colliders[i]
                        : Undo.AddComponent<BoxCollider>(instance);
                    Undo.RecordObject(box, UndoLabel);
                    box.center = targetCenters[i];
                    box.size = targetSizes[i];
                }
            }

            return true;
        }

        /// <summary>
        /// The local centre/size of every BoxCollider one structure object must carry: one
        /// unit box (a plain solid), the proxy's contact box, or one box per part.
        /// </summary>
        private static void ContactBoxesOf(StructureEntry structure, Vector3 targetScale,
            out Vector3[] centers, out Vector3[] sizes)
        {
            float[] partCenters = structure.contactPartCenters;
            float[] partSizes = structure.contactPartSizes;
            if (structure.contactIsProxy && partCenters != null && partSizes != null &&
                partCenters.Length >= 3 && partCenters.Length % 3 == 0 &&
                partSizes.Length == partCenters.Length)
            {
                int count = partCenters.Length / 3;
                centers = new Vector3[count];
                sizes = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    centers[i] = ToColliderLocal(partCenters, i * 3, targetScale);
                    sizes[i] = ToColliderLocal(partSizes, i * 3, targetScale);
                }

                return;
            }

            centers = new[] { Vector3.zero };
            sizes = new[] { Vector3.one };
            if (structure.contactIsProxy && structure.contactCenter != null &&
                structure.contactCenter.Length == 3 && structure.contactSize != null &&
                structure.contactSize.Length == 3)
            {
                centers[0] = ToColliderLocal(structure.contactCenter, 0, targetScale);
                sizes[0] = ToColliderLocal(structure.contactSize, 0, targetScale);
            }
        }

        private static Vector3 ToColliderLocal(float[] metres, int offset, Vector3 targetScale)
        {
            return new Vector3(
                DivideByContactScale(metres[offset], targetScale.x),
                DivideByContactScale(metres[offset + 1], targetScale.y),
                DivideByContactScale(metres[offset + 2], targetScale.z));
        }

        /// <summary>True when the object's BoxColliders are exactly the wanted boxes, in order.</summary>
        private static bool CollidersMatch(BoxCollider[] colliders, Vector3[] centers,
            Vector3[] sizes)
        {
            if (colliders.Length != centers.Length) return false;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (Vector3.Distance(colliders[i].center, centers[i]) > PositionToleranceM) return false;
                if (Vector3.Distance(colliders[i].size, sizes[i]) > PositionToleranceM) return false;
            }

            return true;
        }

        /// <summary>
        /// Converts a metres value in the solid's frame into the collider's local units: the
        /// object is scaled to the AABB, so the contact box is divided by that scale.
        /// </summary>
        /// <remarks>
        /// A degenerate axis (a zero-extent solid) would divide by zero; the value is then taken
        /// as unscaled and <see cref="LevelArenaHost"/> refuses the degenerate box with its own
        /// reason code, which is louder than a NaN collider.
        /// </remarks>
        private static float DivideByContactScale(float value, float scale)
        {
            if (Mathf.Abs(scale) <= 1e-6f) return value;
            return value / scale;
        }

        // ------------------------------------------------------ curved ramps ---

        /// <summary>
        /// One vertical arc as its slab segment frames - the deterministic expansion.
        /// </summary>
        /// <param name="bend">"up" or "down"; anything else is refused (the tessellator is vertical).</param>
        /// <param name="ballDiameterStart">The level's smallest ball; its radius is the chord-budget base.</param>
        /// <remarks>
        /// <para>
        /// The arc maths is <see cref="CurveRampMath.TryTessellateVerticalCurve"/> - the same
        /// tessellator as the Python reference (<c>Docs/Design/BallMotor/contact_v2</c>), so
        /// there is no second copy here. Local +Y is the walking-surface normal and local +Z
        /// runs up the slope, which is what <c>Quaternion.LookRotation(tangent, normal)</c>
        /// builds (and what a straight ramp's box already means).
        /// </para>
        /// <para>
        /// A <b>down</b> arc is the up arc in a mirrored frame: the tessellator is called with
        /// <c>up</c> negated, which already yields the descending centres and tangents, and
        /// the surface normal is negated back. The C# API has no bend parameter and is not
        /// this file's to extend, so the two sides share the trick (documented in
        /// <c>placement_core.expand_curve_ramp</c>).
        /// </para>
        /// </remarks>
        public static bool TryExpandCurveRamp(float radiusM, float arcDeg, int segments,
            float widthM, float thicknessM, string bend, Vector3 origin, Quaternion rotation,
            float ballDiameterStart, out Vector3[] centers, out Quaternion[] rotations,
            out Vector3[] sizes)
        {
            centers = new Vector3[0];
            rotations = new Quaternion[0];
            sizes = new Vector3[0];

            bool down = string.Equals(bend, "down", StringComparison.Ordinal);
            if (!string.Equals(bend, "up", StringComparison.Ordinal) && !down)
            {
                return false;
            }

            // The same ceiling ValidateCurve enforces and the same one the Python validator
            // calls curve_arc (placement_core.CURVE_MAX_ARC_DEG = 360): a full turn is the
            // maximum, so an over-turn arc never reaches the tessellator even when a caller
            // (a test, a future importer path) skips the document validation.
            if (!IsPositiveFinite(radiusM) || !IsPositiveFinite(arcDeg) ||
                !IsPositiveFinite(widthM) || !IsPositiveFinite(thicknessM))
            {
                return false;
            }

            if (arcDeg > CurveMaxArcDeg)
            {
                return false;
            }

            int count = ResolveCurveSegments(radiusM, arcDeg, segments, ballDiameterStart);
            if (count < 1)
            {
                return false;
            }

            var bufferCenters = new Vector3[count];
            var axisX = new Vector3[count];
            var axisY = new Vector3[count];
            var axisZ = new Vector3[count];
            var halfExtents = new Vector3[count];
            var surfaceIds = new int[count];
            var roles = new int[count];
            Vector3 forward = rotation * Vector3.forward;
            Vector3 up = rotation * Vector3.up;
            if (down)
            {
                up = -up;
            }

            float deltaDeg;
            float sagittaM;
            float normalJumpDeg;
            float segmentLengthM;
            float overlap;
            if (!CurveRampMath.TryTessellateVerticalCurve(origin, forward, up, radiusM, arcDeg,
                    count, widthM, thicknessM, bufferCenters, axisX, axisY, axisZ, halfExtents,
                    surfaceIds, roles, out deltaDeg, out sagittaM, out normalJumpDeg,
                    out segmentLengthM, out overlap))
            {
                return false;
            }

            centers = new Vector3[count];
            rotations = new Quaternion[count];
            sizes = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 normal = down ? -axisY[i] : axisY[i];
                centers[i] = bufferCenters[i];
                rotations[i] = Quaternion.LookRotation(axisZ[i], normal);
                sizes[i] = new Vector3(halfExtents[i].x * 2f, halfExtents[i].y * 2f,
                    halfExtents[i].z * 2f);
            }

            return true;
        }

        /// <summary>
        /// Creates, updates and deletes the slab children of one curved ramp.
        /// </summary>
        /// <remarks>
        /// The parent is the authored structure object (a marker with no collider); each
        /// <c>&lt;id&gt;_&lt;nn&gt;</c> child is a cube with a <see cref="BoxCollider"/>, which is
        /// exactly what a straight ramp is. Writes are per value (pose, collider, material),
        /// so a second import reports <c>+0 ~0 -0</c>; a shorter arc deletes the extra
        /// children, and the segment counts are reported separately from the structure count
        /// (one logical ramp, N segments - <c>CONTACT_MODEL_V2</c> 5.7).
        /// </remarks>
        private static bool ApplyCurveSegments(StructureEntry structure, Transform parent,
            PlacementDocument doc, PlacementImportReport report)
        {
            if (!IsCurveRole(structure))
            {
                return true;
            }

            CurveEntry curve = structure.curve;
            bool hasCurve = HasCurveContent(curve);
            string bend = !hasCurve || string.IsNullOrEmpty(curve.bend) ? "up" : curve.bend;
            Vector3[] centers;
            Quaternion[] rotations;
            Vector3[] sizes;
            if (!hasCurve || !TryExpandCurveRamp(curve.radius_m, curve.arc_deg, curve.segments,
                    curve.width_m, curve.thickness_m, bend, ToVector(structure.center),
                    ToQuaternion(structure.rotationQuat),
                    doc != null && doc.level != null ? doc.level.ballDiameterStart : 0.3f,
                    out centers, out rotations, out sizes))
            {
                report.Error("structure '" + structure.id + "' [curve_expand]: the arc could not be " +
                             "expanded (check radius_m, arc_deg, width_m, thickness_m, bend)");
                return false;
            }

            var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            int created = 0;
            int updated = 0;
            for (int i = 0; i < centers.Length; i++)
            {
                string name = CurveSegmentName(structure.id, i);
                wanted.Add(name);
                Transform found = FindChild(parent, name);
                GameObject segment = found != null ? found.gameObject : null;
                bool isNew = segment == null;
                if (isNew)
                {
                    segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    segment.name = name;
                    Undo.RegisterCreatedObjectUndo(segment, UndoLabel);
                    Undo.SetTransformParent(segment.transform, parent, UndoLabel);
                }

                bool changed = isNew ||
                               !PoseMatches(segment.transform, centers[i], rotations[i], sizes[i]);
                if (changed)
                {
                    Undo.RecordObject(segment.transform, UndoLabel);
                    segment.transform.position = centers[i];
                    segment.transform.rotation = rotations[i];
                    segment.transform.localScale = sizes[i];
                }

                BoxCollider collider = segment.GetComponent<BoxCollider>();
                if (collider == null)
                {
                    collider = Undo.AddComponent<BoxCollider>(segment);
                    changed = true;
                }

                if (collider.center != Vector3.zero || collider.size != Vector3.one)
                {
                    Undo.RecordObject(collider, UndoLabel);
                    collider.center = Vector3.zero;
                    collider.size = Vector3.one;
                    changed = true;
                }

                // Same placeholder the box/ramp bodies get: only a built-in default material is
                // replaced, hand art is left alone.
                if (EnsureStructureMaterial(segment.GetComponent<Renderer>(), PropMaterialPath,
                        materials, report))
                {
                    changed = true;
                }

                if (isNew)
                {
                    created++;
                }
                else if (changed)
                {
                    updated++;
                }
            }

            int deleted = 0;
            string prefix = structure.id + CurveSegmentSeparator;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                if (!child.name.StartsWith(prefix, StringComparison.Ordinal) ||
                    wanted.Contains(child.name))
                {
                    continue;
                }

                Undo.DestroyObjectImmediate(child.gameObject);
                deleted++;
            }

            report.CurveRamps++;
            report.CurveSegments += centers.Length;
            report.CurveSegmentsCreated += created;
            report.CurveSegmentsUpdated += updated;
            report.CurveSegmentsDeleted += deleted;
            // Part of the structures' change count so the idempotency gate ("other=0" on a
            // settled scene) stays honest about the segment objects too.
            report.StructuresCreated += created;
            report.StructuresUpdated += updated;
            report.StructuresDeleted += deleted;
            if (created > 0 || updated > 0 || deleted > 0)
            {
                report.Note("curve '" + structure.id + "': " + centers.Length + " slab segments (+" +
                            created + " ~" + updated + " -" + deleted + ")");
            }

            return true;
        }

        /// <summary>The child name of one curve segment: <c>&lt;id&gt;_00</c>, <c>_01</c>, ...</summary>
        private static string CurveSegmentName(string structureId, int index)
        {
            return structureId + CurveSegmentSeparator +
                   index.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>True when a segment's world pose is already the wanted one.</summary>
        private static bool PoseMatches(Transform instance, Vector3 position, Quaternion rotation,
            Vector3 scale)
        {
            return Vector3.Distance(instance.position, position) <= PositionToleranceM &&
                   Mathf.Abs(Quaternion.Dot(instance.rotation, rotation)) >= RotationDotTolerance &&
                   Vector3.Distance(instance.localScale, scale) <= PositionToleranceM;
        }

        // ------------------------------------------------- structure bodies ---

        /// <summary>
        /// Gives the marker roles a visible body (<c>Visual</c> cube + <see cref="BoxCollider"/>)
        /// and puts the role's default material on every structure body, box and ramp included.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why a child, not the marker: <see cref="LevelArenaHost"/> reads the marker's world
        /// position as a plane offset and its local +Z as a wall normal, and
        /// <see cref="CollectProps"/>-style scans under <c>Props</c> treat "has a Renderer" as
        /// "is a prop". A child leaves all of that untouched - the marker keeps scale 1, no
        /// Renderer, no collider - while the player gets something to stand on and walk into.
        /// The compilable rule this implements is "the importer produces what the player
        /// collides with; the arena still reads markers" (PICNIC_CORNER_LEVEL_ONE 5.3 G11).
        /// </para>
        /// <para>
        /// Why the body is not a <c>box</c> to the arena: a boundary is an infinite plane to the
        /// ball, so registering a cube for it would add a walkable ledge and spend a box slot
        /// (design 4.3). <see cref="BindArenaHost"/> fills <c>staticBoxes</c> from the <c>box</c>
        /// role alone and never walks this subtree.
        /// </para>
        /// <para>
        /// Idempotency is per written value: pose, collider shape and material are each compared
        /// before they are written, so a second run reports nothing. A body that already carries
        /// a material this tool did not put there is left alone - the placeholder is a default,
        /// not an overwrite of hand art - and a box/ramp cube only gets a material when it still
        /// carries Unity's built-in one.
        /// </para>
        /// </remarks>
        private static bool ApplyStructureVisuals(PlacementDocument doc, Transform structuresRoot,
            HashSet<string> existedBefore, PlacementImportReport report)
        {
            if (doc.structures == null)
            {
                return true;
            }

            // The ground's footprint is grown to the boundary planes, so the floor reaches the
            // walls instead of stopping short of them.
            Vector3[] boundaryCenters = CollectBoundaryCenters(doc);
            var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
            bool ok = true;
            int bodiesAdded = 0;
            int bodiesUpdated = 0;

            for (int i = 0; i < doc.structures.Length; i++)
            {
                StructureEntry structure = doc.structures[i];
                if (structure == null)
                {
                    continue;   // ValidateDocument already refused it
                }

                Transform instance = FindChild(structuresRoot, structure.id);
                if (instance == null)
                {
                    continue;   // the loop above either built it or refused the run
                }

                if (IsCurveRole(structure))
                {
                    continue;   // a curve's visible bodies are its segment children
                }

                bool preexisting = existedBefore != null && existedBefore.Contains(structure.id);

                if (IsMarkerRole(structure))
                {
                    bool created;
                    bool changed;
                    if (!ApplyMarkerVisual(structure, instance, boundaryCenters, materials, report,
                            out created, out changed))
                    {
                        ok = false;
                        continue;
                    }

                    if (!changed || !preexisting)
                    {
                        continue;   // nothing to do, or part of an object this run created
                    }

                    if (created)
                    {
                        bodiesAdded++;
                    }
                    else
                    {
                        bodiesUpdated++;
                    }
                }
                else
                {
                    if (structure.bodyIsProp)
                    {
                        continue;   // collision only: the prop carries the model, the cube is hidden
                    }

                    // A box/ramp body is the object itself, so only its material is this pass's
                    // business: the pose and the collider belong to the box/ramp path.
                    bool dressed = EnsureStructureMaterial(instance.GetComponent<Renderer>(),
                        PropMaterialPath, materials, report);
                    if (dressed && preexisting)
                    {
                        bodiesUpdated++;
                    }
                }
            }

            if (bodiesAdded > 0 || bodiesUpdated > 0)
            {
                // The update counts belong to the structures (the marker ids own the rows), so
                // the idempotency gate keeps reading "+0 ~0 -0 other=0" on a settled scene.
                report.StructuresUpdated += bodiesAdded + bodiesUpdated;
                report.Note("structure bodies: +" + bodiesAdded + " ~" + bodiesUpdated +
                            " (a ground/boundary marker gained or repaired its Visual child; the marker " +
                            "Transform itself is unchanged)");
            }

            return ok;
        }

        /// <summary>
        /// Creates or repairs one marker's <c>Visual</c> child.
        /// </summary>
        /// <returns>False when the structure was refused (a degenerate size, a missing material
        /// or a hand-broken child); <paramref name="created"/> and <paramref name="changed"/> say
        /// whether the child is new and whether anything was written.</returns>
        private static bool ApplyMarkerVisual(StructureEntry structure, Transform marker,
            Vector3[] boundaryCenters, Dictionary<string, Material> materials,
            PlacementImportReport report, out bool created, out bool changed)
        {
            created = false;
            changed = false;

            Vector3 authoredSize = new Vector3(structure.size[0], structure.size[1], structure.size[2]);
            Quaternion authored = ToQuaternion(structure.rotationQuat);
            Quaternion markerRotation = marker.rotation;
            bool floorGrown = false;
            string floorGrownFrom = null;

            if (string.Equals(structure.role, "ground", StringComparison.Ordinal))
            {
                // The authored size is the designer's floor rectangle; the boundary planes are
                // where the arena actually ends. Grow the slab to reach them when they lie
                // outside it, so a level can shrink its ground block without leaving the walls
                // standing over a hole.
                Vector2 halfExtents = CoverHalfExtentsInPlane(marker.position, markerRotation,
                    authoredSize, boundaryCenters);
                var covered = new Vector3(halfExtents.x * 2f, authoredSize.y, halfExtents.y * 2f);
                if (Vector3.Distance(covered, authoredSize) > PositionToleranceM)
                {
                    floorGrownFrom = Format(authoredSize);
                    authoredSize = covered;
                    floorGrown = true;
                }
            }

            Vector3 localPosition;
            Quaternion localRotation;
            Vector3 localScale;
            if (!TryComputePlaneBodyPose(authored, markerRotation, authoredSize, out localPosition,
                    out localRotation, out localScale))
            {
                report.Error("structure '" + structure.id + "' has no usable size for a visible body (" +
                             "size=[" + Format(authoredSize) + "]; every axis must be finite and " +
                             "positive). The marker is unchanged, but nothing visible was created");
                return false;
            }

            Material material = ResolveStructureMaterial(
                string.Equals(structure.role, "ground", StringComparison.Ordinal)
                    ? GroundMaterialPath
                    : WallMaterialPath, materials, report);
            if (material == null)
            {
                return false;   // the missing asset is already an error, with its path
            }

            Transform visual = FindChild(marker, StructureVisualName);
            if (visual == null)
            {
                GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                primitive.name = StructureVisualName;
                Undo.RegisterCreatedObjectUndo(primitive, UndoLabel);
                Undo.SetTransformParent(primitive.transform, marker, UndoLabel);
                visual = primitive.transform;
                created = true;
                changed = true;
            }

            if (!LocalPoseMatches(visual, localPosition, localRotation, localScale))
            {
                Undo.RecordObject(visual, UndoLabel);
                visual.localPosition = localPosition;
                visual.localRotation = localRotation;
                visual.localScale = localScale;
                changed = true;
            }

            // The collider sits on the scaled cube, so a unit box is exactly the authored size.
            // It is player geometry only: the ball reads the analytic plane and never this box.
            BoxCollider collider = visual.GetComponent<BoxCollider>();
            if (collider == null)
            {
                collider = Undo.AddComponent<BoxCollider>(visual.gameObject);
                changed = true;
            }

            if (collider.center != Vector3.zero || collider.size != Vector3.one)
            {
                Undo.RecordObject(collider, UndoLabel);
                collider.center = Vector3.zero;
                collider.size = Vector3.one;
                changed = true;
            }

            MeshRenderer renderer = visual.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                // A cube primitive always has one, so this is a hand edit (or a replaced mesh).
                report.Error("structure '" + structure.id + "': the " + StructureVisualName +
                             " child has no MeshRenderer; it cannot be made visible. Delete that child " +
                             "and import again");
                return false;
            }

            if (renderer.sharedMaterial != material)
            {
                Undo.RecordObject(renderer, UndoLabel);
                renderer.sharedMaterial = material;
                changed = true;
            }

            // The original game's activity boundary is invisible: a boundary keeps its body
            // collider (player geometry; the ball reads the analytic plane) but is never drawn.
            // The ground stays visible. Owner 2026-11-17 (a tall wall ring around the play area).
            bool shouldRender = !string.Equals(structure.role, "boundary", StringComparison.Ordinal);
            if (renderer.enabled != shouldRender)
            {
                Undo.RecordObject(renderer, UndoLabel);
                renderer.enabled = shouldRender;
                changed = true;
            }

            // Reported only when the growth actually wrote something: a settled scene's report
            // stays free of "and also it grew" lines that changed nothing.
            if (changed && floorGrown)
            {
                report.Note("ground visual grown from " + floorGrownFrom + " to " +
                            Format(authoredSize) + " to cover the boundary planes (the authored size " +
                            "was smaller than the field)");
            }

            return true;
        }

        /// <summary>
        /// Puts <paramref name="path"/>'s material on a body that has no material of its own.
        /// </summary>
        /// <returns>True when the material was assigned.</returns>
        /// <remarks>
        /// Only the built-in default counts as "has none": a box or ramp the operator already
        /// dressed keeps its material, and the idempotency gate stays honest because the second
        /// run finds the role material already in place.
        /// </remarks>
        private static bool EnsureStructureMaterial(Renderer renderer, string path,
            Dictionary<string, Material> materials, PlacementImportReport report)
        {
            if (renderer == null)
            {
                return false;
            }

            Material current = renderer.sharedMaterial;
            if (current != null &&
                !string.Equals(current.name, BuiltinDefaultMaterialName, StringComparison.Ordinal) &&
                !string.Equals(current.name, BuiltinDefaultDiffuseMaterialName, StringComparison.Ordinal))
            {
                return false;
            }

            Material material = ResolveStructureMaterial(path, materials, report);
            if (material == null)
            {
                return false;
            }

            Undo.RecordObject(renderer, UndoLabel);
            renderer.sharedMaterial = material;
            return true;
        }

        /// <summary>
        /// Loads one placeholder material, once per run, and reports a missing asset once.
        /// </summary>
        private static Material ResolveStructureMaterial(string path, Dictionary<string, Material> cache,
            PlacementImportReport report)
        {
            Material material;
            if (cache.TryGetValue(path, out material))
            {
                return material;   // may be null: the error was reported on the first miss
            }

            material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                report.Error("no material at " + path + "; the structure body was created without its " +
                             "placeholder material. Move or restore the asset, then import again");
            }

            cache[path] = material;
            return material;
        }

        /// <summary>
        /// The <c>Visual</c> child's local pose for a <c>ground</c> or <c>boundary</c> marker.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Pure on purpose: <c>Tests/EditMode/LevelBuild/PlacementImporterVisualTests.cs</c>
        /// exercises it by reflection, because this file compiles into the editor assembly and
        /// the test assembly references only <c>VRCKatamari.Runtime</c>.
        /// </para>
        /// <para>
        /// Two facts decide the answer, and both are the document's convention, not a style
        /// choice. First, a plane role declares its normal as the authored local <b>+Y</b> (the
        /// validator's rule), so <c>size</c> reads "in-plane X by thickness by in-plane Z" and
        /// the plane is the authored local <c>y = 0</c> plane through the authored origin.
        /// Second, the visible face must BE that plane - a floor's top face, a wall's inner face
        /// - so the body is the authored box shifted half a thickness along authored
        /// <b>-Y</b>: downward for a floor, outward for a wall.
        /// </para>
        /// <para>
        /// Rotation is the marker's own re-roll of the authored frame.
        /// <see cref="ApplyStructure"/> turns a boundary marker to <c>LookRotation(inward, up)</c>,
        /// which discards the authored roll: for the picnic level's west/east walls the authored
        /// <c>size[0]</c> is the height, for its south/north walls it is the length. Carrying the
        /// relative rotation into the body is what keeps <c>size</c> meaning what the designer
        /// drew instead of guessing which axis is up.
        /// </para>
        /// </remarks>
        public static bool TryComputePlaneBodyPose(Quaternion authoredRotation, Quaternion markerRotation,
            Vector3 authoredSize, out Vector3 localPosition, out Quaternion localRotation,
            out Vector3 localScale)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            localScale = Vector3.one;

            float thickness = authoredSize.y;
            if (!IsPositiveFinite(authoredSize.x) || !IsPositiveFinite(thickness) ||
                !IsPositiveFinite(authoredSize.z))
            {
                return false;
            }

            localScale = new Vector3(authoredSize.x, thickness, authoredSize.z);

            // The authored frame as the marker sees it: the two differ only by a roll about the
            // plane normal, so this stays a rotation about the marker's own +Z.
            localRotation = Quaternion.Inverse(markerRotation) * authoredRotation;
            localPosition = localRotation * new Vector3(0f, -thickness * 0.5f, 0f);
            return true;
        }

        /// <summary>
        /// Half extents, in the plane's own X and Z, a body needs so that every point in
        /// <paramref name="mustCoverWorld"/> lies under it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The input is the boundary planes' origins, not a separate bounds field.
        /// <c>level.bounds</c> would be the obvious source, but it is written as a nested
        /// <c>[[minX, minZ], [maxX, maxZ]]</c> array and <c>JsonUtility</c> does not map jagged
        /// arrays, so reading it would mean a second JSON parser in this tool for one field.
        /// The boundary planes are already parsed, they are where the arena actually ends, and
        /// they are the walls the player can see: covering them covers the field the player can
        /// reach. When a flat bounds block ever lands in the schema, this is the one place to
        /// change.
        /// </para>
        /// <para>
        /// Pure: no scene, no asset, so the EditMode test can pin it.
        /// </para>
        /// </remarks>
        public static Vector2 CoverHalfExtentsInPlane(Vector3 planeOrigin, Quaternion planeRotation,
            Vector3 authoredSize, Vector3[] mustCoverWorld)
        {
            float halfX = authoredSize.x * 0.5f;
            float halfZ = authoredSize.z * 0.5f;
            if (mustCoverWorld == null)
            {
                return new Vector2(halfX, halfZ);
            }

            Vector3 axisX = planeRotation * Vector3.right;
            Vector3 axisZ = planeRotation * Vector3.forward;
            for (int i = 0; i < mustCoverWorld.Length; i++)
            {
                Vector3 offset = mustCoverWorld[i] - planeOrigin;
                float alongX = Mathf.Abs(Vector3.Dot(offset, axisX));
                float alongZ = Mathf.Abs(Vector3.Dot(offset, axisZ));
                if (alongX > halfX)
                {
                    halfX = alongX;
                }

                if (alongZ > halfZ)
                {
                    halfZ = alongZ;
                }
            }

            return new Vector2(halfX, halfZ);
        }

        private static Vector3[] CollectBoundaryCenters(PlacementDocument doc)
        {
            var centers = new List<Vector3>();
            for (int i = 0; i < doc.structures.Length; i++)
            {
                StructureEntry structure = doc.structures[i];
                if (structure == null || !IsVector(structure.center, 3) ||
                    !string.Equals(structure.role, "boundary", StringComparison.Ordinal))
                {
                    continue;
                }

                centers.Add(ToVector(structure.center));
            }

            return centers.ToArray();
        }

        private static HashSet<string> SnapshotStructureIds(Transform structuresRoot)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < structuresRoot.childCount; i++)
            {
                ids.Add(structuresRoot.GetChild(i).name);
            }

            return ids;
        }

        private static bool LocalPoseMatches(Transform body, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale)
        {
            return Vector3.Distance(body.localPosition, localPosition) <= PositionToleranceM &&
                   Vector3.Distance(body.localScale, localScale) <= PositionToleranceM &&
                   Mathf.Abs(Quaternion.Dot(body.localRotation, localRotation)) >= RotationDotTolerance;
        }

        /// <summary>
        /// Content predicates for JsonUtility nested blocks. An absent nested [Serializable]
        /// field is materialised as a default instance, so `field != null` is always true;
        /// presence must be judged by content.
        /// </summary>
        private static bool HasCurveContent(CurveEntry curve)
        {
            if (curve == null) return false;
            return curve.radius_m != 0f || curve.arc_deg != 0f || curve.segments != 0 ||
                   curve.width_m != 0f || curve.thickness_m != 0f ||
                   !string.IsNullOrEmpty(curve.bend);
        }

        private static bool HasBudgetContent(BudgetBlock budget)
        {
            if (budget == null) return false;
            return budget.planes != 0 || budget.boxes != 0 || budget.ramps != 0 ||
                   budget.blocking != 0;
        }

        private static bool IsPositiveFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        /// <summary>"x,y,z" for a report line; invariant, so two runs print the same text.</summary>
        private static string Format(Vector3 size)
        {
            return size.x.ToString("0.###", CultureInfo.InvariantCulture) + "x" +
                   size.y.ToString("0.###", CultureInfo.InvariantCulture) + "x" +
                   size.z.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- sync ---

        /// <summary>
        /// Hands the document's declared per-prop columns to the pickup sync: growth (the
        /// authority), the volume factor, the penalty and the collider proxies.
        /// </summary>
        /// <remarks>
        /// One method because the v1 and v2 documents both reach it - the v2 adapter fills the
        /// same DTO fields, so "how a declared prop reaches the host" has one implementation.
        /// The sync is the only writer of the host arrays (OPEN_DECISIONS (q) U2).
        /// </remarks>
        private static void TrySyncDeclaredFields(PlacementDocument doc, Transform levelRoot,
            Transform propsRoot, PlacementImportReport report, ISet<string> exemptFromField,
            string referencePlacementPath = null)
        {
            Dictionary<string, long> declaredGrowthMm3 = BuildDeclaredGrowthMm3(doc);
            Dictionary<string, float> declaredVolumeK = BuildDeclaredVolumeK(doc);
            Dictionary<string, float> declaredVolumePenalty = BuildDeclaredVolumePenalty(doc);
            Dictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> declaredProxies =
                BuildDeclaredProxies(doc, report);
            report.GrowthMm3Declared = SumDeclaredGrowthMm3(doc);
            Dictionary<string, string> declaredParents = BuildDeclaredParents(doc);
            TrySyncPickupField(levelRoot, report.ArenaId, propsRoot, declaredGrowthMm3,
                declaredVolumeK, declaredVolumePenalty, declaredProxies, exemptFromField,
                declaredParents, report, doc, referencePlacementPath);
        }

        /// <summary>
        /// Child item id to parent item id for every prop that declares a <c>parent</c>:
        /// the single-parent pointer the sync turns into the host's <c>propParentIndex</c>
        /// column (PROP_BEHAVIOURS 10.9). The document is authoritative: an empty map means
        /// "no prop has a parent" and clears the column.
        /// </summary>
        private static Dictionary<string, string> BuildDeclaredParents(PlacementDocument doc)
        {
            var declared = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.props == null)
            {
                return declared;
            }

            for (int i = 0; i < doc.props.Length; i++)
            {
                PropEntry prop = doc.props[i];
                if (prop == null || string.IsNullOrEmpty(prop.itemId) ||
                    string.IsNullOrEmpty(prop.parent))
                {
                    continue;
                }

                declared[prop.itemId] = prop.parent;
            }

            return declared;
        }

        private static bool TrySyncPickupField(Transform levelRoot, string arenaId, Transform propsRoot,
            IDictionary<string, long> declaredGrowthMm3, IDictionary<string, float> declaredVolumeK,
            IDictionary<string, float> declaredVolumePenalty,
            IDictionary<string, PickupFieldAuthoringRules.PickupProxyDefinition> declaredProxies,
            ISet<string> exemptFromField, IDictionary<string, string> declaredParents,
            PlacementImportReport report, PlacementDocument doc, string referencePlacementPath)
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

            // A whole-level document owns fresh FBX instances (the model import has
            // addColliders: 0), so the sync must create the one collider per prop that the
            // host disables on absorb. A cell document is hand-authored scenery: it keeps
            // the old "wire what is already there" behaviour.
            bool createMissingColliders = string.IsNullOrEmpty(arenaId);
            PickupFieldSyncReport sync = PickupFieldSync.SyncFromChildren(host, propsRoot,
                declaredGrowthMm3, declaredVolumeK, declaredVolumePenalty, declaredProxies,
                createMissingColliders, exemptFromField, declaredParents);
            // The sync's own one-block summary is one of the design's expected console lines
            // and it carries the authoritative growth read-back:
            //   growthMm3: written=<sum>, fromFile=<n>, fromBounds=<n>, read-back: verified
            // The menu path logs it itself; this headless path used to swallow it, so the
            // operator saw only the note below (O8 finding).
            sync.Log();
            report.SyncRan = true;
            report.SyncPropCount = sync.PropCount;
            report.SyncRejected = sync.Rejected.Count;
            report.UdonCopyIssued = sync.UdonCopyIssued;
            report.GrowthFileRows = sync.GrowthFileRows;
            report.GrowthBoundsRows = sync.GrowthBoundsRows;
            report.GrowthVerified = sync.GrowthVerified;
            report.ProxyVerified = sync.ProxyVerified;
            report.SyncCollidersCreated = sync.CollidersCreated;

            for (int i = 0; i < sync.GrowthMismatches.Count && i < 20; i++)
            {
                report.GrowthProblems.Add(sync.GrowthMismatches[i]);
            }

            // Rejections are the CAUSE of a failed sync, so they are printed BEFORE the
            // failure line. The early return below used to skip them, which is why a dropped
            // row showed up as "declared X, no row" with no reason anywhere in the log.
            for (int i = 0; i < sync.Rejected.Count && i < 10; i++)
            {
                report.Error("sync rejected " + sync.Rejected[i].Path + ": " +
                             sync.Rejected[i].Reason);
            }

            if (sync.Rejected.Count > 10)
            {
                report.Error("... " + (sync.Rejected.Count - 10) + " more sync rejection(s)");
            }

            if (!sync.Success)
            {
                report.Error("PickupFieldSync failed" +
                             (sync.Rejected.Count > 0
                                 ? " (" + sync.Rejected.Count + " rejected row(s), listed above)"
                                 : "") + ": " + sync.Failure);
                return false;
            }

            SyncAreaCollision(host, doc, exemptFromField, report);
            SyncAbsorbAudio(host, doc, referencePlacementPath, report);
            SyncCatchNames(host, doc, referencePlacementPath, report);

            report.Note("pickup sync wrote " + sync.PropCount + " row(s) into 8 base columns " +
                        "(+ the optional proxy columns), including propGrowthMm3 (the authored " +
                        "growth, verified above) and propVolumeK (per-item volume factor): " +
                        (declaredVolumeK == null ? 0 : declaredVolumeK.Count) +
                        " row(s) declared a k, the rest play at the original's 0.70 default");

            if (sync.CollidersCreated > 0)
            {
                report.Note("pickup sync added " + sync.CollidersCreated +
                            " BoxCollider(s) on props that had none, from each prop's visual local " +
                            "AABB; propColliders is now wired for every row");
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
        /// <c>staticBoxes</c> (BoxColliders whose bounds the host bakes once),
        /// <c>rampBoxes</c> (BoxColliders the host bakes into support-only oriented planes),
        /// and <c>allowedKinds</c> (the whitelist the motor receives). A ramp is deliberately
        /// never in <c>staticBoxes</c>: a sweep box at the ramp foot is an invisible wall.
        /// </para>
        /// <para>
        /// Writing an UdonSharp behaviour is a two-step contract: fill the proxy's serialized
        /// fields, then copy the proxy into the backing <c>UdonBehaviour</c>; the copy is what
        /// makes the values exist at runtime. The same sequence as
        /// <see cref="PickupFieldSync"/>.
        /// </para>
        /// </remarks>
        private static void BindArenaHost(PlacementDocument doc, Transform levelRoot,
            Transform structuresRoot, PlacementImportReport report, Transform[] activityWalls,
            bool hasGroundY, float groundY)
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
            SerializedProperty ramps = serialized.FindProperty(RampBoxesField);
            SerializedProperty kinds = serialized.FindProperty(AllowedKindsField);

            if (groundNormal == null || groundOffset == null || walls == null || boxes == null ||
                ramps == null || kinds == null)
            {
                report.Error("LevelArenaHost is missing one of " + GroundNormalField + "/" +
                             GroundOffsetField + "/" + BoundaryWallsField + "/" + StaticBoxesField +
                             "/" + RampBoxesField + "/" + AllowedKindsField +
                             "; the arena arrays were not filled");
                return;
            }

            var wallList = new List<Transform>();
            var boxList = new List<BoxCollider>();
            // The part index of each box on its object: the name sort below is not stable, so
            // this keeps a split solid's boxes in their document order on every import.
            var boxOrder = new Dictionary<BoxCollider, int>();
            var rampList = new List<BoxCollider>();
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

                    // Keep the visible body and player collider; the ball reads only this bake.
                    if (structure.noBallCollision) continue;

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

                    if (IsCurveRole(structure))
                    {
                        // A curve_ramp is a chain of slabs: every segment collider goes where a
                        // straight ramp's collider goes (rampBoxes), so the arena bakes each one
                        // as a ramp/slab OBB and the host API stays as it is. The parent has no
                        // collider of its own.
                        for (int child = 0; child < found.childCount; child++)
                        {
                            Transform segment = found.GetChild(child);
                            if (!segment.name.StartsWith(structure.id + CurveSegmentSeparator,
                                    StringComparison.Ordinal))
                            {
                                continue;
                            }

                            BoxCollider segmentCollider = segment.GetComponent<BoxCollider>();
                            if (segmentCollider != null)
                            {
                                rampList.Add(segmentCollider);
                            }
                        }

                        continue;
                    }

                    BoxCollider collider = found.GetComponent<BoxCollider>();
                    if (collider == null)
                    {
                        continue;
                    }

                    // A ramp is support-only (P3): it goes into its own array, never into
                    // staticBoxes, because a sweep box of the tilted board is the invisible
                    // wall at the ramp foot (TERRAIN_SUPPORT 5.5).
                    if (string.Equals(structure.role, "ramp", StringComparison.Ordinal))
                    {
                        rampList.Add(collider);
                        continue;
                    }

                    // A split proxy solid (§4 parts) carries one box per part on the same
                    // object; every one of them is a static OBB.
                    BoxCollider[] parts = found.GetComponents<BoxCollider>();
                    for (int part = 0; part < parts.Length; part++)
                    {
                        boxList.Add(parts[part]);
                        boxOrder[parts[part]] = part;
                    }
                }
            }

            if (activityWalls != null)
            {
                // v2's activityArea walls are separate objects from structures (they are a
                // rule, not a body): same plane bake, no box, no support, no visual.
                for (int i = 0; i < activityWalls.Length; i++)
                {
                    if (activityWalls[i] != null) wallList.Add(activityWalls[i]);
                }
            }

            if (!hasGround && hasGroundY)
            {
                // v2 has no role=ground structure: the fallback plane is meta.groundY (§2.2),
                // already checked to sit below the terrain's lowest point. The arena bakes the
                // number the same way it bakes a ground marker's.
                floorNormal = Vector3.up;
                floorOffset = groundY;
                hasGround = true;
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
            boxList.Sort((left, right) =>
            {
                int byName = string.CompareOrdinal(left.name, right.name);
                if (byName != 0) return byName;
                int leftPart;
                int rightPart;
                boxOrder.TryGetValue(left, out leftPart);
                boxOrder.TryGetValue(right, out rightPart);
                return leftPart.CompareTo(rightPart);
            });
            rampList.Sort((left, right) => string.CompareOrdinal(left.name, right.name));

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

            ramps.arraySize = rampList.Count;
            for (int i = 0; i < rampList.Count; i++)
            {
                ramps.GetArrayElementAtIndex(i).objectReferenceValue = rampList[i];
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
            report.ArenaRamps = rampList.Count;
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
        /// Resolves the level's placement document through <see cref="LevelPaths"/>: the
        /// repository root, a nested level id (<c>physics_test_field/C21</c>) and the
        /// "canonical name with v2 content vs placement.v2.json" order are all decided in ONE
        /// place, which the terrain importer uses too.
        /// </summary>
        /// <param name="path">The document's full path.</param>
        /// <param name="tried">
        /// <c>out</c>, exactly like <see cref="LevelPaths.TryResolvePlacementPath"/>: it is
        /// REPLACED by the list that call built, and the importer's callers read it for their
        /// "Searched:" failure message. Nothing is pre-initialised here on purpose - an
        /// <c>out</c> argument discards the caller's list, so a "preserve the caller's
        /// diagnostics" comment would be a lie, and pre-filling it would be dead code.
        /// </param>
        private static bool TryResolvePlacementPath(string levelId, out string path,
            out List<string> tried)
        {
            return LevelPaths.TryResolvePlacementPath(levelId, out path, out tried);
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
                string id = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                if (LevelPaths.IsLevelDirectory(directories[i]))
                {
                    seen.Add(id);
                    string scene = LevelPaths.DeclaredSceneOf(directories[i]);
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
        /// <summary>
        /// True when that level's document is imported into its OWN managed root.
        /// </summary>
        /// <remarks>
        /// A v1 cell declares <c>level.arena</c>; a v2 document ALWAYS gets its own root
        /// (<c>LevelContent/&lt;meta.id&gt;</c>, §17.5), so several v2 documents may share one
        /// scene without registering each other's geometry - which is exactly what the menu's
        /// "several documents name this scene" branch needs to know.
        /// </remarks>
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
                string text = File.ReadAllText(path);
                if (PlacementV2Reader.LooksLikeV2(text))
                {
                    PlacementV2ReadResult v2 = PlacementV2Reader.Read(text);
                    if (v2.Document != null && string.Equals(v2.Document.Schema,
                        PlacementV2Reader.SchemaName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                var doc = JsonUtility.FromJson<PlacementDocument>(text);
                return doc != null && doc.level != null && doc.level.arena != null &&
                       !string.IsNullOrEmpty(doc.level.arena.id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ResolveLevelsFolder()
        {
            return LevelPaths.LevelsFolder();
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

        /// <summary>
        /// Returns the container called <paramref name="name"/> under
        /// <paramref name="parent"/>, creating it when absent, or null when that name is
        /// already used by something that is not a container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A Renderer means "this object is a prop": <see cref="PickupFieldSync"/> stops its
        /// walk there and calls the object a row, so anything parented under it would be
        /// invisible to both the sync and this importer's walk. The old version returned the
        /// first same-named child unconditionally, and a group id that equalled another prop's
        /// item id therefore re-parented the group's children into that prop (8 props lost,
        /// re-created on every import).
        /// </para>
        /// <para>
        /// A collision is an ERROR, not a silently numbered second folder: a duplicate name
        /// makes the next lookup ambiguous, and an ambiguous lookup is exactly how an import
        /// stops being idempotent. The level data has to name its containers differently.
        /// </para>
        /// </remarks>
        private static Transform EnsureFolder(Transform parent, string name,
                                             PlacementImportReport report)
        {
            return EnsureFolderInternal(parent, name, report, false);
        }

        private static Transform EnsureOwnedPropFolder(Transform parent, string name,
                                                       PlacementImportReport report)
        {
            return EnsureFolderInternal(parent, name, report, true);
        }

        private static Transform EnsureFolderInternal(Transform parent, string name,
                                                     PlacementImportReport report, bool ownedPropFolder)
        {
            if (ownedPropFolder && name == PropFolderMarkerName)
            {
                report.Error("the container name '" + name + "' is reserved for placement folder ownership");
                return null;
            }

            Transform existing = FindChild(parent, name);
            if (existing != null)
            {
                if (existing.GetComponent<Renderer>() == null)
                {
                    return existing;
                }

                report.Error("the container '" + name + "' under " + HierarchyPath(parent) +
                             " is a prop (it carries a Renderer), so it cannot own other objects;" +
                             " rename that container in the level data");
                return null;
            }

            var folder = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(folder, UndoLabel);
            Undo.SetTransformParent(folder.transform, parent, UndoLabel);
            if (ownedPropFolder)
            {
                // The mark is serialized with the scene, but stripped from builds. Existing
                // unmarked objects are never adopted merely because their name matches.
                var marker = new GameObject(PropFolderMarkerName);
                marker.tag = PropFolderMarkerTag;
                Undo.RegisterCreatedObjectUndo(marker, UndoLabel);
                Undo.SetTransformParent(marker.transform, folder.transform, UndoLabel);
                report.PropFoldersCreated++;
            }
            return folder.transform;
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

        private static void DeleteEmptyFolders(Transform propsRoot, PlacementImportReport report)
        {
            int removed = 0;
            int unmarkedKept = 0;
            bool insidePrefab = false;
            for (Transform node = propsRoot; node != null; node = node.parent)
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(node.gameObject)) continue;
                insidePrefab = true;
                break;
            }
            if (!insidePrefab) PrunePropFolders(propsRoot, ref removed, ref unmarkedKept);
            report.PropFolderSummary = "[import] folders: " + report.PropFoldersCreated +
                " created-marked, " + removed + " removed, " + unmarkedKept + " unmarked empty nodes kept";
        }

        private static void PrunePropFolders(Transform root, ref int removed, ref int unmarkedKept)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (PrefabUtility.IsPartOfPrefabInstance(child.gameObject) || IsPropFolderMarker(child)) continue;
                PrunePropFolders(child, ref removed, ref unmarkedKept);

                // A new component (even a missing script) or any unmanaged child protects the
                // whole folder; only the marker itself is allowed to be removed with it.
                if (child.childCount > 1 || child.GetComponents<Component>().Length != 1) continue;
                if (child.childCount == 0)
                {
                    unmarkedKept++;
                    continue;
                }
                if (!IsPropFolderMarker(child.GetChild(0))) continue;

                Undo.DestroyObjectImmediate(child.gameObject);
                removed++;
            }
        }

        internal static bool IsPropFolderMarker(Transform node)
        {
            return node.name == PropFolderMarkerName && node.CompareTag(PropFolderMarkerTag) &&
                   node.childCount == 0 && node.GetComponents<Component>().Length == 1 &&
                   !PrefabUtility.IsPartOfPrefabInstance(node.gameObject);
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
