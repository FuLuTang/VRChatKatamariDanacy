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
        [SerializeField] private float baselineVolumeUnits = 1f;
        [SerializeField] private float cubicMetersPerVolumeUnit = 0.0141f;
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
        private int pendingCandidateCount;

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
                AbsorbOutcome outcome = PickupAbsorbMath.EvaluateAbsorb(
                    pendingAbsorbable[i], pendingKinds[i], allowedKinds, allowedKindCount,
                    pendingBounds[i], pendingPositions[i], center, collisionRadiusWorldM,
                    contactRadiusWorldM, maxItemSizeWorldM, maxCollisionRadiusWorldM);
                if (outcome == AbsorbOutcome.Absorb) pendingAccepted[i] = true;
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
            if (!hasLocalRow) return;
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
                }
                if (absorbed)
                {
                    absorbedCount = absorbedCount + 1;
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
                }
                write = write + 1;
            }
            pendingCandidateCount = write;
        }

        private bool TryCommitAbsorb(string itemInstanceId, int generation, int gain)
        {
            absorbRequestSequence = absorbRequestSequence + 1;
            if (!GrowthLedgerMath.TrySubmitClaim(roundState, playerIds, lifeStates,
                playerCount, commitItemIds, commitGenerations, commitCount, claimItemIds,
                claimGenerations, claimPlayerIds, claimFrames, claimRequestSequences,
                ref claimCount, roundId, roundId, itemInstanceId, generation,
                localPlayerId, authorityFrame, absorbRequestSequence))
            {
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
                return false;
            }
            if (commitRow == GrowthLedgerMath.NoRow) return false;

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
            if (pendingCandidateCount >= PendingCandidateCapacity)
            {
                SignalOverflow("pending candidate capacity 4 exceeded");
                return false;
            }
            if (itemInstanceId == null || itemInstanceId.Length == 0) return false;
            if (itemGeneration < 0) return false;
            int index = pendingCandidateCount;
            pendingAbsorbable[index] = absorbable;
            pendingKinds[index] = kind;
            pendingBounds[index] = bounds;
            pendingPositions[index] = worldPosition;
            pendingValues[index] = valueUnits;
            pendingItemIds[index] = itemInstanceId;
            pendingGenerations[index] = itemGeneration;
            pendingAccepted[index] = false;
            pendingCandidateCount = pendingCandidateCount + 1;
            return true;
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
    }
}
