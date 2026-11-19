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
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.Diagnostics;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;
using VRCKatamari.Input;
using VRCKatamari.Riding;
using VRCKatamari.Story;

namespace VRCKatamari.Editor.Story
{
    /// <summary>
    /// Puts the rider's view, click target, panels and exit route into the open scene,
    /// and can be run again without changing anything it already did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it assembles, in the order it does it:
    /// </para>
    /// <list type="number">
    /// <item><description>The follow frame's four eye coefficients on the motor, written
    /// from <see cref="RideViewMath"/> (the one source; the bases are zero): the push stance,
    /// eye 1.900 r above and 1.042 r behind the ball centre, so a standing rider's hands meet
    /// the ball's back surface at every size.</description></item>
    /// <item><description>The VRCStation as a standing station (<c>seated</c> off): the
    /// station carries the avatar by its feet, which the seat puts on the ground.</description></item>
    /// <item><description><c>BallSeatTrigger</c> under the ball's physics root: a trigger
    /// unit sphere with <see cref="BallSeatInteract"/>, sized by that behaviour at runtime.
    /// The seat's own 0.45 m click box (at the rider's eye, not on the ball) is removed.</description></item>
    /// <item><description><c>RidePanels</c> under the follow frame: the parent of every rider
    /// panel, scaled by <see cref="BallRideStation"/> with the rider's eye height. The HUD canvas
    /// (<c>StoryHud</c>) is turned into a world-space panel under it: a screen-space overlay
    /// canvas is drawn for screen users only, so a VR player never sees it.</description></item>
    /// <item><description><c>RideResultCard</c>: one instance of the result card prefab
    /// (<see cref="ResultCardPrefabPath"/>, a VRChat interactable canvas built by the Unity
    /// operator from HUB_PANEL_PREFAB.md) under <c>RidePanels</c>; the presenter's card root and
    /// text, the round's button row and the two uGUI buttons' round/launcher are wired here.
    /// The old <c>HubResultBoard</c> and collider buttons are removed.</description></item>
    /// <item><description>The rider's size: <c>BallRideStation.scaleRiderWithBall</c> is
    /// switched on and its filter and rate-limit fields are written from
    /// <see cref="RideScaleMath"/> (the push stance at every ball size),
    /// <c>SeatEyeCompensation.motor</c> is wired to the motor whose lowest point the seat puts
    /// the feet on, and its vertical limit is the standing head rail.</description></item>
    /// <item><description>The seat's anchor: the VRCStation's own transform and its
    /// <c>stationEnterPlayerLocation</c> are both set on the follow frame's pose and
    /// <c>SeatEyeCompensation.seatAnchor</c> points at the station transform, the transform
    /// VRChat carries a seated player with. An authored entry offset would be measured as
    /// part of the head offset and then clipped by the horizontal limit (the "+0.45 m to the
    /// right" rider bug), so the entry has none.</description></item>
    /// <item><description>The VRCStation's exit location (<c>stationExitPlayerLocation</c>),
    /// created as a child <c>RideExitPoint</c> when the station has none, placed by
    /// <see cref="RideExitMath"/> beside the ball on flat ground; the station moves it again on
    /// every exit.</description></item>
    /// <item><description><c>RideExitPanel</c> (under <c>RidePanels</c>): the exit button (press twice) and the hold-jump
    /// hint, active only while seated, wired to the station.</description></item>
    /// <item><description>本地视图接线（<see cref="AssembleLocalViews"/>）：场景里唯一的
    /// <c>CollisionWireframe</c> 接到设置、motor 和 Static/Terrain/Prop 三个子物体；唯一的
    /// <c>BallPerformanceDiagnostics</c> 被场景里每个带 <c>performanceDiagnostics</c> 字段的
    /// U# 行为引用（motor、各 PickupFieldHost、各 MotionHost）。宿主对象本身在基础场景里手工
    /// 搭好，这里只接线；All-Maps 每次从基础场景重建输出，所以接线必须由装配完成。</description></item>
    /// </list>
    /// <para>
    /// Panels hang on the follow frame (through <c>RidePanels</c>), not on the seat or the ball: the frame is the eye
    /// point, already smoothed and yawed with the ball's heading, so the panels stay in
    /// front of the rider whichever way the ball turns and never roll with the ball's
    /// spin. A standing player sees them near the ball, which doubles as the prompt.
    /// </para>
    /// <para>
    /// Nothing is saved. New behaviours (<see cref="BallSeatInteract"/> is new) need their
    /// UdonSharp program asset; a field added to an existing behaviour is missing from the
    /// existing instances' variable tables until the scene is saved and reopened
    /// (Labs/README.md). The run ends with a read-back that names any such field; the
    /// remedy is compile, save, reopen, run again.
    /// </para>
    /// </remarks>
    public static class RideUxAssembler
    {
        public const string MenuPath = "VRC Katamari/Ride/Assemble Ride UX";

        public const string SeatTriggerName = "BallSeatTrigger";

        /// <summary>The exit location object the assembler creates under the VRCStation.</summary>
        public const string ExitPointName = "RideExitPoint";

        public const string ResultCardName = "RideResultCard";
        public const string PanelRootName = "RidePanels";
        public const string ExitPanelName = "RideExitPanel";

        // Layout for a rider of RideViewMath.PanelReferenceEyeHeightMeters (the panel root
        // scales it with the rider): distance from the eye, azimuth to the right, elevation
        // above the horizon, canvas scale (metres per canvas unit) and the canvas size in units.
        // 0.0014 puts a 30-unit glyph at 4.2 cm, about 1.5 degrees at 1.6 m, which is legible in
        // a headset.
        // 面板与裸球轮廓的净空见 ride_geometry.py 的 PANELS；HUD 位于球的左下方。
        private const float PanelDistance = 1.6f;
        private const float PanelScale = 0.0014f;

        private const float HudAzimuth = -38f;
        private const float HudElevation = -12f;
        private static readonly Vector2 HudPivot = new Vector2(0.5f, 0.5f);
        private static readonly Vector2 HudSize = new Vector2(1000f, 260f);

        private const float CardAzimuth = 0f;
        private const float CardElevation = 6f;
        // Canvas units are millimetres, as on the hub boards (HUB_PANEL_PREFAB.md): the prefab's
        // 1152 x 576 card is the same 1.152 x 0.576 m the earlier 720 x 360 x 0.0016 card was.
        private const float CardScale = 0.001f;

        private const float ExitAzimuth = 24f;
        private const float ExitElevation = -8f;
        private static readonly Vector2 ExitSize = new Vector2(360f, 150f);
        private const float ExitPlateHeight = 90f;

        private const float InteractProximity = 5f;
        private const float BallInteractProximity = 4f;

        // VRCStation.Mobility order: Mobile = 0, Immobilize = 1, ImmobilizeForVehicle = 2.
        // The ride station moves with the follow frame every frame (and the seat compensation
        // moves it again), so VRChat's documented mode for stations that move applies:
        // "Immobilize For Vehicle: ... Use this for stations that move, such as vehicles."
        private const int StationMobilityForVehicle = 2;

        private sealed class Counters
        {
            public int Created;
            public int Changed;
            public readonly List<string> Problems = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public readonly List<Component> Proxies = new List<Component>();
        }

        [MenuItem(MenuPath, false, 410)]
        public static void Assemble()
        {
            string report = AssembleOpenScene();
            Debug.Log(report);
        }

        /// <summary>
        /// Runs the assembly on the active scene and returns the one-paragraph report
        /// (also what the menu logs). Safe to call repeatedly.
        /// </summary>
        public static string AssembleOpenScene()
        {
            return AssembleOpenScene(null);
        }

        /// <summary>
        /// <see cref="AssembleOpenScene()"/> for a caller that folds the ride assembly into its
        /// own report (the All-Maps hub): every PROBLEM is also appended to
        /// <paramref name="problemsOut"/> when it is not null.
        /// </summary>
        public static string AssembleOpenScene(List<string> problemsOut)
        {
            var c = new Counters();
            string report = AssembleInto(c);
            if (problemsOut != null) problemsOut.AddRange(c.Problems);
            return report;
        }

        private static string AssembleInto(Counters c)
        {

            KatamariBallMotor motor = Single<KatamariBallMotor>(c);
            BallRideStation station = Single<BallRideStation>(c);
            StoryRoundController round = Single<StoryRoundController>(c);
            if (motor == null || station == null || round == null)
            {
                return Finish(c, "stopped: the scene needs exactly one motor, one ride station"
                    + " and one round controller");
            }
            StoryResultPresenter presenter = FindOptional<StoryResultPresenter>();
            BallRideExitButton exit = FindOptional<BallRideExitButton>();

            // 新同步来的 U# 行为可能还没有程序资产（AddComponent 会抛）；缺则建并编译。
            var createdPrograms = new List<string>();
            VRCKatamari.Editor.Hub.UdonProgramAssets.EnsureAll(createdPrograms, c.Problems);
            if (createdPrograms.Count > 0)
                c.Notes.Add("created U# program assets: " + string.Join(", ", createdPrograms.ToArray()));
            try
            {
                UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
            }
            catch (Exception exception)
            {
                c.Problems.Add("the UdonSharp compile step threw: " + exception.Message);
            }

            Transform rigRoot = ReadTransform(motor, "followRigRoot");
            Transform ballRoot = ReadTransform(motor, "ballPhysicsRoot");
            if (rigRoot == null) c.Problems.Add("motor.followRigRoot is not wired: nothing can follow the rider");
            if (ballRoot == null) c.Problems.Add("motor.ballPhysicsRoot is not wired: no click sphere");

            AssembleEyeRule(motor, c);
            AssembleStandingStation(station, c);
            BallSeatInteract ballInteract = ballRoot != null
                ? AssembleSeatTrigger(ballRoot, station, motor, c) : null;
            RemoveSeatBox(station, c);

            Font font = null;
            Text targetText = ReadObject<Text>(round, "targetText");
            if (targetText != null) font = targetText.font;

            if (rigRoot != null)
            {
                Transform panels = AssemblePanelRoot(rigRoot, c);
                AssembleHud(targetText, panels, c);
                AssembleResultCard(round, presenter, rigRoot, panels, c);
                GameObject panel = AssembleExitPanel(station, exit, font, rigRoot, panels, c,
                    out Text hint);
                AssembleExitLocation(station, ballRoot, motor, c);
                AssembleStationLinks(station, motor, ballInteract, panel, hint, rigRoot, panels, c);
                NormalizePanelLayers(panels, c);
                AssemblePushCue(motor, rigRoot, station, round, c);
                AssembleCatchName(station, round, c);
            }
            AssembleLocalViews(motor, c);

            CopyAllToUdon(c);
            VerifyUdonTables(station, c);

            EditorSceneManager.MarkAllScenesDirty();
            return Finish(c, "done");
        }

