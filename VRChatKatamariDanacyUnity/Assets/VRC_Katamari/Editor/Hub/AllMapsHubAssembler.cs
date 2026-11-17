using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;
using VRCKatamari.BallPresentation;
using VRCKatamari.Editor.BallPresentation;
using VRCKatamari.Editor.LevelBuild;
using VRCKatamari.Hub;
using VRCKatamari.Input;
using VRCKatamari.Labs.PhysicsTestField;
using VRCKatamari.Pickup;
using VRCKatamari.Riding;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;
using Object = UnityEngine.Object;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// Builds the all-maps hub scene from the level list: a copy of the base scene (the ball,
    /// seat, HUD, room, launcher and result card) with each listed level's content moved into
    /// its own island, then the hub assembly on top. Every run rebuilds the output from the
    /// base, so the same inputs give the same scene and the same report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output scene is a product, never edited by hand. When any level's source scene is
    /// under <c>Assets/_RefUnlicensed/</c> (git-ignored reference material), the output must
    /// be too, so a scene that contains reference assets can never land in a committed path.
    /// </para>
    /// <para>
    /// Steps (<c>Docs/Design/StoryMode/ALL_MAPS_HUB.md</c> section 3): refuse while any scene
    /// has unsaved changes (a previous output of this tool is discarded without asking: it is
    /// generated); copy the base scene file over the output, keeping the output's .meta; open
    /// it; import the hub room; move each level into <c>Island_&lt;id&gt;</c> (see
    /// <see cref="IslandImport"/>); write the <see cref="LevelSwitchHost"/> tables from what
    /// the islands contain; run the hub assembly (pads, launcher, return buttons, per-level
    /// round and result tables); copy every written proxy to its Udon program; check the whole
    /// scene; save the output only when the check finds nothing. Source scenes are never
    /// saved.
    /// </para>
    /// <para>
    /// The report: every line starts with <c>[allmaps]</c>, nothing depends on time, the
    /// machine or object ids, and number formatting is culture-independent, so two runs on
    /// unchanged inputs produce the same text character for character.
    /// </para>
    /// <para>
    /// Source facts. A level's start position, ball size and round settings are read from its
    /// source scene before it is moved. The start ball volume is computed from
    /// <c>start.diameterM</c> with <c>SizeModelMath.TrySphereVolumeMm3</c>, never from a source
    /// motor's <c>startVolumeMm3</c> field (a motor with <c>startDiameterWorldM</c> set
    /// overrides that field in its own <c>Start</c>, so the stored value is not the start).
    /// The physics test field's driver and size console are not moved; the driver is only read
    /// for the order it registers its arenas in, its kind whitelist and its spawn.
    /// </para>
    /// </remarks>
    public static class AllMapsHubAssembler
    {
        public const string AssembleMenuPath = "VRC Katamari/Hub/Assemble All-Maps Hub";
        public const string InspectMenuPath = "VRC Katamari/Hub/Inspect All-Maps Sources";
        public const string FallbackMenuPath = "VRC Katamari/Hub/All-Maps: Use Instantiate Fallback";
        public const string HubId = "AllMapsHub";
        public const string LevelSwitchObjectName = "LevelSwitch";

        private const string Tag = "[allmaps] ";

        [MenuItem(AssembleMenuPath, false, 406)]
        public static void Assemble()
        {
            Debug.Log(AssembleHub(HubId));
        }

        [MenuItem(InspectMenuPath, false, 407)]
        public static void Inspect()
        {
            Debug.Log(InspectSources(HubId));
        }

        [MenuItem(FallbackMenuPath, false, 408)]
        public static void ToggleFallback()
        {
            IslandImport.UseInstantiateFallback = !IslandImport.UseInstantiateFallback;
        }

        [MenuItem(FallbackMenuPath, true)]
        public static bool ToggleFallbackValidate()
        {
            Menu.SetChecked(FallbackMenuPath, IslandImport.UseInstantiateFallback);
            return true;
        }

        // The data one level contributes to the scene, worked out from its island and facts.
        private sealed class LevelData
        {
            public HubLevelCatalog.LevelEntry Entry;
            public int Index;
            public Transform Shell;
            public IslandResult Island;
            public readonly List<LevelArenaHost> Arenas = new List<LevelArenaHost>();
            public float GroundOffset;
            public string[] Kinds = new string[0];
            public TerrainFieldHost Terrain;
            public PickupFieldHost Pickup;
            public Vector3 StartCenterWorld;
            public float StartHeadingDeg;
            public float StartDiameterM;
            public long StartVolumeMm3;
            public HubAssembler.HubLevelPlan Plan;
        }

        // ------------------------------------------------------------ assemble ---

        /// <summary>Assembles the hub with this id and returns the report (also what the menu logs).</summary>
        public static string AssembleHub(string hubId)
        {
            var lines = new List<string>();
            var problems = new List<string>();
            var hc = new HubAssembler.Counters();

            Line(lines, "begin hub '" + hubId + "'");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                problems.Add("exit Play Mode first; the assembly writes scene data");
                return Finish(lines, problems, hc, null);
            }

            // ---- the list ----
            var catalogErrors = new List<string>();
            HubLevelCatalog.CatalogFile catalog;
            if (!HubLevelCatalog.TryLoad(out catalog, catalogErrors))
            {
                problems.AddRange(catalogErrors);
                return Finish(lines, problems, hc, null);
            }
            HubLevelCatalog.HubEntry hub = HubLevelCatalog.FindHub(catalog, hubId);
            if (hub == null)
            {
                problems.Add("hub '" + hubId + "' is not in hubs[] of " + HubLevelCatalog.RepoRelativePath);
                return Finish(lines, problems, hc, null);
            }
            Line(lines, "output=" + hub.scene + " base=" + hub.baseScene + " levels="
                + string.Join(",", hub.levels));

            var levels = new List<HubLevelCatalog.LevelEntry>();
            for (int i = 0; i < hub.levels.Length; i++)
            {
                levels.Add(HubLevelCatalog.FindLevel(catalog, hub.levels[i]));
            }
            if (!CheckPaths(hub, levels, problems)) return Finish(lines, problems, hc, null);

            // ---- scenes: nothing unsaved may be lost; the previous output is generated ----
            if (!CheckNoUnsavedScenes(hub.scene, problems)) return Finish(lines, problems, hc, null);

            // ---- the output: a fresh copy of the base ----
            Scene output;
            if (!OpenFreshOutput(hub, out output, problems)) return Finish(lines, problems, hc, null);
            Line(lines, "opened '" + output.name + "' from the base scene");

            // The shared ball's original core skin, while the scene is still the clean copy
            // (the skin assembler refuses a dirty scene).
            bool skinApplied = AssembleCoreSkin(hub, lines);

            // The hub room first, while the scene is exactly the base's (the importer's
            // known-good context); the levels then move around it.
            HubAssembler.AssembleRoomGeometry(hc);

            // ---- islands ----
            var data = new List<LevelData>();
            for (int i = 0; i < levels.Count; i++)
            {
                HubLevelCatalog.LevelEntry level = levels[i];
                IslandResult island = IslandImport.Import(level, output, hub.baseScene);
                var d = new LevelData();
                d.Entry = level;
                d.Index = i;
                d.Island = island;
                d.Shell = island.Shell;
                data.Add(d);
                for (int p = 0; p < island.Problems.Count; p++) problems.Add(island.Problems[p]);
                if (island.Shell != null)
                {
                    Line(lines, "island " + level.id + ": shell=" + island.Shell.name
                        + " source=" + level.source.scene + " mode=" + island.Mode
                        + " offset=" + IslandMath.Format(level.island.offset)
                        + " roots=" + island.RootsMoved + " objects=" + island.ObjectsMoved
                        + " components=" + island.ComponentsScanned + " remapped=" + island.Remapped
                        + " terrainVertices=" + island.TerrainVerticesShifted
                        + " pathFloats=" + island.PathFloatsShifted);
                    for (int r = 0; r < island.RemapSummary.Count; r++)
                    {
                        Line(lines, "island " + level.id + ": remap " + island.RemapSummary[r]);
                    }
                    for (int r = 0; r < island.Lines.Count; r++)
                    {
                        Line(lines, "island " + level.id + ": " + island.Lines[r]);
                    }
                }
            }
            if (problems.Count > 0) return Finish(lines, problems, hc, output);

            // ---- per-level data ----
            float ratio = BaseSemanticRatio(output);
            long baseVolume = BaseStartVolume(output, ratio);
            for (int i = 0; i < data.Count; i++)
            {
                ResolveLevel(data[i], output, ratio, baseVolume, hc, problems);
            }
            if (problems.Count > 0) return Finish(lines, problems, hc, output);

            // ---- the level switch ----
            LevelSwitchHost levelSwitch = AssembleLevelSwitch(output, data, hc, problems);
            if (levelSwitch == null || problems.Count > 0) return Finish(lines, problems, hc, output);

            // ---- the hub assembly on top ----
            var options = new HubAssembler.HubAssembleOptions();
            options.LevelOrder = hub.levels;
            options.Plans = new Dictionary<string, HubAssembler.HubLevelPlan>();
            options.StartCenters = new Dictionary<string, Vector3>();
            options.SkipRoomGeometry = true;
            for (int i = 0; i < data.Count; i++)
            {
                options.Plans[data[i].Entry.id] = data[i].Plan;
                options.StartCenters[data[i].Entry.id] = data[i].StartCenterWorld;
            }
            HubAssembler.HubRunOutcome outcome = HubAssembler.Run(hc, options);
            if (!outcome.Completed)
            {
                problems.Add("the hub assembly " + outcome.Headline);
                return Finish(lines, problems, hc, output);
            }

            // ---- Udon sync and read-back ----
            var diagLines = new List<string>();
            HubAssembler.CopyAllToUdon(hc);
            VerifyAllTables(outcome, levelSwitch, data, hc, diagLines);

            for (int i = 0; i < diagLines.Count; i++) Line(lines, diagLines[i]);

            // ---- level lines ----
            for (int i = 0; i < data.Count; i++) lines.Add(DescribeLevel(data[i]));

            // ---- self-check ----
            SelfCheck(output, hub, data, outcome, lines, problems);
            if (skinApplied) CheckCoreSkin(output, problems);

            return Finish(lines, problems, hc, output);
        }

        // ------------------------------------------------------------ core skin ---

        // The owner's decision (OPEN_DECISIONS 2026-11-17, CORE_SKIN.md) is that the ball wears
        // the original core, core_01, and the skin follows the collision diameter; which core
        // belongs to which original level is unknown, so there is no per-level skin. The hub has
        // one shared ball, so every level shows the skin, exactly as RefCancer's ball does. The
        // assets live under _RefUnlicensed, so the skin is only applied to an output that is
        // itself under _RefUnlicensed (a committed output must not reference them), and missing
        // assets are a warning: the red base ball stays and the hub still works.
        private static bool AssembleCoreSkin(HubLevelCatalog.HubEntry hub, List<string> lines)
        {
            if (!HubLevelCatalog.IsUnderRefUnlicensed(hub.scene))
            {
                Line(lines, "note: core skin skipped; the output is not under Assets/_RefUnlicensed/");
                return false;
            }
            string project = LevelPaths.ProjectRoot();
            if (!File.Exists(Path.Combine(project, CoreSkinAssembler.ModelPath))
                || !File.Exists(Path.Combine(project, CoreSkinAssembler.TexturePath)))
            {
                Line(lines, "warn: core skin skipped; '" + CoreSkinAssembler.ModelPath + "' and/or '"
                    + CoreSkinAssembler.TexturePath + "' are missing, the ball keeps its base look");
                return false;
            }

            string text = CoreSkinAssembler.AssembleOpenScene();
            bool applied = text.Contains(" core(s); mesh equivalent diameter=");
            Line(lines, (applied ? "skin: " : "warn: core skin not applied: ") + text);
            return applied;
        }

        // The skin is one child named OriginalCoreSkin under the ball's core mesh.
        private static void CheckCoreSkin(Scene output, List<string> problems)
        {
            int count = 0;
            GameObject[] roots = output.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                BallView[] views = roots[r].GetComponentsInChildren<BallView>(true);
                for (int v = 0; v < views.Length; v++)
                {
                    SerializedProperty core = new SerializedObject(views[v]).FindProperty("coreMesh");
                    Transform coreTransform = core == null ? null : core.objectReferenceValue as Transform;
                    if (coreTransform != null && coreTransform.Find(CoreSkinAssembler.SkinName) != null) count++;
                }
            }
            if (count != 1)
            {
                problems.Add("the core skin was applied but " + count + " ball(s) have an '"
                    + CoreSkinAssembler.SkinName + "' under their core mesh (expected 1)");
            }
        }

        // ------------------------------------------------------------ preconditions ---

        private static bool CheckPaths(HubLevelCatalog.HubEntry hub,
            List<HubLevelCatalog.LevelEntry> levels, List<string> problems)
        {
            int before = problems.Count;
            if (!File.Exists(Path.Combine(LevelPaths.ProjectRoot(), hub.baseScene)))
            {
                problems.Add("base scene '" + hub.baseScene + "' does not exist");
            }

            bool anyReference = false;
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] == null || levels[i].source == null) continue;
                if (HubLevelCatalog.IsUnderRefUnlicensed(levels[i].source.scene)) anyReference = true;
                if (string.Equals(levels[i].source.scene, hub.scene, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("level '" + levels[i].id + "' has the output scene as its source");
                }
            }
            if (HubLevelCatalog.IsUnderRefUnlicensed(hub.baseScene)) anyReference = true;
            if (anyReference && !HubLevelCatalog.IsUnderRefUnlicensed(hub.scene))
            {
                problems.Add("a source scene is under Assets/_RefUnlicensed/ (git-ignored reference"
                    + " material), so the output '" + hub.scene + "' must be under"
                    + " Assets/_RefUnlicensed/ too; refusing to write reference content into a"
                    + " committed path");
            }
            if (string.Equals(hub.scene, hub.baseScene, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("the output scene is the base scene; it would overwrite it");
            }
            return problems.Count == before;
        }

        // A scene with unsaved changes is refused, except the previous output of this tool:
        // that one is generated and is rebuilt anyway.
        private static bool CheckNoUnsavedScenes(string outputPath, List<string> problems)
        {
            int before = problems.Count;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty) continue;
                if (string.Equals(scene.path, outputPath, StringComparison.OrdinalIgnoreCase)) continue;
                problems.Add("scene '" + (string.IsNullOrEmpty(scene.path) ? "(untitled)" : scene.path)
                    + "' has unsaved changes; save or discard them first (this tool never saves or"
                    + " discards a scene for you)");
            }
            return problems.Count == before;
        }

        private static bool OpenFreshOutput(HubLevelCatalog.HubEntry hub, out Scene output,
            List<string> problems)
        {
            output = default(Scene);
            string project = LevelPaths.ProjectRoot();
            string baseFull = Path.Combine(project, hub.baseScene);
            string outFull = Path.Combine(project, hub.scene);
            try
            {
                // Closes every scene (the checks above guarantee nothing is lost), so the
                // output file is not open while it is overwritten.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(Path.GetDirectoryName(outFull));
                // Only the scene file is copied: an existing .meta of the output stays, so its
                // GUID does not change between runs.
                File.Copy(baseFull, outFull, true);
                AssetDatabase.Refresh();
                AssetDatabase.ImportAsset(hub.scene, ImportAssetOptions.ForceUpdate);
                output = EditorSceneManager.OpenScene(hub.scene, OpenSceneMode.Single);
            }
            catch (Exception exception)
            {
                problems.Add("cannot create the output scene '" + hub.scene + "': "
                    + exception.Message);
                return false;
            }
            if (!output.IsValid() || !output.isLoaded)
            {
                problems.Add("the output scene '" + hub.scene + "' did not open");
                return false;
            }
            SceneManager.SetActiveScene(output);
            return true;
        }

        // ------------------------------------------------------------ per-level data ---

        private static float BaseSemanticRatio(Scene output)
        {
            KatamariBallMotor motor = IslandImport.FirstIn<KatamariBallMotor>(output);
            if (motor == null) return 1f;
            SerializedProperty ratio = new SerializedObject(motor).FindProperty(
                "semanticDiameterPerCoreDiameter");
            return ratio == null || !(ratio.floatValue > 0f) ? 1f : ratio.floatValue;
        }

        // The base motor's own start size: what a level without a diameter of its own starts with.
        private static long BaseStartVolume(Scene output, float ratio)
        {
            KatamariBallMotor motor = IslandImport.FirstIn<KatamariBallMotor>(output);
            if (motor == null) return 0L;
            var so = new SerializedObject(motor);
            SerializedProperty diameter = so.FindProperty("startDiameterWorldM");
            long volume;
            if (diameter != null && diameter.floatValue > 0f
                && SizeModelMath.TrySphereVolumeMm3(diameter.floatValue, ratio, out volume))
            {
                return volume;
            }
            SerializedProperty stored = so.FindProperty("startVolumeMm3");
            return stored == null ? 0L : stored.longValue;
        }

        private static void ResolveLevel(LevelData d, Scene output, float ratio, long baseVolume,
            HubAssembler.Counters hc, List<string> problems)
        {
            HubLevelCatalog.LevelEntry level = d.Entry;
            IslandSourceFacts facts = d.Island.Facts ?? new IslandSourceFacts();
            string label = "level '" + level.id + "'";
            float[] offset = level.island.offset;

            // ---- arenas: frame first, then the test field's cells in its order, then the rest ----
            var seen = new HashSet<LevelArenaHost>();
            string firstPath = !string.IsNullOrEmpty(level.arenaFirst)
                ? level.arenaFirst : facts.DriverFrameArenaPath;
            AddArena(d, seen, firstPath, label, problems);
            for (int i = 0; i < facts.DriverCellArenaPaths.Count; i++)
            {
                AddArena(d, seen, facts.DriverCellArenaPaths[i], label, problems);
            }
            LevelArenaHost[] all = d.Shell.GetComponentsInChildren<LevelArenaHost>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (seen.Add(all[i])) d.Arenas.Add(all[i]);
            }

            // ---- ground ----
            d.GroundOffset = facts.MotorGroundOffset;
            if (d.Arenas.Count > 0)
            {
                SerializedProperty ground = new SerializedObject(d.Arenas[0]).FindProperty("groundOffset");
                if (ground != null) d.GroundOffset = ground.floatValue;
            }

            // ---- whitelist (the test field's driver sends one; other levels' hosts do) ----
            if (facts.DriverKinds != null && facts.DriverKinds.Length > 0) d.Kinds = facts.DriverKinds;

            // ---- terrain / pickups ----
            TerrainFieldHost[] terrains = d.Shell.GetComponentsInChildren<TerrainFieldHost>(true);
            if (terrains.Length > 1)
            {
                problems.Add(label + " has " + terrains.Length + " TerrainFieldHosts; a level may"
                    + " own at most one (the motor holds one terrain mesh)");
            }
            else if (terrains.Length == 1)
            {
                d.Terrain = terrains[0];
            }
            d.Pickup = ResolvePickup(d, facts);

            // ---- the ball's start ----
            float[] center = null;
            float heading = 0f;
            if (level.start != null && level.start.center != null)
            {
                center = level.start.center;
                if (level.start.hasHeading) heading = level.start.headingDeg;
            }
            else if (facts.HasDriver)
            {
                center = new[] { facts.DriverSpawn.x, facts.DriverSpawn.y, facts.DriverSpawn.z };
                heading = level.start != null && level.start.hasHeading
                    ? level.start.headingDeg : facts.DriverSpawnHeadingDeg;
            }
            else if (facts.HasMotor)
            {
                center = new[] { facts.MotorStartCenter.x, facts.MotorStartCenter.y,
                    facts.MotorStartCenter.z };
                if (level.start != null && level.start.hasHeading) heading = level.start.headingDeg;
            }
            if (center == null)
            {
                problems.Add(label + " has no start: give start.center in the list, or keep a ball"
                    + " motor (or the test field's driver) in its source scene");
                return;
            }
            if (level.start != null && level.start.center != null && facts.HasDriver
                && (Math.Abs(level.start.center[0] - facts.DriverSpawn.x) > 0.01f
                    || Math.Abs(level.start.center[2] - facts.DriverSpawn.z) > 0.01f))
            {
                hc.Notes.Add(label + ": the list's start " + IslandMath.Format(level.start.center)
                    + " differs from the driver's spawn "
                    + IslandMath.Format(new[] { facts.DriverSpawn.x, facts.DriverSpawn.y,
                        facts.DriverSpawn.z }) + "; the list wins");
            }
            float[] world = IslandMath.Add(center, offset);
            d.StartCenterWorld = new Vector3(world[0], world[1], world[2]);
            d.StartHeadingDeg = heading;

            // ---- start size: from the diameter, never from a source motor's volume field ----
            d.StartDiameterM = level.start != null && level.start.diameterM > 0f
                ? level.start.diameterM : facts.MotorStartDiameterM;
            long volume = baseVolume;
            if (d.StartDiameterM > 0f)
            {
                if (!SizeModelMath.TrySphereVolumeMm3(d.StartDiameterM, ratio, out volume))
                {
                    problems.Add(label + ": start diameter " + IslandMath.Num(d.StartDiameterM)
                        + " m is not a usable ball size");
                    return;
                }
            }
            if (volume <= 0L)
            {
                problems.Add(label + ": no start ball size (give start.diameterM, or keep the"
                    + " base motor's)");
                return;
            }
            d.StartVolumeMm3 = volume;

            d.Plan = BuildPlan(d, facts, hc);
        }

        private static void AddArena(LevelData d, HashSet<LevelArenaHost> seen, string path,
            string label, List<string> problems)
        {
            if (string.IsNullOrEmpty(path)) return;
            Transform t = d.Shell.Find(path);
            LevelArenaHost host = t == null ? null : t.GetComponent<LevelArenaHost>();
            if (host == null)
            {
                problems.Add(label + ": '" + path + "' is not a LevelArenaHost in its island");
                return;
            }
            if (seen.Add(host)) d.Arenas.Add(host);
        }

        private static PickupFieldHost ResolvePickup(LevelData d, IslandSourceFacts facts)
        {
            if (!string.IsNullOrEmpty(facts.RoundPickupHostPath))
            {
                Transform t = d.Shell.Find(facts.RoundPickupHostPath);
                PickupFieldHost host = t == null ? null : t.GetComponent<PickupFieldHost>();
                if (host != null) return host;
            }
            PickupFieldHost[] hosts = d.Shell.GetComponentsInChildren<PickupFieldHost>(true);
            return hosts.Length > 0 ? hosts[0] : null;
        }

        // One level's round settings. A free roam level has no goal; a round level takes its
        // source controller's values, then its placement document's (roundFromPlacement),
        // then the list's own round block.
        private static HubAssembler.HubLevelPlan BuildPlan(LevelData d, IslandSourceFacts facts,
            HubAssembler.Counters hc)
        {
            HubLevelCatalog.LevelEntry level = d.Entry;
            var plan = new HubAssembler.HubLevelPlan();
            plan.LevelId = level.id;
            plan.PickupHost = d.Pickup;
            plan.AllowAbort = level.allowAbort;
            plan.CountKind = string.IsNullOrEmpty(facts.RoundCountKind) ? "fruit" : facts.RoundCountKind;
            plan.HasStars = level.stars && !level.IsFreeRoam;

            if (level.IsFreeRoam)
            {
                plan.GoalType = StoryLevelMath.GoalFreeRoam;
                return plan;
            }

            plan.GoalType = facts.HasRound ? facts.RoundGoalType : StoryLevelMath.GoalReachDiameter;
            plan.TargetDiameterM = facts.RoundTargetDiameterM;
            plan.TargetCount = facts.RoundTargetCount;
            plan.TimeLimitSeconds = facts.RoundTimeLimitSeconds;
            plan.KillEnabled = facts.RoundKillEnabled;
            plan.KillPlaneY = facts.RoundKillY;

            if (level.roundFromPlacement)
            {
                HubAssembler.PlacementRoundData placement;
                if (HubAssembler.TryReadPlacementRound(level.id, hc, out placement))
                {
                    if (placement.HasTimeLimit) plan.TimeLimitSeconds = placement.TimeLimitS;
                    if (placement.HasTarget)
                    {
                        plan.GoalType = StoryLevelMath.GoalReachDiameter;
                        plan.TargetDiameterM = placement.TargetDiameterM;
                    }
                    if (placement.HasKillPlane)
                    {
                        plan.KillEnabled = true;
                        plan.KillPlaneY = placement.KillPlaneY;
                    }
                }
            }
            if (level.round != null)
            {
                if (level.round.hasTimeLimit) plan.TimeLimitSeconds = level.round.timeLimitSeconds;
                if (level.round.hasTargetDiameter)
                {
                    plan.GoalType = StoryLevelMath.GoalReachDiameter;
                    plan.TargetDiameterM = level.round.targetDiameterM;
                }
                if (level.round.hasKillPlaneY)
                {
                    plan.KillEnabled = true;
                    plan.KillPlaneY = level.round.killPlaneY;
                }
            }
            return plan;
        }

        // ------------------------------------------------------------ level switch ---

        private static LevelSwitchHost AssembleLevelSwitch(Scene output, List<LevelData> data,
            HubAssembler.Counters hc, List<string> problems)
        {
            KatamariBallMotor motor = IslandImport.FirstIn<KatamariBallMotor>(output);
            if (motor == null)
            {
                problems.Add("the base scene has no KatamariBallMotor for the level switch");
                return null;
            }

            LevelSwitchHost host = IslandImport.FirstIn<LevelSwitchHost>(output);
            if (host == null)
            {
                GameObject go = HubAssembler.FindOrCreateRoot(LevelSwitchObjectName, hc);
                host = HubAssembler.AddUdon<LevelSwitchHost>(go, hc);
                if (host == null)
                {
                    problems.Add("the LevelSwitchHost could not be added (see the problem above)");
                    return null;
                }
            }

            int n = data.Count;
            var pickups = new Object[n];
            var terrains = new Object[n];
            var ids = new string[n];
            var grounds = new float[n];
            var arenaHosts = new List<Object>();
            var arenaStarts = new int[n];
            var arenaCounts = new int[n];
            var kindsFlat = new List<string>();
            var kindsStarts = new int[n];
            var kindsCounts = new int[n];
            var centers = new Vector3[n];
            var headings = new float[n];
            var volumes = new long[n];
            for (int i = 0; i < n; i++)
            {
                LevelData d = data[i];
                pickups[i] = d.Pickup;
                terrains[i] = d.Terrain;
                ids[i] = d.Entry.id;
                grounds[i] = d.GroundOffset;
                arenaStarts[i] = arenaHosts.Count;
                arenaCounts[i] = d.Arenas.Count;
                for (int k = 0; k < d.Arenas.Count; k++) arenaHosts.Add(d.Arenas[k]);
                kindsStarts[i] = kindsFlat.Count;
                kindsCounts[i] = d.Kinds.Length;
                for (int k = 0; k < d.Kinds.Length; k++) kindsFlat.Add(d.Kinds[k]);
                centers[i] = d.StartCenterWorld;
                headings[i] = d.StartHeadingDeg;
                volumes[i] = d.StartVolumeMm3;
            }

            var so = new SerializedObject(host);
            bool changed = false;
            changed |= HubAssembler.SetObject(so, "motor", motor, hc);
            changed |= HubAssembler.SetObjectArray(so, "pickupHosts", pickups, hc);
            changed |= HubAssembler.SetObjectArray(so, "terrainHosts", terrains, hc);
            changed |= HubAssembler.SetStringArray(so, "levelIds", ids, hc);
            changed |= HubAssembler.SetFloatArray(so, "groundOffsets", grounds, hc);
            changed |= HubAssembler.SetObjectArray(so, "arenaHosts", arenaHosts.ToArray(), hc);
            changed |= HubAssembler.SetIntArray(so, "arenaStarts", arenaStarts, hc);
            changed |= HubAssembler.SetIntArray(so, "arenaCounts", arenaCounts, hc);
            changed |= HubAssembler.SetStringArray(so, "kindsFlat", kindsFlat.ToArray(), hc);
            changed |= HubAssembler.SetIntArray(so, "kindsStarts", kindsStarts, hc);
            changed |= HubAssembler.SetIntArray(so, "kindsCounts", kindsCounts, hc);
            changed |= HubAssembler.SetVector3Array(so, "startCenters", centers, hc);
            changed |= HubAssembler.SetFloatArray(so, "startHeadingDegrees", headings, hc);
            changed |= HubAssembler.SetLongArray(so, "startVolumesMm3", volumes, hc);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                hc.Proxies.Add(host);
            }
            return host;
        }

        // ------------------------------------------------------------ table read-back ---

        private static readonly string[] SwitchFields =
        {
            "motor", "pickupHosts", "terrainHosts", "levelIds", "groundOffsets", "arenaHosts",
            "arenaStarts", "arenaCounts", "kindsFlat", "kindsStarts", "kindsCounts",
            "startCenters", "startHeadingDegrees", "startVolumesMm3",
        };

        private static readonly string[] RoundFields =
        {
            "levelIds", "levelGoalTypes", "levelTargetDiameters", "levelTargetCounts",
            "levelTimeLimits", "levelKillEnabled", "levelKillY", "levelCountKinds",
            "levelPickupHosts", "levelAllowAbort",
        };

        private static readonly string[] PresenterFields =
        {
            "levelHasStars", "levelStar2Diameters", "levelStar3Diameters", "levelStar2Counts",
            "levelStar3Counts", "levelPartialBands",
        };

        // A field written through the proxy that the backing table lacks is silently skipped
        // by CopyProxyToUdon, so every new field is read back (Labs/README.md, "two tables").
        private static void VerifyAllTables(HubAssembler.HubRunOutcome outcome,
            LevelSwitchHost levelSwitch, List<LevelData> data, HubAssembler.Counters hc,
            List<string> diag)
        {
            HubAssembler.VerifyTable(levelSwitch, SwitchFields, hc);
            if (outcome.Launcher != null)
            {
                HubAssembler.VerifyTable(outcome.Launcher, new[] { "controller", "levelEntries",
                    "levelNames", "roomSpawn", "rideStation", "rideSeat", "entryTrustMeters",
                    "levelSwitch" }, hc);
            }
            if (outcome.Host != null)
            {
                HubAssembler.VerifyTable(outcome.Host, new[] { "launcher", "controller", "hubCenter",
                    "selectZones", "promptText" }, hc);
            }
            if (outcome.Round != null) HubAssembler.VerifyTable(outcome.Round, RoundFields, hc);
            StoryResultPresenter presenter = IslandImport.FirstIn<StoryResultPresenter>(
                SceneManager.GetActiveScene());
            if (presenter != null) HubAssembler.VerifyTable(presenter, PresenterFields, hc);

            for (int i = 0; i < data.Count; i++)
            {
                if (data[i].Shell == null) continue;
                TerrainFieldHost[] terrains = data[i].Shell.GetComponentsInChildren<TerrainFieldHost>(true);
                for (int t = 0; t < terrains.Length; t++)
                {
                    string where = IslandImport.PathOf(terrains[t].transform);
                    if (!UdonVariableAccess.CheckBool(terrains[t], "deferRegistration", true,
                        where + " (before verify)", diag))
                    {
                        hc.Problems.Add("TerrainFieldHost on '" + where + "' does not have"
                            + " deferRegistration=true in its Udon variables ("
                            + UdonVariableAccess.Describe(terrains[t], "deferRegistration") + "); it would"
                            + " register its terrain in Start and overwrite the active level's");
                    }
                }
            }
        }

        // ------------------------------------------------------------ self-check ---

        private static void SelfCheck(Scene output, HubLevelCatalog.HubEntry hub,
            List<LevelData> data, HubAssembler.HubRunOutcome outcome, List<string> lines,
            List<string> problems)
        {
            // Single infrastructure: exactly one of each, none of the test field's scripts.
            ExpectCount<KatamariBallMotor>(output, 1, problems);
            ExpectCount<DesktopMoveInput>(output, 1, problems);
            ExpectCount<BallRideStation>(output, 1, problems);
            ExpectCount<StoryRoundController>(output, 1, problems);
            ExpectCount<HubLevelLauncher>(output, 1, problems);
            ExpectCount<HubRoomHost>(output, 1, problems);
            ExpectCount<LevelSwitchHost>(output, 1, problems);
            ExpectCount<VRC.SDKBase.VRC_SceneDescriptor>(output, 1, problems);
            ExpectCount<VRC.Core.PipelineManager>(output, 1, problems);
            ExpectCount<PhysicsTestFieldDriver>(output, 0, problems);
            ExpectCount<LabBallSizeSetter>(output, 0, problems);

            // Every arena host belongs to a level (none left to register itself).
            var listed = new HashSet<LevelArenaHost>();
            for (int i = 0; i < data.Count; i++)
            {
                for (int k = 0; k < data[i].Arenas.Count; k++) listed.Add(data[i].Arenas[k]);
            }
            int arenaTotal = 0;
            GameObject[] roots = output.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                LevelArenaHost[] hosts = roots[r].GetComponentsInChildren<LevelArenaHost>(true);
                for (int h = 0; h < hosts.Length; h++)
                {
                    arenaTotal++;
                    if (!listed.Contains(hosts[h]))
                    {
                        problems.Add("LevelArenaHost at '" + IslandImport.PathOf(hosts[h].transform)
                            + "' is not in any level's arena list; it would register itself on"
                            + " the first frame");
                    }
                }
            }

            CheckItemIds(output, problems);
            CheckIslands(output, data, lines, problems);
            CheckStarts(output, data, outcome, lines, problems);
            CheckRespawnHeight(output, data, problems);
            Line(lines, "check: arenaHosts=" + arenaTotal + " listed=" + listed.Count);
        }

        private static void ExpectCount<T>(Scene scene, int expected, List<string> problems)
            where T : Component
        {
            int count = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) count += roots[i].GetComponentsInChildren<T>(true).Length;
            if (count != expected)
            {
                problems.Add("expected " + expected + " " + typeof(T).Name + " in the scene, found " + count);
            }
        }

        private static void CheckItemIds(Scene output, List<string> problems)
        {
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);
            int duplicates = 0;
            string first = null;
            GameObject[] roots = output.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                PickupFieldHost[] hosts = roots[r].GetComponentsInChildren<PickupFieldHost>(true);
                for (int h = 0; h < hosts.Length; h++)
                {
                    SerializedProperty ids = new SerializedObject(hosts[h]).FindProperty("itemIds");
                    if (ids == null || !ids.isArray) continue;
                    string hostPath = IslandImport.PathOf(hosts[h].transform);
                    for (int i = 0; i < ids.arraySize; i++)
                    {
                        string id = ids.GetArrayElementAtIndex(i).stringValue;
                        string other;
                        if (owner.TryGetValue(id, out other))
                        {
                            duplicates++;
                            if (first == null) first = "'" + id + "' in '" + other + "' and '" + hostPath + "'";
                        }
                        else
                        {
                            owner[id] = hostPath;
                        }
                    }
                }
            }
            if (duplicates > 0)
            {
                problems.Add(duplicates + " item id(s) repeat across pickup hosts, first " + first
                    + "; the host of another level would claim this level's absorb events");
            }
        }

        private static void CheckIslands(Scene output, List<LevelData> data, List<string> lines,
            List<string> problems)
        {
            Physics.SyncTransforms();
            var mins = new List<float[]>();
            var maxs = new List<float[]>();
            var names = new List<string>();
            for (int i = 0; i < data.Count; i++)
            {
                float[] min;
                float[] max;
                if (!IslandImport.TryBounds(data[i].Shell, out min, out max))
                {
                    problems.Add("island '" + data[i].Shell.name + "' has no renderer or collider bounds");
                    continue;
                }
                mins.Add(min);
                maxs.Add(max);
                names.Add(data[i].Shell.name);
                Line(lines, "bounds " + data[i].Shell.name + " " + IslandMath.Format(min) + ".."
                    + IslandMath.Format(max));
                if (!IslandMath.IsWithinCoordinateRange(min, max, IslandMath.MaxAbsCoordinateM))
                {
                    problems.Add("island '" + data[i].Shell.name + "' reaches "
                        + IslandMath.Num(IslandMath.MaxAbsCoordinate(min, max)) + " m from the origin; float"
                        + " precision is only good to " + IslandMath.Num(IslandMath.MaxAbsCoordinateM) + " m");
                }
            }

            Transform hubRoom = HubLevelCatalog.FindPath(output,
                PlacementImporter.ManagedRootName + "/" + HubAssembler.HubLevelId);
            float[] hubMin;
            float[] hubMax;
            if (hubRoom != null && IslandImport.TryBounds(hubRoom, out hubMin, out hubMax))
            {
                mins.Add(hubMin);
                maxs.Add(hubMax);
                names.Add("hub room");
                Line(lines, "bounds hub room " + IslandMath.Format(hubMin) + ".." + IslandMath.Format(hubMax));
            }
            else
            {
                problems.Add("the hub room has no bounds (LevelContent/hub missing or empty)");
            }

            int a;
            int b;
            float gap;
            if (IslandMath.FindTooClosePair(mins.ToArray(), maxs.ToArray(), IslandMath.MinIslandGapM,
                out a, out b, out gap))
            {
                problems.Add("'" + names[a] + "' and '" + names[b] + "' are "
                    + IslandMath.Num(gap) + " m apart; islands must be at least "
                    + IslandMath.Num(IslandMath.MinIslandGapM) + " m apart to be out of sight");
            }
        }

        private static void CheckStarts(Scene output, List<LevelData> data,
            HubAssembler.HubRunOutcome outcome, List<string> lines, List<string> problems)
        {
            float trust = 12f;
            if (outcome.Launcher != null)
            {
                SerializedProperty p = new SerializedObject(outcome.Launcher).FindProperty("entryTrustMeters");
                if (p != null) trust = p.floatValue;
            }
            Physics.SyncTransforms();
            for (int i = 0; i < data.Count; i++)
            {
                LevelData d = data[i];
                Transform entry = i < outcome.Offered.Count ? outcome.Offered[i].EntryTransform : null;
                if (entry != null)
                {
                    float distance = Vector3.Distance(entry.position, d.StartCenterWorld);
                    if (distance > trust)
                    {
                        problems.Add("level '" + d.Entry.id + "': the entry marker is "
                            + IslandMath.Num(distance) + " m from the ball start; the launcher trusts an"
                            + " entry only within " + IslandMath.Num(trust) + " m");
                    }
                }

                RaycastHit hit;
                Vector3 origin = d.StartCenterWorld + Vector3.up * 50f;
                if (Physics.Raycast(origin, Vector3.down, out hit, 250f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Line(lines, "check: start of '" + d.Entry.id + "' has ground at y="
                        + IslandMath.Num(hit.point.y));
                }
                else
                {
                    Line(lines, "warn: no collider found below the start of '" + d.Entry.id
                        + "' (the analytic ground plane may still hold the ball)");
                }
            }
        }

        private static void CheckRespawnHeight(Scene output, List<LevelData> data, List<string> problems)
        {
            VRC.SDKBase.VRC_SceneDescriptor descriptor =
                IslandImport.FirstIn<VRC.SDKBase.VRC_SceneDescriptor>(output);
            if (descriptor == null) return;
            for (int i = 0; i < data.Count; i++)
            {
                float lowest = data[i].GroundOffset;
                if (data[i].Plan != null && data[i].Plan.KillEnabled)
                {
                    lowest = Math.Min(lowest, data[i].Plan.KillPlaneY);
                }
                if (!(lowest > descriptor.RespawnHeightY + 1f))
                {
                    problems.Add("level '" + data[i].Entry.id + "' reaches y=" + IslandMath.Num(lowest)
                        + ", not above the scene's respawn height y=" + IslandMath.Num(descriptor.RespawnHeightY)
                        + " + 1 m: a player there would respawn at once");
                }
            }
        }

        // ------------------------------------------------------------ reporting ---

        private static string DescribeLevel(LevelData d)
        {
            HubAssembler.HubLevelPlan p = d.Plan;
            return Tag + "level " + d.Index + " '" + d.Entry.id + "' kind=" + d.Entry.kind
                + " island=" + d.Shell.name
                + " start=" + IslandMath.Format(new[] { d.StartCenterWorld.x, d.StartCenterWorld.y,
                    d.StartCenterWorld.z })
                + " heading=" + IslandMath.Num(d.StartHeadingDeg)
                + " diameterM=" + IslandMath.Num(d.StartDiameterM)
                + " volumeMm3=" + d.StartVolumeMm3
                + " ground=" + IslandMath.Num(d.GroundOffset)
                + " arenas=" + d.Arenas.Count
                + " kinds=" + d.Kinds.Length
                + " terrain=" + (d.Terrain == null ? "none" : IslandImport.PathOf(d.Terrain.transform))
                + " pickup=" + (d.Pickup == null ? "none" : IslandImport.PathOf(d.Pickup.transform))
                + " goal=" + p.GoalType
                + " time=" + IslandMath.Num(p.TimeLimitSeconds)
                + " target=" + IslandMath.Num(p.TargetDiameterM)
                + " kill=" + (p.KillEnabled ? IslandMath.Num(p.KillPlaneY) : "off")
                + " abort=" + p.AllowAbort
                + " stars=" + p.HasStars;
        }

        private static void Line(List<string> lines, string text)
        {
            lines.Add(Tag + text);
        }

        // Saves the output when nothing is wrong, then builds the report text.
        private static string Finish(List<string> lines, List<string> problems,
            HubAssembler.Counters hc, Scene? output)
        {
            for (int i = 0; i < hc.Problems.Count; i++) problems.Add(hc.Problems[i]);
            for (int i = 0; i < hc.Notes.Count; i++) lines.Add(Tag + "hub note: " + hc.Notes[i]);

            string saved = "not saved";
            if (problems.Count == 0 && output.HasValue && output.Value.IsValid())
            {
                try
                {
                    saved = EditorSceneManager.SaveScene(output.Value)
                        ? "saved " + output.Value.path : "save refused";
                }
                catch (Exception exception)
                {
                    problems.Add("saving the output scene threw: " + exception.Message);
                }
            }

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++) text.Append(lines[i]).Append('\n');
            for (int i = 0; i < problems.Count; i++) text.Append(Tag).Append("PROBLEM: ").Append(problems[i]).Append('\n');
            text.Append(Tag).Append("result: +").Append(hc.Created).Append(" ~").Append(hc.Changed)
                .Append(" problems=").Append(problems.Count).Append(' ').Append(saved);
            return text.ToString();
        }

        // ------------------------------------------------------------ inspect ---

        /// <summary>
        /// Read-only look at every source scene of a hub: what the take lists resolve to, what
        /// leaves them, what hosts they hold, and what would be a problem. Changes no scene.
        /// </summary>
        public static string InspectSources(string hubId)
        {
            var lines = new List<string>();
            var problems = new List<string>();
            var catalogErrors = new List<string>();
            HubLevelCatalog.CatalogFile catalog;
            if (!HubLevelCatalog.TryLoad(out catalog, catalogErrors))
            {
                for (int i = 0; i < catalogErrors.Count; i++) problems.Add(catalogErrors[i]);
                return InspectFinish(lines, problems);
            }
            HubLevelCatalog.HubEntry hub = HubLevelCatalog.FindHub(catalog, hubId);
            if (hub == null)
            {
                problems.Add("hub '" + hubId + "' is not in hubs[]");
                return InspectFinish(lines, problems);
            }
            Line(lines, "inspect hub '" + hubId + "' output=" + hub.scene + " base=" + hub.baseScene);

            for (int k = 0; k < hub.levels.Length; k++)
            {
                InspectLevel(HubLevelCatalog.FindLevel(catalog, hub.levels[k]), hub, lines, problems);
            }
            return InspectFinish(lines, problems);
        }

        private static void InspectLevel(HubLevelCatalog.LevelEntry level, HubLevelCatalog.HubEntry hub,
            List<string> lines, List<string> problems)
        {
            Line(lines, "level '" + level.id + "' kind=" + level.kind + " source=" + level.source.scene
                + " offset=" + IslandMath.Format(level.island.offset));

            Scene scene;
            bool openedByUs = false;
            Scene existing = SceneManager.GetSceneByPath(level.source.scene);
            if (existing.IsValid() && existing.isLoaded)
            {
                scene = existing;
            }
            else
            {
                if (!IslandImport.TryOpenSource(level.source.scene, out scene, out openedByUs, problems))
                {
                    return;
                }
            }

            try
            {
                bool isBase = string.Equals(level.source.scene, hub.baseScene,
                    StringComparison.OrdinalIgnoreCase);
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    Line(lines, "  root " + roots[r].name + " children=" + roots[r].transform.childCount
                        + " active=" + roots[r].activeSelf + " components=" + CountComponents(roots[r]));
                }

                IslandAnalysis a = IslandImport.Analyze(level, scene, isBase);
                for (int i = 0; i < a.Problems.Count; i++) problems.Add(a.Problems[i]);
                Line(lines, "  take " + string.Join(" | ", a.TakePaths.ToArray())
                    + (a.LeavePaths.Count > 0 ? " leaveBehind " + string.Join(" | ", a.LeavePaths.ToArray()) : "")
                    + " objects=" + a.MovedObjects.Count + " components=" + a.ComponentCount);
                for (int i = 0; i < a.ArenaHosts.Count; i++) Line(lines, "  LevelArenaHost " + a.ArenaHosts[i]);
                for (int i = 0; i < a.PickupHosts.Count; i++) Line(lines, "  PickupFieldHost " + a.PickupHosts[i]);
                for (int i = 0; i < a.TerrainHosts.Count; i++) Line(lines, "  TerrainFieldHost " + a.TerrainHosts[i]);
                for (int i = 0; i < a.MotionHosts.Count; i++) Line(lines, "  MotionHost " + a.MotionHosts[i]);

                var external = new SortedDictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < a.External.Count; i++)
                {
                    IslandRef r = a.External[i];
                    string key = r.Owner.GetType().Name + "." + StripIndex(r.PropertyPath) + " -> "
                        + r.TargetPath + " (" + r.TargetType.Name + ")";
                    int n;
                    external.TryGetValue(key, out n);
                    external[key] = n + 1;
                }
                foreach (KeyValuePair<string, int> pair in external)
                {
                    Line(lines, "  leaves the taken set: " + pair.Value + " x " + pair.Key);
                }

                IslandSourceFacts f = a.Facts;
                if (f != null)
                {
                    if (f.HasMotor)
                    {
                        Line(lines, "  motor start=" + IslandMath.Format(new[] { f.MotorStartCenter.x,
                            f.MotorStartCenter.y, f.MotorStartCenter.z }) + " diameterM="
                            + IslandMath.Num(f.MotorStartDiameterM) + " groundOffset="
                            + IslandMath.Num(f.MotorGroundOffset));
                    }
                    if (f.HasRound)
                    {
                        Line(lines, "  round levelId=" + f.RoundLevelId + " goal=" + f.RoundGoalType
                            + " target=" + IslandMath.Num(f.RoundTargetDiameterM) + " time="
                            + IslandMath.Num(f.RoundTimeLimitSeconds) + " kill="
                            + (f.RoundKillEnabled ? IslandMath.Num(f.RoundKillY) : "off")
                            + " pickupHost=" + f.RoundPickupHostPath);
                    }
                    if (f.HasDriver)
                    {
                        Line(lines, "  driver frame=" + f.DriverFrameArenaPath + " cells="
                            + f.DriverCellArenaPaths.Count + " kinds="
                            + (f.DriverKinds == null ? 0 : f.DriverKinds.Length) + " spawn="
                            + IslandMath.Format(new[] { f.DriverSpawn.x, f.DriverSpawn.y, f.DriverSpawn.z }));
                    }
                }
                Line(lines, "  baked lighting=" + BakedLightingText(level.source.scene));
            }
            finally
            {
                if (openedByUs) EditorSceneManager.CloseScene(scene, true);
            }
        }

        // The scene file is text YAML: the scene's lighting data asset is one line,
        // "m_LightingDataAsset: {fileID: 0}" when nothing is baked. Scene-level settings do not
        // travel with moved objects, so a baked source would look different in the hub.
        private static string BakedLightingText(string scenePath)
        {
            try
            {
                string[] fileLines = File.ReadAllLines(Path.Combine(LevelPaths.ProjectRoot(), scenePath));
                for (int i = 0; i < fileLines.Length; i++)
                {
                    if (fileLines[i].Contains("m_LightingDataAsset:"))
                    {
                        return fileLines[i].Contains("fileID: 0}") ? "none" : "PRESENT (not carried to the hub)";
                    }
                }
                return "unknown (no m_LightingDataAsset line)";
            }
            catch (Exception exception)
            {
                return "unknown (" + exception.Message + ")";
            }
        }

        private static int CountComponents(GameObject root)
        {
            return root.GetComponentsInChildren<Component>(true).Length;
        }

        // "levels.Array.data[3]" -> "levels[]": one line per field, not per element.
        private static string StripIndex(string propertyPath)
        {
            return System.Text.RegularExpressions.Regex.Replace(propertyPath,
                @"\.Array\.data\[\d+\]", "[]");
        }

        private static string InspectFinish(List<string> lines, List<string> problems)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++) text.Append(lines[i]).Append('\n');
            for (int i = 0; i < problems.Count; i++) text.Append(Tag).Append("PROBLEM: ").Append(problems[i]).Append('\n');
            text.Append(Tag).Append("inspect done: problems=").Append(problems.Count);
            return text.ToString();
        }
    }
}
