using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Ground-support approximation for the ball: recompute the current support
    /// height, then move the center along the ground normal by a clamped
    /// normal-only correction.
    /// </summary>
    /// <remarks>
    /// Lengths and positions are metres in world space with +Y up. The raw ground
    /// plane is dot(n_raw, x) = d_raw; the normal and the offset are rescaled
    /// together at entry, so centerNormalCoordinate is a coordinate along the
    /// unit normal (not a world-Y height) and planeAnchor is the plane's closest
    /// point to the world origin (not the proxy contact point).
    ///
    /// localToWorldRotation is the active rotation that maps local proxy points
    /// into world space and must be unit length; a scaled or non-finite
    /// quaternion is rejected instead of being normalized. The proxy array is
    /// caller-owned and read-only; an empty array is valid and degenerates to the
    /// core sphere. The call allocates nothing, applies only a normal-only
    /// position change, and on false leaves every out value zero.
    /// </remarks>
    public static class GroundContactMath
    {
        // Slack for "this quaternion is unit length": Transform rotations are unit
        // to float precision, while a visibly scaled quaternion is not a rotation.
        private const float RotationTolerance = 1e-4f;

        /// <summary>
        /// Solves one ground-contact step for the given ball state.
        /// </summary>
        /// <remarks>
        /// supportHeight is always the current geometry value, never a historical
        /// maximum. The correction is clamped to [-maxDropPerStep, +maxLiftPerStep]
        /// and only the normal component of the center moves. Every length and
        /// position is in metres. Returns false for invalid input or a non-finite
        /// intermediate; no partial result is written in that case.
        /// </remarks>
        public static bool TrySolve(
            float coreRadius,
            Quaternion localToWorldRotation,
            Vector3[] localProxyPoints,
            Vector3 rawPlaneNormal,
            float rawPlaneOffset,
            Vector3 currentCenter,
            float maxLiftPerStep,
            float maxDropPerStep,
            float requestedNormalSpeedPerturbation,
            float maxNormalSpeedPerturbation,
            out float supportHeight,
            out Vector3 centerPosition,
            out float centerNormalCoordinate,
            out Vector3 planeAnchor,
            out float appliedNormalCorrection,
            out float normalSpeedPerturbation,
            out bool liftClamped,
            out bool dropClamped)
        {
            supportHeight = 0f;
            centerPosition = Vector3.zero;
            centerNormalCoordinate = 0f;
            planeAnchor = Vector3.zero;
            appliedNormalCorrection = 0f;
            normalSpeedPerturbation = 0f;
            liftClamped = false;
            dropClamped = false;

            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            if (!KatamariMath.IsFiniteVector(currentCenter)) return false;
            if (!KatamariMath.IsFiniteFloat(rawPlaneOffset)) return false;
            if (!IsUnitRotation(localToWorldRotation)) return false;
            if (localProxyPoints == null) return false;
            if (!KatamariMath.IsFiniteFloat(maxLiftPerStep) || maxLiftPerStep < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(maxDropPerStep) || maxDropPerStep < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(requestedNormalSpeedPerturbation)) return false;
            if (!KatamariMath.IsFiniteFloat(maxNormalSpeedPerturbation) ||
                maxNormalSpeedPerturbation < 0f) return false;

            Vector3 unitNormal;
            float rawNormalLength;
            if (!KatamariMath.TryNormalize(rawPlaneNormal, out unitNormal, out rawNormalLength)) return false;
            float offset = rawPlaneOffset / rawNormalLength;
            if (!KatamariMath.IsFiniteFloat(offset)) return false;

            // Support height never uses a historical maximum: it is recomputed from
            // the current rotation and proxy points, so removing a proxy lowers it.
            float support = coreRadius;
            for (int i = 0; i < localProxyPoints.Length; i++)
            {
                Vector3 localPoint = localProxyPoints[i];
                if (!KatamariMath.IsFiniteVector(localPoint)) return false;
                Vector3 worldDirection = localToWorldRotation * localPoint;
                if (!KatamariMath.IsFiniteVector(worldDirection)) return false;
                float projection;
                if (!TryDot(unitNormal, worldDirection, out projection)) return false;
                support = Mathf.Max(support, -projection);
            }
            if (!KatamariMath.IsFiniteFloat(support)) return false;

            float currentCoordinate;
            if (!TryDot(unitNormal, currentCenter, out currentCoordinate)) return false;
            float targetCoordinate = offset + support;
            if (!KatamariMath.IsFiniteFloat(targetCoordinate)) return false;
            float requestedCorrection = targetCoordinate - currentCoordinate;
            if (!KatamariMath.IsFiniteFloat(requestedCorrection)) return false;

            // Bounded, normal-only move; the tangential world position is kept.
            float correction = Mathf.Clamp(requestedCorrection, -maxDropPerStep, maxLiftPerStep);
            Vector3 correctedCenter = currentCenter + unitNormal * correction;
            if (!KatamariMath.IsFiniteVector(correctedCenter)) return false;
            float correctedCoordinate;
            if (!TryDot(unitNormal, correctedCenter, out correctedCoordinate)) return false;
            Vector3 anchor = unitNormal * offset;
            if (!KatamariMath.IsFiniteVector(anchor)) return false;
            float perturbation = Mathf.Clamp(requestedNormalSpeedPerturbation,
                -maxNormalSpeedPerturbation, maxNormalSpeedPerturbation);

            supportHeight = support;
            centerPosition = correctedCenter;
            centerNormalCoordinate = correctedCoordinate;
            planeAnchor = anchor;
            appliedNormalCorrection = correction;
            normalSpeedPerturbation = perturbation;
            liftClamped = requestedCorrection > maxLiftPerStep;
            dropClamped = requestedCorrection < -maxDropPerStep;
            return true;
        }

        private static bool IsUnitRotation(Quaternion rotation)
        {
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            float squaredLength = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            return KatamariMath.IsFiniteFloat(squaredLength) &&
                Mathf.Abs(squaredLength - 1f) <= RotationTolerance;
        }

        private static bool TryDot(Vector3 a, Vector3 b, out float value)
        {
            value = 0f;
            float dot = a.x * b.x + a.y * b.y + a.z * b.z;
            if (!KatamariMath.IsFiniteFloat(dot)) return false;
            value = dot;
            return true;
        }
    }
}
