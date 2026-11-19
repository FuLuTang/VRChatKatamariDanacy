namespace VRCKatamari.Riding
{
    /// <summary>
    /// Whether an Interact press must be ignored because it rode in on the same trigger press
    /// that is charging, or has just fired, a dash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owner 2026-11-18. VR's trigger raises two different events from one press:
    /// <c>InputUse</c> (the dash charge, delivered to VrStickMoveInput) and <c>Interact</c>
    /// (delivered to whatever collider the hand points at). The dash needs the trigger, so a
    /// charge aimed at a collider that is pressable while riding would also press it. The
    /// rule is therefore: while the local player is riding, such an Interact press is ignored
    /// while the motor is charging or in the burst. Nothing changes outside a dash.
    /// </para>
    /// <para>
    /// Callers: <c>BallSeatInteract</c> (boarding) and <c>BallRideExitButton</c> (the
    /// rider's "get off" panel), through <c>BallRideStation.ShouldIgnoreDashInteract</c>. The
    /// result card's buttons are uGUI Buttons, not Interact colliders, and are only shown
    /// while the round gate is closed (no dash can run), so they do not use this rule.
    /// </para>
    /// </remarks>
    public static class RideDashInteractMath
    {
        /// <summary>
        /// Whether an Interact press should be dropped: the local player is seated and the
        /// dash is charging or bursting.
        /// </summary>
        public static bool ShouldIgnoreInteract(bool localPlayerSeated, bool dashCharging,
            bool dashing)
        {
            return localPlayerSeated && (dashCharging || dashing);
        }
    }
}
