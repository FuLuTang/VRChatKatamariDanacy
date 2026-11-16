using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRCKatamari.Core;
using VRCKatamari.Riding;
using VRCKatamari.Story;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The room/level loop for one local player: a press in the room sends the player
    /// into a level and opens the round, the settled round (or a press) sends the
    /// player back to the room, and while a round is running the player is kept in the
    /// level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this behaviour is, and what it is not. It is the only place that moves the
    /// player between the two islands and the only place that reacts to the round's
    /// state by moving anyone (HUB_LOOP_PLAN sections 2-5). It is not a second state
    /// machine: <see cref="StoryRoundController"/> keeps the only one, and this
    /// behaviour polls <c>GetState()</c> the same way the optional
    /// <see cref="StoryResultPresenter"/> does, for the same reason - the round must
    /// not know who is watching it, so the arrow points one way and there is no event
    /// to subscribe to. The cost is that a state change is acted on in the frame this
    /// behaviour notices it, which can be one frame after the round changed.
    /// </para>
    /// <para>
    /// Why the round is opened only after an observed arrival. The plan's first
    /// sequence (section 5) opens the round in the same call as the teleport. That is
    /// the one place this file deliberately differs, because the harder requirement is
    /// that Running implies "the player is in the level" (section 2): a teleport that
    /// silently does not move the player would otherwise start a clock nobody is
    /// standing under. So a start is a small two-phase transfer - teleport, then wait
    /// (<c>arrivalCheckFrames</c>) and measure (<c>GetPosition()</c> against the
    /// lifted entry point), and only then call the round. If the player never arrives
    /// within <c>arrivalTimeoutFrames</c>, the round is never opened, the state stays
    /// Idle/Result, and the press can simply be repeated. That is the plan's "log it,
    /// do not hide it" fallback, made safe.
    /// </para>
    /// <para>
    /// Why the seat is left first, on a later frame. A station can block a teleport
    /// (local creator library, <c>entities/world/udon/players/player-positions.md</c>,
    /// section 传送 API: "Stations can prevent it"; official docs
    /// <see href="https://creators.vrchat.com/worlds/udon/players/player-positions/#teleportto"/>
    /// say the same: "unless a Station does not allow it"), and the plan's order is
    /// settle -> exit the station -> teleport (section 5). The exit itself is the
    /// documented way out of a station that has Disable Station Exit on: the station page
    /// says "Prevents the player from exiting the station by moving or jumping. Use the
    /// <c>ExitStation</c> node to remove the player from the station"
    /// (<see href="https://creators.vrchat.com/worlds/components/vrc_station/"/>). Note
    /// that this move puts the player at the station's Station Exit Player Location first,
    /// which is why the return still measures the arrival at the room point afterwards. What is <em>not</em> known is whether
    /// the exit callback has landed by the time the next line runs, so the return is a
    /// three-phase transfer: request <c>ExitStation</c>, wait until the seat reports
    /// empty (or <c>exitStationTimeoutFrames</c>), then move on a frame after that. Every
    /// branch is logged with the path it took, including the timeout branch, so the
    /// first real test can tell "it worked" from "it worked anyway".
    /// </para>
    /// <para>
    /// The third transfer is the correction. Running is the one state where the level
    /// must contain the player, so if the player is outside the level island while the
    /// clock runs (a menu respawn lands at the world spawn, which is the room; falling
    /// out of the world does the same) this behaviour puts them back at the level entry,
    /// up to <c>holdAttemptLimit</c> times. It is not a fence: the radius is far larger
    /// than the island and far smaller than the room distance, so only respawn-class
    /// moves trip it. This is a deliberate difference from the plan's section 5
    /// ("a respawn in the level is tolerated; the clock keeps running"), because the
    /// section 2 invariant is the stronger requirement and the round cannot be ended
    /// early from outside.
    /// </para>
    /// <para>
    /// What it will not do: send the player to the room mid-round. The round controller
    /// has no public "end/settle now" call, and abandoning the round by walking away is
    /// exactly what section 2 forbids, so a return press while Running is refused with
    /// <see cref="HubLoopMath.ReasonRoundStillRunning"/> and a log line. The clock always
    /// ends the round, and the same button works again in Presenting/Result. Adding a
    /// public abandon call to the round is the one missing interface this file would use
    /// if it existed (see the delivery notes); it is not added here because
    /// <c>Runtime/Story/</c> is out of this task's write scope.
    /// </para>
    /// <para>
    /// Ownership, so no two behaviours write the same thing. This behaviour writes
    /// exactly one Text of its own (<c>statusText</c>, the room panel's prompt line) and
    /// never calls <c>SetActive</c> on anything: the round already owns the start prompt
    /// and the retry button's visibility, and the presenter owns the result card's, so a
    /// third writer would fight them. Button availability is expressed by refusing the
    /// press with a reason code, not by hiding the button - which also means a player in
    /// VR always sees something to press, and the console always says why it did nothing.
    /// </para>
    /// <para>
    /// Facts, not stars. The room panel does not repeat the settled measurement or the
    /// stars: the optional presenter already renders that on the card it owns, and it
    /// exposes no read-only getter for a second display to read (its fields are private
    /// and it only writes its own Text). The plan's section 6 rule - the room reads the
    /// round's frozen facts, never the motor's "now" - is kept here for everything this
    /// behaviour does print: <c>GetState</c>, <c>GetLevelId</c>, <c>GetAttemptNumber</c>.
    /// </para>
    /// <para>
    /// Single player, local only (<c>BehaviourSyncMode.None</c>): <c>TeleportTo</c> can
    /// only move the local player, and the plan's section 6.4 already scopes this loop to
    /// one player. Nothing here is synced, so two clients in one instance each run their
    /// own loop.
    /// </para>
    /// <para>
    /// Placement: this behaviour must live on an object that is never deactivated (the
    /// plan's section 6.3 warning applies to the round controller and to this one for the
    /// same reason - its Update is what moves the player back). It is not parented under
    /// the room or the level.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubLevelLauncher : UdonSharpBehaviour
    {
        // ------------------------------------------------------------------ links

        // The only state machine. Read through public getters; started through
        // BeginRound()/RequestRetry(); never a second copy of its state here.
        [Header("Round")]
        [SerializeField] private StoryRoundController controller;

        // One entry transform per level, index == the index a start button carries.
        // Level 0 is the ball's start; the arrays are the room panel's future "one row
        // per level", so adding a level is one more element, not a new code path.
        [Header("Places")]
        [SerializeField] private Transform[] levelEntries;
        [SerializeField] private string[] levelNames;
        [SerializeField] private Transform roomSpawn;

        // The seat to leave before any teleport, and the station to ask. Both are
        // optional: a scene with no ride still runs the loop, it just cannot exit a
        // seat, and then teleports anyway and says so.
        [Header("Ride (optional)")]
        [SerializeField] private VRC.SDK3.Components.VRCStation rideStation;
        [SerializeField] private BallRideStation rideSeat;

        // Owner decision 2026-11-16: the room flow seats the player on the ball, with no
        // click and no Station trigger. A negative flag, so an old scene instance that
        // predates the field reads as "seat on" instead of "off".
        [Header("Seat on start")]
        [SerializeField] private bool disableAutoSeat = false;
        // Frames a seat request may take before the launcher declares it failed.
        // UseStation is asynchronous and OnStationEntered can land a few frames later;
        // 30 frames is about half a second and matches the start's arrival budget.
        [SerializeField] private int seatCheckFrames = 30;

        // The room panel's prompt line. Written only on a state change.
        [Header("Room panel (optional)")]
        [SerializeField] private Text statusText;

        // ------------------------------------------------------------------ tuning

        // Metres added to a destination's Y. Same convention and default as the
        // teleport probe: a point on the floor surface starts the capsule inside the
        // floor collider and the client pushes the player back out.
        [Header("Arrival")]
        [SerializeField] private float arrivalLiftMeters = 0.1f;

        // A teleport counts as arrived within this distance. The plan's acceptance test
        // for the probe is 0.5 m; this is looser because a player who lands on a prop
        // or is nudged by a collider can settle a little off the mark.
        [SerializeField] private float arrivalToleranceMeters = 1f;

        // Frames waited before the arrival is read: one frame applies the move, the
        // second lets the capsule depenetrate. Verified by the probe's two readings.
        [SerializeField] private int arrivalCheckFrames = 2;

        // Frames before a start that never arrived is refused. 30 frames is about half a
        // second at 60 fps - long enough for a slow client, short enough that a broken
        // press is answered while the player is still looking at the button.
        [SerializeField] private int arrivalTimeoutFrames = 30;

        // Frames to wait for the station exit to land before moving anyway.
        [SerializeField] private int exitStationTimeoutFrames = 10;

        // Minimum seconds between two accepted transfers. A press during the cooldown is
        // refused, not queued.
        [Header("Pacing")]
        [SerializeField] private float transferCooldownSeconds = 1f;

        // Puts the player back at the level entry when a round is Running and they are
        // outside the island. Turn it off only to observe that behaviour, not to fix it.
        [Header("Hold the player in the level while a round runs")]
        [SerializeField] private bool holdPlayerInLevel = true;

        // The island's radius around the level entry. Must exceed the island and stay
        // well below the room distance (the plan's provisional 1200 m).
        [SerializeField] private float levelAreaRadiusMeters = 200f;

        // Corrections per round. A broken teleport must not become a per-frame loop.
        [SerializeField] private int holdAttemptLimit = 3;

        // Seconds after a round opens before the containment correction may move anyone.
        // A start is teleport -> measure -> seat; a client that lands or respawns in that
        // window must not be pulled around while it settles.
        [SerializeField] private float holdGraceSeconds = 2.5f;

        // ------------------------------------------------------------------ state

        // -1 is "no state seen yet", so the first Update counts as a change and writes
        // the panel once (the presenter uses the same sentinel).
        private int lastState = -1;

        // Negative means "no transfer has happened yet". Time.time itself starts near
        // zero, so zero cannot be the sentinel.
        private float lastTransferSeconds = -1f;

        private bool startPending;
        private int startLevelIndex = -1;
        private int startMode = HubLoopMath.StartModeNone;
        private int startStateAtRequest = -1;
        private int startFramesWaited;
        private Vector3 startArrival;

        private bool returnPending;
        private int returnFramesWaited;
        private bool returnWaitingForExit;
        private bool returnTeleportIssued;
        private int returnFramesSinceTeleport;
        private Vector3 returnArrival;

        // Which level the running round belongs to: the level area for the hold check
        // and the label on the room panel. -1 until a start has succeeded.
        private int activeLevelIndex = -1;

        private int holdAttempts;

        // Where this attempt's arrival is measured: the seat transform when auto-seat is
        // on and a station is wired, the level entry otherwise. Kept as a Transform so a
        // moving ball's seat keeps the hold radius centred on the rider.
        private Transform startArrivalPoint;
        private Transform activeLevelPoint;

        // Seat request window after a start and after a hold correction. UseStation is
        // asynchronous; a request that has not landed is retried at a fixed interval
        // inside the window, then reported once.
        private bool seatCheckPending;
        private int seatCheckFrame;
        private int seatAttempts;
        private int lastSeatRequestFrame;

        // Time.time when the current round opened; negative means no round yet.
        private float lastStartSeconds = -1f;

        private bool warnedMissingController;
        private bool warnedMissingRoomPoint;

        // Seat retries inside seatCheckFrames: two more attempts ten frames apart.
        private const int SeatRetryLimit = 3;
        private const int SeatRetryIntervalFrames = 10;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            // Sanitised once, so a mistyped serialized number cannot make every press
            // refuse for a reason that has nothing to do with the loop.
            if (!KatamariMath.IsFiniteFloat(arrivalLiftMeters) || arrivalLiftMeters < 0f)
            {
                Debug.LogWarning("[hub] arrivalLiftMeters=" + arrivalLiftMeters
                    + " is not usable; using 0");
                arrivalLiftMeters = 0f;
            }
            if (!KatamariMath.IsFiniteFloat(arrivalToleranceMeters)
                || arrivalToleranceMeters < 0f)
            {
                Debug.LogWarning("[hub] arrivalToleranceMeters=" + arrivalToleranceMeters
                    + " is not usable; using 0.5");
                arrivalToleranceMeters = 0.5f;
            }
            if (arrivalCheckFrames < 1) arrivalCheckFrames = 1;
            if (arrivalTimeoutFrames < arrivalCheckFrames)
            {
                arrivalTimeoutFrames = arrivalCheckFrames;
            }
            if (exitStationTimeoutFrames < 1) exitStationTimeoutFrames = 1;

            if (!KatamariMath.IsFiniteFloat(transferCooldownSeconds)
                || transferCooldownSeconds < 0f)
            {
                Debug.LogWarning("[hub] transferCooldownSeconds=" + transferCooldownSeconds
                    + " is not usable; using 0");
                transferCooldownSeconds = 0f;
            }

            if (!KatamariMath.IsFiniteFloat(levelAreaRadiusMeters)
                || levelAreaRadiusMeters <= 0f)
            {
                Debug.LogWarning("[hub] levelAreaRadiusMeters=" + levelAreaRadiusMeters
                    + " is not usable; holding the player in the level is off");
                holdPlayerInLevel = false;
            }
            if (holdAttemptLimit < 0) holdAttemptLimit = 0;
            // Both are safety values, and a scene saved before the fields existed can
            // deserialize them as zero. Treat zero as "use the default" rather than as an
            // instruction to disable the protection; the operator can still tune them.
            if (seatCheckFrames < 1) seatCheckFrames = 30;
            if (!KatamariMath.IsFiniteFloat(holdGraceSeconds) || holdGraceSeconds <= 0f)
            {
                Debug.LogWarning("[hub] holdGraceSeconds=" + holdGraceSeconds
                    + " is not usable; using 2.5");
                holdGraceSeconds = 2.5f;
            }

            if (controller == null)
            {
                warnedMissingController = true;
                Debug.LogWarning("[hub] no round controller is wired; the room/level loop is"
                    + " inert. The round itself is unaffected.");
            }
            if (roomSpawn == null)
            {
                warnedMissingRoomPoint = true;
                Debug.LogWarning("[hub] no room point is wired; a finished round cannot send"
                    + " the player back, and a return press is refused with reason="
                    + HubLoopMath.ReasonNoRoomPoint);
            }
            if (rideStation == null)
            {
                Debug.Log("[hub] no ride station is wired; no station exit is attempted"
                    + " before a return.");
            }

            Debug.Log("[hub] ready levels=" + LevelCount() + " state=" + StateText()
                + " holdInLevel=" + holdPlayerInLevel
                + " autoSeat=" + (!disableAutoSeat && rideStation != null && rideSeat != null)
                + " holdGrace=" + holdGraceSeconds
                + " lift=" + arrivalLiftMeters
                + " tolerance=" + arrivalToleranceMeters
                + " cooldown=" + transferCooldownSeconds);
        }

        public void Update()
        {
            if (controller == null)
            {
                if (!warnedMissingController)
                {
                    warnedMissingController = true;
                    Debug.LogWarning("[hub] no round controller is wired; the room/level loop"
                        + " is inert. The round itself is unaffected.");
                }
                return;
            }

            int state = controller.GetState();

            // The one-line answer to "did the scripted seat actually take?" is read on
            // later frames, because UseStation is asynchronous: success logs as soon as
            // OnStationEntered lands, and only a request still unanswered at the deadline
            // is a failure. Logged even while a transfer is pending, so a failed seat is
            // never swallowed by a later return.
            if (seatCheckPending)
            {
                bool seatedNow = rideSeat != null && rideSeat.IsLocalPlayerSeated();
                if (seatedNow)
                {
                    seatCheckPending = false;
                    Debug.Log("[hub] seat result seated=true frame=" + Time.frameCount
                        + " state=" + state);
                }
                else if (HubLoopMath.ShouldRetrySeat(seatedNow, seatAttempts, SeatRetryLimit,
                    Time.frameCount - lastSeatRequestFrame, SeatRetryIntervalFrames))
                {
                    // The first request can land while the player is still airborne from
                    // the arrival teleport. Asking again after a short fall/settle window
                    // is cheap and is the difference between "seated" and "standing next
                    // to the ball" on a slow client.
                    seatAttempts++;
                    lastSeatRequestFrame = Time.frameCount;
                    bool again = rideSeat != null && rideSeat.TrySeatLocalPlayer();
                    Debug.Log("[hub] seat retry attempt=" + seatAttempts
                        + " requested=" + again
                        + " frame=" + Time.frameCount);
                }
                else if (Time.frameCount >= seatCheckFrame)
                {
                    seatCheckPending = false;
                    Debug.LogWarning("[hub] seat result seated=false frame=" + Time.frameCount
                        + " attempts=" + seatAttempts
                        + " state=" + state + "; the station did not take the player within "
                        + seatCheckFrames + " frames. The round keeps running and the player"
                        + " can still click the ball (BallRideStation.Interact), but a client"
                        + " without station support cannot drive.");
                }
            }

            if (state != lastState)
            {
                lastState = state;
                // A state change during a start means the round moved without this
                // behaviour opening it (a sit-down, or a delayed Update order). The start
                // must not fire on top of it.
                if (startPending && state != startStateAtRequest)
                {
                    AbortStart(HubLoopMath.ReasonRoundStateChanged);
                }
                OnStateEntered(state);
            }

            // One transfer at a time: a start and a return must never race for the player.
            if (startPending)
            {
                UpdateStart();
                return;
            }
            if (returnPending)
            {
                UpdateReturn();
                return;
            }

            if (holdPlayerInLevel) UpdateHoldInLevel(state);
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>
        /// World-space press on a room "start" button: this is the seam the button
        /// behaviour calls, with the index the button carries.
        /// </summary>
        /// <remarks>
        /// Logs the refusal and nothing else. Refusing to move is the normal answer here
        /// (a second press while a transfer is in flight, a press during Running), so it
        /// is a warning with a reason code, not an error.
        /// </remarks>
        public void PressStart(int levelIndex)
        {
            int reason;
            if (TryStartLevel(levelIndex, out reason)) return;

            Debug.LogWarning("[hub] start refused levelIndex=" + levelIndex
                + " reason=" + reason
                + " (" + HubLoopMath.DescribeReason(reason) + ")"
                + " state=" + StateText()
                + " startPending=" + startPending
                + " returnPending=" + returnPending);
        }

        /// <summary>
        /// Whether a press on a room "start" button may begin a transfer into
        /// <paramref name="levelIndex"/>, and does it if so.
        /// </summary>
        /// <remarks>
        /// Every check is <see cref="HubLoopMath"/>'s; this method adds the scene facts
        /// (is the controller wired, is there a player, is the entry slot filled, is the
        /// arrival point finite) and the arrival point itself. It returns as soon as the
        /// transfer is requested - the round is <em>not</em> open yet, that happens in
        /// <see cref="UpdateStart"/> once the player has been measured at the entry.
        /// </remarks>
        public bool TryStartLevel(int levelIndex, out int reason)
        {
            reason = HubLoopMath.ReasonNone;

            if (controller == null)
            {
                reason = HubLoopMath.ReasonNoController;
                return false;
            }

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                reason = HubLoopMath.ReasonNoLocalPlayer;
                return false;
            }

            int state = controller.GetState();
            bool transferPending = startPending || returnPending;
            float nowSeconds = Time.time;

            if (!HubLoopMath.TryDecideStart(state, levelIndex, LevelCount(), transferPending,
                nowSeconds, lastTransferSeconds, transferCooldownSeconds, out reason))
            {
                return false;
            }

            int mode;
            if (!HubLoopMath.TryStartMode(state, out mode))
            {
                // Unreachable while TryDecideStart accepts the same state, kept so the
                // method is total: a future state constant cannot fall through.
                reason = HubLoopMath.ReasonStartNotInIdleOrResult;
                return false;
            }

            // Where the player is sent. With a station wired, the room flow seats the
            // player on the ball, so the arrival is the seat's own transform: the station
            // carries them from there and a seat that fails still leaves the player on the
            // level floor next to the ball instead of at a hand-placed point that can be
            // metres off (the 2026-11-16 run put LevelEntry in the void, the player fell,
            // and ClientSim respawned them at the room). The level entry stays as the
            // no-station fallback and as the destination when auto-seat is off.
            Transform entry = LevelEntry(levelIndex);
            bool seatArrival = !disableAutoSeat && rideStation != null && rideSeat != null;
            Transform arrivalPoint = entry;
            if (seatArrival) arrivalPoint = rideStation.transform;
            if (arrivalPoint == null)
            {
                reason = HubLoopMath.ReasonLevelEntryUnwired;
                return false;
            }

            Vector3 arrival;
            if (!HubLoopMath.TryLiftedPoint(arrivalPoint.position, arrivalLiftMeters, out arrival))
            {
                reason = HubLoopMath.ReasonPositionNotFinite;
                return false;
            }

            // Stamped on the request, not on the arrival: two presses in the same frame
            // are already refused by startPending, and this covers the frames after.
            lastTransferSeconds = nowSeconds;
            startArrivalPoint = arrivalPoint;

            Vector3 position = player.GetPosition();
            if (HubLoopMath.IsWithin(position, arrival, arrivalToleranceMeters))
            {
                // Already at the entry (a player who walked there, or a start after a
                // move that did land). No transfer: the round opens on this frame.
                StartRound(levelIndex, mode, Vector3.Distance(position, arrival), 0,
                    "already-at-entry");
                return true;
            }

            startPending = true;
            startLevelIndex = levelIndex;
            startMode = mode;
            startStateAtRequest = state;
            startFramesWaited = 0;
            startArrival = arrival;

            TeleportPlayer(arrival, arrivalPoint.rotation,
                "enter-level levelIndex=" + levelIndex
                + " name=" + LevelName(levelIndex)
                + " mode=" + mode
                + " state=" + state
                + " arrivalSource=" + (seatArrival ? "seat" : "entry"));
            return true;
        }

        /// <summary>
        /// World-space press on a "back to the room" button: the seam the button
        /// behaviour calls. Logs the refusal with a reason code.
        /// </summary>
        public void PressReturnToRoom()
        {
            int reason;
            if (TryReturnToRoom(out reason)) return;

            Debug.LogWarning("[hub] return refused reason=" + reason
                + " (" + HubLoopMath.DescribeReason(reason) + ")"
                + " state=" + StateText()
                + " startPending=" + startPending
                + " returnPending=" + returnPending);
        }

        /// <summary>
        /// Whether a press on a "back to the room" button may send the player to the
        /// room, and does it if so.
        /// </summary>
        /// <remarks>
        /// Refused in Running on purpose: see the class remarks. Allowed in Idle,
        /// Presenting and Result; in Idle it is a plain "go to the room" press and is
        /// free because nothing is running.
        /// </remarks>
        public bool TryReturnToRoom(out int reason)
        {
            reason = HubLoopMath.ReasonNone;

            if (controller == null)
            {
                reason = HubLoopMath.ReasonNoController;
                return false;
            }

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                reason = HubLoopMath.ReasonNoLocalPlayer;
                return false;
            }

            bool transferPending = startPending || returnPending;
            float nowSeconds = Time.time;
            if (!HubLoopMath.TryDecideReturn(controller.GetState(), transferPending,
                nowSeconds, lastTransferSeconds, transferCooldownSeconds, out reason))
            {
                return false;
            }

            BeginReturn("button");
            return true;
        }

        // ------------------------------------------------------------------ state entry

        private void OnStateEntered(int state)
        {
            WriteStatus(state);

            Debug.Log("[hub] round state=" + state
                + " level=" + controller.GetLevelId()
                + " attempt=" + controller.GetAttemptNumber()
                + " activeLevelIndex=" + activeLevelIndex);

            // The settled round sends the player back: Presenting is the beat the plan
            // puts "return to the room" in (section 5, step 6). The facts are frozen
            // inside the same call that set this state, so the move cannot outrun them.
            if (state == StoryRoundController.StatePresenting && !returnPending && !startPending)
            {
                BeginReturn("round-settled");
                return;
            }

            // Fallback for a round that reaches Result without this behaviour having seen
            // Presenting: two states can pass between two polls when presentingSeconds is
            // 0 or very small. Only acts when the player is demonstrably not at the room
            // point, so a normal round (already returned during Presenting) stays silent
            // instead of logging a skipped return every time.
            if (state == StoryRoundController.StateResult && !returnPending && !startPending
                && IsPlayerAwayFromRoom())
            {
                BeginReturn("result-outside-room");
            }
        }

        /// <summary>
        /// Whether the local player is measurably away from the room point. Same lifted
        /// point and tolerance as the return itself, so "away" and "arrived" cannot
        /// disagree.
        /// </summary>
        private bool IsPlayerAwayFromRoom()
        {
            if (roomSpawn == null) return false;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null) return false;

            Vector3 arrival;
            if (!HubLoopMath.TryLiftedPoint(roomSpawn.position, arrivalLiftMeters, out arrival))
            {
                return false;
            }

            return !HubLoopMath.IsWithin(player.GetPosition(), arrival, arrivalToleranceMeters);
        }

        // ------------------------------------------------------------------ start transfer

        private void UpdateStart()
        {
            startFramesWaited++;
            if (startFramesWaited < arrivalCheckFrames) return;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                AbortStart(HubLoopMath.ReasonNoLocalPlayer);
                return;
            }

            Vector3 position = player.GetPosition();
            if (HubLoopMath.IsWithin(position, startArrival, arrivalToleranceMeters))
            {
                int levelIndex = startLevelIndex;
                int mode = startMode;
                int frames = startFramesWaited;
                float distance = Vector3.Distance(position, startArrival);
                startPending = false;
                StartRound(levelIndex, mode, distance, frames, "arrived");
                return;
            }

            if (startFramesWaited < arrivalTimeoutFrames) return;

            // The teleport did not put the player at the entry. Opening the round now
            // would put a running clock over an empty level, which is the one thing this
            // loop exists to prevent, so the start is refused and the state is left
            // alone - the press can be repeated.
            AbortStart(HubLoopMath.ReasonArrivalNotReached);
        }

        /// <summary>
        /// Opens the round with the public call its state calls for, now that the player
        /// has been measured at the entry.
        /// </summary>
        /// <remarks>
        /// Result goes through <c>RequestRetry()</c> on purpose: that is the round's own
        /// "reset the ball to its captured pose, restore the props, begin" sequence, and
        /// a reset written here would be a second implementation that can drift from the
        /// one the retry button uses. If the round refuses that reset, the player is
        /// standing in the level while the round is still in Result - the exact
        /// disagreement the plan forbids - so the transfer is rolled back by sending the
        /// player to the room rather than leaving them there.
        /// </remarks>
        private void StartRound(int levelIndex, int mode, float distanceFromEntry,
            int framesWaited, string cause)
        {
            activeLevelIndex = levelIndex;
            activeLevelPoint = startArrivalPoint;
            if (activeLevelPoint == null) activeLevelPoint = LevelEntry(levelIndex);
            holdAttempts = 0;

            if (mode == HubLoopMath.StartModeResetThenBegin)
            {
                if (!controller.RequestRetry())
                {
                    Debug.LogWarning("[hub] start refused reason="
                        + HubLoopMath.ReasonRoundRefusedRestart
                        + " (" + HubLoopMath.DescribeReason(
                            HubLoopMath.ReasonRoundRefusedRestart) + ")"
                        + " levelIndex=" + levelIndex
                        + " state=" + controller.GetState()
                        + "; returning to the room so the round and the player agree");
                    BeginReturn("start-rolled-back");
                    return;
                }
            }
            else if (mode == HubLoopMath.StartModeBeginRound)
            {
                controller.BeginRound();
            }
            else
            {
                Debug.LogWarning("[hub] start refused reason="
                    + HubLoopMath.ReasonStartNotInIdleOrResult
                    + " (" + HubLoopMath.DescribeReason(
                        HubLoopMath.ReasonStartNotInIdleOrResult) + ")"
                    + " mode=" + mode);
                return;
            }

            Debug.Log("[hub] round started cause=" + cause
                + " levelIndex=" + levelIndex
                + " name=" + LevelName(levelIndex)
                + " mode=" + mode
                + " state=" + controller.GetState()
                + " distanceFromEntry=" + distanceFromEntry
                + " framesWaited=" + framesWaited);

            lastStartSeconds = Time.time;
            RequestSeat("started");
        }

        private void AbortStart(int reason)
        {
            if (!startPending) return;

            startPending = false;

            VRCPlayerApi player = Networking.LocalPlayer;
            string positionText = "unavailable";
            float distance = -1f;
            if (player != null)
            {
                Vector3 position = player.GetPosition();
                positionText = "" + position;
                distance = Vector3.Distance(position, startArrival);
            }

            Debug.LogWarning("[hub] start refused reason=" + reason
                + " (" + HubLoopMath.DescribeReason(reason) + ")"
                + " levelIndex=" + startLevelIndex
                + " state=" + StateText()
                + " framesWaited=" + startFramesWaited
                + " arrival=" + startArrival
                + " position=" + positionText
                + " distance=" + distance);
        }

        // ------------------------------------------------------------------ return transfer

        /// <summary>
        /// Starts a return transfer: leave the seat if the player is in it, then move to
        /// the room point on a later frame.
        /// </summary>
        /// <remarks>
        /// Called from three places - the settled round, the return button, and the
        /// rollback of a start the round refused - so "how a return happens" exists once.
        /// The cooldown is not consulted here: callers that are a press were already
        /// gated by <see cref="HubLoopMath.TryDecideReturn"/>, and the two corrective
        /// callers are one-shot by construction.
        /// </remarks>
        private void BeginReturn(string cause)
        {
            if (returnPending) return;
            // A seat whose answer is still in flight is irrelevant once the room flow
            // starts taking the player out; do not log a stale "seated=false" after it.
            seatCheckPending = false;

            if (roomSpawn == null)
            {
                if (!warnedMissingRoomPoint)
                {
                    warnedMissingRoomPoint = true;
                    Debug.LogWarning("[hub] return skipped cause=" + cause
                        + " reason=" + HubLoopMath.ReasonNoRoomPoint
                        + " (" + HubLoopMath.DescribeReason(
                            HubLoopMath.ReasonNoRoomPoint) + "); wire the room point");
                }
                return;
            }

            Vector3 arrival;
            if (!HubLoopMath.TryLiftedPoint(roomSpawn.position, arrivalLiftMeters, out arrival))
            {
                Debug.LogWarning("[hub] return skipped cause=" + cause
                    + " reason=" + HubLoopMath.ReasonPositionNotFinite
                    + " (" + HubLoopMath.DescribeReason(
                        HubLoopMath.ReasonPositionNotFinite) + ")");
                return;
            }

            returnPending = true;
            returnFramesWaited = 0;
            returnWaitingForExit = false;
            returnTeleportIssued = false;
            returnFramesSinceTeleport = 0;
            returnArrival = arrival;
            lastTransferSeconds = Time.time;

            bool seatKnown = rideSeat != null;
            bool seated = false;
            if (seatKnown) seated = rideSeat.IsLocalPlayerSeated();

            if (seated || !seatKnown)
            {
                if (rideStation != null)
                {
                    VRCPlayerApi player = Networking.LocalPlayer;
                    if (player != null)
                    {
                        // Exit before the move, never after: this is the plan's hard
                        // ordering, and the reason is that a station can block a teleport.
                        rideStation.ExitStation(player);
                        returnWaitingForExit = true;
                        Debug.Log("[hub] return exit-station cause=" + cause
                            + " seated=" + seated
                            + " seatKnown=" + seatKnown
                            + " frame=" + Time.frameCount);
                        return;
                    }
                }
                else if (seated)
                {
                    Debug.LogWarning("[hub] return: the local player is seated and no ride"
                        + " station is wired; the teleport may be blocked (unverified)."
                        + " cause=" + cause);
                }
            }

            Debug.Log("[hub] return queued cause=" + cause
                + " seated=" + seated
                + " seatKnown=" + seatKnown
                + " room=" + arrival);
        }

        private void UpdateReturn()
        {
            returnFramesWaited++;

            // Phase 1: leave the seat. The exit callback is what proves the seat is
            // empty; until it arrives the move is not issued.
            if (returnWaitingForExit)
            {
                bool seatKnown = rideSeat != null;
                bool seated = false;
                if (seatKnown) seated = rideSeat.IsLocalPlayerSeated();

                if (seatKnown && seated && returnFramesWaited < exitStationTimeoutFrames)
                {
                    return;
                }

                if (!seatKnown)
                {
                    // The exit was requested without a way to observe the seat. Two
                    // frames of separation still keep the move out of the exit's frame.
                    if (returnFramesWaited < 2) return;
                    Debug.Log("[hub] return: station exit requested, seat state unknown;"
                        + " moving after " + returnFramesWaited + " frames (unverified path)");
                }
                else if (seated)
                {
                    Debug.LogWarning("[hub] return: still seated " + returnFramesWaited
                        + " frames after ExitStation; moving anyway (unverified: a station"
                        + " can block a teleport)");
                }
                else
                {
                    Debug.Log("[hub] return: left the station after "
                        + returnFramesWaited + " frames");
                }

                returnWaitingForExit = false;
                // One more frame, whichever branch ran: the move never shares a frame
                // with the exit request. This is the cheap way to be right about an
                // ordering nobody here has measured yet.
                return;
            }

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                Debug.LogWarning("[hub] return aborted reason=" + HubLoopMath.ReasonNoLocalPlayer
                    + " (" + HubLoopMath.DescribeReason(
                        HubLoopMath.ReasonNoLocalPlayer) + ")");
                returnPending = false;
                return;
            }

            // Phase 2: the move, once.
            if (!returnTeleportIssued)
            {
                Vector3 position = player.GetPosition();
                if (HubLoopMath.IsWithin(position, returnArrival, arrivalToleranceMeters))
                {
                    Debug.Log("[hub] return skipped: the local player already is at the room"
                        + " point; no teleport. frames=" + returnFramesWaited);
                    returnPending = false;
                    return;
                }

                TeleportPlayer(returnArrival, roomSpawn.rotation, "return-to-room");
                returnTeleportIssued = true;
                returnFramesSinceTeleport = 0;
                return;
            }

            // Phase 3: measure the arrival and log it. A failed return is not retried
            // here - the player presses the button again, or uses VRChat's respawn, which
            // lands at the world spawn (the plan's own fallback). A hidden retry loop
            // would hide the bug this measurement exists to find.
            returnFramesSinceTeleport++;
            if (returnFramesSinceTeleport < arrivalCheckFrames) return;

            Vector3 nowPosition = player.GetPosition();
            float distance = Vector3.Distance(nowPosition, returnArrival);
            if (HubLoopMath.IsWithin(nowPosition, returnArrival, arrivalToleranceMeters))
            {
                Debug.Log("[hub] return arrived frames=" + returnFramesWaited
                    + " distance=" + distance);
            }
            else
            {
                Debug.LogWarning("[hub] return teleport did not reach the room point"
                    + " distance=" + distance + " arrived=false; the result card is in the"
                    + " room, so press the return button again or respawn from the menu");
            }
            returnPending = false;
        }

        // ------------------------------------------------------------------ hold in level

        private void UpdateHoldInLevel(int state)
        {
            if (state != StoryRoundController.StateRunning) return;
            if (activeLevelIndex < 0) return;

            // The seat transform when auto-seat is on (it moves with the ball), the
            // hand-placed entry otherwise. Null only if the scene lost both.
            Transform entry = activeLevelPoint;
            if (entry == null) entry = LevelEntry(activeLevelIndex);
            if (entry == null) return;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null) return;

            // Seated is the strongest "in level" reading, and a client can report a seated
            // player's position in more than one place; ask the station instead of measuring
            // a point. The grace window covers the teleport/land/seat frames after a start.
            bool seated = rideSeat != null && rideSeat.IsLocalPlayerSeated();
            Vector3 position = player.GetPosition();
            bool inside = HubLoopMath.IsWithin(position, entry.position, levelAreaRadiusMeters);
            if (!HubLoopMath.ShouldHoldInLevelNow(state, inside, seated, Time.time,
                lastStartSeconds, holdGraceSeconds, holdAttempts, holdAttemptLimit))
            {
                return;
            }
            if (HubLoopMath.IsCoolingDown(Time.time, lastTransferSeconds, transferCooldownSeconds))
            {
                return;
            }

            Vector3 arrival;
            if (!HubLoopMath.TryLiftedPoint(entry.position, arrivalLiftMeters, out arrival))
            {
                return;
            }

            float distance = Vector3.Distance(position, arrival);
            holdAttempts++;
            lastTransferSeconds = Time.time;

            // Not verified after the fact: the next frame re-measures anyway, and the
            // attempt limit is what stops a loop if this keeps failing.
            TeleportPlayer(arrival, entry.rotation,
                "hold-in-level attempt=" + holdAttempts
                + " distanceFromLevel=" + distance);

            // The correction put the player back at the ball; asking the station to seat
            // again is what turns "back in the level" into "riding", and it is the same
            // one-shot path a start uses.
            RequestSeat("hold-in-level");

            if (holdAttempts >= holdAttemptLimit)
            {
                Debug.LogWarning("[hub] hold-in-level gave up after " + holdAttempts
                    + " attempts; the round keeps running and settles on its own");
            }
        }

        // ------------------------------------------------------------------ seat

        /// <summary>
        /// Asks the ball's station to seat the local player and arms the one-line result
        /// check. No-op when auto-seat is off or no station is wired.
        /// </summary>
        /// <remarks>
        /// This is the launcher's only seat call and it goes through
        /// <see cref="BallRideStation.TrySeatLocalPlayer"/>, which wraps the same
        /// <c>VRCStation.UseStation</c> the ball's Interact uses. It is called after a
        /// start opens the round and after a hold correction puts the player back at the
        /// ball; the result is read a few frames later because UseStation is asynchronous.
        /// The round is not delayed on the seat: a client that never seats is still a
        /// valid round (the player can click the ball), just not a drivable one.
        /// </remarks>
        private void RequestSeat(string cause)
        {
            if (disableAutoSeat || rideSeat == null) return;

            bool requested = rideSeat.TrySeatLocalPlayer();
            seatCheckPending = requested;
            seatCheckFrame = Time.frameCount + seatCheckFrames;
            seatAttempts = 1;
            lastSeatRequestFrame = Time.frameCount;
            Debug.Log("[hub] seat request cause=" + cause
                + " requested=" + requested
                + " seated=" + rideSeat.IsLocalPlayerSeated()
                + " frame=" + Time.frameCount);
        }

        // ------------------------------------------------------------------ teleport

        /// <summary>
        /// The single place a player is moved. One log line per call, carrying the cause.
        /// </summary>
        /// <remarks>
        /// Four arguments are passed explicitly: the documented form is
        /// <c>TeleportTo(Vector3, Quaternion, SceneDescriptorSpawnOrientation, bool)</c>,
        /// and naming the orientation behaviour avoids inheriting a future change to the
        /// <c>Default</c> meaning. <c>lerpOnRemote=false</c> is the honest choice for an
        /// instant hop between two islands - it also means the avatar's animation and IK
        /// are reset, which is what a move of more than 1 m or 45 degrees does anyway
        /// (official docs, TeleportTo). The rotation is the destination Transform's own,
        /// which is unit-length by construction - the SDK only fixed non-unit rotations
        /// passed to a teleport in 2026.2.3, and there is no reason to hand it a rotation
        /// built anywhere else.
        /// <para>
        /// No network update is involved: every call comes from <c>Interact</c>, a button
        /// method, or this behaviour's <c>Update</c>, never from a deserialization
        /// callback. That is what the official warning ("do not teleport players during
        /// network updates ... delay the teleport by one frame") is about, so no
        /// <c>SendCustomEventDelayedFrames</c> shim is added here; the multi-frame
        /// transfers above already keep the move out of the frame that changed the state.
        /// </para>
        /// </remarks>
        private void TeleportPlayer(Vector3 arrival, Quaternion rotation, string cause)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                Debug.LogWarning("[hub] teleport skipped cause=" + cause
                    + " reason=" + HubLoopMath.ReasonNoLocalPlayer
                    + " (" + HubLoopMath.DescribeReason(
                        HubLoopMath.ReasonNoLocalPlayer) + ")");
                return;
            }

            Vector3 before = player.GetPosition();
            player.TeleportTo(arrival, rotation,
                VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, false);
            Debug.Log("[hub] teleport cause=" + cause
                + " from=" + before
                + " arrival=" + arrival);
        }

        // ------------------------------------------------------------------ panel

        private void WriteStatus(int state)
        {
            if (statusText == null) return;

            if (state == StoryRoundController.StateIdle)
            {
                if (LevelCount() == 1)
                {
                    statusText.text = "按开始进入：" + LevelName(0);
                    return;
                }
                statusText.text = "按开始进入关卡（共 " + LevelCount() + " 关）";
                return;
            }
            if (state == StoryRoundController.StateRunning)
            {
                statusText.text = "本局进行中";
                return;
            }
            if (state == StoryRoundController.StatePresenting)
            {
                statusText.text = "结算中，正在回房";
                return;
            }
            if (state == StoryRoundController.StateResult)
            {
                // The measured number and the stars are the presenter's card, not this
                // line: the same fact printed twice is the second copy section 6 forbids.
                statusText.text = "上一局已结算：" + ActiveLevelLabel() + "，按开始再来一局";
                return;
            }
            statusText.text = "";
        }

        // ------------------------------------------------------------------ small helpers

        private int LevelCount()
        {
            if (levelEntries == null) return 0;
            return levelEntries.Length;
        }

        private Transform LevelEntry(int levelIndex)
        {
            if (levelEntries == null) return null;
            if (levelIndex < 0) return null;
            if (levelIndex >= levelEntries.Length) return null;
            return levelEntries[levelIndex];
        }

        private string LevelName(int levelIndex)
        {
            if (levelNames == null) return "level-" + levelIndex;
            if (levelIndex < 0) return "level-" + levelIndex;
            if (levelIndex >= levelNames.Length) return "level-" + levelIndex;
            if (levelNames[levelIndex] == null) return "level-" + levelIndex;
            if (levelNames[levelIndex].Length == 0) return "level-" + levelIndex;
            return levelNames[levelIndex];
        }

        private string ActiveLevelLabel()
        {
            if (activeLevelIndex >= 0) return LevelName(activeLevelIndex);
            return controller.GetLevelId();
        }

        private string StateText()
        {
            if (controller == null) return "none";
            return "" + controller.GetState();
        }
    }
}
