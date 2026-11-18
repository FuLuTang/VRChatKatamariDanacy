using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;
using VRCKatamari.Input;
using VRCKatamari.Story;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Seats the local player in the ball's station and opens the ride gate while
    /// they are sitting there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Object layout is not a style choice, it is what each event needs. Interact is
    /// delivered to the behaviour on the GameObject of the collider that was hit,
    /// and VRChat delivers OnStationEntered/OnStationExited to the behaviours on the
    /// station's own GameObject. Both halves therefore have to share one object:
    /// the VRCStation, the interact collider and this behaviour. That is exactly the
    /// layout the StationPlatform lab used and PC-tested - its <c>Station</c> object
    /// carries the VRCStation, the BoxCollider and <c>StationLabDriver</c>
    /// (Labs/StationPlatform/StationPlatformLab.unity:198 station, :277 collider,
    /// :157 driver). That station object is a child of the ball's follow-rig root,
    /// so the seated player is carried by the motor's rig frame instead of by this
    /// script. Sharing an object is legal and this behaviour requires it; the project
    /// convention of one behaviour per object covers the others (clear ownership, a
    /// readable self-check), and the engine does not disable a second behaviour on
    /// the same object (Docs/Design/BallMotor/README.md, assembly constraints).
    /// </para>
    /// <para>
    /// The ride gate exists because <see cref="DesktopMoveInput"/> converts any local
    /// move axis into ball axes regardless of who is standing where. This behaviour is
    /// the only thing that knows whether the player is riding, so it owns
    /// <c>SetRideGate</c>: closed from Start until the local player is seated, and
    /// closed again on exit. Without it a player standing next to the ball could steer
    /// it with WASD. It is one of two gates and owns only this one - the round
    /// controller owns the other (DesktopMoveInput.SetRoundGate) - so neither owner
    /// can re-open the other's gate, and leaving the seat cannot re-enable input after
    /// the round has ended.
    /// </para>
    /// <para>
    /// The start of a round. Sitting down is the start signal: this behaviour is the
    /// only place that knows the local player just took the controls, so it is the only
    /// sensible caller of the round controller's <c>NotifyRiderSeated</c>. The order is
    /// "open the ride gate, then notify", so the sit-down frame already has both gates
    /// open and the player can drive at once; the controller itself decides what the
    /// call means (it starts the round only from Idle and ignores it otherwise). The
    /// link is optional and one-way - this behaviour never reads round state and works
    /// with no controller at all, and leaving the seat tells the controller nothing
    /// because a round keeps running when the player gets off (the round controller's
    /// own notes say why).
    /// </para>
    /// <para>
    /// The prompt: <c>DisableInteractive = true</c> while seated is how the lab
    /// removed the "sit" outline while the crosshair still pointed at the vehicle
    /// (Docs/Evidence/StationPlatform/README.md, D1 PASS on PC; API basis in
    /// fix_seated_prompt.md). The sphere the player clicks to sit is
    /// <see cref="BallSeatInteract"/>, on the ball; this behaviour only turns its prompt
    /// off while seated.
    /// </para>
    /// <para>
    /// Leaving. Disable Station Exit stays on: the move axes drive the ball, so letting
    /// movement or a stray jump stand the player up would end the ride by accident. The way
    /// out is deliberate instead, and every route ends in <see cref="RequestExit"/>: hold
    /// the jump input for <c>exitHoldSeconds</c> (desktop Space, VR whatever the player
    /// bound to Jump; it is the input the lab proved is delivered while seated), or press
    /// <see cref="BallRideExitButton"/> twice on the rider's own panel. <c>RequestExit</c>
    /// first moves the station's exit location onto walkable ground beside the ball
    /// (<see cref="RideExitMath"/>), because the seat itself is under the terrain.
    /// </para>
    /// <para>
    /// Local-only state (<c>BehaviourSyncMode.None</c>): who is seated is the
    /// station's own network state, and this slice is single player. Nothing here is
    /// synced, so a remote client would still see its own prompt - out of scope.
    /// </para>
    /// <para>
    /// Seat height. The follow frame computes where the viewer's EYE should be, and the
    /// station carries the avatar's ROOT, so a seat placed on the eye point puts the real
    /// camera a seated eye height higher - the "viewpoint too high" defect. The correction
    /// belongs to this layer, not to the motor: <see cref="SeatEyeCompensation"/> measures
    /// the local head above the seat once per ride and moves the seat by that much, and this
    /// behaviour is the only caller because it is the only place that knows the local player
    /// sat down. The reference is optional: unwired, the station behaves exactly as before
    /// (seat on the eye point) and says so once in the log.
    /// </para>
    /// <para>
    /// Rider size. With <c>scaleRiderWithBall</c> the avatar's eye height follows the ball's
    /// semantic diameter in the push stance (<see cref="RideViewMath"/>): standing behind the
    /// ball, feet on the ground, the ball's back surface where the player's real hands rest when
    /// pushing. Sequence: on taking the station the original eye height is read and kept, and the
    /// seat is measured once at it; the measurement yields <c>eta</c> (the head's height above
    /// the anchor per metre of eye height, <see cref="RideStanceMath"/>). Without a usable
    /// stance (<see cref="SeatEyeCompensation.HasStance"/> false: a seated pose, an odd datum)
    /// the size still follows the ball with <c>eta = 1</c>, so the hands keep their reach, and the
    /// seat only aligns the eye. Each frame the requested height is low-passed and rate limited
    /// (a request only when it moved by half a percent, at most ten times a second) and the seat
    /// is moved to match without measuring again (<see cref="SeatEyeCompensation.ApplyEyeHeight"/>).
    /// VRChat says Udon must apply a size again whenever the eye height changes, so an eye-height
    /// event whose size is not the one sent is answered by sending it again (at most three times
    /// in a row, then the seat is measured as it is). Requests the avatar does not follow for
    /// about three seconds (avatar scaling off on the website) are abandoned and the seat is
    /// measured as it is. The original eye height is restored on every exit path.
    /// </para>
    /// <para>
    /// Panels. The rider's panels (HUD, result card, exit panel) hang under
    /// <c>panelRoot</c> on the follow frame; every size sent scales that root by
    /// <see cref="RideViewMath.PanelScaleForEyeHeight"/>, so the panels keep their angle and
    /// distance relative to the rider's eyes, and leaving puts the scale back to one.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallRideStation : UdonSharpBehaviour
    {
        // Both live on this GameObject: the station provides the seat and the
        // station callbacks, the collider provides the click.
        [SerializeField] private VRC.SDK3.Components.VRCStation station;

        // The local player's device-to-axis adapter, on its own object.
        [SerializeField] private DesktopMoveInput input;

        // Optional. The VR twin-stick adapter (created by the hub assembly); its ride gate
        // follows the seat exactly like the desktop adapter's.
        [SerializeField] private VrStickMoveInput vrInput;

        // Optional. The story round this seat starts: the first local sit-down in Idle
        // calls NotifyRiderSeated, which begins the clock. Left empty the station still
        // seats, gates input and prompts exactly as before (the StationPlatform lab has
        // no round controller). A scene that has a StoryRoundController must wire this:
        // that controller waits in Idle with its own gate closed until a sit-down
        // arrives, so an unwired station means a round that never starts.
        [SerializeField] private StoryRoundController roundController;

        // Optional. Measures the local head above the seat and offsets the seat so the EYE
        // lands on the follow frame's eye point. Unwired is legal (the seat stays on the eye
        // point, the pre-fix behaviour) and is announced once in Start.
        [SerializeField] private SeatEyeCompensation seatEye;

        // Optional. The ball's click sphere; its prompt is switched off while seated.
        [SerializeField] private BallSeatInteract ballInteract;

        // Optional but required for a safe exit: the ball the landing spot is measured
        // from, and the transform the VRCStation uses as its exit location (the
        // station's stationExitPlayerLocation). Unwired, RequestExit still exits and the
        // authored exit location is used as it is.
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Transform exitPoint;

        // Optional. The panel line that says how to leave and shows the hold progress.
        [SerializeField] private Text exitHintText;

        // Optional. The rider's exit panel (the exit button and the hint line): active only
        // while the local player is seated, so a standing player is not offered a way out.
        [SerializeField] private GameObject exitPanelRoot;

        // Optional. The parent of every rider panel, a child of the follow frame at the eye
        // point; scaled with the rider so the panels stay in front of the rider's eyes.
        [SerializeField] private Transform panelRoot;

        // Whether the rider's size follows the ball while seated (RideScaleMath, RIDE_GEOMETRY.md
        // section 3): the avatar's eye height is set to the push stance's value for the ball,
        // kept there as the ball grows, and put back on leaving. False
        // leaves the player's size alone (the one switch for a world that cannot scale avatars).
        [SerializeField] private bool scaleRiderWithBall = false;

        // The requested eye height is low-passed at this rate (the follow frame's height
        // filter, so the rider and the eye point grow together), hertz.
        [SerializeField] private float eyeHeightFilterHz = 5f;

        // The debounce: a new eye height is sent only when it differs from the last one sent by
        // this fraction, and never more often than the interval. Every send makes VRChat raise an
        // avatar-eye-height event and rescales the avatar.
        [SerializeField] private float eyeHeightStepFraction = 0.005f;
        [SerializeField] private float eyeHeightMinIntervalSeconds = 0.1f;

        // How long to wait for VRChat to announce a re-applied eye height before measuring the
        // seat anyway (a world with avatar scaling switched off on the website ignores the
        // request and never announces anything), seconds.
        [SerializeField] private float scaleWaitSeconds = 2f;

        // How long the jump input must be held to leave, seconds. Long enough that a
        // tap (a future item use shares the input) cannot stand the rider up.
        [SerializeField] private float exitHoldSeconds = 1.5f;

        // Layers the landing probe may hit; -5 is Unity's default raycast mask.
        [SerializeField] private int exitProbeLayers = -5;

        private bool seated;
        private bool exitPending;
        private float exitRequestedAt;

        // An exit request whose OnStationExited has not arrived after this long is sent again.
        private const float ExitRetrySeconds = 1f;
        private bool jumpHeld;
        private float jumpHeldSince;
        private int lastHintPercent = -1;
        private float savedEyeHeight;
        private bool scaleApplied;
        private bool scalePending;
        private int scaleReapplyCount;
        private float scalePendingSince;
        private float eyeHeightSent;
        private float eyeHeightSmooth;
        private float lastSendTime;
        private float stanceEta;
        private bool scaleCalibrated;
        private bool scaleGivenUp;
        private int scaleMismatchChecks;
        private float scaleNextCheck;
        private float lastLoggedEyeHeight;

        private const string ExitHintIdle = "按住跳跃键下车";

        private void Start()
        {
            // Start runs before the local player can sit, so this always closes the
            // ride gate: a standing player must not be able to steer the ball.
            SetRideGate(false);
            SetExitPanel(false);
            if (seatEye == null)
            {
                // One line, once: an unwired correction looks exactly like "the fix does
                // nothing", which is the same trap the input gate had.
                Debug.Log("[ride] no seat-eye compensation wired: the seat stays on the eye point");
            }
        }

        public override void Interact()
        {
            if (seated) return;
            TrySeatLocalPlayer();
        }

        /// <summary>
        /// Seats the local player without a click: the hub's "walk in, teleport, sit"
        /// flow calls this after it has measured the arrival at the ball.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the same <c>VRCStation.UseStation</c> path <see cref="Interact"/> uses,
        /// so the click and the room flow cannot diverge: <c>OnStationEntered</c> is still
        /// the only place that sets <c>seated</c>, opens the ride gate, tells the round
        /// controller and starts the seat-eye measurement. Seating by script is not a
        /// second seat implementation, it is the same station API with a different caller.
        /// </para>
        /// <para>
        /// Refusals are logged and answered false, never thrown. There is no distance
        /// check on purpose: <c>UseStation</c> is a script API, not an Interact ray, so
        /// "the player is not looking at the station" is not a reason to refuse. The
        /// station object must be active, because an inactive station cannot carry an
        /// avatar.
        /// </para>
        /// <para>
        /// The caller still has to verify the result through
        /// <see cref="IsLocalPlayerSeated"/>: <c>UseStation</c> is asynchronous, and a
        /// client that does not support it (notably an environment without a station
        /// implementation) leaves the player standing. That is a logged false, not a
        /// thrown error.
        /// </para>
        /// </remarks>
        public bool TrySeatLocalPlayer()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                Debug.LogWarning("[ride] seat request refused: no local player");
                return false;
            }
            if (station == null)
            {
                Debug.LogWarning("[ride] seat request refused: no VRCStation is wired");
                return false;
            }
            if (!station.gameObject.activeSelf)
            {
                Debug.LogWarning("[ride] seat request refused: the station object is inactive");
                return false;
            }
            if (seated) return true;

            station.UseStation(player);
            Debug.Log("[ride] seat requested without a click frame=" + Time.frameCount);
            return true;
        }

        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            seated = true;
            exitPending = false;
            DisableInteractive = true;
            if (ballInteract != null) ballInteract.SetInteractable(false);
            ClearJumpHold();
            SetExitPanel(true);
            WriteExitHint(ExitHintIdle);
            SetRideGate(true);
            // After the ride gate: the round controller opens its own gate in BeginRound,
            // and the input condition needs both open to be true on the sit-down frame.
            // In any state but Idle the controller ignores the call, so sitting down
            // after the round ended or mid-round cannot restart it.
            if (roundController != null) roundController.NotifyRiderSeated();
            // Measure first, then size: the size comes from the measurement (eta), taken at the
            // eye height the avatar has.
            BeginRideScale(player);
            StartSeatMeasurement(player);
            Debug.Log("[ride] seated entered");
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            // Another exit path may already have dropped the local seated state, so the
            // cleanup below runs unconditionally and the log reports what it found.
            bool wasSeated = seated;
            seated = false;
            exitPending = false;
            DisableInteractive = false;
            if (ballInteract != null) ballInteract.SetInteractable(true);
            ClearJumpHold();
            WriteExitHint("");
            SetExitPanel(false);
            SetRideGate(false);
            if (seatEye != null) seatEye.EndSeated();
            EndRideScale(player);
            // Unconditional and reason-coded. It used to log only when `wasSeated`, and that
            // asymmetry made a real incident unattributable: a ClientSim exit left no line
            // here at all, and "this callback never ran" was indistinguishable from "another
            // path had already cleared the flag". One line per exit, and it names the links
            // it just used, so an unwired field is visible in the same line as the exit.
            Debug.Log("[ride] station exit reason=station-exit wasSeated=" + wasSeated
                + " inputWired=" + (input != null) + " vrInputWired=" + (vrInput != null) + " seatEyeWired=" + (seatEye != null));
        }

        // A respawn while riding only ASKS to leave: the seated state, the seat, the size and
        // the panels are cleaned up in OnStationExited, the one place that knows the station
        // really let go, so IsLocalPlayerSeated never says false while the avatar is still in the
        // station. A respawn can arrive without the exit callback following at once, so
        // IsExitPending stays true and Update asks again (ExitRetrySeconds) until it arrives.
        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            if (!seated || station == null) return;
            ClearJumpHold();
            BeginExit(player, "respawn");
        }

        /// <summary>
        /// A new avatar is a new seated pose, so the seat offset measured for the previous
        /// avatar is wrong: measure again while seated, ignore it while standing.
        /// </summary>
        public override void OnAvatarChanged(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            if (!seated) return;
            // A new avatar is a new body: eta is measured again, at whatever size it has.
            scaleReapplyCount = 0;
            scalePending = false;
            StartSeatMeasurement(player);
        }

        /// <summary>
        /// Avatar scaling changes the avatar's proportions the same way a new avatar does,
        /// and this event fires for scaling as well as for a switch. The station's own size
        /// requests come back through here too and must not restart the seat measurement.
        /// </summary>
        public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float previousEyeHeight)
        {
            if (player == null || !player.isLocal) return;
            if (!seated) return;
            if (scaleApplied && scaleCalibrated && !scaleGivenUp)
            {
                // A size is being held. Within tolerance of what was last sent: our own change, or
                // the one we were waiting for. Anything else (another avatar, the player's own dial)
                // is answered by applying ours again, as VRChat's avatar-scaling page requires.
                int kind = RideScaleMath.ClassifyEyeHeightEvent(player.GetAvatarEyeHeightAsMeters(),
                    eyeHeightSent, scaleReapplyCount);
                if (kind == RideScaleMath.EventOurs)
                {
                    scaleReapplyCount = 0;
                    if (!scalePending) return;
                    scalePending = false;
                    StartSeatMeasurement(player);
                    return;
                }
                if (kind == RideScaleMath.EventReapply)
                {
                    ReapplyRideScale(player);
                    return;
                }
            }
            // No size to hold yet (measuring) or one that cannot be held: measure the seat again
            // at the size the avatar has now.
            scalePending = false;
            StartSeatMeasurement(player);
        }

        /// <summary>
        /// Whether the local player is currently sitting in this ball's station.
        /// Read by the seat-aware lab probe; gameplay code should not need it.
        /// </summary>
        public bool IsLocalPlayerSeated()
        {
            return seated;
        }

        /// <summary>
        /// Whether an Interact press aimed at the rider's panel should be DROPPED because the
        /// local trigger is charging, or has just fired, the dash (owner 2026-11-18).
        /// </summary>
        /// <remarks>
        /// The VR trigger raises <c>InputUse</c> (the dash charge) and <c>Interact</c> (the
        /// pointed-at object) from the same press, so the rider's panel buttons would fire
        /// while the player charges. The panel buttons call this before acting; the rule
        /// itself is <see cref="RideDashInteractMath"/>, and an unwired motor leaves the
        /// button behaving as before.
        /// </remarks>
        public bool ShouldIgnoreDashInteract()
        {
            if (motor == null) return false;
            return RideDashInteractMath.ShouldIgnoreInteract(seated, motor.IsDashCharging(),
                motor.IsDashing());
        }

        /// <summary>
        /// True from an exit request (held jump, the panel button, a respawn) until
        /// <c>OnStationExited</c> arrives for the local player. A caller that must act after
        /// the player has really left the station (the hub's teleport) waits for
        /// <see cref="IsLocalPlayerSeated"/> false; this says a departure is under way.
        /// </summary>
        public bool IsExitPending()
        {
            return exitPending;
        }

        /// <summary>
        /// Jump while seated: held long enough, it leaves the seat (see <see cref="Update"/>).
        /// Delivered with <c>seated</c> true because Disable Station Exit does not stop
        /// input events (Docs/Evidence/StationPlatform/README.md: Jump counted 7 times,
        /// never left the seat).
        /// </summary>
        public override void InputJump(bool value, UdonInputEventArgs args)
        {
            if (!seated)
            {
                ClearJumpHold();
                return;
            }
            if (value)
            {
                if (jumpHeld) return;
                jumpHeld = true;
                jumpHeldSince = Time.time;
                return;
            }
            ClearJumpHold();
            WriteExitHint(ExitHintIdle);
        }

        private void Update()
        {
            if (exitPending && seated && station != null &&
                Time.time - exitRequestedAt >= ExitRetrySeconds)
            {
                VRCPlayerApi local = Networking.LocalPlayer;
                if (local != null) BeginExit(local, "retry");
            }
            if (scalePending && Time.time - scalePendingSince >= scaleWaitSeconds)
            {
                scalePending = false;
                Debug.Log("[ride] eye height unchanged after " + scaleWaitSeconds
                    + " s: measuring the seat as it is");
                if (seated) StartSeatMeasurement(Networking.LocalPlayer);
            }
            if (seated && scaleApplied && !scaleGivenUp && !scalePending) UpdateRideScale();
            if (!seated || !jumpHeld) return;
            float held = Time.time - jumpHeldSince;
            if (exitHoldSeconds <= 0f || held >= exitHoldSeconds)
            {
                ClearJumpHold();
                RequestExit();
                return;
            }
            int percent = Mathf.FloorToInt(100f * held / exitHoldSeconds);
            if (percent == lastHintPercent) return;
            lastHintPercent = percent;
            if (exitHintText != null) exitHintText.text = "下车中… " + percent + "%";
        }

        /// <summary>
        /// Leaves the seat deliberately: puts the exit location on walkable ground beside
        /// the ball, then asks the station to release the player. False when nobody is
        /// seated or the station is not wired. Every exit route (held jump, the panel
        /// button) comes through here.
        /// </summary>
        public bool RequestExit()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || station == null) return false;
            if (!seated) return false;
            PlaceExitPoint();
            BeginExit(player, "request");
            return true;
        }

        // Asks the station to let the local player go and remembers that it asked; the state
        // is cleared only by OnStationExited.
        private void BeginExit(VRCPlayerApi player, string reason)
        {
            exitPending = true;
            exitRequestedAt = Time.time;
            station.ExitStation(player);
            Debug.Log("[ride] exit requested reason=" + reason);
        }

        // Moves the exit location to the first candidate spot beside the ball whose ground
        // is standable. Nothing found keeps the authored location and says so, because a
        // landing under the terrain is the failure this method exists to prevent.
        private void PlaceExitPoint()
        {
            if (exitPoint == null || motor == null) return;
            Vector3 center = motor.GetCenterWorld();
            float radius = motor.GetCollisionRadiusWorldM();
            float heading = motor.GetHeadingDegrees();
            for (int index = 0; index < RideExitMath.CandidateCount; index++)
            {
                Vector3 origin;
                float length;
                float expectedGroundY;
                if (!RideExitMath.TryExitProbe(center, radius, heading, index, out origin,
                    out length, out expectedGroundY))
                {
                    continue;
                }
                RaycastHit hit;
                if (!Physics.Raycast(origin, Vector3.down, out hit, length, exitProbeLayers,
                    QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                if (!RideExitMath.IsStandable(hit.normal.y, hit.point.y, expectedGroundY))
                {
                    continue;
                }
                Vector3 stand;
                float yaw;
                if (!RideExitMath.TryStandPose(hit.point, center, out stand, out yaw)) continue;
                exitPoint.position = stand;
                exitPoint.rotation = Quaternion.Euler(0f, yaw, 0f);
                Debug.Log("[ride] exit point candidate=" + index + " y=" + stand.y);
                return;
            }
            Debug.LogWarning("[ride] no standable exit candidate beside the ball; the authored"
                + " exit location is used");
        }

        // Starts the one seat measurement, at the eye height the avatar has right now (zero
        // when this behaviour is not scaling the rider: the seat then keeps absolute limits and
        // has no stance).
        private void StartSeatMeasurement(VRCPlayerApi player)
        {
            if (seatEye == null) return;
            // A rejected scale request must not leave the panels at the requested (wrong)
            // size. The eye-only fallback still lays panels out for the actual rider.
            SetPanelScale(player != null ? player.GetAvatarEyeHeightAsMeters() : 0f);
            float eyeHeight = 0f;
            if (scaleApplied && !scaleGivenUp)
            {
                if (player != null) eyeHeight = player.GetAvatarEyeHeightAsMeters();
                if (!KatamariMath.IsFiniteFloat(eyeHeight) || eyeHeight <= 0f) eyeHeight = eyeHeightSent;
                eyeHeightSent = eyeHeight;
                eyeHeightSmooth = eyeHeight;
            }
            scaleCalibrated = false;
            seatEye.SetEyeHeight(eyeHeight);
            seatEye.BeginSeated();
        }

        // Prepares the size for the ride: the original eye height is read and kept, nothing is
        // requested yet (the first request needs eta, which the seat measurement gives).
        private void BeginRideScale(VRCPlayerApi player)
        {
            scaleGivenUp = false;
            scaleCalibrated = false;
            if (!scaleRiderWithBall) return;
            float current = player.GetAvatarEyeHeightAsMeters();
            if (!scaleApplied) savedEyeHeight = current;
            scaleApplied = true;
            scaleReapplyCount = 0;
            scaleMismatchChecks = 0;
            scaleNextCheck = Time.time + 1f;
            lastSendTime = Time.time;
            eyeHeightSent = current;
            eyeHeightSmooth = current;
            lastLoggedEyeHeight = current;
            SetPanelScale(current);
            Debug.Log("[ride] eye height " + current + " kept until the seat is measured");
        }

        // The ball's gameplay diameter, or the starting diameter before the motor has one.
        private float CurrentDiameter()
        {
            float diameter = motor != null ? motor.GetSemanticDiameterWorldM() : 0f;
            if (!KatamariMath.IsFiniteFloat(diameter) || diameter <= 0f)
            {
                return RideScaleMath.FallbackBallDiameterMeters;
            }
            return diameter;
        }

        // Each frame while seated with a size to keep: calibrate on the measurement, then follow
        // the ball's diameter with the rate-limited, low-passed eye height. Nothing is sent while
        // the seat is being measured (a measurement across a size change would be meaningless).
        private void UpdateRideScale()
        {
            if (seatEye == null || !seatEye.IsTracking()) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null) return;
            if (!scaleCalibrated)
            {
                if (seatEye.HasStance())
                {
                    stanceEta = seatEye.GetEta();
                    Debug.Log("[ride] seat calibrated: eta " + stanceEta + " at eye height "
                        + eyeHeightSent + " handGap " + seatEye.GetHandGapMeters());
                }
                else
                {
                    // Not a standing pose: size by the platform's definition of the eye height
                    // (the hands still reach the ball) and let the seat align the eye only.
                    stanceEta = RideStanceMath.DefaultEta;
                    Debug.Log("[ride] no usable stance (reason " + seatEye.GetStanceReason()
                        + "): eta " + stanceEta + ", the seat aligns the eye only");
                }
                scaleCalibrated = true;
                scaleReapplyCount = 0;
                scaleMismatchChecks = 0;
                scaleNextCheck = Time.time + 1f;
                eyeHeightSmooth = eyeHeightSent;
            }
            if (Time.time >= scaleNextCheck)
            {
                scaleNextCheck = Time.time + 0.5f;
                if (CheckScaleTookEffect(player)) return;
            }
            float target;
            bool clamped;
            if (!RideScaleMath.TryEyeHeightForDiameter(CurrentDiameter(), stanceEta, out target,
                out clamped))
            {
                return;
            }
            eyeHeightSmooth = RideScaleMath.SmoothEyeHeight(eyeHeightSmooth, target, Time.deltaTime,
                eyeHeightFilterHz);
            if (!RideScaleMath.ShouldSendEyeHeight(eyeHeightSent, eyeHeightSmooth,
                eyeHeightStepFraction, Time.time - lastSendTime, eyeHeightMinIntervalSeconds))
            {
                return;
            }
            float send;
            if (!RideScaleMath.TryResolveTarget(eyeHeightSmooth, out send)) return;
            player.SetAvatarEyeHeightByMeters(send);
            eyeHeightSent = send;
            lastSendTime = Time.time;
            seatEye.ApplyEyeHeight(send);
            SetPanelScale(send);
            if (Mathf.Abs(send - lastLoggedEyeHeight) >= 0.25f * lastLoggedEyeHeight)
            {
                lastLoggedEyeHeight = send;
                Debug.Log("[ride] eye height " + send + " diameter " + CurrentDiameter()
                    + (clamped ? " (target clamped to the platform range)" : "") + " eyeError "
                    + seatEye.GetCurrentEyeErrorMeters() + " handGap "
                    + seatEye.GetHandGapMeters());
            }
        }

        // A world with avatar scaling switched off ignores the requests: if the read-back stays
        // away from what was sent for about three seconds, stop asking and measure the seat at
        // the size the avatar really has. True when it gave up (the caller stops for this frame).
        private bool CheckScaleTookEffect(VRCPlayerApi player)
        {
            if (RideScaleMath.IsApplied(player.GetAvatarEyeHeightAsMeters(), eyeHeightSent))
            {
                scaleMismatchChecks = 0;
                return false;
            }
            scaleMismatchChecks++;
            if (scaleMismatchChecks < 6) return false;
            scaleGivenUp = true;
            Debug.LogWarning("[ride] eye height does not follow the requests: the rider keeps"
                + " its own size and the seat is measured as it is");
            StartSeatMeasurement(player);
            return true;
        }

        // Applies the size that was last sent again, after an eye-height event that was not ours;
        // the measurement then waits for the next announcement. The caller has already counted
        // this against RideScaleMath.MaxConsecutiveReapply through ClassifyEyeHeightEvent.
        private void ReapplyRideScale(VRCPlayerApi player)
        {
            float target;
            if (!RideScaleMath.TryResolveTarget(eyeHeightSent, out target))
            {
                scalePending = false;
                StartSeatMeasurement(player);
                return;
            }
            scaleReapplyCount++;
            scalePending = true;
            scalePendingSince = Time.time;
            player.SetAvatarEyeHeightByMeters(target);
            Debug.Log("[ride] eye height re-applied (" + scaleReapplyCount + ")");
        }

        // Puts the size back to what it was before the ride. Runs on every exit path and
        // does nothing when this behaviour did not change it.
        private void EndRideScale(VRCPlayerApi player)
        {
            scalePending = false;
            scaleCalibrated = false;
            SetPanelScale(0f);
            if (!scaleApplied) return;
            scaleApplied = false;
            scaleGivenUp = false;
            float restore = RideScaleMath.RestoreTarget(savedEyeHeight);
            if (restore > 0f && player != null) player.SetAvatarEyeHeightByMeters(restore);
            Debug.Log("[ride] eye height restored to " + restore);
        }

        private void ClearJumpHold()
        {
            jumpHeld = false;
            lastHintPercent = -1;
        }

        // The panel root's uniform scale for this eye height; zero (or any unknown height) puts
        // it back to one, the authored layout.
        private void SetPanelScale(float eyeHeightMeters)
        {
            if (panelRoot == null) return;
            float scale = RideViewMath.PanelScaleForEyeHeight(eyeHeightMeters);
            panelRoot.localScale = new Vector3(scale, scale, scale);
        }

        private void SetExitPanel(bool active)
        {
            if (exitPanelRoot == null) return;
            exitPanelRoot.SetActive(active);
        }

        private void WriteExitHint(string text)
        {
            if (exitHintText == null) return;
            exitHintText.text = text;
        }

        // Each adapter is gated on its own: a scene without the desktop adapter still gates VR.
        private void SetRideGate(bool open)
        {
            if (input != null) input.SetRideGate(open);
            if (vrInput != null) vrInput.SetRideGate(open);
        }
    }
}
