using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Filters a presentation reference frame from the ball's world position and
    /// scalar horizontal heading.
    /// </summary>
    /// <remarks>
    /// Positions are metres with +Y up; headings are degrees about +Y and are
    /// reported in [0, 360). The frame is a position plus a yaw only: there is no
    /// roll, no pitch and no quaternion in the API, and no HMD pose, controller
    /// view axis or ball self-rotation input exists, so those cannot leak into
    /// the frame. The vertical output is the current ball Y plus a filtered
    /// eye-height offset, and that offset is clamped to the caller's per-call
    /// bounds, so filter lag can never leave them.
    ///
    /// The previous filtered state is caller-owned: hasPreviousFrame plus the
    /// last returned position, yaw and offset are passed back in. A missing
    /// previous frame or resetToTeleport snaps to the new reference instead of
    /// interpolating across the map. Only x and z of previousRigWorldPosition
    /// are read; its y is always recomputed from the current ball Y. False means
    /// invalid input or a non-finite candidate; no output is written and the
    /// caller keeps the state it passed in.
    ///
    /// Each filter alpha is 1 - exp(-2*pi*hz*dt), so 0 Hz or 0 dt holds the
    /// previous value. Yaw uses the shortest arc and never overshoots. The
    /// filter frequencies and eye-height bounds are caller tuning inputs, not
    /// measured product values or VR comfort limits.
    /// </remarks>
    public static class FollowRigMath
    {
        /// <summary>
        /// Advances the filtered reference frame by one sample.
        /// </summary>
        /// <remarks>
        /// eyeOffsetMeters is the committed offset; pass it back on the next call
        /// with the returned position and yaw. On success rigYawDegrees is in
        /// [0, 360).
        /// </remarks>
        public static bool TryUpdate(
            float heightFilterHz,
            float yawFilterHz,
            Vector3 ballWorldPosition,
            float horizontalYawDegrees,
            float dtSeconds,
            float eyeHeightMeters,
            float eyeHeightMinMeters,
            float eyeHeightMaxMeters,
            bool resetToTeleport,
            bool hasPreviousFrame,
            Vector3 previousRigWorldPosition,
            float previousRigYawDegrees,
            float previousEyeOffsetMeters,
            out Vector3 rigWorldPosition,
            out float rigYawDegrees,
            out float eyeOffsetMeters)
        {
            rigWorldPosition = Vector3.zero;
            rigYawDegrees = 0f;
            eyeOffsetMeters = 0f;

            if (!KatamariMath.IsFiniteFloat(heightFilterHz) || heightFilterHz < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(yawFilterHz) || yawFilterHz < 0f) return false;
            if (!KatamariMath.IsFiniteVector(ballWorldPosition)) return false;
            if (!KatamariMath.IsFiniteFloat(horizontalYawDegrees)) return false;
            if (!KatamariMath.IsFiniteFloat(dtSeconds) || dtSeconds < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeHeightMeters)) return false;
            if (!KatamariMath.IsFiniteFloat(eyeHeightMinMeters)) return false;
            if (!KatamariMath.IsFiniteFloat(eyeHeightMaxMeters)) return false;
            if (eyeHeightMinMeters > eyeHeightMaxMeters) return false;

            // The previous frame is read only by the filtered branch, so a reset
            // does not force the caller to repair a stale frame first.
            bool usePrevious = hasPreviousFrame && !resetToTeleport;
            if (usePrevious)
            {
                if (!KatamariMath.IsFiniteVector(previousRigWorldPosition)) return false;
                if (!KatamariMath.IsFiniteFloat(previousRigYawDegrees)) return false;
                if (!KatamariMath.IsFiniteFloat(previousEyeOffsetMeters)) return false;
            }

            float alphaHeight;
            float alphaYaw;
            if (!TryFilterAlpha(heightFilterHz, dtSeconds, out alphaHeight)) return false;
            if (!TryFilterAlpha(yawFilterHz, dtSeconds, out alphaYaw)) return false;

            float targetOffset = Mathf.Clamp(eyeHeightMeters, eyeHeightMinMeters, eyeHeightMaxMeters);
            float targetYaw = Wrap360(horizontalYawDegrees);
            float x;
            float z;
            float offset;
            float yaw;
            if (!usePrevious)
            {
                x = ballWorldPosition.x;
                z = ballWorldPosition.z;
                offset = targetOffset;
                yaw = targetYaw;
            }
            else
            {
                // A changed bound must not make the stored offset illegal.
                float priorOffset = Mathf.Clamp(previousEyeOffsetMeters,
                    eyeHeightMinMeters, eyeHeightMaxMeters);
                offset = Mathf.Clamp(
                    priorOffset + (targetOffset - priorOffset) * alphaHeight,
                    eyeHeightMinMeters, eyeHeightMaxMeters);
                yaw = Wrap360(previousRigYawDegrees +
                    ShortestDelta(targetYaw, previousRigYawDegrees) * alphaYaw);
                x = previousRigWorldPosition.x +
                    (ballWorldPosition.x - previousRigWorldPosition.x) * alphaHeight;
                z = previousRigWorldPosition.z +
                    (ballWorldPosition.z - previousRigWorldPosition.z) * alphaHeight;
            }

            float y = ballWorldPosition.y + offset;
            if (!KatamariMath.IsFiniteFloat(x) || !KatamariMath.IsFiniteFloat(y)) return false;
            if (!KatamariMath.IsFiniteFloat(z)) return false;
            if (!KatamariMath.IsFiniteFloat(offset) || !KatamariMath.IsFiniteFloat(yaw)) return false;

            rigWorldPosition = new Vector3(x, y, z);
            rigYawDegrees = yaw;
            eyeOffsetMeters = offset;
            return true;
        }

        private static bool TryFilterAlpha(float hertz, float dtSeconds, out float alpha)
        {
            alpha = 0f;
            if (hertz == 0f || dtSeconds == 0f) return true;
            float exponent = 2f * Mathf.PI * hertz * dtSeconds;
            if (!KatamariMath.IsFiniteFloat(exponent)) return false;
            float candidate = 1f - Mathf.Exp(-exponent);
            if (!KatamariMath.IsFiniteFloat(candidate)) return false;
            if (candidate < 0f || candidate > 1f) return false;
            alpha = candidate;
            return true;
        }

        private static float Wrap360(float degrees)
        {
            float wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            return wrapped;
        }

        private static float ShortestDelta(float target, float current)
        {
            // The reference uses (target - current + 180) % 360 - 180. This form
            // is equivalent but skips the +180/-180 round trip, which in float
            // would round a difference below ~4e-6 deg to zero.
            float difference = (target - current) % 360f;
            if (difference >= 180f) difference -= 360f;
            if (difference < -180f) difference += 360f;
            return difference;
        }
    }
}
