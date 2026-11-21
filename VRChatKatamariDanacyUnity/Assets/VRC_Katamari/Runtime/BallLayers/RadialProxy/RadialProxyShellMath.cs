using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Connects absorbed props' simplified AABB proxies to a caller-owned radial
    /// shell (the ball's 16 pins): builds the box corners in the ball frame and
    /// composes the incremental attach/detach, the whole-ledger rebuild and the
    /// per-step pin tips that <see cref="RadialProxyMath"/> deliberately leaves to its
    /// caller. Every attached prop is an item; there is no long-object admission test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class adds no new support or transform math. Directions, endpoint
    /// transforms, affected-direction collection, support solving, candidate keys
    /// and the atomic rebuild are calls into <see cref="RadialProxyMath"/>; what
    /// lives here is the integration protocol that has to exist exactly once:
    /// which corner of an absorbed prop is an endpoint, which array slot an
    /// item occupies, and how a removal
    /// compacts the caller's ledger before the affected directions are recomputed.
    /// The state itself stays caller-owned: the motor owns one set of these
    /// arrays, and the EditMode tests own another.
    /// </para>
    /// <para>
    /// Coordinates. Bounds are full edge lengths in metres measured along the PROP'S
    /// OWN LOCAL AXES, never a world AABB. The prop's ball-local pose comes from
    /// <c>AttachmentMath.TryWorldPoseToLocal</c>, whose rotation is
    /// inverse(ball) * propWorldRotation and therefore already contains the prop's
    /// own rotation, so a corner is
    /// <c>localCenter + localRotation * (+-extent/2)</c> in the ball frame
    /// established by <c>BallVisualRollRoot</c>. A world AABB fed into that formula
    /// applies the prop's rotation twice (a yaw-90 lounge chair would get its long
    /// axis put on the wrong world axis). That is why <c>PickupFieldHost</c> passes
    /// <c>propLocalBounds</c> here: a separate authoring column from the
    /// <c>propBounds</c> world AABB that the absorb gate, the blocking boxes and the
    /// support classification consume. Endpoints are static in the ball frame and
    /// rotate with the ball's visual roll.
    /// </para>
    /// <para>
    /// Stored supports are endpoint-only maxima: the core-radius floor is NOT
    /// baked into the table. the contact pass applies the
    /// floor of the current core radius every step and
    /// <see cref="RadialProxyMath.TryProtrusion"/> computes the relative
    /// protrusion, so a growing core needs no support-table write. A shrinking
    /// core (a bank) still takes a whole-ledger rebuild, because the contract asks
    /// for one and it is the only place a stale table could survive.
    /// </para>
    /// <para>
    /// Failure is a false return with the caller's arrays unchanged wherever this
    /// class can guarantee it. An attach validates capacities, the key and every
    /// endpoint before it appends, and writes the support table only after the
    /// scratch solve succeeded. A detach pre-validates the surviving pool before
    /// it compacts, so the later solve cannot fail on ordinary data. There are no
    /// static fields, no allocation and no Transform reads here.
    /// </para>
    /// </remarks>
    public static class RadialProxyShellMath
    {
        /// <summary>Corners of one prop's bounding box: the endpoint set of one item.</summary>
        public const int AabbCornerCount = 8;

        // Why TryResolvePropExtents fell back from the prop-local column to the
        // legacy world-AABB column. 0 means the prop-local value was used; the host
        // prints the code once instead of arguing about which array is stale.
        public const int ExtentsSourceLocal = 0;
        public const int ExtentsSourceWorldMissing = 1;
        public const int ExtentsSourceWorldLengthMismatch = 2;
        public const int ExtentsSourceWorldRowInvalid = 3;

        /// <summary>
        /// Resolves the full extents one prop row contributes to the shell, preferring
        /// the optional prop-local column and falling back to the legacy world-AABB
        /// column when it is unusable.
        /// </summary>
        /// <remarks>
        /// The shell needs sizes along the prop's local axes (see the class remarks);
        /// the absorb gate, blocking boxes and support classification keep using the
        /// world AABB. A scene written before the local column existed has a null or
        /// short array, so this returns the world value with a non-zero
        /// <paramref name="source"/> rather than refusing the prop: the old behaviour
        /// is wrong only for a rotated prop, while dropping the row would lose the
        /// prop. A per-row non-finite or negative local value also falls back, with
        /// <see cref="ExtentsSourceWorldRowInvalid"/>. The world value is not
        /// validated here - the motor's attach validates its input and reports the
        /// ordinary invalid-input skip.
        /// </remarks>
        public static bool TryResolvePropExtents(Vector3[] localExtents, Vector3[] worldExtents,
            int index, int expectedRows, out Vector3 fullExtents, out int source)
        {
            fullExtents = Vector3.zero;
            source = ExtentsSourceLocal;
            if (worldExtents == null || expectedRows < 0) return false;
            if (index < 0 || index >= expectedRows || index >= worldExtents.Length) return false;

            Vector3 world = worldExtents[index];
            if (localExtents == null)
            {
                fullExtents = world;
                source = ExtentsSourceWorldMissing;
                return true;
            }
            if (localExtents.Length != expectedRows)
            {
                fullExtents = world;
                source = ExtentsSourceWorldLengthMismatch;
                return true;
            }

            Vector3 local = localExtents[index];
            if (!KatamariMath.IsFiniteVector(local) || local.x < 0f || local.y < 0f ||
                local.z < 0f)
            {
                fullExtents = world;
                source = ExtentsSourceWorldRowInvalid;
                return true;
            }

            fullExtents = local;
            return true;
        }

        /// <summary>
        /// Builds the eight ball-local corners of one absorbed prop's AABB and
        /// reports the farthest corner's distance from the ball centre.
        /// </summary>
        /// <remarks>
        /// <paramref name="fullExtents"/> is the full edge length of each prop-local
        /// axis, measured along the prop's own axes and NOT a world AABB; the
        /// corners are at +-half on every axis, rotated by the prop's ball-local
        /// rotation and offset by its ball-local centre. The rotation
        /// (<c>inverse(ball) * propWorldRotation</c>, from
        /// <c>AttachmentMath.TryWorldPoseToLocal</c>) already contains the prop's own
        /// orientation, so a world-AABB size would be rotated twice. The rotation
        /// must be unit length (the same rule as every transform call in this
        /// layer); a scaled or zero quaternion is rejected instead of normalized.
        /// The farthest distance is the raw support at this attach pose before the
        /// core floor and before the protrusion cap, and is what the caller
        /// compares against the core to decide whether the item can contribute at
        /// all. On false the corners must not be committed.
        /// </remarks>
        public static bool TryBuildAabbCorners(Vector3 localCenter, Quaternion localRotation,
            Vector3 fullExtents, Vector3[] corners, out float farthestDistanceM)
        {
            farthestDistanceM = 0f;
            if (corners == null || corners.Length < AabbCornerCount) return false;
            if (!KatamariMath.IsFiniteVector(localCenter)) return false;
            if (!RadialProxyMath.IsUnitRotation(localRotation)) return false;
            if (!KatamariMath.IsFiniteVector(fullExtents)) return false;
            if (fullExtents.x < 0f || fullExtents.y < 0f || fullExtents.z < 0f) return false;

            float halfX = fullExtents.x * 0.5f;
            float halfY = fullExtents.y * 0.5f;
            float halfZ = fullExtents.z * 0.5f;
            float farthestSquared = 0f;
            for (int i = 0; i < AabbCornerCount; i++)
            {
                // Bit 0/1/2 is the sign of x/y/z, so the eight corners are the
                // whole AABB and a rod's tip is a corner rather than a special case.
                float signX = (i & 1) == 0 ? -1f : 1f;
                float signY = (i & 2) == 0 ? -1f : 1f;
                float signZ = (i & 4) == 0 ? -1f : 1f;
                Vector3 offset = new Vector3(signX * halfX, signY * halfY, signZ * halfZ);
                Vector3 corner = localCenter + localRotation * offset;
                if (!KatamariMath.IsFiniteVector(corner)) return false;
                corners[i] = corner;
                float distanceSquared = corner.x * corner.x + corner.y * corner.y +
                    corner.z * corner.z;
                if (!KatamariMath.IsFiniteFloat(distanceSquared)) return false;
                if (distanceSquared > farthestSquared) farthestSquared = distanceSquared;
            }
            float farthest = Mathf.Sqrt(farthestSquared);
            if (!KatamariMath.IsFiniteFloat(farthest)) return false;
            farthestDistanceM = farthest;
            return true;
        }

        /// <summary>
        /// Appends one item's endpoints, raises only the supports its endpoints can
        /// affect (an incremental max-merge, not a rescan of the pool), and registers its
        /// candidate key when asked.
        /// </summary>
        /// <remarks>
        /// The pool, the per-item slices, the item keys and the candidate slots are
        /// the caller's ledger; this method is the only writer of their append
        /// protocol. The endpoint array is the item's own corners (not an offset
        /// into the pool), which is why the same array serves attach and detach.
        /// Capacities, key uniqueness and endpoint finiteness are checked before
        /// the pool is touched; the item's own supports are solved into
        /// <paramref name="solveScratch"/> first and merged (max) into the affected slots
        /// only after that solve and the candidate insert succeeded, so a refused
        /// attach leaves the pool and the support table as they were.
        /// </remarks>
        public static bool TryAttachItem(
            Vector3[] directions,
            float[] supports,
            Vector3[] pool,
            ref int poolCount,
            int[] itemStarts,
            int[] itemLengths,
            string[] itemKeys,
            bool[] itemIsCandidate,
            ref int itemCount,
            string[] candidateKeys,
            ref int candidateCount,
            int[] affectedIndices,
            float[] solveScratch,
            string itemKey,
            Vector3[] endpoints,
            int endpointCount,
            bool isCandidate)
        {
            if (directions == null || directions.Length != RadialProxyMath.DirectionCount) return false;
            if (supports == null || supports.Length < RadialProxyMath.DirectionCount) return false;
            if (pool == null || itemStarts == null || itemLengths == null || itemKeys == null ||
                itemIsCandidate == null) return false;
            if (candidateKeys == null || candidateKeys.Length > RadialProxyMath.MaxExtremeCandidates)
                return false;
            if (affectedIndices == null ||
                affectedIndices.Length < RadialProxyMath.DirectionCount) return false;
            if (solveScratch == null || solveScratch.Length < RadialProxyMath.DirectionCount) return false;
            if (itemKey == null || itemKey.Length == 0) return false;
            if (endpoints == null || endpointCount < 1 || endpointCount > AabbCornerCount) return false;
            if (endpointCount > endpoints.Length) return false;
            if (poolCount < 0 || poolCount > pool.Length) return false;
            if (poolCount > pool.Length - endpointCount) return false;
            if (itemCount < 0 || itemCount > itemKeys.Length || itemCount > itemStarts.Length ||
                itemCount > itemLengths.Length || itemCount > itemIsCandidate.Length) return false;
            if (itemCount >= itemKeys.Length) return false;
            if (itemCount >= itemStarts.Length || itemCount >= itemLengths.Length ||
                itemCount >= itemIsCandidate.Length) return false;
            if (isCandidate)
            {
                if (candidateCount < 0 || candidateCount > candidateKeys.Length) return false;
                if (candidateCount >= candidateKeys.Length) return false;
            }

            // The item key must be new: a repeat attach would double-count its
            // endpoints, and the shell has no per-endpoint identity to undo that.
            for (int i = 0; i < itemCount; i++)
            {
                if (string.CompareOrdinal(itemKeys[i], itemKey) == 0) return false;
            }
            if (isCandidate)
            {
                for (int i = 0; i < candidateCount; i++)
                {
                    if (string.CompareOrdinal(candidateKeys[i], itemKey) == 0) return false;
                }
            }
            for (int i = 0; i < endpointCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(endpoints[i])) return false;
            }

            // Incremental: the table is a MAX over the endpoint pool, so adding endpoints can
            // only raise a support, and only in the directions whose hemisphere they enter.
            // The new item's own 8 corners are all that has to be dotted (<= 16 x 8 products),
            // never the whole pool: with every attached prop registered the pool is hundreds
            // of endpoints, and a rescan per attach would be a hitch in Udon. Result is
            // identical to the rescan whenever the table matches the pool, which every
            // writer in this class keeps true.
            int affectedCount;
            if (!RadialProxyMath.TryCollectAffectedDirections(endpoints, endpointCount, directions,
                affectedIndices, out affectedCount))
            {
                return false;
            }
            if (!RadialProxyMath.TrySolveSupports(0f, endpoints, endpointCount, directions,
                affectedIndices, affectedCount, solveScratch))
            {
                return false;
            }
            if (isCandidate)
            {
                if (!RadialProxyMath.TryInsertCandidateKey(candidateKeys, ref candidateCount, itemKey))
                {
                    return false;
                }
            }

            int start = poolCount;
            for (int i = 0; i < endpointCount; i++)
            {
                pool[start + i] = endpoints[i];
            }
            poolCount = poolCount + endpointCount;
            for (int k = 0; k < affectedCount; k++)
            {
                int directionIndex = affectedIndices[k];
                if (solveScratch[directionIndex] > supports[directionIndex])
                {
                    supports[directionIndex] = solveScratch[directionIndex];
                }
            }

            itemStarts[itemCount] = start;
            itemLengths[itemCount] = endpointCount;
            itemKeys[itemCount] = itemKey;
            itemIsCandidate[itemCount] = isCandidate;
            itemCount = itemCount + 1;
            return true;
        }

        /// <summary>
        /// Removes one item's endpoints, compacts the pool and the item rows,
        /// recomputes the directions its endpoints used to affect, and releases its
        /// candidate key.
        /// </summary>
        /// <remarks>
        /// This is the contract's "do not only keep a running max" path: the
        /// affected directions are solved again from the SURVIVING endpoints, so a
        /// second rod sharing a direction takes over when the longest one is
        /// removed. Removing a key that is not registered is not an error
        /// (<paramref name="existed"/> is false); the ledger is unchanged. The
        /// surviving pool is validated before compaction so the post-compaction
        /// solve cannot fail on ordinary data. The removed corners are copied into
        /// <paramref name="removedScratch"/> because the affected-direction scan
        /// takes a contiguous array and the pool slice is about to move.
        /// </remarks>
        public static bool TryDetachItem(
            Vector3[] directions,
            float[] supports,
            Vector3[] pool,
            ref int poolCount,
            int[] itemStarts,
            int[] itemLengths,
            string[] itemKeys,
            bool[] itemIsCandidate,
            ref int itemCount,
            string[] candidateKeys,
            ref int candidateCount,
            int[] affectedIndices,
            float[] solveScratch,
            Vector3[] removedScratch,
            string itemKey,
            out bool existed)
        {
            existed = false;
            if (directions == null || directions.Length != RadialProxyMath.DirectionCount) return false;
            if (supports == null || supports.Length < RadialProxyMath.DirectionCount) return false;
            if (pool == null || itemStarts == null || itemLengths == null || itemKeys == null ||
                itemIsCandidate == null) return false;
            if (candidateKeys == null || candidateKeys.Length > RadialProxyMath.MaxExtremeCandidates)
                return false;
            if (affectedIndices == null ||
                affectedIndices.Length < RadialProxyMath.DirectionCount) return false;
            if (solveScratch == null || solveScratch.Length < RadialProxyMath.DirectionCount) return false;
            if (removedScratch == null || removedScratch.Length < AabbCornerCount) return false;
            if (itemKey == null || itemKey.Length == 0) return false;
            if (poolCount < 0 || poolCount > pool.Length) return false;
            if (itemCount < 0 || itemCount > itemKeys.Length || itemCount > itemStarts.Length ||
                itemCount > itemLengths.Length || itemCount > itemIsCandidate.Length) return false;
            if (candidateCount < 0 || candidateCount > candidateKeys.Length) return false;

            int item = -1;
            for (int i = 0; i < itemCount; i++)
            {
                if (string.CompareOrdinal(itemKeys[i], itemKey) == 0)
                {
                    item = i;
                    break;
                }
            }
            if (item < 0) return true;

            int start = itemStarts[item];
            int length = itemLengths[item];
            if (start < 0 || length < 1 || length > AabbCornerCount) return false;
            if (start > poolCount - length) return false;
            if (length > removedScratch.Length) return false;
            for (int i = 0; i < length; i++)
            {
                removedScratch[i] = pool[start + i];
            }

            // Validate the surviving pool before moving it. The post-compaction solve
            // reads exactly these slots, so a finite pool proves it cannot fail on a
            // non-finite endpoint.
            for (int i = 0; i < poolCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(pool[i])) return false;
            }

            for (int i = start; i + length < poolCount; i++)
            {
                pool[i] = pool[i + length];
            }
            poolCount = poolCount - length;
            for (int i = 0; i < itemCount; i++)
            {
                if (itemStarts[i] > start) itemStarts[i] = itemStarts[i] - length;
            }
            for (int i = item; i < itemCount - 1; i++)
            {
                itemStarts[i] = itemStarts[i + 1];
                itemLengths[i] = itemLengths[i + 1];
                itemKeys[i] = itemKeys[i + 1];
                itemIsCandidate[i] = itemIsCandidate[i + 1];
            }
            itemCount = itemCount - 1;
            itemKeys[itemCount] = null;

            int affectedCount;
            if (!RadialProxyMath.TryCollectAffectedDirections(removedScratch, length, directions,
                affectedIndices, out affectedCount))
            {
                return false;
            }
            if (!RadialProxyMath.TrySolveSupports(0f, pool, poolCount, directions, affectedIndices,
                affectedCount, solveScratch))
            {
                return false;
            }
            for (int k = 0; k < affectedCount; k++)
            {
                int directionIndex = affectedIndices[k];
                supports[directionIndex] = solveScratch[directionIndex];
            }

            bool candidateExisted;
            if (!RadialProxyMath.TryRemoveCandidateKey(candidateKeys, ref candidateCount, itemKey,
                out candidateExisted))
            {
                return false;
            }
            existed = true;
            return true;
        }

        /// <summary>
        /// Rebuilds the whole shell from the caller's current item ledger.
        /// </summary>
        /// <remarks>
        /// The atomic whole-ledger rebuild: <see cref="RadialProxyMath.TryRebuild"/>
        /// validates every item, key and endpoint before writing either the support
        /// table or the candidate list. Supports are solved with a zero core floor
        /// (the table stores endpoint maxima; the reader adds the current core),
        /// so the result does not depend on the core radius and a bank only needs
        /// this call to re-establish the invariant after a discontinuity.
        /// </remarks>
        public static bool TryRebuildShell(
            Vector3[] directions,
            float[] supports,
            Vector3[] pool,
            int poolCount,
            int[] itemStarts,
            int[] itemLengths,
            string[] itemKeys,
            bool[] itemIsCandidate,
            int itemCount,
            string[] candidateKeys,
            out int candidateCount)
        {
            candidateCount = 0;
            if (directions == null || directions.Length != RadialProxyMath.DirectionCount) return false;
            if (supports == null || supports.Length < RadialProxyMath.DirectionCount) return false;
            if (pool == null || itemStarts == null || itemLengths == null || itemKeys == null ||
                itemIsCandidate == null || candidateKeys == null) return false;
            if (candidateKeys.Length > RadialProxyMath.MaxExtremeCandidates) return false;
            if (poolCount < 0 || poolCount > pool.Length) return false;
            if (itemCount < 0 || itemCount > itemStarts.Length || itemCount > itemLengths.Length ||
                itemCount > itemKeys.Length || itemCount > itemIsCandidate.Length) return false;
            // TryRebuild only checks slices against the pool, so refuse a ledger whose
            // slice points past the used part of the pool instead of solving stale slots.
            for (int i = 0; i < itemCount; i++)
            {
                int start = itemStarts[i];
                int length = itemLengths[i];
                if (start < 0 || length < 1) return false;
                if (start > poolCount - length) return false;
            }
            return RadialProxyMath.TryRebuild(0f, pool, itemStarts, itemLengths, itemKeys,
                itemIsCandidate, itemCount, directions, supports, candidateKeys,
                out candidateCount);
        }

        /// <summary>
        /// Empties the shell: every support goes to zero, and the pool, the item
        /// rows and the candidate list are cleared in one call.
        /// </summary>
        /// <remarks>
        /// This is the reset path (a retry, or a field reset that returned every
        /// prop). It is deliberately NOT a special case inside a detach loop: the
        /// ledger is being discarded, not edited.
        /// </remarks>
        public static bool TryClearShell(Vector3[] directions, float[] supports,
            ref int poolCount, ref int itemCount, string[] candidateKeys, ref int candidateCount)
        {
            if (directions == null || directions.Length != RadialProxyMath.DirectionCount) return false;
            if (supports == null || supports.Length < RadialProxyMath.DirectionCount) return false;
            if (candidateKeys == null) return false;
            if (candidateKeys.Length > RadialProxyMath.MaxExtremeCandidates) return false;
            if (poolCount < 0 || itemCount < 0) return false;
            if (candidateCount < 0 || candidateCount > candidateKeys.Length) return false;

            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                supports[i] = 0f;
            }
            for (int i = 0; i < candidateKeys.Length; i++)
            {
                candidateKeys[i] = null;
            }
            poolCount = 0;
            itemCount = 0;
            candidateCount = 0;
            return true;
        }

    }
}
