using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_remote_proxy/test_remote_proxy.py.
    /// Expected values are the dispositions and counts the module documents, not a
    /// replay of the implementation: identity is upstream data, a duplicate is
    /// idempotent, conflicting content never partially updates, generations and
    /// revisions only move forward, tombstones stay, a cleared context cannot
    /// revive, and the renderer budget is a hard cap. Positions are metres in
    /// world space, matching the Python model.
    /// </summary>
    public class RemoteProxyTests
    {
        // Mirrors SyntheticConfirmedSummary plus the fixture's derived digest.
        private sealed class Record
        {
            public string ContextId;
            public string BallId;
            public string InstanceId;
            public int Generation;
            public int AttachSequence;
            public string EventId;
            public int Revision;
            public int DigestLow;
            public int DigestHigh;
            public string VariantId;
            public int VariantSeed;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Visible;
            public bool Buried;
            public bool Removed;
            public int PresentationPriority;
        }

        /// <summary>
        /// Owns the parallel arrays the runtime expects, the way the future
        /// UdonSharpBehaviour will, and adds the read-back views the Python tests
        /// used (summaries, rendered_summaries, renderer_count, tracked_count).
        /// </summary>
        private sealed class Cache
        {
            public readonly int RendererBudget;
            public readonly int[] SlotState;
            public readonly string[] SlotContextId;
            public readonly string[] SlotBallId;
            public readonly string[] SlotInstanceId;
            public readonly int[] SlotGeneration;
            public readonly int[] SlotAttachSequence;
            public readonly string[] SlotEventId;
            public readonly int[] SlotRevision;
            public readonly int[] SlotDigestLow;
            public readonly int[] SlotDigestHigh;
            public readonly string[] SlotVariantId;
            public readonly int[] SlotVariantSeed;
            public readonly Vector3[] SlotPosition;
            public readonly Quaternion[] SlotRotation;
            public readonly bool[] SlotVisible;
            public readonly bool[] SlotBuried;
            public readonly bool[] SlotRemoved;
            public readonly int[] SlotPriority;
            private readonly string[] closedContextId;

            public int LastRebuildStoredCount;

            public Cache(int capacity, int rendererBudget, int closedCapacity = -1)
            {
                if (closedCapacity < 0) closedCapacity = capacity;
                RendererBudget = rendererBudget;
                SlotState = new int[capacity];
                SlotContextId = new string[capacity];
                SlotBallId = new string[capacity];
                SlotInstanceId = new string[capacity];
                SlotGeneration = new int[capacity];
                SlotAttachSequence = new int[capacity];
                SlotEventId = new string[capacity];
                SlotRevision = new int[capacity];
                SlotDigestLow = new int[capacity];
                SlotDigestHigh = new int[capacity];
                SlotVariantId = new string[capacity];
                SlotVariantSeed = new int[capacity];
                SlotPosition = new Vector3[capacity];
                SlotRotation = new Quaternion[capacity];
                SlotVisible = new bool[capacity];
                SlotBuried = new bool[capacity];
                SlotRemoved = new bool[capacity];
                SlotPriority = new int[capacity];
                closedContextId = new string[closedCapacity];
                RemoteProxyMath.ClearState(SlotState, closedContextId);
            }

            public RemoteProxyDisposition Apply(Record record)
            {
                return RemoteProxyMath.Apply(
                    record.ContextId, record.BallId, record.InstanceId,
                    record.Generation, record.AttachSequence, record.EventId,
                    record.Revision, record.DigestLow, record.DigestHigh,
                    record.VariantId, record.VariantSeed, record.Position, record.Rotation,
                    record.Visible, record.Buried, record.Removed, record.PresentationPriority,
                    SlotState, SlotContextId, SlotBallId, SlotInstanceId, SlotGeneration,
                    SlotAttachSequence, SlotEventId, SlotRevision, SlotDigestLow, SlotDigestHigh,
                    SlotVariantId, SlotVariantSeed, SlotPosition, SlotRotation, SlotVisible,
                    SlotBuried, SlotRemoved, SlotPriority, closedContextId);
            }

            public RemoteProxyDisposition Rebuild(string contextId, Record[] snapshot)
            {
                int count = snapshot.Length;
                string[] snapshotContextId = new string[count];
                string[] snapshotBallId = new string[count];
                string[] snapshotInstanceId = new string[count];
                int[] snapshotGeneration = new int[count];
                int[] snapshotAttachSequence = new int[count];
                string[] snapshotEventId = new string[count];
                int[] snapshotRevision = new int[count];
                int[] snapshotDigestLow = new int[count];
                int[] snapshotDigestHigh = new int[count];
                string[] snapshotVariantId = new string[count];
                int[] snapshotVariantSeed = new int[count];
                Vector3[] snapshotPosition = new Vector3[count];
                Quaternion[] snapshotRotation = new Quaternion[count];
                bool[] snapshotVisible = new bool[count];
                bool[] snapshotBuried = new bool[count];
                bool[] snapshotRemoved = new bool[count];
                int[] snapshotPriority = new int[count];
                int[] rebuildOrder = new int[count];
                for (int i = 0; i < count; i++)
                {
                    Record record = snapshot[i];
                    snapshotContextId[i] = record.ContextId;
                    snapshotBallId[i] = record.BallId;
                    snapshotInstanceId[i] = record.InstanceId;
                    snapshotGeneration[i] = record.Generation;
                    snapshotAttachSequence[i] = record.AttachSequence;
                    snapshotEventId[i] = record.EventId;
                    snapshotRevision[i] = record.Revision;
                    snapshotDigestLow[i] = record.DigestLow;
                    snapshotDigestHigh[i] = record.DigestHigh;
                    snapshotVariantId[i] = record.VariantId;
                    snapshotVariantSeed[i] = record.VariantSeed;
                    snapshotPosition[i] = record.Position;
                    snapshotRotation[i] = record.Rotation;
                    snapshotVisible[i] = record.Visible;
                    snapshotBuried[i] = record.Buried;
                    snapshotRemoved[i] = record.Removed;
                    snapshotPriority[i] = record.PresentationPriority;
                }
                int storedCount;
                RemoteProxyDisposition disposition = RemoteProxyMath.RebuildContext(
                    contextId, snapshotContextId, snapshotBallId, snapshotInstanceId,
                    snapshotGeneration, snapshotAttachSequence, snapshotEventId,
                    snapshotRevision, snapshotDigestLow, snapshotDigestHigh,
                    snapshotVariantId, snapshotVariantSeed, snapshotPosition, snapshotRotation,
                    snapshotVisible, snapshotBuried, snapshotRemoved, snapshotPriority,
                    count, rebuildOrder,
                    SlotState, SlotContextId, SlotBallId, SlotInstanceId, SlotGeneration,
                    SlotAttachSequence, SlotEventId, SlotRevision, SlotDigestLow, SlotDigestHigh,
                    SlotVariantId, SlotVariantSeed, SlotPosition, SlotRotation, SlotVisible,
                    SlotBuried, SlotRemoved, SlotPriority, closedContextId, out storedCount);
                LastRebuildStoredCount = storedCount;
                return disposition;
            }

            public RemoteProxyDisposition ClearContext(string contextId, out int removedCount)
            {
                return RemoteProxyMath.ClearContext(contextId,
                    SlotState, SlotContextId, SlotBallId, SlotInstanceId, SlotGeneration,
                    SlotAttachSequence, SlotEventId, SlotRevision, SlotDigestLow, SlotDigestHigh,
                    SlotVariantId, SlotVariantSeed, SlotPosition, SlotRotation, SlotVisible,
                    SlotBuried, SlotRemoved, SlotPriority, closedContextId, out removedCount);
            }

            public void Select(int bufferLength, out int rendered, out int eligible,
                out int tracked, out int buried)
            {
                int[] buffer = new int[bufferLength < 0 ? 0 : bufferLength];
                RemoteProxyMath.TrySelectRendered(
                    SlotState, SlotContextId, SlotBallId, SlotInstanceId, SlotGeneration,
                    SlotAttachSequence, SlotEventId, SlotRevision, SlotDigestLow, SlotDigestHigh,
                    SlotVariantId, SlotVariantSeed, SlotPosition, SlotRotation, SlotVisible,
                    SlotBuried, SlotRemoved, SlotPriority, RendererBudget, buffer,
                    out rendered, out eligible, out tracked, out buried);
            }

            public int RenderedCount()
            {
                int rendered, eligible, tracked, buried;
                Select(RendererBudget, out rendered, out eligible, out tracked, out buried);
                return rendered;
            }

            public int EligibleCount()
            {
                int rendered, eligible, tracked, buried;
                Select(RendererBudget, out rendered, out eligible, out tracked, out buried);
                return eligible;
            }

            public int TrackedCount()
            {
                int rendered, eligible, tracked, buried;
                Select(RendererBudget, out rendered, out eligible, out tracked, out buried);
                return tracked;
            }

            public int MergedBuriedCount()
            {
                int rendered, eligible, tracked, buried;
                Select(RendererBudget, out rendered, out eligible, out tracked, out buried);
                return buried;
            }

            public int[] RenderedSlotIndices(int bufferLength)
            {
                int[] buffer = new int[bufferLength < 0 ? 0 : bufferLength];
                int rendered, eligible, tracked, buried;
                RemoteProxyMath.TrySelectRendered(
                    SlotState, SlotContextId, SlotBallId, SlotInstanceId, SlotGeneration,
                    SlotAttachSequence, SlotEventId, SlotRevision, SlotDigestLow, SlotDigestHigh,
                    SlotVariantId, SlotVariantSeed, SlotPosition, SlotRotation, SlotVisible,
                    SlotBuried, SlotRemoved, SlotPriority, RendererBudget, buffer,
                    out rendered, out eligible, out tracked, out buried);
                int[] result = new int[rendered];
                for (int i = 0; i < rendered; i++) result[i] = buffer[i];
                return result;
            }

            public string[] RenderedIdentities()
            {
                int[] indices = RenderedSlotIndices(RendererBudget);
                string[] identities = new string[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    identities[i] = IdentityOfSlot(indices[i]);
                }
                return identities;
            }

            public string[] RenderedIdentitiesWithBuffer(int bufferLength)
            {
                int[] indices = RenderedSlotIndices(bufferLength);
                string[] identities = new string[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    identities[i] = IdentityOfSlot(indices[i]);
                }
                return identities;
            }

            public Record[] StoredRecords()
            {
                int[] order = new int[SlotState.Length];
                int count = 0;
                for (int i = 0; i < SlotState.Length; i++)
                {
                    if (SlotState[i] == 0) continue;
                    order[count] = i;
                    count++;
                }
                // Python compares summaries in sorted identity order.
                for (int i = 1; i < count; i++)
                {
                    int item = order[i];
                    int j = i - 1;
                    while (j >= 0 && ComesBeforeSlot(item, order[j]))
                    {
                        order[j + 1] = order[j];
                        j--;
                    }
                    order[j + 1] = item;
                }
                Record[] records = new Record[count];
                for (int i = 0; i < count; i++) records[i] = ReadSlot(order[i]);
                return records;
            }

            public string[] StoredIdentities()
            {
                Record[] records = StoredRecords();
                string[] identities = new string[records.Length];
                for (int i = 0; i < records.Length; i++)
                {
                    identities[i] = IdentityOf(records[i]);
                }
                return identities;
            }

            private Record ReadSlot(int slot)
            {
                Record record = new Record();
                record.ContextId = SlotContextId[slot];
                record.BallId = SlotBallId[slot];
                record.InstanceId = SlotInstanceId[slot];
                record.Generation = SlotGeneration[slot];
                record.AttachSequence = SlotAttachSequence[slot];
                record.EventId = SlotEventId[slot];
                record.Revision = SlotRevision[slot];
                record.DigestLow = SlotDigestLow[slot];
                record.DigestHigh = SlotDigestHigh[slot];
                record.VariantId = SlotVariantId[slot];
                record.VariantSeed = SlotVariantSeed[slot];
                record.Position = SlotPosition[slot];
                record.Rotation = SlotRotation[slot];
                record.Visible = SlotVisible[slot];
                record.Buried = SlotBuried[slot];
                record.Removed = SlotRemoved[slot];
                record.PresentationPriority = SlotPriority[slot];
                return record;
            }

            private string IdentityOfSlot(int slot)
            {
                return SlotContextId[slot] + "/" + SlotBallId[slot] + "/" + SlotInstanceId[slot] +
                    "#" + SlotGeneration[slot] + "@" + SlotAttachSequence[slot];
            }

            // The identity order the Python summaries property sorts by.
            private bool ComesBeforeSlot(int left, int right)
            {
                int order = string.CompareOrdinal(SlotContextId[left], SlotContextId[right]);
                if (order != 0) return order < 0;
                order = string.CompareOrdinal(SlotBallId[left], SlotBallId[right]);
                if (order != 0) return order < 0;
                order = string.CompareOrdinal(SlotInstanceId[left], SlotInstanceId[right]);
                if (order != 0) return order < 0;
                if (SlotGeneration[left] != SlotGeneration[right])
                {
                    return SlotGeneration[left] < SlotGeneration[right];
                }
                return SlotAttachSequence[left] < SlotAttachSequence[right];
            }
        }

        private static string IdentityOf(Record record)
        {
            return record.ContextId + "/" + record.BallId + "/" + record.InstanceId +
                "#" + record.Generation + "@" + record.AttachSequence;
        }

        private static Record Summary(
            string instance = "cup",
            int generation = 1,
            int attachSequence = 7,
            string eventId = "evt-7",
            int revision = 0,
            string context = "round-A",
            string ball = "ball-1",
            float positionX = 1f,
            float positionY = 0f,
            float positionZ = 0f,
            int priority = 1,
            bool visible = true,
            bool buried = false,
            bool removed = false,
            string variant = "cup-proxy",
            int seed = 31)
        {
            Record record = new Record();
            record.ContextId = context;
            record.BallId = ball;
            record.InstanceId = instance;
            record.Generation = generation;
            record.AttachSequence = attachSequence;
            record.EventId = eventId;
            record.Revision = revision;
            record.VariantId = variant;
            record.VariantSeed = seed;
            record.Position = new Vector3(positionX, positionY, positionZ);
            record.Rotation = Quaternion.identity;
            record.Visible = visible;
            record.Buried = buried;
            record.Removed = removed;
            record.PresentationPriority = priority;
            // The fixture supplies the digest, like the Python _digest helper; the
            // cache treats it as opaque upstream data.
            bool computed = RemoteProxyMath.TryComputeDigest(
                record.ContextId, record.BallId, record.InstanceId, record.Generation,
                record.AttachSequence, record.EventId, record.Revision, record.VariantId,
                record.VariantSeed, record.Position, record.Rotation,
                record.Visible, record.Buried, record.Removed, record.PresentationPriority,
                out record.DigestLow, out record.DigestHigh);
            Assert.IsTrue(computed, "fixture digest");
            return record;
        }

        [Test]
        public void DuplicateConfirmationIsIdempotentAndIdentityIsUpstream()
        {
            Cache cache = new Cache(8, 4);
            Record record = Summary();
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(record));
            Record[] before = cache.StoredRecords();
            Assert.AreEqual(RemoteProxyDisposition.Duplicate, cache.Apply(record));
            AssertSameRecords(before, cache.StoredRecords());
            Assert.AreEqual(1, cache.TrackedCount());
            // attach_sequence is stored exactly as the upstream fixture supplied it;
            // the layer never allocates or increments identity by itself.
            Assert.AreEqual(7, cache.StoredRecords()[0].AttachSequence);
            // A different upstream sequence is a second record, not a silent replace.
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(Summary(attachSequence: 8)));
            Assert.AreEqual(2, cache.TrackedCount());
        }

        [Test]
        public void SameIdentityRevisionConflictIsRejectedWithoutPartialUpdate()
        {
            Cache cache = new Cache(8, 4);
            Record original = Summary();
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(original));
            Record conflicting = Summary(positionX: 9f);
            Assert.AreEqual(IdentityOf(original), IdentityOf(conflicting), "same identity key");
            Record[] before = cache.StoredRecords();
            Assert.AreEqual(RemoteProxyDisposition.Conflict, cache.Apply(conflicting));
            AssertSameRecords(before, cache.StoredRecords());
            Assert.AreEqual(1, cache.RenderedCount());
        }

        [Test]
        public void RevisionsUpdateAtomicallyAndOlderRevisionIsStale()
        {
            Cache cache = new Cache(8, 4);
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(Summary(revision: 2, positionX: 2f)));
            Assert.AreEqual(RemoteProxyDisposition.Updated, cache.Apply(Summary(revision: 3, positionX: 3f)));
            Assert.AreEqual(RemoteProxyDisposition.StaleRevision, cache.Apply(Summary(revision: 1, positionX: 1f)));
            Assert.AreEqual(3, cache.StoredRecords()[0].Revision);
            Assert.AreEqual(3f, cache.StoredRecords()[0].Position.x, 0f);
        }

        [Test]
        public void NewGenerationReplacesOldAndLateOldGenerationCannotResurrect()
        {
            Cache cache = new Cache(8, 3);
            Record old = Summary(generation: 4, attachSequence: 11, eventId: "evt-old");
            Record current = Summary(generation: 5, attachSequence: 12, eventId: "evt-new");
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(old));
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(current));
            // The older generation was recycled, so only the current record is left.
            Assert.AreEqual(1, cache.TrackedCount());
            Assert.AreEqual(5, cache.StoredRecords()[0].Generation);
            Assert.AreEqual(RemoteProxyDisposition.StaleGeneration, cache.Apply(old));
            Assert.AreEqual(1, cache.TrackedCount());
        }

        [Test]
        public void TombstoneRemovalAndReplacementSequence()
        {
            Cache cache = new Cache(8, 3);
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(Summary()));
            Assert.AreEqual(RemoteProxyDisposition.Updated, cache.Apply(Summary(revision: 1, removed: true)));
            Assert.AreEqual(RemoteProxyDisposition.Duplicate, cache.Apply(Summary(revision: 1, removed: true)));
            Assert.AreEqual(0, cache.RenderedCount());
            // A later revision cannot resurrect the object; only a new upstream
            // attach sequence starts a new object.
            Assert.AreEqual(RemoteProxyDisposition.RemovedTombstone,
                cache.Apply(Summary(revision: 2, removed: false, positionX: 5f)));
            Assert.AreEqual(RemoteProxyDisposition.Accepted,
                cache.Apply(Summary(attachSequence: 8, eventId: "evt-8", revision: 0)));
            Assert.AreEqual(1, cache.RenderedCount());
        }

        [Test]
        public void ContextClearPreventsOldPacketRevivalButNewRoundIsAllowed()
        {
            Cache cache = new Cache(8, 4);
            Record old = Summary();
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(old));
            int removedCount;
            Assert.AreEqual(RemoteProxyDisposition.Cleared, cache.ClearContext("round-A", out removedCount));
            Assert.AreEqual(1, removedCount);
            Assert.AreEqual(RemoteProxyDisposition.ClearedContext, cache.Apply(old));
            Assert.AreEqual(0, cache.TrackedCount());
            Assert.AreEqual(RemoteProxyDisposition.Accepted,
                cache.Apply(Summary(context: "round-B", eventId: "evt-new-round")));
            // A cleared round cannot be rebuilt; the next round needs a new id.
            Assert.AreEqual(RemoteProxyDisposition.ClearedContext, cache.Rebuild("round-A", new Record[0]));
        }

        [Test]
        public void RendererBudgetIsDeterministicAndNeverChangesSummaries()
        {
            Record[] events =
            {
                Summary(instance: "cup", attachSequence: 1, eventId: "cup", priority: 4),
                Summary(instance: "book", attachSequence: 2, eventId: "book", priority: 8),
                Summary(instance: "pin", attachSequence: 3, eventId: "pin", priority: 8),
                Summary(instance: "hidden", attachSequence: 4, eventId: "hidden", priority: 100, visible: false),
                Summary(instance: "buried", attachSequence: 5, eventId: "buried", priority: 90, buried: true)
            };
            Cache first = new Cache(8, 2);
            Cache second = new Cache(8, 2);
            for (int i = 0; i < events.Length; i++) first.Apply(events[i]);
            for (int i = events.Length - 1; i >= 0; i--) second.Apply(events[i]);

            // Equal priorities (book and pin both 8) break by ascending identity, so
            // the two highest-priority visible records are drawn in that order.
            CollectionAssert.AreEqual(
                new[] { IdentityOf(events[1]), IdentityOf(events[2]) },
                first.RenderedIdentities());
            CollectionAssert.AreEqual(first.RenderedIdentities(), second.RenderedIdentities());
            Assert.AreEqual(2, first.RenderedCount());
            Assert.AreEqual(1, first.MergedBuriedCount());
            Assert.AreEqual(5, first.TrackedCount());
            AssertSameRecords(first.StoredRecords(), second.StoredRecords());
        }

        [Test]
        public void ZeroRendererBudgetKeepsConfirmedSummaryWithoutRendering()
        {
            Cache cache = new Cache(3, 0);
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(Summary()));
            Assert.AreEqual(1, cache.TrackedCount());
            Assert.AreEqual(0, cache.RenderedCount());
            Assert.AreEqual(1, cache.EligibleCount());
            Assert.AreEqual(1, cache.StoredRecords().Length);
        }

        [Test]
        public void InvalidSummaryIsRejectedWithoutMutatingCache()
        {
            Cache cache = new Cache(4, 2);
            Record valid = Summary();
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(valid));
            Record[] before = cache.StoredRecords();

            Record notFinite = Summary();
            notFinite.Position = new Vector3(float.NaN, 0f, 0f);
            Record zeroRotation = Summary();
            zeroRotation.Rotation = new Quaternion(0f, 0f, 0f, 0f);
            Record negativeGeneration = Summary(generation: -1);
            Record emptyVariant = Summary(variant: "");
            Record negativePriority = Summary(priority: -1);
            Record negativeRevision = Summary(revision: -1);
            Record nullContext = Summary();
            nullContext.ContextId = null;
            Record[] invalids =
            {
                notFinite, zeroRotation, negativeGeneration, emptyVariant,
                negativePriority, negativeRevision, nullContext
            };
            for (int i = 0; i < invalids.Length; i++)
            {
                Assert.AreEqual(RemoteProxyDisposition.Invalid, cache.Apply(invalids[i]), "invalid #" + i);
                AssertSameRecords(before, cache.StoredRecords());
            }
        }

        [Test]
        public void CapacityFailureIsAtomic()
        {
            Cache cache = new Cache(1, 1);
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(Summary()));
            Record[] before = cache.StoredRecords();
            Assert.AreEqual(RemoteProxyDisposition.Capacity,
                cache.Apply(Summary(instance: "book", attachSequence: 9, eventId: "evt-book")));
            AssertSameRecords(before, cache.StoredRecords());
            Assert.AreEqual(1, cache.RenderedCount());
        }

        [Test]
        public void RebuildSnapshotIsOrderIndependentAndAtomic()
        {
            Record[] entries =
            {
                Summary(instance: "cup", generation: 1, attachSequence: 1, eventId: "cup-old"),
                Summary(instance: "cup", generation: 2, attachSequence: 2, eventId: "cup-new", priority: 8),
                Summary(instance: "book", generation: 1, attachSequence: 3, eventId: "book", priority: 4)
            };
            Cache first = new Cache(8, 2);
            Cache second = new Cache(8, 2);
            Assert.AreEqual(RemoteProxyDisposition.Rebuilt, first.Rebuild("round-A", entries));
            Record[] reversed = { entries[2], entries[1], entries[0] };
            Assert.AreEqual(RemoteProxyDisposition.Rebuilt, second.Rebuild("round-A", reversed));
            AssertSameRecords(first.StoredRecords(), second.StoredRecords());
            CollectionAssert.AreEqual(first.RenderedIdentities(), second.RenderedIdentities());
            // The snapshot's older cup generation was superseded, so two records remain.
            Assert.AreEqual(2, first.TrackedCount());
            Assert.AreEqual(2, first.LastRebuildStoredCount);

            // One conflicting record anywhere in the snapshot leaves the cache alone.
            Record conflict = Summary(instance: "book", generation: 1, attachSequence: 3,
                eventId: "book", positionX: 99f);
            Record[] withConflict = { entries[0], entries[1], entries[2], conflict };
            Record[] before = first.StoredRecords();
            Assert.AreEqual(RemoteProxyDisposition.Conflict, first.Rebuild("round-A", withConflict));
            AssertSameRecords(before, first.StoredRecords());
        }

        [Test]
        public void RebuildPreservesOtherContextAndRevisionTombstone()
        {
            Cache cache = new Cache(8, 5);
            Record other = Summary(context: "round-B", instance: "other", eventId: "other");
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(other));
            Record[] snapshot = { Summary(revision: 0), Summary(revision: 1, removed: true) };
            Assert.AreEqual(RemoteProxyDisposition.Rebuilt, cache.Rebuild("round-A", snapshot));
            Assert.AreEqual(2, cache.TrackedCount());
            Assert.AreEqual(RemoteProxyDisposition.RemovedTombstone, cache.Apply(Summary(revision: 2)));
            // The other round's record survived the rebuild untouched.
            Record[] stored = cache.StoredRecords();
            int otherCount = 0;
            for (int i = 0; i < stored.Length; i++)
            {
                if (stored[i].ContextId != "round-B") continue;
                otherCount++;
                Assert.AreEqual("other", stored[i].InstanceId);
            }
            Assert.AreEqual(1, otherCount);
            Assert.AreEqual(1, cache.RenderedCount());
        }

        [Test]
        public void ClosedContextTombstonesAreBoundedAndClearIsAtomicAtCapacity()
        {
            Cache cache = new Cache(1, 1);
            Record first = Summary(context: "round-one", eventId: "one");
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(first));
            int removedCount;
            Assert.AreEqual(RemoteProxyDisposition.Cleared, cache.ClearContext("round-one", out removedCount));
            Assert.AreEqual(1, removedCount);

            Record second = Summary(context: "round-two", eventId: "two");
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(second));
            Record[] before = cache.StoredRecords();
            // The closed-context table is full, so the clear is refused untouched.
            Assert.AreEqual(RemoteProxyDisposition.Capacity, cache.ClearContext("round-two", out removedCount));
            Assert.AreEqual(0, removedCount);
            AssertSameRecords(before, cache.StoredRecords());
            Assert.AreEqual(RemoteProxyDisposition.Duplicate, cache.Apply(second));
            Assert.AreEqual(RemoteProxyDisposition.ClearedContext, cache.Apply(first));
        }

        [Test]
        public void SameSequenceDifferentEventIdIsAConflict()
        {
            Cache cache = new Cache(4, 2);
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(Summary()));
            Assert.AreEqual(RemoteProxyDisposition.Conflict,
                cache.Apply(Summary(eventId: "different-event")));
            Assert.AreEqual(1, cache.TrackedCount());
        }

        [Test]
        public void RendererBudgetIsStrictlyEnforced()
        {
            Record[] events =
            {
                Summary(instance: "a", attachSequence: 1, eventId: "a", priority: 0),
                Summary(instance: "b", attachSequence: 2, eventId: "b", priority: 1),
                Summary(instance: "c", attachSequence: 3, eventId: "c", priority: 2),
                Summary(instance: "d", attachSequence: 4, eventId: "d", priority: 3),
                Summary(instance: "e", attachSequence: 5, eventId: "e", priority: 4)
            };
            for (int budget = 0; budget <= 6; budget++)
            {
                Cache cache = new Cache(5, budget);
                for (int i = 0; i < events.Length; i++) cache.Apply(events[i]);
                int rendered, eligible, tracked, buried;
                cache.Select(budget, out rendered, out eligible, out tracked, out buried);
                Assert.AreEqual(5, eligible, "eligible at budget " + budget);
                Assert.AreEqual(5, tracked, "tracked at budget " + budget);
                Assert.AreEqual(0, buried, "buried at budget " + budget);
                Assert.AreEqual(Mathf.Min(budget, 5), rendered, "rendered at budget " + budget);
                Assert.LessOrEqual(cache.RenderedIdentities().Length, budget, "cap at budget " + budget);
            }

            // A buffer shorter than the budget caps the write without changing which
            // records win: the two highest priorities are still selected.
            Cache shortBuffer = new Cache(5, 4);
            for (int i = 0; i < events.Length; i++) shortBuffer.Apply(events[i]);
            CollectionAssert.AreEqual(
                new[] { IdentityOf(events[4]), IdentityOf(events[3]) },
                shortBuffer.RenderedIdentitiesWithBuffer(2));
        }

        [Test]
        public void RebuildCapacityIsCheckedDuringReplay()
        {
            // A snapshot that supersedes an older generation still fits a table
            // sized for the final records: the old generation is purged before the
            // next insert, so the peak stays at the final size.
            Cache tight = new Cache(2, 2);
            Record[] superseding =
            {
                Summary(instance: "cup", generation: 1, attachSequence: 1, eventId: "a"),
                Summary(instance: "cup", generation: 2, attachSequence: 2, eventId: "b"),
                Summary(instance: "book", generation: 1, attachSequence: 3, eventId: "c")
            };
            Assert.AreEqual(RemoteProxyDisposition.Rebuilt, tight.Rebuild("round-A", superseding));
            Assert.AreEqual(2, tight.TrackedCount());

            // Three siblings at one generation really do need three slots at the
            // same time, so a two-slot table refuses the snapshot and keeps state.
            Cache tooSmall = new Cache(2, 2);
            Record[] peak =
            {
                Summary(instance: "cup", generation: 1, attachSequence: 1, eventId: "a"),
                Summary(instance: "cup", generation: 1, attachSequence: 2, eventId: "b"),
                Summary(instance: "cup", generation: 1, attachSequence: 3, eventId: "c"),
                Summary(instance: "cup", generation: 2, attachSequence: 4, eventId: "d")
            };
            Assert.AreEqual(RemoteProxyDisposition.Capacity, tooSmall.Rebuild("round-A", peak));
            Assert.AreEqual(0, tooSmall.TrackedCount());

            // Two revisions of one identity are one record, so they fit a one-slot
            // table, and the later revision's tombstone survives.
            Cache oneSlot = new Cache(1, 1);
            Record[] revisions = { Summary(revision: 0), Summary(revision: 1, removed: true) };
            Assert.AreEqual(RemoteProxyDisposition.Rebuilt, oneSlot.Rebuild("round-A", revisions));
            Assert.AreEqual(1, oneSlot.TrackedCount());
            Assert.AreEqual(1, oneSlot.LastRebuildStoredCount);
            Assert.AreEqual(0, oneSlot.RenderedCount());
        }

        [Test]
        public void RecyclingKeepsTrackedVolumeAndLeavesOtherContextsAlone()
        {
            Cache cache = new Cache(2, 2);
            Assert.AreEqual(RemoteProxyDisposition.Accepted,
                cache.Apply(Summary(instance: "cup", generation: 1, attachSequence: 1, eventId: "cup")));
            Assert.AreEqual(RemoteProxyDisposition.Accepted,
                cache.Apply(Summary(context: "round-B", instance: "other", eventId: "other")));
            Assert.AreEqual(2, cache.TrackedCount());
            // The generation bump recycles the cup slot inside the same table: the
            // tracked volume and the other round's record are unchanged.
            Assert.AreEqual(RemoteProxyDisposition.Accepted,
                cache.Apply(Summary(instance: "cup", generation: 2, attachSequence: 2, eventId: "cup-2")));
            Assert.AreEqual(2, cache.TrackedCount());
            CollectionAssert.AreEqual(
                new[] { "round-A/ball-1/cup#2@2", "round-B/ball-1/other#1@7" },
                cache.StoredIdentities());
            Assert.AreEqual(2, cache.RenderedCount());
        }

        [Test]
        public void ContentDigestIsDeterministicAndFieldSensitive()
        {
            int low, high, otherLow, otherHigh;
            Assert.IsTrue(RemoteProxyMath.TryComputeDigest("round-A", "ball-1", "cup", 1, 7, "evt-7",
                0, "cup-proxy", 31, Vector3.zero, Quaternion.identity, true, false, false, 1,
                out low, out high));
            Assert.IsTrue(RemoteProxyMath.TryComputeDigest("round-A", "ball-1", "cup", 1, 7, "evt-7",
                0, "cup-proxy", 31, Vector3.zero, Quaternion.identity, true, false, false, 1,
                out otherLow, out otherHigh));
            Assert.AreEqual(low, otherLow);
            Assert.AreEqual(high, otherHigh);

            Assert.IsTrue(RemoteProxyMath.TryComputeDigest("round-A", "ball-1", "cup", 1, 7, "evt-7",
                1, "cup-proxy", 31, Vector3.zero, Quaternion.identity, true, false, false, 1,
                out otherLow, out otherHigh));
            Assert.IsFalse(low == otherLow && high == otherHigh, "revision changes the digest");

            Assert.IsTrue(RemoteProxyMath.TryComputeDigest("round-A", "ball-1", "cup", 1, 7, "evt-7",
                0, "cup-proxy", 31, new Vector3(1f, 0f, 0f), Quaternion.identity, true, false, false, 1,
                out otherLow, out otherHigh));
            Assert.IsFalse(low == otherLow && high == otherHigh, "position changes the digest");

            Assert.IsFalse(RemoteProxyMath.TryComputeDigest(null, "ball-1", "cup", 1, 7, "evt-7",
                0, "cup-proxy", 31, Vector3.zero, Quaternion.identity, true, false, false, 1,
                out otherLow, out otherHigh));
        }

        [Test]
        public void DigestIsOpaqueAndNotPartOfIdentity()
        {
            Cache cache = new Cache(4, 2);
            Record cup = Summary(instance: "cup");
            Record book = Summary(instance: "book");
            book.DigestLow = cup.DigestLow;
            book.DigestHigh = cup.DigestHigh;
            // The layer keys on the upstream identity tuple, so a reused digest is not
            // read as the same object.
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(cup));
            Assert.AreEqual(RemoteProxyDisposition.Accepted, cache.Apply(book));
            Assert.AreEqual(2, cache.TrackedCount());
        }

        [Test]
        public void RemoteProxyHasNoCollisionOrScoreSurface()
        {
            // The Python tests assert the cache has no ledger or collider attribute.
            // The C# equivalent is a static-only class with no public state and no
            // gameplay member: nothing here can feed collision, scoring or banking.
            MethodInfo[] methods = typeof(RemoteProxyMath).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            string[] forbidden = { "Collider", "Physics", "Ledger", "Score", "Bank", "Pickup", "Avatar" };
            for (int i = 0; i < methods.Length; i++)
            {
                for (int j = 0; j < forbidden.Length; j++)
                {
                    StringAssert.DoesNotContain(forbidden[j], methods[i].Name,
                        "public API " + methods[i].Name);
                }
            }
            Assert.AreEqual(0,
                typeof(RemoteProxyMath).GetFields(BindingFlags.Public | BindingFlags.Static).Length,
                "no public state");

            // The only static state is const: nothing mutable can leak between the
            // visual cache and the collision or scoring paths.
            FieldInfo[] fields = typeof(RemoteProxyMath).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                Assert.IsTrue(fields[i].IsLiteral, "static field " + fields[i].Name + " is not const");
            }
        }

        private static void AssertSameRecords(Record[] expected, Record[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "record count");
            for (int i = 0; i < expected.Length; i++)
            {
                Record left = expected[i];
                Record right = actual[i];
                Assert.AreEqual(IdentityOf(left), IdentityOf(right), "identity " + i);
                Assert.AreEqual(left.EventId, right.EventId, "event " + i);
                Assert.AreEqual(left.Revision, right.Revision, "revision " + i);
                Assert.AreEqual(left.DigestLow, right.DigestLow, "digest low " + i);
                Assert.AreEqual(left.DigestHigh, right.DigestHigh, "digest high " + i);
                Assert.AreEqual(left.VariantId, right.VariantId, "variant " + i);
                Assert.AreEqual(left.VariantSeed, right.VariantSeed, "variant seed " + i);
                Assert.AreEqual(left.Position.x, right.Position.x, 0f, "position x " + i);
                Assert.AreEqual(left.Position.y, right.Position.y, 0f, "position y " + i);
                Assert.AreEqual(left.Position.z, right.Position.z, 0f, "position z " + i);
                Assert.AreEqual(left.Rotation.x, right.Rotation.x, 0f, "rotation x " + i);
                Assert.AreEqual(left.Rotation.y, right.Rotation.y, 0f, "rotation y " + i);
                Assert.AreEqual(left.Rotation.z, right.Rotation.z, 0f, "rotation z " + i);
                Assert.AreEqual(left.Rotation.w, right.Rotation.w, 0f, "rotation w " + i);
                Assert.AreEqual(left.Visible, right.Visible, "visible " + i);
                Assert.AreEqual(left.Buried, right.Buried, "buried " + i);
                Assert.AreEqual(left.Removed, right.Removed, "removed " + i);
                Assert.AreEqual(left.PresentationPriority, right.PresentationPriority, "priority " + i);
            }
        }
    }
}
