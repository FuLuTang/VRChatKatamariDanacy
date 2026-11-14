using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Motion
{
    /// <summary>
    /// One fixed-step planar motion solve for the ball: heading turn, forward
    /// acceleration, exponential friction, bounded lateral assist, the
    /// growth-derated speed and turn caps, the no-input braking bound, and the
    /// planar displacement that step produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of the velocity half of `_step` in
    /// Docs/Design/BallMotion/ball_motion.py, the DRAFT host-side reference
    /// model. This is the only place the model's planar velocity rules live.
    /// The caller owns the clock, the fixed-step accumulator, the heading and
    /// the velocity, and passes all of them back on every call; the class has no
    /// state, allocates nothing, declares no struct, takes no config object and
    /// exposes exactly one entry point.
    /// </para>
    /// <para>
    /// Frame and units: the project's single world space, +Y up, metres and
    /// seconds. Motion is planar in the XZ plane only. headingDegrees is a yaw
    /// about +Y in degrees, kept in [0, 360), where 0 faces +Z; forward is
    /// (sin h, 0, cos h) and the lateral axis is (cos h, 0, -sin h), so heading
    /// grows from +Z towards +X, the same sense as Unity's yaw. Velocities are
    /// metres per second, the displacement is metres for this one step, and
    /// every rate that involves an angle is degrees per second.
    /// </para>
    /// <para>
    /// Vertical motion is not modelled here at all. velocity.y is never read by
    /// the solve, and nextVelocity.y and displacement.y are always 0. The
    /// model's vertical and post-move terms - wall-normal velocity removal, the
    /// slope support constraint y &gt;= floor(x) + radius, the visual spin, the
    /// stuck timer and the out-of-bounds reset - belong to the sweep,
    /// ground-contact, visual-roll and motor layers. A caller holding a vertical
    /// velocity merges it itself; a non-zero velocity.y is accepted and ignored,
    /// while a non-finite one is still rejected as corrupt input.
    /// </para>
    /// <para>
    /// Per step, in the model's order: growthFraction is clamped to [0, 1] (the
    /// caller derives it from the radius it owns - this layer never maps volume
    /// or radius); the speed and turn caps are derated by 1 - factor * growth;
    /// the heading advances by clamp(turn) * turnCap * deltaTime and is wrapped
    /// into [0, 360); acceleration is applied along the heading the step ENDED
    /// with; the whole planar velocity is scaled by exp(-friction * deltaTime);
    /// the lateral component is clamped to +-maxLateralMps; the planar speed is
    /// scaled down to the cap, keeping its direction; and when the clamped
    /// forward input is below 1e-9 the planar speed is reduced by
    /// braking * deltaTime without ever reversing. Position is the new velocity
    /// times deltaTime, so travel is never sub-stepped or clipped: keeping a
    /// fast ball out of a thin wall is the sweep layer's job.
    /// </para>
    /// <para>
    /// Consequence worth knowing before tuning: because the braking bound is a
    /// speed decrement, one step of braking can exceed one step of lateral
    /// buildup (the model's lateral assist is smaller than its braking rate), so
    /// lateral input with no forward input does not sustain sideways travel.
    /// That is the model's behaviour, not an omission here.
    /// </para>
    /// <para>
    /// Deviations from the reference: the model rejects a non-positive fixed
    /// step when it builds its configuration and lets NaN through its clamps,
    /// whereas this port returns false for every non-finite input and for a
    /// deltaTime of zero or less (a zero-length step would otherwise let the
    /// speed cap clamp velocity with no time passing); the
    /// model takes a radius and derives the growth term itself, while
    /// growthFraction arrives as an input here so the radius mapping stays in
    /// SizeModelMath; and the arithmetic is float, not double. All tune values
    /// are caller arguments - the model's defaults are DRAFT placeholders and
    /// are deliberately not baked in as project constants.
    /// </para>
    /// <para>
    /// Validation is atomic: false means no value was consumed and every out
    /// value is zero, so a caller never sees a half-solved step. A non-finite
    /// intermediate (an overflowing acceleration, damping or step length, or a
    /// planar speed beyond float range) is refused rather than clamped.
    /// </para>
    /// </remarks>
    public static class BallMotionMath
    {
        // Below this a clamped forward input is "no forward intent", so the
        // model applies its braking bound instead of acceleration.
        private const float NoForwardIntent = 1e-9f;

        // Largest growth derating factor the model's cap formula can carry
        // without driving a cap negative.
        private const float MaximumGrowthFactor = 1f;

        /// <summary>
        /// Solves one fixed step of planar ball motion.
        /// </summary>
        /// <remarks>
        /// forward, turn and lateral are the model's normalized intent axes and
        /// are clamped to [-1, 1]; headingDegrees is the stored heading and is
        /// wrapped, not clamped; velocity is the current planar velocity in
        /// metres per second (its y is ignored); deltaTime is this fixed step in
        /// seconds and must be positive; growthFraction is the model's
        /// dimensionless growth term, clamped to [0, 1]. The remaining eight
        /// arguments are the caller's tuning inputs in metres, seconds, degrees
        /// and their per-second forms, and must be finite and non-negative;
        /// growthSpeedFactor and growthTurnFactor must also be at most 1.
        ///
        /// On success nextHeadingDegrees is in [0, 360), nextVelocity is the
        /// planar velocity after the step with y = 0, and displacement is the
        /// ball-center move for this step in metres with y = 0. False leaves all
        /// three zero.
        /// </remarks>
        public static bool TryStep(
            float forward,
            float turn,
            float lateral,
            float headingDegrees,
            Vector3 velocity,
            float deltaTime,
            float growthFraction,
            float maxSpeedMps,
            float accelerationMps2,
            float brakingMps2,
            float frictionPerSecond,
            float turnRateDegreesPerSecond,
            float maxLateralMps,
            float growthSpeedFactor,
            float growthTurnFactor,
            out float nextHeadingDegrees,
            out Vector3 nextVelocity,
            out Vector3 displacement)
        {
            nextHeadingDegrees = 0f;
            nextVelocity = Vector3.zero;
            displacement = Vector3.zero;

            if (!KatamariMath.IsFiniteFloat(forward)) return false;
            if (!KatamariMath.IsFiniteFloat(turn)) return false;
            if (!KatamariMath.IsFiniteFloat(lateral)) return false;
            if (!KatamariMath.IsFiniteFloat(headingDegrees)) return false;
            if (!KatamariMath.IsFiniteVector(velocity)) return false;
            if (!KatamariMath.IsFiniteFloat(deltaTime) || deltaTime <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(growthFraction)) return false;
            if (!IsNonNegativeFinite(maxSpeedMps)) return false;
            if (!IsNonNegativeFinite(accelerationMps2)) return false;
            if (!IsNonNegativeFinite(brakingMps2)) return false;
            if (!IsNonNegativeFinite(frictionPerSecond)) return false;
            if (!IsNonNegativeFinite(turnRateDegreesPerSecond)) return false;
            if (!IsNonNegativeFinite(maxLateralMps)) return false;
            if (!IsGrowthFactor(growthSpeedFactor)) return false;
            if (!IsGrowthFactor(growthTurnFactor)) return false;

            // The model reaches this term through a radius already clamped into
            // [radiusMin, radiusMax]; the fraction arrives already normalized, so
            // only its own bound is enforced here.
            float growth = Mathf.Clamp(growthFraction, 0f, 1f);
            float speedCap = maxSpeedMps * (1f - growthSpeedFactor * growth);
            float turnCap = turnRateDegreesPerSecond * (1f - growthTurnFactor * growth);
            if (!KatamariMath.IsFiniteFloat(speedCap) || speedCap < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(turnCap) || turnCap < 0f) return false;

            float forwardIntent = Mathf.Clamp(forward, -1f, 1f);
            float turnIntent = Mathf.Clamp(turn, -1f, 1f);
            float lateralIntent = Mathf.Clamp(lateral, -1f, 1f);
            float heading = Wrap360(headingDegrees + turnIntent * turnCap * deltaTime);
            if (!KatamariMath.IsFiniteFloat(heading)) return false;

            // Spelled from Mathf.PI, the one Mathf constant the Udon runtime
            // sources in this project already use, so the step depends on no
            // extra extern and no literal radian factor.
            float radians = heading * (Mathf.PI / 180f);
            float forwardX = Mathf.Sin(radians);
            float forwardZ = Mathf.Cos(radians);
            float lateralX = Mathf.Cos(radians);
            float lateralZ = -Mathf.Sin(radians);

            // Acceleration points along the heading this step ended with, not the
            // one it started from: the model turns first, then accelerates.
            float velocityX = velocity.x +
                forwardX * forwardIntent * accelerationMps2 * deltaTime;
            float velocityZ = velocity.z +
                forwardZ * forwardIntent * accelerationMps2 * deltaTime;
            float damping;
            if (!TryDamping(frictionPerSecond, deltaTime, out damping)) return false;
            velocityX *= damping;
            velocityZ *= damping;
            if (!KatamariMath.IsFiniteFloat(velocityX) ||
                !KatamariMath.IsFiniteFloat(velocityZ)) return false;

            // Lateral assist is a bounded speed addition, not an acceleration
            // that can accumulate without limit.
            float lateralSpeed = velocityX * lateralX + velocityZ * lateralZ;
            lateralSpeed = Mathf.Clamp(
                lateralSpeed + lateralIntent * maxLateralMps * deltaTime,
                -maxLateralMps, maxLateralMps);
            float forwardSpeed = velocityX * forwardX + velocityZ * forwardZ;

            float planarSpeed;
            if (!TryPlanarSpeed(forwardSpeed, lateralSpeed, out planarSpeed)) return false;
            if (planarSpeed > speedCap)
            {
                // planarSpeed > speedCap >= 0, so the divisor is positive; the
                // direction is kept and only the length shrinks.
                float scale = speedCap / planarSpeed;
                forwardSpeed *= scale;
                lateralSpeed *= scale;
            }

            velocityX = forwardSpeed * forwardX + lateralSpeed * lateralX;
            velocityZ = forwardSpeed * forwardZ + lateralSpeed * lateralZ;

            // No forward intent is a braking bound, not a reverse thrust: the
            // reduction is applied to the recovered velocity and floors at zero,
            // so coasting never oscillates through a stop.
            if (Mathf.Abs(forwardIntent) < NoForwardIntent)
            {
                float speed;
                if (!TryPlanarSpeed(velocityX, velocityZ, out speed)) return false;
                if (speed > 0f)
                {
                    float reduced = Mathf.Max(0f, speed - brakingMps2 * deltaTime);
                    float scale = reduced / speed;
                    velocityX *= scale;
                    velocityZ *= scale;
                }
            }

            float stepX = velocityX * deltaTime;
            float stepZ = velocityZ * deltaTime;
            if (!KatamariMath.IsFiniteFloat(stepX) ||
                !KatamariMath.IsFiniteFloat(stepZ)) return false;
            if (!KatamariMath.IsFiniteFloat(velocityX) ||
                !KatamariMath.IsFiniteFloat(velocityZ)) return false;

            nextHeadingDegrees = heading;
            nextVelocity = new Vector3(velocityX, 0f, velocityZ);
            displacement = new Vector3(stepX, 0f, stepZ);
            return true;
        }

        private static bool IsNonNegativeFinite(float value)
        {
            return KatamariMath.IsFiniteFloat(value) && value >= 0f;
        }

        private static bool IsGrowthFactor(float value)
        {
            return KatamariMath.IsFiniteFloat(value) &&
                value >= 0f && value <= MaximumGrowthFactor;
        }

        private static bool TryDamping(float frictionPerSecond, float deltaTime, out float damping)
        {
            damping = 0f;
            float exponent = -frictionPerSecond * deltaTime;
            if (!KatamariMath.IsFiniteFloat(exponent)) return false;
            float candidate = Mathf.Exp(exponent);
            if (!KatamariMath.IsFiniteFloat(candidate)) return false;
            if (candidate < 0f || candidate > 1f) return false;
            damping = candidate;
            return true;
        }

        /// <summary>
        /// Length of a planar vector, computed the way KatamariMath.TryNormalize
        /// does it: the largest component is divided out first, so a large but
        /// representable speed does not overflow and a tiny one does not
        /// underflow.
        /// </summary>
        private static bool TryPlanarSpeed(float x, float z, out float speed)
        {
            speed = 0f;
            if (!KatamariMath.IsFiniteFloat(x) || !KatamariMath.IsFiniteFloat(z)) return false;
            float largest = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
            if (largest == 0f) return true;
            float scaledX = x / largest;
            float scaledZ = z / largest;
            float length = Mathf.Sqrt(scaledX * scaledX + scaledZ * scaledZ);
            if (!KatamariMath.IsFiniteFloat(length) || length <= 0f) return false;
            float candidate = largest * length;
            if (!KatamariMath.IsFiniteFloat(candidate)) return false;
            speed = candidate;
            return true;
        }

        private static float Wrap360(float degrees)
        {
            // The model's % is a positive modulo, unlike C#'s sign-preserving one.
            float wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            return wrapped;
        }
    }
}
