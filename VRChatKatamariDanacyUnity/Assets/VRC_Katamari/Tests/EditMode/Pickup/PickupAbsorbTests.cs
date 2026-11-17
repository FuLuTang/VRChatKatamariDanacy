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
    /// metres, values are ledger units. The size gate this file exercises is the original's
    /// whole-millimetre rule (owner decision 2026-11-16): the helper passes the item's
    /// per-item factor k and the ball's semantic DIAMETER, and "too large" means
    /// `floor(1000*cbrt(60*k*a*b*c/pi)) > floor(1000*D)`. <c>ContactTableMatchesTheWorkedExample</c> is the one case whose expected
    /// values come from a document instead: the worked contact table in
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

            // Reference case: 0.2 m cube 0.45 m from the ball along x, 0.3 m collision radius
            // (a 0.60 m semantic ball), 0.1 m contact margin. The cube's near face is 0.35 m
            // from the ball's centre and the contact limit is 0.4 m, so it is touched; the
            // cube's Req is 474 mm, inside the 600 mm ball. (The old bounding-sphere reach,
            // 0.573 m, would have taken it from 0.55 m too; its face is 0.45 m away, which
            // is not contact.)
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                new Vector3(0.2f, 0.2f, 0.2f), new Vector3(0.45f, 0f, 0f), Vector3.zero,
                0.3f, 0.1f, 0.60f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                new Vector3(0.2f, 0.2f, 0.2f), new Vector3(0.55f, 0f, 0f), Vector3.zero,
                0.3f, 0.1f, 0.60f, 5f));

            // A 1 m cube at k=0.70 needs 2,373 mm, so a 2.00 m ball (2000 mm) refuses it.
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, Evaluate(true, "stone", allowed,
                Vector3.one, Vector3.zero, Vector3.zero, 1f, 0.1f, 2.00f, 5f));

            // Kind is outside the allowed set.
            Assert.AreEqual(AbsorbOutcome.RejectKindNotAllowed, Evaluate(true, "wood", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 1f, 0.1f, 2.00f, 5f));

            // The ball has already reached the growth cap (5 >= 5).
            Assert.AreEqual(AbsorbOutcome.RejectBallAtGrowthCap, Evaluate(true, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 5f, 0.1f, 10f, 5f));

            // Not absorbable wins over every later gate, as in the model's first condition.
            Assert.AreEqual(AbsorbOutcome.RejectNotAbsorbable, Evaluate(false, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 0f, 0f, 1f, 5f));

            // 2 m away with a 0.2 m reach is not contact.
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                Vector3.zero, new Vector3(2f, 0f, 0f), Vector3.zero, 0.2f, 0f, 0.4f, 5f));
        }

        [Test]
        public void BoundaryComparisonsMatchTheModel()
        {
            string[] allowed = { "stone" };

            // The size gate is inclusive on the ORIGINAL quantity: Req == ball diameter is
            // absorbable, one millimetre less is not. 0.5 * 1 * 1 m at k=0.70 is
            // V = 0.35 m^3, so Req = 1,883 mm and a 1.883 m ball just passes.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                new Vector3(0.5f, 1f, 1f), Vector3.zero, Vector3.zero, 0f, 0f, 1.883f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge, Evaluate(true, "stone", allowed,
                new Vector3(0.5f, 1f, 1f), Vector3.zero, Vector3.zero, 0f, 0f, 1.882f, 5f));
            // A volume of 0 (a flat prop) has Req 0 and is always inside.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed,
                new Vector3(0.5f, 0f, 0f), Vector3.zero, Vector3.zero, 0f, 0f, 0.001f, 5f));

            // The growth cap is inclusive: a radius exactly at the cap is refused.
            Assert.AreEqual(AbsorbOutcome.RejectBallAtGrowthCap, Evaluate(true, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 5f, 0f, 10f, 5f));

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
                Vector3.one, Vector3.zero, Vector3.zero, 0.2f, 0f, float.NaN, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, null, allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 0.2f, 0f, 1f, 5f));
            // A rotation that is not a rotation (NaN, or the zero quaternion) is invalid input.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, PickupAbsorbMath.EvaluateAbsorb(
                true, "stone", allowed, allowed.Length, Vector3.one,
                new Quaternion(float.NaN, 0f, 0f, 1f), Vector3.zero, Vector3.zero, 0.2f, 0f,
                PickupAbsorbMath.DefaultVolumeK, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, PickupAbsorbMath.EvaluateAbsorb(
                true, "stone", allowed, allowed.Length, Vector3.one,
                new Quaternion(0f, 0f, 0f, 0f), Vector3.zero, Vector3.zero, 0.2f, 0f,
                PickupAbsorbMath.DefaultVolumeK, 1f, 5f));
            // A negative ball diameter is invalid input, not "a zero-millimetre ball".
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                Vector3.zero, Vector3.zero, Vector3.zero, 0f, 0f, -1f, 5f));
            // An AABB volume that overflows float is an unrepresentable input, not a large
            // item: 1e20 * 1e20 * 1e20 is infinity, and comparing it would silently absorb.
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput, Evaluate(true, "stone", allowed,
                new Vector3(1e20f, 1e20f, 1e20f), Vector3.zero, Vector3.zero, 0.2f, 0f, 1f,
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
        public void AbsorbGrowthBecomesExactlyTheSameLedgerGain()
        {
            // Since steps C+D the item's growth IS the gain: round(penalty*k*a*b*c*1e9) mm^3,
            // priced once at import. There is no unit scaling, no rounding and no float
            // truncation left on this path; the only refusal is a non-positive gain.
            long gain;
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(2L, out gain));
            Assert.AreEqual(2L, gain);

            long rejected;
            Assert.IsFalse(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(0L, out rejected));
            Assert.AreEqual(0L, rejected);
            Assert.IsFalse(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(-1L, out rejected));

            // 64-bit exactness, both sides: a gain past int.MaxValue and one at long.MaxValue
            // pass through unchanged (the retired float path would have rounded the first).
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(3000000000L, out rejected));
            Assert.AreEqual(3000000000L, rejected);
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(long.MaxValue, out rejected));
            Assert.AreEqual(long.MaxValue, rejected);

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
        public void ContactTableMatchesTheWorkedExample()
        {
            // Source of the expected values: Docs/Design/PickupAbsorb/README.md, section
            // 2 (the contact distance against the item's box). The ball sits at its calibrated start diameter
            // (0.29974 m), so the collision radius is 0.14987 m; contactRadiusWorldM is 0
            // and the item is a cube of side s. Centre distance at which the cube is just
            // touched: straight at a face, r + s/2; straight at a corner (along the cube's
            // diagonal), r + sqrt(3)*s/2. The table is printed to the millimetre, so the
            // tolerance is 1 mm - this test pins the DOCUMENT's numbers, not a formula
            // copied back out of the code.
            float collisionRadius = 0.14987f;
            float[] sides = { 0.10f, 0.25f, 0.35f, 0.50f };
            float[] expectedFaceLimit = { 0.200f, 0.275f, 0.325f, 0.400f };
            float[] expectedCornerLimit = { 0.236f, 0.366f, 0.453f, 0.583f };
            float diagonal = 0.57735027f; // 1/sqrt(3)

            for (int i = 0; i < sides.Length; i++)
            {
                Vector3 size = new Vector3(sides[i], sides[i], sides[i]);
                // Bisect the centre distance at which the box distance equals the limit.
                float face = TouchDistance(size, new Vector3(1f, 0f, 0f), collisionRadius);
                float corner = TouchDistance(size, new Vector3(diagonal, diagonal, diagonal),
                    collisionRadius);
                Assert.AreEqual(expectedFaceLimit[i], face, 1e-3f,
                    "face-on limit for a cube of side " + sides[i]);
                Assert.AreEqual(expectedCornerLimit[i], corner, 1e-3f,
                    "corner-on limit for a cube of side " + sides[i]);
            }
        }

        // Centre distance along `direction` at which the item (identity rotation, at the
        // origin) is just touched by a ball of this radius: found by bisection on the
        // contact function itself, so the table is checked through the shipped code.
        private static float TouchDistance(Vector3 size, Vector3 direction, float radius)
        {
            float low = 0f;
            float high = 5f;
            for (int i = 0; i < 40; i++)
            {
                float mid = 0.5f * (low + high);
                float boxDistance;
                float limit;
                Assert.IsTrue(PickupAbsorbMath.TryComputeContact(size, Quaternion.identity,
                    Vector3.zero, direction * mid, radius, 0f, out boxDistance, out limit));
                // Moving the ball farther out only increases the box distance, so the
                // touching distance is the largest centre distance that is still contact.
                if (boxDistance <= limit) low = mid; else high = mid;
            }
            return 0.5f * (low + high);
        }

        [Test]
        public void TheGateComparesAgainstTryComputeContact()
        {
            string[] allowed = { "stone" };
            // A non-cube approached along its x axis: the near face is half of 0.4 m from
            // its centre, so with a 0.2 m collision radius and a 0.05 m contact margin the
            // face is touched when the centre is 0.45 m away. The gate's threshold is built
            // from the helper's own numbers, not a hand copy of the formula.
            Vector3 size = new Vector3(0.4f, 0.3f, 0.2f);
            float boxDistance;
            float limit;
            Assert.IsTrue(PickupAbsorbMath.TryComputeContact(size, Quaternion.identity,
                new Vector3(0.45f, 0f, 0f), Vector3.zero, 0.2f, 0.05f, out boxDistance,
                out limit));
            Assert.AreEqual(0.25f, boxDistance, 1e-6f);
            Assert.AreEqual(0.25f, limit, 1e-6f);

            // One millimetre inside is contact, one millimetre outside is not; the exact
            // equality case is pinned by BoundaryComparisonsMatchTheModel with numbers
            // that survive float rounding, because the README warns not to sit on 1 ulp.
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(true, "stone", allowed, size,
                new Vector3(0.449f, 0f, 0f), Vector3.zero, 0.2f, 0.05f, 1f, 5f));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(true, "stone", allowed,
                size, new Vector3(0.451f, 0f, 0f), Vector3.zero, 0.2f, 0.05f, 1f, 5f));
        }

        [Test]
        public void TryComputeContactRefusesGeometryTheGateCallsInvalid()
        {
            float boxDistance;
            float limit;
            Quaternion identity = Quaternion.identity;

            // Every input the gate reports as RejectInvalidInput before it has a distance:
            // a negative or non-finite size, a non-finite position, an unusable rotation, a
            // non-finite radius or contact margin, and a negative radius.
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(new Vector3(-0.1f, 0f, 0f),
                identity, Vector3.zero, Vector3.zero, 0.2f, 0f, out boxDistance, out limit));
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(new Vector3(float.NaN, 1f, 1f),
                identity, Vector3.zero, Vector3.zero, 0.2f, 0f, out boxDistance, out limit));
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(Vector3.one, identity,
                new Vector3(float.NaN, 0f, 0f), Vector3.zero, 0.2f, 0f, out boxDistance,
                out limit));
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(Vector3.one,
                new Quaternion(0f, 0f, 0f, 0f), Vector3.zero, Vector3.zero, 0.2f, 0f,
                out boxDistance, out limit));
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(Vector3.one, identity,
                Vector3.zero, Vector3.zero, float.NaN, 0f, out boxDistance, out limit));
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(Vector3.one, identity,
                Vector3.zero, Vector3.zero, 0.2f, float.PositiveInfinity, out boxDistance,
                out limit));
            Assert.IsFalse(PickupAbsorbMath.TryComputeContact(Vector3.one, identity,
                Vector3.zero, Vector3.zero, -0.2f, 0f, out boxDistance, out limit));

            // A refusal zeroes both out values, so a caller cannot report half a number.
            Assert.AreEqual(0f, boxDistance, 1e-6f);
            Assert.AreEqual(0f, limit, 1e-6f);
        }

        private static AbsorbOutcome Evaluate(bool absorbable, string kind, string[] allowed,
            Vector3 bounds, Vector3 itemPosition, Vector3 ballPosition, float collisionRadius,
            float contactRadius, float ballDiameterM, float maxGrowthRadius)
        {
            // The item plays at the original's default factor; the k-specific cases live in
            // AbsorbSizeGateTests.
            return PickupAbsorbMath.EvaluateAbsorb(absorbable, kind, allowed, allowed.Length,
                bounds, Quaternion.identity, itemPosition, ballPosition, collisionRadius,
                contactRadius,
                PickupAbsorbMath.DefaultVolumeK, ballDiameterM, maxGrowthRadius);
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
