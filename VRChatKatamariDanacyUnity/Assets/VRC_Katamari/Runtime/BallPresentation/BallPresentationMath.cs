using VRCKatamari.Core;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Presentation-only math for the core mesh: how large the drawn core looks from one eye.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function so EditMode tests can pin it without a scene. BallView draws the core
    /// at twice the motor's current collision radius, with no smoothing. Nothing here feeds
    /// a size back into the motor, the absorb gate, the ledger or the follow-rig camera.
    /// </para>
    /// </remarks>
    public static class BallPresentationMath
    {
        // An eye closer than this to the ball centre has no usable view of it.
        private const float MinEyeDistanceM = 0.0001f;

        /// <summary>
        /// How large the drawn core looks from one eye: core diameter divided by the distance
        /// from that eye to the ball centre.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Radians for a small angle, i.e. the same number on any eye with the same distance.
        /// This is the quantity the "it did not grow" complaint is really about: the ledger's
        /// relative growth is multiplied by the follow rig's pull-back, and only this number
        /// shows the product.
        /// </para>
        /// <para>
        /// False for a non-finite or non-positive core diameter, and for an eye distance at or
        /// below <see cref="MinEyeDistanceM"/> - a degenerate eye position is a missing
        /// measurement, not an infinite ball. The behaviour reports <c>app=none</c> for it.
        /// </para>
        /// </remarks>
        public static bool TryApparentSize(float coreDiameterM, float eyeDistanceM,
            out float apparent)
        {
            apparent = 0f;
            if (!KatamariMath.IsFiniteFloat(coreDiameterM) || coreDiameterM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(eyeDistanceM) || eyeDistanceM <= MinEyeDistanceM)
                return false;
            float value = coreDiameterM / eyeDistanceM;
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            apparent = value;
            return true;
        }
    }
}
