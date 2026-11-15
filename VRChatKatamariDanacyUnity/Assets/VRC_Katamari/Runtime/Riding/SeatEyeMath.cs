using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Pure math turning the measured "head above the seat" offset into the rig-local seat
    /// offset that puts the rider's EYE on the follow frame's eye point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The follow frame (<see cref="VRCKatamari.BallLayers.FollowRigMath"/>) answers "where
    /// should the viewer's eye be": behind and above the ball. The motor writes that point and
    /// a yaw-only rotation to the rig root. A rider is carried by a <c>VRCStation</c>, and a
    /// station carries the avatar's ROOT, not its eye - so a seat placed exactly on the eye
    /// point puts the real camera one seated eye height higher. That is the "viewpoint too
    /// high after sitting down" defect this layer fixes, and the fix is to derive the SEAT
    /// from the eye point instead of putting the seat on it.
    /// </para>
    /// <para>
    /// Model, all world space unless a frame is named. E is the rig root's world position,
    /// which IS the eye point; R its world rotation (yaw only in this project); L the seat's
    /// offset in the rig frame, so the seat is at S = E + R*L; d the measured
    /// head-relative-to-seat vector, so the head is at H = S + d. The eye lands on E exactly
    /// when R*L + d = 0, i.e.
    /// </para>
    /// <code>
    /// L = -inverse(R) * d
    /// </code>
    /// <para>
    /// d belongs to the seated pose (the avatar is rigidly carried by the station), so one
    /// measurement yields the ABSOLUTE offset and measuring again - after an avatar change,
    /// even with a compensation already applied - returns the same value. This is a fixed
    /// point, not a feedback loop that creeps a little every ride. It also means a scene's
    /// authored seat offset is absorbed by the measurement rather than added to it twice;
    /// take the sample with the seat wherever the scene put it.
    /// </para>
    /// <para>
    /// Units are metres with +Y up. Rotations are unit quaternions in the world frame; a
    /// yaw-only rig is expected but not required. Nothing here reads a Transform, holds state
    /// or allocates: the caller owns the sample sum, the previous offset and the frame clock,
    /// the same caller-owned-state shape <see cref="VRCKatamari.BallLayers.FollowRigMath"/>
    /// uses.
    /// </para>
    /// <para>
    /// Refusal reasons are one small table shared by the three functions, 0 being success, so
    /// a scene log can say WHY a sample or an offset was refused: 1 a non-finite position or
    /// offset, 2 an unusable (non-finite or degenerate) rotation, 3 an unusable filter input
    /// (non-finite hertz or time step, or a negative one), 4 an unusable limit (non-finite or
    /// negative), 5 no samples to average. A refused call writes no new offset: the caller's
    /// previous offset comes back unchanged.
    /// </para>
    /// </remarks>
    public static class SeatEyeMath
    {
        /// <summary>Success; every other code names a refusal.</summary>
        public const int ReasonOk = 0;

        /// <summary>A position or offset was NaN or infinite.</summary>
        public const int ReasonNotFinite = 1;

        /// <summary>A rotation was NaN, infinite or degenerate.</summary>
        public const int ReasonBadRotation = 2;

        /// <summary>A filter input was NaN, infinite or negative.</summary>
        public const int ReasonBadFilter = 3;

        /// <summary>A limit was NaN, infinite or negative.</summary>
        public const int ReasonBadLimit = 4;

        /// <summary>There were no samples to average.</summary>
        public const int ReasonNoSamples = 5;

        /// <summary>
        /// The furthest a seated head may plausibly be from the seat anchor, in metres.
        /// </summary>
        /// <remarks>
        /// A guard against reading obviously wrong data (a standing eye height while the camera
        /// is not actually there), not a product value: past it the DATUM is wrong, and the
        /// caller refuses the correction instead of half-applying it. Measured heads: 1.204159 m
        /// in ClientSim (2026-11-15; the ClientSim camera really is that high above the seat), and
        /// about 0.85 m for a seated pose on real hardware, so 1.35 m leaves headroom over the
        /// largest measured value while staying below a standing eye height (1.5-1.8 m).
        /// </remarks>
        public const float MaxPlausibleHeadAboveSeatMeters = 1.35f;

        // A quaternion shorter than this cannot be inverted into usable numbers. The
        // threshold is on the squared length, so a slightly denormalised rotation still
        // normalises through Quaternion.Inverse instead of being refused.
        private const float MinimumQuaternionSquaredLength = 1e-8f;

        /// <summary>
        /// Whether a measured head-to-seat distance is small enough to trust.
        /// </summary>
        /// <remarks>
        /// The caller passes the length of the rig-frame offset it got from
        /// <see cref="TrySampleHeadOffset"/>. Equal to
        /// <see cref="MaxPlausibleHeadAboveSeatMeters"/> counts as plausible - the guard is a
        /// rail to stay under, not an exclusive bound. A non-finite length compares false, which
        /// is the wanted answer for a datum this layer could not use anyway. The REFUSAL itself
        /// stays with the caller: it owns the reason code and the log line.
        /// </remarks>
        public static bool IsPlausibleHeadDistance(float distanceMeters)
        {
            return distanceMeters <= MaxPlausibleHeadAboveSeatMeters;
        }

        /// <summary>
        /// One sample: where the head is relative to the seat, in the rig frame and in the
        /// seat's own frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="headInRigMeters"/> is the vector the offset is computed from, and it
        /// is sampled in the RIG frame so samples taken while the ball turns average
        /// correctly - a world-space average would smear as the yaw changes.
        /// <paramref name="headInSeatMeters"/> is the same vector in the seat's frame, where y
        /// is the number a play tester reads: how far the real eye sits above the seat.
        /// </para>
        /// <para>
        /// The caller is expected to reject an implausible sample itself, because a refusal
        /// belongs to whoever knows why the datum is wrong: <see cref="IsPlausibleHeadDistance"/>
        /// is the shared rail for "a head datum that is metres away from the seat", and this
        /// function only rejects numbers it cannot use.
        /// </para>
        /// </remarks>
        public static bool TrySampleHeadOffset(
            Vector3 headWorldPosition,
            Vector3 seatWorldPosition,
            Quaternion seatWorldRotation,
            Quaternion rigWorldRotation,
            out Vector3 headInRigMeters,
            out Vector3 headInSeatMeters,
            out int reason)
        {
            headInRigMeters = Vector3.zero;
            headInSeatMeters = Vector3.zero;
            reason = ReasonOk;

            if (!KatamariMath.IsFiniteVector(headWorldPosition)) return Refuse(ReasonNotFinite, out reason);
            if (!KatamariMath.IsFiniteVector(seatWorldPosition)) return Refuse(ReasonNotFinite, out reason);
            if (!IsUsableRotation(seatWorldRotation)) return Refuse(ReasonBadRotation, out reason);
            if (!IsUsableRotation(rigWorldRotation)) return Refuse(ReasonBadRotation, out reason);

            Vector3 offsetWorld = headWorldPosition - seatWorldPosition;
            Vector3 inRig = Quaternion.Inverse(rigWorldRotation) * offsetWorld;
            Vector3 inSeat = Quaternion.Inverse(seatWorldRotation) * offsetWorld;
            if (!KatamariMath.IsFiniteVector(inRig)) return Refuse(ReasonNotFinite, out reason);
            if (!KatamariMath.IsFiniteVector(inSeat)) return Refuse(ReasonNotFinite, out reason);

            headInRigMeters = inRig;
            headInSeatMeters = inSeat;
            return true;
        }

        /// <summary>
        /// The mean of the accumulated rig-frame samples.
        /// </summary>
        /// <remarks>
        /// The caller adds up its rig-frame samples and passes the sum and the count.
        /// Averaging is what makes one measurement resistant to the few frames of sitting-IK
        /// settling after a sit-down and to a VR head that is never perfectly still; the mean
        /// is still a single fixed sample of the seated pose, not a per-frame correction.
        /// </remarks>
        public static bool TryMeanHeadOffset(
            Vector3 sampleSum,
            int sampleCount,
            out Vector3 meanHeadInRigMeters,
            out int reason)
        {
            meanHeadInRigMeters = Vector3.zero;
            reason = ReasonOk;

            if (sampleCount <= 0) return Refuse(ReasonNoSamples, out reason);
            if (!KatamariMath.IsFiniteVector(sampleSum)) return Refuse(ReasonNotFinite, out reason);

            // Multiply by the reciprocal rather than divide: one rounding, and no
            // division extern for a value this layer uses only to place a transform.
            Vector3 mean = sampleSum * (1f / sampleCount);
            if (!KatamariMath.IsFiniteVector(mean)) return Refuse(ReasonNotFinite, out reason);
            meanHeadInRigMeters = mean;
            return true;
        }

        /// <summary>
        /// The seat offset to write: the exact solution, limited, then low-passed toward.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="headInRigMeters"/> is the averaged measurement from
        /// <see cref="TryMeanHeadOffset"/>. The exact solution is its negation, expressed in
        /// the rig frame.
        /// </para>
        /// <para>
        /// <paramref name="verticalOnly"/> drops the horizontal part of the solution, for a
        /// scene that measures a horizontal error it does not want to chase. Both the drop and
        /// the clamps are reported through <paramref name="uncompensatedMeters"/>: that is how
        /// much of the measured eye error the offset deliberately leaves in place. It is zero
        /// when nothing was dropped or clamped, and it is the eye error the scene should expect
        /// once the filter has settled.
        /// </para>
        /// <para>
        /// Limits are magnitudes in metres, not signed rails: <paramref name="verticalLimitMeters"/>
        /// bounds |y| and <paramref name="horizontalLimitMeters"/> bounds the horizontal
        /// length. A limit of zero is legal and means "compensate none of that axis". The
        /// defaults are guards against a wrong head datum (a standing eye height instead of a
        /// seated one is about twice the real value), not measured product values.
        /// </para>
        /// <para>
        /// The low pass is the project's usual alpha = 1 - exp(-2*pi*hz*dt) with
        /// <paramref name="previousSeatOffsetMeters"/> as the caller-owned state, so a zero
        /// hertz or a zero time step holds the previous offset. A moving seat is a moving
        /// viewpoint, so this is what keeps the correction from snapping the rider's world in
        /// one frame; <paramref name="filterResidualMeters"/> is how far the applied offset
        /// still is from its target, i.e. the convergence measure, not an eye error.
        /// <paramref name="resetToTarget"/> skips the smoothing (first frame of a re-measure
        /// after an avatar change, or a caller that knows the jump is invisible).
        /// </para>
        /// </remarks>
        public static bool TryComputeSeatOffset(
            Vector3 headInRigMeters,
            Vector3 previousSeatOffsetMeters,
            float verticalLimitMeters,
            float horizontalLimitMeters,
            bool verticalOnly,
            float filterHz,
            float dtSeconds,
            bool resetToTarget,
            out Vector3 seatOffsetMeters,
            out float uncompensatedMeters,
            out float filterResidualMeters,
            out int reason)
        {
            // A refusal hands the caller its own state back, so a refused frame is a frame
            // that changed nothing.
            seatOffsetMeters = previousSeatOffsetMeters;
            uncompensatedMeters = 0f;
            filterResidualMeters = 0f;
            reason = ReasonOk;

            if (!KatamariMath.IsFiniteVector(headInRigMeters)) return Refuse(ReasonNotFinite, out reason);
            if (!KatamariMath.IsFiniteVector(previousSeatOffsetMeters)) return Refuse(ReasonNotFinite, out reason);
            if (!KatamariMath.IsFiniteFloat(verticalLimitMeters)) return Refuse(ReasonBadLimit, out reason);
            if (verticalLimitMeters < 0f) return Refuse(ReasonBadLimit, out reason);
            if (!KatamariMath.IsFiniteFloat(horizontalLimitMeters)) return Refuse(ReasonBadLimit, out reason);
            if (horizontalLimitMeters < 0f) return Refuse(ReasonBadLimit, out reason);

            float alpha;
            if (!TryFilterAlpha(filterHz, dtSeconds, out alpha)) return Refuse(ReasonBadFilter, out reason);

            // The exact solution: L = -inverse(R) * d, already in the rig frame.
            float exactX = -headInRigMeters.x;
            float exactY = -headInRigMeters.y;
            float exactZ = -headInRigMeters.z;

            float targetX = verticalOnly ? 0f : exactX;
            float targetY = exactY;
            float targetZ = verticalOnly ? 0f : exactZ;

            if (targetY > verticalLimitMeters) targetY = verticalLimitMeters;
            if (targetY < -verticalLimitMeters) targetY = -verticalLimitMeters;

            float horizontal = Mathf.Sqrt(targetX * targetX + targetZ * targetZ);
            if (horizontal > horizontalLimitMeters)
            {
                if (horizontalLimitMeters <= 0f)
                {
                    targetX = 0f;
                    targetZ = 0f;
                }
                else
                {
                    float scale = horizontalLimitMeters / horizontal;
                    targetX = targetX * scale;
                    targetZ = targetZ * scale;
                }
            }

            float appliedX;
            float appliedY;
            float appliedZ;
            if (resetToTarget)
            {
                appliedX = targetX;
                appliedY = targetY;
                appliedZ = targetZ;
            }
            else
            {
                appliedX = previousSeatOffsetMeters.x + (targetX - previousSeatOffsetMeters.x) * alpha;
                appliedY = previousSeatOffsetMeters.y + (targetY - previousSeatOffsetMeters.y) * alpha;
                appliedZ = previousSeatOffsetMeters.z + (targetZ - previousSeatOffsetMeters.z) * alpha;
            }

            if (!KatamariMath.IsFiniteFloat(appliedX)) return Refuse(ReasonNotFinite, out reason);
            if (!KatamariMath.IsFiniteFloat(appliedY)) return Refuse(ReasonNotFinite, out reason);
            if (!KatamariMath.IsFiniteFloat(appliedZ)) return Refuse(ReasonNotFinite, out reason);

            float droppedX = exactX - targetX;
            float droppedY = exactY - targetY;
            float droppedZ = exactZ - targetZ;

            seatOffsetMeters = new Vector3(appliedX, appliedY, appliedZ);
            uncompensatedMeters = Mathf.Sqrt(
                droppedX * droppedX + droppedY * droppedY + droppedZ * droppedZ);
            float residualX = appliedX - targetX;
            float residualY = appliedY - targetY;
            float residualZ = appliedZ - targetZ;
            filterResidualMeters = Mathf.Sqrt(
                residualX * residualX + residualY * residualY + residualZ * residualZ);
            return true;
        }

        // Same shape as FollowRigMath's filter: 1 - exp(-2*pi*hz*dt), and a zero frequency
        // or a zero step holds the previous value instead of being an error.
        private static bool TryFilterAlpha(float hertz, float dtSeconds, out float alpha)
        {
            alpha = 0f;
            if (!KatamariMath.IsFiniteFloat(hertz)) return false;
            if (!KatamariMath.IsFiniteFloat(dtSeconds)) return false;
            if (hertz < 0f || dtSeconds < 0f) return false;
            if (hertz == 0f || dtSeconds == 0f) return true;
            float exponent = 2f * Mathf.PI * hertz * dtSeconds;
            if (!KatamariMath.IsFiniteFloat(exponent)) return false;
            float candidate = 1f - Mathf.Exp(-exponent);
            if (!KatamariMath.IsFiniteFloat(candidate)) return false;
            if (candidate < 0f || candidate > 1f) return false;
            alpha = candidate;
            return true;
        }

        // Quaternion.Inverse divides by the squared length, so only a rotation at or below
        // the threshold (a zero or near-zero quaternion) cannot be used.
        private static bool IsUsableRotation(Quaternion rotation)
        {
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            float squaredLength = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            return squaredLength > MinimumQuaternionSquaredLength;
        }

        // One refusal path so every function reports a reason the same way, and so a refusal
        // can never be returned together with a stray zeroed output by accident.
        private static bool Refuse(int code, out int reason)
        {
            reason = code;
            return false;
        }
    }
}
