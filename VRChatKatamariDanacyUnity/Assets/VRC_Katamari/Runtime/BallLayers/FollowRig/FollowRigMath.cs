using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Filters the presentation frame that the rider's station rides: behind the ball and
    /// above it, both distances growing with the ball's radius.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Positions are metres with +Y up; headings are degrees about +Y and are reported in
    /// [0, 360). The frame is a position plus a yaw only: there is no roll, no pitch and no
    /// quaternion in the API, and no HMD pose, controller view axis or ball self-rotation
    /// input exists, so those cannot leak into the frame.
    /// </para>
    /// <para>
    /// The frame is <c>ballCentre - forward * back + up * height</c> with
    /// <c>back = eyeBaseBackMeters + radius * eyeBackPerRadius</c> and
    /// <c>height = eyeBaseUpMeters + radius * eyeUpPerRadius</c>: the camera is BEHIND the
    /// ball and above it, and both terms grow linearly with the radius. That is the framing
    /// this layer is for - a small ball starts with a low camera just behind it, and a grown
    /// ball lifts the camera because the ball itself is taller. A fixed height above the
    /// CENTRE instead loses surface clearance as the ball grows, and the props standing on
    /// that surface grow with it, which is what buries the view; a frame measured with the
    /// radius term keeps the clearance roughly constant and lets the ball stay in the lower
    /// part of a level view.
    /// </para>
    /// <para>
    /// Why a station and not a camera: VRChat has no API that moves an avatar continuously.
    /// The only mechanisms are <c>VRCStation</c> and teleporting, so "a person pushing the
    /// ball" can only be built as a station placed behind the ball. In first person that
    /// reads exactly as pushing - the ball ahead and below, the eye behind and above it -
    /// but a third-person viewer sees the avatar sitting, which is an engine limit, not a
    /// design choice made here.
    /// </para>
    /// <para>
    /// The previous filtered state is caller-owned: hasPreviousFrame plus the last returned
    /// position, yaw and height are passed back in. A missing previous frame or
    /// resetToTeleport snaps to the new reference instead of interpolating across the map.
    /// Only x and z of previousRigWorldPosition are read; its y is always recomputed from the
    /// current ball Y and the current height. False means invalid input or a non-finite
    /// candidate; no output is written and the caller keeps the state it passed in.
    /// </para>
    /// <para>
    /// Each filter alpha is 1 - exp(-2*pi*hz*dt), so 0 Hz or 0 dt holds the previous value.
    /// Yaw uses the shortest arc and never overshoots, and the back offset is taken along the
    /// filtered yaw - the same yaw written to the transform - so the frame's own facing and
    /// its offset direction cannot disagree. The frequencies, the base/lift factors and their
    /// bounds are caller tuning inputs, not measured product values or VR comfort limits.
    /// </para>
    /// </remarks>
    public static class FollowRigMath
    {
        /// <summary>
        /// Advances the filtered reference frame by one sample.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ballWorldPosition is the ball's CENTRE and ballRadiusM its drawn radius, so the
        /// top of the ball is <c>ballWorldPosition.y + ballRadiusM</c>. heightMeters is the
        /// committed height above that centre; pass it back on the next call with the
        /// returned position and yaw. On success rigYawDegrees is in [0, 360).
        /// </para>
        /// <para>
        /// The bounds bound the height above the CENTRE, and their defaults are rails rather
        /// than tuning: they must not force a small ball up to a standing person's eye height
        /// (a 0.10 m ball is played from about 0.7 m above the floor) and must not pin a large
        /// one. A negative or non-finite radius, factor or bound is a data error, not a clamp
        /// request. A zero radius is allowed: it is the same geometry with no ball, which is
        /// what a motor that has not derived a size yet reports.
        /// </para>
        /// </remarks>
        public static bool TryUpdate(
            float heightFilterHz,
            float yawFilterHz,
            Vector3 ballWorldPosition,
            float ballRadiusM,
            float horizontalYawDegrees,
            float dtSeconds,
            float eyeBaseUpMeters,
            float eyeUpPerRadius,
            float eyeUpMinMeters,
            float eyeUpMaxMeters,
            float eyeBaseBackMeters,
            float eyeBackPerRadius,
            float eyeMaxBackM,
            bool resetToTeleport,
            bool hasPreviousFrame,
            Vector3 previousRigWorldPosition,
            float previousRigYawDegrees,
            float previousHeightMeters,
            out Vector3 rigWorldPosition,
            out float rigYawDegrees,
            out float heightMeters)
        {
            rigWorldPosition = Vector3.zero;
            rigYawDegrees = 0f;
            heightMeters = 0f;

            if (!KatamariMath.IsFiniteFloat(heightFilterHz) || heightFilterHz < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(yawFilterHz) || yawFilterHz < 0f) return false;
            if (!KatamariMath.IsFiniteVector(ballWorldPosition)) return false;
            if (!KatamariMath.IsFiniteFloat(ballRadiusM) || ballRadiusM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(horizontalYawDegrees)) return false;
            if (!KatamariMath.IsFiniteFloat(dtSeconds) || dtSeconds < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeBaseUpMeters) || eyeBaseUpMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeUpPerRadius) || eyeUpPerRadius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeUpMinMeters) || eyeUpMinMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeUpMaxMeters) || eyeUpMaxMeters < 0f) return false;
            if (eyeUpMinMeters > eyeUpMaxMeters) return false;
            if (!KatamariMath.IsFiniteFloat(eyeBaseBackMeters) || eyeBaseBackMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeBackPerRadius) || eyeBackPerRadius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeMaxBackM) || eyeMaxBackM < 0f) return false;

            // The previous frame is read only by the filtered branch, so a reset
            // does not force the caller to repair a stale frame first.
            bool usePrevious = hasPreviousFrame && !resetToTeleport;
            if (usePrevious)
            {
                if (!KatamariMath.IsFiniteVector(previousRigWorldPosition)) return false;
                if (!KatamariMath.IsFiniteFloat(previousRigYawDegrees)) return false;
                if (!KatamariMath.IsFiniteFloat(previousHeightMeters)) return false;
            }

            float alphaHeight;
            float alphaYaw;
            if (!TryFilterAlpha(heightFilterHz, dtSeconds, out alphaHeight)) return false;
            if (!TryFilterAlpha(yawFilterHz, dtSeconds, out alphaYaw)) return false;

            // Behind and above the ball, both measured from its CENTRE and both growing with
            // its radius: the ball approaches the camera from below as it grows, instead of
            // the camera being left behind at a fixed height.
            float targetHeight = eyeBaseUpMeters + ballRadiusM * eyeUpPerRadius;
            if (!KatamariMath.IsFiniteFloat(targetHeight)) return false;
            targetHeight = Mathf.Clamp(targetHeight, eyeUpMinMeters, eyeUpMaxMeters);

            float back = eyeBaseBackMeters + ballRadiusM * eyeBackPerRadius;
            if (!KatamariMath.IsFiniteFloat(back)) return false;
            if (back < 0f) back = 0f;
            if (back > eyeMaxBackM) back = eyeMaxBackM;

            float targetYaw = Wrap360(horizontalYawDegrees);
            float yaw;
            if (!usePrevious)
            {
                yaw = targetYaw;
            }
            else
            {
                yaw = Wrap360(previousRigYawDegrees +
                    ShortestDelta(targetYaw, previousRigYawDegrees) * alphaYaw);
            }

            // Behind the ball along the frame's own yaw, so the offset direction and the
            // facing written to the transform can never disagree.
            float yawRadians = yaw * Mathf.Deg2Rad;
            float targetX = ballWorldPosition.x - Mathf.Sin(yawRadians) * back;
            float targetZ = ballWorldPosition.z - Mathf.Cos(yawRadians) * back;

            float x;
            float z;
            float height;
            if (!usePrevious)
            {
                x = targetX;
                z = targetZ;
                height = Mathf.Clamp(targetHeight, eyeUpMinMeters, eyeUpMaxMeters);
            }
            else
            {
                // A changed bound must not make the stored height illegal.
                float priorHeight = Mathf.Clamp(previousHeightMeters, eyeUpMinMeters, eyeUpMaxMeters);
                height = Mathf.Clamp(
                    priorHeight + (targetHeight - priorHeight) * alphaHeight,
                    eyeUpMinMeters, eyeUpMaxMeters);
                x = previousRigWorldPosition.x + (targetX - previousRigWorldPosition.x) * alphaHeight;
                z = previousRigWorldPosition.z + (targetZ - previousRigWorldPosition.z) * alphaHeight;
            }

            float y = ballWorldPosition.y + height;
            if (!KatamariMath.IsFiniteFloat(x) || !KatamariMath.IsFiniteFloat(y)) return false;
            if (!KatamariMath.IsFiniteFloat(z)) return false;
            if (!KatamariMath.IsFiniteFloat(height) || !KatamariMath.IsFiniteFloat(yaw)) return false;

            rigWorldPosition = new Vector3(x, y, z);
            rigYawDegrees = yaw;
            heightMeters = height;
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
