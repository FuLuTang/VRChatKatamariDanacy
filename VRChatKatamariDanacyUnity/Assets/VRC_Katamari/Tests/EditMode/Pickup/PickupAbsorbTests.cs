using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Economy;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/pickup_absorb/test_pickup_absorb.py, plus the
    /// distinguishing property this port exists for: the contact gate uses the collision
    /// radius, never the semantic diameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four of the Python suite's five tests are ported. The missing one,
    /// `test_uniform_grid_nearby_and_bounds`, exercises the model's UniformGrid candidate
    /// container, which is a host-side broadphase rather than a value decision, and which
    /// Udon cannot back with a List/Dictionary; the module is deliberately pure statics
    /// over value inputs and does not port that container (see the module report). The
    /// absorb gates it would feed are covered here.
    /// </para>
    /// <para>
    /// The Python model's `Ball` keeps its own confirmed and predicted dicts and its
    /// `AuthorityLedger` its own claim store. This port keeps exactly one ledger
    /// (<see cref="GrowthLedgerMath"/>), so the single-count, replay and race cases are
    /// driven through it: a claim changes no score until a commit exists, and a losing
    /// claimant gains nothing. That is a deliberate difference from the model's demo
    /// `Ball.reconcile`, which summed every claim in the authority snapshot without
    /// looking at the claim's player; see the report.
    /// </para>
    /// <para>
    /// Expected values are hand calculations: itemRadius = sqrt(3) * halfExtent for a cube,
    /// reach = collisionRadius + contactRadius + itemRadius, and radius = min(cap,
    /// base + growthPerValue * confirmedValue). Lengths are metres, volumes are cubic
    /// metres, values are ledger units. The size limit this file passes to
    /// <see cref="PickupAbsorbMath.EvaluateAbsorb"/> is an ALREADY-RESOLVED VOLUME in cubic
    /// metres (Docs/OPEN_DECISIONS.md (l)), so "too large" here means `x*y*z` over that
    /// number. <c>ReachTableMatchesTheWorkedExample</c> is the one case whose expected
    /// values come from a document instead: the worked reach table in
    /// Docs/Design/PickupAbsorb/README.md, so a formula change has to disagree with the
    /// doc rather than with a copy of itself.
    /// </para>
    /// </remarks>
    public class PickupAbsorbTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void AbsorbGatesFollowTheModel()
        {
            string[] allowed = { "stone" };

            // Reference case: 0.2 m cube at 0.5 m from the ball, 0.3 m collision radius,
            // 0.1 m contact margin. itemRadius = 0.1 * sqrt(3) = 0.17320508, so the reach
            // is 0.57320508 and 0.5 m is contact.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                new Vector3(0.2f, 0.2f, 0.2f), new Vector3(0.5f, 0f, 0f), Vector3.zero,
                0.3f, 0.1f, 0.5f, 5f));

            // AABB volume 1 * 1 * 1 = 1 m^3 is over the 0.5 m^3 volume limit.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, Evaluate(true, "stone", allowed,
                Vector3.one, Vector3.zero, Vector3.zero, 1f, 0.1f, 0.5f, 5f));

            // Kind is outside the allowed set.
            Assert.AreEqual(AbsorbOutcome.RejectKindNotAllowed, Evaluate(true, "wood", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 1f, 0.1f, 0.5f, 5f));

            // The ball has already reached the growth cap (5 >= 5).
            Assert.AreEqual(AbsorbOutcome.RejectBallAtGrowthCap, Evaluate(true, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 5f, 0.1f, 0.5f, 5f));

            // Not absorbable wins over every later gate, as in the model's first condition.
            Assert.AreEqual(AbsorbOutcome.RejectNotAbsorbable, Evaluate(false, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 0f, 0f, 5f, 5f));

            // 2 m away with a 0.2 m reach is not contact.
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                Vector3.zero, new Vector3(2f, 0f, 0f), Vector3.zero, 0.2f, 0f, 1f, 5f));
        }

        [Test]
        public void BoundaryComparisonsMatchTheModel()
        {
            string[] allowed = { "stone" };

            // The size gate is strict: an AABB volume exactly at the limit is allowed.
            // 0.5 * 1 * 1 = 0.5 m^3 is exact in binary, and so is 0.5001 * 1 * 1 being
            // above it; a volume of 0 (a flat prop) is legal and always inside.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                new Vector3(0.5f, 1f, 1f), Vector3.zero, Vector3.zero, 0f, 0f, 0.5f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, Evaluate(true, "stone", allowed,
                new Vector3(0.5001f, 1f, 1f), Vector3.zero, Vector3.zero, 0f, 0f, 0.5f, 5f));
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                new Vector3(0.5f, 0f, 0f), Vector3.zero, Vector3.zero, 0f, 0f, 0.5f, 5f));

            // The growth cap is inclusive: a radius exactly at the cap is refused.
            Assert.AreEqual(AbsorbOutcome.RejectBallAtGrowthCap, Evaluate(true, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 5f, 0f, 1f, 5f));

            // The contact test is inclusive: distance == reach is contact, one tenth of a
            // millimetre more is not.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                Vector3.zero, new Vector3(0.6f, 0f, 0f), Vector3.zero, 0.5f, 0.1f, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                Vector3.zero, new Vector3(0.6001f, 0f, 0f), Vector3.zero, 0.5f, 0.1f, 1f, 5f));
        }

        [Test]
        public void InvalidGeometryIsRejected()
        {
            string[] allowed = { "stone" };
            float radius;

            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                new Vector3(-0.1f, 0f, 0f), Vector3.zero, Vector3.zero, 0.2f, 0f, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                Vector3.one, new Vector3(float.NaN, 0f, 0f), Vector3.zero, 0.2f, 0f, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                Vector3.one, Vector3.zero, Vector3.zero, -0.2f, 0f, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                Vector3.one, Vector3.zero, Vector3.zero, 0.2f, 0f, float.PositiveInfinity, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, null, allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 0.2f, 0f, 1f, 5f));
            // An AABB volume that overflows float is an unrepresentable input, not a large
            // item: 1e20 * 1e20 * 1e20 is infinity, and comparing it would silently absorb.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                new Vector3(1e20f, 1e20f, 1e20f), Vector3.zero, Vector3.zero, 0.2f, 0f, 1e30f,
                5f));

            // Growth parameters follow the model's constructor checks.
            Assert.IsFalse(PickupAbsorbMath.TryComputeGrowthRadius(1f, 0.1f, 0.5f, 1f,
                out radius)); // base above cap
            Assert.IsFalse(PickupAbsorbMath.TryComputeGrowthRadius(-1f, 0.1f, 2f, 1f,
                out radius));
            Assert.IsFalse(PickupAbsorbMath.TryComputeGrowthRadius(1f, -0.1f, 2f, 1f,
                out radius));
            Assert.IsFalse(PickupAbsorbMath.TryComputeGrowthRadius(1f, 0.1f, 2f, -1f,
                out radius));
            Assert.IsFalse(PickupAbsorbMath.TryComputeGrowthRadius(float.NaN, 0.1f, 2f, 1f,
                out radius));
            Assert.AreEqual(0f, radius, Tolerance);
        }

        [Test]
        public void AbsorbValueBecomesExactlyOneLedgerGain()
        {
            long gain;
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbValueToLedgerGain(2f, out gain));
            Assert.AreEqual(2, gain);

            // Neither model defines a rounding rule, so a fractional value is refused
            // rather than rounded into a total. A rejected call zeroes its out value.
            long rejected;
            Assert.IsFalse(PickupAbsorbMath.TryAbsorbValueToLedgerGain(2.5f, out rejected));
            Assert.IsFalse(PickupAbsorbMath.TryAbsorbValueToLedgerGain(-1f, out rejected));
            Assert.IsFalse(PickupAbsorbMath.TryAbsorbValueToLedgerGain(float.NaN, out rejected));
            // The ledger's gain is a 64-bit quantity (Docs/OPEN_DECISIONS.md (r) step A),
            // so the representable-range bound moved from 2^31 to 2^63. The same probe
            // value (2147483648) is a real gain now, and the refusal sits at the value the
            // ledger genuinely cannot hold - stronger than before, not weaker: it pins
            // both sides of the new bound.
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbValueToLedgerGain(3000000000f, out rejected));
            Assert.AreEqual(3000000000L, rejected);
            Assert.IsFalse(PickupAbsorbMath.TryAbsorbValueToLedgerGain(9223372036854775808f,
                out rejected));
            Assert.AreEqual(0, rejected);

            LedgerTestHost ledger = PlayingLedger();
            Assert.IsTrue(ledger.SubmitClaim("rock", 1, "p1", 0, 1));
            // A replayed request id is one claim, not two.
            Assert.IsTrue(ledger.SubmitClaim("rock", 1, "p1", 0, 1));
            Assert.AreEqual(1, ledger.ClaimCount);
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, ledger.Resolve("rock", 1, gain));
            Assert.AreEqual(2, ledger.Confirmed("p1"));

            // The same committed claim replayed at a replica cannot count twice.
            LedgerTestHost replica = new LedgerTestHost();
            Assert.IsTrue(replica.Join("p1"));
            Assert.IsTrue(replica.ApplyCommit("rock", 1, "p1", 1, gain));
            Assert.IsFalse(replica.ApplyCommit("rock", 1, "p1", 1, gain));
            Assert.AreEqual(2, replica.Confirmed("p1"));

            // base 1 m, 0.1 m per unit, cap 2 m: 1 + 0.1 * 2 = 1.2 m.
            Assert.AreEqual(1.2f, RadiusFor(2f, 1f, 0.1f, 2f), Tolerance);
        }

        [Test]
        public void RaceWinnerGrowsLoserDoesNot()
        {
            LedgerTestHost ledger = PlayingLedger();
            Assert.IsTrue(ledger.Predict("p1", "rock", 1, 2));
            Assert.IsTrue(ledger.Predict("p2", "rock", 1, 2));
            // p1 claims at frame 0, p2 at frame 1: the earliest frame wins.
            Assert.IsTrue(ledger.SubmitClaim("rock", 1, "p1", 0, 1));
            Assert.IsTrue(ledger.SubmitClaim("rock", 1, "p2", 1, 1));

            Assert.AreEqual(0, ledger.Confirmed("p1"));
            Assert.AreEqual(0, ledger.Confirmed("p2"));

            int commit = ledger.Resolve("rock", 1, 2);
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, commit);
            Assert.AreEqual("p1", ledger.CommitWinners[commit]);
            Assert.AreEqual(2, ledger.Confirmed("p1"));
            Assert.AreEqual(0, ledger.Confirmed("p2"));
            // Both local predictions are gone: the authoritative value replaced the
            // winner's and the loser's was rolled back.
            Assert.AreEqual(0, ledger.PredictionCount);

            // Re-resolution cannot let the losing claimant in: one item, one commit.
            Assert.AreEqual(commit, ledger.Resolve("rock", 1, 2));
            Assert.AreEqual(0, ledger.Confirmed("p2"));
            Assert.AreEqual(1, ledger.CommitCount);

            Assert.AreEqual(1.2f, RadiusFor(2f, 1f, 0.1f, 3f), Tolerance);
            Assert.AreEqual(1f, RadiusFor(0f, 1f, 0.1f, 3f), Tolerance);
        }

        [Test]
        public void RadiusIsMonotonicAndCapped()
        {
            LedgerTestHost ledger = PlayingLedger();
            float[] radii = new float[5];
            int count = 0;
            radii[count] = RadiusFor(ledger.Confirmed("p1"), 1f, 0.5f, 2f);
            count = count + 1;

            for (int index = 1; index <= 4; index++)
            {
                string item = "item" + index;
                Assert.IsTrue(ledger.SubmitClaim(item, 1, "p1", index, 1));
                Assert.AreNotEqual(GrowthLedgerMath.NoRow, ledger.Resolve(item, 1, 1));
                radii[count] = RadiusFor(ledger.Confirmed("p1"), 1f, 0.5f, 2f);
                count = count + 1;
            }

            // Confirmed values 0, 1, 2, 3, 4 with base 1 and 0.5 per unit: 1, 1.5, 2, 2, 2.
            Assert.AreEqual(1f, radii[0], Tolerance);
            Assert.AreEqual(1.5f, radii[1], Tolerance);
            Assert.AreEqual(2f, radii[2], Tolerance);
            Assert.AreEqual(2f, radii[3], Tolerance);
            Assert.AreEqual(2f, radii[4], Tolerance);
            for (int index = 1; index < count; index++)
            {
                Assert.GreaterOrEqual(radii[index], radii[index - 1], "radius must not shrink");
            }
            Assert.AreEqual(2f, radii[count - 1], Tolerance);
        }

        [Test]
        public void ReachUsesCollisionRadiusNotSemanticDiameter()
        {
            string[] allowed = { "stone" };
            // A 1.0 m semantic diameter implies a 0.5 m radius, which would reach an item
            // 0.4 m away. The gate is given the 0.2 m collision radius instead, so the
            // item is out of reach; the layer has no diameter parameter to pass by
            // mistake.
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                Vector3.zero, new Vector3(0.4f, 0f, 0f), Vector3.zero, 0.2f, 0f, 1f, 5f));
            // The same geometry is contact once the collision radius itself is 0.4 m.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                Vector3.zero, new Vector3(0.4f, 0f, 0f), Vector3.zero, 0.4f, 0f, 1f, 5f));
        }

        [Test]
        public void ReachTableMatchesTheWorkedExample()
        {
            // Source of the expected values: Docs/Design/PickupAbsorb/README.md, section
            // "reach vs 物件尺寸（工作算例）". The ball sits at its calibrated start diameter
            // (0.29974 m), so the core radius and the collision radius are both 0.14987 m
            // (baselineVolumeUnits / cubicMetersPerVolumeUnit through SizeModelMath);
            // contactRadiusWorldM is 0, and the item is a cube of side s, so
            // itemRadius = sqrt(3) * s / 2 and reach = 0.14987 + contact + itemRadius.
            // The table is printed to the millimetre, so the tolerance is 1 mm - this test
            // pins the DOCUMENT's numbers, not a formula copied back out of the code.
            float collisionRadius = 0.14987f;
            float[] sides = { 0.10f, 0.25f, 0.35f, 0.50f };
            float[] expectedItemRadius = { 0.0866f, 0.2165f, 0.3031f, 0.4330f };
            float[] expectedReach = { 0.236f, 0.366f, 0.453f, 0.583f };
            float[] expectedReachWithContact = { 0.336f, 0.466f, 0.553f, 0.683f };

            for (int i = 0; i < sides.Length; i++)
            {
                Vector3 bounds = new Vector3(sides[i], sides[i], sides[i]);
                float itemRadius;
                float reach;
                Assert.IsTrue(PickupAbsorbMath.TryComputeReach(bounds, collisionRadius, 0f,
                    out itemRadius, out reach), "a cube and a non-negative radius are legal");
                Assert.AreEqual(expectedItemRadius[i], itemRadius, 1e-4f,
                    "itemRadius for a cube of side " + sides[i]);
                Assert.AreEqual(expectedReach[i], reach, 1e-3f,
                    "reach for a cube of side " + sides[i]);

                float contactReach;
                Assert.IsTrue(PickupAbsorbMath.TryComputeReach(bounds, collisionRadius, 0.10f,
                    out itemRadius, out contactReach));
                Assert.AreEqual(expectedReachWithContact[i], contactReach, 1e-3f,
                    "reach with a 0.10 m contact margin for a cube of side " + sides[i]);
            }
        }

        [Test]
        public void TheGateComparesAgainstTryComputeReach()
        {
            string[] allowed = { "stone" };
            // A non-cube: a hand copy of the three half extents is exactly where a second
            // formula drifts, so the gate's threshold is built from the helper's own
            // number. itemRadius = sqrt(0.2^2 + 0.15^2 + 0.1^2) = sqrt(0.0725).
            Vector3 bounds = new Vector3(0.4f, 0.3f, 0.2f);
            float itemRadius;
            float reach;
            Assert.IsTrue(PickupAbsorbMath.TryComputeReach(bounds, 0.2f, 0.05f,
                out itemRadius, out reach));
            Assert.AreEqual(0.26925824f, itemRadius, 1e-6f);
            Assert.AreEqual(0.51925824f, reach, 1e-6f);

            // One millimetre inside is contact, one millimetre outside is not; the exact
            // equality case is pinned by BoundaryComparisonsMatchTheModel with numbers
            // that survive float rounding, because the README warns not to sit on 1 ulp.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed, bounds,
                new Vector3(reach - 0.001f, 0f, 0f), Vector3.zero, 0.2f, 0.05f, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                bounds, new Vector3(reach + 0.001f, 0f, 0f), Vector3.zero, 0.2f, 0.05f, 1f,
                5f));
        }

        [Test]
        public void TryComputeReachRefusesGeometryTheGateCallsInvalid()
        {
            float itemRadius;
            float reach;

            // Every input the gate reports as RejectInvalidInput before it has a reach:
            // a negative extent, a non-finite extent, a non-finite radius or contact
            // margin, and a negative radius.
            Assert.IsFalse(PickupAbsorbMath.TryComputeReach(new Vector3(-0.1f, 0f, 0f), 0.2f,
                0f, out itemRadius, out reach));
            Assert.IsFalse(PickupAbsorbMath.TryComputeReach(new Vector3(float.NaN, 1f, 1f),
                0.2f, 0f, out itemRadius, out reach));
            Assert.IsFalse(PickupAbsorbMath.TryComputeReach(Vector3.one, float.NaN, 0f,
                out itemRadius, out reach));
            Assert.IsFalse(PickupAbsorbMath.TryComputeReach(Vector3.one, 0.2f,
                float.PositiveInfinity, out itemRadius, out reach));
            Assert.IsFalse(PickupAbsorbMath.TryComputeReach(Vector3.one, -0.2f, 0f,
                out itemRadius, out reach));

            // A refusal zeroes both out values, so a caller cannot report half a number.
            Assert.AreEqual(0f, itemRadius, 1e-6f);
            Assert.AreEqual(0f, reach, 1e-6f);
        }

        private static AbsorbOutcome Evaluate(bool absorbable, string kind, string[] allowed,
            Vector3 bounds, Vector3 itemPosition, Vector3 ballPosition, float collisionRadius,
            float contactRadius, float maxItemVolumeWorldM3, float maxGrowthRadius)
        {
            return PickupAbsorbMath.EvaluateAbsorb(absorbable, kind, allowed, allowed.Length,
                bounds, itemPosition, ballPosition, collisionRadius, contactRadius,
                maxItemVolumeWorldM3, maxGrowthRadius);
        }

        private static float RadiusFor(float confirmedValue, float baseRadius, float growth,
            float cap)
        {
            float radius;
            Assert.IsTrue(PickupAbsorbMath.TryComputeGrowthRadius(baseRadius, growth, cap,
                confirmedValue, out radius), "growth parameters were rejected");
            return radius;
        }

        private static LedgerTestHost PlayingLedger()
        {
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsTrue(host.Join("p1"));
            Assert.IsTrue(host.Join("p2"));
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f));
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 1f));
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 2f));
            return host;
        }
    }
}
