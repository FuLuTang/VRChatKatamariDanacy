using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Support vocabulary shared by the motor, the arena host and the pickup host: surface
    /// ids, the step-height limit and its predicate, the absorb-proof prop role
    /// (step or wall), and the static ramp validation and bake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation only. The absorb gate stays where it was
    /// (<see cref="VRCKatamari.Pickup.PickupAbsorbMath.IsBeyondAbsorbLimit"/>, the
    /// original's whole-millimetre Req comparison); nothing here reads a prop - the host
    /// passes the gate's answer in. The support itself is chosen by the motor's contact
    /// solver.
    /// </para>
    /// <para>
    /// A ramp's walking plane is an oriented plane plus a horizontal rectangle:
    /// <c>|dot(probe - centre, right)| &lt;= halfWidth</c> and
    /// <c>|dot(probe - centre, down)| &lt;= halfRun</c>, where <c>down</c> is the
    /// horizontal downhill direction. A ramp is registered as a slab, never also as a
    /// box: the same board would be two solids at one place (design §5.5); the host keeps
    /// the two arrays separate and refuses a collider that appears in both.
    /// </para>
    /// <para>
    /// UdonSharp: static methods, caller-owned arrays, no allocation, no
    /// generic, no overload, no property, no struct. Every failure writes its
    /// out values zero (or false) before returning.
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
        /// How many support props one ball can stand on (6): the width of the prop
        /// surface id range <see cref="IsPropSurfaceId"/> recognises.
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
        /// The ONE step decision: is this top within the ball's step limit?
        /// </summary>
        /// <remarks>
        /// <para>
        /// The terrain mesh query calls it with the BALL'S BOTTOM as the reference. The
        /// comparison is one subtraction and one inclusive bound
        /// (<c>top - reference &lt;= stepLimit</c>). A band written
        /// as <c>top &lt;= reference + stepLimit</c> is not bit-identical to it, and that
        /// second form was half of the 2026-11-15 C09 divergence.
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
        /// The authoring convention (Docs/Design/BallMotor/README.md「关卡几何宿主 LevelArenaHost」节) is
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

        /// <summary>
        /// One step of optional downhill acceleration on a ramp, along the support
        /// plane's tangential direction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Adds <c>gravity * sin(slope) * scale * dt</c> to the planar velocity along the
        /// horizontal downhill direction, clamped to the motor's speed cap. The caller
        /// (<c>KatamariBallMotor.UpdateRampSlide</c>) only calls it while the rest-then-slide
        /// latch (<c>RampSlideMath</c>) is open.
        /// </para>
        /// <para>
        /// Only the horizontal component changes: the vertical channel stays with the
        /// contact solver (a slope does not add a vertical velocity of its own).
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
    }
}
