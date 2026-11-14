using System;
using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Result of one operation on the caller-owned remote-proxy slot tables.
    /// </summary>
    /// <remarks>
    /// The apply outcomes carry the Python reference's disposition strings one
    /// for one: accepted, duplicate, updated, stale_generation, stale_revision,
    /// removed_tombstone and cleared_context. Invalid, Conflict and Capacity are
    /// the reference's three exceptions (SummaryError, SummaryConflictError and
    /// ProxyCapacityError), which Udon can only report as values. Cleared,
    /// AlreadyCleared and Rebuilt belong to the clear and rebuild operations and
    /// are never returned by Apply.
    /// </remarks>
    public enum RemoteProxyDisposition
    {
        Invalid = 0,
        Accepted = 1,
        Duplicate = 2,
        Updated = 3,
        StaleGeneration = 4,
        StaleRevision = 5,
        RemovedTombstone = 6,
        ClearedContext = 7,
        Conflict = 8,
        Capacity = 9,
        Cleared = 10,
        AlreadyCleared = 11,
        Rebuilt = 12
    }

    /// <summary>
    /// Visual-only cache for upstream-confirmed remote decoration summaries:
    /// stable-identity dedupe, generation and revision ordering, tombstones,
    /// closed contexts, and a renderer budget over caller-owned slot tables.
    /// </summary>
    /// <remarks>
    /// Isolation: this layer decides what a decoration looks like and where it
    /// is drawn, never what it collides with or scores. Nothing here writes a
    /// collider, pickup, ledger, banking or score value, and the API has no such
    /// output: the only outputs are a disposition, the caller's own visual slot
    /// tables, rendered slot ranks, and the counts needed to report the pool.
    /// Remote data that this layer keeps must never reach the ball's collision
    /// or scoring path.
    ///
    /// State layout: UdonSharp has no instance classes or structs, so the slot
    /// table that the Python cache keeps in a dictionary lives in caller-owned
    /// parallel arrays. The owning UdonSharpBehaviour allocates them, zeroes them
    /// once through ClearState, and passes the same arrays to every call. All
    /// slot arrays must have the same length; that length is the fixed record
    /// capacity (the Python max_records), and there is no growth.
    ///
    ///   array               type          field (Python SyntheticConfirmedSummary)
    ///   slotState           int[]         0 free, 1 occupied
    ///   slotContextId       string[]      context_id
    ///   slotBallId          string[]      ball_id
    ///   slotInstanceId      string[]      instance_id
    ///   slotGeneration      int[]         generation           &gt;= 0
    ///   slotAttachSequence  int[]         attach_sequence      &gt;= 0
    ///   slotEventId         string[]      event_id
    ///   slotRevision        int[]         revision             &gt;= 0
    ///   slotDigestLow       int[]         summary_digest, low 32 bits
    ///   slotDigestHigh      int[]         summary_digest, high 32 bits
    ///   slotVariantId       string[]      variant_id
    ///   slotVariantSeed     int[]         variant_seed         &gt;= 0
    ///   slotPosition        Vector3[]     position, metres in world space
    ///   slotRotation        Quaternion[]  rotation, normalized to unit length
    ///   slotVisible         bool[]        visible
    ///   slotBuried          bool[]        buried
    ///   slotRemoved         bool[]        removed (tombstone; the slot stays)
    ///   slotPriority        int[]         presentation_priority &gt;= 0
    ///
    /// A second table, with its own caller-chosen capacity, holds the closed
    /// contexts:
    ///
    ///   closedContextId     string[]      cleared context ids; null/empty means
    ///                                     unused, so a shorter table bounds how
    ///                                     many rounds may be closed in a session
    ///
    /// Identity and ordering: identity is exactly the upstream tuple
    /// (context_id, ball_id, instance_id, generation, attach_sequence) and is
    /// never derived from a slot index, arrival order, event id or digest. A
    /// newer generation replaces the older records of the same base
    /// (context, ball, instance); a late older generation is stale and cannot
    /// resurrect them. A removed record becomes a tombstone that only a new
    /// upstream attach sequence can replace. clear_context closes a context
    /// permanently, so old packets of that round stay refused; a new round needs
    /// a new context id.
    ///
    /// Digest: Python's fixture digest is sha256. Udon has no hashlib and no
    /// 64-bit arithmetic, so TryComputeDigest is our own token: two independent
    /// 32-bit FNV-1a lanes over a fixed field stream, returned as a low/high int
    /// pair. It is deterministic and it changes when any field changes, but it
    /// is NOT byte-compatible with the Python fixture digest and it is not
    /// authentication or a wire format. Like Python's summary_digest, it is an
    /// opaque upstream value that Apply stores and compares; Apply never derives
    /// identity from it and never recomputes it.
    ///
    /// Narrowing: Python accepts generation/revision up to 2^32-1 and
    /// attach_sequence/variant_seed up to 2^64-1. This port accepts non-negative
    /// signed 32-bit values only and rejects negatives as Invalid; because the
    /// upstream summary contract is not frozen, the fields are not widened here.
    ///
    /// Every call allocates nothing (TryComputeDigest is the exception: it uses
    /// ToCharArray) and mutates only the caller's arrays. There are no static
    /// fields and no scene reads.
    /// </remarks>
    public static class RemoteProxyMath
    {
        private const int SlotFree = 0;
        private const int SlotOccupied = 1;

        // FNV-1a 32-bit parameters. Two lanes with different offset bases give a
        // 64-bit token out of two ints; every intermediate is below 2^20, so the
        // result is the same whether or not the runtime checks int overflow.
        private const int FnvPrime = 16777619;            // 0x01000193
        private const int FnvLowBasis = -2128831035;      // 0x811C9DC5
        private const int FnvHighBasis = 522970236;       // 0x811C9DC5 ^ 0x9E3779B9

        // Smallest quaternion scale that is still a rotation rather than a
        // denormal. Python rejects below 1e-12 / norm, with norm >= 1 here.
        private const float MinimumRotationScale = 1e-12f;

        /// <summary>
        /// Frees every slot and every closed-context entry, keeping the arrays
        /// and their lengths. Call once when the owning behaviour starts.
        /// </summary>
        public static void ClearState(int[] slotState, string[] closedContextId)
        {
            if (slotState == null) return;
            for (int i = 0; i < slotState.Length; i++) slotState[i] = SlotFree;
            if (closedContextId == null) return;
            for (int i = 0; i < closedContextId.Length; i++) closedContextId[i] = null;
        }

        /// <summary>
        /// Consumes one upstream-confirmed decoration summary and returns its
        /// disposition. The slot tables are mutated only when the record is
        /// accepted, updated, or already present unchanged.
        /// </summary>
        /// <remarks>
        /// Checks run in the reference order: input validity, closed context,
        /// stale generation, then the stored record with the same identity
        /// (event conflict, stale revision, duplicate or conflict at the same
        /// revision, tombstone, update). A record with a new identity purges
        /// older generations of its base and then needs a free slot; both
        /// capacity failures leave every slot untouched (Capacity). position is
        /// metres in world space; rotation is normalized on the way in and the
        /// normalized value is what the duplicate test compares.
        /// </remarks>
        public static RemoteProxyDisposition Apply(
            string contextId,
            string ballId,
            string instanceId,
            int generation,
            int attachSequence,
            string eventId,
            int revision,
            int digestLow,
            int digestHigh,
            string variantId,
            int variantSeed,
            Vector3 position,
            Quaternion rotation,
            bool visible,
            bool buried,
            bool removed,
            int presentationPriority,
            int[] slotState,
            string[] slotContextId,
            string[] slotBallId,
            string[] slotInstanceId,
            int[] slotGeneration,
            int[] slotAttachSequence,
            string[] slotEventId,
            int[] slotRevision,
            int[] slotDigestLow,
            int[] slotDigestHigh,
            string[] slotVariantId,
            int[] slotVariantSeed,
            Vector3[] slotPosition,
            Quaternion[] slotRotation,
            bool[] slotVisible,
            bool[] slotBuried,
            bool[] slotRemoved,
            int[] slotPriority,
            string[] closedContextId)
        {
            if (!SlotTablesValid(slotState, slotContextId, slotBallId, slotInstanceId,
                slotGeneration, slotAttachSequence, slotEventId, slotRevision,
                slotDigestLow, slotDigestHigh, slotVariantId, slotVariantSeed,
                slotPosition, slotRotation, slotVisible, slotBuried, slotRemoved,
                slotPriority)) return RemoteProxyDisposition.Invalid;
            if (closedContextId == null) return RemoteProxyDisposition.Invalid;

            Quaternion unitRotation;
            if (!TryValidatePayload(contextId, ballId, instanceId, eventId, variantId,
                generation, attachSequence, revision, variantSeed, presentationPriority,
                position, rotation, out unitRotation)) return RemoteProxyDisposition.Invalid;

            int capacity = slotState.Length;
            if (IsClosedContext(contextId, closedContextId)) return RemoteProxyDisposition.ClearedContext;

            // The reference keeps a generation high-water table; here the highest
            // generation of a base is the maximum over that base's live slots, so
            // the table cannot disagree with the records it describes.
            int highGeneration = -1;
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                if (!SameBase(slotContextId[i], slotBallId[i], slotInstanceId[i],
                    contextId, ballId, instanceId)) continue;
                if (slotGeneration[i] > highGeneration) highGeneration = slotGeneration[i];
            }
            if (highGeneration >= 0 && generation < highGeneration)
            {
                return RemoteProxyDisposition.StaleGeneration;
            }

            int existing = -1;
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                if (slotGeneration[i] != generation) continue;
                if (slotAttachSequence[i] != attachSequence) continue;
                if (!SameBase(slotContextId[i], slotBallId[i], slotInstanceId[i],
                    contextId, ballId, instanceId)) continue;
                existing = i;
                break;
            }

            int target;
            RemoteProxyDisposition disposition;
            if (existing >= 0)
            {
                if (slotEventId[existing] != eventId) return RemoteProxyDisposition.Conflict;
                if (revision < slotRevision[existing]) return RemoteProxyDisposition.StaleRevision;
                if (revision == slotRevision[existing])
                {
                    // Same identity and revision: byte-identical payload is the
                    // idempotent duplicate, anything else is a conflict. Identity
                    // fields are equal by construction, so they need no compare.
                    bool identical = slotDigestLow[existing] == digestLow &&
                        slotDigestHigh[existing] == digestHigh &&
                        slotVariantId[existing] == variantId &&
                        slotVariantSeed[existing] == variantSeed &&
                        slotPosition[existing].x == position.x &&
                        slotPosition[existing].y == position.y &&
                        slotPosition[existing].z == position.z &&
                        slotRotation[existing].x == unitRotation.x &&
                        slotRotation[existing].y == unitRotation.y &&
                        slotRotation[existing].z == unitRotation.z &&
                        slotRotation[existing].w == unitRotation.w &&
                        slotVisible[existing] == visible &&
                        slotBuried[existing] == buried &&
                        slotRemoved[existing] == removed &&
                        slotPriority[existing] == presentationPriority;
                    return identical
                        ? RemoteProxyDisposition.Duplicate
                        : RemoteProxyDisposition.Conflict;
                }
                if (slotRemoved[existing]) return RemoteProxyDisposition.RemovedTombstone;
                target = existing;
                disposition = RemoteProxyDisposition.Updated;
            }
            else
            {
                int purgeCount = 0;
                bool purging = highGeneration >= 0 && generation > highGeneration;
                if (purging)
                {
                    for (int i = 0; i < capacity; i++)
                    {
                        if (slotState[i] != SlotOccupied) continue;
                        if (slotGeneration[i] >= generation) continue;
                        if (!SameBase(slotContextId[i], slotBallId[i], slotInstanceId[i],
                            contextId, ballId, instanceId)) continue;
                        purgeCount++;
                    }
                }

                int occupied = 0;
                for (int i = 0; i < capacity; i++)
                {
                    if (slotState[i] == SlotOccupied) occupied++;
                }
                if (occupied - purgeCount >= capacity) return RemoteProxyDisposition.Capacity;

                if (purging)
                {
                    for (int i = 0; i < capacity; i++)
                    {
                        if (slotState[i] != SlotOccupied) continue;
                        if (slotGeneration[i] >= generation) continue;
                        if (!SameBase(slotContextId[i], slotBallId[i], slotInstanceId[i],
                            contextId, ballId, instanceId)) continue;
                        slotState[i] = SlotFree;
                    }
                }

                target = -1;
                for (int i = 0; i < capacity; i++)
                {
                    if (slotState[i] != SlotOccupied)
                    {
                        target = i;
                        break;
                    }
                }
                if (target < 0) return RemoteProxyDisposition.Capacity;
                disposition = RemoteProxyDisposition.Accepted;
            }

            slotState[target] = SlotOccupied;
            slotContextId[target] = contextId;
            slotBallId[target] = ballId;
            slotInstanceId[target] = instanceId;
            slotGeneration[target] = generation;
            slotAttachSequence[target] = attachSequence;
            slotEventId[target] = eventId;
            slotRevision[target] = revision;
            slotDigestLow[target] = digestLow;
            slotDigestHigh[target] = digestHigh;
            slotVariantId[target] = variantId;
            slotVariantSeed[target] = variantSeed;
            slotPosition[target] = position;
            slotRotation[target] = unitRotation;
            slotVisible[target] = visible;
            slotBuried[target] = buried;
            slotRemoved[target] = removed;
            slotPriority[target] = presentationPriority;
            return disposition;
        }

        /// <summary>
        /// Chooses the decorations to draw, in draw order, and reports the pool
        /// counts. Never writes more ranks than the renderer budget, the number
        /// of eligible slots, or the caller's buffer length.
        /// </summary>
        /// <remarks>
        /// A slot is eligible when it is occupied, visible, not buried and not a
        /// tombstone. Eligible slots are ordered by presentation_priority
        /// descending, then by stable identity ascending (context, ball,
        /// instance, generation, attach sequence), then by event id ascending, so
        /// equal priorities keep a deterministic order. trackedCount counts every
        /// occupied slot; mergedBuriedCount counts the buried, non-removed ones,
        /// which are merged into a hint and never take a Renderer. Rendered ranks
        /// are slot indices: the caller maps them onto its fixed Renderer pool and
        /// owns enabling and disabling it. A zero-length buffer is valid and
        /// still reports the counts.
        /// </remarks>
        public static bool TrySelectRendered(
            int[] slotState,
            string[] slotContextId,
            string[] slotBallId,
            string[] slotInstanceId,
            int[] slotGeneration,
            int[] slotAttachSequence,
            string[] slotEventId,
            int[] slotRevision,
            int[] slotDigestLow,
            int[] slotDigestHigh,
            string[] slotVariantId,
            int[] slotVariantSeed,
            Vector3[] slotPosition,
            Quaternion[] slotRotation,
            bool[] slotVisible,
            bool[] slotBuried,
            bool[] slotRemoved,
            int[] slotPriority,
            int rendererBudget,
            int[] renderedSlotIndex,
            out int renderedCount,
            out int eligibleCount,
            out int trackedCount,
            out int mergedBuriedCount)
        {
            renderedCount = 0;
            eligibleCount = 0;
            trackedCount = 0;
            mergedBuriedCount = 0;
            if (!SlotTablesValid(slotState, slotContextId, slotBallId, slotInstanceId,
                slotGeneration, slotAttachSequence, slotEventId, slotRevision,
                slotDigestLow, slotDigestHigh, slotVariantId, slotVariantSeed,
                slotPosition, slotRotation, slotVisible, slotBuried, slotRemoved,
                slotPriority)) return false;
            if (renderedSlotIndex == null) return false;
            if (rendererBudget < 0) return false;

            int capacity = slotState.Length;
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                trackedCount++;
                if (slotRemoved[i]) continue;
                if (slotBuried[i])
                {
                    mergedBuriedCount++;
                    continue;
                }
                if (!slotVisible[i]) continue;
                eligibleCount++;
            }

            int limit = eligibleCount;
            if (rendererBudget < limit) limit = rendererBudget;
            if (renderedSlotIndex.Length < limit) limit = renderedSlotIndex.Length;

            // Rank selection instead of a sorted copy: the output buffer may be
            // shorter than the eligible set, and dropping slots before sorting
            // would pick the wrong records. O(capacity^3) at worst, which is fine
            // for a packet-rate visual pool of fixed capacity.
            for (int rank = 0; rank < limit; rank++)
            {
                for (int i = 0; i < capacity; i++)
                {
                    if (!IsEligible(slotState[i], slotVisible[i], slotBuried[i], slotRemoved[i])) continue;
                    int before = 0;
                    for (int j = 0; j < capacity; j++)
                    {
                        if (i == j) continue;
                        if (!IsEligible(slotState[j], slotVisible[j], slotBuried[j], slotRemoved[j])) continue;
                        if (ComesBefore(j, i, slotPriority, slotContextId, slotBallId,
                            slotInstanceId, slotGeneration, slotAttachSequence, slotEventId))
                        {
                            before++;
                        }
                    }
                    if (before == rank)
                    {
                        renderedSlotIndex[rank] = i;
                        renderedCount++;
                        break;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Closes one context for the rest of the session: its slots are freed,
        /// its id is added to the bounded closed-context table, and later packets
        /// from it answer ClearedContext.
        /// </summary>
        /// <remarks>
        /// Returns Cleared with the number of freed slots, AlreadyCleared when the
        /// context was closed before (nothing is freed, which the reference
        /// returns as 0), Invalid for a missing id, and Capacity when the
        /// closed-context table is full. Capacity leaves every slot and the table
        /// untouched; a cleared context can never be rebuilt, a new round must use
        /// a new context id.
        /// </remarks>
        public static RemoteProxyDisposition ClearContext(
            string contextId,
            int[] slotState,
            string[] slotContextId,
            string[] slotBallId,
            string[] slotInstanceId,
            int[] slotGeneration,
            int[] slotAttachSequence,
            string[] slotEventId,
            int[] slotRevision,
            int[] slotDigestLow,
            int[] slotDigestHigh,
            string[] slotVariantId,
            int[] slotVariantSeed,
            Vector3[] slotPosition,
            Quaternion[] slotRotation,
            bool[] slotVisible,
            bool[] slotBuried,
            bool[] slotRemoved,
            int[] slotPriority,
            string[] closedContextId,
            out int removedCount)
        {
            removedCount = 0;
            if (!SlotTablesValid(slotState, slotContextId, slotBallId, slotInstanceId,
                slotGeneration, slotAttachSequence, slotEventId, slotRevision,
                slotDigestLow, slotDigestHigh, slotVariantId, slotVariantSeed,
                slotPosition, slotRotation, slotVisible, slotBuried, slotRemoved,
                slotPriority)) return RemoteProxyDisposition.Invalid;
            if (closedContextId == null) return RemoteProxyDisposition.Invalid;
            if (!IsNonEmpty(contextId)) return RemoteProxyDisposition.Invalid;

            int closedCount = 0;
            for (int i = 0; i < closedContextId.Length; i++)
            {
                if (!IsNonEmpty(closedContextId[i])) continue;
                closedCount++;
                if (closedContextId[i] == contextId) return RemoteProxyDisposition.AlreadyCleared;
            }
            if (closedCount >= closedContextId.Length) return RemoteProxyDisposition.Capacity;

            int capacity = slotState.Length;
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                if (slotContextId[i] != contextId) continue;
                slotState[i] = SlotFree;
                removedCount++;
            }
            for (int i = 0; i < closedContextId.Length; i++)
            {
                if (IsNonEmpty(closedContextId[i])) continue;
                closedContextId[i] = contextId;
                break;
            }
            return RemoteProxyDisposition.Cleared;
        }

        /// <summary>
        /// Atomically replaces one context from a complete synthetic snapshot,
        /// leaving every other context untouched.
        /// </summary>
        /// <remarks>
        /// The snapshot is the caller's parallel record arrays plus a count, and
        /// rebuildOrder is caller-owned scratch of at least that length (it holds
        /// a permutation; its previous contents are ignored). Records are applied
        /// in the reference's stable order (context, ball, instance, generation,
        /// attach sequence, revision, event id), so the outcome does not depend on
        /// the caller's array order.
        ///
        /// Every record is validated, every conflict inside the snapshot is found
        /// and the live size of the replay is checked against the capacity before
        /// any slot is touched; Invalid, Conflict and Capacity therefore leave the
        /// slot tables exactly as they were. The capacity check is exact: it replays
        /// the growth of the candidate table, where a new generation purges the
        /// older records of its base and a repeated identity adds no second record,
        /// and it also checks the merged size with the other contexts. The commit
        /// then frees the context's slots and re-applies the snapshot in that
        /// order. Those applies cannot fail: the inputs are already validated,
        /// generations only rise within a base, no two records of one identity
        /// disagree on event id or on content at the same revision, and the
        /// committed size is within capacity. A cleared context is refused
        /// (ClearedContext) because a round cannot be revived.
        /// storedCount is the number of slots the context occupies afterwards,
        /// which is smaller than the snapshot when it carried older generations.
        /// No generation high-water table is merged because it is derived from the
        /// live slots.
        /// </remarks>
        public static RemoteProxyDisposition RebuildContext(
            string contextId,
            string[] snapshotContextId,
            string[] snapshotBallId,
            string[] snapshotInstanceId,
            int[] snapshotGeneration,
            int[] snapshotAttachSequence,
            string[] snapshotEventId,
            int[] snapshotRevision,
            int[] snapshotDigestLow,
            int[] snapshotDigestHigh,
            string[] snapshotVariantId,
            int[] snapshotVariantSeed,
            Vector3[] snapshotPosition,
            Quaternion[] snapshotRotation,
            bool[] snapshotVisible,
            bool[] snapshotBuried,
            bool[] snapshotRemoved,
            int[] snapshotPriority,
            int snapshotCount,
            int[] rebuildOrder,
            int[] slotState,
            string[] slotContextId,
            string[] slotBallId,
            string[] slotInstanceId,
            int[] slotGeneration,
            int[] slotAttachSequence,
            string[] slotEventId,
            int[] slotRevision,
            int[] slotDigestLow,
            int[] slotDigestHigh,
            string[] slotVariantId,
            int[] slotVariantSeed,
            Vector3[] slotPosition,
            Quaternion[] slotRotation,
            bool[] slotVisible,
            bool[] slotBuried,
            bool[] slotRemoved,
            int[] slotPriority,
            string[] closedContextId,
            out int storedCount)
        {
            storedCount = 0;
            if (!SlotTablesValid(slotState, slotContextId, slotBallId, slotInstanceId,
                slotGeneration, slotAttachSequence, slotEventId, slotRevision,
                slotDigestLow, slotDigestHigh, slotVariantId, slotVariantSeed,
                slotPosition, slotRotation, slotVisible, slotBuried, slotRemoved,
                slotPriority)) return RemoteProxyDisposition.Invalid;
            if (closedContextId == null || rebuildOrder == null) return RemoteProxyDisposition.Invalid;
            if (!IsNonEmpty(contextId)) return RemoteProxyDisposition.Invalid;
            if (snapshotCount < 0 || snapshotCount > rebuildOrder.Length) return RemoteProxyDisposition.Invalid;
            if (snapshotContextId == null || snapshotBallId == null || snapshotInstanceId == null ||
                snapshotGeneration == null || snapshotAttachSequence == null ||
                snapshotEventId == null || snapshotRevision == null ||
                snapshotDigestLow == null || snapshotDigestHigh == null ||
                snapshotVariantId == null || snapshotVariantSeed == null ||
                snapshotPosition == null || snapshotRotation == null ||
                snapshotVisible == null || snapshotBuried == null ||
                snapshotRemoved == null || snapshotPriority == null)
            {
                return RemoteProxyDisposition.Invalid;
            }
            if (snapshotContextId.Length < snapshotCount || snapshotBallId.Length < snapshotCount ||
                snapshotInstanceId.Length < snapshotCount || snapshotGeneration.Length < snapshotCount ||
                snapshotAttachSequence.Length < snapshotCount || snapshotEventId.Length < snapshotCount ||
                snapshotRevision.Length < snapshotCount || snapshotDigestLow.Length < snapshotCount ||
                snapshotDigestHigh.Length < snapshotCount || snapshotVariantId.Length < snapshotCount ||
                snapshotVariantSeed.Length < snapshotCount || snapshotPosition.Length < snapshotCount ||
                snapshotRotation.Length < snapshotCount || snapshotVisible.Length < snapshotCount ||
                snapshotBuried.Length < snapshotCount || snapshotRemoved.Length < snapshotCount ||
                snapshotPriority.Length < snapshotCount)
            {
                return RemoteProxyDisposition.Invalid;
            }
            if (IsClosedContext(contextId, closedContextId)) return RemoteProxyDisposition.ClearedContext;

            for (int i = 0; i < snapshotCount; i++)
            {
                if (snapshotContextId[i] != contextId) return RemoteProxyDisposition.Invalid;
                Quaternion unitRotation;
                if (!TryValidatePayload(snapshotContextId[i], snapshotBallId[i], snapshotInstanceId[i],
                    snapshotEventId[i], snapshotVariantId[i], snapshotGeneration[i],
                    snapshotAttachSequence[i], snapshotRevision[i], snapshotVariantSeed[i],
                    snapshotPriority[i], snapshotPosition[i], snapshotRotation[i], out unitRotation))
                {
                    return RemoteProxyDisposition.Invalid;
                }
            }

            for (int i = 0; i < snapshotCount; i++) rebuildOrder[i] = i;
            for (int i = 1; i < snapshotCount; i++)
            {
                int item = rebuildOrder[i];
                int j = i - 1;
                while (j >= 0 && SnapshotComesBefore(item, rebuildOrder[j], snapshotContextId,
                    snapshotBallId, snapshotInstanceId, snapshotGeneration,
                    snapshotAttachSequence, snapshotRevision, snapshotEventId))
                {
                    rebuildOrder[j + 1] = rebuildOrder[j];
                    j--;
                }
                rebuildOrder[j + 1] = item;
            }

            // Conflicts inside the snapshot, found before anything is written.
            for (int k = 1; k < snapshotCount; k++)
            {
                int previous = rebuildOrder[k - 1];
                int current = rebuildOrder[k];
                if (!SameSnapshotIdentity(previous, current, snapshotContextId, snapshotBallId,
                    snapshotInstanceId, snapshotGeneration, snapshotAttachSequence)) continue;
                if (snapshotEventId[previous] != snapshotEventId[current]) return RemoteProxyDisposition.Conflict;
                if (snapshotRevision[current] == snapshotRevision[previous] &&
                    !SameSnapshotContent(previous, current, snapshotEventId, snapshotRevision,
                        snapshotDigestLow, snapshotDigestHigh, snapshotVariantId, snapshotVariantSeed,
                        snapshotPosition, snapshotRotation, snapshotVisible, snapshotBuried,
                        snapshotRemoved, snapshotPriority))
                {
                    return RemoteProxyDisposition.Conflict;
                }
            }

            int capacity = slotState.Length;
            int kept = 0;
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                if (slotContextId[i] == contextId) continue;
                kept++;
            }

            // Exact replay of the candidate table's size: records arrive in the
            // stable order above, a higher generation empties its base first, and a
            // repeated identity is the duplicate that adds no slot. Python checks
            // capacity at each insertion of its candidate cache, so the peak matters,
            // not only the final size.
            int live = 0;
            int peak = 0;
            int groupLive = 0;
            int groupGeneration = -1;
            string groupBallId = null;
            string groupInstanceId = null;
            for (int k = 0; k < snapshotCount; k++)
            {
                int index = rebuildOrder[k];
                bool sameGroup = groupBallId != null &&
                    snapshotBallId[index] == groupBallId &&
                    snapshotInstanceId[index] == groupInstanceId;
                if (!sameGroup)
                {
                    groupBallId = snapshotBallId[index];
                    groupInstanceId = snapshotInstanceId[index];
                    groupGeneration = snapshotGeneration[index];
                    groupLive = 0;
                }
                else if (snapshotGeneration[index] > groupGeneration)
                {
                    live -= groupLive;
                    groupGeneration = snapshotGeneration[index];
                    groupLive = 0;
                }
                if (k > 0 && SameSnapshotIdentity(rebuildOrder[k - 1], index, snapshotContextId,
                    snapshotBallId, snapshotInstanceId, snapshotGeneration, snapshotAttachSequence))
                {
                    continue;
                }
                live++;
                groupLive++;
                if (live > peak) peak = live;
            }
            if (peak > capacity) return RemoteProxyDisposition.Capacity;
            if (kept + live > capacity) return RemoteProxyDisposition.Capacity;

            // Commit: drop the context, then replay the snapshot in stable order.
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                if (slotContextId[i] != contextId) continue;
                slotState[i] = SlotFree;
            }
            for (int k = 0; k < snapshotCount; k++)
            {
                int index = rebuildOrder[k];
                // Dispositions are Accepted, Duplicate, Updated or
                // RemovedTombstone here; the pre-checks above rule out the rest.
                Apply(snapshotContextId[index], snapshotBallId[index], snapshotInstanceId[index],
                    snapshotGeneration[index], snapshotAttachSequence[index], snapshotEventId[index],
                    snapshotRevision[index], snapshotDigestLow[index], snapshotDigestHigh[index],
                    snapshotVariantId[index], snapshotVariantSeed[index], snapshotPosition[index],
                    snapshotRotation[index], snapshotVisible[index], snapshotBuried[index],
                    snapshotRemoved[index], snapshotPriority[index],
                    slotState, slotContextId, slotBallId, slotInstanceId, slotGeneration,
                    slotAttachSequence, slotEventId, slotRevision, slotDigestLow, slotDigestHigh,
                    slotVariantId, slotVariantSeed, slotPosition, slotRotation, slotVisible,
                    slotBuried, slotRemoved, slotPriority, closedContextId);
            }
            for (int i = 0; i < capacity; i++)
            {
                if (slotState[i] != SlotOccupied) continue;
                if (slotContextId[i] != contextId) continue;
                storedCount++;
            }
            return RemoteProxyDisposition.Rebuilt;
        }

        /// <summary>
        /// Our own deterministic digest over the same field sequence the Python
        /// fixture hashes, returned as a low/high int pair.
        /// </summary>
        /// <remarks>
        /// Two 32-bit FNV-1a lanes (offset bases 0x811C9DC5 and
        /// 0x811C9DC5 ^ 0x9E3779B9, prime 0x01000193) run over text, integers,
        /// float bit patterns and flags in a fixed order: context, ball,
        /// instance, generation, attach sequence, event, revision, variant, seed,
        /// position, rotation, visible, buried, removed, priority. Text is folded
        /// as its length and then each UTF-16 code unit as two octets; integers as
        /// four little-endian octets; floats through BitConverter.SingleToInt32Bits.
        /// The rotation is hashed as passed, not normalized, and the digest itself
        /// is not part of the stream.
        ///
        /// This is NOT the Python sha256 fixture digest and is not byte-compatible
        /// with it. Only determinism and the equality semantics matter. It is a
        /// conflict-detection token, not authentication.
        /// False means a null text field; the out values are then the lane
        /// starting values and are not a usable token. Unlike the rest of this
        /// class, this call allocates (ToCharArray), so it belongs on the
        /// fixture/host path, not per frame.
        /// </remarks>
        public static bool TryComputeDigest(
            string contextId,
            string ballId,
            string instanceId,
            int generation,
            int attachSequence,
            string eventId,
            int revision,
            string variantId,
            int variantSeed,
            Vector3 position,
            Quaternion rotation,
            bool visible,
            bool buried,
            bool removed,
            int presentationPriority,
            out int digestLow,
            out int digestHigh)
        {
            int low = FnvLowBasis;
            int high = FnvHighBasis;
            digestLow = FnvLowBasis;
            digestHigh = FnvHighBasis;
            if (contextId == null || ballId == null || instanceId == null ||
                eventId == null || variantId == null) return false;

            FoldText(ref low, ref high, contextId);
            FoldText(ref low, ref high, ballId);
            FoldText(ref low, ref high, instanceId);
            FoldInt(ref low, ref high, generation);
            FoldInt(ref low, ref high, attachSequence);
            FoldText(ref low, ref high, eventId);
            FoldInt(ref low, ref high, revision);
            FoldText(ref low, ref high, variantId);
            FoldInt(ref low, ref high, variantSeed);
            FoldFloat(ref low, ref high, position.x);
            FoldFloat(ref low, ref high, position.y);
            FoldFloat(ref low, ref high, position.z);
            FoldFloat(ref low, ref high, rotation.x);
            FoldFloat(ref low, ref high, rotation.y);
            FoldFloat(ref low, ref high, rotation.z);
            FoldFloat(ref low, ref high, rotation.w);
            FoldInt(ref low, ref high, visible ? 1 : 0);
            FoldInt(ref low, ref high, buried ? 1 : 0);
            FoldInt(ref low, ref high, removed ? 1 : 0);
            FoldInt(ref low, ref high, presentationPriority);

            digestLow = low;
            digestHigh = high;
            return true;
        }

        // All 18 slot arrays must exist and be exactly as long as slotState, so a
        // short array can never be indexed past its end by a slot that another
        // array considers valid.
        private static bool SlotTablesValid(
            int[] slotState,
            string[] slotContextId,
            string[] slotBallId,
            string[] slotInstanceId,
            int[] slotGeneration,
            int[] slotAttachSequence,
            string[] slotEventId,
            int[] slotRevision,
            int[] slotDigestLow,
            int[] slotDigestHigh,
            string[] slotVariantId,
            int[] slotVariantSeed,
            Vector3[] slotPosition,
            Quaternion[] slotRotation,
            bool[] slotVisible,
            bool[] slotBuried,
            bool[] slotRemoved,
            int[] slotPriority)
        {
            if (slotState == null || slotContextId == null || slotBallId == null ||
                slotInstanceId == null || slotGeneration == null || slotAttachSequence == null ||
                slotEventId == null || slotRevision == null || slotDigestLow == null ||
                slotDigestHigh == null || slotVariantId == null || slotVariantSeed == null ||
                slotPosition == null || slotRotation == null || slotVisible == null ||
                slotBuried == null || slotRemoved == null || slotPriority == null) return false;
            int capacity = slotState.Length;
            return slotContextId.Length == capacity && slotBallId.Length == capacity &&
                slotInstanceId.Length == capacity && slotGeneration.Length == capacity &&
                slotAttachSequence.Length == capacity && slotEventId.Length == capacity &&
                slotRevision.Length == capacity && slotDigestLow.Length == capacity &&
                slotDigestHigh.Length == capacity && slotVariantId.Length == capacity &&
                slotVariantSeed.Length == capacity && slotPosition.Length == capacity &&
                slotRotation.Length == capacity && slotVisible.Length == capacity &&
                slotBuried.Length == capacity && slotRemoved.Length == capacity &&
                slotPriority.Length == capacity;
        }

        private static bool TryValidatePayload(
            string contextId,
            string ballId,
            string instanceId,
            string eventId,
            string variantId,
            int generation,
            int attachSequence,
            int revision,
            int variantSeed,
            int presentationPriority,
            Vector3 position,
            Quaternion rotation,
            out Quaternion unitRotation)
        {
            unitRotation = Quaternion.identity;
            if (!IsNonEmpty(contextId) || !IsNonEmpty(ballId) || !IsNonEmpty(instanceId)) return false;
            if (!IsNonEmpty(eventId) || !IsNonEmpty(variantId)) return false;
            if (generation < 0 || attachSequence < 0 || revision < 0 || variantSeed < 0) return false;
            if (presentationPriority < 0) return false;
            if (!KatamariMath.IsFiniteVector(position)) return false;
            return TryNormalizeRotation(rotation, out unitRotation);
        }

        // Python rescales by the largest component before taking the norm and
        // normalizes instead of rejecting non-unit input; only a non-finite, zero
        // or denormal-scale quaternion is refused.
        private static bool TryNormalizeRotation(Quaternion value, out Quaternion unitRotation)
        {
            unitRotation = Quaternion.identity;
            if (!KatamariMath.IsFiniteQuaternion(value)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x),
                Mathf.Max(Mathf.Abs(value.y), Mathf.Max(Mathf.Abs(value.z), Mathf.Abs(value.w))));
            if (!(scale > 0f)) return false;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            float w = value.w / scale;
            float norm = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!KatamariMath.IsFiniteFloat(norm) || norm <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(scale) || scale <= MinimumRotationScale / norm) return false;
            Quaternion candidate = new Quaternion(x / norm, y / norm, z / norm, w / norm);
            if (!KatamariMath.IsFiniteQuaternion(candidate)) return false;
            unitRotation = candidate;
            return true;
        }

        private static bool IsNonEmpty(string value)
        {
            return value != null && value.Length > 0;
        }

        private static bool IsClosedContext(string contextId, string[] closedContextId)
        {
            for (int i = 0; i < closedContextId.Length; i++)
            {
                if (!IsNonEmpty(closedContextId[i])) continue;
                if (closedContextId[i] == contextId) return true;
            }
            return false;
        }

        private static bool SameBase(string candidateContextId, string candidateBallId,
            string candidateInstanceId, string contextId, string ballId, string instanceId)
        {
            return candidateContextId == contextId && candidateBallId == ballId &&
                candidateInstanceId == instanceId;
        }

        private static bool IsEligible(int state, bool visible, bool buried, bool removed)
        {
            return state == SlotOccupied && !removed && visible && !buried;
        }

        // Draw order: higher priority first, then the stable identity ascending,
        // then the event id. CompareOrdinal keeps this independent of culture; it
        // orders UTF-16 code units, which differs from Python's code-point order
        // only for surrogate pairs (an id would have to contain astral characters).
        private static bool ComesBefore(
            int left,
            int right,
            int[] slotPriority,
            string[] slotContextId,
            string[] slotBallId,
            string[] slotInstanceId,
            int[] slotGeneration,
            int[] slotAttachSequence,
            string[] slotEventId)
        {
            if (slotPriority[left] != slotPriority[right]) return slotPriority[left] > slotPriority[right];
            int order = string.CompareOrdinal(slotContextId[left], slotContextId[right]);
            if (order != 0) return order < 0;
            order = string.CompareOrdinal(slotBallId[left], slotBallId[right]);
            if (order != 0) return order < 0;
            order = string.CompareOrdinal(slotInstanceId[left], slotInstanceId[right]);
            if (order != 0) return order < 0;
            if (slotGeneration[left] != slotGeneration[right])
            {
                return slotGeneration[left] < slotGeneration[right];
            }
            if (slotAttachSequence[left] != slotAttachSequence[right])
            {
                return slotAttachSequence[left] < slotAttachSequence[right];
            }
            return string.CompareOrdinal(slotEventId[left], slotEventId[right]) < 0;
        }

        private static bool SnapshotComesBefore(
            int left,
            int right,
            string[] snapshotContextId,
            string[] snapshotBallId,
            string[] snapshotInstanceId,
            int[] snapshotGeneration,
            int[] snapshotAttachSequence,
            int[] snapshotRevision,
            string[] snapshotEventId)
        {
            int order = string.CompareOrdinal(snapshotContextId[left], snapshotContextId[right]);
            if (order != 0) return order < 0;
            order = string.CompareOrdinal(snapshotBallId[left], snapshotBallId[right]);
            if (order != 0) return order < 0;
            order = string.CompareOrdinal(snapshotInstanceId[left], snapshotInstanceId[right]);
            if (order != 0) return order < 0;
            if (snapshotGeneration[left] != snapshotGeneration[right])
            {
                return snapshotGeneration[left] < snapshotGeneration[right];
            }
            if (snapshotAttachSequence[left] != snapshotAttachSequence[right])
            {
                return snapshotAttachSequence[left] < snapshotAttachSequence[right];
            }
            if (snapshotRevision[left] != snapshotRevision[right])
            {
                return snapshotRevision[left] < snapshotRevision[right];
            }
            return string.CompareOrdinal(snapshotEventId[left], snapshotEventId[right]) < 0;
        }

        private static bool SameSnapshotIdentity(
            int left,
            int right,
            string[] snapshotContextId,
            string[] snapshotBallId,
            string[] snapshotInstanceId,
            int[] snapshotGeneration,
            int[] snapshotAttachSequence)
        {
            return SameBase(snapshotContextId[left], snapshotBallId[left], snapshotInstanceId[left],
                    snapshotContextId[right], snapshotBallId[right], snapshotInstanceId[right]) &&
                snapshotGeneration[left] == snapshotGeneration[right] &&
                snapshotAttachSequence[left] == snapshotAttachSequence[right];
        }

        // Python compares validated records, so two differently scaled copies of
        // the same rotation count as equal; normalize both sides here as well.
        private static bool SameSnapshotContent(
            int left,
            int right,
            string[] snapshotEventId,
            int[] snapshotRevision,
            int[] snapshotDigestLow,
            int[] snapshotDigestHigh,
            string[] snapshotVariantId,
            int[] snapshotVariantSeed,
            Vector3[] snapshotPosition,
            Quaternion[] snapshotRotation,
            bool[] snapshotVisible,
            bool[] snapshotBuried,
            bool[] snapshotRemoved,
            int[] snapshotPriority)
        {
            Quaternion leftRotation;
            Quaternion rightRotation;
            if (!TryNormalizeRotation(snapshotRotation[left], out leftRotation)) return false;
            if (!TryNormalizeRotation(snapshotRotation[right], out rightRotation)) return false;
            return snapshotEventId[left] == snapshotEventId[right] &&
                snapshotRevision[left] == snapshotRevision[right] &&
                snapshotDigestLow[left] == snapshotDigestLow[right] &&
                snapshotDigestHigh[left] == snapshotDigestHigh[right] &&
                snapshotVariantId[left] == snapshotVariantId[right] &&
                snapshotVariantSeed[left] == snapshotVariantSeed[right] &&
                snapshotPosition[left].x == snapshotPosition[right].x &&
                snapshotPosition[left].y == snapshotPosition[right].y &&
                snapshotPosition[left].z == snapshotPosition[right].z &&
                leftRotation.x == rightRotation.x &&
                leftRotation.y == rightRotation.y &&
                leftRotation.z == rightRotation.z &&
                leftRotation.w == rightRotation.w &&
                snapshotVisible[left] == snapshotVisible[right] &&
                snapshotBuried[left] == snapshotBuried[right] &&
                snapshotRemoved[left] == snapshotRemoved[right] &&
                snapshotPriority[left] == snapshotPriority[right];
        }

        // 32-bit multiply modulo 2^32 with 8-bit limbs: no intermediate reaches
        // 2^21, so it never relies on wrap-around and gives the same result under
        // overflow checking.
        private static int MultiplyModulo32(int left, int right)
        {
            int left0 = left & 0xFF;
            int left1 = (left >> 8) & 0xFF;
            int left2 = (left >> 16) & 0xFF;
            int left3 = (left >> 24) & 0xFF;
            int right0 = right & 0xFF;
            int right1 = (right >> 8) & 0xFF;
            int right2 = (right >> 16) & 0xFF;
            int right3 = (right >> 24) & 0xFF;

            int sum0 = left0 * right0;
            int sum1 = left0 * right1 + left1 * right0;
            int sum2 = left0 * right2 + left1 * right1 + left2 * right0;
            int sum3 = left0 * right3 + left1 * right2 + left2 * right1 + left3 * right0;

            int octet0 = sum0 & 0xFF;
            sum1 += sum0 >> 8;
            int octet1 = sum1 & 0xFF;
            sum2 += sum1 >> 8;
            int octet2 = sum2 & 0xFF;
            sum3 += sum2 >> 8;
            int octet3 = sum3 & 0xFF;

            return octet0 | (octet1 << 8) | (octet2 << 16) | (octet3 << 24);
        }

        private static void FoldOctet(ref int low, ref int high, int octet)
        {
            low = MultiplyModulo32(low ^ (octet & 0xFF), FnvPrime);
            high = MultiplyModulo32(high ^ (octet & 0xFF), FnvPrime);
        }

        private static void FoldInt(ref int low, ref int high, int value)
        {
            FoldOctet(ref low, ref high, value);
            FoldOctet(ref low, ref high, value >> 8);
            FoldOctet(ref low, ref high, value >> 16);
            FoldOctet(ref low, ref high, value >> 24);
        }

        private static void FoldFloat(ref int low, ref int high, float value)
        {
            FoldInt(ref low, ref high, BitConverter.SingleToInt32Bits(value));
        }

        // Length first, then each UTF-16 code unit as two octets: the length keeps
        // the stream unambiguous without a separator byte.
        private static void FoldText(ref int low, ref int high, string value)
        {
            FoldInt(ref low, ref high, value.Length);
            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; i++)
            {
                int codeUnit = (int)characters[i];
                FoldOctet(ref low, ref high, codeUnit);
                FoldOctet(ref low, ref high, codeUnit >> 8);
            }
        }
    }
}
