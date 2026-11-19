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
    /// This is the desktop device adapter (VR is <see cref="VrStickMoveInput"/>). It owns no
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
    /// Desktop only. A VR player's left stick also raises <c>InputMoveVertical/Horizontal</c>,
    /// but VR is driven by <see cref="VrStickMoveInput"/> (both thumbsticks, tank or simple
    /// scheme), so both handlers return at once while the local player is in VR. Exactly one
    /// adapter writes the motor's axes on each platform.
    /// </para>
    /// <para>
    /// Two gates, one owner each. <c>SetRideGate</c> belongs to BallRideStation: it
    /// closes in that behaviour's Start (a standing player must not steer the ball)
    /// and opens only while the local player is seated. <c>SetRoundGate</c> belongs
    /// to StoryRoundController: it closes at the bell and opens again in BeginRound.
    /// Both default to open, so a scene with neither owner still drives - the
    /// keyboard-only lab scene has no station and no round controller.
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
    /// DEV-ONLY operator overlay. <c>debugDriveEnabled</c> plus <c>debugAxisX</c>,
    /// <c>debugAxisZ</c> and <c>debugDriveSeconds</c> let an operator in Play feed the two
    /// axes from outside this behaviour (Udon program variables, written with
    /// <c>SetProgramVariable</c> on the backing <c>UdonBehaviour</c>), because MCP cannot
    /// hold a keyboard key and a probe's own <c>SetMoveAxes</c> call is overwritten by the
    /// next device frame. It is off by default, and a scene that never writes the fields
    /// behaves exactly as before: the device handlers keep their order and <c>Update</c>
    /// returns after one bool test. While it is on, the debug axes go through this same
    /// behaviour - same gates, same finite-value drop, same <c>PushAxes</c> - so a standing
    /// player still cannot steer. Override live state is not synced to anyone.
    /// Operator steps and the acceptance run: Docs/Design/ControlIntent/DEBUG_DRIVE.md.
    /// </para>
    /// <para>
    /// Assembly: its own GameObject by project convention (clear ownership and a
    /// readable self-check; a rebuilt hierarchy cannot leave a stale copy running),
    /// and it references a motor on a different object. Sharing an object is legal -
    /// the engine does not disable a second behaviour on the same object
    /// (Docs/Design/BallMotor/README.md, section on assembly constraints) - but the
    /// ball's station object is reserved for BallRideStation, which has to share it
    /// with the VRCStation. The move axes come from the Udon events, not from Unity's
    /// legacy Input; the one legacy read is the dash charge's Shift key (owner
    /// 2026-11-18: VR holds the left trigger, PC holds Shift), polled in Update because it
    /// has no Udon event of its own.
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

        // ---- DEV ONLY: operator drive override (default off; see the class remarks) ----
        // These four are Udon program variables on purpose: the UdonSharp proxy component is
        // only an edit-time shadow, so an operator writes the backing UdonBehaviour's variables
        // (SetProgramVariable) and Update below polls them. The defaults are the release
        // behaviour, so a scene that never touches them drives exactly as it always did.
        [Header("DEV ONLY - operator drive override (leave off in release)")]
        [Tooltip("When on, the two axes below replace the device axes. The ride and round gates still apply.")]
        [SerializeField] private bool debugDriveEnabled = false;
        [Tooltip("Turn axis, the same one InputMoveHorizontal carries (+1 = turn right). Clamped to [-1, 1].")]
        [SerializeField] private float debugAxisX = 0f;
        [Tooltip("Forward axis, the same one InputMoveVertical carries (+1 = forward). Clamped to [-1, 1].")]
        [SerializeField] private float debugAxisZ = 0f;
        [Tooltip("Seconds until the override clears itself. 0 (the default) = until the operator clears it.")]
        [SerializeField] private float debugDriveSeconds = 0f;

        // The override state this behaviour last applied. Kept so the toggle logs once rather
        // than per frame, and so a build where nobody enables it costs Update one bool test.
        private bool debugDriveActive;
        private float debugDriveStartTime;

        // Dash charge: the previous Shift state, so only the press and the release edges are
        // acted on (owner 2026-11-18: hold Shift to charge, release to fire; VR holds the left
        // trigger). Held Shift is VRChat's desktop run key, but a seated player is immobilised,
        // so the two never fight; the gate check below keeps a standing or finished-round
        // player from charging.
        private bool shiftWasDown;

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
            // The DEV-ONLY override owns the axes while it is on; a real key press must not
            // fight it. Off (the default) this is one bool test before the unchanged path.
            if (debugDriveEnabled) return;
            if (IsVrPlayer()) return;
            if (!InputOpen()) return;
            if (!KatamariMath.IsFiniteFloat(value)) return;
            if (value == forward) return;
            forward = value;
            PushAxes();
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            if (debugDriveEnabled) return;
            if (IsVrPlayer()) return;
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

        // VR players drive through VrStickMoveInput, which reads both thumbsticks; the left
        // stick still raises InputMove* here, so without this check the two adapters would
        // write the motor's axes in turns. Desktop (and ClientSim) is never in VR.
        private bool IsVrPlayer()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            return local != null && local.IsUserInVR();
        }

        private bool InputOpen()
        {
            return rideGateOpen && roundGateOpen;
        }

        private void ZeroAxes()
        {
            forward = 0f;
            turn = 0f;
            // 非本平台不写共享马达，也不取消另一输入适配器的冲刺。
            if (IsVrPlayer()) return;
            if (motor != null) motor.CancelDash();
            PushAxes();
        }

        // Update is a Unity lifecycle message forward to Udon, not a member of
        // UdonSharpBehaviour, so it is an ordinary method and must not be override. Its
        // device job is the dash trigger (Shift has no Udon event); the rest is the DEV-ONLY
        // override, which returns after one test when the flag is off.
        private void Update()
        {
            StepDashInput();

            if (!debugDriveEnabled)
            {
                if (!debugDriveActive) return;
                debugDriveActive = false;
                Debug.Log("[input] debug drive off");
                ZeroAxes();
                return;
            }

            if (!debugDriveActive) BeginDebugDrive();

            if (DebugDriveMath.HasExpired(debugDriveStartTime, Time.time, debugDriveSeconds))
            {
                // Clear the operator's flag too, so a Play-mode Inspector or a read-back shows
                // the override is over rather than merely not applying.
                debugDriveEnabled = false;
                debugDriveActive = false;
                Debug.Log("[input] debug drive expired after=" + debugDriveSeconds + "s");
                ZeroAxes();
                return;
            }

            // The same gates as the device path. While either is closed the ball is braked and
            // the override pushes nothing, which is what keeps a standing player, a settled
            // round and a respawn undriven even with the override on.
            if (!InputOpen())
            {
                if (forward != 0f || turn != 0f) ZeroAxes();
                return;
            }

            PushDebugAxes();
        }

        // The dash charge (owner 2026-11-18: PC holds Shift; VR holds the left trigger in
        // VrStickMoveInput). Either Shift key, press starts the charge, release ends it, and
        // both only while the gates are open. VR players are refused here so each platform has
        // exactly one dash entry. The edge is tracked even while a gate is closed, so holding
        // Shift through a gate opening does not charge a dash nobody pressed - and a release
        // after a gate closed cancels instead of firing.
        private void StepDashInput()
        {
            if (IsVrPlayer())
            {
                shiftWasDown = false;
                return;
            }

            bool down = UnityEngine.Input.GetKey(KeyCode.LeftShift)
                || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (down == shiftWasDown) return;
            shiftWasDown = down;

            if (motor == null) return;

            if (down)
            {
                if (!InputOpen()) return;
                bool accepted = motor.BeginDashCharge();
                Debug.Log(accepted
                    ? "[input] dash charge begin"
                    : "[input] dash charge refused (huff, no dash life or dash already active)");
                return;
            }

            if (!InputOpen())
            {
                motor.CancelDash();
                return;
            }
            motor.ReleaseDashCharge();
        }

        private void BeginDebugDrive()
        {
            float axisX;
            float axisZ;
            // The announcement clamps as well, so the line shows the axes that will actually
            // run. A non-finite pair is announced as (0,0); PushDebugAxes keeps dropping it,
            // exactly like a device frame, instead of pushing a repaired value.
            if (!DebugDriveMath.TryClampAxes(debugAxisX, debugAxisZ, out axisX, out axisZ))
            {
                axisX = 0f;
                axisZ = 0f;
            }
            debugDriveActive = true;
            debugDriveStartTime = Time.time;
            Debug.Log("[input] debug drive on axes=(" + axisX + "," + axisZ + ") seconds="
                + debugDriveSeconds);
        }

        private void PushDebugAxes()
        {
            float axisX;
            float axisZ;
            // The device path's validation, reused: a non-finite value is dropped and the last
            // accepted axis kept, because the motor holds the axes of a rejected frame. A push
            // happens only when a clamped value changed, the same traffic as holding a key.
            if (!DebugDriveMath.TryClampAxes(debugAxisX, debugAxisZ, out axisX, out axisZ)) return;
            if (axisX == turn && axisZ == forward) return;
            turn = axisX;
            forward = axisZ;
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
