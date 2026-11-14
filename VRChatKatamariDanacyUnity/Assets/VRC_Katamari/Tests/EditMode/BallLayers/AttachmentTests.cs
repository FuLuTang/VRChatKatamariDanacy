using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_attachment/test_attachment.py and the
    /// ledger half of Docs/tests/ball_attachment/test_growth_attachment_simulation.py.
    /// Expected values are hand calculations or geometric invariants (a +Y rotation
    /// by hand, a pure translation, an identity/instance ordering), never a re-run of
    /// the implementation. Lengths are metres in world space with +Y up, matching the
    /// Python model.
    /// </summary>
    public class AttachmentTests
    {
        private const float Tolerance = 1e-5f;

        /// <summary>
        /// Test-only driver that owns the ten parallel ledger columns a behaviour will
        /// own, plus the snapshot columns. Every decision is a call into AttachmentMath;
        /// the driver exists because the layer is deliberately stateless, so the ledger
        /// table needs an owner.
        /// </summary>
        private sealed class Ledger
        {
            public const int Capacity = 8;

            public readonly string[] ContextIds = new string[Capacity];
            public readonly string[] BallIds = new string[Capacity];
            public readonly string[] InstanceIds = new string[Capacity];
            public readonly int[] Generations = new int[Capacity];
            public readonly long[] AttachSequences = new long[Capacity];
            public readonly string[] EventIds = new string[Capacity];
            public readonly Vector3[] LocalPositions = new Vector3[Capacity];
            public readonly Quaternion[] LocalRotations = new Quaternion[Capacity];
            public readonly Vector3[] ObjectScales = new Vector3[Capacity];
            public readonly bool[] IsRevoked = new bool[Capacity];
            public int Count;

            private readonly string[] snapshotContextIds = new string[Capacity];
            private readonly string[] snapshotBallIds = new string[Capacity];
            private readonly string[] snapshotInstanceIds = new string[Capacity];
            private readonly int[] snapshotGenerations = new int[Capacity];
            private readonly long[] snapshotAttachSequences = new long[Capacity];
            private readonly string[] snapshotEventIds = new string[Capacity];
            private readonly Vector3[] snapshotBallPositions = new Vector3[Capacity];
            private readonly Quaternion[] snapshotBallRotations = new Quaternion[Capacity];
            private readonly Vector3[] snapshotObjectPositions = new Vector3[Capacity];
            private readonly Quaternion[] snapshotObjectRotations = new Quaternion[Capacity];
            private readonly Vector3[] snapshotObjectScales = new Vector3[Capacity];
            private readonly bool[] snapshotIsRevoked = new bool[Capacity];
            private int snapshotCount;

            public bool Apply(string contextId, string ballId, string instanceId, int generation,
                long attachSequence, string eventId, Vector3 ballPosition, Quaternion ballRotation,
                Vector3 objectPosition, Quaternion objectRotation, Vector3 objectScale,
                out int recordIndex, out bool created)
            {
                return AttachmentMath.TryApplyConfirmedEvent(ContextIds, BallIds, InstanceIds, Generations,
                    AttachSequences, EventIds, LocalPositions, LocalRotations, ObjectScales, IsRevoked,
                    ref Count, contextId, ballId, instanceId, generation, attachSequence, eventId,
                    ballPosition, ballRotation, objectPosition, objectRotation, objectScale,
                    out recordIndex, out created);
            }

            // Mirrors the Python test helper event(): ball "ball-A", generation 1, event
            // id "evt-{sequence}-{instance}", ball at the origin unrotated, object at
            // (x,2,3) unrotated with scale (0.4,0.7,1.2).
            public bool ApplyPickup(string contextId, string instanceId, long attachSequence, float x,
                out int recordIndex, out bool created)
            {
                return Apply(contextId, "ball-A", instanceId, 1, attachSequence,
                    "evt-" + attachSequence + "-" + instanceId, Vector3.zero, Quaternion.identity,
                    new Vector3(x, 2f, 3f), Quaternion.identity, new Vector3(0.4f, 0.7f, 1.2f),
                    out recordIndex, out created);
            }

            public bool Revoke(string contextId, string ballId, string instanceId, int generation,
                long attachSequence, string eventId, out bool existed)
            {
                return AttachmentMath.TryRevokeEvent(ContextIds, BallIds, InstanceIds, Generations,
                    AttachSequences, EventIds, LocalPositions, LocalRotations, ObjectScales, IsRevoked,
                    ref Count, contextId, ballId, instanceId, generation, attachSequence, eventId,
                    out existed);
            }

            public bool ClearContext(string contextId)
            {
                return AttachmentMath.TryClearContext(ContextIds, BallIds, InstanceIds, Generations,
                    AttachSequences, EventIds, LocalPositions, LocalRotations, ObjectScales, IsRevoked,
                    ref Count, contextId);
            }

            public void ResetSnapshot()
            {
                snapshotCount = 0;
            }

            public void AddSnapshotRow(string contextId, string instanceId, long attachSequence,
                Vector3 objectPosition, Quaternion ballRotation, bool revoked)
            {
                Assert.Less(snapshotCount, Capacity);
                snapshotContextIds[snapshotCount] = contextId;
                snapshotBallIds[snapshotCount] = "ball-A";
                snapshotInstanceIds[snapshotCount] = instanceId;
                snapshotGenerations[snapshotCount] = 1;
                snapshotAttachSequences[snapshotCount] = attachSequence;
                snapshotEventIds[snapshotCount] = "evt-" + attachSequence + "-" + instanceId;
                snapshotBallPositions[snapshotCount] = Vector3.zero;
                snapshotBallRotations[snapshotCount] = ballRotation;
                snapshotObjectPositions[snapshotCount] = objectPosition;
                snapshotObjectRotations[snapshotCount] = Quaternion.identity;
                snapshotObjectScales[snapshotCount] = new Vector3(0.4f, 0.7f, 1.2f);
                snapshotIsRevoked[snapshotCount] = revoked;
                snapshotCount++;
            }

            public bool Rebuild(string contextId)
            {
                return AttachmentMath.TryRebuildContextSnapshot(ContextIds, BallIds, InstanceIds,
                    Generations, AttachSequences, EventIds, LocalPositions, LocalRotations, ObjectScales,
                    IsRevoked, ref Count, contextId,
                    snapshotContextIds, snapshotBallIds, snapshotInstanceIds, snapshotGenerations,
                    snapshotAttachSequences, snapshotEventIds, snapshotBallPositions, snapshotBallRotations,
                    snapshotObjectPositions, snapshotObjectRotations, snapshotObjectScales,
                    snapshotIsRevoked, snapshotCount);
            }

            public int[] ActiveOrder()
            {
                int[] order = new int[Capacity];
                int orderCount;
                Assert.IsTrue(AttachmentMath.TryBuildActiveOrder(ContextIds, BallIds, InstanceIds,
                    Generations, AttachSequences, EventIds, IsRevoked, Count, order, out orderCount));
                int[] result = new int[orderCount];
                for (int i = 0; i < orderCount; i++) result[i] = order[i];
                return result;
            }

            public string[] ActiveInstances()
            {
                int[] order = ActiveOrder();
                string[] result = new string[order.Length];
                for (int i = 0; i < order.Length; i++) result[i] = InstanceIds[order[i]];
                return result;
            }

            public long[] ActiveSequences()
            {
                int[] order = ActiveOrder();
                long[] result = new long[order.Length];
                for (int i = 0; i < order.Length; i++) result[i] = AttachSequences[order[i]];
                return result;
            }

            public string[] Contexts()
            {
                int[] order = ActiveOrder();
                string[] result = new string[order.Length];
                for (int i = 0; i < order.Length; i++) result[i] = ContextIds[order[i]];
                return result;
            }

            public string[] InstancesFor(string contextId)
            {
                int[] order = ActiveOrder();
                int count = 0;
                string[] buffer = new string[order.Length];
                for (int i = 0; i < order.Length; i++)
                {
                    if (ContextIds[order[i]] != contextId) continue;
                    buffer[count] = InstanceIds[order[i]];
                    count++;
                }
                string[] result = new string[count];
                for (int i = 0; i < count; i++) result[i] = buffer[i];
                return result;
            }

            public int IndexOfInstance(string instanceId)
            {
                int[] order = ActiveOrder();
                for (int i = 0; i < order.Length; i++)
                {
                    if (InstanceIds[order[i]] == instanceId) return order[i];
                }
                return -1;
            }
        }

        [Test]
        public void WorldLocalRoundtripAgainstIndependentYRotationMatrix()
        {
            // +60 deg about +Y, ball at (2,-1,4), object at (6,2,5). The world offset is
            // (4,3,1), and an independent inverse +Y rotation is
            // x' = cos*x - sin*z, z' = sin*x + cos*z.
            float angle = Mathf.PI / 3f;
            Quaternion ballRotation = Quaternion.AngleAxis(60f, Vector3.up);
            Vector3 ballPosition = new Vector3(2f, -1f, 4f);
            Vector3 objectPosition = new Vector3(6f, 2f, 5f);
            Vector3 objectScale = new Vector3(0.4f, 0.7f, 1.2f);

            Vector3 localPosition;
            Quaternion localRotation;
            Assert.IsTrue(AttachmentMath.TryWorldPoseToLocal(ballPosition, ballRotation, objectPosition,
                Quaternion.identity, objectScale, out localPosition, out localRotation));

            float dx = 4f;
            float dy = 3f;
            float dz = 1f;
            Vector3 expectedPosition = new Vector3(Mathf.Cos(angle) * dx - Mathf.Sin(angle) * dz, dy,
                Mathf.Sin(angle) * dx + Mathf.Cos(angle) * dz);
            AssertVector(expectedPosition, localPosition);

            // The stored rotation is the object relative to the ball root, which is the
            // inverse of the ball rotation when the object itself is unrotated.
            AssertSameRotation(Quaternion.Inverse(ballRotation), localRotation);

            Vector3 worldPosition;
            Quaternion worldRotation;
            Assert.IsTrue(AttachmentMath.TryLocalPoseToWorld(ballPosition, ballRotation, localPosition,
                localRotation, objectScale, out worldPosition, out worldRotation));
            AssertVector(objectPosition, worldPosition);
            AssertSameRotation(Quaternion.identity, worldRotation);

            // The object's own scale is validated but never applied: the same local pose
            // under a moved ball lands on the moved ball plus the same world offset.
            Vector3 movedBall = new Vector3(8f, 0f, -2f);
            Assert.IsTrue(AttachmentMath.TryLocalPoseToWorld(movedBall, ballRotation, localPosition,
                localRotation, objectScale, out worldPosition, out worldRotation));
            AssertVector(new Vector3(movedBall.x + dx, movedBall.y + dy, movedBall.z + dz), worldPosition);

            // A non-positive scale component is not a valid attachment scale.
            Assert.IsFalse(AttachmentMath.TryWorldPoseToLocal(ballPosition, ballRotation, objectPosition,
                Quaternion.identity, new Vector3(0.4f, 0f, 1.2f), out localPosition, out localRotation));
        }

        [Test]
        public void AuthoritativeIdentityIdempotencyAndArrivalOrder()
        {
            Ledger a = new Ledger();
            Ledger b = new Ledger();
            int index;
            bool created;

            Assert.IsTrue(a.ApplyPickup("round-1", "i1", 90L, 1f, out index, out created));
            Assert.IsTrue(created);
            Assert.AreEqual(0, index);
            Assert.IsTrue(a.ApplyPickup("round-1", "i2", 44L, 2f, out index, out created));
            Assert.IsTrue(created);

            // A byte-identical repeat is the same record, not a second row.
            Assert.IsTrue(a.ApplyPickup("round-1", "i1", 90L, 1f, out index, out created));
            Assert.IsFalse(created);
            Assert.AreEqual(0, index);
            Assert.AreEqual(2, a.Count);

            // The same two confirmations in the other arrival order.
            Assert.IsTrue(b.ApplyPickup("round-1", "i2", 44L, 2f, out index, out created));
            Assert.IsTrue(b.ApplyPickup("round-1", "i1", 90L, 1f, out index, out created));

            // Records are reported in identity order, so arrival order is invisible:
            // instance "i1" sorts before "i2" even though its sequence (90) is larger.
            CollectionAssert.AreEqual(new long[] { 90L, 44L }, a.ActiveSequences());
            CollectionAssert.AreEqual(a.ActiveSequences(), b.ActiveSequences());
            CollectionAssert.AreEqual(new[] { "i1", "i2" }, a.ActiveInstances());
            Assert.AreEqual(2, b.Count);

            // The remote proxy key is strictly the contract's four fields.
            long key;
            long again;
            long other;
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("seed", "i", 1, 2L, out key));
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("seed", "i", 1, 2L, out again));
            Assert.AreEqual(key, again);
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("other-seed", "i", 1, 2L, out other));
            Assert.AreNotEqual(key, other);
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("seed", "other", 1, 2L, out other));
            Assert.AreNotEqual(key, other);
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("seed", "i", 2, 2L, out other));
            Assert.AreNotEqual(key, other);
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("seed", "i", 1, 3L, out other));
            Assert.AreNotEqual(key, other);

            // The length prefixes distinguish fields whose naive concatenation is equal.
            long abC;
            long aBc;
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("ab", "c", 1, 2L, out abC));
            Assert.IsTrue(AttachmentMath.TryRemoteProxyKeyHash("a", "bc", 1, 2L, out aBc));
            Assert.AreNotEqual(abC, aBc);

            // Empty text and negative (i.e. unsigned-range) integers are rejected. The
            // Python case passed True and 1<<64; both are outside int64 here.
            Assert.IsFalse(AttachmentMath.TryRemoteProxyKeyHash("", "i", 1, 2L, out key));
            Assert.IsFalse(AttachmentMath.TryRemoteProxyKeyHash("seed", "", 1, 2L, out key));
            Assert.IsFalse(AttachmentMath.TryRemoteProxyKeyHash("seed", "i", -1, 2L, out key));
            Assert.IsFalse(AttachmentMath.TryRemoteProxyKeyHash("seed", "i", 1, -1L, out key));

            // The event identity has its own six-field framing, not the remote key.
            long eventKey;
            long eventAgain;
            Assert.IsTrue(AttachmentMath.TryEventIdentityHash("r", "b", "i", 1, 2L, "e", out eventKey));
            Assert.IsTrue(AttachmentMath.TryEventIdentityHash("r", "b", "i", 1, 2L, "e", out eventAgain));
            Assert.AreEqual(eventKey, eventAgain);
            Assert.IsTrue(AttachmentMath.TryEventIdentityHash("r", "b", "i", 1, 2L, "different", out other));
            Assert.AreNotEqual(eventKey, other);
            Assert.IsTrue(AttachmentMath.TryEventIdentityHash("ab", "c", "i", 1, 2L, "e", out abC));
            Assert.IsTrue(AttachmentMath.TryEventIdentityHash("a", "bc", "i", 1, 2L, "e", out aBc));
            Assert.AreNotEqual(abC, aBc);
        }

        [Test]
        public void ConflictRevokeTombstoneAndExactGenerationScope()
        {
            Ledger ledger = new Ledger();
            int index;
            bool created;
            Assert.IsTrue(ledger.ApplyPickup("round-1", "same", 5L, 0f, out index, out created));
            Assert.IsTrue(created);
            Assert.IsTrue(ledger.ApplyPickup("round-1", "same", 5L, 0f, out index, out created));
            Assert.IsFalse(created);

            // Same identity, different pose: rejected, and the stored row is untouched.
            Assert.IsFalse(ledger.ApplyPickup("round-1", "same", 5L, 100f, out index, out created));
            Assert.AreEqual(1, ledger.Count);
            AssertVector(new Vector3(0f, 2f, 3f), ledger.LocalPositions[0]);

            bool existed;
            Assert.IsTrue(ledger.Revoke("round-1", "ball-A", "same", 1, 5L, "evt-5-same", out existed));
            Assert.IsTrue(existed);
            Assert.AreEqual(0, ledger.ActiveInstances().Length);

            // A revoked identity can never be revived by replaying the confirmation.
            Assert.IsFalse(ledger.ApplyPickup("round-1", "same", 5L, 0f, out index, out created));

            // A new generation and a new round are separate identities, even with the
            // same instance id, sequence and event id.
            Assert.IsTrue(ledger.Apply("round-1", "ball-A", "same", 2, 5L, "evt-5-same",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 2f, 3f), Quaternion.identity,
                new Vector3(0.4f, 0.7f, 1.2f), out index, out created));
            Assert.IsTrue(created);
            Assert.IsTrue(ledger.Apply("round-2", "ball-A", "same", 1, 5L, "evt-5-same",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 2f, 3f), Quaternion.identity,
                new Vector3(0.4f, 0.7f, 1.2f), out index, out created));
            Assert.IsTrue(created);
            Assert.AreEqual(2, ledger.ActiveInstances().Length);
        }

        [Test]
        public void InvalidInputsAreAtomicIncludingIdentityAndPoseFailures()
        {
            Ledger ledger = new Ledger();
            int index;
            bool created;
            Assert.IsTrue(ledger.ApplyPickup("round-1", "valid", 2L, 0f, out index, out created));
            int countBefore = ledger.Count;
            string[] orderBefore = ledger.ActiveInstances();

            // The Python case passed True as the attach sequence; the typed equivalent
            // is an out-of-range negative sequence.
            Assert.IsFalse(ledger.Apply("round-1", "ball-A", "bad", 1, -1L, "evt--1-bad",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 2f, 3f), Quaternion.identity,
                new Vector3(0.4f, 0.7f, 1.2f), out index, out created));
            // A non-finite world position cannot be transformed.
            Assert.IsFalse(ledger.ApplyPickup("round-1", "bad", 3L, float.PositiveInfinity,
                out index, out created));
            // A zero ball quaternion and a zero scale component are both invalid.
            Assert.IsFalse(ledger.Apply("round-1", "ball-A", "bad", 1, 4L, "bad-q",
                Vector3.zero, new Quaternion(0f, 0f, 0f, 0f), new Vector3(1f, 2f, 3f),
                Quaternion.identity, Vector3.one, out index, out created));
            Assert.IsFalse(ledger.Apply("round-1", "ball-A", "bad", 1, 4L, "bad-scale",
                Vector3.zero, Quaternion.identity, new Vector3(1f, 2f, 3f), Quaternion.identity,
                new Vector3(1f, 0f, 1f), out index, out created));

            Assert.AreEqual(countBefore, ledger.Count);
            CollectionAssert.AreEqual(orderBefore, ledger.ActiveInstances());
            for (int i = 0; i < ledger.Count; i++)
            {
                Assert.IsFalse(ledger.IsRevoked[i]);
            }
        }

        [Test]
        public void ContextClearAndAtomicSnapshotRebuild()
        {
            Ledger ledger = new Ledger();
            int index;
            bool created;
            Assert.IsTrue(ledger.ApplyPickup("r1", "old", 1L, 0f, out index, out created));
            Assert.IsTrue(ledger.ApplyPickup("r2", "keep", 2L, 0f, out index, out created));

            // The snapshot replaces this context instead of accumulating onto it.
            ledger.ResetSnapshot();
            ledger.AddSnapshotRow("r1", "new", 91L, new Vector3(0f, 2f, 3f), Quaternion.identity, false);
            ledger.AddSnapshotRow("r1", "newer", 8L, new Vector3(0f, 2f, 3f), Quaternion.identity, false);
            Assert.IsTrue(ledger.Rebuild("r1"));
            CollectionAssert.AreEquivalent(new[] { "new", "newer" }, ledger.InstancesFor("r1"));

            string[] before = ledger.ActiveInstances();
            ledger.ResetSnapshot();
            ledger.AddSnapshotRow("r1", "good", 7L, new Vector3(0f, 2f, 3f), Quaternion.identity, false);
            ledger.AddSnapshotRow("r1", "bad", -1L, new Vector3(0f, 2f, 3f), Quaternion.identity, false);
            Assert.IsFalse(ledger.Rebuild("r1"));
            CollectionAssert.AreEqual(before, ledger.ActiveInstances());

            ledger.ClearContext("r1");
            CollectionAssert.AreEqual(new[] { "keep" }, ledger.ActiveInstances());
            CollectionAssert.AreEqual(new[] { "r2" }, ledger.Contexts());
        }

        [Test]
        public void LegacyTransformAndScaleRegression()
        {
            Vector3 localPosition;
            Quaternion localRotation;
            Assert.IsTrue(AttachmentMath.TryWorldPoseToLocal(new Vector3(2f, -3f, 1.5f),
                Quaternion.identity, new Vector3(4f, 2f, 3f), Quaternion.identity,
                new Vector3(0.4f, 0.7f, 1.2f), out localPosition, out localRotation));
            AssertVector(new Vector3(2f, 5f, 1.5f), localPosition);

            Vector3 worldPosition;
            Quaternion worldRotation;
            Assert.IsTrue(AttachmentMath.TryLocalPoseToWorld(new Vector3(-7f, 5f, 9f),
                Quaternion.identity, localPosition, localRotation, new Vector3(0.4f, 0.7f, 1.2f),
                out worldPosition, out worldRotation));
            AssertVector(new Vector3(-5f, 10f, 10.5f), worldPosition);

            // A different object scale must not move the rebuilt pose.
            Assert.IsTrue(AttachmentMath.TryLocalPoseToWorld(new Vector3(-7f, 5f, 9f),
                Quaternion.identity, localPosition, localRotation, new Vector3(2f, 3f, 4f),
                out worldPosition, out worldRotation));
            AssertVector(new Vector3(-5f, 10f, 10.5f), worldPosition);
        }

        [Test]
        public void ConfirmGrowAttachMoveDropOrdering()
        {
            // The ledger half of the cross-layer growth sequence. The item-to-gain map,
            // the volume accumulation, the derived size and the bank are confirmed
            // ledger and SizeModel data, so they are not asserted here; this test keeps
            // the facts this layer owns: out-of-order confirmation, local pose, ball
            // translation carrying the attachment, independent scale and identity-based
            // removal.
            Ledger records = new Ledger();
            int index;
            bool created;
            Assert.IsTrue(records.Apply("match-A", "ball-1", "toy", 1, 13L, "c-13-toy",
                Vector3.zero, Quaternion.identity, new Vector3(-2f, 0f, 1f), Quaternion.identity,
                Vector3.one, out index, out created));
            Assert.IsTrue(records.Apply("match-A", "ball-1", "mug", 1, 71L, "c-71-mug",
                Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f), Quaternion.identity,
                Vector3.one, out index, out created));
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEquivalent(new long[] { 13L, 71L }, records.ActiveSequences());
            CollectionAssert.AreEquivalent(new[] { "mug", "toy" }, records.ActiveInstances());

            int toy = records.IndexOfInstance("toy");
            Assert.GreaterOrEqual(toy, 0);
            AssertVector(new Vector3(-2f, 0f, 1f), records.LocalPositions[toy]);
            AssertVector(Vector3.one, records.ObjectScales[toy]);

            // Moving the ball translates the attachment with the same local pose.
            Vector3 worldPosition;
            Quaternion worldRotation;
            Assert.IsTrue(AttachmentMath.TryLocalPoseToWorld(new Vector3(5f, 2f, -1f),
                Quaternion.identity, records.LocalPositions[toy], records.LocalRotations[toy],
                records.ObjectScales[toy], out worldPosition, out worldRotation));
            AssertVector(new Vector3(3f, 2f, 0f), worldPosition);

            // Dropping by explicit instance identity leaves the other item attached; the
            // gain of a dropped item is never inferred from record order.
            bool existed;
            Assert.IsTrue(records.Revoke("match-A", "ball-1", "mug", 1, 71L, "c-71-mug", out existed));
            Assert.IsTrue(existed);
            CollectionAssert.AreEqual(new[] { "toy" }, records.ActiveInstances());
        }

        [Test]
        public void RevokedReplayInvalidEventSnapshotRollbackAndNewRound()
        {
            Ledger ledger = new Ledger();
            int index;
            bool created;
            Assert.IsTrue(ledger.Apply("m1", "ball-1", "cup", 1, 8L, "c-8-cup",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 0f, 1f), Quaternion.identity,
                Vector3.one, out index, out created));
            bool existed;
            Assert.IsTrue(ledger.Revoke("m1", "ball-1", "cup", 1, 8L, "c-8-cup", out existed));
            Assert.IsTrue(existed);
            Assert.IsFalse(ledger.Apply("m1", "ball-1", "cup", 1, 8L, "c-8-cup",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 0f, 1f), Quaternion.identity,
                Vector3.one, out index, out created));
            string[] before = ledger.ActiveInstances();

            // One good event and one untransformable event: the whole snapshot is
            // rejected and nothing is replaced.
            ledger.ResetSnapshot();
            ledger.AddSnapshotRow("m1", "jar", 9L, new Vector3(3f, 0f, 1f), Quaternion.identity, false);
            ledger.AddSnapshotRow("m1", "bad", 10L, new Vector3(1f, 2f, 3f),
                new Quaternion(0f, 0f, 0f, 0f), false);
            Assert.IsFalse(ledger.Rebuild("m1"));
            CollectionAssert.AreEqual(before, ledger.ActiveInstances());

            // A valid snapshot with an explicit tombstone keeps the replay blocked.
            ledger.ResetSnapshot();
            ledger.AddSnapshotRow("m1", "jar", 9L, new Vector3(3f, 0f, 1f), Quaternion.identity, false);
            ledger.AddSnapshotRow("m1", "cup", 8L, Vector3.zero, Quaternion.identity, true);
            Assert.IsTrue(ledger.Rebuild("m1"));
            Assert.IsFalse(ledger.Apply("m1", "ball-1", "cup", 1, 8L, "c-8-cup",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 0f, 1f), Quaternion.identity,
                Vector3.one, out index, out created));

            // The same identity in another round is a different record.
            Assert.IsTrue(ledger.Apply("m2", "ball-1", "cup", 1, 8L, "c-8-cup",
                Vector3.zero, Quaternion.identity, new Vector3(0f, 0f, 1f), Quaternion.identity,
                Vector3.one, out index, out created));
            CollectionAssert.AreEquivalent(new[] { "m1", "m2" }, ledger.Contexts());
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }

        private static void AssertSameRotation(Quaternion expected, Quaternion actual)
        {
            // q and -q are the same rotation, so compare the absolute dot product.
            float dot = expected.x * actual.x + expected.y * actual.y +
                expected.z * actual.z + expected.w * actual.w;
            Assert.Greater(Mathf.Abs(dot), 1f - Tolerance, "rotation");
        }
    }
}
