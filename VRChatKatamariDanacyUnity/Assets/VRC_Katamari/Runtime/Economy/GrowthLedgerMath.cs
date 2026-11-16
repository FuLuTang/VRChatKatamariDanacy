using VRCKatamari.Core;

namespace VRCKatamari.Economy
{
    /// <summary>
    /// The round phases of the draft round state machine.
    /// </summary>
    /// <remarks>
    /// Port of the model's Phase enum. The numeric order is the model's declaration
    /// order and is also the order the graph tests walk; it has no gameplay meaning.
    /// </remarks>
    public enum LedgerPhase
    {
        Lobby = 0,
        Ready = 1,
        Preparing = 2,
        Playing = 3,
        Settling = 4,
        Results = 5
    }

    /// <summary>
    /// Per-player lifecycle. <see cref="KnockedOut"/> is the model's "KO";
    /// <see cref="RespawnPending"/> and <see cref="Disconnected"/> keep the model's
    /// names.
    /// </summary>
    /// <remarks>
    /// A player's life gates claims (only <see cref="Active"/> may claim or bank) and
    /// nothing else; it never changes a confirmed total by itself.
    /// </remarks>
    public enum PlayerLifecycle
    {
        Active = 0,
        KnockedOut = 1,
        RespawnPending = 2,
        Disconnected = 3
    }

    /// <summary>
    /// The round ledger: unbanked confirmed volume, banked score, the claim-to-commit
    /// arbitration and the lifecycle transactions, as stateless math over
    /// caller-owned parallel arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of Docs/Design/Ledger/ledger.py, the DRAFT host-only reference model. This
    /// is the safety-critical layer: only values that reached a commit may be banked
    /// or may influence score, banking or PvP. Predictions are local-only, are never
    /// banked and never enter a total.
    /// </para>
    /// <para>
    /// Units. A volume unit is the model's integer volume: `confirmed_volume`,
    /// `banked_score` and every event gain are counted in the same unit, and banking
    /// converts 1:1. One unit is stored as a 64-bit integer (Docs/OPEN_DECISIONS.md (r)
    /// step A); the unit itself, its scale and every stored number are unchanged. The
    /// model defines no world-space scale for one unit. Time (`now`,
    /// deadlines, respawn times) is in seconds from the caller's monotonic clock; the
    /// model never reads a clock and every value here is supplied by the caller. A
    /// frame is the authority's integer frame index, used only for claim ordering.
    /// </para>
    /// <para>
    /// Confirmed versus pending. `confirmed_volume` is the unbanked volume of accepted
    /// authoritative commits; it is real value that a KO may still take away.
    /// `banked_score` is the banked score; it survives a KO. The two are separate
    /// accumulators on purpose: no single field ever means both, and total value held
    /// by a player is `confirmed_volume + banked_score`. A *prediction* is the
    /// client-side expectation of an absorb, kept in its own store, never added to
    /// either accumulator and dropped when the authoritative result arrives.
    /// </para>
    /// <para>
    /// Absorption accounting. One absorption event reaches this layer as a claim
    /// (batched, non-binding) and leaves it as a commit with exactly one `gain`. The
    /// commit's key is the model's EventKey: item instance id, round id and item
    /// generation. Resolution adds the gain to the winner's unbanked confirmed volume
    /// exactly once; replaying the same commit is idempotent and a conflicting replay
    /// is rejected. Losses (KO) move or destroy confirmed volume but never create it,
    /// so the sum of all players' `confirmed_volume + banked_score` never exceeds the
    /// sum of committed gains.
    /// </para>
    /// <para>
    /// Stores. Every store is a set of equal-length columns supplied by the caller, one
    /// row per record, plus a by-value row count that each mutating call advances
    /// through `ref`. Nothing is stored in this class and no call allocates.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// roundState (int[GrowthLedgerMath.RoundStateCount]): phase, commit sequence,
    /// revision.
    /// </description></item>
    /// <item><description>
    /// roundFlags (bool[RoundFlagCount]) + roundClock
    /// (float[RoundClockCount]): whether a phase deadline exists and when it expires.
    /// The flag exists because the model's deadline is optional and no float value can
    /// mean "absent".
    /// </description></item>
    /// <item><description>
    /// Player columns: ids, confirmed volumes, banked scores, life states,
    /// respawn-time-set flags, respawn times, generations, count.
    /// </description></item>
    /// <item><description>
    /// Commit columns: item instance ids, item generations, winner ids, sequences,
    /// gains, count. The model also keeps an `item_winners` map; it has exactly the
    /// same key set and the same winner as this store, so it is derived from it
    /// instead of duplicated.
    /// </description></item>
    /// <item><description>
    /// Claim columns: item instance ids, item generations, player ids, frames, request
    /// sequences, count. This is the model's per-EventKey claim batch, flattened:
    /// rows sharing a key form one batch.
    /// </description></item>
    /// <item><description>
    /// Prediction columns: player ids, item instance ids, item generations, gains,
    /// count.
    /// </description></item>
    /// <item><description>
    /// Bank-request columns: player ids, request ids, count. The model keeps this set
    /// forever; the fixed-width store makes its capacity the caller's decision.
    /// </description></item>
    /// </list>
    /// <para>
    /// Event keys. The round component of an EventKey is not stored per row: one ledger
    /// instance covers exactly one round, and every write validates the event's round
    /// against the ledger's round before the remaining components are compared. The
    /// composite key (item, round, generation) is therefore preserved exactly, with
    /// the round held once.
    /// </para>
    /// <para>
    /// Failure. The Python model raises InvalidTransition or ValueError for an illegal
    /// transition, a malformed key or a conflicting replay. Project convention is a
    /// bool return with no exception, so every such path is a false return here, and
    /// `out` values that report a decision (for example whether a commit was applied)
    /// carry the distinction the model expresses by returning a value. Every call
    /// validates all inputs and all row values before writing, so a rejected call
    /// leaves the stores exactly as they were.
    /// </para>
    /// <para>
    /// Fixed-width differences. Python integers are unbounded; these are fixed width.
    /// The QUANTITY columns (confirmed volume, banked score, commit gain, prediction
    /// gain) and every quantity argument are 64-bit <c>long</c>, so a total can pass
    /// int.MaxValue without wrapping (Docs/OPEN_DECISIONS.md (r), migration step A: the
    /// unit and every number are unchanged, only the type is wider). The QUANTITY
    /// guards are therefore <c>long.MaxValue - x</c>: a gain, deposit or attacker share
    /// that would overflow a confirmed total or a banked score is rejected instead of
    /// wrapping negative, because a negative total would break every safety invariant.
    /// Counts, row indices, event generations, claim frames, request sequences and the
    /// round's commit sequence stay 32-bit <c>int</c>: none of them is a volume or a
    /// score, and their stores (roundState, lifeStates, the key columns) are 32-bit on
    /// purpose. Capacities are the column lengths; a full store rejects instead of
    /// evicting.
    /// </para>
    /// <para>
    /// Deliberate omissions. The model's `same_frame_tie`, `round_duration_placeholder`
    /// and `protection_duration_placeholder` are stored but never read, so no parameter
    /// is invented for them (see the module report). The model's phase machine is
    /// ported as-is; authority election, reconnection epochs and settlement policy are
    /// still undecided and are not guessed here.
    /// </para>
    /// </remarks>
    public static class GrowthLedgerMath
    {
        /// <summary>roundState index: the current <see cref="LedgerPhase"/> as an int.</summary>
        public const int RoundPhaseState = 0;

        /// <summary>roundState index: the last accepted commit sequence (strictly increasing).</summary>
        public const int RoundCommitSequenceState = 1;

        /// <summary>roundState index: the model's revision counter, bumped by every state write.</summary>
        public const int RoundRevisionState = 2;

        /// <summary>Minimum length of the round state array.</summary>
        public const int RoundStateCount = 3;

        /// <summary>roundFlags index: whether the current phase has a deadline.</summary>
        public const int RoundDeadlineSetState = 0;

        /// <summary>Minimum length of the round flag array.</summary>
        public const int RoundFlagCount = 1;

        /// <summary>roundClock index: the deadline, in seconds, meaningful only when the flag is set.</summary>
        public const int RoundDeadlineAtClock = 0;

