using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallContact;
using VRCKatamari.BallLayers;
using VRCKatamari.Core;
using VRCKatamari.Economy;
using VRCKatamari.Input;
using VRCKatamari.Motion;
using VRCKatamari.Pickup;

namespace VRCKatamari.BallMotor
{
    /// <summary>
    /// The assembly layer for one local ball: it owns every mutable per-ball
    /// value, calls the eight ball layers in the documented order, and keeps the
    /// previous value of a stage whose solve failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scope. This is the only place that knows the order
    /// ControlIntent, BallMotion, Sweep, GroundContact, PickupAbsorb,
    /// GrowthLedger, SizeModel, VisualRoll, FollowRig. It adds no gameplay
    /// formula: each stage is a call into a stateless static class, and every
    /// numeric decision (speed caps, growth derating, support height, absorb
    /// gates, volume to size, rolling angle, camera lag) already lives there.
    /// The only arithmetic here is what the layers assign to this layer by their
    /// own contracts: the growth fraction fed back to the motion layer, and the
    /// conversion shrink in <see cref="TryBank"/>. The shrink is the
    /// diameter-space rule of Docs/OPEN_DECISIONS.md (a); its arithmetic lives
    /// in <see cref="BankShrinkMath"/> because no ball layer owns a conversion
    /// policy. The growth fraction is still reported as a gap in
    /// Docs/Design/BallMotor/README.md.
    /// </para>
    /// <para>
    /// State ownership. The motor holds the ball's heading, planar velocity,
    /// centre, the three size quantities, the growth fraction fed back to the
    /// motion layer, the confirmed/unbanked/banked volume readings, the
    /// fixed-step accumulator, the stuck timer, the discontinuity flag, the
    /// visual roll pose and the filtered follow-rig frame. The layers receive
    /// those values by value and cannot write them back.
    /// </para>
    /// <para>
    /// Failure. A stage is skipped, not approximated: each assignment happens
    /// inside the "the solve returned true" branch of its stage, so a rejected
    /// call leaves the previous value in place. Size is never accumulated (it is
    /// re-derived from the confirmed volume every step), a gain reaches a total
    /// only through the ledger's commit path, and a discontinuity makes the
    /// visual roll hold its pose instead of spinning across the jump.
    /// </para>
    /// <para>
    /// Fixed step. Unity's frame delta is accumulated and consumed in
    /// fixedStepSeconds slices, at most maxStepsPerFrame per frame; excess time
    /// is dropped so a hitch cannot produce a burst of catch-up steps. All
    /// gameplay state advances per slice; Transforms are written once per
    /// rendered frame. The default 0.02 s (50 Hz) and 5-step clamp are
    /// provisional values, not a measured fixed rate (see the README).
    /// </para>
    /// <para>
    /// Deliberate limits of this revision: one local ball (scalars, no parallel
    /// arrays), one local player row in the ledger (a future round controller
    /// owns the multi-player ledger and remote commits), no networking, no
    /// remote proxy, no HUD, no item registry, no authority election and no
    /// scene wiring (Transforms, ground plane, obstacles and absorb candidates
    /// are supplied by the operator or a future adapter).
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class KatamariBallMotor : UdonSharpBehaviour
    {
        // ---- Fixed-step clock (provisional; Docs/Design/BallMotor/README.md 未定项) ----
        [SerializeField] private float fixedStepSeconds = 0.02f;
        [SerializeField] private int maxStepsPerFrame = 5;
        [SerializeField] private float stuckTimeoutSeconds = 1.5f;

        // ---- BallMotionMath inputs (DRAFT placeholders from Docs/Design/BallMotion/README.md) ----
        [SerializeField] private float maxSpeedMps = 4f;
        [SerializeField] private float accelerationMps2 = 5f;
        [SerializeField] private float brakingMps2 = 8f;
        [SerializeField] private float frictionPerSecond = 1.2f;
        [SerializeField] private float turnRateDegreesPerSecond = 110f;
        [SerializeField] private float maxLateralMps = 1.2f;
        [SerializeField] private float growthSpeedFactor = 0.25f;
        [SerializeField] private float growthTurnFactor = 0.35f;

        // ---- GroundContactMath inputs: one analytic plane + the radial proxy shell ----
        [SerializeField] private Vector3 groundPlaneNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundPlaneOffset = 0f;
        // PROVISIONAL one-step caps, metres. maxLiftPerStep also bounds a teleport-free
        // rise onto a slope; maxProxyLiftPerStep additionally bounds the rise caused by a
        // proxy bump, so a long rod cannot pop the ball up in one step.
        [SerializeField] private float maxLiftPerStep = 0.1f;
        [SerializeField] private float maxDropPerStep = 0.1f;
        [SerializeField] private float maxProxyLiftPerStep = 0.04f;

        // ---- Terrain support (P1: static box tops, the step rule, gravity fall) ----
        // PROVISIONAL values, all from Docs/Design/BallMotor/TERRAIN_SUPPORT.md.
        //
        // The switch: when false, every input this layer feeds to every other layer is
        // exactly what it was before terrain support existed (the sweep keeps all
        // static boxes, GroundContactMath gets the raw ground plane, no fall state
        // exists and VisualRoll gets the raw ground normal), so an older lab can keep
        // its frame behaviour bit for bit. It defaults to true because a level's floor
        // and ledges are static boxes; a lab that predates the step rule must call
        // SetTerrainSupportEnabled(false).
        [SerializeField] private bool terrainSupportEnabled = true;
        // The RATIO, never a height: stepLimit = ratio * semantic diameter. 0.25 is the
        // value already in Docs/Design/Maps/PicnicLawn/layout.json and in the map
        // validator's climb_step rule; runtime read it nowhere before P1. The radius
        // form is deliberately absent (see SupportSurfaceMath.TryStepHeightLimit).
        [SerializeField] private float stepHeightOverBallDiameter = 0.25f;
        // Rail on one step-up lift, so a mis-set ratio cannot teleport the ball. 0.6 is
        // above 0.25 * 2.20 = 0.55, so a correct configuration never touches it.
        [SerializeField] private float maxStepUpLiftWorldM = 0.6f;
        // There is deliberately NO step hysteresis / stick-margin field. The one-way latch
        // that used to live here could only widen the sweep exclusion beyond the support
        // rule, and a box removed from the sweep that cannot support is a ghost step: on
        // 2026-11-15 the mask latched while the ball was airborne at spawn and the 0.30 m
        // ball drove through C09's 0.075 m deck. The mask's refresh gate (2 mm of
        // diameter, 5 mm of support movement) is the de-noise, and both sides now read
        // SupportSurfaceMath.IsStepWithinLimit with the same reference.
        // Below this rise a crossing grants no large lift: numeric de-noise, NOT the
        // map's 0.05 m ground-decal/facade classification threshold.
        [SerializeField] private float minStepHeightM = 0.005f;
        // A fall is real gravity, not the old 0.1 m-per-step drop clamp (which would cap
        // descent at 5 m/s and read as a slow float down). 9.81 is PROVISIONAL: no
        // original-game measurement says the ball falls at Earth gravity.
        [SerializeField] private float gravityMps2 = 9.81f;
        // Safety rail on fall speed so a long drop cannot produce an absurd per-step
        // move; it is tens of m/s, not a re-named 0.1 m clamp.
        [SerializeField] private float maxFallSpeedMps = 40f;
        // A surface drop smaller than this is the same surface, not a ledge. It is a
        // tolerance, not a gameplay cap: 1 mm would make every step on a slightly tilted
        // ground plane enter and leave the fall state (a 7 deg plane at 4 m/s drops 9.8 mm
        // per 20 ms step), which is flicker, not physics. A centimetre is below anything
        // the ball should visibly fall off, and such a drop is taken by the normal
        // maxDropPerStep clamp in one step, i.e. reads as glued to the floor.
        // It only applies when the SELECTED SURFACE changed: a ramp or tilted ground
        // lowers the plane under the moving probe as the ball rolls along it (a 30 deg
        // ramp at 4 m/s drops 46 mm per step) and that is not a lost surface.
        [SerializeField] private float supportDropTriggerM = 0.01f;

        // ---- Static ramps (P3: support only, never sweep obstacles) ----
        // The guard-rail on a ramp's slope, degrees (PROVISIONAL). 45 is the design's
        // 30%-margin value over the hard 51.3 degree ceiling that the one-step clamps
        // imply: tan(theta) <= min(maxLiftPerStep, maxDropPerStep) / (maxSpeedMps *
        // fixedStepSeconds) = 0.1 / (4 * 0.02) = 1.25. Changing maxLiftPerStep,
        // maxDropPerStep, maxSpeedMps or fixedStepSeconds changes that ceiling, which is
        // why this is serialized and NOT hard-coded in the math: SupportSurfaceMath takes
        // it as a parameter. A steeper ramp is refused with a reason code, never
        // registered (the ball would climb a wall) and never silently ignored (the ball
        // would pass through).
        [SerializeField] private float maxSupportSlopeDegrees = SupportSurfaceMath.DefaultMaxSupportSlopeDegrees;
        // Optional downhill acceleration on a ramp, as a fraction of g * sin(slope).
        // PROVISIONAL and OFF by default: the design's U3 recommendation is "a ramp is a
        // channel, the ball does not slide down by itself", and the P1 motion contract is
        // planar velocity only. A pilot can raise this without touching the motion layer;
        // at 0 the step is bit-for-bit the P1 behaviour.
        [SerializeField] private float rampTangentialGravityScale = 0f;

        // ---- Radial proxy caps and long-object admission (PROVISIONAL, unmeasured) ----
        // The protrusion cap is relative to the core radius (the contract's rule) and also
        // has an absolute rail for a large ball. Both are engineering limits against a rod
        // catapult, not measured product values.
        [SerializeField] private float maxProxyProtrusionPerCoreRadius = 0.35f;
        [SerializeField] private float maxProxyProtrusionWorldM = 0.3f;
        // The vertical channel is position-only in this revision, so the requested normal
        // speed perturbation is 0; the cap is still a serialized input so a later revision
        // that does add a normal velocity cannot exceed it.
        [SerializeField] private float maxProxyNormalSpeedPerturbation = 0.15f;
        // Admission: longest edge >= this many ball diameters, and >= this many times the
        // SECOND-longest edge (one axis dominates the other two). A rod/plank passes both;
        // a cup, a box, and a square sheet with one thin axis pass neither.
        [SerializeField] private float longObjectMinLongestEdgePerCoreDiameter = 0.5f;
        [SerializeField] private float longObjectMinElongation = 2.5f;
        // A long item whose farthest corner does not clear the core by this much cannot
        // contribute a support above the core, so it is not registered.
        [SerializeField] private float minProxyProtrusionM = 0.01f;

        // ---- SweepMath inputs ----
        [SerializeField] private float separationEpsilon = 0.001f;

        // ---- SizeModelMath inputs (dimensionless volume scale, not a world scale decision) ----
        // Calibration must match Docs/Design/Gameplay/round_model.py, which every
        // level's star targets are built on: 1356 confirmed units take the ball from
        // the 0.30 m start to the 2.20 m cap, and eating a whole 1284-unit map leaves
        // it at 2.16 m. The previous 0.0141 m^3 per unit reached the cap at ~394 units,
        // i.e. 30% of a map, so growth ran 3.44x too fast against the model.
        //
        // The unit got 3.44x larger, but the baseline is a VOLUME (0.0141 m^3 = the
        // 0.30 m start ball) and must not change with it. Expressing that same volume
        // in the new unit is 0.0141 / 0.0041 = 3.439 units; leaving it at 1 would drop
        // the start diameter to 0.199 m and break the 0.30 m -> 2.20 m round.
        [SerializeField] private float baselineVolumeUnits = 3.4390244f;
        [SerializeField] private float cubicMetersPerVolumeUnit = 0.0041f;
        [SerializeField] private float maxCoreRadiusWorldM = 1.1f;
        [SerializeField] private float collisionToCoreRadiusRatio = 1f;
        [SerializeField] private float semanticDiameterPerCoreDiameter = 1f;

        // ---- Start size: the level-level parameter (parity D6) ----
        // The original's start diameter is a per-level value: 5 cm for MAS1, 22.2 cm for
        // Cancer, 30 cm for our first level (Docs/Design/Parity/PARITY_AUDIT.md, D6 and
        // §5.4 A/B). Before this field, the only start-size input was
        // baselineVolumeUnits, so "the level wants a 5 cm ball" could not be expressed and
        // every level started at the calibration's 0.2997 m.
        //
        // 0 means NOT SET: the level keeps baselineVolumeUnits as authored, which is every
        // scene written before this field existed, so existing levels do not move. A
        // positive value is authoritative: ApplyStartDiameterWorldM rewrites the baseline
        // volume from it in Start, once, and from then on every consumer reads one
        // baseline. The diameter is the SEMANTIC diameter (the narrative size a player
        // reads, see SizeModelMath.TryDerive), not the collision diameter.
        [SerializeField] private float startDiameterWorldM = 0f;

        // ---- PickupAbsorbMath inputs ----
        [SerializeField] private float contactRadiusWorldM = 0f;
        // The size gate's per-item factor and the ball's diameter are the ONLY gate inputs,
        // and neither is serialized here: k travels with the candidate
        // (PickupFieldHost.propVolumeK -> SubmitCandidateWithVolumeK), and the diameter is
        // this motor's own semanticDiameterWorldM. The gate itself lives in
        // PickupAbsorbMath.IsBeyondAbsorbLimit, in the original's quantities (k * bbox,
        // Req floored to whole mm, ball floor(1000*D) >= Req) - owner decision 2026-11-16.
        //
        // Retired fields, deleted and deliberately not replaced:
        // `maxItemVolumeWorldM3` (512f, a per-config cubic-metre ceiling coupled to the
        // ball's own volume) and `absorbVolumeRatio` (10f, the ratio as a knob). The owner
        // decision says the gate follows the original's numbers exactly, so the ratio is
        // PickupAbsorbMath.AbsorbVolumeRatio (a constant) and the ceiling is that file's
        // MaxItemVolumeSanityWorldM3 (a sanity bound far above any ball cap, not a gate).
        // A scene still serializing the old names just has its dead keys ignored (the same
        // rule the earlier `maxItemSizeWorldM`/`absorbSizeRatio` retirement used); deleting
        // them from a scene is editor cleanup, not a behaviour prerequisite
        // (Docs/Design/PickupAbsorb/README.md 1.1).

        // ---- FollowRigMath inputs (PROVISIONAL) ----
        [SerializeField] private float heightFilterHz = 5f;
        [SerializeField] private float yawFilterHz = 8f;
        // The frame is behind the ball and above it, and both distances grow with the ball:
        // height = baseUp + radius * upPerRadius, back = baseBack + radius * backPerRadius.
        // The frame is also where the rider sits, so these numbers ARE the player's view.
        //
        // Why a station and not a camera: VRChat has no API that moves an avatar continuously.
        // The only mechanisms are VRCStation and teleporting, so "a person pushing the ball" is
        // built as a station behind the ball. First person reads as pushing; third person shows
        // a seated avatar, which is the engine's limit, not a choice made here.
        //
        // The bounds below are rails, not tuning: they must not force a 0.10 m ball up to a
        // standing person's eye height, and must not pin a large ball.
        [SerializeField] private float eyeBaseUpMeters = 0.55f;
        [SerializeField] private float eyeUpPerRadius = 2.4f;
        [SerializeField] private float eyeUpMinMeters = 0.1f;
        [SerializeField] private float eyeUpMaxMeters = 8f;
        [SerializeField] private float eyeBaseBackMeters = 0.75f;
        [SerializeField] private float eyeBackPerRadius = 2.2f;
        [SerializeField] private float eyeMaxBackM = 8f;

        // ---- VisualRollMath inputs ----
        [SerializeField] private float maxRollDisplacementM = 0.5f;

        // ---- Conversion rule (PROVISIONAL; Docs/OPEN_DECISIONS.md (a) and (b)) ----
        [SerializeField] private float bankResidualFraction = 0.25f;
        [SerializeField] private float minConversionDiameterWorldM = 0.55f;

        // ---- Local round bootstrap (single player; a round controller replaces this) ----
        [SerializeField] private string roundId = "local-round";
        [SerializeField] private string localPlayerId = "local-player";
        [SerializeField] private bool autoStartPlaying = true;

        // ---- Scene links (the operator wires these; a null link is skipped) ----
        [SerializeField] private Transform ballPhysicsRoot;
        [SerializeField] private Transform ballVisualRollRoot;
        [SerializeField] private Transform followRigRoot;
        [SerializeField] private Vector3 startCenterWorld = new Vector3(0f, 0.15f, 0f);

        // ---- Debug input fallback: a future input adapter drives these axes ----
        [SerializeField] private float debugForward = 0f;
        [SerializeField] private float debugTurn = 0f;
        [SerializeField] private float debugLateral = 0f;
        [SerializeField] private bool debugMenuOpen = false;

        // ---- Ball state (single local ball; see the class remarks) ----
        private float headingDegrees;
        private Vector3 planarVelocity;
        private Vector3 ballCenterWorld;
        private float coreRadiusWorldM;
        private float collisionRadiusWorldM;
        private float semanticDiameterWorldM;
        private float baseCoreRadiusWorldM;
        private float growthFraction;
        private float fixedStepAccumulator;
        private float localClockSeconds;
        private float stuckSeconds;
        private bool discontinuous;
        private Quaternion visualRollRotation;

        // ---- Volume readings: only committed values, never predictions ----
        // The two ledger mirrors are 64-bit (Docs/OPEN_DECISIONS.md (r) step A): the
        // ledger columns are long, and a mirror narrower than its source would wrap at
        // int.MaxValue. The unit and every number are unchanged; carriedVolumeUnits and
        // confirmedVolumeUnits stay float because they are the SIZE MODEL's quantities
        // (a display input), not ledger sums - they are recomputed from the mirrors, they
        // never accumulate, and step C re-derives them from a mm^3 ledger.
        private long unbankedVolumeUnits;
        private float carriedVolumeUnits;
        private float confirmedVolumeUnits;
        private long bankedScoreUnits;
        private int absorbedCount;

        // ---- FollowRig filter state ----
        private Vector3 followRigWorldPosition;
        private float followRigYawDegrees;
        private float followRigHeightMeters;
        private bool hasFollowRigFrame;

        // ---- Input stage state (the intent layer owns the debounce latches) ----
        private float inputForward;
        private float inputTurn;
        private float inputLateral;

        // ---- Ledger stores: one local player, one round, no authority layer ----
        private const int PlayerCapacity = 1;
        private const int CommitCapacity = 256;
        private const int ClaimCapacity = 8;
        private const int PredictionCapacity = 0;
        private const int BankRequestCapacity = 8;

        private int[] roundState;
        private bool[] roundFlags;
        private float[] roundClock;
        private string[] playerIds;
        private long[] confirmedVolumes;
        private long[] bankedScores;
        private int[] lifeStates;
        private bool[] respawnAtSet;
        private float[] respawnAt;
        private int[] playerGenerations;
        private int playerCount;
        private string[] commitItemIds;
        private int[] commitGenerations;
        private string[] commitWinners;
        private int[] commitSequences;
        private long[] commitGains;
        private int commitCount;
        private string[] claimItemIds;
        private int[] claimGenerations;
        private string[] claimPlayerIds;
        private int[] claimFrames;
        private int[] claimRequestSequences;
        private int claimCount;
        private string[] predictedPlayerIds;
        private string[] predictedItemIds;
        private int[] predictedGenerations;
        private long[] predictedGains;
        private int predictedCount;
        private string[] bankRequestPlayerIds;
        private string[] bankRequestIds;
        private int bankRequestCount;
        private int absorbRequestSequence;
        private int authorityFrame;
        private int localRow;
        private bool hasLocalRow;

        // ---- Input buffers (caller-owned, reused, never reallocated per step) ----
        private bool[] previousDown;
        private float[] lastActionTime;
        private bool[] comfortState;
        private ControlIntentKind[] intentKindBuffer;
        private float[] intentXBuffer;
        private float[] intentYBuffer;
        private bool[] intentValueBuffer;

        // ---- Sweep buffers; obstacle order is planes first, then boxes ----
        private const int MaxPlaneCount = 8;
        private const int MaxBoxCount = 8;
        private const int ContactCapacity = 4;

        // Dynamic blocking boxes get their own capacity instead of borrowing the static
        // box budget. A map's 8 static boxes are an authoring contract the map validator
        // mirrors (8/8), while "which nearby props are too big to absorb" is re-decided
        // every scan; sharing one budget would make a lab's walls compete with props for
        // slots and would change every map's data when the prop count needs a bigger
        // window. Six is a provisional estimate of how many props a ball can touch at
        // once (Docs/Design/PickupAbsorb/BLOCKING_PROPS_PLAN.md §9.2), not a measurement.
        private const int MaxBlockingBoxCount = 6;

        // Support props get their own six slots, symmetric to the blocking slice and
        // owned by SupportSurfaceMath (one capacity constant, one predicate). They are
        // a separate pool on purpose: a stage's step must not lose its slot because
        // six walls happen to be closer.
        private const int MaxSupportPropCount = SupportSurfaceMath.MaxSupportPropCount;

        // ---- Cross-behaviour set buffers: ownership (why Start is not the only allocator) ----
        // Every buffer below holds a SET that another behaviour writes through a public
        // entry point: the pickup field pushes its blocking and support sets from its own
        // Update (PickupFieldHost.UpdatePropObstacles) and queues candidates, and the arena
        // host registers planes/boxes/ramps. Unity does not order Start across GameObjects,
        // and this project has already met both orders (F1: a blocking set arrived before
        // this component's Start, the destination was still null, the write threw a null
        // reference and Udon halted the motor for the rest of the session - the ball never
        // started). The rule is one rule, the same one the v2 contact tables follow:
        //   1. The first writer allocates. Every entry point calls an Ensure... method, so
        //      no set depends on Start having run. That first allocation is the only moment
        //      a null or truncated destination can be GROWN, and it grows the whole set at
        //      once (every destination at its own capacity).
        //   2. Start calls the SAME ensure instead of allocating a second copy, so a set
        //      registered before Start is kept rather than wiped by a re-allocation.
        //   3. A null or undersized buffer after that is a refused call, never a null
        //      reference and never a partial write: blocking/support record "set buffers
        //      unavailable", a ramp records its refusal code, the candidate queue logs once
        //      and answers false, and AddPlane/AddBox - which have no reason channel - answer
        //      false for the arena's existing report. It cannot be grown at that point: the
        //      accepted set and its readers hold these exact arrays, so replacing them would
        //      silently empty the ball's world. Nothing is written by a refused call.
        // The sets covered here are the pickup field's two role sets (blocking, support),
        // the scene geometry (planes, boxes, ramps) and the candidate queue. The v2 contact
        // tables have their own ensure for the same reason (EnsureContactV2Buffers); the
        // radial proxy shell is written only from this component's absorbed-event ring,
        // which cannot exist before a step, so it stays a Start allocation.
        private bool registrationBuffersReady;
        private bool candidateBuffersReady;
        // One line if the queue cannot be allocated: a missing buffer is an internal
        // invariant failure, not a gameplay answer, so it must not repeat per submit.
        private bool loggedCandidateBuffersMissing;

        private Vector3[] planeNormalBuffer;
        private float[] planeOffsetBuffer;
        private int planeCount;
        private Vector3[] boxMinimumBuffer;
        private Vector3[] boxMaximumBuffer;
        private int boxCount;
        // The blocking slice as last accepted by SetBlockingBoxes, in the host's order.
        // The sweep reads the appended copy RebuildObstacles builds, never these buffers.
        private Vector3[] blockingMinimumBuffer;
        private Vector3[] blockingMaximumBuffer;
        private int blockingBoxCount;

        // ---- Support props (P2): absorb-proof props the ball may stand on ----
        // A support prop is NOT a sweep obstacle: by construction its top is within
        // one step of the ball, so the ball must be allowed to enter it (exactly the
        // reason a steppable static box leaves the sweep). The host owns which props
        // these are and re-sends the set every scan; the motor only carries it into
        // the support table. Capacity and refusal semantics mirror the blocking
        // slice, but the two are separate pools: a crowded blocking set must never
        // evict the step the ball is climbing.
        private Vector3[] supportPropMinimumBuffer;
        private Vector3[] supportPropMaximumBuffer;
        private int supportPropCount;
        // Refusals are deliberately NOT part of the overflow signal, for the same
        // reason the blocking refusals are not: "more nearby props than the ball can
        // stand on" is a normal per-scan answer, not a mis-wired scene.
        private int supportPropRefusalCount;
        private string supportPropRefusalReason;

        // ---- Static ramps (P3): support-only oriented planes ----
        // These are NOT sweep obstacles and share no slots with the static boxes. A ramp
        // registered as a box would put the radius-expanded AABB of the whole tilted board
        // at the ramp foot: the invisible wall of design §5.5. The parallel arrays are the
        // UdonSharp shape (no user structs); the support query reads exactly rampCount
        // entries. The centre is a point on the WALKING PLANE (the box's +Y face), not the
        // box centre, so the ball sits on the board rather than half a thickness inside it.
        private Vector3[] rampCenterBuffer;
        private Vector3[] rampNormalBuffer;
        private Vector3[] rampRightBuffer;
        private float[] rampHalfWidthBuffer;
        private float[] rampHalfRunBuffer;
        private int rampCount;
        // Refusals are counted separately from SignalOverflow for the same reason prop
        // refusals are, except for capacity: a fifth ramp is a scene-wiring error and does
        // latch the one-shot overflow signal, exactly like a ninth static box.
        private int rampRefusalCount;
        private int rampRefusalCode;
        private float rampRefusalSlopeDegrees;
        // A ramp refused because the set buffers could not be allocated. It is NOT a
        // SupportSurfaceMath code (that table is about geometry); this motor translates it
        // in GetLastRampRefusalReason so the reason is still a literal and still code-visible.
        private const int RampRefusalNoBuffers = 7;

        private Vector3[] planeNormals;
        private float[] planeOffsets;
        private Vector3[] boxMinima;
        private Vector3[] boxMaxima;
        private Vector3[] contactCenters;
        private Vector3[] contactPoints;
        private Vector3[] contactNormals;
        private float[] contactTimes;
        private int[] contactObstacleIndices;
        private float[] contactPenetrationDepths;
        // Outward wall normals for this step's contact response, a second view of
        // the contact log (boxes may report an inner-face normal; see the stage 3
        // remarks). Reused, never reallocated per step.
        private Vector3[] wallContactNormals;

        // ---- Terrain support state (P1) ----
        // The support decision itself lives in SupportSurfaceMath; this layer only
        // carries its result and the two memories the design asks for (which box is a
        // step, and which surface the ball stood on last step).
        //
        // boxIsSteppable is indexed like the static box buffers, caches this step's
        // answer for the sweep arrays, AND is the support table's candidacy answer for a
        // static box (TryResolveSupport reads it). One conclusion, two readers: a true
        // entry is excluded from the sweep and is a support candidate, a false entry stays
        // a wall. Computing the comparison twice was the P1 C09 ghost step (the mask's
        // reference and the query's reference could drift apart while the ball landed);
        // see SupportSurfaceMath.StepMaskNeedsRefresh for the gate.
        private bool[] boxIsSteppable;
        private bool stepMaskValid;
        private bool stepMaskBoxesDirty;
        private int stepMaskRebuildCount;
        private float stepMaskDiameterWorldM;
        // The REFERENCE the mask was computed with, i.e. the predicate's own input
        // (SupportReferenceHeight of the last support and the ball's bottom), not the
        // selected support height. Landing from a spawn clearance moves the reference
        // without moving the support, so a support-height gate could not see it.
        private float stepMaskReferenceWorldM;
        // Selected support of the current step, as the plane pair the ground solve
        // consumes plus the diagnostics the host and the lab read.
        private int supportSurfaceId;
        private Vector3 supportSurfacePointWorld;
        private Vector3 supportSurfaceNormalWorld;
        private float supportSurfaceHeightWorldM;
        private int supportCandidateCount;
        private float stepHeightLimitWorldM;
        // The previous step's selected support: the band reference and the rise base.
        private int previousSupportSurfaceId;
        private float previousSupportHeightWorldM;
        private float lastSupportHeightWorldM;
        private bool hasSupportFrame;
        // Gravity fall state. There is no jump and no upward velocity: the only way in
        // is losing support, and arriving at a surface is the only way out.
        private bool isFalling;
        private float fallSpeedMps;

        // ---- Pending absorb candidates; candidate collection is host-side ----
        private const int PendingCandidateCapacity = 4;

        private bool[] pendingAbsorbable;
        private string[] pendingKinds;
        private Vector3[] pendingBounds;
        private Vector3[] pendingPositions;
        private float[] pendingValues;
        // The item's per-item volume factor k (the original's `Volume = k*bbox`). It rides
        // with the candidate because the gate is a per-item comparison: the same ball, two
        // items of the same AABB, different k -> different Req. Defaults to
        // PickupAbsorbMath.DefaultVolumeK for a submitter that does not supply one.
        private float[] pendingVolumeK;
        private string[] pendingItemIds;
        private int[] pendingGenerations;
        private bool[] pendingAccepted;
        // Submission timestamp per slot: data only, so a reader can tell a stale
        // snapshot from a fresh one. Feeds no decision.
        private float[] pendingSubmitTimes;
        private int pendingCandidateCount;

        // ---- Absorbed-event ring: the only "which item was eaten" exit ----
        // The pickup stage and the ledger know an item was consumed, but until this
        // ring existed nothing outside the motor could learn WHICH item disappeared
        // (GetAbsorbedCount is a total). A presentation or host layer needs the
        // identity, the world position and the size to hide the prop that was just
        // eaten, so the commit path appends one event per committed absorb.
        // Purely additive: no stage of the pipeline reads the ring, and a full ring
        // evicts the oldest event instead of failing (the absorb already committed).
        private const int AbsorbedEventCapacity = 16;

        private string[] absorbedEventItemIds;
        private Vector3[] absorbedEventPositions;
        private int absorbedEventCount;

        // Occupancy of the ring, 0..AbsorbedEventCapacity. It is separate from
        // absorbedEventCount because a round reset empties the ring WITHOUT rewinding
        // the absolute count: PickupFieldHost keeps its own cursor at the absolute
        // number and only resyncs when that number goes backwards, so a reset that
        // rewound it would either replay dead events or (if it skipped forward) make a
        // host miss everything until the next capacity boundary. Keeping the count
        // monotonic means a retry needs no special case in any reader: it simply sees
        // no retained events and then the new round's events, in order.
        private int absorbedEventRingCount;

        // ---- Rejected-candidate ring: why the pickup gate said no ----
        // A rejected candidate used to vanish without a trace: the item stayed in
        // the queue, the ball rolled past, and nothing anywhere said whether the
        // kind whitelist, the size gate, the growth cap or the reach test refused
        // it. That silence is a design defect, not just a debugging inconvenience,
        // because a mis-set whitelist looks exactly like "no props in range".
        // Every non-Absorb outcome is recorded here with its distance, in the same
        // absolute-index form as the absorbed ring so a reader keeps its own cursor.
        private const int RejectEventCapacity = 8;

        private const int rejectDiagnosticsLimit = 3;
        private const int commitDiagnosticsLimit = 3;
        private int commitDiagnosticsLogged;

        // ---- Evaluation trace (observation only; changes no decision) ----
        // The reject ring stores the LAST few refusals, so it can never show whether
        // the item under the ball was evaluated while it was inside reach: a refusal
        // at d > reach is self-fulfilling (outcome comes from the same comparison as
        // d), so "minimum distance" proves approach, not a decision. This trace logs
        // EVERY evaluation of one item, which is what answers the question.
        // Diagnostics only, never a gameplay field: empty means the trace is off and
        // costs one length check per candidate. A scene sets an item id to ask "was
        // this item evaluated, and with what numbers"; nothing reads it for gameplay.
        [SerializeField] private string debugTraceItemId = "";
        // Radial-proxy support trace: one console line per accepted ground solve while
        // enabled, capped by the same TraceLineLimit. This is the single-rod lab's data
        // source (support height / core radius / lift / endpoint count); it is off by
        // default and reads no gameplay state back.
        [SerializeField] private bool debugTraceProxySupport = false;
        private int proxyTraceLinesLogged;
        private const int TraceLineLimit = 400;
        private int traceLinesLogged;

        private float traceLastDistance;
        private float traceLastReach;
        private float traceLastBallZ;
        private int traceLastOutcome;
        private int rejectDiagnosticsLogged;

        private string[] rejectEventItemIds;
        private int[] rejectEventReasons;
        private float[] rejectEventDistances;
        private int rejectEventCount;

        // ---- Settlement awards: keys are semantic, awarded once, held by the ledger ----
        private const int AwardCapacity = 16;

        private string[] awardedKeys;
        private int[] awardedPoints;
        private int awardedCount;
        private int lastAwardFailure;

        // Failure code of the most recent TryResetForNewRound, from the same pattern
        // TryAwardScore uses for its award rule: -1 means the ledger was not consulted
        // (an input this motor refused first), 0 means it accepted, 1..11 is the
        // GrowthLedgerMath rule's own code. Kept apart from the motor's own reason so a
        // caller can tell "this ball refused the call" from "the ledger refused the
        // row" without the two tables sharing numbers.
        private int lastResetLedgerFailure;

        // ---- One-shot capacity signal; see the remarks on RebuildObstacles ----
        private int overflowCount;
        private bool overflowLatched;
        private string firstOverflowReason;
        private bool contactBufferSaturated;

        // ---- Blocking-box refusals, deliberately NOT part of the overflow signal ----
        // "The box capacity was exceeded" and "a mis-wired scene dropped an obstacle" are
        // different failures: the first is a normal per-scan answer from a host that found
        // more nearby props than this ball can touch, the second means the scene is wrong.
        // Counting them together would make a crowded picnic table look like a broken
        // scene forever. Same shape as the overflow signal (a count and one reason string,
        // no per-frame logging); the previous box set stays registered on a refusal.
        private int blockingRefusalCount;
        private string blockingRefusalReason;

        // ---- Radial proxy shell (one owner; the layers stay stateless) ----
        // The shell lives on the motor because the motor already owns the ball pose and
        // is the only place that feeds GroundContactMath. The arrays are allocated once
        // in Start and reused: 16 directions, 16 endpoint-only supports, a 32-slot pool
        // (4 long items x 8 AABB corners), the per-item slices and the 4 candidate keys.
        private const int ProxyItemCapacity = RadialProxyMath.MaxExtremeCandidates;
        private const int ProxyEndpointCapacity = ProxyItemCapacity * RadialProxyShellMath.AabbCornerCount;

        private Vector3[] proxyDirections;
        private float[] proxySupports;
        private Vector3[] proxyEndpointPool;
        private int proxyEndpointCount;
        private int[] proxyItemStarts;
        private int[] proxyItemLengths;
        private string[] proxyItemKeys;
        private bool[] proxyItemIsCandidate;
        private int proxyItemCount;
        private string[] proxyCandidateKeys;
        private int proxyCandidateCount;
        private int[] proxyAffectedIndices;
        private float[] proxySolveScratch;
        private Vector3[] localProxyPoints;
        // localProxyPoints is the array actually passed to GroundContactMath: rebuilt from
        // proxyEndpointPool every step with each endpoint's length clamped to core+cap, and
        // unused slots zeroed (an empty shell keeps it all-zero and support is the core).
        private Vector3[] proxyCorners;
        private Vector3[] proxyRemovedCorners;
        private Vector3[] proxyEmptyPoints;
        private float proxyGroundSupportHeightM;
        private float proxyProtrusionCapWorldM;
        private int proxySkipCount;
        private int proxySkipReason;
        private int proxyRefusalCount;
        private int proxyRefusalLogCount;

        // Why AttachRadialProxy did not register an item. 1 and 2 are ordinary skips
        // (the prop simply cannot contribute); 3..6 are scene/data problems.
        private const int ProxySkipNone = 0;
        private const int ProxySkipNotLongObject = 1;
        private const int ProxySkipInsideCore = 2;
        private const int ProxySkipCandidateFull = 3;
        private const int ProxySkipPoolFull = 4;
        private const int ProxySkipInvalidInput = 5;
        private const int ProxySkipShellRefused = 6;


        // ---- Ball contact v2 (Docs/Design/BallMotor/CONTACT_MODEL_V2.md; S3) ----
        //
        // PROVISIONAL. The flag is the rollback switch: FALSE runs the v1 position
        // authority (StepPositionV1, the pre-v2 statements verbatim); TRUE replaces the
        // step mask, the inflated-AABB sweep, the ramp height-field support and the
        // radial-proxy ground lift with ONE contact solver (StepContactV2). No number in
        // this region is measured or play-tested; the values come from ContactTuning.
        [SerializeField] private bool contactModelV2Enabled = false;

        // Collider proxies (Docs/Design/PickupAbsorb/COLLIDER_PROXY.md §6): when true the
        // prop block is read from the proxy columns instead of the prop OBB columns. FALSE
        // is the v1/v2-box path and must keep every byte-identical behaviour it had; the
        // proxy buffers are allocated anyway so a runtime flip finds them.
        [SerializeField] private bool propProxyContactEnabled = false;

        /// <summary>
        /// Slab thickness an AddRamp becomes in v2, metres (design §1.5 default 0.04).
        /// The walking face is the slab's +local-Y face, so the body hangs below the
        /// authored plane and the ball does not sink by half a board.
        /// </summary>
        [SerializeField] private float rampSlabThicknessM = 0.04f;

        // Contact capacities and motor-side thresholds (design §1.3, §2.1, §2.8, §4.2).
        private const int MaxNeedleContacts = 4;
        private const int MaxPropObbCount = 12;
        private const float ContactResidualRejectM = 0.02f;
        private const float ContactGrowthPushCapM = 0.05f;
        private const float ContactStuckSoftSeconds = 1.5f;
        private const float ContactStuckHardSeconds = 3f;
        private const float ContactRecoveryWindowSeconds = 10f;
        private const int ContactRecoveryHoldCount = 3;
        private const int ContactRecoveryStateNone = 0;
        private const int ContactRecoveryStateSoft = 1;
        private const int ContactRecoveryStateHard = 2;
        private const int ContactRecoveryStateHold = 3;

        // Static OBB table: the map's geometry, registered once (design §1.3, §5.2).
        private Vector3[] staticObbCenter;
        private Vector3[] staticObbAxisX;
        private Vector3[] staticObbAxisY;
        private Vector3[] staticObbAxisZ;
        private Vector3[] staticObbHalfExtents;
        private int[] staticObbSurfaceId;
        private int[] staticObbRole;
        private float[] staticObbBoundingRadius;
        private int staticObbCount;
        private int staticObbOverflowCount;
        private int staticObbRefusalCount;
        private string staticObbRefusalReason;

        // The prop OBB block (design §1.4: blocking/support props are the same primitive).
        // Its entries live in the SAME arrays, immediately after the static prefix
        // [staticObbCount, staticObbCount + propObbCount), so the near window sees one
        // distance-sorted table and needs no second scan or second capacity.
        private int propObbCount;
        private int propObbOverflowCount;
        private int propObbRefusalCount;
        private string propObbRefusalReason;
        private int propProxyCount;
        private int propProxyOverflowCount;
        private int propProxyRefusalCount;
        private string propProxyRefusalReason;

        // Proxy shape data for the 12-slot prop block. Kind/radius/half-height/axis and the
        // hull/face start/count are per LOGICAL slot (static prefix + prop block), while the
        // hull points and faces live in the compact 12-entry arrays indexed by the start.
        private int[] staticProxyKind;
        private float[] staticProxyRadius;
        private float[] staticProxyHalfHeight;
        private int[] staticProxyAxis;
        private int[] staticProxyHullStart;
        private int[] staticProxyHullCount;
        private int[] staticProxyFaceStart;
        private int[] staticProxyFaceCount;
        private Vector3[] propProxyHullPoints;
        private Vector3[] propProxyFaceNormals;
        private float[] propProxyFaceOffsets;
        private int[] propProxyFaceA;
        private int[] propProxyFaceB;
        private int[] propProxyFaceC;

        // Per-step near set, its scratch buffers and the hysteresis state (design §5.2).
        private Vector3[] nearObbCenter;
        private Vector3[] nearObbAxisX;
        private Vector3[] nearObbAxisY;
        private Vector3[] nearObbAxisZ;
        private Vector3[] nearObbHalfExtents;
        private float[] nearObbBoundingRadius;
        private int[] nearObbSurfaceId;
        private int[] nearObbRole;
        // Per-near proxy shape columns; box entries keep kind 0 and the zero counts, so the
        // old OBB path reads exactly the same numbers.
        private int[] nearObbKind;
        private float[] nearObbRadius;
        private float[] nearObbHalfHeight;
        private int[] nearObbAxis;
        private int[] nearObbHullStart;
        private int[] nearObbHullCount;
        private int[] nearObbFaceStart;
        private int[] nearObbFaceCount;
        private int[] nearObbSlot;
        private int nearObbCount;
        private int[] windowScratchIndices;
        private float[] windowScratchDistances;
        private int[] windowSelectedIndices;
        private int[] windowPreviousSurfaceIds;
        private int windowPreviousCount;
        private int nearRebuildCount;
        private int windowDroppedCount;
        private int windowDroppedTotal;
        private int windowOverflowCount;
        private int windowSaturatedCount;
        private bool contactIncomplete;
        private float lastWindowOverflowLogSeconds;

        // The contact log the solver writes (design §1.3, §2.1): parallel arrays, 12 slots.
        private Vector3[] contactNormalsV2;
        private Vector3[] contactPointsV2;
        private float[] contactDepthsV2;
        private float[] contactAnglesV2;
        private int[] contactSurfaceIdsV2;
        private int[] contactRolesV2;
        private int[] contactFeaturesV2;
        private int[] contactClassesV2;
        private int[] contactShapesV2;
        private int v2ContactCount;
        private int v2CoreContactCount;
        private int v2NeedleContactCount;
        private float contactResidualMax;
        private int v2SubstepCount;
        private int v2DominantSurfaceId;
        private int v2DominantClass;
        private bool contactAirborne;
        private Vector3 contactReportNormal;

        // The needle subset (design §4A.3): clamped supports, the compact index list, and
        // the per-step needle contacts before they merge into the contact log.
        private float[] contactNeedleSupports;
        private int[] contactNeedleIndices;
        private int needleCompactCount;
        private Vector3[] contactNeedleNormals;
        private Vector3[] contactNeedlePoints;
        private float[] contactNeedleDepths;
        private float[] contactNeedleAngles;
        private int[] contactNeedleSurfaceIds;
        private int[] contactNeedleRoles;
        private int[] contactNeedleClasses;
        private int[] contactNeedleShapes;
        private int needleContactCount;
        private Vector3 contactPreviousCenterWorld;
        private Quaternion contactPreviousRotation;
        private bool contactHasPreviousFrame;

        // Climb channel state (design §3.4). TryStep owns the transitions; these fields
        // carry them between steps and are what the getters report.
        private bool climbActive;
        private float climbAnchorY;
        private int climbSurfaceId;
        private float climbCooldown;
        private bool climbLocked;
        private float climbAlongSpeed;
        private float climbHeightWorldM;
        private float climbSpeedWorldMps;
        // The wall the climb channel is holding on to. TryStep reports the climb through
        // the state refs but not the wall normal, so WriteContactSupport records it (and
        // its theta) for the standing-gate check of the next step.
        private Vector3 climbWallNormalWorld;
        private float climbContactAngleDeg;

        // The vertical velocity v2 owns (the v1 path only ever has fallSpeedMps).
        private float contactVerticalVelocity;

        // Stuck recovery (design §2.8): the last stable point, the round start, the
        // window and the never-silently-loop state.
        private Vector3 contactStableCenter;
        private bool hasContactStableCenter;
        private float lastStableWriteSeconds;
        private Vector3 roundStartCenter;
        private bool hasRoundStartCenter;
        private int recoveryCount;
        private int recoveryState;
        private int lastRecoveryReason;
        private float recoveryWindowStartSeconds;
        private bool recoveryHoldLogged;
        private bool softRecoveryArmed;
        private float contactV2ClockSeconds;

        // Growth push-out (design §4.2), drive direction, step cost (design §6.4) and the
        // two support kinds v2 adds (4 = climbing contact, 5 = needle support, §2.5).
        private float contactRadiusUsedWorldM;
        private bool sizeApplyDeferred;
        private int sizeApplyDeferredCount;
        private Vector3 contactDriveDirection;
        private float lastStepMicros;
        private int contactSolveFailureCount;
        private int contactRejectedStepCount;
        private bool supportFromNeedle;
        private bool supportFromClimb;

        // Growth push-out has its own contact scratch set so it cannot clobber the log
        // the step's getters report.
        private Vector3[] growthContactNormals;
        private Vector3[] growthContactPoints;
        private float[] growthContactDepths;
        private float[] growthContactAngles;
        private int[] growthContactSurfaceIds;
        private int[] growthContactRoles;
        private int[] growthContactFeatures;
        private int[] growthContactClasses;

        private string[] allowedKinds;
        private int allowedKindCount;
        private float maxCollisionRadiusWorldM;

        private void Start()
        {
            Debug.Log("[BallMotor] Start begin center=" + startCenterWorld);
            if (fixedStepSeconds <= 0f) fixedStepSeconds = 0.02f;
            if (maxStepsPerFrame < 1) maxStepsPerFrame = 1;
            if (stuckTimeoutSeconds <= 0f) stuckTimeoutSeconds = 1.5f;

            roundState = new int[GrowthLedgerMath.RoundStateCount];
            roundFlags = new bool[GrowthLedgerMath.RoundFlagCount];
            roundClock = new float[GrowthLedgerMath.RoundClockCount];
            playerIds = new string[PlayerCapacity];
            confirmedVolumes = new long[PlayerCapacity];
            bankedScores = new long[PlayerCapacity];
            lifeStates = new int[PlayerCapacity];
            respawnAtSet = new bool[PlayerCapacity];
            respawnAt = new float[PlayerCapacity];
            playerGenerations = new int[PlayerCapacity];
            commitItemIds = new string[CommitCapacity];
            commitGenerations = new int[CommitCapacity];
            commitWinners = new string[CommitCapacity];
            commitSequences = new int[CommitCapacity];
            commitGains = new long[CommitCapacity];
            claimItemIds = new string[ClaimCapacity];
            claimGenerations = new int[ClaimCapacity];
            claimPlayerIds = new string[ClaimCapacity];
            claimFrames = new int[ClaimCapacity];
            claimRequestSequences = new int[ClaimCapacity];
            predictedPlayerIds = new string[PredictionCapacity];
            predictedItemIds = new string[PredictionCapacity];
            predictedGenerations = new int[PredictionCapacity];
            predictedGains = new long[PredictionCapacity];
            bankRequestPlayerIds = new string[BankRequestCapacity];
            bankRequestIds = new string[BankRequestCapacity];

            // The pending-candidate stores are the only arrays this type declared
            // without allocating, so SubmitCandidate dereferenced null on its first
            // call. Nothing exercised that seam before a scene drove it; the capacity
            // (4) and the drop-on-full policy are unchanged. The host can reach the
            // submit path before this Start runs, so the queue allocates on first use
            // and Start only calls the same ensure (see the ownership note above the
            // set buffers).
            EnsureCandidateBuffers();

            awardedKeys = new string[AwardCapacity];
            awardedPoints = new int[AwardCapacity];
            awardedCount = 0;

            absorbedEventItemIds = new string[AbsorbedEventCapacity];
            absorbedEventPositions = new Vector3[AbsorbedEventCapacity];
            absorbedEventCount = 0;
            absorbedEventRingCount = 0;

            traceLinesLogged = 0;
            traceLastDistance = 0f;
            traceLastReach = 0f;
            traceLastBallZ = 0f;
            traceLastOutcome = 0;

            rejectEventItemIds = new string[RejectEventCapacity];
            rejectEventReasons = new int[RejectEventCapacity];
            rejectEventDistances = new float[RejectEventCapacity];
            rejectEventCount = 0;

            previousDown = new bool[ControlIntentMath.ActionCount];
            lastActionTime = new float[ControlIntentMath.ActionCount];
            comfortState = new bool[2];
            intentKindBuffer = new ControlIntentKind[ControlIntentMath.MaxIntentsPerFrame];
            intentXBuffer = new float[ControlIntentMath.MaxIntentsPerFrame];
            intentYBuffer = new float[ControlIntentMath.MaxIntentsPerFrame];
            intentValueBuffer = new bool[ControlIntentMath.MaxIntentsPerFrame];
            ControlIntentMath.TryReset(previousDown, lastActionTime, comfortState);

            // The set buffers are allocated on first use (see the ownership note above the
            // fields): the pickup field can push a blocking or support set before this Start
            // runs, so Start only makes sure they exist. Re-allocating here would wipe a set
            // that is already registered and being read by this step loop.
            EnsureRegistrationBuffers();
            contactCenters = new Vector3[ContactCapacity];
            contactPoints = new Vector3[ContactCapacity];
            contactNormals = new Vector3[ContactCapacity];
            contactTimes = new float[ContactCapacity];
            contactObstacleIndices = new int[ContactCapacity];
            contactPenetrationDepths = new float[ContactCapacity];
            wallContactNormals = new Vector3[ContactCapacity];

            // Terrain support: the mask has one entry per static box slot and starts
            // empty (nothing is a step until a step has been evaluated). The support
            // pair starts on the configured ground plane; the first step resolves the
            // real surface.
            boxIsSteppable = new bool[MaxBoxCount];
            stepMaskValid = false;
            stepMaskBoxesDirty = true;
            stepMaskRebuildCount = 0;
            stepMaskDiameterWorldM = 0f;
            stepMaskReferenceWorldM = 0f;
            supportSurfaceId = SupportSurfaceMath.GroundSurfaceId;
            supportSurfacePointWorld = new Vector3(0f, groundPlaneOffset, 0f);
            supportSurfaceNormalWorld = new Vector3(0f, 1f, 0f);
            supportSurfaceHeightWorldM = groundPlaneOffset;
            supportCandidateCount = 1;
            stepHeightLimitWorldM = 0f;
            previousSupportSurfaceId = SupportSurfaceMath.NoSurfaceId;
            previousSupportHeightWorldM = 0f;
            lastSupportHeightWorldM = 0f;
            hasSupportFrame = false;
            isFalling = false;
            fallSpeedMps = 0f;

            // The radial proxy shell is the ball's sparse support shell: 16 fixed
            // directions plus the endpoints of at most four long objects. State is
            // owned here; the attach/detach/rebuild protocol is RadialProxyShellMath
            // and the pure support math stays in RadialProxyMath. An empty shell is
            // the documented core-sphere case.
            proxyDirections = new Vector3[RadialProxyMath.DirectionCount];
            if (!RadialProxyMath.TryFillDirections(proxyDirections))
            {
                Debug.LogWarning("[BallMotor] radial proxy directions could not be generated;"
                    + " the shell stays at the core sphere.");
            }
            proxySupports = new float[RadialProxyMath.DirectionCount];
            proxyEndpointPool = new Vector3[ProxyEndpointCapacity];
            proxyEndpointCount = 0;
            proxyItemStarts = new int[ProxyItemCapacity];
            proxyItemLengths = new int[ProxyItemCapacity];
            proxyItemKeys = new string[ProxyItemCapacity];
            proxyItemIsCandidate = new bool[ProxyItemCapacity];
            proxyItemCount = 0;
            proxyCandidateKeys = new string[RadialProxyMath.MaxExtremeCandidates];
            proxyCandidateCount = 0;
            proxyAffectedIndices = new int[RadialProxyMath.DirectionCount];
            proxySolveScratch = new float[RadialProxyMath.DirectionCount];
            localProxyPoints = new Vector3[ProxyEndpointCapacity];
            proxyCorners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            proxyRemovedCorners = new Vector3[RadialProxyShellMath.AabbCornerCount];
            proxyEmptyPoints = new Vector3[0];
            proxyGroundSupportHeightM = 0f;
            proxyProtrusionCapWorldM = 0f;
            proxySkipCount = 0;
            proxySkipReason = ProxySkipNone;
            proxyRefusalCount = 0;
            proxyRefusalLogCount = 0;
            RadialProxyShellMath.TryClearShell(proxyDirections, proxySupports,
                ref proxyEndpointCount, ref proxyItemCount, proxyCandidateKeys,
                ref proxyCandidateCount);

            // Ball contact v2 (flag off by default): the tables are allocated here, not
            // behind the flag, so a runtime flip finds the scene's registered geometry.
            // Only when they do not exist yet: a host that registered before this Start
            // ran (component order between GameObjects is arbitrary) has already forced
            // the allocation, and re-allocating would wipe its scene geometry.
            if (staticObbCenter == null) AllocateContactV2Buffers();

            allowedKinds = new string[0];
            allowedKindCount = 0;

            ballCenterWorld = startCenterWorld;
            planarVelocity = Vector3.zero;
            headingDegrees = 0f;
            visualRollRotation = Quaternion.identity;
            if (ballVisualRollRoot != null) visualRollRotation = ballVisualRollRoot.rotation;
            discontinuous = true;

            maxCollisionRadiusWorldM = maxCoreRadiusWorldM * collisionToCoreRadiusRatio;

            if (GrowthLedgerMath.TryJoin(roundState, playerIds, confirmedVolumes, bankedScores,
                lifeStates, respawnAtSet, respawnAt, playerGenerations, ref playerCount,
                localPlayerId))
            {
                localRow = 0;
                hasLocalRow = true;
            }
            if (autoStartPlaying) TryBeginPlaying(0f);

            // Parity D6: the start diameter is a per-level parameter. A level that sets
            // startDiameterWorldM rewrites the baseline VOLUME from it here, before any
            // consumer reads the baseline; a level that does not keeps the serialized
            // baselineVolumeUnits (every scene authored before the field existed, i.e. the
            // 0.2997 m start ball). Continuous volume is NOT approved (PARITY_AUDIT 5.3),
            // so the ledger unit stays integer and only the baseline changes.
            ApplyStartDiameterWorldM();

            // The zero-volume mapping is the ball's start size: it is the baseline
            // for the growth fraction the motion layer consumes, and the floor the
            // conversion shrink clamps to.
            float coreRadius;
            float collisionRadius;
            float semanticDiameter;
            float effectiveVolumeUnits;
            if (SizeModelMath.TryDerive(0f, baselineVolumeUnits, cubicMetersPerVolumeUnit,
                maxCoreRadiusWorldM, collisionToCoreRadiusRatio,
                semanticDiameterPerCoreDiameter, out coreRadius, out collisionRadius,
                out semanticDiameter, out effectiveVolumeUnits))
            {
                baseCoreRadiusWorldM = coreRadius;
                coreRadiusWorldM = coreRadius;
                collisionRadiusWorldM = collisionRadius;
                semanticDiameterWorldM = semanticDiameter;
            }
            RecomputeConfirmedAndSize();

            // Publish the step limit and resolve the starting surface once, so the
            // diagnostics read a real surface before the first fixed step and a ball
            // placed above the floor (or teleported later) falls instead of snapping.
            float initialStepLimit;
            if (SupportSurfaceMath.TryStepHeightLimit(semanticDiameterWorldM,
                stepHeightOverBallDiameter, out initialStepLimit))
            {
                stepHeightLimitWorldM = initialStepLimit;
            }
            ResolveTerrainSupport(ballCenterWorld);

            Debug.Log("[BallMotor] Start ready diameter=" + semanticDiameterWorldM
                + " collisionRadius=" + collisionRadiusWorldM
                + " baselineUnits=" + baselineVolumeUnits
                + " startSize=" + (startDiameterWorldM > 0f ? "level" : "calibration")
                + " hasLocalRow=" + hasLocalRow
                + " terrainSupport=" + terrainSupportEnabled);
        }

        /// <summary>
        /// Makes the level's start diameter the ball's baseline, or keeps the serialized
        /// baseline volume when the level does not set one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Parity D6: the original's start diameter is a per-level value (5 cm for MAS1,
        /// 22.2 cm for Cancer, 30 cm for our first level), not a global constant, while the
        /// growth calibration is expressed against a baseline VOLUME. This method is the
        /// single conversion between the two: <c>baselineVolumeUnits</c> is rewritten to the
        /// volume of the requested start sphere and every consumer (the zero-volume mapping
        /// in Start, the growth mapping, the round reset and the bank shrink) then reads
        /// one baseline.
        /// </para>
        /// <para>
        /// The inverse has to agree with <see cref="SizeModelMath.TryDerive"/>: the core
        /// radius is the sphere radius of baseline * cubicMetersPerVolumeUnit, and the
        /// semantic diameter is that radius doubled times
        /// semanticDiameterPerCoreDiameter. 0 means unset, so a scene written before the
        /// field existed keeps its authored baseline (3.4390244 units = the 0.2997 m start
        /// ball). A start diameter the core cap cannot hold is refused and the authored
        /// baseline is kept: writing a baseline above the cap would make every derivation
        /// fail and the ball would silently stay at 0 diameter.
        /// </para>
        /// </remarks>
        private void ApplyStartDiameterWorldM()
        {
            if (!KatamariMath.IsFiniteFloat(startDiameterWorldM) || startDiameterWorldM <= 0f)
            {
                return;
            }

            float startCoreRadius = startDiameterWorldM * 0.5f / semanticDiameterPerCoreDiameter;
            // V = 4/3 * pi * r^3 with r the CORE radius (the semantic diameter is
            // 2 * core * semanticDiameterPerCoreDiameter), and one volume unit is
            // cubicMetersPerVolumeUnit. This is the zero-volume branch of
            // SizeModelMath.TryDerive solved for the baseline; it cannot call that method
            // because the baseline is its input, and it is not
            // TryVolumeUnitsForSemanticDiameter because that answers "confirmed units ABOVE a
            // baseline", not the baseline itself.
            float unitVolumeWorldM3 = (4f / 3f) * Mathf.PI * startCoreRadius
                * startCoreRadius * startCoreRadius;
            float units = unitVolumeWorldM3 / cubicMetersPerVolumeUnit;
            if (!KatamariMath.IsFiniteFloat(startCoreRadius) || startCoreRadius <= 0f ||
                !KatamariMath.IsFiniteFloat(units) || units <= 0f)
            {
                Debug.LogWarning("[BallMotor] startDiameterWorldM=" + startDiameterWorldM
                    + " is not usable; keeping baselineVolumeUnits=" + baselineVolumeUnits);
                return;
            }

            // Verified through the forward map instead of assumed: the candidate baseline must
            // map back to the requested diameter. A start ball the core cap cannot hold (or a
            // ratio that makes the round trip disagree) keeps the authored baseline, because
            // an out-of-range baseline would make every later derivation fail and the ball
            // would silently stay at 0 m.
            float coreRadius;
            float collisionRadius;
            float semanticDiameter;
            float effectiveVolumeUnits;
            if (!SizeModelMath.TryDerive(0f, units, cubicMetersPerVolumeUnit,
                maxCoreRadiusWorldM, collisionToCoreRadiusRatio,
                semanticDiameterPerCoreDiameter, out coreRadius, out collisionRadius,
                out semanticDiameter, out effectiveVolumeUnits))
            {
                Debug.LogWarning("[BallMotor] startDiameterWorldM=" + startDiameterWorldM
                    + " cannot be a baseline (the core cap is " + maxCoreRadiusWorldM
                    + "); keeping baselineVolumeUnits=" + baselineVolumeUnits);
                return;
            }
            if (Mathf.Abs(semanticDiameter - startDiameterWorldM) > startDiameterWorldM * 0.001f)
            {
                Debug.LogWarning("[BallMotor] startDiameterWorldM=" + startDiameterWorldM
                    + " does not round-trip through the size model (" + semanticDiameter
                    + "); keeping baselineVolumeUnits=" + baselineVolumeUnits);
                return;
            }
            baselineVolumeUnits = units;
        }

        /// <summary>
        /// Allocates the set buffers the first time any behaviour writes one, and reports
        /// whether they are usable.
        /// </summary>
        /// <remarks>
        /// <para>
        /// See the ownership note above the field declarations. Called by Start and by every
        /// entry point that writes a scene set, so the component works in either Start order.
        /// The first writer allocates the WHOLE set, every destination at its own capacity,
        /// which is also what makes a null or truncated destination harmless before Start.
        /// </para>
        /// <para>
        /// After that first allocation the set is never replaced: the registered entries and
        /// every reader of them (the sweep copy RebuildObstacles builds, the support query)
        /// hold these exact arrays, so re-allocating would silently empty the ball's world.
        /// A destination that is then null or shorter than its capacity therefore cannot be
        /// grown here; it is a refused call with a reason instead - "set buffers unavailable"
        /// for the blocking and support sets, the ramp's <see cref="RampRefusalNoBuffers"/>,
        /// and a plain false for AddPlane/AddBox, which have no reason channel. A refused call
        /// writes nothing (rule 3 of the ownership note; the F1 null reference cannot come
        /// back through this door).
        /// </para>
        /// </remarks>
        private bool EnsureRegistrationBuffers()
        {
            if (!registrationBuffersReady) AllocateRegistrationBuffers();
            return RegistrationBuffersAtCapacity();
        }

        /// <summary>
        /// True when every scene-set destination exists and can hold a full set.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both halves of every pair are checked, not one representative: a full minima array
        /// next to a short maxima array is exactly the state that writes past the end of the
        /// shorter one, and it is what the ownership test's undersized case exercises.
        /// </para>
        /// <para>
        /// The capacities are this component's own constants
        /// (<see cref="MaxPlaneCount"/>, <see cref="MaxBoxCount"/>,
        /// <see cref="MaxBlockingBoxCount"/>, <see cref="MaxSupportPropCount"/>,
        /// <see cref="SupportSurfaceMath.MaxRampCount"/>), so "a full set" is decided in one
        /// place. Lengths are compared with <c>&lt;</c> rather than equality, so a longer
        /// array stays usable; nothing writes past a capacity.
        /// </para>
        /// </remarks>
        private bool RegistrationBuffersAtCapacity()
        {
            if (planeNormalBuffer == null || planeOffsetBuffer == null) return false;
            if (boxMinimumBuffer == null || boxMaximumBuffer == null) return false;
            if (blockingMinimumBuffer == null || blockingMaximumBuffer == null) return false;
            if (supportPropMinimumBuffer == null || supportPropMaximumBuffer == null) return false;
            if (rampCenterBuffer == null || rampNormalBuffer == null ||
                rampRightBuffer == null) return false;
            if (rampHalfWidthBuffer == null || rampHalfRunBuffer == null) return false;
            if (planeNormalBuffer.Length < MaxPlaneCount) return false;
            if (planeOffsetBuffer.Length < MaxPlaneCount) return false;
            if (boxMinimumBuffer.Length < MaxBoxCount) return false;
            if (boxMaximumBuffer.Length < MaxBoxCount) return false;
            if (blockingMinimumBuffer.Length < MaxBlockingBoxCount) return false;
            if (blockingMaximumBuffer.Length < MaxBlockingBoxCount) return false;
            if (supportPropMinimumBuffer.Length < MaxSupportPropCount) return false;
            if (supportPropMaximumBuffer.Length < MaxSupportPropCount) return false;
            if (rampCenterBuffer.Length < SupportSurfaceMath.MaxRampCount) return false;
            if (rampNormalBuffer.Length < SupportSurfaceMath.MaxRampCount) return false;
            if (rampRightBuffer.Length < SupportSurfaceMath.MaxRampCount) return false;
            if (rampHalfWidthBuffer.Length < SupportSurfaceMath.MaxRampCount) return false;
            if (rampHalfRunBuffer.Length < SupportSurfaceMath.MaxRampCount) return false;
            return true;
        }

        /// <summary>
        /// Allocates the scene-set buffers once. Kept apart from Start so the "first writer
        /// allocates, Start only ensures" rule has exactly one allocation site.
        /// </summary>
        private void AllocateRegistrationBuffers()
        {
            planeNormalBuffer = new Vector3[MaxPlaneCount];
            planeOffsetBuffer = new float[MaxPlaneCount];
            boxMinimumBuffer = new Vector3[MaxBoxCount];
            boxMaximumBuffer = new Vector3[MaxBoxCount];
            blockingMinimumBuffer = new Vector3[MaxBlockingBoxCount];
            blockingMaximumBuffer = new Vector3[MaxBlockingBoxCount];
            supportPropMinimumBuffer = new Vector3[MaxSupportPropCount];
            supportPropMaximumBuffer = new Vector3[MaxSupportPropCount];
            rampCenterBuffer = new Vector3[SupportSurfaceMath.MaxRampCount];
            rampNormalBuffer = new Vector3[SupportSurfaceMath.MaxRampCount];
            rampRightBuffer = new Vector3[SupportSurfaceMath.MaxRampCount];
            rampHalfWidthBuffer = new float[SupportSurfaceMath.MaxRampCount];
            rampHalfRunBuffer = new float[SupportSurfaceMath.MaxRampCount];
            planeCount = 0;
            boxCount = 0;
            blockingBoxCount = 0;
            blockingRefusalCount = 0;
            blockingRefusalReason = "";
            supportPropCount = 0;
            supportPropRefusalCount = 0;
            supportPropRefusalReason = "";
            rampCount = 0;
            rampRefusalCount = 0;
            rampRefusalCode = SupportSurfaceMath.RampRefusalNone;
            rampRefusalSlopeDegrees = 0f;
            // The sweep arrays are derived from the counts above (and are re-created by
            // every rebuild), so the empty rebuild belongs to the same first-time step.
            RebuildObstacles();
            registrationBuffersReady = true;
        }

        /// <summary>
        /// Allocates the candidate queue the first time the host submits one, and reports
        /// whether it is usable.
        /// </summary>
        /// <remarks>
        /// Same ownership rule as the scene sets: the pickup field's scan can reach
        /// SubmitCandidate before this component's Start, so the first submit allocates the
        /// whole queue (every destination at <see cref="PendingCandidateCapacity"/>). After
        /// that the queue is never replaced - it holds the accepted entries - so a missing or
        /// short destination is answered false and logged once, never written into.
        /// </remarks>
        private bool EnsureCandidateBuffers()
        {
            if (!candidateBuffersReady)
            {
                pendingAbsorbable = new bool[PendingCandidateCapacity];
                pendingKinds = new string[PendingCandidateCapacity];
                pendingBounds = new Vector3[PendingCandidateCapacity];
                pendingPositions = new Vector3[PendingCandidateCapacity];
                pendingValues = new float[PendingCandidateCapacity];
                pendingVolumeK = new float[PendingCandidateCapacity];
                pendingItemIds = new string[PendingCandidateCapacity];
                pendingGenerations = new int[PendingCandidateCapacity];
                pendingAccepted = new bool[PendingCandidateCapacity];
                pendingSubmitTimes = new float[PendingCandidateCapacity];
                pendingCandidateCount = 0;
                candidateBuffersReady = true;
            }
            return CandidateBuffersAtCapacity();
        }

        /// <summary>
        /// True when every queue destination exists and can hold a full queue.
        /// </summary>
        /// <remarks>
        /// Every array is checked, for the same reason the scene-set pairs are: the queue is
        /// written by index up to <see cref="PendingCandidateCapacity"/>, so one short array
        /// is an out-of-range write waiting for the first submit.
        /// </remarks>
        private bool CandidateBuffersAtCapacity()
        {
            if (pendingAbsorbable == null || pendingKinds == null) return false;
            if (pendingBounds == null || pendingPositions == null) return false;
            if (pendingValues == null || pendingVolumeK == null) return false;
            if (pendingItemIds == null) return false;
            if (pendingGenerations == null || pendingAccepted == null) return false;
            if (pendingSubmitTimes == null) return false;
            if (pendingAbsorbable.Length < PendingCandidateCapacity) return false;
            if (pendingKinds.Length < PendingCandidateCapacity) return false;
            if (pendingBounds.Length < PendingCandidateCapacity) return false;
            if (pendingPositions.Length < PendingCandidateCapacity) return false;
            if (pendingValues.Length < PendingCandidateCapacity) return false;
            if (pendingVolumeK.Length < PendingCandidateCapacity) return false;
            if (pendingItemIds.Length < PendingCandidateCapacity) return false;
            if (pendingGenerations.Length < PendingCandidateCapacity) return false;
            if (pendingAccepted.Length < PendingCandidateCapacity) return false;
            if (pendingSubmitTimes.Length < PendingCandidateCapacity) return false;
            return true;
        }

        private void Update()
        {
            localClockSeconds = localClockSeconds + Time.deltaTime;
            fixedStepAccumulator = fixedStepAccumulator + Time.deltaTime;

            // Cap the backlog instead of replaying it: a long frame must not turn
            // into a burst of catch-up steps that the player experiences as a jump.
            float maximumAccumulator = fixedStepSeconds * (float)maxStepsPerFrame;
            if (fixedStepAccumulator > maximumAccumulator)
            {
                fixedStepAccumulator = maximumAccumulator;
            }

            int steps = 0;
            while (fixedStepAccumulator >= fixedStepSeconds && steps < maxStepsPerFrame)
            {
                fixedStepAccumulator = fixedStepAccumulator - fixedStepSeconds;
                StepBall(fixedStepSeconds);
                steps = steps + 1;
            }
            ApplyPresentation();
        }

        /// <summary>
        /// Runs one fixed step through the nine stages in their fixed order.
        /// </summary>
        /// <remarks>
        /// Stages 1-2 (intent, motion) and 5-9 (absorb, ledger, size, growth push-out,
        /// visual roll, follow rig) are shared by both contact models. The position
        /// authority between them is chosen by <c>contactModelV2Enabled</c>:
        /// <see cref="StepPositionV1"/> is the pre-v2 order
        /// (RefreshStepMask -> TrySweepSphere -> TryResolveSupport -> GroundContactMath),
        /// <see cref="StepContactV2"/> is the one contact solver of
        /// Docs/Design/BallMotor/CONTACT_MODEL_V2.md §7.3. Rollback = flag off.
        /// </remarks>
        private void StepBall(float dt)
        {
            authorityFrame = authorityFrame + 1;

            // 1. ControlIntentMath: the only place a raw axis becomes an intent,
            //    and the only place a menu suppresses it.
            StepInput();

            // 2. BallMotionMath: planar velocity and the requested displacement.
            //    The request is handed on unclipped; stopping a fast ball is the
            //    sweep's job, not this layer's.
            Vector3 requestedDisplacement = Vector3.zero;
            float nextHeading;
            Vector3 nextVelocity;
            Vector3 stepDisplacement;
            if (BallMotionMath.TryStep(inputForward, inputTurn, inputLateral,
                headingDegrees, planarVelocity, dt, growthFraction, maxSpeedMps,
                accelerationMps2, brakingMps2, frictionPerSecond,
                turnRateDegreesPerSecond, maxLateralMps, growthSpeedFactor,
                growthTurnFactor, out nextHeading, out nextVelocity, out stepDisplacement))
            {
                headingDegrees = nextHeading;
                planarVelocity = nextVelocity;
                requestedDisplacement = stepDisplacement;
            }

            // 2.5 - 4. The position authority for this step. Both write the same three
            //     values, so the stages below are shared and neither path carries a
            //     branch: with the flag off the v1 method runs the original statements in
            //     the original order.
            Vector3 groundedCenter = Vector3.zero;
            Vector3 followCenter = Vector3.zero;
            Vector3 supportNormal = groundPlaneNormal;
            if (contactModelV2Enabled)
            {
                StepContactV2(dt, requestedDisplacement, out groundedCenter, out followCenter,
                    out supportNormal);
            }
            else
            {
                StepPositionV1(dt, requestedDisplacement, out groundedCenter, out followCenter,
                    out supportNormal);
            }

            // 5. PickupAbsorbMath: a decision per candidate, at the final position
            //    only, so nothing is absorbed through a wall or from the old centre.
            EvaluatePendingCandidates(groundedCenter);

            // 6. GrowthLedgerMath: a decision becomes volume only through a commit.
            CommitAcceptedAbsorbs();

            // 7. SizeModelMath: the three sizes are a function of the confirmed
            //    volume, never an accumulation of it. The confirmed total is
            //    recomputed by the one method that owns that identity, so the
            //    per-step stage and a conversion cannot disagree about it.
            RecomputeConfirmedAndSize();

            // 7.5 Growth push-out (design §4.2; v2 only). Absorbing grows the radius
            //     inside one step, so the ball can end a step embedded in the wall it
            //     just grew into. Guarded, so the v1 frame does not change.
            if (contactModelV2Enabled)
            {
                ApplyGrowthPushOut(ref groundedCenter, ref followCenter, ref supportNormal);
            }

            // 8. VisualRollMath: the actual centre move, with the discontinuity flag
            //    supplied so a teleport frame holds the pose.
            Vector3 actualDisplacement = groundedCenter - ballCenterWorld;
            Quaternion nextRotation;
            // The roll axis is the support plane's normal, so a step or a ramp rolls
            // correctly (F8 of the design). With terrain support off this is exactly
            // the raw ground normal the call used before P1.
            if (VisualRollMath.TryAdvanceRotation(visualRollRotation, actualDisplacement,
                supportNormal, coreRadiusWorldM, discontinuous, true,
                maxRollDisplacementM, out nextRotation))
            {
                visualRollRotation = nextRotation;
            }

            // 9. FollowRigMath: consumes this step's final pose, returns presentation
            //    only, and never feeds gameplay state. It is given the smooth core
            //    centre, not the proxy-lifted one, so the camera does not bob.
            UpdateFollowRig(followCenter, dt);

            ballCenterWorld = groundedCenter;
            discontinuous = false;
        }

        /// <summary>
        /// The v1 position authority (design stages 2.5-4 plus the v1 stuck recovery):
        /// step-mask refresh, inflated-AABB sweep, terrain support selection and the
        /// analytic ground solve.
        /// </summary>
        /// <remarks>
        /// This is the original body of <see cref="StepBall"/>, moved here unchanged so
        /// the flag can choose between two position authorities without either one
        /// carrying a branch. Statement order, comments and every constant are the
        /// pre-v2 ones; the only textual difference is that the three values the shared
        /// tail stages read are out parameters instead of locals, and they are
        /// initialised first so every path assigns them (CS0165):
        /// <c>groundedCenter</c> (the final centre), <c>followCenter</c> (the smooth
        /// camera anchor) and <c>supportNormal</c> (the visual-roll axis). With
        /// <c>contactModelV2Enabled</c> false this method is the only position path, so
        /// rollback is the flag alone.
        /// </remarks>
        private void StepPositionV1(float dt, Vector3 requestedDisplacement,
            out Vector3 groundedCenter, out Vector3 followCenter, out Vector3 supportNormal)
        {
            groundedCenter = Vector3.zero;
            followCenter = Vector3.zero;
            supportNormal = groundPlaneNormal;
            // 2.5 Terrain support mask (P1): decide which static boxes are steps and
            //     rebuild the sweep obstacle arrays without them. This runs BEFORE the
            //     sweep because entering a step means being allowed inside the box's
            //     expanded AABB; it uses the previous step's support height, which is
            //     the design's accepted one-step lag. The mask and the support table call
            //     the SAME SupportSurfaceMath predicate with the same reference, so a box
            //     the sweep drops is always one the support table can select.
            if (terrainSupportEnabled) RefreshStepMask();

            // 3. SweepMath: actual displacement. Only this — never the requested
            //    motion — may reach the visual roll later, so a blocked step does
            //    not spin the ball into a wall.
            Vector3 sweptCenter = ballCenterWorld;
            bool sweepAccepted = false;
            bool sweepStuck = false;
            // The sweep also reports `blocked`, but this layer has no reader for it (a
            // blocked step already shows up as an accepted sweep whose centre did not
            // move, plus the stuck timer), so it is requested and dropped rather than
            // stored in a field nothing reads.
            bool sweepBlocked = false;
            Vector3 finalPosition;
            Vector3 remainingDisplacement;
            Vector3 slideDirection;
            int contactCount;
            if (SweepMath.TrySweepSphere(ballCenterWorld, requestedDisplacement,
                collisionRadiusWorldM, planeNormals, planeOffsets, boxMinima, boxMaxima,
                ContactCapacity, separationEpsilon, contactCenters, contactPoints,
                contactNormals, contactTimes, contactObstacleIndices,
                contactPenetrationDepths, out contactCount, out finalPosition,
                out remainingDisplacement, out slideDirection, out sweepStuck,
                out sweepBlocked))
            {
                sweptCenter = finalPosition;
                sweepAccepted = true;
                // SweepMath stops writing contacts at ContactCapacity (4) and
                // reports no truncation flag, so the motor can only record that
                // the buffer filled at least once; any further contact in that
                // step is dropped inside the sweep. See the capacity remarks on
                // RebuildObstacles.
                if (contactCount >= ContactCapacity) contactBufferSaturated = true;
            }
            // Wall response: the sweep only stops the POSITION at the wall. Without
            // this the planar velocity keeps its full speed into the wall, so the
            // ball sticks instead of sliding and carries a stale velocity after it
            // turns away. The sweep reports the contacts, the sweep layer orients
            // them outward, and the sweep layer removes the inward part; this layer
            // only orders the calls. Tangential velocity is untouched by design.
            //
            // Only side walls and boxes are in the sweep: the ground plane is
            // GroundContactMath's analytic plane, never a sweep obstacle, so ground
            // contact can neither brake nor steer the ball here. A failed or empty
            // contact list leaves the velocity alone. Only contacts recorded before
            // the contact buffer filled are corrected, so a saturated buffer
            // (contactBufferSaturated) removes less than it should.
            if (sweepAccepted && contactCount > 0)
            {
                int wallNormalCount;
                if (SweepMath.TryCollectWallContactNormals(contactCenters, contactNormals,
                    contactObstacleIndices, contactCount, planeNormals, planeOffsets,
                    boxMinima, boxMaxima, wallContactNormals, out wallNormalCount))
                {
                    Vector3 correctedVelocity;
                    if (SweepMath.TryRemoveWallNormalVelocity(planarVelocity,
                        wallContactNormals, wallNormalCount, out correctedVelocity))
                    {
                        planarVelocity = correctedVelocity;
                    }
                }
            }

            // 3.5 Terrain support selection (P1): which surface is under the ball
            //     centre at the swept XZ, and does that surface drop away (a ledge)?
            //     The selection is a pure call into SupportSurfaceMath; this layer only
            //     carries the surface pair into stage 4 and the gravity state.
            ResolveTerrainSupport(sweptCenter);

            // 3.6 Optional ramp downhill acceleration (PROVISIONAL, off by default).
            //     The motion layer produced this step's displacement already, so a
            //     non-zero scale takes effect from the next step (a 20 ms lag). At the
            //     default scale 0 this block is skipped and the step is bit-for-bit the
            //     P1 behaviour. The decision math lives in SupportSurfaceMath; this layer
            //     only carries the velocity back.
            if (terrainSupportEnabled && !isFalling && rampTangentialGravityScale > 0f &&
                SupportSurfaceMath.IsRampSurfaceId(supportSurfaceId))
            {
                Vector3 slopeVelocity;
                if (SupportSurfaceMath.TryApplyRampTangentialGravity(planarVelocity,
                    supportSurfaceNormalWorld, gravityMps2, rampTangentialGravityScale, dt,
                    maxSpeedMps, out slopeVelocity))
                {
                    planarVelocity = slopeVelocity;
                }
            }

            // 4. GroundContactMath: support height and the normal-only correction.
            //
            // Two solves per step, both cheap. The first uses the radial proxy shell's
            // clamped endpoints, so a long rod pointing at the ground lifts the core by
            // up to maxProxyProtrusion (and no more); the second uses an empty proxy array
            // and is the same plane solved for the smooth core sphere. Only the first
            // moves the ball; the second is where the follow rig reads the centre, so a
            // bump cannot bob the camera (the height filter alone would only soften it).
            groundedCenter = sweptCenter;
            followCenter = sweptCenter;
            // Every out local is initialised at its declaration: the first ground solve
            // sits behind two conditions (a non-empty shell AND a successful proxy
            // build), so the compiler cannot prove these are assigned before the reads
            // below. This is an initialiser, not a change of logic.
            float supportHeight = 0f;
            Vector3 correctedCenter = Vector3.zero;
            float centerNormalCoordinate = 0f;
            Vector3 planeAnchor = Vector3.zero;
            float appliedNormalCorrection = 0f;
            float normalSpeedPerturbation = 0f;
            bool liftClamped = false;
            bool dropClamped = false;
            float proxyProtrusionCap = maxProxyProtrusionPerCoreRadius * coreRadiusWorldM;
            float absoluteProtrusionCap = maxProxyProtrusionWorldM;
            if (!KatamariMath.IsFiniteFloat(absoluteProtrusionCap) || absoluteProtrusionCap < 0f)
            {
                absoluteProtrusionCap = 0f;
            }
            if (!KatamariMath.IsFiniteFloat(proxyProtrusionCap) || proxyProtrusionCap < 0f)
            {
                proxyProtrusionCap = 0f;
            }
            else if (proxyProtrusionCap > absoluteProtrusionCap)
            {
                proxyProtrusionCap = absoluteProtrusionCap;
            }
            proxyProtrusionCapWorldM = proxyProtrusionCap;
            float proxyLiftCap = maxLiftPerStep;
            if (proxyEndpointCount > 0 && maxProxyLiftPerStep < proxyLiftCap)
            {
                proxyLiftCap = maxProxyLiftPerStep;
            }
            float coreLiftCap = maxLiftPerStep;
            float proxyDropCap = maxDropPerStep;
            float coreDropCap = maxDropPerStep;

            // P1 terrain: the plane this solve sees is the SELECTED support, not the raw
            // ground, and a step crossing gets its own one-step lift allowance (§4.4)
            // instead of the 0.04 m proxy cap. A fall uses this step's gravity budget as
            // both clamps, so descent is gravity and not the old 0.1 m-per-step cap.
            // With terrain support off the pair is the raw ground plane and the caps are
            // exactly the pre-P1 ones, so the frame is bit-identical to before.
            supportNormal = groundPlaneNormal;
            float supportOffset = groundPlaneOffset;
            if (terrainSupportEnabled)
            {
                supportNormal = supportSurfaceNormalWorld;
                supportOffset = supportNormal.x * supportSurfacePointWorld.x
                    + supportNormal.y * supportSurfacePointWorld.y
                    + supportNormal.z * supportSurfacePointWorld.z;
                if (isFalling)
                {
                    float nextFallSpeed;
                    float fallBudget;
                    if (SupportSurfaceMath.TryIntegrateFall(fallSpeedMps, gravityMps2, dt,
                        maxFallSpeedMps, out nextFallSpeed, out fallBudget))
                    {
                        fallSpeedMps = nextFallSpeed;
                        proxyDropCap = fallBudget;
                        coreDropCap = fallBudget;
                        proxyLiftCap = fallBudget;
                        coreLiftCap = fallBudget;
                    }
                    else
                    {
                        // A corrupt gravity setting must not leave the ball falling
                        // forever: drop the state and let the old clamp crawl it down.
                        isFalling = false;
                        fallSpeedMps = 0f;
                    }
                }
                else if (supportSurfaceId != previousSupportSurfaceId &&
                    SupportSurfaceMath.IsStepCrossing(
                        supportSurfaceHeightWorldM - previousSupportHeightWorldM,
                        minStepHeightM))
                {
                    // Only the step that crosses a step gets the large allowance; the next
                    // step is back to 0.1 / 0.04, so a long rod still cannot pop the ball.
                    float stepLift = stepHeightLimitWorldM;
                    if (stepLift > maxStepUpLiftWorldM) stepLift = maxStepUpLiftWorldM;
                    proxyLiftCap = stepLift;
                    coreLiftCap = stepLift;
                }
            }
            bool groundAccepted = false;
            if (proxyEndpointCount > 0 &&
                RadialProxyShellMath.TryBuildContactPoints(coreRadiusWorldM, proxyProtrusionCap,
                    proxyEndpointPool, proxyEndpointCount, localProxyPoints))
            {
                groundAccepted = GroundContactMath.TrySolve(coreRadiusWorldM, visualRollRotation,
                    localProxyPoints, supportNormal, supportOffset, sweptCenter,
                    proxyLiftCap, proxyDropCap, 0f, maxProxyNormalSpeedPerturbation,
                    out supportHeight, out correctedCenter, out centerNormalCoordinate,
                    out planeAnchor, out appliedNormalCorrection, out normalSpeedPerturbation,
                    out liftClamped, out dropClamped);
            }
            if (!groundAccepted)
            {
                // Empty shell, or a rejected proxy build: keep the core-sphere behaviour
                // rather than leaving the ball ungrounded.
                groundAccepted = GroundContactMath.TrySolve(coreRadiusWorldM, visualRollRotation,
                    proxyEmptyPoints, supportNormal, supportOffset, sweptCenter,
                    coreLiftCap, coreDropCap, 0f, maxProxyNormalSpeedPerturbation,
                    out supportHeight, out correctedCenter, out centerNormalCoordinate,
                    out planeAnchor, out appliedNormalCorrection, out normalSpeedPerturbation,
                    out liftClamped, out dropClamped);
            }
            if (groundAccepted)
            {
                groundedCenter = correctedCenter;
                proxyGroundSupportHeightM = supportHeight;
                // Arriving ends the fall: the solve reached the surface without being
                // drop-clamped by this step's fall budget. A failed solve keeps the state.
                if (isFalling)
                {
                    isFalling = SupportSurfaceMath.ResolveFallState(true, false, true,
                        dropClamped);
                    if (!isFalling) fallSpeedMps = 0f;
                }
                if (debugTraceProxySupport && proxyTraceLinesLogged < TraceLineLimit)
                {
                    proxyTraceLinesLogged = proxyTraceLinesLogged + 1;
                    Debug.Log("[ProxyTrace] t=" + localClockSeconds
                        + " support=" + supportHeight
                        + " core=" + coreRadiusWorldM
                        + " lift=" + (supportHeight - coreRadiusWorldM)
                        + " cap=" + proxyProtrusionCap
                        + " points=" + proxyEndpointCount
                        + " candidates=" + proxyCandidateCount
                        + " centerY=" + correctedCenter.y);
                }
            }

            // Camera anchor: the same plane without the proxy points. The per-step lift/drop
            // clamps are deliberately made huge for this call: it does not move the ball, and
            // the anchor must be the EXACT core-sphere centre, not a lagging approximation of
            // it. The follow rig's own height filter and teleport snap handle presentation.
            float cameraAnchorUnclamped = 10f;
            // While falling the anchor must follow the SAME descent as the ball; with the
            // unclamped value above it would snap to the surface underneath and drag the
            // camera down while the ball is still in the air. Grounded, the unclamped call
            // is still the exact core centre (the correction is ~0 on a flat plane).
            float cameraAnchorLift = cameraAnchorUnclamped;
            float cameraAnchorDrop = cameraAnchorUnclamped;
            if (terrainSupportEnabled && isFalling)
            {
                cameraAnchorLift = coreLiftCap;
                cameraAnchorDrop = coreDropCap;
            }
            float smoothSupportHeight;
            Vector3 smoothCenter;
            float smoothNormalCoordinate;
            Vector3 smoothAnchor;
            float smoothCorrection;
            float smoothPerturbation;
            bool smoothLiftClamped;
            bool smoothDropClamped;
            if (GroundContactMath.TrySolve(coreRadiusWorldM, visualRollRotation, proxyEmptyPoints,
                supportNormal, supportOffset, sweptCenter, cameraAnchorLift,
                cameraAnchorDrop, 0f, maxProxyNormalSpeedPerturbation,
                out smoothSupportHeight, out smoothCenter, out smoothNormalCoordinate,
                out smoothAnchor, out smoothCorrection, out smoothPerturbation,
                out smoothLiftClamped, out smoothDropClamped))
            {
                followCenter = smoothCenter;
            }

            // Stuck recovery sits between the position stages and the presentation
            // stages: the sweep reports the condition, and the reset policy is the
            // motor's (the motion layer explicitly leaves it to this layer). The
            // discontinuity flag is what keeps the jump out of the visual roll.
            // A rejected sweep tells us nothing about the contact state, so the
            // timer keeps its previous value instead of being cleared.
            if (sweepAccepted)
            {
                if (sweepStuck)
                {
                    stuckSeconds = stuckSeconds + dt;
                }
                else
                {
                    stuckSeconds = 0f;
                }
            }
            if (stuckSeconds > stuckTimeoutSeconds)
            {
                groundedCenter = startCenterWorld;
                followCenter = startCenterWorld;
                planarVelocity = Vector3.zero;
                stuckSeconds = 0f;
                discontinuous = true;
            }
        }



        /// <summary>
        /// Allocates the v2 tables once. They are allocated whatever the flag says, so a
        /// runtime flag flip finds the geometry the scene already registered (an AddBox
        /// that ran before the flip produced its OBB too). About 25 KB in total, which is
        /// the design's budget (design §1.3).
        /// </summary>
        private void AllocateContactV2Buffers()
        {
            staticObbCenter = new Vector3[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbAxisX = new Vector3[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbAxisY = new Vector3[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbAxisZ = new Vector3[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbHalfExtents = new Vector3[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbSurfaceId = new int[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbRole = new int[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbBoundingRadius = new float[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            staticObbCount = 0;
            staticObbOverflowCount = 0;
            staticObbRefusalCount = 0;
            staticObbRefusalReason = "";
            propObbCount = 0;
            propObbOverflowCount = 0;
            propObbRefusalCount = 0;
            propObbRefusalReason = "";

            nearObbCenter = new Vector3[ContactTuning.MaxNearObbCount];
            nearObbAxisX = new Vector3[ContactTuning.MaxNearObbCount];
            nearObbAxisY = new Vector3[ContactTuning.MaxNearObbCount];
            nearObbAxisZ = new Vector3[ContactTuning.MaxNearObbCount];
            nearObbHalfExtents = new Vector3[ContactTuning.MaxNearObbCount];
            nearObbBoundingRadius = new float[ContactTuning.MaxNearObbCount];
            nearObbSurfaceId = new int[ContactTuning.MaxNearObbCount];
            nearObbRole = new int[ContactTuning.MaxNearObbCount];
            nearObbKind = new int[ContactTuning.MaxNearObbCount];
            nearObbRadius = new float[ContactTuning.MaxNearObbCount];
            nearObbHalfHeight = new float[ContactTuning.MaxNearObbCount];
            nearObbAxis = new int[ContactTuning.MaxNearObbCount];
            nearObbHullStart = new int[ContactTuning.MaxNearObbCount];
            nearObbHullCount = new int[ContactTuning.MaxNearObbCount];
            nearObbFaceStart = new int[ContactTuning.MaxNearObbCount];
            nearObbFaceCount = new int[ContactTuning.MaxNearObbCount];
            nearObbSlot = new int[ContactTuning.MaxNearObbCount];
            nearObbCount = 0;

            int proxySlots = ContactTuning.MaxStaticObbCount + MaxPropObbCount;
            staticProxyKind = new int[proxySlots];
            staticProxyRadius = new float[proxySlots];
            staticProxyHalfHeight = new float[proxySlots];
            staticProxyAxis = new int[proxySlots];
            staticProxyHullStart = new int[proxySlots];
            staticProxyHullCount = new int[proxySlots];
            staticProxyFaceStart = new int[proxySlots];
            staticProxyFaceCount = new int[proxySlots];
            propProxyHullPoints =
                new Vector3[MaxPropObbCount * ProxyContactMath.MaxHullPoints];
            propProxyFaceNormals =
                new Vector3[MaxPropObbCount * ProxyContactMath.MaxHullFaces];
            propProxyFaceOffsets =
                new float[MaxPropObbCount * ProxyContactMath.MaxHullFaces];
            propProxyFaceA = new int[MaxPropObbCount * ProxyContactMath.MaxHullFaces];
            propProxyFaceB = new int[MaxPropObbCount * ProxyContactMath.MaxHullFaces];
            propProxyFaceC = new int[MaxPropObbCount * ProxyContactMath.MaxHullFaces];
            propProxyCount = 0;
            propProxyOverflowCount = 0;
            propProxyRefusalCount = 0;
            propProxyRefusalReason = "";
            // One scratch slot per entry of the LOGICAL table (static prefix + prop
            // block): TrySelectWindow refuses a table longer than its scratch buffers.
            windowScratchIndices = new int[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            windowScratchDistances =
                new float[ContactTuning.MaxStaticObbCount + MaxPropObbCount];
            windowSelectedIndices = new int[ContactTuning.MaxNearObbCount];
            windowPreviousSurfaceIds = new int[ContactTuning.MaxNearObbCount];
            windowPreviousCount = 0;
            nearRebuildCount = 0;
            windowDroppedCount = 0;
            windowDroppedTotal = 0;
            windowOverflowCount = 0;
            windowSaturatedCount = 0;
            contactIncomplete = false;
            lastWindowOverflowLogSeconds = 0f;

            contactNormalsV2 = new Vector3[ContactTuning.ContactCapacity];
            contactPointsV2 = new Vector3[ContactTuning.ContactCapacity];
            contactDepthsV2 = new float[ContactTuning.ContactCapacity];
            contactAnglesV2 = new float[ContactTuning.ContactCapacity];
            contactSurfaceIdsV2 = new int[ContactTuning.ContactCapacity];
            contactRolesV2 = new int[ContactTuning.ContactCapacity];
            contactFeaturesV2 = new int[ContactTuning.ContactCapacity];
            contactClassesV2 = new int[ContactTuning.ContactCapacity];
            contactShapesV2 = new int[ContactTuning.ContactCapacity];
            v2ContactCount = 0;
            v2CoreContactCount = 0;
            v2NeedleContactCount = 0;
            contactResidualMax = 0f;
            v2SubstepCount = 0;
            v2DominantSurfaceId = -1;
            v2DominantClass = ContactCodes.ClassNone;
            contactAirborne = true;
            contactReportNormal = new Vector3(0f, 1f, 0f);

            contactNeedleSupports = new float[RadialProxyMath.DirectionCount];
            contactNeedleIndices = new int[RadialProxyMath.DirectionCount];
            contactNeedleNormals = new Vector3[MaxNeedleContacts];
            contactNeedlePoints = new Vector3[MaxNeedleContacts];
            contactNeedleDepths = new float[MaxNeedleContacts];
            contactNeedleAngles = new float[MaxNeedleContacts];
            contactNeedleSurfaceIds = new int[MaxNeedleContacts];
            contactNeedleRoles = new int[MaxNeedleContacts];
            contactNeedleClasses = new int[MaxNeedleContacts];
            contactNeedleShapes = new int[MaxNeedleContacts];
            needleCompactCount = 0;
            needleContactCount = 0;
            contactPreviousCenterWorld = ballCenterWorld;
            contactPreviousRotation = Quaternion.identity;
            contactHasPreviousFrame = false;

            climbActive = false;
            climbAnchorY = 0f;
            climbSurfaceId = -1;
            climbCooldown = 0f;
            climbLocked = false;
            climbAlongSpeed = 0f;
            climbHeightWorldM = 0f;
            climbSpeedWorldMps = 0f;
            climbWallNormalWorld = new Vector3(0f, 0f, 1f);
            climbContactAngleDeg = 90f;
            contactVerticalVelocity = 0f;

            contactStableCenter = ballCenterWorld;
            hasContactStableCenter = false;
            lastStableWriteSeconds = 0f;
            roundStartCenter = startCenterWorld;
            hasRoundStartCenter = true;
            recoveryCount = 0;
            recoveryState = ContactRecoveryStateNone;
            lastRecoveryReason = 0;
            recoveryWindowStartSeconds = 0f;
            recoveryHoldLogged = false;
            softRecoveryArmed = true;
            contactV2ClockSeconds = 0f;

            contactRadiusUsedWorldM = 0f;
            sizeApplyDeferred = false;
            sizeApplyDeferredCount = 0;
            contactDriveDirection = new Vector3(0f, 0f, 1f);
            lastStepMicros = 0f;
            contactSolveFailureCount = 0;
            contactRejectedStepCount = 0;
            supportFromNeedle = false;
            supportFromClimb = false;

            growthContactNormals = new Vector3[ContactTuning.ContactCapacity];
            growthContactPoints = new Vector3[ContactTuning.ContactCapacity];
            growthContactDepths = new float[ContactTuning.ContactCapacity];
            growthContactAngles = new float[ContactTuning.ContactCapacity];
            growthContactSurfaceIds = new int[ContactTuning.ContactCapacity];
            growthContactRoles = new int[ContactTuning.ContactCapacity];
            growthContactFeatures = new int[ContactTuning.ContactCapacity];
            growthContactClasses = new int[ContactTuning.ContactCapacity];
        }

        /// <summary>
        /// The v2 position authority: near window, one contact solve, support and climb
        /// write-back, the needle pass, growth-independent stuck recovery (design §7.3
        /// stages 2.5-4).
        /// </summary>
        /// <remarks>
        /// <para>
        /// All geometry work is a call into <see cref="ContactSolveMath"/> or
        /// <see cref="BallShapeMath"/>; this method only orders the calls, owns the state
        /// the solver cannot hold (UdonSharp has no structs or static fields) and writes
        /// the readings the diagnostics report. The contact solve replaces four v1 stages
        /// at once: the step mask is gone because a step is now a contact whose normal
        /// points up, the inflated-AABB sweep is gone because the query is exact, the
        /// ramp height field is gone because a ramp is a solid slab, and the radial-proxy
        /// ground lift is gone because a protruding pin is a contact of its own.
        /// </para>
        /// <para>
        /// Failure is closed. A refused solve keeps the previous position and drops the
        /// velocity instead of moving the ball with a value nobody could compute; a
        /// residual beyond 2 cm rejects the step's position (design §2.1); a residual
        /// beyond the radius is a deep embed and recovers to the last stable point
        /// (design §2.8).
        /// </para>
        /// </remarks>
        private void StepContactV2(float dt, Vector3 requestedDisplacement,
            out Vector3 groundedCenter, out Vector3 followCenter, out Vector3 supportNormal)
        {
            float stepStartSeconds = Time.time;
            contactV2ClockSeconds = contactV2ClockSeconds + dt;
            groundedCenter = ballCenterWorld;
            followCenter = ballCenterWorld;
            supportNormal = groundPlaneNormal;

            float radius = collisionRadiusWorldM;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f)
            {
                contactSolveFailureCount = contactSolveFailureCount + 1;
                planarVelocity = Vector3.zero;
                contactVerticalVelocity = 0f;
                v2ContactCount = 0;
                v2CoreContactCount = 0;
                v2NeedleContactCount = 0;
                contactResidualMax = 0f;
                return;
            }

            // The needle shape (design §4A.3 step 1): every proxy support clamped into
            // [r, r + cap], and the protruding ones selected longest first.
            PrepareNeedleShape(radius);

            // The drive the climb gate reads is the motion layer's TARGET speed, not the
            // acceleration-limited request: a ball standing against a wall has no actual
            // speed and would otherwise never start a climb (design §3.2).
            float driveSpeed;
            ComputeDrive(out contactDriveDirection, out driveSpeed);

            // A climb whose gate has closed (input released, or the push turned away)
            // leaves the channel here, so the ball slides back instead of hovering
            // (design §3.2/§3.3; pass6 C09/C14).
            RefreshClimbGate(driveSpeed);

            // The climb cap's protrusion term: how far a pin reaches towards the wall we
            // are pushing into (design §3.7, §4A.3).
            float protrusion = EstimateNeedleProtrusion(radius, contactDriveDirection);

            // The per-step near set (design §5.2). maxTravel covers this step's motion
            // plus the velocity a contact can add inside the step.
            float travelX = requestedDisplacement.x;
            float travelY = requestedDisplacement.y;
            float travelZ = requestedDisplacement.z;
            float travel = Mathf.Sqrt(travelX * travelX + travelY * travelY
                + travelZ * travelZ);
            float margin = (ContactTuning.Gravity + ContactTuning.ClimbRateLimit) * dt * dt;
            float maxTravel = travel + margin;
            if (!KatamariMath.IsFiniteFloat(maxTravel) || maxTravel < 0f) maxTravel = 0f;
            FillNearWindow(radius, maxTravel);

            Vector3 center = ballCenterWorld;
            Vector3 planar = planarVelocity;
            float vertical = contactVerticalVelocity;
            int contactCount;
            int dominantSurfaceId;
            int dominantClass;
            float climbHeight;
            float climbSpeed;
            int substepCount;
            float residual;
            bool airborne;
            Vector3 reportNormal;
            bool proxyActive = ProxyContactActive();
            int[] proxyKinds = proxyActive ? nearObbKind : null;
            float[] proxyRadii = proxyActive ? nearObbRadius : null;
            float[] proxyHalfHeights = proxyActive ? nearObbHalfHeight : null;
            int[] proxyAxes = proxyActive ? nearObbAxis : null;
            int[] proxyHullStarts = proxyActive ? nearObbHullStart : null;
            int[] proxyHullCounts = proxyActive ? nearObbHullCount : null;
            int[] proxyFaceStarts = proxyActive ? nearObbFaceStart : null;
            int[] proxyFaceCounts = proxyActive ? nearObbFaceCount : null;
            bool solved = ContactSolveMath.TryStep(true, groundPlaneNormal, groundPlaneOffset,
                planeNormals, planeOffsets, planeCount, nearObbCenter, nearObbAxisX,
                nearObbAxisY, nearObbAxisZ, nearObbHalfExtents, nearObbSurfaceId, nearObbRole,
                nearObbCount, ref center, radius, ref planar, ref vertical, ref climbActive,
                ref climbAnchorY, ref climbSurfaceId, ref climbCooldown, ref climbLocked,
                ref climbAlongSpeed, contactDriveDirection, driveSpeed, dt, protrusion,
                contactNormalsV2, contactPointsV2, contactDepthsV2, contactAnglesV2,
                contactSurfaceIdsV2, contactRolesV2, contactFeaturesV2, contactClassesV2,
                ContactTuning.ContactCapacity, out contactCount, out dominantSurfaceId,
                out dominantClass, out climbHeight, out climbSpeed, out substepCount,
                out residual, out airborne, out reportNormal, ContactTuning.KappaSubstep,
                ContactTuning.MaxSubsteps, 0f, ContactTuning.ContactSkinM, proxyKinds,
                proxyRadii, proxyHalfHeights, proxyAxes, proxyHullStarts, proxyHullCounts,
                proxyFaceStarts, proxyFaceCounts, propProxyHullPoints, propProxyFaceNormals,
                propProxyFaceOffsets, propProxyFaceA, propProxyFaceB, propProxyFaceC);
            if (!solved)
            {
                // Fail closed (design §2.8): keep the previous position, drop the velocity
                // so a corrupt input cannot keep accelerating, and count it.
                contactSolveFailureCount = contactSolveFailureCount + 1;
                planarVelocity = Vector3.zero;
                contactVerticalVelocity = 0f;
                v2ContactCount = 0;
                v2CoreContactCount = 0;
                v2NeedleContactCount = 0;
                contactResidualMax = 0f;
                return;
            }
            contactSolveFailureCount = 0;
            contactRadiusUsedWorldM = radius;
            v2ContactCount = contactCount;
            v2CoreContactCount = contactCount;
            v2NeedleContactCount = 0;
            contactResidualMax = residual;
            v2SubstepCount = substepCount;
            v2DominantSurfaceId = dominantSurfaceId;
            v2DominantClass = dominantClass;
            climbHeightWorldM = climbHeight;
            climbSpeedWorldMps = climbSpeed;
            contactAirborne = airborne;
            contactReportNormal = reportNormal;
            for (int i = 0; i < v2ContactCount; i++)
            {
                contactShapesV2[i] = 0;
            }

            // A residual beyond 2 cm means the push-out did not resolve the step: the
            // position is rejected (the ball stays where it was) and counted, instead of
            // being placed inside geometry (design §2.1).
            if (residual > ContactResidualRejectM)
            {
                contactRejectedStepCount = contactRejectedStepCount + 1;
                center = ballCenterWorld;
            }

            // Needle tips are swept segments, not points (design §4A.3): they push the
            // whole ball out and merge into the contact log as their own records.
            ResolveNeedleContacts(ref center, radius, v2CoreContactCount);
            ApplyNeedleVelocityResponse(v2CoreContactCount);

            // Deep embed recovery (design §2.8, reason 3): a penetration larger than the
            // radius cannot be pushed out this step, so the ball goes back to its last
            // stable point instead of crawling inside the map.
            if (residual > radius)
            {
                TryRecoverToStablePoint(ref center, 3);
            }

            WriteContactSupport(center, v2CoreContactCount, out supportNormal);

            // Stick recovery, soft then hard, then the hold that never teleports again
            // (design §2.8). This is the ONLY place v2 moves the ball without physics.
            UpdateContactRecovery(dt, ref center);

            planarVelocity = planar;
            contactVerticalVelocity = vertical;
            groundedCenter = center;
            // v2 has no proxy lift, so the camera anchor IS the ball centre: the design
            // wants the eye to follow a climb (design §4.4).
            followCenter = center;

            contactPreviousCenterWorld = center;
            contactPreviousRotation = visualRollRotation;
            contactHasPreviousFrame = true;

            float elapsed = Time.time - stepStartSeconds;
            float micros = elapsed * 1000000f;
            if (!KatamariMath.IsFiniteFloat(micros) || micros < 0f) micros = 0f;
            lastStepMicros = micros;
        }

        /// <summary>
        /// Fills the per-step near set from the static table (design §5.2), with the
        /// hysteresis and the explicit overflow verdict of §5.4.
        /// </summary>
        private void FillNearWindow(float radius, float maxTravel)
        {
            windowDroppedCount = 0;
            if (staticObbCenter == null)
            {
                nearObbCount = 0;
                return;
            }
            // The logical table is the static prefix followed by the prop block, so one
            // selection call sees both kinds and the 24-entry budget is shared: the
            // nearest solids win regardless of which table they came from (design §5.2).
            bool proxyMode = ProxyContactActive();
            int totalObbCount = staticObbCount + (proxyMode ? propProxyCount : propObbCount);
            if (totalObbCount <= 0)
            {
                nearObbCount = 0;
                windowPreviousCount = 0;
                return;
            }

            // The window radius includes the needle cap: a pin can reach the wall before
            // the core does (design §4A.3).
            float windowRadius = radius + BallShapeMath.NeedleProtrusionCap(radius);
            int selectedCount;
            int dropped;
            bool complete;
            bool rebuilt;
            bool selectedOk;
            if (proxyMode)
            {
                selectedOk = ContactWindowMath.TrySelectProxyWindow(totalObbCount,
                    staticObbCenter, staticObbAxisX, staticObbAxisY, staticObbAxisZ,
                    staticObbHalfExtents, staticObbBoundingRadius, staticObbSurfaceId,
                    staticProxyKind, staticProxyRadius, staticProxyHalfHeight,
                    staticProxyAxis, staticProxyHullStart, staticProxyHullCount,
                    staticProxyFaceStart, staticProxyFaceCount, propProxyHullPoints,
                    propProxyFaceNormals, propProxyFaceOffsets, propProxyFaceA,
                    propProxyFaceB, propProxyFaceC, ballCenterWorld, windowRadius,
                    maxTravel, windowPreviousSurfaceIds, windowPreviousCount,
                    ContactTuning.MaxNearObbCount, windowScratchIndices,
                    windowScratchDistances, windowSelectedIndices, out selectedCount,
                    out dropped, out complete, out rebuilt);
            }
            else
            {
                selectedOk = ContactWindowMath.TrySelectWindow(totalObbCount, staticObbCenter,
                    staticObbAxisX, staticObbAxisY, staticObbAxisZ, staticObbHalfExtents,
                    staticObbBoundingRadius, staticObbSurfaceId, ballCenterWorld,
                    windowRadius, maxTravel, windowPreviousSurfaceIds, windowPreviousCount,
                    ContactTuning.MaxNearObbCount, windowScratchIndices,
                    windowScratchDistances, windowSelectedIndices, out selectedCount,
                    out dropped, out complete, out rebuilt);
            }
            if (!selectedOk)
            {
                // A window that cannot be built is a fail-closed cell: no near set, the
                // incomplete flag, and the solver then only sees planes and the ground.
                nearObbCount = 0;
                contactIncomplete = true;
                windowOverflowCount = windowOverflowCount + 1;
                LogWindowOverflowOnce();
                return;
            }

            windowDroppedCount = dropped;
            if (dropped > 0)
            {
                windowDroppedTotal = windowDroppedTotal + dropped;
                if (!complete)
                {
                    // A dropped entry could have been touching: the step's contact set is
                    // incomplete, which the test field must read as an invalid cell rather
                    // than a pass (design §5.4).
                    contactIncomplete = true;
                    windowOverflowCount = windowOverflowCount + 1;
                    LogWindowOverflowOnce();
                }
                else
                {
                    windowSaturatedCount = windowSaturatedCount + 1;
                }
            }

            if (!rebuilt && selectedCount == windowPreviousCount) return;
            for (int i = 0; i < selectedCount; i++)
            {
                int source = windowSelectedIndices[i];
                nearObbCenter[i] = staticObbCenter[source];
                nearObbAxisX[i] = staticObbAxisX[source];
                nearObbAxisY[i] = staticObbAxisY[source];
                nearObbAxisZ[i] = staticObbAxisZ[source];
                nearObbHalfExtents[i] = staticObbHalfExtents[source];
                nearObbBoundingRadius[i] = staticObbBoundingRadius[source];
                nearObbSurfaceId[i] = staticObbSurfaceId[source];
                nearObbRole[i] = staticObbRole[source];
                nearObbKind[i] = staticProxyKind[source];
                nearObbRadius[i] = staticProxyRadius[source];
                nearObbHalfHeight[i] = staticProxyHalfHeight[source];
                nearObbAxis[i] = staticProxyAxis[source];
                nearObbHullStart[i] = staticProxyHullStart[source];
                nearObbHullCount[i] = staticProxyHullCount[source];
                nearObbFaceStart[i] = staticProxyFaceStart[source];
                nearObbFaceCount[i] = staticProxyFaceCount[source];
                nearObbSlot[i] = source;
                windowPreviousSurfaceIds[i] = staticObbSurfaceId[source];
            }
            nearObbCount = selectedCount;
            windowPreviousCount = selectedCount;
            nearRebuildCount = nearRebuildCount + 1;
        }

        /// <summary>One throttled line for a window overflow (design §5.4: at most 1/s).</summary>
        private void LogWindowOverflowOnce()
        {
            if (contactV2ClockSeconds - lastWindowOverflowLogSeconds < 1f) return;
            lastWindowOverflowLogSeconds = contactV2ClockSeconds;
            Debug.Log("[ContactV2] window overflow cell=" + ballCenterWorld.x + ","
                + ballCenterWorld.y + "," + ballCenterWorld.z + " dropped="
                + windowDroppedCount + " near=" + nearObbCount + " static="
                + (staticObbCount + propObbCount + propProxyCount));
        }

        /// <summary>
        /// The motion layer's target speed and the direction the driver pushes in
        /// (design §3.2). The target speed is recomputed from the motion layer's own
        /// three inputs because BallMotionMath does not export it; the expression is its
        /// speed cap times the forward intent.
        /// </summary>
        private void ComputeDrive(out Vector3 direction, out float speed)
        {
            float growth = Mathf.Clamp(growthFraction, 0f, 1f);
            float speedCap = maxSpeedMps * (1f - growthSpeedFactor * growth);
            float intent = Mathf.Abs(Mathf.Clamp(inputForward, -1f, 1f));
            speed = speedCap * intent;
            if (!KatamariMath.IsFiniteFloat(speed) || speed < 0f) speed = 0f;

            Vector3 planar = new Vector3(planarVelocity.x, 0f, planarVelocity.z);
            float length;
            if (!KatamariMath.TryNormalize(planar, out direction, out length))
            {
                // No coasting direction: the way the ball is facing is what a driver
                // pushing from rest means.
                float radians = headingDegrees * Mathf.Deg2Rad;
                direction = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            }
        }

        /// <summary>Clamps every pin support into [r, r + cap] and selects the protruding
        /// ones (design §4A.3).</summary>
        private void PrepareNeedleShape(float radius)
        {
            needleCompactCount = 0;
            if (proxySupports == null || proxyDirections == null ||
                contactNeedleSupports == null || contactNeedleIndices == null) return;
            for (int i = 0; i < RadialProxyMath.DirectionCount; i++)
            {
                float clamped;
                if (!BallShapeMath.TryClampNeedleSupport(proxySupports[i], radius,
                    out clamped))
                {
                    clamped = radius;
                }
                contactNeedleSupports[i] = clamped;
            }
            int count;
            if (!BallShapeMath.TryCollectProtrudingNeedles(contactNeedleSupports,
                RadialProxyMath.DirectionCount, radius, contactNeedleIndices, out count))
            {
                needleCompactCount = 0;
                return;
            }
            needleCompactCount = count;
        }

        /// <summary>
        /// L_eff of design §3.7/§4A.3: how far a pin reaches towards the wall, measured
        /// along the push direction instead of a wall normal the motor does not know
        /// before the solve. Clamped to the needle protrusion cap.
        /// </summary>
        private float EstimateNeedleProtrusion(float radius, Vector3 driveDirection)
        {
            float cap = BallShapeMath.NeedleProtrusionCap(radius);
            if (cap <= 0f || needleCompactCount <= 0) return 0f;
            float best = 0f;
            for (int k = 0; k < needleCompactCount; k++)
            {
                int index = contactNeedleIndices[k];
                Vector3 world = visualRollRotation * proxyDirections[index];
                float projected = world.x * driveDirection.x + world.z * driveDirection.z;
                if (projected <= 0f) continue;
                float reach = contactNeedleSupports[index] * projected;
                if (reach > best) best = reach;
            }
            float protrusion = best - radius;
            if (protrusion < 0f) protrusion = 0f;
            if (protrusion > cap) protrusion = cap;
            return protrusion;
        }

        /// <summary>
        /// Sweeps every compact pin tip from its previous world position to this one and
        /// keeps the deepest <see cref="MaxNeedleContacts"/> contacts.
        /// </summary>
        /// <remarks>
        /// The tips are ball-local, so their world position needs the roll rotation; the
        /// rotation the previous step committed is used for the segment's start, which is
        /// a one-step lag on the rotational part only (stage 8 runs after this one). The
        /// translational part is exact. Duplicate surface ids are dropped here so a pin
        /// and the core never push the same face twice (design §4A.3 step 5).
        /// </remarks>
        private int CollectNeedleContacts(Vector3 center, int coreCount)
        {
            int count = 0;
            if (needleCompactCount <= 0) return 0;
            float tipRadius = ContactTuning.NeedleTipRadiusM;
            float skin = ContactTuning.ContactSkinM;
            for (int k = 0; k < needleCompactCount; k++)
            {
                int index = contactNeedleIndices[k];
                Vector3 localDirection = proxyDirections[index];
                float support = contactNeedleSupports[index];
                Vector3 worldDirection = visualRollRotation * localDirection;
                Vector3 tipNow = new Vector3(center.x + worldDirection.x * support,
                    center.y + worldDirection.y * support,
                    center.z + worldDirection.z * support);
                Vector3 tipPrevious = tipNow;
                if (contactHasPreviousFrame)
                {
                    Vector3 previousDirection = contactPreviousRotation * localDirection;
                    tipPrevious = new Vector3(
                        contactPreviousCenterWorld.x + previousDirection.x * support,
                        contactPreviousCenterWorld.y + previousDirection.y * support,
                        contactPreviousCenterWorld.z + previousDirection.z * support);
                }
                float segmentX = tipNow.x - tipPrevious.x;
                float segmentY = tipNow.y - tipPrevious.y;
                float segmentZ = tipNow.z - tipPrevious.z;
                float segmentHalf = 0.5f * Mathf.Sqrt(segmentX * segmentX
                    + segmentY * segmentY + segmentZ * segmentZ);
                float middleX = 0.5f * (tipNow.x + tipPrevious.x);
                float middleY = 0.5f * (tipNow.y + tipPrevious.y);
                float middleZ = 0.5f * (tipNow.z + tipPrevious.z);

                for (int i = 0; i < nearObbCount; i++)
                {
                    // Broadphase: the segment's bounding sphere against the box's.
                    float dx = middleX - nearObbCenter[i].x;
                    float dy = middleY - nearObbCenter[i].y;
                    float dz = middleZ - nearObbCenter[i].z;
                    float reach = nearObbBoundingRadius[i] + segmentHalf + tipRadius + skin;
                    if (dx * dx + dy * dy + dz * dz > reach * reach) continue;
                    Vector3 normal;
                    float depth;
                    Vector3 point;
                    float fraction;
                    if (!BallShapeMath.TryNeedleSegmentContact(nearObbCenter[i],
                        nearObbAxisX[i], nearObbAxisY[i], nearObbAxisZ[i],
                        nearObbHalfExtents[i], tipPrevious, tipNow, tipRadius, out normal,
                        out depth, out point, out fraction)) continue;
                    TryInsertNeedleContact(normal, point, depth, nearObbSurfaceId[i],
                        nearObbRole[i], 1 + k, coreCount, ref count);
                }

                Vector3 groundNormal;
                float groundDepth;
                Vector3 groundPoint;
                float groundFraction;
                if (BallShapeMath.TryNeedleSegmentPlaneContact(groundPlaneNormal,
                    groundPlaneOffset, tipPrevious, tipNow, tipRadius, out groundNormal,
                    out groundDepth, out groundPoint, out groundFraction))
                {
                    TryInsertNeedleContact(groundNormal, groundPoint, groundDepth,
                        ContactCodes.SurfaceIdGround, ContactCodes.RoleBlocking, 1 + k,
                        coreCount, ref count);
                }
                for (int i = 0; i < planeCount; i++)
                {
                    Vector3 planeNormal;
                    float planeDepth;
                    Vector3 planePoint;
                    float planeFraction;
                    if (!BallShapeMath.TryNeedleSegmentPlaneContact(planeNormals[i],
                        planeOffsets[i], tipPrevious, tipNow, tipRadius, out planeNormal,
                        out planeDepth, out planePoint, out planeFraction)) continue;
                    TryInsertNeedleContact(planeNormal, planePoint, planeDepth,
                        ContactCodes.PlaneSurfaceId(i), ContactCodes.RoleBlocking, 1 + k,
                        coreCount, ref count);
                }
            }
            // Deepest first, so the merge takes the contacts that matter most.
            for (int i = 1; i < count; i++)
            {
                int slot = i;
                while (slot > 0 && contactNeedleDepths[slot] > contactNeedleDepths[slot - 1])
                {
                    SwapNeedleContacts(slot, slot - 1);
                    slot = slot - 1;
                }
            }
            return count;
        }

        private void SwapNeedleContacts(int a, int b)
        {
            Vector3 normal = contactNeedleNormals[a];
            contactNeedleNormals[a] = contactNeedleNormals[b];
            contactNeedleNormals[b] = normal;
            Vector3 point = contactNeedlePoints[a];
            contactNeedlePoints[a] = contactNeedlePoints[b];
            contactNeedlePoints[b] = point;
            float depth = contactNeedleDepths[a];
            contactNeedleDepths[a] = contactNeedleDepths[b];
            contactNeedleDepths[b] = depth;
            float angle = contactNeedleAngles[a];
            contactNeedleAngles[a] = contactNeedleAngles[b];
            contactNeedleAngles[b] = angle;
            int surfaceId = contactNeedleSurfaceIds[a];
            contactNeedleSurfaceIds[a] = contactNeedleSurfaceIds[b];
            contactNeedleSurfaceIds[b] = surfaceId;
            int role = contactNeedleRoles[a];
            contactNeedleRoles[a] = contactNeedleRoles[b];
            contactNeedleRoles[b] = role;
            int contactClass = contactNeedleClasses[a];
            contactNeedleClasses[a] = contactNeedleClasses[b];
            contactNeedleClasses[b] = contactClass;
            int shape = contactNeedleShapes[a];
            contactNeedleShapes[a] = contactNeedleShapes[b];
            contactNeedleShapes[b] = shape;
        }

        private void TryInsertNeedleContact(Vector3 normal, Vector3 point, float depth,
            int surfaceId, int role, int shape, int coreCount, ref int count)
        {
            if (depth < -ContactTuning.ContactSkinM) return;
            for (int i = 0; i < coreCount; i++)
            {
                if (contactSurfaceIdsV2[i] == surfaceId) return;
            }
            for (int i = 0; i < count; i++)
            {
                if (contactNeedleSurfaceIds[i] != surfaceId) continue;
                if (contactNeedleDepths[i] >= depth) return;
                WriteNeedleContact(i, normal, point, depth, surfaceId, role, shape);
                return;
            }
            if (count < MaxNeedleContacts)
            {
                WriteNeedleContact(count, normal, point, depth, surfaceId, role, shape);
                count = count + 1;
                return;
            }
            int shallowest = 0;
            for (int i = 1; i < count; i++)
            {
                if (contactNeedleDepths[i] < contactNeedleDepths[shallowest])
                    shallowest = i;
            }
            if (depth <= contactNeedleDepths[shallowest]) return;
            WriteNeedleContact(shallowest, normal, point, depth, surfaceId, role, shape);
        }

        private void WriteNeedleContact(int slot, Vector3 normal, Vector3 point, float depth,
            int surfaceId, int role, int shape)
        {
            contactNeedleNormals[slot] = normal;
            contactNeedlePoints[slot] = point;
            contactNeedleDepths[slot] = depth;
            contactNeedleAngles[slot] = ObbContactMath.ContactAngleDeg(normal);
            contactNeedleSurfaceIds[slot] = surfaceId;
            contactNeedleRoles[slot] = role;
            contactNeedleClasses[slot] =
                ContactClassificationMath.ClassifyContact(normal);
            contactNeedleShapes[slot] = shape;
        }

        /// <summary>
        /// Runs the needle pass: two sequential whole-ball push-outs with the tips
        /// recomputed after each move, then one final collection merged into the contact
        /// log (design §4A.3 steps 4-6).
        /// </summary>
        /// <remarks>
        /// This is a deviation from the letter of §4A.3, which puts the needle sweep
        /// inside the core substep loop: the core substep loop lives in
        /// <see cref="ContactSolveMath.TryStep"/> and the needle set is recomputed from
        /// the motor's own state, so it runs as a per-step pass instead. The no-tunnel
        /// guarantee for pins comes from the SEGMENT sweep, not from the substep count,
        /// so the deviation costs determinism of ordering, not safety.
        /// </remarks>
        private void ResolveNeedleContacts(ref Vector3 center, float radius, int coreCount)
        {
            needleContactCount = 0;
            v2NeedleContactCount = 0;
            if (needleCompactCount <= 0) return;

            for (int iteration = 0; iteration < 2; iteration++)
            {
                needleContactCount = CollectNeedleContacts(center, coreCount);
                int deepest = -1;
                float deepestDepth = ContactTuning.ContactSlopM;
                for (int i = 0; i < needleContactCount; i++)
                {
                    if (contactNeedleDepths[i] > deepestDepth)
                    {
                        deepest = i;
                        deepestDepth = contactNeedleDepths[i];
                    }
                }
                if (deepest < 0) break;
                center = new Vector3(
                    center.x + contactNeedleNormals[deepest].x * deepestDepth,
                    center.y + contactNeedleNormals[deepest].y * deepestDepth,
                    center.z + contactNeedleNormals[deepest].z * deepestDepth);
            }

            needleContactCount = CollectNeedleContacts(center, coreCount);
            v2NeedleContactCount = MergeNeedleContacts();
        }

        /// <summary>Appends the needle contacts the log still has room for, core first.</summary>
        private int MergeNeedleContacts()
        {
            int merged = 0;
            for (int i = 0; i < needleContactCount; i++)
            {
                if (v2ContactCount >= ContactTuning.ContactCapacity) break;
                int surfaceId = contactNeedleSurfaceIds[i];
                bool duplicate = false;
                for (int j = 0; j < v2ContactCount; j++)
                {
                    if (contactSurfaceIdsV2[j] == surfaceId)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;
                int slot = v2ContactCount;
                contactNormalsV2[slot] = contactNeedleNormals[i];
                contactPointsV2[slot] = contactNeedlePoints[i];
                contactDepthsV2[slot] = contactNeedleDepths[i];
                contactAnglesV2[slot] = contactNeedleAngles[i];
                contactSurfaceIdsV2[slot] = surfaceId;
                contactRolesV2[slot] = contactNeedleRoles[i];
                contactClassesV2[slot] = contactNeedleClasses[i];
                contactFeaturesV2[slot] = contactNeedleDepths[i]
                    > ContactTuning.NeedleTipRadiusM
                    ? ContactCodes.FeatureInside : ContactCodes.FeatureFace;
                contactShapesV2[slot] = contactNeedleShapes[i];
                v2ContactCount = slot + 1;
                merged = merged + 1;
            }
            return merged;
        }

        /// <summary>
        /// The solid velocity response for the needle contacts, using the same three
        /// rules as the solver's non-climb branch: a RIDE contact removes nothing, a
        /// CLIMB contact removes its horizontal inward component, and a NONE contact
        /// (underside) removes all three. Kept here because the needle set is resolved
        /// after the core step; see <see cref="ResolveNeedleContacts"/>.
        /// </summary>
        private void ApplyNeedleVelocityResponse(int coreCount)
        {
            for (int i = coreCount; i < v2ContactCount; i++)
            {
                if (contactDepthsV2[i] <= 0f) continue;
                int contactClass = contactClassesV2[i];
                if (contactClass == ContactCodes.ClassRide) continue;
                Vector3 normal = contactNormalsV2[i];
                if (contactClass == ContactCodes.ClassClimb)
                {
                    float horizontal = Mathf.Sqrt(normal.x * normal.x
                        + normal.z * normal.z);
                    if (horizontal <= 0f) continue;
                    float inverse = 1f / horizontal;
                    float nx = normal.x * inverse;
                    float nz = normal.z * inverse;
                    float inward = planarVelocity.x * nx + planarVelocity.z * nz;
                    if (inward < 0f)
                    {
                        planarVelocity = new Vector3(planarVelocity.x - nx * inward,
                            planarVelocity.y, planarVelocity.z - nz * inward);
                    }
                }
                else
                {
                    float inward = planarVelocity.x * normal.x
                        + contactVerticalVelocity * normal.y
                        + planarVelocity.z * normal.z;
                    if (inward < 0f)
                    {
                        planarVelocity = new Vector3(planarVelocity.x - normal.x * inward,
                            0f, planarVelocity.z - normal.z * inward);
                        contactVerticalVelocity = contactVerticalVelocity
                            - normal.y * inward;
                    }
                }
            }
        }

        /// <summary>
        /// Writes the support surface pair from this step's contact log (design §2.5):
        /// the highest rideable contact, or a needle ride contact when only a pin is
        /// touching, or the climb contact when the ball is on a wall with no floor.
        /// </summary>
        private void WriteContactSupport(Vector3 center, int coreCount,
            out Vector3 supportNormal)
        {
            supportNormal = groundPlaneNormal;
            previousSupportSurfaceId = supportSurfaceId;
            previousSupportHeightWorldM = lastSupportHeightWorldM;

            // The step limit stays a DIAGNOSTIC in v2: it is reported, never used.
            float limit;
            if (!SupportSurfaceMath.TryStepHeightLimit(semanticDiameterWorldM,
                stepHeightOverBallDiameter, out limit)) limit = 0f;
            stepHeightLimitWorldM = limit;

            int support = ContactClassificationMath.SelectSupportContact(contactNormalsV2,
                contactDepthsV2, contactClassesV2, v2ContactCount);
            bool fromNeedle = false;
            if (support < 0)
            {
                for (int i = coreCount; i < v2ContactCount; i++)
                {
                    if (contactClassesV2[i] != ContactCodes.ClassRide) continue;
                    support = i;
                    fromNeedle = true;
                    break;
                }
            }
            else if (support >= coreCount)
            {
                fromNeedle = true;
            }
            supportFromNeedle = fromNeedle;
            supportFromClimb = false;
            supportCandidateCount = v2ContactCount;

            // Record the wall the drive pushes into, whether or not a floor still holds
            // the ball: the normal start of a climb is "supported by the ground while
            // pushing a wall" (§3.4), and RefreshClimbGate needs that wall's normal and
            // theta for the NEXT step's standing-gate check.
            int climbWall = ContactClassificationMath.SelectClimbContact(contactNormalsV2,
                contactClassesV2, v2ContactCount, contactDriveDirection);
            if (climbWall >= 0)
            {
                climbWallNormalWorld = contactNormalsV2[climbWall];
                climbContactAngleDeg = contactAnglesV2[climbWall];
            }

            if (support >= 0)
            {
                Vector3 normal = contactNormalsV2[support];
                Vector3 point = contactPointsV2[support];
                supportSurfaceId = contactSurfaceIdsV2[support];
                supportSurfacePointWorld = point;
                supportSurfaceNormalWorld = normal;
                supportNormal = normal;
                // The v1 getter's reading: the tangent plane's height under the centre.
                // The same expression is the tip formula of §2.5 when the point is a pin
                // tip, which is why a needle support needs no second formula.
                if (Mathf.Abs(normal.y) > 1e-4f)
                {
                    float offset = (normal.x * (center.x - point.x)
                        + normal.y * (center.y - point.y)
                        + normal.z * (center.z - point.z)) / normal.y;
                    supportSurfaceHeightWorldM = center.y - offset;
                }
                else
                {
                    supportSurfaceHeightWorldM = point.y;
                }
                lastSupportHeightWorldM = supportSurfaceHeightWorldM;
                isFalling = false;
                fallSpeedMps = 0f;
                return;
            }

            if (climbActive)
            {
                // A climbing contact is a support kind of its own (4): there is a surface
                // to report, but no FLOOR under the ball. The height therefore reports the
                // ball's own bottom (so the test field's support_gap reads 0) instead of
                // the wall contact point's y: a vertical wall's closest point sits at
                // whatever height the ball is at, and reporting it made support_gap read
                // exactly -r (pass6 C08/C09/C10).
                supportFromClimb = true;
                supportSurfaceId = climbSurfaceId;
                if (climbWall >= 0)
                {
                    supportSurfaceNormalWorld = contactNormalsV2[climbWall];
                    supportSurfacePointWorld = contactPointsV2[climbWall];
                    supportNormal = contactNormalsV2[climbWall];
                }
                else
                {
                    supportSurfaceNormalWorld = climbWallNormalWorld;
                    supportNormal = climbWallNormalWorld;
                }
                supportSurfaceHeightWorldM = center.y - collisionRadiusWorldM;
                lastSupportHeightWorldM = supportSurfaceHeightWorldM;
                isFalling = false;
                fallSpeedMps = 0f;
                return;
            }

            // No support at all: gravity owns the step. The pair keeps its previous
            // point/normal (v1 behaves the same when the support query refuses).
            supportSurfaceId = SupportSurfaceMath.NoSurfaceId;
            isFalling = contactAirborne;
            fallSpeedMps = contactVerticalVelocity < 0f ? -contactVerticalVelocity : 0f;
        }

        /// <summary>
        /// The climb gate of design §3.2 is a STANDING condition, not only an entry test:
        /// a climb whose push-in or gate speed has fallen below the requirement leaves the
        /// channel and lets the ball slide back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why this is needed on top of <see cref="ContactSolveMath.TryStep"/>. The solver
        /// only LEAVES a climb on reaching the cap or on <c>pushIn &lt; p_min</c>, and
        /// <c>ClimbMath.ClimbSpeed</c> returns zero when the gate speed is zero. Releasing
        /// the forward input while still facing a wall therefore gives p = 1, gate = 0,
        /// climb speed 0: the ball hangs on the wall at whatever height it had reached.
        /// The pass6 first run showed exactly that (C09/C14 ended 2-25 cm above the
        /// ground, welded to a boundary plane), and because a vertical wall's contact point
        /// is at the ball's own height it also made <c>support_gap</c> read -r.
        /// </para>
        /// <para>
        /// The reference model has the same gap: its scenarios only ever run all-push
        /// timelines, so this release-while-climbing case is not one of its fixtures. The
        /// rule stays here, in the motor's climb state, so the port and the reference model
        /// keep agreeing on <c>step()</c>; the design text (§3.3 "松开 → 回滑") is what it
        /// implements.
        /// </para>
        /// </remarks>
        private void RefreshClimbGate(float driveSpeed)
        {
            if (!climbActive) return;
            float normalX = climbWallNormalWorld.x;
            float normalZ = climbWallNormalWorld.z;
            float horizontal = Mathf.Sqrt(normalX * normalX + normalZ * normalZ);
            if (horizontal <= 0f) return;
            float inverse = 1f / horizontal;
            float pushIn = Mathf.Max(0f, -(contactDriveDirection.x * normalX * inverse
                + contactDriveDirection.z * normalZ * inverse));
            float planarSpeed = Mathf.Sqrt(planarVelocity.x * planarVelocity.x
                + planarVelocity.z * planarVelocity.z);
            float vGate = Mathf.Max(planarSpeed, ContactTuning.KIntent * driveSpeed);
            if (pushIn >= ContactTuning.PMin &&
                vGate >= ClimbMath.RequiredSpeed(climbContactAngleDeg)) return;
            climbActive = false;
            climbCooldown = ContactTuning.ClimbCooldownS;
            climbAlongSpeed = 0f;
            climbHeightWorldM = 0f;
            climbSpeedWorldMps = 0f;
        }

        /// <summary>
        /// The stuck-recovery state machine of design §2.8: soft at 1.5 s (clear the
        /// velocity, do not move), hard at 3 s (back to the last stable point), and after
        /// three recoveries inside 10 s a hold that stops teleporting and says so once.
        /// </summary>
        private void UpdateContactRecovery(float dt, ref Vector3 center)
        {
            float moveX = center.x - ballCenterWorld.x;
            float moveY = center.y - ballCenterWorld.y;
            float moveZ = center.z - ballCenterWorld.z;
            float progress = Mathf.Sqrt(moveX * moveX + moveY * moveY + moveZ * moveZ);
            float driveIntent = Mathf.Abs(inputForward) + Mathf.Abs(inputLateral);

            if (driveIntent > 0.05f && progress < 0.0005f)
            {
                stuckSeconds = stuckSeconds + dt;
            }
            else
            {
                stuckSeconds = 0f;
                // Moving again re-arms the soft stage: one soft recovery per episode,
                // not one per step (a soft recovery does not move the ball, so its own
                // stall would otherwise re-fire it sixty times a second).
                softRecoveryArmed = true;
            }

            if (progress > 0.001f && contactResidualMax < 0.001f)
            {
                if (!hasContactStableCenter ||
                    contactV2ClockSeconds - lastStableWriteSeconds > 0.1f)
                {
                    contactStableCenter = center;
                    hasContactStableCenter = true;
                    lastStableWriteSeconds = contactV2ClockSeconds;
                }
            }

            // The window expires: a second episode is recoverable and the hold is left.
            if (contactV2ClockSeconds - recoveryWindowStartSeconds
                > ContactRecoveryWindowSeconds)
            {
                recoveryWindowStartSeconds = contactV2ClockSeconds;
                recoveryCount = 0;
                recoveryHoldLogged = false;
                if (recoveryState == ContactRecoveryStateHold)
                    recoveryState = ContactRecoveryStateNone;
            }
            if (recoveryState == ContactRecoveryStateHold) return;
            if (progress > 0.0005f && stuckSeconds <= 0f)
                recoveryState = ContactRecoveryStateNone;

            if (stuckSeconds > ContactStuckHardSeconds)
            {
                if (!TryRecoverToStablePoint(ref center, 2)) return;
                stuckSeconds = 0f;
                return;
            }
            if (stuckSeconds > ContactStuckSoftSeconds && softRecoveryArmed)
            {
                softRecoveryArmed = false;
                planarVelocity = Vector3.zero;
                contactVerticalVelocity = 0f;
                ClearClimbState();
                recoveryCount = recoveryCount + 1;
                lastRecoveryReason = 1;
                recoveryState = ContactRecoveryStateSoft;
                if (recoveryCount >= ContactRecoveryHoldCount) EnterRecoveryHold(center);
            }
        }

        /// <summary>
        /// Moves the ball to the last stable point (or the round start when it never
        /// moved), clears every velocity and climb state, and records why. Reason 2 is the
        /// hard stuck recovery, reason 3 a deep embed (design §2.8).
        /// </summary>
        private bool TryRecoverToStablePoint(ref Vector3 center, int reason)
        {
            Vector3 target;
            if (hasContactStableCenter) target = contactStableCenter;
            else if (hasRoundStartCenter) target = roundStartCenter;
            else return false;
            center = target;
            planarVelocity = Vector3.zero;
            contactVerticalVelocity = 0f;
            ClearClimbState();
            discontinuous = true;
            hasSupportFrame = false;
            isFalling = false;
            fallSpeedMps = 0f;
            contactStableCenter = target;
            hasContactStableCenter = true;
            recoveryCount = recoveryCount + 1;
            lastRecoveryReason = reason;
            recoveryState = ContactRecoveryStateHard;
            if (recoveryCount >= ContactRecoveryHoldCount) EnterRecoveryHold(target);
            return true;
        }

        private void EnterRecoveryHold(Vector3 center)
        {
            recoveryState = ContactRecoveryStateHold;
            if (recoveryHoldLogged) return;
            recoveryHoldLogged = true;
            Debug.Log("[BallMotor] recovery hold cell=" + center.x + "," + center.y + ","
                + center.z + " surface=" + supportSurfaceId + " residual="
                + contactResidualMax + " recoveries=" + recoveryCount);
        }

        private void ClearClimbState()
        {
            climbActive = false;
            climbAnchorY = 0f;
            climbSurfaceId = -1;
            climbCooldown = 0f;
            climbLocked = false;
            climbAlongSpeed = 0f;
            climbHeightWorldM = 0f;
            climbSpeedWorldMps = 0f;
            climbWallNormalWorld = new Vector3(0f, 0f, 1f);
            climbContactAngleDeg = 90f;
        }

        /// <summary>
        /// The growth push-out of design §4.2: after the size stage the radius can be
        /// bigger than the one the step was solved with, so the ball may be embedded in
        /// the contact it just grew into. Up to two pushes of at most 5 cm each; if the
        /// penetration still exceeds 2 cm the push is not applied at all and
        /// <c>sizeApplyDeferred</c> records it, because the data (volume, score, size)
        /// has already been committed and only the position waits a step.
        /// </summary>
        private void ApplyGrowthPushOut(ref Vector3 groundedCenter, ref Vector3 followCenter,
            ref Vector3 supportNormal)
        {
            float radius = collisionRadiusWorldM;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return;
            if (contactRadiusUsedWorldM <= 0f)
            {
                contactRadiusUsedWorldM = radius;
                return;
            }
            if (radius <= contactRadiusUsedWorldM) return;

            Vector3 before = groundedCenter;
            Vector3 center = groundedCenter;
            bool proxyActive = ProxyContactActive();
            int[] proxyKinds = proxyActive ? nearObbKind : null;
            float[] proxyRadii = proxyActive ? nearObbRadius : null;
            float[] proxyHalfHeights = proxyActive ? nearObbHalfHeight : null;
            int[] proxyAxes = proxyActive ? nearObbAxis : null;
            int[] proxyHullStarts = proxyActive ? nearObbHullStart : null;
            int[] proxyHullCounts = proxyActive ? nearObbHullCount : null;
            int[] proxyFaceStarts = proxyActive ? nearObbFaceStart : null;
            int[] proxyFaceCounts = proxyActive ? nearObbFaceCount : null;
            int count = 0;
            for (int iteration = 0; iteration < 2; iteration++)
            {
                int collected;
                if (!ContactSolveMath.TryCollectContacts(true, groundPlaneNormal,
                    groundPlaneOffset, planeNormals, planeOffsets, planeCount,
                    nearObbCenter, nearObbAxisX, nearObbAxisY, nearObbAxisZ,
                    nearObbHalfExtents, nearObbSurfaceId, nearObbRole, nearObbCount, center,
                    radius, growthContactNormals, growthContactPoints, growthContactDepths,
                    growthContactAngles, growthContactSurfaceIds, growthContactRoles,
                    growthContactFeatures, growthContactClasses,
                    ContactTuning.ContactCapacity, out collected,
                    ContactTuning.ContactSkinM, proxyKinds, proxyRadii, proxyHalfHeights,
                    proxyAxes, proxyHullStarts, proxyHullCounts, proxyFaceStarts,
                    proxyFaceCounts, propProxyHullPoints, propProxyFaceNormals,
                    propProxyFaceOffsets, propProxyFaceA, propProxyFaceB, propProxyFaceC))
                {
                    sizeApplyDeferred = true;
                    sizeApplyDeferredCount = sizeApplyDeferredCount + 1;
                    return;
                }
                count = collected;
                int deepest = -1;
                float deepestDepth = ContactTuning.ContactSlopM;
                for (int i = 0; i < count; i++)
                {
                    if (growthContactDepths[i] > deepestDepth)
                    {
                        deepest = i;
                        deepestDepth = growthContactDepths[i];
                    }
                }
                if (deepest < 0) break;
                float push = deepestDepth;
                if (push > ContactGrowthPushCapM) push = ContactGrowthPushCapM;
                center = new Vector3(
                    center.x + growthContactNormals[deepest].x * push,
                    center.y + growthContactNormals[deepest].y * push,
                    center.z + growthContactNormals[deepest].z * push);
            }

            int finalCount;
            if (!ContactSolveMath.TryCollectContacts(true, groundPlaneNormal,
                groundPlaneOffset, planeNormals, planeOffsets, planeCount, nearObbCenter,
                nearObbAxisX, nearObbAxisY, nearObbAxisZ, nearObbHalfExtents,
                nearObbSurfaceId, nearObbRole, nearObbCount, center, radius,
                growthContactNormals, growthContactPoints, growthContactDepths,
                growthContactAngles, growthContactSurfaceIds, growthContactRoles,
                growthContactFeatures, growthContactClasses, ContactTuning.ContactCapacity,
                out finalCount, ContactTuning.ContactSkinM, proxyKinds, proxyRadii,
                proxyHalfHeights, proxyAxes, proxyHullStarts, proxyHullCounts,
                proxyFaceStarts, proxyFaceCounts, propProxyHullPoints,
                propProxyFaceNormals, propProxyFaceOffsets, propProxyFaceA, propProxyFaceB,
                propProxyFaceC))
            {
                sizeApplyDeferred = true;
                sizeApplyDeferredCount = sizeApplyDeferredCount + 1;
                return;
            }
            float finalResidual = 0f;
            for (int i = 0; i < finalCount; i++)
            {
                if (growthContactDepths[i] > finalResidual)
                    finalResidual = growthContactDepths[i];
            }
            if (finalResidual > ContactResidualRejectM)
            {
                // Not applied: the position stays where the step solved it, and the next
                // step retries from there (the volume and the size are already committed).
                groundedCenter = before;
                followCenter = before;
                sizeApplyDeferred = true;
                sizeApplyDeferredCount = sizeApplyDeferredCount + 1;
                return;
            }

            sizeApplyDeferred = false;
            contactRadiusUsedWorldM = radius;
            float movedX = center.x - before.x;
            float movedY = center.y - before.y;
            float movedZ = center.z - before.z;
            float moved = Mathf.Sqrt(movedX * movedX + movedY * movedY + movedZ * movedZ);
            // A growth push is not rolling: keep it out of the visual roll (design §4.2).
            if (moved > 0.02f) discontinuous = true;
            groundedCenter = center;
            followCenter = center;

            int support = ContactClassificationMath.SelectSupportContact(
                growthContactNormals, growthContactDepths, growthContactClasses, finalCount);
            if (support < 0) return;
            Vector3 normal = growthContactNormals[support];
            Vector3 point = growthContactPoints[support];
            supportNormal = normal;
            supportSurfaceId = growthContactSurfaceIds[support];
            supportSurfacePointWorld = point;
            supportSurfaceNormalWorld = normal;
            if (Mathf.Abs(normal.y) > 1e-4f)
            {
                float offset = (normal.x * (center.x - point.x)
                    + normal.y * (center.y - point.y)
                    + normal.z * (center.z - point.z)) / normal.y;
                supportSurfaceHeightWorldM = center.y - offset;
            }
            lastSupportHeightWorldM = supportSurfaceHeightWorldM;
        }

        /// <summary>
        /// Refreshes which static boxes are steps this step, and rebuilds the sweep
        /// array without the steppable ones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the sweep side of §4.3 of Docs/Design/BallMotor/TERRAIN_SUPPORT.md: a
        /// box the ball may climb must be absent from the sweep, because the sweep is a
        /// conservative radius-expanded AABB and a 2.8 cm lip would otherwise be a wall
        /// for every ball. The decision is
        /// <see cref="SupportSurfaceMath.IsStepWithinLimit"/> with the SAME reference the
        /// support table uses (<see cref="SupportSurfaceMath.SupportReferenceHeight"/> of
        /// the previous support and the ball's bottom). One function, one reference: a box
        /// excluded here is always a box the support table can select, so the "removed from
        /// the sweep but unsupportable" state cannot exist.
        /// </para>
        /// <para>
        /// Frequency gate: recompute only when the box set changed, the semantic diameter
        /// moved by more than 2 mm, or the REFERENCE moved by more than 5 mm. The gate is
        /// <see cref="SupportSurfaceMath.StepMaskNeedsRefresh"/>, whose inputs are the
        /// predicate's own inputs (reference height and the diameter that becomes the
        /// limit) rather than the selected support height. That distinction is the second
        /// half of the 2026-11-15 C09 fix: a ball spawning 20 mm above the floor has the
        /// spawn clearance as its reference, lands within a few steps, and the support
        /// height never moves off 0 - a support-height gate would never fire, while the
        /// reference gate fires on the first landed step.
        /// </para>
        /// <para>
        /// The mask result is ALSO the support table's answer for static boxes
        /// (<see cref="SupportSurfaceMath.TryResolveSupport"/> takes this array), so a box
        /// dropped from the sweep is always selectable and a wall is never selectable.
        /// The rebuild of the sweep arrays only runs when the mask actually changed. The
        /// gate is the ONLY anti-jitter device: the one-way hysteresis the mask used to
        /// have was removed on 2026-11-15 because it could diverge from the support table.
        /// </para>
        /// </remarks>
        private void RefreshStepMask()
        {
            if (boxIsSteppable == null) return;
            float limit;
            if (!SupportSurfaceMath.TryStepHeightLimit(semanticDiameterWorldM,
                stepHeightOverBallDiameter, out limit))
            {
                limit = 0f;
            }
            stepHeightLimitWorldM = limit;

            // The reference is the predicate's own input: the higher of the previous
            // support and the ball's own bottom (pre-sweep, the accepted one-step lag).
            float reference = SupportSurfaceMath.SupportReferenceHeight(lastSupportHeightWorldM,
                ballCenterWorld.y - collisionRadiusWorldM);
            if (!SupportSurfaceMath.StepMaskNeedsRefresh(stepMaskValid, stepMaskBoxesDirty,
                semanticDiameterWorldM, stepMaskDiameterWorldM, reference,
                stepMaskReferenceWorldM))
            {
                return;
            }

            stepMaskValid = true;
            stepMaskBoxesDirty = false;
            stepMaskDiameterWorldM = semanticDiameterWorldM;
            stepMaskReferenceWorldM = reference;

            bool changed = false;
            for (int i = 0; i < boxCount; i++)
            {
                bool steppable = SupportSurfaceMath.IsStepWithinLimit(boxMaximumBuffer[i].y,
                    reference, limit);
                if (steppable != boxIsSteppable[i])
                {
                    boxIsSteppable[i] = steppable;
                    changed = true;
                }
            }
            for (int i = boxCount; i < MaxBoxCount; i++)
            {
                boxIsSteppable[i] = false;
            }
            if (changed)
            {
                // A step classification flipped, so the sweep arrays must be rebuilt. This
                // is the allocation the design flags for a P4 profiler pass; the counter is
                // how the physics test field reads it.
                stepMaskRebuildCount = stepMaskRebuildCount + 1;
                RebuildObstacles();
            }
        }

        /// <summary>Forgets every step classification (box set change, feature off).</summary>
        private void ClearStepMask()
        {
            if (boxIsSteppable == null) return;
            for (int i = 0; i < MaxBoxCount; i++)
            {
                boxIsSteppable[i] = false;
            }
            stepMaskValid = false;
            stepMaskBoxesDirty = true;
        }

        /// <summary>
        /// Selects this step's support surface and updates the gravity fall state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The support decision itself is a single call into
        /// <see cref="SupportSurfaceMath"/>; this method only carries the result and the
        /// two memories the layers need (the previous support, for the band and the step
        /// rise) and decides nothing about heights or footprints itself.
        /// </para>
        /// <para>
        /// The first call after Start (and after a teleport, which clears the flag) takes
        /// the ball's own bottom as the reference surface. That is what makes a ball
        /// placed above the floor fall under gravity instead of snapping down at 0.1 m per
        /// step, without a separate "spawned in the air" rule.
        /// </para>
        /// <para>
        /// With terrain support off this still resolves the ground-only surface for the
        /// diagnostics, but the fall state is never entered, so nothing downstream moves.
        /// </para>
        /// </remarks>
        private void ResolveTerrainSupport(Vector3 center)
        {
            if (boxMinimumBuffer == null || boxMaximumBuffer == null) return;
            // The support query reads the step mask for the static boxes; before Start
            // allocates it, there is nothing to resolve against.
            if (boxIsSteppable == null) return;
            if (!hasSupportFrame)
            {
                supportSurfaceId = SupportSurfaceMath.NoSurfaceId;
                previousSupportSurfaceId = SupportSurfaceMath.NoSurfaceId;
                previousSupportHeightWorldM = center.y - collisionRadiusWorldM;
                lastSupportHeightWorldM = previousSupportHeightWorldM;
                hasSupportFrame = true;
                isFalling = false;
                fallSpeedMps = 0f;
            }
            else
            {
                previousSupportSurfaceId = supportSurfaceId;
                previousSupportHeightWorldM = lastSupportHeightWorldM;
            }

            float limit;
            if (!SupportSurfaceMath.TryStepHeightLimit(semanticDiameterWorldM,
                stepHeightOverBallDiameter, out limit))
            {
                limit = 0f;
            }
            stepHeightLimitWorldM = limit;

            // The reference is the higher of the previous support and the ball's own
            // bottom, from the one shared helper the sweep mask also calls. "The previous
            // support + stepLimit" alone would exclude a surface the ball is falling ONTO
            // (above the old support but below the ball) and the ball would fall through
            // it. Grounded, the ball's bottom equals the support, so this is the design's
            // band unchanged; airborne, the ball's own bottom makes "already above the
            // top" true without a second unconditional rule.
            float bandReference = SupportSurfaceMath.SupportReferenceHeight(
                lastSupportHeightWorldM, center.y - collisionRadiusWorldM);

            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;
            // With terrain support off the prop table is not consulted at all, so the
            // query is bit-for-bit the P1 query (an empty prop list) and a diagnostic
            // read can never claim a prop is supporting a ball the feature ignores.
            int activePropCount = terrainSupportEnabled ? supportPropCount : 0;
            // Ramps obey the same switch as the rest of terrain support: off means the
            // query sees an empty ramp table, so the result is exactly the P1/P2 one.
            int activeRampCount = terrainSupportEnabled ? rampCount : 0;
            if (SupportSurfaceMath.TryResolveSupport(center, bandReference, limit,
                groundPlaneNormal, groundPlaneOffset, boxMinimumBuffer, boxMaximumBuffer,
                boxIsSteppable, boxCount, rampCenterBuffer, rampNormalBuffer, rampRightBuffer,
                rampHalfWidthBuffer, rampHalfRunBuffer, activeRampCount,
                supportPropMinimumBuffer, supportPropMaximumBuffer,
                activePropCount, out id, out point, out normal, out height, out candidates))
            {
                supportSurfaceId = id;
                supportSurfacePointWorld = point;
                supportSurfaceNormalWorld = normal;
                supportSurfaceHeightWorldM = height;
                supportCandidateCount = candidates;
            }
            else
            {
                // The ground plane itself was refused (a corrupt normal or offset). Keep
                // the previous surface pair instead of inventing one; the ground solve
                // rejects the same plane and the step keeps its previous position.
                supportCandidateCount = 0;
            }

            bool dropped;
            // A drop only counts when the SELECTED SURFACE changed. A continuous ramp (or
            // a tilted ground plane) lowers the height under the moving probe as the ball
            // rolls along it; treating that as a lost surface would put the ball into the
            // gravity-fall state on every downhill step (a 30 degree ramp at 4 m/s drops
            // 46 mm per 20 ms step). A real ledge changes the surface id.
            bool sameSurface = previousSupportSurfaceId == supportSurfaceId;
            if (terrainSupportEnabled &&
                SupportSurfaceMath.SupportDroppedAway(supportSurfaceHeightWorldM,
                    lastSupportHeightWorldM, supportDropTriggerM, sameSurface, out dropped))
            {
                isFalling = SupportSurfaceMath.ResolveFallState(isFalling, dropped, false, false);
            }
            if (!terrainSupportEnabled)
            {
                isFalling = false;
                fallSpeedMps = 0f;
            }
            lastSupportHeightWorldM = supportSurfaceHeightWorldM;
        }

        /// <summary>
        /// Stage 1: one canonical frame becomes the motion layer's three axes.
        /// </summary>
        /// <remarks>
        /// A rejected frame keeps the previous step's axes instead of becoming a
        /// zero-input braking step. The axis pair is device space (the intent
        /// layer's own contract): y is forward intent and x is lateral intent,
        /// with turn coming from the separate turn axis. No layer maps a device
        /// axis onto a world direction, so this adapter convention is the seam a
        /// future input adapter replaces; see the README gap list.
        /// </remarks>
        private void StepInput()
        {
            Vector2 move = new Vector2(debugLateral, debugForward);
            int intentCount;
            bool accepted = ControlIntentMath.TryProcess(
                localClockSeconds, move, Vector2.zero, false, false, false, false,
                false, false, debugMenuOpen, previousDown, lastActionTime, comfortState,
                intentKindBuffer, intentXBuffer, intentYBuffer, intentValueBuffer,
                out intentCount);
            if (!accepted) return;

            inputForward = 0f;
            inputLateral = 0f;
            for (int i = 0; i < intentCount; i++)
            {
                if (intentKindBuffer[i] != ControlIntentKind.Move2D) continue;
                inputForward = intentYBuffer[i];
                inputLateral = intentXBuffer[i];
            }
            inputTurn = debugMenuOpen ? 0f : debugTurn;
        }

        private void EvaluatePendingCandidates(Vector3 center)
        {
            for (int i = 0; i < pendingCandidateCount; i++)
            {
                pendingAccepted[i] = false;
                // The size gate is the original's whole-millimetre rule, resolved inside the
                // pickup layer from this candidate's own k and this step's semantic
                // diameter, so it grows with the ball. semanticDiameterWorldM is the same
                // reading the size stage derives and GetSemanticDiameterWorldM reports,
                // while reach keeps using collisionRadiusWorldM.
                //
                // Ball contact v2 (design §4A.4): the pickup shape is a TRIGGER, so its
                // reach along this candidate's direction replaces the sphere radius in the
                // gate's reach and in nothing else. With the flag off the value is exactly
                // collisionRadiusWorldM, i.e. the pre-v2 gate.
                float reachRadius = collisionRadiusWorldM;
                if (contactModelV2Enabled)
                {
                    Vector3 toCandidate = new Vector3(pendingPositions[i].x - center.x,
                        pendingPositions[i].y - center.y, pendingPositions[i].z - center.z);
                    float pickupReach;
                    if (BallShapeMath.TryPickupReach(toCandidate, collisionRadiusWorldM,
                        proxyDirections, contactNeedleSupports, RadialProxyMath.DirectionCount,
                        out pickupReach))
                    {
                        reachRadius = pickupReach;
                    }
                }
                AbsorbOutcome outcome = PickupAbsorbMath.EvaluateAbsorbWithValue(
                    pendingAbsorbable[i], pendingKinds[i], allowedKinds, allowedKindCount,
                    pendingBounds[i], pendingPositions[i], center, reachRadius,
                    contactRadiusWorldM, pendingVolumeK[i], semanticDiameterWorldM,
                    maxCollisionRadiusWorldM, pendingValues[i]);
                float evaluatedDistance = Vector3.Distance(pendingPositions[i], center);

                if (debugTraceItemId != null && debugTraceItemId.Length > 0
                    && pendingItemIds[i] == debugTraceItemId
                    && traceLinesLogged < TraceLineLimit)
                {
                    // Data only. The host decides whether and how to print it. The reach
                    // is asked for from the pickup layer's own function - the same call
                    // the gate decides with - and only for the traced item, so an
                    // ordinary candidate pays nothing for a number nothing reads. The
                    // gate refuses illegal bounds before it has a reach; -1 is this
                    // file's invalid marker.
                    float tracedItemRadius;
                    float tracedReach;
                    if (!PickupAbsorbMath.TryComputeReach(pendingBounds[i],
                        reachRadius, contactRadiusWorldM, out tracedItemRadius,
                        out tracedReach))
                    {
                        tracedItemRadius = -1f;
                        tracedReach = -1f;
                    }
                    traceLinesLogged = traceLinesLogged + 1;
                    traceLastDistance = evaluatedDistance;
                    traceLastReach = tracedReach;
                    traceLastBallZ = center.z;
                    traceLastOutcome = (int)outcome;
                }

                if (outcome == AbsorbOutcome.Absorb)
                {
                    pendingAccepted[i] = true;
                }
                else
                {
                    RecordReject(pendingItemIds[i], (int)outcome, evaluatedDistance);
                    LogRejectDiagnosticsOnce(i, outcome, center);
                }
            }
        }

        /// <summary>
        /// Stage 6: commits the accepted gains and drops them from the queue.
        /// </summary>
        /// <remarks>
        /// A rejected gain keeps its candidate queued (the item is not consumed),
        /// and a failed commit leaves every total untouched. The queue is compacted
        /// in place so no list or allocation is needed per step.
        /// </remarks>
        private void CommitAcceptedAbsorbs()
        {
            if (!hasLocalRow)
            {
                // Silent before: with no local ledger row every accepted candidate is
                // dropped without a commit, a reject or an absorb event.
                LogCommitFailureOnce("(none)", 0, 0, 0, -1);
                return;
            }
            int write = 0;
            for (int read = 0; read < pendingCandidateCount; read++)
            {
                bool absorbed = false;
                if (pendingAccepted[read])
                {
                    long gain;
                    if (PickupAbsorbMath.TryAbsorbValueToLedgerGain(pendingValues[read],
                        out gain))
                    {
                        absorbed = TryCommitAbsorb(pendingItemIds[read],
                            pendingGenerations[read], gain);
                    }
                    else
                    {
                        // Also silent before: a value the gain mapping refuses leaves
                        // the candidate accepted-but-uncommitted forever.
                        LogCommitFailureOnce(pendingItemIds[read], 0, 0, 0, -1);
                    }
                }
                if (absorbed)
                {
                    absorbedCount = absorbedCount + 1;
                    // Recorded only here, inside the branch where the ledger commit
                    // returned true, so an event always corresponds to a total that
                    // actually moved.
                    RecordAbsorbedEvent(pendingItemIds[read], pendingPositions[read]);
                    continue;
                }
                CompactPending(write, read);
                write = write + 1;
            }
            pendingCandidateCount = write;
        }

        /// <summary>
        /// Copies one pending-candidate slot onto an earlier slot of the same queue.
        /// </summary>
        /// <remarks>
        /// The single implementation of in-place queue compaction. Both removers - the
        /// commit path (drop what was absorbed) and <see cref="WithdrawCandidate"/> (drop
        /// what the host released) - walk the queue with a read and a write cursor and
        /// keep the surviving order, so the nine per-candidate fields are copied in one
        /// place: two copies of this block would eventually disagree about one field
        /// (pendingSubmitTimes is the one a copy forgets), and a candidate would then be
        /// judged with another candidate's age or bounds. Same slot is a no-op, so a
        /// converging walk costs no copies.
        /// </remarks>
        private void CompactPending(int write, int read)
        {
            if (write == read) return;
            pendingAbsorbable[write] = pendingAbsorbable[read];
            pendingKinds[write] = pendingKinds[read];
            pendingBounds[write] = pendingBounds[read];
            pendingPositions[write] = pendingPositions[read];
            pendingValues[write] = pendingValues[read];
            pendingVolumeK[write] = pendingVolumeK[read];
            pendingItemIds[write] = pendingItemIds[read];
            pendingGenerations[write] = pendingGenerations[read];
            pendingAccepted[write] = pendingAccepted[read];
            pendingSubmitTimes[write] = pendingSubmitTimes[read];
        }

        private bool TryCommitAbsorb(string itemInstanceId, int generation, long gain)
        {
            absorbRequestSequence = absorbRequestSequence + 1;
            int claimFailure;
            if (!GrowthLedgerMath.TrySubmitClaimWithFailure(roundState, playerIds, lifeStates,
                playerCount, commitItemIds, commitGenerations, commitCount, claimItemIds,
                claimGenerations, claimPlayerIds, claimFrames, claimRequestSequences,
                ref claimCount, roundId, roundId, itemInstanceId, generation,
                localPlayerId, authorityFrame, absorbRequestSequence, out claimFailure))
            {
                LogCommitFailureOnce(itemInstanceId, gain, claimFailure, 0, -1);
                return false;
            }

            int commitRow;
            if (!GrowthLedgerMath.TryResolveClaims(roundState, playerIds, confirmedVolumes,
                bankedScores, lifeStates, respawnAtSet, respawnAt, playerGenerations,
                playerCount, commitItemIds, commitGenerations, commitWinners,
                commitSequences, commitGains, ref commitCount, claimItemIds,
                claimGenerations, claimPlayerIds, claimFrames, claimRequestSequences,
                ref claimCount, predictedPlayerIds, predictedItemIds,
                predictedGenerations, predictedGains, ref predictedCount, roundId, roundId,
                itemInstanceId, generation, gain, out commitRow))
            {
                LogCommitFailureOnce(itemInstanceId, gain, 0, 1, -1);
                return false;
            }
            if (commitRow == GrowthLedgerMath.NoRow)
            {
                // TryResolveClaims reports success with NoRow when no claim matched the
                // event. Treating that as a commit would be a silent non-absorb: no
                // event, no reject, candidate stays queued. Logged as commitRow=-1.
                LogCommitFailureOnce(itemInstanceId, gain, 0, 0, -1);
                return false;
            }

            long confirmed = confirmedVolumes[localRow];
            long banked = bankedScores[localRow];
            if (confirmed < 0 || banked < 0) return false;
            unbankedVolumeUnits = confirmed;
            bankedScoreUnits = banked;
            return true;
        }

        /// <summary>
        /// Stage 7: re-derives the three sizes and the growth fraction.
        /// </summary>
        /// <remarks>
        /// Nothing here accumulates: a failed derive leaves the previous sizes in
        /// place, and the next step tries again with the same confirmed volume.
        /// </remarks>
        private void DeriveSize()
        {
            float coreRadius;
            float collisionRadius;
            float semanticDiameter;
            float effectiveVolumeUnits;
            if (!SizeModelMath.TryDerive(confirmedVolumeUnits, baselineVolumeUnits,
                cubicMetersPerVolumeUnit, maxCoreRadiusWorldM,
                collisionToCoreRadiusRatio, semanticDiameterPerCoreDiameter,
                out coreRadius, out collisionRadius, out semanticDiameter,
                out effectiveVolumeUnits))
            {
                return;
            }
            coreRadiusWorldM = coreRadius;
            collisionRadiusWorldM = collisionRadius;
            semanticDiameterWorldM = semanticDiameter;
            DeriveGrowthFraction();
        }

        /// <summary>
        /// Recomputes the confirmed total from its two confirmed sources and
        /// re-derives the three sizes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the only place the volume identity
        /// confirmed = unbanked + carried is written. Both the per-step size
        /// stage and a successful conversion call it, so the identity has one
        /// implementation: neither caller can add the carried residual twice,
        /// skip it, or apply it in a different order.
        /// </para>
        /// <para>
        /// unbankedVolumeUnits is the ledger's read-back (a committed value,
        /// never a prediction) and carriedVolumeUnits is already-scored volume
        /// that can never be banked again. Nothing here accumulates; a failed
        /// size derive leaves the previous sizes in place, and the next call
        /// retries with the same confirmed total.
        /// </para>
        /// </remarks>
        private void RecomputeConfirmedAndSize()
        {
            confirmedVolumeUnits = (float)unbankedVolumeUnits + carriedVolumeUnits;
            DeriveSize();
        }

        /// <summary>
        /// The one feedback value Docs/Design/BallMotion/README.md assigns to the
        /// assembly layer: the already-clamped growth fraction the motion layer
        /// derates its caps with. The mapping itself stays its own helper so the
        /// radius relationship exists in exactly one place.
        /// </summary>
        private void DeriveGrowthFraction()
        {
            float span = maxCoreRadiusWorldM - baseCoreRadiusWorldM;
            if (span <= 0f)
            {
                growthFraction = 0f;
                return;
            }
            float fraction = (coreRadiusWorldM - baseCoreRadiusWorldM) / span;
            if (!KatamariMath.IsFiniteFloat(fraction)) return;
            if (fraction < 0f) fraction = 0f;
            if (fraction > 1f) fraction = 1f;
            growthFraction = fraction;
        }

        private void UpdateFollowRig(Vector3 center, float dt)
        {
            Vector3 rigPosition;
            float rigYaw;
            float height;
            if (!FollowRigMath.TryUpdate(heightFilterHz, yawFilterHz, center,
                semanticDiameterWorldM * 0.5f, headingDegrees,
                dt, eyeBaseUpMeters, eyeUpPerRadius, eyeUpMinMeters, eyeUpMaxMeters,
                eyeBaseBackMeters, eyeBackPerRadius, eyeMaxBackM, discontinuous,
                hasFollowRigFrame, followRigWorldPosition, followRigYawDegrees,
                followRigHeightMeters, out rigPosition, out rigYaw, out height))
            {
                return;
            }
            followRigWorldPosition = rigPosition;
            followRigYawDegrees = rigYaw;
            followRigHeightMeters = height;
            hasFollowRigFrame = true;
        }

        private void ApplyPresentation()
        {
            if (ballPhysicsRoot != null) ballPhysicsRoot.position = ballCenterWorld;
            if (ballVisualRollRoot != null) ballVisualRollRoot.rotation = visualRollRotation;
            if (followRigRoot != null && hasFollowRigFrame)
            {
                followRigRoot.position = followRigWorldPosition;
                followRigRoot.rotation = Quaternion.Euler(0f, followRigYawDegrees, 0f);
            }
        }

        // ------------------------------------------------------------------
        // Public seams: round bootstrap, input, scene data, conversion.
        // ------------------------------------------------------------------

        /// <summary>
        /// Walks the ledger's own legal edges to Playing so banking and commits can
        /// happen. A future round controller owns this flow; the motor only forwards.
        /// </summary>
        public bool TryBeginPlaying(float now)
        {
            if (!hasLocalRow) return false;
            LedgerPhase phase = (LedgerPhase)roundState[GrowthLedgerMath.RoundPhaseState];
            if (phase == LedgerPhase.Playing) return true;
            if (phase != LedgerPhase.Lobby) return false;
            if (!GrowthLedgerMath.TryTransition(roundState, roundFlags, roundClock,
                LedgerPhase.Ready, now, false, 0f)) return false;
            if (!GrowthLedgerMath.TryTransition(roundState, roundFlags, roundClock,
                LedgerPhase.Preparing, now, false, 0f)) return false;
            return GrowthLedgerMath.TryTransition(roundState, roundFlags, roundClock,
                LedgerPhase.Playing, now, false, 0f);
        }

        /// <summary>
        /// Restarts this ball's round in place: a fresh baseline-sized ball at the given
        /// pose, an empty local ledger row, back in Playing, without reloading the scene.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What is reset, in order: the ledger row and every round-scoped store
        /// (<see cref="GrowthLedgerMath.TryResetRowForNewRound"/> - confirmed volume,
        /// banked score, commits, claims, predictions, bank-request markers, award keys);
        /// this motor's round id; the absorb request sequence; the queued candidates; the
        /// absorbed-event ring's occupancy; the reject ring; the per-round volume
        /// readings and the absorbed count; the planar velocity and stuck timer; the
        /// input debounce latches; the heading; the three sizes, re-priced from zero
        /// volume by the same mapping Start used (so the ball is its baseline diameter
        /// again, not a hardcoded 0.30 m); and the world pose.
        /// </para>
        /// <para>
        /// The absorbed-event ABSOLUTE count is deliberately NOT rewound. A host keeps
        /// its own cursor at that number and only resyncs when it goes backwards, so a
        /// reset that rewound it would replay dead events; a reset that skipped it
        /// forward would make a host miss everything until the next wrap. Keeping it
        /// monotonic means a retry needs no special case in any reader: the retained
        /// window is simply empty (GetAbsorbedEventDroppedCount reports the cleared
        /// events), and the new round's events continue at the same absolute numbers.
        /// </para>
        /// <para>
        /// Deliberately untouched: the registered obstacles (a scene's geometry does not
        /// change with a round), follow-rig tuning, localClockSeconds and authorityFrame
        /// (clocks do not run backwards), the diagnostic log caps (they exist to bound
        /// console output for the whole session, so a retry must not refill them), and
        /// the overflow latch (a mis-wired scene does not heal by retrying). The scene's
        /// props are the host's concern: this seam restarts the ball, not the map.
        /// </para>
        /// <para>
        /// A new round id is required and must differ from the current one: commit keys
        /// (item, round, generation) must be new for a new round, and an unchanged id
        /// would make an external ledger or host treat the new absorbs as replays of the
        /// old round's. Reasons: 0 ok / 1 noLocalRow / 2 invalidRoundId /
        /// 3 invalidStartPose / 4 roundIdUnchanged / 5 sizeRefused / 6 ledgerRefused /
        /// 7 reenterFailed. Reason 6 means the ledger rule refused before anything moved;
        /// the rule's own code is then in <see cref="GetLastResetLedgerFailure"/>, and -1
        /// there means the ledger was never consulted. Reason 7 means the row was reset
        /// to Lobby but the walk back to Playing was refused, which only a corrupt round
        /// store can cause; the ball is then reset but not playing, and a later
        /// TryBeginPlaying(0f) can finish it.
        /// </para>
        /// </remarks>
        public bool TryResetForNewRound(string newRoundId, Vector3 startPosition,
            float startHeadingDegrees, out int reason)
        {
            reason = 0;
            lastResetLedgerFailure = -1;
            if (!hasLocalRow) { reason = 1; return false; }
            if (newRoundId == null || newRoundId.Length == 0) { reason = 2; return false; }
            if (!KatamariMath.IsFiniteVector(startPosition)) { reason = 3; return false; }
            if (!KatamariMath.IsFiniteFloat(startHeadingDegrees)) { reason = 3; return false; }
            if (string.CompareOrdinal(newRoundId, roundId) == 0) { reason = 4; return false; }

            // The baseline size the new round starts at, priced by the same zero-volume
            // mapping Start used. Checked before anything moves, so a refused derive
            // leaves the whole round untouched instead of leaving a grown ball in a
            // reset ledger.
            float resetCoreRadius;
            float resetCollisionRadius;
            float resetSemanticDiameter;
            float resetEffectiveVolumeUnits;
            if (!SizeModelMath.TryDerive(0f, baselineVolumeUnits, cubicMetersPerVolumeUnit,
                maxCoreRadiusWorldM, collisionToCoreRadiusRatio,
                semanticDiameterPerCoreDiameter, out resetCoreRadius,
                out resetCollisionRadius, out resetSemanticDiameter,
                out resetEffectiveVolumeUnits))
            {
                reason = 5;
                return false;
            }

            int ledgerFailure;
            if (!GrowthLedgerMath.TryResetRowForNewRound(roundState, roundFlags, roundClock,
                playerIds, confirmedVolumes, bankedScores, lifeStates, respawnAtSet,
                respawnAt, playerGenerations, playerCount, commitItemIds,
                commitGenerations, commitWinners, commitSequences, commitGains,
                ref commitCount, claimItemIds, claimGenerations, claimPlayerIds,
                claimFrames, claimRequestSequences, ref claimCount, predictedPlayerIds,
                predictedItemIds, predictedGenerations, predictedGains, ref predictedCount,
                bankRequestPlayerIds, bankRequestIds, ref bankRequestCount, awardedKeys,
                awardedPoints, ref awardedCount, newRoundId, localPlayerId, out ledgerFailure))
            {
                lastResetLedgerFailure = ledgerFailure;
                reason = 6;
                return false;
            }
            lastResetLedgerFailure = 0;

            // Everything below is an assignment with no failure path: the ledger already
            // accepted, and every value written here is either a constant or an output
            // of the derive checked above.
            roundId = newRoundId;
            absorbRequestSequence = 0;

            ClearCandidates();
            // The shell's items are the old round's attached props. Clear it here as well
            // as on the host's field reset: a retry must not inherit a bench's protrusion
            // even when no host reset runs first.
            ClearRadialProxies();
            absorbedEventRingCount = 0;
            rejectEventCount = 0;
            absorbedCount = 0;
            unbankedVolumeUnits = 0;
            carriedVolumeUnits = 0f;
            confirmedVolumeUnits = 0f;
            bankedScoreUnits = 0;
            lastAwardFailure = 0;
            // awardedKeys / awardedPoints / awardedCount were cleared by the ledger rule.

            coreRadiusWorldM = resetCoreRadius;
            collisionRadiusWorldM = resetCollisionRadius;
            semanticDiameterWorldM = resetSemanticDiameter;
            DeriveGrowthFraction();

            inputForward = 0f;
            inputTurn = 0f;
            inputLateral = 0f;
            ControlIntentMath.TryReset(previousDown, lastActionTime, comfortState);

            TeleportTo(startPosition);
            headingDegrees = startHeadingDegrees;

            // Same walk as Start's autoStartPlaying: Lobby -> Ready -> Preparing ->
            // Playing, through the ledger's own edges. Always entered, because this
            // method's contract is "started again", and a Lobby row cannot absorb.
            if (!TryBeginPlaying(0f))
            {
                reason = 7;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The ledger rule's failure code from the most recent
        /// <see cref="TryResetForNewRound"/>: -1 when the ledger was not consulted
        /// (an input this motor refused first), 0 after the rule accepted, 1..11 for the
        /// rule's own refusal. Same pattern as <see cref="GetLastAwardFailure"/>.
        /// </summary>
        public int GetLastResetLedgerFailure()
        {
            return lastResetLedgerFailure;
        }

        /// <summary>
        /// The round id this ball's ledger keys currently use. A retry owner reads it to
        /// build the next id, so the id has one owner instead of a copy in the caller.
        /// </summary>
        public string GetRoundId()
        {
            return roundId;
        }

        /// <summary>Input seam for the future adapter; replaces the debug fields.</summary>
        public void SetMoveAxes(float forward, float turn, float lateral)
        {
            debugForward = forward;
            debugTurn = turn;
            debugLateral = lateral;
        }

        public void SetMenuOpen(bool open)
        {
            debugMenuOpen = open;
        }

        public void SetAllowedKinds(string[] kinds, int count)
        {
            if (kinds == null) return;
            if (count < 0 || count > kinds.Length) return;
            allowedKinds = kinds;
            allowedKindCount = count;
        }

        /// <summary>
        /// Queues one host-collected candidate for the next step's pickup stage.
        /// </summary>
        /// <remarks>
        /// Candidate collection is a host-side concern in the pickup layer's own
        /// contract (it owns no spatial registry), so the motor only carries the
        /// candidate to the gate and never scans the scene itself.
        /// <para>
        /// The queue holds PendingCandidateCapacity = 4 entries. A candidate
        /// submitted while it is full is DROPPED (false) and latched into the
        /// overflow signal: losing a candidate silently loses an absorbable
        /// item, which is a data error in the host's batching, not a gameplay
        /// choice. The latch is read by an adapter at a checkpoint; nothing is
        /// logged per frame.
        /// </para>
        /// </remarks>
        public bool SubmitCandidate(bool absorbable, string kind, Vector3 bounds,
            Vector3 worldPosition, float valueUnits, string itemInstanceId, int itemGeneration)
        {
            // The un-factored form: the item plays at the original's default k (0.70). A
            // host that knows its per-item factor (the level importer fills
            // PickupFieldHost.propVolumeK) calls SubmitCandidateWithVolumeK instead.
            return SubmitCandidateWithVolumeK(absorbable, kind, bounds, worldPosition,
                valueUnits, itemInstanceId, itemGeneration,
                PickupAbsorbMath.DefaultVolumeK);
        }

        /// <summary>
        /// <see cref="SubmitCandidate"/> plus the item's per-item volume factor k.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `volumeK` is the original's `Volume = k * bbox` factor for THIS item: the gate's
        /// Req depends on it (`Req = floor(1000*cbrt(60*k*a*b*c/pi))`), so two items with
        /// the same bounds but different k are not equally absorbable. It is validated here
        /// against the one authoring range (`0 &lt; k &lt;= MaxVolumeK`) so a bad value is
        /// refused at the queue instead of silently becoming "blocking" in the gate; a
        /// refused submit returns false and the host logs it once (a data error).
        /// </para>
        /// <para>
        /// Kept as a separate entry point rather than an optional parameter because
        /// UdonSharp does not support default arguments, and because the labs that submit a
        /// hand-authored prop (the physics test field, the CPU-skirmish playground) should
        /// not have to name a k they do not have.
        /// </para>
        /// </remarks>
        public bool SubmitCandidateWithVolumeK(bool absorbable, string kind, Vector3 bounds,
            Vector3 worldPosition, float valueUnits, string itemInstanceId, int itemGeneration,
            float volumeK)
        {
            // Same ownership rule as the scene sets: the queue allocates on first use, so
            // a submit that arrives before Start is queued instead of throwing.
            if (!EnsureCandidateBuffers())
            {
                if (!loggedCandidateBuffersMissing)
                {
                    loggedCandidateBuffersMissing = true;
                    Debug.LogError("[BallMotor] candidate queue could not be allocated;"
                        + " submits are refused (reason=no-buffers)");
                }
                return false;
            }
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            if (itemGeneration < 0) return false;
            if (!KatamariMath.IsFiniteFloat(volumeK)) return false;
            if (volumeK <= 0f || volumeK > PickupAbsorbMath.MaxVolumeK) return false;

            // Idempotent by item id: re-submitting something already queued is a no-op
            // that returns true. The queue is the single owner of "is this queued", so a
            // host needs no latch of its own and a latch can never disagree with it.
            for (int i = 0; i < pendingCandidateCount; i++)
            {
                if (pendingItemIds[i] == itemInstanceId) return true;
            }

            if (pendingCandidateCount >= PendingCandidateCapacity)
            {
                SignalOverflow("pending candidate capacity 4 exceeded");
                return false;
            }
            int index = pendingCandidateCount;
            pendingAbsorbable[index] = absorbable;
            pendingKinds[index] = kind;
            pendingBounds[index] = bounds;
            pendingPositions[index] = worldPosition;
            pendingValues[index] = valueUnits;
            pendingVolumeK[index] = volumeK;
            pendingItemIds[index] = itemInstanceId;
            pendingGenerations[index] = itemGeneration;
            pendingAccepted[index] = false;
            pendingSubmitTimes[index] = Time.time;
            pendingCandidateCount = pendingCandidateCount + 1;
            return true;
        }

        /// <summary>
        /// Removes one queued candidate by item id, compacting the queue.
        /// </summary>
        /// <remarks>
        /// Additive seam, and the missing half of SubmitCandidate. A queued candidate
        /// is never removed except by a successful commit, so a host that queues on
        /// proximity pins the four slots with an item the ball then rolls away from:
        /// the item is refused out-of-reach on every step forever and no nearer prop
        /// can ever take that slot. Withdrawing lets a host release what it can no
        /// longer plausibly absorb and re-queue later.
        /// <para>
        /// Compaction is <see cref="CompactPending"/>, the same helper the commit path
        /// uses, so no list is allocated and the two removers cannot drift. Returns
        /// true when at least one entry was removed; the ledger is untouched (a
        /// candidate was never a claim).
        /// </para>
        /// </remarks>
        public bool WithdrawCandidate(string itemInstanceId)
        {
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            if (pendingCandidateCount <= 0) return false;

            int write = 0;
            bool removed = false;
            for (int read = 0; read < pendingCandidateCount; read++)
            {
                if (pendingItemIds[read] == itemInstanceId)
                {
                    removed = true;
                    continue;
                }
                CompactPending(write, read);
                write = write + 1;
            }
            pendingCandidateCount = write;
            return removed;
        }

        public void ClearCandidates()
        {
            pendingCandidateCount = 0;
        }

        /// <summary>
        /// Sets the analytic ground plane (the always-present support fallback).
        /// </summary>
        /// <remarks>
        /// The plane is the same <c>dot(normal, point) = offset</c> pair
        /// GroundContactMath already consumed, so nothing downstream needs a new
        /// convention. It is separate from <see cref="AddPlane"/> on purpose: a boundary
        /// plane is a wall and never a support, and the ground is a support and never a
        /// sweep obstacle (design §3.4). The raw pair is stored (GroundContactMath
        /// rescales normal and offset together), and the surface pair is re-resolved so
        /// the diagnostics do not keep the previous floor.
        /// </remarks>
        public bool SetGroundPlane(Vector3 normal, float offset)
        {
            if (!KatamariMath.IsFiniteVector(normal)) return false;
            if (!KatamariMath.IsFiniteFloat(offset)) return false;
            Vector3 unitNormal;
            float rawLength;
            if (!KatamariMath.TryNormalize(normal, out unitNormal, out rawLength)) return false;
            // A wall-like ground plane cannot answer "what is the height under the probe",
            // so it is refused here instead of failing every step inside the query. The
            // same threshold is enforced by SupportSurfaceMath.TryResolveSupport.
            if (unitNormal.y <= 1e-3f) return false;
            groundPlaneNormal = normal;
            groundPlaneOffset = offset;
            // The floor moved: the previous support/step classification is about the old
            // one. Re-derive from the ball's own bottom on the next step.
            hasSupportFrame = false;
            lastSupportHeightWorldM = ballCenterWorld.y - collisionRadiusWorldM;
            stepMaskValid = false;
            if (boxMinimumBuffer != null) ResolveTerrainSupport(ballCenterWorld);
            return true;
        }

        public bool AddPlane(Vector3 normal, float offset)
        {
            // First writer allocates: the arena host registers in its own Update, and a
            // false here is its existing "this plane was refused" answer.
            if (!EnsureRegistrationBuffers()) return false;
            if (planeCount >= MaxPlaneCount)
            {
                SignalOverflow("plane capacity 8 exceeded");
                return false;
            }
            if (!KatamariMath.IsFiniteVector(normal)) return false;
            if (!KatamariMath.IsFiniteFloat(offset)) return false;
            planeNormalBuffer[planeCount] = normal;
            planeOffsetBuffer[planeCount] = offset;
            planeCount = planeCount + 1;
            RebuildObstacles();
            return true;
        }

        public bool AddBox(Vector3 minimum, Vector3 maximum)
        {
            // Same rule as AddPlane: the writer ensures the destination.
            if (!EnsureRegistrationBuffers()) return false;
            if (boxCount >= MaxBoxCount)
            {
                SignalOverflow("box capacity 8 exceeded");
                return false;
            }
            if (!KatamariMath.IsFiniteVector(minimum)) return false;
            if (!KatamariMath.IsFiniteVector(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y ||
                minimum.z > maximum.z) return false;
            int boxIndex = boxCount;
            boxMinimumBuffer[boxCount] = minimum;
            boxMaximumBuffer[boxCount] = maximum;
            boxCount = boxCount + 1;
            // The new box has no step classification yet; the next step evaluates it.
            stepMaskBoxesDirty = true;
            RebuildObstacles();
            // Ball contact v2: the same box as an axis-aligned OBB under the v1
            // support-table id (1 + index), so an existing scene runs with the flag on
            // without re-authoring. v1 never reads it (design §1.4).
            RegisterBoxObb(boxIndex, minimum, maximum);
            return true;
        }

        /// <summary>
        /// Registers one static ramp: an oriented walking plane plus a horizontal
        /// footprint, a support surface ONLY (never a sweep obstacle).
        /// </summary>
        /// <param name="center">
        /// A point on the walking plane (LevelArenaHost passes the box's +Y face centre
        /// from <see cref="SupportSurfaceMath.TryBakeRamp"/>), not the box centre.
        /// </param>
        /// <param name="normal">Plane normal; must point up (n.y &gt; 0.05).</param>
        /// <param name="right">The footprint's horizontal axis; only its XZ projection is used.</param>
        /// <param name="halfWidth">Half the footprint along <paramref name="right"/>, metres.</param>
        /// <param name="halfRun">Half the HORIZONTAL run along the downhill direction, metres.</param>
        /// <returns>
        /// True when the ramp is now registered; false when it was refused, in which case
        /// nothing is written and <see cref="GetLastRampRefusalCode"/> says why.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is a SEAM, not a rule: the acceptance decision (upward normal, slope
        /// guard, degenerate geometry) is
        /// <see cref="SupportSurfaceMath.TryValidateRamp"/>, and the capacity check is
        /// <see cref="SupportSurfaceMath.IsRampCountAllowed"/>; this method only orders the
        /// calls and stores the result. The slope guard exists because over the limit
        /// neither registering (the ball climbs a wall) nor ignoring (the ball passes
        /// through) is correct: the author must replace that board with an AddBox wall.
        /// </para>
        /// <para>
        /// A ramp must never also be registered through <see cref="AddBox"/>: the sweep is
        /// a radius-expanded AABB, so the tilted board would stop the ball at the ramp
        /// foot (design §5.5). LevelArenaHost keeps the two arrays separate and reports a
        /// collider that appears in both.
        /// </para>
        /// <para>
        /// Registering a ramp does not rebuild the sweep arrays and does not touch the
        /// step mask: ramps are read directly from these buffers by the support query every
        /// step. With terrain support disabled the query receives a ramp count of 0, so the
        /// feature switch stays bit-for-bit the pre-P3 behaviour.
        /// </para>
        /// </remarks>
        public bool AddRamp(Vector3 center, Vector3 normal, Vector3 right,
            float halfWidth, float halfRun)
        {
            if (!EnsureRegistrationBuffers())
            {
                // A ramp has a reason channel, so the refusal is code-visible, not just a
                // false: 0 slope is honest (nothing was measured) and capacity is not the
                // cause, so the code names the buffer instead.
                RefuseRamp(RampRefusalNoBuffers, 0f);
                return false;
            }
            if (!SupportSurfaceMath.IsRampCountAllowed(rampCount))
            {
                RefuseRamp(SupportSurfaceMath.RampRefusalCapacity, 0f);
                SignalOverflow("ramp capacity 4 exceeded");
                return false;
            }

            Vector3 unitNormal;
            Vector3 unitRight;
            float slopeDegrees;
            int refusalCode;
            if (!SupportSurfaceMath.TryValidateRamp(center, normal, right, halfWidth, halfRun,
                maxSupportSlopeDegrees, out unitNormal, out unitRight, out slopeDegrees,
                out refusalCode))
            {
                RefuseRamp(refusalCode, slopeDegrees);
                return false;
            }

            int rampIndex = rampCount;
            rampCenterBuffer[rampCount] = center;
            rampNormalBuffer[rampCount] = unitNormal;
            rampRightBuffer[rampCount] = unitRight;
            rampHalfWidthBuffer[rampCount] = halfWidth;
            rampHalfRunBuffer[rampCount] = halfRun;
            rampCount = rampCount + 1;
            // Ball contact v2: the ramp as a solid slab whose walking face is the same
            // plane (design §1.5). v1 never reads it.
            RegisterRampSlab(rampIndex, center, unitNormal, unitRight, halfWidth, halfRun);
            return true;
        }

        /// <summary>
        /// Removes every static ramp. Planes, static boxes and prop supports stay
        /// registered (the map's other geometry did not move).
        /// </summary>
        public void ClearRamps()
        {
            rampCount = 0;
        }

        /// <summary>Static ramps currently registered (0..4).</summary>
        public int GetRampCount()
        {
            return rampCount;
        }

        /// <summary>How many static ramps one ball can take at once (4).</summary>
        public int GetRampCapacity()
        {
            return SupportSurfaceMath.MaxRampCount;
        }

        /// <summary>Refused <see cref="AddRamp"/> calls so far (monotonic).</summary>
        public int GetRampRefusalCount()
        {
            return rampRefusalCount;
        }

        /// <summary>
        /// Why the last <see cref="AddRamp"/> was refused: one of
        /// <c>SupportSurfaceMath.RampRefusal*</c>, or 0 before any refusal.
        /// </summary>
        public int GetLastRampRefusalCode()
        {
            return rampRefusalCode;
        }

        /// <summary>The last <see cref="AddRamp"/> refusal as a literal, or "".</summary>
        public string GetLastRampRefusalReason()
        {
            if (rampRefusalCode == RampRefusalNoBuffers) return "set buffers unavailable";
            return SupportSurfaceMath.RampRefusalReason(rampRefusalCode);
        }

        /// <summary>The measured slope of the last refused ramp, degrees (0 when none).</summary>
        public float GetLastRampRefusalSlopeDegrees()
        {
            return rampRefusalSlopeDegrees;
        }

        /// <summary>The slope guard-rail this motor applies, degrees.</summary>
        public float GetMaxSupportSlopeDegrees()
        {
            return maxSupportSlopeDegrees;
        }

        private void RefuseRamp(int refusalCode, float slopeDegrees)
        {
            rampRefusalCount = rampRefusalCount + 1;
            rampRefusalCode = refusalCode;
            rampRefusalSlopeDegrees = slopeDegrees;
        }

        /// <summary>
        /// Replaces the whole dynamic blocking-box set in one call: the boxes of the nearby
        /// props this ball cannot absorb yet.
        /// </summary>
        /// <returns>
        /// True when the set is now the one passed (including when it was already exactly
        /// that set); false when the call was refused, in which case the previous set stays
        /// registered.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Batch replace rather than add/remove (BLOCKING_PROPS_PLAN.md §2 A): the caller
        /// re-selects the set from scratch on every scan, and the motor deliberately has no
        /// per-box removal. A refused call is not applied, the same rule AddBox follows, so
        /// one bad scan cannot open a hole in the world.
        /// </para>
        /// <para>
        /// This is a SEAM, not a rule: the motor does not know what a prop is, which props
        /// exist, or when one becomes absorbable. The pickup host owns that question and
        /// answers it with the same volume limit the absorb gate uses, so a prop stops
        /// being a wall on the same threshold that makes it absorbable.
        /// </para>
        /// <para>
        /// Ordering. A blocking box is appended after every static box, so obstacle indices
        /// remain planes, then static boxes, then blocking boxes. SweepMath separates planes
        /// from boxes by array length alone and orients box contact normals the same way it
        /// orients wall normals, so a blocking box is blocked and slid against by exactly
        /// the wall path.
        /// </para>
        /// <para>
        /// Refusals are counted separately from <see cref="SignalOverflow"/> (see the
        /// blocking-refusal remarks on the fields): a neighbour count above this ball's
        /// capacity is a normal gameplay answer, not a scene wiring error. Nothing logs per
        /// call; diagnostics poll <see cref="GetBlockingBoxRefusalCount"/>. Re-sending the
        /// set already registered returns true without rebuilding the sweep arrays, which
        /// is what keeps a 20 Hz host scan allocation-free in the steady state.
        /// </para>
        /// </remarks>
        public bool SetBlockingBoxes(Vector3[] minima, Vector3[] maxima, int count)
        {
            // First writer allocates (ownership note above the set buffers): the pickup
            // field's Update can reach this before Start has run, and a set that arrives
            // then must be kept, not thrown away.
            if (!EnsureRegistrationBuffers())
            {
                RefuseBlockingBoxes("set buffers unavailable");
                return false;
            }
            if (count < 0)
            {
                RefuseBlockingBoxes("negative count");
                return false;
            }
            if (count > MaxBlockingBoxCount)
            {
                RefuseBlockingBoxes("capacity 6 exceeded");
                return false;
            }
            if (count > 0)
            {
                if (minima == null || maxima == null)
                {
                    RefuseBlockingBoxes("null minima/maxima");
                    return false;
                }
                if (count > minima.Length || count > maxima.Length)
                {
                    RefuseBlockingBoxes("count exceeds a buffer length");
                    return false;
                }
            }

            // Every box is validated before the first one is written, so a refused call
            // cannot leave half a set behind.
            for (int i = 0; i < count; i++)
            {
                if (!KatamariMath.IsFiniteVector(minima[i]))
                {
                    RefuseBlockingBoxes("non-finite minimum");
                    return false;
                }
                if (!KatamariMath.IsFiniteVector(maxima[i]))
                {
                    RefuseBlockingBoxes("non-finite maximum");
                    return false;
                }
                if (minima[i].x > maxima[i].x || minima[i].y > maxima[i].y ||
                    minima[i].z > maxima[i].z)
                {
                    RefuseBlockingBoxes("inverted box");
                    return false;
                }
            }

            // The caller caches the boxes it built, so the steady state re-sends exactly
            // the same floats. Only bit-for-bit equality skips the rebuild: anything else
            // takes the ordinary path, which is the only way a moved prop stays correct.
            if (count == blockingBoxCount)
            {
                bool unchanged = true;
                for (int i = 0; i < count; i++)
                {
                    if (minima[i].x != blockingMinimumBuffer[i].x ||
                        minima[i].y != blockingMinimumBuffer[i].y ||
                        minima[i].z != blockingMinimumBuffer[i].z ||
                        maxima[i].x != blockingMaximumBuffer[i].x ||
                        maxima[i].y != blockingMaximumBuffer[i].y ||
                        maxima[i].z != blockingMaximumBuffer[i].z)
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged) return true;
            }

            for (int i = 0; i < count; i++)
            {
                blockingMinimumBuffer[i] = minima[i];
                blockingMaximumBuffer[i] = maxima[i];
            }
            blockingBoxCount = count;
            RebuildObstacles();
            return true;
        }

        /// <summary>
        /// Empties the dynamic blocking set, leaving planes and static boxes registered.
        /// </summary>
        /// <remarks>
        /// The blocking set is a function of where the ball is, not of the scene, so it is
        /// cleared wherever the ball's position is set outside a normal step (a round reset
        /// teleports it) and wherever the whole obstacle set is cleared. Static geometry is
        /// deliberately NOT reset by a retry: the map is the same map. The next host scan
        /// re-registers whatever still blocks, within one scan interval.
        /// </remarks>
        public void ClearBlockingBoxes()
        {
            if (blockingBoxCount == 0) return;
            blockingBoxCount = 0;
            RebuildObstacles();
        }

        /// <summary>Blocking boxes currently registered with the sweep (0..6).</summary>
        public int GetBlockingBoxCount()
        {
            return blockingBoxCount;
        }

        /// <summary>How many blocking boxes the sweep can take at once (6).</summary>
        public int GetBlockingBoxCapacity()
        {
            return MaxBlockingBoxCount;
        }

        /// <summary>Refused <see cref="SetBlockingBoxes"/> calls so far (monotonic).</summary>
        public int GetBlockingBoxRefusalCount()
        {
            return blockingRefusalCount;
        }

        /// <summary>Why the last <see cref="SetBlockingBoxes"/> was refused, or "".</summary>
        public string GetBlockingBoxRefusalReason()
        {
            if (blockingRefusalReason == null) return "";
            return blockingRefusalReason;
        }

        // One refusal: a counter plus the latest reason. Every reason is a literal, so a
        // refusal costs no allocation and no console output even at the scan cadence.
        private void RefuseBlockingBoxes(string reason)
        {
            blockingRefusalCount = blockingRefusalCount + 1;
            blockingRefusalReason = reason;
        }

        /// <summary>
        /// Replaces the whole support-prop set in one call: the tops of the absorb-proof
        /// props the ball may stand on.
        /// </summary>
        /// <returns>
        /// True when the set is now the one passed (including when it was already exactly
        /// that set); false when the call was refused, in which case the previous set stays
        /// registered.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is the P2 seam, and it is deliberately shaped exactly like
        /// <see cref="SetBlockingBoxes"/>: batch replace, validate every box before writing
        /// any, same float-for-float shortcut for an unchanged set, a refused call keeps the
        /// previous set. The host owns which props are supports (it owns the prop table and
        /// the absorb gate); the motor does not know what a prop is.
        /// </para>
        /// <para>
        /// A support prop is never a sweep obstacle. It is registered because its top is
        /// within one step of the ball, and a box the ball may climb must be enterable -
        /// the same rule that removes a steppable static box from the sweep. The support
        /// table reads these buffers directly every step, so a changed set needs no sweep
        /// rebuild and costs no allocation; only the copy below is written.
        /// </para>
        /// <para>
        /// Capacity is <see cref="SupportSurfaceMath.MaxSupportPropCount"/> and the check is
        /// <see cref="SupportSurfaceMath.IsSupportPropCountAllowed"/> (one source, pinned by
        /// a test). Refusals are counted separately from <see cref="SignalOverflow"/> for the
        /// same reason blocking refusals are: more nearby props than six slots is a normal
        /// per-scan answer, not a scene wiring error, so it must not latch the one-shot
        /// overflow signal.
        /// </para>
        /// </remarks>
        public bool SetSupportProps(Vector3[] minima, Vector3[] maxima, int count)
        {
            // Same ownership rule as SetBlockingBoxes: the writer ensures its destination.
            if (!EnsureRegistrationBuffers())
            {
                RefuseSupportProps("set buffers unavailable");
                return false;
            }
            if (count < 0)
            {
                RefuseSupportProps("negative count");
                return false;
            }
            if (!SupportSurfaceMath.IsSupportPropCountAllowed(count))
            {
                RefuseSupportProps("capacity 6 exceeded");
                return false;
            }
            if (count > 0)
            {
                if (minima == null || maxima == null)
                {
                    RefuseSupportProps("null minima/maxima");
                    return false;
                }
                if (count > minima.Length || count > maxima.Length)
                {
                    RefuseSupportProps("count exceeds a buffer length");
                    return false;
                }
            }

            // Every box is validated before the first one is written, so a refused call
            // cannot leave half a set behind.
            for (int i = 0; i < count; i++)
            {
                if (!KatamariMath.IsFiniteVector(minima[i]))
                {
                    RefuseSupportProps("non-finite minimum");
                    return false;
                }
                if (!KatamariMath.IsFiniteVector(maxima[i]))
                {
                    RefuseSupportProps("non-finite maximum");
                    return false;
                }
                if (minima[i].x > maxima[i].x || minima[i].y > maxima[i].y ||
                    minima[i].z > maxima[i].z)
                {
                    RefuseSupportProps("inverted box");
                    return false;
                }
            }

            if (count == supportPropCount)
            {
                bool unchanged = true;
                for (int i = 0; i < count; i++)
                {
                    if (minima[i].x != supportPropMinimumBuffer[i].x ||
                        minima[i].y != supportPropMinimumBuffer[i].y ||
                        minima[i].z != supportPropMinimumBuffer[i].z ||
                        maxima[i].x != supportPropMaximumBuffer[i].x ||
                        maxima[i].y != supportPropMaximumBuffer[i].y ||
                        maxima[i].z != supportPropMaximumBuffer[i].z)
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged) return true;
            }

            for (int i = 0; i < count; i++)
            {
                supportPropMinimumBuffer[i] = minima[i];
                supportPropMaximumBuffer[i] = maxima[i];
            }
            supportPropCount = count;
            return true;
        }

        /// <summary>
        /// Empties the support-prop set. Static boxes and planes stay registered.
        /// </summary>
        /// <remarks>
        /// The set is a function of where the ball is, so it is cleared wherever the ball's
        /// position is set outside a normal step (the host also clears it in its own field
        /// reset). The next host scan re-registers whatever the ball can still stand on,
        /// within one scan interval; a ball that was standing on a cleared prop simply
        /// loses that support and falls under the P1 gravity rule.
        /// </remarks>
        public void ClearSupportProps()
        {
            if (supportPropCount == 0) return;
            supportPropCount = 0;
        }

        /// <summary>Support props currently registered (0..6).</summary>
        public int GetSupportPropCount()
        {
            return supportPropCount;
        }

        /// <summary>How many support props the ball can take at once (6).</summary>
        public int GetSupportPropCapacity()
        {
            return MaxSupportPropCount;
        }

        /// <summary>Refused <see cref="SetSupportProps"/> calls so far (monotonic).</summary>
        public int GetSupportPropRefusalCount()
        {
            return supportPropRefusalCount;
        }

        /// <summary>Why the last <see cref="SetSupportProps"/> was refused, or "".</summary>
        public string GetSupportPropRefusalReason()
        {
            if (supportPropRefusalReason == null) return "";
            return supportPropRefusalReason;
        }

        private void RefuseSupportProps(string reason)
        {
            supportPropRefusalCount = supportPropRefusalCount + 1;
            supportPropRefusalReason = reason;
        }

        public void ClearObstacles()
        {
            planeCount = 0;
            boxCount = 0;
            blockingBoxCount = 0;
            supportPropCount = 0;
            // Ramps are scene geometry like the static boxes: "all obstacles" clears them
            // too, and the arena's Reapply re-registers them after this call.
            rampCount = 0;
            // The step mask is indexed by boxing slot, so it must not outlive the boxes.
            ClearStepMask();
            // The v2 tables are the same scene geometry, so they go with them. Props go
            // first, so ClearObbs does not have to move a prop block down.
            ClearPropObbs();
            ClearObbs();
            RebuildObstacles();
        }

        /// <summary>True while the v2 contact solver owns this ball's position authority.</summary>
        public bool IsContactModelV2()
        {
            return contactModelV2Enabled;
        }

        /// <summary>
        /// Switches the position authority at runtime. The serialized default is false,
        /// which is the v1 path; this setter exists so a lab (and the EditMode tests) can
        /// turn the flag on without a second scene.
        /// </summary>
        /// <remarks>
        /// Nothing is reset on the way across: the v1 state is re-derived from the ball's
        /// position by the v1 path itself, and the v2 state is recomputed on the next v2
        /// step. The only carried detail is the needle sweep's previous frame, which is
        /// dropped so a switch cannot sweep a pin from a position it never had.
        /// </remarks>
        public void SetContactModelV2Enabled(bool enabled)
        {
            if (contactModelV2Enabled == enabled) return;
            contactModelV2Enabled = enabled;
            contactHasPreviousFrame = false;
        }

        // ------------------------------------------------------------------
        // Static OBB registration (design §1.3/§1.4/§5.2). AddBox and AddRamp keep
        // working for v1 and additionally register their v2 solid, so an existing
        // scene runs with the flag on without a second authoring pass.
        // ------------------------------------------------------------------

        /// <summary>
        /// Allocates the v2 tables if they do not exist yet, and reports whether they are
        /// usable. Registration may arrive before this component's Start (Unity's Start
        /// order between GameObjects is arbitrary), and a table that does not exist must
        /// not be a reason to lose the scene's geometry.
        /// </summary>
        private bool EnsureContactV2Buffers()
        {
            if (staticObbCenter != null) return true;
            AllocateContactV2Buffers();
            return staticObbCenter != null;
        }

        /// <summary>
        /// Registers one oriented box in the v2 static table: a centre, three orthonormal
        /// unit axes, three nonnegative half extents in metres, a surface id and a role.
        /// </summary>
        /// <returns>
        /// True when the box is now in the table; false when it was refused, in which case
        /// nothing is written and <see cref="GetLastStaticObbRefusalReason"/> says why.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Capacity is <see cref="ContactTuning.MaxStaticObbCount"/> (256). An entry
        /// beyond it is REFUSED and counted in both
        /// <see cref="GetStaticObbOverflowCount"/> and the motor's overflow signal, never
        /// queued or silently dropped: losing a solid means the ball rolls through a wall
        /// nobody can see (design §5.4).
        /// </para>
        /// <para>
        /// The axes must be unit length and orthogonal
        /// (<see cref="ObbContactMath.IsValidObb"/>). A sheared box (a non-uniformly
        /// scaled parent times a rotation) is refused rather than queried: the bake that
        /// produced it is wrong, and a box the author did not draw is worse than a missing
        /// one. <paramref name="role"/> is
        /// <see cref="ContactCodes.RoleBox"/>, <see cref="ContactCodes.RoleSlab"/>,
        /// <see cref="ContactCodes.RoleProp"/> or <see cref="ContactCodes.RoleBlocking"/>,
        /// and only labels the entry (design §2.1: no behaviour branches on it).
        /// </para>
        /// </remarks>
        public bool AddObb(Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
            Vector3 halfExtents, int surfaceId, int role)
        {
            if (!EnsureContactV2Buffers())
            {
                RefuseStaticObb("contact v2 buffers could not be allocated");
                return false;
            }
            if (surfaceId < 0)
            {
                RefuseStaticObb("negative surface id");
                return false;
            }
            if (staticObbCount >= ContactTuning.MaxStaticObbCount)
            {
                staticObbOverflowCount = staticObbOverflowCount + 1;
                RefuseStaticObb("obb capacity 256 exceeded");
                SignalOverflow("obb capacity 256 exceeded");
                return false;
            }
            if (!ObbContactMath.IsValidObb(center, axisX, axisY, axisZ, halfExtents))
            {
                RefuseStaticObb("not a finite, unit, orthogonal box");
                return false;
            }
            // The prop block lives immediately after the static prefix, so a new static
            // entry has to move it up one slot first. Static registration is a setup-time
            // event, so this copy runs at most a handful of times per scene.
            MovePropBlock(staticObbCount, staticObbCount + 1);
            int index = staticObbCount;
            staticObbCenter[index] = center;
            staticObbAxisX[index] = axisX;
            staticObbAxisY[index] = axisY;
            staticObbAxisZ[index] = axisZ;
            staticObbHalfExtents[index] = halfExtents;
            staticObbSurfaceId[index] = surfaceId;
            staticObbRole[index] = role;
            staticObbBoundingRadius[index] = ObbContactMath.BoundingRadius(halfExtents);
            staticObbCount = index + 1;
            // A new solid can enter the window next step, so the hysteresis must forget
            // the previous selection instead of assuming the near set is unchanged.
            windowPreviousCount = 0;
            return true;
        }

        /// <summary>Empties the v2 static table (the scene's geometry moved).</summary>
        public void ClearObbs()
        {
            // The prop block keeps its place after the (now empty) static prefix, i.e. it
            // moves down to index 0, so the logical range stays contiguous.
            MovePropBlock(staticObbCount, 0);
            staticObbCount = 0;
            windowPreviousCount = 0;
            nearObbCount = 0;
            windowDroppedCount = 0;
            contactIncomplete = false;
        }

        /// <summary>
        /// Replaces the whole prop OBB block: the blocking/absorb-proof props the host
        /// re-selects on its scan (design §1.4, §5.2).
        /// </summary>
        /// <returns>
        /// True when the block is now exactly the one passed; false when the call was
        /// refused, in which case the previous block stays registered.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Props are the SAME primitive as static boxes: an entry is a centre, three
        /// orthonormal unit axes and three nonnegative half extents, and it goes through
        /// <see cref="ContactSolveMath"/> exactly like a static entry - no prop branch
        /// anywhere in the solver. The caller labels entries with a surface id and a role:
        /// the project convention (<see cref="ContactCodes"/>, SupportSurfaceMath) is a
        /// prop surface id of <see cref="SupportSurfaceMath.FirstPropSurfaceId"/> + slot
        /// (100..111) and role <see cref="ContactCodes.RoleProp"/> or
        /// <see cref="ContactCodes.RoleBlocking"/>, which is what makes
        /// <see cref="GetContactRole"/> and <see cref="GetSupportKind"/> (kind 3) classify
        /// a prop contact as a prop. Static boxes stay 1..8, ramps 10..13, the ground 0 and
        /// boundary planes 1000 + i.
        /// </para>
        /// <para>
        /// Capacity is <see cref="MaxPropObbCount"/> (12). More than that is REFUSED and
        /// counted (<see cref="GetPropObbsOverflowCount"/>) with one console line; nothing
        /// is silently dropped, because a dropped prop means the ball rolls through a
        /// bench nobody can see. A refused call changes nothing: every entry is validated
        /// before the first one is written.
        /// </para>
        /// <para>
        /// The block is a function of where the ball is, so a teleport and
        /// <see cref="ClearObstacles"/> clear it (<see cref="ClearPropObbs"/>); the next
        /// host scan re-registers what still blocks.
        /// </para>
        /// </remarks>
        public bool SetPropObbs(Vector3[] centers, Vector3[] axisX, Vector3[] axisY,
            Vector3[] axisZ, Vector3[] halfExtents, int[] surfaceIds, int[] roles, int count)
        {
            if (!EnsureContactV2Buffers())
            {
                RefusePropObbs("contact v2 buffers could not be allocated");
                return false;
            }
            if (count < 0)
            {
                RefusePropObbs("negative count");
                return false;
            }
            if (count > MaxPropObbCount)
            {
                propObbOverflowCount = propObbOverflowCount + 1;
                RefusePropObbs("prop obb capacity 12 exceeded");
                LogPropObbOverflowOnce(count);
                return false;
            }
            if (count > 0)
            {
                if (centers == null || axisX == null || axisY == null || axisZ == null ||
                    halfExtents == null || surfaceIds == null || roles == null)
                {
                    RefusePropObbs("null prop obb buffer");
                    return false;
                }
                if (count > centers.Length || count > axisX.Length || count > axisY.Length ||
                    count > axisZ.Length || count > halfExtents.Length ||
                    count > surfaceIds.Length || count > roles.Length)
                {
                    RefusePropObbs("count exceeds a buffer length");
                    return false;
                }
            }

            // Validate the whole block first, so a refused call cannot leave half a set
            // behind (the same rule SetBlockingBoxes follows).
            for (int i = 0; i < count; i++)
            {
                if (surfaceIds[i] < 0)
                {
                    RefusePropObbs("negative surface id");
                    return false;
                }
                if (!ObbContactMath.IsValidObb(centers[i], axisX[i], axisY[i], axisZ[i],
                    halfExtents[i]))
                {
                    RefusePropObbs("not a finite, unit, orthogonal box");
                    return false;
                }
            }

            int baseIndex = staticObbCount;
            for (int i = 0; i < count; i++)
            {
                int slot = baseIndex + i;
                staticObbCenter[slot] = centers[i];
                staticObbAxisX[slot] = axisX[i];
                staticObbAxisY[slot] = axisY[i];
                staticObbAxisZ[slot] = axisZ[i];
                staticObbHalfExtents[slot] = halfExtents[i];
                staticObbSurfaceId[slot] = surfaceIds[i];
                staticObbRole[slot] = roles[i];
                staticObbBoundingRadius[slot] = ObbContactMath.BoundingRadius(halfExtents[i]);
            }
            // The OBB block and the proxy block are mutually exclusive views of the same
            // 12 slots. Switching back to OBBs must reset the proxy kind/count columns, or a
            // later proxy-mode window would read a stale convex shape.
            ResetPropProxySlots();
            propProxyCount = 0;
            propObbCount = count;
            // The union changed: the hysteresis must forget the previous selection.
            windowPreviousCount = 0;
            return true;
        }

        /// <summary>Empties the prop OBB block (the static table stays).</summary>
        public void ClearPropObbs()
        {
            if (propObbCount == 0) return;
            propObbCount = 0;
            windowPreviousCount = 0;
        }

        /// <summary>
        /// Registers this scan's 12 prop proxies (world-space, already baked). The block is a
        /// whole-block replace like <see cref="SetPropObbs"/>; a box entry is the exact OBB
        /// the old path would have registered, so a prop without a collider block keeps the
        /// same shape.
        /// </summary>
        /// <remarks>
        /// Every entry is validated before the first write, and a refused call changes
        /// nothing. The proxy block and the OBB block are mutually exclusive: this method
        /// sets <c>propObbCount</c> to 0, so the near window cannot see the same prop twice.
        /// </remarks>
        public bool SetPropProxies(
            int count,
            int[] kinds,
            Vector3[] centers,
            Vector3[] axisX,
            Vector3[] axisY,
            Vector3[] axisZ,
            Vector3[] halfExtents,
            float[] radii,
            float[] halfHeights,
            int[] axes,
            Vector3[] hullPoints,
            int[] hullStarts,
            int[] hullCounts,
            Vector3[] faceNormals,
            float[] faceOffsets,
            int[] faceA,
            int[] faceB,
            int[] faceC,
            int[] faceStarts,
            int[] faceCounts,
            int[] surfaceIds,
            int[] roles)
        {
            if (!EnsureContactV2Buffers())
            {
                RefusePropProxies("contact v2 buffers could not be allocated");
                return false;
            }
            if (count < 0)
            {
                RefusePropProxies("negative count");
                return false;
            }
            if (count > MaxPropObbCount)
            {
                propProxyOverflowCount = propProxyOverflowCount + 1;
                RefusePropProxies("prop proxy capacity 12 exceeded");
                return false;
            }
            if (count == 0)
            {
                ClearPropProxies();
                return true;
            }
            if (kinds == null || centers == null || axisX == null || axisY == null ||
                axisZ == null || halfExtents == null || radii == null ||
                halfHeights == null || axes == null || hullPoints == null ||
                hullStarts == null || hullCounts == null || faceNormals == null ||
                faceOffsets == null || faceA == null || faceB == null || faceC == null ||
                faceStarts == null || faceCounts == null || surfaceIds == null ||
                roles == null)
            {
                RefusePropProxies("null prop proxy buffer");
                return false;
            }
            if (count > kinds.Length || count > centers.Length || count > axisX.Length ||
                count > axisY.Length || count > axisZ.Length || count > halfExtents.Length ||
                count > radii.Length || count > halfHeights.Length || count > axes.Length ||
                count > hullStarts.Length || count > hullCounts.Length ||
                count > faceStarts.Length || count > faceCounts.Length ||
                count > surfaceIds.Length || count > roles.Length)
            {
                RefusePropProxies("count exceeds a buffer length");
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (surfaceIds[i] < 0)
                {
                    RefusePropProxies("negative surface id");
                    return false;
                }
                int kind = kinds[i];
                int hullCount = hullCounts[i];
                int faceCount = faceCounts[i];
                if (!ProxyContactMath.IsValidProxy(kind, centers[i], axisX[i], axisY[i],
                    axisZ[i], halfExtents[i], radii[i], halfHeights[i], axes[i], hullCount,
                    faceCount))
                {
                    RefusePropProxies("invalid proxy shape");
                    return false;
                }
                if (kind == ProxyContactMath.KindConvex)
                {
                    if (hullStarts[i] < 0 || hullCounts[i] < 4 ||
                        hullStarts[i] + hullCounts[i] > hullPoints.Length)
                    {
                        RefusePropProxies("convex hull points out of range");
                        return false;
                    }
                    if (faceStarts[i] < 0 || faceCounts[i] < 1 ||
                        faceStarts[i] + faceCounts[i] > faceNormals.Length ||
                        faceStarts[i] + faceCounts[i] > faceOffsets.Length ||
                        faceStarts[i] + faceCounts[i] > faceA.Length ||
                        faceStarts[i] + faceCounts[i] > faceB.Length ||
                        faceStarts[i] + faceCounts[i] > faceC.Length)
                    {
                        RefusePropProxies("convex face arrays out of range");
                        return false;
                    }
                    for (int face = 0; face < faceCount; face++)
                    {
                        int at = faceStarts[i] + face;
                        if (faceA[at] < 0 || faceB[at] < 0 || faceC[at] < 0 ||
                            faceA[at] >= hullCount || faceB[at] >= hullCount ||
                            faceC[at] >= hullCount)
                        {
                            RefusePropProxies("convex face index out of range");
                            return false;
                        }
                        if (!KatamariMath.IsFiniteVector(faceNormals[at]) ||
                            !KatamariMath.IsFiniteFloat(faceOffsets[at]))
                        {
                            RefusePropProxies("non-finite convex face plane");
                            return false;
                        }
                    }
                }
                else if (hullCount != 0 || faceCount != 0)
                {
                    RefusePropProxies("non-convex proxy carries hull/face counts");
                    return false;
                }
            }

            int baseIndex = staticObbCount;
            for (int i = 0; i < count; i++)
            {
                int slot = baseIndex + i;
                int kind = kinds[i];
                int hullStart = kind == ProxyContactMath.KindConvex ? i * ProxyContactMath.MaxHullPoints : 0;
                int faceStart = kind == ProxyContactMath.KindConvex ? i * ProxyContactMath.MaxHullFaces : 0;
                int hullCount = kind == ProxyContactMath.KindConvex ? hullCounts[i] : 0;
                int faceCount = kind == ProxyContactMath.KindConvex ? faceCounts[i] : 0;

                staticObbCenter[slot] = centers[i];
                staticObbAxisX[slot] = axisX[i];
                staticObbAxisY[slot] = axisY[i];
                staticObbAxisZ[slot] = axisZ[i];
                staticObbHalfExtents[slot] = halfExtents[i];
                staticObbSurfaceId[slot] = surfaceIds[i];
                staticObbRole[slot] = roles[i];
                staticObbBoundingRadius[slot] = ProxyContactMath.BoundingRadius(kind,
                    halfExtents[i], radii[i], halfHeights[i], hullPoints, hullStarts[i],
                    hullCount, centers[i]);

                staticProxyKind[slot] = kind;
                staticProxyRadius[slot] = radii[i];
                staticProxyHalfHeight[slot] = halfHeights[i];
                staticProxyAxis[slot] = axes[i];
                staticProxyHullStart[slot] = hullStart;
                staticProxyHullCount[slot] = hullCount;
                staticProxyFaceStart[slot] = faceStart;
                staticProxyFaceCount[slot] = faceCount;

                for (int point = 0; point < hullCount; point++)
                {
                    propProxyHullPoints[hullStart + point] =
                        hullPoints[hullStarts[i] + point];
                }
                for (int face = 0; face < faceCount; face++)
                {
                    int at = faceStarts[i] + face;
                    propProxyFaceNormals[faceStart + face] = faceNormals[at];
                    propProxyFaceOffsets[faceStart + face] = faceOffsets[at];
                    propProxyFaceA[faceStart + face] = faceA[at];
                    propProxyFaceB[faceStart + face] = faceB[at];
                    propProxyFaceC[faceStart + face] = faceC[at];
                }
            }

            propProxyCount = count;
            propObbCount = 0;
            windowPreviousCount = 0;
            return true;
        }

        /// <summary>Empties the prop proxy block (the static table stays).</summary>
        public void ClearPropProxies()
        {
            ResetPropProxySlots();
            if (propProxyCount == 0) return;
            propProxyCount = 0;
            windowPreviousCount = 0;
        }

        private void ResetPropProxySlots()
        {
            if (staticProxyKind == null) return;
            for (int i = 0; i < MaxPropObbCount; i++)
            {
                int slot = staticObbCount + i;
                if (slot < 0 || slot >= staticProxyKind.Length) continue;
                staticProxyKind[slot] = ProxyContactMath.KindBox;
                staticProxyRadius[slot] = 0f;
                staticProxyHalfHeight[slot] = 0f;
                staticProxyAxis[slot] = ProxyContactMath.AxisX;
                staticProxyHullStart[slot] = 0;
                staticProxyHullCount[slot] = 0;
                staticProxyFaceStart[slot] = 0;
                staticProxyFaceCount[slot] = 0;
            }
        }

        /// <summary>Prop proxies currently registered (0..12).</summary>
        public int GetPropProxiesCount()
        {
            return propProxyCount;
        }

        /// <summary>Prop proxy capacity (12).</summary>
        public int GetPropProxiesCapacity()
        {
            return MaxPropObbCount;
        }

        /// <summary>Proxy block overflow refusals (monotonic).</summary>
        public int GetPropProxyOverflowCount()
        {
            return propProxyOverflowCount;
        }

        /// <summary>The last proxy refusal as a literal, or "".</summary>
        public string GetPropProxyRefusalReason()
        {
            if (propProxyRefusalReason == null) return "";
            return propProxyRefusalReason;
        }

        private void RefusePropProxies(string reason)
        {
            propProxyRefusalCount = propProxyRefusalCount + 1;
            propProxyRefusalReason = reason;
            Debug.LogWarning("[ContactV2] prop proxy refused: " + reason);
        }

        /// <summary>True when the proxy prop block is the contact authority.</summary>
        public bool IsPropProxyContactEnabled()
        {
            return propProxyContactEnabled;
        }

        /// <summary>Runtime flag flip for a lab / EditMode test (serialized default false).</summary>
        public void SetPropProxyContactEnabled(bool enabled)
        {
            if (propProxyContactEnabled == enabled) return;
            propProxyContactEnabled = enabled;
            windowPreviousCount = 0;
        }

        private bool ProxyContactActive()
        {
            return propProxyContactEnabled && propProxyCount > 0;
        }

        /// <summary>Prop OBBs currently registered (0..12).</summary>
        public int GetPropObbsCount()
        {
            return propObbCount;
        }

        /// <summary>Prop OBB capacity (12).</summary>
        public int GetPropObbsCapacity()
        {
            return MaxPropObbCount;
        }

        /// <summary>Refused prop blocks caused by the capacity (monotonic).</summary>
        public int GetPropObbsOverflowCount()
        {
            return propObbOverflowCount;
        }

        /// <summary>Every refused <see cref="SetPropObbs"/> call (monotonic).</summary>
        public int GetPropObbsRefusalCount()
        {
            return propObbRefusalCount;
        }

        /// <summary>The last <see cref="SetPropObbs"/> refusal as a literal, or "".</summary>
        public string GetPropObbsRefusalReason()
        {
            if (propObbRefusalReason == null) return "";
            return propObbRefusalReason;
        }

        private void RefusePropObbs(string reason)
        {
            propObbRefusalCount = propObbRefusalCount + 1;
            propObbRefusalReason = reason;
        }

        /// <summary>One console line per capacity refusal (design §5.4 discipline).</summary>
        private void LogPropObbOverflowOnce(int count)
        {
            if (propObbOverflowCount > 1) return;
            Debug.Log("[ContactV2] prop obb overflow count=" + count + " capacity="
                + MaxPropObbCount);
        }

        /// <summary>
        /// Moves the prop block from one base index to another inside the shared table.
        /// </summary>
        /// <remarks>
        /// The layout invariant is "static prefix, then prop block, no gaps", which is what
        /// lets one <see cref="ContactWindowMath.TrySelectWindow"/> call see both kinds in
        /// one distance-sorted set. Only two callers move it: <see cref="AddObb"/> (up one
        /// slot, descending copy) and <see cref="ClearObbs"/> (down to zero, ascending).
        /// </remarks>
        private void MovePropBlock(int fromBase, int toBase)
        {
            if (propObbCount <= 0) return;
            if (fromBase == toBase) return;
            if (toBase > fromBase)
            {
                for (int i = propObbCount - 1; i >= 0; i--)
                {
                    CopyObbSlot(fromBase + i, toBase + i);
                }
                return;
            }
            for (int i = 0; i < propObbCount; i++)
            {
                CopyObbSlot(fromBase + i, toBase + i);
            }
        }

        private void CopyObbSlot(int from, int to)
        {
            if (from == to) return;
            staticObbCenter[to] = staticObbCenter[from];
            staticObbAxisX[to] = staticObbAxisX[from];
            staticObbAxisY[to] = staticObbAxisY[from];
            staticObbAxisZ[to] = staticObbAxisZ[from];
            staticObbHalfExtents[to] = staticObbHalfExtents[from];
            staticObbSurfaceId[to] = staticObbSurfaceId[from];
            staticObbRole[to] = staticObbRole[from];
            staticObbBoundingRadius[to] = staticObbBoundingRadius[from];
        }

        /// <summary>Static OBBs currently registered (0..256).</summary>
        public int GetStaticObbCount()
        {
            return staticObbCount;
        }

        /// <summary>Static OBB capacity (256).</summary>
        public int GetStaticObbCapacity()
        {
            return ContactTuning.MaxStaticObbCount;
        }

        /// <summary>Refused <see cref="AddObb"/> calls caused by the capacity.</summary>
        public int GetStaticObbOverflowCount()
        {
            return staticObbOverflowCount;
        }

        /// <summary>Every refused <see cref="AddObb"/> call (capacity or bad geometry).</summary>
        public int GetStaticObbRefusalCount()
        {
            return staticObbRefusalCount;
        }

        /// <summary>The last <see cref="AddObb"/> refusal as a literal, or "".</summary>
        public string GetLastStaticObbRefusalReason()
        {
            if (staticObbRefusalReason == null) return "";
            return staticObbRefusalReason;
        }

        private void RefuseStaticObb(string reason)
        {
            staticObbRefusalCount = staticObbRefusalCount + 1;
            staticObbRefusalReason = reason;
        }

        /// <summary>
        /// Registers a v1 static box as an axis-aligned OBB under the v1 support-table id
        /// (1 + index), so an existing scene's boxes exist in v2 too (design §1.4).
        /// </summary>
        private void RegisterBoxObb(int boxIndex, Vector3 minimum, Vector3 maximum)
        {
            if (staticObbCenter == null) return;
            Vector3 center = new Vector3((minimum.x + maximum.x) * 0.5f,
                (minimum.y + maximum.y) * 0.5f, (minimum.z + maximum.z) * 0.5f);
            Vector3 halfExtents = new Vector3((maximum.x - minimum.x) * 0.5f,
                (maximum.y - minimum.y) * 0.5f, (maximum.z - minimum.z) * 0.5f);
            AddObb(center, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f), halfExtents, 1 + boxIndex, ContactCodes.RoleBox);
        }

        /// <summary>
        /// Registers a v1 ramp as a solid slab OBB (design §1.5): the walking face is the
        /// slab's +local-Y face, the body hangs one board thickness below it, and the
        /// in-plane half length is the horizontal half run divided by n.y.
        /// </summary>
        /// <remarks>
        /// The mapping is exact where it matters: the top face is the same plane the arena
        /// baked (<see cref="SupportSurfaceMath.TryBakeRamp"/>'s walking centre and
        /// normal), the width is the same footprint, and the run is scaled so the slab's
        /// horizontal projection equals the baked horizontal half run. The stored right
        /// axis is only horizontal, not necessarily perpendicular to the normal, so it is
        /// projected into the plane and the third axis is their cross product: an OBB
        /// needs three orthonormal axes and a sheared one would be refused by AddObb.
        /// </remarks>
        private void RegisterRampSlab(int rampIndex, Vector3 center, Vector3 normal,
            Vector3 right, float halfWidth, float halfRun)
        {
            if (staticObbCenter == null) return;
            float normalY = normal.y;
            if (!KatamariMath.IsFiniteFloat(normalY) || normalY < 0.1f) return;
            Vector3 axisY = normal;
            float projection = right.x * axisY.x + right.y * axisY.y + right.z * axisY.z;
            Vector3 inPlane = new Vector3(right.x - axisY.x * projection,
                right.y - axisY.y * projection, right.z - axisY.z * projection);
            Vector3 axisX;
            float xLength;
            if (!KatamariMath.TryNormalize(inPlane, out axisX, out xLength)) return;
            Vector3 axisZ;
            float zLength;
            if (!KatamariMath.TryNormalize(Vector3.Cross(axisY, axisX), out axisZ, out zLength))
                return;
            float thickness = rampSlabThicknessM;
            if (!KatamariMath.IsFiniteFloat(thickness) || thickness <= 0f) thickness = 0.04f;
            float halfThickness = thickness * 0.5f;
            float halfLength = halfRun / normalY;
            if (!KatamariMath.IsFiniteFloat(halfLength) || halfLength <= 0f) halfLength = halfRun;
            if (!KatamariMath.IsFiniteFloat(halfWidth) || halfWidth <= 0f) return;
            Vector3 slabCenter = new Vector3(center.x - axisY.x * halfThickness,
                center.y - axisY.y * halfThickness, center.z - axisY.z * halfThickness);
            AddObb(slabCenter, axisX, axisY, axisZ,
                new Vector3(halfWidth, halfThickness, halfLength),
                SupportSurfaceMath.FirstRampSurfaceId + rampIndex, ContactCodes.RoleSlab);
        }

        // ------------------------------------------------------------------
        // Contact-model diagnostics (design §6.4). Every index-taking getter returns a
        // neutral value outside the valid range instead of throwing: Udon has no
        // try/catch and a test-field probe must not stop the world.
        // ------------------------------------------------------------------

        /// <summary>Contacts recorded by the last v2 step (core first, then needle).</summary>
        public int GetContactCount()
        {
            if (!contactModelV2Enabled) return 0;
            return v2ContactCount;
        }

        /// <summary>Unit normal of contact <paramref name="index"/>, or zero.</summary>
        public Vector3 GetContactNormal(int index)
        {
            if (index < 0 || index >= v2ContactCount) return Vector3.zero;
            return contactNormalsV2[index];
        }

        /// <summary>Closest point on the solid for contact <paramref name="index"/>.</summary>
        public Vector3 GetContactPoint(int index)
        {
            if (index < 0 || index >= v2ContactCount) return Vector3.zero;
            return contactPointsV2[index];
        }

        /// <summary>Penetration depth of contact <paramref name="index"/> (may be negative).</summary>
        public float GetContactDepth(int index)
        {
            if (index < 0 || index >= v2ContactCount) return 0f;
            return contactDepthsV2[index];
        }

        /// <summary>
        /// Class of contact <paramref name="index"/>:
        /// <see cref="ContactCodes.ClassNone"/> / <see cref="ContactCodes.ClassRide"/> /
        /// <see cref="ContactCodes.ClassClimb"/>.
        /// </summary>
        public int GetContactClass(int index)
        {
            if (index < 0 || index >= v2ContactCount) return ContactCodes.ClassNone;
            return contactClassesV2[index];
        }

        /// <summary>
        /// Role of contact <paramref name="index"/> (<see cref="ContactCodes.RoleBox"/>,
        /// <see cref="ContactCodes.RoleSlab"/>, <see cref="ContactCodes.RoleProp"/>,
        /// <see cref="ContactCodes.RoleBlocking"/>), or
        /// <see cref="ContactCodes.RoleBox"/> outside the valid range.
        /// </summary>
        public int GetContactRole(int index)
        {
            if (index < 0 || index >= v2ContactCount) return ContactCodes.RoleBox;
            return contactRolesV2[index];
        }

        /// <summary>
        /// Surface id of contact <paramref name="index"/>, or -1. The id carries the kind,
        /// which is how a reader tells a prop contact from a static one (there is no
        /// separate prop branch in the solver): 0 ground, 1..8 static box, 10..13 ramp,
        /// 100..111 prop, 1000 + i boundary plane. The role of the same record
        /// (<see cref="GetContactRole"/>) is the second, table-independent label.
        /// </summary>
        public int GetContactSurfaceId(int index)
        {
            if (index < 0 || index >= v2ContactCount) return -1;
            return contactSurfaceIdsV2[index];
        }

        /// <summary>Shape of contact <paramref name="index"/>: 0 core, 1 + i pin.</summary>
        public int GetContactShape(int index)
        {
            if (index < 0 || index >= v2ContactCount) return 0;
            return contactShapesV2[index];
        }

        /// <summary>How many of the reported contacts came from a pin tip.</summary>
        public int GetNeedleContactCount()
        {
            return v2NeedleContactCount;
        }

        /// <summary>Class of the core solver's dominant contact (design §2.5).</summary>
        public int GetDominantContactClass()
        {
            return v2DominantClass;
        }

        /// <summary>Surface id of the core solver's dominant contact, or -1.</summary>
        public int GetDominantContactSurfaceId()
        {
            return v2DominantSurfaceId;
        }

        /// <summary>Largest penetration the last v2 step's push-out left behind, metres.</summary>
        public float GetSolverResidualMax()
        {
            return contactResidualMax;
        }

        /// <summary>
        /// True once a window overflow could have hidden a touching solid (design §5.4).
        /// Latched: a test field treats the cell as invalid, not as a pass.
        /// </summary>
        public bool WasContactIncomplete()
        {
            return contactIncomplete;
        }

        /// <summary>Solids in the last step's near set (0..24).</summary>
        public int GetNearObbCount()
        {
            return nearObbCount;
        }

        /// <summary>Candidates the last window had to drop (0 when it fit).</summary>
        public int GetNearObbDropped()
        {
            return windowDroppedCount;
        }

        /// <summary>Candidates dropped by every window so far (monotonic).</summary>
        public int GetNearObbDroppedTotal()
        {
            return windowDroppedTotal;
        }

        /// <summary>Near-set copies so far (the hysteresis exists to keep this low).</summary>
        public int GetNearRebuildCount()
        {
            return nearRebuildCount;
        }

        /// <summary>Windows that could have hidden a touching solid (monotonic).</summary>
        public int GetWindowOverflowCount()
        {
            return windowOverflowCount;
        }

        /// <summary>Windows that dropped only solids that were out of reach (monotonic).</summary>
        public int GetWindowSaturatedCount()
        {
            return windowSaturatedCount;
        }

        /// <summary>
        /// Climb state of design §6.4: 0 none, 1 climbing, 2 sliding back (cooldown after
        /// leaving the channel), 3 locked after reaching the cap.
        /// </summary>
        public int GetClimbState()
        {
            if (climbLocked) return 3;
            if (climbActive) return 1;
            if (climbCooldown > 0f) return 2;
            return 0;
        }

        /// <summary>True while the climb channel owns the velocity.</summary>
        public bool IsClimbing()
        {
            return climbActive;
        }

        /// <summary>Height climbed above the anchor, metres (0 when not climbing).</summary>
        public float GetClimbHeightWorldM()
        {
            return climbHeightWorldM;
        }

        /// <summary>Vertical climb rate of the last step, m/s (0 when not climbing).</summary>
        public float GetClimbSpeedMps()
        {
            return climbSpeedWorldMps;
        }

        /// <summary>True when the last climb reached the cap and only a release clears it.</summary>
        public bool IsClimbLocked()
        {
            return climbLocked;
        }

        /// <summary>Substeps the last v2 step used (1..8, design §1.7).</summary>
        public int GetSubstepCount()
        {
            return v2SubstepCount;
        }

        /// <summary>Recoveries inside the current 10 s window (design §2.8).</summary>
        public int GetRecoveryCount()
        {
            return recoveryCount;
        }

        /// <summary>
        /// Recovery state: 0 none, 1 soft (velocity cleared), 2 hard (moved to the last
        /// stable point), 3 hold (three recoveries inside 10 s: stopped and reported).
        /// </summary>
        public int GetRecoveryState()
        {
            return recoveryState;
        }

        /// <summary>Why the last recovery happened: 1 soft, 2 hard, 3 deep embed, 0 never.</summary>
        public int GetLastRecoveryReason()
        {
            return lastRecoveryReason;
        }

        /// <summary>
        /// Wall-clock cost of the last v2 step, microseconds. COARSE: Udon exposes no
        /// high-resolution clock (no Stopwatch), and Time.time resolves per frame, so a
        /// frame that runs several fixed steps reports 0 for all but the first and the
        /// reading is a whole-frame upper bound rather than a per-step measurement. Use
        /// it to prove the solver's order of magnitude, not to compare nanoseconds.
        /// </summary>
        public float GetLastStepMicros()
        {
            return lastStepMicros;
        }

        /// <summary>theta of the support (or climbing) contact in degrees, 0 when none.</summary>
        public float GetSupportSlopeDegrees()
        {
            if (supportSurfaceId == SupportSurfaceMath.NoSurfaceId) return 0f;
            Vector3 normal = supportSurfaceNormalWorld;
            return ObbContactMath.ContactAngleDeg(normal);
        }

        /// <summary>Physical clamp on a pin's protrusion, min(0.35 r, 0.30), metres.</summary>
        public float GetNeedleProtrusionCapWorldM()
        {
            return BallShapeMath.NeedleProtrusionCap(collisionRadiusWorldM);
        }

        /// <summary>Largest protrusion among the compact pins this step, metres.</summary>
        public float GetNeedleProtrusionMaxWorldM()
        {
            float radius = collisionRadiusWorldM;
            float best = 0f;
            for (int k = 0; k < needleCompactCount; k++)
            {
                int index = contactNeedleIndices[k];
                float protrusion = contactNeedleSupports[index] - radius;
                if (protrusion > best) best = protrusion;
            }
            return best;
        }

        /// <summary>r_phys: the contact radius (design §4A.5, the v1 collision radius).</summary>
        public float GetPhysicalRadiusWorldM()
        {
            return collisionRadiusWorldM;
        }

        /// <summary>r_visual = 0.95 r_phys (design §4A.2).</summary>
        public float GetVisualCoreRadiusWorldM()
        {
            float visual;
            if (!BallShapeMath.TryVisualCoreRadius(collisionRadiusWorldM, out visual))
                return collisionRadiusWorldM;
            return visual;
        }

        /// <summary>How far the rolling shape reaches along the heading, metres.</summary>
        public float GetRollReachWorldM()
        {
            Vector3 direction = HeadingDirection();
            float reach;
            if (!BallShapeMath.TryRollingReach(direction, collisionRadiusWorldM,
                proxyDirections, contactNeedleSupports, RadialProxyMath.DirectionCount,
                out reach)) return collisionRadiusWorldM;
            return reach;
        }

        /// <summary>
        /// How far the pickup trigger reaches along the heading, metres. This is a TRIGGER
        /// reach only: it never blocks, pushes or supports (design §4A.4).
        /// </summary>
        public float GetPickupReachWorldM()
        {
            Vector3 direction = HeadingDirection();
            float reach;
            if (!BallShapeMath.TryPickupReach(direction, collisionRadiusWorldM,
                proxyDirections, contactNeedleSupports, RadialProxyMath.DirectionCount,
                out reach)) return collisionRadiusWorldM;
            return reach;
        }

        /// <summary>True when the last growth push-out could not be applied (design §4.2).</summary>
        public bool IsSizeApplyDeferred()
        {
            return sizeApplyDeferred;
        }

        /// <summary>Growth push-outs that had to be deferred so far (monotonic).</summary>
        public int GetSizeApplyDeferredCount()
        {
            return sizeApplyDeferredCount;
        }

        /// <summary>Steps the contact solver refused (invalid input), monotonic.</summary>
        public int GetContactSolveFailureCount()
        {
            return contactSolveFailureCount;
        }

        /// <summary>Steps whose position was rejected for leaving more than 2 cm of penetration.</summary>
        public int GetContactRejectedStepCount()
        {
            return contactRejectedStepCount;
        }

        /// <summary>The radius the last v2 solve used (lags the size only after a deferral).</summary>
        public float GetContactRadiusUsedWorldM()
        {
            return contactRadiusUsedWorldM;
        }

        /// <summary>The current drive direction the climb gate read (unit, planar).</summary>
        public Vector3 GetContactDriveDirection()
        {
            return contactDriveDirection;
        }

        private Vector3 HeadingDirection()
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        }

        /// <summary>
        /// Records one refused fixed-capacity registration in the latched
        /// overflow signal.
        /// </summary>
        /// <remarks>
        /// UdonSharp-safe by construction: an int counter, a bool latch and one
        /// string field - no collection, no allocation on the refusal path, no
        /// Debug.Log. The reason string is written once, so a caller that
        /// submits candidates every step cannot grow it or spam the console;
        /// diagnostics poll the getters instead.
        /// </remarks>
        private void SignalOverflow(string reason)
        {
            overflowCount = overflowCount + 1;
            if (overflowLatched) return;
            overflowLatched = true;
            firstOverflowReason = reason;
        }

        /// <summary>
        /// Resizes the arrays the sweep reads so their lengths match the counts.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The sweep treats the array length as the obstacle set (a padded slot would be a
        /// ghost box at the origin), so the arrays must hold exactly the registered
        /// entries. The arrays are reused when the total box count is unchanged and only
        /// reallocated when it moves: static registration is a scene-load event and the
        /// blocking slice changes at the host's scan cadence, so "same length, overwrite in
        /// place" is what keeps a 20 Hz scan from allocating a pair of arrays per tick.
        /// </para>
        /// <para>
        /// Fixed capacities are MaxPlaneCount = 8, MaxBoxCount = 8,
        /// MaxBlockingBoxCount = 6, PendingCandidateCapacity = 4 and ContactCapacity = 4.
        /// An entry beyond a capacity is DROPPED, never queued, resized or degraded:
        /// overflowing the obstacle set changes what the ball can collide with,
        /// and overflowing the candidate queue silently loses an absorbable
        /// item, so an overflow is a data error (a mis-wired scene, or a host
        /// batching more than four candidates per step), not a gameplay choice.
        /// AddPlane, AddBox and SubmitCandidate latch overflowCount and the
        /// first reason string; a blocking set above its own capacity is counted
        /// separately (see the blocking-refusal fields) because it is a per-scan answer
        /// rather than a wiring error. The sweep's contact buffer is capped inside
        /// SweepMath, which reports no truncation, so it only surfaces as
        /// contactBufferSaturated. The signals are one-shot and read at a
        /// checkpoint: nothing is logged per frame, and nothing here changes
        /// gameplay.
        /// </para>
        /// </remarks>
        private void RebuildObstacles()
        {
            planeNormals = new Vector3[planeCount];
            planeOffsets = new float[planeCount];
            for (int i = 0; i < planeCount; i++)
            {
                planeNormals[i] = planeNormalBuffer[i];
                planeOffsets[i] = planeOffsetBuffer[i];
            }

            // Step boxes are excluded: a box the ball may climb must be enterable, and
            // the sweep only reports where a sphere stops. The exclusion is OFF when
            // terrain support is disabled, in which case this loop copies every static
            // box in registration order, exactly as before P1.
            int staticBoxSweepCount = 0;
            for (int i = 0; i < boxCount; i++)
            {
                if (terrainSupportEnabled && boxIsSteppable != null && boxIsSteppable[i]) continue;
                staticBoxSweepCount = staticBoxSweepCount + 1;
            }
            int totalBoxCount = staticBoxSweepCount + blockingBoxCount;
            if (boxMinima == null || boxMaxima == null ||
                boxMinima.Length != totalBoxCount || boxMaxima.Length != totalBoxCount)
            {
                boxMinima = new Vector3[totalBoxCount];
                boxMaxima = new Vector3[totalBoxCount];
            }
            int writeIndex = 0;
            for (int i = 0; i < boxCount; i++)
            {
                if (terrainSupportEnabled && boxIsSteppable != null && boxIsSteppable[i]) continue;
                boxMinima[writeIndex] = boxMinimumBuffer[i];
                boxMaxima[writeIndex] = boxMaximumBuffer[i];
                writeIndex = writeIndex + 1;
            }
            for (int i = 0; i < blockingBoxCount; i++)
            {
                boxMinima[writeIndex] = blockingMinimumBuffer[i];
                boxMaxima[writeIndex] = blockingMaximumBuffer[i];
                writeIndex = writeIndex + 1;
            }
        }

        /// <summary>
        /// The conversion a bank trigger calls: the confirmed unbanked amount goes
        /// through the ledger, then the ball shrinks by the residual rule.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Refused when the ball is below the minimum conversion diameter
        /// (Docs/OPEN_DECISIONS.md (b)), when nothing unbanked is confirmed, or
        /// when the shrink cannot be planned. The ledger decides the rest: only
        /// a Playing round from an Active player may bank, the amount may not
        /// exceed the confirmed total, and a repeated (player, requestId) pair
        /// is refused, so a retried trigger cannot double count. Nothing is
        /// mirrored into the motor's fields unless the transaction succeeded.
        /// </para>
        /// <para>
        /// The shrink is the diameter-space rule of Docs/OPEN_DECISIONS.md (a):
        /// post = max(start, start + (pre - start) * residual) with residual
        /// clamped to [0, 1]. start is the forward size map's own diameter at
        /// zero confirmed volume (derived by TryDerive, not a hardcoded 0.30),
        /// pre is this ball's current semantic diameter, and post is priced back
        /// into ledger units through the size model's inverse before the ledger
        /// call. A failed plan leaves the ledger untouched and the previous size
        /// in place, so a conversion can never bank points without also applying
        /// its shrink; after the transaction only the carried residual is
        /// written and the confirmed total is recomputed once.
        /// </para>
        /// </remarks>
        public bool TryBank(float now, string requestId)
        {
            int ignoredReason;
            return TryBankWithReason(now, requestId, out ignoredReason);
        }

        /// <summary>
        /// The single conversion (bank) implementation, reporting why it refused.
        /// </summary>
        /// <remarks>
        /// <para>
        /// reason = 0 means the conversion succeeded; 1..8 name the refusal. The table
        /// (number, name, trigger) is documented in Docs/Design/BallMotor/README.md so a
        /// reader does not have to decode numbers from this file.
        /// </para>
        /// <para>
        /// There is exactly ONE implementation: TryBank forwards here. A second copy of
        /// this decision would be the "two copies of one fact" failure this project has
        /// already paid for twice (the pickup gain rule, the host queue latch).
        /// </para>
        /// </remarks>
        public bool TryBankWithReason(float now, string requestId, out int reason)
        {
            reason = 0;
            if (!hasLocalRow) { reason = 1; return false; }
            if (requestId == null || requestId.Length == 0) { reason = 2; return false; }
            if (!KatamariMath.IsFiniteFloat(now)) { reason = 3; return false; }
            if (!KatamariMath.IsFiniteFloat(semanticDiameterWorldM)) { reason = 3; return false; }
            if (semanticDiameterWorldM < minConversionDiameterWorldM) { reason = 4; return false; }
            if (unbankedVolumeUnits <= 0) { reason = 5; return false; }

            // The zero-volume mapping is the start of the rule, so the start is
            // whatever the same forward map reports at zero confirmed units.
            float startCoreRadius;
            float startCollisionRadius;
            float startDiameter;
            float startEffectiveVolumeUnits;
            if (!SizeModelMath.TryDerive(0f, baselineVolumeUnits, cubicMetersPerVolumeUnit,
                maxCoreRadiusWorldM, collisionToCoreRadiusRatio,
                semanticDiameterPerCoreDiameter, out startCoreRadius,
                out startCollisionRadius, out startDiameter, out startEffectiveVolumeUnits))
            {
                reason = 6;
                return false;
            }

            float postBankDiameter;
            if (!BankShrinkMath.TryPostBankDiameter(startDiameter, semanticDiameterWorldM,
                bankResidualFraction, out postBankDiameter))
            {
                reason = 6;
                return false;
            }

            float postBankVolumeUnits;
            if (!BankShrinkMath.TryBankVolumeUnits(postBankDiameter, baselineVolumeUnits,
                cubicMetersPerVolumeUnit, maxCoreRadiusWorldM,
                semanticDiameterPerCoreDiameter, out postBankVolumeUnits))
            {
                reason = 6;
                return false;
            }

            if (!GrowthLedgerMath.TryBank(roundState, playerIds, confirmedVolumes,
                bankedScores, lifeStates, respawnAtSet, respawnAt, playerGenerations,
                playerCount, bankRequestPlayerIds, bankRequestIds, ref bankRequestCount,
                localPlayerId, unbankedVolumeUnits, requestId))
            {
                reason = 7;
                return false;
            }

            long confirmed = confirmedVolumes[localRow];
            long banked = bankedScores[localRow];
            if (confirmed < 0 || banked < 0) { reason = 7; return false; }
            unbankedVolumeUnits = confirmed;
            bankedScoreUnits = banked;
            if (!ApplyBankShrink(postBankVolumeUnits)) { reason = 8; return false; }
            return true;
        }

        /// <summary>
        /// Applies the already-planned conversion shrink and recomputes the
        /// confirmed total once.
        /// </summary>
        /// <remarks>
        /// <para>
        /// postBankVolumeUnits is the ledger value the diameter-space rule
        /// priced in <see cref="BankShrinkMath"/> before the ledger call. The
        /// ledger has already moved the unbanked amount into the score, so the
        /// carried residual is that value minus whatever is still unbanked
        /// (normally zero), clamped to zero: already scored, never bankable
        /// again.
        /// </para>
        /// <para>
        /// Returns false, leaving the previous size in place, when the carried
        /// subtraction is not finite. The ledger transaction is not undone by a
        /// false here - its (player, requestId) pair is one-shot, so a retry
        /// cannot double count.
        /// </para>
        /// </remarks>
        private bool ApplyBankShrink(float postBankVolumeUnits)
        {
            float carried;
            if (!BankShrinkMath.TryCarriedVolumeUnits(postBankVolumeUnits,
                (float)unbankedVolumeUnits, out carried))
            {
                return false;
            }
            carriedVolumeUnits = carried;
            RecomputeConfirmedAndSize();
            // A conversion can shrink the core a long way. The endpoint pool is unchanged,
            // but the contract asks for a whole-ledger rebuild after a large shrink, so the
            // shell is re-derived from the surviving items instead of being scaled.
            RebuildRadialProxies();
            return true;
        }

        /// <summary>
        /// Moves the ball without rolling it. The discontinuity is consumed by the
        /// next step's visual roll and follow-rig calls.
        /// </summary>
        /// <remarks>
        /// The dynamic blocking set is dropped: it was chosen for where the ball used to
        /// be, and a box the ball has just been teleported into would be resolved by the
        /// sweep's initial-penetration branch as a push. The next host scan re-registers
        /// whatever blocks at the new position, within one scan interval. Static planes and
        /// boxes stay: the map did not move.
        /// </remarks>
        public void TeleportTo(Vector3 center)
        {
            if (!KatamariMath.IsFiniteVector(center)) return;
            ballCenterWorld = center;
            planarVelocity = Vector3.zero;
            stuckSeconds = 0f;
            discontinuous = true;
            // Ball contact v2: the stable point, the climb channel, the vertical velocity
            // and the needle sweep's previous frame are all about where the ball used to
            // be. The round start IS redefined here: a teleport is a new start
            // (design §2.8).
            contactVerticalVelocity = 0f;
            ClearClimbState();
            hasContactStableCenter = false;
            contactHasPreviousFrame = false;
            contactPreviousCenterWorld = center;
            roundStartCenter = center;
            hasRoundStartCenter = true;
            sizeApplyDeferred = false;
            contactRadiusUsedWorldM = collisionRadiusWorldM;
            // The prop block was selected for the old position, exactly like the blocking
            // box set the v1 path clears here; the next host scan re-registers it.
            ClearPropObbs();
            // A teleport can cross the whole map: the old support surface, the old fall
            // state and the old step mask are all about where the ball used to be. The
            // next step re-derives them from the new position and the ball's own bottom,
            // so a teleport into the air becomes a real fall (not a 0.1 m/step crawl).
            hasSupportFrame = false;
            lastSupportHeightWorldM = center.y - collisionRadiusWorldM;
            isFalling = false;
            fallSpeedMps = 0f;
            ClearStepMask();
            ClearBlockingBoxes();
            // The support set was chosen for the old position too; a stale prop top cannot
            // support a ball whose XZ is elsewhere, but clearing keeps the "set is a
            // function of where the ball is" rule true for both prop pools. The next host
            // scan re-registers within one scan interval.
            ClearSupportProps();
            // The endpoints are ball-local, so a teleport cannot change them; the rebuild
            // is the contract's discontinuity path and proves the support table still
            // matches the ledger instead of carrying a value across the jump.
            RebuildRadialProxies();
        }

        // ------------------------------------------------------------------
        // Radial proxy seam: the pickup host registers an absorbed prop's
        // simplified AABB after it attaches the visual prop. The motor owns the
        // shell; the host owns no proxy state.
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers one absorbed prop's simplified AABB as a radial proxy item.
        /// </summary>
        /// <returns>
        /// True when the item is represented (or was already registered); false when it
        /// was skipped. A skip is normal for a prop that is not a long object or that
        /// does not clear the core; capacity and invalid-input skips are refusals and
        /// are counted/logged. The reason of the most recent skip is
        /// <see cref="GetLastProxySkipReason"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// bounds is the prop's full AABB edge lengths in metres (the same
        /// <c>propBounds</c> the absorb gate and the blocking boxes use), read as the
        /// prop's local axes. ballLocalCenter and ballLocalRotation are the pose
        /// <c>AttachmentMath.TryWorldPoseToLocal</c> just produced for the visual
        /// attach, so the proxy and the visible prop occupy the same ball-frame box.
        /// Endpoints are the eight corners, and the item becomes one of the at-most-four
        /// long-object candidates.
        /// </para>
        /// <para>
        /// Idempotent by item id: one call per attached prop, and a retried call cannot
        /// add the same corners twice. Registered items are removed by a field reset,
        /// by <see cref="DetachRadialProxy"/>, or by <see cref="TryResetForNewRound"/>.
        /// </para>
        /// </remarks>
        public bool AttachRadialProxy(string itemInstanceId, Vector3 bounds,
            Vector3 ballLocalCenter, Quaternion ballLocalRotation)
        {
            if (proxyEndpointPool == null)
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }
            if (itemInstanceId == null || itemInstanceId.Length == 0)
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }
            for (int i = 0; i < proxyItemCount; i++)
            {
                if (string.CompareOrdinal(proxyItemKeys[i], itemInstanceId) == 0) return true;
            }
            if (!KatamariMath.IsFiniteVector(bounds) || bounds.x < 0f || bounds.y < 0f ||
                bounds.z < 0f)
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }
            if (!KatamariMath.IsFiniteVector(ballLocalCenter) ||
                !RadialProxyMath.IsUnitRotation(ballLocalRotation))
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }
            if (!KatamariMath.IsFiniteFloat(coreRadiusWorldM) || coreRadiusWorldM < 0f)
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }

            bool isLongObject;
            if (!RadialProxyShellMath.TryClassifyLongObject(bounds, coreRadiusWorldM,
                longObjectMinElongation, longObjectMinLongestEdgePerCoreDiameter,
                out isLongObject))
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }
            if (!isLongObject)
            {
                RecordProxySkip(ProxySkipNotLongObject);
                return false;
            }

            float farthestDistance;
            if (!RadialProxyShellMath.TryBuildAabbCorners(ballLocalCenter, ballLocalRotation,
                bounds, proxyCorners, out farthestDistance))
            {
                RecordProxySkip(ProxySkipInvalidInput);
                return false;
            }
            float minProtrusion = minProxyProtrusionM;
            if (!KatamariMath.IsFiniteFloat(minProtrusion) || minProtrusion < 0f) minProtrusion = 0f;
            if (farthestDistance <= coreRadiusWorldM + minProtrusion)
            {
                RecordProxySkip(ProxySkipInsideCore);
                return false;
            }
            if (proxyItemCount >= ProxyItemCapacity ||
                proxyCandidateCount >= proxyCandidateKeys.Length)
            {
                RecordProxySkip(ProxySkipCandidateFull);
                return false;
            }
            if (proxyEndpointCount > ProxyEndpointCapacity - RadialProxyShellMath.AabbCornerCount)
            {
                RecordProxySkip(ProxySkipPoolFull);
                return false;
            }
            if (!RadialProxyShellMath.TryAttachItem(proxyDirections, proxySupports,
                proxyEndpointPool, ref proxyEndpointCount, proxyItemStarts, proxyItemLengths,
                proxyItemKeys, proxyItemIsCandidate, ref proxyItemCount, proxyCandidateKeys,
                ref proxyCandidateCount, proxyAffectedIndices, proxySolveScratch, itemInstanceId,
                proxyCorners, RadialProxyShellMath.AabbCornerCount, true))
            {
                RecordProxySkip(ProxySkipShellRefused);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Removes one item's proxy contribution and recomputes the directions it used
        /// to affect. Returns false when the id was not registered; the shell is then
        /// unchanged. There is no drop path in this revision, so the only runtime
        /// removals are a whole field reset and <see cref="TryResetForNewRound"/>.
        /// </summary>
        public bool DetachRadialProxy(string itemInstanceId)
        {
            if (proxyEndpointPool == null) return false;
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            bool existed;
            if (!RadialProxyShellMath.TryDetachItem(proxyDirections, proxySupports,
                proxyEndpointPool, ref proxyEndpointCount, proxyItemStarts, proxyItemLengths,
                proxyItemKeys, proxyItemIsCandidate, ref proxyItemCount, proxyCandidateKeys,
                ref proxyCandidateCount, proxyAffectedIndices, proxySolveScratch,
                proxyRemovedCorners, itemInstanceId, out existed))
            {
                RecordProxySkip(ProxySkipShellRefused);
                return false;
            }
            return existed;
        }

        /// <summary>
        /// Empties the shell: every support returns to zero and every item row and
        /// candidate slot is released. Used by a round retry and by a pickup field
        /// reset, which returns every prop to its authored pose.
        /// </summary>
        public void ClearRadialProxies()
        {
            if (proxyDirections == null) return;
            if (!RadialProxyShellMath.TryClearShell(proxyDirections, proxySupports,
                ref proxyEndpointCount, ref proxyItemCount, proxyCandidateKeys,
                ref proxyCandidateCount))
            {
                RecordProxySkip(ProxySkipShellRefused);
                return;
            }
            proxyGroundSupportHeightM = 0f;
        }

        /// <summary>
        /// Re-derives the whole support table and candidate list from the surviving
        /// items. False leaves the previous shell in place (the rebuild is atomic).
        /// Called after a bank shrink and on a teleport.
        /// </summary>
        public bool RebuildRadialProxies()
        {
            if (proxyDirections == null) return false;
            int candidateCount;
            if (!RadialProxyShellMath.TryRebuildShell(proxyDirections, proxySupports,
                proxyEndpointPool, proxyEndpointCount, proxyItemStarts, proxyItemLengths,
                proxyItemKeys, proxyItemIsCandidate, proxyItemCount, proxyCandidateKeys,
                out candidateCount))
            {
                RecordProxySkip(ProxySkipShellRefused);
                return false;
            }
            proxyCandidateCount = candidateCount;
            return true;
        }

        private void RecordProxySkip(int reason)
        {
            proxySkipCount = proxySkipCount + 1;
            proxySkipReason = reason;
            if (reason == ProxySkipNotLongObject || reason == ProxySkipInsideCore) return;
            proxyRefusalCount = proxyRefusalCount + 1;
            if (proxyRefusalLogCount >= 3) return;
            proxyRefusalLogCount = proxyRefusalLogCount + 1;
            Debug.LogWarning("[BallMotor] radial proxy refused: " + ProxySkipText(reason)
                + " (refusals " + proxyRefusalCount + ")");
        }

        private string ProxySkipText(int reason)
        {
            if (reason == ProxySkipNotLongObject) return "not a long object";
            if (reason == ProxySkipInsideCore) return "no meaningful protrusion";
            if (reason == ProxySkipCandidateFull) return "candidate slots full (4)";
            if (reason == ProxySkipPoolFull) return "endpoint pool full (32)";
            if (reason == ProxySkipInvalidInput) return "invalid item input";
            if (reason == ProxySkipShellRefused) return "shell math refused the ledger";
            return "none";
        }

        /// <summary>Endpoints currently in the shell pool (0..32).</summary>
        public int GetProxyPointCount()
        {
            return proxyEndpointCount;
        }

        /// <summary>Long-object items currently registered (0..4).</summary>
        public int GetProxyCandidateCount()
        {
            return proxyCandidateCount;
        }

        /// <summary>Fixed direction count of the shell (16).</summary>
        public int GetProxyDirectionCount()
        {
            return RadialProxyMath.DirectionCount;
        }

        /// <summary>
        /// Current support length along one direction, metres, floored by the core
        /// radius. This is the RAW endpoint maximum (uncapped); the per-step contact
        /// clamp to core+maxProtrusion is applied when the ground points are built, so
        /// a value here can exceed what the ball actually lifted by. -1 for an
        /// out-of-range index or before Start.
        /// </summary>
        public float GetProxySupportLength(int directionIndex)
        {
            if (proxySupports == null || directionIndex < 0 ||
                directionIndex >= RadialProxyMath.DirectionCount) return -1f;
            float support = proxySupports[directionIndex];
            if (support < coreRadiusWorldM) support = coreRadiusWorldM;
            return support;
        }

        /// <summary>
        /// The support height the last accepted ground solve used, metres. This is the
        /// target height (including the proxy bump and its protrusion cap), not the
        /// clamped centre move; 0 before the first accepted solve.
        /// </summary>
        public float GetGroundSupportHeightWorldM()
        {
            return proxyGroundSupportHeightM;
        }

        /// <summary>
        /// Turns the P1 terrain-support feature on or off at runtime.
        /// </summary>
        /// <remarks>
        /// Off is the pre-P1 frame: the sweep keeps every static box, the ground solve
        /// gets the raw ground plane, no fall state exists and VisualRoll gets the raw
        /// ground normal. The switch exists so a lab whose geometry predates the step
        /// rule can keep its old behaviour bit for bit (design §8.1 / S7).
        /// </remarks>
        public void SetTerrainSupportEnabled(bool enabled)
        {
            if (terrainSupportEnabled == enabled) return;
            terrainSupportEnabled = enabled;
            if (!enabled)
            {
                ClearStepMask();
                RebuildObstacles();
                isFalling = false;
                fallSpeedMps = 0f;
                // Re-resolve with an empty prop table so a diagnostic read never keeps
                // reporting a prop surface the feature no longer honours. With buffers not
                // allocated yet (before Start) the next step resolves it anyway.
                if (boxMinimumBuffer != null) ResolveTerrainSupport(ballCenterWorld);
            }
            else
            {
                // Re-evaluate every box against the current size and support height.
                stepMaskValid = false;
            }
        }

        /// <summary>True while the P1 support/step/fall path is active.</summary>
        public bool IsTerrainSupportEnabled()
        {
            return terrainSupportEnabled;
        }

        /// <summary>
        /// Height of the selected support surface under the ball centre, world Y, metres.
        /// </summary>
        /// <remarks>
        /// This is the SURFACE height, not "centre.y minus the support length": reporting
        /// the latter would be lifted by the radial-proxy shell, so a host comparing a
        /// prop's top against it would classify against a moving number (design R6).
        /// </remarks>
        public float GetSupportSurfaceHeightWorldM()
        {
            return supportSurfaceHeightWorldM;
        }

        /// <summary>Unit normal of the selected support surface.</summary>
        public Vector3 GetSupportNormalWorld()
        {
            return supportSurfaceNormalWorld;
        }

        /// <summary>
        /// Which surface supports the ball: 0 = ground, 1+i = static box i,
        /// 10+i = static ramp i (P3), 100+i = support-prop slot i (P2). The host
        /// registers its support rows in ascending prop-index order, so a slot is stable
        /// while the prop stays selected; it is a diagnostic key, not a durable prop
        /// identity.
        /// </summary>
        public int GetSupportSurfaceId()
        {
            return supportSurfaceId;
        }

        /// <summary>Surfaces inside this step's band (footprint not applied). Diagnostic.</summary>
        public int GetSupportSurfaceCount()
        {
            return supportCandidateCount;
        }

        /// <summary>This step's step limit: kStep * semantic diameter, metres.</summary>
        public float GetStepHeightLimitWorldM()
        {
            return stepHeightLimitWorldM;
        }

        /// <summary>True while the ball is falling (lost its support; gravity active).</summary>
        public bool IsFalling()
        {
            return isFalling;
        }

        /// <summary>
        /// True when the selected support is a prop top (P2). False for the ground, a
        /// static box, or no resolved surface. With terrain support off no prop is ever
        /// resolved, so this stays false.
        /// </summary>
        public bool IsSupportingAProp()
        {
            return SupportSurfaceMath.IsPropSurfaceId(supportSurfaceId);
        }

        /// <summary>
        /// What kind of surface supports the ball: 0 = ground, 1 = static box top,
        /// 2 = static ramp (P3), 3 = support prop top (P2), -1 = no surface resolved.
        /// </summary>
        /// <remarks>
        /// It is a kind, not an index: <see cref="GetSupportSurfaceId"/> names WHICH box,
        /// ramp or prop, this says WHAT it is, and a reader that only needs "is this a
        /// slope" should read this one. Ids are ordered so the range tests are the ones
        /// SupportSurfaceMath already owns: ramps are 10..13 (FirstRampSurfaceId + i) and
        /// props are 100..105 (FirstPropSurfaceId + i), while static boxes stay 1..8.
        /// </remarks>
        public int GetSupportKind()
        {
            // v2 adds two kinds (design §2.5): 5 = only a pin is holding the ball up, and
            // 4 = the ball is on a climbing contact (a wall with no floor). The id ranges
            // 0/1..8/10..13/100..105 keep their v1 meaning, which is what lets the test
            // field's existing reads keep working with the flag on.
            if (contactModelV2Enabled)
            {
                if (supportFromNeedle) return 5;
                if (supportFromClimb) return 4;
            }
            if (supportSurfaceId == SupportSurfaceMath.GroundSurfaceId) return 0;
            if (SupportSurfaceMath.IsRampSurfaceId(supportSurfaceId)) return 2;
            if (SupportSurfaceMath.IsPropSurfaceId(supportSurfaceId)) return 3;
            if (supportSurfaceId > 0) return 1;
            return -1;
        }

        /// <summary>Sweep-array rebuilds caused by a step classification flip (diagnostic).</summary>
        public int GetStepMaskRebuildCount()
        {
            return stepMaskRebuildCount;
        }

        /// <summary>
        /// The last accepted support height above the core sphere: max(0,
        /// supportHeight - coreRadius). This is the bump the long term wants to see in
        /// the single-rod lab; it is capped by maxProxyProtrusionPerCoreRadius.
        /// </summary>
        public float GetGroundProxyLiftWorldM()
        {
            float lift = proxyGroundSupportHeightM - coreRadiusWorldM;
            if (lift < 0f) return 0f;
            return lift;
        }

        /// <summary>
        /// The protrusion cap the last ground solve used, metres: min(core *
        /// maxProxyProtrusionPerCoreRadius, maxProxyProtrusionWorldM). Read-only, for the
        /// single-rod lab and for a HUD that shows why the lift stopped growing.
        /// </summary>
        public float GetProxyProtrusionCapWorldM()
        {
            return proxyProtrusionCapWorldM;
        }

        /// <summary>Every attach that did not register an item (normal skips included).</summary>
        public int GetProxySkipCount()
        {
            return proxySkipCount;
        }

        /// <summary>Reason code of the most recent proxy skip; 0 before any skip.</summary>
        public int GetLastProxySkipReason()
        {
            return proxySkipReason;
        }

        /// <summary>Skips that were faults (capacity/invalid input/math), not shape misses.</summary>
        public int GetProxyRefusalCount()
        {
            return proxyRefusalCount;
        }

        // ------------------------------------------------------------------
        // Read-only accessors: HUD, tests and adapters read; only the stages write.
        // ------------------------------------------------------------------

        public Vector3 GetCenterWorld()
        {
            return ballCenterWorld;
        }

        public float GetHeadingDegrees()
        {
            return headingDegrees;
        }

        public Vector3 GetPlanarVelocity()
        {
            return planarVelocity;
        }

        public float GetCollisionRadiusWorldM()
        {
            return collisionRadiusWorldM;
        }

        public float GetSemanticDiameterWorldM()
        {
            return semanticDiameterWorldM;
        }

        /// <summary>
        /// The confirmed volume the size model consumes: the ledger's unbanked volume plus
        /// the already-scored carried residual, as a float.
        /// </summary>
        /// <remarks>
        /// A SIZE quantity, not a ledger sum: it is recomputed from the 64-bit ledger
        /// mirror on every step through <see cref="RecomputeConfirmedAndSize"/>, so it
        /// never accumulates and cannot drift away from the ledger. The exact 64-bit
        /// ledger quantity is <see cref="GetConfirmedLedgerVolumeUnits"/>.
        /// </remarks>
        public float GetConfirmedVolumeUnits()
        {
            return confirmedVolumeUnits;
        }

        /// <summary>
        /// The ledger's confirmed (unbanked) volume for the local row, exactly, as a long.
        /// </summary>
        /// <remarks>
        /// This is the ledger quantity itself: a commit adds to it, a bank subtracts from
        /// it, and it is never a prediction. It is 64-bit because the ledger columns are
        /// (Docs/OPEN_DECISIONS.md (r), migration step A), so a total beyond int.MaxValue
        /// reads back exactly. A per-absorb delta is the difference of two of these, which
        /// is exact integer arithmetic; subtracting two float accumulators is not.
        /// </remarks>
        public long GetConfirmedLedgerVolumeUnits()
        {
            return unbankedVolumeUnits;
        }

        /// <summary>The ledger's banked score for the local row, exactly, as a long.</summary>
        /// <remarks>
        /// The only banked-score accessor: it returns the ledger quantity itself (64-bit since
        /// Docs/OPEN_DECISIONS.md (r) step A), so a score past <c>int.MaxValue</c> reads back
        /// exactly instead of saturating. Step B retired the 32-bit saturating projection and
        /// moved the one caller that used it (the CPU-skirmish debug harness) here.
        /// </remarks>
        public long GetBankedLedgerScoreUnits()
        {
            return bankedScoreUnits;
        }

        public int GetAbsorbedCount()
        {
            return absorbedCount;
        }

        public float GetStuckSeconds()
        {
            return stuckSeconds;
        }

        public Quaternion GetVisualRollRotation()
        {
            return visualRollRotation;
        }

        public int GetPendingCandidateCount()
        {
            return pendingCandidateCount;
        }

        /// <summary>
        /// The contact-radius input the pickup gate adds to reach, metres.
        /// </summary>
        /// <remarks>
        /// Read-only, for diagnostics: a reader that shows "reach" without it would
        /// report a smaller reach than the gate actually used.
        /// </remarks>
        public float GetContactRadiusWorldM()
        {
            return contactRadiusWorldM;
        }

        /// <summary>
        /// The ball side of the size gate: this step's semantic diameter in WHOLE
        /// MILLIMETRES (<c>floor(1000*D + 1e-3)</c>). -1 when the diameter is invalid.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read-only, for diagnostics, tests and a host that shows "ball 300 mm vs item
        /// Req 268 mm". It is the exact number the gate compares
        /// (<see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/> floors the same diameter the
        /// same way) - there is no per-step volume limit to report any more, because the
        /// original quantizes the diameter, not the volume.
        /// </para>
        /// <para>
        /// The item side is
        /// <see cref="PickupAbsorbMath.TryComputePickupReqDiameterMm"/>, also in whole mm;
        /// a reader should print the two next to each other and never print an AABB volume
        /// as if it were the gate's quantity.
        /// </para>
        /// </remarks>
        public float GetAbsorbGateDiameterMm()
        {
            float millimetres;
            if (!PickupAbsorbMath.TryFloorMillimetres(semanticDiameterWorldM, out millimetres))
                return -1f;
            return millimetres;
        }

        /// <summary>
        /// How many candidates the queue holds (4).
        /// </summary>
        /// <remarks>
        /// Read by a host that polls for a free slot instead of submitting into a
        /// full queue. Submitting anyway is legal but latches the overflow signal,
        /// which exists for lost data, so a polling host checks this first.
        /// </remarks>
        public int GetPendingCandidateCapacity()
        {
            return PendingCandidateCapacity;
        }

        /// <summary>
        /// True once any fixed-capacity registration was refused, or once the
        /// sweep's contact buffer filled at least once.
        /// </summary>
        public bool HasOverflowSignal()
        {
            return overflowLatched || contactBufferSaturated;
        }

        /// <summary>
        /// Number of refused plane, box and candidate registrations. The first
        /// refusal also latches a reason; see <see cref="GetFirstOverflowReason"/>.
        /// </summary>
        public int GetOverflowCount()
        {
            return overflowCount;
        }

        /// <summary>
        /// Reason of the first refused registration, or null before one happens
        /// (and null when only the sweep contact buffer saturated).
        /// </summary>
        /// <remarks>
        /// Written once, so it names a capacity rather than growing into a
        /// running log; the sweep's contact saturation has its own getter.
        /// </remarks>
        public string GetFirstOverflowReason()
        {
            return firstOverflowReason;
        }

        /// <summary>
        /// True if the sweep's contact buffer (ContactCapacity = 4) was full at
        /// least once. SweepMath caps contacts there without a truncation flag,
        /// so contacts beyond the buffer in such a step are dropped by the
        /// sweep, not by this motor.
        /// </summary>
        public bool WasContactBufferSaturated()
        {
            return contactBufferSaturated;
        }

        // ------------------------------------------------------------------
        // Absorbed-event ring (additive; nothing in the pipeline reads it)
        // ------------------------------------------------------------------

        /// <summary>
        /// Appends one committed absorb to the ring.
        /// </summary>
        /// <remarks>
        /// Slot is the absolute event index modulo the capacity, so event #0 lands
        /// in slot 0 and event #16 overwrites slot 0. Callers index by the ABSOLUTE
        /// event number (0 = first absorb ever), which lets a reader keep its own
        /// cursor across frames without knowing about wraparound.
        /// <para>
        /// Only the item id and the submission-time position are kept. The size was
        /// dropped together with its reader: a host that hides the eaten prop reads the
        /// prop's own current scale, because a submission-time snapshot is stale by the
        /// time the event is read (Docs/Design/StoryMode/README.md).
        /// </para>
        /// </remarks>
        private void RecordAbsorbedEvent(string itemId, Vector3 position)
        {
            if (absorbedEventItemIds == null) return;
            int slot = absorbedEventCount % AbsorbedEventCapacity;
            absorbedEventItemIds[slot] = itemId;
            absorbedEventPositions[slot] = position;
            absorbedEventCount = absorbedEventCount + 1;
            if (absorbedEventRingCount < AbsorbedEventCapacity)
            {
                absorbedEventRingCount = absorbedEventRingCount + 1;
            }
        }

        /// <summary>
        /// The number of absorbed events recorded so far. Monotonic across rounds: it is
        /// a total, not a ring occupancy, so a reader can use it as its own cursor and a
        /// round reset never makes it go backwards.
        /// </summary>
        public int GetAbsorbedEventCount()
        {
            return absorbedEventCount;
        }

        public int GetAbsorbedEventCapacity()
        {
            return AbsorbedEventCapacity;
        }

        /// <summary>
        /// Number of events the ring can no longer show: evicted by wraparound, or
        /// dropped by a round reset.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT routed into <see cref="SignalOverflow"/>: eviction is
        /// normal when a run absorbs more than 16 items, whereas the overflow latch
        /// means a host lost a candidate and is read as a fault by the tests. This
        /// getter keeps the same "a reader fell behind" information visible without
        /// turning ordinary play into a latched error. It is
        /// count - retained, so after a reset it equals the whole absolute count: those
        /// events existed and can no longer be read, which is exactly what a reader
        /// needs to know. Monotonic, like the count it is derived from.
        /// </remarks>
        public int GetAbsorbedEventDroppedCount()
        {
            return absorbedEventCount - absorbedEventRingCount;
        }

        /// <summary>
        /// Item id of the event at an absolute index, or "" once it was evicted or
        /// when the index is out of range.
        /// </summary>
        public string GetAbsorbedEventItemId(int absoluteIndex)
        {
            int slot = AbsorbedEventSlot(absoluteIndex);
            if (slot < 0) return "";
            string id = absorbedEventItemIds[slot];
            if (id == null) return "";
            return id;
        }

        public Vector3 GetAbsorbedEventPosition(int absoluteIndex)
        {
            int slot = AbsorbedEventSlot(absoluteIndex);
            if (slot < 0) return Vector3.zero;
            return absorbedEventPositions[slot];
        }

        /// <summary>
        /// Prints the full input set of the first few refusals, once each.
        /// </summary>
        /// <remarks>
        /// The ring says which branch refused; this says with what numbers, so a
        /// wrong reach or a wrong item radius is visible without reading coordinates
        /// by hand. Capped at <c>rejectDiagnosticsLimit</c> lines for the whole run so
        /// a persistent refusal cannot flood the console.
        /// </remarks>
        private void LogRejectDiagnosticsOnce(int index, AbsorbOutcome outcome, Vector3 center)
        {
            if (rejectDiagnosticsLogged >= rejectDiagnosticsLimit) return;
            rejectDiagnosticsLogged = rejectDiagnosticsLogged + 1;

            float itemRadius;
            float reach;
            if (!PickupAbsorbMath.TryComputeReach(pendingBounds[index], collisionRadiusWorldM,
                contactRadiusWorldM, out itemRadius, out reach))
            {
                // Illegal bounds: the gate refused them as invalid input before it had a
                // reach, so there is no number to print. -1 is this file's invalid marker.
                itemRadius = -1f;
                reach = -1f;
            }

            Debug.Log("[BallMotor] reject#" + rejectDiagnosticsLogged
                + " id=" + pendingItemIds[index]
                + " kind=" + pendingKinds[index]
                + " outcome=" + (int)outcome
                + " d=" + Vector3.Distance(pendingPositions[index], center)
                + " reach=" + reach
                + " itemR=" + itemRadius
                + " ballR=" + collisionRadiusWorldM
                + " contactR=" + contactRadiusWorldM
                + " allowedKinds=" + allowedKindCount
                + " ballCenter=" + center
                + " itemPos=" + pendingPositions[index]);
        }

        /// <summary>How many evaluations of the traced item have been recorded.</summary>
        public int GetTraceCount()
        {
            return traceLinesLogged;
        }

        public float GetTraceLastDistance()
        {
            return traceLastDistance;
        }

        public float GetTraceLastReach()
        {
            return traceLastReach;
        }

        public float GetTraceLastBallZ()
        {
            return traceLastBallZ;
        }

        public int GetTraceLastOutcome()
        {
            return traceLastOutcome;
        }

        public string GetDebugTraceItemId()
        {
            return debugTraceItemId;
        }

        /// <summary>Item id of the queued candidate at a slot, or "" when out of range.</summary>
        public string GetPendingCandidateItemId(int index)
        {
            if (pendingItemIds == null) return "";
            if (index < 0 || index >= pendingCandidateCount) return "";
            string id = pendingItemIds[index];
            if (id == null) return "";
            return id;
        }

        /// <summary>
        /// Value the motor holds for the candidate at a slot, or -1 when out of range.
        /// </summary>
        /// <remarks>
        /// A prop's own serialized value is not proof of what the motor received: the
        /// submission carries the value, and a wrong field at the call site would show up
        /// only here. Read-only.
        /// </remarks>
        public float GetPendingCandidateValue(int index)
        {
            if (pendingValues == null) return -1f;
            if (index < 0 || index >= pendingCandidateCount) return -1f;
            return pendingValues[index];
        }

        /// <summary>
        /// Awards settlement points once per semantic key.
        /// </summary>
        /// <remarks>
        /// The rule lives in <see cref="GrowthLedgerMath.TryAwardOnce"/>; this method only
        /// supplies this ball's ledger row and its awarded-key arrays, then adds the points
        /// to the ledger's own banked score. A refused award leaves the score untouched.
        /// </remarks>
        public bool TryAwardScore(string awardKey, int points)
        {
            // Every check that can refuse WITHOUT consuming the key happens first. An
            // earlier revision recorded the key and then discovered a bad row, which burned
            // the key forever and made the bonus unrecoverable after a retry.
            if (!hasLocalRow) { lastAwardFailure = 5; return false; }
            if (bankedScores == null) { lastAwardFailure = 5; return false; }

            long banked = bankedScores[localRow];
            if (banked < 0) { lastAwardFailure = 5; return false; }
            if (points > 0)
            {
                long projected = banked + points;
                if (projected < banked) { lastAwardFailure = 5; return false; }
            }

            int newCount;
            int failureCode;
            if (!GrowthLedgerMath.TryAwardOnce(awardedKeys, awardedPoints, awardedCount,
                awardKey, points, out newCount, out failureCode))
            {
                lastAwardFailure = failureCode;
                return false;
            }

            awardedCount = newCount;
            bankedScores[localRow] = banked + points;
            bankedScoreUnits = bankedScores[localRow];
            lastAwardFailure = 0;
            return true;
        }

        /// <summary>How many distinct award keys this ball has been granted.</summary>
        public int GetAwardedAwardCount()
        {
            return awardedCount;
        }

        /// <summary>Failure code of the most recent TryAwardScore, 0 after a success.</summary>
        public int GetLastAwardFailure()
        {
            return lastAwardFailure;
        }

        /// <summary>
        /// Seconds since the candidate at a slot was submitted, or -1 when out of range.
        /// </summary>
        /// <remarks>
        /// A stale position snapshot and a fresh one look identical from the outside;
        /// this is what distinguishes them. Data only, feeds no decision.
        /// </remarks>
        public float GetPendingCandidateAge(int index)
        {
            if (pendingSubmitTimes == null) return -1f;
            if (index < 0 || index >= pendingCandidateCount) return -1f;
            return Time.time - pendingSubmitTimes[index];
        }

        /// <summary>
        /// Centre distance of the queued candidate at a slot, or -1 when out of range.
        /// </summary>
        /// <remarks>
        /// Data only: rounding and formatting belong to whoever presents it. This type
        /// deliberately exposes no human-readable strings - a formatter here would be
        /// called by the next presenter and slowly become a second layout to maintain.
        /// </remarks>
        public float GetPendingCandidateDistance(int index)
        {
            if (pendingPositions == null) return -1f;
            if (index < 0 || index >= pendingCandidateCount) return -1f;
            return Vector3.Distance(pendingPositions[index], ballCenterWorld);
        }

        /// <summary>
        /// One-shot record of why a claim/commit was refused.
        /// </summary>
        /// <remarks>
        /// A refused commit used to leave no trace at all: the candidate passed the
        /// geometry gate (so no reject event), never produced an absorbed event, and
        /// stayed queued until it was withdrawn. That is indistinguishable from "no
        /// props in range". claimFailure is the failure code from
        /// GrowthLedgerMath.TrySubmitClaimWithFailure (1..11, 0 when the claim was
        /// accepted); resolveFailure is 1 when TryResolveClaims itself refused, and
        /// commitRow -1 marks the "success but no commit row" case.
        /// </remarks>
        private void LogCommitFailureOnce(string itemId, long gain, int claimFailure,
            int resolveFailure, int commitRow)
        {
            if (commitDiagnosticsLogged >= commitDiagnosticsLimit) return;
            commitDiagnosticsLogged = commitDiagnosticsLogged + 1;

            int phase = -1;
            int life = -1;
            if (roundState != null) phase = roundState[GrowthLedgerMath.RoundPhaseState];
            if (hasLocalRow && lifeStates != null) life = lifeStates[localRow];

            Debug.Log("[BallMotor] commit id=" + itemId
                + " gain=" + gain
                + " claim=" + (claimFailure == 0 ? "ok" : "FAIL(" + claimFailure + ")")
                + " resolve=" + (resolveFailure == 0 ? "ok" : "FAIL(" + resolveFailure + ")")
                + " commitRow=" + commitRow
                + " phase=" + phase
                + " life=" + life
                + " claimCount=" + claimCount);
        }

        /// <summary>
        /// Appends one refused candidate and the reason the gate gave.
        /// </summary>
        /// <remarks>
        /// The reason is the <c>PickupAbsorbMath.AbsorbOutcome</c> value as an int so
        /// the assembly layer stays free of a second enum, and the distance is the
        /// centre-to-centre distance the reach test saw. Recorded only for a refused
        /// candidate: an accepted one is silent here and appears in the absorbed ring
        /// once its ledger commit succeeds.
        /// </remarks>
        private void RecordReject(string itemId, int reason, float distance)
        {
            if (rejectEventItemIds == null) return;
            int slot = rejectEventCount % RejectEventCapacity;
            rejectEventItemIds[slot] = itemId;
            rejectEventReasons[slot] = reason;
            rejectEventDistances[slot] = distance;
            rejectEventCount = rejectEventCount + 1;
        }

        /// <summary>Number of refused candidates recorded so far (monotonic).</summary>
        public int GetRejectEventCount()
        {
            return rejectEventCount;
        }

        public int GetRejectEventCapacity()
        {
            return RejectEventCapacity;
        }

        /// <summary>Refusals the 8-slot ring has already evicted.</summary>
        public int GetRejectEventDroppedCount()
        {
            if (rejectEventCount <= RejectEventCapacity) return 0;
            return rejectEventCount - RejectEventCapacity;
        }

        /// <summary>Item id of the refusal at an absolute index, or "" once evicted.</summary>
        public string GetRejectEventItemId(int absoluteIndex)
        {
            int slot = RejectEventSlot(absoluteIndex);
            if (slot < 0) return "";
            string id = rejectEventItemIds[slot];
            if (id == null) return "";
            return id;
        }

        /// <summary>
        /// Reason of the refusal at an absolute index, as a
        /// <c>PickupAbsorbMath.AbsorbOutcome</c> value; -1 when out of range.
        /// </summary>
        public int GetRejectEventReason(int absoluteIndex)
        {
            int slot = RejectEventSlot(absoluteIndex);
            if (slot < 0) return -1;
            return rejectEventReasons[slot];
        }

        /// <summary>Centre-to-centre distance the reach test saw, or -1 when invalid.</summary>
        public float GetRejectEventDistance(int absoluteIndex)
        {
            int slot = RejectEventSlot(absoluteIndex);
            if (slot < 0) return -1f;
            return rejectEventDistances[slot];
        }

        private int RejectEventSlot(int absoluteIndex)
        {
            if (rejectEventItemIds == null) return -1;
            if (absoluteIndex < 0) return -1;
            if (absoluteIndex >= rejectEventCount) return -1;
            int retained = rejectEventCount;
            if (retained > RejectEventCapacity) retained = RejectEventCapacity;
            int firstRetained = rejectEventCount - retained;
            if (absoluteIndex < firstRetained) return -1;
            return absoluteIndex % RejectEventCapacity;
        }

        /// <summary>
        /// How many kind entries the pickup gate will accept.
        /// </summary>
        /// <remarks>
        /// ZERO MEANS "DENY EVERY KIND", not "no filter": the gate's kind test walks
        /// the allowed list and returns false when nothing matches, and Start()
        /// initialises the count to 0. A host must therefore call SetAllowedKinds
        /// before the first step, and should read this back to prove it landed;
        /// otherwise every candidate is refused as kind-not-allowed and the run looks
        /// silently empty.
        /// </remarks>
        public int GetAllowedKindCount()
        {
            return allowedKindCount;
        }

        /// <summary>
        /// Maps an absolute index to its ring slot, or -1 when out of range or
        /// already evicted (including events a round reset emptied).
        /// </summary>
        private int AbsorbedEventSlot(int absoluteIndex)
        {
            if (absorbedEventItemIds == null) return -1;
            if (absoluteIndex < 0) return -1;
            if (absoluteIndex >= absorbedEventCount) return -1;
            int retained = absorbedEventRingCount;
            int firstRetained = absorbedEventCount - retained;
            if (absoluteIndex < firstRetained) return -1;
            return absoluteIndex % AbsorbedEventCapacity;
        }
    }
}
