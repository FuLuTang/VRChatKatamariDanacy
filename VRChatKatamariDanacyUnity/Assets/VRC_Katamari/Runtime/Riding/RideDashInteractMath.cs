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
    /// charge aimed at the rider's own panel would also press its buttons - the "get off"
    /// button above all. The rule is therefore: while the local player is riding, a panel or
    /// result-card Interact press is ignored while the motor is charging or in the burst.
    /// Nothing changes outside a dash.
    /// </para>
    /// <para>
    /// A dash charge can only START while both input gates are open, and the ride gate is
    /// open only while the local player is seated, so "dash input active" already implies
    /// "riding". Callers that also have the seated flag pass it anyway; callers that do not
    /// (the result card's retry button, which has no ride link) use
    /// <see cref="IsDashInputActive"/> and rely on that invariant.
    /// </para>
    /// </remarks>
    public static class RideDashInteractMath
    {
        /// <summary>Whether the dash is charging or bursting right now.</summary>
        public static bool IsDashInputActive(bool dashCharging, bool dashing)
        {
            return dashCharging || dashing;
        }

        /// <summary>
        /// Whether a panel/result-card Interact press should be dropped: the local player is
        /// seated and the dash is charging or bursting.
        /// </summary>
        public static bool ShouldIgnoreInteract(bool localPlayerSeated, bool dashCharging,
            bool dashing)
        {
            return localPlayerSeated && IsDashInputActive(dashCharging, dashing);
        }
    }
}
