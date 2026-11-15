// Port of Docs/Design/BallLayers/Attachment/attachment.py to UdonSharp-safe static
// math. Two deliberate differences from the reference model:
//   1. The identity digests are 32-bit FNV-1a, because Udon has no SHA-256 and no
//      UTF-8 encoder. The field order and the length-prefixed framing follow the
//      model, but the text payload is UTF-16 code units, so a digest here is NOT
//      the Python hexdigest and must never be compared with one produced offline.
//   2. Records and revocation tombstones share one caller-owned row store marked by
//      an isRevoked flag; the model keeps a dict and a set. Behaviour (idempotent
//      replay, conflict rejection, replay of a revoked identity, context isolation,
//      atomic snapshot replacement) is otherwise the model's.
// The only string API used is the indexer, to read one UTF-16 code unit at a time;
// that and the FNV arithmetic still need a real UdonSharp compile to confirm.
using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Confirmed attachment identity, ball-relative local pose and the bounded
    /// record store, as stateless math over caller-owned parallel arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coordinates: world space is the project's single left-handed space, +Y up,
    /// metres. A local pose is the attached object seen from the ball's rigid root:
    /// worldPosition = ballPosition + ballRotation * localPosition and
    /// worldRotation = ballRotation * localRotation. The object's own scale is
    /// carried separately and is never applied to the pose: a growing ball core must
    /// not scale an attached cup. Non-uniform parent scale and shear are outside
    /// this model, so the caller must normalize or reject them before calling.
    /// </para>
    /// <para>
    /// The layer keeps no state. The ledger is ten equal-length columns supplied by
    /// the caller, one row per identity:
    /// contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
    /// localPositions, localRotations, objectScales, isRevoked. A row with
    /// isRevoked set is a tombstone: it keeps only its identity columns and blocks
    /// that identity from being confirmed again. Ten columns at cell granularity is
    /// how a bounded parallel-array store replaces the model's dict plus set.
    /// </para>
    /// <para>
    /// The identity of one confirmation is the six fields
    /// (context, ball, instance, generation, attachSequence, eventId). Its sequence
    /// and event id are upstream facts and are never generated here. A repeat of the
    /// same identity with an identical payload is idempotent and returns the existing
    /// row; an identical identity with any different pose, rotation or scale is a
    /// conflict and is rejected without touching the store. The comparison is exact
    /// component equality, not Unity's approximate Vector3 ==, because the model
    /// treats any real payload change under one identity as a conflict; a rotation is
    /// compared up to its sign, since q and -q are the same rotation and Unity may
    /// produce either representative. Rows are bounded by the column length; a full
    /// store rejects instead of evicting.
    /// </para>
    /// <para>
    /// The two digests are separate on purpose. <see cref="TryEventIdentityHash"/>
    /// covers the transaction identity, including context, ball and event id.
    /// <see cref="TryRemoteProxyKeyHash"/> covers strictly the technical contract's
    /// four remote-key fields: ballSeed, instance id, generation, attach sequence;
    /// context, ball id and event id are not inferred or included. Both are
    /// deterministic summary keys, not authentication and not a collision-free
    /// identity proof.
    /// </para>
    /// <para>
    /// Failure is a false return, never an exception, and every entry point validates
    /// all of its inputs before writing any column, so a rejected confirmation, a
    /// rejected revocation or a rejected snapshot leaves the store exactly as it was.
    /// The call allocates nothing and keeps no static state.
    /// </para>
    /// </remarks>
    public static class AttachmentMath
    {
        // FNV-1a (32-bit) parameters; see the file header for why this is not SHA-256.
        private const long HashOffsetBasis = 2166136261L;
        private const long HashPrime = 16777619L;
        private const long HashMask = 0xFFFFFFFFL;

        /// <summary>
        /// Converts one confirmed world pose into the ball-relative local pose.
        /// </summary>
        /// <remarks>
        /// Both rotations are normalized from any finite non-zero quaternion, as the
        /// reference model does, so a slightly denormalized serialized rotation does
        /// not change the stored pose; a zero or non-finite quaternion is rejected.
        /// objectScale is validated (three finite values, all greater than zero) and
        /// returned to the caller unchanged by construction: it is not applied to the
        /// position or rotation, because the ball's growth must not scale the object.
        /// The local rotation is the normalized product inverse(ballRotation) *
        /// objectRotation, i.e. the object's rotation relative to the ball root.
        /// Any attached geometry that a caller sizes alongside this pose must
        /// therefore be expressed in the object's OWN local axes. The world AABB of
        /// the object already contains its rotation, so passing that size to a
        /// consumer that multiplies by <paramref name="localRotation"/> rotates the
        /// object's orientation twice; use a prop-local size instead.
        /// </remarks>
        public static bool TryWorldPoseToLocal(Vector3 ballPosition, Quaternion ballRotation,
            Vector3 objectPosition, Quaternion objectRotation, Vector3 objectScale,
            out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (!KatamariMath.IsFiniteVector(ballPosition) || !KatamariMath.IsFiniteVector(objectPosition))
                return false;
            if (!IsValidObjectScale(objectScale)) return false;
            Quaternion ballUnit;
            Quaternion objectUnit;
            if (!TryNormalizeRotation(ballRotation, out ballUnit)) return false;
            if (!TryNormalizeRotation(objectRotation, out objectUnit)) return false;
            Vector3 delta = objectPosition - ballPosition;
            if (!KatamariMath.IsFiniteVector(delta)) return false;
            Vector3 position = Quaternion.Inverse(ballUnit) * delta;
            if (!KatamariMath.IsFiniteVector(position)) return false;
            Quaternion relative = Quaternion.Inverse(ballUnit) * objectUnit;
            Quaternion rotation;
            if (!TryNormalizeRotation(relative, out rotation)) return false;
            localPosition = position;
            localRotation = rotation;
            return true;
        }

        /// <summary>
        /// Rebuilds the world pose of an attached object from its stored local pose.
        /// </summary>
        /// <remarks>
        /// Inverse of <see cref="TryWorldPoseToLocal"/> for a rigid ball root. The
        /// local scale is validated exactly like the world-side scale and is
        /// deliberately not applied: an attached object keeps its own scale while the
        /// ball grows around it, so the scale is caller data, not part of the pose.
        /// </remarks>
        public static bool TryLocalPoseToWorld(Vector3 ballPosition, Quaternion ballRotation,
            Vector3 localPosition, Quaternion localRotation, Vector3 localScale,
            out Vector3 worldPosition, out Quaternion worldRotation)
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            if (!KatamariMath.IsFiniteVector(ballPosition) || !KatamariMath.IsFiniteVector(localPosition))
                return false;
            if (!IsValidObjectScale(localScale)) return false;
            Quaternion ballUnit;
            Quaternion localUnit;
            if (!TryNormalizeRotation(ballRotation, out ballUnit)) return false;
            if (!TryNormalizeRotation(localRotation, out localUnit)) return false;
            Vector3 offset = ballUnit * localPosition;
            if (!KatamariMath.IsFiniteVector(offset)) return false;
            Vector3 position = ballPosition + offset;
            if (!KatamariMath.IsFiniteVector(position)) return false;
            Quaternion rotation;
            if (!TryNormalizeRotation(ballUnit * localUnit, out rotation)) return false;
            worldPosition = position;
            worldRotation = rotation;
            return true;
        }

        /// <summary>
        /// Hashes the six-field transaction identity of one confirmation.
        /// </summary>
        /// <remarks>
        /// Field order is context, ball, instance, event, then generation and attach
        /// sequence, matching the model's framing: each text field is length-prefixed
        /// so that ("ab","c") and ("a","bc") cannot collide by naive concatenation,
        /// and the two integers are big-endian at fixed width. This port hashes with
        /// 32-bit FNV-1a instead of SHA-256 and feeds each text field as UTF-16 code
        /// units (low byte, then high byte) with a code-unit count as the prefix,
        /// because Udon exposes neither SHA-256 nor a UTF-8 encoder. The consequence
        /// is explicit: this digest is deterministic and equally field-separated, but
        /// it is not the model's hexdigest and is not interchangeable with one.
        /// Generation and sequence must be non-negative (they are unsigned upstream
        /// fields; the model also bounds them to 32 and 64 bits).
        /// </remarks>
        public static bool TryEventIdentityHash(string contextId, string ballId, string instanceId,
            int generation, long attachSequence, string eventId, out long identityHash)
        {
            identityHash = 0L;
            if (!IsValidIdentity(contextId, ballId, instanceId, generation, attachSequence, eventId))
                return false;
            long hash = HashOffsetBasis;
            hash = HashText(hash, contextId);
            hash = HashText(hash, ballId);
            hash = HashText(hash, instanceId);
            hash = HashText(hash, eventId);
            hash = HashInt32(hash, generation);
            hash = HashInt64(hash, attachSequence);
            identityHash = hash;
            return true;
        }

        /// <summary>
        /// Hashes exactly the technical contract's four remote-proxy key fields.
        /// </summary>
        /// <remarks>
        /// ballSeed, instance id, generation and attach sequence only: no context,
        /// ball id or event id is inferred or folded in, so two confirmations that
        /// differ only by transaction scope still share this key. ballSeed is caller
        /// text whose source and value semantics are still open upstream; this
        /// signature requires canonical non-empty text and must be revisited if a
        /// numeric seed is chosen. Digest caveat and integer ranges are the same as
        /// <see cref="TryEventIdentityHash"/>: FNV-1a over the model's
        /// length-prefixed framing, not the model's SHA-256 value.
        /// </remarks>
        public static bool TryRemoteProxyKeyHash(string ballSeed, string instanceId, int generation,
            long attachSequence, out long remoteKeyHash)
        {
            remoteKeyHash = 0L;
            if (!IsValidText(ballSeed) || !IsValidText(instanceId)) return false;
            if (generation < 0 || attachSequence < 0) return false;
            long hash = HashOffsetBasis;
            hash = HashText(hash, ballSeed);
            hash = HashText(hash, instanceId);
            hash = HashInt32(hash, generation);
            hash = HashInt64(hash, attachSequence);
            remoteKeyHash = hash;
            return true;
        }

        /// <summary>
        /// Records one confirmed attachment into the caller's ledger rows.
        /// </summary>
        /// <remarks>
        /// The store columns must all have the same length (that length is the
        /// capacity) and recordCount must address the live rows. On success
        /// recordIndex is the row holding this identity and created says whether the
        /// call appended it. An identical identity with an identical local pose and
        /// scale is an idempotent replay (true, created false, same row); the same
        /// identity with any different payload is rejected, and so is any identity
        /// whose row is a tombstone, so a revoked confirmation can never be revived by
        /// replay. Rejection leaves every column and recordCount untouched.
        /// </remarks>
        public static bool TryApplyConfirmedEvent(
            string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds,
            Vector3[] localPositions, Quaternion[] localRotations, Vector3[] objectScales,
            bool[] isRevoked, ref int recordCount,
            string contextId, string ballId, string instanceId,
            int generation, long attachSequence, string eventId,
            Vector3 ballPosition, Quaternion ballRotation,
            Vector3 objectPosition, Quaternion objectRotation, Vector3 objectScale,
            out int recordIndex, out bool created)
        {
            recordIndex = -1;
            created = false;
            int capacity;
            if (!TryValidateColumns(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                localPositions, localRotations, objectScales, isRevoked, recordCount, out capacity)) return false;
            if (!IsValidIdentity(contextId, ballId, instanceId, generation, attachSequence, eventId))
                return false;
            Vector3 localPosition;
            Quaternion localRotation;
            if (!TryWorldPoseToLocal(ballPosition, ballRotation, objectPosition, objectRotation, objectScale,
                out localPosition, out localRotation)) return false;

            int existing;
            if (TryFindRow(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                recordCount, contextId, ballId, instanceId, generation, attachSequence, eventId, out existing))
            {
                if (isRevoked[existing]) return false;
                if (!IsSameLocalPose(localPositions[existing], localRotations[existing], objectScales[existing],
                    localPosition, localRotation, objectScale)) return false;
                recordIndex = existing;
                return true;
            }
            if (recordCount >= capacity) return false;

            int index = recordCount;
            contextIds[index] = contextId;
            ballIds[index] = ballId;
            instanceIds[index] = instanceId;
            generations[index] = generation;
            attachSequences[index] = attachSequence;
            eventIds[index] = eventId;
            localPositions[index] = localPosition;
            localRotations[index] = localRotation;
            objectScales[index] = objectScale;
            isRevoked[index] = false;
            recordCount = recordCount + 1;
            recordIndex = index;
            created = true;
            return true;
        }

        /// <summary>
        /// Revokes one confirmation identity and leaves a replay-blocking tombstone.
        /// </summary>
        /// <remarks>
        /// Revoking a live row marks it and returns existed true; revoking an identity
        /// that was never seen still records a tombstone (existed false) so a later
        /// replay of it is rejected, which is the model's behaviour. Revoking a row
        /// that is already a tombstone is accepted with existed false. A tombstone
        /// keeps its identity columns and resets the pose columns, which are then never
        /// read. Rejection (invalid identity, malformed columns, or no room for a new
        /// tombstone) leaves the store untouched.
        /// </remarks>
        public static bool TryRevokeEvent(
            string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds,
            Vector3[] localPositions, Quaternion[] localRotations, Vector3[] objectScales,
            bool[] isRevoked, ref int recordCount,
            string contextId, string ballId, string instanceId,
            int generation, long attachSequence, string eventId,
            out bool existed)
        {
            existed = false;
            int capacity;
            if (!TryValidateColumns(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                localPositions, localRotations, objectScales, isRevoked, recordCount, out capacity)) return false;
            if (!IsValidIdentity(contextId, ballId, instanceId, generation, attachSequence, eventId))
                return false;

            int existing;
            if (TryFindRow(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                recordCount, contextId, ballId, instanceId, generation, attachSequence, eventId, out existing))
            {
                existed = !isRevoked[existing];
                isRevoked[existing] = true;
                localPositions[existing] = Vector3.zero;
                localRotations[existing] = Quaternion.identity;
                objectScales[existing] = Vector3.one;
                return true;
            }
            if (recordCount >= capacity) return false;

            int index = recordCount;
            contextIds[index] = contextId;
            ballIds[index] = ballId;
            instanceIds[index] = instanceId;
            generations[index] = generation;
            attachSequences[index] = attachSequence;
            eventIds[index] = eventId;
            localPositions[index] = Vector3.zero;
            localRotations[index] = Quaternion.identity;
            objectScales[index] = Vector3.one;
            isRevoked[index] = true;
            recordCount = recordCount + 1;
            return true;
        }

        /// <summary>
        /// Drops every row of one context, live and tombstone alike.
        /// </summary>
        /// <remarks>
        /// This is the model's per-round isolation: a new round's ledger does not
        /// inherit the previous round's records or its tombstones. Other contexts keep
        /// their relative row order. The remaining rows are compacted to the front and
        /// the vacated slots are reset to neutral values.
        /// </remarks>
        public static bool TryClearContext(
            string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds,
            Vector3[] localPositions, Quaternion[] localRotations, Vector3[] objectScales,
            bool[] isRevoked, ref int recordCount, string contextId)
        {
            int capacity;
            if (!TryValidateColumns(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                localPositions, localRotations, objectScales, isRevoked, recordCount, out capacity)) return false;
            if (!IsValidText(contextId)) return false;

            int write = 0;
            for (int read = 0; read < recordCount; read++)
            {
                if (string.CompareOrdinal(contextIds[read], contextId) == 0) continue;
                if (write != read)
                {
                    contextIds[write] = contextIds[read];
                    ballIds[write] = ballIds[read];
                    instanceIds[write] = instanceIds[read];
                    generations[write] = generations[read];
                    attachSequences[write] = attachSequences[read];
                    eventIds[write] = eventIds[read];
                    localPositions[write] = localPositions[read];
                    localRotations[write] = localRotations[read];
                    objectScales[write] = objectScales[read];
                    isRevoked[write] = isRevoked[read];
                }
                write++;
            }
            for (int index = write; index < recordCount; index++)
            {
                ResetRow(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                    localPositions, localRotations, objectScales, isRevoked, index);
            }
            recordCount = write;
            return true;
        }

        /// <summary>
        /// Fills an index buffer with the live rows in the model's record order.
        /// </summary>
        /// <remarks>
        /// The model exposes records sorted by identity, so arrival order must not
        /// change what a caller iterates. Order is
        /// context, ball, instance, generation, attach sequence, event id, with text
        /// compared ordinally (UTF-16 code-unit order, which equals code-point order
        /// for the BMP text these keys use). Tombstones are not records and are
        /// skipped. The order buffer must have room for every live row; orderCount is
        /// the number of indices written.
        /// </remarks>
        public static bool TryBuildActiveOrder(
            string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds,
            bool[] isRevoked, int recordCount, int[] order, out int orderCount)
        {
            orderCount = 0;
            int capacity;
            if (!TryValidateIdentityColumns(contextIds, ballIds, instanceIds, generations, attachSequences,
                eventIds, recordCount, out capacity)) return false;
            if (isRevoked == null || isRevoked.Length != capacity) return false;
            if (order == null || order.Length < recordCount) return false;

            for (int row = 0; row < recordCount; row++)
            {
                if (isRevoked[row]) continue;
                int insertAt = orderCount;
                while (insertAt > 0 && CompareRowsAt(contextIds, ballIds, instanceIds, generations,
                    attachSequences, eventIds, order[insertAt - 1], row) > 0)
                {
                    order[insertAt] = order[insertAt - 1];
                    insertAt--;
                }
                order[insertAt] = row;
                orderCount++;
            }
            return true;
        }

        /// <summary>
        /// Atomically replaces one context's rows with a complete confirmed snapshot.
        /// </summary>
        /// <remarks>
        /// The snapshot is a second set of parallel columns: each row is one confirmed
        /// event, with snapshotIsRevoked marking the rows that are tombstones. A
        /// tombstone row carries identity only, so its pose columns are validated
        /// neither for meaning nor for finiteness; pass neutral values. Every row must
        /// belong to contextId, every identity must be complete and in range, and every
        /// live row's world pose must be transformable. The model's "identity cannot be
        /// both active and revoked" is the case where the same identity appears twice
        /// with different revocation flags, which is rejected. A repeated tombstone is
        /// accepted and stored once. A repeated live identity is accepted only when its
        /// full world payload is identical, and is also stored once, so an
        /// out-of-order duplicate cannot turn into a silent overwrite; the reference
        /// model reaches the same place by comparing derived records.
        ///
        /// Nothing is written until the whole snapshot validated and the final row
        /// count (other contexts plus the snapshot's distinct rows) is known to fit, so
        /// a rejected snapshot leaves the store exactly as it was. Rows of other
        /// contexts are preserved in their relative order; this context's live rows and
        /// tombstones are both replaced, because a snapshot is complete for its context.
        /// </remarks>
        public static bool TryRebuildContextSnapshot(
            string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds,
            Vector3[] localPositions, Quaternion[] localRotations, Vector3[] objectScales,
            bool[] isRevoked, ref int recordCount, string contextId,
            string[] snapshotContextIds, string[] snapshotBallIds, string[] snapshotInstanceIds,
            int[] snapshotGenerations, long[] snapshotAttachSequences, string[] snapshotEventIds,
            Vector3[] snapshotBallPositions, Quaternion[] snapshotBallRotations,
            Vector3[] snapshotObjectPositions, Quaternion[] snapshotObjectRotations,
            Vector3[] snapshotObjectScales, bool[] snapshotIsRevoked, int snapshotCount)
        {
            int capacity;
            if (!TryValidateColumns(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                localPositions, localRotations, objectScales, isRevoked, recordCount, out capacity)) return false;
            if (!IsValidText(contextId)) return false;
            if (snapshotContextIds == null || snapshotBallIds == null || snapshotInstanceIds == null ||
                snapshotGenerations == null || snapshotAttachSequences == null || snapshotEventIds == null ||
                snapshotBallPositions == null || snapshotBallRotations == null ||
                snapshotObjectPositions == null || snapshotObjectRotations == null ||
                snapshotObjectScales == null || snapshotIsRevoked == null) return false;
            if (snapshotCount < 0) return false;
            if (snapshotCount > snapshotContextIds.Length || snapshotCount > snapshotBallIds.Length ||
                snapshotCount > snapshotInstanceIds.Length || snapshotCount > snapshotGenerations.Length ||
                snapshotCount > snapshotAttachSequences.Length || snapshotCount > snapshotEventIds.Length ||
                snapshotCount > snapshotBallPositions.Length || snapshotCount > snapshotBallRotations.Length ||
                snapshotCount > snapshotObjectPositions.Length || snapshotCount > snapshotObjectRotations.Length ||
                snapshotCount > snapshotObjectScales.Length || snapshotCount > snapshotIsRevoked.Length)
                return false;

            for (int row = 0; row < snapshotCount; row++)
            {
                if (!IsValidIdentity(snapshotContextIds[row], snapshotBallIds[row], snapshotInstanceIds[row],
                    snapshotGenerations[row], snapshotAttachSequences[row], snapshotEventIds[row])) return false;
                if (string.CompareOrdinal(snapshotContextIds[row], contextId) != 0) return false;
                if (snapshotIsRevoked[row]) continue;
                Vector3 probePosition;
                Quaternion probeRotation;
                if (!TryWorldPoseToLocal(snapshotBallPositions[row], snapshotBallRotations[row],
                    snapshotObjectPositions[row], snapshotObjectRotations[row], snapshotObjectScales[row],
                    out probePosition, out probeRotation)) return false;
            }

            int duplicateCount = 0;
            for (int row = 0; row < snapshotCount; row++)
            {
                for (int earlier = 0; earlier < row; earlier++)
                {
                    if (!IsSameSnapshotIdentity(snapshotContextIds, snapshotBallIds, snapshotInstanceIds,
                        snapshotGenerations, snapshotAttachSequences, snapshotEventIds, row, earlier)) continue;
                    if (snapshotIsRevoked[row] != snapshotIsRevoked[earlier]) return false;
                    if (!snapshotIsRevoked[row] &&
                        !IsSameSnapshotPayload(snapshotBallPositions, snapshotBallRotations,
                            snapshotObjectPositions, snapshotObjectRotations, snapshotObjectScales, row, earlier))
                        return false;
                    duplicateCount++;
                    break;
                }
            }

            int keptRows = 0;
            for (int row = 0; row < recordCount; row++)
            {
                if (string.CompareOrdinal(contextIds[row], contextId) != 0) keptRows++;
            }
            int finalCount = keptRows + snapshotCount - duplicateCount;
            if (finalCount > capacity) return false;

            // Everything validated: compact the other contexts, then append the snapshot.
            int write = 0;
            for (int read = 0; read < recordCount; read++)
            {
                if (string.CompareOrdinal(contextIds[read], contextId) == 0) continue;
                if (write != read)
                {
                    contextIds[write] = contextIds[read];
                    ballIds[write] = ballIds[read];
                    instanceIds[write] = instanceIds[read];
                    generations[write] = generations[read];
                    attachSequences[write] = attachSequences[read];
                    eventIds[write] = eventIds[read];
                    localPositions[write] = localPositions[read];
                    localRotations[write] = localRotations[read];
                    objectScales[write] = objectScales[read];
                    isRevoked[write] = isRevoked[read];
                }
                write++;
            }
            int live = write;
            for (int row = 0; row < snapshotCount; row++)
            {
                bool repeated = false;
                for (int earlier = 0; earlier < row; earlier++)
                {
                    if (!IsSameSnapshotIdentity(snapshotContextIds, snapshotBallIds, snapshotInstanceIds,
                        snapshotGenerations, snapshotAttachSequences, snapshotEventIds, row, earlier)) continue;
                    repeated = true;
                    break;
                }
                if (repeated) continue;
                contextIds[live] = snapshotContextIds[row];
                ballIds[live] = snapshotBallIds[row];
                instanceIds[live] = snapshotInstanceIds[row];
                generations[live] = snapshotGenerations[row];
                attachSequences[live] = snapshotAttachSequences[row];
                eventIds[live] = snapshotEventIds[row];
                isRevoked[live] = snapshotIsRevoked[row];
                if (snapshotIsRevoked[row])
                {
                    localPositions[live] = Vector3.zero;
                    localRotations[live] = Quaternion.identity;
                    objectScales[live] = Vector3.one;
                }
                else
                {
                    Vector3 localPosition;
                    Quaternion localRotation;
                    // Validated above, so this exact call cannot fail.
                    TryWorldPoseToLocal(snapshotBallPositions[row], snapshotBallRotations[row],
                        snapshotObjectPositions[row], snapshotObjectRotations[row], snapshotObjectScales[row],
                        out localPosition, out localRotation);
                    localPositions[live] = localPosition;
                    localRotations[live] = localRotation;
                    objectScales[live] = snapshotObjectScales[row];
                }
                live++;
            }
            for (int index = live; index < recordCount; index++)
            {
                ResetRow(contextIds, ballIds, instanceIds, generations, attachSequences, eventIds,
                    localPositions, localRotations, objectScales, isRevoked, index);
            }
            recordCount = live;
            return true;
        }

        private static bool TryNormalizeRotation(Quaternion rotation, out Quaternion unit)
        {
            unit = Quaternion.identity;
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            // Divide the largest component out first so neither a huge nor a tiny
            // rotation underflows the sum of squares.
            float scale = Mathf.Max(Mathf.Abs(rotation.x), Mathf.Max(Mathf.Abs(rotation.y),
                Mathf.Max(Mathf.Abs(rotation.z), Mathf.Abs(rotation.w))));
            if (!KatamariMath.IsFiniteFloat(scale) || scale <= 0f) return false;
            float x = rotation.x / scale;
            float y = rotation.y / scale;
            float z = rotation.z / scale;
            float w = rotation.w / scale;
            float magnitude = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!KatamariMath.IsFiniteFloat(magnitude) || magnitude <= 0f) return false;
            Quaternion candidate = new Quaternion(x / magnitude, y / magnitude, z / magnitude, w / magnitude);
            if (!KatamariMath.IsFiniteQuaternion(candidate)) return false;
            unit = candidate;
            return true;
        }

        private static bool IsValidObjectScale(Vector3 scale)
        {
            if (!KatamariMath.IsFiniteVector(scale)) return false;
            return scale.x > 0f && scale.y > 0f && scale.z > 0f;
        }

        private static bool IsValidText(string text)
        {
            return text != null && text.Length > 0;
        }

        private static bool IsValidIdentity(string contextId, string ballId, string instanceId,
            int generation, long attachSequence, string eventId)
        {
            if (!IsValidText(contextId) || !IsValidText(ballId)) return false;
            if (!IsValidText(instanceId) || !IsValidText(eventId)) return false;
            return generation >= 0 && attachSequence >= 0;
        }

        private static bool IsSameLocalPose(Vector3 localPosition, Quaternion localRotation, Vector3 objectScale,
            Vector3 otherPosition, Quaternion otherRotation, Vector3 otherScale)
        {
            return IsSameVector(localPosition, otherPosition) &&
                IsSameRotation(localRotation, otherRotation) &&
                IsSameVector(objectScale, otherScale);
        }

        private static bool IsSameVector(Vector3 a, Vector3 b)
        {
            // Component equality, not Unity's approximate Vector3 ==.
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }

        private static bool IsSameRotation(Quaternion a, Quaternion b)
        {
            // q and -q are the same rotation; the model compares tuples, so compare
            // the sign-normalized components to keep a re-serialized rotation from
            // reading as a conflict.
            float sign = (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) < 0f ? -1f : 1f;
            return a.x == b.x * sign && a.y == b.y * sign && a.z == b.z * sign && a.w == b.w * sign;
        }

        private static bool TryValidateColumns(string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds, Vector3[] localPositions,
            Quaternion[] localRotations, Vector3[] objectScales, bool[] isRevoked, int recordCount,
            out int capacity)
        {
            capacity = 0;
            if (!TryValidateIdentityColumns(contextIds, ballIds, instanceIds, generations, attachSequences,
                eventIds, recordCount, out capacity)) return false;
            if (localPositions == null || localRotations == null || objectScales == null || isRevoked == null)
                return false;
            if (localPositions.Length != capacity || localRotations.Length != capacity ||
                objectScales.Length != capacity || isRevoked.Length != capacity) return false;
            return true;
        }

        private static bool TryValidateIdentityColumns(string[] contextIds, string[] ballIds,
            string[] instanceIds, int[] generations, long[] attachSequences, string[] eventIds,
            int recordCount, out int capacity)
        {
            capacity = 0;
            if (contextIds == null || ballIds == null || instanceIds == null) return false;
            if (generations == null || attachSequences == null || eventIds == null) return false;
            capacity = contextIds.Length;
            if (ballIds.Length != capacity || instanceIds.Length != capacity ||
                generations.Length != capacity || attachSequences.Length != capacity ||
                eventIds.Length != capacity) return false;
            return recordCount >= 0 && recordCount <= capacity;
        }

        private static bool TryFindRow(string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds, int recordCount,
            string contextId, string ballId, string instanceId, int generation, long attachSequence,
            string eventId, out int recordIndex)
        {
            recordIndex = -1;
            for (int row = 0; row < recordCount; row++)
            {
                // Integer and sequence fields first: they reject most rows cheaply.
                if (generations[row] != generation) continue;
                if (attachSequences[row] != attachSequence) continue;
                if (string.CompareOrdinal(eventIds[row], eventId) != 0) continue;
                if (string.CompareOrdinal(contextIds[row], contextId) != 0) continue;
                if (string.CompareOrdinal(ballIds[row], ballId) != 0) continue;
                if (string.CompareOrdinal(instanceIds[row], instanceId) != 0) continue;
                recordIndex = row;
                return true;
            }
            return false;
        }

        private static bool IsSameSnapshotIdentity(string[] snapshotContextIds, string[] snapshotBallIds,
            string[] snapshotInstanceIds, int[] snapshotGenerations, long[] snapshotAttachSequences,
            string[] snapshotEventIds, int row, int earlier)
        {
            if (snapshotGenerations[row] != snapshotGenerations[earlier]) return false;
            if (snapshotAttachSequences[row] != snapshotAttachSequences[earlier]) return false;
            if (string.CompareOrdinal(snapshotEventIds[row], snapshotEventIds[earlier]) != 0) return false;
            if (string.CompareOrdinal(snapshotContextIds[row], snapshotContextIds[earlier]) != 0) return false;
            if (string.CompareOrdinal(snapshotBallIds[row], snapshotBallIds[earlier]) != 0) return false;
            return string.CompareOrdinal(snapshotInstanceIds[row], snapshotInstanceIds[earlier]) == 0;
        }

        private static bool IsSameSnapshotPayload(Vector3[] snapshotBallPositions,
            Quaternion[] snapshotBallRotations, Vector3[] snapshotObjectPositions,
            Quaternion[] snapshotObjectRotations, Vector3[] snapshotObjectScales, int row, int earlier)
        {
            return IsSameVector(snapshotBallPositions[row], snapshotBallPositions[earlier]) &&
                IsSameRotation(snapshotBallRotations[row], snapshotBallRotations[earlier]) &&
                IsSameVector(snapshotObjectPositions[row], snapshotObjectPositions[earlier]) &&
                IsSameRotation(snapshotObjectRotations[row], snapshotObjectRotations[earlier]) &&
                IsSameVector(snapshotObjectScales[row], snapshotObjectScales[earlier]);
        }

        private static int CompareRowsAt(string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds, int a, int b)
        {
            int comparison = string.CompareOrdinal(contextIds[a], contextIds[b]);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(ballIds[a], ballIds[b]);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(instanceIds[a], instanceIds[b]);
            if (comparison != 0) return comparison;
            if (generations[a] != generations[b]) return generations[a] < generations[b] ? -1 : 1;
            if (attachSequences[a] != attachSequences[b]) return attachSequences[a] < attachSequences[b] ? -1 : 1;
            return string.CompareOrdinal(eventIds[a], eventIds[b]);
        }

        private static void ResetRow(string[] contextIds, string[] ballIds, string[] instanceIds,
            int[] generations, long[] attachSequences, string[] eventIds, Vector3[] localPositions,
            Quaternion[] localRotations, Vector3[] objectScales, bool[] isRevoked, int index)
        {
            contextIds[index] = null;
            ballIds[index] = null;
            instanceIds[index] = null;
            generations[index] = 0;
            attachSequences[index] = 0L;
            eventIds[index] = null;
            localPositions[index] = Vector3.zero;
            localRotations[index] = Quaternion.identity;
            objectScales[index] = Vector3.one;
            isRevoked[index] = false;
        }

        private static long HashText(long hash, string text)
        {
            // Udon has no UTF-8 encoder: the prefix counts UTF-16 code units and each
            // code unit contributes its low byte then its high byte.
            long result = HashInt32(hash, text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                int codeUnit = text[i];
                result = HashByte(result, codeUnit & 0xFF);
                result = HashByte(result, (codeUnit >> 8) & 0xFF);
            }
            return result;
        }

        private static long HashInt32(long hash, int value)
        {
            long result = hash;
            result = HashByte(result, (value >> 24) & 0xFF);
            result = HashByte(result, (value >> 16) & 0xFF);
            result = HashByte(result, (value >> 8) & 0xFF);
            result = HashByte(result, value & 0xFF);
            return result;
        }

        private static long HashInt64(long hash, long value)
        {
            long result = hash;
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                result = HashByte(result, (int)((value >> shift) & 0xFF));
            }
            return result;
        }

        private static long HashByte(long hash, int value)
        {
            // 32-bit FNV-1a step evaluated in a long, so no narrowing conversion or
            // int overflow trap can fire in the Udon VM.
            return ((hash ^ (value & 0xFF)) * HashPrime) & HashMask;
        }
    }
}
