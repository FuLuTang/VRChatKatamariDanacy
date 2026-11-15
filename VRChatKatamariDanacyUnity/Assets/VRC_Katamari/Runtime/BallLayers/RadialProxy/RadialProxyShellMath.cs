using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Connects absorbed props' simplified AABB proxies to a caller-owned radial
    /// shell: builds the box corners in the ball frame, decides which items are
    /// long-object candidates, and composes the incremental attach/detach and the
    /// whole-ledger rebuild that <see cref="RadialProxyMath"/> deliberately leaves
    /// to its caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class adds no new support or transform math. Directions, endpoint
    /// transforms, affected-direction collection, support solving, candidate keys
    /// and the atomic rebuild are calls into <see cref="RadialProxyMath"/>; what
    /// lives here is the integration protocol that has to exist exactly once:
    /// which corner of an absorbed prop is an endpoint, when an item is a
    /// long-object candidate, which array slot it occupies, and how a removal
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
    /// baked into the table. <see cref="GroundContactMath.TrySolve"/> applies the
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

        /// <summary>Long-object candidates the model allows (the same four as the key list).</summary>
        public const int ItemCapacity = RadialProxyMath.MaxExtremeCandidates;

        /// <summary>Endpoint pool a full candidate list needs: 4 items x 8 corners.</summary>
        public const int EndpointCapacity = ItemCapacity * AabbCornerCount;

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
        /// Decides whether an absorbed prop is a long object worth an endpoint
        /// candidate, from its full extents along the prop's local axes and the ball's
        /// core radius.
        /// </summary>
        /// <remarks>
        /// Two provisional shape rules, both needed: the longest edge must be at
        /// least <paramref name="minLongestEdgePerCoreDiameter"/> ball diameters
        /// (a prop much shorter than the ball cannot dominate the 16-direction
        /// shell), and the longest edge must be at least
        /// <paramref name="minElongation"/> times the SECOND-longest edge (one axis
        /// dominates the other two: a rod or plank, not a cup and not a square
        /// sheet whose thin axis merely makes longest/shortest large). A zero
        /// extent is legal, so a flat plank still qualifies. A prop with no
        /// positive extent returns true with <paramref name="isLongObject"/> false:
        /// there is nothing to represent, which is not a data error.
        /// </remarks>
        public static bool TryClassifyLongObject(Vector3 fullExtents, float coreRadius,
            float minElongation, float minLongestEdgePerCoreDiameter, out bool isLongObject)
        {
            isLongObject = false;
            if (!KatamariMath.IsFiniteVector(fullExtents)) return false;
            if (fullExtents.x < 0f || fullExtents.y < 0f || fullExtents.z < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(minElongation) || minElongation < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(minLongestEdgePerCoreDiameter) ||
                minLongestEdgePerCoreDiameter < 0f) return false;

            float longest = fullExtents.x;
            float secondLongest = 0f;
            if (fullExtents.y > longest)
            {
                secondLongest = longest;
                longest = fullExtents.y;
            }
            else
            {
                secondLongest = fullExtents.y;
            }
            if (fullExtents.z > longest)
            {
                secondLongest = longest;
                longest = fullExtents.z;
            }
            else if (fullExtents.z > secondLongest)
            {
                secondLongest = fullExtents.z;
            }
            if (longest <= 0f) return true;

            float diameterBound = 2f * coreRadius * minLongestEdgePerCoreDiameter;
            if (!KatamariMath.IsFiniteFloat(diameterBound)) return false;
            float elongationBound = minElongation * secondLongest;
            if (!KatamariMath.IsFiniteFloat(elongationBound)) return false;

            isLongObject = longest >= diameterBound && longest >= elongationBound;
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
        /// Appends one item's endpoints, recomputes only the directions its
        /// endpoints can affect, and registers its candidate key when asked.
        /// </summary>
        /// <remarks>
        /// The pool, the per-item slices, the item keys and the candidate slots are
        /// the caller's ledger; this method is the only writer of their append
        /// protocol. The endpoint array is the item's own corners (not an offset
        /// into the pool), which is why the same array serves attach and detach.
        /// Capacities, key uniqueness and endpoint finiteness are checked before
        /// the pool is touched; the supports are solved into
        /// <paramref name="solveScratch"/> first and copied to the affected slots
        /// only after that solve and the candidate insert succeeded, so a refused
        /// attach leaves the support table as it was.
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

            int start = poolCount;
            for (int i = 0; i < endpointCount; i++)
            {
                pool[start + i] = endpoints[i];
            }
            poolCount = poolCount + endpointCount;

            int affectedCount;
            if (!RadialProxyMath.TryCollectAffectedDirections(endpoints, endpointCount, directions,
                affectedIndices, out affectedCount))
            {
                poolCount = start;
                return false;
            }
            if (!RadialProxyMath.TrySolveSupports(0f, pool, poolCount, directions, affectedIndices,
                affectedCount, solveScratch))
            {
                poolCount = start;
                return false;
            }
            if (isCandidate)
            {
                if (!RadialProxyMath.TryInsertCandidateKey(candidateKeys, ref candidateCount, itemKey))
                {
                    poolCount = start;
                    return false;
                }
            }
            for (int k = 0; k < affectedCount; k++)
            {
                int directionIndex = affectedIndices[k];
                supports[directionIndex] = solveScratch[directionIndex];
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
        /// (the table stores endpoint maxima; GroundContact adds the current core),
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

        /// <summary>
        /// Builds the ball-local endpoint array handed to
        /// <see cref="GroundContactMath.TrySolve"/> each step: the first
        /// <paramref name="endpointCount"/> slots are the endpoints with their
        /// length clamped to <paramref name="coreRadius"/> +
        /// <paramref name="maxProtrusionM"/>, and every unused slot is zero.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The clamp is the hard maximum-protrusion rule: a long rod may not lift
        /// the ball more than the cap above its core, however far its tip is. It is
        /// applied here, not when the endpoint is stored, because the stored raw
        /// length is what lets a growing core retract the relative protrusion
        /// naturally. The clamped points are the only proxy data the per-step
        /// contact solve sees, so the cap cannot be bypassed by a stale support
        /// table.
        /// </para>
        /// <para>
        /// Zero slots are valid input: their projection is zero, which never raises
        /// a support above the core floor. <paramref name="contactPoints"/> must be
        /// exactly as long as the pool so the arrays cannot disagree about the
        /// unused slots.
        /// </para>
        /// </remarks>
        public static bool TryBuildContactPoints(float coreRadius, float maxProtrusionM,
            Vector3[] endpoints, int endpointCount, Vector3[] contactPoints)
        {
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(maxProtrusionM) || maxProtrusionM < 0f) return false;
            if (endpoints == null || contactPoints == null) return false;
            if (contactPoints.Length != endpoints.Length) return false;
            if (endpointCount < 0 || endpointCount > endpoints.Length) return false;

            float maxLength = coreRadius + maxProtrusionM;
            if (!KatamariMath.IsFiniteFloat(maxLength)) return false;
            for (int i = 0; i < endpointCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(endpoints[i])) return false;
            }

            for (int i = 0; i < contactPoints.Length; i++)
            {
                if (i >= endpointCount)
                {
                    contactPoints[i] = Vector3.zero;
                    continue;
                }
                Vector3 point = endpoints[i];
                float lengthSquared = point.x * point.x + point.y * point.y + point.z * point.z;
                if (!KatamariMath.IsFiniteFloat(lengthSquared)) return false;
                float length = Mathf.Sqrt(lengthSquared);
                if (!KatamariMath.IsFiniteFloat(length)) return false;
                if (length <= 0f)
                {
                    contactPoints[i] = Vector3.zero;
                    continue;
                }
                if (length <= maxLength)
                {
                    contactPoints[i] = point;
                    continue;
                }
                float scale = maxLength / length;
                if (!KatamariMath.IsFiniteFloat(scale)) return false;
                Vector3 clamped = point * scale;
                if (!KatamariMath.IsFiniteVector(clamped)) return false;
                contactPoints[i] = clamped;
            }
            return true;
        }
    }
}
