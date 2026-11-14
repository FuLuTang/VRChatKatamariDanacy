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
    /// Lifecycle: <c>SetInputEnabled(false)</c>, station exit, respawn and the
    /// behaviour being disabled all zero both axes and push that zero once, so the
    /// ball brakes instead of holding the last pressed direction. Enabling it again
    /// pushes nothing: the axes are already zero until the player moves.
    /// </para>
    /// <para>
    /// Assembly: needs its own GameObject (two UdonSharpBehaviours on one object
    /// means UdonSharp silently disables one of them) and references a motor on a
    /// different object. Nothing here reads Unity's legacy Input class.
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
        private bool inputEnabled = true;

        /// <summary>
        /// Gate for a round controller: a disabled adapter holds the ball at zero
        /// axes even while the player keeps pressing keys.
        /// </summary>
        public void SetInputEnabled(bool enabled)
        {
            inputEnabled = enabled;
            if (enabled) return;
            ZeroAxes();
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            if (!inputEnabled) return;
            if (!KatamariMath.IsFiniteFloat(value)) return;
            if (value == forward) return;
            forward = value;
            PushAxes();
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            if (!inputEnabled) return;
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
