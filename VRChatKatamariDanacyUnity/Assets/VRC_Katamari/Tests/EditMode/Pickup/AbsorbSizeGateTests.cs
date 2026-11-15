using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the relative absorb size gate of Docs/OPEN_DECISIONS.md (l): an item is
    /// absorbable when its AABB volume `a*b*c` is at most the ball's own volume
    /// `(pi/6) * D^3` divided by the absorb volume ratio, bounded above by the
    /// serialized volume ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The oracle is the original's rule as researched in
    /// Docs/Research/KatamariBallRules/ABSORB_SIZE_RATIO.md:
    /// `V_ball &gt;= 10 * V_item`, i.e. `a*b*c &lt;= (pi/6) * D^3 / 10`. That note also
    /// derives the equivalent form `D &gt;= 2.154 * d_eq` with
    /// `d_eq = (6abc/pi)^(1/3)`, which is why a CUBE needs a ball 2.673 times its edge
    /// (2.154 * 1.2407) - the numbers below use that fact rather than a copy of the code.
    /// </para>
    /// <para>
    /// Deliberate divergence, recorded so nobody "fixes" the runtime back: the offline
    /// round model still carries the retired linear gate
    /// (`pickup_diameter_m &lt;= ball_diameter_m * absorb_size_ratio`,
    /// Docs/Design/Gameplay/round_model.py) and Docs/Design/PickupAbsorb/pickup_absorb.py
    /// still compares the longest bound. Neither is the runtime contract; this file pins
    /// the runtime. Equality cases are only asserted where the limit is an exact argument
    /// (see <see cref="BlockingPropMathTests"/>), because the relative limit carries a pi
    /// and a division and cannot land on a binary-exact value: the product-relative cases
    /// here stay about 1% away from the limit, which is many orders above float noise.
    /// </para>
    /// <para>
    /// Every case puts the item at the ball's centre, so the centre distance is 0 and
    /// the contact gate can never mask the size gate; the growth cap is set far above
    /// every ball used here for the same reason.
    /// </para>
    /// </remarks>
    public class AbsorbSizeGateTests
    {
        private const float ContactRadiusM = 0f;

        // Far above any collision radius used here, so the growth-cap gate (which is
        // inclusive at the cap) cannot mask the size gate.
        private const float MaxGrowthRadiusM = 5f;

        // The ceiling the motor serializes after decision (l): 512 m^3, an 8 m cube. It is
        // a safety bound, not the gameplay rule, and it replaces the retired 8 m LENGTH
        // ceiling (a length bound next to a volume gate truncates at 8 m of longest edge).
        private const float MotorCeilingM3 = 512f;

        // The original's volume ratio (decision (l)).
        private const float Ratio = 10f;

        private static readonly string[] Allowed = new string[] { "stone" };
        private static readonly Vector3 AtBall = Vector3.zero;

        private static AbsorbOutcome EvaluateAtBall(float ballDiameterM, float ratio,
            Vector3 bounds, float ceilingM3)
        {
            // The collision radius is half the semantic diameter, the motor's default
            // relationship (collisionToCoreRadiusRatio = 1, semantic = 2 * core).
            return PickupAbsorbMath.EvaluateAbsorbWithBallSize(true, "stone", Allowed,
                Allowed.Length, bounds, AtBall, AtBall, ballDiameterM * 0.5f, ContactRadiusM,
                ceilingM3, MaxGrowthRadiusM, ballDiameterM, ratio);
        }

        [Test]
        public void AnItemJustUnderTheRelativeLimitIsAbsorbableAndJustOverIsRefused()
        {
            // 0.50 m ball: ball volume (pi/6) * 0.125 = 0.065450 m^3, over ratio 10 =
            // 0.006545 m^3 limit.
            // 0.09 * 0.09 * 0.80 = 0.006480 m^3 (99.0% of the limit) is absorbable.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.5f, Ratio,
                new Vector3(0.09f, 0.09f, 0.80f), MotorCeilingM3));
            // 0.09 * 0.09 * 0.81 = 0.006561 m^3 (100.2%) is refused with the same outcome
            // code the retired absolute gate used.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.5f, Ratio,
                new Vector3(0.09f, 0.09f, 0.81f), MotorCeilingM3));

            // The limit is a quotient of the ball's volume, not the diameter: 0.80 m ball
            // gives 0.268083 / 5 = 0.053617 m^3, so 0.4 * 0.4 * 0.33 = 0.052800 passes and
            // 0.4 * 0.4 * 0.34 = 0.054400 does not. Same ball, half the ratio, twice the
            // limit - and neither item has a side longer than the ball's diameter, which
            // the retired linear gate would also have accepted.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.8f, Ratio * 0.5f,
                new Vector3(0.4f, 0.4f, 0.33f), MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.8f,
                Ratio * 0.5f, new Vector3(0.4f, 0.4f, 0.34f), MotorCeilingM3));
        }

        [Test]
        public void SmallBallRefusesTheChunkyCubeAHugeBallAccepts()
        {
            // A cube needs a ball 2.673 times its edge under the volume gate (2.154 times
            // its volume-equivalent diameter, which for a cube of side a is 1.2407 a).
            // A 0.30 m cube is 0.027 m^3, so:
            //   0.30 m ball: limit 0.001414 m^3 -> refused (and the retired linear gate
            //                also refused it, for the wrong reason: 0.30 > 0.30 * 1.0)
            //   0.80 m ball: limit 0.026812 m^3 -> still refused, 0.9% short
            //   0.82 m ball: limit 0.028895 m^3 -> accepted
            //   1.25 m ball: limit 0.102265 m^3 -> accepted
            // The retired linear gate accepted this cube at a 1.00 m ball (1.0 <= 1.0),
            // i.e. it was ~2.7x too generous for chunky props.
            Vector3 cube = new Vector3(0.30f, 0.30f, 0.30f);
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                EvaluateAtBall(0.30f, Ratio, cube, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                EvaluateAtBall(0.80f, Ratio, cube, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.Absorb,
                EvaluateAtBall(0.82f, Ratio, cube, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.Absorb,
                EvaluateAtBall(1.25f, Ratio, cube, MotorCeilingM3));
        }

        [Test]
        public void TheCeilingRefusesAnItemEvenForAHugeBall()
        {
            // A 4 m ball would allow 3.351 m^3 by the ratio alone; the 1 m^3 ceiling in
            // this case still refuses 1.01 m^3. The ceiling is inclusive at equality, and
            // 1 * 1 * 1 = 1 m^3 is exact in binary, so this is the one equality case the
            // relative path can state exactly.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(4f, Ratio,
                new Vector3(1f, 1f, 1.01f), 1f));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(4f, Ratio,
                new Vector3(1f, 1f, 1f), 1f));
        }

        [Test]
        public void RelativeLimitIsTheMinimumOfTheCeilingAndTheProduct()
        {
            float limit;
            // The full ball (2.20 m) is under the motor's 512 m^3 ceiling, so growth is
            // never clipped by the ceiling in normal play:
            // (pi/6) * 2.20^3 / 10 = 5.5753 / 10 = 0.5575 m^3.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(2.20f, Ratio, 512f,
                out limit));
            Assert.AreEqual(0.5575f, limit, 1e-3f);
            // A ceiling below the product wins.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(2.20f, Ratio, 1f,
                out limit));
            Assert.AreEqual(1f, limit, 1e-6f);
            // The start ball (0.29974 m) gives (pi/6) * 0.026930 = 0.014100 m^3, over the
            // ratio 10 = 0.001410 m^3; the same number the growth model calls the start
            // ball's volume (3.4390244 * 0.0041).
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(0.29974f, Ratio, 512f,
                out limit));
            Assert.AreEqual(0.001410f, limit, 1e-6f);
            // The ratio divides: the same ball with ratio 2 allows five times as much.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(0.5f, 2f, 512f,
                out limit));
            Assert.AreEqual(0.0327250f, limit, 1e-6f);
            // A zero diameter is accepted and yields a zero limit: nothing with any volume
            // passes. It is a broken size reading, but not an invalid input.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(0f, Ratio, 512f,
                out limit));
            Assert.AreEqual(0f, limit, 1e-9f);
        }

        [Test]
        public void NonPositiveOrNonFiniteRatioIsInvalidInput()
        {
            Vector3 bounds = new Vector3(0.1f, 0.1f, 0.1f);
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, 0f, bounds, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, -0.5f, bounds, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, float.NaN, bounds, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, float.PositiveInfinity, bounds, MotorCeilingM3));

            // A ratio of zero would silently disable every absorb, so it is refused
            // with the existing invalid-input outcome rather than clamped to a limit of
            // zero. The round model's constructor rejects a non-positive ratio too.
            float limit;
            Assert.IsFalse(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(1f, 0f, 512f,
                out limit));
            Assert.AreEqual(0f, limit, 1e-9f);
            Assert.IsFalse(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(1f, -1f, 512f,
                out limit));

            // The diameter and the ceiling are validated on the same path.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(float.NaN, Ratio, bounds, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(-1f, Ratio, bounds, MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, Ratio, bounds, float.PositiveInfinity));
            // A cube that overflows to infinity is refused, not treated as "allow
            // everything": float.MaxValue cubed is not finite.
            Assert.IsFalse(PickupAbsorbMath.TryComputeAbsorbVolumeLimit(float.MaxValue,
                Ratio, 512f, out limit));
        }

        [Test]
        public void TheTwoCubesDifferInVolumeTwoHundredAndSixteenTimes()
        {
            // The example the decision record carries. Edges 0.05 m and 0.30 m differ by
            // 6x; volumes differ by 6^3 = 216x:
            //   0.05^3 = 0.000125 m^3
            //   0.30^3 = 0.027000 m^3     0.027 / 0.000125 = 216
            // No linear ratio can express that: the retired field could only answer "6".
            Vector3 small = new Vector3(0.05f, 0.05f, 0.05f);
            Vector3 large = new Vector3(0.30f, 0.30f, 0.30f);
            float smallVolume;
            float largeVolume;
            Assert.IsTrue(PickupAbsorbMath.TryComputeItemVolume(small, out smallVolume));
            Assert.IsTrue(PickupAbsorbMath.TryComputeItemVolume(large, out largeVolume));
            Assert.AreEqual(0.000125f, smallVolume, 1e-7f);
            Assert.AreEqual(0.027f, largeVolume, 1e-7f);
            Assert.AreEqual(216f, largeVolume / smallVolume, 1e-4f);

            // At a 0.30 m ball the limit is 0.001414 m^3 - a 0.112 m cube - so the 0.05 m
            // one is absorbed and the 0.30 m one is not.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.30f, Ratio, small,
                MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.30f, Ratio,
                large, MotorCeilingM3));

            // At 1.00 m the limit is (pi/6) / 10 = 0.052360 m^3, and both pass: 216x the
            // volume is still 0.0514 m^3 below a limit that grew 10.5x.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(1.00f, Ratio, small,
                MotorCeilingM3));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(1.00f, Ratio, large,
                MotorCeilingM3));
        }

        [Test]
        public void ASlenderPropPassesTheVolumeGateThatALinearGateRefused()
        {
            // A baseball bat: 0.02 x 0.02 x 0.85 m = 0.000340 m^3. Under the retired
            // linear gate (ratio 1.0) a 0.50 m ball refused it, because 0.85 > 0.50; a
            // bat-shaped prop needed a 0.85 m ball. The volume gate lets a 0.50 m ball
            // take it: limit 0.006545 m^3, and 0.000340 is 5% of that.
            Vector3 bat = new Vector3(0.02f, 0.02f, 0.85f);
            float batVolume;
            Assert.IsTrue(PickupAbsorbMath.TryComputeItemVolume(bat, out batVolume));
            Assert.AreEqual(0.000340f, batVolume, 1e-7f);
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.50f, Ratio, bat,
                MotorCeilingM3));

            // The item sits at the ball's centre in these cases, so this is the size gate
            // talking. The bat's bounding-sphere radius is sqrt(0.01^2 + 0.01^2 + 0.425^2)
            // = 0.42523 m, i.e. its reach is 0.25 + 0.42523 = 0.67523 m - so it is also
            // in contact at 0.6 m from centre, which is what the reach gate alone would
            // have said. The size gate was the only thing standing in the way.
            float itemRadius;
            float reach;
            Assert.IsTrue(PickupAbsorbMath.TryComputeReach(bat, 0.25f, ContactRadiusM,
                out itemRadius, out reach));
            Assert.AreEqual(0.42523f, itemRadius, 1e-4f);
            Assert.AreEqual(0.67523f, reach, 1e-4f);
            Assert.AreEqual(AbsorbOutcome.Absorb, PickupAbsorbMath.EvaluateAbsorb(true,
                "stone", Allowed, Allowed.Length, bat, new Vector3(0.6f, 0f, 0f), AtBall,
                0.25f, ContactRadiusM, 0.006545f, MaxGrowthRadiusM));

            // Counterweight so the case is not read as "thin props are free": a 0.85 m
            // CUBE is 0.614 m^3 and needs a ball of 2.673 * 0.85 = 2.27 m, i.e. beyond the
            // project's 2.20 m envelope.
            Vector3 block = new Vector3(0.85f, 0.85f, 0.85f);
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(1.25f, Ratio,
                block, MotorCeilingM3));
        }

        [Test]
        public void TheGainGateStillRunsAfterTheRelativeGate()
        {
            Vector3 item = new Vector3(0.1f, 0.1f, 0.1f);
            // Too large for the ball AND worth less than one unit: the geometry verdict
            // wins, exactly as in the absolute-gate entry point. 0.45^3 = 0.091125 m^3
            // against a 0.30 m ball's 0.001414 m^3 limit.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, new Vector3(0.45f, 0.45f, 0.45f), AtBall, AtBall, 0.15f,
                    ContactRadiusM, MotorCeilingM3, MaxGrowthRadiusM, 0.30f, Ratio, 0.3f));
            // Under the volume limit (0.001 m^3 against 0.001414) and worth two units.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    MotorCeilingM3, MaxGrowthRadiusM, 0.30f, Ratio, 2f));
            // In reach, under the limit, but below one unit: the gain gate reports it.
            Assert.AreEqual(AbsorbOutcome.RejectNoGain,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    MotorCeilingM3, MaxGrowthRadiusM, 0.30f, Ratio, 0.3f));
            // An invalid ratio outranks every later gate, including the gain gate.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    MotorCeilingM3, MaxGrowthRadiusM, 0.30f, 0f, 0.3f));
        }
    }
}
