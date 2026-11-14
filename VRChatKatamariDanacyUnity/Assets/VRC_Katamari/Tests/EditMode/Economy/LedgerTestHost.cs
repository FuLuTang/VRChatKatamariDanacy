using NUnit.Framework;
using VRCKatamari.Economy;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Test-only mirror of the Python ledger host object: it owns every caller-owned
    /// store and offers the model-shaped calls (join, transition, claim, resolve, bank,
    /// ...) that the Python test bodies use. Shared by the ledger and pickup tests so
    /// there is exactly one host implementation; it is not runtime code.
    /// </summary>
    internal sealed class LedgerTestHost
    {
        /// <summary>Round id used when a test does not pass one explicitly.</summary>
        public const string DefaultRoundId = "r1";

        private const int PlayerCapacity = 8;
        private const int CommitCapacity = 16;
        private const int ClaimCapacity = 32;
        private const int PredictionCapacity = 16;
        private const int BankRequestCapacity = 16;

        public readonly string Round;

        public readonly int[] RoundState = new int[GrowthLedgerMath.RoundStateCount];
        public readonly bool[] RoundFlags = new bool[GrowthLedgerMath.RoundFlagCount];
        public readonly float[] RoundClock = new float[GrowthLedgerMath.RoundClockCount];

        public readonly string[] PlayerIds = new string[PlayerCapacity];
        public readonly int[] ConfirmedVolumes = new int[PlayerCapacity];
        public readonly int[] BankedScores = new int[PlayerCapacity];
        public readonly int[] LifeStates = new int[PlayerCapacity];
        public readonly bool[] RespawnAtSet = new bool[PlayerCapacity];
        public readonly float[] RespawnAt = new float[PlayerCapacity];
        public readonly int[] PlayerGenerations = new int[PlayerCapacity];
        public int PlayerCount;

        public readonly string[] CommitItemIds = new string[CommitCapacity];
        public readonly int[] CommitGenerations = new int[CommitCapacity];
        public readonly string[] CommitWinners = new string[CommitCapacity];
        public readonly int[] CommitSequences = new int[CommitCapacity];
        public readonly int[] CommitGains = new int[CommitCapacity];
        public int CommitCount;

        public readonly string[] ClaimItemIds = new string[ClaimCapacity];
        public readonly int[] ClaimGenerations = new int[ClaimCapacity];
        public readonly string[] ClaimPlayerIds = new string[ClaimCapacity];
        public readonly int[] ClaimFrames = new int[ClaimCapacity];
        public readonly int[] ClaimRequestSequences = new int[ClaimCapacity];
        public int ClaimCount;

        public readonly string[] PredictionPlayerIds = new string[PredictionCapacity];
        public readonly string[] PredictionItemIds = new string[PredictionCapacity];
        public readonly int[] PredictionGenerations = new int[PredictionCapacity];
        public readonly int[] PredictionGains = new int[PredictionCapacity];
        public int PredictionCount;

        public readonly string[] BankRequestPlayerIds = new string[BankRequestCapacity];
        public readonly string[] BankRequestIds = new string[BankRequestCapacity];
        public int BankRequestCount;

        public LedgerTestHost(string round = DefaultRoundId)
        {
            Round = round;
        }

        public bool Join(string playerId)
        {
            return GrowthLedgerMath.TryJoin(RoundState, PlayerIds, ConfirmedVolumes,
                BankedScores, LifeStates, RespawnAtSet, RespawnAt, PlayerGenerations,
                ref PlayerCount, playerId);
        }

        public bool Transition(LedgerPhase target, float now, float? timeout = null)
        {
            return GrowthLedgerMath.TryTransition(RoundState, RoundFlags, RoundClock,
                target, now, timeout.HasValue, timeout.HasValue ? timeout.Value : 0f);
        }

        public bool AdvanceTimeout(float now)
        {
            bool advanced;
            Assert.IsTrue(GrowthLedgerMath.TryAdvanceTimeout(RoundState, RoundFlags,
                RoundClock, now, out advanced), "advance_timeout input was rejected");
            return advanced;
        }

        public bool Predict(string playerId, string item, int generation, int gain)
        {
            return GrowthLedgerMath.TryPredict(Round, PlayerIds, PlayerCount,
                PredictionPlayerIds, PredictionItemIds, PredictionGenerations,
                PredictionGains, ref PredictionCount, playerId, Round, item, generation, gain);
        }

        public bool SubmitClaim(string item, int generation, string playerId, int frame,
            int requestSequence, string eventRound = null)
        {
            return GrowthLedgerMath.TrySubmitClaim(RoundState, PlayerIds, LifeStates,
                PlayerCount, CommitItemIds, CommitGenerations, CommitCount, ClaimItemIds,
                ClaimGenerations, ClaimPlayerIds, ClaimFrames, ClaimRequestSequences,
                ref ClaimCount, Round, eventRound == null ? Round : eventRound, item,
                generation, playerId, frame, requestSequence);
        }

        public bool TryResolve(string item, int generation, int gain, out int commitRow)
        {
            return GrowthLedgerMath.TryResolveClaims(RoundState, PlayerIds,
                ConfirmedVolumes, BankedScores, LifeStates, RespawnAtSet, RespawnAt,
                PlayerGenerations, PlayerCount, CommitItemIds, CommitGenerations,
                CommitWinners, CommitSequences, CommitGains, ref CommitCount, ClaimItemIds,
                ClaimGenerations, ClaimPlayerIds, ClaimFrames, ClaimRequestSequences,
                ref ClaimCount, PredictionPlayerIds, PredictionItemIds,
                PredictionGenerations, PredictionGains, ref PredictionCount, Round, Round,
                item, generation, gain, out commitRow);
        }

        public int Resolve(string item, int generation, int gain)
        {
            int commitRow;
            Assert.IsTrue(TryResolve(item, generation, gain, out commitRow),
                "resolve was rejected");
            return commitRow;
        }

        public bool TryApplyCommit(string item, int generation, string winner, int sequence,
            int gain, out bool applied)
        {
            return GrowthLedgerMath.TryApplyCommit(RoundState, PlayerIds, ConfirmedVolumes,
                BankedScores, LifeStates, RespawnAtSet, RespawnAt, PlayerGenerations,
                PlayerCount, CommitItemIds, CommitGenerations, CommitWinners,
                CommitSequences, CommitGains, ref CommitCount, PredictionPlayerIds,
                PredictionItemIds, PredictionGenerations, PredictionGains,
                ref PredictionCount, Round, Round, item, generation, winner, sequence, gain,
                out applied);
        }

        public bool ApplyCommit(string item, int generation, string winner, int sequence,
            int gain)
        {
            bool applied;
            Assert.IsTrue(TryApplyCommit(item, generation, winner, sequence, gain, out applied),
                "apply_commit was rejected");
            return applied;
        }

        public bool Bank(string playerId, int amount, string requestId)
        {
            return GrowthLedgerMath.TryBank(RoundState, PlayerIds, ConfirmedVolumes,
                BankedScores, LifeStates, RespawnAtSet, RespawnAt, PlayerGenerations,
                PlayerCount, BankRequestPlayerIds, BankRequestIds, ref BankRequestCount,
                playerId, amount, requestId);
        }

        public bool Ko(string victim, string attacker, float now, float delay, int share)
        {
            return GrowthLedgerMath.TryKo(RoundState, PlayerIds, ConfirmedVolumes,
                BankedScores, LifeStates, RespawnAtSet, RespawnAt, PlayerGenerations,
                PlayerCount, PredictionPlayerIds, PredictionItemIds, PredictionGenerations,
                PredictionGains, ref PredictionCount, victim, attacker, now, delay, share);
        }

        public bool Respawn(string playerId, float now)
        {
            return GrowthLedgerMath.TryRespawn(RoundState, PlayerIds, ConfirmedVolumes,
                BankedScores, LifeStates, RespawnAtSet, RespawnAt, PlayerGenerations,
                PlayerCount, PredictionPlayerIds, PredictionItemIds, PredictionGenerations,
                PredictionGains, ref PredictionCount, playerId, now);
        }

        public bool Disconnect(string playerId)
        {
            return GrowthLedgerMath.TryDisconnect(RoundState, PlayerIds, ConfirmedVolumes,
                BankedScores, LifeStates, RespawnAtSet, RespawnAt, PlayerGenerations,
                PlayerCount, ClaimItemIds, ClaimGenerations, ClaimPlayerIds, ClaimFrames,
                ClaimRequestSequences, ref ClaimCount, PredictionPlayerIds,
                PredictionItemIds, PredictionGenerations, PredictionGains,
                ref PredictionCount, playerId);
        }

        public bool InstallSnapshot(LedgerTestHost snapshot)
        {
            return GrowthLedgerMath.TryInstallSnapshot(RoundState, RoundFlags, RoundClock,
                PlayerIds, ConfirmedVolumes, BankedScores, LifeStates, RespawnAtSet,
                RespawnAt, PlayerGenerations, ref PlayerCount, CommitItemIds,
                CommitGenerations, CommitWinners, CommitSequences, CommitGains,
                ref CommitCount, ref PredictionCount, ref ClaimCount, Round, snapshot.Round,
                snapshot.RoundState, snapshot.RoundFlags, snapshot.RoundClock,
                snapshot.PlayerIds, snapshot.ConfirmedVolumes, snapshot.BankedScores,
                snapshot.LifeStates, snapshot.PlayerCount, snapshot.CommitItemIds,
                snapshot.CommitGenerations, snapshot.CommitWinners, snapshot.CommitSequences,
                snapshot.CommitGains, snapshot.CommitCount);
        }

        public int PlayerRow(string playerId)
        {
            int row;
            Assert.IsTrue(GrowthLedgerMath.TryFindPlayer(PlayerIds, PlayerCount, playerId,
                out row), "player " + playerId + " has not joined");
            return row;
        }

        public int Confirmed(string playerId)
        {
            return ConfirmedVolumes[PlayerRow(playerId)];
        }

        public int Banked(string playerId)
        {
            return BankedScores[PlayerRow(playerId)];
        }

        public PlayerLifecycle Life(string playerId)
        {
            return (PlayerLifecycle)LifeStates[PlayerRow(playerId)];
        }

        public bool HasPrediction(string playerId, string item, int generation)
        {
            for (int row = 0; row < PredictionCount; row++)
            {
                if (PredictionGenerations[row] != generation) continue;
                if (PredictionItemIds[row] != item) continue;
                if (PredictionPlayerIds[row] != playerId) continue;
                return true;
            }
            return false;
        }

        public LedgerPhase CurrentPhase()
        {
            return (LedgerPhase)RoundState[GrowthLedgerMath.RoundPhaseState];
        }

        public int CommitSequence()
        {
            return RoundState[GrowthLedgerMath.RoundCommitSequenceState];
        }

        public int Revision()
        {
            return RoundState[GrowthLedgerMath.RoundRevisionState];
        }

        public long TotalHeld()
        {
            long total = 0;
            for (int row = 0; row < PlayerCount; row++)
            {
                total += ConfirmedVolumes[row];
                total += BankedScores[row];
            }
            return total;
        }

        public long AbsorbedTotal()
        {
            long total = 0;
            for (int row = 0; row < CommitCount; row++)
            {
                total += CommitGains[row];
            }
            return total;
        }
    }
}
