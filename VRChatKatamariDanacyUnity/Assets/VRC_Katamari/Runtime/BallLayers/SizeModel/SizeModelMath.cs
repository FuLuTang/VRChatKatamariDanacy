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
    /// The map also runs backwards. <see cref="TryVolumeUnitsForSemanticDiameter"/>
    /// recovers the confirmed volume units that produce a requested semantic
    /// diameter, which is what a rule written in diameter space needs: a
    /// banking residual applied to a diameter cannot be applied to the volume
    /// units that happen to be in flight, because the map between them is a
    /// cube, not a proportion. Both directions share the same constants and the
    /// same log-domain radius expression, so the two cannot drift apart: the
    /// forward map's numbers are not copied into the inverse, they are the
    /// parameters of one equation solved for its other unknown.
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

        // ln(2): the radius-to-diameter doubling the forward map applies as
        // "2f * core". The inverse needs it inside the log domain so a huge
        // semanticDiameterPerCoreDiameter cannot overflow an intermediate
        // 2*ratio product. It is the same factor, not a second convention.
        private const float LnTwo = 0.6931472f;

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
            float logBaselineRadius = LogCoreRadiusFromLogVolume(Mathf.Log(baselineVolumeUnits), logScale);
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
                float logCoreRadius = LogCoreRadiusFromLogVolume(logEffectiveVolume, logScale);
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

        /// <summary>
        /// Recovers the confirmed volume units whose forward mapping reports
        /// the requested semantic diameter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the inverse of <see cref="TryDerive"/> restricted to the
        /// quantity a diameter-space rule can name. It answers "how many units
        /// is this narrative diameter worth", which is how a rule that shrinks
        /// the ball by a fraction of a diameter (the banking residual) can
        /// express its result as a ledger value.
        /// </para>
        /// <para>
        /// Formula, and why it is exact rather than fitted. The forward map is
        /// semanticDiameter = 2 * ratio * cbrt(3 * (baseline + units) * c / 4*pi).
        /// Solving that equation for units is pure algebra - no series, no
        /// approximation, no second curve: coreRadius = semanticDiameter /
        /// (2 * ratio), total units = (4*pi/3) * coreRadius^3 / c, units = total
        /// - baseline. The cube and the sphere constant are not new numbers
        /// here: the same LnThreeOverFourPi / LnFourPiOverThree pair and the
        /// same LogCoreRadiusFromLogVolume expression feed both directions, so
        /// the two maps are one equation read in two directions. The only
        /// inexactness is float rounding, roughly one unit in the last place of
        /// the returned diameter (measured round trip over 0.30..2.20 m: worst
        /// 1.9e-7 m).
        /// </para>
        /// <para>
        /// Domain. The forward map cannot report a diameter below its baseline
        /// (the ball at zero confirmed units is already that big) or above its
        /// cap. Like the reference model's units_for_diameter, this method
        /// clamps instead of failing there: a diameter at or below the baseline
        /// returns zero confirmed units, a diameter above the cap returns the
        /// cap's volume. Clamping is the honest answer for a residual - a
        /// shrink that lands below the baseline means "no growth survived", not
        /// "the parameters are wrong" - while the caller's own start clamp keeps
        /// the range in practice. An unrepresentable volume (log total at or
        /// beyond the float range) is rejected rather than saturated, because
        /// no float input could then reproduce the diameter.
        /// </para>
        /// <para>
        /// semanticDiameter must be positive and finite, and the four
        /// parameters must be positive and finite with a cap radius at or above
        /// the baseline radius, exactly as in the forward map.
        /// collisionToCoreRadiusRatio is absent on purpose: the collision radius
        /// is a sibling of the core, not an input to the semantic diameter, so
        /// there is no value of it that changes this answer.
        /// </para>
        /// <para>
        /// On false the out value is zero and must not be applied. The call
        /// allocates nothing and keeps no state; it is a pure function of its
        /// arguments, so re-deriving a size after a bank, a reset or a
        /// post-collapse rebuild is simply another call.
        /// </para>
        /// </remarks>
        public static bool TryVolumeUnitsForSemanticDiameter(
            float semanticDiameter,
            float baselineVolumeUnits,
            float cubicMetersPerVolumeUnit,
            float maxCoreRadiusWorldM,
            float semanticDiameterPerCoreDiameter,
            out float confirmedVolumeUnits)
        {
            confirmedVolumeUnits = 0f;

            if (!IsPositiveFinite(semanticDiameter)) return false;
            if (!IsPositiveFinite(baselineVolumeUnits)) return false;
            if (!IsPositiveFinite(cubicMetersPerVolumeUnit)) return false;
            if (!IsPositiveFinite(maxCoreRadiusWorldM)) return false;
            if (!IsPositiveFinite(semanticDiameterPerCoreDiameter)) return false;

            float logScale = Mathf.Log(cubicMetersPerVolumeUnit);
            if (!KatamariMath.IsFiniteFloat(logScale)) return false;

            // The same two radius bounds the forward map compares, built by the
            // same helper, so an inverse answer can never sit outside the range
            // the forward map can actually produce.
            float logBaselineRadius = LogCoreRadiusFromLogVolume(Mathf.Log(baselineVolumeUnits), logScale);
            float logCapRadius = Mathf.Log(maxCoreRadiusWorldM);
            if (!KatamariMath.IsFiniteFloat(logBaselineRadius)) return false;
            if (!KatamariMath.IsFiniteFloat(logCapRadius)) return false;
            if (logCapRadius < logBaselineRadius) return false;

            // Invert "semantic = 2 * core * ratio" without materializing
            // 2 * ratio: a huge ratio would otherwise divide by infinity and
            // quietly answer zero.
            float logCoreRadius = Mathf.Log(semanticDiameter) -
                Mathf.Log(semanticDiameterPerCoreDiameter) - LnTwo;
            if (!KatamariMath.IsFiniteFloat(logCoreRadius)) return false;

            // Below the baseline the forward map cannot go smaller: zero
            // confirmed units is the whole of that branch, and 0f is a valid
            // answer rather than a failure.
            if (logCoreRadius <= logBaselineRadius) return true;
            if (logCoreRadius > logCapRadius) logCoreRadius = logCapRadius;

            // Total units, not confirmed units: the volume the whole mapping
            // reads, before the baseline is subtracted below.
            float logTotalUnits = LnFourPiOverThree + 3f * logCoreRadius - logScale;
            if (!KatamariMath.IsFiniteFloat(logTotalUnits)) return false;
            if (logTotalUnits >= LnFloatMax) return false;
            float totalUnits = Mathf.Exp(logTotalUnits);
            if (!IsPositiveFinite(totalUnits)) return false;

            float confirmed = totalUnits - baselineVolumeUnits;
            if (!KatamariMath.IsFiniteFloat(confirmed)) return false;
            if (confirmed < 0f) confirmed = 0f;
            confirmedVolumeUnits = confirmed;
            return true;
        }

        private static bool IsPositiveFinite(float value)
        {
            return KatamariMath.IsFiniteFloat(value) && value > 0f;
        }

        // log of the core radius a log-volume in volume units maps to under
        // V = 4*pi*r^3/3 with a metres-per-unit scale. One expression, used by
        // the forward map for the baseline and for the mapped core, and by the
        // inverse for the bounds it must stay between, so the two directions
        // cannot disagree about where a volume sits.
        private static float LogCoreRadiusFromLogVolume(float logVolumeUnits, float logScale)
        {
            return (logVolumeUnits + logScale + LnThreeOverFourPi) / 3f;
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
