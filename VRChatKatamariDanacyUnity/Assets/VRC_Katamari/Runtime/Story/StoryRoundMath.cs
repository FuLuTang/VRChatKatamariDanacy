using VRCKatamari.Core;

namespace VRCKatamari.Story
{
    /// <summary>
    /// The round clock of one story round: how much time is left, and the one text
    /// convention a round shows a length with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the round's own arithmetic, and deliberately nothing else. The star table
    /// moved to <see cref="StoryStarMath"/> when scoring became an optional module: the
    /// round has to time, settle and freeze whether or not anyone grades it, so the clock
    /// must not be reachable only through the scoring rule. Nothing here reads a scene, a
    /// motor or a clock: the caller passes its own time readings, so the rule can be
    /// tested without a Unity scene and the round has no second copy of it.
    /// </para>
    /// <para>
    /// The length text (<see cref="HundredthsText"/>) is not arithmetic at all, but it is
    /// the round's display convention for the one number the round measures, and both the
    /// HUD (which the round owns) and the result card (which the optional presenter owns)
    /// must truncate it the same way. It lives in this static class because both sides may
    /// use it and neither may depend on the other; a copy on each side would drift.
    /// </para>
    /// <para>
    /// UdonSharp: static class, static methods only, no fields, no overloads, no
    /// allocation. Times are seconds on the caller's clock, in the project's single time
    /// base; lengths are metres on its single world scale.
    /// </para>
    /// </remarks>
    public static class StoryRoundMath
    {
        /// <summary>
        /// Remaining round time in seconds, clamped to [0, timeLimitSeconds].
        /// </summary>
        /// <remarks>
        /// startTimeSeconds is when the round began and nowSeconds is the caller's
        /// current clock reading, both on the same monotonic clock in seconds
        /// (UnityEngine.Time.time); timeLimitSeconds is the level's time_limit_s and
        /// must be positive and finite. The result never goes negative (an expired
        /// round reads 0, so a HUD cannot show "-3 s") and never exceeds the limit (a
        /// clock read before the recorded start reads the full limit, which is what a
        /// frame that ran during startup should show). Non-finite input, a
        /// non-positive limit and a difference that is not representable are refused.
        /// </remarks>
        public static bool TryRemainingSeconds(float startTimeSeconds, float nowSeconds,
            float timeLimitSeconds, out float remainingSeconds)
        {
            remainingSeconds = 0f;

            if (!KatamariMath.IsFiniteFloat(startTimeSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(nowSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(timeLimitSeconds)) return false;
            if (timeLimitSeconds <= 0f) return false;

            float elapsedSeconds = nowSeconds - startTimeSeconds;
            float remaining = timeLimitSeconds - elapsedSeconds;
            if (!KatamariMath.IsFiniteFloat(remaining)) return false;

            if (remaining > timeLimitSeconds) remaining = timeLimitSeconds;
            if (remaining < 0f) remaining = 0f;
            remainingSeconds = remaining;
            return true;
        }

        /// <summary>
        /// A metric length to two decimals, truncated rather than rounded.
        /// </summary>
        /// <remarks>
        /// Truncation is the same convention the playground HUD uses, and it is the
        /// honest one here: a shown number must never claim a size the ball did not
        /// reach, so 1.2499 m reads 1.24 m next to a 1.25 m threshold it missed. Shared
        /// by the round's progress line and the result card so the two can never show the
        /// same measurement with different last digits.
        /// </remarks>
        public static string HundredthsText(float value)
        {
            return (UnityEngine.Mathf.Floor(value * 100f) / 100f).ToString();
        }
    }
}
