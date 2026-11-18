using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;

namespace VRCKatamari.Input
{
    /// <summary>
    /// Reads both VR controller thumbsticks every frame and drives one ball through the
    /// motor's <c>SetMoveAxes(forward, turn, lateral)</c>, in the original two-stick tank
    /// scheme (default) or the simple scheme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Desktop stays with <see cref="DesktopMoveInput"/>: this behaviour does nothing unless
    /// the local player is in VR (<c>VRCPlayerApi.IsUserInVR</c>), and DesktopMoveInput
    /// should be told to ignore VR (see the README's assembly notes), so exactly one
    /// adapter writes the motor's axes on each platform.
    /// </para>
    /// <para>
    /// Device read: <c>Input.GetAxisRaw</c> on the four
    /// <c>Oculus_CrossPlatform_{Primary,Secondary}Thumbstick{Horizontal,Vertical}</c>
    /// axes, which the VRChat project's InputManager defines (deadzone 0.19, vertical
    /// inverted so +1 is forward). The Udon input events cannot carry the tank scheme:
    /// <c>InputLookHorizontal</c> stops reporting the right stick when the player has
    /// Comfort Turning on, and the right stick's y is not a move event. The axis names
    /// do not follow the player's VRChat rebinding; this is documented, not hidden.
    /// All mapping math is in <see cref="TankStickMath"/>.
    /// </para>
    /// <para>
    /// Gates are the same two as DesktopMoveInput's, same owners: <c>SetRideGate</c>
    /// (BallRideStation) and <c>SetRoundGate</c> (StoryRoundController). Both default to
    /// open. When the effective gate closes, or on station exit, respawn or disable, the
    /// axes are zeroed and pushed once.
    /// </para>
    /// <para>
    /// Dash (owner 2026-11-18: the original's Charge'n'Roll). The LEFT controller trigger
    /// (<c>InputUse</c>, left hand only) holds a charge and releases it; the motor owns every
    /// rule - the tier time by ball size, the in-place spin from gacha count 3, the burst, the
    /// cooldown, the boost energy and the huff (DashMath, CONTACT_MODEL_V2 §3.8). The charge
    /// only runs while both gates are open. Desktop left-click also raises <c>InputUse</c>, so
    /// this handler refuses everything outside VR: the desktop adapter owns Shift.
    /// </para>
    /// <para>
    /// Why the left trigger and not the grip: the trigger is the use input, which the owner
    /// named; the cost is that a trigger pull also raises <c>Interact</c> on whatever the hand
    /// points at (they are different events, and both fire - see the README's platform facts),
    /// so aiming at the ride panel's buttons while charging will also press them. That is a
    /// known, documented conflict, not silently ignored; the grip has no such object-level
    /// pairing but is not the input the owner chose.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class VrStickMoveInput : UdonSharpBehaviour
    {
        private const string LeftXAxis = "Oculus_CrossPlatform_PrimaryThumbstickHorizontal";
        private const string LeftYAxis = "Oculus_CrossPlatform_PrimaryThumbstickVertical";
        private const string RightXAxis = "Oculus_CrossPlatform_SecondaryThumbstickHorizontal";
        private const string RightYAxis = "Oculus_CrossPlatform_SecondaryThumbstickVertical";

        [SerializeField] private KatamariBallMotor motor;

        [Tooltip("0 = original two-stick tank scheme (default), 1 = simple (left stick rolls, right stick x turns).")]
        [SerializeField] private int controlMode = TankStickMath.ModeTank;

        [Tooltip("Stick deadzone when no hub settings are wired, on top of the InputManager's own 0.19.")]
        [SerializeField] private float deadzone = TankStickMath.DefaultDeadzone;

        [Tooltip("Optional hub settings for hand swap, stick deadzone and turn sensitivity.")]
        [SerializeField] private HubSettingsHost settings;

        private bool settingsRead;
        private int settingsRevision;
        private bool swapHands;
        private float settingsDeadzone;
        private float turnSensitivity;

        private bool rideGateOpen = true;
        private bool roundGateOpen = true;

        private float forward;
        private float turn;
        private float lateral;

        private bool isVr;
        private bool vrKnown;

        public void SetRideGate(bool open)
        {
            bool wasOpen = InputOpen();
            rideGateOpen = open;
            if (wasOpen && !InputOpen()) ZeroAxes();
        }

        public void SetRoundGate(bool open)
        {
            bool wasOpen = InputOpen();
            roundGateOpen = open;
            if (wasOpen && !InputOpen()) ZeroAxes();
        }

        /// <summary>Switches scheme; unknown values are refused. The entry the owner's settings UI calls.</summary>
        public void SetControlMode(int mode)
        {
            if (mode != TankStickMath.ModeTank && mode != TankStickMath.ModeSimple) return;
            if (mode == controlMode) return;
            controlMode = mode;
            Debug.Log("[input] stick mode=" + (mode == TankStickMath.ModeTank ? "tank" : "simple"));
        }

        public int GetControlMode()
        {
            return controlMode;
        }

        private void RefreshStickSettings()
        {
            // Read on the first open frame even at revision zero, then only on changes.
            // This catches a restore or an edit made while the input gates were closed.
            int revision = settings.GetRevision();
            if (settingsRead && revision == settingsRevision) return;
            swapHands = settings.IsSwapHands();
            settingsDeadzone = settings.GetDeadzone();
            turnSensitivity = settings.GetTurnSensitivity();
            settingsRevision = revision;
            settingsRead = true;
        }

        private void Update()
        {
            if (!TryDetectVr()) return;
            if (!InputOpen()) return;

            bool hasSettings = settings != null;
            if (hasSettings) RefreshStickSettings();

            float lx = UnityEngine.Input.GetAxisRaw(LeftXAxis);
            float ly = UnityEngine.Input.GetAxisRaw(LeftYAxis);
            float rx = UnityEngine.Input.GetAxisRaw(RightXAxis);
            float ry = UnityEngine.Input.GetAxisRaw(RightYAxis);

            float f;
            float t;
            float l;
            // A refused frame keeps the last axes, like the desktop path.
            if (!TankStickMath.TryMapWithSettings(controlMode, lx, ly, rx, ry,
                hasSettings && swapHands, hasSettings ? settingsDeadzone : deadzone,
                hasSettings ? turnSensitivity : TankStickMath.DefaultTurnSensitivity,
                out f, out t, out l)) return;

            if (f == forward && t == turn && l == lateral) return;
            forward = f;
            turn = t;
            lateral = l;
            PushAxes();
        }

        /// <summary>
        /// The dash charge: left trigger down starts it, release ends it. Only the left hand
        /// counts (the owner's input); the motor decides whether the charge was long enough
        /// and whether a burst fires. A press while a gate is closed, or while huffing or
        /// cooling down, is refused and logged.
        /// </summary>
        public override void InputUse(bool value, UdonInputEventArgs args)
        {
            if (args.handType != HandType.LEFT) return;
            if (!TryDetectVr()) return;
            if (motor == null) return;

            if (value)
            {
                if (!InputOpen()) return;
                bool accepted = motor.BeginDashCharge();
                Debug.Log(accepted
                    ? "[input] dash charge begin"
                    : "[input] dash charge refused (huff, cooldown or already charging)");
                return;
            }

            // The release always reaches the motor, even if a gate closed while charging, so
            // a latched charge can never survive the button going up.
            if (!InputOpen())
            {
                motor.CancelDashCharge();
                return;
            }
            int released = motor.ReleaseDashCharge();
            Debug.Log(released == DashMath.ReleaseDash
                ? "[input] dash fired"
                : released == DashMath.ReleaseNotFull
                    ? "[input] dash released, charge not full"
                    : "[input] dash release ignored");
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

        private void OnDisable()
        {
            ZeroAxes();
        }

        // The one VR test both the per-frame path and the trigger handler use. IsUserInVR is
        // stable for a session, so it is read once; Update can run before Networking has a
        // local player, in which case nothing is known yet and the next frame retries.
        private bool TryDetectVr()
        {
            if (vrKnown) return isVr;
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null) return false;
            isVr = local.IsUserInVR();
            vrKnown = true;
            return isVr;
        }

        private bool InputOpen()
        {
            return rideGateOpen && roundGateOpen;
        }

        private void ZeroAxes()
        {
            forward = 0f;
            turn = 0f;
            lateral = 0f;
            // A charge must not survive the station exit, a respawn or a closing gate.
            if (motor != null) motor.CancelDashCharge();
            // Desktop players never drive through this adapter; pushing a zero there
            // would fight DesktopMoveInput's axes.
            if (!isVr) return;
            PushAxes();
        }

        private void PushAxes()
        {
            if (motor == null) return;
            motor.SetMoveAxes(forward, turn, lateral);
        }
    }
}
