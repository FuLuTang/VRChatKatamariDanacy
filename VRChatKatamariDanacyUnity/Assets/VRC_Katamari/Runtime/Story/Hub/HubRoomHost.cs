using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRCKatamari.Core;
using VRCKatamari.Hub;
using VRCKatamari.Story;

namespace VRCKatamari.Story.Hub
{
    /// <summary>
    /// The room's host: walks the player into a level circle, writes the room's one prompt
    /// line, and follows the round through Hub -> LevelLoading -> Playing -> Result and back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it owns, and what it deliberately does not. It owns the room phase (a
    /// projection of the round state plus the launcher's transfers), the armed/dwell state
    /// of the level circles, and the prompt Text. It does not move the player, exit the
    /// seat, freeze the round, count time, grade, or draw the result card: a start is
    /// <see cref="HubLevelLauncher.TryStartLevel"/> (which teleports, verifies the arrival
    /// and then calls the round's own <c>BeginRound()</c>/<c>RequestRetry()</c>), a return
    /// is the launcher's own Presenting reaction or its <c>HubReturnButton</c>, and the
    /// card is <see cref="StoryResultPresenter"/>'s. So a scene without this host behaves
    /// exactly as before it existed (the v1 path is untouched), and a scene with it has one
    /// owner per rule.
    /// </para>
    /// <para>
    /// Why proximity instead of a button or a Station. The circle reads
    /// <c>Networking.LocalPlayer.GetPosition()</c>, which the launcher already reads and
    /// which ClientSim and a real client both answer; it needs no collider, no ray, no
    /// EventSystem, no Station and no keyboard key. VRChat exposes <c>InputUse</c>
    /// globally, not as "the object the player is looking at", so a "press E" prompt would
    /// have to hijack a global input; the project's in-world interaction is a collider plus
    /// a left click, which is a physical button, not a circle. The cost is an accidental
    /// start, handled by the re-arm rule in <see cref="HubRoomMath"/> and by putting the
    /// room spawn outside every circle.
    /// </para>
    /// <para>
    /// No new input gate. The host never reads a move axis and never touches
    /// <c>DesktopMoveInput</c>: the ride gate (owner: <c>BallRideStation</c>) and the round
    /// gate (owner: <c>StoryRoundController</c>) keep exactly their existing meaning.
    /// Walking into a circle starts a level; it does not let a standing player drive.
    /// </para>
    /// <para>
    /// Allocation-free per frame. The zone positions are copied into an array allocated
    /// once in <c>Start</c>; the prompt string is rebuilt only when its id changes; log
    /// lines are written only on a phase change, a refused start whose reason changed, or
    /// a wiring problem. Nothing here is synced (<c>BehaviourSyncMode.None</c>): the loop
    /// is local and single player, the same scope as the launcher and the round.
    /// </para>
    /// <para>
    /// Assembly. One object, never disabled and never parented under the room or the level
    /// (its <c>Update</c> is the phase machine). It must not be on the same object as the
    /// launcher or the round controller, by the project's one-behaviour-per-object
    /// convention, and because a disabled host would freeze the room's prompt with no log.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubRoomHost : UdonSharpBehaviour
    {
        // ------------------------------------------------------------------ links

        // The only place the player is moved and the only thing that opens a round.
        [Header("Loop")]
        [SerializeField] private HubLevelLauncher launcher;

        // Read-only: GetState()/GetLevelId(). The round keeps the only state machine.
        [SerializeField] private StoryRoundController controller;

        // The centre and radius of "the player is in the room" (the whole room, not the
        // circle). Used to decide that a settled round has actually come home.
        [Header("Room")]
        [SerializeField] private Transform hubCenter;
        [SerializeField] private float hubRadiusMeters = 12f;

        // One circle per playable level, index == the launcher's levelEntries index.
        // The circle is a point and a radius; the visible object (the select pad) can be
        // its Transform, because the pad is flat and has no collider.
        [Header("Level circles")]
        [SerializeField] private Transform[] selectZones;
        [SerializeField] private float selectRadiusMeters = 1.2f;

        // How far outside the circle the approach prompt still shows. It never starts
        // anything; it only makes the circle discoverable before the player is inside it.
        [SerializeField] private float promptMarginMeters = 1.5f;

        // The room's own prompt line. Optional: an unwired HUD is a room with no prompt,
        // not an error (the player can still walk into the circle).
        [Header("Prompt (optional)")]
        [SerializeField] private Text promptText;

        // Standing still inside the circle for this long starts the level. A short dwell
        // keeps a player who just crosses the circle on the way to the board from starting
        // a round by accident.
        [Header("Pacing")]
        [SerializeField] private float dwellSeconds = 0.35f;

        // Frames a start may stay in LevelLoading without the round running before the room
        // gives up and shows Hub again. The launcher's own arrival timeout is shorter
        // (arrivalTimeoutFrames, 30 by default); this is the room's outer bound, not a
        // second arrival check.
        [SerializeField] private int loadingTimeoutFrames = 90;

        // How long the settled result stays on the board before the room says "start again".
        [SerializeField] private float resultHoldSeconds = 2.5f;

        // ------------------------------------------------------------------ state

        private int phase = HubRoomMath.PhaseHub;
        private float phaseEnteredSeconds;

        // The circle must be re-armed by a measured frame outside every circle after a
        // start. Start() leaves it unarmed, so a spawn that happens to be inside a circle
        // cannot start a round on load.
        private bool armed;
        private float dwellInZoneSeconds;

        private int loadingFrames;
        private bool startAccepted;
        private bool startFailed;

        private int lastPromptId = -1;
        private int lastRefusalReason;

        // The world positions of the circles, refreshed once per frame from the Transforms.
        // Allocated once in Start; a null slot is parked far away rather than becoming NaN,
        // because a NaN would compare false and could read as "inside" by accident.
        private Vector3[] zoneCache;
        private Vector3 invalidZonePoint;

        private bool warnedBadState;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            phase = HubRoomMath.PhaseHub;
            phaseEnteredSeconds = Time.time;
            armed = false;
            invalidZonePoint = new Vector3(1000000f, 1000000f, 1000000f);

            if (!KatamariMath.IsFiniteFloat(selectRadiusMeters) || selectRadiusMeters <= 0f)
            {
                Debug.LogWarning("[hub-room] selectRadiusMeters=" + selectRadiusMeters
                    + " is not usable; using 1.2");
                selectRadiusMeters = 1.2f;
            }
            if (!KatamariMath.IsFiniteFloat(promptMarginMeters) || promptMarginMeters < 0f)
            {
                promptMarginMeters = 0f;
            }
            if (!KatamariMath.IsFiniteFloat(hubRadiusMeters) || hubRadiusMeters <= 0f)
            {
                Debug.LogWarning("[hub-room] hubRadiusMeters=" + hubRadiusMeters
                    + " is not usable; using 12");
                hubRadiusMeters = 12f;
            }
            if (!KatamariMath.IsFiniteFloat(dwellSeconds) || dwellSeconds < 0f)
            {
                dwellSeconds = 0f;
            }
            if (loadingTimeoutFrames < 1) loadingTimeoutFrames = 1;
            if (!KatamariMath.IsFiniteFloat(resultHoldSeconds) || resultHoldSeconds < 0f)
            {
                resultHoldSeconds = 0f;
            }

            if (launcher == null)
            {
                Debug.LogWarning("[hub-room] no HubLevelLauncher is wired; the room is inert."
                    + " The round and the launcher are unaffected.");
            }
            if (controller == null)
            {
                Debug.LogWarning("[hub-room] no StoryRoundController is wired; the room cannot"
                    + " follow the round. The round itself is unaffected.");
            }
            if (hubCenter == null)
            {
                Debug.LogWarning("[hub-room] no hubCenter is wired; a settled round cannot count"
                    + " as home, so the room stays in Result.");
            }

            int zoneCount = ZoneCount();
            if (zoneCount == 0)
            {
                Debug.LogWarning("[hub-room] no level circles are wired; walking cannot start a"
                    + " level. Wire selectZones[0] to the select pad.");
            }
            else
            {
                zoneCache = new Vector3[zoneCount];
            }

            Debug.Log("[hub-room] ready zones=" + zoneCount
                + " radius=" + selectRadiusMeters
                + " dwell=" + dwellSeconds
                + " hubRadius=" + hubRadiusMeters
                + " state=" + StateText());
        }

