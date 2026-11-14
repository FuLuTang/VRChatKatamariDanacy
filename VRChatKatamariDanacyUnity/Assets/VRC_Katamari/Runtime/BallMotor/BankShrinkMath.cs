using VRCKatamari.BallLayers;
using VRCKatamari.Core;

namespace VRCKatamari.BallMotor
{
    /// <summary>
    /// The conversion (bank) shrink in the space the rule was decided in: the
    /// semantic diameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Docs/OPEN_DECISIONS.md (a) decides
    /// post = max(start, start + (pre - start) * residual) with residual = 0.25
    /// and a 0.55 m minimum conversion diameter. Both numbers are PROJECT
    /// values, not measured original-game values; the formula is applied as
    /// decided and is not re-derived here from another curve.
    /// </para>
    /// <para>
    /// Why this is a class rather than two lines in the behaviour: the map
    /// between a diameter and the volume units the ledger stores is a cube, not
    /// a proportion, so applying the residual to volume units leaves a
    /// different ball than applying it to the diameter. At the motor's default
    /// sizing (1 baseline unit, 0.0141 m^3 per unit, 2.20 m display cap) a
    /// 1.49 m ball is about 121.8 confirmed units; the diameter rule keeps
    /// 0.597 m / about 6.9 units, while the old volume-space rule kept about
    /// 31.2 units / 0.954 m. The arithmetic is written once, here, so it is
    /// testable without a UdonSharpBehaviour and stays in the UdonSharp
    /// language subset: static methods only, no state, no allocation.
    /// </para>
    /// <para>
    /// Every method is pure and refuses a non-finite input rather than
    /// clamping it: a caller that gets false must keep its previous size. The
    /// only clamp the rule itself asks for is the start floor; the size model's
    /// inverse additionally prices a diameter below its own baseline as zero
    /// confirmed units and a diameter above its cap as the cap's volume, which
    /// is that layer's documented domain behaviour, not a second rule.
    /// </para>
    /// </remarks>
    public static class BankShrinkMath
    {
        /// <summary>
        /// Applies the residual to the growth in DIAMETER space.
        /// </summary>
        /// <remarks>
        /// post = max(start, start + (pre - start) * residual), with residual
        /// clamped to [0, 1]: 0 reproduces the old reset-to-start rule, 1 leaves
        /// the ball unchanged. startDiameterM is the forward size map's own
        /// diameter at zero confirmed volume (the caller derives it; it is not a
        /// hardcoded 0.30). Returns false for a non-positive or non-finite
        /// start/pre diameter, a non-finite residual, or a non-finite result; no
        /// out value is written in that case.
        /// </remarks>
        public static bool TryPostBankDiameter(float startDiameterM,
            float preBankDiameterM, float residualFraction, out float postBankDiameterM)
        {
            postBankDiameterM = 0f;
            if (!KatamariMath.IsFiniteFloat(startDiameterM) || startDiameterM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(preBankDiameterM) || preBankDiameterM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(residualFraction)) return false;

            float residual = residualFraction;
            if (residual < 0f) residual = 0f;
            if (residual > 1f) residual = 1f;

            float post = startDiameterM + (preBankDiameterM - startDiameterM) * residual;
            if (!KatamariMath.IsFiniteFloat(post)) return false;
            if (post < startDiameterM) post = startDiameterM;
            postBankDiameterM = post;
            return true;
        }

        /// <summary>
        /// Prices a post-conversion diameter back into the confirmed volume
        /// units the size model consumes.
        /// </summary>
        /// <remarks>
        /// This is the size model's inverse, called by name at the one place the
        /// bank rule needs it, so the conversion cannot pick up a second
        /// volume-to-diameter convention. Returns false when the inverse fails
        /// (invalid diameter or parameters, or a cap radius below the baseline
        /// radius); the caller then refuses the conversion before the ledger is
        /// touched.
        /// </remarks>
        public static bool TryBankVolumeUnits(float postBankDiameterM,
            float baselineVolumeUnits, float cubicMetersPerVolumeUnit,
            float maxCoreRadiusWorldM, float semanticDiameterPerCoreDiameter,
            out float postBankVolumeUnits)
        {
            return SizeModelMath.TryVolumeUnitsForSemanticDiameter(postBankDiameterM,
                baselineVolumeUnits, cubicMetersPerVolumeUnit, maxCoreRadiusWorldM,
                semanticDiameterPerCoreDiameter, out postBankVolumeUnits);
        }

        /// <summary>
        /// Prices the carried residual the ball keeps after a conversion.
        /// </summary>
        /// <remarks>
        /// stillUnbankedVolumeUnits is the ledger's read-back of what remains
        /// bankable after the transaction (normally zero, because a bank moves
        /// the whole unbanked amount). carried = post - stillUnbanked, clamped
        /// to zero: a residual that prices below the still-unbanked amount is
        /// not a carried value at all. The result is already scored and must
        /// never be banked again. Returns false when the difference is not
        /// finite; the caller keeps its previous size.
        /// </remarks>
        public static bool TryCarriedVolumeUnits(float postBankVolumeUnits,
            float stillUnbankedVolumeUnits, out float carriedVolumeUnits)
        {
            carriedVolumeUnits = 0f;
            if (!KatamariMath.IsFiniteFloat(postBankVolumeUnits)) return false;
            if (!KatamariMath.IsFiniteFloat(stillUnbankedVolumeUnits)) return false;

            float carried = postBankVolumeUnits - stillUnbankedVolumeUnits;
            if (!KatamariMath.IsFiniteFloat(carried)) return false;
            if (carried < 0f) carried = 0f;
            carriedVolumeUnits = carried;
            return true;
        }
    }
}
