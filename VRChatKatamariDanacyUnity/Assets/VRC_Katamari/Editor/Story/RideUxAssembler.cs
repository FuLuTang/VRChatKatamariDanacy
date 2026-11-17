using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRCKatamari.BallMotor;
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
    /// from <see cref="RideViewMath"/> (the one source; the bases are zero), so the ball sits
    /// 18 degrees below the horizon at every size and fills a third of the view.</description></item>
    /// <item><description><c>BallSeatTrigger</c> under the ball's physics root: a trigger
    /// unit sphere with <see cref="BallSeatInteract"/>, sized by that behaviour at runtime.
    /// The seat's own 0.45 m click box (at the rider's eye, not on the ball) is removed.</description></item>
    /// <item><description>The HUD canvas (<c>StoryHud</c>) turned into a world-space panel
    /// under the follow frame. A screen-space overlay canvas is not drawn by the VRChat
    /// client.</description></item>
    /// <item><description><c>RideResultCard</c>: a world-space card under the follow frame
    /// that takes the result text, the card panel and the retry button; the existing
    /// <see cref="StoryRetryButton"/> gets a collider inside the retry button so a click can
    /// reach it. Skipped (and reported) when those objects already live on another
    /// world-space canvas. The hub's "back to the hub" button is a child of the retry button
    /// (HubAssembler), so it moves with it.</description></item>
    /// <item><description>The rider's size: <c>BallRideStation.scaleRiderWithBall</c> is
    /// switched on and its filter and rate-limit fields are written from
    /// <see cref="RideScaleMath"/> (the avatar's measured hips level with the top of the ball,
    /// at every ball size), and <c>SeatEyeCompensation.motor</c> is wired to the motor whose
    /// ball top the seat places the hips on.</description></item>
    /// <item><description>The VRCStation's exit location (<c>stationExitPlayerLocation</c>),
    /// created as a child <c>RideExitPoint</c> when the station has none, placed by
    /// <see cref="RideExitMath"/> beside the ball on flat ground; the station moves it again on
    /// every exit.</description></item>
    /// <item><description><c>RideExitPanel</c>: the exit button (press twice) and the hold-jump
    /// hint, active only while seated, wired to the station.</description></item>
    /// </list>
    /// <para>
    /// Panels hang on the follow frame, not on the seat or the ball: the frame is the eye
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
        public const string ExitPanelName = "RideExitPanel";

        // Layout: distance from the eye, azimuth to the right, elevation above the horizon,
        // canvas scale (metres per canvas unit) and the canvas size in units. 0.0014 puts a
        // 30-unit glyph at 4.2 cm, about 1.5 degrees at 1.6 m, which is legible in a headset.
        private const float PanelDistance = 1.6f;
        private const float PanelScale = 0.0014f;

        private const float HudAzimuth = -30f;
        private const float HudElevation = 26f;
        private static readonly Vector2 HudSize = new Vector2(640f, 280f);

        private const float CardAzimuth = 0f;
        private const float CardElevation = 6f;
        private const float CardScale = 0.0016f;
        private static readonly Vector2 CardSize = new Vector2(720f, 360f);

        private const float ExitAzimuth = 24f;
        private const float ExitElevation = -8f;
        private static readonly Vector2 ExitSize = new Vector2(360f, 150f);
        private const float ExitPlateHeight = 90f;

        private const float InteractProximity = 5f;
        private const float BallInteractProximity = 4f;

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
            var c = new Counters();

            KatamariBallMotor motor = Single<KatamariBallMotor>(c);
            BallRideStation station = Single<BallRideStation>(c);
            StoryRoundController round = Single<StoryRoundController>(c);
            if (motor == null || station == null || round == null)
            {
                return Finish(c, "stopped: the scene needs exactly one motor, one ride station"
                    + " and one round controller");
            }
            StoryResultPresenter presenter = FindOptional<StoryResultPresenter>();
            StoryRetryButton retry = FindOptional<StoryRetryButton>();
            BallRideExitButton exit = FindOptional<BallRideExitButton>();

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
            BallSeatInteract ballInteract = ballRoot != null
                ? AssembleSeatTrigger(ballRoot, station, motor, c) : null;
            RemoveSeatBox(station, c);

            Font font = null;
            Text targetText = ReadObject<Text>(round, "targetText");
            if (targetText != null) font = targetText.font;

            if (rigRoot != null)
            {
                AssembleHud(targetText, rigRoot, c);
                GameObject card = AssembleResultCard(round, presenter, retry, font, rigRoot, c);
                GameObject panel = AssembleExitPanel(station, exit, font, rigRoot, c,
                    out Text hint);
                AssembleExitLocation(station, ballRoot, motor, c);
                AssembleStationLinks(station, motor, ballInteract, panel, hint, c);
            }

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
            // The 20 m ball (r = 10 m) needs 17.8 m of lift and 54.8 m of back-off.
            changed |= SetFloat(so, "eyeUpMaxMeters", 128f, c);
            changed |= SetFloat(so, "eyeMaxBackM", 128f, c);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                c.Proxies.Add(motor);
                c.Notes.Add("eye rule written to the motor (depression "
                    + RideViewMath.DepressionDegrees + " deg)");
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

        private static void AssembleHud(Text anyHudText, Transform rigRoot, Counters c)
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
            bool alreadyOurs = canvas.renderMode == RenderMode.WorldSpace
                && canvas.transform.parent == rigRoot;
            if (alreadyOurs) return;

            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvas.transform;
            Undo.SetTransformParent(rect, rigRoot, "Hang the HUD on the follow frame");
            PlacePanel(rect, HudSize, new Vector2(0f, 1f), PanelScale, HudAzimuth, HudElevation);
            c.Changed++;
            c.Notes.Add("HUD canvas '" + canvas.name + "' is world-space under the follow frame");
        }

        // --------------------------------------------------------------- result card ---

        private static GameObject AssembleResultCard(StoryRoundController round,
            StoryResultPresenter presenter, StoryRetryButton retry, Font font,
            Transform rigRoot, Counters c)
        {
            Text resultText = presenter != null ? ReadObject<Text>(presenter, "resultText") : null;
            GameObject oldCard = presenter != null
                ? ReadObject<GameObject>(presenter, "resultCardRoot") : null;
            GameObject retryRoot = ReadObject<GameObject>(round, "retryButtonRoot");

            Transform existing = rigRoot.Find(ResultCardName);
            GameObject card;
            if (existing != null)
            {
                card = existing.gameObject;
            }
            else
            {
                if (WorldCanvasOwnsCard(resultText, retryRoot))
                {
                    c.Notes.Add("result card and retry button already live on a world-space"
                        + " canvas elsewhere; left in place (the retry collider is still added)");
                    AssembleRetryCollider(retry, retryRoot, round, c);
                    return null;
                }
                card = CreateCanvas(ResultCardName, rigRoot, CardSize, new Vector2(0.5f, 0.5f),
                    CardScale, CardAzimuth, CardElevation, c);
                Reparent(oldCard, card.transform, c, true);
                Reparent(resultText != null ? resultText.gameObject : null, card.transform, c,
                    false);
                Reparent(retryRoot, card.transform, c, false);
                LayoutCard(oldCard, resultText, retryRoot, card);
                card.SetActive(false);
                c.Notes.Add("result card canvas created and starts hidden (the presenter shows it)");
            }

            if (presenter != null)
            {
                var so = new SerializedObject(presenter);
                if (SetObject(so, "resultCardRoot", card, c))
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    c.Proxies.Add(presenter);
                }
            }
            AssembleRetryCollider(retry, retryRoot, round, c);
            return card;
        }

        private static bool WorldCanvasOwnsCard(Text resultText, GameObject retryRoot)
        {
            Canvas canvas = null;
            if (resultText != null) canvas = resultText.GetComponentInParent<Canvas>();
            if (canvas == null && retryRoot != null) canvas = retryRoot.GetComponentInParent<Canvas>();
            return canvas != null && canvas.renderMode == RenderMode.WorldSpace;
        }

        private static void LayoutCard(GameObject oldCard, Text resultText, GameObject retryRoot,
            GameObject card)
        {
            if (oldCard != null)
            {
                var r = oldCard.GetComponent<RectTransform>();
                if (r != null)
                {
                    r.anchorMin = Vector2.zero;
                    r.anchorMax = Vector2.one;
                    r.offsetMin = Vector2.zero;
                    r.offsetMax = Vector2.zero;
                    r.SetAsFirstSibling();
                }
            }
            else
            {
                Image back = card.GetComponent<Image>();
                if (back == null) back = card.AddComponent<Image>();
                back.color = new Color(0f, 0f, 0f, 0.6f);
                back.raycastTarget = false;
            }
            if (resultText != null)
            {
                var r = resultText.rectTransform;
                r.anchorMin = new Vector2(0f, 1f);
                r.anchorMax = new Vector2(1f, 1f);
                r.pivot = new Vector2(0.5f, 1f);
                r.anchoredPosition = new Vector2(0f, -16f);
                r.sizeDelta = new Vector2(-48f, 230f);
            }
            if (retryRoot != null)
            {
                var r = retryRoot.GetComponent<RectTransform>();
                if (r != null)
                {
                    // Retry sits left of centre with the hub's "back to the hub" button on its
                    // right; the layout is one function shared with the hub assembly, so the
                    // card looks the same whichever assembler ran first. Centre pivot, so the
                    // collider added below sits on the button's face with a zero centre.
                    VRCKatamari.Editor.Hub.HubAssembler.LayoutRetryForReturn(r);
                }
            }
        }

        // The press target: the existing retry behaviour moves inside the retry button (so
        // the controller's SetActive on the button also switches the collider) and gets a
        // trigger box the size of the button's face.
        private static void AssembleRetryCollider(StoryRetryButton retry, GameObject retryRoot,
            StoryRoundController round, Counters c)
        {
            if (retry == null)
            {
                c.Problems.Add("no StoryRetryButton in the scene: the result card cannot be pressed");
                return;
            }
            Transform target = retryRoot != null ? retryRoot.transform : null;
            if (target != null && retry.transform.parent != target)
            {
                Undo.SetTransformParent(retry.transform, target, "Move the retry behaviour onto the button");
                retry.transform.localPosition = Vector3.zero;
                retry.transform.localRotation = Quaternion.identity;
                retry.transform.localScale = Vector3.one;
                c.Changed++;
            }
            BoxCollider box = retry.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = retry.gameObject.AddComponent<BoxCollider>();
                c.Created++;
            }
            Vector2 size = VRCKatamari.Editor.Hub.HubAssembler.CardButtonSize;
            if (retryRoot != null)
            {
                var r = retryRoot.GetComponent<RectTransform>();
                if (r != null && r.rect.width > 1f) size = r.rect.size;
            }
            Vector3 wanted = new Vector3(size.x, size.y, 40f);
            if (!box.isTrigger || box.size != wanted || box.center != Vector3.zero)
            {
                box.isTrigger = true;
                box.size = wanted;
                box.center = Vector3.zero;
                c.Changed++;
            }
            var so = new SerializedObject(retry);
            if (SetObject(so, "controller", round, c)) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(retry);
            SetInteractText(retry, "再试一次", InteractProximity, c);
        }

        // ---------------------------------------------------------------- exit panel ---

        private static GameObject AssembleExitPanel(BallRideStation station,
            BallRideExitButton exit, Font font, Transform rigRoot, Counters c, out Text hint)
        {
            hint = null;
            Transform existing = rigRoot.Find(ExitPanelName);
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
                panel = CreateCanvas(ExitPanelName, rigRoot, ExitSize, new Vector2(0.5f, 0.5f),
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
            Text hint, Counters c)
        {
            var so = new SerializedObject(station);
            bool changed = SetObject(so, "motor", motor, c);
            if (ballInteract != null) changed |= SetObject(so, "ballInteract", ballInteract, c);
            SeatEyeCompensation seatEye = FindOptional<SeatEyeCompensation>();
            if (seatEye != null)
            {
                changed |= SetObject(so, "seatEye", seatEye, c);
                AssembleSeatLinks(seatEye, motor, c);
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
            if (hint != null) changed |= SetObject(so, "exitHintText", hint, c);

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

        // The seat places the rider's hips on the ball's top, so it needs the ball.
        private static void AssembleSeatLinks(SeatEyeCompensation seatEye, KatamariBallMotor motor,
            Counters c)
        {
            var so = new SerializedObject(seatEye);
            if (SetObject(so, "motor", motor, c)) so.ApplyModifiedPropertiesWithoutUndo();
            c.Proxies.Add(seatEye);
        }

        private static Transform ReadStationExitPoint(BallRideStation station)
        {
            UnityEngine.Object linked = ReadLinkedStation(station);
            if (linked == null) return null;
            SerializedProperty exitProperty =
                new SerializedObject(linked).FindProperty("stationExitPlayerLocation");
            if (exitProperty == null) return null;
            return exitProperty.objectReferenceValue as Transform;
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
            try
            {
                T added = UdonSharpUndo.AddComponent<T>(go);
                c.Created++;
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
                "scaleWaitSeconds" }, c);
            SeatEyeCompensation seatEye = FindOptional<SeatEyeCompensation>();
            if (seatEye != null) VerifyTable(seatEye, new[] { "motor" }, c);
            BallRideExitButton exit = FindOptional<BallRideExitButton>();
            if (exit != null) VerifyTable(exit, new[] { "ride", "label", "confirmSeconds" }, c);
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