        public void Update()
        {
            if (launcher == null || controller == null) return;

            int state = controller.GetState();

            // Both flags are one-frame events, so they must not survive the frame that
            // produced them: a stale startFailed would turn the next accepted start into an
            // immediate failure on its first Loading frame.
            startAccepted = false;
            startFailed = false;

            VRCPlayerApi player = Networking.LocalPlayer;
            bool hasPlayer = player != null;
            Vector3 position = Vector3.zero;
            if (hasPlayer) position = player.GetPosition();

            bool inHub = false;
            if (hasPlayer && hubCenter != null)
            {
                inHub = HubLoopMath.IsWithin(position, hubCenter.position, hubRadiusMeters);
            }

            bool insideZone = false;
            float nearestZoneDistance = invalidZonePoint.x;
            int zoneIndex = -1;
            if (hasPlayer && zoneCache != null)
            {
                zoneIndex = PickZone(position, out insideZone, out nearestZoneDistance);
            }

            // Re-arm and dwell. A start sets armed=false; only a frame measured outside
            // every circle arms it again.
            armed = HubRoomMath.NextArmed(armed, insideZone);
            if (insideZone && armed)
            {
                dwellInZoneSeconds = dwellInZoneSeconds + Time.deltaTime;
            }
            else
            {
                dwellInZoneSeconds = 0f;
            }

            bool nearZone = nearestZoneDistance <= selectRadiusMeters + promptMarginMeters;
            bool startReady = HubRoomMath.CanAutoStart(phase, armed, insideZone,
                dwellInZoneSeconds, dwellSeconds);

            // The start itself. Every legality rule stays in the launcher (round state,
            // transfer already in flight, cooldown, index range); this only decides that the
            // player stood in a circle, and which one.
            if (startReady && zoneIndex >= 0)
            {
                int reason;
                if (launcher.TryStartLevel(zoneIndex, out reason))
                {
                    startAccepted = true;
                    armed = false;
                    dwellInZoneSeconds = 0f;
                    loadingFrames = 0;
                    lastRefusalReason = 0;
                    Debug.Log("[hub-room] auto-start levelIndex=" + zoneIndex
                        + " state=" + state
                        + " distance=" + nearestZoneDistance);
                }
                else if (reason != lastRefusalReason)
                {
                    // Once per reason change: a cooldown refusal repeats every frame while
                    // the player stands in the circle, and the next attempt after the
                    // cooldown is the one that matters.
                    lastRefusalReason = reason;
                    Debug.LogWarning("[hub-room] auto-start refused reason=" + reason
                        + " (" + HubLoopMath.DescribeReason(reason) + ")"
                        + " levelIndex=" + zoneIndex
                        + " state=" + state);
                }
            }

            // The room's outer bound on "loading". The launcher aborts a start that never
            // arrived at the level; this only stops the room from saying "loading" forever
            // if the launcher itself is disabled or mis-wired.
            if (phase == HubRoomMath.PhaseLoading)
            {
                loadingFrames++;
                if (loadingFrames > loadingTimeoutFrames) startFailed = true;
            }
            else
            {
                loadingFrames = 0;
            }

            bool holdElapsed = phase == HubRoomMath.PhaseResult &&
                (Time.time - phaseEnteredSeconds) >= resultHoldSeconds;

            int next;
            bool usable = HubRoomMath.TryAdvance(phase, state, startAccepted, startFailed,
                inHub, holdElapsed, out next);
            if (!usable && !warnedBadState)
            {
                warnedBadState = true;
                Debug.LogWarning("[hub-room] unusable phase/round state (phase=" + phase
                    + " state=" + state + "); the room falls back to Hub.");
            }
            if (next != phase)
            {
                Debug.Log("[hub-room] phase " + HubRoomMath.PhaseText(phase)
                    + " -> " + HubRoomMath.PhaseText(next)
                    + " state=" + state
                    + " inHub=" + inHub
                    + " zone=" + zoneIndex);
                phase = next;
                phaseEnteredSeconds = Time.time;
            }

            int promptId = HubRoomMath.PromptFor(phase, nearZone, insideZone, armed,
                startReady, inHub);
            if (promptId != lastPromptId)
            {
                lastPromptId = promptId;
                WritePrompt(promptId);
            }
        }

