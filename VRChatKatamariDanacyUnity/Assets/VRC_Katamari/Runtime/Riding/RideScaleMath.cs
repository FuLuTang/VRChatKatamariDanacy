using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// How big the rider is: the eye height to ask VRChat for so that the standing rider's feet
    /// are on the ground and the hands reach the ball at every ball size, plus the clamp, the
    /// smoothing, the rate limit and the "is that still our size" decision that keep the request
    /// from fighting the seat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule: the rider scales with the ball (small ball, small rider; large ball, large
    /// rider) in the push stance of <see cref="RideViewMath"/>: the eye height is
    /// <see cref="RideViewMath.EyeHeightPerRadius"/> times the radius, which is what puts the
    /// real hands on the ball's back surface. The avatar's own standing pose is measured
    /// (<see cref="RideStanceMath"/>: <c>eta</c>, the head's height above the station anchor per
    /// metre of eye height), and the request is divided by it so the anchor, i.e. the feet,
    /// lands on the ground. Every term is proportional to the radius, so <c>E</c> is a constant
    /// (per avatar, through <c>eta</c>) times the ball diameter: one picture at every size.
    /// </para>
    /// <para>
    /// Platform limits (RIDE_GEOMETRY.md section 4.1 gives the sources): Udon's target eye
    /// height is clamped to 0.1 to 100 m, and VRChat supports nothing outside that range. Where
    /// the world has avatar scaling switched off on the website the Set functions have no
    /// effect, so the real eye height is read back instead of assuming the request took effect,
    /// and Udon has to apply its size again whenever the avatar's eye height changes.
    /// </para>
    /// </remarks>
    public static class RideScaleMath
    {
        /// <summary>Smallest eye height Udon can set (the platform clamps there), metres.</summary>
        public const float MinSupportedEyeHeightMeters = 0.1f;

        /// <summary>Largest eye height Udon can set, metres.</summary>
        public const float MaxSupportedEyeHeightMeters = 100f;

        /// <summary>The diameter assumed before the motor has derived one (the story's starting ball), metres.</summary>
        public const float FallbackBallDiameterMeters = 0.222f;

        /// <summary>A measured eye height this close to the target counts as applied.</summary>
        public const float AppliedToleranceFraction = 0.05f;

        /// <summary>Low-pass on the requested eye height, hertz: the follow frame's height filter.</summary>
        public const float DefaultFilterHz = 5f;

        /// <summary>
        /// A new eye height is sent only when it differs from the last one sent by this fraction.
        /// Each send makes VRChat raise an avatar-eye-height event and rescales the avatar, so
        /// the step is the debounce.
        /// </summary>
        public const float DefaultStepFraction = 0.005f;

        /// <summary>No two sends are closer than this, seconds.</summary>
        public const float DefaultMinIntervalSeconds = 0.1f;

        /// <summary>The size changed by someone else this many times in a row is re-applied at most this often.</summary>
        public const int MaxConsecutiveReapply = 3;

        /// <summary>An eye-height event whose read-back is the size we sent: our own change.</summary>
        public const int EventOurs = 0;

        /// <summary>An eye-height event with another size: apply ours again.</summary>
        public const int EventReapply = 1;

        /// <summary>Another size again and again: stop fighting and measure the seat as it is.</summary>
        public const int EventGiveUp = 2;

        /// <summary>
        /// The eye height to ask VRChat for. False (and zero) when the request is zero,
        /// negative or not a number; any other request is clamped to the supported range.
        /// </summary>
        public static bool TryResolveTarget(float requestedMeters, out float targetMeters)
        {
            targetMeters = 0f;
            if (!KatamariMath.IsFiniteFloat(requestedMeters) || requestedMeters <= 0f) return false;
            targetMeters = Mathf.Clamp(requestedMeters, MinSupportedEyeHeightMeters,
                MaxSupportedEyeHeightMeters);
            return true;
        }

        /// <summary>
        /// Whether the eye height read back from VRChat is the one that was asked for, within
        /// <see cref="AppliedToleranceFraction"/>. False for a non-finite or non-positive read
        /// (the avatar is not loaded yet).
        /// </summary>
        public static bool IsApplied(float currentMeters, float targetMeters)
        {
            if (!KatamariMath.IsFiniteFloat(currentMeters) || currentMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(targetMeters) || targetMeters <= 0f) return false;
            return Mathf.Abs(currentMeters - targetMeters) <= targetMeters * AppliedToleranceFraction;
        }

        /// <summary>
        /// What an avatar-eye-height event means for a rider whose size is being held: our own
        /// change (<see cref="EventOurs"/>), someone else's (the player's own dial, another
        /// avatar), to be re-applied (<see cref="EventReapply"/>), or the same again too many
        /// times in a row (<see cref="EventGiveUp"/>). <paramref name="reapplyCount"/> is how many
        /// re-applications were already made since the last event that was ours.
        /// </summary>
        public static int ClassifyEyeHeightEvent(float readBackMeters, float sentMeters,
            int reapplyCount)
        {
            if (IsApplied(readBackMeters, sentMeters)) return EventOurs;
            if (reapplyCount < MaxConsecutiveReapply) return EventReapply;
            return EventGiveUp;
        }

        /// <summary>
        /// The eye height (as VRChat measures it) for the push stance on a ball of this semantic
        /// diameter, for an avatar whose head stands <paramref name="eta"/> eye heights above the
        /// station anchor, clamped to the platform range; <paramref name="clamped"/> says whether
        /// the clamp changed it. False (and zero) for a bad diameter or eta.
        /// </summary>
        public static bool TryEyeHeightForDiameter(float ballDiameterMeters, float eta,
            out float eyeHeightMeters, out bool clamped)
        {
            eyeHeightMeters = 0f;
            clamped = false;
            if (!KatamariMath.IsFiniteFloat(ballDiameterMeters) || ballDiameterMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eta) || eta <= 0f) return false;
            float wanted = RideViewMath.EyeHeightPerRadius() * ballDiameterMeters * 0.5f / eta;
            if (!KatamariMath.IsFiniteFloat(wanted) || wanted <= 0f) return false;
            eyeHeightMeters = Mathf.Clamp(wanted, MinSupportedEyeHeightMeters,
                MaxSupportedEyeHeightMeters);
            clamped = !Mathf.Approximately(eyeHeightMeters, wanted);
            return true;
        }

        /// <summary>
        /// One step of the usual low pass, <c>1 - exp(-2 pi hz dt)</c>, from
        /// <paramref name="current"/> toward <paramref name="target"/>; a zero frequency or time
        /// step holds the current value, and a bad input returns the current value.
        /// </summary>
        public static float SmoothEyeHeight(float current, float target, float dtSeconds,
            float hertz)
        {
            if (!KatamariMath.IsFiniteFloat(current) || !KatamariMath.IsFiniteFloat(target)) return current;
            if (!KatamariMath.IsFiniteFloat(dtSeconds) || !KatamariMath.IsFiniteFloat(hertz)) return current;
            if (hertz <= 0f || dtSeconds <= 0f) return current;
            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * hertz * dtSeconds);
            return current + (target - current) * alpha;
        }

        /// <summary>
        /// The debounce: whether a candidate eye height is far enough from the last one sent
        /// (at least <paramref name="stepFraction"/> of it) to be worth another request. True
        /// when nothing was sent yet.
        /// </summary>
        public static bool ShouldApplyEyeHeight(float sentMeters, float candidateMeters,
            float stepFraction)
        {
            if (!KatamariMath.IsFiniteFloat(candidateMeters) || candidateMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(sentMeters) || sentMeters <= 0f) return true;
            if (!KatamariMath.IsFiniteFloat(stepFraction) || stepFraction < 0f) return false;
            return Mathf.Abs(candidateMeters - sentMeters) >= sentMeters * stepFraction;
        }

        /// <summary>
        /// The rate limit: <see cref="ShouldApplyEyeHeight"/> and at least
        /// <paramref name="minIntervalSeconds"/> since the last send. A non-finite interval or
        /// elapsed time refuses.
        /// </summary>
        public static bool ShouldSendEyeHeight(float sentMeters, float candidateMeters,
            float stepFraction, float secondsSinceLastSend, float minIntervalSeconds)
        {
            if (!KatamariMath.IsFiniteFloat(secondsSinceLastSend)) return false;
            if (!KatamariMath.IsFiniteFloat(minIntervalSeconds) || minIntervalSeconds < 0f) return false;
            if (secondsSinceLastSend < minIntervalSeconds) return false;
            return ShouldApplyEyeHeight(sentMeters, candidateMeters, stepFraction);
        }

        /// <summary>
        /// The eye height to restore when the ride ends. The value read before the ride is
        /// used when it is a real height; otherwise zero, meaning "leave the size alone".
        /// </summary>
        public static float RestoreTarget(float savedMeters)
        {
            if (!KatamariMath.IsFiniteFloat(savedMeters) || savedMeters <= 0f) return 0f;
            return Mathf.Clamp(savedMeters, MinSupportedEyeHeightMeters, MaxSupportedEyeHeightMeters);
        }
    }
}
