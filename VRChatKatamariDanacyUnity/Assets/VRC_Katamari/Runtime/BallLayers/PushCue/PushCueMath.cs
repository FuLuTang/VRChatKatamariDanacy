using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>在球心前上方放置水平箭头；偏移与长度均按语义半径计算。</summary>
    public static class PushCueMath
    {
        // 工程起点（非原作值）；真机可试：高度 1.2–1.5 r、前移 0.5–0.8 r、长度 0.8–1.0 r。
        // 前移宜大于半长，使箭尾也位于球顶前方，避开推球站姿看向球顶的视线。
        private const float HeightPerRadius = 1.25f;
        private const float ForwardPerRadius = 0.6f;
        private const float LengthPerRadius = 0.9f;

        /// <summary>输出世界位姿与 1 m 长、尖端沿 +Z 的原型的均匀缩放。</summary>
        public static bool TryEvaluate(Vector3 ballCenterWorld, float semanticRadiusMeters, float pushYawDegrees,
            out Vector3 position, out Quaternion rotation, out float scale)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = 0f;
            if (!KatamariMath.IsFiniteVector(ballCenterWorld)
                || !KatamariMath.IsFiniteFloat(semanticRadiusMeters) || semanticRadiusMeters <= 0f
                || !KatamariMath.IsFiniteFloat(pushYawDegrees)) return false;

            float halfYaw = 0.5f * (pushYawDegrees % 360f) * Mathf.Deg2Rad;
            Quaternion heading = new Quaternion(0f, Mathf.Sin(halfYaw), 0f, Mathf.Cos(halfYaw));
            Vector3 candidate = ballCenterWorld + Vector3.up * (HeightPerRadius * semanticRadiusMeters)
                + (heading * Vector3.forward) * (ForwardPerRadius * semanticRadiusMeters);
            float length = LengthPerRadius * semanticRadiusMeters;
            if (!KatamariMath.IsFiniteVector(candidate) || length <= 0f) return false;

            position = candidate;
            rotation = heading;
            scale = length;
            return true;
        }
    }
}
