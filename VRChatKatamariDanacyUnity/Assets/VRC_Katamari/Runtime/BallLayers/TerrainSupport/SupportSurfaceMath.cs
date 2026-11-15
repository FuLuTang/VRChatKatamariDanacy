using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Terrain-support selection for one ball: which local surface sits under the
    /// ball centre this step, whether a static box is a step or a wall, and the
    /// vertical fall mode once that surface drops away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation only. Docs/Design/BallMotor/TERRAIN_SUPPORT.md §3 puts
    /// the whole support decision here (selection, step band, step hysteresis,
    /// gravity fall state) and the motor only orders calls and carries the
    /// result. The absorb gate stays where it was
    /// (<see cref="VRCKatamari.Pickup.PickupAbsorbMath.IsBeyondAbsorbVolumeLimit"/>);
    /// nothing here reads a prop.
    /// </para>
    /// <para>
    /// Geometry. Lengths are metres in the project's single world space (+Y up).
    /// A candidate surface is a horizontal-ish plane with a finite footprint:
    /// the analytic ground plane (unbounded) or the top face of an axis-aligned
    /// static box. Box top y is <c>maximum.y</c> and its footprint is
    /// <c>[minimum.x, maximum.x] x [minimum.z, maximum.z]</c>; the edges are
    /// inclusive on purpose (a probe exactly on the rim still counts as on the
    /// box) and a test pins that. Slopes/ramps are P3 and are absent here.
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
    /// The step band. A surface is a candidate only when its height is at most
    /// <c>referenceSupportHeight + stepLimit</c>. That single upper bound is
    /// what stops a wall's top face from acting as a floor: a 2 m wall is not in
    /// the band of a ball standing on the ground, so the ball keeps the ground.
    /// The ground is always the fallback even when it is outside the band, so a
    /// query never reports "no support".
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
        /// Three-state step test for one static box: may the ball enter it (step)
        /// or does it stay a wall?
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the <b>only</b> place the "rise vs step limit" comparison
        /// exists. The motor calls it once per static box for the sweep mask and
        /// uses the same answer for the support table, so the sweep and the
        /// support table cannot disagree about a box.
        /// </para>
        /// <para>
        /// Three anti-jitter rules from the design (§4.5):
        /// <list type="number">
        /// <item>one-way hysteresis: a box already classified as a step stays a
        /// step until its rise exceeds <c>stepLimit + hysteresis</c>;</item>
        /// <item>already above the top wins unconditionally, so a ball standing
        /// on the box is never re-classified into a wall while its centre is
        /// inside the expanded box (that would make the sweep push it out);</item>
        /// <item>the minimum-step rule does not live here: it only decides
        /// whether a crossing grants the large step lift
        /// (<see cref="IsStepCrossing"/>), because a 4 mm lip is still something
        /// the ball must be allowed to enter.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Invalid input returns false (the conservative "wall") instead of
        /// throwing or guessing.
        /// </para>
        /// </remarks>
        public static bool IsSteppable(float riseM, float stepLimitM, bool alreadyAbove,
            bool wasSteppable, float hysteresisM)
        {
            if (!KatamariMath.IsFiniteFloat(riseM)) return false;
            if (!KatamariMath.IsFiniteFloat(stepLimitM) || stepLimitM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(hysteresisM) || hysteresisM < 0f) return false;
            if (alreadyAbove) return true;
            if (riseM <= stepLimitM) return true;
            if (wasSteppable && riseM <= stepLimitM + hysteresisM) return true;
            return false;
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
        /// Picks the highest candidate surface under the probe's XZ and reports it
        /// as a plane (normal + point + world-Y height).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Selection order is deterministic: the ground is the initial winner and
        /// a box replaces it only when strictly higher, so ties keep the ground
        /// and, among boxes, the earliest registration. Two identical calls
        /// return identical values; a test pins that.
        /// </para>
        /// <para>
        /// <paramref name="referenceSupportHeightM"/> is the height of the surface
        /// the ball was on in the previous step (the caller's memory). It only
        /// builds the band; it never selects. <paramref name="stepLimitM"/> is
        /// <see cref="TryStepHeightLimit"/>'s result for this step's size.
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
        /// box with inverted/non-finite bounds, and writes nothing that could be
        /// mistaken for a surface.
        /// </para>
        /// </remarks>
        public static bool TryResolveSupport(Vector3 probePoint, float referenceSupportHeightM,
            float stepLimitM, Vector3 groundNormal, float groundOffset,
            Vector3[] boxMinima, Vector3[] boxMaxima, int boxCount,
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
            if (boxCount < 0) return false;
            if (boxCount > boxMinima.Length || boxCount > boxMaxima.Length) return false;

            Vector3 unitNormal;
            float rawLength;
            if (!KatamariMath.TryNormalize(groundNormal, out unitNormal, out rawLength)) return false;
            if (unitNormal.y <= MinUpwardNormalY) return false;
            float offset = groundOffset / rawLength;
            if (!KatamariMath.IsFiniteFloat(offset)) return false;

            // Check every box before selecting, the same "validate first" shape the
            // sweep uses: a bad registration anywhere refuses the query instead of
            // being silently skipped.
            for (int i = 0; i < boxCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(boxMinima[i])) return false;
                if (!KatamariMath.IsFiniteVector(boxMaxima[i])) return false;
                if (boxMinima[i].x > boxMaxima[i].x ||
                    boxMinima[i].y > boxMaxima[i].y ||
                    boxMinima[i].z > boxMaxima[i].z) return false;
            }

            float bandHeight = referenceSupportHeightM + stepLimitM;
            if (!KatamariMath.IsFiniteFloat(bandHeight)) return false;

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

            int activeCount = groundHeight <= bandHeight ? 1 : 0;
            for (int i = 0; i < boxCount; i++)
            {
                float top = boxMaxima[i].y;
                if (top > bandHeight) continue;
                activeCount = activeCount + 1;
                if (probePoint.x < boxMinima[i].x || probePoint.x > boxMaxima[i].x) continue;
                if (probePoint.z < boxMinima[i].z || probePoint.z > boxMaxima[i].z) continue;
                if (top <= selectedHeight) continue;
                selectedId = 1 + i;
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
