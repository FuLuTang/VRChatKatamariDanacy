using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Terrain-support selection for one ball: which local surface sits under the
    /// ball centre this step, whether a static box or an absorb-proof prop is a
    /// step or a wall, where a static ramp's walking plane is, and the vertical
    /// fall mode once that surface drops away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation only. Docs/Design/BallMotor/TERRAIN_SUPPORT.md §3 puts
    /// the whole support decision here (selection, the step decision, gravity fall
    /// state, and P2's prop role/support table) and the motor only orders calls and
    /// carries the result. The absorb gate stays where it was
    /// (<see cref="VRCKatamari.Pickup.PickupAbsorbMath.IsBeyondAbsorbLimit"/>, the
    /// original's whole-millimetre Req comparison); nothing here reads a prop - the host
    /// passes the gate's answer in.
    /// </para>
    /// <para>
    /// Geometry. Lengths are metres in the project's single world space (+Y up).
    /// A candidate surface is a plane with a finite footprint: the analytic
    /// ground plane (unbounded), the top face of an axis-aligned static box, a
    /// static ramp's walking plane (P3: an oriented plane plus a horizontal
    /// rectangle), or the top face of a support prop (P2; the same AABB shape,
    /// from the host's gameplay volume). A box/prop top is <c>maximum.y</c> and
    /// its footprint is <c>[minimum.x, maximum.x] x [minimum.z, maximum.z]</c>;
    /// a ramp's height is the plane equation at the probe and its footprint is
    /// <c>|dot(probe - centre, right)| &lt;= halfWidth</c> and
    /// <c>|dot(probe - centre, down)| &lt;= halfRun</c>, where <c>down</c> is the
    /// horizontal downhill direction (the horizontal part of the plane normal; a
    /// normal tilts toward its low side). Every edge is inclusive on purpose (a
    /// probe exactly on the rim still counts as on the surface) and a test pins
    /// that.
    /// </para>
    /// <para>
    /// Ramps are SUPPORT ONLY and never sweep obstacles. A ramp registered as a
    /// box would put the radius-expanded AABB of the whole tilted board at the
    /// ramp foot, which is the invisible wall of design §5.5; the host keeps the
    /// two arrays separate and refuses a collider that appears in both.
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
    /// The step decision. For a static box it is computed ONCE per refresh by the
    /// motor's sweep mask (<see cref="IsStepWithinLimit"/>) and this query reads that
    /// mask; the sweep exclusion and the support candidacy are therefore the same bit,
    /// and no reference drift between the two call sites can reopen the C09 ghost step.
    /// For the ground, ramps, props and the terrain mesh - none of which has a sweep
    /// entry - the query itself calls the same one predicate. A query therefore never
    /// reports "no support": where the mesh answers under the ball the mesh is the
    /// answer, and where it does not the ground plane is.
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
        /// (<c>1 + i</c>, 1..64) unambiguous and lets a reader tell "a prop
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

        /// <summary>
        /// The first surface id owned by a static ramp: ramp slot <c>i</c> is
        /// <c>FirstRampSurfaceId + i</c> (500..503). Every range is disjoint and each is
        /// documented at its mint: ground 0, static box <c>1 + i</c> (1..64), prop
        /// <see cref="FirstPropSurfaceId"/> + i (100..105), ramp 500 + i, boundary plane
        /// (<see cref="VRCKatamari.BallContact.ContactCodes.PlaneSurfaceId"/>,
        /// 1000..1007) and terrain 2000 +.
        /// 10 + i would collide with box <c>1 + i</c> once the static box table grows past 9
        /// (the table holds 64), and 1000 + i is taken by the v2 solver's boundary planes.
        /// Like a prop slot it is a
        /// diagnostic and a switch key, not a durable identity; registration order is fixed
        /// for the scene's lifetime.
        /// </summary>
        public const int FirstRampSurfaceId = 500;

        /// <summary>
        /// How many static ramps one ball can have registered (4). This is the ONE
        /// capacity constant: <see cref="IsRampCountAllowed"/> is the ONE predicate the
        /// motor uses to refuse a fifth ramp, so a second number cannot drift.
        /// </summary>
        public const int MaxRampCount = 4;

        /// <summary>
        /// Step-mask refresh gates: semantic-diameter movement and reference-height
        /// movement that force a recompute. They are PUBLIC because the motor and the
        /// tests must use the same numbers; they are a frequency gate, not a gameplay
        /// tolerance.
        /// </summary>
        public const float StepMaskDiameterGateM = 0.002f;
        public const float StepMaskReferenceGateM = 0.005f;

        /// <summary>
        /// A ramp whose normal is closer to horizontal than this cannot answer "which
        /// way is downhill", so it is refused instead of becoming a wall-like floor
        /// (design §8 P3 case 4: <c>n.y &lt;= 0.05</c> is refused).
        /// </summary>
        public const float MinRampUpwardNormalY = 0.05f;

        /// <summary>
        /// The design guard-rail on a ramp's slope (45 degrees, PROVISIONAL). The hard
        /// ceiling derived from the one-step clamps is
        /// <c>atan(min(maxLiftPerStep, maxDropPerStep) / (maxSpeedMps * fixedStepSeconds))</c>
        /// = 51.3 degrees at 0.1 m / 4 m/s / 20 ms; the rail keeps 30% margin. This
        /// constant is only the default: the motor serializes the field and passes it
        /// in, so re-tuning one number re-tunes every refusal.
        /// </summary>
        public const float DefaultMaxSupportSlopeDegrees = 45f;

        /// <summary>
        /// A ramp plane closer to horizontal than this has no usable downhill
        /// direction. It is a numeric degeneracy, not a gameplay rule: a flat board is
        /// an <c>AddBox</c> top, not a ramp.
        /// </summary>
        private const float MinRampHorizontalNormal = 1e-4f;

        // Ramp registration refusal codes. The motor stores the code (an int, no
        // allocation) and surfaces <see cref="RampRefusalReason"/> as the literal a
        // HUD or a log line can print; the numeric slope is kept separately so the
        // message never has to be built by concatenating floats at runtime.
        public const int RampRefusalNone = 0;
        public const int RampRefusalInvalidInput = 1;
        public const int RampRefusalNormalNotUpward = 2;
        public const int RampRefusalSlopeTooSteep = 3;
        public const int RampRefusalDegenerateAxis = 4;
        public const int RampRefusalSlopeLimitInvalid = 5;
        public const int RampRefusalCapacity = 6;

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
        /// The higher of two heights (a total function: a non-finite second argument is
        /// ignored).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The sweep mask and the support table do NOT compare against this function: a
        /// stale anchor is wrong. A support that is higher than the ball's own bottom widens
        /// the band upward and lets a surface the ball is falling past be adopted, and a
        /// support below the ball (an arena plane at -1000) makes the mesh look unreachable.
        /// The band is anchored on the BALL'S BOTTOM alone (<c>LEVEL_FORMAT_V2</c> §1.3
        /// rule 3, §3.6), and the motor passes that number. The function is the plain max
        /// utility the C09 post-mortem's "already above the top" argument is written in
        /// terms of.
        /// </para>
        /// <para>
        /// A non-finite <paramref name="ballBottomM"/> is ignored rather than poisoning the
        /// caller: the surface height is the conservative answer.
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
        /// The motor's sweep mask calls THIS function per box, with the BALL'S BOTTOM as
        /// the reference, and stores the answer in <c>boxIsSteppable</c>;
        /// <see cref="TryResolveSupport"/> reads that mask for the static boxes instead of
        /// calling this again, and the terrain query passes the same ball bottom. The
        /// comparison is one subtraction and one inclusive bound
        /// (<c>top - reference &lt;= stepLimit</c>). A band written
        /// as <c>top &lt;= reference + stepLimit</c> is not bit-identical to it, and that
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
        /// The step-mask refresh gate: recompute only when the box set changed, the
        /// semantic diameter moved by more than 2 mm, or the reference height moved by
        /// more than 5 mm.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gate is a function of the SAME two inputs the step predicate uses
        /// (<see cref="IsStepWithinLimit"/> takes a top and compares it against a
        /// reference and a limit; the limit is a function of the diameter). That is the
        /// whole point: a gate keyed on the selected support height cannot see the ball
        /// falling from its spawn clearance onto the floor, so the mask could stay
        /// "steppable" while the support table's reference had already moved (the
        /// 2026-11-15 C09 regression: spawn ball bottom 0.020 m, landing ball bottom 0).
        /// Keyed on the reference and the diameter, any input change large enough to
        /// flip the predicate also refreshes the conclusion.
        /// </para>
        /// <para>
        /// The mask is computed once and the support table then READS it for every static
        /// box (<see cref="TryResolveSupport"/> takes <c>boxIsSteppable</c>), so "removed
        /// from the sweep" and "selectable as a support" are the same bit by
        /// construction, not two comparisons that have to agree.
        /// </para>
        /// </remarks>
        public static bool StepMaskNeedsRefresh(bool maskValid, bool boxesDirty,
            float semanticDiameterM, float maskedDiameterM,
            float referenceHeightM, float maskedReferenceHeightM)
        {
            if (!maskValid || boxesDirty) return true;
            if (!KatamariMath.IsFiniteFloat(semanticDiameterM) ||
                !KatamariMath.IsFiniteFloat(maskedDiameterM)) return true;
            if (!KatamariMath.IsFiniteFloat(referenceHeightM) ||
                !KatamariMath.IsFiniteFloat(maskedReferenceHeightM)) return true;
            float diameterChange = semanticDiameterM - maskedDiameterM;
            if (diameterChange < 0f) diameterChange = -diameterChange;
            if (diameterChange > StepMaskDiameterGateM) return true;
            float referenceChange = referenceHeightM - maskedReferenceHeightM;
            if (referenceChange < 0f) referenceChange = -referenceChange;
            return referenceChange > StepMaskReferenceGateM;
        }

        /// <summary>True when <paramref name="count"/> static ramps fit this ball.</summary>
        /// <remarks>
        /// The motor's <c>AddRamp</c> calls exactly this before writing, so the number
        /// lives in one place (design §3.5, §10.2).
        /// </remarks>
        public static bool IsRampCountAllowed(int count)
        {
            return count >= 0 && count <= MaxRampCount;
        }

        /// <summary>True when <paramref name="surfaceId"/> names a static ramp.</summary>
        public static bool IsRampSurfaceId(int surfaceId)
        {
            return surfaceId >= FirstRampSurfaceId &&
                surfaceId < FirstRampSurfaceId + MaxRampCount;
        }

        /// <summary>Ramp index inside that surface id, or -1 when it is not one.</summary>
        public static int RampSurfaceIndex(int surfaceId)
        {
            if (!IsRampSurfaceId(surfaceId)) return -1;
            return surfaceId - FirstRampSurfaceId;
        }

        /// <summary>The literal reason for a ramp refusal code; "" when accepted.</summary>
        /// <remarks>
        /// Literals only: no float formatting and no allocation, so a host may print
        /// this at setup cadence. The numeric slope is reported separately
        /// (<see cref="TryValidateRamp"/> writes <c>slopeDegrees</c> even when it
        /// refuses), which is why the text does not embed "58 deg exceeds 45".
        /// </remarks>
        public static string RampRefusalReason(int refusalCode)
        {
            if (refusalCode == RampRefusalNone) return "";
            if (refusalCode == RampRefusalInvalidInput) return "non-finite or negative ramp input";
            if (refusalCode == RampRefusalNormalNotUpward) return "ramp normal is not upward";
            if (refusalCode == RampRefusalSlopeTooSteep) return "ramp slope exceeds the support limit";
            if (refusalCode == RampRefusalDegenerateAxis) return "ramp axis or extent is degenerate";
            if (refusalCode == RampRefusalSlopeLimitInvalid) return "max support slope is not in (0, 90)";
            if (refusalCode == RampRefusalCapacity) return "ramp capacity 4 exceeded";
            return "unknown ramp refusal";
        }

        /// <summary>Slope of a ramp plane in degrees: the angle between its normal and +Y.</summary>
        public static bool TryRampSlopeDegrees(Vector3 normal, out float slopeDegrees)
        {
            slopeDegrees = 0f;
            Vector3 unitNormal;
            float length;
            if (!KatamariMath.TryNormalize(normal, out unitNormal, out length)) return false;
            float upward = unitNormal.y;
            if (upward > 1f) upward = 1f;
            if (upward < -1f) upward = -1f;
            float slope = Mathf.Acos(upward) * Mathf.Rad2Deg;
            if (!KatamariMath.IsFiniteFloat(slope)) return false;
            slopeDegrees = slope;
            return true;
        }

        /// <summary>
        /// The ONE ramp registration decision: accepts an oriented plane plus a
        /// horizontal footprint half-extent pair, or refuses it with a reason code.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what <c>AddRamp</c> calls; <see cref="TryBakeRamp"/> calls it too
        /// after deriving a plane from a box, so a baked ramp and a hand-registered
        /// one pass through exactly one acceptance rule. The rules are:
        /// the normal must be finite, point up by more than
        /// <see cref="MinRampUpwardNormalY"/>, and be at most
        /// <paramref name="maxSupportSlopeDegrees"/> away from +Y; the supplied
        /// <paramref name="right"/> must have a non-zero horizontal projection (the
        /// stored right is that projection, normalized); the footprint extents must be
        /// finite and positive; and the plane must have a usable downhill direction
        /// (a flat plane is a box top, not a ramp).
        /// </para>
        /// <para>
        /// The slope is the design's §5.2 rule, not a second one: over the limit the
        /// ramp is REFUSED rather than registered, because registering it would let the
        /// ball climb a wall, while not registering it would let the ball pass through
        /// (the author must replace it with an <c>AddBox</c> wall instead). The limit
        /// depends on <c>maxLiftPerStep</c>, <c>maxDropPerStep</c>, <c>maxSpeedMps</c>
        /// and <c>fixedStepSeconds</c>; changing any of those changes the ceiling, so
        /// the motor serializes the value.
        /// </para>
        /// <para>
        /// On failure the out values are zero and <paramref name="refusalCode"/> says
        /// why; on <see cref="RampRefusalSlopeTooSteep"/> the measured slope is still
        /// written so diagnostics can show it.
        /// </para>
        /// </remarks>
        public static bool TryValidateRamp(Vector3 center, Vector3 normal, Vector3 right,
            float halfWidth, float halfRun, float maxSupportSlopeDegrees,
            out Vector3 unitNormal, out Vector3 unitRight, out float slopeDegrees,
            out int refusalCode)
        {
            unitNormal = Vector3.zero;
            unitRight = Vector3.zero;
            slopeDegrees = 0f;
            refusalCode = RampRefusalNone;

            if (!KatamariMath.IsFiniteVector(center) || !KatamariMath.IsFiniteVector(normal) ||
                !KatamariMath.IsFiniteVector(right) ||
                !KatamariMath.IsFiniteFloat(halfWidth) ||
                !KatamariMath.IsFiniteFloat(halfRun))
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }
            if (halfWidth <= 0f || halfRun <= 0f)
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }
            if (!KatamariMath.IsFiniteFloat(maxSupportSlopeDegrees) ||
                maxSupportSlopeDegrees <= 0f || maxSupportSlopeDegrees >= 90f)
            {
                refusalCode = RampRefusalSlopeLimitInvalid;
                return false;
            }

            Vector3 unitN;
            float normalLength;
            if (!KatamariMath.TryNormalize(normal, out unitN, out normalLength))
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }
            if (unitN.y <= MinRampUpwardNormalY)
            {
                refusalCode = RampRefusalNormalNotUpward;
                return false;
            }
            // The downhill direction is the horizontal part of +n: a plane's normal
            // tilts toward its LOW side, so n.xz points downhill. A flat
            // plane has none; it is a box top and must not be registered as a ramp.
            float horizontalNormal = Mathf.Sqrt(unitN.x * unitN.x + unitN.z * unitN.z);
            if (!KatamariMath.IsFiniteFloat(horizontalNormal) ||
                horizontalNormal <= MinRampHorizontalNormal)
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }
            float slope;
            if (!TryRampSlopeDegrees(unitN, out slope))
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }
            slopeDegrees = slope;
            if (slope > maxSupportSlopeDegrees)
            {
                refusalCode = RampRefusalSlopeTooSteep;
                return false;
            }

            Vector3 flatRight = new Vector3(right.x, 0f, right.z);
            Vector3 unitR;
            float rightLength;
            if (!KatamariMath.TryNormalize(flatRight, out unitR, out rightLength))
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }

            unitNormal = unitN;
            unitRight = unitR;
            return true;
        }

        /// <summary>
        /// Bakes a rotated box into a ramp plane: the walking surface is the box's +Y
        /// face, not its mid-plane.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The authoring convention (Docs/Design/BallMotor/TERRAIN_SUPPORT.md §5.1) is
        /// the one the level tooling emits: <paramref name="boxCenter"/> is the box
        /// centre, <paramref name="boxSize"/> is its world size (x = width along local
        /// +X, y = thickness along local +Y, z = length along local +Z), and the
        /// <b>walking plane is the +Y face</b>, a board thickness above the centre. So
        /// the reported <paramref name="walkingCenter"/> is
        /// <c>boxCenter + unitNormal * boxSize.y / 2</c>, and the footprint is measured
        /// from that face centre. Taking the box centre instead would sink the ball by
        /// half the board thickness (0.02 m for the shipped 0.04 m boards).
        /// </para>
        /// <para>
        /// <paramref name="boxRight"/> may be tilted (a rolled board). The stored right
        /// is its horizontal projection; the two footprint half-extents are measured by
        /// projecting the four walking-face corners onto that right and onto the
        /// horizontal downhill direction, taking the largest absolute projection. That
        /// is why <c>halfRun</c> is <c>length/2 * cos(slope)</c> for a pitched board:
        /// the footprint is HORIZONTAL, so the tilted length must be projected. Do not
        /// replace it with <c>boxSize.z / 2</c>.
        /// </para>
        /// <para>
        /// The final acceptance is <see cref="TryValidateRamp"/>, so slope, upward
        /// normal and degenerate geometry are refused with the same codes and the same
        /// limit as a hand-registered ramp. On failure every geometry out is zero.
        /// </para>
        /// </remarks>
        public static bool TryBakeRamp(Vector3 boxCenter, Vector3 boxNormal,
            Vector3 boxRight, Vector3 boxSize, float maxSupportSlopeDegrees,
            out Vector3 walkingCenter, out Vector3 rampNormal, out Vector3 rampRight,
            out float halfWidth, out float halfRun, out float slopeDegrees,
            out int refusalCode)
        {
            walkingCenter = Vector3.zero;
            rampNormal = Vector3.zero;
            rampRight = Vector3.zero;
            halfWidth = 0f;
            halfRun = 0f;
            slopeDegrees = 0f;
            refusalCode = RampRefusalNone;

            if (!KatamariMath.IsFiniteVector(boxCenter) ||
                !KatamariMath.IsFiniteVector(boxNormal) ||
                !KatamariMath.IsFiniteVector(boxRight) ||
                !KatamariMath.IsFiniteVector(boxSize))
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }
            if (boxSize.x < 0f || boxSize.y < 0f || boxSize.z < 0f)
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }

            Vector3 unitN;
            float normalLength;
            if (!KatamariMath.TryNormalize(boxNormal, out unitN, out normalLength))
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }
            Vector3 localX;
            float rightLength;
            if (!KatamariMath.TryNormalize(boxRight, out localX, out rightLength))
            {
                refusalCode = RampRefusalInvalidInput;
                return false;
            }
            Vector3 localZ;
            float forwardLength;
            if (!KatamariMath.TryNormalize(Vector3.Cross(unitN, localX), out localZ,
                out forwardLength))
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }
            Vector3 rightFlat;
            float flatLength;
            if (!KatamariMath.TryNormalize(new Vector3(localX.x, 0f, localX.z), out rightFlat,
                out flatLength))
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }
            Vector3 downhill;
            float dropLength;
            if (!KatamariMath.TryNormalize(new Vector3(unitN.x, 0f, unitN.z), out downhill,
                out dropLength))
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }

            Vector3 faceCenter = boxCenter + unitN * (boxSize.y * 0.5f);
            float halfX = boxSize.x * 0.5f;
            float halfZ = boxSize.z * 0.5f;
            float widthExtent = 0f;
            float runExtent = 0f;
            for (int sx = -1; sx <= 1; sx = sx + 2)
            {
                for (int sz = -1; sz <= 1; sz = sz + 2)
                {
                    Vector3 offset = localX * (sx * halfX) + localZ * (sz * halfZ);
                    float widthProjection = offset.x * rightFlat.x + offset.z * rightFlat.z;
                    if (widthProjection < 0f) widthProjection = -widthProjection;
                    float runProjection = offset.x * downhill.x + offset.z * downhill.z;
                    if (runProjection < 0f) runProjection = -runProjection;
                    if (widthProjection > widthExtent) widthExtent = widthProjection;
                    if (runProjection > runExtent) runExtent = runProjection;
                }
            }
            if (!KatamariMath.IsFiniteFloat(widthExtent) ||
                !KatamariMath.IsFiniteFloat(runExtent) ||
                widthExtent <= 0f || runExtent <= 0f)
            {
                refusalCode = RampRefusalDegenerateAxis;
                return false;
            }

            Vector3 bakedNormal;
            Vector3 bakedRight;
            float bakedSlope;
            int code;
            if (!TryValidateRamp(faceCenter, unitN, rightFlat, widthExtent, runExtent,
                maxSupportSlopeDegrees, out bakedNormal, out bakedRight, out bakedSlope,
                out code))
            {
                refusalCode = code;
                slopeDegrees = bakedSlope;
                return false;
            }

            walkingCenter = faceCenter;
            rampNormal = bakedNormal;
            rampRight = bakedRight;
            halfWidth = widthExtent;
            halfRun = runExtent;
            slopeDegrees = bakedSlope;
            return true;
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
        /// own answer (<c>PickupAbsorbMath.IsBeyondAbsorbLimit</c> with the prop's k and
        /// the motor's semantic diameter), passed in so this layer never reads a prop and
        /// so the three identities share one predicate with no second threshold.
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
        /// the support table: the table's reference is the ball's own bottom, so when the
        /// bottom is at or above the top the rise is at most zero.
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
        /// <remarks>
        /// A BOUNDED range (100..105), not "100 and above": the ramp (500+), boundary-plane
        /// (1000+) and terrain (2000+) ranges live above it, and an open-ended test reported
        /// a terrain or ramp support as "the ball is standing on a prop" in
        /// <c>IsSupportingAProp</c>.
        /// </remarks>
        public static bool IsPropSurfaceId(int surfaceId)
        {
            return surfaceId >= FirstPropSurfaceId &&
                surfaceId < FirstPropSurfaceId + MaxSupportPropCount;
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
        /// ONE selection for every source. The candidates are the terrain mesh's answer
        /// (<paramref name="terrainSurfaceId"/> &gt;= 0: a height under the probe's XZ plus
        /// that triangle's normal), the static boxes whose sweep-mask entry is true, the
        /// static ramps, the absorb-proof prop tops, and - only where the mesh does not
        /// answer - the analytic ground plane. Selection order is deterministic: the
        /// starting surface is the terrain when it answers and the ground otherwise, and a
        /// box, ramp or prop replaces it only when strictly higher, so ties keep the earlier
        /// reader (terrain, then boxes, then ramps, then props, each in registration order).
        /// Two identical calls return identical values; a test pins that.
        /// </para>
        /// <para>
        /// THE GROUND IS NOT A COMPETITOR WHERE THE MESH ANSWERS. That is the whole fix for
        /// the 2026-11-16 terrain run: a copied arena plane at y=0 held a 0.30 m ball at
        /// y=0.15 over a 1 m gully because the mesh triangle at -1 could only replace it by
        /// being HIGHER. A plane is now the answer only when the mesh has nothing under the
        /// ball (a seam hole, or a level with no terrain), which is its documented fallback
        /// role (ORIGINAL_TERRAIN §6.5) - it never holds the ball above terrain.
        /// </para>
        /// <para>
        /// <paramref name="referenceSupportHeightM"/> is the height the band is measured
        /// FROM, and it is the BALL'S BOTTOM, not the previous support and not the ground
        /// plane (<c>LEVEL_FORMAT_V2</c> §1.3 rule 3, §3.6). Upward the band is
        /// <paramref name="stepLimitM"/> (the step gate); downward it has no bound at all, so
        /// a surface any distance below the ball is a candidate and a real fall is the
        /// level's kill plane's decision. It never selects.
        /// </para>
        /// <para>
        /// <paramref name="boxIsSteppable"/> is the motor's step mask, indexed like the
        /// box table (a true entry is a box the sweep has already dropped). Static boxes
        /// are candidates exactly when their entry is true; this function deliberately
        /// does NOT re-run <see cref="IsStepWithinLimit"/> on a box top, because doing so
        /// was the second half of the C09 ghost step: the mask uses the pre-sweep ball
        /// bottom as its reference and this query uses the post-sweep centre, so a 20 mm
        /// landing changed one side and not the other. One conclusion (the mask), one
        /// reader. The gate that decides when the mask is recomputed is
        /// <see cref="StepMaskNeedsRefresh"/> and it is keyed on the predicate's own
        /// inputs (reference height and diameter).
        /// </para>
        /// <para>
        /// <paramref name="rampCenters"/>/<paramref name="rampNormals"/>/
        /// <paramref name="rampRights"/>/<paramref name="rampHalfWidths"/>/
        /// <paramref name="rampHalfRuns"/> are the static ramps the arena registered:
        /// <paramref name="rampCenters"/> is a point on each walking plane (the
        /// <c>TryBakeRamp</c> walking-face centre), the normal is unit and upward, the
        /// right is the footprint's horizontal axis. Ramp <c>i</c> reports
        /// <c>FirstRampSurfaceId + i</c> and its surface normal is the plane normal, so
        /// the motor's visual roll and ground solve automatically use the slope. The
        /// caller owns the capacity and this query reads exactly
        /// <paramref name="rampCount"/> entries.
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
        /// Returns false for non-finite input, a non-upward ground normal, a box/prop with
        /// inverted/non-finite bounds, or a terrain candidate whose id is set but whose
        /// height/normal is not a finite upward plane, and writes nothing that could be
        /// mistaken for a surface.
        /// </para>
        /// </remarks>
        public static bool TryResolveSupport(Vector3 probePoint, float referenceSupportHeightM,
            float stepLimitM, Vector3 groundNormal, float groundOffset,
            Vector3[] boxMinima, Vector3[] boxMaxima, bool[] boxIsSteppable, int boxCount,
            Vector3[] rampCenters, Vector3[] rampNormals, Vector3[] rampRights,
            float[] rampHalfWidths, float[] rampHalfRuns, int rampCount,
            Vector3[] propMinima, Vector3[] propMaxima, int propCount,
            int terrainSurfaceId, float terrainHeightM, Vector3 terrainNormal,
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
            if (boxIsSteppable == null) return false;
            if (rampCenters == null || rampNormals == null || rampRights == null) return false;
            if (rampHalfWidths == null || rampHalfRuns == null) return false;
            if (propMinima == null || propMaxima == null) return false;
            if (boxCount < 0) return false;
            if (boxCount > boxMinima.Length || boxCount > boxMaxima.Length) return false;
            if (boxCount > boxIsSteppable.Length) return false;
            if (rampCount < 0) return false;
            if (rampCount > rampCenters.Length || rampCount > rampNormals.Length ||
                rampCount > rampRights.Length || rampCount > rampHalfWidths.Length ||
                rampCount > rampHalfRuns.Length) return false;
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
            // Ramp registrations are validated before the first candidate is read, the
            // same "validate first" shape as the boxes. The slope guard is the motor's
            // registration gate, not this query's: this only refuses a plane that
            // cannot answer "height under the probe" (non-upward or too flat to have a
            // downhill direction).
            for (int i = 0; i < rampCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(rampCenters[i])) return false;
                if (!KatamariMath.IsFiniteVector(rampNormals[i])) return false;
                if (!KatamariMath.IsFiniteVector(rampRights[i])) return false;
                if (!KatamariMath.IsFiniteFloat(rampHalfWidths[i])) return false;
                if (!KatamariMath.IsFiniteFloat(rampHalfRuns[i])) return false;
                if (rampHalfWidths[i] <= 0f || rampHalfRuns[i] <= 0f) return false;
                Vector3 validationNormal;
                float normalLength;
                if (!KatamariMath.TryNormalize(rampNormals[i], out validationNormal,
                    out normalLength)) return false;
                if (validationNormal.y <= MinRampUpwardNormalY) return false;
                float horizontalNormal = Mathf.Sqrt(validationNormal.x * validationNormal.x +
                    validationNormal.z * validationNormal.z);
                if (!KatamariMath.IsFiniteFloat(horizontalNormal) ||
                    horizontalNormal <= MinRampHorizontalNormal) return false;
                Vector3 validationRight;
                float rightLength;
                if (!KatamariMath.TryNormalize(new Vector3(rampRights[i].x, 0f,
                    rampRights[i].z), out validationRight, out rightLength)) return false;
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

            int selectedId;
            float selectedHeight;
            Vector3 selectedPoint;
            Vector3 selectedNormal;
            int activeCount;

            // The terrain candidate: the mesh's own answer for the ball's footprint
            // (TerrainMeshMath.TrySelectSupport) as one height under the ball's XZ plus that
            // triangle's face normal. A negative id means "the mesh did not answer here", the
            // same convention every surface id uses.
            Vector3 terrainUnitNormal = Vector3.zero;
            bool terrainSelected = terrainSurfaceId >= 0;
            if (terrainSelected)
            {
                if (!KatamariMath.IsFiniteFloat(terrainHeightM)) return false;
                if (!KatamariMath.IsFiniteVector(terrainNormal)) return false;
                float terrainNormalLength;
                if (!KatamariMath.TryNormalize(terrainNormal, out terrainUnitNormal,
                    out terrainNormalLength)) return false;
                // A normal that is not upward cannot answer "what is the height under the
                // probe"; the mesh already refuses those, this is the second gate.
                if (terrainUnitNormal.y <= 0f) return false;
                selectedId = terrainSurfaceId;
                selectedHeight = terrainHeightM;
                selectedPoint = new Vector3(probePoint.x, terrainHeightM, probePoint.z);
                selectedNormal = terrainUnitNormal;
                activeCount = 1;
            }
            else
            {
                // The analytic ground plane is the "no terrain here" answer: the fallback
                // under a seam hole, and the only surface on a level with no mesh. It is NOT
                // a competitor against a mesh triangle, above OR below it: where the mesh
                // answers, the plane is out of the competition entirely, which is the
                // "there is no support plane where there is terrain" rule (LEVEL_FORMAT_V2
                // §1.3, §13.5; ORIGINAL_TERRAIN §6.5 keeps the plane as the seam net).
                selectedId = GroundSurfaceId;
                selectedHeight = groundHeight;
                selectedPoint = groundPoint;
                selectedNormal = unitNormal;
                // The ground, ramps and props have no sweep mask, so they use the one band
                // predicate (IsStepWithinLimit) with the caller's reference. Writing the band
                // as `top <= reference + limit` instead is not bit-identical and was half of
                // the C09 ghost-step bug.
                activeCount = IsStepWithinLimit(groundHeight, referenceSupportHeightM,
                    stepLimitM) ? 1 : 0;
            }
            // Static boxes do NOT re-run the step comparison here. boxIsSteppable is the
            // motor's mask, produced once per refresh by IsStepWithinLimit with the same
            // reference the sweep exclusion used, and this query reads that conclusion.
            // That makes "the mask dropped the box from the sweep" and "the box can
            // support the ball" the same bit: no reference drift between the two sides can
            // reopen the 2026-11-15 C09 ghost step. The reference/limit parameters still
            // control the GROUND, ramps and props, which have no mask (ramps and props are
            // never sweep obstacles, so they have nothing to diverge from).
            for (int i = 0; i < boxCount; i++)
            {
                if (!boxIsSteppable[i]) continue;
                float top = boxMaxima[i].y;
                if (!KatamariMath.IsFiniteFloat(top)) return false;
                activeCount = activeCount + 1;
                if (probePoint.x < boxMinima[i].x || probePoint.x > boxMaxima[i].x) continue;
                if (probePoint.z < boxMinima[i].z || probePoint.z > boxMaxima[i].z) continue;
                if (top <= selectedHeight) continue;
                selectedId = 1 + i;
                selectedHeight = top;
                selectedPoint = new Vector3(probePoint.x, top, probePoint.z);
                selectedNormal = new Vector3(0f, 1f, 0f);
            }
            // Ramps: an oriented plane plus a horizontal rectangle. The height under the
            // probe is the plane equation (there is no error: that is the same plane
            // GroundContactMath will solve), and the footprint is measured along the
            // stored horizontal right and the horizontal downhill direction. The step
            // decision is the SAME IsStepWithinLimit predicate the mask and the ground use,
            // with the same reference: a ramp whose walking plane is out of the band is
            // not a candidate. Ramps are read after the boxes (a box wins a tie) and before
            // the props (a ramp wins a tie against a prop top).
            for (int i = 0; i < rampCount; i++)
            {
                Vector3 rampNormal;
                float normalLength;
                if (!KatamariMath.TryNormalize(rampNormals[i], out rampNormal, out normalLength))
                    continue;
                float planeOffset = rampNormal.x * rampCenters[i].x +
                    rampNormal.y * rampCenters[i].y + rampNormal.z * rampCenters[i].z;
                float rampHeight = (planeOffset - rampNormal.x * probePoint.x -
                    rampNormal.z * probePoint.z) / rampNormal.y;
                if (!KatamariMath.IsFiniteFloat(rampHeight)) return false;
                if (!IsStepWithinLimit(rampHeight, referenceSupportHeightM, stepLimitM)) continue;
                activeCount = activeCount + 1;

                Vector3 rampRight;
                float rightLength;
                if (!KatamariMath.TryNormalize(new Vector3(rampRights[i].x, 0f,
                    rampRights[i].z), out rampRight, out rightLength)) continue;
                Vector3 downhill;
                float dropLength;
                if (!KatamariMath.TryNormalize(new Vector3(rampNormal.x, 0f,
                    rampNormal.z), out downhill, out dropLength)) continue;
                float deltaX = probePoint.x - rampCenters[i].x;
                float deltaZ = probePoint.z - rampCenters[i].z;
                float alongRight = deltaX * rampRight.x + deltaZ * rampRight.z;
                if (alongRight < -rampHalfWidths[i] || alongRight > rampHalfWidths[i]) continue;
                float alongDown = deltaX * downhill.x + deltaZ * downhill.z;
                if (alongDown < -rampHalfRuns[i] || alongDown > rampHalfRuns[i]) continue;
                if (rampHeight <= selectedHeight) continue;
                selectedId = FirstRampSurfaceId + i;
                selectedHeight = rampHeight;
                selectedPoint = new Vector3(probePoint.x, rampHeight, probePoint.z);
                selectedNormal = rampNormal;
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
        /// One step of optional downhill acceleration on a ramp, along the support
        /// plane's tangential direction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the PROVISIONAL U3 experiment and it is OFF by default: the motor's
        /// <c>rampTangentialGravityScale</c> starts at 0, which is the design's
        /// recommendation (the ball does not slide down a slope; a ramp is a channel
        /// whose speed comes from input only). A scale above zero adds
        /// <c>gravity * sin(slope) * scale * dt</c> to the planar velocity along the
        /// horizontal downhill direction, clamped to the motor's speed cap. Calling it
        /// with scale 0 returns the input unchanged, so the default is bit-for-bit the
        /// P1 behaviour.
        /// </para>
        /// <para>
        /// Only the horizontal component changes: the ball's vertical channel remains
        /// GroundContactMath's (a slope does not add a vertical velocity of its own).
        /// A flat or near-flat support normal adds nothing.
        /// </para>
        /// </remarks>
        public static bool TryApplyRampTangentialGravity(Vector3 planarVelocity,
            Vector3 supportNormal, float gravityMps2, float tangentialGravityScale,
            float fixedStepSeconds, float maxSpeedMps, out Vector3 nextPlanarVelocity)
        {
            nextPlanarVelocity = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(planarVelocity)) return false;
            if (!KatamariMath.IsFiniteVector(supportNormal)) return false;
            if (!KatamariMath.IsFiniteFloat(gravityMps2) || gravityMps2 < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(tangentialGravityScale) ||
                tangentialGravityScale < 0f || tangentialGravityScale > 1f) return false;
            if (!KatamariMath.IsFiniteFloat(fixedStepSeconds) || fixedStepSeconds <= 0f)
                return false;
            if (!KatamariMath.IsFiniteFloat(maxSpeedMps) || maxSpeedMps <= 0f) return false;

            Vector3 unitNormal;
            float normalLength;
            if (!KatamariMath.TryNormalize(supportNormal, out unitNormal, out normalLength))
                return false;
            if (unitNormal.y <= 0f) return false;
            float horizontalNormal = Mathf.Sqrt(unitNormal.x * unitNormal.x +
                unitNormal.z * unitNormal.z);
            if (!KatamariMath.IsFiniteFloat(horizontalNormal)) return false;
            if (horizontalNormal <= MinRampHorizontalNormal)
            {
                nextPlanarVelocity = planarVelocity;
                return true;
            }

            float downhillX = unitNormal.x / horizontalNormal;
            float downhillZ = unitNormal.z / horizontalNormal;
            float acceleration = gravityMps2 * horizontalNormal * tangentialGravityScale;
            float deltaSpeed = acceleration * fixedStepSeconds;
            if (!KatamariMath.IsFiniteFloat(deltaSpeed)) return false;

            float velocityX = planarVelocity.x + downhillX * deltaSpeed;
            float velocityZ = planarVelocity.z + downhillZ * deltaSpeed;
            float speed = Mathf.Sqrt(velocityX * velocityX + velocityZ * velocityZ);
            if (!KatamariMath.IsFiniteFloat(speed)) return false;
            if (speed > maxSpeedMps)
            {
                float factor = maxSpeedMps / speed;
                velocityX = velocityX * factor;
                velocityZ = velocityZ * factor;
                speed = maxSpeedMps;
            }
            if (!KatamariMath.IsFiniteFloat(velocityX) || !KatamariMath.IsFiniteFloat(velocityZ))
                return false;

            nextPlanarVelocity = new Vector3(velocityX, 0f, velocityZ);
            return true;
        }

        /// <summary>
        /// True when the selected support surface is a DIFFERENT surface that is
        /// lower than the previous step's by more than <paramref name="dropTriggerM"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the "walked off the ledge" trigger. The trigger is a separation
        /// tolerance, not a gameplay cap: a 1 mm geometric change resolves inside one
        /// step and is invisible, while a real ledge starts a fall. The comparison is
        /// against the surface height, never against the ball's height, so a
        /// radial-proxy lift cannot fake a ledge.
        /// </para>
        /// <para>
        /// <paramref name="sameSurface"/> suppresses the trigger while the SAME selected
        /// surface stays selected. That is not an extra rule: a plane's height under the
        /// moving probe falls simply because the ball rolled along it. On a ramp (P3)
        /// this is the difference between "rolling down the board" and "the board went
        /// away"; without it every step down a 30 degree ramp at 4 m/s drops 46 mm and
        /// would enter the gravity-fall state, so the ball would float and re-land
        /// instead of rolling. The same applies to a tilted ground plane. A real ledge
        /// changes the selected surface id (ramp -> ground, box -> ground, ...), so it
        /// still fires.
        /// </para>
        /// </remarks>
        public static bool SupportDroppedAway(float supportHeightM, float previousSupportHeightM,
            float dropTriggerM, bool sameSurface, out bool dropped)
        {
            dropped = false;
            if (!KatamariMath.IsFiniteFloat(supportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(previousSupportHeightM)) return false;
            if (!KatamariMath.IsFiniteFloat(dropTriggerM) || dropTriggerM < 0f) return false;
            if (sameSurface) return true;
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
