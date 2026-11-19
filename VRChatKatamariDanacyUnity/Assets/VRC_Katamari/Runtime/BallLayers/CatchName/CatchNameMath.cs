using UnityEngine;
using VRCKatamari.Core;
using VRCKatamari.Hub;
using VRCKatamari.Riding;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// 「刚吸到的东西」提示的纯规则：逐字计时、名字停留、同帧替换优先级、语言选择、
    /// 小模型缩放与自转，以及面板跟随视角的位置。
    /// </summary>
    /// <remarks>
    /// 计时取自 WLK REROLL+（v20230608）的 UIMonoNameDisplay.Start：_NEXT_CHR_WAIT = 2 帧/字，
    /// _DISP_WAIT = 105 帧，_CHANGE_CHR_ = 5（≤5 字与更长名字两套版式）；UILastCatchMonoDisplay
    /// 的 ROT_PER_SEC = -60。帧换秒按 30 帧/秒（计时乘 FpsScale，基准帧率是推断，未验证）。
    /// </remarks>
    public static class CatchNameMath
    {
        public const float FramesPerSecond = 30f;
        public const float CharSeconds = 2f / FramesPerSecond;
        public const float HoldSeconds = 105f / FramesPerSecond;
        public const int ShortNameMaxChars = 5;
        public const float SpinDegreesPerSecond = -60f;

        // 跟随视角 HUD：视线下方偏左（Docs/Design/BallLayers/CatchName/CATCH_NAME_HUD.md）。待 VR 实测。
        public const float DefaultDistanceMeters = 1.4f;
        public const float DefaultElevationDegrees = -15f;
        public const float DefaultYawOffsetDegrees = -12f;
        public const float DefaultAngularSizeDegrees = 7f;
        public const float DefaultDeadZoneDegrees = 27f;
        public const float DefaultCatchUpSeconds = 0.4f;

        private const float SettleDegrees = 1f;
        private const float MinFlatForwardSquared = 0.0001f;

        /// <summary>从头部前向的水平投影取偏航；近乎垂直时沿用上次有效方向。</summary>
        public static float HeadYaw(Quaternion headRotation, float previousYawDegrees)
        {
            Vector3 headForward = headRotation * Vector3.forward;
            float flatSquared = headForward.x * headForward.x + headForward.z * headForward.z;
            if (flatSquared < MinFlatForwardSquared) return previousYawDegrees;
            return Mathf.Atan2(headForward.x, headForward.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 死区只决定何时开始追随；已在追随时继续向中心靠拢，不在死区边缘停住。
        /// 目标不动时，经过 catchUpSeconds 约消去 95% 的角差（3 个时间常数）。
        /// </summary>
        public static float StepAnchorYaw(float anchorYawDegrees, float headYawDegrees,
            float deadZoneDegrees, float catchUpSeconds, float deltaSeconds, bool wasFollowing,
            out bool following)
        {
            float delta = Mathf.DeltaAngle(anchorYawDegrees, headYawDegrees);
            following = wasFollowing || Mathf.Abs(delta) > deadZoneDegrees;
            if (!following || deltaSeconds <= 0f) return anchorYawDegrees;

            float remaining = delta * Mathf.Exp(-3f * deltaSeconds / catchUpSeconds);
            if (Mathf.Abs(remaining) <= SettleDegrees)
            {
                following = false;
                return headYawDegrees;
            }
            return headYawDegrees - remaining;
        }

        /// <summary>按眼点、偏航、世界仰角和名义视角计算圆底中心及直径。</summary>
        public static bool TryEvaluateHudPlacement(Vector3 headPosition, float yawDegrees,
            float distanceMeters, float elevationDegrees, float angularSizeDegrees,
            out Vector3 position, out float diameter)
        {
            position = Vector3.zero;
            diameter = 0f;
            if (!KatamariMath.IsFiniteVector(headPosition)
                || !KatamariMath.IsFiniteFloat(yawDegrees)
                || !KatamariMath.IsFiniteFloat(distanceMeters) || distanceMeters <= 0f
                || !(elevationDegrees > -90f && elevationDegrees < 90f)
                || !(angularSizeDegrees > 0f && angularSizeDegrees < 90f)) return false;

            float yaw = yawDegrees * Mathf.Deg2Rad;
            float elevation = elevationDegrees * Mathf.Deg2Rad;
            float horizontal = distanceMeters * Mathf.Cos(elevation);
            Vector3 offset = new Vector3(horizontal * Mathf.Sin(yaw),
                distanceMeters * Mathf.Sin(elevation), horizontal * Mathf.Cos(yaw));
            Vector3 candidate = headPosition + offset;
            float size = distanceMeters * (2f * Mathf.Tan(0.5f * angularSizeDegrees * Mathf.Deg2Rad));
            if (!KatamariMath.IsFiniteVector(candidate) || !KatamariMath.IsFiniteFloat(size)) return false;

            position = candidate;
            diameter = size;
            return true;
        }

        /// <summary>原型里模型占圆底直径的比例（圆底直径 = 1）。</summary>
        public const float DefaultModelFitDiameter = 0.75f;

        /// <summary>吸到瞬间从物件拉到圆底的线，原作约 0.1–0.3 s 消失。</summary>
        public const float LineSeconds = 0.3f;

        /// <summary>线的起点从物件向圆底收拢的比例（0 = 刚吸到，1 = 已收到圆底）；之后不再画线。</summary>
        public static float LineProgress(float elapsedSeconds)
        {
            if (!(elapsedSeconds >= 0f)) return 1f;
            if (elapsedSeconds >= LineSeconds) return 1f;
            return elapsedSeconds / LineSeconds;
        }

        /// <summary>
        /// 面板朝向：先绕世界竖轴转 yaw，再按仰角俯仰，使面板正对眼点（+Z 背离观看者）。
        /// 视线下方（仰角为负）时面板上缘向观看者倾。托管乘法，NUnit 可在本地跑。
        /// </summary>
        public static Quaternion FacingRotation(float yawDegrees, float elevationDegrees)
        {
            float halfYaw = 0.5f * (yawDegrees % 360f) * Mathf.Deg2Rad;
            float halfPitch = -0.5f * elevationDegrees * Mathf.Deg2Rad;
            return new Quaternion(0f, Mathf.Sin(halfYaw), 0f, Mathf.Cos(halfYaw))
                * new Quaternion(Mathf.Sin(halfPitch), 0f, 0f, Mathf.Cos(halfPitch));
        }

        /// <summary>
        /// 眼点到面板的距离随骑手眼高缩放（与骑乘面板同一规则 <see cref="RideViewMath.PanelScaleForEyeHeight"/>）：
        /// 视角大小不变，人随球缩小时面板也不会落到地面以下被遮住。
        /// </summary>
        public static float ScaledDistance(float distanceMeters, float eyeHeightMeters)
        {
            return distanceMeters * RideViewMath.PanelScaleForEyeHeight(eyeHeightMeters);
        }

        /// <summary>吸到后经过 elapsed 秒应显示的字数：第一个字立即出现，之后每 CharSeconds 多一个。</summary>
        public static int VisibleCharacters(float elapsedSeconds, int length)
        {
            if (length <= 0 || !(elapsedSeconds >= 0f)) return 0;
            float steps = elapsedSeconds / CharSeconds;
            if (steps >= length) return length;
            int shown = (int)steps + 1;
            return shown > length ? length : shown;
        }

        /// <summary>名字打完后再停留 HoldSeconds；小模型不受此限，一直保留到下一件。</summary>
        public static bool IsNameShown(float elapsedSeconds, int length)
        {
            if (length <= 0 || !(elapsedSeconds >= 0f)) return false;
            return elapsedSeconds < length * CharSeconds + HoldSeconds;
        }

        /// <summary>≤5 字用大号版式（原作 Under5Text），更长的用小号（Over5Text）。</summary>
        public static bool UsesShortLayout(int length)
        {
            return length <= ShortNameMaxChars;
        }

        /// <summary>小模型绕竖轴的角度（度，0..360）。</summary>
        public static float SpinDegrees(float elapsedSeconds)
        {
            if (!KatamariMath.IsFiniteFloat(elapsedSeconds)) return 0f;
            float angle = (SpinDegreesPerSecond * elapsedSeconds) % 360f;
            if (angle < 0f) angle += 360f;
            return angle;
        }

        /// <summary>
        /// 实际画出的自转角：低特效设置开启时小模型不转（停在 0°，正面朝向原型 -Z），
        /// 否则同 <see cref="SpinDegrees"/>。
        /// </summary>
        public static float ShownSpinDegrees(float elapsedSeconds, bool lowEffects)
        {
            return lowEffects ? 0f : SpinDegrees(elapsedSeconds);
        }

        /// <summary>
        /// 新吸到的一件是否顶掉当前显示。不同帧一律换成最新的；同一帧里按显示优先级
        /// （原作 u8WndDispPrio），优先级相同时取后到的。
        /// </summary>
        public static bool Replaces(bool sameFrame, int currentPriority, int newPriority)
        {
            return !sameFrame || newPriority >= currentPriority;
        }

        /// <summary>按语言取名字；缺失时依次退回英文、中文、日文。</summary>
        public static string PickName(int language, string zh, string en, string ja)
        {
            string preferred = language == HubSettingsMath.LangZh ? zh
                : language == HubSettingsMath.LangJa ? ja : en;
            if (!string.IsNullOrEmpty(preferred)) return preferred;
            if (!string.IsNullOrEmpty(en)) return en;
            if (!string.IsNullOrEmpty(zh)) return zh;
            if (!string.IsNullOrEmpty(ja)) return ja;
            return "";
        }

        /// <summary>半边长为 extents 的盒子旋转 rotation 后，其轴对齐外包盒的半边长。</summary>
        public static Vector3 RotatedHalfExtents(Quaternion rotation, Vector3 extents)
        {
            Vector3 ax = rotation * Vector3.right;
            Vector3 ay = rotation * Vector3.up;
            Vector3 az = rotation * Vector3.forward;
            return new Vector3(
                Mathf.Abs(ax.x) * extents.x + Mathf.Abs(ay.x) * extents.y + Mathf.Abs(az.x) * extents.z,
                Mathf.Abs(ax.y) * extents.x + Mathf.Abs(ay.y) * extents.y + Mathf.Abs(az.y) * extents.z,
                Mathf.Abs(ax.z) * extents.x + Mathf.Abs(ay.z) * extents.y + Mathf.Abs(az.z) * extents.z);
        }

        /// <summary>把物件自身坐标下的包围盒缩到直径 fitDiameter 的圆里；最长边为 0 或非有限时返回 0。</summary>
        public static float FitScale(Vector3 min, Vector3 max, float fitDiameter)
        {
            float x = max.x - min.x;
            float y = max.y - min.y;
            float z = max.z - min.z;
            // 每一轴都要有限：比较里的 NaN 恒为 false，只查最长边会把坏轴漏过去。
            if (!KatamariMath.IsFiniteFloat(x) || !KatamariMath.IsFiniteFloat(y)
                || !KatamariMath.IsFiniteFloat(z)) return 0f;
            float edge = x > y ? x : y;
            if (z > edge) edge = z;
            if (!KatamariMath.IsFiniteFloat(edge) || !(edge > 0f)
                || !KatamariMath.IsFiniteFloat(fitDiameter) || !(fitDiameter > 0f)) return 0f;
            return fitDiameter / edge;
        }
    }
}