        /// <summary>Minimum length of the round clock array.</summary>
        public const int RoundClockCount = 1;

        /// <summary>Row index meaning "no row" (no player, no commit). Any negative value works.</summary>
        public const int NoRow = -1;

        /// <summary>Internal sentinel returned by the timeout-target lookup for "no successor".</summary>
        private const int NoPhase = -1;

        /// <summary>
        /// True when the model's ALLOWED table permits one phase transition.
        /// </summary>
        /// <remarks>
        /// Lobby to Ready; Ready to Preparing or back to Lobby; Preparing to Playing or
        /// back to Lobby; Playing to Settling; Settling to Results; Results to Lobby.
        /// The two cancellation edges back to Lobby are deliberate: they are the model's
        /// explicitly permitted cancels. Every other pair, including any unknown enum
        /// value, is false. This reads no state, so a caller can walk the whole graph to
        /// prove it has no dead end.
        /// </remarks>
        public static bool IsTransitionAllowed(LedgerPhase from, LedgerPhase to)
        {
            switch (from)
            {
                case LedgerPhase.Lobby:
                    return to == LedgerPhase.Ready;
                case LedgerPhase.Ready:
                    return to == LedgerPhase.Preparing || to == LedgerPhase.Lobby;
                case LedgerPhase.Preparing:
                    return to == LedgerPhase.Playing || to == LedgerPhase.Lobby;
                case LedgerPhase.Playing:
                    return to == LedgerPhase.Settling;
                case LedgerPhase.Settling:
                    return to == LedgerPhase.Results;
                case LedgerPhase.Results:
                    return to == LedgerPhase.Lobby;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when a round in this phase may be reset for a new round id.
        /// </summary>
        /// <remarks>
        /// Every known phase is allowed, including <see cref="LedgerPhase.Playing"/>.
        /// The decision is written as a switch with a false default rather than as
        /// "not Lobby": a retry is a new round, and abandoning an in-flight round is
        /// exactly what a retry does, so refusing it mid-round would force a caller to
        /// fake Settling/Results edges first. The cost is that a mid-round reset throws
        /// away the row's unbanked volume AND its banked score; that is a decision for
        /// the round's owner (the motor's TryResetForNewRound), never for this math.
        /// A phase value outside the enum is false, so adding a phase later must
        /// revisit this function instead of silently becoming resettable.
        /// </remarks>
        public static bool IsResetAllowedInPhase(LedgerPhase phase)
        {
            switch (phase)
            {
                case LedgerPhase.Lobby:
                case LedgerPhase.Ready:
                case LedgerPhase.Preparing:
                case LedgerPhase.Playing:
                case LedgerPhase.Settling:
                case LedgerPhase.Results:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when one event key belongs to this ledger: same round, non-empty item
        /// instance id and a non-negative item generation.
        /// </summary>
        /// <remarks>
        /// This is the model's `_validate_key`. Text is compared ordinally, which is
        /// code-point order for the BMP text these ids use and matches the model's
        /// Python string comparison. The empty item instance id is invalid in the model;
        /// the empty round id and empty player id are not rejected there and are not
        /// rejected here.
        /// </remarks>
        public static bool IsValidEventKey(string roundId, string eventRoundId,
            string itemInstanceId, int generation)
        {
            if (roundId == null || eventRoundId == null || itemInstanceId == null) return false;
            if (itemInstanceId.Length == 0) return false;
            if (string.CompareOrdinal(roundId, eventRoundId) != 0) return false;
            return generation >= 0;
        }

        /// <summary>
        /// Finds one player's row by id. Read-only; false when the player never joined.
        /// </summary>
        /// <remarks>
        /// The model looks this up in a dict. Linear scan over the caller's column is the
        /// Udon-safe equivalent and allocates nothing. playerIndex is
        /// <see cref="NoRow"/> when the lookup fails.
        /// </remarks>
        public static bool TryFindPlayer(string[] playerIds, int playerCount,
            string playerId, out int playerIndex)
        {
            playerIndex = NoRow;
            if (playerIds == null || playerId == null) return false;
            if (playerCount < 0 || playerCount > playerIds.Length) return false;
            for (int row = 0; row < playerCount; row++)
            {
                if (playerIds[row] == null) continue;
                if (string.CompareOrdinal(playerIds[row], playerId) != 0) continue;
                playerIndex = row;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Applies one legal phase transition and its optional deadline.
        /// </summary>
        /// <remarks>
        /// The model's transition(): an edge outside the ALLOWED table is rejected (there
        /// it raises InvalidTransition), a supplied timeout must be positive, and the
        /// deadline is either cleared or set to `now + timeout`. The revision is bumped
        /// exactly once. `now` is always validated for finiteness even when it is unused,
        /// matching the port convention that no non-finite input is accepted anywhere.
        /// </remarks>
        public static bool TryTransition(int[] roundState, bool[] roundFlags,
            float[] roundClock, LedgerPhase target, float now, bool hasTimeout, float timeout)
        {
            if (!TryValidateRoundStore(roundState, roundFlags, roundClock)) return false;
            if (!KatamariMath.IsFiniteFloat(now)) return false;
            LedgerPhase current = (LedgerPhase)roundState[RoundPhaseState];
            if (!IsTransitionAllowed(current, target)) return false;
            float deadline = 0f;
            if (hasTimeout)
            {
                if (!KatamariMath.IsFiniteFloat(timeout) || timeout <= 0f) return false;
                deadline = now + timeout;
                if (!KatamariMath.IsFiniteFloat(deadline)) return false;
            }
            roundState[RoundPhaseState] = (int)target;
            roundFlags[RoundDeadlineSetState] = hasTimeout;
            roundClock[RoundDeadlineAtClock] = deadline;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            return true;
        }

        /// <summary>
        /// Applies the phase's timeout transition when the deadline has expired.
        /// </summary>
        /// <remarks>
        /// The model's advance_timeout(): nothing happens before the deadline, and a phase
        /// with no deadline is left alone. Ready, Preparing, Playing, Settling and Results
        /// advance through the same deterministic chain; Lobby (or any phase without a
        /// successor) just drops a stale deadline. `advanced` is the model's bool return:
        /// true only when a phase transition actually happened, so repeating the call
        /// after advancing cannot fire the same old deadline again. The new phase has no
        /// deadline, exactly as the model's timeout-less transition leaves it.
        /// </remarks>
        public static bool TryAdvanceTimeout(int[] roundState, bool[] roundFlags,
            float[] roundClock, float now, out bool advanced)
        {
            advanced = false;
            if (!TryValidateRoundStore(roundState, roundFlags, roundClock)) return false;
            if (!KatamariMath.IsFiniteFloat(now)) return false;
            if (!roundFlags[RoundDeadlineSetState]) return true;
            if (now < roundClock[RoundDeadlineAtClock]) return true;

            LedgerPhase current = (LedgerPhase)roundState[RoundPhaseState];
            int target = GetTimeoutTarget(current);
            if (target == NoPhase)
            {
                roundFlags[RoundDeadlineSetState] = false;
                roundClock[RoundDeadlineAtClock] = 0f;
                return true;
            }
            if (!IsTransitionAllowed(current, (LedgerPhase)target)) return false;
            roundState[RoundPhaseState] = target;
            roundFlags[RoundDeadlineSetState] = false;
            roundClock[RoundDeadlineAtClock] = 0f;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            advanced = true;
            return true;
        }

        /// <summary>
        /// Adds a player to the roster with an empty confirmed ledger, once.
        /// </summary>
        /// <remarks>
        /// The model's join(): joining an id that already exists is accepted and changes
        /// nothing but the revision, which the model bumps unconditionally. A new row
        /// starts at zero confirmed volume, zero banked score, Active life, no respawn
        /// time and generation 0. A null id or a full store is rejected; the empty id is
        /// not rejected because the model does not reject it.
        /// </remarks>
        public static bool TryJoin(int[] roundState, string[] playerIds,
            long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations,
            ref int playerCount, string playerId)
        {
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (playerId == null) return false;

            if (HasPlayer(playerIds, playerCount, playerId))
            {
                roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
                return true;
            }
            if (playerCount >= playerIds.Length) return false;

            int row = playerCount;
            playerIds[row] = playerId;
            confirmedVolumes[row] = 0;
            bankedScores[row] = 0;
            lifeStates[row] = (int)PlayerLifecycle.Active;
            respawnAtSet[row] = false;
            respawnAt[row] = 0f;
            playerGenerations[row] = 0;
            playerCount = playerCount + 1;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            return true;
        }

        /// <summary>
        /// Resets one player's row and the round-scoped stores back to the just-joined
        /// state for a new round id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the retry seam of the story round (the "no retry seam" gap of
        /// Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md, step S6): a failed round starts
        /// again without reloading the scene. The row keeps its player id and loses
        /// everything a round gave it - confirmed volume, banked score, player
        /// generation, life state (back to Active) and any respawn time - and every
        /// store that carries a round's identity is emptied: commits (so an old event
        /// key is no longer "already committed"), claims, predictions, bank-request
        /// markers and the awarded-key table. The award table is round-scoped on
        /// purpose: a key such as "settle:first" is granted once PER ROUND, so a new
        /// round must be able to grant every key again.
        /// </para>
        /// <para>
        /// The phase returns to <see cref="LedgerPhase.Lobby"/> and the deadline is
        /// cleared, because that is where a just-joined row starts. This rule never
        /// jumps to Playing: the caller walks the phase graph again
        /// (Lobby -> Ready -> Preparing -> Playing), which is the same path the first
        /// round took, so no state can be entered that the graph does not allow.
        /// </para>
        /// <para>
        /// Phase rules: allowed in every known phase, including mid-round; see
        /// <see cref="IsResetAllowedInPhase"/>. A mid-round reset therefore discards
        /// the row's unbanked volume and its banked score. That is the meaning of
        /// "start this round over" and is the round owner's decision.
        /// </para>
        /// <para>
        /// Only a single-row ledger may be reset. With more than one row the commits
        /// this rule clears would still be the source of the other rows' confirmed
        /// volume, leaving value that no commit backs - an invariant break, not a
        /// smaller reset. Restarting a shared round means resetting every row and is a
        /// separate rule, deliberately not guessed here.
        /// </para>
        /// <para>
        /// The round id is not stored per row (one ledger covers one round), so this
        /// function cannot compare the new id with the previous one: it validates the
        /// new id as non-empty and leaves "actually a new round" to the caller that
        /// owns the id.
        /// </para>
        /// <para>
        /// failureCode: 0 ok / 1 invalidRoundStore (including a phase that is not
        /// reset-eligible) / 2 invalidPlayerColumns / 3 invalidRoundId /
        /// 4 invalidCommitColumns / 5 invalidClaimColumns / 6 invalidPredictionColumns /
        /// 7 invalidBankRequestColumns / 8 invalidAwardColumns / 9 invalidPlayerId /
        /// 10 unknownPlayer / 11 sharedRoundNotSupported. Every check runs before the
        /// first write, so a refused reset leaves every store exactly as it was.
        /// </para>
        /// </remarks>
        public static bool TryResetRowForNewRound(int[] roundState, bool[] roundFlags,
            float[] roundClock,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] commitItemIds, int[] commitGenerations, string[] commitWinners,
            int[] commitSequences, long[] commitGains, ref int commitCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount,
            string[] bankRequestPlayerIds, string[] bankRequestIds, ref int bankRequestCount,
            string[] awardedKeys, int[] awardedPoints, ref int awardedCount,
            string newRoundId, string playerId, out int failureCode)
        {
            // 0 = accepted; 1..11 name the exact refusal, in source order, so a caller
            // can say which gate refused without duplicating the validation here.
            failureCode = 0;
            if (!TryValidateRoundStore(roundState, roundFlags, roundClock)) { failureCode = 1; return false; }
            if (!IsResetAllowedInPhase((LedgerPhase)roundState[RoundPhaseState])) { failureCode = 1; return false; }
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) { failureCode = 2; return false; }
            if (newRoundId == null || newRoundId.Length == 0) { failureCode = 3; return false; }
            if (!TryValidateCommitColumns(commitItemIds, commitGenerations, commitWinners,
                commitSequences, commitGains, commitCount)) { failureCode = 4; return false; }
            if (!TryValidateClaimColumns(claimItemIds, claimGenerations, claimPlayerIds,
                claimFrames, claimRequestSequences, claimCount)) { failureCode = 5; return false; }
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) { failureCode = 6; return false; }
            if (!TryValidateBankRequestColumns(bankRequestPlayerIds, bankRequestIds,
                bankRequestCount)) { failureCode = 7; return false; }
            if (!TryValidateAwardColumns(awardedKeys, awardedPoints, awardedCount)) { failureCode = 8; return false; }
            if (playerId == null) { failureCode = 9; return false; }

            int playerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, playerId, out playerRow)) { failureCode = 10; return false; }
            if (playerCount != 1) { failureCode = 11; return false; }

            roundState[RoundPhaseState] = (int)LedgerPhase.Lobby;
            roundState[RoundCommitSequenceState] = 0;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            roundFlags[RoundDeadlineSetState] = false;
            roundClock[RoundDeadlineAtClock] = 0f;

            confirmedVolumes[playerRow] = 0;
            bankedScores[playerRow] = 0;
            lifeStates[playerRow] = (int)PlayerLifecycle.Active;
            respawnAtSet[playerRow] = false;
            respawnAt[playerRow] = 0f;
            playerGenerations[playerRow] = 0;

            // The stores are emptied rather than compacted: a new round has no rows,
            // and the string slots are released so a stale item id cannot be read
            // back from beyond the new count.
            for (int row = 0; row < commitCount; row++)
            {
                commitItemIds[row] = null;
                commitGenerations[row] = 0;
                commitWinners[row] = null;
                commitSequences[row] = 0;
                commitGains[row] = 0;
            }
            commitCount = 0;

            for (int row = 0; row < claimCount; row++)
            {
                claimItemIds[row] = null;
                claimGenerations[row] = 0;
                claimPlayerIds[row] = null;
                claimFrames[row] = 0;
                claimRequestSequences[row] = 0;
            }
            claimCount = 0;

            for (int row = 0; row < predictedCount; row++)
            {
                predictedPlayerIds[row] = null;
                predictedItemIds[row] = null;
                predictedGenerations[row] = 0;
                predictedGains[row] = 0;
            }
            predictedCount = 0;

            for (int row = 0; row < bankRequestCount; row++)
            {
                bankRequestPlayerIds[row] = null;
                bankRequestIds[row] = null;
            }
            bankRequestCount = 0;

            for (int row = 0; row < awardedCount; row++)
            {
                awardedKeys[row] = null;
                awardedPoints[row] = 0;
            }
            awardedCount = 0;
            return true;
        }

        /// <summary>
        /// Records or replaces this client's local prediction for one absorbed event.
        /// </summary>
        /// <remarks>
        /// The model's predict(): the key must be this ledger's key and gain must be
        /// non-negative, otherwise the call is rejected. A prediction for a key that is
        /// already predicted is replaced, matching the model's dict assignment. This is
        /// local-only state: no confirmed volume, banked score, commit or revision is
        /// touched, so a prediction can never be spent.
        /// </remarks>
        public static bool TryPredict(string roundId, string[] playerIds, int playerCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount,
            string playerId, string eventRoundId, string itemInstanceId, int generation, long gain)
        {
            if (!TryValidatePlayerIdentity(playerIds, playerCount)) return false;
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) return false;
            if (!IsValidEventKey(roundId, eventRoundId, itemInstanceId, generation)) return false;
            if (gain < 0) return false;
            if (playerId == null) return false;

            if (!HasPlayer(playerIds, playerCount, playerId)) return false;

            int row = FindPredictionRow(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedCount, playerId, itemInstanceId, generation);
            if (row != NoRow)
            {
                predictedGains[row] = gain;
                return true;
            }
            if (predictedCount >= predictedPlayerIds.Length) return false;

            int write = predictedCount;
            predictedPlayerIds[write] = playerId;
            predictedItemIds[write] = itemInstanceId;
            predictedGenerations[write] = generation;
            predictedGains[write] = gain;
            predictedCount = predictedCount + 1;
            return true;
        }

