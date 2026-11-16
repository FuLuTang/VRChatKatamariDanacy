using NUnit.Framework;
using VRCKatamari.Economy;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ledger/test_ledger.py, plus the safety invariants the
    /// module exists for: a deposit cannot double count, a pending or losing absorb never
    /// changes a score, totals never exceed absorbed volume, and no accepted path yields a
    /// negative total.
    /// </summary>
    /// <remarks>
    /// Every expected value is a hand calculation from the model's transactions (one
    /// commit adds its gain once; one bank moves exactly its amount 1:1; a KO takes the
    /// victim's unbanked value and gives the attacker min(lost, share)). No assertion
    /// re-runs an implementation formula. Volumes are the model's integer volume units and
    /// times are seconds from the test's own clock.
    /// <para>
    /// Totals are 32-bit integers, so a NaN (or any fractional) total is not
    /// representable; the only numeric hazard is wraparound, which every mutating call
    /// checks before writing. The non-negativity assertions here therefore look for a
    /// wrapped value, and every negative or overflowing input is refused.
    /// </para>
    /// </remarks>
    public class GrowthLedgerTests
    {
        // Deterministic host-side random walk. The Python model seeds random.Random(183);
        // the seed value is irrelevant to the invariant, only that the walk is repeatable.
        private sealed class Lcg
        {
            private uint state;

            public Lcg(uint seed)
            {
                state = seed;
            }

            public int Next(int bound)
            {
                state = state * 1664525u + 1013904223u;
                return (int)(state % (uint)bound);
            }
        }

        private static LedgerTestHost Playing()
        {
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsTrue(host.Join("a"));
            Assert.IsTrue(host.Join("b"));
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f));
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 1f));
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 2f));
            return host;
        }

        [Test]
        public void DuplicateClaimAndReplayDoNotDoubleScore()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("item", 4, "a", 10, 1));
            // The same claim again is accepted idempotently: one row, not two.
            Assert.IsTrue(host.SubmitClaim("item", 4, "a", 10, 1));
            Assert.AreEqual(1, host.ClaimCount);

            int commit = host.Resolve("item", 4, 7);
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, commit);
            // Resolving again returns the existing commit and adds nothing.
            Assert.AreEqual(commit, host.Resolve("item", 4, 7));
            Assert.AreEqual(7, host.Confirmed("a"));
            Assert.AreEqual(1, host.CommitCount);

            LedgerTestHost replica = new LedgerTestHost();
            Assert.IsTrue(replica.Join("a"));
            Assert.IsTrue(replica.Join("b"));
            Assert.IsTrue(replica.ApplyCommit("item", 4, "a", 1, 7));
            // A byte-identical replay is accepted but not applied.
            Assert.IsFalse(replica.ApplyCommit("item", 4, "a", 1, 7));
            Assert.AreEqual(7, replica.Confirmed("a"));
        }

        [Test]
        public void SameFrameTieAndLoserRollback()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.Predict("a", "i", 0, 50));
            Assert.IsTrue(host.Predict("b", "i", 0, 50));
            // Same frame, so the ordinally smaller player id wins regardless of arrival.
            Assert.IsTrue(host.SubmitClaim("i", 0, "b", 12, 1));
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 12, 1));

            int commit = host.Resolve("i", 0, 3);
            Assert.AreEqual("a", host.CommitWinners[commit]);
            Assert.AreEqual(3, host.Confirmed("a"));
            Assert.AreEqual(0, host.Confirmed("b"));
            Assert.AreEqual(0, host.PredictionCount);
        }

        [Test]
        public void PredictedValueCannotBank()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.Predict("a", "i", 0, 9));
            Assert.IsFalse(host.Bank("a", 9, "bank-1"));
            Assert.AreEqual(0, host.Banked("a"));
            Assert.AreEqual(0, host.Confirmed("a"));
        }

        [Test]
        public void LateJoinSnapshotIsConsistent()
        {
            LedgerTestHost source = Playing();
            Assert.IsTrue(source.SubmitClaim("item", 1, "a", 3, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, source.Resolve("item", 1, 11));
            Assert.IsTrue(source.Bank("a", 4, "b1"));
            // join("late") returns the snapshot in the model; the host's stores are it.
            Assert.IsTrue(source.Join("late"));

            LedgerTestHost client = new LedgerTestHost();
            Assert.IsTrue(client.InstallSnapshot(source));

            // 11 confirmed minus the 4 already banked leaves 7 unbanked.
            Assert.AreEqual(7, client.Confirmed("a"));
            Assert.AreEqual(4, client.Banked("a"));
            AssertSnapshotEquals(source, client);
        }

        [Test]
        public void KoRespawnLifecycleIsLegalAndClearsPrediction()
        {
            LedgerTestHost host = Playing();
            // Seed 10 unbanked through the ledger itself instead of assigning a total, so
            // the KO transaction is the only thing that could move the value.
            Assert.IsTrue(host.SubmitClaim("x", 0, "a", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("x", 0, 10));
            Assert.IsTrue(host.Predict("a", "y", 0, 999));

            Assert.IsTrue(host.Ko("a", "b", 5f, 2f, 3));
            Assert.AreEqual(PlayerLifecycle.RespawnPending, host.Life("a"));
            Assert.AreEqual(0, host.Confirmed("a"));
            Assert.IsFalse(host.HasPrediction("a", "y", 0));
            // min(10 lost, 3 share) goes to the attacker.
            Assert.AreEqual(3, host.Confirmed("b"));

            // Respawn is due at 5 + 2 = 7.
            Assert.IsFalse(host.Respawn("a", 6f));
            Assert.IsTrue(host.Respawn("a", 7f));
            Assert.AreEqual(PlayerLifecycle.Active, host.Life("a"));
            Assert.IsTrue(host.Disconnect("a"));
            Assert.AreEqual(PlayerLifecycle.Disconnected, host.Life("a"));
        }

        [Test]
        public void IllegalTransitionRejectedAndTimeoutOnce()
        {
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsFalse(host.Transition(LedgerPhase.Playing, 0f));
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f, 5f));
            Assert.IsFalse(host.AdvanceTimeout(4f));
            Assert.IsTrue(host.AdvanceTimeout(5f));
            Assert.AreEqual(LedgerPhase.Preparing, host.CurrentPhase());
            // The deadline was cleared by the advance, so the old deadline cannot refire.
            Assert.IsFalse(host.AdvanceTimeout(100f));
        }

        [Test]
        public void AllPhasesReachableNoDeadEndAndRandomWalk()
        {
            bool[] reached = new bool[6];
            LedgerPhase[] pending = new LedgerPhase[6];
            int pendingCount = 0;
            pending[pendingCount] = LedgerPhase.Lobby;
            pendingCount = pendingCount + 1;
            reached[(int)LedgerPhase.Lobby] = true;
            int reachedCount = 1;
            while (pendingCount > 0)
            {
                pendingCount = pendingCount - 1;
                LedgerPhase phase = pending[pendingCount];
                for (int target = 0; target < 6; target++)
                {
                    LedgerPhase next = (LedgerPhase)target;
                    if (!GrowthLedgerMath.IsTransitionAllowed(phase, next)) continue;
                    if (reached[target]) continue;
                    reached[target] = true;
                    reachedCount = reachedCount + 1;
                    pending[pendingCount] = next;
                    pendingCount = pendingCount + 1;
                }
            }
            Assert.AreEqual(6, reachedCount, "every phase must be reachable from Lobby");

            Lcg random = new Lcg(183u);
            LedgerPhase[] successors = new LedgerPhase[6];
            for (int walk = 0; walk < 100; walk++)
            {
                LedgerPhase phase = LedgerPhase.Lobby;
                for (int step = 0; step < 50; step++)
                {
                    int count = 0;
                    for (int target = 0; target < 6; target++)
                    {
                        LedgerPhase next = (LedgerPhase)target;
                        if (!GrowthLedgerMath.IsTransitionAllowed(phase, next)) continue;
                        successors[count] = next;
                        count = count + 1;
                    }
                    Assert.Greater(count, 0, "dead end at " + phase);
                    phase = successors[random.Next(count)];
                }
            }
        }

        [Test]
        public void OtherRoundAndInvalidGenerationRejected()
        {
            LedgerTestHost host = Playing();
            Assert.IsFalse(host.SubmitClaim("i", 1, "a", 0, 1, "other"));
            Assert.IsFalse(host.SubmitClaim("i", -1, "a", 0, 1));
            Assert.AreEqual(0, host.ClaimCount);
            // The model has no notion of an item's current generation, so a key with an
            // older but non-negative generation is indistinguishable from a current one.
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 0, 1));
        }

        [Test]
        public void BankIsIdempotentPerRequestId()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("i", 0, 10));

            Assert.IsTrue(host.Bank("a", 4, "b1"));
            Assert.AreEqual(6, host.Confirmed("a"));
            Assert.AreEqual(4, host.Banked("a"));

            // The same request id is refused and moves nothing.
            Assert.IsFalse(host.Bank("a", 4, "b1"));
            Assert.AreEqual(6, host.Confirmed("a"));
            Assert.AreEqual(4, host.Banked("a"));

            Assert.IsTrue(host.Bank("a", 6, "b2"));
            Assert.AreEqual(0, host.Confirmed("a"));
            Assert.AreEqual(10, host.Banked("a"));
            Assert.IsFalse(host.Bank("a", 1, "b3"));
        }

        [Test]
        public void PendingClaimNeverChangesScore()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 0, 1));
            Assert.AreEqual(0, host.Confirmed("a"));
            Assert.IsFalse(host.Bank("a", 1, "b1"));

            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("i", 0, 5));
            Assert.AreEqual(5, host.Confirmed("a"));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("i", 0, 5));
            Assert.AreEqual(5, host.Confirmed("a"));
            Assert.AreEqual(1, host.CommitCount);
        }

        [Test]
        public void DepositMovesExactlyTheConfirmedAmount()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("i", 0, 10));
            Assert.IsTrue(host.SubmitClaim("j", 0, "b", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("j", 0, 4));

            Assert.IsTrue(host.Bank("a", 10, "b1"));
            Assert.AreEqual(0, host.Confirmed("a"));
            Assert.AreEqual(10, host.Banked("a"));
            // Nothing was lost or created by the move: 10 + 4 absorbed, 10 + 4 held.
            Assert.AreEqual(14L, host.TotalHeld());
            Assert.AreEqual(host.AbsorbedTotal(), host.TotalHeld());
        }

        [Test]
        public void ReorderedEventsGiveTheSameFinalLedger()
        {
            LedgerTestHost first = Playing();
            LedgerTestHost second = Playing();

            // Same claims, opposite arrival order. i is won by a (frame 10), j by b
            // (frame 10), so both ledgers must end with a = 7 and b = 5.
            Assert.IsTrue(first.SubmitClaim("i", 0, "a", 10, 1));
            Assert.IsTrue(first.SubmitClaim("i", 0, "b", 12, 1));
            Assert.IsTrue(first.SubmitClaim("j", 0, "b", 10, 1));
            Assert.IsTrue(first.SubmitClaim("j", 0, "a", 12, 1));
            Assert.IsTrue(second.SubmitClaim("i", 0, "b", 12, 1));
            Assert.IsTrue(second.SubmitClaim("i", 0, "a", 10, 1));
            Assert.IsTrue(second.SubmitClaim("j", 0, "a", 12, 1));
            Assert.IsTrue(second.SubmitClaim("j", 0, "b", 10, 1));

            // Resolution order also differs; commit sequence ids legitimately follow it,
            // but the balances must not.
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, first.Resolve("i", 0, 7));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, first.Resolve("j", 0, 5));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, second.Resolve("j", 0, 5));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, second.Resolve("i", 0, 7));

            Assert.AreEqual(7, first.Confirmed("a"));
            Assert.AreEqual(5, first.Confirmed("b"));
            Assert.AreEqual(7, second.Confirmed("a"));
            Assert.AreEqual(5, second.Confirmed("b"));

            // Deposits in opposite order land on the same totals too.
            Assert.IsTrue(first.Bank("a", 3, "b1"));
            Assert.IsTrue(first.Bank("a", 4, "b2"));
            Assert.IsTrue(second.Bank("a", 4, "b2"));
            Assert.IsTrue(second.Bank("a", 3, "b1"));
            Assert.AreEqual(first.Confirmed("a"), second.Confirmed("a"));
            Assert.AreEqual(first.Banked("a"), second.Banked("a"));
            Assert.AreEqual(first.TotalHeld(), second.TotalHeld());
        }

        [Test]
        public void AbsorbedVolumeIsAnUpperBound()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("i", 0, 3));
            Assert.IsTrue(host.SubmitClaim("j", 0, "b", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("j", 0, 4));
            Assert.IsTrue(host.SubmitClaim("k", 0, "a", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("k", 0, 2));

            Assert.AreEqual(9L, host.AbsorbedTotal());
            Assert.AreEqual(9L, host.TotalHeld());

            // A KO may destroy value (share 1 of 5 lost) but never create it.
            Assert.IsTrue(host.Ko("a", "b", 10f, 1f, 1));
            Assert.LessOrEqual(host.TotalHeld(), host.AbsorbedTotal());
            Assert.AreEqual(5L, host.TotalHeld());

            // Banking is a pure move; a deposit cannot lift the total above what was
            // absorbed.
            Assert.IsTrue(host.Respawn("a", 11f));
            Assert.IsTrue(host.Bank("b", 5, "b1"));
            Assert.LessOrEqual(host.TotalHeld(), host.AbsorbedTotal());

            for (int row = 0; row < host.PlayerCount; row++)
            {
                Assert.GreaterOrEqual(host.ConfirmedVolumes[row], 0, "confirmed volume");
                Assert.GreaterOrEqual(host.BankedScores[row], 0, "banked score");
            }
        }

        [Test]
        public void RejectedReplayCannotDoubleCount()
        {
            LedgerTestHost replica = new LedgerTestHost();
            Assert.IsTrue(replica.Join("a"));

            // Sequence 2 with an empty store is a gap, not a skip.
            bool applied;
            Assert.IsFalse(replica.TryApplyCommit("i", 0, "a", 2, 5, out applied));
            Assert.AreEqual(0, replica.CommitCount);

            Assert.IsTrue(replica.ApplyCommit("i", 0, "a", 1, 5));
            // Same key, different gain: a conflict, refused rather than added.
            Assert.IsFalse(replica.TryApplyCommit("i", 0, "a", 1, 6, out applied));
            Assert.AreEqual(5, replica.Confirmed("a"));
            // Same key, different winner: also a conflict.
            Assert.IsTrue(replica.Join("b"));
            Assert.IsFalse(replica.TryApplyCommit("i", 0, "b", 1, 5, out applied));
            Assert.AreEqual(5, replica.Confirmed("a"));
            Assert.AreEqual(0, replica.Confirmed("b"));
            // A new key reusing the old sequence is a gap.
            Assert.IsFalse(replica.TryApplyCommit("j", 0, "a", 1, 5, out applied));
            Assert.AreEqual(1, replica.CommitCount);
        }

        [Test]
        public void InvalidInputIsRejectedWithoutChangingState()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("i", 0, "a", 0, 1));
            long confirmedBefore = host.Confirmed("a");
            int commitCountBefore = host.CommitCount;
            int row;

            // A negative gain is not the model's apply_commit rule and resolve_claims
            // forgets to check it; this port refuses it so a total cannot go negative.
            Assert.IsFalse(host.TryResolve("i", 0, -1, out row));
            Assert.AreEqual(GrowthLedgerMath.NoRow, row);
            Assert.AreEqual(confirmedBefore, host.Confirmed("a"));
            Assert.AreEqual(commitCountBefore, host.CommitCount);

            // Non-finite time is refused by every time-taking entry point.
            Assert.IsFalse(host.Transition(LedgerPhase.Settling, float.NaN));
            Assert.AreEqual(LedgerPhase.Playing, host.CurrentPhase());
            Assert.IsFalse(host.Ko("a", "b", float.PositiveInfinity, 0f, 0));
            Assert.IsFalse(host.Respawn("a", float.NaN));

            // A deposit of nothing, a negative deposit and an empty request id are all
            // refused.
            Assert.IsFalse(host.Bank("a", 0, "zero"));
            Assert.IsFalse(host.Bank("a", -1, "neg"));
            Assert.IsFalse(host.Bank("a", 1, ""));
            Assert.AreEqual(0, host.Banked("a"));

            // A claim from an unknown player, during the wrong phase, is refused.
            Assert.IsFalse(host.SubmitClaim("k", 0, "ghost", 0, 1));
            Assert.IsTrue(host.SubmitClaim("k", 0, "a", 0, 1));
            Assert.AreNotEqual(GrowthLedgerMath.NoRow, host.Resolve("k", 0, 2));
            Assert.IsTrue(host.Transition(LedgerPhase.Settling, 20f));
            Assert.IsFalse(host.Bank("a", 1, "late"));

            // Predictions are local and cannot be negative either.
            Assert.IsFalse(host.Predict("a", "z", 0, -5));
        }

        [Test]
        public void NullStoresAreRefused()
        {
            Assert.IsFalse(GrowthLedgerMath.TryJoin(null, null, null, null, null, null, null,
                null, ref UnsafePlayerCount, "a"));
            Assert.IsFalse(GrowthLedgerMath.TryFindPlayer(null, 0, "a", out UnsafeRow));
            Assert.IsFalse(GrowthLedgerMath.IsTransitionAllowed((LedgerPhase)99,
                LedgerPhase.Lobby));
        }

        // Out values for the null-store checks; a refused call leaves them at the
        // documented initial values.
        private int UnsafePlayerCount;

        private int UnsafeRow;

        private static void AssertSnapshotEquals(LedgerTestHost expected, LedgerTestHost actual)
        {
            Assert.AreEqual(expected.CurrentPhase(), actual.CurrentPhase(), "snapshot phase");
            Assert.AreEqual(expected.CommitSequence(), actual.CommitSequence(),
                "snapshot commit sequence");
            Assert.AreEqual(expected.Revision(), actual.Revision(), "snapshot revision");
            Assert.AreEqual(expected.RoundFlags[GrowthLedgerMath.RoundDeadlineSetState],
                actual.RoundFlags[GrowthLedgerMath.RoundDeadlineSetState], "snapshot deadline flag");
            if (expected.RoundFlags[GrowthLedgerMath.RoundDeadlineSetState])
            {
                Assert.AreEqual(expected.RoundClock[GrowthLedgerMath.RoundDeadlineAtClock],
                    actual.RoundClock[GrowthLedgerMath.RoundDeadlineAtClock],
                    "snapshot deadline");
            }

            Assert.AreEqual(expected.PlayerCount, actual.PlayerCount, "snapshot roster size");
            for (int row = 0; row < expected.PlayerCount; row++)
            {
                string playerId = expected.PlayerIds[row];
                int actualRow = actual.PlayerRow(playerId);
                Assert.AreEqual(expected.ConfirmedVolumes[row], actual.ConfirmedVolumes[actualRow],
                    playerId + " confirmed volume");
                Assert.AreEqual(expected.BankedScores[row], actual.BankedScores[actualRow],
                    playerId + " banked score");
                Assert.AreEqual(expected.LifeStates[row], actual.LifeStates[actualRow],
                    playerId + " life");
            }

            Assert.AreEqual(expected.CommitCount, actual.CommitCount, "snapshot commit count");
            for (int row = 0; row < expected.CommitCount; row++)
            {
                int actualRow = FindCommit(actual, expected.CommitItemIds[row],
                    expected.CommitGenerations[row]);
                Assert.GreaterOrEqual(actualRow, 0, "snapshot commit missing");
                Assert.AreEqual(expected.CommitWinners[row], actual.CommitWinners[actualRow],
                    "snapshot commit winner");
                Assert.AreEqual(expected.CommitSequences[row], actual.CommitSequences[actualRow],
                    "snapshot commit sequence");
                Assert.AreEqual(expected.CommitGains[row], actual.CommitGains[actualRow],
                    "snapshot commit gain");
            }
        }

        private static int FindCommit(LedgerTestHost host, string item, int generation)
        {
            for (int row = 0; row < host.CommitCount; row++)
            {
                if (host.CommitGenerations[row] != generation) continue;
                if (host.CommitItemIds[row] != item) continue;
                return row;
            }
            return GrowthLedgerMath.NoRow;
        }
    }
}
