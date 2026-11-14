using NUnit.Framework;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the conversion (bank) shrink arithmetic.
    /// </summary>
    /// <remarks>
    /// KatamariBallMotor is an UdonSharpBehaviour, so this project does not
    /// instantiate it in EditMode and these tests do not fake the behaviour.
    /// They pin only the pure rule the motor calls: BankShrinkMath, which is
    /// the decided diameter-space formula of Docs/OPEN_DECISIONS.md (a) plus
    /// the size model's inverse. "pre" is the semantic diameter before a
    /// conversion, "start" is the forward size map's diameter at zero confirmed
    /// volume, and the numbers below are hand calculations or the calibrated
    /// configuration of the motor's serialized defaults - none of them re-runs
    /// the mapping to invent its own expectation.
    /// </remarks>
    public class BankShrinkMathTests
    {
        private const float Tolerance = 1e-6f;

        // Calibrated sizing, the same one KatamariBallMotor serializes: 1
        // baseline unit, 0.0141 m^3 per unit, a 1.1 m core-radius display cap
        // (2.20 m semantic diameter), and both ratios 1. The zero-confirmed
        // diameter is 0.29974 m and the cap is 2.20 m.
        private const float CalibratedBaseline = 1f;
        private const float CalibratedCubicMetersPerUnit = 0.0141f;
        private const float CalibratedMaxCoreRadius = 1.1f;
        private const float CalibratedCollisionRatio = 1f;
        private const float CalibratedSemanticRatio = 1f;

        // The inverse is algebraic, so only float rounding is left; 1e-5 m is
        // the same round-trip margin SizeModelTests uses (about 50x the worst
        // measured 1.9e-7 m) and is still 0.01 mm.
        private const float RoundTripToleranceM = 1e-5f;

        [Test]
        public void ResidualAppliesInDiameterSpaceAndEndsNearSixtyCentimetres()
        {
            // Docs/OPEN_DECISIONS.md (a), the decided rule:
            //   post = max(start, start + (pre - start) * residual)
            //   0.30 + (1.49 - 0.30) * 0.25 = 0.30 + 0.2975 = 0.5975 m.
            // 1.49 m is the highest peak the round model reached in four-player
            // runs (Gameplay README 4.3), so it is the case the rule must get
            // right.
            float postBankDiameter;
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f, 0.25f,
                out postBankDiameter));
            Assert.AreEqual(0.5975f, postBankDiameter, Tolerance);

            // The decision describes "about 0.60 m"; the volume-space residual
            // this replaces left 0.954 m at the same input, so the gap the
            // diameter-space rule closes is ~0.36 m.
            Assert.AreEqual(0.60f, postBankDiameter, 0.01f);
            Assert.Less(postBankDiameter, 0.62f);
        }

        [Test]
        public void StartFloorClampsAShrinkBelowStart()
        {
            // A pre diameter below start can only come from a parameter conflict,
            // but the rule's floor still applies instead of shrinking further.
            float postBankDiameter;
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 0.20f, 0.25f,
                out postBankDiameter));
            Assert.AreEqual(0.30f, postBankDiameter, Tolerance);
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 0.30f, 0.25f,
                out postBankDiameter));
            Assert.AreEqual(0.30f, postBankDiameter, Tolerance);
        }

        [Test]
        public void ResidualZeroResetsToStartAndOneKeepsTheDiameter()
        {
            // residual 0 is the old reset-to-start rule; residual 1 is a
            // conversion that does not shrink at all. Both are legal inputs of
            // the decided formula and must not be special-cased away.
            float postBankDiameter;
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f, 0f,
                out postBankDiameter));
            Assert.AreEqual(0.30f, postBankDiameter, Tolerance);
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f, 1f,
                out postBankDiameter));
            Assert.AreEqual(1.49f, postBankDiameter, Tolerance);

            // Out-of-range residuals clamp to the same two endpoints rather than
            // extrapolating a bigger or negative ball.
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f, -0.5f,
                out postBankDiameter));
            Assert.AreEqual(0.30f, postBankDiameter, Tolerance);
            Assert.IsTrue(BankShrinkMath.TryPostBankDiameter(0.30f, 1.49f, 2f,
                out postBankDiameter));
            Assert.AreEqual(1.49f, postBankDiameter, Tolerance);
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
        }

        [Test]
        public void PostBankDiameterPricesBackIntoTheUnitsTheLedgerStores()
        {
            // 0.5975 m at calibrated sizing is ~6.92 confirmed units. Hand
            // check: r = 0.29875 m, V = 4/3*pi*r^3 = 0.1117 m^3,
            // 0.1117 / 0.0141 = 7.921 total units, minus the 1 baseline unit.
            float postBankVolumeUnits;
            Assert.IsTrue(BankShrinkMath.TryBankVolumeUnits(0.5975f, CalibratedBaseline,
                CalibratedCubicMetersPerUnit, CalibratedMaxCoreRadius,
                CalibratedSemanticRatio, out postBankVolumeUnits));
            Assert.AreEqual(6.921f, postBankVolumeUnits, 1e-2f);

            // Round trip: the forward size map must report the same diameter,
            // which is what makes the shrink a diameter rule rather than a
            // volume approximation.
            float core, collision, semantic, effective;
            Assert.IsTrue(SizeModelMath.TryDerive(postBankVolumeUnits, CalibratedBaseline,
                CalibratedCubicMetersPerUnit, CalibratedMaxCoreRadius,
                CalibratedCollisionRatio, CalibratedSemanticRatio, out core,
                out collision, out semantic, out effective));
            Assert.AreEqual(0.5975f, semantic, RoundTripToleranceM);

            // carried = post - still unbanked, clamped to zero: after a bank the
            // still-unbanked read-back is normally zero, and a residual that
            // prices below it is not a carried value.
            float carried;
            Assert.IsTrue(BankShrinkMath.TryCarriedVolumeUnits(postBankVolumeUnits,
                0f, out carried));
            Assert.AreEqual(postBankVolumeUnits, carried, Tolerance);
            Assert.IsTrue(BankShrinkMath.TryCarriedVolumeUnits(postBankVolumeUnits,
                postBankVolumeUnits + 5f, out carried));
            Assert.AreEqual(0f, carried);
        }

        [Test]
        public void InverseFailureIsReportedAndWritesNothing()
        {
            // The motor refuses a conversion when this returns false, before the
            // ledger call, so a false here means no points and no shrink.
            float postBankVolumeUnits;
            Assert.IsFalse(BankShrinkMath.TryBankVolumeUnits(float.NaN, CalibratedBaseline,
                CalibratedCubicMetersPerUnit, CalibratedMaxCoreRadius,
                CalibratedSemanticRatio, out postBankVolumeUnits));
            Assert.AreEqual(0f, postBankVolumeUnits);

            Assert.IsFalse(BankShrinkMath.TryBankVolumeUnits(0f, CalibratedBaseline,
                CalibratedCubicMetersPerUnit, CalibratedMaxCoreRadius,
                CalibratedSemanticRatio, out postBankVolumeUnits));

            // A cap radius below the 0.14987 m baseline radius is a parameter
            // conflict, not a small ball: the same pair the forward map rejects.
            Assert.IsFalse(BankShrinkMath.TryBankVolumeUnits(0.60f, CalibratedBaseline,
                CalibratedCubicMetersPerUnit, 0.01f, CalibratedSemanticRatio,
                out postBankVolumeUnits));

            float carried;
            Assert.IsFalse(BankShrinkMath.TryCarriedVolumeUnits(float.NaN, 0f, out carried));
            Assert.IsFalse(BankShrinkMath.TryCarriedVolumeUnits(1f,
                float.PositiveInfinity, out carried));
            Assert.AreEqual(0f, carried);
        }
    }
}
