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

        private bool seated;

        private void Start()
        {
            // Start runs before the local player can sit, so this always closes the
            // ride gate: a standing player must not be able to steer the ball.
            SetRideGate(false);
        }

        public override void Interact()
        {
            if (seated) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || station == null) return;
            station.UseStation(player);
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
            Debug.Log("[ride] seated entered");
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            // Another exit path may already have dropped the local seated state;
            // the state cleanup below is unconditional, only the log is guarded.
            bool wasSeated = seated;
            seated = false;
            DisableInteractive = false;
            SetRideGate(false);
            if (wasSeated) Debug.Log("[ride] seated exited");
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            bool wasSeated = seated;
            seated = false;
            DisableInteractive = false;
            SetRideGate(false);
            if (!wasSeated || station == null) return;
            // A respawn can leave the avatar in the station without a station-exit
            // callback, which would leave Interact disabled for good. The lab took
            // the same precaution (StationLabDriver.OnPlayerRespawn).
            Debug.Log("[ride] respawn while seated: leaving station");
            station.ExitStation(player);
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
