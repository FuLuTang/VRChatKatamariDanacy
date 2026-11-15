using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Terrain-support selection for one ball: which local surface sits under the
    /// ball centre this step, whether a static box or an absorb-proof prop is a
    /// step or a wall, and the vertical fall mode once that surface drops away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation only. Docs/Design/BallMotor/TERRAIN_SUPPORT.md §3 puts
    /// the whole support decision here (selection, the step decision, gravity fall
    /// state, and P2's prop role/support table) and the motor only orders calls and
    /// carries the result. The absorb gate stays where it was
    /// (<see cref="VRCKatamari.Pickup.PickupAbsorbMath.IsBeyondAbsorbVolumeLimit"/>);
    /// nothing here reads a prop - the host passes the gate's answer in.
    /// </para>
    /// <para>
    /// Geometry. Lengths are metres in the project's single world space (+Y up).
    /// A candidate surface is a horizontal-ish plane with a finite footprint:
    /// the analytic ground plane (unbounded), the top face of an axis-aligned
    /// static box, or the top face of a support prop (P2; the same AABB shape,
    /// from the host's gameplay volume). A box/prop top is <c>maximum.y</c> and
    /// its footprint is <c>[minimum.x, maximum.x] x [minimum.z, maximum.z]</c>;
    /// the edges are inclusive on purpose (a probe exactly on the rim still
    /// counts as on the surface) and a test pins that. Slopes/ramps are P3 and
    /// are absent here.
    /// </para>
    /// <para>
    /// Why the probe is the ball centre's XZ, not the lowest contact point's:
    /// for any plane the ball centre's height is exact
    /// (<c>y = planeHeight(C.x, C.z) + r / n.y</c>, which is what
    /// GroundContactMath already solves), so the only approximation is at a
    /// footprint edge, where the ball falls slightly early. That error is
    /// bounded by the ball radius and is conservative (never floats). The
    /// radial-proxy shell can move the lowest point by at most
    /// <c>maxProxyProtrusionWorldM</c>, so the same bound applies to it. See
    /// §3.3 of the design.
    /// </para>
    /// <para>
    /// The step decision. A surface is a candidate only when
    /// <see cref="IsStepWithinLimit"/> accepts it, which is one subtraction against the
    /// caller's reference (<c>SupportReferenceHeight</c>) and an inclusive step limit.
    /// That single comparison is also what the motor's sweep mask calls, so a box the
    /// sweep drops is always a box this query can select: there is no "removed from the
    /// sweep but unsupportable" state. The ground is always the fallback even when it is
    /// outside the band, so a query never reports "no support".
    /// </para>
    /// <para>
    /// UdonSharp: static methods, caller-owned arrays, no allocation, no
    /// generic, no overload, no property, no struct. Every failure writes its
    /// out values zero (or false) before returning, so a caller that ignores the
    /// result can never read a half-written surface.
    /// </para>
    /// </remarks>
    public static class SupportSurfaceMath
    {
        /// <summary>Surface id meaning "no surface"; only a failed query reports it.</summary>
        public const int NoSurfaceId = -1;

        /// <summary>Surface id of the analytic ground plane (always the fallback).</summary>
        public const int GroundSurfaceId = 0;

        /// <summary>
        /// The first surface id owned by a support prop: prop slot <c>i</c> is
        /// <c>FirstPropSurfaceId + i</c>. The gap leaves the static box range
        /// (<c>1 + i</c>, at most eight) unambiguous and lets a reader tell "a prop
        /// supports me" from "a box supports me" by id alone. It is a diagnostic
        /// and a switch key, not a stable prop identity: the host sorts its
        /// selected rows by prop index, so the slot is stable while membership is,
        /// but it can move when the set changes.
        /// </summary>
        public const int FirstPropSurfaceId = 100;

        /// <summary>
        /// How many support props one ball can have registered (6), the same
        /// estimate as the blocking slice. This is the ONE capacity constant:
        /// <see cref="IsSupportPropCountAllowed"/> is the ONE predicate the motor
        /// uses to refuse a seventh box, so a second number cannot drift.
        /// </summary>
        public const int MaxSupportPropCount = 6;

        /// <summary>A prop the ball can absorb: neither a wall nor a support.</summary>
        public const int PropRoleNone = 0;

        /// <summary>An absorb-proof prop within the step limit: a step and a floor.</summary>
        public const int PropRoleSupport = 1;

        /// <summary>An absorb-proof prop above the step limit: a wall.</summary>
        public const int PropRoleBlocking = 2;

        /// <summary>
        /// A ground plane whose normal is closer to horizontal than this cannot
        /// answer "what is the height under the probe", so it is refused instead
        /// of being read as a wall-like floor.
        /// </summary>
        private const float MinUpwardNormalY = 1e-3f;
        /// <summary>
        /// Step limit for one ball: <c>k * semanticDiameter</c>.
        /// </summary>
        /// <remarks>
        /// The diameter form is the only one used at runtime, because the map
        /// data, the map validator and the level gates are all diameter-based
        /// (<c>scale.max_step_height_over_ball_diameter = 0.25</c> in
        /// Docs/Design/Maps/PicnicLawn/layout.json). Mixing the radius form in
        /// once produces the inverted "a 1.10 m ball can climb the 0.26 m bed but
        /// a 1.20 m ball cannot" bug. <paramref name="stepHeightOverBallDiameter"/>
        /// is the ratio, not a height; it is refused outside [0, 1] because a
        /// ratio above one would make a ball climb its own diameter.
        /// </remarks>
        public static bool TryStepHeightLimit(float semanticDiameterM,
            float stepHeightOverBallDiameter, out float stepLimitM)
        {
            stepLimitM = 0f;
            if (!KatamariMath.IsFiniteFloat(semanticDiameterM) || semanticDiameterM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(stepHeightOverBallDiameter) ||
                stepHeightOverBallDiameter < 0f || stepHeightOverBallDiameter > 1f) return false;
            float limit = semanticDiameterM * stepHeightOverBallDiameter;
            if (!KatamariMath.IsFiniteFloat(limit)) return false;
            stepLimitM = limit;
            return true;
        }

        /// <summary>
        /// The support reference height for one step: the higher of the surface the
        /// ball was on and the ball's own bottom.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the ONE reference the sweep mask and the support table both compare
        /// a box top against. The band form "top &lt;= reference + limit" alone would
        /// exclude a surface the ball is falling ONTO (above the old support but below
        /// the ball), so the ball's bottom joins in; and because the mask reads the same
        /// expression, "the ball is already above the top" needs no separate
        /// unconditional rule: the reference is then the ball's own bottom, so the rise
        /// is zero or negative.
        /// </para>
        /// <para>
        /// A non-finite <paramref name="ballBottomM"/> is ignored rather than poisoning
        /// the step: the surface height is the conservative answer.
        /// </para>
        /// </remarks>
        public static float SupportReferenceHeight(float supportHeightM, float ballBottomM)
        {
            if (!KatamariMath.IsFiniteFloat(supportHeightM)) return supportHeightM;
            if (!KatamariMath.IsFiniteFloat(ballBottomM)) return supportHeightM;
            return ballBottomM > supportHeightM ? ballBottomM : supportHeightM;
        }

        /// <summary>
        /// The ONE static-box step decision: is this box top within the ball's step
        /// limit? False means the box stays a wall and stays in the sweep.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both sides call THIS function with the same reference
        /// (<see cref="SupportReferenceHeight"/>): the motor's sweep mask calls it per
        /// box to decide exclusion, and <see cref="TryResolveSupport"/> calls it per box
        /// to decide candidacy. The comparison is one subtraction and one inclusive
        /// bound (<c>top - reference &lt;= stepLimit</c>). A band written as
        /// <c>top &lt;= reference + stepLimit</c> is not bit-identical to it, and that
        /// second form was half of the 2026-11-15 C09 divergence.
        /// </para>
        /// <para>
        /// There is deliberately no one-way hysteresis here. A latch can only widen the
        /// exclusion beyond the support rule, and every box the sweep drops MUST be a box
        /// the support table can select: a box removed from the sweep that cannot support
        /// is the "ghost step" that let the 0.30 m ball drive through C09's 0.075 m deck
        /// (the mask latched while the ball was airborne at spawn, then never released).
        /// The mask's own refresh gate (5 mm of support movement, 2 mm of diameter) is the
        /// de-noise, and it is symmetric because both sides read this one function.
        /// </para>
        /// <para>
        /// The minimum-step rule does not live here: it only decides whether a crossing
        /// grants the large step lift (<see cref="IsStepCrossing"/>), because a 4 mm lip is
        /// still something the ball must be allowed to enter.
        /// </para>
        /// <para>
        /// Invalid input returns false (the conservative "wall") instead of throwing or
        /// guessing.
        /// </para>
        /// </remarks>
        public static bool IsStepWithinLimit(float topY, float referenceSupportHeightM,
            float stepLimitM)
        {
            if (!KatamariMath.IsFiniteFloat(topY)) return false;
            if (!KatamariMath.IsFiniteFloat(referenceSupportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(stepLimitM) || stepLimitM < 0f) return false;
            float rise = topY - referenceSupportHeightM;
            if (!KatamariMath.IsFiniteFloat(rise)) return false;
            return rise <= stepLimitM;
        }

        /// <summary>
        /// True when a rise is large enough to count as "crossing a step" for the
        /// one-step lift allowance, rather than numerical noise.
        /// </summary>
        /// <remarks>
        /// <c>minStepHeightM</c> is a different number from the map's
        /// <c>max_route_step_m = 0.05</c>: 0.05 classifies "ground decal vs
        /// facade" for authors, 0.005 only de-noises the runtime. Using 0.05 here
        /// would make a 2.8 cm napkin invisible to the step machinery, so the two
        /// are deliberately not mixed.
        /// </remarks>
        public static bool IsStepCrossing(float riseM, float minStepHeightM)
        {
            if (!KatamariMath.IsFiniteFloat(riseM)) return false;
            if (!KatamariMath.IsFiniteFloat(minStepHeightM) || minStepHeightM < 0f) return false;
            return riseM > minStepHeightM;
        }

        /// <summary>
        /// The whole "what is this absorb-proof prop to the ball" decision: a food
        /// item (role none), a step/floor (support), or a wall (blocking).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the <b>only</b> place the prop role is computed, and it uses the same
        /// <see cref="IsStepWithinLimit"/> comparison the static-box mask uses.
        /// <paramref name="beyondAbsorbVolumeLimit"/> is the absorb gate's
        /// own answer (<c>PickupAbsorbMath.IsBeyondAbsorbVolumeLimit</c> with the
        /// motor's limit), passed in so this layer never reads a prop and so the
        /// three identities share one predicate with no second threshold.
        /// </para>
        /// <para>
        /// Absorbable wins first: a prop the ball can eat is neither a wall nor a
        /// floor, whatever its geometry. Absorb-proof then splits on
        /// <c>rise = topY - supportHeightM</c> against the step limit, with the
        /// boundary inclusive (<c>rise == stepLimit</c> is a support). That equality
        /// is what the picnic corner's 0.35 m chair -> 0.70 m table chain needs at
        /// <c>D = 1.40</c>.
        /// </para>
        /// <para>
        /// <paramref name="alreadyAbove"/> (the ball's bottom is at or above the prop
        /// top) makes the role support unconditionally. That branch cannot disagree with
        /// the support table: the motor's reference is
        /// <see cref="SupportReferenceHeight"/>, so when the ball's bottom is at or above
        /// the top the reference is at least the top and the table's rise is at most zero.
        /// It is kept as an explicit branch because this layer is given the host's
        /// already-above answer rather than the ball's bottom.
        /// </para>
        /// <para>
        /// Invalid input returns false with <paramref name="role"/> = none, so a
        /// caller that ignores the result cannot build a box from a bad row.
        /// </para>
        /// </remarks>
        public static bool TryClassifyProp(bool beyondAbsorbVolumeLimit, float topY,
            float supportHeightM, float stepLimitM, bool alreadyAbove, out int role)
        {
            role = PropRoleNone;
            if (!KatamariMath.IsFiniteFloat(topY)) return false;
            if (!KatamariMath.IsFiniteFloat(supportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(stepLimitM) || stepLimitM < 0f) return false;
            if (!beyondAbsorbVolumeLimit) return true;

            if (alreadyAbove)
            {
                role = PropRoleSupport;
                return true;
            }
            role = IsStepWithinLimit(topY, supportHeightM, stepLimitM)
                ? PropRoleSupport : PropRoleBlocking;
            return true;
        }

        /// <summary>
        /// True when <paramref name="count"/> support props fit this ball's capacity.
        /// </summary>
        /// <remarks>
        /// The motor's <c>SetSupportProps</c> calls exactly this before writing, and
        /// a test pins 6 accepted / 7 refused, so the number lives in one place.
        /// </remarks>
        public static bool IsSupportPropCountAllowed(int count)
        {
            return count >= 0 && count <= MaxSupportPropCount;
        }

        /// <summary>True when <paramref name="surfaceId"/> names a prop top.</summary>
        public static bool IsPropSurfaceId(int surfaceId)
        {
            return surfaceId >= FirstPropSurfaceId;
        }

        /// <summary>Prop index inside that surface id, or -1 when it is not one.</summary>
        public static int PropSurfaceIndex(int surfaceId)
        {
            if (!IsPropSurfaceId(surfaceId)) return -1;
            return surfaceId - FirstPropSurfaceId;
        }

        /// <summary>
        /// Picks the highest candidate surface under the probe's XZ and reports it
        /// as a plane (normal + point + world-Y height).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Selection order is deterministic: the ground is the initial winner and
        /// a box or prop replaces it only when strictly higher, so ties keep the
        /// ground and, among equals, the earlier reader (boxes before props, then
        /// registration order). Two identical calls return identical values; a test
        /// pins that.
        /// </para>
        /// <para>
        /// <paramref name="referenceSupportHeightM"/> is the height the caller compares
        /// against: <see cref="SupportReferenceHeight"/> of the previous support and the
        /// ball's own bottom. It only builds the step decision; it never selects.
        /// <paramref name="stepLimitM"/> is <see cref="TryStepHeightLimit"/>'s result for
        /// this step's size.
        /// </para>
        /// <para>
        /// <paramref name="propMinima"/>/<paramref name="propMaxima"/> are the
        /// absorb-proof props the host registered as supports (a prop the ball can
        /// absorb never arrives here; the host's <see cref="TryClassifyProp"/>
        /// excluded it). They are read after the static boxes and obey the same
        /// band and footprint rules, so a prop top is a surface exactly like a box
        /// top. Prop <c>i</c> reports <c>FirstPropSurfaceId + i</c>. The caller owns
        /// the capacity; this query reads exactly <paramref name="propCount"/>
        /// entries (the motor can never register more than
        /// <see cref="MaxSupportPropCount"/>).
        /// </para>
        /// <para>
        /// <paramref name="surfacePoint"/> is a point on the winning surface with
        /// <c>dot(surfaceNormal, surfacePoint) == planeOffset</c>, which is exactly
        /// the pair GroundContactMath consumes. <paramref name="candidateCount"/>
        /// counts the surfaces inside the band (footprint not yet applied) and is a
        /// diagnostic only.
        /// </para>
        /// <para>
        /// Returns false for non-finite input, a non-upward ground normal, or a
        /// box/prop with inverted/non-finite bounds, and writes nothing that could
        /// be mistaken for a surface.
        /// </para>
        /// </remarks>
        public static bool TryResolveSupport(Vector3 probePoint, float referenceSupportHeightM,
            float stepLimitM, Vector3 groundNormal, float groundOffset,
            Vector3[] boxMinima, Vector3[] boxMaxima, int boxCount,
            Vector3[] propMinima, Vector3[] propMaxima, int propCount,
            out int surfaceId, out Vector3 surfacePoint, out Vector3 surfaceNormal,
            out float surfaceHeightM, out int candidateCount)
        {
            surfaceId = NoSurfaceId;
            surfacePoint = Vector3.zero;
            surfaceNormal = Vector3.zero;
            surfaceHeightM = 0f;
            candidateCount = 0;

            if (!KatamariMath.IsFiniteVector(probePoint)) return false;
            if (!KatamariMath.IsFiniteFloat(referenceSupportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(stepLimitM) || stepLimitM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(groundOffset)) return false;
            if (boxMinima == null || boxMaxima == null) return false;
            if (propMinima == null || propMaxima == null) return false;
            if (boxCount < 0) return false;
            if (boxCount > boxMinima.Length || boxCount > boxMaxima.Length) return false;
            if (propCount < 0) return false;
            if (propCount > propMinima.Length || propCount > propMaxima.Length) return false;

            Vector3 unitNormal;
            float rawLength;
            if (!KatamariMath.TryNormalize(groundNormal, out unitNormal, out rawLength)) return false;
            if (unitNormal.y <= MinUpwardNormalY) return false;
            float offset = groundOffset / rawLength;
            if (!KatamariMath.IsFiniteFloat(offset)) return false;

            // Check every box and prop before selecting, the same "validate first"
            // shape the sweep uses: a bad registration anywhere refuses the query
            // instead of being silently skipped.
            for (int i = 0; i < boxCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(boxMinima[i])) return false;
                if (!KatamariMath.IsFiniteVector(boxMaxima[i])) return false;
                if (boxMinima[i].x > boxMaxima[i].x ||
                    boxMinima[i].y > boxMaxima[i].y ||
                    boxMinima[i].z > boxMaxima[i].z) return false;
            }
            for (int i = 0; i < propCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(propMinima[i])) return false;
                if (!KatamariMath.IsFiniteVector(propMaxima[i])) return false;
                if (propMinima[i].x > propMaxima[i].x ||
                    propMinima[i].y > propMaxima[i].y ||
                    propMinima[i].z > propMaxima[i].z) return false;
            }

            // Ground plane height under the probe: y = (offset - nx*px - nz*pz) / ny.
            float groundHeight = (offset - unitNormal.x * probePoint.x -
                unitNormal.z * probePoint.z) / unitNormal.y;
            if (!KatamariMath.IsFiniteFloat(groundHeight)) return false;

            // Closest point of the ground plane to the probe: dot(n, point) == offset.
            float probeCoordinate = unitNormal.x * probePoint.x +
                unitNormal.y * probePoint.y + unitNormal.z * probePoint.z;
            float toPlane = probeCoordinate - offset;
            Vector3 groundPoint = new Vector3(probePoint.x - unitNormal.x * toPlane,
                probePoint.y - unitNormal.y * toPlane,
                probePoint.z - unitNormal.z * toPlane);
            if (!KatamariMath.IsFiniteVector(groundPoint)) return false;

            int selectedId = GroundSurfaceId;
            float selectedHeight = groundHeight;
            Vector3 selectedPoint = groundPoint;
            Vector3 selectedNormal = unitNormal;

            // The step decision is the SAME call the motor's sweep mask makes per box
            // (IsStepWithinLimit). Writing the band as `top <= reference + limit` here
            // instead was the second half of the C09 ghost-step bug: a box the mask
            // excluded from the sweep could still fail this comparison and never support.
            int activeCount = IsStepWithinLimit(groundHeight, referenceSupportHeightM,
                stepLimitM) ? 1 : 0;
            for (int i = 0; i < boxCount; i++)
            {
                float top = boxMaxima[i].y;
                if (!IsStepWithinLimit(top, referenceSupportHeightM, stepLimitM)) continue;
                activeCount = activeCount + 1;
                if (probePoint.x < boxMinima[i].x || probePoint.x > boxMaxima[i].x) continue;
                if (probePoint.z < boxMinima[i].z || probePoint.z > boxMaxima[i].z) continue;
                if (top <= selectedHeight) continue;
                selectedId = 1 + i;
                selectedHeight = top;
                selectedPoint = new Vector3(probePoint.x, top, probePoint.z);
                selectedNormal = new Vector3(0f, 1f, 0f);
            }
            // Prop tops: same step decision, same inclusive footprint, same "strictly
            // higher wins" tie-break. Reading them after the boxes means a box wins a tie,
            // which is the stable registration order (scene geometry before a
            // per-scan prop set).
            for (int i = 0; i < propCount; i++)
            {
                float top = propMaxima[i].y;
                if (!IsStepWithinLimit(top, referenceSupportHeightM, stepLimitM)) continue;
                activeCount = activeCount + 1;
                if (probePoint.x < propMinima[i].x || probePoint.x > propMaxima[i].x) continue;
                if (probePoint.z < propMinima[i].z || probePoint.z > propMaxima[i].z) continue;
                if (top <= selectedHeight) continue;
                selectedId = FirstPropSurfaceId + i;
                selectedHeight = top;
                selectedPoint = new Vector3(probePoint.x, top, probePoint.z);
                selectedNormal = new Vector3(0f, 1f, 0f);
            }

            if (!KatamariMath.IsFiniteFloat(selectedHeight)) return false;
            if (!KatamariMath.IsFiniteVector(selectedPoint)) return false;

            surfaceId = selectedId;
            surfacePoint = selectedPoint;
            surfaceNormal = selectedNormal;
            surfaceHeightM = selectedHeight;
            candidateCount = activeCount;
            return true;
        }

        /// <summary>
        /// True when the surface under the ball is now lower than the previous
        /// step's surface by more than <paramref name="dropTriggerM"/>.
        /// </summary>
        /// <remarks>
        /// This is the "walked off the ledge" trigger. The trigger is a
        /// separation tolerance, not a gameplay cap: a 1 mm geometric change
        /// resolves inside one step and is invisible, while a real ledge starts a
        /// fall. The comparison is against the surface height, never against the
        /// ball's height, so a radial-proxy lift cannot fake a ledge.
        /// </remarks>
        public static bool SupportDroppedAway(float supportHeightM, float previousSupportHeightM,
            float dropTriggerM, out bool dropped)
        {
            dropped = false;
            if (!KatamariMath.IsFiniteFloat(supportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(previousSupportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(dropTriggerM) || dropTriggerM < 0f) return false;
            dropped = supportHeightM < previousSupportHeightM - dropTriggerM;
            return true;
        }

        /// <summary>
        /// The falling state machine's whole decision for one fixed step.
        /// </summary>
        /// <remarks>
        /// Falling starts when the surface dropped away (or was already falling)
        /// and ends in the step whose ground solve reaches the surface without
        /// being drop-clamped (the solve reports "the correction fit inside this
        /// step's fall budget", i.e. the ball arrived). A failed ground solve keeps
        /// the previous state; it is not evidence of landing. There is no jump and
        /// no upward velocity anywhere: the only way into this state is a loss of
        /// support.
        /// </remarks>
        public static bool ResolveFallState(bool wasFalling, bool supportDropped,
            bool groundAccepted, bool dropClamped)
        {
            if (!wasFalling && !supportDropped) return false;
            if (groundAccepted && !dropClamped) return false;
            return true;
        }

        /// <summary>
        /// One gravity step: semi-implicit Euler, clamped to a fall-speed guard,
        /// returning both the new speed and this step's fall budget.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The budget is passed to GroundContactMath as BOTH its per-step lift and
        /// drop clamp while falling, so the ground solve can travel exactly this
        /// step's fall distance and no further. That is what keeps a fall from
        /// being slowed to the old <c>maxDropPerStep = 0.1 m</c> (which would cap
        /// descent at 5 m/s and read as a slow float down); and because the same
        /// budget is also the lift clamp, a ball that fell just past the surface in
        /// one step is put back on it instead of staying embedded.
        /// </para>
        /// <para>
        /// <paramref name="maxFallSpeedMps"/> is a safety rail, not a product
        /// value: it bounds one step's travel so a bad teleport or a long drop
        /// cannot produce an absurd per-step move. It is not the old drop clamp
        /// under another name — it is tens of m/s, not 0.1 m per step.
        /// </para>
        /// </remarks>
        public static bool TryIntegrateFall(float fallSpeedMps, float gravityMps2,
            float fixedStepSeconds, float maxFallSpeedMps,
            out float nextFallSpeedMps, out float fallBudgetM)
        {
            nextFallSpeedMps = 0f;
            fallBudgetM = 0f;
            if (!KatamariMath.IsFiniteFloat(fallSpeedMps) || fallSpeedMps < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(gravityMps2) || gravityMps2 < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(fixedStepSeconds) || fixedStepSeconds <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(maxFallSpeedMps) || maxFallSpeedMps <= 0f) return false;

            float speed = fallSpeedMps + gravityMps2 * fixedStepSeconds;
            if (!KatamariMath.IsFiniteFloat(speed)) return false;
            if (speed > maxFallSpeedMps) speed = maxFallSpeedMps;
            float budget = speed * fixedStepSeconds;
            if (!KatamariMath.IsFiniteFloat(budget)) return false;

            nextFallSpeedMps = speed;
            fallBudgetM = budget;
            return true;
        }
    }
}