        /// <summary>
        /// Accepts one claim into the per-event batch. A claim never changes a score.
        /// </summary>
        /// <remarks>
        /// The model's submit_claim(): only while Playing, only from an Active player,
        /// never for an event that is already committed, and a repeated
        /// (player, request sequence) inside the same event batch is accepted
        /// idempotently without a second row. The batch is the model's
        /// `_claims[key]`; nothing here is confirmed, so a pending or losing claim can
        /// never appear in a total. Claim frames and request sequences are authority data
        /// and are not range-checked by the model, so they are not range-checked here.
        /// </remarks>
        /// <summary>
        /// Accepts one claim into the per-event batch (signature-compatible wrapper).
        /// </summary>
        public static bool TrySubmitClaim(int[] roundState,
            string[] playerIds, int[] lifeStates, int playerCount,
            string[] commitItemIds, int[] commitGenerations, int commitCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string roundId, string eventRoundId, string itemInstanceId, int generation,
            string playerId, int frame, int requestSequence)
        {
            int ignoredFailureCode;
            return TrySubmitClaimWithFailure(roundState, playerIds, lifeStates, playerCount,
                commitItemIds, commitGenerations, commitCount, claimItemIds, claimGenerations,
                claimPlayerIds, claimFrames, claimRequestSequences, ref claimCount, roundId,
                eventRoundId, itemInstanceId, generation, playerId, frame, requestSequence,
                out ignoredFailureCode);
        }

