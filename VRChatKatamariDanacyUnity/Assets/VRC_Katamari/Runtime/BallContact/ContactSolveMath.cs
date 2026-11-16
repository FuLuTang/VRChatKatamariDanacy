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
            int[] faceC = null)
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
                contactCount++;
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
            int[] faceC = null)
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
                faceC))
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
                    faceA, faceB, faceC))
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
            int[] faceC = null)
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

            Vector3 position = center;
            Vector3 vPlanar = planarVelocity;
            float vY = verticalVelocity;
            bool active = climbActive;
            float anchorY = climbAnchorY;
            int surfaceId = climbSurfaceId;
            float cooldown = Mathf.Max(0f, climbCooldown - dt);
            bool locked = climbLocked;
            float along = climbAlongSpeed;
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
                    faceNormals, faceOffsets, faceA, faceB, faceC))
                    return false;
                position = pushedCenter;
                count = pushedCount;
                lastResidual = pushedResidual;

                supportIndex = ContactClassificationMath.SelectSupportContact(contactNormals,
                    contactDepths, contactClasses, count);
                wallIndex = ContactClassificationMath.SelectClimbContact(contactNormals,
                    contactClasses, count, drive);

                float supportTheta = 0f;
                if (supportIndex >= 0) supportTheta = contactAngles[supportIndex];
                // A contact that is only a little steeper than the surface the ball rolls
                // on is that surface curving over: rolled over by the push-out, never
                // climbed and never allowed to clip the velocity.
                if (wallIndex >= 0 && supportIndex >= 0 &&
                    !ContactClassificationMath.IsClimbSteeperThanSupport(
                        contactAngles[wallIndex], supportTheta)) wallIndex = -1;

                float theta = 0f;
                float pushIn = 0f;
                float vGate = 0f;
                if (wallIndex >= 0)
                {
                    theta = contactAngles[wallIndex];
                    float wallX = contactNormals[wallIndex].x;
                    float wallZ = contactNormals[wallIndex].z;
                    float horizontalLength = Mathf.Sqrt(wallX * wallX + wallZ * wallZ);
                    if (horizontalLength <= 0f) return false;
                    float inverse = 1f / horizontalLength;
                    pushIn = Mathf.Max(0f, -(drive.x * wallX * inverse
                        + drive.z * wallZ * inverse));
                    float planarSpeed = Mathf.Sqrt(vPlanar.x * vPlanar.x
                        + vPlanar.z * vPlanar.z);
                    vGate = Mathf.Max(planarSpeed, ContactTuning.KIntent * driveSpeed);
                    if (pushIn < ContactTuning.PMin)
                    {
                        // Releasing the push is the only way to clear a cap lock: holding
                        // into a wall taller than H_cap must not loop climb/fall silently.
                        locked = false;
                    }
                    if (active && pushIn < ContactTuning.PMin)
                    {
                        active = false;
                        cooldown = ContactTuning.ClimbCooldownS;
                        along = 0f;
                    }
                    if (!active && !locked && pushIn >= ContactTuning.PMin &&
                        cooldown <= 0f && vGate >= ClimbMath.RequiredSpeed(theta) &&
                        hCap > 0f)
                    {
                        active = true;
                        anchorY = position.y;
                        surfaceId = contactSurfaceIds[wallIndex];
                        along = 0f;
                    }
                }
                else
                {
                    active = false;
                    along = 0f;
                }

                if (active && wallIndex >= 0)
                {
                    // The climb channel OWNS the velocity and drives it along the uphill
                    // tangent: +Y for a wall, up-the-hill for a steep slope. Setting the
                    // tangent (instead of clipping the inward part) is what lets a 70 deg
                    // slope be climbed at all - the small into-surface component is what
                    // keeps the contact alive and the push-out turns it into ascent.
                    float cosTheta = Mathf.Clamp(contactNormals[wallIndex].y, -1f, 1f);
                    Vector3 tangent = new Vector3(
                        -contactNormals[wallIndex].x * cosTheta,
                        1f - contactNormals[wallIndex].y * cosTheta,
                        -contactNormals[wallIndex].z * cosTheta);
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
                    vPlanar = new Vector3(tangentUnit.x * along, 0f, tangentUnit.z * along);
                    vY = tangentUnit.y * along;
                    climbSpeedValue = vY;
                    if (height >= hCap - ContactTuning.ClimbCapMarginM)
                    {
                        active = false;
                        cooldown = ContactTuning.ClimbCooldownS;
                        locked = true;
                        along = 0f;
                    }
                    else if (pushIn < ContactTuning.PMin)
                    {
                        active = false;
                        cooldown = ContactTuning.ClimbCooldownS;
                        along = 0f;
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
    }
}
