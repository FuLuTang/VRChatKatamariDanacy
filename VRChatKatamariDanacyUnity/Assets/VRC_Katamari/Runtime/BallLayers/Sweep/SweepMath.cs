using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// One sphere-sweep step against caller-owned one-sided planes and
    /// conservative radius-expanded boxes.
    /// </summary>
    /// <remarks>
    /// Positions and lengths are metres in the project's single world space
    /// (+Y up). No angle is involved. A plane is the free half-space
    /// dot(normal, x) &gt;= offset with the normal pointing into free space; its
    /// normal and offset are rescaled together at entry. A box is solid over
    /// [minimum, maximum] on the world axes. The box is expanded by the sphere
    /// radius and then swept as an AABB, so a corner is treated as square: the
    /// test may report an early contact and must not be read as an exact
    /// sphere-vs-rounded-box distance.
    ///
    /// UdonSharp has no structs, so obstacles and contacts are parallel
    /// caller-owned arrays. Plane i uses planeNormals[i]/planeOffsets[i]; box j
    /// uses boxMinima[j]/boxMaxima[j]. Obstacle order is deterministic: all
    /// planes first, then all boxes, so box j has obstacle index
    /// planeNormals.Length + j. Contacts at the same time of impact are visited
    /// in that order; impacts inside the simultaneity window but not exactly
    /// equal are also visited in obstacle order. The call allocates nothing and
    /// does not materialise the reference's de-duplicated normal list: every
    /// simultaneous normal is projected, and a repeated normal is idempotent.
    /// It reads no scene state and writes only its out values and the contact
    /// buffers. The six contact arrays are written at one shared index; when
    /// the shortest of them is full, later contacts are dropped from the log
    /// (the solve is unaffected) and contactCount stops at that capacity.
    ///
    /// blocked means contacts consumed the requested motion without a solver
    /// failure. stuck means the solve did not finish: no progress, the contact
    /// budget ran out with displacement left, or a final re-check still finds
    /// the sphere embedded. A clean stop is blocked=true, stuck=false.
    ///
    /// Epsilon (1e-6 m) and the simultaneous-impact window (1e-6 of the step)
    /// are float-scale placeholders for the Python reference's 1e-10 and 1e-8
    /// double values; they are not measured tolerances.
    /// </remarks>
    public static class SweepMath
    {
        // Near-zero threshold for distances, lengths and embedded-feature depth.
        private const float Epsilon = 1e-6f;

        // Local times within this window are treated as one simultaneous impact.
        private const float SimultaneousTimeTolerance = 1e-6f;

        // Deterministic projection passes over the active contact normals.
        private const int ProjectionPasses = 4;

        // A wall normal with at most this much horizontal part cannot remove any
        // planar velocity, so it is treated as a floor/ceiling contact.
        private const float PlanarNormalEpsilon = 1e-6f;

        /// <summary>
        /// Sweeps the sphere from start along displacement and reports where it
        /// stops, what displacement is left, and the contacts found.
        /// </summary>
        /// <remarks>
        /// Returns false for invalid input or a non-finite intermediate; every
        /// out value is then zero and no contact is reported. finalPosition is
        /// where the sphere stops, remainingDisplacement is unspent step motion,
        /// and slideDirection is its unit direction (zero when nothing is left).
        /// contactPoints are approximate feature points; a box corner point is
        /// conservative, not an exact sphere-box contact. Obstacle indices use
        /// the plane-then-box numbering. maxContacts bounds the contact-solving
        /// loop, as in the reference, while one step can record several
        /// simultaneous contacts, so the log can hold more entries than that.
        /// Separate contact buffers may have different lengths; the shortest one
        /// bounds the log.
        /// </remarks>
        public static bool TrySweepSphere(
            Vector3 start,
            Vector3 displacement,
            float radius,
            Vector3[] planeNormals,
            float[] planeOffsets,
            Vector3[] boxMinima,
            Vector3[] boxMaxima,
            int maxContacts,
            float separationEpsilon,
            Vector3[] contactCenters,
            Vector3[] contactPoints,
            Vector3[] contactNormals,
            float[] contactTimes,
            int[] contactObstacleIndices,
            float[] contactPenetrationDepths,
            out int contactCount,
            out Vector3 finalPosition,
            out Vector3 remainingDisplacement,
            out Vector3 slideDirection,
            out bool stuck,
            out bool blocked)
        {
            contactCount = 0;
            finalPosition = Vector3.zero;
            remainingDisplacement = Vector3.zero;
            slideDirection = Vector3.zero;
            stuck = false;
            blocked = false;

            if (!KatamariMath.IsFiniteVector(start)) return false;
            if (!KatamariMath.IsFiniteVector(displacement)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(separationEpsilon) || separationEpsilon < 0f) return false;
            if (maxContacts < 1) return false;
            if (planeNormals == null || planeOffsets == null) return false;
            if (planeNormals.Length != planeOffsets.Length) return false;
            if (boxMinima == null || boxMaxima == null) return false;
            if (boxMinima.Length != boxMaxima.Length) return false;
            if (contactCenters == null || contactPoints == null || contactNormals == null) return false;
            if (contactTimes == null || contactObstacleIndices == null) return false;
            if (contactPenetrationDepths == null) return false;

            // Validate every obstacle before solving, matching the reference's
            // check-everything-first behaviour.
            for (int i = 0; i < planeNormals.Length; i++)
            {
                Vector3 unitNormal;
                float offset;
                if (!TryValidatePlane(planeNormals[i], planeOffsets[i], out unitNormal, out offset)) return false;
            }
            for (int i = 0; i < boxMinima.Length; i++)
            {
                Vector3 low;
                Vector3 high;
                if (!TryExpandBox(boxMinima[i], boxMaxima[i], radius, out low, out high)) return false;
            }

            int contactCapacity = contactCenters.Length;
            contactCapacity = Mathf.Min(contactCapacity, contactPoints.Length);
            contactCapacity = Mathf.Min(contactCapacity, contactNormals.Length);
            contactCapacity = Mathf.Min(contactCapacity, contactTimes.Length);
            contactCapacity = Mathf.Min(contactCapacity, contactObstacleIndices.Length);
            contactCapacity = Mathf.Min(contactCapacity, contactPenetrationDepths.Length);

            int obstacleCount = planeNormals.Length + boxMinima.Length;
            Vector3 position = start;
            Vector3 remaining = displacement;
            float timeCursor = 0f;
            float timeScale = 1f;
            bool unresolved = false;
            bool blockedByContact = false;
            bool ranOutOfSteps = true;

            for (int step = 0; step < maxContacts; step++)
            {
                bool anyHit;
                float hitTime;
                if (!TryFindEarliestHit(position, remaining, radius,
                    planeNormals, planeOffsets, boxMinima, boxMaxima,
                    out anyHit, out hitTime)) return false;

                if (!anyHit)
                {
                    Vector3 moved;
                    if (!TryAdd(position, remaining, out moved)) return false;
                    position = moved;
                    remaining = Vector3.zero;
                    ranOutOfSteps = false;
                    break;
                }

                Vector3 stepMotion;
                if (!TryScale(remaining, hitTime, out stepMotion)) return false;
                Vector3 impactPosition;
                if (!TryAdd(position, stepMotion, out impactPosition)) return false;
                Vector3 residual;
                if (!TryScale(remaining, 1f - hitTime, out residual)) return false;

                float contactTime = timeCursor + timeScale * hitTime;
                if (!KatamariMath.IsFiniteFloat(contactTime)) return false;
                float nextTimeScale = timeScale * (1f - hitTime);
                if (!KatamariMath.IsFiniteFloat(nextTimeScale)) return false;

                // Hit tests are pure in (position, remaining), so the same
                // pre-impact snapshot re-finds the simultaneous contacts after
                // the position has moved. That replaces the reference's
                // variable-length normals list without allocating.
                Vector3 scanPosition = position;
                position = impactPosition;
                Vector3 priorPosition = impactPosition;

                for (int pass = 0; pass < ProjectionPasses; pass++)
                {
                    for (int index = 0; index < obstacleCount; index++)
                    {
                        bool hit;
                        float candidateTime;
                        Vector3 normal;
                        float penetration;
                        if (!TryHitObstacle(scanPosition, remaining, radius,
                            planeNormals, planeOffsets, boxMinima, boxMaxima, index,
                            out hit, out candidateTime, out normal, out penetration)) return false;
                        if (!hit || Mathf.Abs(candidateTime - hitTime) > SimultaneousTimeTolerance) continue;

                        if (pass == 0)
                        {
                            // Embedded geometry is pushed out by its real depth
                            // plus the caller's separation epsilon; a clean hit is
                            // pushed out by the epsilon alone.
                            Vector3 pushOffset;
                            if (!TryScale(normal, penetration + separationEpsilon, out pushOffset)) return false;
                            Vector3 separated;
                            if (!TryAdd(position, pushOffset, out separated)) return false;
                            position = separated;

                            if (contactCount < contactCapacity)
                            {
                                if (!RecordContact(position, normal, radius, contactTime, index, penetration,
                                    contactCount, contactCenters, contactPoints, contactNormals,
                                    contactTimes, contactObstacleIndices, contactPenetrationDepths)) return false;
                                contactCount++;
                            }
                        }

                        Vector3 projected;
                        if (!TryProject(residual, normal, out projected)) return false;
                        residual = projected;
                    }
                }

                float priorMotionLength;
                if (!TryLength(remaining, out priorMotionLength)) return false;
                float residualMotionLength;
                if (!TryLength(residual, out residualMotionLength)) return false;
                if (priorMotionLength > Epsilon && residualMotionLength <= Epsilon)
                {
                    blockedByContact = true;
                }

                Vector3 positionDelta;
                if (!TrySubtract(position, priorPosition, out positionDelta)) return false;
                float positionProgress;
                if (!TryLength(positionDelta, out positionProgress)) return false;
                Vector3 motionChange;
                if (!TrySubtract(residual, remaining, out motionChange)) return false;
                float motionChangeLength;
                if (!TryLength(motionChange, out motionChangeLength)) return false;

                if (hitTime <= Epsilon && positionProgress <= Epsilon && motionChangeLength <= Epsilon)
                {
                    remaining = residual;
                    unresolved = true;
                    ranOutOfSteps = false;
                    break;
                }

                timeCursor = contactTime;
                timeScale = nextTimeScale;
                remaining = residual;
            }

            if (ranOutOfSteps)
            {
                float leftover;
                if (!TryLength(remaining, out leftover)) return false;
                if (leftover > Epsilon) unresolved = true;
            }

            // A contact push must not leave the sphere inside another obstacle.
            for (int i = 0; i < planeNormals.Length; i++)
            {
                bool penetrating;
                if (!TryPlanePenetrates(position, radius, planeNormals[i], planeOffsets[i],
                    out penetrating)) return false;
                if (penetrating) unresolved = true;
            }
            for (int i = 0; i < boxMinima.Length; i++)
            {
                bool penetrating;
                if (!TryBoxPenetrates(position, radius, boxMinima[i], boxMaxima[i],
                    out penetrating)) return false;
                if (penetrating) unresolved = true;
            }

            float remainingLength;
            if (!TryLength(remaining, out remainingLength)) return false;
            if (remainingLength > Epsilon)
            {
                Vector3 slide;
                if (!TryScale(remaining, 1f / remainingLength, out slide)) return false;
                slideDirection = slide;
            }

            finalPosition = position;
            remainingDisplacement = remaining;
            stuck = unresolved;
            blocked = blockedByContact && !unresolved;
            return true;
        }

        /// <summary>
        /// Turns this step's recorded contacts into wall normals that point out
        /// of the obstacle, for a velocity response.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The sweep records what it hit, not what a velocity rule needs. Plane
        /// contacts already carry the free-space normal, but a box entered along
        /// a negative axis reports the inner face normal, because the slab sign
        /// rule is kept bit-identical to the Python reference (see
        /// Docs/Design/BallLayers/Sweep/README.md). A velocity cannot be
        /// projected with that inward sign, so this function re-orients each box
        /// normal to the side of the unexpanded box the contact centre is on:
        /// the recorded centre is the sphere centre after separation, which sits
        /// on the outer side of the face the sphere approached (within the
        /// separation epsilon), so the side test is unambiguous except within
        /// Epsilon of the box's mid-plane on that axis, where the reported sign
        /// is kept.
        /// </para>
        /// <para>
        /// It re-runs no sweep and moves nothing; the contact arrays and the
        /// obstacle arrays are the ones passed to TrySweepSphere. wallNormals is
        /// caller-owned and reused, so the call allocates nothing. The write is
        /// bounded by the shorter of contactCount and wallNormals.Length, and
        /// wallNormalCount is how many entries were written (a short buffer
        /// truncates the list, like the contact log itself). Duplicate normals
        /// are kept rather than de-duplicated: the velocity projection is
        /// idempotent for a repeated normal.
        /// </para>
        /// <para>
        /// Returns false for a null or too-short array, a negative or too-large
        /// contactCount, an invalid plane or box, an obstacle index outside the
        /// plane-then-box range, or a non-finite contact value. Every out value
        /// is then zero and nothing is written. The sweep is called with walls
        /// and boxes only (the analytic ground plane is fed to
        /// GroundContactMath, not to the sweep), so a floor-like normal can
        /// appear here only if the caller registered the floor as an obstacle.
        /// </para>
        /// </remarks>
        public static bool TryCollectWallContactNormals(
            Vector3[] contactCenters,
            Vector3[] contactNormals,
            int[] contactObstacleIndices,
            int contactCount,
            Vector3[] planeNormals,
            float[] planeOffsets,
            Vector3[] boxMinima,
            Vector3[] boxMaxima,
            Vector3[] wallNormals,
            out int wallNormalCount)
        {
            wallNormalCount = 0;
            if (contactCenters == null || contactNormals == null) return false;
            if (contactObstacleIndices == null || wallNormals == null) return false;
            if (contactCount < 0) return false;
            if (contactCount > contactCenters.Length) return false;
            if (contactCount > contactNormals.Length) return false;
            if (contactCount > contactObstacleIndices.Length) return false;
            if (planeNormals == null || planeOffsets == null) return false;
            if (planeNormals.Length != planeOffsets.Length) return false;
            if (boxMinima == null || boxMaxima == null) return false;
            if (boxMinima.Length != boxMaxima.Length) return false;

            int planeCount = planeNormals.Length;
            int obstacleCount = planeCount + boxMinima.Length;
            int limit = Mathf.Min(contactCount, wallNormals.Length);
            for (int i = 0; i < limit; i++)
            {
                Vector3 center = contactCenters[i];
                Vector3 rawNormal = contactNormals[i];
                int obstacleIndex = contactObstacleIndices[i];
                if (!KatamariMath.IsFiniteVector(center)) return false;
                if (!KatamariMath.IsFiniteVector(rawNormal)) return false;
                if (obstacleIndex < 0 || obstacleIndex >= obstacleCount) return false;

                Vector3 outward;
                if (!TryOrientWallNormal(center, obstacleIndex, rawNormal,
                    planeNormals, planeOffsets, boxMinima, boxMaxima, out outward)) return false;
                wallNormals[i] = outward;
            }
            wallNormalCount = limit;
            return true;
        }

        /// <summary>
        /// Removes the part of a planar velocity that points into each wall
        /// normal, in the given order, keeping the tangential part.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the wall response the sweep's contact data exists for:
        /// v' = v - min(0, v . n) n per normal n pointing out of the obstacle,
        /// applied in array order (the sweep's contact order, which is stable:
        /// time of impact, then obstacle index). Removing only the inward part
        /// leaves the tangential velocity untouched, which is what makes a ball
        /// slide along a wall instead of stopping dead against it. For a corner
        /// the second normal is applied to the already-corrected velocity, so
        /// both inward components are removed; a repeated normal is idempotent.
        /// </para>
        /// <para>
        /// The result is planar: only the XZ components are changed and
        /// correctedVelocity.y is always 0. The normal is normalised and then
        /// projected onto XZ, so a wall that is not perfectly vertical still
        /// leaves no horizontal motion into it. A normal with no meaningful
        /// horizontal part (a box top or a ceiling contact, |n_xz| &lt;= 1e-6)
        /// is ignored: a planar velocity has no component along it, and ground
        /// support is the layer that owns vertical contact. A non-finite
        /// velocity, a non-finite normal, a zero-length (degenerate) normal, a
        /// null array or a count outside the array is refused, and every out
        /// value is then zero.
        /// </para>
        /// <para>
        /// Deliberately no friction and no restitution: the original game's wall
        /// response is not confirmed, and removing the inward component is the
        /// simplest rule that stops the ball sticking to a wall. The choice is
        /// marked PROVISIONAL in Docs/Design/BallMotor/README.md and is expected
        /// to be revisited with play-test evidence.
        /// </para>
        /// </remarks>
        public static bool TryRemoveWallNormalVelocity(
            Vector3 velocity,
            Vector3[] wallNormals,
            int wallNormalCount,
            out Vector3 correctedVelocity)
        {
            correctedVelocity = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(velocity)) return false;
            if (wallNormals == null) return false;
            if (wallNormalCount < 0 || wallNormalCount > wallNormals.Length) return false;

            float velocityX = velocity.x;
            float velocityZ = velocity.z;
            for (int i = 0; i < wallNormalCount; i++)
            {
                Vector3 unitNormal;
                float length;
                if (!KatamariMath.TryNormalize(wallNormals[i], out unitNormal, out length)) return false;

                // A vertical normal (box top, ceiling) cannot remove planar
                // motion; skipping it is not a refusal, because the contact is
                // real and the ground/support path owns that direction.
                float horizontalLength = Mathf.Sqrt(
                    unitNormal.x * unitNormal.x + unitNormal.z * unitNormal.z);
                if (horizontalLength <= PlanarNormalEpsilon) continue;

                float normalX = unitNormal.x / horizontalLength;
                float normalZ = unitNormal.z / horizontalLength;
                float inward = velocityX * normalX + velocityZ * normalZ;
                if (inward >= 0f) continue;

                velocityX = velocityX - inward * normalX;
                velocityZ = velocityZ - inward * normalZ;
                if (!KatamariMath.IsFiniteFloat(velocityX)) return false;
                if (!KatamariMath.IsFiniteFloat(velocityZ)) return false;
            }

            correctedVelocity = new Vector3(velocityX, 0f, velocityZ);
            return true;
        }

        private static bool TryOrientWallNormal(
            Vector3 contactCenter,
            int obstacleIndex,
            Vector3 reportedNormal,
            Vector3[] planeNormals,
            float[] planeOffsets,
            Vector3[] boxMinima,
            Vector3[] boxMaxima,
            out Vector3 outwardNormal)
        {
            outwardNormal = Vector3.zero;
            int planeCount = planeNormals.Length;
            if (obstacleIndex < planeCount)
            {
                Vector3 unitNormal;
                float offset;
                if (!TryValidatePlane(planeNormals[obstacleIndex], planeOffsets[obstacleIndex],
                    out unitNormal, out offset)) return false;
                outwardNormal = unitNormal;
                return true;
            }

            int boxIndex = obstacleIndex - planeCount;
            if (boxIndex < 0 || boxIndex >= boxMinima.Length) return false;
            Vector3 minimum = boxMinima[boxIndex];
            Vector3 maximum = boxMaxima[boxIndex];
            if (!KatamariMath.IsFiniteVector(minimum)) return false;
            if (!KatamariMath.IsFiniteVector(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z) return false;

            float absoluteX = Mathf.Abs(reportedNormal.x);
            float absoluteY = Mathf.Abs(reportedNormal.y);
            float absoluteZ = Mathf.Abs(reportedNormal.z);
            int axis = 0;
            float best = absoluteX;
            if (absoluteY > best)
            {
                axis = 1;
                best = absoluteY;
            }
            if (absoluteZ > best)
            {
                axis = 2;
                best = absoluteZ;
            }
            if (best <= Epsilon) return false;

            float reportedSign = Component(reportedNormal, axis) > 0f ? 1f : -1f;
            float centerCoordinate = Component(contactCenter, axis);
            float boxCenter = (Component(minimum, axis) + Component(maximum, axis)) * 0.5f;
            float sign = reportedSign;
            if (centerCoordinate > boxCenter + Epsilon) sign = 1f;
            else if (centerCoordinate < boxCenter - Epsilon) sign = -1f;
            outwardNormal = AxisNormal(axis, sign);
            return true;
        }

        private static bool RecordContact(
            Vector3 position,
            Vector3 normal,
            float radius,
            float contactTime,
            int obstacleIndex,
            float penetration,
            int writeIndex,
            Vector3[] contactCenters,
            Vector3[] contactPoints,
            Vector3[] contactNormals,
            float[] contactTimes,
            int[] contactObstacleIndices,
            float[] contactPenetrationDepths)
        {
            Vector3 pointOffset;
            if (!TryScale(normal, radius, out pointOffset)) return false;
            Vector3 point;
            if (!TrySubtract(position, pointOffset, out point)) return false;
            contactCenters[writeIndex] = position;
            contactPoints[writeIndex] = point;
            contactNormals[writeIndex] = normal;
            contactTimes[writeIndex] = contactTime;
            contactObstacleIndices[writeIndex] = obstacleIndex;
            contactPenetrationDepths[writeIndex] = penetration;
            return true;
        }

        private static bool TryFindEarliestHit(
            Vector3 position,
            Vector3 direction,
            float radius,
            Vector3[] planeNormals,
            float[] planeOffsets,
            Vector3[] boxMinima,
            Vector3[] boxMaxima,
            out bool found,
            out float earliestTime)
        {
            found = false;
            earliestTime = 0f;
            float best = float.MaxValue;
            int obstacleCount = planeNormals.Length + boxMinima.Length;
            for (int index = 0; index < obstacleCount; index++)
            {
                bool hit;
                float time;
                Vector3 normal;
                float penetration;
                if (!TryHitObstacle(position, direction, radius,
                    planeNormals, planeOffsets, boxMinima, boxMaxima, index,
                    out hit, out time, out normal, out penetration)) return false;
                if (hit && time < best)
                {
                    best = time;
                    found = true;
                }
            }
            if (found) earliestTime = best;
            return true;
        }

        private static bool TryHitObstacle(
            Vector3 position,
            Vector3 direction,
            float radius,
            Vector3[] planeNormals,
            float[] planeOffsets,
            Vector3[] boxMinima,
            Vector3[] boxMaxima,
            int obstacleIndex,
            out bool hit,
            out float time,
            out Vector3 normal,
            out float penetration)
        {
            hit = false;
            time = 0f;
            normal = Vector3.zero;
            penetration = 0f;
            int planeCount = planeNormals.Length;
            if (obstacleIndex < planeCount)
            {
                return TryPlaneHit(position, direction, radius,
                    planeNormals[obstacleIndex], planeOffsets[obstacleIndex],
                    out hit, out time, out normal, out penetration);
            }
            int boxIndex = obstacleIndex - planeCount;
            return TryBoxHit(position, direction, radius,
                boxMinima[boxIndex], boxMaxima[boxIndex],
                out hit, out time, out normal, out penetration);
        }

        private static bool TryValidatePlane(
            Vector3 rawNormal,
            float rawOffset,
            out Vector3 normal,
            out float offset)
        {
            normal = Vector3.zero;
            offset = 0f;
            if (!KatamariMath.IsFiniteFloat(rawOffset)) return false;
            float length;
            if (!KatamariMath.TryNormalize(rawNormal, out normal, out length)) return false;
            float normalizedOffset = rawOffset / length;
            if (!KatamariMath.IsFiniteFloat(normalizedOffset)) return false;
            offset = normalizedOffset;
            return true;
        }

        private static bool TryExpandBox(
            Vector3 minimum,
            Vector3 maximum,
            float radius,
            out Vector3 low,
            out Vector3 high)
        {
            low = Vector3.zero;
            high = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(minimum)) return false;
            if (!KatamariMath.IsFiniteVector(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z) return false;
            Vector3 expandedLow = new Vector3(
                minimum.x - radius, minimum.y - radius, minimum.z - radius);
            Vector3 expandedHigh = new Vector3(
                maximum.x + radius, maximum.y + radius, maximum.z + radius);
            if (!KatamariMath.IsFiniteVector(expandedLow)) return false;
            if (!KatamariMath.IsFiniteVector(expandedHigh)) return false;
            low = expandedLow;
            high = expandedHigh;
            return true;
        }

        private static bool TryPlaneHit(
            Vector3 position,
            Vector3 direction,
            float radius,
            Vector3 rawNormal,
            float rawOffset,
            out bool hit,
            out float time,
            out Vector3 normal,
            out float penetration)
        {
            hit = false;
            time = 0f;
            normal = Vector3.zero;
            penetration = 0f;

            Vector3 unitNormal;
            float offset;
            if (!TryValidatePlane(rawNormal, rawOffset, out unitNormal, out offset)) return false;

            float normalCoordinate;
            if (!TryDot(position, unitNormal, out normalCoordinate)) return false;
            float distance = normalCoordinate - offset - radius;
            if (!KatamariMath.IsFiniteFloat(distance)) return false;
            float normalMotion;
            if (!TryDot(direction, unitNormal, out normalMotion)) return false;

            if (distance < -Epsilon)
            {
                hit = true;
                normal = unitNormal;
                penetration = -distance;
                return true;
            }
            if (normalMotion >= -Epsilon) return true;

            float candidate = -distance / normalMotion;
            if (!KatamariMath.IsFiniteFloat(candidate)) return false;
            if (candidate < -Epsilon || candidate > 1f + Epsilon) return true;

            hit = true;
            time = Mathf.Clamp01(candidate);
            normal = unitNormal;
            return true;
        }

        private static bool TryBoxHit(
            Vector3 position,
            Vector3 direction,
            float radius,
            Vector3 minimum,
            Vector3 maximum,
            out bool hit,
            out float time,
            out Vector3 normal,
            out float penetration)
        {
            hit = false;
            time = 0f;
            normal = Vector3.zero;
            penetration = 0f;

            Vector3 low;
            Vector3 high;
            if (!TryExpandBox(minimum, maximum, radius, out low, out high)) return false;

            bool inside =
                position.x >= low.x - Epsilon && position.x <= high.x + Epsilon &&
                position.y >= low.y - Epsilon && position.y <= high.y + Epsilon &&
                position.z >= low.z - Epsilon && position.z <= high.z + Epsilon;

            if (inside)
            {
                // Nearest free face of the expanded AABB. Ties choose the lowest
                // axis and then the negative face, so recovery is deterministic.
                float bestDepth = float.MaxValue;
                int bestAxis = 0;
                float bestSign = -1f;
                for (int axis = 0; axis < 3; axis++)
                {
                    float coordinate = Component(position, axis);
                    float lowDepth = coordinate - Component(low, axis);
                    if (lowDepth < bestDepth)
                    {
                        bestDepth = lowDepth;
                        bestAxis = axis;
                        bestSign = -1f;
                    }
                    float highDepth = Component(high, axis) - coordinate;
                    if (highDepth < bestDepth)
                    {
                        bestDepth = highDepth;
                        bestAxis = axis;
                        bestSign = 1f;
                    }
                }
                normal = AxisNormal(bestAxis, bestSign);
                if (bestDepth > Epsilon)
                {
                    hit = true;
                    penetration = bestDepth;
                    return true;
                }
                // At the boundary only inward movement is a collision; tangent
                // or outward motion is free and must not be reported as stuck.
                float boundaryMotion;
                if (!TryDot(direction, normal, out boundaryMotion)) return false;
                if (boundaryMotion < -Epsilon) hit = true;
                return true;
            }

            float enter = float.MinValue;
            float leave = float.MaxValue;
            Vector3 enterNormal = Vector3.zero;
            bool hasEnterNormal = false;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = Component(position, axis);
                float movement = Component(direction, axis);
                float lower = Component(low, axis);
                float upper = Component(high, axis);
                if (Mathf.Abs(movement) <= Epsilon)
                {
                    if (origin < lower || origin > upper) return true;
                    continue;
                }
                float first = (lower - origin) / movement;
                float second = (upper - origin) / movement;
                if (!KatamariMath.IsFiniteFloat(first)) return false;
                if (!KatamariMath.IsFiniteFloat(second)) return false;
                // The entry normal follows the reference's rule: the sign starts
                // from the movement direction and flips when the two crossing
                // times swap. A negative-direction entry therefore reports the
                // inner face; the next step's embedded branch still separates
                // the sphere and re-projects. Kept identical to sweep.py.
                float sign = movement > 0f ? -1f : 1f;
                if (first > second)
                {
                    float swap = first;
                    first = second;
                    second = swap;
                    sign = -sign;
                }
                if (first > enter)
                {
                    enter = first;
                    enterNormal = AxisNormal(axis, sign);
                    hasEnterNormal = true;
                }
                if (second < leave) leave = second;
            }
            if (!hasEnterNormal || enter > leave + Epsilon || leave < 0f || enter > 1f) return true;

            hit = true;
            time = Mathf.Clamp01(enter);
            normal = enterNormal;
            return true;
        }

        private static bool TryPlanePenetrates(
            Vector3 position,
            float radius,
            Vector3 rawNormal,
            float rawOffset,
            out bool penetrating)
        {
            penetrating = false;
            Vector3 normal;
            float offset;
            if (!TryValidatePlane(rawNormal, rawOffset, out normal, out offset)) return false;
            float normalCoordinate;
            if (!TryDot(position, normal, out normalCoordinate)) return false;
            penetrating = normalCoordinate - offset < radius - Epsilon;
            return true;
        }

        private static bool TryBoxPenetrates(
            Vector3 position,
            float radius,
            Vector3 minimum,
            Vector3 maximum,
            out bool penetrating)
        {
            penetrating = false;
            Vector3 low;
            Vector3 high;
            if (!TryExpandBox(minimum, maximum, radius, out low, out high)) return false;
            penetrating =
                position.x > low.x + Epsilon && position.x < high.x - Epsilon &&
                position.y > low.y + Epsilon && position.y < high.y - Epsilon &&
                position.z > low.z + Epsilon && position.z < high.z - Epsilon;
            return true;
        }

        private static bool TryProject(Vector3 residual, Vector3 normal, out Vector3 projected)
        {
            projected = residual;
            float inward;
            if (!TryDot(residual, normal, out inward)) return false;
            if (inward >= 0f) return true;
            Vector3 offset;
            if (!TryScale(normal, inward, out offset)) return false;
            return TrySubtract(residual, offset, out projected);
        }

        private static float Component(Vector3 value, int axis)
        {
            if (axis == 0) return value.x;
            if (axis == 1) return value.y;
            return value.z;
        }

        private static Vector3 AxisNormal(int axis, float sign)
        {
            if (axis == 0) return new Vector3(sign, 0f, 0f);
            if (axis == 1) return new Vector3(0f, sign, 0f);
            return new Vector3(0f, 0f, sign);
        }

        private static bool TryAdd(Vector3 a, Vector3 b, out Vector3 sum)
        {
            Vector3 candidate = new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
            sum = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(candidate)) return false;
            sum = candidate;
            return true;
        }

        private static bool TrySubtract(Vector3 a, Vector3 b, out Vector3 difference)
        {
            Vector3 candidate = new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
            difference = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(candidate)) return false;
            difference = candidate;
            return true;
        }

        private static bool TryScale(Vector3 value, float scalar, out Vector3 scaled)
        {
            Vector3 candidate = new Vector3(value.x * scalar, value.y * scalar, value.z * scalar);
            scaled = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(candidate)) return false;
            scaled = candidate;
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

        private static bool TryLength(Vector3 value, out float length)
        {
            length = 0f;
            if (!KatamariMath.IsFiniteVector(value)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x),
                Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
            if (scale <= 0f) return true;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            float magnitude = Mathf.Sqrt(x * x + y * y + z * z);
            if (!KatamariMath.IsFiniteFloat(magnitude) || magnitude <= 0f) return false;
            float candidate = scale * magnitude;
            if (!KatamariMath.IsFiniteFloat(candidate)) return false;
            length = candidate;
            return true;
        }
    }
}
