using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Maps a caller-confirmed fixed-point volume to the three separate ball dimensions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Docs/OPEN_DECISIONS.md (r) steps C+D: volume is a fixed-point <c>long</c> in cubic
    /// millimetres (1 LSB = 1 mm^3 = 1e-9 m^3). The ball's TOTAL volume is
    /// <c>startVolumeMm3 + confirmedVolumeMm3</c>; the confirmed part is the ledger's
    /// growth (absorbed gains minus banked), and the start is the per-level start ball.
    /// The equivalent-sphere diameter is <c>(6V/pi)^(1/3)</c>; the original's HUD adds
    /// +0.02 cm to the DISPLAYED value only (parity G09), which
    /// <see cref="HudDiameterWorldM"/> does and the gate never reads.
    /// </para>
    /// <para>
    /// The project passes one global semantic diameter cap. A total volume at or above
    /// the cap's volume saturates exactly on the cap diameter without truncating the ledger.
    /// </para>
    /// <para>
    /// FLOAT32 HAZARD, deliberate and documented: Udon has no first-class double, so the
    /// cube root runs in float. Converting a <c>long</c> volume to float rounds the low bits
    /// above 2^24 mm^3 (0.0168 m^3); the relative error is at most 2^-24, i.e. ~2e-8 on the
    /// diameter (8 micrometres on a 400 m ball) from that conversion alone. This is
    /// sub-millimetre but may cross a boundary in a narrow band around an exact millimetre. The rule that follows: NEVER
    /// accumulate volume in float - only the ledger's integer sum is authoritative, and this
    /// class is a stateless read of it. The editor and Python compute growth in double and
    /// store an exact <c>long</c> in the host column, so the runtime never re-prices.
    /// </para>
    /// <para>
    /// The three dimensions stay distinct out values: <c>coreRadiusWorldM</c> (visual core,
    /// sphere-equivalent), <c>collisionRadiusWorldM</c> (the only collision quantity) and
    /// <c>semanticDiameter</c> (the narrative size the gate and the HUD use as a diameter).
    /// <c>semanticDiameterPerCoreDiameter</c> and <c>collisionToCoreRadiusRatio</c> keep
    /// their old meaning; both default to 1.
    /// </para>
    /// <para>
    /// The call allocates nothing, keeps no state, and on false every out value is zero.
    /// </para>
    /// </remarks>
    public static class SizeModelMath
    {
        /// <summary>One cubic metre in the ledger's quantum (1 LSB = 1 mm^3).</summary>
        public const long Mm3PerCubicMetre = 1000000000L;

        /// <summary>
        /// Cap saturation tolerance as a divisor of the cap volume: 1,000,000 = 1 ppm. See
        /// <see cref="TryDerive"/>; an integer divisor keeps the comparison in the ledger's
        /// own integer domain.
        /// </summary>
        public const long CapVolumeToleranceDivisor = 1000000L;

        /// <summary>The original's HUD fudge: +0.02 cm on the displayed diameter (G09).</summary>
        public const float HudDiameterOffsetM = 0.0002f;

        /// <summary>
        /// The equivalent-sphere diameter in metres for a TOTAL volume in mm^3.
        /// </summary>
        /// <remarks>
        /// Returns 0 for a non-positive volume. Callers that need a rejection use
        /// <see cref="TryDerive"/>; this helper exists for diagnostics and for the HUD, and
        /// it saturates at the float range rather than returning infinity.
        /// </remarks>
        public static float DiameterFromVolumeMm3(long volumeMm3)
        {
            if (volumeMm3 <= 0L) return 0f;
            float volumeM3 = (float)volumeMm3 * 1e-9f;
            float diameter = Mathf.Pow(6f * volumeM3 / Mathf.PI, 1f / 3f);
            return KatamariMath.IsFiniteFloat(diameter) ? diameter : 0f;
        }

        /// <summary>
        /// The TOTAL sphere volume of a ball with the given semantic diameter, in mm^3.
        /// </summary>
        /// <remarks>
        /// <c>V = pi/6 * (D / semanticDiameterPerCoreDiameter)^3</c>: the semantic diameter
        /// is the core diameter times the ratio, and the sphere is sized by the core. The
        /// cap is NOT applied here (this is the raw sphere); <see cref="TryDerive"/> clamps
        /// the total it is given to the cap, so a start/bank volume can be computed first and
        /// judged there.
        /// </remarks>
        public static bool TrySphereVolumeMm3(float semanticDiameterM,
            float semanticDiameterPerCoreDiameter, out long volumeMm3)
        {
            volumeMm3 = 0L;
            if (!KatamariMath.IsFiniteFloat(semanticDiameterM) || semanticDiameterM <= 0f)
                return false;
            if (!IsPositiveFinite(semanticDiameterPerCoreDiameter)) return false;
            float coreDiameterM = semanticDiameterM / semanticDiameterPerCoreDiameter;
            if (!IsPositiveFinite(coreDiameterM)) return false;

            float cubicMetres = Mathf.PI / 6f * coreDiameterM * coreDiameterM * coreDiameterM;
            if (!KatamariMath.IsFiniteFloat(cubicMetres) || cubicMetres <= 0f) return false;
            float mm3 = cubicMetres * (float)Mm3PerCubicMetre;
            // (float)long.MaxValue rounds UP to 2^63; equality is already out of range.
            if (!KatamariMath.IsFiniteFloat(mm3) || mm3 >= (float)long.MaxValue) return false;
            volumeMm3 = (long)Mathf.Round(mm3);
            return volumeMm3 > 0L;
        }

        /// <summary>
        /// The CONFIRMED volume (above the start ball) that produces a requested semantic
        /// diameter. This is the inverse the bank-shrink rule needs.
        /// </summary>
        public static bool TryConfirmedVolumeMm3ForSemanticDiameter(float semanticDiameterM,
            long startVolumeMm3, float semanticDiameterPerCoreDiameter,
            out long confirmedVolumeMm3)
        {
            confirmedVolumeMm3 = 0L;
            if (startVolumeMm3 <= 0L) return false;
            long totalMm3;
            if (!TrySphereVolumeMm3(semanticDiameterM, semanticDiameterPerCoreDiameter,
                out totalMm3))
            {
                return false;
            }
            confirmedVolumeMm3 = totalMm3 > startVolumeMm3 ? totalMm3 - startVolumeMm3 : 0L;
            return true;
        }

        /// <summary>
        /// Derives the presented dimensions from the ball's start and confirmed volumes.
        /// </summary>
        /// <param name="startVolumeMm3">The per-level start ball's volume (positive).</param>
        /// <param name="confirmedVolumeMm3">
        /// The ledger's growth above the start (>= 0; unbanked + carried).
        /// </param>
        /// <param name="maxBallDiameterM">The one global cap (semantic diameter, metres).</param>
        /// <param name="collisionToCoreRadiusRatio">Collision radius / core radius.</param>
        /// <param name="semanticDiameterPerCoreDiameter">Semantic diameter / core diameter.</param>
        /// <param name="coreRadiusWorldM">Visual core radius.</param>
        /// <param name="collisionRadiusWorldM">The only collision quantity.</param>
        /// <param name="semanticDiameter">Narrative diameter (gate + HUD input).</param>
        /// <param name="effectiveConfirmedMm3">
        /// The presentation diagnostic: the confirmed volume that fits under the cap.
        /// </param>
        public static bool TryDerive(long startVolumeMm3, long confirmedVolumeMm3,
            float maxBallDiameterM, float collisionToCoreRadiusRatio,
            float semanticDiameterPerCoreDiameter, out float coreRadiusWorldM,
            out float collisionRadiusWorldM, out float semanticDiameter,
            out long effectiveConfirmedMm3)
        {
            coreRadiusWorldM = 0f;
            collisionRadiusWorldM = 0f;
            semanticDiameter = 0f;
            effectiveConfirmedMm3 = 0L;

            if (startVolumeMm3 <= 0L) return false;
            if (confirmedVolumeMm3 < 0L) return false;
            if (!IsPositiveFinite(maxBallDiameterM)) return false;
            if (!IsPositiveFinite(collisionToCoreRadiusRatio)) return false;
            if (!IsPositiveFinite(semanticDiameterPerCoreDiameter)) return false;

            long capVolumeMm3;
            if (!TrySphereVolumeMm3(maxBallDiameterM, semanticDiameterPerCoreDiameter,
                out capVolumeMm3))
            {
                return false;
            }

            // Integer compare first: saturation is a property of the LEDGER quantity, not of
            // two floats that a rounding could order differently.
            long totalMm3 = startVolumeMm3 + confirmedVolumeMm3;
            if (totalMm3 < 0L) return false;                 // long overflow, refuse

            // The cap volume itself comes out of a float expression (pi/6 * d * d * d), whose
            // rounding depends on whether the runtime keeps float intermediates in double
            // (Unity's Mono) or rounds every step (.NET; Udon is not guaranteed to match
            // either). A strict `>=` against it
            // would make "exactly the cap" depend on that. The saturation threshold is
            // therefore the cap volume minus its own rounding budget (1 ppm, ~4x the worst
            // float chain error), in integer arithmetic. A total inside the budget is the
            // cap; the diameter that this swallows is about 0.13 mm at 400 m.
            long capRoundingBudgetMm3 = capVolumeMm3 / CapVolumeToleranceDivisor;
            bool atCap = totalMm3 >= capVolumeMm3 - capRoundingBudgetMm3;
            long effectiveTotalMm3 = atCap ? capVolumeMm3 : totalMm3;
            long effectiveConfirmed = effectiveTotalMm3 - startVolumeMm3;
            if (effectiveConfirmed < 0L) effectiveConfirmed = 0L;

            float measuredDiameter;
            if (atCap)
            {
                measuredDiameter = maxBallDiameterM;
            }
            else
            {
                measuredDiameter = DiameterFromVolumeMm3(effectiveTotalMm3);
                if (!IsPositiveFinite(measuredDiameter)) return false;
            }

            // The semantic diameter is the core diameter times the ratio; the core is
            // measured by the sphere, so D_core = D_semantic / ratio.
            float coreDiameter = measuredDiameter / semanticDiameterPerCoreDiameter;
            if (!IsPositiveFinite(coreDiameter)) return false;
            float core = coreDiameter * 0.5f;
            float collision = core * collisionToCoreRadiusRatio;
            float semantic = (2f * core) * semanticDiameterPerCoreDiameter;
            if (!IsPositiveFinite(core) || !IsPositiveFinite(collision) ||
                !IsPositiveFinite(semantic))
            {
                return false;
            }

            if (atCap)
            {
                // Exact saturation: report the cap's own numbers, not cbrt round-trips.
                semantic = maxBallDiameterM;
                core = (semantic * 0.5f) / semanticDiameterPerCoreDiameter;
                collision = core * collisionToCoreRadiusRatio;
                if (!IsPositiveFinite(core) || !IsPositiveFinite(collision)) return false;
            }

            coreRadiusWorldM = core;
            collisionRadiusWorldM = collision;
            semanticDiameter = semantic;
            effectiveConfirmedMm3 = effectiveConfirmed;
            return true;
        }

        /// <summary>The HUD diameter: the semantic diameter plus the original's +0.02 cm.</summary>
        public static float HudDiameterWorldM(float semanticDiameterWorldM)
        {
            if (!KatamariMath.IsFiniteFloat(semanticDiameterWorldM) ||
                semanticDiameterWorldM <= 0f)
            {
                return 0f;
            }
            return semanticDiameterWorldM + HudDiameterOffsetM;
        }

        private static bool IsPositiveFinite(float value)
        {
            return KatamariMath.IsFiniteFloat(value) && value > 0f;
        }
    }
}
