using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the ORIGINAL absorb size gate (owner decision 2026-11-16): an item's volume is
    /// `V = k * a*b*c`, its required ball is `Req = cbrt(60*V/pi)` FLOORED to whole
    /// millimetres, and it is absorbable iff the ball's whole-millimetre diameter is at
    /// least Req.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The oracle is the original's researched rule, not a copy of the code:
    /// `V_ball &gt;= 10 * V_item` (raw_growth_mechanism §2; PARITY_AUDIT G07), the item
    /// volume `V = k * bbox` with default k = 0.70 (G13), and the requirement "rounded down
    /// to the nearest millimetre" (G08). Every number below is re-derived from that formula
    /// in the test; the float32 values were checked independently so the millimetre floors
    /// are not a tautology.
    /// </para>
    /// <para>
    /// Two facts make the expected numbers what they are, and each has a case below: k
    /// multiplies the VOLUME (so Req scales with k^(1/3), and a cube needs a ball 2.3734
    /// times its edge at k=0.70), and the floor is to whole millimetres (so the accepted
    /// ball is up to 1 mm SMALLER than the exact 10x requirement - the original's own
    /// "generally a little bit less than 10 times bigger").
    /// </para>
    /// <para>
    /// Every case puts the item at the ball's centre, so the centre distance is 0 and the
    /// contact gate can never mask the size gate; the growth cap is set far above every
    /// ball used here for the same reason.
    /// </para>
    /// </remarks>
    public class AbsorbSizeGateTests
    {
        private const float ContactRadiusM = 0f;

        // Far above any collision radius used here, so the growth-cap gate (which is
        // inclusive at the cap) cannot mask the size gate. The helper passes the ball's
        // radius as half its diameter, so the ceiling must clear the LARGEST ball below:
        // the 100 m ball's 50 m radius is exactly the old ceiling and the cap gate fired
        // (RejectBallAtGrowthCap) before the size gate could answer. 1000 m has 20x headroom.
        private const float MaxGrowthRadiusM = 1000f;

        // The original's default per-item volume factor (PARITY_AUDIT G13).
        private const float K = PickupAbsorbMath.DefaultVolumeK;

        private static readonly string[] Allowed = new string[] { "stone" };
        private static readonly Vector3 AtBall = Vector3.zero;

        private static AbsorbOutcome EvaluateAtBall(float ballDiameterM, float volumeK,
            Vector3 bounds)
        {
            // The collision radius is half the semantic diameter, the motor's default
            // relationship (collisionToCoreRadiusRatio = 1, semantic = 2 * core).
            return PickupAbsorbMath.EvaluateAbsorb(true, "stone", Allowed, Allowed.Length,
                bounds, AtBall, AtBall, ballDiameterM * 0.5f, ContactRadiusM, volumeK,
                ballDiameterM, MaxGrowthRadiusM);
        }

        [Test]
        public void ReqIsTheOriginalFormulaAtTheDefaultFactor()
        {
            // apple's authored box: V = 0.70 * 0.08 * 0.0789 * 0.078 = 0.000344635 m^3,
            // Req = cbrt(60 * V / pi) = 0.187408 m = 187.408 mm -> 187 mm.
            Vector3 apple = new Vector3(0.08f, 0.0789f, 0.078f);
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(apple, K, out reqMm));
            Assert.AreEqual(187f, reqMm);

            // The same three checks stated against the formula, so the golden above cannot
            // silently follow a changed implementation.
            float expectedM = Mathf.Pow(60f * K * 0.08f * 0.0789f * 0.078f / Mathf.PI, 1f / 3f);
            Assert.AreEqual(0.187408f, expectedM, 1e-6f);
            Assert.AreEqual(187f, Mathf.Floor(expectedM * 1000f + 1e-3f), 1e-3f);

            // Four more types, one per shape class the authors treat differently:
            //  - 0.30 m cube: 0.0189 m^3 -> 712.01 mm -> 712 mm  (2.3734 * edge)
            //  - 0.05 m cube: 0.00008750 m^3 -> 118.67 mm -> 118 mm
            //  - 0.10 m cube: 0.0007000 m^3 -> 237.34 mm -> 237 mm
            //  - watermelon box 0.30 x 0.22 x 0.24: 0.011088 m^3 -> 596.05 mm -> 596 mm
            Assert.AreEqual(712f, ReqMm(new Vector3(0.30f, 0.30f, 0.30f), K));
            Assert.AreEqual(118f, ReqMm(new Vector3(0.05f, 0.05f, 0.05f), K));
            Assert.AreEqual(237f, ReqMm(new Vector3(0.10f, 0.10f, 0.10f), K));
            Assert.AreEqual(596f, ReqMm(new Vector3(0.30f, 0.22f, 0.24f), K));
        }

        [Test]
        public void AnItemExactlyAtReqIsAbsorbableAndOneMillimetreLessIsNot()
        {
            // A 0.10 m cube at k=0.70 needs 237 mm, so the boundary is the only thing
            // distinguishing these two balls: 237 mm absorbs, 236 mm does not.
            Vector3 cube = new Vector3(0.10f, 0.10f, 0.10f);
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(cube, K, out reqMm));
            Assert.AreEqual(237f, reqMm);

            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.237f, K, cube));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.236f, K, cube));

            // Equality is absorbable on the predicate too, i.e. the two answers are the same
            // function and not two thresholds that merely agree here.
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(cube, K, 0.237f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(cube, K, 0.236f));
        }

        [Test]
        public void TheEpsilonRepairsAnExactMillimetreBoundary()
        {
            // Choose the third side so the EXACT requirement is 300 mm:
            // c = pi * 0.3^3 / (60 * 0.70 * 0.10 * 0.10). In float32 the product lands at
            // 299.99998 mm, i.e. just below the integer; without the boundary guard the
            // floor would be 299 and the item would need a 300 mm ball it mathematically
            // does not. The guard (1e-3 mm) lifts it back to 300 and the 300 mm ball absorbs.
            float c = Mathf.PI * 0.027f / (60f * K * 0.01f);
            Vector3 bounds = new Vector3(0.10f, 0.10f, c);
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(bounds, K, out reqMm));
            Assert.AreEqual(300f, reqMm);
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.300f, K, bounds));

            // The guard is a floor guard, not a "round up" rule: one millimetre below the
            // boundary still refuses, so it cannot promote an item that is genuinely
            // smaller.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.299f, K, bounds));

            // And it is the same guard on the ball side: 0.3 m * 1000 is 300.0000119 in
            // float32, which floors to 300 either way, so assert the exact integer form.
            float ballMm;
            Assert.IsTrue(PickupAbsorbMath.TryFloorMillimetres(0.3f, out ballMm));
            Assert.AreEqual(300f, ballMm);
        }

        [Test]
        public void KMultipliesTheVolumeNotTheLength()
        {
            // Same watermelon box, three factors: Req scales with k^(1/3), relative to the
            // 0.70 default (so k=2.0 is 2.857x the volume, 1.4194x the length: 596.05 *
            // 1.4194 = 845.8 mm; k=0.5 is 0.7143x the volume, 0.8939x the length = 532.8 mm).
            Vector3 box = new Vector3(0.30f, 0.22f, 0.24f);
            Assert.AreEqual(596f, ReqMm(box, 0.70f));
            Assert.AreEqual(845f, ReqMm(box, 2.0f));
            Assert.AreEqual(532f, ReqMm(box, 0.5f));

            // A factor is not a length: the same 0.70 m ball absorbs the default-k item and
            // refuses the k=2 one, even though neither box edge changed.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.70f, 0.70f, box));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.70f, 2.0f, box));

            // The original's thin-sheet end of the range: k=16 makes a 0.5 x 0.3 x 0.002 m
            // sheet (0.0003 m^3 bbox) worth 0.0048 m^3, i.e. a 450 mm requirement - a sheet
            // is priced by its material, not by its thickness.
            Vector3 sheet = new Vector3(0.5f, 0.002f, 0.3f);
            Assert.AreEqual(450f, ReqMm(sheet, 16f));
        }

        [Test]
        public void TheSanityCeilingIsNotAGameplayGate()
        {
            // The retired 512 m^3 cap on the item's bbox refused anything larger, whatever
            // the ball. The replacement is a 14,137 m^3 guard on the ORIGINAL volume
            // (k * bbox): steps C+D raised it because the ONE global cap is 30 m, whose
            // largest legal item is (pi/6*30^3)/10 = 1,413.7 m^3, and the guard keeps a 10x
            // margin over that. It is a broken-data bound, not a gameplay gate.
            Assert.AreEqual(14137f, PickupAbsorbMath.MaxItemVolumeSanityWorldM3);
            Assert.AreEqual(10f, PickupAbsorbMath.AbsorbVolumeRatio);

            // 640 m^3 of bbox at k=0.70 is 448 m^3 of item volume: under the guard, and a
            // 21 m ball (20,453 mm requirement) takes it. Under the retired 512 m^3 bbox cap
            // the same item was allowed too, so this asserts the guard did not tighten.
            Vector3 big = new Vector3(8f, 8f, 10f);
            Assert.AreEqual(20453f, ReqMm(big, 0.70f));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(21f, 0.70f, big));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(20f, 0.70f, big));

            // Above the guard the answer is "not absorbable", never "absorbable" and never
            // an infinity: 27,000 m^3 of bbox at k=0.70 is 18,900 m^3 of item volume, which
            // is past 14,137.
            Vector3 absurd = new Vector3(30f, 30f, 30f);
            float reqMm;
            Assert.IsFalse(PickupAbsorbMath.TryComputePickupReqDiameterMm(absurd, K, out reqMm));
            Assert.AreEqual(0f, reqMm, 1e-9f);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(absurd, K, 100f));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(100f, K, absurd));
        }

        [Test]
        public void IllegalKOrDiameterIsInvalidInputNotAClamp()
        {
            Vector3 bounds = new Vector3(0.1f, 0.1f, 0.1f);
            // 0 would price every item at zero volume; a value past the ceiling is a typo
            // like 70 for 0.70. Both are refused with the existing invalid-input outcome.
            foreach (float badK in new float[] { 0f, -0.5f, 32.5f, 70f, float.NaN,
                         float.PositiveInfinity })
            {
                Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                    EvaluateAtBall(1f, badK, bounds), "k=" + badK);
            }

            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(float.NaN, K, bounds));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(-1f, K, bounds));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(float.PositiveInfinity, K, bounds));

            // The raw products are validated too: a cube that overflows float is refused,
            // not treated as "allow everything".
            float volume;
            float reqMm;
            Assert.IsFalse(PickupAbsorbMath.TryComputeItemVolume(
                new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), out volume));
            Assert.AreEqual(0f, volume, 1e-9f);
            Assert.IsFalse(PickupAbsorbMath.TryComputeOriginalItemVolume(
                new Vector3(0.1f, 0.1f, 0.1f), float.NaN, out volume));
            Assert.IsFalse(PickupAbsorbMath.TryComputePickupReqDiameterMm(
                new Vector3(-0.1f, 0.1f, 0.1f), K, out reqMm));
        }

        [Test]
        public void TheBlockingPredicateAndTheGateAgreeOnEveryCase()
        {
            // One predicate, one implementation: for each case the gate's size refusal is
            // exactly the blocking predicate's answer. This is the property the host's wall
            // and floor classification relies on.
            Vector3[] cases =
            {
                new Vector3(0.10f, 0.10f, 0.10f),
                new Vector3(0.30f, 0.30f, 0.30f),
                new Vector3(0.30f, 0.22f, 0.24f),
                new Vector3(8f, 8f, 10f),
                new Vector3(10f, 10f, 20f),
            };
            float[] balls = { 0.236f, 0.30f, 0.596f, 0.712f, 21f, 100f };
            for (int i = 0; i < cases.Length; i++)
            {
                for (int j = 0; j < balls.Length; j++)
                {
                    bool beyond = PickupAbsorbMath.IsBeyondAbsorbLimit(cases[i], K, balls[j]);
                    AbsorbOutcome outcome = EvaluateAtBall(balls[j], K, cases[i]);
                    Assert.AreEqual(beyond,
                        outcome == AbsorbOutcome.RejectItemTooLarge,
                        "bounds " + i + " ball " + balls[j]);
                    if (!beyond)
                    {
                        Assert.AreEqual(AbsorbOutcome.Absorb, outcome);
                    }
                }
            }

            // Illegal input answers "beyond" (blocking), matching the gate's refusal, so bad
            // data can never be neither blocking nor absorbable.
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(float.NaN, 1f, 1f), K, 1f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.1f, 0.1f, 0.1f), 0f, 1f));
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(
                new Vector3(0.1f, 0.1f, 0.1f), K, float.NaN));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, 0f, new Vector3(0.1f, 0.1f, 0.1f)));
        }

        [Test]
        public void TheGateProbeReportsTheSameTwoNumbersThePredicateCompares()
        {
            Vector3 bounds = new Vector3(0.30f, 0.22f, 0.24f);
            float reqMm;
            float ballMm;
            bool within;
            Assert.IsTrue(PickupAbsorbMath.TryResolveAbsorbGate(bounds, K, 0.700f, out reqMm,
                out ballMm, out within));
            Assert.AreEqual(596f, reqMm);
            Assert.AreEqual(700f, ballMm);
            Assert.IsTrue(within);
            Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, K, 0.700f));

            Assert.IsTrue(PickupAbsorbMath.TryResolveAbsorbGate(bounds, K, 0.596f, out reqMm,
                out ballMm, out within));
            Assert.AreEqual(596f, reqMm);
            Assert.AreEqual(596f, ballMm);
            Assert.IsTrue(within);

            Assert.IsTrue(PickupAbsorbMath.TryResolveAbsorbGate(bounds, K, 0.595f, out reqMm,
                out ballMm, out within));
            Assert.IsFalse(within);
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, K, 0.595f));

            // An illegal input resolves nothing, and the predicate answers "beyond".
            Assert.IsFalse(PickupAbsorbMath.TryResolveAbsorbGate(bounds, 0f, 1f, out reqMm,
                out ballMm, out within));
            Assert.IsFalse(within);
        }

        [Test]
        public void TheTwoCubesDifferInVolumeTwoHundredAndSixteenTimes()
        {
            // The example the decision record carries: edges 0.05 m and 0.30 m differ 6x,
            // volumes 216x, and the gate's Req differs by 6x too (118 mm vs 712 mm) - which
            // is exactly why a linear ratio cannot express this gate.
            Vector3 small = new Vector3(0.05f, 0.05f, 0.05f);
            Vector3 large = new Vector3(0.30f, 0.30f, 0.30f);
            float smallVolume;
            float largeVolume;
            Assert.IsTrue(PickupAbsorbMath.TryComputeItemVolume(small, out smallVolume));
            Assert.IsTrue(PickupAbsorbMath.TryComputeItemVolume(large, out largeVolume));
            Assert.AreEqual(0.000125f, smallVolume, 1e-7f);
            Assert.AreEqual(0.027f, largeVolume, 1e-7f);
            Assert.AreEqual(216f, largeVolume / smallVolume, 1e-4f);

            Assert.AreEqual(118f, ReqMm(small, K));
            Assert.AreEqual(712f, ReqMm(large, K));

            // At the project's 0.30 m start ball the small cube is food and the large one is
            // an obstacle; at 1.00 m both are food (712 mm < 1000 mm).
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.30f, K, small));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.30f, K, large));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(1.00f, K, small));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(1.00f, K, large));
        }

        [Test]
        public void ASlenderPropIsPricedByVolumeNotByItsLongestEdge()
        {
            // A 0.02 x 0.02 x 0.85 m bat: V = 0.70 * 0.00034 = 0.000238 m^3, Req = 165 mm.
            // A linear "longest edge vs ball diameter" gate would have needed an 0.85 m ball.
            Vector3 bat = new Vector3(0.02f, 0.02f, 0.85f);
            float batVolume;
            Assert.IsTrue(PickupAbsorbMath.TryComputeItemVolume(bat, out batVolume));
            Assert.AreEqual(0.00034f, batVolume, 1e-7f);
            Assert.AreEqual(165f, ReqMm(bat, K));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.50f, K, bat));

            // Reach is a separate quantity and still uses the bounding sphere: the bat's
            // radius is sqrt(0.01^2 + 0.01^2 + 0.425^2) = 0.42523 m, so at 0.6 m from the
            // ball's centre the gate's reach (0.25 + 0.42523 = 0.67523 m) is satisfied and
            // the size gate is what lets it through.
            float itemRadius;
            float reach;
            Assert.IsTrue(PickupAbsorbMath.TryComputeReach(bat, 0.25f, ContactRadiusM,
                out itemRadius, out reach));
            Assert.AreEqual(0.42523f, itemRadius, 1e-4f);
            Assert.AreEqual(0.67523f, reach, 1e-4f);
            Assert.AreEqual(AbsorbOutcome.Absorb, PickupAbsorbMath.EvaluateAbsorb(true,
                "stone", Allowed, Allowed.Length, bat, new Vector3(0.6f, 0f, 0f), AtBall,
                0.25f, ContactRadiusM, K, 0.5f, MaxGrowthRadiusM));

            // A 0.85 m CUBE is 0.614 m^3 of bbox, 0.430 m^3 at k=0.70, Req = 2.017 m - i.e.
            // inside the project's 2.20 m envelope but far past a 1.25 m ball.
            Vector3 block = new Vector3(0.85f, 0.85f, 0.85f);
            Assert.AreEqual(2017f, ReqMm(block, K));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(1.25f, K, block));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(2.02f, K, block));
        }

        [Test]
        public void TheGainGateStillRunsAfterTheSizeGate()
        {
            Vector3 item = new Vector3(0.1f, 0.1f, 0.1f);
            // Too large for the ball AND no gain: the geometry verdict wins, so a
            // "no gain" reading can never explain away a size refusal.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.EvaluateAbsorbWithValue(true, "stone", Allowed,
                    Allowed.Length, new Vector3(0.45f, 0.45f, 0.45f), AtBall, AtBall, 0.15f,
                    ContactRadiusM, K, 0.30f, MaxGrowthRadiusM, 0L));
            // Inside the gate (237 mm requirement vs a 300 mm ball) and a real gain.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                PickupAbsorbMath.EvaluateAbsorbWithValue(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    K, 0.30f, MaxGrowthRadiusM, 2L));
            // In reach, inside the gate, but no growth: the gain gate reports it.
            Assert.AreEqual(AbsorbOutcome.RejectNoGain,
                PickupAbsorbMath.EvaluateAbsorbWithValue(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    K, 0.30f, MaxGrowthRadiusM, 0L));
            // An illegal k outranks every later gate, including the gain gate.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                PickupAbsorbMath.EvaluateAbsorbWithValue(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    0f, 0.30f, MaxGrowthRadiusM, 0L));
            // A gain above int.MaxValue is a real gain (the ledger is 64-bit); this is the
            // step that would have been refused before steps C/D.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                PickupAbsorbMath.EvaluateAbsorbWithValue(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    K, 0.30f, MaxGrowthRadiusM, 3000000000L));
        }

        private static float ReqMm(Vector3 bounds, float volumeK)
        {
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(bounds, volumeK,
                out reqMm));
            return reqMm;
        }
    }
}
