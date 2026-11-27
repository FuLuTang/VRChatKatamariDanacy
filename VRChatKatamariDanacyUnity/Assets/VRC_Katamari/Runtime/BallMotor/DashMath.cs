using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallMotor
{
    /// <summary>
    /// WLK REROLL+ Royal Reverie（v20230608）的冲刺档位、寿命和喘气。
    /// 用长按时长代替交替推杆；不在这里计算平移滚阻，沿用 BallMotionMath。
    /// </summary>
    public static class DashMath
    {
        // 状态存为定长数组，供 UdonSharp 与 EditMode 共用。
        public const int StateLength = 8;
        private const int Charging = 0;
        private const int HeldSeconds = 1;
        private const int DashKind = 2; // 1 = 满蓄，2 = 空转后松手；0 = 不在冲刺。
        private const int LaunchPending = 3;
        private const int LifeFrames = 4;
        private const int HuffFrames = 5;
        private const int FrameClock = 6;
        private const int RecoverFrames = 7;
        private const int FullDash = 1;
        private const int HelpDash = 2;

        public const float OriginalTickSeconds = 1f / 30f;
        // owner 2026-11-27：按真人每秒约 5 次交替换算，一次等效输入 0.2 s；
        // 第 N 次有效输入发生在 (N-1) × 0.2 s（原作 14 帧只是等待下一次反向输入的最长窗口）。
        public const float InputIntervalSeconds = 0.2f;
        public const float HelpSpeedScale = 0.67f; // f32DashHelpSpeedCoef
        private const int FullChargeAfterSpin = 4; // sDashSetInput
        public const int LifeCapacityFrames = 90; // s16DashLife
        public const int HuffDurationFrames = 240; // s16DashForbidTime
        private const int RecoverAfterFrames = 10; // s16DashRecoverTime
        private const float TimeTolerance = 1e-5f;

        // 工程表现值，单位是球壳的度/秒；不冒充 WLK 的 f32AccD / mExtraRoll。
        public const float SpinRateDegreesPerSecond = 180f;

        public const int ReleaseNone = 0;
        public const int ReleaseHelpDash = 1;
        public const int ReleaseBeforeSpin = 2;

        public static int SpinGachasFor(float diameterM)
        {
            // s32DashWheelSpinData（60,000 / 300,000 mm）。
            if (!KatamariMath.IsFiniteFloat(diameterM)) return 3;
            if (diameterM >= 300f) return 5;
            if (diameterM >= 60f) return 4;
            return 3;
        }

        public static float SpinSecondsFor(float diameterM)
        {
            return (SpinGachasFor(diameterM) - 1) * InputIntervalSeconds;
        }

        public static float ChargeSecondsFor(float diameterM)
        {
            return (SpinGachasFor(diameterM) + FullChargeAfterSpin - 1) * InputIntervalSeconds;
        }

        /// <summary>在本项目支持的 0–400 m 范围内，先分列插值 WLK RunParam，再取比值。
        /// 1,000 m 是 340–400 m 查询需要的上端点；不搬 WLK 的绝对速度刻度。</summary>
        public static void DriveScales(float[] state, float diameterM, out float speed, out float acceleration)
        {
            speed = 1f;
            acceleration = 1f;
            if (!IsDashing(state)) return;
            FullDriveScales(diameterM, out speed, out acceleration);
        }

        public static void FullDriveScales(float diameterM, out float speed, out float acceleration)
        {
            // gsCoreRunParam: 每段按 m、SpeedF/D、AccF/D 分别插值。
            if (!KatamariMath.IsFiniteFloat(diameterM) || diameterM <= 0.05f)
            {
                speed = 27f / 9f;
                acceleration = 5f / 0.4f;
                return;
            }
            if (diameterM <= 0.2f)
                InterpolateDriveScales(diameterM, 0.05f, 0.2f, 9f, 33f, 27f, 82f, 0.4f, 0.7f, 5f, 4f, out speed, out acceleration);
            else if (diameterM <= 0.5f)
                InterpolateDriveScales(diameterM, 0.2f, 0.5f, 33f, 97f, 82f, 242f, 0.7f, 1.9f, 4f, 5f, out speed, out acceleration);
            else if (diameterM <= 1f)
                InterpolateDriveScales(diameterM, 0.5f, 1f, 97f, 160f, 242f, 400f, 1.9f, 2.6f, 5f, 5f, out speed, out acceleration);
            else if (diameterM <= 3f)
                InterpolateDriveScales(diameterM, 1f, 3f, 160f, 340f, 400f, 850f, 2.6f, 6f, 5f, 30f, out speed, out acceleration);
            else if (diameterM <= 5f)
                InterpolateDriveScales(diameterM, 3f, 5f, 340f, 463f, 850f, 1157f, 6f, 6f, 30f, 50f, out speed, out acceleration);
            else if (diameterM <= 12f)
                InterpolateDriveScales(diameterM, 5f, 12f, 463f, 957f, 1157f, 2966f, 6f, 18f, 50f, 120f, out speed, out acceleration);
            else if (diameterM <= 30f)
                InterpolateDriveScales(diameterM, 12f, 30f, 957f, 1909f, 2966f, 5918f, 18f, 43f, 120f, 300f, out speed, out acceleration);
            else if (diameterM <= 60f)
                InterpolateDriveScales(diameterM, 30f, 60f, 1909f, 2799f, 5918f, 8677f, 43f, 60f, 300f, 500f, out speed, out acceleration);
            else if (diameterM <= 90f)
                InterpolateDriveScales(diameterM, 60f, 90f, 2799f, 4669f, 8677f, 14474f, 60f, 85f, 500f, 500f, out speed, out acceleration);
            else if (diameterM <= 340f)
                InterpolateDriveScales(diameterM, 90f, 340f, 4669f, 20000f, 14474f, 62000f, 85f, 172f, 500f, 500f, out speed, out acceleration);
            else
                InterpolateDriveScales(diameterM, 340f, 1000f, 20000f, 40280f, 62000f, 100000f, 172f, 302f, 500f, 500f, out speed, out acceleration);
        }

        private static void InterpolateDriveScales(float diameterM, float lowM, float highM,
            float speedFLow, float speedFHigh, float speedDLow, float speedDHigh,
            float accFLow, float accFHigh, float accDLow, float accDHigh,
            out float speed, out float acceleration)
        {
            float t = Mathf.Clamp01((diameterM - lowM) / (highM - lowM));
            speed = Mathf.Lerp(speedDLow, speedDHigh, t) / Mathf.Lerp(speedFLow, speedFHigh, t);
            acceleration = Mathf.Lerp(accDLow, accDHigh, t) / Mathf.Lerp(accFLow, accFHigh, t);
        }

        public static bool TryReset(float[] state)
        {
            if (state == null || state.Length < StateLength) return false;
            for (int i = 0; i < StateLength; i++) state[i] = 0f;
            state[LifeFrames] = LifeCapacityFrames;
            return true;
        }

        public static bool TryBeginCharge(float[] state)
        {
            if (!IsValid(state) || state[Charging] > 0f || state[DashKind] > 0f ||
                state[HuffFrames] > 0f || state[LifeFrames] <= 0f) return false;
            state[Charging] = 1f;
            state[HeldSeconds] = 0f;
            return true;
        }

        public static void Cancel(float[] state)
        {
            if (!IsValid(state)) return;
            EndCharge(state);
            EndDash(state);
        }

        public static int ReleaseCharge(float[] state, float diameterM)
        {
            if (!IsCharging(state)) return ReleaseNone;
            bool spinning = IsSpinning(state, diameterM);
            EndCharge(state);
            if (!spinning) return ReleaseBeforeSpin;
            BeginDash(state, HelpDash);
            return ReleaseHelpDash;
        }

        private static void EndCharge(float[] state)
        {
            state[Charging] = 0f;
            state[HeldSeconds] = 0f;
        }

        private static void BeginDash(float[] state, int kind)
        {
            EndCharge(state);
            state[DashKind] = kind;
            state[LaunchPending] = 1f;
            state[RecoverFrames] = 0f;
            state[FrameClock] = 0f;
        }

        private static void EndDash(float[] state)
        {
            if (state[DashKind] <= 0f) return;
            state[DashKind] = 0f;
            state[LaunchPending] = 0f;
            state[FrameClock] = 0f;
        }

        /// <summary>后拉先退出冲刺，再由普通运动施加反向刹车，不立即清掉惯性。</summary>
        public static bool TryBrake(float[] state, float forwardIntent)
        {
            if (!KatamariMath.IsFiniteFloat(forwardIntent) || forwardIntent >= 0f ||
                !IsDashing(state) || state[LaunchPending] > 0f) return false;
            EndDash(state);
            return true;
        }

        /// <summary>仅首个运动步注入出射速度；以后只滑行，不覆盖接触结果。</summary>
        public static bool TryTakeLaunch(float[] state, float headingDegrees, float baseSpeedMps,
            float speedScale, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!IsDashing(state) || state[LaunchPending] <= 0f ||
                !KatamariMath.IsFiniteFloat(headingDegrees)) return false;
            float speed = baseSpeedMps * LaunchSpeedScale(state, speedScale);
            if (!KatamariMath.IsFiniteFloat(speed) || speed <= 0f) return false;
            float radians = headingDegrees * Mathf.Deg2Rad;
            velocity = new Vector3(Mathf.Sin(radians) * speed, 0f, Mathf.Cos(radians) * speed);
            state[LaunchPending] = 0f;
            return true;
        }

        /// <summary>
        /// 在运动和接触之后推进一次。满蓄返回 autoLaunched，下一运动步才取出射速度。
        /// 速度跌落不结束冲刺；接触层仅在确认撞墙后传入 hitWall。
        /// </summary>
        public static bool TryStep(float[] state, float dt, float diameterM, bool hitWall,
            out bool autoLaunched)
        {
            autoLaunched = false;
            if (!IsValid(state) || !KatamariMath.IsFiniteFloat(dt) || dt < 0f) return false;
            if (dt == 0f || state[LaunchPending] > 0f) return true;

            float clock = state[FrameClock] + dt;
            if (!KatamariMath.IsFiniteFloat(clock)) return false;
            float frameCount = Mathf.Floor((clock + TimeTolerance) / OriginalTickSeconds);
            state[FrameClock] = Mathf.Max(0f, clock - frameCount * OriginalTickSeconds);

            if (state[HuffFrames] > 0f)
            {
                state[HuffFrames] = Mathf.Max(0f, state[HuffFrames] - frameCount);
                if (state[HuffFrames] <= 0f)
                {
                    state[LifeFrames] = LifeCapacityFrames;
                    state[FrameClock] = 0f;
                }
                return true;
            }

            if (state[DashKind] > 0f)
            {
                state[LifeFrames] = Mathf.Max(0f, state[LifeFrames] - frameCount);
                if (state[LifeFrames] <= 0f)
                {
                    EndDash(state);
                    state[HuffFrames] = HuffDurationFrames;
                }
                else if (hitWall)
                {
                    EndDash(state);
                }
                return true;
            }

            // 不冲刺也不喘气：蓄力和空转同样可按 WLK 的恢复节拍回寿命。
            float recovered = state[RecoverFrames] + frameCount;
            float increments = Mathf.Floor(recovered / RecoverAfterFrames);
            state[RecoverFrames] = recovered - increments * RecoverAfterFrames;
            state[LifeFrames] = Mathf.Min(LifeCapacityFrames, state[LifeFrames] + increments);

            if (state[Charging] > 0f)
            {
                state[HeldSeconds] += dt;
                if (state[HeldSeconds] + TimeTolerance >= ChargeSecondsFor(diameterM))
                {
                    BeginDash(state, FullDash);
                    autoLaunched = true;
                }
            }
            return true;
        }

        public static bool IsCharging(float[] state)
        {
            return IsValid(state) && state[Charging] > 0f;
        }

        public static bool IsSpinning(float[] state, float diameterM)
        {
            return IsCharging(state) && state[HeldSeconds] + TimeTolerance >= SpinSecondsFor(diameterM);
        }

        public static float ChargeSeconds(float[] state)
        {
            return IsCharging(state) ? state[HeldSeconds] : 0f;
        }

        public static bool IsDashing(float[] state)
        {
            return IsValid(state) && state[DashKind] > 0f;
        }

        public static float LaunchSpeedScale(float[] state, float fullScale)
        {
            return IsDashing(state) ? fullScale * (state[DashKind] == HelpDash ? HelpSpeedScale : 1f) : 1f;
        }

        // 撞墙判定的门槛（工程值）：入射法向速度至少 0.5 m/s 且占水平速度的 35% 以上（贴墙擦过不算），
        // 且接触把其中至少 60% 去掉（被挡住，而不是被导向）。
        public const float WallMinIntoSpeedMps = 0.5f;
        public const float WallMinIntoFraction = 0.35f;
        public const float WallMinRemovedFraction = 0.6f;
        // 法线与竖直方向夹角至少 80°：45–60° 的坡由坡道通道处理，低矮台阶的棱接触法线偏上，都不算墙。
        public const float WallMinAngleDeg = 80f;

        /// <summary>
        /// 本步一条接触是否算「冲刺撞墙」。接触深度不参与判断：求解后的记录已被推回表面，深度为 0
        /// （实测 cls=2 depth=0.0000）。obstacleHeightM 是障碍顶面高出球底的高度，
        /// 不高于球半径的台阶会被滚越，不算墙。alreadyClimbingThisFace 只能传「本步开始前」
        /// 就已在爬这个面——同一步里爬墙通道刚接管这个面，恰恰是撞上去的那一步。
        /// </summary>
        public static bool IsDashWallHit(Vector3 normal, float contactAngleDeg, float obstacleHeightM,
            float radiusM, Vector3 before, Vector3 after, bool alreadyClimbingThisFace)
        {
            if (alreadyClimbingThisFace) return false;
            if (!KatamariMath.IsFiniteVector(normal) || !KatamariMath.IsFiniteVector(before) ||
                !KatamariMath.IsFiniteVector(after) || !KatamariMath.IsFiniteFloat(contactAngleDeg) ||
                !KatamariMath.IsFiniteFloat(obstacleHeightM) || !KatamariMath.IsFiniteFloat(radiusM)) return false;
            if (contactAngleDeg < WallMinAngleDeg) return false;
            if (obstacleHeightM <= radiusM) return false;
            float horizontal = Mathf.Sqrt(normal.x * normal.x + normal.z * normal.z);
            if (horizontal <= 1e-4f) return false;
            float nx = normal.x / horizontal;
            float nz = normal.z / horizontal;
            float into = -(before.x * nx + before.z * nz);
            float speed = Mathf.Sqrt(before.x * before.x + before.z * before.z);
            if (into < WallMinIntoSpeedMps || into < WallMinIntoFraction * speed) return false;
            float after2 = -(after.x * nx + after.z * nz);
            return into - after2 >= WallMinRemovedFraction * into;
        }

        public static float EnergySeconds(float[] state)
        {
            return IsValid(state) ? state[LifeFrames] * OriginalTickSeconds : 0f;
        }

        public static bool IsHuffed(float[] state)
        {
            return IsValid(state) && state[HuffFrames] > 0f;
        }

        private static bool IsValid(float[] state)
        {
            if (state == null || state.Length < StateLength) return false;
            for (int i = 0; i < StateLength; i++)
            {
                if (!KatamariMath.IsFiniteFloat(state[i]) || state[i] < 0f) return false;
            }
            return true;
        }
    }
}
