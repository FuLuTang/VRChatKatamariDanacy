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

        // ---- GroundContactMath inputs: one analytic plane, no proxy points yet ----
        [SerializeField] private Vector3 groundPlaneNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundPlaneOffset = 0f;
        [SerializeField] private float maxLiftPerStep = 0.1f;
        [SerializeField] private float maxDropPerStep = 0.1f;

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
        [SerializeField] private float maxItemSizeWorldM = 1f;

        // ---- FollowRigMath inputs ----
        [SerializeField] private float heightFilterHz = 5f;
        [SerializeField] private float yawFilterHz = 8f;
        [SerializeField] private float eyeHeightMeters = 1.6f;
        [SerializeField] private float eyeHeightMinMeters = 0.25f;
        [SerializeField] private float eyeHeightMaxMeters = 3f;

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
        private bool lastSweepBlocked;
        private bool lastSweepStuck;
        private bool discontinuous;
        private Quaternion visualRollRotation;
        private Quaternion proxyRotation;

        // ---- Volume readings: only committed values, never predictions ----
        private int unbankedVolumeUnits;
        private float carriedVolumeUnits;
        private float confirmedVolumeUnits;
        private int bankedScoreUnits;
        private int absorbedCount;

        // ---- FollowRig filter state ----
        private Vector3 followRigWorldPosition;
        private float followRigYawDegrees;
        private float followRigEyeOffsetMeters;
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

        private Vector3[] planeNormalBuffer;
        private float[] planeOffsetBuffer;
        private int planeCount;
        private Vector3[] boxMinimumBuffer;
        private Vector3[] boxMaximumBuffer;
        private int boxCount;
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
        private float[] absorbedEventDiameters;
        private int absorbedEventCount;

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

        // ---- One-shot capacity signal; see the remarks on RebuildObstacles ----
        private int overflowCount;
        private bool overflowLatched;
        private string firstOverflowReason;
        private bool contactBufferSaturated;

        // ---- Sizes used once in Start; not part of the per-step state ----
        private Vector3[] localProxyPoints;
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

            absorbedEventItemIds = new string[AbsorbedEventCapacity];
            absorbedEventPositions = new Vector3[AbsorbedEventCapacity];
            absorbedEventDiameters = new float[AbsorbedEventCapacity];
            absorbedEventCount = 0;

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
            planeCount = 0;
            boxCount = 0;
            RebuildObstacles();
            contactCenters = new Vector3[ContactCapacity];
            contactPoints = new Vector3[ContactCapacity];
            contactNormals = new Vector3[ContactCapacity];
            contactTimes = new float[ContactCapacity];
            contactObstacleIndices = new int[ContactCapacity];
            contactPenetrationDepths = new float[ContactCapacity];

            // RadialProxy is its own layer; until it feeds the motor the support
            // height degenerates to the core sphere, which is the documented
            // empty-proxy case.
            localProxyPoints = new Vector3[0];
            allowedKinds = new string[0];
            allowedKindCount = 0;

            ballCenterWorld = startCenterWorld;
            planarVelocity = Vector3.zero;
            headingDegrees = 0f;
            visualRollRotation = Quaternion.identity;
            proxyRotation = Quaternion.identity;
            if (ballVisualRollRoot != null) visualRollRotation = ballVisualRollRoot.rotation;
            if (ballPhysicsRoot != null) proxyRotation = ballPhysicsRoot.rotation;
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
            Debug.Log("[BallMotor] Start ready diameter=" + semanticDiameterWorldM
                + " collisionRadius=" + collisionRadiusWorldM
                + " hasLocalRow=" + hasLocalRow);
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

            // 3. SweepMath: actual displacement. Only this — never the requested
            //    motion — may reach the visual roll later, so a blocked step does
            //    not spin the ball into a wall.
            Vector3 sweptCenter = ballCenterWorld;
            bool sweepAccepted = false;
            bool sweepStuck = false;
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
                lastSweepBlocked = sweepBlocked;
                lastSweepStuck = sweepStuck;
                sweepAccepted = true;
                // SweepMath stops writing contacts at ContactCapacity (4) and
                // reports no truncation flag, so the motor can only record that
                // the buffer filled at least once; any further contact in that
                // step is dropped inside the sweep. See the capacity remarks on
                // RebuildObstacles.
                if (contactCount >= ContactCapacity) contactBufferSaturated = true;
            }
            // Deliberately no velocity change after a contact: removing the
            // normal component of the planar velocity is not exposed by any layer
            // (SweepMath projects the residual internally and returns only the
            // slide direction). The ball therefore stalls against a wall instead
            // of sliding along it; reported as a gap in the README rather than
            // re-derived here.

            // 4. GroundContactMath: support height and the normal-only correction.
            Vector3 groundedCenter = sweptCenter;
            float supportHeight;
            Vector3 correctedCenter;
            float centerNormalCoordinate;
            Vector3 planeAnchor;
            float appliedNormalCorrection;
            float normalSpeedPerturbation;
            bool liftClamped;
            bool dropClamped;
            if (GroundContactMath.TrySolve(coreRadiusWorldM, proxyRotation, localProxyPoints,
                groundPlaneNormal, groundPlaneOffset, sweptCenter, maxLiftPerStep,
                maxDropPerStep, 0f, 0f, out supportHeight, out correctedCenter,
                out centerNormalCoordinate, out planeAnchor, out appliedNormalCorrection,
                out normalSpeedPerturbation, out liftClamped, out dropClamped))
            {
                groundedCenter = correctedCenter;
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
            if (VisualRollMath.TryAdvanceRotation(visualRollRotation, actualDisplacement,
                groundPlaneNormal, coreRadiusWorldM, discontinuous, true,
                maxRollDisplacementM, out nextRotation))
            {
                visualRollRotation = nextRotation;
            }

            // 9. FollowRigMath: consumes this step's final pose, returns presentation
            //    only, and never feeds gameplay state.
            UpdateFollowRig(groundedCenter, dt);

            ballCenterWorld = groundedCenter;
            discontinuous = false;
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
                AbsorbOutcome outcome = PickupAbsorbMath.EvaluateAbsorbWithValue(
                    pendingAbsorbable[i], pendingKinds[i], allowedKinds, allowedKindCount,
                    pendingBounds[i], pendingPositions[i], center, collisionRadiusWorldM,
                    contactRadiusWorldM, maxItemSizeWorldM, maxCollisionRadiusWorldM,
                    pendingValues[i]);
                float evaluatedDistance = Vector3.Distance(pendingPositions[i], center);
                Vector3 evaluatedBounds = pendingBounds[i];
                float evaluatedHalfX = evaluatedBounds.x * 0.5f;
                float evaluatedHalfY = evaluatedBounds.y * 0.5f;
                float evaluatedHalfZ = evaluatedBounds.z * 0.5f;
                float evaluatedItemRadius = Mathf.Sqrt(evaluatedHalfX * evaluatedHalfX
                    + evaluatedHalfY * evaluatedHalfY + evaluatedHalfZ * evaluatedHalfZ);
                float evaluatedReach = collisionRadiusWorldM + contactRadiusWorldM
                    + evaluatedItemRadius;

                if (debugTraceItemId != null && debugTraceItemId.Length > 0
                    && pendingItemIds[i] == debugTraceItemId
                    && traceLinesLogged < TraceLineLimit)
                {
                    // Data only. The host decides whether and how to print it.
                    traceLinesLogged = traceLinesLogged + 1;
                    traceLastDistance = evaluatedDistance;
                    traceLastReach = evaluatedReach;
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
                    // actually moved. The diameter is the candidate's largest bound
                    // component, the same extent the pickup gate compared against.
                    RecordAbsorbedEvent(pendingItemIds[read], pendingPositions[read],
                        Mathf.Max(pendingBounds[read].x,
                            Mathf.Max(pendingBounds[read].y, pendingBounds[read].z)));
                    continue;
                }
                if (write != read)
                {
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
                write = write + 1;
            }
            pendingCandidateCount = write;
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
            float eyeOffset;
            if (!FollowRigMath.TryUpdate(heightFilterHz, yawFilterHz, center, headingDegrees,
                dt, eyeHeightMeters, eyeHeightMinMeters, eyeHeightMaxMeters, discontinuous,
                hasFollowRigFrame, followRigWorldPosition, followRigYawDegrees,
                followRigEyeOffsetMeters, out rigPosition, out rigYaw, out eyeOffset))
            {
                return;
            }
            followRigWorldPosition = rigPosition;
            followRigYawDegrees = rigYaw;
            followRigEyeOffsetMeters = eyeOffset;
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
        /// Compaction mirrors CommitAcceptedAbsorbs so no list is allocated. Returns
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
                if (write != read)
                {
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
                write = write + 1;
            }
            pendingCandidateCount = write;
            return removed;
        }

        public void ClearCandidates()
        {
            pendingCandidateCount = 0;
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
            RebuildObstacles();
            return true;
        }

        public void ClearObstacles()
        {
            planeCount = 0;
            boxCount = 0;
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
        /// The sweep treats the array length as the obstacle set, so the motor
        /// rebuilds exact-length arrays on registration instead of paying a copy
        /// on every step; registration is a scene-load event, not a per-step one.
        /// </para>
        /// <para>
        /// Fixed capacities are MaxPlaneCount = 8, MaxBoxCount = 8,
        /// PendingCandidateCapacity = 4 and ContactCapacity = 4. An entry beyond
        /// a capacity is DROPPED, never queued, resized or degraded:
        /// overflowing the obstacle set changes what the ball can collide with,
        /// and overflowing the candidate queue silently loses an absorbable
        /// item, so an overflow is a data error (a mis-wired scene, or a host
        /// batching more than four candidates per step), not a gameplay choice.
        /// AddPlane, AddBox and SubmitCandidate latch overflowCount and the
        /// first reason string; the sweep's contact buffer is capped inside
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
            boxMinima = new Vector3[boxCount];
            boxMaxima = new Vector3[boxCount];
            for (int i = 0; i < boxCount; i++)
            {
                boxMinima[i] = boxMinimumBuffer[i];
                boxMaxima[i] = boxMaximumBuffer[i];
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
            if (!hasLocalRow) return false;
            if (requestId == null || requestId.Length == 0) return false;
            if (!KatamariMath.IsFiniteFloat(now)) return false;
            if (!KatamariMath.IsFiniteFloat(semanticDiameterWorldM)) return false;
            if (semanticDiameterWorldM < minConversionDiameterWorldM) return false;
            if (unbankedVolumeUnits <= 0) return false;

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
                return false;
            }

            float postBankDiameter;
            if (!BankShrinkMath.TryPostBankDiameter(startDiameter, semanticDiameterWorldM,
                bankResidualFraction, out postBankDiameter))
            {
                return false;
            }

            float postBankVolumeUnits;
            if (!BankShrinkMath.TryBankVolumeUnits(postBankDiameter, baselineVolumeUnits,
                cubicMetersPerVolumeUnit, maxCoreRadiusWorldM,
                semanticDiameterPerCoreDiameter, out postBankVolumeUnits))
            {
                return false;
            }

            if (!GrowthLedgerMath.TryBank(roundState, playerIds, confirmedVolumes,
                bankedScores, lifeStates, respawnAtSet, respawnAt, playerGenerations,
                playerCount, bankRequestPlayerIds, bankRequestIds, ref bankRequestCount,
                localPlayerId, unbankedVolumeUnits, requestId))
            {
                return false;
            }

            int confirmed = confirmedVolumes[localRow];
            int banked = bankedScores[localRow];
            if (confirmed < 0 || banked < 0) return false;
            unbankedVolumeUnits = confirmed;
            bankedScoreUnits = banked;
            return ApplyBankShrink(postBankVolumeUnits);
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
            return true;
        }

        /// <summary>
        /// Moves the ball without rolling it. The discontinuity is consumed by the
        /// next step's visual roll and follow-rig calls.
        /// </summary>
        public void TeleportTo(Vector3 center)
        {
            if (!KatamariMath.IsFiniteVector(center)) return;
            ballCenterWorld = center;
            planarVelocity = Vector3.zero;
            stuckSeconds = 0f;
            discontinuous = true;
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

        public float GetCoreRadiusWorldM()
        {
            return coreRadiusWorldM;
        }

        public float GetCollisionRadiusWorldM()
        {
            return collisionRadiusWorldM;
        }

        public float GetSemanticDiameterWorldM()
        {
            return semanticDiameterWorldM;
        }

        public float GetGrowthFraction()
        {
            return growthFraction;
        }

        public float GetConfirmedVolumeUnits()
        {
            return confirmedVolumeUnits;
        }

        public int GetUnbankedVolumeUnits()
        {
            return unbankedVolumeUnits;
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

        public bool LastSweepWasBlocked()
        {
            return lastSweepBlocked;
        }

        public bool LastSweepWasStuck()
        {
            return lastSweepStuck;
        }

        public Quaternion GetVisualRollRotation()
        {
            return visualRollRotation;
        }

        public Vector3 GetFollowRigWorldPosition()
        {
            return followRigWorldPosition;
        }

        public float GetFollowRigYawDegrees()
        {
            return followRigYawDegrees;
        }

        public bool HasFollowRigFrame()
        {
            return hasFollowRigFrame;
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
        /// </remarks>
        private void RecordAbsorbedEvent(string itemId, Vector3 position, float diameter)
        {
            if (absorbedEventItemIds == null) return;
            int slot = absorbedEventCount % AbsorbedEventCapacity;
            absorbedEventItemIds[slot] = itemId;
            absorbedEventPositions[slot] = position;
            absorbedEventDiameters[slot] = diameter;
            absorbedEventCount = absorbedEventCount + 1;
        }

        /// <summary>
        /// The number of absorbed events recorded so far. Monotonic; it is a total,
        /// not a ring occupancy, so a reader can use it as its own cursor.
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
        /// Number of events the ring has already evicted (0 until it wraps).
        /// </summary>
        /// <remarks>
        /// Deliberately NOT routed into <see cref="SignalOverflow"/>: eviction is
        /// normal when a run absorbs more than 16 items, whereas the overflow latch
        /// means a host lost a candidate and is read as a fault by the tests. This
        /// getter keeps the same "a reader fell behind" information visible without
        /// turning ordinary play into a latched error.
        /// </remarks>
        public int GetAbsorbedEventDroppedCount()
        {
            if (absorbedEventCount <= AbsorbedEventCapacity) return 0;
            return absorbedEventCount - AbsorbedEventCapacity;
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

        public float GetAbsorbedEventDiameter(int absoluteIndex)
        {
            int slot = AbsorbedEventSlot(absoluteIndex);
            if (slot < 0) return 0f;
            return absorbedEventDiameters[slot];
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

            Vector3 bounds = pendingBounds[index];
            float halfX = bounds.x * 0.5f;
            float halfY = bounds.y * 0.5f;
            float halfZ = bounds.z * 0.5f;
            float itemRadius = Mathf.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
            float reach = collisionRadiusWorldM + contactRadiusWorldM + itemRadius;

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
        /// already evicted.
        /// </summary>
        private int AbsorbedEventSlot(int absoluteIndex)
        {
            if (absorbedEventItemIds == null) return -1;
            if (absoluteIndex < 0) return -1;
            if (absoluteIndex >= absorbedEventCount) return -1;
            int retained = absorbedEventCount;
            if (retained > AbsorbedEventCapacity) retained = AbsorbedEventCapacity;
            int firstRetained = absorbedEventCount - retained;
            if (absoluteIndex < firstRetained) return -1;
            return absoluteIndex % AbsorbedEventCapacity;
        }
    }
}
