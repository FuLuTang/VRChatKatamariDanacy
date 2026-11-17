using NUnit.Framework;
using VRCKatamari.BallPresentation;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the presentation math that is left: the apparent-size helper the <c>[eye]</c>
    /// diagnostics line uses. The scene probe that reports it is pinned separately, in
    /// BallGrowthDiagnosticsTests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core mesh diameter itself is no longer computed here. The core does not grow: it
    /// is the level's start diameter, reported by the motor (<c>GetStartDiameterWorldM</c>,
    /// pinned in ContactModelV2Tests) and written once by <c>BallView</c>. The 0.95 factor,
    /// the target-diameter helper and the catch-up spring that drew a growing core were
    /// deleted with that decision (Docs/Design/BallLayers/Attachment/ATTACH_PLACEMENT.md 3.2).
    /// </para>
    /// <para>
    /// The apparent-size case is pinned against a sample taken from a full round, so a
    /// printed line can be checked against it by hand.
    /// </para>
    /// </remarks>
    public class BallPresentationTests
    {
        // ---------------------------------------------------------------- log quantities
        /// <summary>
        /// The apparent size the <c>[eye]</c> line prints is the core diameter over the eye
        /// distance. The first case is a sample from a full round (0.7863 m core at
        /// 2.297 m reads 0.3423 rad), so the printed line can be checked by hand.
        /// </summary>
        [Test]
        public void ApparentSizeIsCoreDiameterOverEyeDistance()
        {
            float apparent;
            Assert.IsTrue(BallPresentationMath.TryApparentSize(0.7863f, 2.297f, out apparent));
            Assert.AreEqual(0.3423f, apparent, 5e-5f);

            Assert.IsTrue(BallPresentationMath.TryApparentSize(1f, 2f, out apparent));
            Assert.AreEqual(0.5f, apparent, 1e-6f);

            // Doubling the distance halves the apparent size: that factor is exactly the
            // follow rig's pull-back, which is why the audit multiplies growth by it.
            float near;
            float far;
            Assert.IsTrue(BallPresentationMath.TryApparentSize(0.5f, 1f, out near));
            Assert.IsTrue(BallPresentationMath.TryApparentSize(0.5f, 2f, out far));
            Assert.AreEqual(0.5f * near, far, 1e-6f);
        }

        /// <summary>
        /// A degenerate eye or a missing ball is not a measurement: the line then prints
        /// <c>app=none</c> instead of an invented number.
        /// </summary>
        [Test]
        public void ApparentSizeRefusesADegenerateEyeOrBall()
        {
            float apparent;
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, 0f, out apparent));
            Assert.AreEqual(0f, apparent);
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, 0.00001f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, -1f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, float.NaN, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0f, 2f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(-1f, 2f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(float.NaN, 2f, out apparent));
        }
    }
}
