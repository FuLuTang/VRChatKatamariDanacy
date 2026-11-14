using NUnit.Framework;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the runtime sizing calibration to the offline model every level's
    /// star targets are built on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The oracle is <c>Docs/Design/Gameplay/round_model.py</c>: a round runs from
    /// the 0.30 m start ball to the 2.20 m cap, the cap is reached at 1356
    /// confirmed units, and eating a whole 1284-unit map leaves the ball at
    /// 2.16 m. Those are the numbers the star thresholds and target times were
    /// derived from, so the runtime must reproduce them exactly; a runtime whose
    /// unit is a different size silently rescales the whole game's pacing.
    /// </para>
    /// <para>
    /// These are the three nails. They are deliberately written against
    /// <see cref="SizeModelMath.TryDerive"/> with the constants the motor
    /// serializes, not against a helper of this file, so a change to either
    /// default breaks this test rather than drifting quietly.
    /// </para>
    /// <para>
    /// Why the baseline is 3.4390244 rather than 1: the baseline is a VOLUME (the
    /// 0.30 m start ball, 0.0141 m^3), not a unit count. When the unit shrank to
    /// 0.0041 m^3 the same volume became 0.0141 / 0.0041 = 3.439 units. Holding
    /// the baseline at 1 unit would start the ball at 0.199 m instead of 0.30 m.
    /// </para>
    /// </remarks>
    public class SizeCalibrationTests
    {
        // The constants KatamariBallMotor serializes (Runtime/BallMotor/KatamariBallMotor.cs).
        private const float BaselineUnits = 3.4390244f;
        private const float CubicMetersPerUnit = 0.0041f;
        private const float MaxCoreRadiusM = 1.1f;
        private const float UnionRatio = 1f;

        // 1 mm is far below a gameplay band but far above float noise here.
        private const float DiameterToleranceM = 0.005f;

        private static float DiameterForUnits(float confirmedUnits)
        {
            float core, collision, semantic, effective;
            Assert.IsTrue(SizeModelMath.TryDerive(confirmedUnits, BaselineUnits,
                CubicMetersPerUnit, MaxCoreRadiusM, UnionRatio, UnionRatio,
                out core, out collision, out semantic, out effective),
                "TryDerive rejected " + confirmedUnits + " units");
            return semantic;
        }

        [Test]
        public void BaselineIsTheThreeHundredMillimetreStartBall()
        {
            float diameter = DiameterForUnits(0f);
            Assert.AreEqual(0.29974f, diameter, DiameterToleranceM);
        }

        [Test]
        public void OneMapOfGrowthEndsAtTwoPointOneSixMetres()
        {
            // 1284 units is what the decided round's map is worth; the model says
            // a full clear leaves the ball at 2.16 m, not at the cap.
            float diameter = DiameterForUnits(1284f);
            Assert.AreEqual(2.160f, diameter, DiameterToleranceM);
            Assert.Less(diameter, 2.20f, "a full map must not already be capped");
        }

        [Test]
        public void TheCapIsReachedAtThirteenHundredFiftySixUnits()
        {
            float diameter = DiameterForUnits(1356f);
            Assert.AreEqual(2.200f, diameter, DiameterToleranceM);
        }

        [Test]
        public void TheOldCalibrationWouldHaveCappedFourTimesTooEarly()
        {
            // The regression this file exists for: under the retired 0.0141 m^3
            // per unit, ~394 units (30% of a 1284-unit map) already hit the cap,
            // so growth ran 3.44x faster than the model. At the model's
            // calibration those same 394 units must be well below the cap.
            float diameter = DiameterForUnits(394.4f);
            Assert.Less(diameter, 1.50f,
                "394 units must be mid-round, not capped; the unit is too large again");
        }

        [Test]
        public void TierDiametersPriceIntoTheUnitsTheDesignUses()
        {
            // The inverse of the calibration, for the design side: how many
            // confirmed units each decided tier diameter costs. Values from
            // round_model.py's units_for_diameter at this calibration.
            Assert.AreEqual(17.8f, UnitsForDiameter(0.55f), 0.2f);
            Assert.AreEqual(106.1f, UnitsForDiameter(0.95f), 0.3f);
            Assert.AreEqual(385.9f, UnitsForDiameter(1.45f), 0.6f);
            Assert.AreEqual(1356.4f, UnitsForDiameter(2.20f), 1.0f);
        }

        private static float UnitsForDiameter(float diameter)
        {
            float units;
            Assert.IsTrue(SizeModelMath.TryVolumeUnitsForSemanticDiameter(diameter,
                BaselineUnits, CubicMetersPerUnit, MaxCoreRadiusM, UnionRatio, out units),
                "inverse rejected " + diameter + " m");
            return units;
        }
    }
}
