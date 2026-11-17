using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRCKatamari.Editor.LevelBuild;
using VRCKatamari.Hub;
using VRCKatamari.Pickup;
using VRCKatamari.Riding;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// Puts the hub - room, level-select pads, start/return wiring and the result card's
    /// return button - into the open scene from the level list, and can be run again without
    /// changing anything it already did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why it is an assembler and not a scene edit. The reference scene
    /// (<c>Assets/_RefUnlicensed/Scenes/RefCancer.unity</c>) is git-ignored, so nothing
    /// done to it by hand survives a fresh copy, and the formal scene (StoryMode) needs the
    /// same hub. One routine builds both: it reads the level list
    /// (<c>Docs/Design/StoryMode/hub_levels.json</c>), keeps the entries the open scene can
    /// play, and makes the scene match. Every object it owns is found by type or by name
    /// before it is created, so a second run reports <c>+0 ~0</c> and a scene that already
    /// has a hub (StoryMode) is adopted, not duplicated.
    /// </para>
    /// <para>
    /// What it does, in order:
    /// </para>
    /// <list type="number">
    /// <item><description>Resolves the level list against the scene (which levels are
    /// present, where the player lands in each).</description></item>
    /// <item><description>Imports the hub room document (<c>hub</c>, an arena cell under
    /// <c>LevelContent/hub</c>) after making sure the cell has its inert
    /// <see cref="PickupFieldHost"/>.</description></item>
    /// <item><description>Anchors: <c>HubCenter</c> and <c>HubSpawn</c>; the scene
    /// descriptor's <c>spawns</c> is pointed at <c>HubSpawn</c>, so the player is born in
    /// the room and a level's own spawn is only where a start sends them.</description></item>
    /// <item><description>One pad per offered level, in a centred row, each with a name
    /// label; pads of levels the scene no longer offers are removed.</description></item>
    /// <item><description><c>HubRoom</c> (<see cref="HubRoomHost"/>) with a world-space
    /// prompt line, and <c>HubLevel</c> (<see cref="HubLevelLauncher"/>) with the compact
    /// entry/name lists; a return button next to the first level's entry.</description></item>
    /// <item><description>A "back to the hub" button on the result card, next to "try
    /// again". The card itself is the rider's (RideUxAssembler); this assembler does not move
    /// it.</description></item>
    /// <item><description>The round controller: <c>startOnLoad</c> off (the hub opens the
    /// round), and for a level whose list entry says so, the level id, clock, target and
    /// kill plane from that level's placement document.</description></item>
    /// </list>
    /// <para>
    /// Order matters once: importing a level that declares <c>meta.playerStart</c> points the
    /// scene descriptor's <c>spawns</c> at that level's spawn marker. Run this assembler
    /// after the last level import; it puts <c>spawns</c> back on <c>HubSpawn</c>, and the
    /// report says when it had to.
    /// </para>
    /// <para>
    /// Nothing is saved. New behaviours need their UdonSharp program asset; a field added
    /// to an existing behaviour is missing from the existing instances' variable tables
    /// until the scene is saved and reopened (Labs/README.md); the run ends with a read-back
    /// that names any such field, and the remedy is compile, save, reopen, run again.
    /// </para>
    /// </remarks>
    public static class HubAssembler
    {
        public const string MenuPath = "VRC Katamari/Hub/Assemble Hub";

        public const string HubLevelId = "hub";
        public const string HubCenterName = "HubCenter";
        public const string HubSpawnName = "HubSpawn";
        public const string HubRoomName = "HubRoom";
        public const string HubLevelName = "HubLevel";
        public const string PadsRootName = "HubPads";
        public const string PadPrefix = "HubPad_";
        public const string PromptBoardName = "HubPrompt";
        public const string CardReturnName = "HubReturnCardButton";
        public const string ReturnButtonName = "HubReturnButton";

        // The room is built around this point (the hub document's coordinates).
        private static readonly Vector3 HubCenterPosition = new Vector3(1200f, 0f, 0f);
        private static readonly Vector3 HubSpawnPosition = new Vector3(1200f, 0.1f, -2.8f);

        // World-space text: metres per canvas unit, so 0.004 puts a 60-unit glyph at 24 cm.
        private const float PromptScale = 0.004f;
        private static readonly Vector2 PromptSize = new Vector2(1200f, 120f);
        private static readonly Vector3 PromptOffset = new Vector3(0f, 2.55f, 3.85f);

        // The result card's two buttons (canvas units): retry on the left, return on the right.
        public static readonly Vector2 CardButtonSize = new Vector2(300f, 80f);
        public const float CardRetryX = -170f;
        public const float CardReturnOffsetX = 340f;
        public const float CardButtonY = 60f;

        private const float PadThicknessM = 0.02f;
        private const float PadLabelScale = 0.004f;
        private static readonly Vector2 PadLabelSize = new Vector2(640f, 120f);
        private const float PadLabelHeightM = 1.6f;

        private const float InteractProximity = 5f;

        private sealed class Counters
        {
            public int Created;
            public int Changed;
            public readonly List<string> Problems = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public readonly List<Component> Proxies = new List<Component>();
        }

        [MenuItem(MenuPath, false, 405)]
        public static void Assemble()
        {
            Debug.Log(AssembleOpenScene());
        }

        /// <summary>
        /// Runs the assembly on the active scene and returns the report (also what the menu
        /// logs). Safe to call repeatedly.
        /// </summary>
        public static string AssembleOpenScene()
        {
            var c = new Counters();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return Finish(c, "stopped: exit Play Mode first; the assembly writes scene data");
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return Finish(c, "stopped: there is no loaded active scene");
            }

            var catalogErrors = new List<string>();
            HubLevelCatalog.CatalogFile catalog;
            if (!HubLevelCatalog.TryLoad(out catalog, catalogErrors))
            {
                for (int i = 0; i < catalogErrors.Count; i++) c.Problems.Add(catalogErrors[i]);
                return Finish(c, "stopped: the level list is unusable");
            }

            HubLevelCatalog.Resolved[] resolved = HubLevelCatalog.ResolveAgainst(catalog, scene);
            List<HubLevelCatalog.Resolved> offered = HubLevelCatalog.Offered(resolved);
            ReportResolution(scene, resolved, c);
            if (offered.Count == 0)
            {
                return Finish(c, "stopped: scene '" + scene.name + "' offers no level; import a"
                    + " level listed for it in " + HubLevelCatalog.RepoRelativePath);
            }

            StoryRoundController round = Single<StoryRoundController>(c);
            if (round == null)
            {
                return Finish(c, "stopped: the scene needs exactly one round controller");
            }
            BallRideStation seat = FindOptional<BallRideStation>();
            VRC.SDK3.Components.VRCStation station = seat != null
                ? seat.GetComponent<VRC.SDK3.Components.VRCStation>() : null;

            try
            {
                UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
            }
            catch (Exception exception)
            {
                c.Problems.Add("the UdonSharp compile step threw: " + exception.Message);
            }

            Font font = null;
            Text targetText = ReadObject<Text>(round, "targetText");
            if (targetText != null) font = targetText.font;
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            AssembleRoomGeometry(c);
            Transform center = EnsureAnchor(HubCenterName, HubCenterPosition, Quaternion.identity, c);
            Transform spawn = EnsureAnchor(HubSpawnName, HubSpawnPosition, Quaternion.identity, c);
            AssembleSceneSpawn(spawn, c);

            Transform[] pads = AssemblePads(offered, catalog.room, center, font, c);
            Text prompt = AssemblePrompt(center, font, c);
            HubLevelLauncher launcher = AssembleLauncher(round, offered, spawn, seat, station, c);
            HubRoomHost host = AssembleRoomHost(launcher, round, center, pads, catalog.room, prompt, c);
            AssembleReturnButton(launcher, offered[0].EntryTransform, c);
            AssembleCardReturnButton(round, launcher, font, c);
            AssembleRound(round, offered, c);

            CopyAllToUdon(c);
            VerifyTables(launcher, host, c);

            string report = Finish(c, "scene '" + scene.name + "': " + offered.Count
                + " level(s) offered");
            return report;
        }

        // ------------------------------------------------------------ level list ---

        private static void ReportResolution(Scene scene, HubLevelCatalog.Resolved[] resolved,
            Counters c)
        {
            for (int i = 0; i < resolved.Length; i++)
            {
                HubLevelCatalog.Resolved r = resolved[i];
                string id = r.Entry.id;
                if (r.Offered)
                {
                    c.Notes.Add("offered: " + id + " ('" + r.Entry.name + "'), entry "
                        + Describe(r.EntryTransform));
                }
                else if (!r.SceneListed)
                {
                    c.Notes.Add("not offered: " + id + " is listed for scenes ["
                        + string.Join(", ", r.Entry.scenes) + "], not '" + scene.name + "'");
                }
                else
                {
                    c.Notes.Add("not offered: " + id + " is listed for this scene but the scene"
                        + " lacks " + (r.ContentPresent ? "" : "'" + r.Entry.content + "' ")
                        + (r.EntryTransform == null ? "'" + r.Entry.entry + "'" : "")
                        + " (import the level, then run this again)");
                }
            }
        }

        // ------------------------------------------------------------ room geometry ---

        // The room is the hub placement document, imported as an arena cell (HUB_ROOM 7.2):
        // the importer has one managed root per scene and the first level owns it. The cell
        // needs its own inert PickupFieldHost for the five decor props, so the host is added
        // first and the import runs second; a missing host would fail the import.
        private static void AssembleRoomGeometry(Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform content = FindRoot(scene, PlacementImporter.ManagedRootName);
            if (content == null)
            {
                var go = new GameObject(PlacementImporter.ManagedRootName);
                Undo.RegisterCreatedObjectUndo(go, "Create LevelContent");
                content = go.transform;
                c.Created++;
            }

            Transform hub = content.Find(HubLevelId);
            if (hub == null)
            {
                var go = new GameObject(HubLevelId);
                Undo.RegisterCreatedObjectUndo(go, "Create hub room folder");
                Undo.SetTransformParent(go.transform, content, "Create hub room folder");
                hub = go.transform;
                c.Created++;
            }

            if (hub.GetComponentInChildren<PickupFieldHost>(true) == null)
            {
                var go = new GameObject("PickupField");
                Undo.RegisterCreatedObjectUndo(go, "Create hub pickup host");
                Undo.SetTransformParent(go.transform, hub, "Create hub pickup host");
                AddUdon<PickupFieldHost>(go, c);
                c.Notes.Add("added the room's inert PickupFieldHost (no motor: the five decor"
                    + " props only need the cell contract)");
            }

            var report = new PlacementImportReport();
            bool ok = PlacementImporter.RunImport(HubLevelId, report);
            report.Log();
            c.Notes.Add("hub room import: " + report.BuildSummary());
            if (!ok)
            {
                c.Problems.Add("the hub room import was not accepted (see the [level] lines)");
            }
        }

        private static Transform EnsureAnchor(string name, Vector3 position, Quaternion rotation,
            Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform existing = FindRoot(scene, name);
            if (existing != null)
            {
                if (Vector3.Distance(existing.position, position) > 0.05f)
                {
                    c.Notes.Add(name + " is at " + existing.position + ", not the room's "
                        + position + "; left where it is");
                }
                return existing;
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.position = position;
            go.transform.rotation = rotation;
            c.Created++;
            return go.transform;
        }

        private static void AssembleSceneSpawn(Transform spawn, Counters c)
        {
            VRC.SDKBase.VRC_SceneDescriptor[] descriptors =
                UnityEngine.Object.FindObjectsOfType<VRC.SDKBase.VRC_SceneDescriptor>();
            if (descriptors.Length != 1)
            {
                c.Problems.Add("expected exactly one VRC_SceneDescriptor, found "
                    + descriptors.Length + "; the spawn was not set");
                return;
            }

            VRC.SDKBase.VRC_SceneDescriptor descriptor = descriptors[0];
            Transform[] spawns = descriptor.spawns;
            bool already = spawns != null && spawns.Length == 1 && spawns[0] == spawn;
            if (!already)
            {
                string before = spawns != null && spawns.Length > 0 && spawns[0] != null
                    ? Describe(spawns[0]) : "none";
                Undo.RecordObject(descriptor, "Point the world spawn at the hub");
                descriptor.spawns = new[] { spawn };
                EditorUtility.SetDirty(descriptor);
                MarkDirty();
                c.Changed++;
                c.Notes.Add("world spawn moved from " + before + " to " + HubSpawnName
                    + " (a level import points it at that level's spawn; run this after the"
                    + " last import)");
            }

            if (spawn.position.y < descriptor.RespawnHeightY + 1f)
            {
                c.Problems.Add("the room floor (y=" + spawn.position.y + ") is not above the"
                    + " scene's respawn height (y=" + descriptor.RespawnHeightY + "): a player"
                    + " born in the room would respawn at once. Lower RespawnHeightY below the"
                    + " room or raise the room");
            }
        }

        // ------------------------------------------------------------ pads ---

        private static Transform[] AssemblePads(List<HubLevelCatalog.Resolved> offered,
            HubLevelCatalog.RoomBlock room, Transform center, Font font, Counters c)
        {
            var result = new Transform[offered.Count];

            if (!HubSelectMath.PadsFit(offered.Count, room.padSizeM, room.padGapM, room.marginM,
                RoomWidth(center)))
            {
                c.Problems.Add(offered.Count + " pad(s) need a room "
                    + HubSelectMath.RoomWidthNeeded(offered.Count, room.padSizeM, room.padGapM,
                        room.marginM).ToString("0.0#") + " m wide; the hub room is "
                    + RoomWidth(center).ToString("0.0#") + " m. Widen the hub document's"
                    + " floor and walls (Docs/Design/Levels/hub/placement.json)");
            }

            Transform root = EnsureChild(null, PadsRootName, c);
            var wanted = new HashSet<string>();
            for (int i = 0; i < offered.Count; i++)
            {
                string padName = PadPrefix + offered[i].Entry.id;
                wanted.Add(padName);

                Vector3 padCenter;
                if (!HubSelectMath.TryPadCenter(center.position, room.padForwardM, i,
                    offered.Count, room.padSizeM, room.padGapM, out padCenter))
                {
                    c.Problems.Add("pad " + i + " ('" + offered[i].Entry.id + "') has no valid"
                        + " position: check the room block of the level list");
                    continue;
                }
                padCenter.y = center.position.y + PadThicknessM * 0.5f;

                Transform pad = root.Find(padName);
                if (pad == null)
                {
                    GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = padName;
                    // A flat, collider-free tile: the player walks over it, the room host
                    // only measures a distance to its centre.
                    Collider collider = cube.GetComponent<Collider>();
                    if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
                    Undo.RegisterCreatedObjectUndo(cube, "Create " + padName);
                    Undo.SetTransformParent(cube.transform, root, "Create " + padName);
                    pad = cube.transform;
                    c.Created++;
                }

                Vector3 scale = new Vector3(room.padSizeM, PadThicknessM, room.padSizeM);
                if (Vector3.Distance(pad.position, padCenter) > 1e-4f
                    || Vector3.Distance(pad.localScale, scale) > 1e-4f)
                {
                    Undo.RecordObject(pad, "Place " + padName);
                    pad.position = padCenter;
                    pad.rotation = Quaternion.identity;
                    pad.localScale = scale;
                    c.Changed++;
                    MarkDirty();
                }
                result[i] = pad;

                AssemblePadLabel(pad, offered[i].Entry.name, room, font, c);
            }

            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (!child.name.StartsWith(PadPrefix, StringComparison.Ordinal)) continue;
                if (wanted.Contains(child.name)) continue;
                c.Notes.Add("removed the pad '" + child.name + "': the scene no longer offers"
                    + " that level");
                Undo.DestroyObjectImmediate(child.gameObject);
                c.Changed++;
            }

            return result;
        }

        private static void AssemblePadLabel(Transform pad, string levelName,
            HubLevelCatalog.RoomBlock room, Font font, Counters c)
        {
            Transform existing = pad.Find("Label");
            Text text;
            if (existing == null)
            {
                GameObject canvas = CreateWorldCanvas("Label", pad, PadLabelSize);
                text = CreateText("Text", canvas.transform, font, 64, TextAnchor.MiddleCenter);
                Stretch((RectTransform)text.transform);
                c.Created++;
                existing = canvas.transform;
            }
            else
            {
                text = existing.GetComponentInChildren<Text>(true);
            }

            // World placement, set on every run so a moved pad takes its label with it.
            Vector3 padWorld = pad.position;
            Vector3 labelPosition = new Vector3(padWorld.x, padWorld.y + PadLabelHeightM,
                padWorld.z);
            Vector3 labelScale = new Vector3(PadLabelScale, PadLabelScale, PadLabelScale);
            Vector3 localScale = WorldToLocalScale(pad, labelScale);
            if (Vector3.Distance(existing.position, labelPosition) > 1e-4f
                || Vector3.Distance(existing.localScale, localScale) > 1e-6f)
            {
                Undo.RecordObject(existing, "Place pad label");
                existing.position = labelPosition;
                existing.rotation = Quaternion.identity;
                existing.localScale = localScale;
                c.Changed++;
                MarkDirty();
            }

            if (text != null && !string.Equals(text.text, levelName))
            {
                Undo.RecordObject(text, "Name pad");
                text.text = levelName;
                EditorUtility.SetDirty(text);
                c.Changed++;
            }
        }

        // A child's local scale that gives it the wanted world scale under a scaled parent.
        private static Vector3 WorldToLocalScale(Transform parent, Vector3 world)
        {
            Vector3 p = parent.lossyScale;
            return new Vector3(
                Mathf.Abs(p.x) < 1e-6f ? world.x : world.x / p.x,
                Mathf.Abs(p.y) < 1e-6f ? world.y : world.y / p.y,
                Mathf.Abs(p.z) < 1e-6f ? world.z : world.z / p.z);
        }

        // The room's width along X, read from the imported floor's body. The structure marker
        // (hub_floor) stays at scale 1; the authored size lives on its Visual child, whose
        // lossy scale is the document's size x. A missing floor or body reads as a very wide
        // room so the missing import is reported by the import, not twice.
        private static float RoomWidth(Transform center)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform floor = HubLevelCatalog.FindPath(scene,
                PlacementImporter.ManagedRootName + "/" + HubLevelId + "/Structures/hub_floor");
            if (floor == null) return 1000f;
            Transform body = floor.Find("Visual");
            if (body == null) return 1000f;
            return Mathf.Abs(body.lossyScale.x);
        }

        // ------------------------------------------------------------ prompt ---

        // The room's instruction line. An overlay canvas is not drawn by the VRChat client,
        // so an existing prompt on one is replaced by a world-space line on the room's wall.
        private static Text AssemblePrompt(Transform center, Font font, Counters c)
        {
            HubRoomHost existingHost = FindOptional<HubRoomHost>();
            Text wired = existingHost != null ? ReadObject<Text>(existingHost, "promptText") : null;
            if (wired != null)
            {
                Canvas canvas = wired.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.renderMode == RenderMode.WorldSpace) return wired;
                c.Notes.Add("the room prompt '" + wired.name + "' is on a screen-space canvas,"
                    + " which the VRChat client does not draw; a world-space line replaces it"
                    + " (the old object is left in place)");
            }

            Scene scene = SceneManager.GetActiveScene();
            Transform board = FindRoot(scene, PromptBoardName);
            if (board == null)
            {
                GameObject canvas = CreateWorldCanvas(PromptBoardName, null, PromptSize);
                canvas.transform.position = center.position + PromptOffset;
                canvas.transform.rotation = Quaternion.identity;
                canvas.transform.localScale = new Vector3(PromptScale, PromptScale, PromptScale);
                Text text = CreateText("Text", canvas.transform, font, 56, TextAnchor.MiddleCenter);
                Stretch((RectTransform)text.transform);
                c.Created++;
                board = canvas.transform;
            }

            return board.GetComponentInChildren<Text>(true);
        }

        // ------------------------------------------------------------ launcher / host ---

        private static HubLevelLauncher AssembleLauncher(StoryRoundController round,
            List<HubLevelCatalog.Resolved> offered, Transform spawn, BallRideStation seat,
            VRC.SDK3.Components.VRCStation station, Counters c)
        {
            HubLevelLauncher launcher = FindOptional<HubLevelLauncher>();
            if (launcher == null)
            {
                GameObject go = FindOrCreateRoot(HubLevelName, c);
                launcher = AddUdon<HubLevelLauncher>(go, c);
                if (launcher == null) return null;
            }

            var entries = new Transform[offered.Count];
            var names = new string[offered.Count];
            for (int i = 0; i < offered.Count; i++)
            {
                entries[i] = offered[i].EntryTransform;
                names[i] = offered[i].Entry.name;
            }

            var so = new SerializedObject(launcher);
            bool changed = false;
            changed |= SetObject(so, "controller", round, c);
            changed |= SetObject(so, "roomSpawn", spawn, c);
            changed |= SetObjectArray(so, "levelEntries", entries, c);
            changed |= SetStringArray(so, "levelNames", names, c);
            if (seat != null && station != null)
            {
                changed |= SetObject(so, "rideSeat", seat, c);
                changed |= SetObject(so, "rideStation", station, c);
            }
            else
            {
                c.Notes.Add("no BallRideStation with a VRCStation: the launcher runs without a"
                    + " seat (no auto-seat, no station exit before a return)");
            }
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(launcher);
            }
            return launcher;
        }

        private static HubRoomHost AssembleRoomHost(HubLevelLauncher launcher,
            StoryRoundController round, Transform center, Transform[] pads,
            HubLevelCatalog.RoomBlock room, Text prompt, Counters c)
        {
            HubRoomHost host = FindOptional<HubRoomHost>();
            if (host == null)
            {
                GameObject go = FindOrCreateRoot(HubRoomName, c);
                host = AddUdon<HubRoomHost>(go, c);
                if (host == null) return null;
            }

            var so = new SerializedObject(host);
            bool changed = false;
            changed |= SetObject(so, "launcher", launcher, c);
            changed |= SetObject(so, "controller", round, c);
            changed |= SetObject(so, "hubCenter", center, c);
            changed |= SetObjectArray(so, "selectZones", pads, c);
            if (prompt != null) changed |= SetObject(so, "promptText", prompt, c);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(host);
            }
            return host;
        }

        // ------------------------------------------------------------ return button ---

        // The manual way home if the automatic return after a round fails. One per scene,
        // next to the first level's entry; the launcher refuses it while a round runs.
        private static void AssembleReturnButton(HubLevelLauncher launcher, Transform entry,
            Counters c)
        {
            HubReturnButton button = FindOptional<HubReturnButton>();
            if (button == null)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = ReturnButtonName;
                Undo.RegisterCreatedObjectUndo(cube, "Create " + ReturnButtonName);
                cube.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
                cube.transform.position = entry.position + entry.right * 1.2f + Vector3.up * 1.0f;
                c.Created++;
                button = AddUdon<HubReturnButton>(cube, c);
                if (button == null) return;
                SetInteractText(button, "回大厅", c);
            }

            var so = new SerializedObject(button);
            if (SetObject(so, "launcher", launcher, c))
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(button);
            }
        }

        // ------------------------------------------------------------ result card ---

        // The result card is the rider's (RideUxAssembler puts it in front of the seat) and
        // this assembler does not move it, resize it or touch its retry button. It adds one
        // thing: a "back to the hub" button next to "try again", as a child of the retry
        // button so the round's own SetActive shows both together. The button runs the same
        // launcher call as the level's return button, so there is one way home.
        //
        // The two buttons share one layout (LayoutRetryForReturn, which RideUxAssembler
        // also calls when it lays the card out), so the result is the same whichever
        // assembler ran first and a second run changes nothing.
        private static void AssembleCardReturnButton(StoryRoundController round,
            HubLevelLauncher launcher, Font font, Counters c)
        {
            GameObject retryRoot = ReadObject<GameObject>(round, "retryButtonRoot");
            if (retryRoot == null)
            {
                c.Notes.Add("the round has no retry button root: the result card gets no"
                    + " 'back to the hub' button (the level's own return button still works)");
                return;
            }

            var retryRect = retryRoot.GetComponent<RectTransform>();
            if (retryRect != null && LayoutRetryForReturn(retryRect))
            {
                c.Changed++;
                MarkDirty();
            }
            SyncRetryCollider(retryRoot, c);

            Transform existing = retryRoot.transform.Find(CardReturnName);
            HubReturnButton button;
            if (existing == null)
            {
                var go = new GameObject(CardReturnName, typeof(RectTransform), typeof(Image),
                    typeof(BoxCollider));
                Undo.RegisterCreatedObjectUndo(go, "Create " + CardReturnName);
                Undo.SetTransformParent(go.transform, retryRoot.transform,
                    "Create " + CardReturnName);
                var image = go.GetComponent<Image>();
                image.color = new Color(0f, 0f, 0f, 0.6f);
                image.raycastTarget = false;
                Text label = CreateText("Label", go.transform, font, 36, TextAnchor.MiddleCenter);
                label.text = "回大厅";
                Stretch((RectTransform)label.transform);
                c.Created++;

                button = AddUdon<HubReturnButton>(go, c);
                if (button == null) return;
                SetInteractText(button, "回大厅", c);
                existing = go.transform;
            }
            else
            {
                button = existing.GetComponent<HubReturnButton>();
                if (button == null)
                {
                    c.Problems.Add("'" + CardReturnName + "' exists without a HubReturnButton");
                    return;
                }
            }

            var rect = (RectTransform)existing;
            if (LayoutReturnButton(rect))
            {
                c.Changed++;
                MarkDirty();
            }
            var box = existing.GetComponent<BoxCollider>();
            Vector3 wanted = new Vector3(CardButtonSize.x, CardButtonSize.y, 40f);
            if (box != null && (!box.isTrigger || box.size != wanted || box.center != Vector3.zero))
            {
                Undo.RecordObject(box, "Size the return hit box");
                box.isTrigger = true;
                box.size = wanted;
                box.center = Vector3.zero;
                c.Changed++;
            }

            var so = new SerializedObject(button);
            if (SetObject(so, "launcher", launcher, c))
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(button);
            }
        }

        /// <summary>
        /// The retry button's place on the card when the "back to the hub" button sits to
        /// its right: bottom-centre anchor, 170 units left of centre, 300 x 80. Returns
        /// whether anything changed. Shared with the ride assembly so both lay the card out
        /// the same way.
        /// </summary>
        public static bool LayoutRetryForReturn(RectTransform retry)
        {
            bool changed = false;
            Vector2 anchor = new Vector2(0.5f, 0f);
            Vector2 pivot = new Vector2(0.5f, 0.5f);
            Vector2 position = new Vector2(CardRetryX, CardButtonY);
            if (retry.anchorMin != anchor) { retry.anchorMin = anchor; changed = true; }
            if (retry.anchorMax != anchor) { retry.anchorMax = anchor; changed = true; }
            if (retry.pivot != pivot) { retry.pivot = pivot; changed = true; }
            if (retry.sizeDelta != CardButtonSize) { retry.sizeDelta = CardButtonSize; changed = true; }
            if (retry.anchoredPosition != position)
            {
                retry.anchoredPosition = position;
                changed = true;
            }
            return changed;
        }

        // The return button is a child of the retry button, centred 340 units to its right.
        private static bool LayoutReturnButton(RectTransform rect)
        {
            bool changed = false;
            Vector2 anchor = new Vector2(0.5f, 0.5f);
            Vector2 position = new Vector2(CardReturnOffsetX, 0f);
            if (rect.anchorMin != anchor) { rect.anchorMin = anchor; changed = true; }
            if (rect.anchorMax != anchor) { rect.anchorMax = anchor; changed = true; }
            if (rect.pivot != anchor) { rect.pivot = anchor; changed = true; }
            if (rect.sizeDelta != CardButtonSize) { rect.sizeDelta = CardButtonSize; changed = true; }
            if (rect.anchoredPosition != position) { rect.anchoredPosition = position; changed = true; }
            if (rect.localRotation != Quaternion.identity) { rect.localRotation = Quaternion.identity; changed = true; }
            if (rect.localScale != Vector3.one) { rect.localScale = Vector3.one; changed = true; }
            return changed;
        }

        // The retry button's press box follows its (possibly just changed) face, the same
        // rule the ride assembly uses, so neither run leaves it stale.
        private static void SyncRetryCollider(GameObject retryRoot, Counters c)
        {
            StoryRetryButton direct = retryRoot.GetComponentInChildren<StoryRetryButton>(true);
            if (direct == null) return;
            var box = direct.GetComponent<BoxCollider>();
            if (box == null) return;
            Vector3 wanted = new Vector3(CardButtonSize.x, CardButtonSize.y, 40f);
            if (box.size == wanted) return;
            Undo.RecordObject(box, "Size the retry hit box");
            box.size = wanted;
            c.Changed++;
        }

        // ------------------------------------------------------------ round ---

        // The hub opens the round, so the round must not open itself at load. For a level
        // whose list entry says so, its clock, target and kill plane come from that level's
        // own placement document - one source, not a copy in the list. A scene can hold one
        // round controller, so only the first offered level configures it.
        private static void AssembleRound(StoryRoundController round,
            List<HubLevelCatalog.Resolved> offered, Counters c)
        {
            var so = new SerializedObject(round);
            bool changed = SetBool(so, "startOnLoad", false, c);

            if (offered.Count > 1)
            {
                c.Problems.Add(offered.Count + " levels are offered in one scene, but the scene has"
                    + " one ball and one round controller: only '" + offered[0].Entry.id
                    + "' is configured, and a start on the others would play it with that"
                    + " level's clock. List one level per scene until the round can be"
                    + " configured per level");
            }

            HubLevelCatalog.LevelEntry first = offered[0].Entry;
            if (first.roundFromPlacement)
            {
                changed |= ApplyPlacementRound(so, first.id, c);
            }

            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(round);
            }
        }

        private static bool ApplyPlacementRound(SerializedObject so, string levelId, Counters c)
        {
            string path;
            List<string> tried;
            if (!LevelPaths.TryResolvePlacementPath(levelId, out path, out tried))
            {
                c.Problems.Add("no placement document for '" + levelId + "', so its round"
                    + " settings were not applied. Searched: " + string.Join("; ", tried.ToArray()));
                return false;
            }

            string text;
            try
            {
                text = System.IO.File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                c.Problems.Add("cannot read " + path + ": " + exception.Message);
                return false;
            }
            if (!PlacementV2Reader.LooksLikeV2(text))
            {
                c.Notes.Add("'" + levelId + "' has a v1 placement document: no round settings to"
                    + " read; the controller keeps its own");
                return false;
            }

            PlacementV2ReadResult read = PlacementV2Reader.Read(text);
            if (!read.Ok)
            {
                c.Problems.Add("the placement document of '" + levelId + "' did not parse: "
                    + (read.Errors.Count > 0 ? read.Errors[0] : "unknown error"));
                return false;
            }

            PlacementV2Meta meta = read.Document.Meta;
            bool changed = false;
            changed |= SetString(so, "levelId", levelId, c);
            if (meta.HasTimeLimit && meta.TimeLimitS > 0f)
            {
                changed |= SetFloat(so, "timeLimitSeconds", meta.TimeLimitS, c);
            }
            if (meta.Ball != null && meta.Ball.HasTargetDiameter && meta.Ball.TargetDiameterM > 0f)
            {
                changed |= SetEnum(so, "goalType", (int)StoryGoalType.ReachTargetDiameter, c);
                changed |= SetFloat(so, "targetDiameterM", meta.Ball.TargetDiameterM, c);
            }
            PlacementV2Activity activity = read.Document.Activity;
            if (activity != null && activity.KillPlane != null)
            {
                changed |= SetBool(so, "killPlaneEnabled", true, c);
                changed |= SetFloat(so, "killPlaneY", activity.KillPlane.Y, c);
            }
            c.Notes.Add("round settings of '" + levelId + "' read from its placement document:"
                + " clock " + (meta.HasTimeLimit ? meta.TimeLimitS + " s" : "unchanged")
                + ", target "
                + (meta.Ball != null && meta.Ball.HasTargetDiameter
                    ? meta.Ball.TargetDiameterM + " m" : "unchanged")
                + ", kill plane "
                + (activity != null && activity.KillPlane != null
                    ? "y=" + activity.KillPlane.Y : "unchanged"));
            return changed;
        }

        // ------------------------------------------------------------ udon plumbing ---

        private static T AddUdon<T>(GameObject go, Counters c) where T : UdonSharpBehaviour
        {
            try
            {
                T added = UdonSharpUndo.AddComponent<T>(go);
                c.Created++;
                c.Proxies.Add(added);
                return added;
            }
            catch (Exception exception)
            {
                c.Problems.Add("could not add " + typeof(T).Name + " to '" + go.name + "': "
                    + exception.Message + " (does " + typeof(T).Name + ".asset, its UdonSharp"
                    + " program asset, exist next to the .cs?)");
                return null;
            }
        }

        // The click prompt and its reach are fields of the backing UdonBehaviour, not of the
        // U# proxy.
        private static void SetInteractText(UdonSharpBehaviour proxy, string text, Counters c)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            if (backing == null)
            {
                c.Problems.Add(proxy.GetType().Name + " has no backing UdonBehaviour yet");
                return;
            }
            if (backing.interactText != text
                || !Mathf.Approximately(backing.proximity, InteractProximity))
            {
                backing.interactText = text;
                backing.proximity = InteractProximity;
                EditorUtility.SetDirty(backing);
                c.Changed++;
            }
        }

        private static void CopyAllToUdon(Counters c)
        {
            var done = new HashSet<Component>();
            for (int i = 0; i < c.Proxies.Count; i++)
            {
                var proxy = c.Proxies[i] as UdonSharpBehaviour;
                if (proxy == null || !done.Add(proxy)) continue;
                try
                {
                    UdonSharpEditorUtility.CopyProxyToUdon(proxy);
                }
                catch (Exception exception)
                {
                    c.Problems.Add("CopyProxyToUdon threw for " + proxy.GetType().Name + " on '"
                        + proxy.name + "': " + exception.Message + ". Compile, save the scene,"
                        + " reopen it and run the assembly again");
                }
            }
        }

        // Reads the instance variable table (a non-public field) for the fields this assembly
        // writes: a missing symbol means "save, reopen, run again".
        private static void VerifyTables(HubLevelLauncher launcher, HubRoomHost host, Counters c)
        {
            if (launcher != null)
            {
                VerifyTable(launcher, new[] { "controller", "levelEntries", "levelNames",
                    "roomSpawn", "rideStation", "rideSeat", "entryTrustMeters" }, c);
            }
            if (host != null)
            {
                VerifyTable(host, new[] { "launcher", "controller", "hubCenter", "selectZones",
                    "promptText" }, c);
            }
            HubStartButton[] starts = UnityEngine.Object.FindObjectsOfType<HubStartButton>();
            for (int i = 0; i < starts.Length; i++)
            {
                VerifyTable(starts[i], new[] { "launcher", "levelIndex" }, c);
            }
        }

        private static void VerifyTable(UdonSharpBehaviour proxy, string[] names, Counters c)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            if (backing == null) return;
            FieldInfo field = typeof(UdonBehaviour).GetField("publicVariables",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return;
            var table = field.GetValue(backing) as IUdonVariableTable;
            if (table == null) return;
            var missing = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                object value;
                if (!table.TryGetVariableValue(names[i], out value)) missing.Add(names[i]);
            }
            if (missing.Count > 0)
            {
                c.Problems.Add(proxy.GetType().Name + " on '" + proxy.name + "' lacks Udon"
                    + " variables: " + string.Join(", ", missing.ToArray())
                    + ". Save the scene, reopen it and run the assembly again");
            }
        }

        // ------------------------------------------------------------ ui plumbing ---

        private static GameObject CreateWorldCanvas(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            if (parent != null) go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            return go;
        }

        private static Text CreateText(string name, Transform parent, Font font, int size,
            TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            if (font != null) text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.raycastTarget = false;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // ------------------------------------------------------------ scene plumbing ---

        private static Transform FindRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, name)) return roots[i].transform;
            }
            return null;
        }

        private static Transform EnsureChild(Transform parent, string name, Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform existing = parent == null ? FindRoot(scene, name) : parent.Find(name);
            if (existing != null) return existing;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            if (parent != null) Undo.SetTransformParent(go.transform, parent, "Create " + name);
            c.Created++;
            return go.transform;
        }

        private static GameObject FindOrCreateRoot(string name, Counters c)
        {
            return EnsureChild(null, name, c).gameObject;
        }

        private static void MarkDirty()
        {
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        private static string Describe(Transform t)
        {
            if (t == null) return "none";
            Vector3 p = t.position;
            return t.name + " (" + p.x.ToString("0.##") + ", " + p.y.ToString("0.##") + ", "
                + p.z.ToString("0.##") + ")";
        }

        // ------------------------------------------------------------ serialized access ---

        private static bool SetFloat(SerializedObject so, string field, float value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (Mathf.Approximately(property.floatValue, value)) return false;
            property.floatValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetBool(SerializedObject so, string field, bool value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (property.boolValue == value) return false;
            property.boolValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetEnum(SerializedObject so, string field, int value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (property.intValue == value) return false;
            property.intValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetString(SerializedObject so, string field, string value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (string.Equals(property.stringValue, value)) return false;
            property.stringValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetObject(SerializedObject so, string field, UnityEngine.Object value,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (property.objectReferenceValue == value) return false;
            property.objectReferenceValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetObjectArray(SerializedObject so, string field,
            UnityEngine.Object[] values, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);

            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = property.GetArrayElementAtIndex(i).objectReferenceValue == values[i];
            }
            if (same) return false;

            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
            c.Changed++;
            return true;
        }

        private static bool SetStringArray(SerializedObject so, string field, string[] values,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);

            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = string.Equals(property.GetArrayElementAtIndex(i).stringValue, values[i]);
            }
            if (same) return false;

            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).stringValue = values[i];
            }
            c.Changed++;
            return true;
        }

        private static bool Missing(SerializedObject so, string field, Counters c)
        {
            c.Problems.Add(so.targetObject.GetType().Name + "." + field
                + " is not a serialized field (renamed? the assembler reads the runtime's"
                + " field names)");
            return false;
        }

        private static T ReadObject<T>(UnityEngine.Object owner, string field)
            where T : UnityEngine.Object
        {
            var so = new SerializedObject(owner);
            SerializedProperty property = so.FindProperty(field);
            return property == null ? null : property.objectReferenceValue as T;
        }

        private static T Single<T>(Counters c) where T : UnityEngine.Object
        {
            T[] all = UnityEngine.Object.FindObjectsOfType<T>();
            if (all.Length != 1)
            {
                c.Problems.Add("expected exactly one " + typeof(T).Name + ", found " + all.Length);
                return null;
            }
            return all[0];
        }

        private static T FindOptional<T>() where T : UnityEngine.Object
        {
            T[] all = UnityEngine.Object.FindObjectsOfType<T>();
            return all.Length == 0 ? null : all[0];
        }

        private static string Finish(Counters c, string headline)
        {
            string text = "[hub-assemble] " + headline + ": +" + c.Created + " ~" + c.Changed
                + " problems=" + c.Problems.Count;
            for (int i = 0; i < c.Notes.Count; i++) text += "\n  note: " + c.Notes[i];
            for (int i = 0; i < c.Problems.Count; i++) text += "\n  PROBLEM: " + c.Problems[i];
            return text;
        }
    }
}