        // ------------------------------------------------------------------ eye rule ---

        private static void AssembleEyeRule(KatamariBallMotor motor, Counters c)
        {
            var so = new SerializedObject(motor);
            bool changed = false;
            changed |= SetFloat(so, "eyeBaseUpMeters", RideViewMath.EyeBaseUpMeters, c);
            changed |= SetFloat(so, "eyeUpPerRadius", RideViewMath.EyeUpPerRadius(), c);
            changed |= SetFloat(so, "eyeBaseBackMeters", RideViewMath.EyeBaseBackMeters, c);
            changed |= SetFloat(so, "eyeBackPerRadius", RideViewMath.EyeBackPerRadius(), c);
            // 这两个护栏只限制加性底座，不限制半径项。
            changed |= SetFloat(so, "eyeUpMaxMeters", 128f, c);
            changed |= SetFloat(so, "eyeMaxBackM", 128f, c);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(motor);
                c.Notes.Add("push stance written to the motor (push angle "
                    + RideViewMath.PushAngleDegrees + " deg)");
            }
        }

        // The push stance is a standing one: the station carries the avatar by its feet, and a
        // seated pose would put the thighs into the ball. VRCStation's serialized "seated".
        private static void AssembleStandingStation(BallRideStation station, Counters c)
        {
            UnityEngine.Object linked = ReadLinkedStation(station);
            if (linked == null)
            {
                c.Problems.Add("BallRideStation.station is not wired: the station cannot be made standing");
                return;
            }
            var so = new SerializedObject(linked);
            bool changed = SetBool(so, "seated", false, c);
            changed |= SetInt(so, "PlayerMobility", StationMobilityForVehicle, c);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Notes.Add("the VRCStation is a standing, moving-station station"
                    + " (seated off, PlayerMobility=ImmobilizeForVehicle)");
            }
        }

        // -------------------------------------------------------------- click sphere ---

        private static BallSeatInteract AssembleSeatTrigger(Transform ballRoot,
            BallRideStation station, KatamariBallMotor motor, Counters c)
        {
            Transform existing = ballRoot.Find(SeatTriggerName);
            GameObject go;
            if (existing == null)
            {
                go = new GameObject(SeatTriggerName);
                Undo.RegisterCreatedObjectUndo(go, "Create " + SeatTriggerName);
                go.transform.SetParent(ballRoot, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                float diameter = RideViewMath.SeatTriggerRadius(0.111f) * 2f;
                go.transform.localScale = new Vector3(diameter, diameter, diameter);
                c.Created++;
            }
            else
            {
                go = existing.gameObject;
            }

            SphereCollider sphere = go.GetComponent<SphereCollider>();
            if (sphere == null)
            {
                sphere = go.AddComponent<SphereCollider>();
                c.Created++;
            }
            if (!sphere.isTrigger || !Mathf.Approximately(sphere.radius, 0.5f)
                || sphere.center != Vector3.zero)
            {
                sphere.isTrigger = true;
                sphere.radius = 0.5f;
                sphere.center = Vector3.zero;
                c.Changed++;
            }

            BallSeatInteract interact = go.GetComponent<BallSeatInteract>();
            if (interact == null)
            {
                interact = AddUdon<BallSeatInteract>(go, c);
                if (interact == null) return null;
            }
            var so = new SerializedObject(interact);
            bool changed = SetObject(so, "ride", station, c);
            changed |= SetObject(so, "motor", motor, c);
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(interact);
            SetInteractText(interact, "骑上去", BallInteractProximity, c);
            return interact;
        }

        // The seat's own box sits at the rider's eye point, not on the ball, so a click on
        // the ball never reaches it; it only offers a prompt in mid-air.
        private static void RemoveSeatBox(BallRideStation station, Counters c)
        {
            Collider[] colliders = station.GetComponents<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Undo.DestroyObjectImmediate(colliders[i]);
                c.Changed++;
                c.Notes.Add("removed the seat's own click collider " + colliders[i].GetType().Name);
            }
        }

        // ----------------------------------------------------------------------- HUD ---

        // The one parent of the rider's panels, directly under the follow frame at the eye
        // point with no offset, so scaling it scales the panels about the eyes.
        private static Transform AssemblePanelRoot(Transform rigRoot, Counters c)
        {
            Transform root = rigRoot.Find(PanelRootName);
            if (root == null)
            {
                var go = new GameObject(PanelRootName);
                Undo.RegisterCreatedObjectUndo(go, "Create " + PanelRootName);
                go.transform.SetParent(rigRoot, false);
                root = go.transform;
                c.Created++;
                c.Notes.Add("panel root created under the follow frame");
            }
            if (root.localPosition != Vector3.zero || root.localRotation != Quaternion.identity
                || root.localScale != Vector3.one)
            {
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                root.localScale = Vector3.one;
                c.Changed++;
            }
            return root;
        }

        // A panel an earlier assembly put directly on the follow frame moves under the panel
        // root, keeping its local pose (the root has none of its own).
        private static Transform FindPanel(Transform rigRoot, Transform panels, string name,
            Counters c)
        {
            Transform found = panels.Find(name);
            if (found != null) return found;
            found = rigRoot.Find(name);
            if (found == null) return null;
            Undo.SetTransformParent(found, panels, "Move the panel under the panel root");
            c.Changed++;
            c.Notes.Add("'" + name + "' moved under " + PanelRootName);
            return found;
        }

        // VRChat's UI (5) and UiMenu (12) layers gate UI pointers on the menu; layer 8 is
        // unused by the client and also keeps stickers off our Interact colliders. Repair the
        // whole subtree, including migrated text and inactive result/exit panels.
        // The result card is a VRChat interactable canvas and sits on Default (0) like the hub
        // boards (HUB_PANEL_PREFAB.md); the prefab owns that layer, so its subtree is skipped.
        private static void NormalizePanelLayers(Transform panels, Counters c)
        {
            Transform[] children = panels.GetComponentsInChildren<Transform>(true);
            Transform card = panels.Find(ResultCardName);
            for (int i = 0; i < children.Length; i++)
            {
                GameObject go = children[i].gameObject;
                if (card != null && children[i].IsChildOf(card)) continue;
                if (go.layer == 8) continue;
                Undo.RecordObject(go, "Set ride panel layer");
                go.layer = 8;
                c.Changed++;
            }
        }

        private static void AssembleHud(Text anyHudText, Transform panels, Counters c)
        {
            if (anyHudText == null)
            {
                c.Problems.Add("round.targetText is not wired: the HUD canvas cannot be found");
                return;
            }
            Canvas canvas = anyHudText.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                c.Problems.Add("the HUD text is not under a Canvas");
                return;
            }
            var existingRect = (RectTransform)canvas.transform;
            bool alreadyOurs = canvas.renderMode == RenderMode.WorldSpace
                && canvas.transform.parent == panels
                && existingRect.localScale == Vector3.one * PanelScale
                && existingRect.sizeDelta == HudSize
                && existingRect.pivot == HudPivot
                && existingRect.localPosition == RideViewMath.HudLocalPosition(
                    PanelDistance, HudAzimuth, HudElevation)
                && existingRect.localRotation == RideViewMath.HudLocalRotation(HudAzimuth, HudElevation);
            if (alreadyOurs) return;

            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvas.transform;
            Undo.SetTransformParent(rect, panels, "Hang the HUD on the rider's panel root");
            PlacePanel(rect, HudSize, HudPivot, PanelScale, HudAzimuth, HudElevation);
            c.Changed++;
            c.Notes.Add("HUD canvas '" + canvas.name + "' is world-space under " + PanelRootName);
        }

        // 复用场景中已有的箭头外观；不生成另一套网格或材质。显示根节点独立于球和缩放面板。
        private static void AssemblePushCue(KatamariBallMotor motor, Transform rigRoot, BallRideStation station,
            StoryRoundController round, Counters c)
        {
            var scene = station.gameObject.scene;
            PushDirectionCue cue = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<PushDirectionCue>(true))
            {
                if (candidate.gameObject.scene != scene) continue;
                if (cue != null)
                {
                    c.Problems.Add("more than one PushDirectionCue in the ride scene; none changed");
                    return;
                }
                cue = candidate;
            }
            if (cue == null)
            {
                c.Notes.Add("no authored PushDirectionCue in the ride scene");
                return;
            }

            Transform visual = ReadTransform(cue, "cueRoot");
            if (visual == null || visual.parent != cue.transform)
            {
                c.Problems.Add("PushDirectionCue.cueRoot must be a direct visual child; cue left untouched");
                return;
            }

            HubSettingsHost settings = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<HubSettingsHost>(true))
            {
                if (candidate.gameObject.scene != scene) continue;
                if (settings != null)
                {
                    c.Problems.Add("more than one HubSettingsHost in the ride scene; cue left untouched");
                    return;
                }
                settings = candidate;
            }

            Transform root = cue.transform;
            if (root.parent != null)
            {
                Undo.SetTransformParent(root, null, "Detach the push cue from the ball");
                c.Changed++;
            }
            if (root.localScale != Vector3.one)
            {
                Undo.RecordObject(root, "Keep the push cue in world metres");
                root.localScale = Vector3.one;
                c.Changed++;
            }
            if (visual.gameObject.activeSelf)
            {
                Undo.RecordObject(visual.gameObject, "Hide the push cue until riding");
                visual.gameObject.SetActive(false);
                c.Changed++;
            }

            var so = new SerializedObject(cue);
            bool changed = SetObject(so, "motor", motor, c);
            changed |= SetObject(so, "pushYawSource", rigRoot, c);
            changed |= SetObject(so, "rideStation", station, c);
            changed |= SetObject(so, "roundController", round, c);
            changed |= SetObject(so, "settings", settings, c);
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(cue);
            c.Notes.Add("push cue is scene-rooted and ball-relative; local settings "
                + (settings == null ? "absent (default on)" : "wired"));
        }

        // ---------------------------------------------------------------- catch name ---

        // 「刚吸到的东西」HUD（CATCH_NAME_HUD.md）：与推进箭头同为场景根独立物体，只接线、
        // 缺什么建什么。圆底用内置 UI 圆形精灵，小模型槽位不带网格（运行时借用被吸物件的
        // sharedMesh/sharedMaterials），不新建网格、材质或相机。吸到瞬间的线默认不建。
        private const string CatchHudName = "CatchNameHud";
        private const string CatchModelPivotName = "ModelPivot";
        private const int CatchModelSlots = 4;
        private const float CatchCanvasUnits = 100f;
        private const string CatchFontPath = "Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans SDF.asset";

        private static void AssembleCatchName(BallRideStation station, StoryRoundController round,
            Counters c)
        {
            var scene = station.gameObject.scene;
            CatchNameDisplay display = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<CatchNameDisplay>(true))
            {
                if (candidate.gameObject.scene != scene) continue;
                if (display != null)
                {
                    c.Problems.Add("more than one CatchNameDisplay in the ride scene; none changed");
                    return;
                }
                display = candidate;
            }
            if (display == null)
            {
                c.Notes.Add("no authored CatchNameDisplay in the ride scene");
                return;
            }

            HubSettingsHost settings = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<HubSettingsHost>(true))
            {
                if (candidate.gameObject.scene != scene) continue;
                if (settings != null)
                {
                    c.Problems.Add("more than one HubSettingsHost in the ride scene; catch name left untouched");
                    return;
                }
                settings = candidate;
            }

            Transform root = display.transform;
            if (root.parent != null)
            {
                Undo.SetTransformParent(root, null, "Detach the catch-name HUD");
                c.Changed++;
            }
            if (root.localScale != Vector3.one)
            {
                Undo.RecordObject(root, "Keep the catch-name HUD in world metres");
                root.localScale = Vector3.one;
                c.Changed++;
            }

            Transform hud = root.Find(CatchHudName);
            if (hud == null) hud = BuildCatchHud(root, c);
            Transform pivot = hud.Find(CatchModelPivotName);
            var filters = pivot != null ? pivot.GetComponentsInChildren<MeshFilter>(true) : new MeshFilter[0];
            var renderers = new MeshRenderer[filters.Length];
            for (int i = 0; i < filters.Length; i++) renderers[i] = filters[i].GetComponent<MeshRenderer>();
            TMPro.TextMeshProUGUI shortText = FindTmp(hud, "ShortName");
            TMPro.TextMeshProUGUI longText = FindTmp(hud, "LongName");
            if (pivot == null || filters.Length == 0 || shortText == null || longText == null
                || Array.IndexOf(renderers, null) >= 0)
            {
                c.Problems.Add(CatchHudName + " lacks ModelPivot slots or ShortName/LongName texts;"
                    + " delete it and run the assembly again");
                return;
            }
            if (hud.gameObject.activeSelf)
            {
                Undo.RecordObject(hud.gameObject, "Hide the catch-name HUD until riding");
                hud.gameObject.SetActive(false);
                c.Changed++;
            }

            var so = new SerializedObject(display);
            bool changed = SetObject(so, "rideStation", station, c);
            changed |= SetObject(so, "roundController", round, c);
            changed |= SetObject(so, "settings", settings, c);
            changed |= SetObject(so, "hudRoot", hud, c);
            changed |= SetObject(so, "modelPivot", pivot, c);
            changed |= SetObject(so, "shortNameText", shortText, c);
            changed |= SetObject(so, "longNameText", longText, c);
            changed |= SetObjectArray(so, "modelFilters", filters, c);
            changed |= SetObjectArray(so, "modelRenderers", renderers, c);
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(display);
            int hosts = WireCatchNameHosts(display, scene, c);
            c.Notes.Add("catch-name HUD is scene-rooted with " + filters.Length + " model slots; local settings "
                + (settings == null ? "absent (default on)" : "wired") + "; pickup hosts wired " + hosts);
        }

        // 每个道具宿主的 catchNameDisplay 指向同一个 HUD。宿主还没有这个字段时只记一条说明。
        private static int WireCatchNameHosts(CatchNameDisplay display, UnityEngine.SceneManagement.Scene scene,
            Counters c)
        {
            int wired = 0;
            foreach (var host in UnityEngine.Object.FindObjectsOfType<VRCKatamari.Pickup.PickupFieldHost>(true))
            {
                if (host.gameObject.scene != scene) continue;
                var hostSo = new SerializedObject(host);
                SerializedProperty field = hostSo.FindProperty("catchNameDisplay");
                if (field == null)
                {
                    c.Problems.Add("PickupFieldHost.catchNameDisplay is not a serialized field (renamed?)");
                    return 0;
                }
                if (field.objectReferenceValue != display)
                {
                    field.objectReferenceValue = display;
                    hostSo.ApplyModifiedPropertiesWithoutUndo();
                    c.Changed++;
                }
                // 代理一致而 Udon 表落后时（场景存盘前的旧表）也要补拷，拷贝本身不算改动。
                c.Proxies.Add(host);
                wired++;
            }
            return wired;
        }

        // ------------------------------------------------------------ local views ---

        /// <summary>引用性能诊断的宿主字段名（motor、PickupFieldHost、MotionHost 共用）。</summary>
        internal const string PerformanceDiagnosticsField = "performanceDiagnostics";
        internal const string CatchNameDisplayField = "catchNameDisplay";
        internal static readonly string[] WireframeChildren = { "Static", "Terrain", "Prop" };
        private static readonly string[] WireframeLineFields = { "staticLines", "terrainLines", "propLines" };
        private static readonly string[] WireframeFilterFields = { "staticFilter", "terrainFilter", "propFilter" };
        private const string WireframeArtFolder = "Assets/VRC_Katamari/Art/Debug/";

        // 碰撞线框与性能诊断都是基础场景里手工搭好的根对象；这里只接线（缺线框子物体时按
        // Art/Debug 的材质与空网格补建）。场景里没有对象时记一条说明，有两个以上时记 problem。
        private static void AssembleLocalViews(KatamariBallMotor motor, Counters c)
        {
            Scene scene = motor.gameObject.scene;
            string wire = AssembleWireframe(scene, motor, c);
            string diag = AssemblePerformanceHosts(scene, c);
            string comfort = AssembleMotorSettings(scene, motor, c);
            c.Notes.Add("local views: " + wire + "; " + diag + "; " + comfort);
        }

        /// <summary>骑乘球 motor 读「减弱自动转向」的设置宿主字段名。</summary>
        internal const string MotorSettingsField = "settings";

        // 骑乘球 motor 的 settings 指向场景里唯一的 HubSettingsHost（「减弱自动转向」）。
        private static string AssembleMotorSettings(Scene scene, KatamariBallMotor motor, Counters c)
        {
            List<HubSettingsHost> settings = InScene<HubSettingsHost>(scene);
            if (settings.Count == 0) return "motor settings absent (no HubSettingsHost)";
            if (settings.Count > 1)
            {
                c.Problems.Add("more than one HubSettingsHost in the scene; motor.settings left untouched");
                return "motor settings not wired";
            }
            var so = new SerializedObject(motor);
            if (so.FindProperty(MotorSettingsField) == null)
            {
                c.Problems.Add("KatamariBallMotor." + MotorSettingsField + " is not a serialized field (renamed?)");
                return "motor settings not wired";
            }
            if (SetObject(so, MotorSettingsField, settings[0], c)) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(motor);
            return "motor settings wired";
        }

        private static string AssembleWireframe(Scene scene, KatamariBallMotor motor, Counters c)
        {
            List<CollisionWireframe> all = InScene<CollisionWireframe>(scene);
            if (all.Count == 0) return "no authored CollisionWireframe";
            if (all.Count > 1)
            {
                c.Problems.Add("more than one CollisionWireframe in the scene (" + all.Count + "); none wired");
                return "wireframe not wired";
            }
            CollisionWireframe wire = all[0];
            List<HubSettingsHost> settings = InScene<HubSettingsHost>(scene);
            if (settings.Count > 1)
            {
                c.Problems.Add("more than one HubSettingsHost in the scene; CollisionWireframe.settings left untouched");
                return "wireframe not wired";
            }

            var so = new SerializedObject(wire);
            bool changed = SetObject(so, "motor", motor, c);
            if (settings.Count == 1) changed |= SetObject(so, "settings", settings[0], c);
            for (int i = 0; i < WireframeChildren.Length; i++)
            {
                Transform child = EnsureWireframeChild(wire.transform, WireframeChildren[i], c);
                if (child == null) continue;
                changed |= SetObject(so, WireframeLineFields[i], child.GetComponent<MeshRenderer>(), c);
                changed |= SetObject(so, WireframeFilterFields[i], child.GetComponent<MeshFilter>(), c);
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(wire);
            return "wireframe settings " + (settings.Count == 1 ? "wired" : "absent (no HubSettingsHost)");
        }

        // 一个线条子物体：MeshFilter（空网格资产）+ MeshRenderer（Unlit 颜色材质，初始关）。
        // 已有的子物体只补缺的组件和空引用，不改手工设置的材质/网格。
        private static Transform EnsureWireframeChild(Transform root, string name, Counters c)
        {
            Transform child = root.Find(name);
            if (child == null)
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create wireframe " + name);
                go.transform.SetParent(root, false);
                child = go.transform;
                c.Created++;
            }
            var filter = child.GetComponent<MeshFilter>();
            if (filter == null) { filter = Undo.AddComponent<MeshFilter>(child.gameObject); c.Created++; }
            var renderer = child.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<MeshRenderer>(child.gameObject);
                renderer.enabled = false;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                c.Created++;
            }
            if (filter.sharedMesh == null)
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(WireframeArtFolder + "Mesh_CollisionWire_" + name + ".asset");
                if (mesh == null) c.Problems.Add("missing " + WireframeArtFolder + "Mesh_CollisionWire_" + name + ".asset");
                else { Undo.RecordObject(filter, "Wireframe mesh"); filter.sharedMesh = mesh; c.Changed++; }
            }
            if (renderer.sharedMaterial == null)
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(WireframeArtFolder + "Mat_CollisionWire_" + name + ".mat");
                if (material == null) c.Problems.Add("missing " + WireframeArtFolder + "Mat_CollisionWire_" + name + ".mat");
                else { Undo.RecordObject(renderer, "Wireframe material"); renderer.sharedMaterial = material; c.Changed++; }
            }
            return child;
        }

        // 场景里每个带 performanceDiagnostics 字段的 U# 行为都指向唯一的诊断器。
        private static string AssemblePerformanceHosts(Scene scene, Counters c)
        {
            List<BallPerformanceDiagnostics> all = InScene<BallPerformanceDiagnostics>(scene);
            if (all.Count == 0) return "no authored BallPerformanceDiagnostics";
            if (all.Count > 1)
            {
                c.Problems.Add("more than one BallPerformanceDiagnostics in the scene (" + all.Count + "); no host wired");
                return "performance diagnostics not wired";
            }
            BallPerformanceDiagnostics diag = all[0];
            c.Proxies.Add(diag);
            int hosts = 0;
            foreach (UdonSharpBehaviour host in InScene<UdonSharpBehaviour>(scene))
            {
                var so = new SerializedObject(host);
                SerializedProperty field = so.FindProperty(PerformanceDiagnosticsField);
                if (field == null || field.propertyType != SerializedPropertyType.ObjectReference) continue;
                hosts++;
                if (field.objectReferenceValue != diag)
                {
                    field.objectReferenceValue = diag;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    c.Changed++;
                }
                c.Proxies.Add(host);
            }
            return "performance diagnostics wired " + hosts + " host(s)";
        }

        /// <summary>装配后读回的本地视图接线（代理字段与 Udon 表都对上才算 wired）。</summary>
        internal sealed class LocalWiring
        {
            public int DiagnosticsHosts;
            public int DiagnosticsWired;
            public int CatchHosts;
            public int CatchWired;
            public bool WireframeFound;
            public bool WireframeSettingsWired;
            public bool MotorSettingsWired;
        }

        /// <summary>
        /// 读回 <paramref name="scene"/> 的三组接线：性能诊断的宿主、物品名 HUD 的道具宿主、
        /// 碰撞线框的设置/motor/六个子引用。每处缺接线或 Udon 表不一致都加一条 problem。
        /// All-Maps 装配和 Check Room Wiring 共用这一个实现。
        /// </summary>
        internal static LocalWiring ReadLocalWiring(Scene scene, List<string> problems)
        {
            var r = new LocalWiring();

            List<BallPerformanceDiagnostics> diags = InScene<BallPerformanceDiagnostics>(scene);
            if (diags.Count != 1)
                problems.Add("expected exactly one BallPerformanceDiagnostics, found " + diags.Count);
            BallPerformanceDiagnostics diag = diags.Count == 1 ? diags[0] : null;
            foreach (UdonSharpBehaviour host in InScene<UdonSharpBehaviour>(scene))
            {
                SerializedProperty field = new SerializedObject(host).FindProperty(PerformanceDiagnosticsField);
                if (field == null || field.propertyType != SerializedPropertyType.ObjectReference) continue;
                r.DiagnosticsHosts++;
                if (diag == null) continue;
                if (field.objectReferenceValue == diag && UdonReferenceIs(host, PerformanceDiagnosticsField, diag))
                    r.DiagnosticsWired++;
                else
                    problems.Add(host.GetType().Name + " on '" + host.name + "' does not point "
                        + PerformanceDiagnosticsField + " at the scene's BallPerformanceDiagnostics (proxy and Udon table)");
            }
            if (diag != null && r.DiagnosticsHosts == 0)
                problems.Add("no behaviour in the scene has a " + PerformanceDiagnosticsField + " field (renamed?)");

            List<CatchNameDisplay> displays = InScene<CatchNameDisplay>(scene);
            if (displays.Count != 1)
                problems.Add("expected exactly one CatchNameDisplay, found " + displays.Count);
            CatchNameDisplay display = displays.Count == 1 ? displays[0] : null;

            // 骑乘球 motor 的 settings（「减弱自动转向」）。
            List<HubSettingsHost> hubSettings = InScene<HubSettingsHost>(scene);
            List<KatamariBallMotor> rideMotors = InScene<KatamariBallMotor>(scene);
            r.MotorSettingsWired = hubSettings.Count == 1 && rideMotors.Count == 1
                && ReadProperty(new SerializedObject(rideMotors[0]), MotorSettingsField) == hubSettings[0]
                && UdonReferenceIs(rideMotors[0], MotorSettingsField, hubSettings[0]);
            if (!r.MotorSettingsWired)
                problems.Add("KatamariBallMotor.settings is not the scene's HubSettingsHost (proxy and Udon"
                    + " table); the \"less auto-turn\" setting would do nothing");
            foreach (VRCKatamari.Pickup.PickupFieldHost host in InScene<VRCKatamari.Pickup.PickupFieldHost>(scene))
            {
                r.CatchHosts++;
                if (display == null) continue;
                SerializedProperty field = new SerializedObject(host).FindProperty(CatchNameDisplayField);
                if (field != null && field.objectReferenceValue == display
                    && UdonReferenceIs(host, CatchNameDisplayField, display))
                    r.CatchWired++;
                else
                    problems.Add("PickupFieldHost on '" + host.name + "' does not point " + CatchNameDisplayField
                        + " at the scene's CatchNameDisplay (proxy and Udon table)");
            }

            List<CollisionWireframe> wires = InScene<CollisionWireframe>(scene);
            if (wires.Count != 1)
            {
                problems.Add("expected exactly one CollisionWireframe, found " + wires.Count);
                return r;
            }
            r.WireframeFound = true;
            CollisionWireframe wire = wires[0];
            List<HubSettingsHost> settings = InScene<HubSettingsHost>(scene);
            List<KatamariBallMotor> motors = InScene<KatamariBallMotor>(scene);
            var wireSo = new SerializedObject(wire);
            UnityEngine.Object wiredSettings = ReadProperty(wireSo, "settings");
            r.WireframeSettingsWired = settings.Count == 1 && wiredSettings == settings[0]
                && UdonReferenceIs(wire, "settings", settings[0]);
            if (!r.WireframeSettingsWired)
                problems.Add("CollisionWireframe.settings is not the scene's HubSettingsHost (proxy and Udon"
                    + " table); the \"show physics\" setting would never draw");
            if (motors.Count != 1 || ReadProperty(wireSo, "motor") != motors[0]
                || !UdonReferenceIs(wire, "motor", motors[0]))
                problems.Add("CollisionWireframe.motor is not the scene's KatamariBallMotor (proxy and Udon table)");
            for (int i = 0; i < WireframeChildren.Length; i++)
            {
                var lines = ReadProperty(wireSo, WireframeLineFields[i]) as Renderer;
                var filter = ReadProperty(wireSo, WireframeFilterFields[i]) as MeshFilter;
                if (lines == null || filter == null || lines.transform.parent != wire.transform
                    || filter.gameObject != lines.gameObject || filter.sharedMesh == null || lines.sharedMaterial == null)
                    problems.Add("CollisionWireframe " + WireframeChildren[i] + " lines are not a wired child"
                        + " with mesh and material (" + WireframeLineFields[i] + "/" + WireframeFilterFields[i] + ")");
            }
            return r;
        }

        /// <summary>All-Maps 汇总行（不含前缀）。</summary>
        internal static string LocalWiringSummary(int diagnosticsWired, int diagnosticsHosts,
            int catchWired, int catchHosts, bool wireframeFound, bool wireframeSettingsWired)
        {
            return "diagnostics wired " + diagnosticsWired + "/" + diagnosticsHosts + " hosts, catch-name wired "
                + catchWired + "/" + catchHosts + ", wireframe settings "
                + (!wireframeFound ? "MISSING (no CollisionWireframe)" : wireframeSettingsWired ? "wired" : "NOT wired");
        }

        /// <summary>骑乘球 motor 设置接线的汇总读数（「减弱自动转向」），接在上一行之后打印。</summary>
        internal static string MotorSettingsSummary(bool motorSettingsWired)
        {
            return "motor settings (less auto-turn) " + (motorSettingsWired ? "wired" : "NOT wired");
        }

        private static UnityEngine.Object ReadProperty(SerializedObject so, string field)
        {
            SerializedProperty property = so.FindProperty(field);
            return property == null ? null : property.objectReferenceValue;
        }

        // owner 的 Udon 表里 field = target 的 backing UdonBehaviour。
        private static bool UdonReferenceIs(UdonSharpBehaviour owner, string field, UdonSharpBehaviour target)
        {
            if (target == null) return false;
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(target);
            object value;
            return backing != null && VRCKatamari.Editor.Hub.UdonVariableAccess.TryRead(owner, field, out value)
                && (value as UdonBehaviour) == backing;
        }

        // owner 的 Udon 表里 field = target（GameObject 或普通组件，不是 U# 行为）。
        private static bool UdonReferenceIsObject(UdonSharpBehaviour owner, string field, UnityEngine.Object target)
        {
            object value;
            return target != null && VRCKatamari.Editor.Hub.UdonVariableAccess.TryRead(owner, field, out value)
                && (value as UnityEngine.Object) == target;
        }

        private static List<T> InScene<T>(Scene scene) where T : Component
        {
            var found = new List<T>();
            if (!scene.IsValid() || !scene.isLoaded) return found;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) found.AddRange(roots[i].GetComponentsInChildren<T>(true));
            return found;
        }

        // 原型：圆底直径 1、中心在原点、正面朝 -Z（观看者一侧）；名字在圆底下方。
        private static Transform BuildCatchHud(Transform root, Counters c)
        {
            var hudGo = new GameObject(CatchHudName);
            Undo.RegisterCreatedObjectUndo(hudGo, "Create " + CatchHudName);
            hudGo.transform.SetParent(root, false);
            hudGo.layer = 8;
            c.Created++;

            GameObject canvasGo = CreateCanvasChild("Disc", hudGo.transform, new Vector2(CatchCanvasUnits, CatchCanvasUnits * 1.6f));
            var canvasRect = (RectTransform)canvasGo.transform;
            canvasRect.localPosition = new Vector3(0f, -0.3f, 0.05f);
            GameObject discGo = CreateRect("Back", canvasGo.transform);
            var disc = (RectTransform)discGo.transform;
            disc.anchorMin = disc.anchorMax = new Vector2(0.5f, 1f);
            disc.pivot = new Vector2(0.5f, 1f);
            disc.sizeDelta = new Vector2(CatchCanvasUnits, CatchCanvasUnits);
            disc.anchoredPosition = Vector2.zero;
            Image back = discGo.AddComponent<Image>();
            back.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
            back.color = new Color(0.08f, 0.08f, 0.12f, 0.55f);
            back.raycastTarget = false;
            CreateCatchText("ShortName", canvasGo.transform, 22f);
            CreateCatchText("LongName", canvasGo.transform, 17f);

            var pivotGo = new GameObject(CatchModelPivotName);
            pivotGo.transform.SetParent(hudGo.transform, false);
            pivotGo.transform.localPosition = new Vector3(0f, 0f, -0.45f);
            pivotGo.layer = 8;
            for (int i = 0; i < CatchModelSlots; i++)
            {
                var slot = new GameObject("Slot_" + i, typeof(MeshFilter), typeof(MeshRenderer));
                slot.transform.SetParent(pivotGo.transform, false);
                slot.layer = 8;
                var r = slot.GetComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = false;
            }
            foreach (Transform t in hudGo.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 8;
            return hudGo.transform;
        }

        private static GameObject CreateCanvasChild(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            rect.pivot = new Vector2(0.5f, 0.5f);
            float scale = 1f / CatchCanvasUnits;
            rect.localScale = new Vector3(scale, scale, scale);
            return go;
        }

        // 名字用大厅面板字体（没有时用 TMP 默认字体）；字体资产外的字（任意中文/日文名）由
        // VRChat 内置后备字体渲染，Editor 里显示方块属正常（TMP Settings 的 fallback 为空）。
        private static void CreateCatchText(string name, Transform parent, float size)
        {
            GameObject go = CreateRect(name, parent);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(CatchCanvasUnits * 2f, CatchCanvasUnits * 0.55f);
            rect.anchoredPosition = Vector2.zero;
            var text = go.AddComponent<TMPro.TextMeshProUGUI>();
            var font = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(CatchFontPath);
            if (font != null) text.font = font;
            text.text = "";
            text.fontSize = size;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.color = Color.white;
            text.outlineWidth = 0.2f;
            text.outlineColor = new Color32(0, 0, 0, 200);
            text.raycastTarget = false;
        }

        private static TMPro.TextMeshProUGUI FindTmp(Transform hud, string name)
        {
            foreach (var text in hud.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
                if (text.name == name) return text;
            return null;
        }

        private static bool SetObjectArray(SerializedObject so, string field, UnityEngine.Object[] values,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null || !property.isArray)
            {
                c.Problems.Add(so.targetObject.GetType().Name + "." + field + " is not a serialized array");
                return false;
            }
            bool changed = property.arraySize != values.Length;
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue == values[i]) continue;
                element.objectReferenceValue = values[i];
                changed = true;
            }
            if (changed) c.Changed++;
            return changed;
        }

        // --------------------------------------------------------------- result card ---

        /// <summary>The result card prefab (HUB_PANEL_PREFAB.md, "结算卡"), built by the Unity operator.</summary>
        public const string ResultCardPrefabPath = "Assets/VRC_Katamari/Prefabs/Story/RideResultCard.prefab";

        // The prefab's naming contract; paths are under the card root.
        internal const string CardTextPath = "ResultText";
        internal const string CardButtonsPath = "Buttons";
        internal const string CardRetryPath = "Buttons/RetryButton";
        internal const string CardReturnPath = "Buttons/ReturnButton";

        // Roots that earlier builds of the card left in a scene: the base scene's hub board
        // (legacy Text + Interact colliders) and the hub's collider return button. Removed on
        // sight; the prefab instance replaces them.
        internal const string LegacyResultBoardName = "HubResultBoard";
        internal const string LegacyCardReturnName = "HubReturnCardButton";

        // One instance of the result card prefab under RidePanels, in front of the rider. The
        // card's structure, words and Button bindings are the prefab's; this places it and
        // writes the scene references a prefab cannot hold: the presenter's card root and text,
        // the round's button row, the retry button's round and launcher, the return button's
        // launcher. A scene without a hub launcher hides the return button.
        private static GameObject AssembleResultCard(StoryRoundController round,
            StoryResultPresenter presenter, Transform rigRoot, Transform panels, Counters c)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResultCardPrefabPath);
            if (prefab == null)
            {
                c.Problems.Add("no result card prefab at " + ResultCardPrefabPath + "; build it from"
                    + " Docs/Design/StoryMode/HUB_PANEL_PREFAB.md (结算卡), then assemble again");
                return null;
            }

            RemoveLegacyCard(c);
            Transform existing = FindPanel(rigRoot, panels, ResultCardName, c);
            if (existing != null
                && PrefabUtility.GetCorrespondingObjectFromSource(existing.gameObject) != prefab)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
                existing = null;
                c.Changed++;
                c.Notes.Add("replaced the assembled '" + ResultCardName + "' with an instance of "
                    + ResultCardPrefabPath);
            }
            GameObject card;
            if (existing == null)
            {
                card = (GameObject)PrefabUtility.InstantiatePrefab(prefab, panels.gameObject.scene);
                Undo.RegisterCreatedObjectUndo(card, "Place " + ResultCardName);
                card.name = ResultCardName;
                card.transform.SetParent(panels, false);
                card.SetActive(false);
                c.Created++;
                c.Notes.Add("result card instance placed under " + PanelRootName
                    + " and starts hidden (the presenter shows it)");
            }
            else
            {
                card = existing.gameObject;
            }
            var rect = (RectTransform)card.transform;
            if (rect.localPosition != RideViewMath.HudLocalPosition(PanelDistance, CardAzimuth, CardElevation)
                || rect.localRotation != RideViewMath.HudLocalRotation(CardAzimuth, CardElevation)
                || rect.localScale != Vector3.one * CardScale)
            {
                Undo.RecordObject(rect, "Place " + ResultCardName);
                PlacePanel(rect, rect.sizeDelta, rect.pivot, CardScale, CardAzimuth, CardElevation);
                c.Changed++;
            }

            Transform textT = card.transform.Find(CardTextPath);
            TMPro.TextMeshProUGUI text = textT != null ? textT.GetComponent<TMPro.TextMeshProUGUI>() : null;
            Transform buttons = card.transform.Find(CardButtonsPath);
            Transform retryT = card.transform.Find(CardRetryPath);
            Transform returnT = card.transform.Find(CardReturnPath);
            StoryRetryButton retry = retryT != null ? retryT.GetComponent<StoryRetryButton>() : null;
            HubReturnButton back = returnT != null ? returnT.GetComponent<HubReturnButton>() : null;
            if (text == null) c.Problems.Add(ResultCardName + "/" + CardTextPath + " is not a TextMeshProUGUI (HUB_PANEL_PREFAB.md)");
            if (buttons == null) c.Problems.Add(ResultCardName + " has no '" + CardButtonsPath + "' row (HUB_PANEL_PREFAB.md)");
            if (retry == null) c.Problems.Add(ResultCardName + "/" + CardRetryPath + " has no StoryRetryButton (HUB_PANEL_PREFAB.md)");
            if (back == null) c.Problems.Add(ResultCardName + "/" + CardReturnPath + " has no HubReturnButton (HUB_PANEL_PREFAB.md)");

            if (presenter != null)
            {
                var so = new SerializedObject(presenter);
                bool changed = SetObject(so, "resultCardRoot", card, c);
                if (text != null) changed |= SetObject(so, "resultText", text, c);
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(presenter);
            }
            if (buttons != null)
            {
                var so = new SerializedObject(round);
                if (SetObject(so, "retryButtonRoot", buttons.gameObject, c))
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    c.Proxies.Add(round);
                }
            }

            HubLevelLauncher launcher = FindOptional<HubLevelLauncher>();
            if (retry != null)
            {
                var so = new SerializedObject(retry);
                bool changed = SetObject(so, "controller", round, c);
                changed |= SetObject(so, "launcher", launcher, c);
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(retry);
            }
            if (back != null)
            {
                var so = new SerializedObject(back);
                if (SetObject(so, "launcher", launcher, c)) so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(back);
                bool wanted = launcher != null;
                if (back.gameObject.activeSelf != wanted)
                {
                    Undo.RecordObject(back.gameObject, "Show the return button");
                    back.gameObject.SetActive(wanted);
                    c.Changed++;
                }
                if (!wanted)
                    c.Notes.Add("no HubLevelLauncher in the scene: the card's return button is hidden");
            }
            return card;
        }

        // The earlier card: the base scene's HubResultBoard (and anything under it), any
        // collider return button an older hub run put elsewhere, and any StoryRetryButton
        // left outside a card (the base scene's root StoryRetryButton object).
        private static void RemoveLegacyCard(Counters c)
        {
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != LegacyResultBoardName) continue;
                Undo.DestroyObjectImmediate(root);
                c.Changed++;
                c.Notes.Add("removed the old '" + LegacyResultBoardName + "' (the result card prefab replaces it)");
            }
            foreach (HubReturnButton b in InScene<HubReturnButton>(scene))
            {
                if (b.name != LegacyCardReturnName) continue;
                Undo.DestroyObjectImmediate(b.gameObject);
                c.Changed++;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResultCardPrefabPath);
            foreach (StoryRetryButton r in InScene<StoryRetryButton>(scene))
            {
                GameObject owner = PrefabUtility.GetOutermostPrefabInstanceRoot(r.gameObject);
                if (owner != null && PrefabUtility.GetCorrespondingObjectFromSource(owner) == prefab) continue;
                if (r.transform.childCount > 0)
                {
                    c.Problems.Add("a StoryRetryButton outside the result card ('" + r.name + "') has"
                        + " children; remove it by hand");
                    continue;
                }
                Undo.DestroyObjectImmediate(r.gameObject);
                c.Changed++;
                c.Notes.Add("removed the old StoryRetryButton object (the card's RetryButton replaces it)");
            }
        }

        // ---------------------------------------------------------------- exit panel ---

        private static GameObject AssembleExitPanel(BallRideStation station,
            BallRideExitButton exit, Font font, Transform rigRoot, Transform panels, Counters c,
            out Text hint)
        {
            hint = null;
            Transform existing = FindPanel(rigRoot, panels, ExitPanelName, c);
            GameObject panel;
            Text label;
            if (existing != null)
            {
                panel = existing.gameObject;
                Transform hintTransform = panel.transform.Find("ExitHold");
                if (hintTransform != null) hint = hintTransform.GetComponent<Text>();
                Transform labelTransform = panel.transform.Find("ExitPlate/Label");
                label = labelTransform != null ? labelTransform.GetComponent<Text>() : null;
            }
            else
            {
                panel = CreateCanvas(ExitPanelName, panels, ExitSize, new Vector2(0.5f, 0.5f),
                    PanelScale, ExitAzimuth, ExitElevation, c);

                GameObject plate = CreateRect("ExitPlate", panel.transform);
                var plateRect = (RectTransform)plate.transform;
                plateRect.anchorMin = new Vector2(0f, 1f);
                plateRect.anchorMax = new Vector2(1f, 1f);
                plateRect.pivot = new Vector2(0.5f, 1f);
                plateRect.anchoredPosition = Vector2.zero;
                plateRect.sizeDelta = new Vector2(0f, ExitPlateHeight);
                Image face = plate.AddComponent<Image>();
                face.color = new Color(0.55f, 0.15f, 0.15f, 0.9f);
                face.raycastTarget = false;

                label = CreateText("Label", plate.transform, font, 34, TextAnchor.MiddleCenter);
                Stretch((RectTransform)label.transform);
                label.text = "下车（点两次）";

                hint = CreateText("ExitHold", panel.transform, font, 26, TextAnchor.MiddleCenter);
                var hintRect = (RectTransform)hint.transform;
                hintRect.anchorMin = new Vector2(0f, 0f);
                hintRect.anchorMax = new Vector2(1f, 0f);
                hintRect.pivot = new Vector2(0.5f, 0f);
                hintRect.anchoredPosition = Vector2.zero;
                hintRect.sizeDelta = new Vector2(0f, ExitSize.y - ExitPlateHeight);
                hint.text = "按住跳跃键下车";
                c.Notes.Add("exit panel created; BallRideStation shows it only while seated");
            }

            if (exit == null)
            {
                c.Problems.Add("no BallRideExitButton in the scene: the panel has no button");
                return panel;
            }
            if (exit.transform.parent != panel.transform)
            {
                Undo.SetTransformParent(exit.transform, panel.transform,
                    "Move the exit button onto the rider's panel");
                // Centre of the plate in the panel's local (pivot-centred) units.
                float plateCentreY = ExitSize.y * 0.5f - ExitPlateHeight * 0.5f;
                exit.transform.localPosition = new Vector3(0f, plateCentreY, 0f);
                exit.transform.localRotation = Quaternion.identity;
                exit.transform.localScale = Vector3.one;
                c.Changed++;
            }
            BoxCollider box = exit.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = exit.gameObject.AddComponent<BoxCollider>();
                c.Created++;
            }
            Vector3 wanted = new Vector3(ExitSize.x, ExitPlateHeight, 40f);
            if (!box.isTrigger || box.size != wanted || box.center != Vector3.zero)
            {
                box.isTrigger = true;
                box.size = wanted;
                box.center = Vector3.zero;
                c.Changed++;
            }
            var so = new SerializedObject(exit);
            bool changed = SetObject(so, "ride", station, c);
            changed |= SetObject(so, "label", label, c);
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(exit);
            SetInteractText(exit, "下车", InteractProximity, c);
            return panel;
        }

        private static void AssembleStationLinks(BallRideStation station,
            KatamariBallMotor motor, BallSeatInteract ballInteract, GameObject exitPanel,
            Text hint, Transform rigRoot, Transform panels, Counters c)
        {
            var so = new SerializedObject(station);
            bool changed = SetObject(so, "motor", motor, c);
            if (ballInteract != null) changed |= SetObject(so, "ballInteract", ballInteract, c);
            SeatEyeCompensation seatEye = FindOptional<SeatEyeCompensation>();
            if (seatEye != null)
            {
                changed |= SetObject(so, "seatEye", seatEye, c);
                AssembleSeatLinks(seatEye, motor, station, rigRoot, c);
            }
            else c.Problems.Add("no SeatEyeCompensation in the scene: the seat stays on the eye point"
                + " and the rider cannot be sized");
            // The rider's size follows the ball; RideScaleMath is the one source of the numbers.
            changed |= SetBool(so, "scaleRiderWithBall", true, c);
            changed |= SetFloat(so, "eyeHeightFilterHz", RideScaleMath.DefaultFilterHz, c);
            changed |= SetFloat(so, "eyeHeightStepFraction", RideScaleMath.DefaultStepFraction, c);
            changed |= SetFloat(so, "eyeHeightMinIntervalSeconds",
                RideScaleMath.DefaultMinIntervalSeconds, c);
            if (exitPanel != null) changed |= SetObject(so, "exitPanelRoot", exitPanel, c);
            changed |= SetObject(so, "panelRoot", panels, c);
            if (hint != null) changed |= SetObject(so, "exitHintText", hint, c);
            changed |= AssembleVrInputLink(so, c);

            Transform exitPoint = ReadStationExitPoint(station);
            if (exitPoint == null)
            {
                c.Problems.Add("the VRCStation has no exit location (stationExitPlayerLocation):"
                    + " RequestExit cannot move the landing spot");
            }
            else
            {
                changed |= SetObject(so, "exitPoint", exitPoint, c);
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(station);
        }

        // The VR thumbstick adapter's ride gate follows the seat like the desktop adapter's. The
        // adapter belongs to the hub assembly (HubAssembler.AssembleVrStickInput), which creates
        // it; a scene without one (a lab, a base scene before the hub assembly) is left as it is
        // and noted, never written to null. The All-Maps hub runs this assembly after the hub's,
        // so there it always exists, and it checks the link itself.
        private static bool AssembleVrInputLink(SerializedObject stationSo, Counters c)
        {
            VrStickMoveInput[] all = UnityEngine.Object.FindObjectsOfType<VrStickMoveInput>(true);
            if (all.Length > 1)
            {
                c.Problems.Add("expected at most one VrStickMoveInput, found " + all.Length
                    + "; BallRideStation.vrInput was not wired");
                return false;
            }
            if (all.Length == 0)
            {
                c.Notes.Add("no VrStickMoveInput in the scene (the hub assembly creates it):"
                    + " BallRideStation.vrInput left as it is");
                return false;
            }
            return SetObject(stationSo, "vrInput", all[0], c);
        }

        // The transform that receives the compensation must be the VRCStation's own transform:
        // that is what VRChat carries a seated player with (the mechanism a vehicle station
        // moves its riders by). The station's ENTER location only chooses the pose the player
        // is put at when seating, so it must carry no authored offset relative to the station:
        // such an offset is measured as part of the head offset and then clipped by the
        // horizontal limit, which is the rider's "+0.45 m to the right" defect.
        private static void AssembleSeatLinks(SeatEyeCompensation seatEye, KatamariBallMotor motor,
            BallRideStation station, Transform rigRoot, Counters c)
        {
            Component linked = ReadLinkedStation(station) as Component;
            if (linked == null)
            {
                c.Problems.Add("BallRideStation.station is not wired: the transform that carries"
                    + " the seated player cannot be given to SeatEyeCompensation");
                return;
            }
            Transform carrier = linked.transform;
            Transform enter = ReadStationPoint(station, "stationEnterPlayerLocation");
            Transform exit = ReadStationExitPoint(station);
            if (carrier == rigRoot || !carrier.IsChildOf(rigRoot))
            {
                c.Problems.Add("the VRCStation must be a child of FollowRigRoot: the follow frame"
                    + " carries the rider by moving that station transform");
                return;
            }
            if (enter == null || enter == rigRoot || !enter.IsChildOf(rigRoot) || enter == exit)
            {
                c.Problems.Add("VRCStation needs a distinct entry transform under FollowRigRoot"
                    + " for SeatEyeCompensation");
                return;
            }

            // Both poses are the rig pose. The station carries the player and the entry puts
            // the player at the station, so any remaining offset would be a fixed offset in
            // every measurement and every sized ride.
            AlignPose(carrier, rigRoot, "Align the ride station to the ride frame", c);
            AlignPose(enter, carrier, "Align the station entry to the ride station", c);

            var so = new SerializedObject(seatEye);
            bool changed = SetObject(so, "followRigRoot", rigRoot, c);
            changed |= SetObject(so, "seatAnchor", carrier, c);
            changed |= SetObject(so, "motor", motor, c);
            changed |= SetFloat(so, "verticalLimitMeters",
                SeatEyeMath.MaxPlausibleHeadAboveSeatMeters, c);
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(seatEye);
        }

        private static void AlignPose(Transform target, Transform frame, string what, Counters c)
        {
            if (target.position == frame.position && target.rotation == frame.rotation) return;
            Undo.RecordObject(target, what);
            target.SetPositionAndRotation(frame.position, frame.rotation);
            c.Changed++;
        }

        private static Transform ReadStationExitPoint(BallRideStation station)
        {
            return ReadStationPoint(station, "stationExitPlayerLocation");
        }

        private static Transform ReadStationPoint(BallRideStation station, string field)
        {
            UnityEngine.Object linked = ReadLinkedStation(station);
            return linked == null ? null : ReadTransform(linked, field);
        }

        private static bool SamePose(Transform a, Transform b)
        {
            return a != null && b != null
                && Vector3.Distance(a.position, b.position) <= 1e-5f
                && Quaternion.Angle(a.rotation, b.rotation) <= 1e-3f;
        }

        private static UnityEngine.Object ReadLinkedStation(BallRideStation station)
        {
            SerializedProperty linked = new SerializedObject(station).FindProperty("station");
            return linked == null ? null : linked.objectReferenceValue;
        }

        // The VRCStation needs an exit location for BallRideStation.RequestExit to move: a
        // child object of the station placed where the first landing candidate beside the ball
        // would be on flat ground (RideExitMath, the same rule the exit uses at run time, which
        // moves it again on every exit). Created once; a station that already has one is left
        // alone, so running the assembly again changes nothing.
        private static void AssembleExitLocation(BallRideStation station, Transform ballRoot,
            KatamariBallMotor motor, Counters c)
        {
            UnityEngine.Object linked = ReadLinkedStation(station);
            if (linked == null)
            {
                c.Problems.Add("BallRideStation.station is not wired: there is no VRCStation to give"
                    + " an exit location");
                return;
            }
            var vrcSo = new SerializedObject(linked);
            SerializedProperty exitProperty = vrcSo.FindProperty("stationExitPlayerLocation");
            if (exitProperty == null)
            {
                c.Problems.Add("the VRCStation has no stationExitPlayerLocation field");
                return;
            }
            if (exitProperty.objectReferenceValue != null) return;
            var stationComponent = linked as Component;
            if (stationComponent == null || ballRoot == null)
            {
                c.Problems.Add("cannot place the exit location: no station transform or ball root");
                return;
            }

            float diameter = RideScaleMath.FallbackBallDiameterMeters;
            var motorSo = new SerializedObject(motor);
            SerializedProperty start = motorSo.FindProperty("startDiameterWorldM");
            if (start != null && start.floatValue > 0f) diameter = start.floatValue;
            Vector3 center = ballRoot.position;
            Vector3 origin;
            float length;
            float groundY;
            Vector3 position;
            float yaw;
            if (!RideExitMath.TryExitProbe(center, diameter * 0.5f, 0f, 0, out origin, out length,
                out groundY)
                || !RideExitMath.TryStandPose(new Vector3(origin.x, groundY, origin.z), center,
                    out position, out yaw))
            {
                c.Problems.Add("cannot place the exit location: the ball position is not finite");
                return;
            }

            Transform point = stationComponent.transform.Find(ExitPointName);
            if (point == null)
            {
                var go = new GameObject(ExitPointName);
                Undo.RegisterCreatedObjectUndo(go, "Create " + ExitPointName);
                go.transform.SetParent(stationComponent.transform, true);
                point = go.transform;
                c.Created++;
            }
            point.position = position;
            point.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (SetObject(vrcSo, "stationExitPlayerLocation", point, c))
            {
                vrcSo.ApplyModifiedPropertiesWithoutUndo();
            }
            c.Notes.Add("exit location created at " + position);
        }

        // ------------------------------------------------------------ udon plumbing ---

        private static T AddUdon<T>(GameObject go, Counters c) where T : UdonSharpBehaviour
        {
            T added = VRCKatamari.Editor.Hub.UdonProgramAssets.AddComponent<T>(go, c.Problems, c.Notes);
            if (added != null) c.Created++;
            return added;
        }

        // The click prompt and its reach are fields of the backing UdonBehaviour, not of the
        // U# proxy.
        private static void SetInteractText(UdonSharpBehaviour proxy, string text, float proximity,
            Counters c)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            if (backing == null)
            {
                c.Problems.Add(proxy.GetType().Name + " has no backing UdonBehaviour yet");
                return;
            }
            if (backing.interactText != text || !Mathf.Approximately(backing.proximity, proximity))
            {
                backing.interactText = text;
                backing.proximity = proximity;
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

        // Reads the instance variable table (a non-public field) for the fields this
        // assembly relies on: a missing symbol means "save, reopen, run again".
        private static void VerifyUdonTables(BallRideStation station, Counters c)
        {
            VerifyTable(station, new[] { "motor", "exitPoint", "exitHintText", "exitPanelRoot",
                "ballInteract", "exitHoldSeconds", "exitProbeLayers", "scaleRiderWithBall",
                "eyeHeightFilterHz", "eyeHeightStepFraction", "eyeHeightMinIntervalSeconds",
                "scaleWaitSeconds", "panelRoot", "vrInput" }, c);
            SeatEyeCompensation seatEye = FindOptional<SeatEyeCompensation>();
            if (seatEye != null)
            {
                VerifyTable(seatEye, new[] { "motor", "verticalLimitMeters", "followRigRoot",
                    "seatAnchor" }, c);
                Component linked = ReadLinkedStation(station) as Component;
                Transform carrier = linked != null ? linked.transform : null;
                Transform rig = ReadTransform(seatEye, "followRigRoot");
                Transform anchor = ReadTransform(seatEye, "seatAnchor");
                Transform enter = ReadStationPoint(station, "stationEnterPlayerLocation");
                Transform exitPoint = ReadStationExitPoint(station);
                object anchorValue;
                object rigValue;
                bool wired = carrier != null && rig != null && anchor != null && enter != null
                    && anchor == carrier && carrier != rig && carrier.IsChildOf(rig)
                    && enter != rig && enter != exitPoint && enter.IsChildOf(rig)
                    && SamePose(carrier, rig) && SamePose(enter, carrier);
                if (!wired
                    || !VRCKatamari.Editor.Hub.UdonVariableAccess.TryRead(seatEye, "seatAnchor", out anchorValue)
                    || (anchorValue as Transform) != anchor
                    || !VRCKatamari.Editor.Hub.UdonVariableAccess.TryRead(seatEye, "followRigRoot", out rigValue)
                    || (rigValue as Transform) != rig)
                {
                    c.Problems.Add("SeatEyeCompensation must be wired to the follow frame and to"
                        + " the VRCStation's own transform (the player-carrying transform), with"
                        + " the station and its entry location both on the rig pose, in both the"
                        + " proxy and the Udon table");
                }
            }
            Component stationComponent = ReadLinkedStation(station) as Component;
            if (stationComponent == null)
            {
                c.Problems.Add("BallRideStation.station is not wired: the VRCStation settings"
                    + " cannot be checked");
            }
            else
            {
                var vrcSo = new SerializedObject(stationComponent);
                SerializedProperty seated = vrcSo.FindProperty("seated");
                SerializedProperty mobility = vrcSo.FindProperty("PlayerMobility");
                if (seated == null || mobility == null)
                {
                    c.Problems.Add("the VRCStation has no seated/PlayerMobility field");
                }
                else if (seated.boolValue || mobility.intValue != StationMobilityForVehicle)
                {
                    c.Problems.Add("the VRCStation must be a standing, moving-station station:"
                        + " seated off and PlayerMobility = ImmobilizeForVehicle ("
                        + StationMobilityForVehicle + "), found seated=" + seated.boolValue
                        + " mobility=" + mobility.intValue);
                }
            }
            BallRideExitButton exit = FindOptional<BallRideExitButton>();
            if (exit != null) VerifyTable(exit, new[] { "ride", "label", "confirmSeconds" }, c);
            c.Notes.Add(ReadResultCard(SceneManager.GetActiveScene(), c.Problems));
        }

        /// <summary>
        /// Reads the result card back (HUB_PANEL_PREFAB.md, 结算卡): one instance of
        /// <see cref="ResultCardPrefabPath"/> under the follow frame's panel root, a VRChat
        /// interactable canvas (<c>HubAssembler.BoardCanvasProblem</c>), the presenter and the round
        /// pointing at it, and its two buttons wired as panel buttons
        /// (<c>HubAssembler.PanelButtonProblem</c>: onClick = SendCustomEvent("Press") on the
        /// button's own UdonBehaviour, no collider of their own) with their scene references in
        /// the proxy and the Udon table. No leftover HubResultBoard, collider return button or
        /// second StoryRetryButton. Every finding is one problem; the return value is the summary
        /// line. Shared by this assembly and Check Room Wiring.
        /// </summary>
        internal static string ReadResultCard(Scene scene, List<string> problems)
        {
            int before = problems.Count;
            List<StoryResultPresenter> presenters = InScene<StoryResultPresenter>(scene);
            List<StoryRoundController> rounds = InScene<StoryRoundController>(scene);
            List<HubLevelLauncher> launchers = InScene<HubLevelLauncher>(scene);
            if (presenters.Count != 1 || rounds.Count != 1)
            {
                problems.Add("result card: expected one StoryResultPresenter and one StoryRoundController,"
                    + " found " + presenters.Count + "/" + rounds.Count);
                return "result card NOT checked";
            }
            StoryResultPresenter presenter = presenters[0];
            StoryRoundController round = rounds[0];
            HubLevelLauncher launcher = launchers.Count == 1 ? launchers[0] : null;

            var card = ReadProperty(new SerializedObject(presenter), "resultCardRoot") as GameObject;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResultCardPrefabPath);
            if (card == null)
            {
                problems.Add("result card: StoryResultPresenter.resultCardRoot is not wired");
                return "result card MISSING";
            }
            if (prefab == null || PrefabUtility.GetCorrespondingObjectFromSource(card) != prefab)
                problems.Add("result card: '" + card.name + "' is not an instance of " + ResultCardPrefabPath);
            if (card.transform.parent == null || card.transform.parent.name != PanelRootName)
                problems.Add("result card: '" + card.name + "' is not under " + PanelRootName);
            if (!UdonReferenceIsObject(presenter, "resultCardRoot", card))
                problems.Add("result card: StoryResultPresenter.resultCardRoot differs in the Udon table");
            string canvasProblem = VRCKatamari.Editor.Hub.HubAssembler.BoardCanvasProblem(card);
            if (canvasProblem != null) problems.Add("result card: " + canvasProblem);
            if (card.layer != 0) problems.Add("result card: layer " + card.layer + ", wants Default (0) like the hub boards");

            Transform textT = card.transform.Find(CardTextPath);
            var text = textT != null ? textT.GetComponent<TMPro.TextMeshProUGUI>() : null;
            if (text == null || ReadProperty(new SerializedObject(presenter), "resultText") != text
                || !UdonReferenceIsObject(presenter, "resultText", text))
                problems.Add("result card: StoryResultPresenter.resultText is not the card's " + CardTextPath
                    + " (proxy and Udon table)");
            Transform buttons = card.transform.Find(CardButtonsPath);
            if (buttons == null || ReadProperty(new SerializedObject(round), "retryButtonRoot") != buttons.gameObject
                || !UdonReferenceIsObject(round, "retryButtonRoot", buttons.gameObject))
                problems.Add("result card: StoryRoundController.retryButtonRoot is not the card's "
                    + CardButtonsPath + " (proxy and Udon table)");

            Transform retryT = card.transform.Find(CardRetryPath);
            StoryRetryButton retry = retryT != null ? retryT.GetComponent<StoryRetryButton>() : null;
            if (retry == null)
            {
                problems.Add("result card: " + CardRetryPath + " has no StoryRetryButton");
            }
            else
            {
                CardButtonProblems(retry, StoryRetryButton.Label, problems);
                var so = new SerializedObject(retry);
                if (ReadProperty(so, "controller") != round || !UdonReferenceIs(retry, "controller", round))
                    problems.Add("result card: RetryButton.controller is not the scene's round (proxy and Udon table)");
                if (ReadProperty(so, "launcher") != launcher
                    || (launcher != null && !UdonReferenceIs(retry, "launcher", launcher)))
                    problems.Add("result card: RetryButton.launcher is not the scene's HubLevelLauncher");
            }

            Transform returnT = card.transform.Find(CardReturnPath);
            HubReturnButton back = returnT != null ? returnT.GetComponent<HubReturnButton>() : null;
            if (back == null)
            {
                problems.Add("result card: " + CardReturnPath + " has no HubReturnButton");
            }
            else
            {
                CardButtonProblems(back, HubReturnButton.Label, problems);
                if (launcher != null && (ReadProperty(new SerializedObject(back), "launcher") != launcher
                    || !UdonReferenceIs(back, "launcher", launcher)))
                    problems.Add("result card: ReturnButton.launcher is not the scene's HubLevelLauncher (proxy and Udon table)");
                if (back.gameObject.activeSelf != (launcher != null))
                    problems.Add("result card: ReturnButton is " + (back.gameObject.activeSelf ? "shown" : "hidden")
                        + " but the scene " + (launcher != null ? "has" : "has no") + " hub launcher");
            }

            int retries = InScene<StoryRetryButton>(scene).Count;
            if (retries != 1) problems.Add("result card: expected one StoryRetryButton (the card's), found " + retries);
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == LegacyResultBoardName)
                    problems.Add("result card: the old '" + LegacyResultBoardName + "' is still in the scene");
            foreach (HubReturnButton b in InScene<HubReturnButton>(scene))
                if (b.name == LegacyCardReturnName)
                    problems.Add("result card: the old collider button '" + LegacyCardReturnName + "' is still in the scene");

            int found = problems.Count - before;
            return "result card " + (found == 0 ? "ok" : found + " problem(s)") + ": retry "
                + (retry != null ? "wired" : "MISSING") + ", return "
                + (back == null ? "MISSING" : back.gameObject.activeSelf ? "wired" : "hidden (no hub)");
        }

        // A card button is a panel button (one onClick -> SendCustomEvent("Press"), no collider)
        // whose TextMeshPro label shows the behaviour's own word.
        private static void CardButtonProblems(UdonSharpBehaviour button, string label, List<string> problems)
        {
            string problem = VRCKatamari.Editor.Hub.HubAssembler.PanelButtonProblem(button);
            if (problem != null) problems.Add("result card: " + button.name + ": " + problem);
            var text = button.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (text == null || text.text != label)
                problems.Add("result card: " + button.name + "'s label is '" + (text == null ? "(none)" : text.text)
                    + "', wants '" + label + "'");
        }

        // Reads the instance variable table back through the one shared reader
        // (Hub/UdonVariableAccess): a missing symbol means "save, reopen, run again", and a
        // proxy with no backing behaviour is a problem, not a skip.
        private static void VerifyTable(UdonSharpBehaviour proxy, string[] names, Counters c)
        {
            List<string> missing = VRCKatamari.Editor.Hub.UdonVariableAccess.MissingNames(proxy, names);
            if (missing == null)
            {
                c.Problems.Add(proxy.GetType().Name + " has no backing UdonBehaviour, so its Udon"
                    + " variables could not be checked");
                return;
            }
            if (missing.Count > 0)
            {
                c.Problems.Add(proxy.GetType().Name + "'s Udon variable table lacks: "
                    + string.Join(", ", missing.ToArray()) + ". Save the scene, reopen it and"
                    + " run the assembly again");
            }
        }

        // -------------------------------------------------------------- ui plumbing ---

        private static GameObject CreateCanvas(string name, Transform parent, Vector2 size,
            Vector2 pivot, float scale, float azimuth, float elevation, Counters c)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            go.transform.SetParent(parent, false);
            PlacePanel((RectTransform)go.transform, size, pivot, scale, azimuth, elevation);
            c.Created++;
            return go;
        }

        private static void PlacePanel(RectTransform rect, Vector2 size, Vector2 pivot,
            float scale, float azimuth, float elevation)
        {
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.localPosition = RideViewMath.HudLocalPosition(PanelDistance, azimuth, elevation);
            rect.localRotation = RideViewMath.HudLocalRotation(azimuth, elevation);
            rect.localScale = new Vector3(scale, scale, scale);
        }

        private static GameObject CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Text CreateText(string name, Transform parent, Font font, int size,
            TextAnchor alignment)
        {
            GameObject go = CreateRect(name, parent);
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

        private static void Reparent(GameObject child, Transform parent, Counters c, bool first)
        {
            if (child == null || child.transform.parent == parent) return;
            Undo.SetTransformParent(child.transform, parent, "Move onto the rider's card");
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            if (first) child.transform.SetAsFirstSibling();
            c.Changed++;
        }

        // ---------------------------------------------------------- serialized access ---

        private static bool SetFloat(SerializedObject so, string field, float value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
            {
                c.Problems.Add(so.targetObject.GetType().Name + "." + field + " is not a serialized field");
                return false;
            }
            if (Mathf.Approximately(property.floatValue, value)) return false;
            property.floatValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetBool(SerializedObject so, string field, bool value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
            {
                c.Problems.Add(so.targetObject.GetType().Name + "." + field + " is not a serialized field");
                return false;
            }
            if (property.boolValue == value) return false;
            property.boolValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetInt(SerializedObject so, string field, int value, Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
            {
                c.Problems.Add(so.targetObject.GetType().Name + "." + field + " is not a serialized field");
                return false;
            }
            if (property.intValue == value) return false;
            property.intValue = value;
            c.Changed++;
            return true;
        }

        private static bool SetObject(SerializedObject so, string field, UnityEngine.Object value,
            Counters c)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
            {
                c.Problems.Add(so.targetObject.GetType().Name + "." + field + " is not a serialized field");
                return false;
            }
            if (property.objectReferenceValue == value) return false;
            property.objectReferenceValue = value;
            c.Changed++;
            return true;
        }

        private static T ReadObject<T>(UnityEngine.Object owner, string field)
            where T : UnityEngine.Object
        {
            var so = new SerializedObject(owner);
            SerializedProperty property = so.FindProperty(field);
            return property == null ? null : property.objectReferenceValue as T;
        }

        private static Transform ReadTransform(UnityEngine.Object owner, string field)
        {
            return ReadObject<Transform>(owner, field);
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
            string text = "[ride-ux] " + headline + ": +" + c.Created + " ~" + c.Changed
                + " problems=" + c.Problems.Count;
            for (int i = 0; i < c.Notes.Count; i++) text += "\n  note: " + c.Notes[i];
            for (int i = 0; i < c.Problems.Count; i++) text += "\n  PROBLEM: " + c.Problems[i];
            return text;
        }
    }
}
