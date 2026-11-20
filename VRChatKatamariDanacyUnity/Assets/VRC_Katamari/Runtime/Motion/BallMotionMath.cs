using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Motion
{
    /// <summary>
    /// One fixed-step planar motion solve for the ball: heading turn, forward
    /// acceleration or brake push, forward/lateral drag with lateral grip, rolling
    /// resistance, strafe assist, the size-scaled speed cap, and the
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
    /// Per step, in the model's order: every length-type tune value (max speed,
    /// acceleration, braking, rolling resistance, strafe cap) is multiplied by
    /// sizeScale = D / D0, the current semantic diameter over the START diameter
    /// (the caller owns it; this layer never maps volume or radius), so the
    /// feel in diameters per second is the same at every size; the time-type
    /// values (friction, lateral grip, turn rate) are not scaled and there is no
    /// growth derating; the heading advances by clamp(turn) * turnRate *
    /// deltaTime and is wrapped into [0, 360); the velocity is split along the
    /// NEW heading into forward
    /// and lateral parts; forward input adds acceleration, or brakingMps2 when it
    /// opposes the forward velocity; forward drag is exp(-friction * deltaTime)
    /// and lateral drag exp(-(friction + lateralGrip) * deltaTime); strafe input
    /// adds up to maxLateralMps of lateral speed; with no forward input the
    /// forward speed loses rollingResistance * deltaTime, floored at zero;
    /// the WLK stick curve sets a longitudinal drive target. Same-direction
    /// overspeed rolls down to it; opposing input brakes without a target clamp
    /// before crossing zero. The base forward/reverse and planar caps still apply;
    /// the planar speed is scaled
    /// down to the cap, keeping its direction. Position is the new velocity
    /// times deltaTime, so travel is never sub-stepped or clipped: keeping a
    /// fast ball out of a thin wall is the sweep layer's job.
    /// </para>
    /// <para>
    /// Inertia (BALL_INERTIA.md): all tune values are PROVISIONAL. The values are
    /// given at D0; the KatamariBallMotor defaults are acceleration 7.5, friction 0, rolling
    /// resistance 1.9, lateral grip 2.9, braking 8, max speed 4. The old behaviour is
    /// approximated by sizeScale 1, acceleration 5, braking 8, friction 1.2, rolling
    /// resistance 8, lateral grip 1000 (comparison only; not bit-identical,
    /// because the old clamp acted on the lateral part and the old brake on the
    /// total speed).
    /// </para>
    /// <para>
    /// Deviations from the reference: the model rejects a non-positive fixed
    /// step when it builds its configuration and lets NaN through its clamps,
    /// whereas this port returns false for every non-finite input and for a
    /// deltaTime of zero or less (a zero-length step would otherwise let the
    /// speed cap clamp velocity with no time passing); the
    /// model takes a radius and derives the scale itself, while sizeScale
    /// arrives as an input here so the radius mapping stays in
    /// SizeModelMath; and the arithmetic is float, not double. All tune values
    /// are caller arguments and are deliberately not baked in as project constants.
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
        // Below this a clamped forward input is "no forward intent": no thrust,
        // and rolling resistance applies instead.
        private const float NoForwardIntent = 1e-9f;

        // Reverse speed cap as a fraction of the size-scaled forward cap
        // (PROVISIONAL, BALL_INERTIA.md section 3).
        private const float ReverseSpeedFraction = 0.5f;

        // WLK REROLL+ Royal Reverie v20230608 反汇编：sGetMaxSpeedRate
        // 0x9835b7–0x98365b，BasicParam.f32SpeedTilt1/2、f32HalfSpeedRate。
        // 证据：.work/notes/research/wlk_reroll_mining.md；inertia_wlk.md「WLK 的模型」。
        private const float HalfSpeedStart = 0.29f;
        private const float FullSpeedStart = 0.99f;
        private const float HalfSpeedRate = 0.4f;

        /// <summary>把前后意图映射为目标速度倍率；松手没有驱动目标。</summary>
        public static float DriveSpeedFraction(float forward)
        {
            if (!KatamariMath.IsFiniteFloat(forward)) return 0f;
            float power = Mathf.Abs(forward);
            if (power < NoForwardIntent) return 0f;
            if (power < HalfSpeedStart) return HalfSpeedRate * (power / HalfSpeedStart);
            return power < FullSpeedStart ? HalfSpeedRate : 1f;
        }

        /// <summary>静止纵轴由滚阻抵消不超过它的坡重力，横向分量不变。</summary>
        public static Vector3 HoldLongitudinalSlope(Vector3 velocity, Vector3 slopeVelocity,
            float headingDegrees, float rollingSpeedDelta)
        {
            float radians = headingDegrees * (Mathf.PI / 180f);
            float x = Mathf.Sin(radians);
            float z = Mathf.Cos(radians);
            float forwardSpeed = velocity.x * x + velocity.z * z;
            float change = (slopeVelocity.x - velocity.x) * x + (slopeVelocity.z - velocity.z) * z;
            // WLK REROLL+ sResistance / sGravity（inertia_wlk.md「WLK 的模型」）：
            // 重力不超过滚阻时不持续下滑。本作只对静止的纵轴实现这道门，
            // 因接触层在运动层扣完滚阻后才加重力，否则这个增量会每步造成位移。
            if (Mathf.Abs(forwardSpeed) > rollingSpeedDelta * 1e-4f ||
                Mathf.Abs(change) > rollingSpeedDelta) return slopeVelocity;
            return new Vector3(slopeVelocity.x - x * change, slopeVelocity.y,
                slopeVelocity.z - z * change);
        }

        /// <summary>
        /// Solves one fixed step of planar ball motion.
        /// </summary>
        /// <remarks>
        /// forward, turn and lateral are the model's normalized intent axes and
        /// are clamped to [-1, 1]; headingDegrees is the stored heading and is
        /// wrapped, not clamped; velocity is the current planar velocity in
        /// metres per second (its y is ignored); deltaTime is this fixed step in
        /// seconds and must be positive; sizeScale = D / D0 must be finite and
        /// positive. The remaining nine arguments are the caller's tuning inputs
        /// at D0 in metres, seconds, degrees and their per-second forms, and
        /// must be finite and non-negative.
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
            float sizeScale,
            float maxSpeedMps,
            float accelerationMps2,
            float brakingMps2,
            float frictionPerSecond,
            float rollingResistanceMps2,
            float lateralGripPerSecond,
            float turnRateDegreesPerSecond,
            float maxLateralMps,
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
            if (!KatamariMath.IsFiniteFloat(sizeScale) || sizeScale <= 0f) return false;
            if (!IsNonNegativeFinite(maxSpeedMps)) return false;
            if (!IsNonNegativeFinite(accelerationMps2)) return false;
            if (!IsNonNegativeFinite(brakingMps2)) return false;
            if (!IsNonNegativeFinite(frictionPerSecond)) return false;
            if (!IsNonNegativeFinite(rollingResistanceMps2)) return false;
            if (!IsNonNegativeFinite(lateralGripPerSecond)) return false;
            if (!IsNonNegativeFinite(turnRateDegreesPerSecond)) return false;
            if (!IsNonNegativeFinite(maxLateralMps)) return false;

            // Every length-type tune value is given at the START diameter D0 and
            // scales with sizeScale = D / D0, so speed in diameters per second,
            // glide in diameters and every start/stop time are the same at any
            // size. Time constants (friction, grip, turn rate) do not scale.
            float speedCap = maxSpeedMps * sizeScale;
            float scaledAcceleration = accelerationMps2 * sizeScale;
            float scaledBraking = brakingMps2 * sizeScale;
            float scaledRolling = rollingResistanceMps2 * sizeScale;
            float scaledLateral = maxLateralMps * sizeScale;
            if (!KatamariMath.IsFiniteFloat(speedCap)) return false;
            if (!KatamariMath.IsFiniteFloat(scaledAcceleration)) return false;
            if (!KatamariMath.IsFiniteFloat(scaledBraking)) return false;
            if (!KatamariMath.IsFiniteFloat(scaledRolling)) return false;
            if (!KatamariMath.IsFiniteFloat(scaledLateral)) return false;

            float forwardIntent = Mathf.Clamp(forward, -1f, 1f);
            float turnIntent = Mathf.Clamp(turn, -1f, 1f);
            float lateralIntent = Mathf.Clamp(lateral, -1f, 1f);
            float heading = Wrap360(headingDegrees + turnIntent * turnRateDegreesPerSecond * deltaTime);
            if (!KatamariMath.IsFiniteFloat(heading)) return false;

            // Spelled from Mathf.PI, the one Mathf constant the Udon runtime
            // sources in this project already use, so the step depends on no
            // extra extern and no literal radian factor.
            float radians = heading * (Mathf.PI / 180f);
            float forwardX = Mathf.Sin(radians);
            float forwardZ = Mathf.Cos(radians);
            float lateralX = Mathf.Cos(radians);
            float lateralZ = -Mathf.Sin(radians);

            // The world velocity is split along the heading this step ENDED with
            // (the model turns first), so a turn leaves the old direction behind as
            // lateral velocity: that is the drift.
            float forwardSpeed = velocity.x * forwardX + velocity.z * forwardZ;
            float lateralSpeed = velocity.x * lateralX + velocity.z * lateralZ;

            float driveSpeedFraction = DriveSpeedFraction(forwardIntent);
            bool hasForwardIntent = driveSpeedFraction > 0f;
            float driveSign = forwardIntent < 0f ? -1f : 1f;
            float targetSpeed = speedCap * driveSpeedFraction;
            if (driveSign < 0f) targetSpeed *= ReverseSpeedFraction;
            float speedInDriveDirection = forwardSpeed * driveSign;
            bool aboveTarget = hasForwardIntent && speedInDriveDirection > targetSpeed;
            if (hasForwardIntent && !aboveTarget)
            {
                // 反推先按杆量和刹车率减速，过零后才受另一方向的目标限制。
                float rate = speedInDriveDirection < 0f ? scaledBraking : scaledAcceleration;
                forwardSpeed += forwardIntent * rate * deltaTime;
            }
            if (!KatamariMath.IsFiniteFloat(forwardSpeed)) return false;

            // Viscous drag; the lateral axis adds lateralGripPerSecond, which
            // replaces the old hard lateral clamp.
            float forwardDamping;
            float lateralDamping;
            if (!TryDamping(frictionPerSecond, deltaTime, out forwardDamping)) return false;
            if (!TryDamping(frictionPerSecond + lateralGripPerSecond, deltaTime,
                out lateralDamping)) return false;
            forwardSpeed *= forwardDamping;
            lateralSpeed *= lateralDamping;

            // Strafe input adds sideways speed but never pushes it past
            // maxLateralMps; speed already beyond it (drift) is left to the grip.
            if (lateralIntent > 0f)
            {
                lateralSpeed = Mathf.Max(lateralSpeed, Mathf.Min(
                    lateralSpeed + lateralIntent * scaledLateral * deltaTime, scaledLateral));
            }
            else if (lateralIntent < 0f)
            {
                lateralSpeed = Mathf.Min(lateralSpeed, Mathf.Max(
                    lateralSpeed + lateralIntent * scaledLateral * deltaTime, -scaledLateral));
            }

            // 松杆匀减到零；同向超出目标时不再加推力，以普通滚阻降到目标。
            // 原作 sSpeedLimit 区分既有超速与本帧越限，且有刹车状态分支；
            // 这里用同向滚阻接近目标是工程近似，不是其分支的逐行移植。
            if (!hasForwardIntent || aboveTarget)
            {
                float floor = aboveTarget ? targetSpeed : 0f;
                float speed = Mathf.Abs(forwardSpeed);
                if (speed > floor)
                    forwardSpeed = (forwardSpeed < 0f ? -1f : 1f) *
                        Mathf.Max(floor, speed - scaledRolling * deltaTime);
            }
            else if (forwardSpeed * driveSign > targetSpeed)
            {
                // 只约束本步新增的推力，不能将已有速度硬削到杆量目标。
                forwardSpeed = driveSign * targetSpeed;
            }

            forwardSpeed = Mathf.Clamp(forwardSpeed, -ReverseSpeedFraction * speedCap, speedCap);

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

            float velocityX = forwardSpeed * forwardX + lateralSpeed * lateralX;
            float velocityZ = forwardSpeed * forwardZ + lateralSpeed * lateralZ;
            if (!KatamariMath.IsFiniteFloat(velocityX) ||
                !KatamariMath.IsFiniteFloat(velocityZ)) return false;

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
