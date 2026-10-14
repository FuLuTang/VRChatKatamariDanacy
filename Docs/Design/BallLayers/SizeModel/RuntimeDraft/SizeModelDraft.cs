// Unity 2022.3 / Udon-portable algorithm sketch; NOT compiled or attached to a scene.
// Inputs are confirmed volume from an external ledger; this class never owns or clamps it.
using UnityEngine;

namespace VrcKatamari.BallLayers.SizeModelDraft
{
    public struct CoreRadiusWorldM { public float value; }
    public struct CollisionRadiusWorldM { public float value; }
    public struct SemanticDiameter { public float value; }

    public struct SizeParametersDraft
    {
        public float baselineVolumeUnits;
        public float cubicMetersPerVolumeUnit;
        public float maxCoreRadiusWorldM;
        public float collisionToCoreRadiusRatio;
        public float semanticDiameterPerCoreDiameter;
    }

    public struct SizeOutputDraft
    {
        public CoreRadiusWorldM coreRadius;
        public CollisionRadiusWorldM collisionRadius;
        public SemanticDiameter semanticDiameter;
        public float effectivePresentationVolumeUnits;
        public bool valid;
    }

    public static class SizeModelDraft
    {
        private const float LnThreeOverFourPi = -1.4324119583f;
        private const float LnFourPiOverThree = 1.4324119583f;
        private const float LnFloatMax = 88.722839f;

        private static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        private static bool Positive(float v) { return Finite(v) && v > 0f; }

        // Sphere-equivalent radius via logarithms; avoids product/cube intermediate overflow.
        public static bool CoreRadiusFromVolume(float volumeUnits, float cubicScale,
            out CoreRadiusWorldM radius)
        {
            radius.value = 0f;
            if (!Positive(volumeUnits) || !Positive(cubicScale)) return false;
            float logRadius = (Mathf.Log(volumeUnits) + Mathf.Log(cubicScale) +
                LnThreeOverFourPi) / 3f;
            if (!Finite(logRadius) || logRadius > LnFloatMax) return false;
            float value = Mathf.Exp(logRadius);
            if (!Positive(value)) return false;
            radius.value = value;
            return true;
        }

        // Stable log(base + confirmed) without materializing a potentially overflowing sum.
        private static float LogSum(float a, float b)
        {
            if (b <= 0f) return Mathf.Log(a);
            float la = Mathf.Log(a);
            float lb = Mathf.Log(b);
            float hi = la > lb ? la : lb;
            float lo = la > lb ? lb : la;
            return hi + Mathf.Log(1f + Mathf.Exp(lo - hi));
        }

        // confirmedVolumeUnits is read-only input. Cap is solely the visible size mapping.
        public static SizeOutputDraft Derive(float confirmedVolumeUnits, SizeParametersDraft p)
        {
            SizeOutputDraft output = new SizeOutputDraft();
            if (!Finite(confirmedVolumeUnits) || confirmedVolumeUnits < 0f ||
                !Positive(p.baselineVolumeUnits) || !Positive(p.cubicMetersPerVolumeUnit) ||
                !Positive(p.maxCoreRadiusWorldM) || !Positive(p.collisionToCoreRadiusRatio) ||
                !Positive(p.semanticDiameterPerCoreDiameter)) return output;

            float logScale = Mathf.Log(p.cubicMetersPerVolumeUnit);
            float logBaselineR = (Mathf.Log(p.baselineVolumeUnits) + logScale +
                LnThreeOverFourPi) / 3f;
            float logCapR = Mathf.Log(p.maxCoreRadiusWorldM);
            if (logCapR < logBaselineR) return output;

            float logTotal = LogSum(p.baselineVolumeUnits, confirmedVolumeUnits);
            float logCapUnits = LnFourPiOverThree + 3f * logCapR - logScale;
            float logEffective = logTotal < logCapUnits ? logTotal : logCapUnits;
            float logRadius = (logEffective + logScale + LnThreeOverFourPi) / 3f;
            float core = logRadius >= logCapR ? p.maxCoreRadiusWorldM : Mathf.Exp(logRadius);
            float collision = core * p.collisionToCoreRadiusRatio;
            float semantic = (2f * core) * p.semanticDiameterPerCoreDiameter;
            if (!Positive(core) || !Positive(collision) || !Positive(semantic)) return output;

            float effective = logEffective >= LnFloatMax ? float.MaxValue : Mathf.Exp(logEffective);
            if (!Positive(effective)) return output;
            output.coreRadius.value = core;
            output.collisionRadius.value = collision;
            output.semanticDiameter.value = semantic;
            output.effectivePresentationVolumeUnits = effective;
            output.valid = true;
            return output;
        }

        public static bool CoreDiameter(CoreRadiusWorldM radius, out float diameter)
        {
            diameter = 0f;
            if (!Positive(radius.value) || radius.value > float.MaxValue * 0.5f) return false;
            diameter = 2f * radius.value;
            return Positive(diameter);
        }
    }
}
