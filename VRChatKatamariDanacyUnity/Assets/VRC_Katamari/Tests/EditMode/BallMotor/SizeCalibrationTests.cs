using System;
using NUnit.Framework;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the fixed-point sizing calibration (Docs/OPEN_DECISIONS.md (r) steps C+D) to the
    /// volumes the design note records, replacing the retired 1356-unit oracle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old oracle (round_model.py's 1356 confirmed units from 0.30 m to 2.20 m) was a
    /// UNIT calibration: it only made sense while one unit was 0.0041 m^3. Steps C+D make the
    /// ledger quantity cubic millimetres, so the anchors become sphere volumes, all hand
    /// calculations: <c>V = pi/6 * d^3 * 1e9</c>.
    /// </para>
    /// <list type="bullet">
    /// <item>start 0.2997 m -> 14,094,798 mm^3 (the motor's serialized
    /// <c>startVolumeMm3</c>);</item>
    /// <item>0.30 m -> 14,137,167 mm^3 and 2.20 m -> 5,575,279,763 mm^3 (the map's content
    /// ladder top, still the analysis ceiling);</item>
    /// <item>400 m -> 33,510,321,638,291,128 mm^3 (the ONE global cap,
    /// <c>maxBallDiameterM</c>).</item>
    /// </list>
    /// <para>
    /// The tests call <see cref="SizeModelMath.TryDerive"/> with the motor's current defaults
    /// rather than a helper of this file, so a default change breaks here instead of drifting.
    /// Diameters are compared with 1 mm tolerance and volumes exactly, because the model's
    /// cube root runs in float (documented in the class remarks).
    /// </para>
    /// </remarks>
    public class SizeCalibrationTests
    {
        private const long StartMm3 = 14094798L;
        private const float MaxBallDiameterM = KatamariBallMotor.GlobalMaxBallDiameterM;
        private const float Ratio = 1f;
        private const float DiameterToleranceM = 0.001f;

        // FLOAT32 BUDGET for a VOLUME golden (documented in FIXED_POINT_STEP_CD section 4):
        // the model converts the long to float for the cube root, so a sphere-volume golden
        // computed in double can differ in the last float bits. Worst case is ~6 float ops
        // (three multiplies, one divide, Mathf.Pow, one multiply by 1e9), each within 0.5 ulp
        // of float32 eps = 1.19e-7, i.e. <= ~7e-7 relative; 2e-6 is that with 3x margin and
        // is still far below one millimetre of diameter. The +1 covers a 1-2 mm^3 rounding
        // at small values. Diameters are compared in METRES with DiameterToleranceM.
        private const double FloatVolumeBudget = 2e-6;

        private static void AssertVolume(long expected, long actual)
        {
            double delta = Math.Abs((double)actual - (double)expected);
            Assert.IsTrue(delta <= Math.Abs((double)expected) * FloatVolumeBudget + 1.0,
                "sphere volume " + actual + " must equal " + expected +
                " within the float32 budget 2e-6 relative");
        }

        private static float DiameterForConfirmed(long confirmedMm3)
        {
            float core, collision, semantic;
            long effective;
            Assert.IsTrue(SizeModelMath.TryDerive(StartMm3, confirmedMm3, MaxBallDiameterM,
                Ratio, Ratio, out core, out collision, out semantic, out effective),
                "TryDerive rejected " + confirmedMm3 + " mm^3");
            return semantic;
        }

        [Test]
        public void TheStartBallIsTheCalibrationVolume()
        {
            Assert.AreEqual(0.2997f, DiameterForConfirmed(0L), DiameterToleranceM);
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(0.2997f, Ratio, out long volume));
            AssertVolume(StartMm3, volume);
        }

        [Test]
        public void TheDesignLadderVolumesMapBackToTheirDiameters()
        {
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(0.30f, Ratio, out long v030));
            AssertVolume(14137167L, v030);
            Assert.AreEqual(0.30f, DiameterForConfirmed(v030 - StartMm3), DiameterToleranceM);

            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(2.20f, Ratio, out long v220));
            AssertVolume(5575279763L, v220);
            Assert.AreEqual(2.20f, DiameterForConfirmed(v220 - StartMm3), DiameterToleranceM);
        }

        [Test]
        public void TheGlobalFourHundredMetreCapSaturatesExactly()
        {
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(MaxBallDiameterM, Ratio, out long cap));
            AssertVolume(33510321638291128L, cap);
            // A confirmed total far past the cap still reads the cap diameter exactly.
            Assert.AreEqual(400f, DiameterForConfirmed(cap), 0f);
            Assert.AreEqual(400f, DiameterForConfirmed(cap * 3L), 0f);
        }

        [Test]
        public void SphereVolumeRejectsFiniteInputsThatWouldOverflowLong()
        {
            Assert.IsFalse(SizeModelMath.TrySphereVolumeMm3(400f, 0.1f, out long volume));
            Assert.AreEqual(0L, volume);
        }

        [Test]
        public void DiameterAndVolumeAreOneSphereRelation()
        {
            // 1 m -> 523,598,776 mm^3; doubling the diameter multiplies the volume by 8.
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(1f, Ratio, out long one));
            AssertVolume(523598776L, one);
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(2f, Ratio, out long two));
            Assert.AreEqual(one * 8L, two, "volume scales with the cube of the diameter");
            Assert.AreEqual(0.5f, SizeModelMath.HudDiameterWorldM(0.4998f), 0.001f);
        }
    }
}
