using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_size_model/test_size_model.py.
    /// Expected values are hand calculations or the independent sphere-volume
    /// relation V = 4*pi*r^3/3; none of the assertions re-runs the mapping.
    /// Lengths are metres in world space, the effective volume is in volume
    /// units, and the five parameters below are test inputs, not product
    /// defaults.
    /// </summary>
    public class SizeModelTests
    {
        private const float Tolerance = 1e-6f;

        // Test-only sizing: one volume-unit baseline, 0.25 m^3 per unit, a 3 m
        // display cap, collision radius 0.8 x core, semantic diameter 1.5 x
        // core diameter.
        private const float Baseline = 1f;
        private const float CubicMetersPerUnit = 0.25f;
        private const float MaxCoreRadius = 3f;
        private const float CollisionRatio = 0.8f;
        private const float SemanticRatio = 1.5f;

        [Test]
        public void SphereGeometryAndEightfoldVolumeRatioHold()
        {
            // One baseline unit at 2 m^3/unit is 2 m^3; seven confirmed units
            // add 14 m^3 for 16 m^3, which is eight times as much, so the
            // radius must double. Both sides are hand calculations.
            float core2, collision2, semantic2, effective2;
            Assert.IsTrue(SizeModelMath.TryDerive(0f, 1f, 2f, 3f, 1f, 1f,
                out core2, out collision2, out semantic2, out effective2));
            float core16, collision16, semantic16, effective16;
            Assert.IsTrue(SizeModelMath.TryDerive(7f, 1f, 2f, 3f, 1f, 1f,
                out core16, out collision16, out semantic16, out effective16));

            Assert.AreEqual(2f, (4f * Mathf.PI / 3f) * core2 * core2 * core2, 1e-3f);
            Assert.AreEqual(16f, (4f * Mathf.PI / 3f) * core16 * core16 * core16, 1e-3f);
            Assert.AreEqual(2f, core16 / core2, Tolerance);

            // The diagnostic is a volume in volume units, so it is the
            // baseline-only 1 unit and the 8 units, not cubic metres.
            Assert.AreEqual(1f, effective2, 1e-4f);
            Assert.AreEqual(8f, effective16, 1e-4f);
        }

        [Test]
        public void CoreCollisionAndSemanticDimensionsStayDistinct()
        {
            float core, collision, semantic, effective;
            Assert.IsTrue(Derive(0f, out core, out collision, out semantic, out effective));
            float diameter;
            Assert.IsTrue(SizeModelMath.TryCoreDiameter(core, out diameter));

            // Baseline only: 1 unit is 0.25 m^3, so r = cbrt(3*0.25/(4*pi)) =
            // 0.390796 m and d = 0.781593 m. Collision is 0.8*r = 0.312637 m
            // and the semantic diameter is 1.5*d = 1.172389 m.
            Assert.AreEqual(0.3907963f, core, Tolerance);
            Assert.AreEqual(0.7815927f, diameter, Tolerance);
            Assert.AreEqual(0.3126371f, collision, Tolerance);
            Assert.AreEqual(1.1723890f, semantic, Tolerance);
            Assert.AreEqual(1f, effective, Tolerance);

            // No field may carry two quantities: the core radius, the core
            // diameter, the collision radius and the narrative diameter are
            // four different numbers in this configuration.
            Assert.IsTrue(core != diameter, "a radius was reported as a diameter");
            Assert.IsTrue(core != collision, "the collision radius was reported as the core radius");
            Assert.IsTrue(core != semantic, "the semantic diameter was reported as the core radius");
            Assert.IsTrue(diameter != semantic, "the semantic diameter was reported as the core diameter");
            Assert.IsTrue(collision != semantic, "the semantic diameter was reported as the collision radius");

            // The reference raises TypeError when a collision radius or a
            // semantic diameter is passed where a core radius belongs. Plain
            // floats carry no such tag, so the separation is structural: the
            // mapping takes a volume, TryCoreDiameter takes the core radius,
            // and the three dimensions leave through distinct out parameters.
            // What remains checkable is that a value that cannot be a radius
            // is refused by the only conversion that interprets one.
            Assert.IsFalse(SizeModelMath.TryCoreDiameter(0f, out diameter));
            Assert.IsFalse(SizeModelMath.TryCoreDiameter(-0.4f, out diameter));
            Assert.IsFalse(SizeModelMath.TryCoreDiameter(float.NaN, out diameter));
            Assert.AreEqual(0f, diameter);
        }

        [Test]
        public void DisplayCapIsExactAndIndependentOfTheConfirmedAmount()
        {
            // 1e15 and 2e15 volume units are both far beyond the 3 m cap and
            // are distinct float values, so the two calls really differ.
            float core15, collision15, semantic15, effective15;
            Assert.IsTrue(Derive(1e15f, out core15, out collision15, out semantic15, out effective15));
            float core2e15, collision2e15, semantic2e15, effective2e15;
            Assert.IsTrue(Derive(2e15f, out core2e15, out collision2e15, out semantic2e15, out effective2e15));

            // The cap branch assigns the parameter itself, so saturation is
            // exact and a larger confirmed volume cannot yield a larger
            // display.
            Assert.AreEqual(MaxCoreRadius, core15);
            Assert.AreEqual(MaxCoreRadius, core2e15);
            Assert.AreEqual(collision15, collision2e15);
            Assert.AreEqual(semantic15, semantic2e15);
            Assert.AreEqual(effective15, effective2e15);

            // The cap holds 4/3*pi*3^3/0.25 = 452.389 volume units, so the
            // diagnostic reports that and not the confirmed 1e15: it is a
            // finite presentation value, never a balance.
            Assert.AreEqual((4f * Mathf.PI / 3f) * 27f / CubicMetersPerUnit, effective15, 1e-3f);
            Assert.Less(effective15, 1e15f);

            // The confirmed amount is a by-value argument, so there is no
            // ledger this call could rewrite; the Python test's untouched
            // dict has no C# counterpart and needs none.
        }

        [Test]
        public void ResetRebuildsFromTheNewConfirmedValueOnly()
        {
            float grownCore, grownCollision, grownSemantic, grownEffective;
            Assert.IsTrue(Derive(20f, out grownCore, out grownCollision, out grownSemantic, out grownEffective));
            float rebuiltCore, rebuiltCollision, rebuiltSemantic, rebuiltEffective;
            Assert.IsTrue(Derive(0f, out rebuiltCore, out rebuiltCollision, out rebuiltSemantic, out rebuiltEffective));

            // 20 confirmed units give 21 * 0.25 = 5.25 m^3, so r =
            // cbrt(15.75/(4*pi)) = 1.078177 m; the rebuild is the baseline
            // 0.390796 m. The rebuild cannot be a scale applied to the grown
            // display because the two calls share no state.
            Assert.AreEqual(1.0781775f, grownCore, 1e-5f);
            Assert.AreEqual(0.3907963f, rebuiltCore, Tolerance);
            Assert.Greater(grownCore, rebuiltCore);

            // Repeating the rebuild from the same input is identical, which is
            // what "rebuilt from the new ledger, not from the last display"
            // means for a pure function.
            float againCore, againCollision, againSemantic, againEffective;
            Assert.IsTrue(Derive(0f, out againCore, out againCollision, out againSemantic, out againEffective));
            Assert.AreEqual(rebuiltCore, againCore);
            Assert.AreEqual(rebuiltCollision, againCollision);
            Assert.AreEqual(rebuiltSemantic, againSemantic);
            Assert.AreEqual(rebuiltEffective, againEffective);
        }

        [Test]
        public void FiniteExtremesSaturateOrRejectWithoutInfinity()
        {
            // The largest finite confirmed volume maps to the cap and every
            // output stays finite.
            float core, collision, semantic, effective;
            Assert.IsTrue(Derive(float.MaxValue, out core, out collision, out semantic, out effective));
            AssertFinite(core, "core radius");
            AssertFinite(collision, "collision radius");
            AssertFinite(semantic, "semantic diameter");
            AssertFinite(effective, "effective volume");
            Assert.AreEqual(MaxCoreRadius, core);

            // Volume factor and conversion factor are both at the largest
            // finite float. The product would overflow, so the log domain is
            // what keeps the mapping representable; the relation it must
            // satisfy is the sphere volume in log form, ln r = (2*ln(max) +
            // ln 3 - ln(4*pi))/3.
            float extremeCore, extremeCollision, extremeSemantic, extremeEffective;
            Assert.IsTrue(SizeModelMath.TryDerive(float.MaxValue, 1f, float.MaxValue, float.MaxValue, 1f, 1f,
                out extremeCore, out extremeCollision, out extremeSemantic, out extremeEffective));
            AssertFinite(extremeCore, "extreme core radius");
            float expectedLogRadius = (2f * Mathf.Log(float.MaxValue) + Mathf.Log(3f) -
                Mathf.Log(4f * Mathf.PI)) / 3f;
            Assert.AreEqual(expectedLogRadius, Mathf.Log(extremeCore), 1e-3f);

            // A finite core can still overflow once the ratio is applied: the
            // core is about 3.0e25 m and a 1e14 ratio is infinite, which must
            // be rejected with no partial output.
            float rejectedCore, rejectedCollision, rejectedSemantic, rejectedEffective;
            Assert.IsFalse(SizeModelMath.TryDerive(float.MaxValue, 1f, float.MaxValue, float.MaxValue, 1e14f, 1f,
                out rejectedCore, out rejectedCollision, out rejectedSemantic, out rejectedEffective));
            Assert.AreEqual(0f, rejectedCore);
            Assert.AreEqual(0f, rejectedCollision);
            Assert.AreEqual(0f, rejectedSemantic);
            Assert.AreEqual(0f, rejectedEffective);
        }

        [Test]
        public void InvalidInputsAndCapBelowBaselineAreRejected()
        {
            float core, collision, semantic, effective;
            Assert.IsFalse(Derive(-1f, out core, out collision, out semantic, out effective));
            Assert.IsFalse(Derive(float.NaN, out core, out collision, out semantic, out effective));
            Assert.IsFalse(Derive(float.PositiveInfinity, out core, out collision, out semantic, out effective));
            Assert.IsFalse(Derive(float.NegativeInfinity, out core, out collision, out semantic, out effective));
            Assert.AreEqual(0f, core);
            Assert.AreEqual(0f, collision);
            Assert.AreEqual(0f, semantic);
            Assert.AreEqual(0f, effective);

            // The Python cases for a bool and for 10**1000 have no C# form:
            // bool does not convert to float, and 10**1000 is not a float
            // literal. What remains is the parameter conflict the model
            // defines: a 0.01 m cap cannot hold the 0.620 m baseline sphere,
            // so it is rejected instead of shrinking the baseline.
            Assert.IsFalse(SizeModelMath.TryDerive(0f, 1f, 1f, 0.01f, 1f, 1f,
                out core, out collision, out semantic, out effective));
            // Every parameter is required to be positive.
            Assert.IsFalse(SizeModelMath.TryDerive(0f, 0f, 1f, 3f, 1f, 1f,
                out core, out collision, out semantic, out effective));
            Assert.IsFalse(SizeModelMath.TryDerive(0f, 1f, 0f, 3f, 1f, 1f,
                out core, out collision, out semantic, out effective));
            Assert.IsFalse(SizeModelMath.TryDerive(0f, 1f, 1f, 3f, 0f, 1f,
                out core, out collision, out semantic, out effective));
            Assert.IsFalse(SizeModelMath.TryDerive(0f, 1f, 1f, 3f, 1f, 0f,
                out core, out collision, out semantic, out effective));
        }

        [Test]
        public void ReportedDimensionsRemainFiniteAtTheCap()
        {
            // 1e12 confirmed units are capped too, and the three legacy
            // readouts are the cap, 0.8 x cap and 1.5 x the cap diameter.
            float core, collision, semantic, effective;
            Assert.IsTrue(Derive(1e12f, out core, out collision, out semantic, out effective));
            AssertFinite(core, "core radius");
            AssertFinite(collision, "collision radius");
            AssertFinite(semantic, "semantic diameter");
            AssertFinite(effective, "effective volume");
            Assert.AreEqual(3f, core);
            Assert.AreEqual(2.4f, collision, 1e-5f);
            Assert.AreEqual(9f, semantic, 1e-5f);
        }

        private static bool Derive(float confirmedVolumeUnits, out float coreRadiusWorldM,
            out float collisionRadiusWorldM, out float semanticDiameter, out float effectiveVolumeUnits)
        {
            return SizeModelMath.TryDerive(confirmedVolumeUnits, Baseline, CubicMetersPerUnit,
                MaxCoreRadius, CollisionRatio, SemanticRatio,
                out coreRadiusWorldM, out collisionRadiusWorldM, out semanticDiameter,
                out effectiveVolumeUnits);
        }

        private static void AssertFinite(float value, string label)
        {
            Assert.IsFalse(float.IsNaN(value), label + " is NaN");
            Assert.IsFalse(float.IsInfinity(value), label + " is infinite");
        }
    }
}
