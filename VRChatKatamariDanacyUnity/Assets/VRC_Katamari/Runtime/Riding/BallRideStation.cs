using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
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
    /// fix_seated_prompt.md). The exit is one-way by design in the scene: Disable
    /// Station Exit is on, so <see cref="BallRideExitButton"/> is the only door out.
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
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallRideStation : UdonSharpBehaviour
    {
        // Both live on this GameObject: the station provides the seat and the
        // station callbacks, the collider provides the click.
        [SerializeField] private VRC.SDK3.Components.VRCStation station;

        // The local player's device-to-axis adapter, on its own object.
        [SerializeField] private DesktopMoveInput input;

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

        private bool seated;

        private void Start()
        {
            // Start runs before the local player can sit, so this always closes the
            // ride gate: a standing player must not be able to steer the ball.
            SetRideGate(false);
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
            DisableInteractive = true;
            SetRideGate(true);
            // After the ride gate: the round controller opens its own gate in BeginRound,
            // and the input condition needs both open to be true on the sit-down frame.
            // In any state but Idle the controller ignores the call, so sitting down
            // after the round ended or mid-round cannot restart it.
            if (roundController != null) roundController.NotifyRiderSeated();
            if (seatEye != null) seatEye.BeginSeated();
            Debug.Log("[ride] seated entered");
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            // Another exit path may already have dropped the local seated state, so the
            // cleanup below runs unconditionally and the log reports what it found.
            bool wasSeated = seated;
            seated = false;
            DisableInteractive = false;
            SetRideGate(false);
            if (seatEye != null) seatEye.EndSeated();
            // Unconditional and reason-coded. It used to log only when `wasSeated`, and that
            // asymmetry made a real incident unattributable: a ClientSim exit left no line
            // here at all, and "this callback never ran" was indistinguishable from "another
            // path had already cleared the flag". One line per exit, and it names the links
            // it just used, so an unwired field is visible in the same line as the exit.
            Debug.Log("[ride] station exit reason=station-exit wasSeated=" + wasSeated
                + " inputWired=" + (input != null) + " seatEyeWired=" + (seatEye != null));
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            bool wasSeated = seated;
            seated = false;
            DisableInteractive = false;
            SetRideGate(false);
            // Before the station exit below: the exit can deliver OnStationExited (which does
            // the same cleanup) but a path that keeps the avatar in the station still has to
            // put the seat back, or the next ride would start from a moved seat.
            if (seatEye != null) seatEye.EndSeated();
            if (!wasSeated || station == null) return;
            // A respawn can leave the avatar in the station without a station-exit
            // callback, which would leave Interact disabled for good. The lab took
            // the same precaution (StationLabDriver.OnPlayerRespawn).
            Debug.Log("[ride] respawn while seated: leaving station");
            station.ExitStation(player);
        }

        /// <summary>
        /// A new avatar is a new seated pose, so the seat offset measured for the previous
        /// avatar is wrong: measure again while seated, ignore it while standing.
        /// </summary>
        public override void OnAvatarChanged(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            if (!seated || seatEye == null) return;
            seatEye.BeginSeated();
        }

        /// <summary>
        /// Avatar scaling changes the avatar's proportions the same way a new avatar does,
        /// and this event fires for scaling as well as for a switch.
        /// </summary>
        public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float previousEyeHeight)
        {
            if (player == null || !player.isLocal) return;
            if (!seated || seatEye == null) return;
            seatEye.BeginSeated();
        }

        /// <summary>
        /// Whether the local player is currently sitting in this ball's station.
        /// Read by the seat-aware lab probe; gameplay code should not need it.
        /// </summary>
        public bool IsLocalPlayerSeated()
        {
            return seated;
        }

        private void SetRideGate(bool open)
        {
            if (input == null) return;
            input.SetRideGate(open);
        }
    }
}
