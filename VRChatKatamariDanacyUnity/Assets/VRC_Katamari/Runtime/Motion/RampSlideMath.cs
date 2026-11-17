using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Motion
{
    /// <summary>
    /// The "rest, then slide" latch for the downhill gravity component
    /// (BALL_INERTIA.md section 4). The ball only starts to slide down a slope after
    /// it has been still for a moment; while it is moving, or the player is pushing,
    /// no extra downhill acceleration is applied.
    /// </summary>
    /// <remarks>
    /// Stateless and allocation-free; the caller owns the two state values
    /// (restSeconds, sliding) and hands them back each step. The slope test
    /// (walkable support slope, not a wall, not flat) and the gravity itself stay
    /// with the caller and SupportSurfaceMath.TryApplyRampTangentialGravity; the
    /// acceleration it applies is g * sin(slope) * sizeScale, so in diameters per
    /// second squared it is the same at any ball size.
    ///
    /// Rule: rest accumulates while the planar speed is below restSpeedMps and the
    /// drive intent is at most NoDriveIntent, and resets otherwise. The latch sets
    /// when rest reaches restHoldSeconds on a slope; once set it stays on while the
    /// ball remains on a slope with no drive intent (the slide itself is motion, so
    /// the rest timer cannot be what keeps it on), and clears on any push or when
    /// the ball leaves the slope.
    /// </remarks>
    public static class RampSlideMath
    {
        /// <summary>Drive intent (|forward| + |lateral|) at or below this counts as no push.</summary>
        public const float NoDriveIntent = 0.05f;

        public static bool TryStepRestLatch(
            float restSeconds,
            bool sliding,
            float planarSpeed,
            float restSpeedMps,
            float driveIntent,
            bool onSlope,
            float deltaTime,
            float restHoldSeconds,
            out float nextRestSeconds,
            out bool nextSliding)
        {
            nextRestSeconds = 0f;
            nextSliding = false;
            if (!KatamariMath.IsFiniteFloat(restSeconds) || restSeconds < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(planarSpeed) || planarSpeed < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(restSpeedMps) || restSpeedMps < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(driveIntent) || driveIntent < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(deltaTime) || deltaTime <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(restHoldSeconds) || restHoldSeconds < 0f) return false;

            bool pushing = driveIntent > NoDriveIntent;
            bool still = !pushing && planarSpeed < restSpeedMps;
            float rest = still ? restSeconds + deltaTime : 0f;
            if (!KatamariMath.IsFiniteFloat(rest)) return false;

            bool latched = onSlope && !pushing && (sliding || rest >= restHoldSeconds);
            nextRestSeconds = rest;
            nextSliding = latched;
            return true;
        }
    }
}
