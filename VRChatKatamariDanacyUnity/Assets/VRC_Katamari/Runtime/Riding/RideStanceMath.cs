using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// The standing rider's feet: what the seat measurement does with the head it reads, so
    /// that the station (which carries a standing avatar by its feet) lands on the ground
    /// beside the ball whatever this avatar's standing pose is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After the rider takes the station, the behaviour reads the local player's tracking head
    /// relative to the station anchor (<see cref="SeatEyeMath"/> samples it in the rig frame).
    /// <c>eta</c> is the head's height above the anchor divided by the avatar's eye height at
    /// the time. VRChat defines the eye height as the viewpoint's height above the feet when
    /// standing, so a standing station gives <c>eta</c> near one; the measured value absorbs
    /// whatever the station's pose does to it. <see cref="RideScaleMath"/> divides the stance's
    /// eye height by <c>eta</c>, which puts the feet on the ground when the eye is on the eye
    /// point. Avatar scaling is a uniform scale of the root, so the offset is taken to be
    /// proportional to the eye height; that is an inference to confirm on a real client.
    /// </para>
    /// <para>
    /// Where the seat goes. Its horizontal position puts the head on the follow frame's eye
    /// point; its height puts the anchor (the feet) on the ball's lowest point, which is read
    /// from the ball every frame so the follow frame's lag never lifts the feet off the ground.
    /// </para>
    /// <para>
    /// The way back. A head height outside [<see cref="MinEta"/>, <see cref="MaxEta"/>] of the
    /// eye height is not a standing pose (a seated station, a broken datum). The seat then only
    /// aligns the eye, and the size uses <c>eta = 1</c>, the platform's definition, so the
    /// hands still reach the ball in VR.
    /// </para>
    /// </remarks>
    public static class RideStanceMath
    {
        /// <summary>Success; every other code names why the stance could not be measured.</summary>
        public const int ReasonOk = 0;

        /// <summary>The eye height at the measurement is unknown (the world does not scale the avatar).</summary>
        public const int ReasonNoEyeHeight = 1;

        /// <summary>A measurement was NaN or infinite.</summary>
        public const int ReasonNotFinite = 2;

        /// <summary>eta came out outside the guard band: not a standing pose.</summary>
        public const int ReasonEtaOutOfBand = 3;

        /// <summary>There is no ball to take the ground from.</summary>
        public const int ReasonNoBall = 4;

        /// <summary>Smallest believable eta. Guard, not product value: a seated head is about half the eye height.</summary>
        public const float MinEta = 0.75f;

        /// <summary>Largest believable eta. Guard, not product value.</summary>
        public const float MaxEta = 1.25f;

        /// <summary>The eta used when none could be measured: VRChat's definition of the eye height.</summary>
        public const float DefaultEta = 1f;

        /// <summary>
        /// The vertical limit handed to <see cref="SeatEyeMath.TryComputeSeatOffset"/> when the
        /// stance places the seat: the head datum was already vetted at the measurement, and the
        /// ground can legitimately be further below the eye than the authored limit while the
        /// size lags the ball.
        /// </summary>
        public const float UnlimitedMeters = 1e6f;

        /// <summary>
        /// eta: the head's height above the anchor divided by the eye height it was measured at,
        /// from the averaged rig-frame head offset relative to the anchor.
        /// </summary>
        public static bool TryMeasureEta(Vector3 headInRigMeters, float eyeHeightMeters,
            out float eta, out int reason)
        {
            eta = 0f;
            reason = ReasonOk;
            if (!KatamariMath.IsFiniteFloat(eyeHeightMeters) || eyeHeightMeters <= 0f)
            {
                reason = ReasonNoEyeHeight;
                return false;
            }
            if (!KatamariMath.IsFiniteVector(headInRigMeters))
            {
                reason = ReasonNotFinite;
                return false;
            }
            float candidate = headInRigMeters.y / eyeHeightMeters;
            if (!KatamariMath.IsFiniteFloat(candidate))
            {
                reason = ReasonNotFinite;
                return false;
            }
            if (candidate < MinEta || candidate > MaxEta)
            {
                reason = ReasonEtaOutOfBand;
                return false;
            }
            eta = candidate;
            return true;
        }

        /// <summary>
        /// The ball's lowest point relative to the rig root (the eye point), metres, in the rig's
        /// up direction: where the feet are to be. The rig carries yaw only, so its up is the
        /// world's.
        /// </summary>
        public static float FeetTargetInRig(float ballCenterWorldY, float ballRadiusMeters,
            float rigWorldY)
        {
            return ballCenterWorldY - ballRadiusMeters - rigWorldY;
        }

        /// <summary>
        /// The head offset <see cref="SeatEyeMath.TryComputeSeatOffset"/> is to solve against.
        /// With a measured stance the height is replaced so that the solution puts the anchor at
        /// <paramref name="feetTargetInRigY"/> (the seat offset is minus this vector); without
        /// one the head is returned as it is (eye-only alignment). Horizontal parts are always
        /// the head's.
        /// </summary>
        public static Vector3 EffectiveHeadInRig(Vector3 headInRigMeters, bool hasStance,
            float feetTargetInRigY)
        {
            if (!hasStance) return headInRigMeters;
            return new Vector3(headInRigMeters.x, -feetTargetInRigY, headInRigMeters.z);
        }
    }
}
