using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the relative absorb size gate of Docs/OPEN_DECISIONS.md (h): an item is
    /// absorbable when its largest full extent is at most the ball's semantic diameter
    /// times the absorb size ratio, bounded above by the serialized ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The oracle is the round model, `can_absorb` in
    /// Docs/Design/Gameplay/round_model.py:493-502:
    /// `pickup_diameter_m &lt;= ball_diameter_m * absorb_size_ratio + 1e-9`. The
    /// boundary is inclusive; this port adds no epsilon, so the equality cases below use
    /// values that are exact in binary (0.5 * 1, 0.8 * 0.5) and the "just past" cases
    /// stay far above float noise. Do not tighten them to one ulp: the runtime gate has
    /// no epsilon and a last-bit difference is allowed to flip.
    /// </para>
    /// <para>
    /// Which quantity is compared: the runtime compares the item's `bounds` largest
    /// FULL extent, while the model compares the item's declared `pickup_diameter_m`.
    /// The two are not interchangeable (decision (h)); the bounds-radius case in
    /// <see cref="SmallBallRefusesTheItemAHugeBallAccepts"/> exists to keep the
    /// comparison pointed at the extent and not at the bounding-sphere radius that the
    /// reach test uses.
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

        // The ceiling the motor serializes after decision (h). Raised from the retired
        // absolute 1 m gate; it is a safety bound, not the gameplay rule.
        private const float MotorCeilingM = 8f;

        private static readonly string[] Allowed = new string[] { "stone" };
        private static readonly Vector3 AtBall = Vector3.zero;

        private static AbsorbOutcome EvaluateAtBall(float ballDiameterM, float ratio,
            Vector3 bounds, float ceilingM)
        {
            // The collision radius is half the semantic diameter, the motor's default
            // relationship (collisionToCoreRadiusRatio = 1, semantic = 2 * core).
            return PickupAbsorbMath.EvaluateAbsorbWithBallSize(true, "stone", Allowed,
                Allowed.Length, bounds, AtBall, AtBall, ballDiameterM * 0.5f, ContactRadiusM,
                ceilingM, MaxGrowthRadiusM, ballDiameterM, ratio);
        }

        [Test]
        public void AnExtentExactlyAtTheRelativeLimitIsAbsorbable()
        {
            // 0.5 m diameter * ratio 1 = 0.5 m limit, and 0.5 is exact in binary.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.5f, 1f,
                new Vector3(0.5f, 0.05f, 0.05f), MotorCeilingM));

            // Just below the limit is absorbable, just above is refused with the same
            // outcome code the absolute gate used.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.5f, 1f,
                new Vector3(0.4999f, 0.05f, 0.05f), MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.5f, 1f,
                new Vector3(0.5001f, 0.05f, 0.05f), MotorCeilingM));

            // The limit is a product, not the diameter: 0.8 m * 0.5 = 0.4 m exactly, so
            // 0.4 m passes at equality and 0.45 m does not.
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(0.8f, 0.5f,
                new Vector3(0.4f, 0.05f, 0.05f), MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(0.8f, 0.5f,
                new Vector3(0.45f, 0.05f, 0.05f), MotorCeilingM));
        }

        [Test]
        public void SmallBallRefusesTheItemAHugeBallAccepts()
        {
            // Decision (h) headline case: a 0.30 m ball cannot take a 0.45 m item
            // (0.45 > 0.30 * 1.0), and a 1.25 m ball can (0.45 <= 1.25 * 1.0). Under the
            // retired absolute 1 m gate both accepted, so this is the behaviour change.
            Vector3 bounds = new Vector3(0.45f, 0.05f, 0.05f);
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                EvaluateAtBall(0.30f, 1f, bounds, MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.Absorb,
                EvaluateAtBall(1.25f, 1f, bounds, MotorCeilingM));

            // The compared quantity is the largest full extent, not the bounding-sphere
            // radius: sqrt(0.225^2 + 0.025^2 + 0.025^2) = 0.2268 m < 0.30 m, yet the
            // item is refused by the small ball because its longest side is 0.45 m.
            float boundingRadius = Mathf.Sqrt(0.225f * 0.225f + 0.025f * 0.025f
                + 0.025f * 0.025f);
            Assert.Less(boundingRadius, 0.30f,
                "the reach radius must be below the limit for this case to be meaningful");
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                EvaluateAtBall(0.30f, 1f, bounds, MotorCeilingM));

            // The same item is also reachable at the bigger ball, so the refusal above
            // is the size gate and not the contact gate.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                EvaluateAtBall(1.25f, 1f, bounds, MotorCeilingM));
        }

        [Test]
        public void TheCeilingRefusesAnItemEvenForAHugeBall()
        {
            // A 4 m ball would allow 4 m items by the ratio alone; the 1 m ceiling in
            // this case still refuses 1.2 m. The ceiling is inclusive at equality.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, EvaluateAtBall(4f, 1f,
                new Vector3(1.2f, 0.1f, 0.1f), 1f));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(4f, 1f,
                new Vector3(1f, 0.1f, 0.1f), 1f));
        }

        [Test]
        public void RelativeLimitIsTheMinimumOfTheCeilingAndTheProduct()
        {
            float limit;
            // The full ball (2.20 m) is under the motor's 8 m ceiling, so growth is
            // never clipped by the ceiling in normal play.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(2.20f, 1f, 8f, out limit));
            Assert.AreEqual(2.20f, limit, 1e-6f);
            // A ceiling below the product wins.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(2.20f, 1f, 1f, out limit));
            Assert.AreEqual(1f, limit, 1e-6f);
            // The start ball's limit is its own diameter, and the ratio scales it.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(0.29974f, 1f, 8f, out limit));
            Assert.AreEqual(0.29974f, limit, 1e-6f);
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(0.5f, 2f, 8f, out limit));
            Assert.AreEqual(1f, limit, 1e-6f);
            // A zero diameter is accepted and yields a zero limit: nothing with any
            // extent passes. It is a broken size reading, but not an invalid input.
            Assert.IsTrue(PickupAbsorbMath.TryComputeAbsorbSizeLimit(0f, 1f, 8f, out limit));
            Assert.AreEqual(0f, limit, 1e-9f);
        }

        [Test]
        public void NonPositiveOrNonFiniteRatioIsInvalidInput()
        {
            Vector3 bounds = new Vector3(0.1f, 0.1f, 0.1f);
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, 0f, bounds, MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, -0.5f, bounds, MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, float.NaN, bounds, MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, float.PositiveInfinity, bounds, MotorCeilingM));

            // A ratio of zero would silently disable every absorb, so it is refused
            // with the existing invalid-input outcome rather than clamped to a limit of
            // zero (the round model's constructor rejects a non-positive ratio too).
            float limit;
            Assert.IsFalse(PickupAbsorbMath.TryComputeAbsorbSizeLimit(1f, 0f, 8f, out limit));
            Assert.AreEqual(0f, limit, 1e-9f);
            Assert.IsFalse(PickupAbsorbMath.TryComputeAbsorbSizeLimit(1f, -1f, 8f, out limit));

            // The diameter and the ceiling are validated on the same path.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(float.NaN, 1f, bounds, MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(-1f, 1f, bounds, MotorCeilingM));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(1f, 1f, bounds, float.PositiveInfinity));
            // A product that overflows to infinity is refused, not treated as "allow
            // everything": float.MaxValue * 2 is not finite.
            Assert.IsFalse(PickupAbsorbMath.TryComputeAbsorbSizeLimit(float.MaxValue, 2f, 8f,
                out limit));
        }

        [Test]
        public void TheGainGateStillRunsAfterTheRelativeGate()
        {
            Vector3 item = new Vector3(0.1f, 0.1f, 0.1f);
            // Too large for the ball AND worth less than one unit: the geometry verdict
            // wins, exactly as in the absolute-gate entry point.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, new Vector3(0.45f, 0.05f, 0.05f), AtBall, AtBall, 0.15f,
                    ContactRadiusM, MotorCeilingM, MaxGrowthRadiusM, 0.30f, 1f, 0.3f));
            // In reach and over the relative limit is still a size refusal.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    MotorCeilingM, MaxGrowthRadiusM, 0.30f, 1f, 2f));
            // In reach, under the limit, but below one unit: the gain gate reports it.
            Assert.AreEqual(AbsorbOutcome.RejectNoGain,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    MotorCeilingM, MaxGrowthRadiusM, 0.30f, 1f, 0.3f));
            // An invalid ratio outranks every later gate, including the gain gate.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(true, "stone", Allowed,
                    Allowed.Length, item, AtBall, AtBall, 0.15f, ContactRadiusM,
                    MotorCeilingM, MaxGrowthRadiusM, 0.30f, 0f, 0.3f));
        }
    }
}
