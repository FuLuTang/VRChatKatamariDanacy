using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Maps a caller-confirmed volume to the three separate ball dimensions,
    /// with the presentation cap applied only to this mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lengths are metres in the project's single world space (+Y up); volumes
    /// are in the caller's volume units and cubicMetersPerVolumeUnit converts
    /// them to cubic metres. Every parameter is supplied by the caller: this
    /// class has no product defaults and does not decide the world scale.
    /// </para>
    /// <para>
    /// The three dimensions are distinct out values on purpose, because one
    /// field must never carry both a radius and a diameter, and the quantities
    /// are not interchangeable:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// coreRadiusWorldM is the visual core radius: it scales CoreMesh and
    /// satisfies the sphere-equivalent relation V = 4*pi*r^3/3.
    /// </description></item>
    /// <item><description>
    /// collisionRadiusWorldM is the only collision quantity: the simplified
    /// PVP and environment test radius.
    /// </description></item>
    /// <item><description>
    /// semanticDiameter is the narrative size: UI readouts and the size-tier
    /// comparison the player sees. It is a diameter, it may differ from the
    /// physical metres, and it is never an input to collision or to a gameplay
    /// gate; the pickup threshold follows the confirmed volume, not this value.
    /// </description></item>
    /// </list>
    /// <para>
    /// The cap is display policy only. The confirmed volume is read by value,
    /// so there is no ledger this class could truncate, zero or rewrite; the
    /// effective presentation volume returned here is a finite diagnostic and
    /// never a balance. Because the call is stateless, a reset or a
    /// post-collapse rebuild is simply another call with the new confirmed
    /// value: nothing remembers a previous display size.
    /// </para>
    /// <para>
    /// The mapping runs in the log domain, so a finite volume and a finite
    /// scale never overflow through the intermediate product, cube or sum.
    /// The presentation diagnostic saturates at float.MaxValue once its log
    /// passes the float range; the three dimensions do not saturate - their
    /// final multiplications are checked one by one and an unrepresentable
    /// result is rejected. This port uses the runtime's float (not the
    /// reference model's double), so the accepted input domain is every finite
    /// non-negative float and the cap saturates exactly. A cap radius below the
    /// baseline radius is a parameter conflict and is rejected rather than
    /// silently shrinking the baseline.
    /// </para>
    /// <para>
    /// The call allocates nothing and keeps no state. On false every out value
    /// is zero and must not be applied.
    /// </para>
    /// </remarks>
    public static class SizeModelMath
    {
        // ln(3/(4*pi)) and ln(4*pi/3): the sphere-volume constant enters the
        // radius mapping as a log offset, so the product/cube never materialize.
        private const float LnThreeOverFourPi = -1.4324119583f;
        private const float LnFourPiOverThree = 1.4324119583f;

        // ln(float.MaxValue): a log-domain value at or above this would
        // exponentiate to infinity, so it saturates the diagnostic instead.
        private const float LnFloatMax = 88.722839f;

        /// <summary>
        /// Derives the presented dimensions from a confirmed volume.
        /// </summary>
        /// <remarks>
        /// confirmedVolumeUnits is the caller's read-only ledger reading and
        /// must be non-negative and finite; the five parameters must be
        /// positive and finite. baselineVolumeUnits is the volume the ball has
        /// before any confirmed growth, cubicMetersPerVolumeUnit converts one
        /// volume unit to cubic metres, maxCoreRadiusWorldM caps only this
        /// mapping, and the two ratios derive the collision radius and the
        /// semantic diameter from the core. All lengths are metres, radius and
        /// diameter are never the same quantity, and effectiveVolumeUnits is
        /// the presentation diagnostic in volume units (not cubic metres, not
        /// a balance). Returns false for invalid input, a cap below the
        /// baseline radius, or a non-finite result; no partial output is
        /// written in that case.
        /// </remarks>
        public static bool TryDerive(
            float confirmedVolumeUnits,
            float baselineVolumeUnits,
            float cubicMetersPerVolumeUnit,
            float maxCoreRadiusWorldM,
            float collisionToCoreRadiusRatio,
            float semanticDiameterPerCoreDiameter,
            out float coreRadiusWorldM,
            out float collisionRadiusWorldM,
            out float semanticDiameter,
            out float effectiveVolumeUnits)
        {
            coreRadiusWorldM = 0f;
            collisionRadiusWorldM = 0f;
            semanticDiameter = 0f;
            effectiveVolumeUnits = 0f;

            if (!KatamariMath.IsFiniteFloat(confirmedVolumeUnits) || confirmedVolumeUnits < 0f) return false;
            if (!IsPositiveFinite(baselineVolumeUnits)) return false;
            if (!IsPositiveFinite(cubicMetersPerVolumeUnit)) return false;
            if (!IsPositiveFinite(maxCoreRadiusWorldM)) return false;
            if (!IsPositiveFinite(collisionToCoreRadiusRatio)) return false;
            if (!IsPositiveFinite(semanticDiameterPerCoreDiameter)) return false;

            float logScale = Mathf.Log(cubicMetersPerVolumeUnit);
            if (!KatamariMath.IsFiniteFloat(logScale)) return false;

            // Baseline and cap are compared in the log domain for the same
            // reason the mapping runs there: no baseline-volume cube.
            float logBaselineRadius = (Mathf.Log(baselineVolumeUnits) + logScale +
                LnThreeOverFourPi) / 3f;
            float logCapRadius = Mathf.Log(maxCoreRadiusWorldM);
            if (!KatamariMath.IsFiniteFloat(logBaselineRadius)) return false;
            if (!KatamariMath.IsFiniteFloat(logCapRadius)) return false;
            if (logCapRadius < logBaselineRadius) return false;

            // log(baseline + confirmed) without materializing a sum that can
            // overflow, although both addends are finite.
            float logTotal = AddLogs(baselineVolumeUnits, confirmedVolumeUnits);
            if (!KatamariMath.IsFiniteFloat(logTotal)) return false;

            // The cap expressed as a volume: the effective volume is the
            // smaller of the confirmed total and the volume the cap can hold.
            float logCappedVolume = LnFourPiOverThree + 3f * logCapRadius - logScale;
            if (!KatamariMath.IsFiniteFloat(logCappedVolume)) return false;
            float logEffectiveVolume = Mathf.Min(logTotal, logCappedVolume);
            if (!KatamariMath.IsFiniteFloat(logEffectiveVolume)) return false;

            float effective = logEffectiveVolume >= LnFloatMax
                ? float.MaxValue
                : Mathf.Exp(logEffectiveVolume);
            if (!IsPositiveFinite(effective)) return false;

            // Saturation is exact: once the confirmed total reaches the cap
            // volume the core radius is the cap parameter itself, so two
            // different large confirmed values cannot produce two display
            // sizes that differ by float rounding.
            float core;
            if (logTotal > logCappedVolume)
            {
                core = maxCoreRadiusWorldM;
            }
            else
            {
                float logCoreRadius = (logEffectiveVolume + logScale + LnThreeOverFourPi) / 3f;
                if (!KatamariMath.IsFiniteFloat(logCoreRadius)) return false;
                core = Mathf.Exp(logCoreRadius);
                if (!IsPositiveFinite(core)) return false;
            }

            // Final per-quantity checks: a finite core can still overflow once
            // the ratio is applied, and that must be rejected, not clamped.
            float collision = core * collisionToCoreRadiusRatio;
            float semantic = (2f * core) * semanticDiameterPerCoreDiameter;
            if (!IsPositiveFinite(collision)) return false;
            if (!IsPositiveFinite(semantic)) return false;

            coreRadiusWorldM = core;
            collisionRadiusWorldM = collision;
            semanticDiameter = semantic;
            effectiveVolumeUnits = effective;
            return true;
        }

        /// <summary>
        /// Converts a core radius to the core diameter in metres.
        /// </summary>
        /// <remarks>
        /// This is the module's only radius-to-diameter conversion, so a
        /// readout asks for a diameter by name instead of doubling whichever
        /// quantity it happens to hold. The input must be the positive finite
        /// core radius; a collision radius or a semantic diameter is not a
        /// core radius and has no way to be passed here by type, which is why
        /// the caller must keep the out values apart. False means the input is
        /// not a positive finite radius or the doubled value is not finite.
        /// </remarks>
        public static bool TryCoreDiameter(float coreRadiusWorldM, out float coreDiameterWorldM)
        {
            coreDiameterWorldM = 0f;
            if (!IsPositiveFinite(coreRadiusWorldM)) return false;
            float diameter = coreRadiusWorldM * 2f;
            if (!IsPositiveFinite(diameter)) return false;
            coreDiameterWorldM = diameter;
            return true;
        }

        private static bool IsPositiveFinite(float value)
        {
            return KatamariMath.IsFiniteFloat(value) && value > 0f;
        }

        // log(a + b) with a stable log-sum: the larger log leads, so the
        // exponential only ever sees a non-positive exponent. A zero second
        // addend is the baseline-only case and never reaches log(0).
        private static float AddLogs(float a, float b)
        {
            float logA = Mathf.Log(a);
            if (b <= 0f) return logA;
            float logB = Mathf.Log(b);
            float high = Mathf.Max(logA, logB);
            float low = Mathf.Min(logA, logB);
            return high + Mathf.Log(1f + Mathf.Exp(low - high));
        }
    }
}
