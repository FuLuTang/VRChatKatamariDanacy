using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>视角锚点与推进方向各自独立，全部使用世界坐标。</summary>
    public static class PushCueMath
    {
        public const float DefaultDeadZoneDegrees = 27f;
        public const float DefaultCatchUpSeconds = 0.4f;
        public const float DefaultDistanceMeters = 1.6f;
        public const float DefaultElevationDegrees = 12f;
        public const float DefaultAngularSizeDegrees = 4f;

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

        /// <summary>
        /// 距离由眼点到箭头中心量起；仰角相对世界水平。尺寸是 1 m 原型的均匀缩放，
        /// 按给定名义视角换算；水平旋转后的透视缩短不作补偿。
        /// </summary>
        public static bool TryEvaluate(Vector3 headPosition, float anchorYawDegrees,
            float pushYawDegrees, float distanceMeters, float elevationDegrees, float angularSizeDegrees,
            out Vector3 position, out Quaternion rotation, out float scale)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = 0f;
            if (!KatamariMath.IsFiniteVector(headPosition)
                || !KatamariMath.IsFiniteFloat(anchorYawDegrees)
                || !KatamariMath.IsFiniteFloat(pushYawDegrees)
                || !KatamariMath.IsFiniteFloat(distanceMeters) || distanceMeters <= 0f
                || !(elevationDegrees >= 0f && elevationDegrees < 90f)
                || !(angularSizeDegrees > 0f && angularSizeDegrees < 90f)) return false;

            float yaw = anchorYawDegrees * Mathf.Deg2Rad;
            float elevation = elevationDegrees * Mathf.Deg2Rad;
            float horizontal = distanceMeters * Mathf.Cos(elevation);
            Vector3 offset = new Vector3(horizontal * Mathf.Sin(yaw),
                distanceMeters * Mathf.Sin(elevation), horizontal * Mathf.Cos(yaw));
            Vector3 candidate = headPosition + offset;
            float length = distanceMeters * (2f * Mathf.Tan(0.5f * angularSizeDegrees * Mathf.Deg2Rad));
            if (!KatamariMath.IsFiniteVector(candidate) || !KatamariMath.IsFiniteFloat(length)) return false;

            position = candidate;
            float halfPushYaw = 0.5f * (pushYawDegrees % 360f) * Mathf.Deg2Rad;
            rotation = new Quaternion(0f, Mathf.Sin(halfPushYaw), 0f, Mathf.Cos(halfPushYaw));
            scale = length;
            return true;
        }
    }
}
