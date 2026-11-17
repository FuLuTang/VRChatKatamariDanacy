using UnityEditor;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;
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
            if (OptionalReference<UnityEngine.UI.Text>(host, "promptText") == null)
            {
                Debug.Log("[hub-check] host.promptText is not wired: the room has no on-screen"
                    + " instruction line (walking into a circle still starts a level)");
            }

            int zones = ArraySize(host, "selectZones");
            float zoneRadius = FloatField(host, "selectRadiusMeters", 1.2f);
            float hubRadius = FloatField(host, "hubRadiusMeters", 12f);
            if (zones == 0)
            {
                Report("host.selectZones is empty: walking cannot start a level", ref problems);
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
            if (entries != zones)
            {
                Warn("launcher.levelEntries has " + entries + " entries but the room has " + zones
                    + " circles; index i of both is the same level, so they must match");
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
            if (roomSpawn != null && zones > 0)
            {
                Transform firstZone = Element<Transform>(host, "selectZones", 0);
                if (firstZone != null)
                {
                    float toFirstCircle = Vector3.Distance(roomSpawn.position, firstZone.position);
                    if (toFirstCircle <= zoneRadius + 0.5f)
                    {
                        Warn("launcher.roomSpawn is inside the first level circle ("
                            + toFirstCircle.ToString("F1") + " m <= radius " + zoneRadius
                            + "); the re-arm rule means the player would have to step out and"
                            + " back in after every round. Move the spawn outside every circle");
                    }
                }
            }
            if (host.gameObject == launcher.gameObject)
            {
                Warn("HubRoomHost and HubLevelLauncher share one GameObject; one behaviour per"
                    + " object is the project convention");
            }

            // The launcher's own panel line says "按开始进入…", which is the wrong verb for a
            // walk-in circle; the room's prompt is the host's Text instead.
            if (OptionalReference<UnityEngine.UI.Text>(launcher, "statusText") != null)
            {
                Warn("launcher.statusText is wired: it writes a press-to-start line that"
                    + " disagrees with the walk-in circle. Leave it unwired so the host's"
                    + " promptText is the room's only instruction line");
            }

            int controllers = Object.FindObjectsOfType<StoryRoundController>().Length;
            if (controllers != 1)
            {
                Report("expected exactly one active StoryRoundController, found " + controllers,
                    ref problems);
            }

            int presenters = Object.FindObjectsOfType<StoryResultPresenter>().Length;
            if (presenters == 0)
            {
                Warn("no StoryResultPresenter in the scene: the room has no result board. The"
                    + " board is the presenter's card (it owns the stars and the measurement);"
                    + " the room does not duplicate them");
            }

            Debug.Log("[hub-check] done: " + problems + " problem(s), " + zones + " circle(s), "
                + entries + " level entry/entries, zone radius " + zoneRadius
                + ", room radius " + hubRadius);
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
