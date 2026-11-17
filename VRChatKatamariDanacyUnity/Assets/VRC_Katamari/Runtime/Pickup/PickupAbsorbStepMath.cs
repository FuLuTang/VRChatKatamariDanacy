using UnityEngine;
using VRCKatamari.Economy;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Pickup
{
    /// <summary>Value-only pickup stages. Callers own all queue, ledger and scratch columns.</summary>
    public static class PickupAbsorbStepMath
    {
        /// <summary>Evaluates one snapshot against the collision sphere and returns the gate's diagnostic distances.</summary>
        public static AbsorbOutcome EvaluateCandidate(bool absorbable, string kind,
            string[] allowedKinds, int allowedKindCount, Vector3 size, Quaternion rotation,
            Vector3 position, Vector3 center, float collisionRadius, float contactRadius,
            float volumeK, float semanticDiameter, float maxCollisionRadius, long growthMm3,
            out float boxDistance, out float contactLimit)
        {
            AbsorbOutcome outcome = PickupAbsorbMath.EvaluateAbsorbWithValue(absorbable,
                kind, allowedKinds, allowedKindCount, size, rotation, position, center,
                collisionRadius, contactRadius, volumeK, semanticDiameter, maxCollisionRadius,
                growthMm3);
            if (!PickupAbsorbMath.TryComputeContact(size, rotation, position, center,
                collisionRadius, contactRadius, out boxDistance, out contactLimit))
            {
                boxDistance = -1f;
                contactLimit = -1f;
            }
            return outcome;
        }

        /// <summary>Evaluates the whole queue with one pre-growth ball size. Output columns match candidate indices.</summary>
        public static void EvaluatePendingCandidates(bool[] absorbable, string[] kinds,
            Vector3[] sizes, Quaternion[] rotations, Vector3[] positions, long[] growthMm3,
            float[] volumeK, int count, string[] allowedKinds, int allowedKindCount,
            Vector3 center, float collisionRadius, float contactRadius, float semanticDiameter,
            float maxCollisionRadius, bool[] accepted, int[] outcomes, float[] boxDistances,
            float[] contactLimits)
        {
            for (int i = 0; i < count; i++)
            {
                AbsorbOutcome outcome = EvaluateCandidate(absorbable[i], kinds[i], allowedKinds,
                    allowedKindCount, sizes[i], rotations[i], positions[i], center,
                    collisionRadius, contactRadius, volumeK[i], semanticDiameter,
                    maxCollisionRadius, growthMm3[i], out boxDistances[i], out contactLimits[i]);
                outcomes[i] = (int)outcome;
                accepted[i] = outcome == AbsorbOutcome.Absorb;
            }
        }

        /// <summary>
        /// Commits accepted candidates in queue order, without resizing the ball or changing candidate snapshots.
        /// Committed marks the rows to record as contact events and then remove. False means no local ledger row.
        /// Failure columns describe the same candidate row; a negative total refuses silently.
        /// claimCountsAtFailure holds, for each row with a logged failure, the claim count at the
        /// moment that row failed (0 for every other row), so later logging sees the per-row value.
        /// </summary>
        public static bool CommitAcceptedAbsorbs(bool hasLocalRow, bool[] accepted,
            string[] itemIds, int[] generations, long[] growthMm3, int count,
            int[] roundState, string[] playerIds, long[] confirmedVolumes, long[] bankedScores,
            int[] lifeStates, bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations,
            int playerCount, string[] commitItemIds, int[] commitGenerations,
            string[] commitWinners, int[] commitSequences, long[] commitGains, ref int commitCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount, string roundId, string localPlayerId,
            int localRow, int authorityFrame, ref int requestSequence,
            ref long unbankedVolumeUnits, ref long bankedScoreUnits, ref int absorbedCount,
            bool[] committed, long[] gains, bool[] logFailures, int[] claimFailures,
            int[] resolveFailures, int[] claimCountsAtFailure)
        {
            if (!hasLocalRow) return false;
            for (int i = 0; i < count; i++)
            {
                committed[i] = false;
                gains[i] = 0L;
                logFailures[i] = false;
                claimFailures[i] = 0;
                resolveFailures[i] = 0;
                claimCountsAtFailure[i] = 0;
                if (!accepted[i]) continue;
                long gain;
                if (!PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(growthMm3[i], out gain))
                {
                    logFailures[i] = true;
                    claimCountsAtFailure[i] = claimCount;
                    continue;
                }
                gains[i] = gain;
                int commitRow;
                committed[i] = TryCommitAbsorb(itemIds[i], generations[i], gain, roundState,
                    playerIds, confirmedVolumes, bankedScores, lifeStates, respawnAtSet,
                    respawnAt, playerGenerations, playerCount, commitItemIds, commitGenerations,
                    commitWinners, commitSequences, commitGains, ref commitCount, claimItemIds,
                    claimGenerations, claimPlayerIds, claimFrames, claimRequestSequences,
                    ref claimCount, predictedPlayerIds, predictedItemIds, predictedGenerations,
                    predictedGains, ref predictedCount, roundId, localPlayerId, localRow,
                    authorityFrame, ref requestSequence, ref unbankedVolumeUnits,
                    ref bankedScoreUnits, out claimFailures[i], out resolveFailures[i],
                    out commitRow, out logFailures[i]);
                if (logFailures[i]) claimCountsAtFailure[i] = claimCount;
                if (committed[i]) absorbedCount = absorbedCount + 1;
            }
            return true;
        }

        /// <summary>Submits one authority claim, resolves it and reads back only confirmed totals.</summary>
        public static bool TryCommitAbsorb(string itemInstanceId, int generation, long gain,
            int[] roundState, string[] playerIds, long[] confirmedVolumes, long[] bankedScores,
            int[] lifeStates, bool[] respawnAtSet, float[] respawnAt, int[] playerGenerations,
            int playerCount, string[] commitItemIds, int[] commitGenerations,
            string[] commitWinners, int[] commitSequences, long[] commitGains, ref int commitCount,
            string[] claimItemIds, int[] claimGenerations, string[] claimPlayerIds,
            int[] claimFrames, int[] claimRequestSequences, ref int claimCount,
            string[] predictedPlayerIds, string[] predictedItemIds, int[] predictedGenerations,
            long[] predictedGains, ref int predictedCount, string roundId, string localPlayerId,
            int localRow, int authorityFrame, ref int requestSequence,
            ref long unbankedVolumeUnits, ref long bankedScoreUnits, out int claimFailure,
            out int resolveFailure, out int commitRow, out bool logFailure)
        {
            requestSequence = requestSequence + 1;
            claimFailure = 0;
            resolveFailure = 0;
            commitRow = GrowthLedgerMath.NoRow;
            logFailure = false;
            if (!GrowthLedgerMath.TrySubmitClaimWithFailure(roundState, playerIds, lifeStates,
                playerCount, commitItemIds, commitGenerations, commitCount, claimItemIds,
                claimGenerations, claimPlayerIds, claimFrames, claimRequestSequences,
                ref claimCount, roundId, roundId, itemInstanceId, generation,
                localPlayerId, authorityFrame, requestSequence, out claimFailure))
            {
                logFailure = true;
                return false;
            }
            if (!GrowthLedgerMath.TryResolveClaims(roundState, playerIds, confirmedVolumes,
                bankedScores, lifeStates, respawnAtSet, respawnAt, playerGenerations,
                playerCount, commitItemIds, commitGenerations, commitWinners,
                commitSequences, commitGains, ref commitCount, claimItemIds,
                claimGenerations, claimPlayerIds, claimFrames, claimRequestSequences,
                ref claimCount, predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, ref predictedCount, roundId, roundId,
                itemInstanceId, generation, gain, out commitRow))
            {
                resolveFailure = 1;
                logFailure = true;
                return false;
            }
            if (commitRow == GrowthLedgerMath.NoRow)
            {
                logFailure = true;
                return false;
            }
            long confirmed = confirmedVolumes[localRow];
            long banked = bankedScores[localRow];
            if (confirmed < 0 || banked < 0) return false;
            unbankedVolumeUnits = confirmed;
            bankedScoreUnits = banked;
            return true;
        }

        /// <summary>Copies all columns of one pending snapshot; same-slot copy is a no-op.</summary>
        public static void CopyPendingCandidate(int write, int read, bool[] absorbable,
            string[] kinds, Vector3[] sizes, Quaternion[] rotations, Vector3[] positions,
            long[] growthMm3, float[] volumeK, string[] itemIds, int[] generations,
            bool[] accepted, float[] submitTimes)
        {
            if (write == read) return;
            absorbable[write] = absorbable[read];
            kinds[write] = kinds[read];
            sizes[write] = sizes[read];
            rotations[write] = rotations[read];
            positions[write] = positions[read];
            growthMm3[write] = growthMm3[read];
            volumeK[write] = volumeK[read];
            itemIds[write] = itemIds[read];
            generations[write] = generations[read];
            accepted[write] = accepted[read];
            submitTimes[write] = submitTimes[read];
        }

        /// <summary>Removes committed rows in place and preserves the order and every column of survivors.</summary>
        public static void CompactPendingCandidates(bool[] committed, ref int count,
            bool[] absorbable, string[] kinds, Vector3[] sizes, Quaternion[] rotations,
            Vector3[] positions, long[] growthMm3, float[] volumeK, string[] itemIds,
            int[] generations, bool[] accepted, float[] submitTimes)
        {
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                if (committed[read]) continue;
                CopyPendingCandidate(write, read, absorbable, kinds, sizes, rotations,
                    positions, growthMm3, volumeK, itemIds, generations, accepted, submitTimes);
                write = write + 1;
            }
            count = write;
        }

        /// <summary>Derives the three sizes; invalid input holds the caller's previous sizes.</summary>
        public static bool DeriveSize(long startVolumeMm3, long confirmedVolumeMm3,
            float maxBallDiameterM, float collisionToCoreRadiusRatio,
            float semanticDiameterPerCoreDiameter, ref float coreRadiusWorldM,
            ref float collisionRadiusWorldM, ref float semanticDiameterWorldM)
        {
            float coreRadius;
            float collisionRadius;
            float semanticDiameter;
            long effectiveConfirmedMm3;
            if (!SizeModelMath.TryDerive(startVolumeMm3, confirmedVolumeMm3, maxBallDiameterM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter, out coreRadius,
                out collisionRadius, out semanticDiameter, out effectiveConfirmedMm3)) return false;
            coreRadiusWorldM = coreRadius;
            collisionRadiusWorldM = collisionRadius;
            semanticDiameterWorldM = semanticDiameter;
            return true;
        }

        /// <summary>Sets confirmed = unbanked + carried, then derives sizes without accumulating growth.</summary>
        public static bool RecomputeConfirmedAndSize(long unbankedVolumeUnits, long carriedVolumeMm3,
            long startVolumeMm3, float maxBallDiameterM, float collisionToCoreRadiusRatio,
            float semanticDiameterPerCoreDiameter, ref long confirmedVolumeMm3,
            ref float coreRadiusWorldM, ref float collisionRadiusWorldM,
            ref float semanticDiameterWorldM)
        {
            confirmedVolumeMm3 = unbankedVolumeUnits + carriedVolumeMm3;
            return DeriveSize(startVolumeMm3, confirmedVolumeMm3, maxBallDiameterM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                ref coreRadiusWorldM, ref collisionRadiusWorldM, ref semanticDiameterWorldM);
        }
    }
}
