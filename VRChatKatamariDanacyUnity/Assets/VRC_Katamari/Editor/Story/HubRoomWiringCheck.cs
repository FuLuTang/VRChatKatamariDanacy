using UnityEditor;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Editor.Hub;
using VRCKatamari.Hub;
using VRCKatamari.Input;
using VRCKatamari.Pickup;
using VRCKatamari.Riding;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Editor.Story
{
    /// <summary>
    /// Read-only wiring report for the room, run after the pieces are placed. It changes
    /// nothing; every line is a observation the operator can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The room is wired across four behaviours and one scene descriptor, and the failure
    /// mode of a missing link is silence (a room that never starts, or a card that never
    /// appears), not an exception. This report makes those links visible before Play Mode:
    /// the host's launcher/controller/centre/circles/prompt, the launcher's round/entries/
    /// room point, the round and the result presenter existing, and the circle count
    /// matching the launcher's level count.
    /// </para>
    /// <para>
    /// Fields are read by their serialized names, exactly like the importer reads
    /// <c>LevelArenaHost</c>'s arrays: the runtime fields are private, and a report that
    /// silently skipped a renamed field would be worse than none, so a missing field is
    /// reported as a problem. Nothing here is part of the runtime path; deleting this file
    /// changes only whether the menu exists.
    /// </para>
    /// </remarks>
    public static class HubRoomWiringCheck
    {
        public const string MenuPath = "VRC Katamari/Hub/Check Room Wiring";

        [MenuItem(MenuPath, false, 400)]
        public static void Check()
        {
            int problems = 0;

            var hosts = Object.FindObjectsOfType<HubRoomHost>();
            if (hosts.Length != 1)
            {
                Report("expected exactly one active HubRoomHost, found " + hosts.Length
                    + " (one object, never parented under the room or the level)", ref problems);
                if (hosts.Length == 0)
                {
                    Debug.Log("[hub-check] done: " + problems + " problem(s)");
                    return;
                }
            }

            HubRoomHost host = hosts[0];
            HubLevelLauncher launcher = ObjectReference<HubLevelLauncher>(host, "launcher",
                "host.launcher (the HubLevelLauncher that moves the player)", ref problems);
            ObjectReference<StoryRoundController>(host, "controller",
                "host.controller (the round the room follows)", ref problems);
            Transform hubCenter = ObjectReference<Transform>(host, "hubCenter",
                "host.hubCenter (an empty at the room's centre)", ref problems);
            if (OptionalReference<TMPro.TextMeshProUGUI>(host, "promptText") == null)
            {
                Debug.Log("[hub-check] host.promptText is not wired: the room has no on-screen"
                    + " instruction line (use the wall panel buttons to start)");
            }

            int buttons = ArraySize(host, "levelButtons");
            float hubRadius = FloatField(host, "hubRadiusMeters", 12f);
            if (buttons == 0)
            {
                Report("host.levelButtons is empty: the level cards are not wired", ref problems);
            }

            if (hubCenter != null)
            {
                float fromOrigin = Vector3.Distance(hubCenter.position, Vector3.zero);
                if (fromOrigin < 500f)
                {
                    Warn("hubCenter is " + fromOrigin.ToString("F1") + " m from the level island"
                        + " (origin); the two islands would be visible from each other. The plan's"
                        + " provisional separation is 1200 m");
                }
                else
                {
                    Debug.Log("[hub-check] hub island is " + fromOrigin.ToString("F1")
                        + " m from the level island");
                }
            }

            if (launcher == null)
            {
                Debug.Log("[hub-check] done: " + problems + " problem(s)");
                return;
            }

            ObjectReference<StoryRoundController>(launcher, "controller",
                "launcher.controller (the same round object the host uses)", ref problems);
            Transform roomSpawn = ObjectReference<Transform>(launcher, "roomSpawn",
                "launcher.roomSpawn (where a finished round puts the player back)", ref problems);
            int entries = ArraySize(launcher, "levelEntries");
            if (entries < 1)
            {
                Report("launcher.levelEntries is empty: a start has nowhere to send the player",
                    ref problems);
            }
            if (entries != buttons)
            {
                Warn("launcher.levelEntries has " + entries + " entries but the room has " + buttons
                    + " buttons; index i of both is the same level, so they must match");
            }
            Transform firstEntry = Element<Transform>(launcher, "levelEntries", 0);
            var levelSwitch = Object.FindObjectOfType<LevelSwitchHost>();
            if (levelSwitch == null)
            {
                if (firstEntry == null && entries > 0)
                {
                    Report("launcher.levelEntries[0] is empty", ref problems);
                }
            }
            else
            {
                CheckLevelEntries(launcher, levelSwitch, entries, hubCenter, ref problems);
            }
            // The entry is the fallback destination when auto-seat is off or no station is
            // wired, so it still has to be on the level floor next to the ball. An entry far from the
            // ball puts the player in the void, where they fall and ClientSim respawns them
            // in the room.
            var motor = Object.FindObjectOfType<KatamariBallMotor>();
            if (levelSwitch == null && motor != null && firstEntry != null)
            {
                SerializedProperty startPose = new SerializedObject(motor).FindProperty(
                    "startCenterWorld");
                if (startPose == null)
                {
                    Warn("cannot read KatamariBallMotor.startCenterWorld; the level entry's"
                        + " position was not compared with the ball");
                }
                else
                {
                    float fromBall = Vector3.Distance(firstEntry.position,
                        startPose.vector3Value);
                    if (fromBall > 5f)
                    {
                        Warn("launcher.levelEntries[0] is " + fromBall.ToString("F1")
                            + " m from the ball's startCenterWorld. It is the fallback arrival,"
                            + " and it must be on the level floor near the ball; an entry in"
                            + " the void makes the player fall and respawn at the room spawn");
                    }
                    else
                    {
                        Debug.Log("[hub-check] level entry is " + fromBall.ToString("F1")
                            + " m from the ball start");
                    }
                }
            }
            if (levelSwitch == null && firstEntry != null && hubCenter != null)
            {
                float islandDistance = Vector3.Distance(firstEntry.position, hubCenter.position);
                if (islandDistance < 50f)
                {
                    Warn("the level entry is only " + islandDistance.ToString("F1")
                        + " m from the room; the islands would overlap. Level 1's entry belongs"
                        + " at the ball, near the origin");
                }
            }
            for (int i = 0; i < buttons; i++)
            {
                Transform button = Element<Transform>(host, "levelButtons", i);
                HubStartButton start = button == null ? null : button.GetComponent<HubStartButton>();
                if (start == null)
                {
                    Report("level card " + i + " has no HubStartButton", ref problems);
                    continue;
                }
                string cardProblem = HubAssembler.PanelButtonProblem(start);
                if (cardProblem != null) Report(button.name + ": " + cardProblem, ref problems);
            }
            if (host.gameObject == launcher.gameObject)
            {
                Warn("HubRoomHost and HubLevelLauncher share one GameObject; one behaviour per"
                    + " object is the project convention");
            }

            // The host owns the panel instruction line; avoid two competing status labels.
            if (OptionalReference<Object>(launcher, "statusText") != null)
                Warn("launcher.statusText duplicates host.promptText; use only one instruction line");

            int controllers = Object.FindObjectsOfType<StoryRoundController>().Length;
            if (controllers != 1)
            {
                Report("expected exactly one active StoryRoundController, found " + controllers,
                    ref problems);
            }

            CheckVrStickInput(OptionalReference<HubSettingsHost>(host, "settings"), ref problems);
            int arrows = CheckPanels(host, entries, ref problems);
            CheckAbsorbFeedback(OptionalReference<HubSettingsHost>(host, "settings"), ref problems);
            CheckLocalViews(host.gameObject.scene, ref problems);

            int presenters = Object.FindObjectsOfType<StoryResultPresenter>().Length;
            if (presenters == 0)
            {
                Warn("no StoryResultPresenter in the scene: the room has no result board. The"
                    + " board is the presenter's card (it owns the stars and the measurement);"
                    + " the room does not duplicate them");
            }

            Debug.Log("[hub-check] done: " + problems + " problem(s), " + buttons + " level card(s), "
                + entries + " level entry/entries, " + arrows + " setting arrow(s), room radius "
                + hubRadius);
        }

        // ------------------------------------------------------------------ helpers

        // A multi-level hub: each level's entry is compared with that level's own ball start
        // (LevelSwitchHost.startCenters[i]); a missing entry is fine for a free roam level
        // (the launcher lands the player on the ball's seat), a problem for any other.
        private static void CheckLevelEntries(HubLevelLauncher launcher,
            LevelSwitchHost levelSwitch, int entries, Transform hubCenter, ref int problems)
        {
            SerializedProperty starts = Find(levelSwitch, "startCenters");
            SerializedProperty ids = Find(levelSwitch, "levelIds");
            SerializedProperty goals = Find(Object.FindObjectOfType<StoryRoundController>(),
                "levelGoalTypes");
            float trust = FloatField(launcher, "entryTrustMeters", 12f);
            if (starts == null || !starts.isArray)
            {
                Report("levelSwitch.startCenters is missing (renamed?): the entries were not"
                    + " compared with the level starts", ref problems);
                return;
            }
            if (starts.arraySize != entries)
            {
                Report("levelSwitch has " + starts.arraySize + " level start(s) but the launcher"
                    + " has " + entries + " entry/entries; index i of both is the same level",
                    ref problems);
            }

            int count = System.Math.Min(entries, starts.arraySize);
            for (int i = 0; i < count; i++)
            {
                string id = ids != null && ids.isArray && i < ids.arraySize
                    ? ids.GetArrayElementAtIndex(i).stringValue : ("#" + i);
                Transform entry = Element<Transform>(launcher, "levelEntries", i);
                bool freeRoam = goals != null && goals.isArray && i < goals.arraySize
                    && goals.GetArrayElementAtIndex(i).intValue == StoryLevelMath.GoalFreeRoam;
                if (entry == null)
                {
                    if (!freeRoam)
                    {
                        Report("launcher.levelEntries[" + i + "] ('" + id + "') is empty and the"
                            + " level is not free roam", ref problems);
                    }
                    continue;
                }

                float fromStart = Vector3.Distance(entry.position,
                    starts.GetArrayElementAtIndex(i).vector3Value);
                if (fromStart > trust)
                {
                    Report("launcher.levelEntries[" + i + "] ('" + id + "') is "
                        + fromStart.ToString("F1") + " m from the level's ball start; the"
                        + " launcher trusts an entry only within " + trust.ToString("F0")
                        + " m and would land the player on the seat instead", ref problems);
                }
                else if (fromStart > 5f)
                {
                    Warn("launcher.levelEntries[" + i + "] ('" + id + "') is "
                        + fromStart.ToString("F1") + " m from the level's ball start");
                }
                else
                {
                    Debug.Log("[hub-check] level '" + id + "' entry is " + fromStart.ToString("F1")
                        + " m from its ball start");
                }

                if (hubCenter != null
                    && Vector3.Distance(entry.position, hubCenter.position) < 50f)
                {
                    Warn("level '" + id + "' entry is only "
                        + Vector3.Distance(entry.position, hubCenter.position).ToString("F1")
                        + " m from the room; the islands would overlap");
                }
            }
        }

        // VR drive: one VrStickMoveInput, wired to the scene's motor, whose round gate the
        // round controller closes (Docs/Design/ControlIntent/README.md). The ride gate's owner,
        // BallRideStation, is checked by the ride wiring, not here.
        private static void CheckVrStickInput(HubSettingsHost settings, ref int problems)
        {
            var adapters = Object.FindObjectsOfType<VrStickMoveInput>(true);
            if (adapters.Length != 1)
            {
                Report("expected exactly one VrStickMoveInput, found " + adapters.Length
                    + " (VR players drive the ball through it; run the hub assembly)", ref problems);
                return;
            }
            KatamariBallMotor motor = ObjectReference<KatamariBallMotor>(adapters[0], "motor",
                "VrStickMoveInput.motor (the ball it drives)", ref problems);
            var sceneMotor = Object.FindObjectOfType<KatamariBallMotor>();
            if (motor != null && sceneMotor != null && motor != sceneMotor)
                Report("VrStickMoveInput.motor is not the scene's KatamariBallMotor", ref problems);
            string settingsProblem = HubAssembler.ReadVrStickSettingsWiringProblem(adapters[0], settings);
            if (settingsProblem != null) Report(settingsProblem, ref problems);

            var round = Object.FindObjectOfType<StoryRoundController>();
            if (round != null)
            {
                VrStickMoveInput wired = ObjectReference<VrStickMoveInput>(round, "vrInput",
                    "round.vrInput (closes the VR adapter's round gate at the bell)", ref problems);
                if (wired != null && wired != adapters[0])
                    Report("round.vrInput points at a different VrStickMoveInput", ref problems);
            }
        }

        // The settings store, the record book and the panel display (HUB_ROOM.md): one each,
        // wired to each other; the three boards are instances of their panel prefabs and VRChat
        // interactable canvases (World Space,
        // VRCUiShape, BoxCollider, GraphicRaycaster, not on the UI layer, one EventSystem); every
        // setting row has two arrow Buttons with the right id/delta and onClick ->
        // SendCustomEvent("Press").
        private static int CheckPanels(HubRoomHost host, int entries, ref int problems)
        {
            var settings = Object.FindObjectsOfType<HubSettingsHost>(true);
            var records = Object.FindObjectsOfType<HubRecordBook>(true);
            var displays = Object.FindObjectsOfType<HubPanelDisplay>(true);
            if (settings.Length != 1 || records.Length != 1 || displays.Length != 1)
            {
                Report("expected one HubSettingsHost, HubRecordBook and HubPanelDisplay, found "
                    + settings.Length + "/" + records.Length + "/" + displays.Length, ref problems);
                return 0;
            }
            if (ObjectReference<HubSettingsHost>(host, "settings",
                "host.settings (the prompt's language)", ref problems) != settings[0])
                Report("host.settings is not the scene's HubSettingsHost", ref problems);
            ObjectReference<VrStickMoveInput>(settings[0], "vrInput",
                "settings.vrInput (VR controls setting -> stick scheme)", ref problems);
            if (ObjectReference<HubSettingsHost>(displays[0], "settings", "display.settings",
                ref problems) != settings[0]) Report("display.settings is another store", ref problems);
            if (ObjectReference<HubRecordBook>(displays[0], "records", "display.records",
                ref problems) != records[0]) Report("display.records is another book", ref problems);
            ObjectReference<StoryRoundController>(records[0], "controller", "records.controller",
                ref problems);
            if (ArraySize(records[0], "levelIds") != entries)
                Report("records.levelIds has " + ArraySize(records[0], "levelIds") + " ids, the"
                    + " launcher " + entries + " entries", ref problems);
            if (ArraySize(displays[0], "levelNameTexts") != entries)
                Report("display.levelNameTexts has " + ArraySize(displays[0], "levelNameTexts")
                    + " texts for " + entries + " levels", ref problems);
            if (ArraySize(displays[0], "settingValueTexts") != HubSettingsMath.Count)
                Report("display.settingValueTexts is not one per setting", ref problems);

            var arrows = Object.FindObjectsOfType<HubSettingButton>(true);
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < arrows.Length; i++)
            {
                var so = new SerializedObject(arrows[i]);
                int id = so.FindProperty("settingId").intValue;
                int delta = so.FindProperty("delta").intValue;
                if (so.FindProperty("settings").objectReferenceValue != settings[0])
                    Report(arrows[i].name + ".settings is not the scene's store", ref problems);
                if (!HubSettingsMath.IsValidId(id) || (delta != 1 && delta != -1)
                    || !seen.Add(id * 2 + (delta > 0 ? 1 : 0)))
                    Report(arrows[i].name + " has id " + id + " delta " + delta, ref problems);
                string arrowProblem = HubAssembler.PanelButtonProblem(arrows[i]);
                if (arrowProblem != null) Report(arrows[i].name + ": " + arrowProblem, ref problems);
            }
            foreach (string id in HubAssembler.BoardIds)
            {
                GameObject board = GameObject.Find(HubAssembler.BoardPrefix + id);
                string boardProblem = board == null ? "missing"
                    : HubAssembler.BoardCanvasProblem(board);
                if (boardProblem != null)
                {
                    Report(HubAssembler.BoardPrefix + id + ": " + boardProblem, ref problems);
                    continue;
                }
                string prefabPath = HubAssembler.PanelPrefabPath(id);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(board) as GameObject;
                if (source == null || AssetDatabase.GetAssetPath(source) != prefabPath)
                    Report(board.name + " is not an instance of " + prefabPath, ref problems);
            }
            int eventSystems = Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>(true).Length;
            if (eventSystems != 1)
                Report("expected one EventSystem (uGUI clicks), found " + eventSystems, ref problems);
            if (seen.Count != HubSettingsMath.Count * 2)
                Report("setting arrows cover " + seen.Count + " of " + (HubSettingsMath.Count * 2)
                    + " (id, direction) pairs", ref problems);
            return arrows.Length;
        }

        // Absorb feedback (sound + haptics): one host, wired to the ride seat and the settings
        // store in the proxy and the Udon table, a full voice pool, clip/class tables of equal
        // length, and every level's PickupFieldHost pointing at it. No clip at all is a note
        // (silent, not broken).
        private static void CheckAbsorbFeedback(HubSettingsHost settings, ref int problems)
        {
            var hosts = Object.FindObjectsOfType<AbsorbFeedbackHost>(true);
            if (hosts.Length != 1)
            {
                Report("expected exactly one AbsorbFeedbackHost, found " + hosts.Length, ref problems);
                return;
            }
            AbsorbFeedbackHost feedback = hosts[0];
            var seat = Object.FindObjectOfType<BallRideStation>(true);
            if (ObjectReference<BallRideStation>(feedback, "ride", "absorbFeedback.ride", ref problems) != seat)
                Report("AbsorbFeedbackHost.ride is not the scene's BallRideStation", ref problems);
            if (!UdonReferenceIs(feedback, "ride", seat))
                Report("AbsorbFeedbackHost.ride in the Udon table is not the seat's behaviour", ref problems);
            if (ObjectReference<HubSettingsHost>(feedback, "settings", "absorbFeedback.settings",
                ref problems) != settings || settings == null)
                Report("AbsorbFeedbackHost.settings is not the room's HubSettingsHost", ref problems);
            if (!UdonReferenceIs(feedback, "settings", settings))
                Report("AbsorbFeedbackHost.settings in the Udon table is not the settings behaviour", ref problems);

            SerializedProperty voices = Find(feedback, "voices");
            if (voices == null || voices.arraySize != AbsorbFeedbackMath.VoiceCount)
            {
                Report("AbsorbFeedbackHost.voices has " + (voices == null ? 0 : voices.arraySize)
                    + " sources, wants " + AbsorbFeedbackMath.VoiceCount, ref problems);
            }
            else
            {
                for (int i = 0; i < voices.arraySize; i++)
                    if (voices.GetArrayElementAtIndex(i).objectReferenceValue == null)
                        Report("AbsorbFeedbackHost.voices[" + i + "] is empty", ref problems);
            }
            SerializedProperty clips = Find(feedback, "clips");
            SerializedProperty classes = Find(feedback, "clipClasses");
            int clipCount = clips == null ? 0 : clips.arraySize;
            int classCount = classes == null ? 0 : classes.arraySize;
            if (clipCount != classCount)
                Report("AbsorbFeedbackHost has " + clipCount + " clips but " + classCount
                    + " clip classes", ref problems);
            for (int i = 0; classes != null && i < classCount; i++)
                if (classes.GetArrayElementAtIndex(i).intValue < 0)
                    Report("AbsorbFeedbackHost.clipClasses[" + i + "] is negative", ref problems);
            if (clipCount == 0)
                Debug.Log("[hub-check] AbsorbFeedbackHost has no clips: pickups are silent (haptics only)");

            foreach (PickupFieldHost field in Object.FindObjectsOfType<PickupFieldHost>(true))
            {
                SerializedProperty decor = Find(field, "decorativeOnly");
                if (decor != null && decor.boolValue) continue;
                if (OptionalReference<AbsorbFeedbackHost>(field, "absorbFeedback") != feedback)
                    Report("PickupFieldHost on '" + field.name + "' does not point absorbFeedback at"
                        + " the scene's AbsorbFeedbackHost", ref problems);
            }
        }

        // 碰撞线框、性能诊断、物品名 HUD 的接线：与 All-Maps 装配的读回是同一个实现
        // （RideUxAssembler.ReadLocalWiring），每处缺接线一条 problem，再打一行汇总。
        private static void CheckLocalViews(UnityEngine.SceneManagement.Scene scene, ref int problems)
        {
            var found = new System.Collections.Generic.List<string>();
            RideUxAssembler.LocalWiring wiring = RideUxAssembler.ReadLocalWiring(scene, found);
            for (int i = 0; i < found.Count; i++) Report(found[i], ref problems);
            Debug.Log("[hub-check] " + RideUxAssembler.LocalWiringSummary(wiring.DiagnosticsWired,
                wiring.DiagnosticsHosts, wiring.CatchWired, wiring.CatchHosts, wiring.WireframeFound,
                wiring.WireframeSettingsWired));
        }

        // The Udon table of owner holds field = target's backing UdonBehaviour.
        private static bool UdonReferenceIs(UdonSharp.UdonSharpBehaviour owner, string field,
            UdonSharp.UdonSharpBehaviour target)
        {
            if (target == null) return false;
            var backing = UdonSharpEditor.UdonSharpEditorUtility.GetBackingUdonBehaviour(target);
            object value;
            return backing != null && UdonVariableAccess.TryRead(owner, field, out value)
                && (value as VRC.Udon.UdonBehaviour) == backing;
        }

        private static void Report(string message, ref int problems)
        {
            problems++;
            Debug.LogWarning("[hub-check] " + message);
        }

        private static void Warn(string message)
        {
            Debug.LogWarning("[hub-check] " + message);
        }

        private static T ObjectReference<T>(Object owner, string field, string what,
            ref int problems) where T : Object
        {
            SerializedProperty property = Find(owner, field);
            if (property == null)
            {
                Report(what + ": no serialized field '" + field + "' (renamed?)", ref problems);
                return null;
            }
            T value = property.objectReferenceValue as T;
            if (value == null)
            {
                Report(what + " is not wired", ref problems);
                return null;
            }
            return value;
        }

        private static T OptionalReference<T>(Object owner, string field) where T : Object
        {
            SerializedProperty property = Find(owner, field);
            if (property == null) return null;
            return property.objectReferenceValue as T;
        }

        private static int ArraySize(Object owner, string field)
        {
            SerializedProperty property = Find(owner, field);
            if (property == null || !property.isArray) return 0;
            return property.arraySize;
        }

        private static T Element<T>(Object owner, string field, int index) where T : Object
        {
            SerializedProperty property = Find(owner, field);
            if (property == null || !property.isArray) return null;
            if (index < 0 || index >= property.arraySize) return null;
            return property.GetArrayElementAtIndex(index).objectReferenceValue as T;
        }

        private static float FloatField(Object owner, string field, float fallback)
        {
            SerializedProperty property = Find(owner, field);
            if (property == null) return fallback;
            return property.floatValue;
        }

        private static SerializedProperty Find(Object owner, string field)
        {
            if (owner == null) return null;
            return new SerializedObject(owner).FindProperty(field);
        }
    }
}