        public static bool TrySubmitClaimWithFailure(int[] roundState,
            string[] playerIds, int[] lifeStates, int playerCount,
            string[] commitItemIds, int[] commitGenerations, int commitCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string roundId, string eventRoundId, string itemInstanceId, int generation,
            string playerId, int frame, int requestSequence, out int failureCode)
        {
            // 0 = accepted; 1..11 name the exact refusal, in source order, so a caller
            // can say which gate refused without duplicating the validation here.
            failureCode = 0;
            if (!TryValidateRoundScalars(roundState)) { failureCode = 1; return false; }
            if (!TryValidatePlayerIdentityAndLife(playerIds, lifeStates, playerCount)) { failureCode = 2; return false; }
            if (!TryValidateCommitKeyColumns(commitItemIds, commitGenerations, commitCount)) { failureCode = 3; return false; }
            if (!TryValidateClaimColumns(claimItemIds, claimGenerations, claimPlayerIds,
                claimFrames, claimRequestSequences, claimCount))
            {
                failureCode = 4;
                return false;
            }
            if (!IsValidEventKey(roundId, eventRoundId, itemInstanceId, generation)) { failureCode = 5; return false; }
            if (playerId == null) { failureCode = 6; return false; }
            if (roundState[RoundPhaseState] != (int)LedgerPhase.Playing) { failureCode = 7; return false; }

            int playerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, playerId, out playerRow)) { failureCode = 8; return false; }
            if (lifeStates[playerRow] != (int)PlayerLifecycle.Active) { failureCode = 9; return false; }
            if (HasCommitKey(commitItemIds, commitGenerations, commitCount, itemInstanceId,
                generation))
            {
                failureCode = 10;
                return false;
            }

            for (int row = 0; row < claimCount; row++)
            {
                if (!IsEventKeyMatch(claimItemIds[row], claimGenerations[row], itemInstanceId, generation))
                    continue;
                if (string.CompareOrdinal(claimPlayerIds[row], playerId) != 0) continue;
                if (claimRequestSequences[row] != requestSequence) continue;
                return true;
            }
            if (claimCount >= claimItemIds.Length) { failureCode = 11; return false; }

