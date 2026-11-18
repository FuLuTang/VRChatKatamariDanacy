using System;
using System.Collections.Generic;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;
using VRCKatamari.Input;
using VRCKatamari.Pickup;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The hub room's look and its three wall boards (levels, how to play + controls,
    /// settings): the Blender-built model, world-space text on its anchors, the Interact
    /// buttons, the settings store, the record book and the panel display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model. <c>Art/Hub/HubRoomArt.fbx</c> (built by <c>Tools/hubart/build_hub_room.py</c>
    /// from <c>Tools/hubart/hub_layout.json</c>) is instantiated once under the root
    /// <c>HubRoomArt</c> at <c>HubCenter</c>, colliders stripped (the room's walls and floor
    /// collide through the placement import, not the art). Its materials are named
    /// <c>Hub_&lt;colour&gt;</c>; the assembler makes one Standard material per name in
    /// <c>Art/Hub/Materials/</c> with the layout's colour, and remaps the model to them.
    /// Every <c>Anchor_*</c> empty is compared with the layout; one more than 2 cm off means the
    /// FBX's axes or scale are not what the script meant and is a PROBLEM.
    /// </para>
    /// <para>
    /// Text and buttons. One world-space Canvas per board (<c>HubBoard_&lt;id&gt;</c>, 1 mm per
    /// canvas unit) holds every Text, placed from the layout (same numbers as the anchors).
    /// Buttons are separate objects with a trigger BoxCollider and an Udon behaviour
    /// (Interact: desktop left click, VR trigger); no uGUI Button, no raycast targets.
    /// Without the model (not imported yet) the boards still get a flat coloured backing so
    /// the panel works, and the run says the art is missing.
    /// </para>
    /// </remarks>
    public static partial class HubAssembler
    {
        public const string RoomArtName = "HubRoomArt";
        public const string BoardPrefix = "HubBoard_";
        public const string LevelButtonPrefix = "LevelButton_";
        public const string SettingButtonPrefix = "SettingButton_";
        public const string SettingsHostName = "HubSettings";
        public const string RecordBookName = "HubRecords";
        public const string PanelDisplayName = "HubPanelDisplay";
        public const string RoomLightsName = "HubRoomLights";
        internal const string LegacyPanelName = "HubLevelPanel";
        internal const string ArtMaterialFolder = "Assets/VRC_Katamari/Art/Hub/Materials";
        internal const float AnchorToleranceM = 0.02f;
        internal const float BoardMetresPerUnit = 0.001f;
        private const float ButtonDepthM = 0.04f;
        internal const float TextLiftM = 0.006f;
        internal const int MaxFontSize = 200;
        internal const float PreviewNumberLiftM = 0.015f;

        internal sealed class RoomParts
        {
            public HubRoomLayout Layout;
            public readonly Dictionary<string, Transform> Boards = new Dictionary<string, Transform>();
            public Transform[] LevelButtons = new Transform[0];
            public Text Prompt;
            public HubSettingsHost Settings;
            public HubRecordBook Records;
            public HubPanelDisplay Display;
        }

        // ------------------------------------------------------------ entry ---

        internal static RoomParts AssembleRoom(List<HubLevelCatalog.Resolved> offered,
            Dictionary<string, HubLevelPlan> plans, StoryRoundController round, Transform center,
            Font font, HubLevelLauncher launcher, Counters c)
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
            if (layout.Cards.Count < offered.Count)
            {
                c.Problems.Add(offered.Count + " levels are offered but the hub layout has "
                    + layout.Cards.Count + " level cards (" + HubRoomLayout.RepoRelativePath + ")");
            }
            if (layout.Rows.Count != HubSettingsMath.Count)
            {
                c.Problems.Add("the hub layout has " + layout.Rows.Count + " settings rows; the"
                    + " settings store has " + HubSettingsMath.Count);
            }

            RemoveLegacyPanel(c);
            Dictionary<string, Material> materials = EnsureArtMaterials(layout, c);
            Transform art = AssembleRoomArt(layout, center, materials, c);
            AssembleRoomLights(layout, center, c);

            foreach (HubRoomLayout.Board board in layout.Boards.Values)
            {
                parts.Boards[board.Id] = AssembleBoardCanvas(board, center, art == null
                    ? FlatBacking(layout, board.Id) : Color.clear, c);
            }

            parts.Settings = AssembleSettingsHost(c);
            parts.Records = AssembleRecordBook(round, offered, plans, c);
            parts.Display = AssemblePanelDisplay(c);

            // Texts.
            var texts = new Dictionary<string, Text>();
            foreach (KeyValuePair<string, HubRoomLayout.Box> pair in layout.Texts)
            {
                texts[pair.Key] = PlaceText("Text_" + pair.Key, pair.Value, parts, layout, center,
                    font, c);
            }
            texts.TryGetValue("Prompt", out parts.Prompt);

            // Level cards.
            int n = Math.Min(offered.Count, layout.Cards.Count);
            parts.LevelButtons = new Transform[n];
            var nameTexts = new Text[n];
            var goalTexts = new Text[n];
            var recordTexts = new Text[n];
            var startTexts = new Text[n];
            var wanted = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                HubRoomLayout.Card card = layout.Cards[i];
                string id = offered[i].Entry.id;
                nameTexts[i] = PlaceText("Level" + i + "_Name", card.Name, parts, layout, center, font, c);
                goalTexts[i] = PlaceText("Level" + i + "_Goal", card.Goal, parts, layout, center, font, c);
                recordTexts[i] = PlaceText("Level" + i + "_Record", card.Record, parts, layout, center, font, c);
                startTexts[i] = PlaceText("Level" + i + "_Start", card.Start, parts, layout, center, font, c);
                // The preview swatch is a 5 mm slab built at the preview depth + 4 mm, so its
                // front face is 6.5 mm in front of the anchor; the number goes in front of that.
                Text number = PlaceText("Level" + i + "_Number", card.Preview, parts, layout, center,
                    font, c, PreviewNumberLiftM);
                if (number != null) number.text = (i + 1).ToString();

                string buttonName = LevelButtonPrefix + id;
                wanted.Add(buttonName);
                Transform buttonRoot;
                HubStartButton start = EnsureButton<HubStartButton>(parts.Boards[card.Button.Board],
                    buttonName, card.Button, layout, center, c, out buttonRoot);
                if (start == null) continue;
                var so = new SerializedObject(start);
                bool changed = SetObject(so, "launcher", launcher, c);
                SerializedProperty index = so.FindProperty("levelIndex");
                if (index.intValue != i) { index.intValue = i; changed = true; }
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(start);
                SetInteractText(start, "Start: " + offered[i].Entry.name, c);
                parts.LevelButtons[i] = buttonRoot;
            }
            RemoveUnwanted(parts.Boards["Levels"], LevelButtonPrefix, wanted, c);

            // Settings rows.
            var labelTexts = new Text[HubSettingsMath.Count];
            var valueTexts = new Text[HubSettingsMath.Count];
            for (int r = 0; r < layout.Rows.Count && r < HubSettingsMath.Count; r++)
            {
                HubRoomLayout.Row row = layout.Rows[r];
                labelTexts[row.Id] = PlaceText("Setting" + row.Id.ToString("00") + "_Label", row.Label,
                    parts, layout, center, font, c);
                valueTexts[row.Id] = PlaceText("Setting" + row.Id.ToString("00") + "_Value", row.Value,
                    parts, layout, center, font, c);
                WireSettingButton(parts, row.Id, -1, row.Less, layout, center, c);
                WireSettingButton(parts, row.Id, 1, row.More, layout, center, c);
            }

            // Display wiring.
            if (parts.Display != null)
            {
                var names = new string[n];
                var goals = new int[n];
                var diameters = new float[n];
                var counts = new int[n];
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
                        limits[i] = plan.TimeLimitSeconds;
                    }
                    else
                    {
                        ReadSingleLevelGoal(round, out goals[i], out diameters[i], out counts[i],
                            out limits[i]);
                    }
                }
                var so = new SerializedObject(parts.Display);
                SetObject(so, "settings", parts.Settings, c);
                SetObject(so, "records", parts.Records, c);
                SetStringArray(so, "levelNames", names, c);
                SetIntArray(so, "levelGoalTypes", goals, c);
                SetFloatArray(so, "levelTargetDiameters", diameters, c);
                SetIntArray(so, "levelTargetCounts", counts, c);
                SetFloatArray(so, "levelTimeLimits", limits, c);
                SetObjectArray(so, "levelNameTexts", nameTexts, c);
                SetObjectArray(so, "levelGoalTexts", goalTexts, c);
                SetObjectArray(so, "levelRecordTexts", recordTexts, c);
                SetObjectArray(so, "levelStartTexts", startTexts, c);
                SetObject(so, "levelsTitle", Get(texts, "LevelsTitle"), c);
                SetObject(so, "helpTitle", Get(texts, "HelpTitle"), c);
                SetObject(so, "howToText", Get(texts, "HowTo"), c);
                SetObject(so, "controlsTitle", Get(texts, "ControlsTitle"), c);
                SetObject(so, "controlsVrText", Get(texts, "ControlsVr"), c);
                SetObject(so, "controlsDesktopText", Get(texts, "ControlsDesktop"), c);
                SetObject(so, "settingsTitle", Get(texts, "SettingsTitle"), c);
                SetObject(so, "settingsHintText", Get(texts, "SettingsHint"), c);
                SetObject(so, "creditsText", Get(texts, "Credits"), c);
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

        private static Text Get(Dictionary<string, Text> texts, string id)
        {
            Text t;
            return texts.TryGetValue(id, out t) ? t : null;
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
            Shader shader = Shader.Find("Standard");
            foreach (KeyValuePair<string, Color> pair in layout.Palette)
            {
                string path = ArtMaterialFolder + "/" + pair.Key + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(shader) { name = pair.Key };
                    AssetDatabase.CreateAsset(m, path);
                    c.Created++;
                }
                if (m.color != pair.Value || m.GetFloat("_Glossiness") != 0.25f)
                {
                    m.color = pair.Value;
                    m.SetFloat("_Glossiness", 0.25f);
                    m.SetFloat("_Metallic", 0f);
                    EditorUtility.SetDirty(m);
                    c.Changed++;
                }
                result[pair.Key] = m;
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

        internal static Transform AssembleRoomArt(HubRoomLayout layout, Transform center,
            Dictionary<string, Material> materials, Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform existing = FindRoot(scene, RoomArtName);
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(layout.ModelPath);
            if (model == null)
            {
                c.Notes.Add("the hub art model " + layout.ModelPath + " is not imported yet; the"
                    + " boards get flat coloured backings instead (run Tools/hubart/build_hub_room.py,"
                    + " let Unity import it, then assemble again)");
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

        // Every anchor of the model must sit where the layout says (hub-local), within 2 cm;
        // this is the import-side half of build_hub_room.py's axis mapping.
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
            Action<string, HubRoomLayout.Box> check = (name, box) =>
            {
                total++;
                Transform a;
                if (!anchors.TryGetValue(name, out a))
                {
                    bad++;
                    if (first == null) first = name + " missing";
                    return;
                }
                Vector3 want;
                Quaternion rot;
                layout.Pose(box, out want, out rot);
                Vector3 got = art.InverseTransformPoint(a.position);
                if (Vector3.Distance(got, want) > AnchorToleranceM)
                {
                    bad++;
                    if (first == null) first = name + " at " + got.ToString("F3") + ", layout " + want.ToString("F3");
                }
            };
            foreach (KeyValuePair<string, HubRoomLayout.Box> t in layout.Texts)
                check(HubRoomLayout.AnchorName("Text", 0, t.Key), t.Value);
            foreach (HubRoomLayout.Card card in layout.Cards)
            {
                check(HubRoomLayout.AnchorName("Level", card.Index, "Button"), card.Button);
                check(HubRoomLayout.AnchorName("Level", card.Index, "Name"), card.Name);
                check(HubRoomLayout.AnchorName("Level", card.Index, "Start"), card.Start);
            }
            foreach (HubRoomLayout.Row row in layout.Rows)
            {
                check(HubRoomLayout.AnchorName("Setting", row.Id, "Less"), row.Less);
                check(HubRoomLayout.AnchorName("Setting", row.Id, "More"), row.More);
            }
            c.Notes.Add("hub art anchors: " + (total - bad) + " of " + total + " within "
                + (AnchorToleranceM * 100f) + " cm of " + HubRoomLayout.RepoRelativePath);
            if (bad > 0)
            {
                c.Problems.Add(bad + " hub art anchor(s) disagree with " + HubRoomLayout.RepoRelativePath
                    + " (first: " + first + "); the FBX's axes/scale are not what build_hub_room.py"
                    + " meant, so text and buttons would float off the boards. Re-export the model"
                    + " (axis_forward -Z, up Y, FBX_SCALE_UNITS) and assemble again");
            }
        }

        private static Color FlatBacking(HubRoomLayout layout, string boardId)
        {
            Color cream;
            return layout.Palette.TryGetValue("Hub_Cream", out cream) ? cream : new Color(1f, 0.96f, 0.86f);
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

        // ------------------------------------------------------------ boards ---

        internal static Transform AssembleBoardCanvas(HubRoomLayout.Board board, Transform center,
            Color backing, Counters c)
        {
            string name = BoardPrefix + board.Id;
            Transform t = FindRoot(SceneManager.GetActiveScene(), name);
            Vector2 sizeUnits = board.Size / BoardMetresPerUnit;
            if (t == null)
            {
                t = CreateWorldCanvas(name, null, sizeUnits).transform;
                c.Created++;
            }
            t.position = center.position + board.Center;
            t.rotation = board.Rotation;
            t.localScale = Vector3.one * BoardMetresPerUnit;
            ((RectTransform)t).sizeDelta = sizeUnits;
            t.gameObject.layer = 0;
            Image image = t.GetComponent<Image>();
            if (backing.a > 0f)
            {
                if (image == null) image = t.gameObject.AddComponent<Image>();
                image.color = backing;
                image.raycastTarget = false;
            }
            else if (image != null)
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
            return t;
        }

        // Text Rect in its board's canvas: centre at the box, size of the box, glyph em =
        // fontSize canvas units = em metres. Depth puts it just in front of the art surface.
        internal static Text PlaceText(string name, HubRoomLayout.Box box, RoomParts parts,
            HubRoomLayout layout, Transform center, Font font, Counters c)
        {
            return PlaceText(name, box, parts, layout, center, font, c, TextLiftM);
        }

        // Text is drawn depth-tested against the art, so it must sit in front of the surface it
        // labels: lift = metres in front of the anchor.
        internal static Text PlaceText(string name, HubRoomLayout.Box box, RoomParts parts,
            HubRoomLayout layout, Transform center, Font font, Counters c, float lift)
        {
            if (box == null || string.IsNullOrEmpty(box.Board)) return null;
            Transform board;
            if (!parts.Boards.TryGetValue(box.Board, out board)) return null;
            Transform t = board.Find(name);
            Text text;
            if (t == null)
            {
                text = CreateText(name, board, font, 10, box.Align);
                c.Created++;
                t = text.transform;
            }
            else text = t.GetComponent<Text>();
            var rect = (RectTransform)t;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            // uGUI Text is only safe up to fontSize 300 (larger requests can be clamped or drop
            // glyphs from the dynamic atlas); a bigger glyph is drawn at a smaller fontSize on a
            // scaled rect, so the world size stays em metres.
            int wanted = Mathf.Max(1, Mathf.RoundToInt(box.Em / BoardMetresPerUnit));
            int scale = Mathf.Max(1, Mathf.CeilToInt(wanted / (float)MaxFontSize));
            rect.sizeDelta = new Vector2(box.W, Mathf.Max(box.H, box.Em * 1.4f))
                / BoardMetresPerUnit / scale;
            // Canvas local -Z is towards the reader, so depth is negative local z.
            rect.localPosition = new Vector3(box.X, box.Y, -(box.Depth + lift)) / BoardMetresPerUnit;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one * scale;
            text.font = font;
            text.fontSize = Mathf.Max(1, Mathf.RoundToInt(wanted / (float)scale));
            text.alignment = box.Align;
            text.lineSpacing = 1.0f;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            Color navy;
            text.color = box.White ? Color.white
                : layout.Palette.TryGetValue("Hub_Navy", out navy) ? navy : new Color(0.13f, 0.17f, 0.38f);
            t.gameObject.layer = 0;
            return text;
        }

        // A press box: its own object under the board canvas, sized to the box face, with a
        // trigger collider and the Udon behaviour (Interact is delivered to the behaviour on
        // the collider's object). Default layer, so the interact ray is not filtered out.
        internal static T EnsureButton<T>(Transform board, string name, HubRoomLayout.Box box,
            HubRoomLayout layout, Transform center, Counters c, out Transform root)
            where T : UdonSharpBehaviour
        {
            root = board.Find(name);
            T behaviour;
            if (root == null)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(BoxCollider));
                Undo.RegisterCreatedObjectUndo(go, "Create " + name);
                go.transform.SetParent(board, false);
                behaviour = AddUdon<T>(go, c);
                root = go.transform;
                c.Created++;
            }
            else behaviour = root.GetComponent<T>();
            if (behaviour == null)
            {
                c.Problems.Add(name + " has no " + typeof(T).Name);
                return null;
            }
            var rect = (RectTransform)root;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(box.W, box.H) / BoardMetresPerUnit;
            rect.localPosition = new Vector3(box.X, box.Y, -box.Depth) / BoardMetresPerUnit;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            root.gameObject.layer = 0;
            var collider = root.GetComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.size = new Vector3(box.W, box.H, ButtonDepthM) / BoardMetresPerUnit;
            return behaviour;
        }

        private static void RemoveUnwanted(Transform board, string prefix, HashSet<string> wanted,
            Counters c)
        {
            for (int i = board.childCount - 1; i >= 0; i--)
            {
                Transform child = board.GetChild(i);
                if (!child.name.StartsWith(prefix, StringComparison.Ordinal) || wanted.Contains(child.name)) continue;
                c.Notes.Add("removed '" + child.name + "': the scene no longer offers that level");
                Undo.DestroyObjectImmediate(child.gameObject);
                c.Changed++;
            }
        }

        private static void WireSettingButton(RoomParts parts, int id, int delta,
            HubRoomLayout.Box box, HubRoomLayout layout, Transform center, Counters c)
        {
            string name = SettingButtonPrefix + id.ToString("00") + (delta < 0 ? "_Less" : "_More");
            Transform root;
            HubSettingButton button = EnsureButton<HubSettingButton>(parts.Boards[box.Board], name,
                box, layout, center, c, out root);
            if (button == null) return;
            var so = new SerializedObject(button);
            bool changed = SetObject(so, "settings", parts.Settings, c);
            SerializedProperty sid = so.FindProperty("settingId");
            SerializedProperty sd = so.FindProperty("delta");
            if (sid.intValue != id) { sid.intValue = id; changed = true; }
            if (sd.intValue != delta) { sd.intValue = delta; changed = true; }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(button);
            SetInteractText(button, (delta < 0 ? "< " : "> ")
                + HubPanelTextMath.SettingLabel(id, HubSettingsMath.LangEn), c);
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
            if (vr.Length == 1) SetObject(so, "vrInput", vr[0], c);
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
            for (int i = 0; i < offered.Count; i++)
            {
                ids[i] = offered[i].Entry.id;
                HubLevelPlan plan;
                if (plans != null && plans.TryGetValue(ids[i], out plan)) hosts[i] = plan.PickupHost;
                else hosts[i] = ReadObject<PickupFieldHost>(round, "pickupHost");
            }
            var so = new SerializedObject(book);
            SetObject(so, "controller", round, c);
            SetObject(so, "motor", FindOptional<KatamariBallMotor>(), c);
            SetStringArray(so, "levelIds", ids, c);
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
            VerifyTable(records, new[] { "controller", "motor", "levelIds", "levelPickupHosts", "listeners" }, c);
            VerifyTable(display, new[] { "settings", "records", "levelNames", "levelGoalTypes",
                "levelNameTexts", "levelRecordTexts", "settingLabelTexts", "settingValueTexts",
                "howToText", "controlsVrText", "controlsDesktopText" }, c);
            HubSettingButton[] arrows = UnityEngine.Object.FindObjectsOfType<HubSettingButton>(true);
            if (arrows.Length != HubSettingsMath.Count * 2)
            {
                c.Problems.Add("expected " + (HubSettingsMath.Count * 2) + " setting arrows, found "
                    + arrows.Length);
            }
            for (int i = 0; i < arrows.Length; i++)
                VerifyTable(arrows[i], new[] { "settings", "settingId", "delta" }, c);
            if (ReadObject<VrStickMoveInput>(settings, "vrInput") == null)
            {
                c.Problems.Add("HubSettingsHost.vrInput is not wired: the VR controls setting"
                    + " would not switch the stick scheme");
            }
        }
    }
}
