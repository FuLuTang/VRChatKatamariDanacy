using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Sparse radial shell for the ball: 16 fixed directions with one support
    /// length each, plus the at-most-four long-object extreme candidate keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coordinates: world space is the project's single left-handed space, +Y up,
    /// lengths in metres. Directions and support lengths live in the ball-local
    /// frame. A world endpoint reaches it as
    /// local = inverse(ballRotation) * (worldPoint - ballPosition), and
    /// direction * length builds an endpoint from a unit direction. A support
    /// length is the distance along its direction to the shell, so it is also in
    /// metres and is never negative: the core radius is its floor.
    /// </para>
    /// <para>
    /// The class is stateless math over a caller-owned shell table. The caller
    /// owns the 16 directions (built once by <see cref="TryFillDirections"/>), the
    /// 16 support lengths, the flattened endpoint pool and the candidate-key
    /// slots. Nothing allocates, nothing is cached in a static field, and no call
    /// reads time, a transform or a scene object.
    /// </para>
    /// <para>
    /// Ported behaviour. A support is
    /// max(coreRadius, dot(endpoint, direction)) over every attached endpoint, so
    /// support is a max and never a historical running maximum: removing the
    /// largest co-directional contribution uncovers the next largest instead of
    /// leaving the rod at its old length. An attach recomputes only the
    /// directions inside the new item's endpoint hemispheres
    /// (<see cref="TryCollectAffectedDirections"/>); an item removal recomputes
    /// the same hemispheres from the surviving endpoints
    /// (<see cref="TrySolveSupports"/>). Unaffected directions need no write
    /// because a removed or added endpoint that does not point into them cannot
    /// change a max whose floor is the non-negative core radius.
    /// </para>
    /// <para>
    /// The 16 directions are a decided sampling summary (see the
    /// <see cref="DirectionCount"/> remarks); the 4-candidate limit is still a
    /// provisional prototype value, not a measured budget. A directions array of
    /// any other length is rejected rather than sampled differently, and a
    /// candidate-key array longer than the limit is rejected as a configuration
    /// error. A full candidate list rejects the new key; it never evicts a
    /// surviving one.
    /// </para>
    /// <para>
    /// Failure is a false return, never an exception. Inputs are checked before
    /// anything is written, and the rebuild entry point validates the whole
    /// ledger before it writes a single support, which is the model's atomic
    /// rebuild. A buffer passed to a failed call must not be committed.
    /// </para>
    /// </remarks>
    public static class RadialProxyMath
    {
        /// <summary>Fixed direction count of the model; any other length is rejected.</summary>
        /// <remarks>
        /// Sixteen, not thirty-two (the earlier provisional value). This table is only a
        /// sampling summary of the ball + long-object envelope for diagnostics and the
        /// future collision summary: the per-step ground solve consumes the endpoint
        /// cloud itself, never this table. Sixteen matches the original game's rolling
        /// model (16 fixed pins plus 4 optional, patent US7402104B2), and the worst-case
        /// angular gap grows only from about 27.4 to 38.9 degrees. The endpoint pool is
        /// unrelated: 4 long items x 8 AABB corners = 32 endpoints stays as it is.
        /// </remarks>
        public const int DirectionCount = 16;

        /// <summary>Maximum length of the extreme-candidate key list (0..4 are valid).</summary>
        public const int MaxExtremeCandidates = 4;

        // Float-scaled replacement for the model's 1e-12 double epsilon: a Fibonacci
        // direction computed in float is unit to roughly 1e-7, not to 1e-12.
        private const float UnitDirectionTolerance = 1e-5f;

        // Slack for "this quaternion is unit length", the same rule the ground-contact
        // layer applies to the same ball rotation.
        private const float RotationTolerance = 1e-4f;

        /// <summary>
        /// Fills a 16-slot array with the model's deterministic Fibonacci sphere.
        /// </summary>
        /// <remarks>
        /// y_i = 1 - 2*(i+0.5)/16 and the azimuth advances by the golden angle, so
        /// the set is spread without a seam and is deterministic; platform trig
        /// differences stay well inside the unit tolerance. The result is unit
        /// length to float precision, not exactly 1.
        /// False only means the buffer does not have exactly
        /// <see cref="DirectionCount"/> slots.
        /// </remarks>
        public static bool TryFillDirections(Vector3[] directions)
        {
            if (directions == null || directions.Length != DirectionCount) return false;
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < DirectionCount; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / DirectionCount;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;
                Vector3 direction = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
                if (!KatamariMath.IsFiniteVector(direction)) return false;
                directions[i] = direction;
            }
            return true;
        }

        /// <summary>
        /// Builds the endpoint p = direction * length from a unit direction.
        /// </summary>
        /// <remarks>
        /// The length is metres and must be non-negative and finite; zero is a
        /// valid endpoint at the ball center. The direction must already be unit
        /// length to within 1e-5 (a direction from
        /// <see cref="TryFillDirections"/> qualifies) and is renormalized before
        /// the multiply, so the endpoint keeps exactly the requested length.
        /// </remarks>
        public static bool TryDirectionLengthPoint(Vector3 direction, float length, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 unit;
            float magnitude;
            if (!KatamariMath.TryNormalize(direction, out unit, out magnitude)) return false;
            if (Mathf.Abs(magnitude - 1f) > UnitDirectionTolerance) return false;
            if (!KatamariMath.IsFiniteFloat(length) || length < 0f) return false;
            Vector3 candidate = unit * length;
            if (!KatamariMath.IsFiniteVector(candidate)) return false;
            point = candidate;
            return true;
        }

        /// <summary>
        /// Transforms one world-space point into the ball-local frame.
        /// </summary>
        /// <remarks>
        /// The ball rotation is the active unit-length rotation that maps local
        /// points into world space (a Transform rotation), so the inverse maps the
        /// world offset back. It must be unit length: a scaled, zero or non-finite
        /// quaternion is rejected instead of normalized, because silently rescaling
        /// would hide a broken transform. The reference model normalizes a host
        /// quaternion; every runtime rotation here comes from a Unity Transform.
        /// </remarks>
        public static bool TryWorldPointToLocal(Vector3 worldPoint, Vector3 ballPosition,
            Quaternion ballRotation, out Vector3 localPoint)
        {
            localPoint = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(ballPosition)) return false;
            if (!IsUnitRotation(ballRotation)) return false;
            return TryLocalPoint(worldPoint, ballPosition, ballRotation, out localPoint);
        }

        /// <summary>
        /// Transforms a whole attach/rebuild endpoint list into the ball-local
        /// frame, writing into the caller's pool.
        /// </summary>
        /// <remarks>
        /// Every endpoint is validated before the pool is written, so a rejected
        /// attach leaves the previous pool intact. worldEndpointCount selects the
        /// used slots, which lets the caller reuse an oversized pool. The pool must
        /// have room for the whole batch.
        /// </remarks>
        public static bool TryTransformEndpointsToLocal(Vector3[] worldEndpoints, int worldEndpointCount,
            Vector3 ballPosition, Quaternion ballRotation, Vector3[] localEndpoints)
        {
            if (worldEndpoints == null || localEndpoints == null) return false;
            if (worldEndpointCount < 0 || worldEndpointCount > worldEndpoints.Length) return false;
            if (localEndpoints.Length < worldEndpointCount) return false;
            if (!KatamariMath.IsFiniteVector(ballPosition)) return false;
            if (!IsUnitRotation(ballRotation)) return false;
            for (int i = 0; i < worldEndpointCount; i++)
            {
                Vector3 probe;
                if (!TryLocalPoint(worldEndpoints[i], ballPosition, ballRotation, out probe)) return false;
            }
            for (int i = 0; i < worldEndpointCount; i++)
            {
                Vector3 localPoint;
                // The dry pass above proved this exact call cannot fail.
                TryLocalPoint(worldEndpoints[i], ballPosition, ballRotation, out localPoint);
                localEndpoints[i] = localPoint;
            }
            return true;
        }

        /// <summary>
        /// Collects, in ascending direction order, the directions whose hemisphere
        /// contains at least one of the given ball-local endpoints.
        /// </summary>
        /// <remarks>
        /// This is the model's near-direction scan: a direction is affected when
        /// dot(endpoint, direction) &gt; 0 for any endpoint. Each dot must be
        /// finite, so an extreme endpoint is rejected rather than compared against
        /// infinity. The index buffer must have room for the worst case (all
        /// <see cref="DirectionCount"/> directions); a smaller buffer is rejected
        /// up front so a partially written index list is never returned.
        /// </remarks>
        public static bool TryCollectAffectedDirections(Vector3[] localEndpoints, int localEndpointCount,
            Vector3[] directions, int[] affectedIndices, out int affectedCount)
        {
            affectedCount = 0;
            if (localEndpoints == null || directions == null || affectedIndices == null) return false;
            if (localEndpointCount < 0 || localEndpointCount > localEndpoints.Length) return false;
            if (directions.Length != DirectionCount) return false;
            if (affectedIndices.Length < DirectionCount) return false;
            for (int d = 0; d < DirectionCount; d++)
            {
                if (!IsUnitDirection(directions[d])) return false;
            }
            for (int i = 0; i < localEndpointCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(localEndpoints[i])) return false;
            }
            for (int d = 0; d < DirectionCount; d++)
            {
                for (int i = 0; i < localEndpointCount; i++)
                {
                    float projection;
                    if (!TryDot(localEndpoints[i], directions[d], out projection))
                    {
                        affectedCount = 0;
                        return false;
                    }
                    if (projection > 0f)
                    {
                        affectedIndices[affectedCount] = d;
                        affectedCount++;
                        break;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Recomputes the supports of the listed directions from the given
        /// ball-local endpoints, leaving every other support slot untouched.
        /// </summary>
        /// <remarks>
        /// This serves both an attach and a detach: the caller passes the
        /// hemispheres from <see cref="TryCollectAffectedDirections"/> and the
        /// full surviving endpoint set. support[i] = max(coreRadius,
        /// dot(endpoint, directions[i])) over all endpoints, so the surviving
        /// second-largest contribution becomes the support after the largest item
        /// is removed. The core radius is metres and must be non-negative.
        ///
        /// The supports buffer is indexed by direction, so it must hold
        /// <see cref="DirectionCount"/> slots. The whole index set is proved valid
        /// and finite before a slot is written; a false return leaves the supports
        /// buffer unchanged.
        /// </remarks>
        public static bool TrySolveSupports(float coreRadius, Vector3[] localEndpoints, int localEndpointCount,
            Vector3[] directions, int[] directionIndices, int directionIndexCount, float[] supports)
        {
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            if (localEndpoints == null || directions == null || directionIndices == null || supports == null)
                return false;
            if (localEndpointCount < 0 || localEndpointCount > localEndpoints.Length) return false;
            if (directionIndexCount < 0 || directionIndexCount > directionIndices.Length) return false;
            if (directions.Length != DirectionCount) return false;
            if (supports.Length < DirectionCount) return false;
            for (int i = 0; i < localEndpointCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(localEndpoints[i])) return false;
            }
            for (int d = 0; d < DirectionCount; d++)
            {
                if (!IsUnitDirection(directions[d])) return false;
            }
            for (int k = 0; k < directionIndexCount; k++)
            {
                if (!IsDirectionIndex(directionIndices[k])) return false;
            }
            // Dry pass first: an overflowing dot must not leave a half-updated
            // support table behind.
            for (int k = 0; k < directionIndexCount; k++)
            {
                float probe;
                if (!TrySupportAt(localEndpoints, 0, localEndpointCount, directions[directionIndices[k]],
                    coreRadius, out probe)) return false;
            }
            for (int k = 0; k < directionIndexCount; k++)
            {
                float support;
                // The dry pass above proved this exact call cannot fail.
                TrySupportAt(localEndpoints, 0, localEndpointCount, directions[directionIndices[k]],
                    coreRadius, out support);
                supports[directionIndices[k]] = support;
            }
            return true;
        }

        /// <summary>
        /// Returns how far a support sticks out of the core: max(0, length - radius).
        /// </summary>
        /// <remarks>
        /// Both values are metres. The core radius is supplied by the caller, never
        /// derived here, so a ball that has already grown reports a shrinking
        /// protrusion at an unchanged support length. False means a non-finite
        /// value or a negative radius, and the out value is then zero.
        /// </remarks>
        public static bool TryProtrusion(float supportLength, float coreRadius, out float protrusion)
        {
            protrusion = 0f;
            if (!KatamariMath.IsFiniteFloat(supportLength)) return false;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            float value = Mathf.Max(0f, supportLength - coreRadius);
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            protrusion = value;
            return true;
        }

        /// <summary>
        /// Inserts a candidate key into its ordinal-ordered slot list.
        /// </summary>
        /// <remarks>
        /// The key list reports in ordinal order, and a duplicate or empty key is
        /// rejected: an item key is a stable instance identity (spawn id plus
        /// generation) supplied by the caller. The slot count is the capacity; a
        /// list longer than <see cref="MaxExtremeCandidates"/> is a configuration
        /// error. When the list is full the new key is rejected and no surviving
        /// key is evicted, which is the model's explicit overflow refusal.
        /// </remarks>
        public static bool TryInsertCandidateKey(string[] candidateKeys, ref int candidateCount, string itemKey)
        {
            if (candidateKeys == null) return false;
            if (candidateKeys.Length > MaxExtremeCandidates) return false;
            if (candidateCount < 0 || candidateCount > candidateKeys.Length) return false;
            if (itemKey == null || itemKey.Length == 0) return false;
            if (candidateCount >= candidateKeys.Length) return false;
            for (int i = 0; i < candidateCount; i++)
            {
                if (string.CompareOrdinal(candidateKeys[i], itemKey) == 0) return false;
            }
            int insertAt = candidateCount;
            while (insertAt > 0 && string.CompareOrdinal(candidateKeys[insertAt - 1], itemKey) > 0)
            {
                candidateKeys[insertAt] = candidateKeys[insertAt - 1];
                insertAt--;
            }
            candidateKeys[insertAt] = itemKey;
            candidateCount++;
            return true;
        }

        /// <summary>
        /// Removes one candidate key, closing the gap and keeping the order.
        /// </summary>
        /// <remarks>
        /// Removing a key that is not listed is not an error: detach removes a
        /// contribution whether or not it was a declared extreme candidate. The
        /// freed slot is reused by the next insert, which is how a dropped long
        /// object releases its candidate place.
        /// </remarks>
        public static bool TryRemoveCandidateKey(string[] candidateKeys, ref int candidateCount,
            string itemKey, out bool existed)
        {
            existed = false;
            if (candidateKeys == null) return false;
            if (candidateKeys.Length > MaxExtremeCandidates) return false;
            if (candidateCount < 0 || candidateCount > candidateKeys.Length) return false;
            if (itemKey == null || itemKey.Length == 0) return false;
            for (int i = 0; i < candidateCount; i++)
            {
                if (string.CompareOrdinal(candidateKeys[i], itemKey) != 0) continue;
                for (int move = i; move < candidateCount - 1; move++)
                {
                    candidateKeys[move] = candidateKeys[move + 1];
                }
                candidateCount--;
                candidateKeys[candidateCount] = null;
                existed = true;
                return true;
            }
            return true;
        }

        /// <summary>
        /// Rebuilds the whole shell from a caller-owned ledger of ball-local items.
        /// </summary>
        /// <remarks>
        /// Each item is a slice (start and length) of the flattened endpoint pool
        /// plus its stable key and its extreme-candidate flag. Compatibility with
        /// the model's rebuild: item order does not matter (a support is a max over
        /// every ledger endpoint, and unreferenced pool slots never count), every
        /// item needs at least one endpoint, keys must be unique and non-empty, and
        /// a candidate count above the candidate-key capacity rejects the whole
        /// rebuild. The candidate-key array length is the capacity and may be 0,
        /// which forbids candidates entirely.
        ///
        /// The call validates the entire ledger, then proves every support finite,
        /// and only then writes the supports and the candidate list: a false return
        /// leaves both output arrays exactly as the caller had them. That is the
        /// model's atomic rebuild, which must not leave a partly rebuilt shell after
        /// a rejected ledger.
        /// </remarks>
        public static bool TryRebuild(float coreRadius, Vector3[] localEndpoints, int[] itemStarts,
            int[] itemLengths, string[] itemKeys, bool[] itemIsExtremeCandidate, int itemCount,
            Vector3[] directions, float[] supports, string[] candidateKeys, out int candidateCount)
        {
            candidateCount = 0;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            if (localEndpoints == null || itemStarts == null || itemLengths == null ||
                itemKeys == null || itemIsExtremeCandidate == null) return false;
            if (directions == null || supports == null || candidateKeys == null) return false;
            if (directions.Length != DirectionCount) return false;
            if (supports.Length < DirectionCount) return false;
            if (candidateKeys.Length > MaxExtremeCandidates) return false;
            if (itemCount < 0) return false;
            if (itemCount > itemStarts.Length || itemCount > itemLengths.Length ||
                itemCount > itemKeys.Length || itemCount > itemIsExtremeCandidate.Length) return false;
            for (int d = 0; d < DirectionCount; d++)
            {
                if (!IsUnitDirection(directions[d])) return false;
            }
            int extremeCount = 0;
            for (int item = 0; item < itemCount; item++)
            {
                string key = itemKeys[item];
                if (key == null || key.Length == 0) return false;
                for (int earlier = 0; earlier < item; earlier++)
                {
                    if (string.CompareOrdinal(itemKeys[earlier], key) == 0) return false;
                }
                int start = itemStarts[item];
                int length = itemLengths[item];
                if (start < 0 || length < 1) return false;
                if (start > localEndpoints.Length - length) return false;
                for (int i = start; i < start + length; i++)
                {
                    if (!KatamariMath.IsFiniteVector(localEndpoints[i])) return false;
                }
                if (itemIsExtremeCandidate[item]) extremeCount++;
            }
            if (extremeCount > candidateKeys.Length) return false;
            // Dry pass over the whole direction set before any output is written.
            for (int d = 0; d < DirectionCount; d++)
            {
                float probe;
                if (!TryItemSupport(localEndpoints, itemStarts, itemLengths, itemCount, directions[d],
                    coreRadius, out probe)) return false;
            }
            for (int d = 0; d < DirectionCount; d++)
            {
                float support;
                // The dry pass above proved this exact call cannot fail.
                TryItemSupport(localEndpoints, itemStarts, itemLengths, itemCount, directions[d],
                    coreRadius, out support);
                supports[d] = support;
            }
            int written = 0;
            for (int item = 0; item < itemCount; item++)
            {
                if (!itemIsExtremeCandidate[item]) continue;
                // Validated above: unique, non-empty, and within capacity.
                TryInsertCandidateKey(candidateKeys, ref written, itemKeys[item]);
            }
            for (int slot = written; slot < candidateKeys.Length; slot++)
            {
                candidateKeys[slot] = null;
            }
            candidateCount = written;
            return true;
        }

        private static bool TryLocalPoint(Vector3 worldPoint, Vector3 ballPosition, Quaternion ballRotation,
            out Vector3 localPoint)
        {
            localPoint = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(worldPoint)) return false;
            Vector3 delta = worldPoint - ballPosition;
            if (!KatamariMath.IsFiniteVector(delta)) return false;
            Vector3 candidate = Quaternion.Inverse(ballRotation) * delta;
            if (!KatamariMath.IsFiniteVector(candidate)) return false;
            localPoint = candidate;
            return true;
        }

        private static bool TrySupportAt(Vector3[] localEndpoints, int start, int end, Vector3 direction,
            float coreRadius, out float support)
        {
            support = 0f;
            float value = coreRadius;
            for (int i = start; i < end; i++)
            {
                float projection;
                if (!TryDot(localEndpoints[i], direction, out projection)) return false;
                if (projection > value) value = projection;
            }
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            support = value;
            return true;
        }

        // Rebuild support: only the ledger's item slices count, so a stale slot beyond
        // the last item cannot raise a support.
        private static bool TryItemSupport(Vector3[] localEndpoints, int[] itemStarts, int[] itemLengths,
            int itemCount, Vector3 direction, float coreRadius, out float support)
        {
            support = 0f;
            float value = coreRadius;
            for (int item = 0; item < itemCount; item++)
            {
                float itemSupport;
                int start = itemStarts[item];
                if (!TrySupportAt(localEndpoints, start, start + itemLengths[item], direction, coreRadius,
                    out itemSupport)) return false;
                if (itemSupport > value) value = itemSupport;
            }
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            support = value;
            return true;
        }

        private static bool TryDot(Vector3 a, Vector3 b, out float value)
        {
            value = 0f;
            float dot = a.x * b.x + a.y * b.y + a.z * b.z;
            if (!KatamariMath.IsFiniteFloat(dot)) return false;
            value = dot;
            return true;
        }

        private static bool IsUnitDirection(Vector3 direction)
        {
            Vector3 unit;
            float magnitude;
            if (!KatamariMath.TryNormalize(direction, out unit, out magnitude)) return false;
            return Mathf.Abs(magnitude - 1f) <= UnitDirectionTolerance;
        }

        /// <summary>
        /// True when the quaternion is finite and unit length within the layer's
        /// tolerance.
        /// </summary>
        /// <remarks>
        /// Public because the integration layer (<see cref="RadialProxyShellMath"/>)
        /// builds an absorbed prop's corners in the ball frame and must reject the same
        /// broken rotations this layer rejects, rather than normalizing a scaled one.
        /// </remarks>
        public static bool IsUnitRotation(Quaternion rotation)
        {
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            float squaredLength = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            return KatamariMath.IsFiniteFloat(squaredLength) &&
                Mathf.Abs(squaredLength - 1f) <= RotationTolerance;
        }

        private static bool IsDirectionIndex(int index)
        {
            return index >= 0 && index < DirectionCount;
        }
    }
}
