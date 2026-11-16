using System;
using NUnit.Framework;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the fixed-point size model (Docs/OPEN_DECISIONS.md (r) steps C+D): total volume is
    /// start + confirmed in mm^3, the diameter is the equivalent sphere, and the ONE global cap
    /// (default 30 m) saturates exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Numbers are hand calculations, not re-runs of the implementation:
    /// <c>V(d) = pi/6 * d^3 * 1e9</c>, so 0.30 m -> 14,137,167 mm^3, 2.20 m -> 5,575,279,763
    /// mm^3 and 30 m -> 14,137,166,941,154 mm^3. The retired 1356-unit calibration (5.5737 m^3
    /// at 2.20 m) and the 3.4390244 baseline are gone; the model now reads an integer volume.
    /// </para>
    /// <para>
    /// Float32 note, asserted where it matters: the model converts the long volume to float for
    /// the cube root, so a 1 mm^3 change near 14,000 m^3 is below float's resolution. The tests
    /// therefore compare DIAMETERS with a 1e-6 m tolerance and volumes exactly, never the other
    /// way round.
    /// </para>
    /// </remarks>
    public class SizeModelTests
    {
        private const float Tolerance = 1e-6f;
        private const long StartMm3 = 14094798L;          // pi/6 * 0.2997^3 * 1e9
        private const long Cap30Mm3 = 14137166941154L;    // pi/6 * 30^3 * 1e9

        // FLOAT32 BUDGET for volume goldens (FIXED_POINT_STEP_CD section 4): the model's
        // cube root runs in float, so a double golden can differ in the last bits by
        // <= ~7e-7 relative (6 float ops x 0.5 ulp of eps=1.19e-7); 2e-6 with 3x margin.
        // Diameters are asserted in metres with 1e-6 m, far below one millimetre.
        private const double FloatVolumeBudget = 2e-6;

        private static void AssertVolume(long expected, long actual)
        {
            Assert.IsTrue(Math.Abs((double)actual - (double)expected) <=
                Math.Abs((double)expected) * FloatVolumeBudget + 1.0,
                "volume " + actual + " must equal " + expected +
                " within the float32 budget 2e-6 relative");
        }

        /// <summary>Derives at the default ratios (both 1) and the given cap.</summary>
        private static bool Derive(long startMm3, long confirmedMm3, float capDiameterM,
            out float coreRadius, out float collisionRadius, out float diameter,
            out long effectiveConfirmedMm3)
        {
            return SizeModelMath.TryDerive(startMm3, confirmedMm3, capDiameterM, 1f, 1f,
                out coreRadius, out collisionRadius, out diameter, out effectiveConfirmedMm3);
        }

        [Test]
        public void StartVolumeMapsToTheStartDiameter()
        {
            Assert.IsTrue(Derive(StartMm3, 0L, 30f, out float core, out float collision,
                out float diameter, out long effective));
            Assert.AreEqual(0.2997f, diameter, Tolerance, "the start sphere is 0.2997 m");
            Assert.AreEqual(0.14985f, core, Tolerance);
            Assert.AreEqual(0.14985f, collision, Tolerance, "ratios are 1 by default");
            Assert.AreEqual(0L, effective);
        }

        [Test]
        public void AddingGrowthMovesTheDiameterAlongTheSphereCurve()
        {
            // 1,417,085,102 mm^3 is story 01's whole derived content growth (design note §7).
            Assert.IsTrue(Derive(StartMm3, 1417085102L, 30f, out float core, out float collision,
                out float diameter, out long effective));
            Assert.AreEqual(1.39819f, diameter, 1e-5f, "eating the level's 130 props reaches ~1.398 m");
            Assert.AreEqual(0.699095f, core, 1e-5f);
            Assert.AreEqual(0f, collision - core, Tolerance);
            Assert.AreEqual(1417085102L, effective);
        }

        [Test]
        public void TheCapSaturatesExactlyAndReportsTheCapVolume()
        {
            // One millimetre past the cap volume must still report the cap's own diameter, not
            // a cbrt of a clamped value: two different over-cap totals cannot differ on screen.
            Assert.IsTrue(Derive(StartMm3, Cap30Mm3 - StartMm3, 30f, out float coreA, out _,
                out float diameterA, out long effectiveA));
            Assert.IsTrue(Derive(StartMm3, Cap30Mm3 * 4L, 30f, out float coreB, out _,
                out float diameterB, out long effectiveB));
            Assert.AreEqual(30f, diameterA, Tolerance);
            Assert.AreEqual(30f, diameterB, Tolerance);
            Assert.AreEqual(diameterA, diameterB, 0f, "saturation is exact, not approximate");
            // effectiveConfirmedMm3 is the CONFIRMED part that fits under the cap, i.e. the
            // cap volume minus the start volume - not the cap volume itself.
            AssertVolume(Cap30Mm3 - StartMm3, effectiveA);
            AssertVolume(Cap30Mm3 - StartMm3, effectiveB);
            Assert.AreEqual(15f, coreA, Tolerance);

            // A per-level cap still works through the same single parameter (2.20 m here).
            Assert.IsTrue(Derive(StartMm3, 5575279763L, 2.2f, out _, out _, out float d22,
                out long e22));
            Assert.AreEqual(2.2f, d22, Tolerance);
            AssertVolume(5575279763L - StartMm3, e22);
        }

        [Test]
        public void TheInverseIsTheForwardMapSolvedForDiameter()
        {
            // D = 1.00 m -> V_total = pi/6 * 1e9 = 523,598,776 mm^3 (rounded); confirmed is
            // that minus the start.
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(1f, 1f, out long total));
            AssertVolume(523598776L, total);
            Assert.IsTrue(SizeModelMath.TryConfirmedVolumeMm3ForSemanticDiameter(1f, StartMm3,
                1f, out long confirmed));
            Assert.AreEqual(total - StartMm3, confirmed);
            Assert.IsTrue(Derive(StartMm3, confirmed, 30f, out _, out _, out float back,
                out _));
            Assert.AreEqual(1f, back, 1e-5f, "the inverse and the forward map agree in metres");
        }

        [Test]
        public void HudDiameterIsTheSemanticPlusTwoHundredthsOfAMillimetre()
        {
            Assert.AreEqual(0.3002f, SizeModelMath.HudDiameterWorldM(0.3000f), Tolerance);
            Assert.AreEqual(2.2002f, SizeModelMath.HudDiameterWorldM(2.2000f), Tolerance);
            Assert.AreEqual(0f, SizeModelMath.HudDiameterWorldM(0f));
            // The offset is display only: the model's own diameter stays un-offset.
            Assert.IsTrue(Derive(StartMm3, 0L, 30f, out _, out _, out float diameter, out _));
            Assert.AreEqual(0.2997f, diameter, Tolerance);
        }

        [Test]
        public void TheModelRefusesIllegalInputInsteadOfClamping()
        {
            float core, collision, diameter;
            long effective;
            Assert.IsFalse(Derive(0L, 0L, 30f, out core, out collision, out diameter, out effective));
            Assert.IsFalse(Derive(StartMm3, -1L, 30f, out _, out _, out _, out _));
            Assert.IsFalse(Derive(StartMm3, 0L, 0f, out _, out _, out _, out _));
            Assert.IsFalse(Derive(StartMm3, 0L, float.NaN, out _, out _, out _, out _));
            Assert.IsFalse(Derive(StartMm3, 0L, float.PositiveInfinity, out _, out _, out _,
                out _));
            // Ratios are validated, not ignored.
            Assert.IsFalse(SizeModelMath.TryDerive(StartMm3, 0L, 30f, 0f, 1f, out _, out _,
                out _, out _));
            Assert.IsFalse(SizeModelMath.TryDerive(StartMm3, 0L, 30f, 1f, 0f, out _, out _,
                out _, out _));
            // A failed derive writes zeros, so a caller that ignores the answer cannot apply
            // whatever happened to be in the out variables.
            Assert.AreEqual(0f, core);
            Assert.AreEqual(0f, collision);
            Assert.AreEqual(0f, diameter);
            Assert.AreEqual(0L, effective);
        }

        [Test]
        public void RatiosScaleTheCollisionRadiusAndTheSemanticDiameter()
        {
            // The ratio is SEMANTIC DIAMETER / CORE DIAMETER (the old convention:
            // semantic = 2 * coreRadius * ratio), so with ratio = 2 the core radius is
            // D / (2 * 2) = 0.074925 m, NOT D / 2. The first version of this test read the
            // core as the no-ratio radius and "found" a half-size bug that was not there;
            // this assertion is the convention, written out.
            Assert.IsTrue(SizeModelMath.TryDerive(StartMm3, 0L, 30f, 1.5f, 2f,
                out float core, out float collision, out float semantic, out _));
            Assert.AreEqual(0.2997f, semantic, 1e-5f);
            Assert.AreEqual(0.2997f / 4f, core, 1e-5f);
            Assert.AreEqual(core * 1.5f, collision, 1e-5f);

            // Sanity at the default ratio 1: the core radius is half the diameter and the
            // collision radius equals it (ratio 1), i.e. the plain sphere case.
            Assert.IsTrue(SizeModelMath.TryDerive(StartMm3, 0L, 30f, 1f, 1f,
                out float core1, out float collision1, out float semantic1, out _));
            Assert.AreEqual(0.2997f / 2f, core1, 1e-5f);
            Assert.AreEqual(core1, collision1, 1e-6f);
            Assert.AreEqual(0.2997f, semantic1, 1e-5f);
        }
    }
}
