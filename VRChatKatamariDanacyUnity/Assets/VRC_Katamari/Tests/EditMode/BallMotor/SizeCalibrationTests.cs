using NUnit.Framework;
using VRCKatamari.BallLayers;

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
    /// <item>30 m -> 14,137,166,941,154 mm^3 (the ONE global cap,
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
        private const float MaxBallDiameterM = 30f;
        private const float Ratio = 1f;
        private const float DiameterToleranceM = 0.001f;

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
            Assert.AreEqual(StartMm3, volume);
        }

        [Test]
        public void TheDesignLadderVolumesMapBackToTheirDiameters()
        {
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(0.30f, Ratio, out long v030));
            Assert.AreEqual(14137167L, v030);
            Assert.AreEqual(0.30f, DiameterForConfirmed(v030 - StartMm3), DiameterToleranceM);

            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(2.20f, Ratio, out long v220));
            Assert.AreEqual(5575279763L, v220);
            Assert.AreEqual(2.20f, DiameterForConfirmed(v220 - StartMm3), DiameterToleranceM);
        }

        [Test]
        public void TheGlobalThirtyMetreCapIsOneVolumeConstant()
        {
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(MaxBallDiameterM, Ratio, out long cap));
            Assert.AreEqual(14137166941154L, cap, "pi/6 * 30^3 * 1e9");
            // A confirmed total far past the cap still reads the cap diameter exactly.
            Assert.AreEqual(30f, DiameterForConfirmed(cap), 0f);
            Assert.AreEqual(30f, DiameterForConfirmed(cap * 3L), 0f);
        }

        [Test]
        public void DiameterAndVolumeAreOneSphereRelation()
        {
            // 1 m -> 523,598,776 mm^3; doubling the diameter multiplies the volume by 8.
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(1f, Ratio, out long one));
            Assert.AreEqual(523598776L, one);
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(2f, Ratio, out long two));
            Assert.AreEqual(one * 8L, two, "volume scales with the cube of the diameter");
            Assert.AreEqual(0.5f, SizeModelMath.HudDiameterWorldM(0.4998f), 0.001f);
        }
    }
}
