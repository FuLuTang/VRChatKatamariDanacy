using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
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
        // A box already classified as a step stays one until its rise exceeds
        // stepLimit + this. Stops the sweep mask from flipping on rim noise.
        [SerializeField] private float stepHysteresisM = 0.02f;
        // "The ball is already above this box's top" margin. Without it, a ball standing
        // on a step box can be re-classified into a wall while its centre is inside the
        // expanded box, and the sweep's penetration branch pushes it off.
        [SerializeField] private float stepStickMarginM = 0.01f;
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
        [SerializeField] private float supportDropTriggerM = 0.01f;

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

        // ---- PickupAbsorbMath inputs ----
        [SerializeField] private float contactRadiusWorldM = 0f;
        // The size gate is a VOLUME gate relative to the ball (Docs/OPEN_DECISIONS.md
        // (l)): an item is absorbable when its AABB volume a*b*c is at most
        // min(maxItemVolumeWorldM3, (pi/6) * semanticDiameter^3 / absorbVolumeRatio).
        // absorbVolumeRatio is the gameplay number: the ball's volume must be that many
        // times the item's, and 10 is the original's ratio. maxItemVolumeWorldM3 is only
        // a safety ceiling so a mis-set ratio cannot let a tiny ball eat a house; 512 m^3
        // is an 8 m cube, the retired 8 m length ceiling read as a cube.
        //
        // The retired fields were `maxItemSizeWorldM` (8f, a LENGTH ceiling) and
        // `absorbSizeRatio` (1f, item longest edge / ball diameter). They are deleted, not
        // deprecated: a length ceiling next to a volume gate truncates at 8 m of longest
        // edge, which is not a volume statement, and a linear ratio cannot express a
        // quantity where edges differing 6x differ 216x in volume. A scene or prefab
        // still serializing the old names just has its dead keys ignored and falls back to
        // these defaults; deleting them is editor cleanup, not a behaviour prerequisite
        // (see Docs/Design/PickupAbsorb/README.md 1.1).
        [SerializeField] private float maxItemVolumeWorldM3 = 512f;
        [SerializeField] private float absorbVolumeRatio = 10f;

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
        private int unbankedVolumeUnits;
        private float carriedVolumeUnits;
        private float confirmedVolumeUnits;
        private int bankedScoreUnits;
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
        private int[] confirmedVolumes;
        private int[] bankedScores;
        private int[] lifeStates;
        private bool[] respawnAtSet;
        private float[] respawnAt;
        private int[] playerGenerations;
        private int playerCount;
        private string[] commitItemIds;
        private int[] commitGenerations;
        private string[] commitWinners;
        private int[] commitSequences;
        private int[] commitGains;
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
        private int[] predictedGains;
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
        // boxIsSteppable is indexed like the static box buffers and is the ONE answer
        // both the sweep mask and the support table read (§4.3: two places must not
        // compute rise separately). A true entry is excluded from the sweep and can
        // support the ball; a false entry stays a wall.
        private bool[] boxIsSteppable;
        private bool stepMaskValid;
        private bool stepMaskBoxesDirty;
        private int stepMaskRebuildCount;
        private float stepMaskDiameterWorldM;
        private float stepMaskSupportHeightWorldM;
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
        // in Start and reused: 32 directions, 32 endpoint-only supports, a 32-slot pool
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
            confirmedVolumes = new int[PlayerCapacity];
            bankedScores = new int[PlayerCapacity];
            lifeStates = new int[PlayerCapacity];
            respawnAtSet = new bool[PlayerCapacity];
            respawnAt = new float[PlayerCapacity];
            playerGenerations = new int[PlayerCapacity];
            commitItemIds = new string[CommitCapacity];
            commitGenerations = new int[CommitCapacity];
            commitWinners = new string[CommitCapacity];
            commitSequences = new int[CommitCapacity];
            commitGains = new int[CommitCapacity];
            claimItemIds = new string[ClaimCapacity];
            claimGenerations = new int[ClaimCapacity];
            claimPlayerIds = new string[ClaimCapacity];
            claimFrames = new int[ClaimCapacity];
            claimRequestSequences = new int[ClaimCapacity];
            predictedPlayerIds = new string[PredictionCapacity];
            predictedItemIds = new string[PredictionCapacity];
            predictedGenerations = new int[PredictionCapacity];
            predictedGains = new int[PredictionCapacity];
            bankRequestPlayerIds = new string[BankRequestCapacity];
            bankRequestIds = new string[BankRequestCapacity];

            // The pending-candidate stores are the only arrays this type declared
            // without allocating, so SubmitCandidate dereferenced null on its first
            // call. Nothing exercised that seam before a scene drove it; the capacity
            // (4) and the drop-on-full policy are unchanged.
            pendingAbsorbable = new bool[PendingCandidateCapacity];
            pendingKinds = new string[PendingCandidateCapacity];
            pendingBounds = new Vector3[PendingCandidateCapacity];
            pendingPositions = new Vector3[PendingCandidateCapacity];
            pendingValues = new float[PendingCandidateCapacity];
            pendingItemIds = new string[PendingCandidateCapacity];
            pendingGenerations = new int[PendingCandidateCapacity];
            pendingAccepted = new bool[PendingCandidateCapacity];
            pendingSubmitTimes = new float[PendingCandidateCapacity];
            pendingCandidateCount = 0;

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

            planeNormalBuffer = new Vector3[MaxPlaneCount];
            planeOffsetBuffer = new float[MaxPlaneCount];
            boxMinimumBuffer = new Vector3[MaxBoxCount];
            boxMaximumBuffer = new Vector3[MaxBoxCount];
            blockingMinimumBuffer = new Vector3[MaxBlockingBoxCount];
            blockingMaximumBuffer = new Vector3[MaxBlockingBoxCount];
            planeCount = 0;
            boxCount = 0;
            blockingBoxCount = 0;
            blockingRefusalCount = 0;
            blockingRefusalReason = "";
            RebuildObstacles();
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
            stepMaskSupportHeightWorldM = 0f;
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

            // The radial proxy shell is the ball's sparse support shell: 32 fixed
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
                + " hasLocalRow=" + hasLocalRow
                + " terrainSupport=" + terrainSupportEnabled);
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

            // 2.5 Terrain support mask (P1): decide which static boxes are steps and
            //     rebuild the sweep obstacle arrays without them. This runs BEFORE the
            //     sweep because entering a step means being allowed inside the box's
            //     expanded AABB; it uses the previous step's support height, which is
            //     the design's accepted one-step lag. The mask and the support table
            //     read the same answer (boxIsSteppable), so they cannot disagree.
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

            // 4. GroundContactMath: support height and the normal-only correction.
            //
            // Two solves per step, both cheap. The first uses the radial proxy shell's
            // clamped endpoints, so a long rod pointing at the ground lifts the core by
            // up to maxProxyProtrusion (and no more); the second uses an empty proxy array
            // and is the same plane solved for the smooth core sphere. Only the first
            // moves the ball; the second is where the follow rig reads the centre, so a
            // bump cannot bob the camera (the height filter alone would only soften it).
            Vector3 groundedCenter = sweptCenter;
            Vector3 followCenter = sweptCenter;
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
            Vector3 supportNormal = groundPlaneNormal;
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
        /// Refreshes which static boxes are steps this step, and rebuilds the sweep
        /// array without the steppable ones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the sweep side of §4.3 of Docs/Design/BallMotor/TERRAIN_SUPPORT.md: a
        /// box the ball may climb must be absent from the sweep, because the sweep is a
        /// conservative radius-expanded AABB and a 2.8 cm lip would otherwise be a wall
        /// for every ball. The support table reads the same <c>boxIsSteppable</c> answer,
        /// so the two cannot disagree about a box.
        /// </para>
        /// <para>
        /// Frequency gate (design §3.5): recompute only when the box set changed, the
        /// semantic diameter moved by more than 2 mm, or the reference support height
        /// moved by more than 5 mm. The mask uses the previous step's support height,
        /// which is the accepted one-step lag; the rebuild of the sweep arrays only runs
        /// when the mask actually changed.
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

            bool needRecompute = !stepMaskValid || stepMaskBoxesDirty;
            if (!needRecompute)
            {
                if (Mathf.Abs(semanticDiameterWorldM - stepMaskDiameterWorldM) > 0.002f)
                {
                    needRecompute = true;
                }
                else if (Mathf.Abs(lastSupportHeightWorldM - stepMaskSupportHeightWorldM) > 0.005f)
                {
                    needRecompute = true;
                }
            }
            if (!needRecompute) return;

            stepMaskValid = true;
            stepMaskBoxesDirty = false;
            stepMaskDiameterWorldM = semanticDiameterWorldM;
            stepMaskSupportHeightWorldM = lastSupportHeightWorldM;

            bool changed = false;
            float ballBottom = ballCenterWorld.y - collisionRadiusWorldM;
            for (int i = 0; i < boxCount; i++)
            {
                float top = boxMaximumBuffer[i].y;
                float rise = top - lastSupportHeightWorldM;
                // "Already above the top" is unconditional: a ball standing on the box
                // must never be re-classified into a wall while its centre is inside the
                // expanded box, or the sweep's penetration branch pushes it off.
                bool alreadyAbove = ballBottom >= top - stepStickMarginM;
                bool steppable = SupportSurfaceMath.IsSteppable(rise, limit, alreadyAbove,
                    boxIsSteppable[i], stepHysteresisM);
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

            // The band reference is the higher of the previous support and the ball's own
            // bottom. With gravity in the model, "the previous support + stepLimit" alone
            // would exclude a surface the ball is falling ONTO (it is above the old
            // support but below the ball), and because such a box is also out of the sweep
            // when it is already-above, the ball would fall through it. Grounded, the ball
            // bottom equals the support, so this is the design's band unchanged.
            float bandReference = lastSupportHeightWorldM;
            float ballBottom = center.y - collisionRadiusWorldM;
            if (ballBottom > bandReference) bandReference = ballBottom;

            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;
            if (SupportSurfaceMath.TryResolveSupport(center, bandReference, limit,
                groundPlaneNormal, groundPlaneOffset, boxMinimumBuffer, boxMaximumBuffer,
                boxCount, out id, out point, out normal, out height, out candidates))
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
            if (terrainSupportEnabled &&
                SupportSurfaceMath.SupportDroppedAway(supportSurfaceHeightWorldM,
                    lastSupportHeightWorldM, supportDropTriggerM, out dropped))
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
                // The volume limit is resolved inside the pickup layer from this step's
                // semantic diameter, so the gate grows with the ball; maxItemVolumeWorldM3
                // is passed as the ceiling only. semanticDiameterWorldM is the same
                // reading the size stage derives and GetSemanticDiameterWorldM reports,
                // while reach keeps using collisionRadiusWorldM.
                AbsorbOutcome outcome = PickupAbsorbMath.EvaluateAbsorbWithValueAndBallSize(
                    pendingAbsorbable[i], pendingKinds[i], allowedKinds, allowedKindCount,
                    pendingBounds[i], pendingPositions[i], center, collisionRadiusWorldM,
                    contactRadiusWorldM, maxItemVolumeWorldM3, maxCollisionRadiusWorldM,
                    semanticDiameterWorldM, absorbVolumeRatio, pendingValues[i]);
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
                        collisionRadiusWorldM, contactRadiusWorldM, out tracedItemRadius,
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
                    int gain;
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
            pendingItemIds[write] = pendingItemIds[read];
            pendingGenerations[write] = pendingGenerations[read];
            pendingAccepted[write] = pendingAccepted[read];
            pendingSubmitTimes[write] = pendingSubmitTimes[read];
        }

        private bool TryCommitAbsorb(string itemInstanceId, int generation, int gain)
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

            int confirmed = confirmedVolumes[localRow];
            int banked = bankedScores[localRow];
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
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            if (itemGeneration < 0) return false;

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
            if (boxCount >= MaxBoxCount)
            {
                SignalOverflow("box capacity 8 exceeded");
                return false;
            }
            if (!KatamariMath.IsFiniteVector(minimum)) return false;
            if (!KatamariMath.IsFiniteVector(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y ||
                minimum.z > maximum.z) return false;
            boxMinimumBuffer[boxCount] = minimum;
            boxMaximumBuffer[boxCount] = maximum;
            boxCount = boxCount + 1;
            // The new box has no step classification yet; the next step evaluates it.
            stepMaskBoxesDirty = true;
            RebuildObstacles();
            return true;
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

        public void ClearObstacles()
        {
            planeCount = 0;
            boxCount = 0;
            blockingBoxCount = 0;
            // The step mask is indexed by boxing slot, so it must not outlive the boxes.
            ClearStepMask();
            RebuildObstacles();
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

            int confirmed = confirmedVolumes[localRow];
            int banked = bankedScores[localRow];
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

        /// <summary>Fixed direction count of the shell (32).</summary>
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

        /// <summary>Which surface supports the ball: 0 = ground, 1+i = static box i.</summary>
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
        /// What kind of surface supports the ball: 0 = ground, 1 = static box top,
        /// -1 = no surface resolved.
        /// </summary>
        /// <remarks>
        /// 2 (ramp) and 3 (prop top) are reserved for P3/P2 and are never returned by this
        /// revision. It is a kind, not an index: <see cref="GetSupportSurfaceId"/> names
        /// WHICH box, this says WHAT it is, and a reader that only needs "can a prop sit
        /// here" should read this one.
        /// </remarks>
        public int GetSupportKind()
        {
            if (supportSurfaceId == SupportSurfaceMath.GroundSurfaceId) return 0;
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

        public float GetConfirmedVolumeUnits()
        {
            return confirmedVolumeUnits;
        }

        public int GetBankedScoreUnits()
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
        /// The volume limit the absorb gate compares an item's AABB volume against: the
        /// ball's current volume divided by <c>absorbVolumeRatio</c>, capped by
        /// <c>maxItemVolumeWorldM3</c>. Cubic metres; -1 when the inputs are invalid.
        /// </summary>
        /// <remarks>
        /// Read-only, for diagnostics and for a host that decides whether an item is
        /// worth submitting. Reason 3 (RejectItemTooLarge) used to be a constant, so
        /// "the item is over the limit" could be answered from the serialized field
        /// alone; now the answer moves with the ball, and a reader should show this
        /// number next to the item's AABB volume (its three bounds multiplied), never
        /// next to its longest edge.
        /// </remarks>
        public float GetAbsorbVolumeLimitWorldM3()
        {
            float limit;
            if (!PickupAbsorbMath.TryComputeAbsorbVolumeLimit(semanticDiameterWorldM,
                absorbVolumeRatio, maxItemVolumeWorldM3, out limit)) return -1f;
            return limit;
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

            int banked = bankedScores[localRow];
            if (banked < 0) { lastAwardFailure = 5; return false; }
            if (points > 0)
            {
                int projected = banked + points;
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
        private void LogCommitFailureOnce(string itemId, int gain, int claimFailure,
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
