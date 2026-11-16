using NUnit.Framework;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the conversion (bank) shrink arithmetic, in the fixed-point mm^3
    /// model (Docs/OPEN_DECISIONS.md (a) and (r) C+D).
    /// </summary>
    /// <remarks>
    /// <para>
    /// KatamariBallMotor is an UdonSharpBehaviour, so these tests pin only the pure rule it
    /// calls: the decided diameter-space formula <c>post = max(start, start + (pre - start) *
    /// residual)</c> plus the size model's inverse. "pre" is the semantic diameter before a
    /// conversion, "start" is the forward map's diameter at zero confirmed volume.
    /// </para>
    /// <para>
    /// Hand calculations, not re-runs: the 0.2997 m start is 14,094,798 mm^3; the decided
    /// example is 1.49 m -> 0.5975 m with residual 0.25; a 0.5975 m sphere is
    /// <c>pi/6 * 0.5975^3 * 1e9 = 111,689,501 mm^3</c>, so its confirmed part is
    /// 97,594,703 mm^3. The retired unit bookkeeping (23.802 "units", baseline 3.4390244) is
    /// gone: the ledger stores mm^3, and the carried residual is an integer subtraction.
    /// </para>
    /// </remarks>
    public class BankShrinkMathTests
    {
        private const float Tolerance = 1e-6f;

        private const long StartMm3 = 14094798L;              // the 0.2997 m calibration ball
        private const long PostBankConfirmedMm3 = 97594703L;  // 0.5975 m sphere minus the start
        private const float RoundTripToleranceM = 1e-5f;

        [Test]
        public void ResidualAppliesInDiameterSpaceAndEndsNearSixtyCentimetres()
        {
            // Docs/OPEN_DECISIONS.md (a), the decided rule:
            //   post = max(start, start + (pre - start) * residual)
            //   0.30 + (1.49 - 0.30) * 0.25 = 0.30 + 0.2975 = 0.5975 m.
            float postBankDiameter;
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f, 0.25f,
                out postBankDiameter));
            Assert.AreEqual(0.5975f, postBankDiameter, Tolerance);
            Assert.AreEqual(0.60f, postBankDiameter, 0.01f);
            Assert.Less(postBankDiameter, 0.62f);
        }

        [Test]
        public void ThePostBankDiameterPricesBackToTheConfirmedMm3()
        {
            // 0.5975 m sphere = 111,689,501 mm^3 total; confirmed above the 0.2997 m start is
            // 97,594,703 mm^3. This is the one conversion the bank path uses.
            Assert.IsTrue(BankShrinkMath.TryBankVolumeMm3(0.5975f, StartMm3, 1f,
                out long postBankConfirmedMm3));
            Assert.AreEqual(PostBankConfirmedMm3, postBankConfirmedMm3);

            // Round trip: the forward map must report the same diameter, which is what makes
            // the shrink a diameter rule rather than a volume approximation.
            Assert.IsTrue(SizeModelMath.TryDerive(StartMm3, postBankConfirmedMm3, 30f, 1f, 1f,
                out _, out _, out float semantic, out _));
            Assert.AreEqual(0.5975f, semantic, RoundTripToleranceM);

            // Below the start diameter the inverse is zero confirmed, not negative.
            Assert.IsTrue(BankShrinkMath.TryBankVolumeMm3(0.10f, StartMm3, 1f,
                out long underStart));
            Assert.AreEqual(0L, underStart);
        }

        [Test]
        public void CarriedResidualIsAnIntegerSubtractionClampedAtZero()
        {
            // carried = max(0, postBankConfirmed - stillUnbanked): the ledger has already moved
            // the unbanked amount into the score, so a residual priced below it is not carried.
            Assert.IsTrue(BankShrinkMath.TryCarriedVolumeMm3(PostBankConfirmedMm3, 0L,
                out long carried));
            Assert.AreEqual(PostBankConfirmedMm3, carried);
            Assert.IsTrue(BankShrinkMath.TryCarriedVolumeMm3(PostBankConfirmedMm3,
                PostBankConfirmedMm3 + 5_000_000L, out carried));
            Assert.AreEqual(0L, carried);
            Assert.IsTrue(BankShrinkMath.TryCarriedVolumeMm3(PostBankConfirmedMm3,
                1_000_000L, out carried));
            Assert.AreEqual(PostBankConfirmedMm3 - 1_000_000L, carried);
        }

        [Test]
        public void NonFiniteOrNonPositiveInputsAreRefusedRatherThanClamped()
        {
            float postBankDiameter;
            Assert.IsFalse(BankShrinkMath.TryPostBankDiameter(float.NaN, 1.49f, 0.25f,
                out postBankDiameter));
            Assert.AreEqual(0f, postBankDiameter);
            Assert.IsFalse(BankShrinkMath.TryPostBankDiameter(0f, 1.49f, 0.25f,
                out postBankDiameter));
            Assert.IsFalse(BankShrinkMath.TryPostBankDiameter(0.30f,
                float.PositiveInfinity, 0.25f, out postBankDiameter));
            Assert.IsFalse(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f,
                float.NaN, out postBankDiameter));

            long volumeMm3;
            Assert.IsFalse(BankShrinkMath.TryBankVolumeMm3(float.NaN, StartMm3, 1f,
                out volumeMm3));
            Assert.AreEqual(0L, volumeMm3);
            Assert.IsFalse(BankShrinkMath.TryBankVolumeMm3(0f, StartMm3, 1f, out volumeMm3));
            // A zero/negative start is not a small ball: there is no sphere to invert.
            Assert.IsFalse(BankShrinkMath.TryBankVolumeMm3(0.60f, 0L, 1f, out volumeMm3));

            long carried;
            Assert.IsFalse(BankShrinkMath.TryCarriedVolumeMm3(-1L, 0L, out carried));
            Assert.IsFalse(BankShrinkMath.TryCarriedVolumeMm3(1L, -1L, out carried));
            Assert.AreEqual(0L, carried);
        }
    }
}
