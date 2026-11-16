using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The per-step near set: which static boxes can possibly be touched this step,
    /// with a two-radius hysteresis and an explicit overflow verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a window at all. The static table is 256 entries (a big platform plus ramps);
    /// querying all of it every step costs ten times what the old twelve-solid map cost,
    /// so the solver only queries the near set (24). The window is recomputed from the
    /// position every step and carries no hidden state except the previous id list the
    /// hysteresis needs, so a teleport or a rollback reproduces it exactly.
    /// </para>
    /// <para>
    /// The two radii are geometric, not guessed: <c>R_in = r_phys + needleCap +
    /// maxStepTravel + 0.05</c> (the caller passes <c>maxTravel</c> already including the
    /// needle cap) and <c>R_out = R_in + 0.5</c>. An item joins when its exact distance is
    /// within R_in; an item that was already selected stays until it is beyond R_out, so a
    /// boundary item jittering by a millimetre cannot flap the working set (and its
    /// rebuild cost) every step. The hysteresis never changes physics, only rebuilds.
    /// </para>
    /// <para>
    /// The needle cap of the design's R_in belongs at the call site, exactly as in the
    /// reference model's <c>select_window(obbs, center, radius, max_travel, ...)</c>: this
    /// function takes the radius and the travel it is given and adds the margin. A caller
    /// that has pins in play passes <c>r_phys + cap</c> as the radius (or folds the cap
    /// into maxTravel) and gets the design's two radii; the tests pass the plain radius
    /// because their fixtures have no pins.
    /// </para>
    /// <para>
    /// Overflow is explicit, never silent. The nearest <c>capacity</c> candidates are
    /// kept in (distance, surface id) order and the rest are counted; <c>complete</c> is
    /// false only when a dropped item could have been within contact reach of the ball
    /// (<c>r + 0.02</c>), which is exactly the case the test field must treat as an
    /// invalid cell instead of a pass. <c>rebuilt</c> says whether the selected id
    /// sequence changed, i.e. whether the caller has to copy the near arrays.
    /// </para>
    /// <para>
    /// The scan mirrors the reference model line for line (approximate bounding-sphere
    /// distance first, then the exact closest point), including its selection order:
    /// sorted by distance, ties by surface id. The scan uses the caller's scratch buffers
    /// so a step allocates nothing, and every buffer is validated up front rather than
    /// truncated.
    /// </para>
    /// </remarks>
    public static class ContactWindowMath
    {
        /// <summary>
        /// R_in = r + maxTravel + enterMargin: the radius an item must be within to join
        /// the near set.
        /// </summary>
        public static float WindowRadiusIn(float radius, float maxTravel,
            float enterMargin = ContactTuning.WindowEnterMarginM)
        {
            return radius + maxTravel + enterMargin;
        }

        /// <summary>
        /// R_out = R_in + exitMargin: the radius an already-selected item must leave.
        /// </summary>
        public static float WindowRadiusOut(float radius, float maxTravel,
            float enterMargin = ContactTuning.WindowEnterMarginM,
            float exitMargin = ContactTuning.WindowExitMarginM)
        {
            return WindowRadiusIn(radius, maxTravel, enterMargin) + exitMargin;
        }

        /// <summary>
        /// Fills <paramref name="selectedIndices"/> with the near-set indices (sorted by
        /// distance, ties by surface id) and reports the overflow verdict.
        /// </summary>
        /// <remarks>
        /// All buffers are caller-owned parallel arrays of the static table:
        /// <paramref name="scratchIndices"/>/<paramref name="scratchDistances"/> must hold
        /// <paramref name="obbCount"/> entries each, <paramref name="selectedIndices"/> at
        /// least <paramref name="capacity"/> entries, and
        /// <paramref name="previousSurfaceIds"/> the previous selection's ids in order.
        /// False means an unusable argument or a non-finite table entry; every out value is
        /// then the empty/complete/rebuilt state and nothing is selected - a fail-closed
        /// answer, because a window built from broken geometry must not be used.
        /// </remarks>
        public static bool TrySelectWindow(
            int obbCount,
            Vector3[] obbCenters,
            Vector3[] obbAxisX,
            Vector3[] obbAxisY,
            Vector3[] obbAxisZ,
            Vector3[] obbHalfExtents,
            float[] obbBoundingRadii,
            int[] obbSurfaceIds,
            Vector3 center,
            float radius,
            float maxTravel,
            int[] previousSurfaceIds,
            int previousCount,
            int capacity,
            int[] scratchIndices,
            float[] scratchDistances,
            int[] selectedIndices,
            out int selectedCount,
            out int dropped,
            out bool complete,
            out bool rebuilt,
            float enterMargin = ContactTuning.WindowEnterMarginM,
            float exitMargin = ContactTuning.WindowExitMarginM,
            float completeMargin = ContactTuning.WindowCompleteMarginM)
        {
            selectedCount = 0;
            dropped = 0;
            complete = true;
            rebuilt = false;

            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(maxTravel) || maxTravel < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(enterMargin) || enterMargin < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(exitMargin) || exitMargin < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(completeMargin) || completeMargin < 0f)
                return false;
            if (capacity < 0) return false;
            if (obbCenters == null || obbAxisX == null || obbAxisY == null ||
                obbAxisZ == null || obbHalfExtents == null || obbBoundingRadii == null ||
                obbSurfaceIds == null) return false;
            if (previousSurfaceIds == null) return false;
            if (scratchIndices == null || scratchDistances == null ||
                selectedIndices == null) return false;
            if (obbCount < 0 || obbCount > obbCenters.Length ||
                obbCount > obbAxisX.Length || obbCount > obbAxisY.Length ||
                obbCount > obbAxisZ.Length || obbCount > obbHalfExtents.Length ||
                obbCount > obbBoundingRadii.Length || obbCount > obbSurfaceIds.Length)
                return false;
            if (scratchIndices.Length < obbCount || scratchDistances.Length < obbCount)
                return false;
            if (selectedIndices.Length < capacity) return false;
            if (previousCount < 0 || previousCount > previousSurfaceIds.Length) return false;

            float radiusIn = WindowRadiusIn(radius, maxTravel, enterMargin);
            float radiusOut = radiusIn + exitMargin;

            int candidateCount = 0;
            for (int i = 0; i < obbCount; i++)
            {
                float dx = center.x - obbCenters[i].x;
                float dy = center.y - obbCenters[i].y;
                float dz = center.z - obbCenters[i].z;
                float approximate = Mathf.Sqrt(dx * dx + dy * dy + dz * dz)
                    - obbBoundingRadii[i];
                if (!KatamariMath.IsFiniteFloat(approximate)) return false;
                if (approximate > radiusOut) continue;
                bool wasSelected = ContainsSurfaceId(previousSurfaceIds, previousCount,
                    obbSurfaceIds[i]);
                Vector3 closestPoint;
                float distance;
                Vector3 normal;
                int feature;
                if (!ObbContactMath.TryClosestPointOnObb(obbCenters[i], obbAxisX[i],
                    obbAxisY[i], obbAxisZ[i], obbHalfExtents[i], center, out closestPoint,
                    out distance, out normal, out feature)) return false;
                if (distance <= radiusIn || (wasSelected && distance <= radiusOut))
                {
                    scratchIndices[candidateCount] = i;
                    scratchDistances[candidateCount] = distance;
                    candidateCount++;
                }
            }

            // Partial selection sort: each slot takes the nearest remaining candidate
            // (ties by surface id), so the first `capacity` entries are exactly what a full
            // sort would put there, at capacity x candidates comparisons instead of a full
            // sort of up to 256.
            int keep = Mathf.Min(capacity, candidateCount);
            for (int slot = 0; slot < keep; slot++)
            {
                int bestSlot = slot;
                for (int k = slot + 1; k < candidateCount; k++)
                {
                    float candidateDistance = scratchDistances[k];
                    float bestDistance = scratchDistances[bestSlot];
                    if (candidateDistance < bestDistance ||
                        (candidateDistance == bestDistance &&
                            obbSurfaceIds[scratchIndices[k]] <
                            obbSurfaceIds[scratchIndices[bestSlot]]))
                    {
                        bestSlot = k;
                    }
                }
                if (bestSlot != slot)
                {
                    int swapIndex = scratchIndices[slot];
                    scratchIndices[slot] = scratchIndices[bestSlot];
                    scratchIndices[bestSlot] = swapIndex;
                    float swapDistance = scratchDistances[slot];
                    scratchDistances[slot] = scratchDistances[bestSlot];
                    scratchDistances[bestSlot] = swapDistance;
                }
                selectedIndices[slot] = scratchIndices[slot];
            }
            selectedCount = keep;

            dropped = Mathf.Max(0, candidateCount - capacity);
            if (dropped > 0)
            {
                float nearestDropped = scratchDistances[keep];
                for (int k = keep + 1; k < candidateCount; k++)
                {
                    if (scratchDistances[k] < nearestDropped)
                        nearestDropped = scratchDistances[k];
                }
                complete = nearestDropped > radius + completeMargin;
            }

            rebuilt = !SameSurfaceIds(selectedIndices, selectedCount, previousSurfaceIds,
                previousCount, obbSurfaceIds);
            return true;
        }

        /// <summary>
        /// The proxy-table twin of <see cref="TrySelectWindow"/>: same scan, hysteresis,
        /// ordering and overflow verdict, but the exact distance comes from
        /// <see cref="ProxyContactMath.TryClosestPointOnProxy"/> for a slot whose kind is
        /// not <see cref="ProxyContactMath.KindBox"/>. Box slots (the 256 static prefix and a
        /// prop without a collider block) still use <see cref="ObbContactMath"/>, so a scene
        /// with no proxy is bit-for-bit the old selection.
        /// </summary>
        public static bool TrySelectProxyWindow(
            int obbCount,
            Vector3[] obbCenters,
            Vector3[] obbAxisX,
            Vector3[] obbAxisY,
            Vector3[] obbAxisZ,
            Vector3[] obbHalfExtents,
            float[] obbBoundingRadii,
            int[] obbSurfaceIds,
            int[] obbKinds,
            float[] obbRadii,
            float[] obbHalfHeights,
            int[] obbAxes,
            int[] hullStarts,
            int[] hullCounts,
            int[] faceStarts,
            int[] faceCounts,
            Vector3[] hullPoints,
            Vector3[] faceNormals,
            float[] faceOffsets,
            int[] faceA,
            int[] faceB,
            int[] faceC,
            Vector3 center,
            float radius,
            float maxTravel,
            int[] previousSurfaceIds,
            int previousCount,
            int capacity,
            int[] scratchIndices,
            float[] scratchDistances,
            int[] selectedIndices,
            out int selectedCount,
            out int dropped,
            out bool complete,
            out bool rebuilt,
            float enterMargin = ContactTuning.WindowEnterMarginM,
            float exitMargin = ContactTuning.WindowExitMarginM,
            float completeMargin = ContactTuning.WindowCompleteMarginM)
        {
            selectedCount = 0;
            dropped = 0;
            complete = true;
            rebuilt = false;

            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(maxTravel) || maxTravel < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(enterMargin) || enterMargin < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(exitMargin) || exitMargin < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(completeMargin) || completeMargin < 0f)
                return false;
            if (capacity < 0) return false;
            if (obbCenters == null || obbAxisX == null || obbAxisY == null ||
                obbAxisZ == null || obbHalfExtents == null || obbBoundingRadii == null ||
                obbSurfaceIds == null || obbKinds == null || obbRadii == null ||
                obbHalfHeights == null || obbAxes == null || hullStarts == null ||
                hullCounts == null || faceStarts == null || faceCounts == null ||
                hullPoints == null || faceNormals == null || faceOffsets == null ||
                faceA == null || faceB == null || faceC == null) return false;
            if (previousSurfaceIds == null || scratchIndices == null ||
                scratchDistances == null || selectedIndices == null) return false;
            if (obbCount < 0 || obbCount > obbCenters.Length ||
                obbCount > obbAxisX.Length || obbCount > obbAxisY.Length ||
                obbCount > obbAxisZ.Length || obbCount > obbHalfExtents.Length ||
                obbCount > obbBoundingRadii.Length || obbCount > obbSurfaceIds.Length ||
                obbCount > obbKinds.Length || obbCount > obbRadii.Length ||
                obbCount > obbHalfHeights.Length || obbCount > obbAxes.Length ||
                obbCount > hullStarts.Length || obbCount > hullCounts.Length ||
                obbCount > faceStarts.Length || obbCount > faceCounts.Length)
                return false;
            if (scratchIndices.Length < obbCount || scratchDistances.Length < obbCount)
                return false;
            if (selectedIndices.Length < capacity) return false;
            if (previousCount < 0 || previousCount > previousSurfaceIds.Length) return false;

            float radiusIn = WindowRadiusIn(radius, maxTravel, enterMargin);
            float radiusOut = radiusIn + exitMargin;

            int candidateCount = 0;
            for (int i = 0; i < obbCount; i++)
            {
                float dx = center.x - obbCenters[i].x;
                float dy = center.y - obbCenters[i].y;
                float dz = center.z - obbCenters[i].z;
                float approximate = Mathf.Sqrt(dx * dx + dy * dy + dz * dz)
                    - obbBoundingRadii[i];
                if (!KatamariMath.IsFiniteFloat(approximate)) return false;
                if (approximate > radiusOut) continue;
                bool wasSelected = ContainsSurfaceId(previousSurfaceIds, previousCount,
                    obbSurfaceIds[i]);

                int kind = obbKinds[i];
                Vector3 closestPoint;
                float distance;
                Vector3 normal;
                int feature;
                if (kind == ProxyContactMath.KindBox)
                {
                    if (!ObbContactMath.TryClosestPointOnObb(obbCenters[i], obbAxisX[i],
                        obbAxisY[i], obbAxisZ[i], obbHalfExtents[i], center, out closestPoint,
                        out distance, out normal, out feature)) return false;
                }
                else if (!ProxyContactMath.TryClosestPointOnProxy(kind, obbCenters[i],
                    obbAxisX[i], obbAxisY[i], obbAxisZ[i], obbHalfExtents[i], obbRadii[i],
                    obbHalfHeights[i], obbAxes[i], hullPoints, hullStarts[i], hullCounts[i],
                    faceNormals, faceOffsets, faceA, faceB, faceC, faceStarts[i],
                    faceCounts[i], center, out closestPoint, out distance, out normal,
                    out feature))
                {
                    return false;
                }

                if (distance <= radiusIn || (wasSelected && distance <= radiusOut))
                {
                    scratchIndices[candidateCount] = i;
                    scratchDistances[candidateCount] = distance;
                    candidateCount++;
                }
            }

            int keep = Mathf.Min(capacity, candidateCount);
            for (int slot = 0; slot < keep; slot++)
            {
                int bestSlot = slot;
                for (int k = slot + 1; k < candidateCount; k++)
                {
                    float candidateDistance = scratchDistances[k];
                    float bestDistance = scratchDistances[bestSlot];
                    if (candidateDistance < bestDistance ||
                        (candidateDistance == bestDistance &&
                            obbSurfaceIds[scratchIndices[k]] <
                            obbSurfaceIds[scratchIndices[bestSlot]]))
                    {
                        bestSlot = k;
                    }
                }
                if (bestSlot != slot)
                {
                    int swapIndex = scratchIndices[slot];
                    scratchIndices[slot] = scratchIndices[bestSlot];
                    scratchIndices[bestSlot] = swapIndex;
                    float swapDistance = scratchDistances[slot];
                    scratchDistances[slot] = scratchDistances[bestSlot];
                    scratchDistances[bestSlot] = swapDistance;
                }
                selectedIndices[slot] = scratchIndices[slot];
            }
            selectedCount = keep;

            dropped = Mathf.Max(0, candidateCount - capacity);
            if (dropped > 0)
            {
                float nearestDropped = scratchDistances[keep];
                for (int k = keep + 1; k < candidateCount; k++)
                {
                    if (scratchDistances[k] < nearestDropped)
                        nearestDropped = scratchDistances[k];
                }
                complete = nearestDropped > radius + completeMargin;
            }

            rebuilt = !SameSurfaceIds(selectedIndices, selectedCount, previousSurfaceIds,
                previousCount, obbSurfaceIds);
            return true;
        }

        private static bool ContainsSurfaceId(int[] surfaceIds, int count, int surfaceId)
        {
            for (int i = 0; i < count; i++)
            {
                if (surfaceIds[i] == surfaceId) return true;
            }
            return false;
        }

        private static bool SameSurfaceIds(int[] selectedIndices, int selectedCount,
            int[] previousSurfaceIds, int previousCount, int[] obbSurfaceIds)
        {
            if (selectedCount != previousCount) return false;
            for (int i = 0; i < selectedCount; i++)
            {
                if (obbSurfaceIds[selectedIndices[i]] != previousSurfaceIds[i]) return false;
            }
            return true;
        }
    }
}
