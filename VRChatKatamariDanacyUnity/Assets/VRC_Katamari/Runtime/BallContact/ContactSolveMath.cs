using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// One fixed step of the ball contact v2 solver: substep, move, push out, classify,
    /// choose support and wall, run the climb channel or the solid response, integrate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UdonSharp has no structs and no state classes, so the "world" is the caller's
    /// parallel arrays (one ground half-space, a boundary-plane list, the near OBB set) and
    /// the per-ball state is passed as <c>ref</c> scalars, exactly the fields the design
    /// lists in section 3.4. The contact record is likewise eight parallel arrays the caller
    /// owns; this file never allocates, never reads scene state and has no static state.
    /// The OBB arrays are the per-step near set (at most
    /// <see cref="ContactTuning.MaxNearObbCount"/>), not the 256-entry static table.
    /// </para>
    /// <para>
    /// The step is a direct port of the reference model's <c>step()</c>, same order and same
    /// constants. The substep count is fixed from the pre-step velocity plus the largest
    /// velocity a contact can add inside one step (gravity or the climb ramp), so the
    /// no-tunnel bound covers the whole step and not just its first substep. Each substep
    /// moves with the CURRENT velocity, pushes out of the deepest contact three times
    /// (re-querying), then picks support and wall INDEPENDENTLY: being supported on the
    /// floor while pushing a wall is the normal start of a climb, so a single dominant
    /// contact would never let one start.
    /// </para>
    /// <para>
    /// Velocity response in one sentence per class: a RIDE contact removes nothing (a rolling
    /// contact is not a collision, which is what keeps a ramp exit from dumping speed); a
    /// CLIMB contact either has the climb channel own the velocity (along the uphill
    /// tangent, rate-limited) or, when the ball is not climbing, has only its horizontal
    /// inward component removed; a NONE contact (ceiling/underside) removes the full
    /// three-dimensional inward component; a contact with depth &lt;= 0 is skin-only and is
    /// never treated as touching. A climb-class contact that is not clearly steeper than the
    /// support is exempt from both the climb channel and that clip, because it is the
    /// surface turning over (the 15 deg rule).
    /// </para>
    /// <para>
    /// Failure is closed. False means unusable input, a degenerate direction the model would
    /// raise on, or a contact log smaller than the contact set; in every case the state refs
    /// are left untouched and the out values stay at their neutral values, so the motor can
    /// keep its previous position (section 2.8). The contact buffers are undefined on false.
    /// </para>
    /// </remarks>
    public static class ContactSolveMath
    {
        // The model's per-query contact log is unbounded (a Python list); here the log is a
        // fixed array and running past it fails the query instead of dropping geometry.
        // The design's capacity is ContactTuning.ContactCapacity.

        /// <summary>
        /// Reject an unresolved candidate as one motion transaction: keep the previous
        /// centre and discard both velocities, never bank speed for a move not taken.
        /// The caller supplies its residual acceptance limit (metres).
        /// </summary>
        public static bool RejectUnresolvedMotion(Vector3 previousCenter, float residual,
            float rejectAboveM, ref Vector3 center, ref Vector3 planarVelocity,
            ref float verticalVelocity)
        {
            if (KatamariMath.IsFiniteFloat(residual) && residual <= rejectAboveM) return false;
            center = previousCenter;
            planarVelocity = Vector3.zero;
            verticalVelocity = 0f;
            return true;
        }

        /// <summary>
        /// N = clamp(ceil(|d| / (kappa * r)), 1, cap): the substep count that cannot tunnel
        /// through a convex solid.
        /// </summary>
        /// <remarks>
        /// dist(centre, solid) is 1-Lipschitz and zero inside a convex solid, so the set
        /// where it is at most r is at least 2r long along any straight path that enters the
        /// solid: a sample spacing of at most r always lands inside it, and kappa = 0.5
        /// gives a 4x margin. The cap is the cost bound, and it is why the fall
        /// displacement is clamped to cap * kappa * r. A radius that is not positive gives 1
        /// (the model returns 1 rather than raising).
        /// </remarks>
        public static int MaxSubSteps(Vector3 displacement, float radius,
            float kappa = ContactTuning.KappaSubstep, int cap = ContactTuning.MaxSubsteps)
        {
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return 1;
            if (!KatamariMath.IsFiniteFloat(kappa) || kappa <= 0f) return 1;
            if (cap < 1) return 1;
            if (!KatamariMath.IsFiniteVector(displacement)) return 1;
            float length = Mathf.Sqrt(displacement.x * displacement.x
                + displacement.y * displacement.y + displacement.z * displacement.z);
            if (!KatamariMath.IsFiniteFloat(length)) return 1;
            float ratio = length / (kappa * radius) - 1e-9f;
            if (!KatamariMath.IsFiniteFloat(ratio)) return cap;
            // Compare before converting: a huge ratio would overflow the int cast, and the
            // cap is the answer in exactly that case.
            if (ratio >= cap) return cap;
            if (ratio <= 1f) return 1;
            return Mathf.CeilToInt(ratio);
        }

        /// <summary>
        /// Collects every solid contact around the sphere into the caller's parallel
        /// contact arrays: ground first, then the boundary planes, then the near OBBs.
        /// </summary>
        /// <remarks>
        /// False means unusable input or a contact set larger than the caller's log; the log
        /// then holds the entries written so far and <paramref name="contactCount"/> says how
        /// many, so an overflow is visible instead of silently losing geometry. Boundary
        /// planes use surface id 1000 + index and the ground uses id 0, both with the
        /// blocking role, exactly like the model. An OBB whose description is invalid
        /// contributes no contact: the near window refuses such a table entry before the
        /// solver ever sees it, and the arena bake refuses it before that.
        /// </remarks>
        public static bool TryCollectContacts(
            bool hasGround,
            Vector3 groundNormal,
            float groundOffset,
            Vector3[] planeNormals,
            float[] planeOffsets,
            int planeCount,
            Vector3[] obbCenters,
            Vector3[] obbAxisX,
            Vector3[] obbAxisY,
            Vector3[] obbAxisZ,
            Vector3[] obbHalfExtents,
            int[] obbSurfaceIds,
            int[] obbRoles,
            int obbCount,
            Vector3 sphereCenter,
            float radius,
            Vector3[] contactNormals,
            Vector3[] contactPoints,
            float[] contactDepths,
            float[] contactAngles,
            int[] contactSurfaceIds,
            int[] contactRoles,
            int[] contactFeatures,
            int[] contactClasses,
            int capacity,
            out int contactCount,
            float skin = ContactTuning.ContactSkinM,
            int[] obbKinds = null,
            float[] obbRadii = null,
            float[] obbHalfHeights = null,
            int[] obbAxes = null,
            int[] hullStarts = null,
            int[] hullCounts = null,
            int[] faceStarts = null,
            int[] faceCounts = null,
            Vector3[] hullPoints = null,
            Vector3[] faceNormals = null,
            float[] faceOffsets = null,
            int[] faceA = null,
            int[] faceB = null,
            int[] faceC = null,
            float[] contactTopY = null,
            int[] terrainCandidates = null,
            int terrainCandidateCount = 0,
            float[] terrainTriangleMinX = null,
            float[] terrainTriangleMaxX = null,
            float[] terrainTriangleMinZ = null,
            float[] terrainTriangleMaxZ = null,
            int[] terrainTriangleSurfaceId = null,
            Vector3[] terrainVertices = null,
            int[] terrainIndices = null,
            int terrainTriangleCount = 0,
            Vector3[] terrainChunkMin = null,
            Vector3[] terrainChunkMax = null,
            int[] terrainChunkStarts = null,
            int[] terrainChunkTriangleCounts = null,
            int[] terrainChunkZoneIds = null,
            int terrainChunkCount = 0)
        {
            contactCount = 0;
            if (!KatamariMath.IsFiniteVector(sphereCenter)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(skin) || skin < 0f) return false;
            if (capacity < 0) return false;
            if (contactNormals == null || contactPoints == null || contactDepths == null ||
                contactAngles == null || contactSurfaceIds == null || contactRoles == null ||
                contactFeatures == null || contactClasses == null) return false;
            int limit = capacity;
            if (limit > contactNormals.Length) limit = contactNormals.Length;
            if (limit > contactPoints.Length) limit = contactPoints.Length;
            if (limit > contactDepths.Length) limit = contactDepths.Length;
            if (limit > contactAngles.Length) limit = contactAngles.Length;
            if (limit > contactSurfaceIds.Length) limit = contactSurfaceIds.Length;
            if (limit > contactRoles.Length) limit = contactRoles.Length;
            if (limit > contactFeatures.Length) limit = contactFeatures.Length;
            if (limit > contactClasses.Length) limit = contactClasses.Length;
            if (planeNormals == null || planeOffsets == null) return false;
            if (obbCenters == null || obbAxisX == null || obbAxisY == null ||
                obbAxisZ == null || obbHalfExtents == null || obbSurfaceIds == null ||
                obbRoles == null) return false;
            if (planeCount < 0 || planeCount > planeNormals.Length ||
                planeCount > planeOffsets.Length) return false;
            if (obbCount < 0 || obbCount > obbCenters.Length || obbCount > obbAxisX.Length ||
                obbCount > obbAxisY.Length || obbCount > obbAxisZ.Length ||
                obbCount > obbHalfExtents.Length || obbCount > obbSurfaceIds.Length ||
                obbCount > obbRoles.Length) return false;
            if (obbKinds != null)
            {
                if (obbRadii == null || obbHalfHeights == null || obbAxes == null ||
                    hullStarts == null || hullCounts == null || faceStarts == null ||
                    faceCounts == null || hullPoints == null || faceNormals == null ||
                    faceOffsets == null || faceA == null || faceB == null || faceC == null)
                    return false;
                if (obbCount > obbKinds.Length || obbCount > obbRadii.Length ||
                    obbCount > obbHalfHeights.Length || obbCount > obbAxes.Length ||
                    obbCount > hullStarts.Length || obbCount > hullCounts.Length ||
                    obbCount > faceStarts.Length || obbCount > faceCounts.Length)
                    return false;
            }

            if (hasGround)
            {
                if (!KatamariMath.IsFiniteVector(groundNormal)) return false;
                if (!KatamariMath.IsFiniteFloat(groundOffset)) return false;
                Vector3 normal;
                Vector3 point;
                float depth;
                float angleDeg;
                int contactClass;
                if (ObbContactMath.TryPlaneContact(groundNormal, groundOffset, sphereCenter,
                    radius, out normal, out point, out depth, out angleDeg, out contactClass,
                    skin))
                {
                    if (contactCount >= limit) return false;
                    contactNormals[contactCount] = normal;
                    contactPoints[contactCount] = point;
                    contactDepths[contactCount] = depth;
                    contactAngles[contactCount] = angleDeg;
                    contactSurfaceIds[contactCount] = ContactCodes.SurfaceIdGround;
                    contactRoles[contactCount] = ContactCodes.RoleBlocking;
                    contactFeatures[contactCount] = ContactCodes.FeatureFace;
                    contactClasses[contactCount] = contactClass;
                    // The ground plane has no top: a plane cannot be climbed over.
                    if (contactTopY != null) contactTopY[contactCount] = float.MaxValue;
                    contactCount++;
                }
            }

            for (int i = 0; i < planeCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(planeNormals[i])) return false;
                if (!KatamariMath.IsFiniteFloat(planeOffsets[i])) return false;
                Vector3 normal;
                Vector3 point;
                float depth;
                float angleDeg;
                int contactClass;
                if (!ObbContactMath.TryPlaneContact(planeNormals[i], planeOffsets[i],
                    sphereCenter, radius, out normal, out point, out depth, out angleDeg,
                    out contactClass, skin)) continue;
                if (contactCount >= limit) return false;
                contactNormals[contactCount] = normal;
                contactPoints[contactCount] = point;
                contactDepths[contactCount] = depth;
                contactAngles[contactCount] = angleDeg;
                contactSurfaceIds[contactCount] = ContactCodes.PlaneSurfaceId(i);
                contactRoles[contactCount] = ContactCodes.RoleBlocking;
                contactFeatures[contactCount] = ContactCodes.FeatureFace;
                contactClasses[contactCount] = contactClass;
                if (contactTopY != null) contactTopY[contactCount] = float.MaxValue;
                contactCount++;
            }

            for (int i = 0; i < obbCount; i++)
            {
                Vector3 normal;
                Vector3 point;
                float depth;
                int feature;
                float angleDeg;
                int contactClass;
                int kind = obbKinds == null ? ProxyContactMath.KindBox : obbKinds[i];
                if (kind == ProxyContactMath.KindBox)
                {
                    if (!ObbContactMath.TrySphereObbContact(obbCenters[i], obbAxisX[i],
                        obbAxisY[i], obbAxisZ[i], obbHalfExtents[i], sphereCenter, radius,
                        out normal, out point, out depth, out feature, out angleDeg,
                        out contactClass, skin)) continue;
                }
                else if (!ProxyContactMath.TrySphereProxyContact(kind, obbCenters[i],
                    obbAxisX[i], obbAxisY[i], obbAxisZ[i], obbHalfExtents[i], obbRadii[i],
                    obbHalfHeights[i], obbAxes[i], hullPoints, hullStarts[i], hullCounts[i],
                    faceNormals, faceOffsets, faceA, faceB, faceC, faceStarts[i],
                    faceCounts[i], sphereCenter, radius, out normal, out point, out depth,
                    out feature, out angleDeg, out contactClass, skin))
                {
                    continue;
                }

                if (contactCount >= limit) return false;
                contactNormals[contactCount] = normal;
                contactPoints[contactCount] = point;
                contactDepths[contactCount] = depth;
                contactAngles[contactCount] = angleDeg;
                contactSurfaceIds[contactCount] = obbSurfaceIds[i];
                contactRoles[contactCount] = obbRoles[i];
                contactFeatures[contactCount] = feature;
                contactClasses[contactCount] = contactClass;
                if (contactTopY != null)
                {
                    // A plain OBB is a wall with a top: "bottom reached the top" is the
                    // climb's successful exit (the same rule terrain uses). A proxy shape
                    // (prop hull/capsule/sphere) has no single top, so it never exits by
                    // ledge - the cap still ends it.
                    float top = float.MaxValue;
                    if (kind == ProxyContactMath.KindBox)
                    {
                        top = obbCenters[i].y +
                            Mathf.Abs(obbAxisX[i].y) * obbHalfExtents[i].x +
                            Mathf.Abs(obbAxisY[i].y) * obbHalfExtents[i].y +
                            Mathf.Abs(obbAxisZ[i].y) * obbHalfExtents[i].z;
                        if (!KatamariMath.IsFiniteFloat(top)) top = float.MaxValue;
                    }
                    contactTopY[contactCount] = top;
                }
                contactCount++;
            }

            // The terrain mesh is one more contact SOURCE, not a second solver: the
            // collector walks the motor's per-step candidate table and appends records in
            // exactly this format (TerrainMeshMath.TryAppendContacts). It runs last so the
            // analytic bodies keep their indices, and it fails closed on capacity overflow
            // like every other source.
            if (terrainVertices != null && terrainIndices != null)
            {
                int extended;
                if (!TerrainMeshMath.TryAppendContacts(sphereCenter, radius, skin,
                    terrainCandidates, terrainCandidateCount, terrainTriangleMinX,
                    terrainTriangleMaxX, terrainTriangleMinZ, terrainTriangleMaxZ,
                    terrainTriangleSurfaceId, terrainVertices, terrainIndices,
                    terrainTriangleCount, terrainChunkMin, terrainChunkMax,
                    terrainChunkStarts, terrainChunkTriangleCounts, terrainChunkZoneIds,
                    terrainChunkCount, contactNormals, contactPoints, contactDepths,
                    contactAngles, contactSurfaceIds, contactRoles, contactFeatures,
                    contactClasses, contactTopY, limit, contactCount, out extended))
                    return false;
                contactCount = extended;
            }
            return true;
        }

        /// <summary>
        /// Sequential push-out of the deepest contact, re-querying after each pass, and the
        /// largest penetration that remains.
        /// </summary>
        /// <remarks>
        /// This is the position authority (section 2.3): the deepest contact pushes the
        /// centre out along its own normal, then the contacts are re-queried, so a concave
        /// corner resolves one normal at a time instead of averaging them. Only a depth
        /// beyond the 0.5 mm slop moves anything, so a resting contact does not jitter, and
        /// the push never adds velocity. The push amount is also the natural step lift: it
        /// can never exceed this substep's travel, which is why the old one-shot step lift
        /// is gone.
        /// </remarks>
        public static bool TryPushOut(
            bool hasGround,
            Vector3 groundNormal,
            float groundOffset,
            Vector3[] planeNormals,
            float[] planeOffsets,
            int planeCount,
            Vector3[] obbCenters,
            Vector3[] obbAxisX,
            Vector3[] obbAxisY,
            Vector3[] obbAxisZ,
            Vector3[] obbHalfExtents,
            int[] obbSurfaceIds,
            int[] obbRoles,
            int obbCount,
            Vector3 center,
            float radius,
            Vector3[] contactNormals,
            Vector3[] contactPoints,
            float[] contactDepths,
            float[] contactAngles,
            int[] contactSurfaceIds,
            int[] contactRoles,
            int[] contactFeatures,
            int[] contactClasses,
            int capacity,
            out Vector3 correctedCenter,
            out int contactCount,
            out float residual,
            int iterations = ContactTuning.SolverIterations,
            float skin = ContactTuning.ContactSkinM,
            int[] obbKinds = null,
            float[] obbRadii = null,
            float[] obbHalfHeights = null,
            int[] obbAxes = null,
            int[] hullStarts = null,
            int[] hullCounts = null,
            int[] faceStarts = null,
            int[] faceCounts = null,
            Vector3[] hullPoints = null,
            Vector3[] faceNormals = null,
            float[] faceOffsets = null,
            int[] faceA = null,
            int[] faceB = null,
            int[] faceC = null,
            float[] contactTopY = null,
            int[] terrainCandidates = null,
            int terrainCandidateCount = 0,
            float[] terrainTriangleMinX = null,
            float[] terrainTriangleMaxX = null,
            float[] terrainTriangleMinZ = null,
            float[] terrainTriangleMaxZ = null,
            int[] terrainTriangleSurfaceId = null,
            Vector3[] terrainVertices = null,
            int[] terrainIndices = null,
            int terrainTriangleCount = 0,
            Vector3[] terrainChunkMin = null,
            Vector3[] terrainChunkMax = null,
            int[] terrainChunkStarts = null,
            int[] terrainChunkTriangleCounts = null,
            int[] terrainChunkZoneIds = null,
            int terrainChunkCount = 0)
        {
            correctedCenter = center;
            contactCount = 0;
            residual = 0f;
            if (iterations < 0) return false;
            Vector3 position = center;
            int count;
            if (!TryCollectContacts(hasGround, groundNormal, groundOffset, planeNormals,
                planeOffsets, planeCount, obbCenters, obbAxisX, obbAxisY, obbAxisZ,
                obbHalfExtents, obbSurfaceIds, obbRoles, obbCount, position, radius,
                contactNormals, contactPoints, contactDepths, contactAngles, contactSurfaceIds,
                contactRoles, contactFeatures, contactClasses, capacity, out count, skin,
                obbKinds, obbRadii, obbHalfHeights, obbAxes, hullStarts, hullCounts,
                faceStarts, faceCounts, hullPoints, faceNormals, faceOffsets, faceA, faceB,
                faceC, contactTopY, terrainCandidates, terrainCandidateCount,
                terrainTriangleMinX, terrainTriangleMaxX, terrainTriangleMinZ,
                terrainTriangleMaxZ, terrainTriangleSurfaceId, terrainVertices,
                terrainIndices, terrainTriangleCount, terrainChunkMin, terrainChunkMax,
                terrainChunkStarts, terrainChunkTriangleCounts, terrainChunkZoneIds,
                terrainChunkCount))
            {
                contactCount = count;
                return false;
            }

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                int deepest = -1;
                float deepestDepth = 0f;
                for (int i = 0; i < count; i++)
                {
                    float depth = contactDepths[i];
                    if (depth > ContactTuning.ContactSlopM && (deepest < 0 ||
                        depth > deepestDepth))
                    {
                        deepest = i;
                        deepestDepth = depth;
                    }
                }
                if (deepest < 0) break;
                position = new Vector3(
                    position.x + contactNormals[deepest].x * contactDepths[deepest],
                    position.y + contactNormals[deepest].y * contactDepths[deepest],
                    position.z + contactNormals[deepest].z * contactDepths[deepest]);
                if (!TryCollectContacts(hasGround, groundNormal, groundOffset, planeNormals,
                    planeOffsets, planeCount, obbCenters, obbAxisX, obbAxisY, obbAxisZ,
                    obbHalfExtents, obbSurfaceIds, obbRoles, obbCount, position, radius,
                    contactNormals, contactPoints, contactDepths, contactAngles,
                    contactSurfaceIds, contactRoles, contactFeatures, contactClasses, capacity,
                    out count, skin, obbKinds, obbRadii, obbHalfHeights, obbAxes, hullStarts,
                    hullCounts, faceStarts, faceCounts, hullPoints, faceNormals, faceOffsets,
                    faceA, faceB, faceC, contactTopY, terrainCandidates, terrainCandidateCount,
                    terrainTriangleMinX, terrainTriangleMaxX, terrainTriangleMinZ,
                    terrainTriangleMaxZ, terrainTriangleSurfaceId, terrainVertices,
                    terrainIndices, terrainTriangleCount, terrainChunkMin, terrainChunkMax,
                    terrainChunkStarts, terrainChunkTriangleCounts, terrainChunkZoneIds,
                    terrainChunkCount))
                {
                    contactCount = count;
                    return false;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (contactDepths[i] > residual) residual = contactDepths[i];
            }
            correctedCenter = position;
            contactCount = count;
            return true;
        }

        /// <summary>
        /// One fixed step: substeps, push-out, support/wall choice, climb or solid response,
        /// gravity, and the diagnostic report.
        /// </summary>
        /// <remarks>
        /// The state refs are the model's State fields, minus its two mirrors
        /// (<c>climbing</c> is <paramref name="climbActive"/> and its
        /// <c>climb_height</c> is the <paramref name="climbHeight"/> out), which are returned
        /// once instead of duplicated. <paramref name="driveDirection"/> is the unit planar
        /// direction the motion layer pushes in; <paramref name="driveSpeed"/> is its TARGET
        /// speed, not what the contacts left over - that is what makes "push hard to climb"
        /// work while the ball is standing still against a wall.
        /// <paramref name="gateSizeScale"/> is D / D0: the climb gate reads its speed divided by it (ClimbMath.GateSpeed), so full and half pushes behave the same at every ball size.
        /// <paramref name="driveAccel"/> exists only for the reference scenarios (it stands
        /// in for the motion layer); 0 leaves the planar velocity untouched.
        /// </remarks>
        public static bool TryStep(
            bool hasGround,
            Vector3 groundNormal,
            float groundOffset,
            Vector3[] planeNormals,
            float[] planeOffsets,
            int planeCount,
            Vector3[] obbCenters,
            Vector3[] obbAxisX,
            Vector3[] obbAxisY,
            Vector3[] obbAxisZ,
            Vector3[] obbHalfExtents,
            int[] obbSurfaceIds,
            int[] obbRoles,
            int obbCount,
            ref Vector3 center,
            float radius,
            ref Vector3 planarVelocity,
            ref float verticalVelocity,
            ref bool climbActive,
            ref float climbAnchorY,
            ref int climbSurfaceId,
            ref float climbCooldown,
            ref bool climbLocked,
            ref float climbAlongSpeed,
            ref float climbTopY,
            ref Vector3 climbNormal,
            ref Vector3 climbPoint,
            Vector3 driveDirection,
            float driveSpeed,
            float dt,
            float protrusionM,
            Vector3[] contactNormals,
            Vector3[] contactPoints,
            float[] contactDepths,
            float[] contactAngles,
            int[] contactSurfaceIds,
            int[] contactRoles,
            int[] contactFeatures,
            int[] contactClasses,
            int capacity,
            out int contactCount,
            out int dominantSurfaceId,
            out int dominantClass,
            out float climbHeight,
            out float climbSpeed,
            out int substepCount,
            out float residual,
            out bool airborne,
            out Vector3 reportNormal,
            float kappa = ContactTuning.KappaSubstep,
            int maxSubstepCap = ContactTuning.MaxSubsteps,
            float driveAccel = 0f,
            float skin = ContactTuning.ContactSkinM,
            int[] obbKinds = null,
            float[] obbRadii = null,
            float[] obbHalfHeights = null,
            int[] obbAxes = null,
            int[] hullStarts = null,
            int[] hullCounts = null,
            int[] faceStarts = null,
            int[] faceCounts = null,
            Vector3[] hullPoints = null,
            Vector3[] faceNormals = null,
            float[] faceOffsets = null,
            int[] faceA = null,
            int[] faceB = null,
            int[] faceC = null,
            float[] contactTopY = null,
            int[] terrainCandidates = null,
            int terrainCandidateCount = 0,
            float[] terrainTriangleMinX = null,
            float[] terrainTriangleMaxX = null,
            float[] terrainTriangleMinZ = null,
            float[] terrainTriangleMaxZ = null,
            int[] terrainTriangleSurfaceId = null,
            Vector3[] terrainVertices = null,
            int[] terrainIndices = null,
            int terrainTriangleCount = 0,
            Vector3[] terrainChunkMin = null,
            Vector3[] terrainChunkMax = null,
            int[] terrainChunkStarts = null,
            int[] terrainChunkTriangleCounts = null,
            int[] terrainChunkZoneIds = null,
            int terrainChunkCount = 0,
            float gateSizeScale = 1f)
        {
            contactCount = 0;
            dominantSurfaceId = -1;
            dominantClass = ContactCodes.ClassNone;
            climbHeight = 0f;
            climbSpeed = 0f;
            substepCount = 1;
            residual = 0f;
            airborne = true;
            reportNormal = new Vector3(0f, 1f, 0f);

            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(dt) || dt <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(driveSpeed) || driveSpeed < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(protrusionM)) return false;
            if (!KatamariMath.IsFiniteFloat(driveAccel) || driveAccel < 0f) return false;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!KatamariMath.IsFiniteVector(planarVelocity)) return false;
            if (!KatamariMath.IsFiniteFloat(verticalVelocity)) return false;
            if (!KatamariMath.IsFiniteVector(driveDirection)) return false;
            Vector3 drive;
            float driveLength;
            if (!KatamariMath.TryNormalize(driveDirection, out drive, out driveLength))
                return false;
            if (!KatamariMath.IsFiniteFloat(climbAnchorY)) return false;
            if (!KatamariMath.IsFiniteFloat(climbCooldown)) return false;
            if (!KatamariMath.IsFiniteFloat(climbAlongSpeed)) return false;
            if (!KatamariMath.IsFiniteFloat(climbTopY)) return false;
            if (!KatamariMath.IsFiniteVector(climbNormal)) return false;
            if (!KatamariMath.IsFiniteVector(climbPoint)) return false;

            Vector3 position = center;
            Vector3 vPlanar = planarVelocity;
            float vY = verticalVelocity;
            bool active = climbActive;
            float anchorY = climbAnchorY;
            int surfaceId = climbSurfaceId;
            float cooldown = Mathf.Max(0f, climbCooldown - dt);
            bool locked = climbLocked;
            float along = climbAlongSpeed;
            float topY = climbTopY;
            Vector3 surfaceNormal = climbNormal;
            Vector3 surfacePoint = climbPoint;
            float hCap = ClimbMath.ClimbHeightCap(radius, protrusionM);

            // The substep count covers the pre-step velocity PLUS the largest velocity a
            // contact can add inside the step, so the no-tunnel bound holds for the whole
            // step instead of only its first substep.
            Vector3 displacement = new Vector3(vPlanar.x * dt, vY * dt, vPlanar.z * dt);
            float margin = (ContactTuning.Gravity + ContactTuning.ClimbRateLimit) * dt * dt;
            Vector3 padded = new Vector3(displacement.x + margin, displacement.y + margin,
                displacement.z + margin);
            int substeps = MaxSubSteps(padded, radius, kappa, maxSubstepCap);

            int count = 0;
            float lastResidual = 0f;
            int supportIndex = -1;
            int wallIndex = -1;
            float climbSpeedValue = 0f;

            for (int substep = 0; substep < substeps; substep++)
            {
                // The substep moves with the CURRENT velocity, so a velocity the contact
                // adds (climb, gravity) acts in this step instead of one step later.
                position = new Vector3(
                    position.x + vPlanar.x * dt / substeps,
                    position.y + vY * dt / substeps,
                    position.z + vPlanar.z * dt / substeps);
                Vector3 pushedCenter;
                int pushedCount;
                float pushedResidual;
                if (!TryPushOut(hasGround, groundNormal, groundOffset, planeNormals,
                    planeOffsets, planeCount, obbCenters, obbAxisX, obbAxisY, obbAxisZ,
                    obbHalfExtents, obbSurfaceIds, obbRoles, obbCount, position, radius,
                    contactNormals, contactPoints, contactDepths, contactAngles,
                    contactSurfaceIds, contactRoles, contactFeatures, contactClasses, capacity,
                    out pushedCenter, out pushedCount, out pushedResidual,
                    ContactTuning.SolverIterations, skin, obbKinds, obbRadii, obbHalfHeights,
                    obbAxes, hullStarts, hullCounts, faceStarts, faceCounts, hullPoints,
                    faceNormals, faceOffsets, faceA, faceB, faceC, contactTopY,
                    terrainCandidates, terrainCandidateCount, terrainTriangleMinX,
                    terrainTriangleMaxX, terrainTriangleMinZ, terrainTriangleMaxZ,
                    terrainTriangleSurfaceId, terrainVertices, terrainIndices,
                    terrainTriangleCount, terrainChunkMin, terrainChunkMax,
                    terrainChunkStarts, terrainChunkTriangleCounts, terrainChunkZoneIds,
                    terrainChunkCount))
                    return false;
                position = pushedCenter;
                count = pushedCount;
                lastResidual = pushedResidual;

                supportIndex = ContactClassificationMath.SelectSupportContact(contactNormals,
                    contactDepths, contactClasses, count);
                wallIndex = ContactClassificationMath.SelectClimbContact(contactNormals,
                    contactClasses, count, drive);

                // Selection hysteresis (TERRAIN_CLIMB 6.6). The drive is the VELOCITY
                // direction, and in a narrow V the solver strips each face's normal in
                // turn, so the velocity swings and the face "the drive pushes into most"
                // flips between two faces. While a climb is alive the face it started on
                // keeps being the face the solver reads (normal, angle, top), for as long
                // as that plane is touched and the push into it still holds the climb
                // (pushIn >= p_min): a push turning TOWARDS the other face of a corner
                // hands over to it at once instead of releasing the climb. Once the plane
                // is lost, or the push has left it, the ordinary selection stands.
                if (active && surfaceId >= 0 && wallIndex >= 0)
                {
                    int keptFace = ContactClassificationMath.FindClimbContactOnPlane(
                        contactNormals, contactPoints, contactDepths, contactClasses, count,
                        surfaceNormal, surfacePoint);
                    if (keptFace >= 0 && keptFace != wallIndex)
                    {
                        float keptPushIn;
                        if (TryPushIn(contactNormals[keptFace], drive, out keptPushIn) &&
                            keptPushIn >= ContactTuning.PMin) wallIndex = keptFace;
                    }
                }

                float supportTheta = 0f;
                if (supportIndex >= 0) supportTheta = contactAngles[supportIndex];
                // A contact that is only a little steeper than the surface the ball rolls
                // on is that surface curving over: rolled over by the push-out, never
                // climbed and never allowed to clip the velocity.
                if (wallIndex >= 0 && supportIndex >= 0 &&
                    !ContactClassificationMath.IsClimbSteeperThanSupport(
                        contactAngles[wallIndex], supportTheta)) wallIndex = -1;

                // While a climb is alive its surface is REMEMBERED: above a low face the
                // contact ends before the ball's bottom clears the top, and the climb has to
                // finish that last radius (TERRAIN_CLIMB 6.3). A dead hug ends the climb.
                if (active && wallIndex >= 0)
                {
                    surfaceNormal = contactNormals[wallIndex];
                    surfacePoint = contactPoints[wallIndex];
                    topY = ContactTopAt(contactTopY, wallIndex);
                }
                Vector3 pushNormal = wallIndex >= 0 ? contactNormals[wallIndex] : surfaceNormal;
                float theta = wallIndex >= 0 ? contactAngles[wallIndex]
                    : ObbContactMath.ContactAngleDeg(pushNormal);
                float pushIn = 0f;
                if (!TryPushIn(pushNormal, drive, out pushIn)) pushIn = 0f;
                float planarSpeed = Mathf.Sqrt(vPlanar.x * vPlanar.x + vPlanar.z * vPlanar.z);
                float vGate = ClimbMath.GateSpeed(planarSpeed, driveSpeed, gateSizeScale);

                // A cap lock releases itself once the ball has slid halfway back, so a player
                // holding into a wall taller than H_cap sees climb-fall-retry instead of a
                // dead stick (owner 2026-11-16: it "climbs up and drops"). Releasing the push
                // still clears it immediately.
                if (locked && hCap > 0f && (position.y - anchorY) < 0.5f * hCap) locked = false;
                if (!active && pushIn < ContactTuning.PMin) locked = false;

                // The anchor of the height cap follows where the ball STANDS: a supported,
                // non-climbing substep puts it at the centre (a RIDE contact is a base).
                // Only a ball that is not standing - sliding down a wall, falling past it -
                // keeps the anchor it had, so a climb that starts from there continues the
                // same wall-hugging stretch instead of beginning a new one.
                if (!active && supportIndex >= 0) anchorY = position.y;

                if (!active && wallIndex >= 0 && !locked && cooldown <= 0f &&
                    pushIn >= ContactTuning.PMin &&
                    vGate >= ClimbMath.RequiredSpeed(theta) && hCap > 0f)
                {
                    // The cap is a height above the BASE of this wall-hugging stretch: the
                    // lowest centre height since the ball last stood on something (see
                    // ClimbMath.ClimbAnchorAtStart). It does not depend on WHICH face the
                    // climb starts on. It used to: a retry on the same face (normal, plane
                    // and top equal) kept the anchor and any other face re-anchored at the
                    // current height. In a narrow V the selected face flips, so every
                    // cap-fall-retry cycle re-anchored higher - a held push reached 0.92 m
                    // on a 0.60 m cap (TERRAIN_CLIMB 6.6); the 50 deg slab's D=0.30 ball
                    // had the same ratchet on split triangles.
                    float faceTop = ContactTopAt(contactTopY, wallIndex);
                    anchorY = ClimbMath.ClimbAnchorAtStart(surfaceId >= 0, anchorY, position.y);
                    active = true;
                    surfaceId = contactSurfaceIds[wallIndex];
                    along = 0f;
                    surfaceNormal = contactNormals[wallIndex];
                    surfacePoint = contactPoints[wallIndex];
                    topY = faceTop;
                }

                // The climb's endings, in priority order: release, ledge, cap, lost hug.
                if (active && pushIn < ContactTuning.PMin)
                {
                    active = false;
                    cooldown = ContactTuning.ClimbCooldownS;
                    along = 0f;
                    locked = false;
                }
                if (active && position.y - radius >= topY - ContactTuning.ClimbLedgeSlopM)
                {
                    // The bottom reached the climb face's top. Hand the climb's speed to the
                    // drive so the ball crosses its own radius onto the ledge, and end the
                    // climb's vertical motion HERE: the exit is a hand-over, not a leap. The
                    // climb may arrive up to ClimbLedgeSlopM (5 mm) below the top - within the
                    // contact skin - so the bottom is settled onto it (<= 5 mm, the same order
                    // as the skin) and vY is zeroed. R25b: without the settle the residual
                    // climb rate (0.36 m/s on the 70 deg slope) carried the ball +4.6 mm over
                    // the deck rest, and without the zeroed vY a lip push-out would have to
                    // pop it the rest of the way.
                    active = false;
                    cooldown = 0f;
                    locked = false;
                    along = 0f;
                    if (topY < float.MaxValue)
                    {
                        float remaining = (topY + radius) - position.y;
                        if (remaining > 0f && remaining <= ContactTuning.ClimbLedgeSlopM)
                        {
                            position.y = position.y + remaining;
                        }
                    }
                    vY = 0f;
                    vPlanar = LedgeExitVelocity(vPlanar, surfaceNormal, driveSpeed, radius);
                }
                if (active && hCap > 0f &&
                    position.y - anchorY >= hCap - ContactTuning.ClimbCapMarginM)
                {
                    active = false;
                    cooldown = ContactTuning.ClimbCooldownS;
                    locked = true;
                    along = 0f;
                    vPlanar = PeelVelocity(vPlanar, surfaceNormal);
                }
                // A hang (climbing on with no live contact) exists ONLY to finish the last
                // radius above a KNOWN ledge. Without a top there is nothing to finish, and
                // carrying on would lift the ball to the cap above a wall it already cleared
                // (R25c: a 0.15 m wall peaked at 0.581 = 0.15 + H_cap - margin). End it.
                if (active && wallIndex < 0 && !(topY < float.MaxValue))
                {
                    active = false;
                    cooldown = ContactTuning.ClimbCooldownS;
                    along = 0f;
                }
                if (active && !Hugging(position, surfaceNormal, surfacePoint, radius))
                {
                    active = false;
                    cooldown = ContactTuning.ClimbCooldownS;
                    along = 0f;
                }

                if (active)
                {
                    // The climb channel OWNS the velocity and drives it along the uphill
                    // tangent: +Y for a wall, up-the-hill for a steep slope. Without a live
                    // wall (the last radius above a low face) the remembered normal gives a
                    // vertical lift, and the ledge exit above is what ends it.
                    float cosTheta = Mathf.Clamp(pushNormal.y, -1f, 1f);
                    Vector3 tangent = new Vector3(-pushNormal.x * cosTheta,
                        1f - pushNormal.y * cosTheta, -pushNormal.z * cosTheta);
                    Vector3 tangentUnit;
                    float tangentLength;
                    if (!KatamariMath.TryNormalize(tangent, out tangentUnit, out tangentLength))
                        return false;
                    float height = position.y - anchorY;
                    float verticalTarget = ClimbMath.ClimbSpeed(pushIn, vGate, theta, height,
                        hCap);
                    float sinTheta = Mathf.Sin(theta * Mathf.Deg2Rad);
                    float alongTarget = verticalTarget / Mathf.Max(sinTheta,
                        ContactTuning.MinClimbSinTheta);
                    float rate = ContactTuning.ClimbRateLimit * dt / substeps;
                    if (alongTarget > along) along = Mathf.Min(along + rate, alongTarget);
                    else along = Mathf.Max(along - rate, alongTarget);
                    // The climb may never lift the bottom PAST the ledge: clamp this substep's
                    // vertical travel to the distance that is left (topY < +inf means the ledge
                    // is known). Without it a step of the climb's own rate lands the ball above
                    // the top - R25c measured a 0.15 m wall peaking 5.3 mm over its rest - and
                    // the exit's settle can only move it DOWN to the ledge, never back.
                    if (topY < float.MaxValue)
                    {
                        float substepSeconds = dt / substeps;
                        float room = (topY + radius) - position.y;
                        if (room < 0f) room = 0f;
                        float verticalCap = substepSeconds > 0f ? room / substepSeconds : 0f;
                        if (!KatamariMath.IsFiniteFloat(verticalCap)) verticalCap = 0f;
                        if (tangentUnit.y > 0f && verticalCap < tangentUnit.y * along)
                        {
                            along = verticalCap / tangentUnit.y;
                        }
                    }
                    vPlanar = new Vector3(tangentUnit.x * along, 0f, tangentUnit.z * along);
                    vY = tangentUnit.y * along;
                    climbSpeedValue = vY;
                    if (height >= hCap - ContactTuning.ClimbCapMarginM)
                    {
                        active = false;
                        cooldown = ContactTuning.ClimbCooldownS;
                        locked = true;
                        along = 0f;
                        vPlanar = PeelVelocity(vPlanar, surfaceNormal);
                    }
                }
                else
                {
                    for (int c = 0; c < count; c++)
                    {
                        if (contactClasses[c] == ContactCodes.ClassRide) continue;
                        if (contactDepths[c] <= 0f) continue;
                        if (c == supportIndex) continue;
                        if (supportIndex >= 0 &&
                            contactClasses[c] == ContactCodes.ClassClimb &&
                            !ContactClassificationMath.IsClimbSteeperThanSupport(
                                contactAngles[c], supportTheta)) continue;
                        if (contactClasses[c] == ContactCodes.ClassClimb)
                        {
                            float normalX = contactNormals[c].x;
                            float normalZ = contactNormals[c].z;
                            float horizontalLength = Mathf.Sqrt(normalX * normalX +
                                normalZ * normalZ);
                            if (horizontalLength <= 0f) return false;
                            float inverse = 1f / horizontalLength;
                            normalX *= inverse;
                            normalZ *= inverse;
                            float inward = vPlanar.x * normalX + vPlanar.z * normalZ;
                            if (inward < 0f)
                            {
                                vPlanar = new Vector3(vPlanar.x - normalX * inward,
                                    vPlanar.y, vPlanar.z - normalZ * inward);
                            }
                        }
                        else
                        {
                            float inward = vPlanar.x * contactNormals[c].x
                                + vY * contactNormals[c].y
                                + vPlanar.z * contactNormals[c].z;
                            if (inward < 0f)
                            {
                                vPlanar = new Vector3(
                                    vPlanar.x - contactNormals[c].x * inward,
                                    0f,
                                    vPlanar.z - contactNormals[c].z * inward);
                                vY = vY - contactNormals[c].y * inward;
                            }
                        }
                    }
                    if (supportIndex >= 0) vY = 0f;
                    else if (wallIndex >= 0)
                        vY = Mathf.Max(vY - ContactTuning.Gravity * dt / substeps,
                            -ContactTuning.VSlideMax);
                    else
                        vY = Mathf.Max(vY - ContactTuning.Gravity * dt / substeps,
                            -ContactTuning.MaxFallSpeedMps);
                    if (driveAccel > 0f)
                    {
                        float current = vPlanar.x * drive.x + vPlanar.y * drive.y
                            + vPlanar.z * drive.z;
                        float delta = Mathf.Clamp(driveSpeed - current,
                            -driveAccel * dt / substeps, driveAccel * dt / substeps);
                        vPlanar = new Vector3(vPlanar.x + drive.x * delta,
                            vPlanar.y + drive.y * delta, vPlanar.z + drive.z * delta);
                    }
                }
            }

            center = position;
            planarVelocity = vPlanar;
            verticalVelocity = vY;
            climbActive = active;
            climbAnchorY = anchorY;
            climbSurfaceId = surfaceId;
            climbCooldown = cooldown;
            climbLocked = locked;
            climbAlongSpeed = along;
            climbTopY = topY;
            climbNormal = surfaceNormal;
            climbPoint = surfacePoint;

            contactCount = count;
            residual = lastResidual;
            climbSpeed = climbSpeedValue;
            substepCount = substeps;
            climbHeight = active ? Mathf.Max(0f, position.y - anchorY) : 0f;
            if (supportIndex >= 0)
            {
                dominantSurfaceId = contactSurfaceIds[supportIndex];
                dominantClass = ContactCodes.ClassRide;
                reportNormal = contactNormals[supportIndex];
                airborne = false;
            }
            else if (surfaceId >= 0)
            {
                dominantSurfaceId = surfaceId;
                dominantClass = ContactCodes.ClassClimb;
                airborne = false;
            }
            else
            {
                dominantSurfaceId = -1;
                dominantClass = ContactCodes.ClassNone;
                airborne = true;
            }
            return true;
        }

        /// <summary>The drive's push into a normal's horizontal direction, 0..1.</summary>
        private static bool TryPushIn(Vector3 normal, Vector3 unitDrive, out float pushIn)
        {
            pushIn = 0f;
            float x = normal.x;
            float z = normal.z;
            float length = Mathf.Sqrt(x * x + z * z);
            if (!KatamariMath.IsFiniteFloat(length) || length <= 0f) return false;
            float inverse = 1f / length;
            float value = Mathf.Max(0f,
                -(unitDrive.x * x * inverse + unitDrive.z * z * inverse));
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            pushIn = value;
            return true;
        }

        /// <summary>The climb face's top, or +inf when the source has none.</summary>
        private static float ContactTopAt(float[] contactTopY, int index)
        {
            if (contactTopY == null || index < 0 || index >= contactTopY.Length)
                return float.MaxValue;
            float top = contactTopY[index];
            return KatamariMath.IsFiniteFloat(top) ? top : float.MaxValue;
        }

        /// <summary>True while the ball is still within a hand's width of the climb plane.</summary>
        private static bool Hugging(Vector3 center, Vector3 normal, Vector3 point, float radius)
        {
            float distance = normal.x * (center.x - point.x) + normal.y * (center.y - point.y)
                + normal.z * (center.z - point.z);
            if (!KatamariMath.IsFiniteFloat(distance)) return false;
            return Mathf.Abs(distance) <= radius + ContactTuning.ClimbHugSlopM;
        }

        /// <summary>
        /// The ledge exit: the drive's inward speed, scaled so crossing one radius takes
        /// short enough that the drop stays a few cm.
        /// </summary>
        private static Vector3 LedgeExitVelocity(Vector3 planar, Vector3 normal,
            float driveSpeed, float radius)
        {
            float x = normal.x;
            float z = normal.z;
            float length = Mathf.Sqrt(x * x + z * z);
            if (!KatamariMath.IsFiniteFloat(length) || length <= 0f) return planar;
            float inverse = 1f / length;
            float speed = ClimbMath.LedgeExitSpeed(radius, driveSpeed);
            if (!KatamariMath.IsFiniteFloat(speed) || speed < 0f) return planar;
            return new Vector3(planar.x - x * inverse * speed, planar.y,
                planar.z - z * inverse * speed);
        }

        /// <summary>Cap release: a small outward push along the wall normal.</summary>
        private static Vector3 PeelVelocity(Vector3 planar, Vector3 normal)
        {
            float x = normal.x;
            float z = normal.z;
            float length = Mathf.Sqrt(x * x + z * z);
            if (!KatamariMath.IsFiniteFloat(length) || length <= 0f) return planar;
            float inverse = ContactTuning.ClimbPeelSpeedMps / length;
            return new Vector3(planar.x + x * inverse, planar.y, planar.z + z * inverse);
        }
    }
}
