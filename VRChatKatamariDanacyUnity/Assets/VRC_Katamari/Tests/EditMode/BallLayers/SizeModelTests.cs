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

        // Calibrated sizing for the inverse tests, matching
        // KatamariBallMotor's serialized defaults (3.4390244 baseline units,
        // 0.0041 m^3 per unit, 1.1 m display cap, 1.0 ratios): the baseline is a
        // 0.29974 m semantic diameter and the cap is 2.20 m, reached at 1356
        // confirmed units. The decided round runs 0.30 m -> 2.20 m, so this is
        // the configuration the inverse has to round-trip. Same calibration as
        // Docs/Design/Gameplay/round_model.py.
        private const float CalibratedBaseline = 3.4390244f;
        private const float CalibratedCubicMetersPerUnit = 0.0041f;
        private const float CalibratedMaxCoreRadius = 1.1f;
        private const float CalibratedCollisionRatio = 1f;
        private const float CalibratedSemanticRatio = 1f;

        // Round-trip tolerance in metres. The inverse is algebraic, so only
        // float rounding is left: the worst error measured over the ten
        // round-trip diameters is 1.9e-7 m, about one unit in the last place at
        // 2.2 m. 1e-5 m is ~50x that margin, to absorb a different libm's
        // log/exp in the player build, and is still 0.01 mm - far below any
        // gameplay meaning (the smallest decided band gap is 4 cm).
        private const float RoundTripToleranceM = 1e-5f;

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

        // ---- Inverse map: volume units for a requested semantic diameter ----
        // The reference oracle is round_model.py units_for_diameter (clamped to
        // [start, max], 1/3 exponent). The diameters below are the decided
        // round's ends and band edges, not values the mapping produced.

        [Test]
        public void InverseRoundTripsThroughTheForwardMapAtTenDiameters()
        {
            // 0.30 (start) and 2.20 (max), the tier band edges 0.55 / 0.95 /
            // 1.45, and five interior points.
            float[] diameters = new float[]
            {
                0.30f, 0.40f, 0.55f, 0.70f, 0.95f,
                1.10f, 1.45f, 1.70f, 1.95f, 2.20f
            };

            float previousUnits = -1f;
            for (int i = 0; i < diameters.Length; i++)
            {
                float requested = diameters[i];
                float units;
                Assert.IsTrue(TryUnits(requested, out units), "inverse rejected " + requested + " m");
                Assert.GreaterOrEqual(units, 0f);

                float core, collision, semantic, effective;
                Assert.IsTrue(DeriveCalibrated(units, out core, out collision, out semantic,
                    out effective));
                AssertFinite(core, "core radius");
                AssertFinite(semantic, "semantic diameter");

                Assert.AreEqual(requested, semantic, RoundTripToleranceM,
                    "forward(inverse(" + requested + " m))");
                Assert.Greater(units, previousUnits, "units must grow with diameter at " + requested);
                previousUnits = units;
            }

            // The 0.30 m end sits just above this configuration's own baseline
            // (0.29974 m), so the lowest round-trip point is a small positive
            // volume rather than the clamped zero. That 0.26 mm offset is why
            // the caller's "start" and the model's baseline are not the same
            // literal.
            float baselineCore, baselineCollision, baselineSemantic, baselineEffective;
            Assert.IsTrue(DeriveCalibrated(0f, out baselineCore, out baselineCollision,
                out baselineSemantic, out baselineEffective));
            Assert.Less(baselineSemantic, 0.30f);
            Assert.Greater(baselineSemantic, 0.29f);

            // Both ends clamp like the reference model: below the baseline the
            // answer is zero confirmed units, above the cap it is the cap's
            // volume and never more.
            float belowBaseline;
            Assert.IsTrue(TryUnits(0.20f, out belowBaseline));
            Assert.AreEqual(0f, belowBaseline);
            float atCap, aboveCap;
            Assert.IsTrue(TryUnits(2.20f, out atCap));
            Assert.IsTrue(TryUnits(2.60f, out aboveCap));
            Assert.AreEqual(atCap, aboveCap);

            // The largest finite diameter is the extreme-input guard: the log
            // domain clamps it to the cap instead of overflowing, and the answer
            // is finite.
            float hugeUnits;
            Assert.IsTrue(TryUnits(float.MaxValue, out hugeUnits));
            Assert.AreEqual(atCap, hugeUnits);
            AssertFinite(hugeUnits, "units for float.MaxValue m");

            // The cap's volume is the sphere at the 1.1 m cap radius, 4/3*pi*r^3
            // / 0.0041 m^3 per unit, minus the 3.4390244 baseline units.
            float expectedCapUnits = (4f * Mathf.PI / 3f) * 1.331f / CalibratedCubicMetersPerUnit
                - CalibratedBaseline;
            Assert.AreEqual(expectedCapUnits, atCap, 1e-3f);
        }

        [Test]
        public void InverseIsStrictlyMonotonicAcrossTheMappedRange()
        {
            float startCore, startCollision, startSemantic, startEffective;
            Assert.IsTrue(DeriveCalibrated(0f, out startCore, out startCollision, out startSemantic,
                out startEffective));

            // 0.01 m steps from the model's own baseline (0.29974 m) to the cap:
            // a strict increase at every step means no two diameters share a
            // volume, so a banked residual can never land on a plateau where
            // "smaller" and "bigger" cost the same.
            float previous = -1f;
            int steps = 0;
            for (int i = 0; i <= 190; i++)
            {
                float diameter = startSemantic + 0.01f * i;
                float units;
                Assert.IsTrue(TryUnits(diameter, out units), "inverse rejected " + diameter + " m");
                Assert.Greater(units, previous, "units are not strictly increasing at " + diameter + " m");
                previous = units;
                steps++;
            }
            Assert.AreEqual(191, steps);
            Assert.Less(startSemantic + 0.01f * 190, 2.20f);
        }

        [Test]
        public void InverseRejectsZeroAndNegativeDiameter()
        {
            // A diameter of zero would be a zero-volume ball, which the forward
            // map never reports, so the inverse must refuse it rather than
            // answer with a negative volume.
            float units;
            Assert.IsFalse(TryUnits(0f, out units));
            Assert.AreEqual(0f, units);
            Assert.IsFalse(TryUnits(-1e-6f, out units));
            Assert.AreEqual(0f, units);
            Assert.IsFalse(TryUnits(-0.5f, out units));
            Assert.AreEqual(0f, units);
            Assert.IsFalse(TryUnits(-float.MaxValue, out units));
            Assert.AreEqual(0f, units);
        }

        [Test]
        public void InverseRejectsNonFiniteDiameterAndParameters()
        {
            float units;
            Assert.IsFalse(TryUnits(float.NaN, out units));
            Assert.AreEqual(0f, units);
            Assert.IsFalse(TryUnits(float.PositiveInfinity, out units));
            Assert.AreEqual(0f, units);
            Assert.IsFalse(TryUnits(float.NegativeInfinity, out units));
            Assert.AreEqual(0f, units);

            // The four parameters get the same treatment as the forward map,
            // including the cap-below-baseline conflict: a 0.01 m cap cannot
            // hold the 0.1499 m baseline radius, so the pair is rejected.
            Assert.IsFalse(InverseWith(1f, 0f, CalibratedCubicMetersPerUnit,
                CalibratedMaxCoreRadius, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, -1f, CalibratedCubicMetersPerUnit,
                CalibratedMaxCoreRadius, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, 0f,
                CalibratedMaxCoreRadius, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, -0.0141f,
                CalibratedMaxCoreRadius, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, CalibratedCubicMetersPerUnit,
                0f, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, CalibratedCubicMetersPerUnit,
                CalibratedMaxCoreRadius, 0f, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, CalibratedCubicMetersPerUnit,
                CalibratedMaxCoreRadius, -1f, out units));
            Assert.IsFalse(InverseWith(1f, float.NaN, CalibratedCubicMetersPerUnit,
                CalibratedMaxCoreRadius, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, float.PositiveInfinity,
                CalibratedMaxCoreRadius, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, CalibratedCubicMetersPerUnit,
                float.NaN, CalibratedSemanticRatio, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, CalibratedCubicMetersPerUnit,
                CalibratedMaxCoreRadius, float.NaN, out units));
            Assert.IsFalse(InverseWith(1f, CalibratedBaseline, CalibratedCubicMetersPerUnit,
                0.01f, CalibratedSemanticRatio, out units));
            Assert.AreEqual(0f, units);
        }

        [Test]
        public void BankingResidualAppliedInDiameterSpaceEndsNearSixtyCentimetres()
        {
            // Docs/OPEN_DECISIONS.md (a), the decided rule:
            //   post = max(start, start + (pre - start) * residual), residual
            //   = 0.25, start = 0.30 m.
            // 1.49 m is the highest peak the round model reached in
            // four-player runs (Gameplay README 4.3), so it is the case the
            // rule has to get right.
            const float startDiameter = 0.30f;
            const float preBankDiameter = 1.49f;
            const float residual = 0.25f;

            float expected = startDiameter + (preBankDiameter - startDiameter) * residual;
            Assert.AreEqual(0.5975f, expected, 1e-6f);

            // The rule is in diameter space: price the post-bank diameter with
            // the inverse, and the forward map must report it back.
            float units;
            Assert.IsTrue(TryUnits(expected, out units));
            float core, collision, semantic, effective;
            Assert.IsTrue(DeriveCalibrated(units, out core, out collision, out semantic, out effective));
            Assert.AreEqual(expected, semantic, RoundTripToleranceM);

            // ...which is the "about 0.60 m" the decision describes, not the
            // 0.95 m a volume-space residual leaves.
            Assert.AreEqual(0.60f, semantic, 0.01f);

            // The volume-space residual, for contrast: 1.49 m is 121.84 units,
            // and 1 + (121.84 - 1) * 0.25 = 31.21 units is still a 0.9537 m
            // ball. That ≈0.36 m gap is what the missing inverse cost.
            float preBankUnits;
            Assert.IsTrue(TryUnits(preBankDiameter, out preBankUnits));
            float volumeSpaceUnits = CalibratedBaseline + (preBankUnits - CalibratedBaseline) * residual;
            float volumeSpaceCore, volumeSpaceCollision, volumeSpaceSemantic, volumeSpaceEffective;
            Assert.IsTrue(DeriveCalibrated(volumeSpaceUnits, out volumeSpaceCore,
                out volumeSpaceCollision, out volumeSpaceSemantic, out volumeSpaceEffective));
            Assert.AreEqual(0.9537f, volumeSpaceSemantic, 1e-3f);
            Assert.Greater(volumeSpaceSemantic, semantic + 0.3f);
        }

        private static bool TryUnits(float semanticDiameter, out float confirmedVolumeUnits)
        {
            return SizeModelMath.TryVolumeUnitsForSemanticDiameter(semanticDiameter,
                CalibratedBaseline, CalibratedCubicMetersPerUnit, CalibratedMaxCoreRadius,
                CalibratedSemanticRatio, out confirmedVolumeUnits);
        }

        private static bool InverseWith(float semanticDiameter, float baselineVolumeUnits,
            float cubicMetersPerVolumeUnit, float maxCoreRadiusWorldM,
            float semanticDiameterPerCoreDiameter, out float confirmedVolumeUnits)
        {
            return SizeModelMath.TryVolumeUnitsForSemanticDiameter(semanticDiameter,
                baselineVolumeUnits, cubicMetersPerVolumeUnit, maxCoreRadiusWorldM,
                semanticDiameterPerCoreDiameter, out confirmedVolumeUnits);
        }

        private static bool DeriveCalibrated(float confirmedVolumeUnits, out float coreRadiusWorldM,
            out float collisionRadiusWorldM, out float semanticDiameter, out float effectiveVolumeUnits)
        {
            return SizeModelMath.TryDerive(confirmedVolumeUnits, CalibratedBaseline,
                CalibratedCubicMetersPerUnit, CalibratedMaxCoreRadius, CalibratedCollisionRatio,
                CalibratedSemanticRatio, out coreRadiusWorldM, out collisionRadiusWorldM,
                out semanticDiameter, out effectiveVolumeUnits);
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
