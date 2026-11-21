using NUnit.Framework;
using VRCKatamari.Economy;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The retry seam of a story round:
    /// <see cref="GrowthLedgerMath.TryResetRowForNewRound"/> takes one row and
    /// the round-scoped stores back to the just-joined state, so a failed round can start
    /// again without reloading the scene.
    /// </summary>
    /// <remarks>
    /// Only static math is exercised: a UdonSharpBehaviour cannot be instantiated in
    /// EditMode, and the motor's own part of the reset is assignments around this rule.
    /// The fixture is <see cref="LedgerTestHost"/>, the same caller-owned store mirror the
    /// other ledger tests use, so no second host implementation exists. The award table is
    /// motor-side state in production (the motor passes its arrays in), so the test owns
    /// the same two arrays here.
    /// <para>
    /// Expected values are hand calculations: a bank moves exactly its amount 1:1, a
    /// resolved commit adds its gain once, and a reset must leave every one of those
    /// totals at zero. No assertion re-runs an implementation formula.
    /// </para>
    /// </remarks>
    public class GrowthLedgerResetTests
    {
        // The motor's award table in miniature: TryAwardOnce owns the rule, the caller
        // owns these arrays, and a round reset must empty them.
        private string[] awardedKeys = new string[4];
        private int[] awardedPoints = new int[4];
        private int awardedCount;

        /// <summary>A one-player ledger in Playing, the state a round starts from.</summary>
        private static LedgerTestHost Playing()
        {
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsTrue(host.Join("a"));
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f));
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 1f));
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 2f));
            return host;
        }

        private static LedgerTestHost AtPhase(LedgerPhase phase)
        {
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsTrue(host.Join("a"));
            if (phase == LedgerPhase.Lobby) return host;
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f));
            if (phase == LedgerPhase.Ready) return host;
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 1f));
            if (phase == LedgerPhase.Preparing) return host;
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 2f));
            if (phase == LedgerPhase.Playing) return host;
            Assert.IsTrue(host.Transition(LedgerPhase.Settling, 3f));
            if (phase == LedgerPhase.Settling) return host;
            Assert.IsTrue(host.Transition(LedgerPhase.Results, 4f));
            return host;
        }

        private bool TryReset(LedgerTestHost host, string newRoundId, string playerId,
            out int failureCode)
        {
            return GrowthLedgerMath.TryResetRowForNewRound(host.RoundState, host.RoundFlags,
                host.RoundClock, host.PlayerIds, host.ConfirmedVolumes, host.BankedScores,
                host.LifeStates, host.RespawnAtSet, host.RespawnAt, host.PlayerGenerations,
                host.PlayerCount, host.CommitItemIds, host.CommitGenerations,
                host.CommitWinners, host.CommitSequences, host.CommitGains,
                ref host.CommitCount, host.ClaimItemIds, host.ClaimGenerations,
                host.ClaimPlayerIds, host.ClaimFrames, host.ClaimRequestSequences,
                ref host.ClaimCount, host.PredictionPlayerIds, host.PredictionItemIds,
                host.PredictionGenerations, host.PredictionGains, ref host.PredictionCount,
                host.BankRequestPlayerIds, host.BankRequestIds, ref host.BankRequestCount,
                awardedKeys, awardedPoints, ref awardedCount, newRoundId, playerId,
                out failureCode);
        }

        private void ResetOk(LedgerTestHost host, string newRoundId, string playerId)
        {
            int failureCode;
            Assert.IsTrue(TryReset(host, newRoundId, playerId, out failureCode),
                "reset was refused with failure code " + failureCode);
            Assert.AreEqual(0, failureCode);
        }

        [Test]
        public void ResetReturnsTheRowToItsJustJoinedState()
        {
            LedgerTestHost host = Playing();
            // A whole round's worth of state: one committed absorb, one claim still in
            // flight, a prediction, a bank, and an awarded settlement key.
            Assert.IsTrue(host.SubmitClaim("apple", 3, "a", 10, 1));
            Assert.AreEqual(0, host.Resolve("apple", 3, 7));
            Assert.IsTrue(host.Predict("a", "plum", 1, 2));
            Assert.IsTrue(host.SubmitClaim("pear", 1, "a", 11, 2));
            Assert.IsTrue(host.Bank("a", 3, "req-1"));
            int newCount;
            int awardFailure;
            Assert.IsTrue(GrowthLedgerMath.TryAwardOnce(awardedKeys, awardedPoints,
                awardedCount, "settle:first", 10, out newCount, out awardFailure));
            awardedCount = newCount;
            Assert.IsTrue(host.Transition(LedgerPhase.Settling, 5f));
            Assert.IsTrue(host.Transition(LedgerPhase.Results, 6f));

            Assert.AreEqual(4, host.Confirmed("a"));
            Assert.AreEqual(3, host.Banked("a"));
            Assert.AreEqual(1, host.CommitCount);
            Assert.AreEqual(1, host.ClaimCount);
            Assert.AreEqual(1, host.PredictionCount);
            Assert.AreEqual(1, host.BankRequestCount);
            Assert.AreEqual(1, awardedCount);
            int revision = host.Revision();

            ResetOk(host, "r2", "a");

            // The row is back to what TryJoin writes: zero value, Active, generation 0.
            Assert.AreEqual(0, host.Confirmed("a"));
            Assert.AreEqual(0, host.Banked("a"));
            Assert.AreEqual(PlayerLifecycle.Active, host.Life("a"));
            Assert.AreEqual(0, host.PlayerGenerations[0]);
            Assert.IsFalse(host.RespawnAtSet[0]);
            Assert.AreEqual(0f, host.RespawnAt[0], 0f);
            Assert.AreEqual(1, host.PlayerCount);
            Assert.AreEqual("a", host.PlayerIds[0]);

            // Every round-scoped store is empty, so no old key can shadow a new one.
            Assert.AreEqual(0, host.CommitCount);
            Assert.AreEqual(0, host.ClaimCount);
            Assert.AreEqual(0, host.PredictionCount);
            Assert.AreEqual(0, host.BankRequestCount);
            Assert.AreEqual(0, awardedCount);
            Assert.IsNull(awardedKeys[0]);
            Assert.AreEqual(0, awardedPoints[0]);

            // The round restarts where a joined row starts, with a bumped revision.
            Assert.AreEqual(LedgerPhase.Lobby, host.CurrentPhase());
            Assert.AreEqual(0, host.CommitSequence());
            Assert.AreEqual(revision + 1, host.Revision());
            Assert.IsFalse(host.RoundFlags[GrowthLedgerMath.RoundDeadlineSetState]);

            // And it can walk back to Playing through the same edges as the first round.
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 7f));
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 8f));
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 9f));
            Assert.AreEqual(LedgerPhase.Playing, host.CurrentPhase());
            Assert.AreEqual(0, host.Confirmed("a"));
        }

        [Test]
        public void ResetMakesAnOldEventKeyUsableAgainUnderTheNewRoundId()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("apple", 0, "a", 10, 1));
            Assert.AreEqual(0, host.Resolve("apple", 0, 5));
            Assert.AreEqual(5, host.Confirmed("a"));
            // The same event key is now committed: the ledger refuses a second claim.
            Assert.IsFalse(host.SubmitClaim("apple", 0, "a", 11, 2));

            // Reset mid-round (Playing), with a claim still queued for deletion.
            Assert.IsTrue(host.SubmitClaim("pear", 2, "a", 12, 3));
            ResetOk(host, "r2", "a");
            Assert.AreEqual(0, host.ClaimCount);
            Assert.AreEqual(0, host.CommitCount);

            // The reset leaves the row in Lobby, so the round is walked back to Playing
            // through the ledger's own edges before anything may claim again.
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 13f));
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 14f));
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 15f));

            // The same item id and generation are a NEW key under the new round id, so
            // they claim and commit again - and the commit sequence restarts at 1.
            int claimFailure;
            Assert.IsTrue(GrowthLedgerMath.TrySubmitClaimWithFailure(host.RoundState,
                host.PlayerIds, host.LifeStates, host.PlayerCount, host.CommitItemIds,
                host.CommitGenerations, host.CommitCount, host.ClaimItemIds,
                host.ClaimGenerations, host.ClaimPlayerIds, host.ClaimFrames,
                host.ClaimRequestSequences, ref host.ClaimCount, "r2", "r2", "apple", 0,
                "a", 20, 3, out claimFailure),
                "the old event key was still blocked, claim failure " + claimFailure);
            int commitRow;
            Assert.IsTrue(GrowthLedgerMath.TryResolveClaims(host.RoundState, host.PlayerIds,
                host.ConfirmedVolumes, host.BankedScores, host.LifeStates, host.RespawnAtSet,
                host.RespawnAt, host.PlayerGenerations, host.PlayerCount,
                host.CommitItemIds, host.CommitGenerations, host.CommitWinners,
                host.CommitSequences, host.CommitGains, ref host.CommitCount,
                host.ClaimItemIds, host.ClaimGenerations, host.ClaimPlayerIds,
                host.ClaimFrames, host.ClaimRequestSequences, ref host.ClaimCount,
                host.PredictionPlayerIds, host.PredictionItemIds,
                host.PredictionGenerations, host.PredictionGains, ref host.PredictionCount,
                "r2", "r2", "apple", 0, 5, out commitRow));
            Assert.AreEqual(0, commitRow);
            Assert.AreEqual(5, host.Confirmed("a"));
            Assert.AreEqual(1, host.CommitSequence());
            Assert.AreEqual(1, host.CommitCount);
        }

        [Test]
        public void ResetRefusesEachInvalidInputWithItsOwnCode()
        {
            // 1: the round store itself is invalid. A phase value outside the enum is
            // also the only way to reach the phase gate today.
            LedgerTestHost host = Playing();
            host.RoundState[GrowthLedgerMath.RoundPhaseState] = 99;
            int failureCode;
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(1, failureCode);

            // 2: a player column is corrupt.
            host = Playing();
            host.LifeStates[0] = 99;
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(2, failureCode);

            // 3: the new round id is missing.
            host = Playing();
            Assert.IsFalse(TryReset(host, null, "a", out failureCode));
            Assert.AreEqual(3, failureCode);
            Assert.IsFalse(TryReset(host, "", "a", out failureCode));
            Assert.AreEqual(3, failureCode);

            // 4: a commit column is corrupt.
            host = Playing();
            Assert.IsTrue(host.SubmitClaim("apple", 0, "a", 10, 1));
            Assert.AreEqual(0, host.Resolve("apple", 0, 5));
            host.CommitItemIds[0] = null;
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(4, failureCode);

            // 5: a claim column is corrupt.
            host = Playing();
            Assert.IsTrue(host.SubmitClaim("apple", 0, "a", 10, 1));
            host.ClaimItemIds[0] = null;
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(5, failureCode);

            // 6: a prediction row is corrupt.
            host = Playing();
            Assert.IsTrue(host.Predict("a", "plum", 1, 2));
            host.PredictionGains[0] = -1;
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(6, failureCode);

            // 7: a bank-request marker is corrupt.
            host = Playing();
            Assert.IsTrue(host.SubmitClaim("apple", 0, "a", 10, 1));
            Assert.AreEqual(0, host.Resolve("apple", 0, 5));
            Assert.IsTrue(host.Bank("a", 1, "req-1"));
            host.BankRequestIds[0] = null;
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(7, failureCode);

            // 8: the caller's award table is malformed.
            host = Playing();
            awardedPoints = new int[awardedKeys.Length + 1];
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(8, failureCode);
            awardedPoints = new int[awardedKeys.Length];

            // 9: no player id was supplied.
            host = Playing();
            Assert.IsFalse(TryReset(host, "r2", null, out failureCode));
            Assert.AreEqual(9, failureCode);

            // 10: the id never joined.
            host = Playing();
            Assert.IsFalse(TryReset(host, "r2", "nobody", out failureCode));
            Assert.AreEqual(10, failureCode);

            // 11: a shared round may not be reset one row at a time.
            host = Playing();
            Assert.IsTrue(host.Join("b"));
            Assert.IsFalse(TryReset(host, "r2", "a", out failureCode));
            Assert.AreEqual(11, failureCode);
        }

        [Test]
        public void RefusedResetLeavesTheStoresExactlyAsTheyWere()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.SubmitClaim("apple", 0, "a", 10, 1));
            Assert.AreEqual(0, host.Resolve("apple", 0, 5));
            Assert.IsTrue(host.SubmitClaim("pear", 1, "a", 11, 2));
            Assert.IsTrue(host.Bank("a", 2, "req-1"));
            int newCount;
            int awardFailure;
            Assert.IsTrue(GrowthLedgerMath.TryAwardOnce(awardedKeys, awardedPoints,
                awardedCount, "settle:first", 10, out newCount, out awardFailure));
            awardedCount = newCount;
            int revision = host.Revision();

            int failureCode;
            Assert.IsFalse(TryReset(host, null, "a", out failureCode));
            Assert.AreEqual(3, failureCode);

            Assert.AreEqual(3, host.Confirmed("a"));
            Assert.AreEqual(2, host.Banked("a"));
            Assert.AreEqual(1, host.CommitCount);
            Assert.AreEqual(1, host.ClaimCount);
            Assert.AreEqual(1, host.BankRequestCount);
            Assert.AreEqual(1, awardedCount);
            Assert.AreEqual("settle:first", awardedKeys[0]);
            Assert.AreEqual(LedgerPhase.Playing, host.CurrentPhase());
            Assert.AreEqual(1, host.CommitSequence());
            Assert.AreEqual(revision, host.Revision());
        }

        [Test]
        public void ResetIsAllowedInEveryKnownPhase()
        {
            Assert.IsTrue(GrowthLedgerMath.IsResetAllowedInPhase(LedgerPhase.Lobby));
            Assert.IsTrue(GrowthLedgerMath.IsResetAllowedInPhase(LedgerPhase.Ready));
            Assert.IsTrue(GrowthLedgerMath.IsResetAllowedInPhase(LedgerPhase.Preparing));
            Assert.IsTrue(GrowthLedgerMath.IsResetAllowedInPhase(LedgerPhase.Playing));
            Assert.IsTrue(GrowthLedgerMath.IsResetAllowedInPhase(LedgerPhase.Settling));
            Assert.IsTrue(GrowthLedgerMath.IsResetAllowedInPhase(LedgerPhase.Results));
            // An unknown phase is not silently resettable: adding a phase must revisit
            // the decision instead of inheriting "yes".
            Assert.IsFalse(GrowthLedgerMath.IsResetAllowedInPhase((LedgerPhase)6));
            Assert.IsFalse(GrowthLedgerMath.IsResetAllowedInPhase((LedgerPhase)(-1)));

            LedgerPhase[] phases = new LedgerPhase[]
            {
                LedgerPhase.Lobby, LedgerPhase.Ready, LedgerPhase.Preparing,
                LedgerPhase.Playing, LedgerPhase.Settling, LedgerPhase.Results
            };
            for (int i = 0; i < phases.Length; i++)
            {
                LedgerTestHost host = AtPhase(phases[i]);
                Assert.AreEqual(phases[i], host.CurrentPhase(), "fixture phase");
                ResetOk(host, "r2", "a");
                Assert.AreEqual(LedgerPhase.Lobby, host.CurrentPhase(),
                    "reset from " + phases[i] + " did not return to Lobby");
            }
        }

        [Test]
        public void ResetClearsThePhaseDeadline()
        {
            // Lobby -> Ready carries the round's timeout, which is the only deadline the
            // model can hold before a round ends.
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsTrue(host.Join("a"));
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f, 30f));
            Assert.IsTrue(host.RoundFlags[GrowthLedgerMath.RoundDeadlineSetState]);
            Assert.AreEqual(30f, host.RoundClock[GrowthLedgerMath.RoundDeadlineAtClock], 0f);

            ResetOk(host, "r2", "a");

            Assert.IsFalse(host.RoundFlags[GrowthLedgerMath.RoundDeadlineSetState]);
            Assert.AreEqual(0f, host.RoundClock[GrowthLedgerMath.RoundDeadlineAtClock], 0f);
            Assert.AreEqual(LedgerPhase.Lobby, host.CurrentPhase());
        }
    }
}
