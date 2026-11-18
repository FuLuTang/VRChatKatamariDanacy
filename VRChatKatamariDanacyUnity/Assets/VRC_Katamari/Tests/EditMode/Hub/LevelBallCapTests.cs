using NUnit.Framework;
using VRCKatamari.BallLayers;
using VRCKatamari.Hub;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The per-level ball cap (owner 2026-11-18): which level gets which cap, and that the
    /// existing size derive stops the growth at it - the cap is one parameter of
    /// <see cref="SizeModelMath.TryDerive"/>, not a second gate.
    /// </summary>
    public class LevelBallCapTests
    {
        private static readonly float[] HubCaps = { 30f, 1.2f, 30f, 30f };

        [Test]
        public void EachLevelGetsItsOwnCap()
        {
            // AllMapsHub order: picnic corner, Cancer, physics test field, terrain lab.
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(HubCaps, 0, 30f));
            Assert.AreEqual(1.2f, LevelBallCapMath.Resolve(HubCaps, 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(HubCaps, 2, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(HubCaps, 3, 30f));
        }

        [Test]
        public void ANullShortOrInvalidEntryFallsBackToTheSceneCap()
        {
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(null, 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new float[0], 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new[] { 30f }, 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new[] { 30f, -1f }, 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new[] { 30f, 0f }, 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new[] { 30f, float.NaN }, 1, 30f));
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new[] { 30f, float.PositiveInfinity }, 1, 30f));
        }

        [Test]
        public void TheFallbackIsTheSceneCapNotThePreviousLevelsCap()
        {
            // Switching away from Cancer must not carry its 1.2 m cap into a level whose
            // table entry is missing; the scene's own cap is what that level used to get.
            float previousLevelCap = 1.2f;
            Assert.AreEqual(30f, LevelBallCapMath.Resolve(new float[0], 0, 30f),
                "the scene default, not the previous level's cap");
            Assert.AreNotEqual(previousLevelCap, LevelBallCapMath.Resolve(new float[0], 0, 30f));
        }

        [Test]
        public void UsabilityIsFiniteAndPositive()
        {
            Assert.IsTrue(LevelBallCapMath.IsUsable(1.2f));
            Assert.IsFalse(LevelBallCapMath.IsUsable(0f));
            Assert.IsFalse(LevelBallCapMath.IsUsable(-1.2f));
            Assert.IsFalse(LevelBallCapMath.IsUsable(float.NaN));
            Assert.IsFalse(LevelBallCapMath.IsUsable(float.PositiveInfinity));
        }

        [Test]
        public void CancerCapStopsTheGrowthAtOnePointTwoMetres()
        {
            // The original Cancer level starts at 22.2 cm and its ball caps at about 1.2 m.
            // Fed far more confirmed volume than the cap, the derive must saturate exactly at
            // the cap (and report the cap's collision radius through the same ratios), which
            // is the existing growth-stop rule the per-level cap reuses.
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(0.222f, 1f, out long startMm3));
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(1.2f, 1f, out long capMm3));
            Assert.Greater(capMm3, startMm3);

            Assert.IsTrue(SizeModelMath.TryDerive(startMm3, capMm3 * 100L, 1.2f, 1f, 1f,
                out float core, out float collision, out float semantic,
                out long effectiveConfirmedMm3));
            Assert.AreEqual(1.2f, semantic, 1e-5f, "the semantic diameter is the cap");
            Assert.AreEqual(0.6f, core, 1e-5f, "core radius is half the semantic diameter");
            Assert.AreEqual(0.6f, collision, 1e-5f, "collision radius follows the cap");
            Assert.AreEqual(capMm3 - startMm3, effectiveConfirmedMm3,
                "only the confirmed volume that fits under the cap counts");
            Assert.AreEqual(1.2f, 2f * core, 1e-5f);
        }

        [Test]
        public void UnderTheCancerCapTheDeriveStillReportsTheRealSize()
        {
            // A quarter of the cap's volume on top of the start ball is well under 1.2 m; the
            // cap must not clamp a ball that has not reached it.
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(0.222f, 1f, out long startMm3));
            Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(1.2f, 1f, out long capMm3));
            Assert.IsTrue(SizeModelMath.TryDerive(startMm3, capMm3 / 4L, 1.2f, 1f, 1f,
                out _, out _, out float semantic, out _));
            Assert.AreEqual(0.7623f, semantic, 2e-3f,
                "pi/6 * D^3 once, with no cap clamp below the cap");
            Assert.Less(semantic, 1.2f);
        }
    }
}
