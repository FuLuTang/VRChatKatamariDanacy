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

            // 新同步来的 U# 行为可能还没有程序资产，后面的 AddComponent 会抛；缺则建并编译。
            // 放在核心皮肤之后（皮肤装配拒绝脏场景，编译可能把场景标脏）、任何 U# 组件添加之前。
            var createdPrograms = new List<string>();
            UdonProgramAssets.EnsureAll(createdPrograms, problems);
            if (createdPrograms.Count > 0)
                Line(lines, "created U# program assets: " + string.Join(", ", createdPrograms.ToArray()));
            if (problems.Count > 0) return Finish(lines, problems, hc, output);

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

            // ---- the rider's UX on top of the hub ----
            // After the hub assembly, never before: the hub creates the VR thumbstick adapter the
            // ride station's gate needs (vrInput), and the ride assembly only adds to what the hub
            // links (it keeps the BallRideStation and its VRCStation where they are, moves the
            // retry button root into the result card with its hub return child, and leaves the
            // retry button's words to the hub). It also turns the screen-space HUD into a
            // world-space panel a headset can see (RIDE_GEOMETRY.md section 6.1).
            AssembleRide(lines, problems);
            CheckRideVrInput(output, problems);
            if (problems.Count > 0) return Finish(lines, problems, hc, output);
            AssembleAbsorbFeedback(output, levelSwitch, data, hc, lines, problems);
            if (problems.Count > 0) return Finish(lines, problems, hc, output);

            // ---- GPU instancing on the repeated prop materials ----
            // After every island is in the scene (so the props are visible to the pass) and
            // before the occlusion marks, which only read a material's shader and render
            // queue (the instanced copy keeps both). A reference prop's material is a
            // read-only sub-asset of its model, so the pass copies it to an asset under
            // _RefUnlicensed and assigns the copy; that is only allowed when the output scene
            // is reference material itself (MaterialInstancing).
            EnableGpuInstancing(output, lines);

            // ---- static occlusion ----
            // Only the flags are set here; the operator bakes and saves the umbra data
            // (ALL_MAPS_HUB.md section 6.1). The rule reads what the geometry is, never a
            // hand-kept path list (OccluderRule).
            MarkStaticOcclusion(output, lines, problems);
            if (problems.Count > 0) return Finish(lines, problems, hc, output);

            // ---- Udon sync and read-back ----
            var diagLines = new List<string>();
            HubAssembler.CopyAllToUdon(hc);
            VerifyAllTables(outcome, levelSwitch, data, hc, diagLines);
            VerifyGlobalBallCap(IslandImport.FirstIn<KatamariBallMotor>(output), hc, diagLines);
            ReportLocalWiring(output, hc, diagLines);

            for (int i = 0; i < diagLines.Count; i++) Line(lines, diagLines[i]);

            // ---- level lines ----
            for (int i = 0; i < data.Count; i++) lines.Add(DescribeLevel(data[i]));

            // ---- self-check ----
            SelfCheck(output, hub, data, outcome, lines, problems);
            if (skinApplied) CheckCoreSkin(output, problems);

            return Finish(lines, problems, hc, output);
        }

        // ------------------------------------------------------------ ride UX ---

        private static void AssembleRide(List<string> lines, List<string> problems)
        {
            string report = VRCKatamari.Editor.Story.RideUxAssembler.AssembleOpenScene(problems);
            string[] rows = report.Split('\n');
            for (int i = 0; i < rows.Length; i++) Line(lines, "ride: " + rows[i].Trim());
        }

        // The ride station's VR gate must be the scene's one thumbstick adapter.
        private static void CheckRideVrInput(Scene output, List<string> problems)
        {
            BallRideStation station = IslandImport.FirstIn<BallRideStation>(output);
            VrStickMoveInput vr = IslandImport.FirstIn<VrStickMoveInput>(output);
            if (station == null || vr == null) return; // the self-check counts both
            var so = new SerializedObject(station);
            SerializedProperty link = so.FindProperty("vrInput");
            if (link == null || link.objectReferenceValue != vr)
            {
                problems.Add("BallRideStation.vrInput is not the scene's VrStickMoveInput: the VR"
                    + " sticks would push the ball while nobody rides it");
            }
        }

        // ------------------------------------------------------------ local pickup feedback ---

        private static void AssembleAbsorbFeedback(Scene output, LevelSwitchHost levelSwitch,
            List<LevelData> levels, HubAssembler.Counters hc, List<string> lines, List<string> problems)
        {
            var ball = IslandImport.FirstIn<KatamariBallMotor>(output);
            var ride = IslandImport.FirstIn<BallRideStation>(output);
            var settings = IslandImport.FirstIn<HubSettingsHost>(output);
            if (ball == null || ride == null || settings == null)
            {
                problems.Add("absorb feedback needs the shared ball, ride station and Hub settings");
                return;
            }

            const string objectName = "AbsorbFeedback";
            Transform anchor = ball.transform.Find(objectName);
            if (anchor == null)
            {
                var root = new GameObject(objectName);
                Undo.RegisterCreatedObjectUndo(root, "Create " + objectName);
                root.transform.SetParent(ball.transform, false);
                anchor = root.transform;
            }
            var feedback = anchor.GetComponent<AbsorbFeedbackHost>();
            if (feedback == null) feedback = HubAssembler.AddUdon<AbsorbFeedbackHost>(anchor.gameObject, hc);
            if (feedback == null)
            {
                problems.Add("could not add AbsorbFeedbackHost to the shared ball");
                return;
            }

            var voices = new AudioSource[AbsorbFeedbackMath.VoiceCount];
            for (int i = 0; i < voices.Length; i++)
            {
                string slotName = i == 0 ? "RollIn" : "Scream" + i;
                Transform slot = anchor.Find(slotName);
                if (slot == null)
                {
                    var go = new GameObject(slotName);
                    Undo.RegisterCreatedObjectUndo(go, "Create " + slotName);
                    go.transform.SetParent(anchor, false);
                    slot = go.transform;
                }
                var source = slot.GetComponent<AudioSource>();
                if (source == null) source = Undo.AddComponent<AudioSource>(slot.gameObject);
                Undo.RecordObject(source, "Configure local absorb voice");
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.clip = null;
                EditorUtility.SetDirty(source);
                voices[i] = source;
            }

            var clips = new List<AudioClip>();
            var screamIds = new List<int>();
            var rollInStarts = new int[levels.Count];
            var rollInCounts = new int[levels.Count];
            var missingClips = new List<string>();
            int rollInClips = 0;
            // 不把原作音频引用写进正式场景。
            if (HubLevelCatalog.IsUnderRefUnlicensed(output.path))
            {
                var map = ReferenceAbsorbAudio.Load(out string note);
                if (note != null) Line(lines, "note: absorb feedback: " + note);
                if (map != null)
                {
                    var groupStarts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var groupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var group in map.rollInGroups)
                    {
                        groupStarts.Add(group.id, clips.Count);
                        groupCounts.Add(group.id, AddAbsorbClips(group.wavs, 0, clips, screamIds, missingClips));
                    }
                    rollInClips = clips.Count;
                    for (int i = 0; i < levels.Count; i++)
                    {
                        string levelId = levels[i].Entry.id;
                        var level = ReferenceAbsorbAudio.FindLevel(map, levelId);
                        string groupId = level == null ? map.defaultRollInGroup : level.rollInGroup;
                        rollInStarts[i] = groupStarts[groupId];
                        rollInCounts[i] = groupCounts[groupId];
                        Line(lines, "absorb feedback: level=" + levelId + " rollInGroup=" + groupId
                            + " variants=" + rollInCounts[i] + (level == null ? " (default)" : ""));
                    }
                    // 二分查找要求正编号升序、同号变体连续。
                    Array.Sort(map.screams, (a, b) => a.screamId.CompareTo(b.screamId));
                    foreach (var scream in map.screams)
                        AddAbsorbClips(scream.wavs, scream.screamId, clips, screamIds, missingClips);
                }
            }
            else Line(lines, "note: absorb feedback: clips omitted from non-reference output");
            if (missingClips.Count > 0)
                Line(lines, "note: absorb feedback: " + missingClips.Count + "/" + (clips.Count + missingClips.Count)
                    + " local clip(s) not imported under " + ReferenceAbsorbAudio.ClipAssetFolder
                    + ": " + string.Join(", ", missingClips.ToArray()));

            var so = new SerializedObject(feedback);
            HubAssembler.SetObject(so, "ride", ride, hc);
            HubAssembler.SetObject(so, "settings", settings, hc);
            HubAssembler.SetObject(so, "levelSwitch", levelSwitch, hc);
            HubAssembler.SetObjectArray(so, "voices", voices, hc);
            AudioClip[] clipTable = clips.ToArray();
            int[] screamIdTable = screamIds.ToArray();
            HubAssembler.SetObjectArray(so, "clips", clipTable, hc);
            HubAssembler.SetIntArray(so, "clipClasses", screamIdTable, hc);
            HubAssembler.SetIntArray(so, "rollInStarts", rollInStarts, hc);
            HubAssembler.SetIntArray(so, "rollInCounts", rollInCounts, hc);
            so.ApplyModifiedProperties();
            if (!CopyFeedbackProxy(feedback, problems)) return;

            int linked = 0;
            int pickupFields = 0;
            foreach (LevelData row in levels)
            {
                if (row.Pickup == null) continue;
                pickupFields++;
                var field = new SerializedObject(row.Pickup);
                HubAssembler.SetObject(field, "absorbFeedback", feedback, hc);
                field.ApplyModifiedProperties();
                if (!CopyFeedbackProxy(row.Pickup, problems)) return;
                if (FeedbackReferenceMatches(row.Pickup, "absorbFeedback", feedback)) linked++;
                else problems.Add("absorb feedback link did not read back on level " + row.Entry.id);
            }
            bool readBack = FeedbackReferenceMatches(feedback, "ride", ride)
                && FeedbackReferenceMatches(feedback, "settings", settings)
                && FeedbackReferenceMatches(feedback, "levelSwitch", levelSwitch)
                && FeedbackArrayMatches(feedback, "voices", voices)
                && FeedbackArrayMatches(feedback, "clips", clipTable)
                && FeedbackArrayMatches(feedback, "clipClasses", screamIdTable)
                && FeedbackArrayMatches(feedback, "rollInStarts", rollInStarts)
                && FeedbackArrayMatches(feedback, "rollInCounts", rollInCounts);
            if (!readBack) problems.Add("AbsorbFeedbackHost backing Udon references or clip table did not read back");
            Line(lines, "absorb feedback: voices=" + voices.Length + " rollInClips=" + rollInClips
                + " screamClips=" + (clips.Count - rollInClips)
                + " linked pickup fields=" + linked + "/" + pickupFields
                + " Udon read-back=" + (readBack ? "verified" : "MISMATCH"));
        }

        private static int AddAbsorbClips(string[] wavs, int screamId, List<AudioClip> clips,
            List<int> screamIds, List<string> missingClips)
        {
            int start = clips.Count;
            foreach (string wav in wavs)
            {
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ReferenceAbsorbAudio.ClipAssetFolder + wav);
                if (clip == null) missingClips.Add(wav);
                else
                {
                    clips.Add(clip);
                    screamIds.Add(screamId);
                }
            }
            return clips.Count - start;
        }

        private static bool CopyFeedbackProxy(UdonSharpBehaviour proxy, List<string> problems)
        {
            try
            {
                UdonSharpEditorUtility.CopyProxyToUdon(proxy);
                return true;
            }
            catch (Exception exception)
            {
                problems.Add("absorb feedback: CopyProxyToUdon threw for '" + proxy.name + "': "
                    + exception.Message + ". Compile, save the scene, reopen it and run the"
                    + " assembly again");
                return false;
            }
        }

        private static bool FeedbackReferenceMatches(UdonSharpBehaviour owner, string symbol,
            UdonSharpBehaviour expected)
        {
            return UdonVariableAccess.TryRead(owner, symbol, out object value)
                && Equals(value, UdonSharpEditorUtility.GetBackingUdonBehaviour(expected));
        }

        private static bool FeedbackArrayMatches(UdonSharpBehaviour owner, string symbol, Array expected)
        {
            if (!UdonVariableAccess.TryRead(owner, symbol, out object read)) return false;
            // An empty serialized array may surface as a null variable value; either way it
            // means "no entries", which is the state a scene without local clips must reach.
            if (expected.Length == 0) return read == null || (read is Array none && none.Length == 0);
            if (!(read is Array actual) || actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++)
                if (!Equals(actual.GetValue(i), expected.GetValue(i))) return false;
            return true;
        }

        // ------------------------------------------------------------ GPU instancing ---

        // The repeated prop materials get GPU instancing: a material and mesh shared by n
        // renderers becomes one draw instead of n. The Cancer island's props use read-only
        // embedded model materials, so their copies are written under _RefUnlicensed; the
        // output scene must be reference material itself for that (it is whenever a source is).
        // Problems are warnings, not refused saves: instancing is an optimisation, and the
        // pass leaves any material it cannot name unchanged.
        private static void EnableGpuInstancing(Scene output, List<string> lines)
        {
            bool reference = HubLevelCatalog.IsUnderRefUnlicensed(output.path);
            MaterialInstancing.Result instancing =
                MaterialInstancing.EnableInScene(output, reference);
            if (!reference)
            {
                Line(lines, "note: instancing skipped; the output is not under Assets/_RefUnlicensed/");
                return;
            }
            Line(lines, "instancing: enabled=" + instancing.Enabled
                + " duplicated=" + instancing.Duplicated
                + " already=" + instancing.AlreadyEnabled
                + " renderers=" + instancing.RenderersRewritten
                + " skipped committed=" + instancing.SkippedCommitted
                + " unsupported=" + instancing.SkippedUnsupported
                + " non-opaque=" + instancing.SkippedNonOpaque);
            for (int i = 0; i < instancing.Problems.Count; i++)
            {
                Line(lines, "warn: " + instancing.Problems[i]);
            }
        }

        // ------------------------------------------------------------ static occlusion ---

        // The hub's fixed geometry gets its static-occlusion flags, and the report says what
        // the operator is about to bake: the occluder count and every marked occluder path,
        // plus each fixed piece that stayed an occludee and why.
        private static void MarkStaticOcclusion(Scene output, List<string> lines,
            List<string> problems)
        {
            OccluderRule.MarkResult marks = OccluderRule.Mark(output);
            for (int i = 0; i < marks.Problems.Count; i++) problems.Add(marks.Problems[i]);

            Line(lines, "static: occluders=" + marks.Occluders + " occludees=" + marks.Occludees
                + " batching=" + marks.BatchingStatic + " changed=" + marks.Changed);
            if (marks.Occluders == 0)
            {
                // A hub with no occluder at all means the rule found no closed structure (a
                // broken hierarchy or an unreadable primitive mesh); the operator would bake
                // an empty umbra, so refuse to save and say so.
                problems.Add("static occlusion: no occluder among the scene's fixed structure"
                    + " geometry; the operator's bake would be empty (see OccluderRule)");
            }
            for (int i = 0; i < marks.OccluderPaths.Count; i++)
            {
                Line(lines, "static occluder " + marks.OccluderPaths[i]);
            }
            for (int i = 0; i < marks.Skips.Count; i++)
            {
                Line(lines, "static skip " + marks.Skips[i]);
            }
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

            long capVolume;
            if (!SizeModelMath.TrySphereVolumeMm3(KatamariBallMotor.GlobalMaxBallDiameterM,
                ratio, out capVolume) || volume >= capVolume)
            {
                problems.Add(label + ": the start ball must fit under the global "
                    + KatamariBallMotor.GlobalMaxBallDiameterM + " m cap");
                return;
            }

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
            plan.CountKind = facts.RoundCountKind;
            plan.HasStars = level.stars;
            plan.GoalType = facts.HasRound ? facts.RoundGoalType : (int)StoryGoalType.A;
            plan.TargetDiameterM = facts.RoundTargetDiameterM;
            plan.TargetCount = facts.RoundTargetCount;
            plan.TimeLimitSeconds = facts.RoundTimeLimitSeconds;
            plan.KillEnabled = facts.RoundKillEnabled;
            plan.KillPlaneY = facts.RoundKillY;
            HubAssembler.ApplyRoundDefinition(level, plan, hc);
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

            var motorSo = new SerializedObject(motor);
            if (HubAssembler.SetFloat(motorSo, "maxBallDiameterM",
                KatamariBallMotor.GlobalMaxBallDiameterM, hc))
            {
                motorSo.ApplyModifiedPropertiesWithoutUndo();
            }
            // Copy even if the proxy was already correct: the backing table may be stale.
            hc.Proxies.Add(motor);

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
            var islands = new Object[n];
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
                islands[i] = d.Shell != null ? d.Shell.gameObject : null;
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
            if (!IslandActivationMath.RootsMatchLevels(islands.Length, ids.Length))
            {
                hc.Problems.Add("island roots (" + islands.Length + ") do not match levels ("
                    + ids.Length + ")");
            }
            changed |= HubAssembler.SetObjectArray(so, "islandRoots", islands, hc);
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
            "motor", "pickupHosts", "terrainHosts", "islandRoots", "levelIds", "groundOffsets", "arenaHosts",
            "arenaStarts", "arenaCounts", "kindsFlat", "kindsStarts", "kindsCounts",
            "startCenters", "startHeadingDegrees", "startVolumesMm3",
        };

        private static readonly string[] RoundFields =
        {
            "levelIds", "levelGoalTypes", "levelTargetDiameters", "levelTargetCounts",
            "levelTimeLimits", "levelKillEnabled", "levelKillY", "levelCountKinds",
            "levelPickupHosts", "levelAllowAbort", "settings",
        };

        private static readonly string[] PresenterFields =
        {
            "levelHasStars", "levelStar1Diameters", "levelStar1Counts", "levelStar2Diameters", "levelStar3Diameters", "levelStar2Counts",
            "levelStar3Counts", "levelPartialBands",
        };

        // A field written through the proxy that the backing table lacks is silently skipped
        // by CopyProxyToUdon, so every new field is read back (Labs/README.md, "two tables").
        private static void VerifyAllTables(HubAssembler.HubRunOutcome outcome,
            LevelSwitchHost levelSwitch, List<LevelData> data, HubAssembler.Counters hc,
            List<string> diag)
        {
            HubAssembler.VerifyTable(levelSwitch, SwitchFields, hc);
            HubAssembler.VerifyTables(outcome.Launcher, outcome.Host, hc);
            if (outcome.Launcher != null)
                HubAssembler.VerifyTable(outcome.Launcher, new[] { "levelSwitch" }, hc);
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

        // 输出场景从基础场景复制而来，基础场景里没有 HubSettings 和关卡宿主，这三组接线只能
        // 由装配完成（Ride UX 段接，这里在 Udon 拷贝之后读回）。缺一处就是 problem，场景不保存。
        private static void ReportLocalWiring(Scene output, HubAssembler.Counters hc, List<string> diag)
        {
            VRCKatamari.Editor.Story.RideUxAssembler.LocalWiring wiring =
                VRCKatamari.Editor.Story.RideUxAssembler.ReadLocalWiring(output, hc.Problems);
            diag.Add(VRCKatamari.Editor.Story.RideUxAssembler.LocalWiringSummary(
                wiring.DiagnosticsWired, wiring.DiagnosticsHosts, wiring.CatchWired, wiring.CatchHosts,
                wiring.WireframeFound, wiring.WireframeSettingsWired));
            diag.Add(VRCKatamari.Editor.Story.RideUxAssembler.MotorSettingsSummary(wiring.MotorSettingsWired));
        }

        private static void VerifyGlobalBallCap(KatamariBallMotor motor,
            HubAssembler.Counters hc, List<string> diag)
        {
            if (motor == null)
            {
                hc.Problems.Add("the shared motor is missing; its global ball diameter cap "
                    + "could not be verified");
                return;
            }
            const string field = "maxBallDiameterM";
            const float expected = KatamariBallMotor.GlobalMaxBallDiameterM;
            SerializedProperty property = new SerializedObject(motor).FindProperty(field);
            bool proxyMatches = property != null && property.floatValue == expected;
            bool tableMatches = UdonVariableAccess.TryRead(motor, field, out object value)
                && value is float actual && actual == expected;
            diag.Add("diag global ball diameter cap: expected=" + expected + " m, proxy="
                + (property == null ? "missing" : IslandMath.Num(property.floatValue))
                + ", Udon=" + (value == null ? "missing" : value.ToString()));
            if (!proxyMatches || !tableMatches)
                hc.Problems.Add("the shared motor's global ball diameter cap did not read back as "
                    + expected + " m on both the proxy and backing Udon table");
        }

        // ------------------------------------------------------------ self-check ---

        private static void SelfCheck(Scene output, HubLevelCatalog.HubEntry hub,
            List<LevelData> data, HubAssembler.HubRunOutcome outcome, List<string> lines,
            List<string> problems)
        {
            // Single infrastructure: exactly one of each, none of the test field's scripts.
            ExpectCount<KatamariBallMotor>(output, 1, problems);
            ExpectCount<DesktopMoveInput>(output, 1, problems);
            ExpectCount<VrStickMoveInput>(output, 1, problems);
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
            CheckIslandRoots(data, lines, problems);
            CheckStarts(output, data, outcome, lines, problems);
            CheckRespawnHeight(output, data, problems);
            Line(lines, "check: arenaHosts=" + arenaTotal + " listed=" + listed.Count);
        }

        // The level switch turns islands off at run time, one root per level. Each root must
        // be the level's own shell, be on when the scene is saved (an object that was never
        // active has not run Start, and its behaviours cannot be called safely), and must not
        // contain the ball, the seat, the round, the hub or the switch itself (they would
        // vanish with it).
        private static void CheckIslandRoots(List<LevelData> data, List<string> lines,
            List<string> problems)
        {
            if (!IslandActivationMath.RootsMatchLevels(data.Count, data.Count)) return;
            var seen = new HashSet<Transform>();
            for (int i = 0; i < data.Count; i++)
            {
                Transform shell = data[i].Shell;
                string id = data[i].Entry.id;
                if (shell == null)
                {
                    problems.Add("level '" + id + "' has no island root; it would never be switched off");
                    continue;
                }
                if (!seen.Add(shell)) problems.Add("island '" + shell.name + "' is used by two levels");
                if (!shell.gameObject.activeSelf)
                {
                    problems.Add("island '" + shell.name + "' is inactive in the scene; it must be"
                        + " on when saved so its behaviours run Start (the switch turns it off at run time)");
                }
                if (shell.GetComponentInChildren<KatamariBallMotor>(true) != null
                    || shell.GetComponentInChildren<BallRideStation>(true) != null
                    || shell.GetComponentInChildren<StoryRoundController>(true) != null
                    || shell.GetComponentInChildren<HubLevelLauncher>(true) != null
                    || shell.GetComponentInChildren<HubRoomHost>(true) != null
                    || shell.GetComponentInChildren<HubReturnButton>(true) != null
                    || shell.GetComponentInChildren<LevelSwitchHost>(true) != null)
                {
                    problems.Add("island '" + shell.name + "' contains infrastructure (ball, seat,"
                        + " round, hub launcher, return button or level switch); it would be switched off with it");
                }
                Line(lines, "islandRoot[" + i + "] " + id + " -> " + shell.name
                    + " activeSelf=" + shell.gameObject.activeSelf);
            }
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
                + " type=" + (StoryGoalType)p.GoalType
                + " time=" + IslandMath.Num(p.TimeLimitSeconds)
                + (p.GoalType == (int)StoryGoalType.B
                    ? " targetCount=" + p.TargetCount + " countKind=" + p.CountKind
                    : " targetDiameterM=" + IslandMath.Num(p.TargetDiameterM))
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
