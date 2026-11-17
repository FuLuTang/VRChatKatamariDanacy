using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// How big the rider is: the eye height to ask VRChat for so that, whatever the ball's
    /// size, the seated waist is level with the ball's reference height, plus the smoothing
    /// and debounce that keep the request from fighting the seat measurement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule (owner 2026-11-17): the ball is small and the rider small, the ball large and
    /// the rider large, and in the seated view the rider's waist is exactly as high as the
    /// ball. "The ball's height" is read as the height of the ball's top above its lowest
    /// point (<see cref="WaistBallHeightFraction"/> = 1: the waist is level with the top of
    /// the ball); 0.5 would be the centre. The seat floats, so the waist is not measured from
    /// the ground but from the seat: with the eye <c>up</c> above the ball centre
    /// (<see cref="RideViewMath"/>), a seated eye <c>m</c> above the seat and the waist
    /// <c>w = WaistToSeatedEyeRatio * m</c> above the seat, the waist is at
    /// <c>up - m + w</c> above the ball centre. Setting that equal to
    /// <c>(2 * fraction - 1) * r</c> (the reference height over the centre) gives
    /// <c>m = (up - (2 * fraction - 1) * r) / (1 - WaistToSeatedEyeRatio)</c>, and the avatar's
    /// eye height is <c>E = m / seatedEyeRatio</c>. Every term is proportional to the radius,
    /// so <c>E</c> is a constant times the ball diameter: the whole arrangement is one picture
    /// at every size.
    /// </para>
    /// <para>
    /// The body proportions are PROVISIONAL human values (<see cref="SeatedEyeToEyeHeightRatio"/>
    /// and <see cref="WaistToSeatedEyeRatio"/>, sources in RIDE_GEOMETRY.md). The seated eye
    /// ratio is the one that varies between avatars, so the seat measurement reports the real
    /// one (<c>SeatEyeCompensation.GetSeatedEyeRatio</c>) and
    /// <see cref="ResolveSeatedEyeRatio"/> keeps a sane value of it; the waist ratio cannot be
    /// measured (there is no hip tracking datum) and stays nominal.
    /// </para>
    /// <para>
    /// Platform limits (RIDE_GEOMETRY.md section 3.1 says which are confirmed): where the world
    /// has avatar scaling switched off on the website the Set functions have no effect, so the
    /// real eye height is read back instead of assuming the request took effect. NOT confirmed
    /// against the official page: the 0.1..100 m clamp (the local library page says
    /// 0.2..5 m for the player's own range) and that scaling is uniform.
    /// </para>
    /// </remarks>
    public static class RideScaleMath
    {
        /// <summary>Smallest eye height Udon can set (the platform clamps there), metres.</summary>
        public const float MinSupportedEyeHeightMeters = 0.1f;

        /// <summary>Largest eye height Udon can set, metres.</summary>
        public const float MaxSupportedEyeHeightMeters = 100f;

        /// <summary>
        /// Seated eye height above the seat over the standing eye height, nominal: 800 mm over
        /// 1640 mm (remembered anthropometric values, not re-checked online). Only the first guess: the
        /// seat measurement replaces it with the avatar's real ratio.
        /// </summary>
        public const float SeatedEyeToEyeHeightRatio = 0.4878f;

        /// <summary>The measured ratio is trusted only inside this band; outside it the nominal one is used.</summary>
        public const float MinSeatedEyeRatio = 0.25f;

        /// <summary>See <see cref="MinSeatedEyeRatio"/>.</summary>
        public const float MaxSeatedEyeRatio = 0.9f;

        /// <summary>
        /// Waist (belt line) above the seat over the seated eye height above the seat: sitting
        /// elbow height 250 mm over sitting eye height 800 mm (same source). Not measurable at
        /// runtime.
        /// </summary>
        public const float WaistToSeatedEyeRatio = 0.3125f;

        /// <summary>
        /// The waist's height above the ball's lowest point, in ball diameters: 1 is the top of
        /// the ball, 0.5 its centre. The scene's one switch for the owner's "waist is the
        /// ball's height" reading.
        /// </summary>
        public const float WaistBallHeightFraction = 1f;

        /// <summary>The diameter assumed before the motor has derived one (the story's starting ball), metres.</summary>
        public const float FallbackBallDiameterMeters = 0.222f;

        /// <summary>A measured eye height this close to the target counts as applied.</summary>
        public const float AppliedToleranceFraction = 0.05f;

        /// <summary>Low-pass on the requested eye height, hertz: the follow frame's height filter.</summary>
        public const float DefaultFilterHz = 5f;

        /// <summary>
        /// A new eye height is sent only when it differs from the last one sent by this fraction.
        /// Each send makes VRChat raise an avatar-eye-height event and rescales the avatar, so
        /// the step is the debounce: 0.5 % of the eye height is half a degree of nothing.
        /// </summary>
        public const float DefaultStepFraction = 0.005f;

        /// <summary>No two sends are closer than this, seconds.</summary>
        public const float DefaultMinIntervalSeconds = 0.1f;

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
        /// The seated eye height above the seat, metres, that puts the seated waist at
        /// <see cref="WaistBallHeightFraction"/> ball diameters above the ball's lowest point,
        /// for a ball of this radius with the eye where <see cref="RideViewMath"/> puts it.
        /// Zero for a bad radius.
        /// </summary>
        public static float EyeAboveSeatForRadius(float radiusMeters)
        {
            float up;
            float back;
            if (!RideViewMath.TryEyeOffset(radiusMeters, out up, out back)) return 0f;
            float referenceAboveCentre = (2f * WaistBallHeightFraction - 1f) * radiusMeters;
            float m = (up - referenceAboveCentre) / (1f - WaistToSeatedEyeRatio);
            return m > 0f ? m : 0f;
        }

        /// <summary>The waist's height above the seat for a seated eye this far above it, metres.</summary>
        public static float WaistAboveSeat(float eyeAboveSeatMeters)
        {
            return eyeAboveSeatMeters * WaistToSeatedEyeRatio;
        }

        /// <summary>
        /// The waist's height above the ball's lowest point, metres, for an eye
        /// <paramref name="upMeters"/> above the ball centre and a seated eye
        /// <paramref name="eyeAboveSeatMeters"/> above the seat: the check of the rule above.
        /// </summary>
        public static float WaistAboveBallBottom(float radiusMeters, float upMeters,
            float eyeAboveSeatMeters)
        {
            return radiusMeters + upMeters - eyeAboveSeatMeters + WaistAboveSeat(eyeAboveSeatMeters);
        }

        /// <summary>
        /// The eye height (standing, as VRChat measures it) that makes the seated waist level
        /// with the ball for a ball of this semantic diameter, clamped to the platform range;
        /// <paramref name="clamped"/> says whether the clamp changed it (below about 0.09 m
        /// the platform cannot make the rider small enough). False (and zero) for a bad
        /// diameter or ratio.
        /// </summary>
        public static bool TryEyeHeightForDiameter(float ballDiameterMeters,
            float seatedEyeRatio, out float eyeHeightMeters, out bool clamped)
        {
            eyeHeightMeters = 0f;
            clamped = false;
            if (!KatamariMath.IsFiniteFloat(ballDiameterMeters) || ballDiameterMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(seatedEyeRatio) || seatedEyeRatio <= 0f) return false;
            float m = EyeAboveSeatForRadius(ballDiameterMeters * 0.5f);
            if (m <= 0f) return false;
            float wanted = m / seatedEyeRatio;
            if (!KatamariMath.IsFiniteFloat(wanted)) return false;
            eyeHeightMeters = Mathf.Clamp(wanted, MinSupportedEyeHeightMeters,
                MaxSupportedEyeHeightMeters);
            clamped = !Mathf.Approximately(eyeHeightMeters, wanted);
            return true;
        }

        /// <summary>
        /// The seated eye ratio to use: the measured one when it is inside
        /// [<see cref="MinSeatedEyeRatio"/>, <see cref="MaxSeatedEyeRatio"/>], otherwise the
        /// nominal <see cref="SeatedEyeToEyeHeightRatio"/> (no measurement yet, or a datum that
        /// is not a seated head).
        /// </summary>
        public static float ResolveSeatedEyeRatio(float measuredRatio)
        {
            if (!KatamariMath.IsFiniteFloat(measuredRatio)) return SeatedEyeToEyeHeightRatio;
            if (measuredRatio < MinSeatedEyeRatio || measuredRatio > MaxSeatedEyeRatio)
            {
                return SeatedEyeToEyeHeightRatio;
            }
            return measuredRatio;
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
