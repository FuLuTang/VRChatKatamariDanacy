using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Economy;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    public class PickupAbsorbStepTests
    {
        private int[] round;
        private string[] players;
        private long[] confirmed;
        private long[] banked;
        private int[] life;
        private bool[] respawnSet;
        private float[] respawnAt;
        private int[] playerGenerations;
        private string[] commitIds;
        private int[] commitGenerations;
        private string[] winners;
        private int[] sequences;
        private long[] commitGains;
        private int commitCount;
        private string[] claimIds;
        private int[] claimGenerations;
        private string[] claimPlayers;
        private int[] frames;
        private int[] requests;
        private int claimCount;
        private string[] predictedPlayers;
        private string[] predictedIds;
        private int[] predictedGenerations;
        private long[] predictedGains;
        private int predictedCount;
        private int requestSequence;
        private long unbankedMirror;
        private long bankedMirror;
        private int absorbedCount;
        private bool[] committed;
        private long[] gains;
        private bool[] logFailures;
        private int[] claimFailures;
        private int[] resolveFailures;
        private int[] claimCountsAtFailure;

        [SetUp]
        public void SetUp()
        {
            round = new int[GrowthLedgerMath.RoundStateCount];
            round[GrowthLedgerMath.RoundPhaseState] = (int)LedgerPhase.Playing;
            players = new[] { "player" };
            confirmed = new long[1];
            banked = new long[1];
            life = new[] { (int)PlayerLifecycle.Active };
            respawnSet = new bool[1];
            respawnAt = new float[1];
            playerGenerations = new int[1];
            commitIds = new string[1];
            commitGenerations = new int[1];
            winners = new string[1];
            sequences = new int[1];
            commitGains = new long[1];
            claimIds = new string[8];
            claimGenerations = new int[8];
            claimPlayers = new string[8];
            frames = new int[8];
            requests = new int[8];
            predictedPlayers = new string[0];
            predictedIds = new string[0];
            predictedGenerations = new int[0];
            predictedGains = new long[0];
            committed = new bool[4];
            gains = new long[4];
            logFailures = new bool[4];
            claimFailures = new int[4];
            resolveFailures = new int[4];
            claimCountsAtFailure = new int[4];
            commitCount = claimCount = predictedCount = requestSequence = absorbedCount = 0;
            unbankedMirror = bankedMirror = 0L;
        }

        private bool Commit(bool hasLocalRow, bool[] accepted, string[] ids, long[] growth)
        {
            return PickupAbsorbStepMath.CommitAcceptedAbsorbs(hasLocalRow, accepted, ids,
                new int[ids.Length], growth, ids.Length, round, players, confirmed, banked,
                life, respawnSet, respawnAt, playerGenerations, 1, commitIds, commitGenerations,
                winners, sequences, commitGains, ref commitCount, claimIds, claimGenerations,
                claimPlayers, frames, requests, ref claimCount, predictedPlayers, predictedIds,
                predictedGenerations, predictedGains, ref predictedCount, "round", "player", 0,
                12, ref requestSequence, ref unbankedMirror, ref bankedMirror, ref absorbedCount,
                committed, gains, logFailures, claimFailures, resolveFailures,
                claimCountsAtFailure);
        }

        [TestCase(.1f, AbsorbOutcome.Absorb)]
        [TestCase(.1001f, AbsorbOutcome.RejectOutOfReach)]
        public void ContactBoundaryUsesCollisionSphere(float distance, AbsorbOutcome expected)
        {
            float boxDistance;
            float contactLimit;
            Assert.AreEqual(expected, PickupAbsorbStepMath.EvaluateCandidate(true, "test",
                new[] { "test" }, 1, Vector3.zero, Quaternion.identity,
                new Vector3(distance, 0f, 0f), Vector3.zero, .1f, 0f, .7f, .2f, 1f, 1L,
                out boxDistance, out contactLimit));
            Assert.AreEqual(distance, boxDistance, 1e-7f);
            Assert.AreEqual(.1f, contactLimit);
        }

        [TestCase(.023f, AbsorbOutcome.Absorb)]
        [TestCase(.022f, AbsorbOutcome.RejectItemTooLarge)]
        public void SizeGateKeepsWholeMillimetreBoundary(float diameter, AbsorbOutcome expected)
        {
            float boxDistance;
            float contactLimit;
            // k=.7 and a=b=c=.01 gives Req=floor(cuberoot(60*.7e-6/pi)*1000)=23 mm.
            Assert.AreEqual(expected, PickupAbsorbStepMath.EvaluateCandidate(true, "test",
                new[] { "test" }, 1, new Vector3(.01f, .01f, .01f), Quaternion.identity,
                Vector3.zero, Vector3.zero, .01f, 0f, .7f, diameter, 1f, 1L,
                out boxDistance, out contactLimit));
        }

        [Test]
        public void InvalidContactUsesBothDiagnosticSentinels()
        {
            float distance;
            float limit;
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                PickupAbsorbStepMath.EvaluateCandidate(true, "test", new[] { "test" }, 1,
                    Vector3.zero, new Quaternion(0f, 0f, 0f, 0f), Vector3.zero, Vector3.zero,
                    .1f, 0f, .7f, .2f, 1f, 1L, out distance, out limit));
            Assert.AreEqual(-1f, distance);
            Assert.AreEqual(-1f, limit);
        }

        [Test]
        public void QueueEvaluationOverwritesAcceptedAndUsesOnePreGrowthRadius()
        {
            bool[] accepted = { false, true };
            int[] outcomes = new int[2];
            float[] distances = new float[2];
            float[] limits = new float[2];
            PickupAbsorbStepMath.EvaluatePendingCandidates(new[] { true, true },
                new[] { "test", "test" }, new Vector3[2],
                new[] { Quaternion.identity, Quaternion.identity },
                new[] { new Vector3(.1f, 0f, 0f), new Vector3(.1001f, 0f, 0f) },
                new[] { 1000000L, 1L }, new[] { .7f, .7f }, 2, new[] { "test" }, 1,
                Vector3.zero, .1f, 0f, .2f, 1f, accepted, outcomes, distances, limits);
            CollectionAssert.AreEqual(new[] { true, false }, accepted);
            CollectionAssert.AreEqual(new[] { (int)AbsorbOutcome.Absorb,
                (int)AbsorbOutcome.RejectOutOfReach }, outcomes);
            CollectionAssert.AreEqual(new[] { .1f, .1f }, limits);
        }

        [Test]
        public void DuplicateCommitCannotGrowTwiceAndOnlySuccessIsMarkedConsumed()
        {
            Assert.IsTrue(Commit(true, new[] { true, true }, new[] { "item", "item" },
                new[] { 10L, 20L }));
            Assert.IsTrue(committed[0]);
            Assert.IsFalse(committed[1]);
            Assert.IsTrue(logFailures[1]);
            Assert.AreEqual(10, claimFailures[1]);
            Assert.AreEqual(10L, confirmed[0]);
            Assert.AreEqual(10L, unbankedMirror);
            Assert.AreEqual(1, absorbedCount);
            Assert.AreEqual(1, commitCount);
            Assert.AreEqual(2, requestSequence);
        }

        [Test]
        public void FailureRowSnapshotsClaimCountAtThatMoment()
        {
            // The one-row commit ledger fills with row 0; rows 1 and 2 then fail to resolve and
            // each leaves its claim pending, so the batch ends with two pending claims while
            // the rows saw one and two respectively.
            Assert.IsTrue(Commit(true, new[] { true, true, true },
                new[] { "first", "second", "third" }, new[] { 10L, 20L, 30L }));
            Assert.IsTrue(committed[0]);
            Assert.IsTrue(logFailures[1]);
            Assert.IsTrue(logFailures[2]);
            Assert.AreEqual(0, claimCountsAtFailure[0]);
            Assert.AreEqual(1, claimCountsAtFailure[1]);
            Assert.AreEqual(2, claimCountsAtFailure[2]);
            Assert.AreEqual(2, claimCount);
        }

        [Test]
        public void FullCommitLedgerLeavesCandidateAndMirrorsUnconsumed()
        {
            Assert.IsTrue(Commit(true, new[] { true }, new[] { "first" }, new[] { 10L }));
            Assert.IsTrue(Commit(true, new[] { true }, new[] { "second" }, new[] { 20L }));
            Assert.IsFalse(committed[0]);
            Assert.IsTrue(logFailures[0]);
            Assert.AreEqual(0, claimFailures[0]);
            Assert.AreEqual(1, resolveFailures[0]);
            Assert.AreEqual(10L, confirmed[0]);
            Assert.AreEqual(10L, unbankedMirror);
            Assert.AreEqual(1, absorbedCount);
            Assert.AreEqual(1, commitCount);
            Assert.AreEqual(1, claimCount);
        }

        [Test]
        public void MissingLocalRowHoldsEveryStateAndOutput()
        {
            committed[0] = true;
            Assert.IsFalse(Commit(false, new[] { true }, new[] { "item" }, new[] { 10L }));
            Assert.IsTrue(committed[0]);
            Assert.AreEqual(0, requestSequence);
            Assert.AreEqual(0, absorbedCount);
            Assert.AreEqual(0, commitCount);
            Assert.AreEqual(0L, unbankedMirror);
        }

        [Test]
        public void RejectedAndInvalidGrowthRowsDoNotSubmitClaims()
        {
            Assert.IsTrue(Commit(true, new[] { false, true }, new[] { "rejected", "invalid" },
                new[] { 10L, 0L }));
            Assert.IsFalse(committed[0]);
            Assert.IsFalse(committed[1]);
            Assert.IsFalse(logFailures[0]);
            Assert.IsTrue(logFailures[1]);
            Assert.AreEqual(0L, gains[1]);
            Assert.AreEqual(0, requestSequence);
            Assert.AreEqual(0, commitCount);
        }

        [Test]
        public void ClaimAndOverflowFailuresKeepTheirDistinctDiagnostics()
        {
            round[GrowthLedgerMath.RoundPhaseState] = (int)LedgerPhase.Lobby;
            Assert.IsTrue(Commit(true, new[] { true }, new[] { "item" }, new[] { 10L }));
            Assert.AreEqual(7, claimFailures[0]);
            Assert.AreEqual(0, resolveFailures[0]);
            Assert.IsFalse(committed[0]);
            Assert.AreEqual(1, requestSequence);
            round[GrowthLedgerMath.RoundPhaseState] = (int)LedgerPhase.Playing;
            confirmed[0] = long.MaxValue;
            unbankedMirror = 123L;
            Assert.IsTrue(Commit(true, new[] { true }, new[] { "item" }, new[] { 1L }));
            Assert.AreEqual(0, claimFailures[0]);
            Assert.AreEqual(1, resolveFailures[0]);
            Assert.IsFalse(committed[0]);
            Assert.AreEqual(123L, unbankedMirror);
            Assert.AreEqual(long.MaxValue, confirmed[0]);
        }

        [Test]
        public void CompactionRetainsEverySnapshotColumnAndOrder()
        {
            bool[] absorbable = { true, false, true };
            string[] kinds = { "a", "b", "c" };
            Vector3[] sizes = { Vector3.zero, Vector3.one, Vector3.up };
            Quaternion[] rotations = { Quaternion.identity, new Quaternion(0f, 0f, 1f, 0f),
                new Quaternion(0f, 1f, 0f, 0f) };
            Vector3[] positions = { Vector3.zero, Vector3.right, Vector3.forward };
            long[] growth = { 1L, 2L, 3L };
            float[] volumeK = { .6f, .7f, .8f };
            string[] ids = { "id-a", "id-b", "id-c" };
            int[] generations = { 5, 6, 7 };
            bool[] accepted = { true, false, true };
            float[] times = { 10f, 11f, 12f };
            int count = 3;
            PickupAbsorbStepMath.CompactPendingCandidates(new[] { true, false, false },
                ref count, absorbable, kinds, sizes, rotations, positions, growth, volumeK,
                ids, generations, accepted, times);
            Assert.AreEqual(2, count);
            Assert.IsFalse(absorbable[0]);
            Assert.AreEqual("b", kinds[0]);
            Assert.AreEqual(Vector3.one, sizes[0]);
            Assert.AreEqual(new Quaternion(0f, 0f, 1f, 0f), rotations[0]);
            Assert.AreEqual(Vector3.right, positions[0]);
            Assert.AreEqual(2L, growth[0]);
            Assert.AreEqual(.7f, volumeK[0]);
            Assert.AreEqual("id-b", ids[0]);
            Assert.AreEqual(6, generations[0]);
            Assert.IsFalse(accepted[0]);
            Assert.AreEqual(11f, times[0]);
            Assert.AreEqual("id-c", ids[1]);
            Assert.AreEqual(12f, times[1]);
        }

        [Test]
        public void OneAbsorbUsesFrozenEventBeforeGrowthAndRegistersAfterGrowth()
        {
            long total = 0L;
            float core = 0f, collision = 0f, diameter = 0f;
            Assert.IsTrue(PickupAbsorbStepMath.RecomputeConfirmedAndSize(0L, 0L, 4188790L,
                2f, 1f, 1f, ref total, ref core, ref collision, ref diameter));
            float contactCollision = collision;
            bool[] accepted = new bool[1];
            int[] outcomes = new int[1];
            float[] distances = new float[1], limits = new float[1];
            Vector3[] sizes = { new Vector3(.04f, .04f, .04f) };
            Vector3[] itemPositions = { new Vector3(.119f, 0f, 0f) };
            Quaternion[] rotations = { Quaternion.identity };
            PickupAbsorbStepMath.EvaluatePendingCandidates(new[] { true }, new[] { "test" },
                sizes, rotations, itemPositions, new[] { 4000000L }, new[] { .7f }, 1,
                new[] { "test" }, 1, Vector3.zero, collision, 0f, diameter, 1f, accepted,
                outcomes, distances, limits);
            Assert.IsTrue(accepted[0]);
            Assert.IsTrue(Commit(true, accepted, new[] { "item" }, new[] { 4000000L }));
            Assert.IsTrue(committed[0]);
            int capacity = AttachmentStepMath.EventCapacity;
            string[] eventIds = new string[capacity];
            Vector3[] eventPositions = new Vector3[capacity], locals = new Vector3[capacity],
                centers = new Vector3[capacity];
            Quaternion[] localRotations = new Quaternion[capacity];
            float[] radii = new float[capacity], margins = new float[capacity];
            int[] steps = new int[capacity];
            bool[] valid = new bool[capacity];
            int eventCount = 0, ringCount = 0;
            AttachmentStepMath.RecordAbsorbedEvent("item", itemPositions[0], rotations[0],
                Vector3.zero, Quaternion.identity, Vector3.zero, Vector3.up, core, collision,
                0f, false, 1f, 12, eventIds, eventPositions, locals, localRotations, centers,
                radii, margins, steps, valid, ref eventCount, ref ringCount);
            int queueCount = 1;
            PickupAbsorbStepMath.CompactPendingCandidates(committed, ref queueCount,
                new[] { true }, new[] { "test" }, sizes, rotations, itemPositions,
                new[] { 4000000L }, new[] { .7f }, new[] { "item" }, new int[1], accepted,
                new float[1]);
            Assert.AreEqual(0, queueCount);
            Assert.IsTrue(PickupAbsorbStepMath.RecomputeConfirmedAndSize(unbankedMirror, 0L,
                4188790L, 2f, 1f, 1f, ref total, ref core, ref collision, ref diameter));
            Assert.Greater(collision, contactCollision);
            Vector3 local, center;
            Quaternion localRotation;
            float radius, margin;
            int delay;
            Assert.IsTrue(AttachmentStepMath.TryReadAbsorbedEventAttachment(0, 20, eventIds,
                locals, localRotations, centers, radii, margins, steps, valid, eventCount,
                ringCount, out local, out localRotation, out center, out radius, out margin,
                out delay));
            Assert.AreEqual(itemPositions[0], local);
            Assert.AreEqual(contactCollision, radius);
            Assert.AreEqual(8, delay);
            Vector3[] directions = new Vector3[RadialProxyMath.DirectionCount];
            Assert.IsTrue(RadialProxyMath.TryFillDirections(directions));
            int endpointCount = 0, itemCount = 0, candidateCount = 0, skip;
            Assert.IsTrue(AttachmentStepMath.TryAttachRadialProxy("item", sizes[0], local,
                localRotation, core, .01f, directions, new float[directions.Length],
                new Vector3[AttachmentStepMath.ProxyEndpointCapacity], ref endpointCount,
                new int[AttachmentStepMath.ProxyItemCapacity], new int[AttachmentStepMath.ProxyItemCapacity],
                new string[AttachmentStepMath.ProxyItemCapacity], new bool[AttachmentStepMath.ProxyItemCapacity],
                ref itemCount, new string[RadialProxyMath.MaxExtremeCandidates], ref candidateCount,
                new int[directions.Length], new float[directions.Length],
                new Vector3[RadialProxyShellMath.AabbCornerCount], out skip));
            Assert.AreEqual(1, itemCount);
            Assert.AreEqual(8, endpointCount);
        }

        [Test]
        public void SizeRecomputeAddsCarryOnceAndInvalidDeriveHoldsSizes()
        {
            long total = 0L;
            float core = 1f, collision = 2f, diameter = 3f;
            Assert.IsTrue(PickupAbsorbStepMath.RecomputeConfirmedAndSize(2000000L, 3000000L,
                1000000L, 2f, 1f, 2f, ref total, ref core, ref collision, ref diameter));
            Assert.AreEqual(5000000L, total);
            float expected = (float)System.Math.Pow(3.0 * .006 / (4.0 * System.Math.PI), 1.0 / 3.0) / 2f;
            Assert.AreEqual(expected, core, 1e-6f);
            Assert.AreEqual(core, collision);
            Assert.AreEqual(core * 4f, diameter, 1e-6f);
            float first = core;
            Assert.IsTrue(PickupAbsorbStepMath.RecomputeConfirmedAndSize(2000000L, 3000000L,
                1000000L, 2f, 1f, 2f, ref total, ref core, ref collision, ref diameter));
            Assert.AreEqual(first, core);
            Assert.IsFalse(PickupAbsorbStepMath.RecomputeConfirmedAndSize(10L, 20L,
                0L, 2f, 1f, 2f, ref total, ref core, ref collision, ref diameter));
            Assert.AreEqual(30L, total);
            Assert.AreEqual(first, core);
        }
    }
}