        /// <summary>The room phase, for a scene log or a debug readout.</summary>
        public int GetPhase()
        {
            return phase;
        }

        // ------------------------------------------------------------------ circles

        /// <summary>
        /// Refreshes the circle position cache and answers which circle contains the
        /// player, plus the distance to the nearest circle (for the approach prompt).
        /// </summary>
        private int PickZone(Vector3 position, out bool inside, out float nearestDistance)
        {
            inside = false;
            nearestDistance = invalidZonePoint.x;

            if (selectZones == null) return -1;
            if (zoneCache == null || zoneCache.Length != selectZones.Length)
            {
                zoneCache = new Vector3[selectZones.Length];
            }

            for (int i = 0; i < selectZones.Length; i++)
            {
                Vector3 center = invalidZonePoint;
                if (selectZones[i] != null) center = selectZones[i].position;
                zoneCache[i] = center;

                if (selectZones[i] == null) continue;
                float distance = Vector3.Distance(position, center);
                if (KatamariMath.IsFiniteFloat(distance) && distance < nearestDistance)
                {
                    nearestDistance = distance;
                }
            }

            int index;
            if (HubRoomMath.TryPickZone(position, zoneCache, selectRadiusMeters, out index))
            {
                inside = true;
                return index;
            }
            return -1;
        }

