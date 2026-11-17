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
    /// 0.597 m / about 6.9 units, while applying the residual to volume units keeps about
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
        /// clamped to [0, 1]: 0 resets to the start diameter, 1 leaves
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
        /// Prices the post-shrink CONFIRMED volume (above the start ball) from the
        /// post-shrink diameter, in the ledger's mm^3 quantum.
        /// </summary>
        /// <remarks>
        /// The diameter rule and the ledger live in different spaces (a cube apart), so the
        /// caller computes the post diameter first and this is the one conversion into
        /// volume. Since steps C+D the quantum is mm^3 (Docs/OPEN_DECISIONS.md (r)); this
        /// delegates to the size model's inverse, so there is one conversion on the path.
        /// </remarks>
        public static bool TryBankVolumeMm3(float postBankDiameterM, long startVolumeMm3,
            float semanticDiameterPerCoreDiameter, out long postBankConfirmedMm3)
        {
            return SizeModelMath.TryConfirmedVolumeMm3ForSemanticDiameter(postBankDiameterM,
                startVolumeMm3, semanticDiameterPerCoreDiameter, out postBankConfirmedMm3);
        }

        /// <summary>
        /// Prices the carried residual the ball keeps after a conversion, in mm^3.
        /// </summary>
        /// <remarks>
        /// carried = max(0, postBankConfirmed - stillUnbanked): the part of the post-bank
        /// confirmed volume that the ledger did NOT move into the score. Integer subtraction
        /// only - the whole point of the fixed-point step is that this quantity never passes
        /// through a float. Returns false for a negative input; the caller keeps its previous
        /// size.
        /// </remarks>
        public static bool TryCarriedVolumeMm3(long postBankConfirmedMm3,
            long stillUnbankedMm3, out long carriedVolumeMm3)
        {
            carriedVolumeMm3 = 0L;
            if (postBankConfirmedMm3 < 0L) return false;
            if (stillUnbankedMm3 < 0L) return false;

            long carried = postBankConfirmedMm3 - stillUnbankedMm3;
            if (carried < 0L) carried = 0L;
            carriedVolumeMm3 = carried;
            return true;
        }
    }
}
