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
        internal static readonly Vector3 HubCenterPosition = new Vector3(1200f, 0f, 0f);
        internal static readonly Vector3 HubSpawnPosition = new Vector3(1200f, 0.1f, -2.8f);

        // World-space text: metres per canvas unit, so 0.004 puts a 60-unit glyph at 24 cm.
        private const float PromptScale = 0.004f;
        internal static readonly Vector2 PromptSize = new Vector2(1200f, 120f);
        internal static readonly Vector3 PromptOffset = new Vector3(0f, 2.55f, 3.85f);

        // The result card's two buttons (canvas units): retry on the left, return on the right.
        public static readonly Vector2 CardButtonSize = new Vector2(300f, 80f);
        public const float CardRetryX = -170f;
        public const float CardReturnOffsetX = 340f;
        public const float CardButtonY = 60f;

        private const float PadThicknessM = 0.02f;
        private const float PadLabelScale = 0.004f;
        internal static readonly Vector2 PadLabelSize = new Vector2(640f, 120f);
        private const float PadLabelHeightM = 1.6f;

        private const float InteractProximity = 5f;

        internal sealed class Counters
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
            HubRunOutcome outcome = Run(c, null);
            if (!outcome.Completed) return Finish(c, outcome.Headline);

            CopyAllToUdon(c);
            VerifyTables(outcome.Launcher, outcome.Host, c);
            return Finish(c, outcome.Headline);
        }

        /// <summary>What a <see cref="Run"/> found and built, for the caller's own follow-up.</summary>
        internal sealed class HubRunOutcome
        {
            /// <summary>False when the run stopped early; <see cref="Headline"/> then says why.</summary>
            public bool Completed;
            public string Headline;
            public HubLevelLauncher Launcher;
            public HubRoomHost Host;
            public StoryRoundController Round;
            public List<HubLevelCatalog.Resolved> Offered;
        }

        /// <summary>
        /// One level's settings as the round controller and the result card store them
        /// (parallel arrays, one slot per level). Supplied by the all-maps assembler, which
        /// reads them from each level's source scene and placement document.
        /// </summary>
        internal sealed class HubLevelPlan
        {
            public string LevelId;
            public int GoalType;
            public float TargetDiameterM;
            public int TargetCount;
            public float TimeLimitSeconds;
            public bool KillEnabled;
            public float KillPlaneY;
            public string CountKind;
            public PickupFieldHost PickupHost;
            public bool AllowAbort;
            public bool HasStars;
        }

        /// <summary>
        /// Switches the run to a multi-level hub scene: the levels are the ones listed in
        /// <see cref="LevelOrder"/> (the pad order), their round/result tables come from
        /// <see cref="Plans"/>, and the caller finishes the Udon sync (it owns more proxies
        /// than this assembler does). Null options = the single-level behaviour.
        /// </summary>
        internal sealed class HubAssembleOptions
        {
            public string[] LevelOrder;
            public Dictionary<string, HubLevelPlan> Plans;
            /// <summary>Each level's ball start, world metres (return buttons stand beside it).</summary>
            public Dictionary<string, Vector3> StartCenters;
            /// <summary>The room geometry was imported by the caller before the levels moved.</summary>
            public bool SkipRoomGeometry;
        }

        /// <summary>
        /// The assembly proper, without the final proxy copy and table check, so a caller that
        /// adds more behaviours can do both once for everything.
        /// </summary>
        internal static HubRunOutcome Run(Counters c, HubAssembleOptions options)
        {
            var outcome = new HubRunOutcome();
            bool hub = options != null;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                outcome.Headline = "stopped: exit Play Mode first; the assembly writes scene data";
                return outcome;
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                outcome.Headline = "stopped: there is no loaded active scene";
                return outcome;
            }

            var catalogErrors = new List<string>();
            HubLevelCatalog.CatalogFile catalog;
            if (!HubLevelCatalog.TryLoad(out catalog, catalogErrors))
            {
                for (int i = 0; i < catalogErrors.Count; i++) c.Problems.Add(catalogErrors[i]);
                outcome.Headline = "stopped: the level list is unusable";
                return outcome;
            }

            HubLevelCatalog.Resolved[] resolved = HubLevelCatalog.ResolveAgainst(catalog, scene);
            List<HubLevelCatalog.Resolved> offered = hub
                ? OfferedInOrder(resolved, options.LevelOrder, c)
                : HubLevelCatalog.Offered(resolved);
            ReportResolution(scene, resolved, c);
            if (offered.Count == 0)
            {
                outcome.Headline = "stopped: scene '" + scene.name + "' offers no level; import a"
                    + " level listed for it in " + HubLevelCatalog.RepoRelativePath;
                return outcome;
            }
            outcome.Offered = offered;

            StoryRoundController round = Single<StoryRoundController>(c);
            if (round == null)
            {
                outcome.Headline = "stopped: the scene needs exactly one round controller";
                return outcome;
            }
            outcome.Round = round;
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

            if (!hub || !options.SkipRoomGeometry) AssembleRoomGeometry(c);
            Transform center = EnsureAnchor(HubCenterName, HubCenterPosition, Quaternion.identity, c);
            Transform spawn = EnsureAnchor(HubSpawnName, HubSpawnPosition, Quaternion.identity, c);
            AssembleSceneSpawn(spawn, c);

            Transform[] pads = AssemblePads(offered, catalog.room, center, font, c);
            Text prompt = AssemblePrompt(center, font, c);
            HubLevelLauncher launcher = AssembleLauncher(round, offered, spawn, seat, station, hub, c);
            HubRoomHost host = AssembleRoomHost(launcher, round, center, pads, catalog.room, prompt, c);
            if (hub)
            {
                AssembleReturnButtons(launcher, offered, options.StartCenters, c);
            }
            else
            {
                AssembleReturnButton(launcher, offered[0].EntryTransform, c);
            }
            AssembleCardReturnButton(round, launcher, font, c);
            if (hub)
            {
                AssembleRoundTable(round, offered, options.Plans, c);
            }
            else
            {
                AssembleRound(round, offered, c);
            }

            outcome.Launcher = launcher;
            outcome.Host = host;
            outcome.Completed = true;
            outcome.Headline = "scene '" + scene.name + "': " + offered.Count + " level(s) offered";
            return outcome;
        }

        // The offered levels in the order the hub lists them. A listed level the scene does not
        // offer is a PROBLEM: the hub's level order is the pad order, so a hole would renumber
        // every pad after it.
        private static List<HubLevelCatalog.Resolved> OfferedInOrder(
            HubLevelCatalog.Resolved[] resolved, string[] order, Counters c)
        {
            var list = new List<HubLevelCatalog.Resolved>();
            if (order == null) return list;
            for (int k = 0; k < order.Length; k++)
            {
                HubLevelCatalog.Resolved match = null;
                for (int i = 0; i < resolved.Length; i++)
                {
                    if (string.Equals(resolved[i].Entry.id, order[k])) match = resolved[i];
                }
                if (match == null || !match.Offered)
                {
                    c.Problems.Add("the hub lists level '" + order[k] + "' but the scene does not"
                        + " offer it (see the 'not offered' note)");
                    continue;
                }
                list.Add(match);
            }
            return list;
        }

        // ------------------------------------------------------------ level list ---

        internal static void ReportResolution(Scene scene, HubLevelCatalog.Resolved[] resolved,
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
        internal static void AssembleRoomGeometry(Counters c)
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

        internal static Transform EnsureAnchor(string name, Vector3 position, Quaternion rotation,
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

        internal static void AssembleSceneSpawn(Transform spawn, Counters c)
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

        internal static Transform[] AssemblePads(List<HubLevelCatalog.Resolved> offered,
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

        internal static void AssemblePadLabel(Transform pad, string levelName,
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
        internal static Vector3 WorldToLocalScale(Transform parent, Vector3 world)
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
        internal static float RoomWidth(Transform center)
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
        internal static Text AssemblePrompt(Transform center, Font font, Counters c)
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

        internal static HubLevelLauncher AssembleLauncher(StoryRoundController round,
            List<HubLevelCatalog.Resolved> offered, Transform spawn, BallRideStation seat,
            VRC.SDK3.Components.VRCStation station, bool hub, Counters c)
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
            if (hub)
            {
                // A multi-level hub switches the motor's world before it starts a level.
                LevelSwitchHost levelSwitch = FindOptional<LevelSwitchHost>();
                if (levelSwitch == null)
                {
                    c.Problems.Add("a multi-level hub needs a LevelSwitchHost in the scene (the"
                        + " launcher would start a level without switching to its world)");
                }
                else
                {
                    changed |= SetObject(so, "levelSwitch", levelSwitch, c);
                }
            }
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

        internal static HubRoomHost AssembleRoomHost(HubLevelLauncher launcher,
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
        internal static void AssembleReturnButton(HubLevelLauncher launcher, Transform entry,
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

        // A multi-level hub: one return button per level, standing beside that level's
        // entry marker (or, with no marker, beside its ball start), so a free roam level -
        // which has no result card - always has its way out. Named HubReturnButton_<id>; the
        // base scene's single root HubReturnButton (the first level's old button) is adopted
        // for the first level instead of being left behind next to nothing.
        internal static void AssembleReturnButtons(HubLevelLauncher launcher,
            List<HubLevelCatalog.Resolved> offered, Dictionary<string, Vector3> startCenters,
            Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            for (int k = 0; k < offered.Count; k++)
            {
                string id = offered[k].Entry.id;
                string name = ReturnButtonName + "_" + id;

                Vector3 anchor;
                Vector3 right = Vector3.right;
                Transform entry = offered[k].EntryTransform;
                Vector3 start;
                if (entry != null)
                {
                    anchor = entry.position;
                    right = entry.right;
                }
                else if (startCenters != null && startCenters.TryGetValue(id, out start))
                {
                    anchor = start;
                }
                else
                {
                    c.Problems.Add("level '" + id + "' has no entry marker and no start centre: its"
                        + " return button has nowhere to stand");
                    continue;
                }
                Vector3 position = anchor + right * 1.2f + Vector3.up * 1.0f;

                Transform existing = FindRoot(scene, name);
                if (existing == null && k == 0)
                {
                    Transform legacy = FindRoot(scene, ReturnButtonName);
                    if (legacy != null && legacy.GetComponent<HubReturnButton>() != null)
                    {
                        Undo.RecordObject(legacy.gameObject, "Rename the return button");
                        legacy.name = name;
                        existing = legacy;
                        c.Changed++;
                        c.Notes.Add("the base scene's '" + ReturnButtonName + "' is now the return"
                            + " button of '" + id + "'");
                    }
                }

                HubReturnButton button;
                if (existing == null)
                {
                    GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = name;
                    Undo.RegisterCreatedObjectUndo(cube, "Create " + name);
                    cube.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
                    cube.transform.position = position;
                    c.Created++;
                    button = AddUdon<HubReturnButton>(cube, c);
                    if (button == null) continue;
                    SetInteractText(button, "回大厅", c);
                }
                else
                {
                    button = existing.GetComponent<HubReturnButton>();
                    if (button == null)
                    {
                        c.Problems.Add("'" + name + "' exists without a HubReturnButton");
                        continue;
                    }
                    if (Vector3.Distance(existing.position, position) > 1e-3f)
                    {
                        Undo.RecordObject(existing, "Place " + name);
                        existing.position = position;
                        c.Changed++;
                        MarkDirty();
                    }
                }

                var so = new SerializedObject(button);
                if (SetObject(so, "launcher", launcher, c))
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    c.Proxies.Add(button);
                }
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
        internal static void AssembleCardReturnButton(StoryRoundController round,
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
        internal static bool LayoutReturnButton(RectTransform rect)
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
        internal static void SyncRetryCollider(GameObject retryRoot, Counters c)
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
        internal static void AssembleRound(StoryRoundController round,
            List<HubLevelCatalog.Resolved> offered, Counters c)
        {
            var so = new SerializedObject(round);
            bool changed = SetBool(so, "startOnLoad", false, c);

            if (offered.Count > 1)
            {
                c.Problems.Add(offered.Count + " levels are offered in one scene, but this menu"
                    + " configures one: only '" + offered[0].Entry.id + "' would be, and a start"
                    + " on the others would play it with that level's clock. Several levels in"
                    + " one scene are assembled by VRC Katamari/Hub/Assemble All-Maps Hub");
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

        /// <summary>A level's round settings as its placement document states them.</summary>
        internal sealed class PlacementRoundData
        {
            public bool HasTimeLimit;
            public float TimeLimitS;
            public bool HasTarget;
            public float TargetDiameterM;
            public bool HasKillPlane;
            public float KillPlaneY;
        }

        /// <summary>
        /// Reads a level's round settings from its placement document. False (with a PROBLEM
        /// or note on <paramref name="c"/>) when there is nothing to read.
        /// </summary>
        internal static bool TryReadPlacementRound(string levelId, Counters c,
            out PlacementRoundData data)
        {
            data = null;
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
            var result = new PlacementRoundData();
            result.HasTimeLimit = meta.HasTimeLimit && meta.TimeLimitS > 0f;
            result.TimeLimitS = meta.TimeLimitS;
            result.HasTarget = meta.Ball != null && meta.Ball.HasTargetDiameter
                && meta.Ball.TargetDiameterM > 0f;
            result.TargetDiameterM = meta.Ball != null ? meta.Ball.TargetDiameterM : 0f;
            PlacementV2Activity activity = read.Document.Activity;
            result.HasKillPlane = activity != null && activity.KillPlane != null;
            result.KillPlaneY = result.HasKillPlane ? activity.KillPlane.Y : 0f;

            c.Notes.Add("round settings of '" + levelId + "' read from its placement document:"
                + " clock " + (meta.HasTimeLimit ? meta.TimeLimitS + " s" : "unchanged")
                + ", target "
                + (meta.Ball != null && meta.Ball.HasTargetDiameter
                    ? meta.Ball.TargetDiameterM + " m" : "unchanged")
                + ", kill plane "
                + (activity != null && activity.KillPlane != null
                    ? "y=" + activity.KillPlane.Y : "unchanged"));
            data = result;
            return true;
        }

        internal static bool ApplyPlacementRound(SerializedObject so, string levelId, Counters c)
        {
            PlacementRoundData data;
            if (!TryReadPlacementRound(levelId, c, out data)) return false;

            bool changed = false;
            changed |= SetString(so, "levelId", levelId, c);
            if (data.HasTimeLimit)
            {
                changed |= SetFloat(so, "timeLimitSeconds", data.TimeLimitS, c);
            }
            if (data.HasTarget)
            {
                changed |= SetEnum(so, "goalType", (int)StoryGoalType.ReachTargetDiameter, c);
                changed |= SetFloat(so, "targetDiameterM", data.TargetDiameterM, c);
            }
            if (data.HasKillPlane)
            {
                changed |= SetBool(so, "killPlaneEnabled", true, c);
                changed |= SetFloat(so, "killPlaneY", data.KillPlaneY, c);
            }
            return changed;
        }

        // A multi-level hub: the round controller's per-level table and the result card's
        // per-level star table, one slot per offered level in pad order. The controller's own
        // single-level fields stay as the base scene has them (SelectLevel copies a slot over
        // them). The star thresholds are the presenter's own single-level values copied into
        // every slot (a level without stars ignores them), so the table is complete and the
        // only per-level decision is whether the card draws stars.
        internal static void AssembleRoundTable(StoryRoundController round,
            List<HubLevelCatalog.Resolved> offered, Dictionary<string, HubLevelPlan> plans,
            Counters c)
        {
            int n = offered.Count;
            var ids = new string[n];
            var goals = new int[n];
            var diameters = new float[n];
            var counts = new int[n];
            var limits = new float[n];
            var killEnabled = new bool[n];
            var killY = new float[n];
            var kinds = new string[n];
            var hosts = new UnityEngine.Object[n];
            var abort = new bool[n];
            var hasStars = new bool[n];
            for (int i = 0; i < n; i++)
            {
                string id = offered[i].Entry.id;
                HubLevelPlan plan;
                if (plans == null || !plans.TryGetValue(id, out plan))
                {
                    c.Problems.Add("no round plan for level '" + id + "'");
                    plan = new HubLevelPlan { LevelId = id, CountKind = "", AllowAbort = false };
                }
                ids[i] = id;
                goals[i] = plan.GoalType;
                diameters[i] = plan.TargetDiameterM;
                counts[i] = plan.TargetCount;
                limits[i] = plan.TimeLimitSeconds;
                killEnabled[i] = plan.KillEnabled;
                killY[i] = plan.KillPlaneY;
                kinds[i] = plan.CountKind ?? "";
                hosts[i] = plan.PickupHost;
                abort[i] = plan.AllowAbort;
                hasStars[i] = plan.HasStars;
            }

            var so = new SerializedObject(round);
            bool changed = SetBool(so, "startOnLoad", false, c);
            changed |= SetStringArray(so, "levelIds", ids, c);
            changed |= SetIntArray(so, "levelGoalTypes", goals, c);
            changed |= SetFloatArray(so, "levelTargetDiameters", diameters, c);
            changed |= SetIntArray(so, "levelTargetCounts", counts, c);
            changed |= SetFloatArray(so, "levelTimeLimits", limits, c);
            changed |= SetBoolArray(so, "levelKillEnabled", killEnabled, c);
            changed |= SetFloatArray(so, "levelKillY", killY, c);
            changed |= SetStringArray(so, "levelCountKinds", kinds, c);
            changed |= SetObjectArray(so, "levelPickupHosts", hosts, c);
            changed |= SetBoolArray(so, "levelAllowAbort", abort, c);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(round);
            }

            StoryResultPresenter presenter = FindOptional<StoryResultPresenter>();
            if (presenter == null)
            {
                c.Notes.Add("no StoryResultPresenter: the star table was not written");
                return;
            }
            var pso = new SerializedObject(presenter);
            SerializedProperty star2D = pso.FindProperty("star2DiameterM");
            SerializedProperty star3D = pso.FindProperty("star3DiameterM");
            SerializedProperty star2C = pso.FindProperty("star2Count");
            SerializedProperty star3C = pso.FindProperty("star3Count");
            SerializedProperty band = pso.FindProperty("partialBand");
            if (star2D == null || star3D == null || star2C == null || star3C == null || band == null)
            {
                c.Problems.Add("StoryResultPresenter lacks one of its single-level star fields");
                return;
            }
            var s2d = new float[n];
            var s3d = new float[n];
            var s2c = new int[n];
            var s3c = new int[n];
            var bands = new float[n];
            for (int i = 0; i < n; i++)
            {
                s2d[i] = star2D.floatValue;
                s3d[i] = star3D.floatValue;
                s2c[i] = star2C.intValue;
                s3c[i] = star3C.intValue;
                bands[i] = band.floatValue;
            }
            bool pchanged = SetBoolArray(pso, "levelHasStars", hasStars, c);
            pchanged |= SetFloatArray(pso, "levelStar2Diameters", s2d, c);
            pchanged |= SetFloatArray(pso, "levelStar3Diameters", s3d, c);
            pchanged |= SetIntArray(pso, "levelStar2Counts", s2c, c);
            pchanged |= SetIntArray(pso, "levelStar3Counts", s3c, c);
            pchanged |= SetFloatArray(pso, "levelPartialBands", bands, c);
            if (pchanged)
            {
                pso.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(presenter);
            }
        }

        // ------------------------------------------------------------ udon plumbing ---

        internal static T AddUdon<T>(GameObject go, Counters c) where T : UdonSharpBehaviour
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
        internal static void SetInteractText(UdonSharpBehaviour proxy, string text, Counters c)
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

        internal static void CopyAllToUdon(Counters c)
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
        internal static void VerifyTables(HubLevelLauncher launcher, HubRoomHost host, Counters c)
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

        // Reads the instance variable table back (UdonVariableAccess): a missing symbol means
        // "save, reopen, run again". A proxy with no backing behaviour is a problem too, not a skip.
        internal static void VerifyTable(UdonSharpBehaviour proxy, string[] names, Counters c)
        {
            List<string> missing = UdonVariableAccess.MissingNames(proxy, names);
            if (missing == null)
            {
                c.Problems.Add(proxy.GetType().Name + " on '" + proxy.name + "' has no backing"
                    + " UdonBehaviour, so its Udon variables could not be checked");
                return;
            }
            if (missing.Count > 0)
            {
                c.Problems.Add(proxy.GetType().Name + " on '" + proxy.name + "' lacks Udon"
                    + " variables: " + string.Join(", ", missing.ToArray())
                    + ". Save the scene, reopen it and run the assembly again");
            }
        }

        // ------------------------------------------------------------ ui plumbing ---

        internal static GameObject CreateWorldCanvas(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            if (parent != null) go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            return go;
        }

        internal static Text CreateText(string name, Transform parent, Font font, int size,
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

        internal static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // ------------------------------------------------------------ scene plumbing ---

        internal static Transform FindRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, name)) return roots[i].transform;
            }
            return null;
        }

        internal static Transform EnsureChild(Transform parent, string name, Counters c)
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

        internal static GameObject FindOrCreateRoot(string name, Counters c)
        {
            return EnsureChild(null, name, c).gameObject;
        }

        internal static void MarkDirty()
        {
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        internal static string Describe(Transform t)
        {
            if (t == null) return "none";
            Vector3 p = t.position;
            return t.name + " (" + p.x.ToString("0.##") + ", " + p.y.ToString("0.##") + ", "
                + p.z.ToString("0.##") + ")";
        }

        // ------------------------------------------------------------ serialized access ---

        internal static bool SetFloat(SerializedObject so, string field, float value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (Mathf.Approximately(property.floatValue, value)) return false;
            property.floatValue = value;
            c.Changed++;
            return true;
        }

        internal static bool SetBool(SerializedObject so, string field, bool value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (property.boolValue == value) return false;
            property.boolValue = value;
            c.Changed++;
            return true;
        }

        internal static bool SetEnum(SerializedObject so, string field, int value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (property.intValue == value) return false;
            property.intValue = value;
            c.Changed++;
            return true;
        }

        internal static bool SetString(SerializedObject so, string field, string value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (string.Equals(property.stringValue, value)) return false;
            property.stringValue = value;
            c.Changed++;
            return true;
        }

        internal static bool SetObject(SerializedObject so, string field, UnityEngine.Object value,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            if (property.objectReferenceValue == value) return false;
            property.objectReferenceValue = value;
            c.Changed++;
            return true;
        }

        internal static bool SetObjectArray(SerializedObject so, string field,
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

        internal static bool SetStringArray(SerializedObject so, string field, string[] values,
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

        internal static bool SetIntArray(SerializedObject so, string field, int[] values, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = property.GetArrayElementAtIndex(i).intValue == values[i];
            }
            if (same) return false;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).intValue = values[i];
            }
            c.Changed++;
            return true;
        }

        internal static bool SetLongArray(SerializedObject so, string field, long[] values,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = property.GetArrayElementAtIndex(i).longValue == values[i];
            }
            if (same) return false;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).longValue = values[i];
            }
            c.Changed++;
            return true;
        }

        internal static bool SetFloatArray(SerializedObject so, string field, float[] values,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = Mathf.Approximately(property.GetArrayElementAtIndex(i).floatValue, values[i]);
            }
            if (same) return false;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).floatValue = values[i];
            }
            c.Changed++;
            return true;
        }

        internal static bool SetBoolArray(SerializedObject so, string field, bool[] values,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = property.GetArrayElementAtIndex(i).boolValue == values[i];
            }
            if (same) return false;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).boolValue = values[i];
            }
            c.Changed++;
            return true;
        }

        internal static bool SetVector3Array(SerializedObject so, string field, Vector3[] values,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null) return Missing(so, field, c);
            bool same = property.arraySize == values.Length;
            for (int i = 0; same && i < values.Length; i++)
            {
                same = property.GetArrayElementAtIndex(i).vector3Value == values[i];
            }
            if (same) return false;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).vector3Value = values[i];
            }
            c.Changed++;
            return true;
        }

        internal static bool Missing(SerializedObject so, string field, Counters c)
        {
            c.Problems.Add(so.targetObject.GetType().Name + "." + field
                + " is not a serialized field (renamed? the assembler reads the runtime's"
                + " field names)");
            return false;
        }

        internal static T ReadObject<T>(UnityEngine.Object owner, string field)
            where T : UnityEngine.Object
        {
            var so = new SerializedObject(owner);
            SerializedProperty property = so.FindProperty(field);
            return property == null ? null : property.objectReferenceValue as T;
        }

        internal static T Single<T>(Counters c) where T : UnityEngine.Object
        {
            T[] all = UnityEngine.Object.FindObjectsOfType<T>();
            if (all.Length != 1)
            {
                c.Problems.Add("expected exactly one " + typeof(T).Name + ", found " + all.Length);
                return null;
            }
            return all[0];
        }

        internal static T FindOptional<T>() where T : UnityEngine.Object
        {
            T[] all = UnityEngine.Object.FindObjectsOfType<T>();
            return all.Length == 0 ? null : all[0];
        }

        internal static string Finish(Counters c, string headline)
        {
            string text = "[hub-assemble] " + headline + ": +" + c.Created + " ~" + c.Changed
                + " problems=" + c.Problems.Count;
            for (int i = 0; i < c.Notes.Count; i++) text += "\n  note: " + c.Notes[i];
            for (int i = 0; i < c.Problems.Count; i++) text += "\n  PROBLEM: " + c.Problems[i];
            return text;
        }
    }
}