        private int ZoneCount()
        {
            if (selectZones == null) return 0;
            return selectZones.Length;
        }

        // ------------------------------------------------------------------ prompt

        private void WritePrompt(int promptId)
        {
            if (promptText == null) return;

            if (promptId == HubRoomMath.PromptApproach)
            {
                promptText.text = "走进房间中央的野餐垫自动开始";
                return;
            }
            if (promptId == HubRoomMath.PromptRetryApproach)
            {
                promptText.text = "走进野餐垫再来一局";
                return;
            }
            if (promptId == HubRoomMath.PromptStandIn)
            {
                promptText.text = "站住别动，马上开始…";
                return;
            }
            if (promptId == HubRoomMath.PromptStarting)
            {
                promptText.text = "开始！";
                return;
            }
            if (promptId == HubRoomMath.PromptReArm)
            {
                promptText.text = "先走出圈外，再走进来才会开始";
                return;
            }
            if (promptId == HubRoomMath.PromptLoading)
            {
                promptText.text = "正在进入关卡…";
                return;
            }
            if (promptId == HubRoomMath.PromptPlaying)
            {
                promptText.text = "关卡进行中";
                return;
            }
            if (promptId == HubRoomMath.PromptResult)
            {
                promptText.text = "上一局已结算，走进野餐垫再来一局";
                return;
            }
            if (promptId == HubRoomMath.PromptResultAway)
            {
                promptText.text = "本局已结算，按关卡里的「回房」按钮返回";
                return;
            }

            promptText.text = "";
        }

        // ------------------------------------------------------------------ small helpers

        private string StateText()
        {
            if (controller == null) return "none";
            return "" + controller.GetState();
        }
    }
}
