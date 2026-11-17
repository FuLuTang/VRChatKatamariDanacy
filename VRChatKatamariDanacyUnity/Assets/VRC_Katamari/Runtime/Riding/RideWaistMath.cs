using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// The seated waist: what the seat measurement does with the avatar's hips, so that the
    /// waist lands on the top of the ball whatever the avatar's proportions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing about the body is assumed. After the rider sits, the behaviour reads the local
    /// player's tracking head and Hips bone relative to the seat (<see cref="SeatEyeMath"/>
    /// samples both in the rig frame). <c>kappa</c> is the vertical distance from the head down
    /// to the hips divided by the avatar's eye height at the time: a number of this avatar, in
    /// this seated pose, that <see cref="RideScaleMath"/> turns into the eye height that puts
    /// the hips on the ball's top. Avatar scaling is a uniform scale of the avatar's root, so
    /// both seated offsets are taken to be proportional to the eye height; that is an inference
    /// to confirm on a real client, not a platform guarantee.
    /// </para>
    /// <para>
    /// Where the seat goes. The seat's horizontal position still puts the head on the follow
    /// frame's eye point (the view rule). Its vertical position puts the hips, not the head, on
    /// the ball's top: the seat offset is <c>-effectiveHead</c> (<see cref="SeatEyeMath"/>'s
    /// solution), where <see cref="EffectiveHeadInRig"/> is the head with its height replaced
    /// by the hips' height minus the waist target. When the eye height is the one
    /// <see cref="RideScaleMath"/> asks for, the head is on the eye point as well; when it
    /// lags or is clamped, the waist stays on the ball and the eye drifts.
    /// </para>
    /// <para>
    /// The way back. A bone that does not exist is not guaranteed to read as anything
    /// useful, so a hips reading is used only when it is finite, not exactly the origin, close
    /// enough to the seat, in every sample, and the resulting kappa is inside
    /// [<see cref="MinKappa"/>, <see cref="MaxKappa"/>]. Otherwise the seat only aligns the
    /// eye, from the head offset measured at sit-down, and the rider's size is left alone.
    /// </para>
    /// </remarks>
    public static class RideWaistMath
    {
        /// <summary>Success; every other code names why the waist could not be used.</summary>
        public const int ReasonOk = 0;

        /// <summary>The hips could not be read: non-finite, exactly the origin, or missing in some sample.</summary>
        public const int ReasonHipsMissing = 1;

        /// <summary>The hips were further from the seat than a seated body can be.</summary>
        public const int ReasonHipsImplausible = 2;

        /// <summary>The eye height at the measurement is unknown (the world does not scale the avatar).</summary>
        public const int ReasonNoEyeHeight = 3;

        /// <summary>A measurement was NaN or infinite.</summary>
        public const int ReasonNotFinite = 4;

        /// <summary>kappa came out outside the guard band: the hips are not a believable waist.</summary>
        public const int ReasonKappaOutOfBand = 5;

        /// <summary>There is no ball to take the waist target from.</summary>
        public const int ReasonNoBall = 6;

        /// <summary>
        /// Smallest believable kappa. Guard, not product value: a hips reading within this
        /// fraction of an eye height of the head is the head, not a waist.
        /// </summary>
        public const float MinKappa = 0.05f;

        /// <summary>
        /// Largest believable kappa. Guard, not product value: hips more than a whole eye height
        /// below the viewpoint are not a seated waist.
        /// </summary>
        public const float MaxKappa = 1f;

        /// <summary>
        /// The vertical limit handed to <see cref="SeatEyeMath.TryComputeSeatOffset"/> when the
        /// waist places the seat: the head datum was already vetted at the measurement, and the
        /// waist target can legitimately be far below the eye while the size lags the ball.
        /// </summary>
        public const float UnlimitedMeters = 1e6f;

        /// <summary>
        /// Whether a bone position read from VRChat can be used: finite and not exactly the
        /// origin. A bone the avatar lacks is expected to read as the origin (this return is
        /// documented only by the project's local library, not by VRChat), and a real hips bone
        /// is never at the origin of an ordinary world.
        /// </summary>
        public static bool IsUsableBonePosition(Vector3 worldPosition)
        {
            if (!KatamariMath.IsFiniteVector(worldPosition)) return false;
            return worldPosition.x != 0f || worldPosition.y != 0f || worldPosition.z != 0f;
        }

        /// <summary>
        /// kappa: the head's height above the hips divided by the eye height they were measured
        /// at, from the averaged rig-frame offsets of the head and the hips relative to the seat.
        /// </summary>
        public static bool TryMeasureKappa(Vector3 headInRigMeters, Vector3 hipsInRigMeters,
            float eyeHeightMeters, out float kappa, out int reason)
        {
            kappa = 0f;
            reason = ReasonOk;
            if (!KatamariMath.IsFiniteFloat(eyeHeightMeters) || eyeHeightMeters <= 0f)
            {
                reason = ReasonNoEyeHeight;
                return false;
            }
            if (!KatamariMath.IsFiniteVector(headInRigMeters) ||
                !KatamariMath.IsFiniteVector(hipsInRigMeters))
            {
                reason = ReasonNotFinite;
                return false;
            }
            float candidate = (headInRigMeters.y - hipsInRigMeters.y) / eyeHeightMeters;
            if (!KatamariMath.IsFiniteFloat(candidate))
            {
                reason = ReasonNotFinite;
                return false;
            }
            if (candidate < MinKappa || candidate > MaxKappa)
            {
                reason = ReasonKappaOutOfBand;
                return false;
            }
            kappa = candidate;
            return true;
        }

        /// <summary>
        /// The ball's top relative to the rig root (the eye point), metres, in the rig's up
        /// direction: where the hips are to be. The rig carries yaw only, so its up is the
        /// world's.
        /// </summary>
        public static float WaistTargetInRig(float ballCenterWorldY, float ballRadiusMeters,
            float rigWorldY)
        {
            return ballCenterWorldY + ballRadiusMeters - rigWorldY;
        }

        /// <summary>
        /// The head offset <see cref="SeatEyeMath.TryComputeSeatOffset"/> is to solve against.
        /// With a usable waist the height is replaced so that the solution puts the hips at
        /// <paramref name="waistTargetInRigY"/>; without one the head is returned as it is
        /// (eye-only alignment). Horizontal parts are always the head's.
        /// </summary>
        public static Vector3 EffectiveHeadInRig(Vector3 headInRigMeters, Vector3 hipsInRigMeters,
            bool hasWaist, float waistTargetInRigY)
        {
            if (!hasWaist) return headInRigMeters;
            return new Vector3(headInRigMeters.x, hipsInRigMeters.y - waistTargetInRigY,
                headInRigMeters.z);
        }
    }
}
