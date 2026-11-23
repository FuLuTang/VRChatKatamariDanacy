using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon;
using VRCKatamari.BallMotor;
using VRCKatamari.Editor.LevelBuild;
using VRCKatamari.Hub;
using VRCKatamari.Input;
using VRCKatamari.Pickup;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The hub room's look and its three wall panels (levels, how to play + controls,
    /// settings): the Blender-built room model, the three panel prefabs, the settings store,
    /// the record book and the panel display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model. <c>Art/Hub/HubRoomArt.fbx</c> (built by <c>Tools/hubart/build_hub_room.py</c>
    /// from <c>Tools/hubart/hub_layout.json</c>) is the room: floor, walls, decor and each
    /// board's coloured frame, legs and bolts. Instantiated once under <c>HubRoomArt</c> at
    /// <c>HubCenter</c>, colliders stripped (walls and floor collide through the placement
    /// import); materials remapped to <c>Art/Hub/Materials/Hub_&lt;colour&gt;</c>. The three
    /// <c>Anchor_Board_*</c> empties are compared with the layout (2 cm).
    /// </para>
    /// <para>
    /// Panels. Each board is a prefab, <c>Prefabs/Hub/HubBoard_&lt;id&gt;.prefab</c>, built by
    /// the Unity operator from Docs/Design/StoryMode/HUB_PANEL_PREFAB.md: a VRChat interactable
    /// world-space canvas (World Space, VRCUiShape + BoxCollider + GraphicRaycaster on the canvas
    /// object, Default layer, scale 0.001), TextMeshPro labels with Raycast Target off, and uGUI
    /// Buttons whose persistent onClick is <c>UdonBehaviour.SendCustomEvent("Press")</c> on the
    /// button's own HubStartButton / HubSettingButton. Everything inside a prefab is the
    /// prefab's. This file only places one instance per board at the board's canvas centre,
    /// writes the references a prefab cannot hold (launcher, settings store, level index, the
    /// display's text table) and reads the prefab contract back; a missing or broken prefab is
    /// a PROBLEM pointing at the spec, never rebuilt here.
    /// </para>
    /// </remarks>
    public static partial class HubAssembler
    {
        public const string RoomArtName = "HubRoomArt";
        public const string BoardPrefix = "HubBoard_";
        public const string PanelPrefabFolder = "Assets/VRC_Katamari/Prefabs/Hub";
        public const string SettingsHostName = "HubSettings";
        public const string RecordBookName = "HubRecords";
        public const string PanelDisplayName = "HubPanelDisplay";
        public const string RoomLightsName = "HubRoomLights";
        /// <summary>The event every panel Button sends (SendCustomEvent argument).</summary>
        public const string PressEvent = "Press";
        internal const string LegacyPanelName = "HubLevelPanel";
        internal const string ArtMaterialFolder = "Assets/VRC_Katamari/Art/Hub/Materials";
        internal const float AnchorToleranceM = 0.02f;
        internal static readonly string[] BoardIds = { "Levels", "Help", "Settings" };

        // ---- the prefab naming contract (HUB_PANEL_PREFAB.md); paths are under the board root.
        internal const string LevelCardPrefix = "LevelCard_";
        internal const string SettingRowPrefix = "SettingRow_";

        internal static string LevelCardPath(int index) { return LevelCardPrefix + index; }

        internal static string SettingRowPath(int id) { return SettingRowPrefix + id.ToString("00"); }

        internal static string PanelPrefabPath(string boardId)
        {
            return PanelPrefabFolder + "/" + BoardPrefix + boardId + ".prefab";
        }

        internal sealed class RoomParts
        {
            public HubRoomLayout Layout;
            public readonly Dictionary<string, Transform> Boards = new Dictionary<string, Transform>();
            public Transform[] LevelButtons = new Transform[0];
            public TextMeshProUGUI Prompt;
            public HubSettingsHost Settings;
            public HubRecordBook Records;
            public HubPanelDisplay Display;
        }

        // ------------------------------------------------------------ entry ---

        internal static RoomParts AssembleRoom(List<HubLevelCatalog.Resolved> offered,
            Dictionary<string, HubLevelPlan> plans, StoryRoundController round, Transform center,
            HubLevelLauncher launcher, Counters c)
        {
            var parts = new RoomParts();
            var errors = new List<string>();
            HubRoomLayout layout;
            if (!HubRoomLayout.TryLoad(out layout, errors))
            {
                for (int i = 0; i < errors.Count; i++) c.Problems.Add(errors[i]);
                return parts;
            }
            parts.Layout = layout;

            RemoveLegacyPanel(c);
            Dictionary<string, Material> materials = EnsureArtMaterials(layout, c);
            Transform art = AssembleRoomArt(layout, center, materials, c);
            RemoveStaleArtMaterials(layout, art, c);
            AssembleRoomLights(layout, center, c);
            EnsureEventSystem(c);

            foreach (string id in BoardIds)
            {
                HubRoomLayout.Board board;
                if (!layout.Boards.TryGetValue(id, out board)) continue;
                Transform instance = PlacePanelPrefab(board, layout, center, c);
                if (instance != null) parts.Boards[id] = instance;
            }

            parts.Settings = AssembleSettingsHost(c);
            parts.Records = AssembleRecordBook(round, offered, plans, c);
            parts.Display = AssemblePanelDisplay(c);

            Transform levels = Board(parts, "Levels");
            Transform help = Board(parts, "Help");
            Transform settingsBoard = Board(parts, "Settings");
            parts.Prompt = FindText(levels, "Text_Prompt", c);

            // Level cards: card i is launcher level i; cards beyond the offered levels are hidden.
            int n = offered.Count;
            parts.LevelButtons = new Transform[n];
            var nameTexts = new TextMeshProUGUI[n];
            var goalTexts = new TextMeshProUGUI[n];
            var recordTexts = new TextMeshProUGUI[n];
            var startTexts = new TextMeshProUGUI[n];
            int cardsInPrefab = 0;
            if (levels != null)
            {
                for (int i = 0; levels.Find(LevelCardPath(i)) != null; i++) cardsInPrefab++;
                if (cardsInPrefab < n)
                {
                    c.Problems.Add(n + " levels are offered but " + PanelPrefabPath("Levels") + " has "
                        + cardsInPrefab + " level cards (" + LevelCardPrefix + "0..); add cards per"
                        + " HUB_PANEL_PREFAB.md");
                }
                for (int i = 0; i < cardsInPrefab; i++)
                {
                    Transform card = levels.Find(LevelCardPath(i));
                    bool used = i < n;
                    if (card.gameObject.activeSelf != used)
                    {
                        Undo.RecordObject(card.gameObject, "Show level card");
                        card.gameObject.SetActive(used);
                        c.Changed++;
                    }
                    if (!used) continue;
                    HubStartButton start = card.GetComponent<HubStartButton>();
                    if (start == null)
                    {
                        c.Problems.Add(card.name + " has no HubStartButton (HUB_PANEL_PREFAB.md)");
                        continue;
                    }
                    var so = new SerializedObject(start);
                    bool changed = SetObject(so, "launcher", launcher, c);
                    SerializedProperty index = so.FindProperty("levelIndex");
                    if (index.intValue != i) { index.intValue = i; changed = true; c.Changed++; }
                    if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                    c.Proxies.Add(start);
                    parts.LevelButtons[i] = card;
                    nameTexts[i] = FindText(card, "Name", c);
                    goalTexts[i] = FindText(card, "Goal", c);
                    recordTexts[i] = FindText(card, "Record", c);
                    startTexts[i] = FindText(card, "Start", c);
                    TextMeshProUGUI number = FindText(card, "Number", c);
                    if (number != null && number.text != (i + 1).ToString())
                    {
                        Undo.RecordObject(number, "Number the level card");
                        number.text = (i + 1).ToString();
                        c.Changed++;
                    }
                    bool versus = offered[i].Entry != null && offered[i].Entry.IsVersus;
                    ApplyCardLook(card, versus, c);
                    ApplyCardPreview(card, layout.CardPreviewColor(offered[i].Entry != null ? offered[i].Entry.id : null,
                        versus), c);
                }
            }

            // Settings rows: the arrows' id/delta are the prefab's; the store is the scene's.
            var labelTexts = new TextMeshProUGUI[HubSettingsMath.Count];
            var valueTexts = new TextMeshProUGUI[HubSettingsMath.Count];
            if (settingsBoard != null)
            {
                for (int id = 0; id < HubSettingsMath.Count; id++)
                {
                    Transform row = settingsBoard.Find(SettingRowPath(id));
                    if (row == null)
                    {
                        c.Problems.Add(PanelPrefabPath("Settings") + " has no " + SettingRowPath(id)
                            + " (HUB_PANEL_PREFAB.md)");
                        continue;
                    }
                    labelTexts[id] = FindText(row, "Label", c);
                    valueTexts[id] = FindText(row, "Value", c);
                    WireSettingArrow(row, "Less", parts.Settings, c);
                    WireSettingArrow(row, "More", parts.Settings, c);
                }
                CheckPanelSprites(settingsBoard, c);
            }
            CheckPanelSprites(levels, c);

            // Display wiring.
            if (parts.Display != null)
            {
                var names = new string[n];
                var goals = new int[n];
                var diameters = new float[n];
                var counts = new int[n];
                var kinds = new string[n];
                var limits = new float[n];
                for (int i = 0; i < n; i++)
                {
                    names[i] = offered[i].Entry.name;
                    HubLevelPlan plan;
                    if (plans != null && plans.TryGetValue(offered[i].Entry.id, out plan))
                    {
                        goals[i] = plan.GoalType;
                        diameters[i] = plan.TargetDiameterM;
                        counts[i] = plan.TargetCount;
                        kinds[i] = plan.CountKind ?? "";
                        limits[i] = plan.TimeLimitSeconds;
                    }
                    else
                    {
                        ReadSingleLevelGoal(round, out goals[i], out diameters[i], out counts[i],
                            out limits[i]);
                        kinds[i] = round.GetCountGoalKind() ?? "";
                    }
                }
                var so = new SerializedObject(parts.Display);
                SetObject(so, "settings", parts.Settings, c);
                SetObject(so, "records", parts.Records, c);
                SetStringArray(so, "levelNames", names, c);
                SetIntArray(so, "levelGoalTypes", goals, c);
                SetFloatArray(so, "levelTargetDiameters", diameters, c);
                SetIntArray(so, "levelTargetCounts", counts, c);
                SetStringArray(so, "levelCountKinds", kinds, c);
                SetFloatArray(so, "levelTimeLimits", limits, c);
                SetObjectArray(so, "levelNameTexts", nameTexts, c);
                SetObjectArray(so, "levelGoalTexts", goalTexts, c);
                SetObjectArray(so, "levelRecordTexts", recordTexts, c);
                SetObjectArray(so, "levelStartTexts", startTexts, c);
                SetObject(so, "levelsTitle", FindText(levels, "Text_LevelsTitle", c), c);
                SetObject(so, "creditsText", FindText(levels, "Text_Credits", c), c);
                SetObject(so, "helpTitle", FindText(help, "Text_HelpTitle", c), c);
                SetObject(so, "howToText", FindText(help, "Text_HowTo", c), c);
                SetObject(so, "controlsTitle", FindText(help, "Text_ControlsTitle", c), c);
                SetObject(so, "controlsVrText", FindText(help, "Text_ControlsVr", c), c);
                SetObject(so, "controlsDesktopText", FindText(help, "Text_ControlsDesktop", c), c);
                SetObject(so, "settingsTitle", FindText(settingsBoard, "Text_SettingsTitle", c), c);
                SetObject(so, "settingsHintText", FindText(settingsBoard, "Text_SettingsHint", c), c);
                SetObjectArray(so, "settingLabelTexts", labelTexts, c);
                SetObjectArray(so, "settingValueTexts", valueTexts, c);
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(parts.Display);
                // Editor preview of the words the runtime will write (Chinese, defaults).
                try
                {
                    parts.Display.Redraw();
                }
                catch (Exception exception)
                {
                    c.Notes.Add("editor preview of the panel words failed (" + exception.Message
                        + "); the runtime writes them at Start");
                }
            }

            WireStoreListeners(parts, c);
            MarkDirty();
            return parts;
        }

        // ---- level card look (HUB_PANEL_PREFAB.md「方向一外观」): a story card and a versus card are
        // the same prefab card; the versus one shows VersusBadge, the striped edge strip and the
        // brick start pill, the story one its number disc, the sage strip and the tomato pill.
        internal const string CardStoryStripName = "StoryStrip";
        internal const string CardVersusStripName = "VersusStrip";
        internal const string CardVersusBadgeName = "VersusBadge";
        internal const string CardNumberDiscName = "NumberDisc";
        internal const string CardNumberName = "Number";
        internal const string CardStartPillName = "StartPill";
        internal const string CardVersusPillName = "VersusPill";
        internal static readonly string[] CardLookChildren =
        {
            CardStoryStripName, CardVersusStripName, CardVersusBadgeName, CardNumberDiscName,
            CardNumberName, CardStartPillName, CardVersusPillName,
        };

        /// <summary>
        /// Which of a card's look children are on: a versus card shows its badge, striped strip
        /// and brick pill; a story card its sage strip, number disc and number, and tomato pill.
        /// Pure, for tests.
        /// </summary>
        internal static bool CardChildActive(string childName, bool versus)
        {
            switch (childName)
            {
                case CardVersusStripName:
                case CardVersusBadgeName:
                case CardVersusPillName:
                    return versus;
                case CardStoryStripName:
                case CardNumberDiscName:
                case CardNumberName:
                case CardStartPillName:
                    return !versus;
                default:
                    return true;
            }
        }

        // Instance overrides like the card's other scene data; a card prefab without the look
        // children (built before direction 1) is a PROBLEM pointing at the spec, not rebuilt.
        private static void ApplyCardLook(Transform card, bool versus, Counters c)
        {
            foreach (string name in CardLookChildren)
            {
                Transform child = card.Find(name);
                if (child == null)
                {
                    c.Problems.Add(card.name + "/" + name + " is missing from " + PanelPrefabPath("Levels")
                        + " (HUB_PANEL_PREFAB.md, level card look)");
                    continue;
                }
                bool on = CardChildActive(name, versus);
                if (child.gameObject.activeSelf != on)
                {
                    Undo.RecordObject(child.gameObject, "Level card look");
                    child.gameObject.SetActive(on);
                    c.Changed++;
                }
            }
        }

        /// <summary>
        /// Prefix of every direction-1 panel sprite (<c>Art/Hub/Sprites/Hub*.png</c>). A panel
        /// Image or Button sprite state that still uses Unity's built-in <c>UISprite</c> or no
        /// sprite is a leftover of the old look.
        /// </summary>
        internal const string PanelSpritePrefix = "Hub";

        /// <summary>True when a sprite name is one of ours (direction 1), not the built-in UISprite or none.</summary>
        internal static bool IsPanelSprite(string spriteName)
        {
            return !string.IsNullOrEmpty(spriteName)
                && spriteName.StartsWith(PanelSpritePrefix, StringComparison.Ordinal);
        }

        // Read-back only (the prefab is the operator's): every Image on the board and every
        // Button's sprite states must use the direction-1 sprites. One PROBLEM per board, naming
        // the count and the first offender, so an old look cannot pass assembly unnoticed.
        private static void CheckPanelSprites(Transform board, Counters c)
        {
            if (board == null) return;
            int bad = 0;
            string first = null;
            foreach (Image image in board.GetComponentsInChildren<Image>(true))
            {
                string name = image.sprite != null ? image.sprite.name : "";
                if (IsPanelSprite(name)) continue;
                bad++;
                if (first == null) first = PanelPath(board, image.transform) + " (" + (name.Length > 0 ? name : "no sprite") + ")";
            }
            foreach (Button button in board.GetComponentsInChildren<Button>(true))
            {
                if (button.transition != Selectable.Transition.SpriteSwap)
                {
                    bad++;
                    if (first == null) first = PanelPath(board, button.transform) + " (transition " + button.transition + ")";
                    continue;
                }
                SpriteState st = button.spriteState;
                foreach (Sprite sprite in new[] { st.highlightedSprite, st.pressedSprite, st.disabledSprite })
                {
                    if (IsPanelSprite(sprite != null ? sprite.name : "")) continue;
                    bad++;
                    if (first == null) first = PanelPath(board, button.transform) + " (sprite state)";
                    break;
                }
            }
            if (bad > 0)
            {
                c.Problems.Add(board.name + ": " + bad + " Image/Button(s) still have the old look (first: " + first
                    + "); restyle per HUB_PANEL_PREFAB.md「方向一外观」");
            }
        }

        private static string PanelPath(Transform root, Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // Placeholder thumbnail: the card's Preview Image takes the level's colour from
        // hub_layout.json cardPreviews (an instance override, like the card's number) until the
        // level has a screenshot sprite.
        private static void ApplyCardPreview(Transform card, Color color, Counters c)
        {
            Transform t = card.Find("Preview");
            Image preview = t == null ? null : t.GetComponent<Image>();
            if (preview == null)
            {
                c.Problems.Add(card.name + "/Preview is not an Image in " + PanelPrefabPath("Levels")
                    + " (HUB_PANEL_PREFAB.md, level card)");
                return;
            }
            if (preview.color == color) return;
            Undo.RecordObject(preview, "Level card preview colour");
            preview.color = color;
            c.Changed++;
        }

        private static Transform Board(RoomParts parts, string id)
        {
            Transform t;
            return parts.Boards.TryGetValue(id, out t) ? t : null;
        }

        private static TextMeshProUGUI FindText(Transform parent, string path, Counters c)
        {
            if (parent == null) return null;
            Transform t = parent.Find(path);
            TextMeshProUGUI text = t == null ? null : t.GetComponent<TextMeshProUGUI>();
            if (text == null)
            {
                c.Problems.Add(parent.name + "/" + path + " is not a TextMeshProUGUI in the panel"
                    + " prefab (HUB_PANEL_PREFAB.md)");
            }
            return text;
        }

        private static void ReadSingleLevelGoal(StoryRoundController round, out int goal,
            out float diameter, out int count, out float limit)
        {
            var so = new SerializedObject(round);
            SerializedProperty g = so.FindProperty("goalType");
            goal = g != null ? g.intValue : 0;
            SerializedProperty d = so.FindProperty("targetDiameterM");
            diameter = d != null ? d.floatValue : 0f;
            SerializedProperty k = so.FindProperty("targetCount");
            count = k != null ? k.intValue : 0;
            SerializedProperty t = so.FindProperty("timeLimitSeconds");
            limit = t != null ? t.floatValue : 0f;
        }

        // ------------------------------------------------------------ panels ---

        // One instance of the board's prefab, a scene root named HubBoard_<id>, at the board's
        // canvas centre facing the reader, scale = metres per canvas unit. A same-named root
        // that is not an instance of that prefab (an older build of the board) is replaced.
        internal static Transform PlacePanelPrefab(HubRoomLayout.Board board, HubRoomLayout layout,
            Transform center, Counters c)
        {
            string name = BoardPrefix + board.Id;
            string path = PanelPrefabPath(board.Id);
            Scene scene = SceneManager.GetActiveScene();
            Transform existing = FindRoot(scene, name);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                c.Problems.Add("no panel prefab at " + path + "; build it from"
                    + " Docs/Design/StoryMode/HUB_PANEL_PREFAB.md, then assemble again");
                return null;
            }
            if (existing != null
                && PrefabUtility.GetCorrespondingObjectFromSource(existing.gameObject) != prefab)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
                existing = null;
                c.Changed++;
                c.Notes.Add("replaced '" + name + "' with an instance of " + path);
            }
            if (existing == null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                go.name = name;
                Undo.RegisterCreatedObjectUndo(go, "Place " + name);
                existing = go.transform;
                c.Created++;
            }
            Vector3 position = center.position + layout.CanvasCenter(board);
            Vector3 scale = Vector3.one * layout.CanvasMetresPerUnit;
            if (Vector3.Distance(existing.position, position) > 1e-4f
                || Quaternion.Angle(existing.rotation, board.Rotation) > 0.01f
                || existing.localScale != scale)
            {
                Undo.RecordObject(existing, "Place " + name);
                existing.position = position;
                existing.rotation = board.Rotation;
                existing.localScale = scale;
                c.Changed++;
            }
            return existing;
        }

        private static void WireSettingArrow(Transform row, string side, HubSettingsHost settings,
            Counters c)
        {
            Transform t = row.Find(side);
            HubSettingButton arrow = t == null ? null : t.GetComponent<HubSettingButton>();
            if (arrow == null)
            {
                c.Problems.Add(row.name + "/" + side + " has no HubSettingButton (HUB_PANEL_PREFAB.md)");
                return;
            }
            var so = new SerializedObject(arrow);
            if (SetObject(so, "settings", settings, c)) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(arrow);
        }

        // ------------------------------------------------------------ art ---

        private static void RemoveLegacyPanel(Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            foreach (string name in new[] { LegacyPadsRootName, LegacyPanelName, PromptBoardName })
            {
                Transform old = FindRoot(scene, name);
                if (old == null) continue;
                Undo.DestroyObjectImmediate(old.gameObject);
                c.Changed++;
                c.Notes.Add("removed the old '" + name + "' (the wall boards replace it)");
            }
        }

        internal static Dictionary<string, Material> EnsureArtMaterials(HubRoomLayout layout,
            Counters c)
        {
            var result = new Dictionary<string, Material>();
            if (!AssetDatabase.IsValidFolder(ArtMaterialFolder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/VRC_Katamari/Art/Hub"))
                    AssetDatabase.CreateFolder("Assets/VRC_Katamari/Art", "Hub");
                AssetDatabase.CreateFolder("Assets/VRC_Katamari/Art/Hub", "Materials");
            }
            Shader shader = Shader.Find(layout.ShaderName);
            if (shader == null)
            {
                c.Problems.Add("hub layout shader '" + layout.ShaderName + "' is not in the project");
                shader = Shader.Find("Standard");
            }
            foreach (HubRoomLayout.Surface surface in layout.Surfaces.Values)
            {
                string path = ArtMaterialFolder + "/" + surface.Name + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(shader) { name = surface.Name };
                    AssetDatabase.CreateAsset(m, path);
                    c.Created++;
                }
                Texture2D texture = null;
                HubRoomLayout.TextureSpec spec;
                if (!string.IsNullOrEmpty(surface.Texture) && layout.Textures.TryGetValue(surface.Texture, out spec))
                {
                    texture = AssetDatabase.LoadAssetAtPath<Texture2D>(spec.File);
                    if (texture == null)
                    {
                        c.Problems.Add("hub surface '" + surface.Name + "' texture " + spec.File
                            + " is not imported (sync the repository and let Unity import it)");
                    }
                }
                if (ApplySurface(m, shader, surface, texture))
                {
                    EditorUtility.SetDirty(m);
                    c.Changed++;
                }
                result[surface.Name] = m;
            }

            string glowPath = ArtMaterialFolder + "/Hub_LampGlow.mat";
            var glow = AssetDatabase.LoadAssetAtPath<Material>(glowPath);
            if (glow == null)
            {
                glow = new Material(shader) { name = "Hub_LampGlow" };
                glow.color = new Color(1f, 0.95f, 0.8f);
                glow.EnableKeyword("_EMISSION");
                glow.SetColor("_EmissionColor", new Color(1f, 0.92f, 0.7f) * 1.5f);
                AssetDatabase.CreateAsset(glow, glowPath);
                c.Created++;
            }
            result["Hub_LampGlow"] = glow;
            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>
        /// Writes one surface onto its material (shader, colour, smoothness, metal 0, texture
        /// and metre tiling); true when anything changed. A surface without a texture clears it.
        /// </summary>
        internal static bool ApplySurface(Material m, Shader shader, HubRoomLayout.Surface surface,
            Texture2D texture)
        {
            bool changed = false;
            if (m.shader != shader) { m.shader = shader; changed = true; }
            if (m.color != surface.Color) { m.color = surface.Color; changed = true; }
            if (m.HasProperty("_Glossiness") && !Mathf.Approximately(m.GetFloat("_Glossiness"), surface.Smoothness))
            {
                m.SetFloat("_Glossiness", surface.Smoothness);
                changed = true;
            }
            if (m.HasProperty("_Metallic") && m.GetFloat("_Metallic") != 0f)
            {
                m.SetFloat("_Metallic", 0f);
                changed = true;
            }
            if (m.mainTexture != texture) { m.mainTexture = texture; changed = true; }
            Vector2 tiling = texture != null ? surface.Tiling : Vector2.one;
            if (m.mainTextureScale != tiling) { m.mainTextureScale = tiling; changed = true; }
            if (m.mainTextureOffset != Vector2.zero) { m.mainTextureOffset = Vector2.zero; changed = true; }
            return changed;
        }

        /// <summary>
        /// Deletes the <c>Hub_*.mat</c> files the layout no longer names (an older palette), but
        /// only once the room model uses nothing else: while the model still carries an old
        /// material name, its material is kept so the room keeps drawing until the FBX is rebuilt.
        /// </summary>
        internal static void RemoveStaleArtMaterials(HubRoomLayout layout, Transform art, Counters c)
        {
            if (art == null || !AssetDatabase.IsValidFolder(ArtMaterialFolder)) return;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (MeshRenderer r in art.GetComponentsInChildren<MeshRenderer>(true))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null) used.Add(m.name.Replace(" (Instance)", ""));
                }
            }
            var unknown = new List<string>();
            foreach (string name in used)
            {
                if (!layout.Surfaces.ContainsKey(name) && name != "Hub_LampGlow") unknown.Add(name);
            }
            if (unknown.Count > 0)
            {
                unknown.Sort(StringComparer.Ordinal);
                c.Problems.Add("the hub art model uses material(s) that are not surfaces of "
                    + HubRoomLayout.RepoRelativePath + ": " + string.Join(", ", unknown.ToArray())
                    + " (rebuild the model with Tools/hubart/build_hub_room.py)");
                return;
            }
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { ArtMaterialFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                if (layout.Surfaces.ContainsKey(name) || name == "Hub_LampGlow") continue;
                if (!name.StartsWith("Hub_", StringComparison.Ordinal)) continue;
                AssetDatabase.DeleteAsset(path);
                c.Changed++;
                c.Notes.Add("deleted the old hub material " + path + " (not a surface any more)");
            }
        }

        internal static Transform AssembleRoomArt(HubRoomLayout layout, Transform center,
            Dictionary<string, Material> materials, Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform existing = FindRoot(scene, RoomArtName);
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(layout.ModelPath);
            if (model == null)
            {
                c.Notes.Add("the hub art model " + layout.ModelPath + " is not imported yet; the"
                    + " panels still work but the room has no walls' art or board frames (run"
                    + " Tools/hubart/build_hub_room.py, let Unity import it, then assemble again)");
                if (existing != null)
                {
                    Undo.DestroyObjectImmediate(existing.gameObject);
                    c.Changed++;
                }
                return null;
            }

            if (existing != null && PrefabUtility.GetCorrespondingObjectFromSource(existing.gameObject) != model)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
                existing = null;
                c.Changed++;
            }
            if (existing == null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                go.name = RoomArtName;
                Undo.RegisterCreatedObjectUndo(go, "Create hub art");
                existing = go.transform;
                c.Created++;
            }
            existing.position = center.position;
            existing.rotation = Quaternion.identity;
            existing.localScale = Vector3.one;

            int remapped = 0;
            foreach (Collider col in existing.GetComponentsInChildren<Collider>(true))
            {
                Undo.DestroyObjectImmediate(col);
                c.Changed++;
            }
            foreach (MeshRenderer r in existing.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] shared = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    string key = shared[i] == null ? "" : shared[i].name.Replace(" (Instance)", "");
                    Material ours;
                    if (materials.TryGetValue(key, out ours) && shared[i] != ours)
                    {
                        shared[i] = ours;
                        changed = true;
                    }
                }
                if (changed)
                {
                    Undo.RecordObject(r, "Hub art materials");
                    r.sharedMaterials = shared;
                    remapped++;
                }
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
            }
            if (remapped > 0) c.Changed++;

            CheckAnchors(layout, existing, c);
            return existing;
        }

        // Each board anchor of the model must sit at its canvas centre (hub-local), within 2 cm;
        // this is the import-side half of build_hub_room.py's axis mapping: a wrong axis or
        // scale would leave the frames somewhere other than behind the panels.
        internal static void CheckAnchors(HubRoomLayout layout, Transform art, Counters c)
        {
            var anchors = new Dictionary<string, Transform>();
            foreach (Transform t in art.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("Anchor_", StringComparison.Ordinal)) anchors[t.name] = t;
            }
            int total = 0;
            int bad = 0;
            string first = null;
            foreach (HubRoomLayout.Board board in layout.Boards.Values)
            {
                total++;
                string name = HubRoomLayout.BoardAnchorName(board.Id);
                Transform a;
                if (!anchors.TryGetValue(name, out a))
                {
                    bad++;
                    if (first == null) first = name + " missing";
                    continue;
                }
                Vector3 want = layout.CanvasCenter(board);
                Vector3 got = art.InverseTransformPoint(a.position);
                if (Vector3.Distance(got, want) > AnchorToleranceM)
                {
                    bad++;
                    if (first == null) first = name + " at " + got.ToString("F3") + ", layout " + want.ToString("F3");
                }
            }
            c.Notes.Add("hub art anchors: " + (total - bad) + " of " + total + " within "
                + (AnchorToleranceM * 100f) + " cm of " + HubRoomLayout.RepoRelativePath);
            if (bad > 0)
            {
                c.Problems.Add(bad + " hub art anchor(s) disagree with " + HubRoomLayout.RepoRelativePath
                    + " (first: " + first + "); the FBX's axes/scale are not what build_hub_room.py"
                    + " meant, so the board frames would not sit behind the panels. Re-export the"
                    + " model (axis_forward -Z, up Y, FBX_SCALE_UNITS) and assemble again");
            }
        }

        // Three warm point lights under the ceiling lamps plus a soft trilight ambient for the
        // room, children of one root so a rerun replaces them. Realtime, no shadows: the hub is
        // small and lights only its own island (range 9 m).
        internal static void AssembleRoomLights(HubRoomLayout layout, Transform center, Counters c)
        {
            Transform root = EnsureChild(null, RoomLightsName, c);
            root.position = center.position;
            for (int i = 0; i < layout.Lamps.Count; i++)
            {
                string name = "Lamp" + i;
                Transform t = root.Find(name);
                if (t == null)
                {
                    var go = new GameObject(name, typeof(Light));
                    Undo.RegisterCreatedObjectUndo(go, "Create hub lamp");
                    go.transform.SetParent(root, false);
                    t = go.transform;
                    c.Created++;
                }
                t.position = center.position + layout.Lamps[i];
                Light light = t.GetComponent<Light>();
                light.type = LightType.Point;
                light.range = 9f;
                light.intensity = 0.9f;
                light.color = new Color(1f, 0.94f, 0.82f);
                light.shadows = LightShadows.None;
                light.renderMode = LightRenderMode.Auto;
            }
        }

        // ------------------------------------------------------------ event system ---

        // uGUI needs one EventSystem in the scene for VRChat's pointer to click canvases
        // (vrc_uishape "Common problems"). Keep an existing one; add one only when none exists.
        internal static void EnsureEventSystem(Counters c)
        {
            EventSystem[] all = UnityEngine.Object.FindObjectsOfType<EventSystem>(true);
            if (all.Length > 1)
            {
                c.Problems.Add(all.Length + " EventSystems in the scene; uGUI needs exactly one");
                return;
            }
            if (all.Length == 1)
            {
                if (all[0].GetComponent<BaseInputModule>() == null)
                {
                    Undo.AddComponent<StandaloneInputModule>(all[0].gameObject);
                    c.Changed++;
                }
                return;
            }
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(go, "Create EventSystem");
            c.Created++;
            c.Notes.Add("added an EventSystem (the scene had none; the wall panels need one)");
        }

        // ------------------------------------------------------------ stores ---

        internal static HubSettingsHost AssembleSettingsHost(Counters c)
        {
            HubSettingsHost host = FindOptional<HubSettingsHost>();
            if (host == null)
            {
                host = AddUdon<HubSettingsHost>(FindOrCreateRoot(SettingsHostName, c), c);
                if (host == null) return null;
            }
            VrStickMoveInput[] vr = UnityEngine.Object.FindObjectsOfType<VrStickMoveInput>(true);
            var so = new SerializedObject(host);
            if (vr.Length == 1)
            {
                SetObject(so, "vrInput", vr[0], c);
                var inputSo = new SerializedObject(vr[0]);
                if (SetObject(inputSo, "settings", host, c))
                    inputSo.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(vr[0]);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(host);
            return host;
        }

        internal static HubRecordBook AssembleRecordBook(StoryRoundController round,
            List<HubLevelCatalog.Resolved> offered, Dictionary<string, HubLevelPlan> plans,
            Counters c)
        {
            HubRecordBook book = FindOptional<HubRecordBook>();
            if (book == null)
            {
                book = AddUdon<HubRecordBook>(FindOrCreateRoot(RecordBookName, c), c);
                if (book == null) return null;
            }
            var ids = new string[offered.Count];
            var hosts = new UnityEngine.Object[offered.Count];
            var modes = new int[offered.Count];
            for (int i = 0; i < offered.Count; i++)
            {
                ids[i] = offered[i].Entry.id;
                HubLevelPlan plan;
                int goal;
                if (plans != null && plans.TryGetValue(ids[i], out plan))
                {
                    hosts[i] = plan.PickupHost;
                    goal = plan.GoalType;
                }
                else
                {
                    hosts[i] = ReadObject<PickupFieldHost>(round, "pickupHost");
                    goal = (int)round.GetGoalType();
                }
                HubLevelCatalog.RoundBlock rule = offered[i].Entry.round;
                modes[i] = goal == (int)StoryGoalType.F ? HubRecordMath.None
                    : goal == (int)StoryGoalType.B && (rule == null || !rule.recordDiameterAndTotal)
                        ? HubRecordMath.CategoryAndDiameter : HubRecordMath.DiameterAndTotal;
            }
            var so = new SerializedObject(book);
            SetObject(so, "controller", round, c);
            SetStringArray(so, "levelIds", ids, c);
            SetIntArray(so, "levelRecordModes", modes, c);
            SetObjectArray(so, "levelPickupHosts", hosts, c);
            so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(book);
            return book;
        }

        internal static HubPanelDisplay AssemblePanelDisplay(Counters c)
        {
            HubPanelDisplay display = FindOptional<HubPanelDisplay>();
            if (display == null)
            {
                display = AddUdon<HubPanelDisplay>(FindOrCreateRoot(PanelDisplayName, c), c);
            }
            return display;
        }

        // The settings store tells the panel display and the room host's prompt (language)
        // about changes; the record book tells the panel display.
        private static void WireStoreListeners(RoomParts parts, Counters c)
        {
            if (parts.Settings != null)
            {
                var so = new SerializedObject(parts.Settings);
                SetObjectArray(so, "listeners", parts.Display != null
                    ? new UnityEngine.Object[] { parts.Display } : new UnityEngine.Object[0], c);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            if (parts.Records != null)
            {
                var so = new SerializedObject(parts.Records);
                SetObjectArray(so, "listeners", parts.Display != null
                    ? new UnityEngine.Object[] { parts.Display } : new UnityEngine.Object[0], c);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }


        // ------------------------------------------------------------ read-back ---

        internal static void VerifyRoomTables(Counters c)
        {
            HubSettingsHost settings = FindOptional<HubSettingsHost>();
            HubRecordBook records = FindOptional<HubRecordBook>();
            HubPanelDisplay display = FindOptional<HubPanelDisplay>();
            if (settings == null || records == null || display == null)
            {
                c.Problems.Add("the hub needs one HubSettingsHost, HubRecordBook and HubPanelDisplay"
                    + " after assembly");
                return;
            }
            VerifyTable(settings, new[] { "vrInput", "listeners" }, c);
            VerifyTable(records, new[] { "controller", "levelIds", "levelRecordModes", "levelPickupHosts", "listeners" }, c);
            VerifyTable(display, new[] { "settings", "records", "levelNames", "levelGoalTypes", "levelCountKinds",
                "levelNameTexts", "levelRecordTexts", "settingLabelTexts", "settingValueTexts",
                "howToText", "controlsVrText", "controlsDesktopText" }, c);
            HubSettingButton[] arrows = UnityEngine.Object.FindObjectsOfType<HubSettingButton>(true);
            if (arrows.Length != HubSettingsMath.Count * 2)
            {
                c.Problems.Add("expected " + (HubSettingsMath.Count * 2) + " setting arrows, found "
                    + arrows.Length);
            }
            for (int i = 0; i < arrows.Length; i++)
            {
                VerifyTable(arrows[i], new[] { "settings", "settingId", "delta" }, c);
                string problem = PanelButtonProblem(arrows[i]);
                if (problem != null) c.Problems.Add(arrows[i].name + ": " + problem);
            }
            HubStartButton[] cards = UnityEngine.Object.FindObjectsOfType<HubStartButton>(false);
            for (int i = 0; i < cards.Length; i++)
            {
                string problem = PanelButtonProblem(cards[i]);
                if (problem != null) c.Problems.Add(cards[i].name + ": " + problem);
            }
            Scene scene = SceneManager.GetActiveScene();
            foreach (string id in BoardIds)
            {
                Transform t = FindRoot(scene, BoardPrefix + id);
                string problem = t == null ? "missing" : BoardCanvasProblem(t.gameObject);
                if (problem != null) c.Problems.Add(BoardPrefix + id + ": " + problem);
            }
            EventSystem[] systems = UnityEngine.Object.FindObjectsOfType<EventSystem>(true);
            if (systems.Length != 1)
                c.Problems.Add("expected one EventSystem for the wall panels, found " + systems.Length);
            if (ReadObject<VrStickMoveInput>(settings, "vrInput") == null)
            {
                c.Problems.Add("HubSettingsHost.vrInput is not wired: the VR controls setting"
                    + " would not switch the stick scheme");
            }
        }

        /// <summary>
        /// A panel button's contract (HUB_PANEL_PREFAB.md): a uGUI Button with Navigation None
        /// whose target graphic is a raycast-target Image, no Collider of its own, under a board
        /// canvas with VRCUiShape, and exactly one persistent onClick call:
        /// <c>SendCustomEvent("Press")</c> on this behaviour's backing UdonBehaviour. Null = ok.
        /// </summary>
        internal static string PanelButtonProblem(UdonSharpBehaviour proxy)
        {
            var button = proxy.GetComponent<Button>();
            if (button == null) return "no uGUI Button on the object";
            if (button.navigation.mode != Navigation.Mode.None) return "Button navigation is not None";
            var image = button.targetGraphic as Image;
            if (image == null || !image.raycastTarget) return "Button's target graphic is not a raycast-target Image";
            if (proxy.GetComponent<Collider>() != null) return "has a Collider of its own (the canvas's collider is the hit box)";
            Canvas canvas = proxy.GetComponentInParent<Canvas>(true);
            if (canvas == null || canvas.rootCanvas.GetComponent<VRCUiShape>() == null)
                return "not under a canvas with VRCUiShape";
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            if (backing == null) return "no backing UdonBehaviour";
            string binding = PressBindingProblem(button, backing);
            return binding == null ? null : "Button " + binding;
        }

        /// <summary>Null when the Button's onClick is exactly SendCustomEvent("Press") on backing.</summary>
        internal static string PressBindingProblem(Button button, UdonBehaviour backing)
        {
            int n = button.onClick.GetPersistentEventCount();
            if (n != 1) return "onClick has " + n + " persistent calls, wants 1";
            if (button.onClick.GetPersistentTarget(0) != backing)
                return "onClick targets '" + button.onClick.GetPersistentTarget(0)
                    + "', not this object's UdonBehaviour";
            if (button.onClick.GetPersistentMethodName(0) != "SendCustomEvent")
                return "onClick calls " + button.onClick.GetPersistentMethodName(0) + ", not SendCustomEvent";
            if (button.onClick.GetPersistentListenerState(0) == UnityEventCallState.Off)
                return "onClick call is Off";
            var so = new SerializedObject(button);
            SerializedProperty arg = so.FindProperty(
                "m_OnClick.m_PersistentCalls.m_Calls.Array.data[0].m_Arguments.m_StringArgument");
            if (arg == null || arg.stringValue != PressEvent)
                return "onClick argument is '" + (arg == null ? "?" : arg.stringValue) + "', not '"
                    + PressEvent + "'";
            return null;
        }

        /// <summary>
        /// A board root's contract: VRChat's interactable canvas (vrc_uishape): World Space Canvas,
        /// VRCUiShape and BoxCollider on the same object, GraphicRaycaster, not on the UI/UiMenu
        /// layer, scale 0.001-0.01, TextMeshPro labels that are not raycast targets, no legacy
        /// Text. Null = ok.
        /// </summary>
        internal static string BoardCanvasProblem(GameObject go)
        {
            var canvas = go.GetComponent<Canvas>();
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return "not a World Space Canvas";
            if (go.GetComponent<VRCUiShape>() == null) return "no VRCUiShape on the Canvas object";
            if (go.GetComponent<GraphicRaycaster>() == null) return "no GraphicRaycaster";
            if (go.GetComponent<BoxCollider>() == null) return "no BoxCollider on the Canvas object";
            if (go.layer == LayerMask.NameToLayer("UI") || go.layer == LayerMask.NameToLayer("UiMenu"))
                return "on layer " + LayerMask.LayerToName(go.layer) + " (must not be UI/UiMenu)";
            float s = go.transform.lossyScale.x;
            if (!(s >= 0.0009f && s <= 0.0101f)) return "scale " + s + " is outside 0.001-0.01";
            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.raycastTarget) return text.name + " is a raycast target (it could cover a button)";
                if (text.font == null) return text.name + " has no font asset";
            }
            foreach (Text legacy in go.GetComponentsInChildren<Text>(true))
                return legacy.name + " is a legacy uGUI Text (use TextMeshPro)";
            foreach (Selectable selectable in go.GetComponentsInChildren<Selectable>(true))
                if (selectable.navigation.mode != Navigation.Mode.None) return selectable.name + " navigation is not None";
            return null;
        }
    }
}