            int write = claimCount;
            claimItemIds[write] = itemInstanceId;
            claimGenerations[write] = generation;
            claimPlayerIds[write] = playerId;
            claimFrames[write] = frame;
            claimRequestSequences[write] = requestSequence;
            claimCount = claimCount + 1;
            return true;
        }

        /// <summary>
        /// Awards points once per semantic key, and only once.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The key is a semantic label ("settle:first"), never a placement number: a
        /// placement can recur (reconnect, scene reload, event replay) and a numeric key
        /// would award the same bonus again. Duplicate suppression is therefore structural,
        /// not the caller's discipline.
        /// </para>
        /// <para>
        /// The awarded keys live in the caller-provided fixed arrays, which belong to the
        /// ledger's owner (the motor). Nothing outside the ledger keeps a bonus table, so
        /// there is one owner for the score.
        /// </para>
        /// <para>
        /// failureCode: 0 ok / 1 duplicateKey / 2 nonPositivePoints / 3 invalidKey /
        /// 4 capacityFull. (5 ledgerRefused belongs to the caller's row validation and is
        /// produced there, not here.)
        /// </para>
        /// <para>
        /// The new count is returned through <c>out</c>, not <c>ref</c>: only <c>out</c> has
        /// been proven to work under UdonSharp in this project, and a ref semantic that
        /// silently behaved differently in the Udon VM would corrupt the awarded-key table.
        /// </para>
        /// </remarks>
        public static bool TryAwardOnce(string[] awardedKeys, int[] awardedPoints,
            int awardedCount, string awardKey, int points, out int newCount,
            out int failureCode)
        {
            newCount = awardedCount;
            failureCode = 0;
            if (awardKey == null || awardKey.Length == 0) { failureCode = 3; return false; }
            if (points <= 0) { failureCode = 2; return false; }

            for (int i = 0; i < awardedCount; i++)
            {
                if (awardedKeys[i] == null) continue;
                if (awardedKeys[i] != awardKey) continue;
                failureCode = 1;
                return false;
            }

            if (awardedCount >= awardedKeys.Length) { failureCode = 4; return false; }

            int slot = awardedCount;
            awardedKeys[slot] = awardKey;
            awardedPoints[slot] = points;
            newCount = awardedCount + 1;
            return true;
        }

        /// <summary>
        /// Resolves one event's claim batch into at most one authoritative commit.
        /// </summary>
        /// <remarks>
        /// The model's resolve_claims(): earliest frame wins, ties break on the ordinally
        /// smaller player id and then on the smaller request sequence (the model's DRAFT
        /// stable-id policy). The winner's gain is added to unbanked confirmed volume
        /// exactly once; every claimant's prediction for that key is dropped, because the
        /// authoritative gain replaces the winner's estimate and the losers' estimates are
        /// wrong. Resolving an already committed event returns that commit's row and adds
        /// nothing. A batch with no claims produces no commit: `commitRow` is
        /// <see cref="NoRow"/> and the call still succeeds, matching the model's None
        /// return. The model's resolve has no phase gate and none is added. A negative
        /// gain is rejected here although the model forgets to check it on this path (see
        /// the module report); accepting it would drive a total below zero.
        /// </remarks>
        public static bool TryResolveClaims(int[] roundState,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] commitItemIds, int[] commitGenerations, string[] commitWinners,
            int[] commitSequences, long[] commitGains, ref int commitCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount,
            string roundId, string eventRoundId, string itemInstanceId, int generation, long gain,
            out int commitRow)
        {
            commitRow = NoRow;
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidateCommitColumns(commitItemIds, commitGenerations, commitWinners,
                commitSequences, commitGains, commitCount)) return false;
            if (!TryValidateClaimColumns(claimItemIds, claimGenerations, claimPlayerIds,
                claimFrames, claimRequestSequences, claimCount)) return false;
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) return false;
            if (!IsValidEventKey(roundId, eventRoundId, itemInstanceId, generation)) return false;
            if (gain < 0) return false;

            int existingCommit;
            if (TryFindCommitRow(commitItemIds, commitGenerations, commitCount, itemInstanceId,
                generation, out existingCommit))
            {
                commitRow = existingCommit;
                return true;
            }

            int winnerRow = NoRow;
            for (int row = 0; row < claimCount; row++)
            {
                if (!IsEventKeyMatch(claimItemIds[row], claimGenerations[row], itemInstanceId, generation))
                    continue;
                if (winnerRow == NoRow || CompareClaims(claimPlayerIds, claimFrames,
                    claimRequestSequences, row, winnerRow) < 0)
                {
                    winnerRow = row;
                }
            }
            if (winnerRow == NoRow) return true;

            string winnerId = claimPlayerIds[winnerRow];
            int winnerPlayerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, winnerId, out winnerPlayerRow)) return false;
            if (roundState[RoundCommitSequenceState] >= int.MaxValue) return false;
            if (commitCount >= commitItemIds.Length) return false;
            if (confirmedVolumes[winnerPlayerRow] > long.MaxValue - gain) return false;

            int sequence = roundState[RoundCommitSequenceState] + 1;
            for (int row = 0; row < claimCount; row++)
            {
                if (!IsEventKeyMatch(claimItemIds[row], claimGenerations[row], itemInstanceId, generation))
                    continue;
                RemovePredictionsForPlayer(predictedPlayerIds, predictedItemIds,
                    predictedGenerations, predictedGains, ref predictedCount, claimPlayerIds[row]);
            }
            RemoveClaimsForKey(claimItemIds, claimGenerations, claimPlayerIds, claimFrames,
                claimRequestSequences, ref claimCount, itemInstanceId, generation);

            int write = commitCount;
            commitItemIds[write] = itemInstanceId;
            commitGenerations[write] = generation;
            commitWinners[write] = winnerId;
            commitSequences[write] = sequence;
            commitGains[write] = gain;
            commitCount = commitCount + 1;
            confirmedVolumes[winnerPlayerRow] = confirmedVolumes[winnerPlayerRow] + gain;
            roundState[RoundCommitSequenceState] = sequence;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            commitRow = write;
            return true;
        }

        /// <summary>
        /// Replays one authority commit at a replica, idempotently.
        /// </summary>
        /// <remarks>
        /// The model's apply_commit(): a byte-identical replay of an already committed key
        /// does nothing and reports applied = false; the same key with a different winner,
        /// sequence or gain is a conflict and is rejected; a sequence that is not exactly
        /// one past the current commit sequence is a gap or reorder and is rejected; a
        /// negative gain or unknown winner is rejected. On success the commit is stored,
        /// the gain is added to the winner's unbanked confirmed volume and every player's
        /// prediction for that key is dropped (the model clears them for all players on
        /// this path). The model raises for the rejected cases; here they are false and
        /// leave the stores untouched.
        /// </remarks>
        public static bool TryApplyCommit(int[] roundState,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] commitItemIds, int[] commitGenerations, string[] commitWinners,
            int[] commitSequences, long[] commitGains, ref int commitCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount,
            string roundId, string eventRoundId, string itemInstanceId, int generation,
            string winnerId, int sequence, long gain, out bool applied)
        {
            applied = false;
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidateCommitColumns(commitItemIds, commitGenerations, commitWinners,
                commitSequences, commitGains, commitCount)) return false;
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) return false;
            if (!IsValidEventKey(roundId, eventRoundId, itemInstanceId, generation)) return false;
            if (winnerId == null || gain < 0) return false;

            int existingCommit;
            if (TryFindCommitRow(commitItemIds, commitGenerations, commitCount, itemInstanceId,
                generation, out existingCommit))
            {
                bool identical = commitSequences[existingCommit] == sequence &&
                    commitGains[existingCommit] == gain &&
                    string.CompareOrdinal(commitWinners[existingCommit], winnerId) == 0;
                return identical;
            }
            if (sequence != roundState[RoundCommitSequenceState] + 1) return false;

            int winnerPlayerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, winnerId, out winnerPlayerRow)) return false;
            if (commitCount >= commitItemIds.Length) return false;
            if (confirmedVolumes[winnerPlayerRow] > long.MaxValue - gain) return false;

            int write = commitCount;
            commitItemIds[write] = itemInstanceId;
            commitGenerations[write] = generation;
            commitWinners[write] = winnerId;
            commitSequences[write] = sequence;
            commitGains[write] = gain;
            commitCount = commitCount + 1;
            confirmedVolumes[winnerPlayerRow] = confirmedVolumes[winnerPlayerRow] + gain;
            RemovePredictionsForKey(predictedPlayerIds, predictedItemIds, predictedGenerations,
                predictedGains, ref predictedCount, itemInstanceId, generation);
            roundState[RoundCommitSequenceState] = sequence;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            applied = true;
            return true;
        }

        /// <summary>
        /// Converts confirmed unbanked volume into banked score, once per request id.
        /// </summary>
        /// <remarks>
        /// The model's bank(): one atomic transaction that moves exactly `amount` from
        /// unbanked confirmed volume to banked score, never more than the confirmed
        /// amount, and only while Playing from an Active player. The (player, request id)
        /// pair is the idempotency marker: a repeated request is refused and moves
        /// nothing, so a retried deposit cannot double count. Only confirmed volume is
        /// spendable, which is why a prediction cannot be banked. A rejected request is
        /// not recorded, so the same id may be retried later with corrected inputs, as in
        /// the model.
        /// </remarks>
        public static bool TryBank(int[] roundState,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] bankRequestPlayerIds, string[] bankRequestIds, ref int bankRequestCount,
            string playerId, long amount, string requestId)
        {
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidateBankRequestColumns(bankRequestPlayerIds, bankRequestIds, bankRequestCount))
                return false;
            if (playerId == null || requestId == null || requestId.Length == 0) return false;
            if (roundState[RoundPhaseState] != (int)LedgerPhase.Playing) return false;

            for (int row = 0; row < bankRequestCount; row++)
            {
                if (string.CompareOrdinal(bankRequestPlayerIds[row], playerId) != 0) continue;
                if (string.CompareOrdinal(bankRequestIds[row], requestId) != 0) continue;
                return false;
            }

            int playerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, playerId, out playerRow)) return false;
            if (lifeStates[playerRow] != (int)PlayerLifecycle.Active) return false;
            if (amount <= 0 || amount > confirmedVolumes[playerRow]) return false;
            if (bankRequestCount >= bankRequestPlayerIds.Length) return false;
            if (bankedScores[playerRow] > long.MaxValue - amount) return false;

            int write = bankRequestCount;
            bankRequestPlayerIds[write] = playerId;
            bankRequestIds[write] = requestId;
            bankRequestCount = bankRequestCount + 1;
            confirmedVolumes[playerRow] = confirmedVolumes[playerRow] - amount;
            bankedScores[playerRow] = bankedScores[playerRow] + amount;
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            return true;
        }

        /// <summary>
        /// Performs one KO transaction: the victim's unbanked value is lost, the banked
        /// score is kept, and an attacker may receive a bounded share.
        /// </summary>
        /// <remarks>
        /// The model's ko(): only while Playing against an Active victim. The victim's
        /// unbanked confirmed volume becomes zero, all its predictions are dropped, it
        /// enters RespawnPending and its player generation is bumped. Banked score is
        /// never touched. The attacker share is `min(lost, attackerShare)` and is moved
        /// to the attacker's unbanked confirmed volume, so a KO never creates value; the
        /// default share of zero means the lost value simply disappears, which is the
        /// model's explicitly configured placeholder and not a settlement decision made
        /// here. A null or empty attacker id means "no attacker", matching the model's
        /// falsy optional; an attacker id equal to the victim or absent from the roster
        /// gets no share. Negative placeholder values are rejected (the model raises).
        /// </remarks>
        public static bool TryKo(int[] roundState,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount,
            string victimId, string attackerId, float now, float respawnDelay, long attackerShare)
        {
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) return false;
            if (victimId == null) return false;
            if (!KatamariMath.IsFiniteFloat(now)) return false;
            if (!KatamariMath.IsFiniteFloat(respawnDelay) || respawnDelay < 0f) return false;
            if (attackerShare < 0) return false;
            if (roundState[RoundPhaseState] != (int)LedgerPhase.Playing) return false;

            int victimRow;
            if (!TryFindPlayerRow(playerIds, playerCount, victimId, out victimRow)) return false;
            if (lifeStates[victimRow] != (int)PlayerLifecycle.Active) return false;
            if (playerGenerations[victimRow] >= int.MaxValue) return false;

            long lost = confirmedVolumes[victimRow];
            long share = 0L;
            int attackerRow = NoRow;
            if (attackerId != null && attackerId.Length > 0 &&
                string.CompareOrdinal(attackerId, victimId) != 0)
            {
                if (TryFindPlayerRow(playerIds, playerCount, attackerId, out attackerRow))
                {
                    share = attackerShare < lost ? attackerShare : lost;
                    if (confirmedVolumes[attackerRow] > long.MaxValue - share) return false;
                }
            }
            float respawnAtValue = now + respawnDelay;
            if (!KatamariMath.IsFiniteFloat(respawnAtValue)) return false;

            confirmedVolumes[victimRow] = 0;
            RemovePredictionsForPlayer(predictedPlayerIds, predictedItemIds, predictedGenerations,
                predictedGains, ref predictedCount, victimId);
            lifeStates[victimRow] = (int)PlayerLifecycle.RespawnPending;
            respawnAtSet[victimRow] = true;
            respawnAt[victimRow] = respawnAtValue;
            playerGenerations[victimRow] = playerGenerations[victimRow] + 1;
            if (attackerRow != NoRow)
            {
                confirmedVolumes[attackerRow] = confirmedVolumes[attackerRow] + share;
            }
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            return true;
        }

        /// <summary>
        /// Returns a RespawnPending player to Active once its respawn time has passed.
        /// </summary>
        /// <remarks>
        /// The model's respawn(): the player must be RespawnPending with a respawn time
        /// that is not None, and `now` must have reached it. Predictions are dropped and
        /// the timer is cleared. The model's protection-duration placeholder is never read
        /// by its body, so no protection parameter exists here.
        /// </remarks>
        public static bool TryRespawn(int[] roundState,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount, string playerId, float now)
        {
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) return false;
            if (playerId == null || !KatamariMath.IsFiniteFloat(now)) return false;

            int playerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, playerId, out playerRow)) return false;
            if (lifeStates[playerRow] != (int)PlayerLifecycle.RespawnPending) return false;
            if (!respawnAtSet[playerRow]) return false;
            if (now < respawnAt[playerRow]) return false;

            lifeStates[playerRow] = (int)PlayerLifecycle.Active;
            respawnAtSet[playerRow] = false;
            respawnAt[playerRow] = 0f;
            RemovePredictionsForPlayer(predictedPlayerIds, predictedItemIds, predictedGenerations,
                predictedGains, ref predictedCount, playerId);
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            return true;
        }

        /// <summary>
        /// Marks a player Disconnected and removes its transient claim and prediction rows.
        /// </summary>
        /// <remarks>
        /// The model's disconnect(): confirmed volume and banked score are preserved (the
        /// README is explicit that a temporary disconnect must not delete banked score),
        /// predictions are dropped, the respawn timer is cleared and the player's pending
        /// claims are removed from every batch. The player row itself is kept, so a later
        /// rejoin of the same id does not get a fresh ledger. Reconnection epochs are
        /// undecided in the model and are not invented here.
        /// </remarks>
        public static bool TryDisconnect(int[] roundState,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations, int playerCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount, string playerId)
        {
            if (!TryValidateRoundScalars(roundState)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidateClaimColumns(claimItemIds, claimGenerations, claimPlayerIds,
                claimFrames, claimRequestSequences, claimCount)) return false;
            if (!TryValidatePredictionColumns(predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, predictedCount)) return false;
            if (playerId == null) return false;

            int playerRow;
            if (!TryFindPlayerRow(playerIds, playerCount, playerId, out playerRow)) return false;

            lifeStates[playerRow] = (int)PlayerLifecycle.Disconnected;
            respawnAtSet[playerRow] = false;
            respawnAt[playerRow] = 0f;
            RemovePredictionsForPlayer(predictedPlayerIds, predictedItemIds, predictedGenerations,
                predictedGains, ref predictedCount, playerId);
            RemoveClaimsForPlayer(claimItemIds, claimGenerations, claimPlayerIds, claimFrames,
                claimRequestSequences, ref claimCount, playerId);
            roundState[RoundRevisionState] = roundState[RoundRevisionState] + 1;
            return true;
        }

        /// <summary>
        /// Atomically replaces the replica's committed view with one complete snapshot.
        /// </summary>
        /// <remarks>
        /// The model's install_snapshot(): the snapshot must belong to this round; then the
        /// phase, deadline, revision and commit sequence are adopted, the roster and commit
        /// store are replaced wholesale, and claims and predictions are dropped. Only the
        /// committed view crosses over: a late joiner sees exactly the confirmed volumes,
        /// banked scores and life states in the snapshot, never another client's
        /// predictions. The snapshot's roster carries no respawn time and no player
        /// generation, so installed players start with no active timer and generation 0 -
        /// the model loses those fields the same way, which is why a RespawnPending entry
        /// restored from a snapshot cannot be respawned until the authority supplies a new
        /// time. Bank-request markers are deliberately not cleared, as in the model.
        /// Duplicate roster ids or duplicate commit keys cannot be represented without
        /// silently dropping a row (the model's dicts would), so they are rejected.
        /// </remarks>
        public static bool TryInstallSnapshot(int[] roundState, bool[] roundFlags,
            float[] roundClock,
            string[] playerIds, long[] confirmedVolumes, long[] bankedScores, int[] lifeStates,
            bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations,
            ref int playerCount,
            string[] commitItemIds, int[] commitGenerations, string[] commitWinners,
            int[] commitSequences, long[] commitGains, ref int commitCount,
            ref int predictedCount, ref int claimCount,
            string roundId, string snapshotRoundId,
            int[] snapshotRoundState, bool[] snapshotRoundFlags, float[] snapshotRoundClock,
            string[] snapshotPlayerIds, long[] snapshotConfirmedVolumes, long[] snapshotBankedScores,
            int[] snapshotLifeStates, int snapshotPlayerCount,
            string[] snapshotCommitItemIds, int[] snapshotCommitGenerations,
            string[] snapshotCommitWinners, int[] snapshotCommitSequences,
            long[] snapshotCommitGains, int snapshotCommitCount)
        {
            if (!TryValidateRoundStore(roundState, roundFlags, roundClock)) return false;
            if (!TryValidatePlayerColumns(playerIds, confirmedVolumes, bankedScores, lifeStates,
                respawnAtSet, respawnAt, playerGenerations, playerCount)) return false;
            if (!TryValidateCommitColumns(commitItemIds, commitGenerations, commitWinners,
                commitSequences, commitGains, commitCount)) return false;
            if (predictedCount < 0 || claimCount < 0) return false;
            if (roundId == null || snapshotRoundId == null) return false;
            if (string.CompareOrdinal(roundId, snapshotRoundId) != 0) return false;
            if (!TryValidateRoundStore(snapshotRoundState, snapshotRoundFlags, snapshotRoundClock))
                return false;
            if (!TryValidateSnapshotPlayerColumns(snapshotPlayerIds, snapshotConfirmedVolumes,
                snapshotBankedScores, snapshotLifeStates, snapshotPlayerCount)) return false;
            if (!TryValidateSnapshotCommitColumns(snapshotCommitItemIds, snapshotCommitGenerations,
                snapshotCommitWinners, snapshotCommitSequences, snapshotCommitGains,
                snapshotCommitCount)) return false;
            if (snapshotPlayerCount > playerIds.Length) return false;
            if (snapshotCommitCount > commitItemIds.Length) return false;

            roundState[RoundPhaseState] = snapshotRoundState[RoundPhaseState];
            roundState[RoundCommitSequenceState] = snapshotRoundState[RoundCommitSequenceState];
            roundState[RoundRevisionState] = snapshotRoundState[RoundRevisionState];
            roundFlags[RoundDeadlineSetState] = snapshotRoundFlags[RoundDeadlineSetState];
            roundClock[RoundDeadlineAtClock] = snapshotRoundClock[RoundDeadlineAtClock];

            for (int row = 0; row < snapshotPlayerCount; row++)
            {
                playerIds[row] = snapshotPlayerIds[row];
                confirmedVolumes[row] = snapshotConfirmedVolumes[row];
                bankedScores[row] = snapshotBankedScores[row];
                lifeStates[row] = snapshotLifeStates[row];
                respawnAtSet[row] = false;
                respawnAt[row] = 0f;
                playerGenerations[row] = 0;
            }
            playerCount = snapshotPlayerCount;

            for (int row = 0; row < snapshotCommitCount; row++)
            {
                commitItemIds[row] = snapshotCommitItemIds[row];
                commitGenerations[row] = snapshotCommitGenerations[row];
                commitWinners[row] = snapshotCommitWinners[row];
                commitSequences[row] = snapshotCommitSequences[row];
                commitGains[row] = snapshotCommitGains[row];
            }
            commitCount = snapshotCommitCount;
            predictedCount = 0;
            claimCount = 0;
            return true;
        }

        /// <summary>
        /// The phase a timeout advances to, or <see cref="NoPhase"/> when the phase has
        /// no automatic successor.
        /// </summary>
        private static int GetTimeoutTarget(LedgerPhase phase)
        {
            switch (phase)
            {
                case LedgerPhase.Ready:
                    return (int)LedgerPhase.Preparing;
                case LedgerPhase.Preparing:
                    return (int)LedgerPhase.Playing;
                case LedgerPhase.Playing:
                    return (int)LedgerPhase.Settling;
                case LedgerPhase.Settling:
                    return (int)LedgerPhase.Results;
                case LedgerPhase.Results:
                    return (int)LedgerPhase.Lobby;
                default:
                    return NoPhase;
            }
        }

        private static bool TryValidateRoundScalars(int[] roundState)
        {
            if (roundState == null || roundState.Length < RoundStateCount) return false;
            int phase = roundState[RoundPhaseState];
            if (phase < (int)LedgerPhase.Lobby || phase > (int)LedgerPhase.Results) return false;
            if (roundState[RoundCommitSequenceState] < 0) return false;
            return roundState[RoundRevisionState] >= 0;
        }

        private static bool TryValidateRoundStore(int[] roundState, bool[] roundFlags,
            float[] roundClock)
        {
            if (!TryValidateRoundScalars(roundState)) return false;
            if (roundFlags == null || roundFlags.Length < RoundFlagCount) return false;
            if (roundClock == null || roundClock.Length < RoundClockCount) return false;
            if (roundFlags[RoundDeadlineSetState] &&
                !KatamariMath.IsFiniteFloat(roundClock[RoundDeadlineAtClock])) return false;
            return true;
        }

        private static bool TryValidatePlayerIdentity(string[] playerIds, int playerCount)
        {
            if (playerIds == null) return false;
            if (playerCount < 0 || playerCount > playerIds.Length) return false;
            for (int row = 0; row < playerCount; row++)
            {
                if (playerIds[row] == null) return false;
            }
            return true;
        }

        private static bool TryValidatePlayerIdentityAndLife(string[] playerIds, int[] lifeStates,
            int playerCount)
        {
            if (!TryValidatePlayerIdentity(playerIds, playerCount)) return false;
            if (lifeStates == null || lifeStates.Length != playerIds.Length) return false;
            for (int row = 0; row < playerCount; row++)
            {
                if (lifeStates[row] < (int)PlayerLifecycle.Active ||
                    lifeStates[row] > (int)PlayerLifecycle.Disconnected) return false;
            }
            return true;
        }

        private static bool TryValidatePlayerColumns(string[] playerIds, long[] confirmedVolumes,
            long[] bankedScores, int[] lifeStates, bool[] respawnAtSet, float[] respawnAt,
            int[] playerGenerations, int playerCount)
        {
            if (!TryValidatePlayerIdentityAndLife(playerIds, lifeStates, playerCount)) return false;
            if (confirmedVolumes == null || bankedScores == null) return false;
            if (respawnAtSet == null || respawnAt == null || playerGenerations == null) return false;
            int capacity = playerIds.Length;
            if (confirmedVolumes.Length != capacity || bankedScores.Length != capacity ||
                respawnAtSet.Length != capacity || respawnAt.Length != capacity ||
                playerGenerations.Length != capacity) return false;
            for (int row = 0; row < playerCount; row++)
            {
                // A stored total must be a real non-negative count; a corrupt row would
                // otherwise let a later deposit or gain wrap.
                if (confirmedVolumes[row] < 0 || bankedScores[row] < 0) return false;
                if (playerGenerations[row] < 0) return false;
                if (respawnAtSet[row] && !KatamariMath.IsFiniteFloat(respawnAt[row])) return false;
            }
            return true;
        }

        private static bool TryValidateCommitKeyColumns(string[] commitItemIds,
            int[] commitGenerations, int commitCount)
        {
            if (commitItemIds == null || commitGenerations == null) return false;
            if (commitItemIds.Length != commitGenerations.Length) return false;
            if (commitCount < 0 || commitCount > commitItemIds.Length) return false;
            for (int row = 0; row < commitCount; row++)
            {
                if (commitItemIds[row] == null) return false;
                if (commitGenerations[row] < 0) return false;
            }
            return true;
        }

        private static bool TryValidateCommitColumns(string[] commitItemIds,
            int[] commitGenerations, string[] commitWinners, int[] commitSequences,
            long[] commitGains, int commitCount)
        {
            if (!TryValidateCommitKeyColumns(commitItemIds, commitGenerations, commitCount))
                return false;
            if (commitWinners == null || commitSequences == null || commitGains == null) return false;
            int capacity = commitItemIds.Length;
            if (commitWinners.Length != capacity || commitSequences.Length != capacity ||
                commitGains.Length != capacity) return false;
            for (int row = 0; row < commitCount; row++)
            {
                if (commitWinners[row] == null) return false;
                if (commitSequences[row] < 0 || commitGains[row] < 0) return false;
            }
            return true;
        }

        private static bool TryValidateClaimColumns(string[] claimItemIds,
            int[] claimGenerations, string[] claimPlayerIds, int[] claimFrames,
            int[] claimRequestSequences, int claimCount)
        {
            if (claimItemIds == null || claimGenerations == null || claimPlayerIds == null)
                return false;
            if (claimFrames == null || claimRequestSequences == null) return false;
            int capacity = claimItemIds.Length;
            if (claimGenerations.Length != capacity || claimPlayerIds.Length != capacity ||
                claimFrames.Length != capacity || claimRequestSequences.Length != capacity)
                return false;
            if (claimCount < 0 || claimCount > capacity) return false;
            for (int row = 0; row < claimCount; row++)
            {
                if (claimItemIds[row] == null || claimPlayerIds[row] == null) return false;
                if (claimGenerations[row] < 0) return false;
            }
            return true;
        }

        private static bool TryValidatePredictionColumns(string[] predictedPlayerIds,
            string[] predictedItemIds, int[] predictedGenerations, long[] predictedGains,
            int predictedCount)
        {
            if (predictedPlayerIds == null || predictedItemIds == null) return false;
            if (predictedGenerations == null || predictedGains == null) return false;
            int capacity = predictedPlayerIds.Length;
            if (predictedItemIds.Length != capacity || predictedGenerations.Length != capacity ||
                predictedGains.Length != capacity) return false;
            if (predictedCount < 0 || predictedCount > capacity) return false;
            for (int row = 0; row < predictedCount; row++)
            {
                if (predictedPlayerIds[row] == null || predictedItemIds[row] == null) return false;
                if (predictedGenerations[row] < 0 || predictedGains[row] < 0) return false;
            }
            return true;
        }

        private static bool TryValidateBankRequestColumns(string[] bankRequestPlayerIds,
            string[] bankRequestIds, int bankRequestCount)
        {
            if (bankRequestPlayerIds == null || bankRequestIds == null) return false;
            int capacity = bankRequestPlayerIds.Length;
            if (bankRequestIds.Length != capacity) return false;
            if (bankRequestCount < 0 || bankRequestCount > capacity) return false;
            for (int row = 0; row < bankRequestCount; row++)
            {
                if (bankRequestPlayerIds[row] == null || bankRequestIds[row] == null) return false;
            }
            return true;
        }

        private static bool TryValidateAwardColumns(string[] awardedKeys, int[] awardedPoints,
            int awardedCount)
        {
            // The keys and points are the caller's award table, not a ledger store
            // (TryAwardOnce owns its rule), so only shape and range are checked here:
            // a round reset must clear the table, not judge its contents.
            if (awardedKeys == null || awardedPoints == null) return false;
            if (awardedKeys.Length != awardedPoints.Length) return false;
            if (awardedCount < 0 || awardedCount > awardedKeys.Length) return false;
            return true;
        }

        private static bool TryValidateSnapshotPlayerColumns(string[] snapshotPlayerIds,
            long[] snapshotConfirmedVolumes, long[] snapshotBankedScores,
            int[] snapshotLifeStates, int snapshotPlayerCount)
        {
            if (snapshotPlayerIds == null || snapshotConfirmedVolumes == null) return false;
            if (snapshotBankedScores == null || snapshotLifeStates == null) return false;
            int capacity = snapshotPlayerIds.Length;
            if (snapshotConfirmedVolumes.Length != capacity ||
                snapshotBankedScores.Length != capacity || snapshotLifeStates.Length != capacity)
                return false;
            if (snapshotPlayerCount < 0 || snapshotPlayerCount > capacity) return false;
            for (int row = 0; row < snapshotPlayerCount; row++)
            {
                if (snapshotPlayerIds[row] == null) return false;
                if (snapshotConfirmedVolumes[row] < 0 || snapshotBankedScores[row] < 0) return false;
                if (snapshotLifeStates[row] < (int)PlayerLifecycle.Active ||
                    snapshotLifeStates[row] > (int)PlayerLifecycle.Disconnected) return false;
                for (int earlier = 0; earlier < row; earlier++)
                {
                    if (string.CompareOrdinal(snapshotPlayerIds[earlier], snapshotPlayerIds[row]) == 0)
                        return false;
                }
            }
            return true;
        }

        private static bool TryValidateSnapshotCommitColumns(string[] snapshotCommitItemIds,
            int[] snapshotCommitGenerations, string[] snapshotCommitWinners,
            int[] snapshotCommitSequences, long[] snapshotCommitGains, int snapshotCommitCount)
        {
            if (!TryValidateCommitKeyColumns(snapshotCommitItemIds, snapshotCommitGenerations,
                snapshotCommitCount)) return false;
            if (snapshotCommitWinners == null || snapshotCommitSequences == null ||
                snapshotCommitGains == null) return false;
            int capacity = snapshotCommitItemIds.Length;
            if (snapshotCommitWinners.Length != capacity ||
                snapshotCommitSequences.Length != capacity ||
                snapshotCommitGains.Length != capacity) return false;
            for (int row = 0; row < snapshotCommitCount; row++)
            {
                if (snapshotCommitWinners[row] == null) return false;
                if (snapshotCommitSequences[row] < 0 || snapshotCommitGains[row] < 0) return false;
                for (int earlier = 0; earlier < row; earlier++)
                {
                    if (IsSameCommitKey(snapshotCommitItemIds[earlier],
                        snapshotCommitGenerations[earlier], snapshotCommitItemIds[row],
                        snapshotCommitGenerations[row])) return false;
                }
            }
            return true;
        }

        private static bool TryFindPlayerRow(string[] playerIds, int playerCount,
            string playerId, out int playerRow)
        {
            return TryFindPlayer(playerIds, playerCount, playerId, out playerRow);
        }

        private static bool HasPlayer(string[] playerIds, int playerCount, string playerId)
        {
            int playerRow;
            return TryFindPlayer(playerIds, playerCount, playerId, out playerRow);
        }

        private static bool HasCommitKey(string[] commitItemIds, int[] commitGenerations,
            int commitCount, string itemInstanceId, int generation)
        {
            int commitRow;
            return TryFindCommitRow(commitItemIds, commitGenerations, commitCount, itemInstanceId,
                generation, out commitRow);
        }

        private static bool IsEventKeyMatch(string itemId, int itemGeneration,
            string keyItemId, int keyGeneration)
        {
            if (itemGeneration != keyGeneration) return false;
            return string.CompareOrdinal(itemId, keyItemId) == 0;
        }

        private static int CompareClaims(string[] claimPlayerIds, int[] claimFrames,
            int[] claimRequestSequences, int row, int earlier)
        {
            if (claimFrames[row] != claimFrames[earlier]) return claimFrames[row] < claimFrames[earlier] ? -1 : 1;
            int byPlayer = string.CompareOrdinal(claimPlayerIds[row], claimPlayerIds[earlier]);
            if (byPlayer != 0) return byPlayer < 0 ? -1 : 1;
            if (claimRequestSequences[row] == claimRequestSequences[earlier]) return 0;
            return claimRequestSequences[row] < claimRequestSequences[earlier] ? -1 : 1;
        }

        private static bool IsSameCommitKey(string commitItemId, int commitGeneration,
            string otherItemId, int otherGeneration)
        {
            if (commitGeneration != otherGeneration) return false;
            return string.CompareOrdinal(commitItemId, otherItemId) == 0;
        }

        private static bool TryFindCommitRow(string[] commitItemIds, int[] commitGenerations,
            int commitCount, string itemInstanceId, int generation, out int commitRow)
        {
            commitRow = NoRow;
            for (int row = 0; row < commitCount; row++)
            {
                if (!IsSameCommitKey(commitItemIds[row], commitGenerations[row],
                    itemInstanceId, generation)) continue;
                commitRow = row;
                return true;
            }
            return false;
        }

        private static int FindPredictionRow(string[] predictedPlayerIds,
            string[] predictedItemIds, int[] predictedGenerations, int predictedCount,
            string playerId, string itemInstanceId, int generation)
        {
            for (int row = 0; row < predictedCount; row++)
            {
                if (predictedGenerations[row] != generation) continue;
                if (string.CompareOrdinal(predictedItemIds[row], itemInstanceId) != 0) continue;
                if (string.CompareOrdinal(predictedPlayerIds[row], playerId) != 0) continue;
                return row;
            }
            return NoRow;
        }

        private static void RemovePredictionsForPlayer(string[] predictedPlayerIds,
            string[] predictedItemIds, int[] predictedGenerations, long[] predictedGains,
            ref int predictedCount, string playerId)
        {
            int write = 0;
            for (int read = 0; read < predictedCount; read++)
            {
                if (string.CompareOrdinal(predictedPlayerIds[read], playerId) == 0) continue;
                if (write != read)
                {
                    predictedPlayerIds[write] = predictedPlayerIds[read];
                    predictedItemIds[write] = predictedItemIds[read];
                    predictedGenerations[write] = predictedGenerations[read];
                    predictedGains[write] = predictedGains[read];
                }
                write = write + 1;
            }
            predictedCount = write;
        }

        private static void RemovePredictionsForKey(string[] predictedPlayerIds,
            string[] predictedItemIds, int[] predictedGenerations, long[] predictedGains,
            ref int predictedCount, string itemInstanceId, int generation)
        {
            int write = 0;
            for (int read = 0; read < predictedCount; read++)
            {
                if (IsEventKeyMatch(predictedItemIds[read], predictedGenerations[read],
                    itemInstanceId, generation)) continue;
                if (write != read)
                {
                    predictedPlayerIds[write] = predictedPlayerIds[read];
                    predictedItemIds[write] = predictedItemIds[read];
                    predictedGenerations[write] = predictedGenerations[read];
                    predictedGains[write] = predictedGains[read];
                }
                write = write + 1;
            }
            predictedCount = write;
        }

        private static void RemoveClaimsForKey(string[] claimItemIds, int[] claimGenerations,
            string[] claimPlayerIds, int[] claimFrames, int[] claimRequestSequences,
            ref int claimCount, string itemInstanceId, int generation)
        {
            int write = 0;
            for (int read = 0; read < claimCount; read++)
            {
                if (IsEventKeyMatch(claimItemIds[read], claimGenerations[read], itemInstanceId,
                    generation)) continue;
                if (write != read)
                {
                    claimItemIds[write] = claimItemIds[read];
                    claimGenerations[write] = claimGenerations[read];
                    claimPlayerIds[write] = claimPlayerIds[read];
                    claimFrames[write] = claimFrames[read];
                    claimRequestSequences[write] = claimRequestSequences[read];
                }
                write = write + 1;
            }
            claimCount = write;
        }

        private static void RemoveClaimsForPlayer(string[] claimItemIds, int[] claimGenerations,
            string[] claimPlayerIds, int[] claimFrames, int[] claimRequestSequences,
            ref int claimCount, string playerId)
        {
            int write = 0;
            for (int read = 0; read < claimCount; read++)
            {
                if (string.CompareOrdinal(claimPlayerIds[read], playerId) == 0) continue;
                if (write != read)
                {
                    claimItemIds[write] = claimItemIds[read];
                    claimGenerations[write] = claimGenerations[read];
                    claimPlayerIds[write] = claimPlayerIds[read];
                    claimFrames[write] = claimFrames[read];
                    claimRequestSequences[write] = claimRequestSequences[read];
                }
                write = write + 1;
            }
            claimCount = write;
        }
    }
}
