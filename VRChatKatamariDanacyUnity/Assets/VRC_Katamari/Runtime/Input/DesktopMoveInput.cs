using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.Input
{
    /// <summary>
    /// Turns the local player's move axes into one ball's three motor axes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place in the runtime that reads a device axis. It owns no
    /// gameplay rule: no speed, growth, pickup or round state, and it never touches
    /// the motor's mode, ledger or transforms. Its whole output is the motor's
    /// documented input seam, <c>SetMoveAxes(forward, turn, lateral)</c>.
    /// </para>
    /// <para>
    /// Axes: vertical is forward (+1 = forward) and horizontal is turn (+1 = turn
    /// right, since the motor adds <c>turn * turnCap * dt</c> to the heading and
    /// derives forward as (+sin, +cos) of that heading). Lateral has no desktop
    /// binding yet and is always pushed as 0. Both stored values are raw device
    /// values; the motor clamps to [-1, 1] itself, so this layer does not duplicate
    /// that clamp. Non-finite values are dropped: the motor keeps the axes of a
    /// rejected frame, so letting a NaN through would freeze that ball for good.
    /// </para>
    /// <para>
    /// Two gates, one owner each. <c>SetRideGate</c> belongs to BallRideStation: it
    /// closes in that behaviour's Start (a standing player must not steer the ball)
    /// and opens only while the local player is seated. <c>SetRoundGate</c> belongs
    /// to StoryRoundController: it closes at the bell and opens again in BeginRound.
    /// Both default to open, so a scene with neither owner still drives - the
    /// keyboard lab <c>Labs/StoryLevel</c> has no station and no round controller.
    /// One gate per owner is deliberate: with a single shared switch the ride host's
    /// open on the next sit-down undid the round controller's close at the bell, so
    /// leaving and re-entering the seat re-opened input after the round had ended,
    /// and either owner's write silently overwrote the other's.
    /// </para>
    /// <para>
    /// Lifecycle: effective input is both gates open. Whenever that becomes closed -
    /// a gate closing, station exit, respawn, the behaviour being disabled - this
    /// behaviour zeroes both axes and pushes that zero once, so the ball brakes
    /// instead of holding the last pressed direction. Re-opening a gate pushes
    /// nothing: the axes are already zero until the player moves.
    /// </para>
    /// <para>
    /// Assembly: its own GameObject by project convention (clear ownership and a
    /// readable self-check; a rebuilt hierarchy cannot leave a stale copy running),
    /// and it references a motor on a different object. Sharing an object is legal -
    /// the engine does not disable a second behaviour on the same object
    /// (Docs/Design/BallMotor/README.md, section on assembly constraints) - but the
    /// ball's station object is reserved for BallRideStation, which has to share it
    /// with the VRCStation. Nothing here reads Unity's legacy Input class.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DesktopMoveInput : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;

        // Adapter state only: the last axis values this layer pushed. No other
        // behaviour may write the motor's axes.
        private float forward;
        private float turn;

        // Gate state. Each field is written by exactly one owner; both start open so a
        // scene without a ride station or a round controller still drives.
        private bool rideGateOpen = true;
        private bool roundGateOpen = true;

        /// <summary>
        /// The ride gate, owned by BallRideStation: open only while the local player is
        /// seated in the ball's station.
        /// </summary>
        public void SetRideGate(bool open)
        {
            bool wasOpen = InputOpen();
            rideGateOpen = open;
            if (wasOpen && !InputOpen()) ZeroAxes();
        }

        /// <summary>
        /// The round gate, owned by StoryRoundController: closed from the bell until the
        /// next BeginRound.
        /// </summary>
        public void SetRoundGate(bool open)
        {
            bool wasOpen = InputOpen();
            roundGateOpen = open;
            if (wasOpen && !InputOpen()) ZeroAxes();
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            if (!InputOpen()) return;
            if (!KatamariMath.IsFiniteFloat(value)) return;
            if (value == forward) return;
            forward = value;
            PushAxes();
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            if (!InputOpen()) return;
            if (!KatamariMath.IsFiniteFloat(value)) return;
            if (value == turn) return;
            turn = value;
            PushAxes();
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            ZeroAxes();
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            ZeroAxes();
        }

        // OnDisable is a Unity lifecycle message forward to Udon, not a member of
        // UdonSharpBehaviour, so it is an ordinary method and must not be override.
        private void OnDisable()
        {
            ZeroAxes();
        }

        private bool InputOpen()
        {
            return rideGateOpen && roundGateOpen;
        }

        private void ZeroAxes()
        {
            forward = 0f;
            turn = 0f;
            PushAxes();
        }

        private void PushAxes()
        {
            // A missing link is a wiring bug the scene owner sees as a still ball;
            // nothing here can log per frame or repair it.
            if (motor == null) return;
            motor.SetMoveAxes(forward, turn, 0f);
        }
    }
}
